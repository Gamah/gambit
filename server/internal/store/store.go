// Package store is gamchess's data layer: every SQL statement lives here, and
// nothing here knows about HTTP.
//
// Note what is absent and must stay absent: there is no lichess token anywhere
// in this package, because there is no column for one. gamchess relays OAuth
// codes (in memory, see internal/relay) and the client does the token exchange
// itself. If a function here ever grows a `token` parameter, something has gone
// badly wrong.
package store

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgxpool"
)

var (
	// ErrNotFound is returned instead of pgx.ErrNoRows so callers don't import pgx.
	ErrNotFound = errors.New("not found")
	// ErrUsernameTaken means a different SteamID already claims this lichess
	// username (the lower(lichess_username) unique index).
	ErrUsernameTaken = errors.New("lichess username already linked to another account")
)

// EnsurePlayer creates the players row for steamID, which the games and
// lichess_links foreign keys require.
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
	ID            string    `json:"id"`
	ClientGameID  string    `json:"client_game_id"`
	Pgn           string    `json:"pgn"`
	WhiteSteamID  *int64    `json:"white_steam_id"`
	BlackSteamID  *int64    `json:"black_steam_id"`
	Result        string    `json:"result"`
	PlayedAt      time.Time `json:"played_at"`
	LichessGameID *string   `json:"lichess_game_id"`
	SubmittedBy   int64     `json:"submitted_by"`
}

const gameCols = `id, client_game_id, pgn, white_steam_id, black_steam_id,
                  result, played_at, lichess_game_id, submitted_by`

func scanGame(row pgx.Row) (Game, error) {
	var g Game
	err := row.Scan(&g.ID, &g.ClientGameID, &g.Pgn, &g.WhiteSteamID, &g.BlackSteamID,
		&g.Result, &g.PlayedAt, &g.LichessGameID, &g.SubmittedBy)
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
		                   result, lichess_game_id, submitted_by)
		VALUES ($1, $2, $3, $4, $5, $6, $7)
		ON CONFLICT (client_game_id) DO NOTHING
		RETURNING `+gameCols,
		g.ClientGameID, g.Pgn, g.WhiteSteamID, g.BlackSteamID,
		g.Result, g.LichessGameID, g.SubmittedBy))

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

// UpsertLichessLink records the SteamID -> lichess username link.
//
// The username is public (it comes from lichess's own /api/account), but the
// PAIRING is durable identity data Gambit has never held before: never log it,
// and keep DeleteLichessLink reachable.
func UpsertLichessLink(ctx context.Context, db *pgxpool.Pool, steamID int64, username string) error {
	_, err := db.Exec(ctx, `
		INSERT INTO lichess_links (steam_id, lichess_username) VALUES ($1, $2)
		ON CONFLICT (steam_id) DO UPDATE
		  SET lichess_username = EXCLUDED.lichess_username, linked_at = NOW()`,
		steamID, username)

	var pgErr *pgconn.PgError
	// 23505 here can only be lichess_links_username_uniq: the steam_id conflict is
	// handled above. Match the structured SQLSTATE, not the driver's message text.
	if errors.As(err, &pgErr) && pgErr.Code == "23505" {
		return ErrUsernameTaken
	}
	if err != nil {
		return fmt.Errorf("upsert lichess link: %w", err)
	}
	return nil
}

// DeleteLichessLink unlinks. Reports whether a link existed.
func DeleteLichessLink(ctx context.Context, db *pgxpool.Pool, steamID int64) (bool, error) {
	tag, err := db.Exec(ctx, `DELETE FROM lichess_links WHERE steam_id = $1`, steamID)
	if err != nil {
		return false, fmt.Errorf("delete lichess link: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}
