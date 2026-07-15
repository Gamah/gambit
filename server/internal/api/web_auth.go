package api

import (
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/gamah/gambit/server/internal/steam"
	"github.com/gamah/gambit/server/internal/store"
	"go.uber.org/zap"
)

// Steam sign-in for the archive viewer.
//
// NOTE the protocol: Steam's browser login is **OpenID 2.0**, not OAuth2 — there
// is no Steam OAuth2 endpoint to use, however often it gets called that.
//
// Two independent ways to prove the same SteamID:
//   - in-game: a Facepunch auth token (requireSteam)
//   - on the web: Steam OpenID (this file)
//
// Both are FP/Steam-attested. Neither trusts a client-supplied SteamID.

// nonceStore enforces single-use of openid.response_nonce. steam.Verify
// deliberately only shape-checks the nonce and leaves the value to us, because
// the TTL store is ours. Without this, a captured return URL could be replayed to
// mint a fresh session for as long as Steam's signature stayed valid.
type nonceStore struct {
	mu   sync.Mutex
	seen map[string]time.Time
	ttl  time.Duration
}

func newNonceStore(ttl time.Duration) *nonceStore {
	return &nonceStore{seen: make(map[string]time.Time), ttl: ttl}
}

// use reports whether this nonce is fresh, and burns it. False = replay.
func (n *nonceStore) use(nonce string) bool {
	n.mu.Lock()
	defer n.mu.Unlock()

	now := time.Now()
	for k, t := range n.seen { // sweep on the write path — same reasoning as relay.Store
		if now.Sub(t) > n.ttl {
			delete(n.seen, k)
		}
	}
	if _, replayed := n.seen[nonce]; replayed {
		return false
	}
	n.seen[nonce] = now
	return true
}

func (h *handler) steamReturnURL() string {
	return strings.TrimSuffix(h.baseURL, "/") + "/auth/steam/return"
}

// GET /auth/steam/login — bounce the browser to Steam.
func (h *handler) steamLogin(w http.ResponseWriter, r *http.Request) {
	if h.baseURL == "" {
		writeError(w, http.StatusNotImplemented, "web sign-in is not configured")
		return
	}
	// realm scopes the login and is shown to the user by Steam; return_to must
	// live under it.
	http.Redirect(w, r, steam.LoginURL(strings.TrimSuffix(h.baseURL, "/"), h.steamReturnURL()), http.StatusFound)
}

// GET /auth/steam/return — Steam sends the browser back here with openid.* params.
func (h *handler) steamReturn(w http.ResponseWriter, r *http.Request) {
	if h.baseURL == "" {
		writeError(w, http.StatusNotImplemented, "web sign-in is not configured")
		return
	}

	steamID64, ok, err := steam.Verify(r.Context(), r.URL.Query(), h.steamReturnURL())
	if err != nil || !ok {
		// Fail closed and say nothing useful: a failed assertion is either a bug
		// or an attack, and neither deserves detail.
		h.log.Warn("steam openid verify failed", zap.Error(err), zap.Bool("valid", ok))
		http.Redirect(w, r, "/?error=signin", http.StatusFound)
		return
	}

	// Single-use: steam.Verify checked the nonce exists, we check it's fresh.
	if !h.nonces.use(r.URL.Query().Get("openid.response_nonce")) {
		h.log.Warn("steam openid nonce replayed")
		http.Redirect(w, r, "/?error=signin", http.StatusFound)
		return
	}

	steamID, err := strconv.ParseInt(steamID64, 10, 64)
	if err != nil || steamID <= 0 {
		http.Redirect(w, r, "/?error=signin", http.StatusFound)
		return
	}

	// Steam vouched for them, so they're a real player — record it.
	if err := h.ensureWebPlayer(r, steamID); err != nil {
		h.log.Error("ensure player failed on steam login", zap.Error(err))
	}

	h.sessions.set(w, steamID)
	http.Redirect(w, r, "/", http.StatusFound)
}

// POST /auth/steam/logout — clears the cookie. POST (not GET) so a stray link or
// prefetch can't sign you out; SameSite=Lax covers the cross-site case.
func (h *handler) steamLogout(w http.ResponseWriter, r *http.Request) {
	h.sessions.clear(w)
	writeJSON(w, http.StatusOK, map[string]bool{"ok": true})
}

// GET /api/v1/me — who the viewer is talking to. 401 when signed out; the page
// uses this to decide whether to show the sign-in prompt.
func (h *handler) me(w http.ResponseWriter, r *http.Request) {
	steamID, ok := h.sessions.read(r)
	if !ok {
		writeError(w, http.StatusUnauthorized, "not signed in")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{
		"steam_id": strconv.FormatInt(steamID, 10),
	})
}

// ensureWebPlayer creates the players row for an OpenID-verified SteamID.
func (h *handler) ensureWebPlayer(r *http.Request, steamID int64) error {
	if h.db == nil {
		return nil
	}
	return store.EnsurePlayer(r.Context(), h.db, steamID, true)
}

// callerSteamID resolves who is asking, from EITHER proof of the same identity:
//
//	a web session   — minted by Steam OpenID (the archive viewer)
//	an FP token     — minted by Facepunch (the s&box client, console commands)
//
// Both are attested by Steam or Facepunch; neither trusts a client-supplied
// SteamID. Supporting both is why gambit_gamchess_games still works after the
// archive went private.
func (h *handler) callerSteamID(w http.ResponseWriter, r *http.Request) (int64, bool) {
	if id, ok := h.sessions.read(r); ok {
		return id, true
	}
	// No session — if they brought FP credentials, verify those instead.
	// requireSteam writes its own 401.
	if r.Header.Get(steamIDHeader) != "" || r.Header.Get("Authorization") != "" {
		return h.requireSteam(w, r)
	}
	writeError(w, http.StatusUnauthorized, "sign in to view your games")
	return 0, false
}
