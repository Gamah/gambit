package api

import (
	"encoding/json"
	"errors"
	"net/http"
	"regexp"
	"strings"

	"github.com/gamah/gambit/server/internal/store"
	"go.uber.org/zap"
)

// lichessUserRe matches a lichess username: 2-30 chars, alphanumeric with _ and
// - allowed inside. We only ever store what the client read back from lichess's
// own /api/account, so this is a sanity bound, not a trust boundary.
var lichessUserRe = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9_-]{1,29}$`)

// PUT /api/v1/links/lichess — FP token. Body {lichess_username}.
//
// NOTE: this endpoint is an addition to issue #7's table, which lists only the
// DELETE. Without it nothing ever writes lichess_links and the table is dead.
// The client is the only party that can supply the username: it alone holds the
// bearer, so it alone can call lichess's /api/account. gamchess cannot look the
// name up for itself — by design, since it never has a token.
//
// This means the username is client-asserted. That is acceptable because the
// link is a convenience (display + "who am I"), never an authorisation input:
// nothing in gamchess grants anything based on lichess_username. SteamID64
// remains the only identity, and it is FP-verified.
func (h *handler) putLichessLink(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}

	var in struct {
		LichessUsername string `json:"lichess_username"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&in); err != nil {
		writeError(w, http.StatusBadRequest, "malformed body")
		return
	}
	username := strings.TrimSpace(in.LichessUsername)
	if !lichessUserRe.MatchString(username) {
		writeError(w, http.StatusBadRequest, "lichess_username is not a valid lichess username")
		return
	}

	ctx := r.Context()
	if err := store.EnsurePlayer(ctx, h.db, steamID, true); err != nil {
		h.log.Error("ensure player failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}

	err := store.UpsertLichessLink(ctx, h.db, steamID, username)
	if errors.Is(err, store.ErrUsernameTaken) {
		// Someone else already claims this lichess account. Don't say who.
		writeError(w, http.StatusConflict, "that lichess account is already linked to another player")
		return
	}
	if err != nil {
		// Deliberately no username or steam_id in the log — the pairing is the
		// sensitive part, and this is exactly where it would leak.
		h.log.Error("link lichess failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"lichess_username": username})
}

// DELETE /api/v1/links/lichess — FP token. The unlink path the security posture
// requires: a persisted SteamID<->lichess link is durable identity data, so it
// must always be removable by its owner.
func (h *handler) deleteLichessLink(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireSteam(w, r)
	if !ok {
		return
	}
	existed, err := store.DeleteLichessLink(r.Context(), h.db, steamID)
	if err != nil {
		h.log.Error("unlink lichess failed", zap.Error(err))
		writeError(w, http.StatusInternalServerError, "internal error")
		return
	}
	// Idempotent: unlinking when not linked is a success, not a 404.
	writeJSON(w, http.StatusOK, map[string]bool{"unlinked": existed})
}
