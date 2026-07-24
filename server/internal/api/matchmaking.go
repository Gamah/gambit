package api

import (
	"context"
	"crypto/rand"
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
	maxMatchListLimit = 50
	maxOpenerName     = 64
	maxMatchBody      = 4 << 10

	// An open/matched row not heartbeat within this window is swept closed — the
	// backstop for a client that vanished (crash, quit) without cancelling. The
	// opener heartbeats via its own poll (getMatchmaking), so a live waiter stays
	// listed; this only reaps the dead. Generous enough to survive a slow poll.
	matchmakingTTL       = 90 * time.Second
	matchmakingSweepEach = 30 * time.Second
)

// runMatchmakingSweep closes stale adverts on a timer. Process-lived, matching
// the keyring/TV daemons. Modelled on the TV linger sweep: the live signal is the
// heartbeat, the TTL is the backstop, correctness rests on neither being missed.
func (h *handler) runMatchmakingSweep(ctx context.Context) {
	t := time.NewTicker(matchmakingSweepEach)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			n, err := store.ExpireStaleMatches(ctx, h.db, matchmakingTTL)
			if err != nil {
				h.log.Warn("matchmaking sweep failed", zap.Error(err))
			} else if n > 0 {
				h.log.Info("matchmaking sweep closed stale adverts", zap.Int64("rows", n))
			}
		}
	}
}

// matchOpenIn is the "advertise a game" body. opener_name is cosmetic (shown in
// the list); lobby_id is the opener's s&box lobby (their host SteamId string),
// required for 'join' mode and ignored for 'relay'.
type matchOpenIn struct {
	Mode        string `json:"mode"`
	LobbyID     string `json:"lobby_id"`
	TimeControl string `json:"time_control"`
	OpenerName  string `json:"opener_name"`
}

// matchJSON is a list/poll row. lobby_id is DELIBERATELY absent — it is a live
// connect target handed out only in a successful join response, never listed.
type matchJSON struct {
	ID          string `json:"id"`
	OpenerName  string `json:"opener_name"`
	Mode        string `json:"mode"`
	TimeControl string `json:"time_control"`
	Status      string `json:"status"`
	CreatedAt   string `json:"created_at"`
	// Set only on a participant's poll of a matched row, so they learn the outcome:
	// their colour, (relay) the game to start relaying, and — for the opener host,
	// which seats both players — who plays which side. SteamIDs are strings.
	YourColor    string `json:"your_color,omitempty"`
	GameID       string `json:"game_id,omitempty"`
	WhiteSteamID string `json:"white_steam_id,omitempty"`
	BlackSteamID string `json:"black_steam_id,omitempty"`
	OpponentName string `json:"opponent_name,omitempty"`
}

