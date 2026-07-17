// Package api owns gamchess's HTTP surface: stdlib net/http with Go 1.22
// method-pattern routing, no framework. Handlers hang off one dependency-injected
// handler struct — no global state.
package api

import (
	"encoding/json"
	"errors"
	"net/http"
	"path"
	"strings"
	"time"

	"github.com/gamah/gambit/server/internal/lichess"
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
	// and return root, and (via lichessRedirectURL) the lichess OAuth redirect.
	// Blank disables web sign-in and lichess linking.
	baseURL string

	// Web sign-in (Steam OpenID) for the archive viewer.
	sessions *sessions
	nonces   *nonceStore

	// Lichess (M8). tokens is nil when LICHESS_TOKEN_KEY is unset, which switches
	// the whole feature off — we never fall back to storing a plaintext token.
	// There is no client-id field: lichess.ClientID is a constant, because
	// lichess records the redirect ORIGIN on a token and never the client_id.
	tokens  *lichess.Cipher
	pending *pendingLinks
	relay   *relay

	// TV (M9) is deliberately NOT gated on tokens: /api/tv/{channel}/feed is
	// anonymous upstream, so TV must keep working for a player who has never
	// linked a lichess account, and on a deployment with no LICHESS_TOKEN_KEY at
	// all.
	tv *tv
	// auditKey gates the token-audit sweep. Blank hides the route entirely.
	auditKey string
}

// Config is what NewRouter needs. A struct rather than a parameter list because
// the list had already reached six strings and the next one would have been a
// silent mis-ordering waiting to happen — every field here is a string, so the
// compiler would not have caught it.
type Config struct {
	Version       string
	BaseURL       string
	FrontendDir   string
	SessionSecret string

	// LichessTokenKey is 32 bytes (base64 or hex). Blank ⇒ lichess is off.
	LichessTokenKey string
	// LichessAuditKey gates POST /api/v1/lichess/audit. Blank ⇒ no such route.
	LichessAuditKey string
}

