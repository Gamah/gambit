// Package api owns gamchess's HTTP surface: stdlib net/http with Go 1.22
// method-pattern routing, no framework. Handlers hang off one dependency-injected
// handler struct — no global state.
package api

import (
	"encoding/json"
	"net/http"
	"path"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"go.uber.org/zap"
)

// How long a used Steam OpenID nonce is remembered. Only has to outlive the
// window in which Steam's signature on the same assertion stays valid.
const openidNonceTTL = 30 * time.Minute

type handler struct {
	db      *pgxpool.Pool
	log     *zap.Logger
	version string

	// baseURL is the public root gamchess is served at — the Steam OpenID realm
	// and return root. Blank disables web sign-in.
	baseURL string

	// Web sign-in (Steam OpenID) for the archive viewer.
	sessions *sessions
	nonces   *nonceStore
}

func NewRouter(db *pgxpool.Pool, log *zap.Logger, version, baseURL, frontendDir, sessionSecret string) *http.ServeMux {
	h := &handler{
		db:       db,
		log:      log,
		version:  version,
		baseURL:  baseURL,
		sessions: newSessions(sessionSecret),
		nonces:   newNonceStore(openidNonceTTL),
	}

	mux := http.NewServeMux()

	// Liveness. Deliberately unwrapped: no auth, no rate limit.
	mux.HandleFunc("GET /health", h.health)

	// Steam OpenID sign-in for the archive viewer. NOT OAuth2 — Steam has no
	// OAuth2 endpoint.
	mux.HandleFunc("GET /auth/steam/login", h.steamLogin)
	mux.HandleFunc("GET /auth/steam/return", h.steamReturn)
	mux.HandleFunc("POST /auth/steam/logout", h.steamLogout)
	mux.HandleFunc("GET /api/v1/me", h.me)

	// Game archive. Private: every route needs a caller (Steam OpenID session or
	// an FP token), and you only ever see games you sat in.
	mux.HandleFunc("POST /api/v1/games", h.postGame)
	mux.HandleFunc("GET /api/v1/games", h.listGames)
	mux.HandleFunc("GET /api/v1/games/{id}", h.getGame)

	// The archive viewer. Registered last and rooted at "/", which in Go 1.22's
	// mux is the least-specific pattern — every route above still wins. Blank
	// FRONTEND_DIR serves no web UI and changes nothing else.
	if frontendDir != "" {
		fs := http.FileServer(http.Dir(frontendDir))
		mux.Handle("GET /", noStoreIndex(fs))
		log.Info("serving the archive viewer", zap.String("dir", frontendDir))
	} else {
		log.Warn("FRONTEND_DIR not set — archive viewer disabled")
	}

	return mux
}

// noStoreIndex keeps index.html and the JS from being cached across a deploy —
// the viewer is small and served over one connection, so staleness costs more
// than the bytes do.
func noStoreIndex(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// package.json is a build artifact of the JS module layout (it marks the
		// dir as ESM for node), not part of the site. Don't serve it.
		if path.Base(r.URL.Path) == "package.json" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Cache-Control", "no-cache")
		next.ServeHTTP(w, r)
	})
}

func (h *handler) health(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, map[string]string{
		"status":  "ok",
		"version": h.version,
	})
}

func writeJSON(w http.ResponseWriter, code int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(code)
	json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, code int, msg string) {
	writeJSON(w, code, map[string]string{"error": msg})
}