// POST /api/v1/matchmaking — advertise an open game. Opener is the verified caller.
func (h *handler) postMatchmaking(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	var in matchOpenIn
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxMatchBody)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	if in.Mode != "join" && in.Mode != "relay" {
		writeError(w, http.StatusBadRequest, `mode must be "join" or "relay"`)
		return
	}
	if _, _, ok := parseTimeControl(in.TimeControl); !ok {
		writeError(w, http.StatusBadRequest, `time_control must be "secs+inc" or "-"`)
		return
	}
	// 'join' needs a real lobby to connect into — the opener's host SteamId. For
	// 'relay' nobody connects anywhere, so lobby_id is cleared.
	lobby := ""
	if in.Mode == "join" {
		lobby = strings.TrimSpace(in.LobbyID)
		if !validSteamID(lobby) {
			writeError(w, http.StatusBadRequest, "lobby_id must be the host SteamID for a join game")
			return
		}
	}
	name := strings.TrimSpace(in.OpenerName)
	if len(name) > maxOpenerName {
		name = name[:maxOpenerName]
	}

	ctx := r.Context()
	if err := store.EnsurePlayer(ctx, h.db, steamID, true); err != nil {
		h.log.Error("mm ensure opener failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	m, err := store.OpenMatch(ctx, h.db, store.Match{
		OpenerSteamID: steamID,
		OpenerName:    name,
		LobbyID:       lobby,
		Mode:          in.Mode,
		TimeControl:   in.TimeControl,
	})
	if err != nil {
		h.log.Error("open match failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"id": m.ID})
}

// GET /api/v1/matchmaking — open games, not your own, newest first.
func (h *handler) listMatchmaking(w http.ResponseWriter, r *http.Request) {
	if _, ok := h.requireSteam(w, r); !ok {
		return
	}
	limit := clampInt(r.URL.Query().Get("limit"), maxMatchListLimit, 1, maxMatchListLimit)
	matches, err := store.ListOpenMatches(r.Context(), h.db, limit)
	if err != nil {
		h.log.Error("list matches failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	out := make([]matchJSON, 0, len(matches))
	for _, m := range matches {
		out = append(out, matchListJSON(m))
	}
	writeJSON(w, http.StatusOK, map[string]any{"matches": out})
}

// GET /api/v1/matchmaking/{id} — poll one match. The opener waits on this to learn
// someone joined (and their colour, and the relay game_id). It also HEARTBEATS the
// opener's open row, so an opener who is still polling keeps their advert alive and
// one who has walked away lets the sweep close it.
func (h *handler) getMatchmaking(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	id := r.PathValue("id")
	if _, err := uuid.Parse(id); err != nil {
		writeError(w, http.StatusBadRequest, "id must be a UUID")
		return
	}
	m, err := store.MatchByID(r.Context(), h.db, id)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "no such match")
		return
	}
	if err != nil {
		h.log.Error("get match failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	// Only the two participants may read a match's detail (the opener always; the
	// joiner once matched). Others get 404 so ids aren't probeable.
	isOpener := m.OpenerSteamID == steamID
	isJoiner := m.JoinerSteamID != nil && *m.JoinerSteamID == steamID
	if !isOpener && !isJoiner {
		writeError(w, http.StatusNotFound, "no such match")
		return
	}
	if isOpener && m.Status == "open" {
		_ = store.TouchMatch(r.Context(), h.db, id, steamID) // best-effort heartbeat
	}

	out := matchListJSON(m)
	if m.Status == "matched" {
		// The poller is the opener (only the opener polls a match for its outcome), so
		// your_color is the opener's colour. The joiner already got its colour from the
		// join response — role-based, so it holds even when both SteamIDs are equal.
		out.YourColor = colorWord(m.OpenerColor)
		if m.GameID != nil {
			out.GameID = *m.GameID
		}
		// The opener host seats both players, so it needs the assignment as SteamIDs.
		if m.WhiteSteamID != nil {
			out.WhiteSteamID = strconv.FormatInt(*m.WhiteSteamID, 10)
		}
		if m.BlackSteamID != nil {
			out.BlackSteamID = strconv.FormatInt(*m.BlackSteamID, 10)
		}
	}
	writeJSON(w, http.StatusOK, out)
}

// POST /api/v1/matchmaking/{id}/join — claim an open game. Joiner is the verified
// caller. gamchess coin-flips the colour so the opener can't default to White.
func (h *handler) joinMatchmaking(w http.ResponseWriter, r *http.Request) {
	joiner, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	id := r.PathValue("id")
	if _, err := uuid.Parse(id); err != nil {
		writeError(w, http.StatusBadRequest, "id must be a UUID")
		return
	}
	ctx := r.Context()
	m, err := store.MatchByID(ctx, h.db, id)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "no such match")
		return
	}
	if err != nil {
		h.log.Error("join lookup failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	// Self-join is allowed: playing yourself is a feature (and the one-machine test
	// path). The two sides are told apart by opener_color, not by SteamID.
	if m.Status != "open" {
		writeError(w, http.StatusConflict, "that game is no longer open")
		return
	}
	if err := store.EnsurePlayer(ctx, h.db, joiner, true); err != nil {
		h.log.Error("join ensure player failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	// The coin flip — gamchess assigns sides, neither client picks (crypto/rand so it
	// isn't a predictable PRNG). openerColor is the side the OPENER plays; the joiner
	// plays the other. Stored so self-play (opener == joiner) still has two distinct
	// sides addressable by role rather than by an ambiguous SteamID.
	openerColor := "w"
	if coinHeads() {
		openerColor = "b"
	}
	white, black := m.OpenerSteamID, joiner
	if openerColor == "b" {
		white, black = joiner, m.OpenerSteamID
	}

	claimed, err := store.ClaimMatch(ctx, h.db, id, joiner, white, black, openerColor, nil)
	if errors.Is(err, store.ErrConflict) {
		writeError(w, http.StatusConflict, "someone else just took that game")
		return
	}
	if err != nil {
		h.log.Error("claim match failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	resp := map[string]string{
		"mode":       claimed.Mode,
		"your_color": oppositeColorWord(openerColor), // the joiner plays the other side
	}
	switch claimed.Mode {
	case "join":
		// The connect target — handed out only here, on a real join.
		resp["lobby_id"] = claimed.LobbyID
	case "relay":
		// Spin up the gamchess-authoritative game and point the match at it.
		initMs, incMs, _ := parseTimeControl(claimed.TimeControl)
		rg, err := store.CreateRelayGame(ctx, h.db, store.RelayGame{
			WhiteSteamID: white,
			BlackSteamID: black,
			TimeControl:  claimed.TimeControl,
			InitialMs:    initMs,
			IncrementMs:  incMs,
			WhiteMs:      max64(initMs, 0),
			BlackMs:      max64(initMs, 0),
		})
		if err != nil {
			h.log.Error("create relay game failed", zap.Error(err))
			writeError(w, http.StatusInternalServerError, "internal error")
			return
		}
		if err := store.SetMatchGameID(ctx, h.db, id, rg.ID); err != nil {
			h.log.Error("set match game failed", zap.Error(err))
			writeError(w, http.StatusInternalServerError, "internal error")
			return
		}
		resp["game_id"] = rg.ID
	}
	writeJSON(w, http.StatusOK, resp)
}

// DELETE /api/v1/matchmaking/{id} — opener cancels their advert.
func (h *handler) deleteMatchmaking(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	id := r.PathValue("id")
	if _, err := uuid.Parse(id); err != nil {
		writeError(w, http.StatusBadRequest, "id must be a UUID")
		return
	}
	if err := store.CloseMatch(r.Context(), h.db, id, steamID); err != nil {
		h.log.Error("cancel match failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "closed"})
}

func matchListJSON(m store.Match) matchJSON {
	return matchJSON{
		ID:          m.ID,
		OpenerName:  m.OpenerName,
		Mode:        m.Mode,
		TimeControl: m.TimeControl,
		Status:      m.Status,
		CreatedAt:   m.CreatedAt.UTC().Format("2006-01-02T15:04:05Z07:00"),
	}
}

// colorWord expands a stored "w"/"b" to "white"/"black" (or "" for neither). Role-based,
// so it is correct even in self-play where a SteamID comparison would be ambiguous.
func colorWord(short string) string {
	switch short {
	case "w":
		return "white"
	case "b":
		return "black"
	}
	return ""
}

// oppositeColorWord is colorWord of the other side.
func oppositeColorWord(short string) string {
	switch short {
	case "w":
		return "black"
	case "b":
		return "white"
	}
	return ""
}

// parseTimeControl reads a PGN spec — "secs+inc" (e.g. "180+2") or "-" (untimed) —
// into millisecond clock values. initialMs is -1 for untimed. ok=false on garbage.
func parseTimeControl(tc string) (initialMs, incMs int64, ok bool) {
	tc = strings.TrimSpace(tc)
	if tc == "" || tc == "-" {
		return -1, 0, true
	}
	secs, inc, found := strings.Cut(tc, "+")
	s, err1 := strconv.Atoi(secs)
	i, err2 := strconv.Atoi(inc)
	if !found || err1 != nil || err2 != nil || s <= 0 || i < 0 || s > 86400 || i > 3600 {
		return 0, 0, false
	}
	return int64(s) * 1000, int64(i) * 1000, true
}

// coinHeads is one unbiased bit from crypto/rand. Falls to false only if the OS
// RNG fails, which is not survivable elsewhere in this server either — a biased
// default here just means White goes to the opener that once, not a security hole.
func coinHeads() bool {
	var b [1]byte
	if _, err := rand.Read(b[:]); err != nil {
		return false
	}
	return b[0]&1 == 1
}

func max64(a, b int64) int64 {
	if a > b {
		return a
	}
	return b
}
