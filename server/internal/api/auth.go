package api

import (
	"net/http"
	"regexp"
	"strconv"
	"strings"

	"github.com/gamah/gambit/server/internal/steam"
	"go.uber.org/zap"
)

// How the s&box client authenticates. Two shapes, one identity:
//
//	Authorization: Bearer <facepunch-auth-token>   (Sandbox.Services.Auth.GetToken)
//	X-Steam-Id: <steamid64>
//
//	Authorization: Bearer gcs_<game-session>       (POST /api/v1/session)
//
// The FP shape costs a live HTTP call to Facepunch on EVERY request, which is
// why the session exists: a polling client (a relayed lichess game, the TV wall)
// would otherwise spend one Facepunch round-trip per player per ~5s, forever, and
// TV multiplies that by everyone standing at a wall. A session is verified with a
// local HMAC and no I/O at all.
//
// The FP shape is not going away — it is the only way to MINT a session, and the
// console commands and one-shot calls keep using it directly.
//
// The SteamID header is an unverified CLAIM. It exists only so we can ask
// Facepunch "does this token belong to this account?" — the answer, not the
// claim, is the identity. A session bearer carries its SteamID inside the MAC and
// so needs no header. Nothing in gamchess may ever take a SteamID from a header,
// body, or query string as authorisation.
const steamIDHeader = "X-Steam-Id"

// bearerToken pulls the Authorization bearer value, tolerating the absent header.
func bearerToken(r *http.Request) string {
	return strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))
}

// steamIDRe matches a SteamID64 — a 1–20 digit number with no leading zero.
// Shape-checking before we hand it to Facepunch (and before ParseInt) keeps
// garbage out of the outbound call. Rejecting a leading zero also rules out "0"
// (which the client uses to mean "empty seat", never a player) and stops the
// same account arriving in two spellings, e.g. "76561197960287930" and
// "076561197960287930".
var steamIDRe = regexp.MustCompile(`^[1-9][0-9]{0,19}$`)

func validSteamID(s string) bool { return steamIDRe.MatchString(s) }

// validateToken is a package var for the same reason steam.endpoint is one: it
// lets tests stub the Facepunch boundary. Production always points at the real
// verifier.
var validateToken = steam.ValidateToken

// verifySteam validates a Facepunch auth token for steamID, failing closed: any
// transport/validation error is logged and treated as "not verified", so a
// Facepunch outage can never grant an unverified SteamID. Callers must already
// have shape-checked steamID with validSteamID.
func (h *handler) verifySteam(r *http.Request, steamID, token string) bool {
	if token == "" {
		return false
	}
	ok, err := validateToken(r.Context(), steamID, token)
	if err != nil {
		h.log.Warn("steam token validation failed", zap.String("steam_id", steamID), zap.Error(err))
	}
	return ok
}

// requireSteam is the gate every authed endpoint goes through. It returns the
// verified SteamID64, or writes a 401 and returns ok=false. The returned int64 is
// the only value in the process that may be treated as an identity.
//
// A `gcs_` bearer is checked first and answers locally. Anything else takes the
// Facepunch path.
func (h *handler) requireSteam(w http.ResponseWriter, r *http.Request) (int64, bool) {
	token := bearerToken(r)

	// A game session. The prefix is a discriminator, not a credential — the MAC is
	// what proves anything, and it is checked before the SteamID is read.
	if strings.HasPrefix(token, gamePrefix) {
		if id, ok := h.sessions.readGame(token); ok {
			return id, true
		}
		// Prefixed but not valid: expired, forged, or signed by a previous process's
		// random key. Refuse here rather than falling through — the Facepunch path
		// would spend a live round-trip proving what we already know, and an expired
		// session is exactly the 401 that makes the client re-mint.
		writeError(w, http.StatusUnauthorized, "session expired")
		return 0, false
	}

	return h.requireFacepunch(w, r)
}

// requireFacepunch is requireSteam without the session shortcut: it accepts ONLY
// a live Facepunch token. Separate because minting a session must never accept
// one — a session that can mint a session renews itself forever, and the 1-hour
// TTL that justifies the whole design would be a fiction. Every other caller
// wants requireSteam.
func (h *handler) requireFacepunch(w http.ResponseWriter, r *http.Request) (int64, bool) {
	claimed := strings.TrimSpace(r.Header.Get(steamIDHeader))
	token := bearerToken(r)

	if !validSteamID(claimed) || token == "" || strings.HasPrefix(token, gamePrefix) {
		writeError(w, http.StatusUnauthorized, "missing or malformed credentials")
		return 0, false
	}
	if !h.verifySteam(r, claimed, token) {
		writeError(w, http.StatusUnauthorized, "steam auth failed")
		return 0, false
	}
	// Safe: validSteamID already proved this parses, and a SteamID64 (~7.6e16)
	// is well inside int64.
	steamID, err := strconv.ParseInt(claimed, 10, 64)
	if err != nil {
		writeError(w, http.StatusUnauthorized, "steam auth failed")
		return 0, false
	}
	return steamID, true
}

// SessionResponse is what POST /api/v1/session returns.
type SessionResponse struct {
	Token string `json:"token"`
	// ExpiresAt is unix seconds. The client re-mints on a 401 rather than watching
	// the clock, so this is advisory — it exists so a caller CAN be polite about it.
	ExpiresAt int64 `json:"expires_at"`
}

// postSession trades a Facepunch token for a game session bearer: one Facepunch
// round-trip now, zero on every later request.
//
// FP-gated only (see requireFacepunch). Mints unconditionally for any verified
// SteamID — there is no player row to create and nothing to look up, because a
// session attests identity and nothing else.
func (h *handler) postSession(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.requireFacepunch(w, r)
	if !ok {
		return
	}
	token, exp := h.sessions.issueGame(steamID)
	writeJSON(w, http.StatusOK, SessionResponse{Token: token, ExpiresAt: exp.Unix()})
}
