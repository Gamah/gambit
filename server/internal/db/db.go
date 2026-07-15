// Package db owns the Postgres pool and the migration runner. Ported from
// rotaliate/internal/db/db.go — same shape, same reasoning.
package db

import (
	"context"
	"database/sql"
	"fmt"

	"github.com/jackc/pgx/v5/pgxpool"
	// Registered solely so goose (which speaks database/sql) can reuse the same
	// DSN as the pgxpool above. Nothing else in gamchess uses database/sql.
	_ "github.com/jackc/pgx/v5/stdlib"
	"github.com/pressly/goose/v3"
)

func Connect(dsn string) (*pgxpool.Pool, error) {
	pool, err := pgxpool.New(context.Background(), dsn)
	if err != nil {
		return nil, fmt.Errorf("pgxpool.New: %w", err)
	}
	if err := pool.Ping(context.Background()); err != nil {
		return nil, fmt.Errorf("db ping: %w", err)
	}
	return pool, nil
}

// Migrate runs goose in-process at startup, reading migrations from the
// filesystem (the Dockerfile copies migrations/ into the image). This is what
// makes deploy `git pull && make up` with no separate migrate step.
func Migrate(dsn string) error {
	db, err := sql.Open("pgx", dsn)
	if err != nil {
		return fmt.Errorf("sql.Open: %w", err)
	}
	defer db.Close()

	if err := goose.SetDialect("postgres"); err != nil {
		return err
	}
	return goose.Up(db, "migrations")
}
