package api

import (
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/gamah/gambit/server/internal/store"
	"github.com/google/uuid"
	"go.uber.org/zap"
)

const (
	// A long correspondence game's PGN is a few KB; 256KB is far past any real
	// game and still bounds what one POST can cost us.
	maxPgnBytes  = 256 << 10
	maxGameBody  = maxPgnBytes + (4 << 10)
	defaultLimit = 50
	maxLimit     = 200
)

// validResults are the only values the games.result column may take. This
// mirrors ChessGame.ResultString on the client ("*" = unfinished).
var validResults = map[string]bool{"1-0": true, "0-1": true, "1/2-1/2": true, "*": true}

// SteamIDs cross the wire as STRINGS, in both directions.
//
// A SteamID64 (~7.6e16) is larger than JavaScript's 2^53 safe-integer range, so
// a browser calling JSON.parse on a bare number silently corrupts the last
// digits. rotaliate hit this and went as far as storing steam_id as TEXT;
// gamchess keeps BIGINT in the DB (compact, indexed) and converts at the edge.
// The archive is public and read by a web page eventually, so the wire format
// has to be right now — this contract is hand-mirrored in C#, not generated.
type gamePost struct {
	ClientGameID  string  `json:"client_game_id"`
	Pgn           string  `json:"pgn"`
	WhiteSteamID  string  `json:"white_steam_id"` // "" or "0" = empty seat
	BlackSteamID  string  `json:"black_steam_id"`
	Result        string  `json:"result"`
}

// gameJSON is the response shape — store.Game with SteamIDs stringified.
type gameJSON struct {
	ID            string  `json:"id"`
	ClientGameID  string  `json:"client_game_id"`
	Pgn           string  `json:"pgn"`
	WhiteSteamID  *string `json:"white_steam_id"`
	BlackSteamID  *string `json:"black_steam_id"`
	Result        string  `json:"result"`
	PlayedAt      string  `json:"played_at"`
	SubmittedBy   string  `json:"submitted_by"`
}

func toGameJSON(g store.Game) gameJSON {
	return gameJSON{
		ID:            g.ID,
		ClientGameID:  g.ClientGameID,
		Pgn:           g.Pgn,
		WhiteSteamID:  seatString(g.WhiteSteamID),
		BlackSteamID:  seatString(g.BlackSteamID),
		Result:        g.Result,
		PlayedAt:      g.PlayedAt.UTC().Format(time.RFC3339),
		SubmittedBy:   strconv.FormatInt(g.SubmittedBy, 10),
	}
}

func seatString(seat *int64) *string {
	if seat == nil {
		return nil
	}
	s := strconv.FormatInt(*seat, 10)
	return &s
}

// parseSeat reads a seat SteamID from the wire. "" and "0" both mean "empty
// seat" — the client uses 0 for that (ChessStation) and the DB says NULL.
func parseSeat(raw string) (*int64, bool) {
	raw = strings.TrimSpace(raw)
	if raw == "" || raw == "0" {
		return nil, true
	}
	if !validSteamID(raw) {
		return nil, false
	}
	n, err := strconv.ParseInt(raw, 10, 64)
	if err != nil {
		return nil, false
	}
	return &n, true
}

