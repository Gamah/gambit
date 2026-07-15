// Package api owns gamchess's HTTP surface: stdlib net/http with Go 1.22
// method-pattern routing, no framework. Handlers hang off one dependency-injected
// handler struct — no global state.
package api

import (
	"encoding/json"
	"net/http"

	"github.com/jackc/pgx/v5/pgxpool"
	"go.uber.org/zap"
)

type handler struct {
	db      *pgxpool.Pool
	log     *zap.Logger
	version string
}

// NewRouter wires the routes. baseURL is the public root gamchess is reached at
// (the OAuth redirect_uri root); blank disables the lichess code relay.
func NewRouter(db *pgxpool.Pool, log *zap.Logger, version, baseURL string) *http.ServeMux {
	h := &handler{db: db, log: log, version: version}

	mux := http.NewServeMux()

	// Liveness. Deliberately unwrapped: no auth, no rate limit.
	mux.HandleFunc("GET /health", h.health)

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
