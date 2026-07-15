// Package api owns gamchess's HTTP surface: stdlib net/http with Go 1.22
// method-pattern routing, no framework. Handlers hang off one dependency-injected
// handler struct — no global state.
package api

import (
	"encoding/json"
	"net/http"
	"time"

	"github.com/gamah/gambit/server/internal/relay"
	"github.com/jackc/pgx/v5/pgxpool"
	"go.uber.org/zap"
)

// OAuth timings. The state outlives the code because it has to cover a human
// reading a lichess consent screen; the code only has to survive the client's
// next poll. Both are far shorter than lichess's own ~1min code TTL matters for.
const (
	stateTTL = 5 * time.Minute
	codeTTL  = 2 * time.Minute
)

type handler struct {
	db      *pgxpool.Pool
	log     *zap.Logger
	version string

	// baseURL is the public root gamchess is served at — the root of the OAuth
	// redirect_uri. Blank disables the lichess code relay.
	baseURL string

	// relay is in-memory only, by design: it holds OAuth codes, which have no
	// business being persisted.
	relay *relay.Store
}

func NewRouter(db *pgxpool.Pool, log *zap.Logger, version, baseURL string) *http.ServeMux {
	h := &handler{
		db:      db,
		log:     log,
		version: version,
		baseURL: baseURL,
		relay:   relay.New(stateTTL, codeTTL),
	}

	mux := http.NewServeMux()

	// Liveness. Deliberately unwrapped: no auth, no rate limit.
	mux.HandleFunc("GET /health", h.health)

	// lichess OAuth code relay. /callback is the browser's landing spot and takes
	// no auth; the other two are FP-token-gated.
	mux.HandleFunc("POST /api/v1/auth/lichess/begin", h.lichessBegin)
	mux.HandleFunc("GET /api/v1/auth/lichess/code", h.lichessCode)
	mux.HandleFunc("GET /callback", h.lichessCallback)

	// Game archive. Reads are public — PGNs are public chess — but writes are
	// FP-gated and you may only archive a game you sat in.
	mux.HandleFunc("POST /api/v1/games", h.postGame)
	mux.HandleFunc("GET /api/v1/games", h.listGames)
	mux.HandleFunc("GET /api/v1/games/{id}", h.getGame)

	// lichess identity link.
	mux.HandleFunc("PUT /api/v1/links/lichess", h.putLichessLink)
	mux.HandleFunc("DELETE /api/v1/links/lichess", h.deleteLichessLink)

	return mux
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
