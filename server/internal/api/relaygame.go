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

// A relay game is the 'relay' matchmaking mode: two players in SEPARATE lobbies,
// gamchess as the authority. Structurally the lichess relay with gamchess in
// lichess's place — POST a move, poll state since a cursor, server-authoritative
// clock. gamchess runs NO chess engine: it trusts the mover (who ran the vendored
// rules) and carries `fen` as the checksum, exactly as the two-seat host trusts a
// NetChessMove. The clock is the one thing it adjudicates.

const maxRelayBody = 4 << 10

// relayStateJSON is what a poller renders. Clocks are LIVE (the ticking side
// already decremented to `now`); the client runs them down locally between polls
// and snaps on each one — the same house rule as the lichess/TV clocks, never
// reading HIGH. SteamIDs are strings (the 2^53 rule).
type relayStateJSON struct {
	ID           string   `json:"id"`
	WhiteSteamID string   `json:"white_steam_id"`
	BlackSteamID string   `json:"black_steam_id"`
	TimeControl  string   `json:"time_control"`
	Ply          int      `json:"ply"`   // total half-moves played
	Moves        []string `json:"moves"` // UCI from the requested `since` cursor
	Fen          string   `json:"fen"`
	Turn         string   `json:"turn"` // "w" | "b"
	WhiteMs      int64    `json:"white_ms"`
	BlackMs      int64    `json:"black_ms"`
	Untimed      bool     `json:"untimed"`
	Status       string   `json:"status"`
	Reason       string   `json:"reason"`
	DrawOffer    string   `json:"draw_offer"` // "" | "w" | "b"
}

func toRelayJSON(g store.RelayGame, since int) relayStateJSON {
	all := strings.Fields(g.Moves)
	if since < 0 {
		since = 0
	}
	if since > len(all) {
		since = len(all)
	}
	whiteMs, blackMs, _ := g.LiveClocks(time.Now())
	return relayStateJSON{
		ID:           g.ID,
		WhiteSteamID: strconv.FormatInt(g.WhiteSteamID, 10),
		BlackSteamID: strconv.FormatInt(g.BlackSteamID, 10),
		TimeControl:  g.TimeControl,
		Ply:          len(all),
		Moves:        append([]string{}, all[since:]...),
		Fen:          g.Fen,
		Turn:         g.Turn,
		WhiteMs:      whiteMs,
		BlackMs:      blackMs,
		Untimed:      g.Untimed(),
		Status:       g.Status,
		Reason:       g.Reason,
		DrawOffer:    g.DrawOffer,
	}
}

// relayMember loads a relay game and asserts the caller is one of its two players.
// 404 (not 403) for a non-member, so game ids aren't probeable.
func (h *handler) relayMember(w http.ResponseWriter, r *http.Request, steamID int64) (store.RelayGame, bool) {
	id := r.PathValue("id")
	if _, err := uuid.Parse(id); err != nil {
		writeError(w, http.StatusBadRequest, "id must be a UUID")
		return store.RelayGame{}, false
	}
	g, err := store.ReadRelayGame(r.Context(), h.db, id)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "no such game")
		return store.RelayGame{}, false
	}
	if err != nil {
		h.log.Error("read relay game failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return store.RelayGame{}, false
	}
	if steamID != g.WhiteSteamID && steamID != g.BlackSteamID {
		writeError(w, http.StatusNotFound, "no such game")
		return store.RelayGame{}, false
	}
	return g, true
}

// GET /api/v1/relaygame/{id}?since=N — poll. ReadRelayGame flags a timeout on the
// way through, so a poll is also how a flag is discovered and made authoritative.
func (h *handler) getRelayGame(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	g, ok := h.relayMember(w, r, steamID)
	if !ok {
		return
	}
	since := clampInt(r.URL.Query().Get("since"), 0, 0, 1<<20)
	writeJSON(w, http.StatusOK, toRelayJSON(g, since))
}

type relayMoveBody struct {
	Uci    string `json:"uci"`
	Fen    string `json:"fen"`
	Over   bool   `json:"over"`
	Result string `json:"result"` // "white_won" | "black_won" | "draw", when over
	Reason string `json:"reason"`
}

// POST /api/v1/relaygame/{id}/move — the caller plays their move. Turn ownership,
// clock and flip are enforced in the store under a row lock.
func (h *handler) postRelayMove(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	g, ok := h.relayMember(w, r, steamID)
	if !ok {
		return
	}
	var in relayMoveBody
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxRelayBody)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	if l := len(in.Uci); l < 4 || l > 5 {
		writeError(w, http.StatusBadRequest, "uci must be 4-5 chars")
		return
	}
	if in.Fen == "" || len(in.Fen) > 128 {
		writeError(w, http.StatusBadRequest, "fen required")
		return
	}
	over := store.RelayMoveIn{Uci: in.Uci, Fen: in.Fen}
	if in.Over {
		if in.Result != "white_won" && in.Result != "black_won" && in.Result != "draw" {
			writeError(w, http.StatusBadRequest, "bad result")
			return
		}
		over.Over, over.Result, over.Reason = true, in.Result, clip(in.Reason, 40)
	}

	out, err := store.ApplyRelayMove(r.Context(), h.db, g.ID, steamID, over)
	if errors.Is(err, store.ErrConflict) {
		// Not your turn, or the game already ended (incl. a flag found on the way
		// in). Hand back the current state so the client resyncs rather than retries.
		cur, rerr := store.ReadRelayGame(r.Context(), h.db, g.ID)
		if rerr == nil {
			writeJSON(w, http.StatusConflict, toRelayJSON(cur, 0))
			return
		}
		writeError(w, http.StatusConflict, "not your move")
		return
	}
	if err != nil {
		h.log.Error("apply relay move failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, toRelayJSON(out, 0))
}

// POST /api/v1/relaygame/{id}/{action} — resign / abort / draw-offer /
// draw-accept / draw-decline.
func (h *handler) postRelayAction(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	g, ok := h.relayMember(w, r, steamID)
	if !ok {
		return
	}
	action := r.PathValue("action")
	out, err := store.RelayAction(r.Context(), h.db, g.ID, steamID, action)
	if errors.Is(err, store.ErrConflict) {
		writeError(w, http.StatusConflict, "that action isn't available")
		return
	}
	if err != nil {
		h.log.Error("relay action failed", zap.Error(err), zap.String("action", action))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, toRelayJSON(out, 0))
}

func clip(s string, n int) string {
	if len(s) > n {
		return s[:n]
	}
	return s
}
