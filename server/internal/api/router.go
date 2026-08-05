// Package api owns gamchess's HTTP surface: stdlib net/http with Go 1.22
// method-pattern routing, no framework. Handlers hang off one dependency-injected
// handler struct — no global state.
package api

import (
	"context"
	"encoding/json"
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

	// Lichess (M8, rebuilt by HTTPFIX). NOTE WHAT IS NOT HERE ANY MORE: a key
	// ring, a cipher, a token store and a play relay. The client holds its own
	// token and talks to lichess itself, so this process holds no lichess secret
	// and needs no key to protect one. The whole feature is gated on baseURL
	// alone — a redirect URI to come back to.
	//
	// There is no client-id field either: lichess.ClientID is a constant, because
	// lichess records the redirect ORIGIN on a token and never the client_id.
	links      *linkSlots
	rendezvous *rendezvous

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
}

func NewRouter(db *pgxpool.Pool, log *zap.Logger, cfg Config) *http.ServeMux {
	h := &handler{
		db:       db,
		log:      log,
		version:  cfg.Version,
		baseURL:  strings.TrimSuffix(cfg.BaseURL, "/"),
		sessions: newSessions(cfg.SessionSecret),
		nonces:   newNonceStore(openidNonceTTL),
	}
	h.links = newLinkSlots(linkTTL)
	h.rendezvous = newRendezvous()

	if h.baseURL == "" {
		log.Warn("PUBLIC_BASE_URL not set — lichess linking is disabled (no redirect URI)")
	} else {
		log.Info("lichess linking enabled",
			zap.String("client_id", lichess.ClientID),
			zap.String("redirect_uri", h.lichessRedirectURL()),
			zap.String("scopes", lichess.Scopes))
	}
	h.tv = newTv(log)

	// Reap matchmaking adverts whose opener vanished without cancelling (M19).
	go h.runMatchmakingSweep(context.Background())

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

	// Matchmaking (M19): the directory of solo players open to a game. gamchess
	// pairs them and assigns a RANDOM colour; it does not run 'join'-mode games
	// (the joiner Networking.Connect()s into the opener's lobby). Session-gated —
	// an open advert is attributable to a verified SteamID.
	mux.HandleFunc("POST /api/v1/matchmaking", h.postMatchmaking)
	mux.HandleFunc("GET /api/v1/matchmaking", h.listMatchmaking)
	mux.HandleFunc("GET /api/v1/matchmaking/{id}", h.getMatchmaking)
	mux.HandleFunc("POST /api/v1/matchmaking/{id}/join", h.joinMatchmaking)
	mux.HandleFunc("DELETE /api/v1/matchmaking/{id}", h.deleteMatchmaking)

	// Relay games (M19, 'relay' mode): a gamchess-authoritative live game between
	// two players who never share a lobby. The one game type that REQUIRES gamchess.
	// Membership-gated: only the two players.
	mux.HandleFunc("GET /api/v1/relaygame/{id}", h.getRelayGame)
	mux.HandleFunc("POST /api/v1/relaygame/{id}/move", h.postRelayMove)
	mux.HandleFunc("POST /api/v1/relaygame/{id}/{action}", h.postRelayAction)

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

	// Lichess, from in-game. FP-token/session gated.
	//
	// NOTE WHAT IS GONE: /play, /seek, /challenge, /open, the play long poll and
	// its action route. Those were the relay — gamchess playing lichess games on
	// a player's behalf because the s&box client could not read a stream. The
	// client does that itself now, straight to lichess, with its own token.
	//
	// `linked` and `username` keep their JSON names on the status route: gamchess
	// deploys before the s&box package updates, so an OLD client polls it for a
	// window and must degrade rather than null out.
	mux.HandleFunc("GET /api/v1/lichess", h.lichessStatus)
	mux.HandleFunc("DELETE /api/v1/lichess", h.lichessUnlink)

	// The link flow's in-game half: register a PKCE challenge, then collect the
	// code the browser parked, then claim an identity with the resulting token.
	// gamchess never sees the verifier, so a parked code is inert in its hands.
	mux.HandleFunc("POST /api/v1/lichess/link/start", h.lichessLinkStart)
	mux.HandleFunc("POST /api/v1/lichess/link/collect", h.lichessLinkCollect)
	mux.HandleFunc("POST /api/v1/lichess/claim", h.lichessClaim)

	// The directory: two seats that have BOTH posted an intent for the same
	// client_game_id learn each other's lichess username, so one can challenge
	// the other by name. The two-intent rule survives as a disclosure rule — see
	// lichess.go — not as a consent one.
	mux.HandleFunc("POST /api/v1/lichess/rendezvous", h.lichessRendezvous)

	// lichess TV (M9, WebSocket push since M18), for the spectator wall.
	// Session-gated like everything else — anonymous upstream is exactly why an open
	// relay here would be attractive to abuse, and lichess would see our IP and our
	// User-Agent on all of it.
	//
	// One upstream stream per CHANNEL regardless of how many clients connect: that
	// invariant is the whole reason this is proxied rather than hit directly. The
	// {channel} route is a WebSocket upgrade (tvSocket) that pushes a full snapshot
	// per state change; /channels stays a plain JSON GET. Go 1.22's more-specific-wins
	// keeps the literal ahead of {channel}, so the ordering is already correct.
	mux.HandleFunc("GET /api/v1/tv/channels", h.tvChannels)
	mux.HandleFunc("GET /api/v1/tv/{channel}", h.tvSocket)

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
