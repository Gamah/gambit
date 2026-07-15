package api

import (
	"context"
	"net/http"
	"regexp"
	"strconv"
	"strings"

	"github.com/gamah/gambit/server/internal/steam"
	"go.uber.org/zap"
)

// How the s&box client authenticates:
//
//	Authorization: Bearer <facepunch-auth-token>   (Sandbox.Services.Auth.GetToken)
//	X-Steam-Id: <steamid64>
//
// The SteamID header is an unverified CLAIM. It exists only so we can ask
// Facepunch "does this token belong to this account?" — the answer, not the
// claim, is the identity. Nothing in gamchess may ever take a SteamID from a
// header, body, or query string as authorisation.
const steamIDHeader = "X-Steam-Id"

// steamIDRe matches a SteamID64 — a 1–20 digit number. Range-checking the shape
// before we hand it to Facepunch (and before ParseInt) keeps garbage out of the
// outbound call.
var steamIDRe = regexp.MustCompile(`^[0-9]{1,20}$`)

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
// FP-verified SteamID64, or writes a 401 and returns ok=false. The returned
// int64 is the only value in the process that may be treated as an identity.
func (h *handler) requireSteam(w http.ResponseWriter, r *http.Request) (int64, bool) {
	claimed := strings.TrimSpace(r.Header.Get(steamIDHeader))
	token := strings.TrimSpace(strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer "))

	if !validSteamID(claimed) || token == "" {
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

// ensurePlayer upserts the players row for a verified SteamID and bumps
// last_seen. Every authed path calls this, so the FK targets in lichess_links
// and games always exist. Only ever called with an FP-verified ID.
func (h *handler) ensurePlayer(ctx context.Context, steamID int64) error {
	_, err := h.db.Exec(ctx, `
		INSERT INTO players (steam_id) VALUES ($1)
		ON CONFLICT (steam_id) DO UPDATE SET last_seen = NOW()
	`, steamID)
	return err
}