func NewRouter(db *pgxpool.Pool, log *zap.Logger, cfg Config) *http.ServeMux {
	h := &handler{
		db:       db,
		log:      log,
		version:  cfg.Version,
		baseURL:  strings.TrimSuffix(cfg.BaseURL, "/"),
		sessions: newSessions(cfg.SessionSecret),
		nonces:   newNonceStore(openidNonceTTL),
		pending:  newPendingLinks(pendingTTL),
		auditKey: cfg.LichessAuditKey,
	}

	// The token cipher is the on/off switch for everything lichess. Absent key ⇒
	// feature off with a warning, never fatal and never plaintext — the same
	// discipline as a blank SESSION_SECRET. A key that is PRESENT but broken is
	// fatal, because the operator meant to set it.
	switch c, err := lichess.NewCipher(cfg.LichessTokenKey); {
	case errors.Is(err, lichess.ErrNoKey):
		log.Warn("LICHESS_TOKEN_KEY not set — lichess linking and play are disabled")
	case err != nil:
		log.Fatal("LICHESS_TOKEN_KEY is set but unusable", zap.Error(err))
	default:
		h.tokens = c
		if h.baseURL == "" {
			log.Warn("PUBLIC_BASE_URL not set — lichess linking is disabled (no redirect URI)")
		} else {
			log.Info("lichess linking enabled",
				zap.String("client_id", lichess.ClientID),
				zap.String("redirect_uri", h.lichessRedirectURL()))
		}
	}
	h.relay = newRelay(log, db, h.tokens)
	h.tv = newTv(log)

	mux := http.NewServeMux()

	// Liveness. Deliberately unwrapped: no auth, no rate limit.
	mux.HandleFunc("GET /health", h.health)

	// Steam OpenID sign-in for the archive viewer. NOT OAuth2 — Steam has no
	// OAuth2 endpoint.
	mux.HandleFunc("GET /auth/steam/login", h.steamLogin)
	mux.HandleFunc("GET /auth/steam/return", h.steamReturn)
	mux.HandleFunc("POST /auth/steam/logout", h.steamLogout)
	mux.HandleFunc("GET /api/v1/me", h.me)

	// A game session for the s&box client: one Facepunch round-trip here, then a
	// local HMAC on every later request instead of one Facepunch call per request.
	// FP-gated ONLY — a session may not mint a session, or the 1-hour TTL that
	// justifies it renews itself forever.
	mux.HandleFunc("POST /api/v1/session", h.postSession)

	// Game archive. Private: every route needs a caller (Steam OpenID session or
	// an FP token), and you only ever see games you sat in.
	mux.HandleFunc("POST /api/v1/games", h.postGame)
	mux.HandleFunc("GET /api/v1/games", h.listGames)
	mux.HandleFunc("GET /api/v1/games/{id}", h.getGame)

	// Lichess account linking (M8), in a browser. Grouped with the auth routes
	// because that is what they are: an OAuth flow, Steam-session gated.
	//
	// NOTE for Caddy: /lichess/callback takes the OAuth code in the QUERY STRING,
	// so this vhost must never gain a `log` directive — the same rule
	// /auth/steam/return already imposes. Caddy logs nothing by default; the job
	// is not to start.
	mux.HandleFunc("GET /lichess/link", h.lichessLink)
	mux.HandleFunc("GET /lichess/start", h.lichessStart)
	mux.HandleFunc("GET /lichess/callback", h.lichessCallback)
	mux.HandleFunc("POST /lichess/unlink", h.lichessWebUnlink)

	// Lichess, from in-game. FP-token gated (the status/unlink pair also accept a
	// web session, so the viewer can show the same thing).
	mux.HandleFunc("GET /api/v1/lichess", h.lichessStatus)
	mux.HandleFunc("DELETE /api/v1/lichess", h.lichessUnlink)
	mux.HandleFunc("POST /api/v1/lichess/play", h.lichessPlay)
	// A lobby seek: one player, a random opponent. Rapid or slower only, and
	// capped at ~5/min across the WHOLE playerbase (the limit is per IP).
	mux.HandleFunc("POST /api/v1/lichess/seek", h.lichessSeek)
	mux.HandleFunc("POST /api/v1/lichess/challenge", h.lichessChallenge)
	mux.HandleFunc("GET /api/v1/lichess/play/{id}", h.lichessPlayState)
	mux.HandleFunc("DELETE /api/v1/lichess/play/{id}", h.lichessPlayCancel)
	mux.HandleFunc("POST /api/v1/lichess/play/{id}/{action}", h.lichessPlayAct)

	// lichess TV (M9), for the spectator wall. Session-gated like everything else —
	// anonymous upstream is exactly why an open relay here would be attractive to
	// abuse, and lichess would see our IP and our User-Agent on all of it.
	//
	// One upstream stream per CHANNEL regardless of how many clients poll: that
	// invariant is the whole reason this is proxied rather than hit directly.
	mux.HandleFunc("GET /api/v1/tv/channels", h.tvChannels)
	mux.HandleFunc("GET /api/v1/tv/{channel}", h.tvState)

	// The audit sweep: the only fast incident lever we own (lichess has no bulk
	// revoke). Operator-gated by LICHESS_AUDIT_KEY; 404s when that is unset.
	mux.HandleFunc("POST /api/v1/lichess/audit", h.lichessAudit)

	// The archive viewer. Registered last and rooted at "/", which in Go 1.22's
	// mux is the least-specific pattern — every route above still wins. Blank
	// FRONTEND_DIR serves no web UI and changes nothing else.
	if cfg.FrontendDir != "" {
		fs := http.FileServer(http.Dir(cfg.FrontendDir))
		mux.Handle("GET /", noStoreIndex(fs))
		log.Info("serving the archive viewer", zap.String("dir", cfg.FrontendDir))
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
