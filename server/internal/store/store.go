// Package store is gamchess's data layer: every SQL statement lives here, and
// nothing here knows about HTTP.
package store

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

// ErrNotFound is returned instead of pgx.ErrNoRows so callers don't import pgx.
var ErrNotFound = errors.New("not found")

// EnsurePlayer creates the players row for steamID, which the games foreign keys
// require.
//
// bumpLastSeen must be true ONLY for an FP-verified caller. For a claimed
// SteamID — an opponent named in someone else's submission — pass false: we
// need the row to exist for the FK, but we have no evidence that account was
// actually online, so we must not pretend we saw them.
func EnsurePlayer(ctx context.Context, db *pgxpool.Pool, steamID int64, bumpLastSeen bool) error {
	q := `INSERT INTO players (steam_id) VALUES ($1) ON CONFLICT (steam_id) DO NOTHING`
	if bumpLastSeen {
		q = `INSERT INTO players (steam_id) VALUES ($1)
		     ON CONFLICT (steam_id) DO UPDATE SET last_seen = NOW()`
	}
	if _, err := db.Exec(ctx, q, steamID); err != nil {
		return fmt.Errorf("ensure player: %w", err)
	}
	return nil
}

type Game struct {
	ID           string    `json:"id"`
	ClientGameID string    `json:"client_game_id"`
	Pgn          string    `json:"pgn"`
	WhiteSteamID *int64    `json:"white_steam_id"`
	BlackSteamID *int64    `json:"black_steam_id"`
	Result       string    `json:"result"`
	PlayedAt     time.Time `json:"played_at"`
	SubmittedBy  int64     `json:"submitted_by"`
}

const gameCols = `id, client_game_id, pgn, white_steam_id, black_steam_id,
                  result, played_at, submitted_by`

func scanGame(row pgx.Row) (Game, error) {
	var g Game
	err := row.Scan(&g.ID, &g.ClientGameID, &g.Pgn, &g.WhiteSteamID, &g.BlackSteamID,
		&g.Result, &g.PlayedAt, &g.SubmittedBy)
	if errors.Is(err, pgx.ErrNoRows) {
		return Game{}, ErrNotFound
	}
	if err != nil {
		return Game{}, fmt.Errorf("scan game: %w", err)
	}
	return g, nil
}

// UpsertGame archives a game idempotently on client_game_id.
//
// Both seats may POST the same game: move history lives in each seated client's
// own ChessGame rather than the host's, so the host may have no PGN to submit
// and we can't nominate a single submitter. The second POST is a NO-OP that
// returns the stored row — deliberately not an overwrite, so a later submitter
// can't rewrite an archived game's PGN or result.
func UpsertGame(ctx context.Context, db *pgxpool.Pool, g Game) (Game, error) {
	out, err := scanGame(db.QueryRow(ctx, `
		INSERT INTO games (client_game_id, pgn, white_steam_id, black_steam_id,
		                   result, submitted_by)
		VALUES ($1, $2, $3, $4, $5, $6)
		ON CONFLICT (client_game_id) DO NOTHING
		RETURNING `+gameCols,
		g.ClientGameID, g.Pgn, g.WhiteSteamID, g.BlackSteamID,
		g.Result, g.SubmittedBy))

	if err == nil {
		return out, nil
	}
	if !errors.Is(err, ErrNotFound) {
		return Game{}, err
	}
	// DO NOTHING returned no row: this client_game_id is already archived.
	return GameByClientID(ctx, db, g.ClientGameID)
}

func GameByClientID(ctx context.Context, db *pgxpool.Pool, clientGameID string) (Game, error) {
	return scanGame(db.QueryRow(ctx,
		`SELECT `+gameCols+` FROM games WHERE client_game_id = $1`, clientGameID))
}

func GameByID(ctx context.Context, db *pgxpool.Pool, id string) (Game, error) {
	return scanGame(db.QueryRow(ctx,
		`SELECT `+gameCols+` FROM games WHERE id = $1`, id))
}

// GamesBySteamID returns a player's games, newest first, from either seat.
func GamesBySteamID(ctx context.Context, db *pgxpool.Pool, steamID int64, limit, offset int) ([]Game, error) {
	rows, err := db.Query(ctx, `
		SELECT `+gameCols+` FROM games
		WHERE white_steam_id = $1 OR black_steam_id = $1
		ORDER BY played_at DESC
		LIMIT $2 OFFSET $3`, steamID, limit, offset)
	if err != nil {
		return nil, fmt.Errorf("list games: %w", err)
	}
	defer rows.Close()

	// Non-nil so an empty archive marshals as [] rather than null.
	out := make([]Game, 0, limit)
	for rows.Next() {
		g, err := scanGame(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, g)
	}
	return out, rows.Err()
}
