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

// Lichess account links. Same rule as the rest of this package: every SQL
// statement lives here, and nothing here knows about HTTP.
//
// SINCE HTTPFIX THIS TABLE HOLDS NO TOKEN. It is an identity row and nothing
// else: this Steam account plays lichess as this lichess account. The token
// lives on the player's own machine, and gamchess ends with no lichess secrets —
// no ciphertext column, no nonce, no key version, no scope string, and no key
// ring behind them. If a future change wants to put a credential back in here,
// that is a reversal of the custody decision and not a schema tweak.

// ErrLichessIDTaken means the lichess account is already linked to a DIFFERENT
// Steam account. Callers answer 409 — never a silent steal, and never a silent
// no-op.
var ErrLichessIDTaken = errors.New("that lichess account is linked to another Steam account")

// LichessLink is one row of lichess_links.
//
// LichessID is AUTHORITATIVE, not a claim: it is what lichess itself returned
// from GET /api/account for the token the player minted, resolved from the
// bearer rather than asserted by anyone. That is what keeps the plain
// UNIQUE(lichess_id) safe — an asserted id would let a liar squat a real
// account's row and lock its owner out of ever linking.
type LichessLink struct {
	SteamID   int64
	LichessID string // canonical lowercase id from /api/account — the identity
	Username  string // display casing — cosmetic
	LinkedAt  time.Time
}

const lichessCols = `steam_id, lichess_id, username, linked_at`

func scanLink(row pgx.Row) (LichessLink, error) {
	var l LichessLink
	err := row.Scan(&l.SteamID, &l.LichessID, &l.Username, &l.LinkedAt)
	if errors.Is(err, pgx.ErrNoRows) {
		return LichessLink{}, ErrNotFound
	}
	if err != nil {
		return LichessLink{}, fmt.Errorf("scan lichess link: %w", err)
	}
	return l, nil
}

// UpsertLichessLink links (or re-links) a Steam account to a lichess account.
//
// Re-linking the SAME player replaces their row. The UNIQUE(lichess_id)
// constraint is what makes the "already taken" check race-proof rather than a
// check-then-write with a window in it: two simultaneous links of the same
// lichess account can't both win.
func UpsertLichessLink(ctx context.Context, db *pgxpool.Pool, l LichessLink) (LichessLink, error) {
	out, err := scanLink(db.QueryRow(ctx, `
		INSERT INTO lichess_links (steam_id, lichess_id, username)
		VALUES ($1, $2, $3)
		ON CONFLICT (steam_id) DO UPDATE SET
			lichess_id = EXCLUDED.lichess_id,
			username   = EXCLUDED.username,
			linked_at  = NOW()
		RETURNING `+lichessCols,
		l.SteamID, l.LichessID, l.Username))

	if err == nil {
		return out, nil
	}

	// 23505 = unique_violation. The steam_id conflict is handled by DO UPDATE
	// above, so the only unique constraint left to break is lichess_id — someone
	// else already holds this lichess account.
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && pgErr.Code == "23505" {
		return LichessLink{}, ErrLichessIDTaken
	}
	return LichessLink{}, err
}

// LichessLinkBySteamID returns a player's link, or ErrNotFound.
func LichessLinkBySteamID(ctx context.Context, db *pgxpool.Pool, steamID int64) (LichessLink, error) {
	return scanLink(db.QueryRow(ctx,
		`SELECT `+lichessCols+` FROM lichess_links WHERE steam_id = $1`, steamID))
}

// LichessLinksBySteamIDs returns the links for a set of players, keyed by
// SteamID, skipping any that aren't linked.
//
// This is the DIRECTORY read, and it exists for exactly one caller: the paired
// rendezvous, where two seats that have BOTH posted an intent for the same
// client_game_id learn each other's lichess username so one can challenge the
// other by name. It is deliberately not reachable any other way — a player's
// lichess username is not something gamchess hands out to whoever asks.
func LichessLinksBySteamIDs(ctx context.Context, db *pgxpool.Pool, steamIDs []int64) (map[int64]LichessLink, error) {
	out := make(map[int64]LichessLink, len(steamIDs))
	if len(steamIDs) == 0 {
		return out, nil
	}
	rows, err := db.Query(ctx,
		`SELECT `+lichessCols+` FROM lichess_links WHERE steam_id = ANY($1)`, steamIDs)
	if err != nil {
		return nil, fmt.Errorf("list lichess links: %w", err)
	}
	defer rows.Close()

	for rows.Next() {
		l, err := scanLink(rows)
		if err != nil {
			return nil, err
		}
		out[l.SteamID] = l
	}
	return out, rows.Err()
}

// DeleteLichessLink removes a link. Reports whether a row actually went — the
// unlink handler uses that to answer honestly rather than claim success for a
// link that was never there.
//
// NOTE what this no longer does: revoke. The token is the client's, and
// DELETE /api/token must be signed BY that token, so only the client can kill
// it. gamchess just forgets the row — which is finally the honest division of
// labour rather than the best-effort revoke it used to attempt.
func DeleteLichessLink(ctx context.Context, db *pgxpool.Pool, steamID int64) (bool, error) {
	tag, err := db.Exec(ctx, `DELETE FROM lichess_links WHERE steam_id = $1`, steamID)
	if err != nil {
		return false, fmt.Errorf("delete lichess link: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}