// POST /api/v1/games — FP token. Idempotent on client_game_id.
func (h *handler) postGame(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}

	var in gamePost
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxGameBody)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}

	// client_game_id is host-generated at game start and [Sync]ed to both seats;
	// it is what makes a double submission a no-op.
	if _, err := uuid.Parse(in.ClientGameID); err != nil {
		writeError(w, http.StatusBadRequest, "client_game_id must be a UUID")
		return
	}
	if in.Pgn == "" || len(in.Pgn) > maxPgnBytes {
		writeError(w, http.StatusBadRequest, "pgn must be non-empty and under 256KB")
		return
	}
	if !validResults[in.Result] {
		writeError(w, http.StatusBadRequest, `result must be one of "1-0", "0-1", "1/2-1/2", "*"`)
		return
	}

	white, okW := parseSeat(in.WhiteSteamID)
	black, okB := parseSeat(in.BlackSteamID)
	if !okW || !okB {
		writeError(w, http.StatusBadRequest, "seat steam ids must be SteamID64 strings (or \"0\"/\"\" for an empty seat)")
		return
	}

	// The seat SteamIDs are CLAIMS from the body — only submitted_by is verified.
	// So: you may only archive a game you sat in. Without this, anyone with a
	// valid Facepunch token could inject arbitrary games into any other player's
	// archive, since GET /games is keyed on those claimed seat IDs.
	//
	// This does still let your actual opponent submit the game (they were there),
	// which is the point — either seat may POST.
	if !seatMatches(steamID, white) && !seatMatches(steamID, black) {
		writeError(w, http.StatusForbidden, "you may only archive a game you played in")
		return
	}

	ctx := r.Context()
	// The caller is FP-verified, so bump last_seen. The opponent is a claim, so
	// create the FK target without pretending we saw them.
	if err := store.EnsurePlayer(ctx, h.db, steamID, true); err != nil {
		h.log.Error("ensure submitter failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	for _, seat := range []*int64{white, black} {
		if seat == nil || *seat == steamID {
			continue
		}
		if err := store.EnsurePlayer(ctx, h.db, *seat, false); err != nil {
			h.log.Error("ensure opponent failed", zap.Error(err))
			writeError(w, http.StatusInternalServerError, "internal error")
			return
		}
	}

	g, err := store.UpsertGame(ctx, h.db, store.Game{
		ClientGameID:  in.ClientGameID,
		Pgn:           in.Pgn,
		WhiteSteamID:  white,
		BlackSteamID:  black,
		Result:        in.Result,
		SubmittedBy:   steamID,
	})
	if err != nil {
		h.log.Error("archive game failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	// 200 either way: a second submission of the same client_game_id is a no-op
	// that returns the stored row, so the client needs no special case.
	writeJSON(w, http.StatusOK, toGameJSON(g))
}

// seatMatches reports whether an FP-verified steamID occupies this seat.
//
// The *seat != 0 guard is load-bearing: the client uses 0 for "empty seat", so
// without it a caller who somehow presented as 0 would "occupy" every empty
// seat and clear the participant check. requireSteam should never yield 0, but
// this check is the thing standing between a claim and someone's archive, so it
// doesn't get to rely on that.
func seatMatches(steamID int64, seat *int64) bool {
	return seat != nil && *seat != 0 && *seat == steamID
}

// GET /api/v1/games?limit=&offset= — YOUR games only.
//
// The archive is private: you see games you sat in, and nothing else. There is
// deliberately no ?steam_id= — taking the SteamID from the request would make
// every player's history enumerable by anyone who could sign in, which is exactly
// what gating this was meant to stop. The identity comes from the session (Steam
// OpenID) or the FP token, never from the query.
func (h *handler) listGames(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	q := r.URL.Query()
	limit := clampInt(q.Get("limit"), defaultLimit, 1, maxLimit)
	offset := clampInt(q.Get("offset"), 0, 0, 1<<30)

	games, err := store.GamesBySteamID(r.Context(), h.db, steamID, limit, offset)
	if err != nil {
		h.log.Error("list games failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	out := make([]gameJSON, 0, len(games))
	for _, g := range games {
		out = append(out, toGameJSON(g))
	}
	writeJSON(w, http.StatusOK, map[string]any{"games": out})
}

// GET /api/v1/games/{id} — one of YOUR games.
func (h *handler) getGame(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.callerSteamID(w, r)
	if !ok {
		return
	}
	id := r.PathValue("id")
	if _, err := uuid.Parse(id); err != nil {
		writeError(w, http.StatusBadRequest, "id must be a UUID")
		return
	}
	g, err := store.GameByID(r.Context(), h.db, id)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}
	if err != nil {
		h.log.Error("get game failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	// 404, not 403: a game you didn't play in must be indistinguishable from one
	// that doesn't exist, or the id space becomes probeable.
	if !seatMatches(steamID, g.WhiteSteamID) && !seatMatches(steamID, g.BlackSteamID) {
		writeError(w, http.StatusNotFound, "no such game")
		return
	}
	writeJSON(w, http.StatusOK, toGameJSON(g))
}

// clampInt parses a query int, falling back to def, bounded to [lo, hi].
func clampInt(raw string, def, lo, hi int) int {
	if raw == "" {
		return def
	}
	n, err := strconv.Atoi(raw)
	if err != nil {
		return def
	}
	if n < lo {
		return lo
	}
	if n > hi {
		return hi
	}
	return n
}
