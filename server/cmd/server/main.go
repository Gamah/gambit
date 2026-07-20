// Command server is gamchess: Terry's Gambit's backend. It carries server-side
// identity (Steam — a Facepunch auth token in-game, OpenID on the web), a durable
// game archive, and the web viewer for it.
package main

import (
	"context"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/gamah/gambit/server/internal/api"
	"github.com/gamah/gambit/server/internal/db"
	"go.uber.org/zap"
)

// version is injected at build time via -ldflags "-X main.version=<git-hash>".
// Falls back to "dev" for local builds without ldflags.
var version = "dev"

func main() {
	log, _ := zap.NewProduction()
	defer log.Sync()

	// Config is read here and only here — os.Getenv in main.go, no config lib.
	// Optional keys degrade to "feature off" with a warning; DATABASE_URL is the
	// only fatal one.
	dsn := os.Getenv("DATABASE_URL")
	if strings.TrimSpace(dsn) == "" {
		log.Fatal("DATABASE_URL is required")
	}

	pool, err := db.Connect(dsn)
	if err != nil {
		log.Fatal("failed to connect to database", zap.Error(err))
	}
	defer pool.Close()

	// Migrations run in-process at startup and are fatal on failure, so deploy
	// is `git pull && make up` with no separate migrate step.
	if err := db.Migrate(dsn); err != nil {
		log.Fatal("migration failed", zap.Error(err))
	}

	// PUBLIC_BASE_URL is the public root gamchess is served at — the Steam OpenID
	// realm and return root. Blank disables web sign-in, which gates the viewer.
	baseURL := strings.TrimSpace(os.Getenv("PUBLIC_BASE_URL"))
	if baseURL == "" {
		log.Warn("PUBLIC_BASE_URL not set — Steam web sign-in disabled")
	}

	// FRONTEND_DIR holds the archive viewer's static files. Blank disables the web
	// UI; the API and the relay are unaffected.
	frontendDir := strings.TrimSpace(os.Getenv("FRONTEND_DIR"))

	// SESSION_SECRET signs the viewer's session cookies. Blank is safe — a random
	// key is generated per process — it just means a restart signs everyone out.
	sessionSecret := strings.TrimSpace(os.Getenv("SESSION_SECRET"))
	if sessionSecret == "" {
		log.Warn("SESSION_SECRET not set — web sessions will not survive a restart")
	}

	// LICHESS_TOKEN_KEY is the KEK: it wraps the rotating data keys that seal the
	// board:play tokens at rest (32 bytes, base64 or hex). Blank switches lichess
	// off entirely — the router warns and starts. It is never a fallback to
	// plaintext: gamchess holding a plaintext token store is the one outcome that
	// must not be reachable by forgetting a config key.
	lichessTokenKey := strings.TrimSpace(os.Getenv("LICHESS_TOKEN_KEY"))

	// LICHESS_TOKEN_KEY_OLD is the PREVIOUS KEK, set only while rotating the KEK
	// (M15). For one deploy it lets gamchess re-wrap any data key the new KEK can't
	// open; drop it once the logs show the re-wrap happened. Blank normally.
	lichessTokenKeyOld := strings.TrimSpace(os.Getenv("LICHESS_TOKEN_KEY_OLD"))

	// LICHESS_KEY_ROTATION_DAYS is how often the data key rotates. Blank ⇒ 30. "0"
	// (or anything non-positive) disables timed rotation — versioning and the
	// legacy-row migration still run, there is just no automatic re-key.
	rotationDays := 30
	if v := strings.TrimSpace(os.Getenv("LICHESS_KEY_ROTATION_DAYS")); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			rotationDays = n
		} else {
			log.Warn("LICHESS_KEY_ROTATION_DAYS is not a number — using the default 30",
				zap.String("value", v))
		}
	}

	// LICHESS_AUDIT_KEY gates the token-audit sweep — the only fast incident
	// lever we own, since lichess has no bulk revoke. Blank hides the route.
	lichessAuditKey := strings.TrimSpace(os.Getenv("LICHESS_AUDIT_KEY"))

	mux := api.NewRouter(pool, log, api.Config{
		Version:            version,
		BaseURL:            baseURL,
		FrontendDir:        frontendDir,
		SessionSecret:      sessionSecret,
		LichessTokenKey:    lichessTokenKey,
		LichessTokenKeyOld: lichessTokenKeyOld,
		KeyRotationDays:    rotationDays,
		LichessAuditKey:    lichessAuditKey,
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "6464"
	}

	srv := &http.Server{
		Addr:         fmt.Sprintf(":%s", port),
		Handler:      mux,
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 30 * time.Second,
		IdleTimeout:  60 * time.Second,
	}

	go func() {
		log.Info("server starting", zap.String("port", port), zap.String("version", version))
		if err := srv.ListenAndServe(); err != nil && err != http.ErrServerClosed {
			log.Fatal("server error", zap.Error(err))
		}
	}()

	quit := make(chan os.Signal, 1)
	signal.Notify(quit, syscall.SIGINT, syscall.SIGTERM)
	<-quit

	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	if err := srv.Shutdown(ctx); err != nil {
		log.Fatal("shutdown error", zap.Error(err))
	}
	log.Info("server stopped")
}
