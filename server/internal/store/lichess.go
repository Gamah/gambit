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

// Lichess account links (M8). Same rule as the rest of this package: every SQL
// statement lives here, and nothing here knows about HTTP — or about
// encryption. The token arrives already sealed and leaves still sealed; this
// layer never sees a plaintext token, which is what keeps "encrypted before any
// INSERT" a property you can check by reading one file.

// ErrLichessIDTaken means the lichess account is already linked to a DIFFERENT
// Steam account. Callers answer 409 — never a silent steal, and never a silent
// no-op.
var ErrLichessIDTaken = errors.New("that lichess account is linked to another Steam account")

// LichessLink is one row of lichess_links.
type LichessLink struct {
	SteamID    int64
	LichessID  string // canonical lowercase id from /api/account — the identity
	Username   string // display casing — cosmetic
	TokenEnc   []byte // AES-256-GCM ciphertext, never plaintext
	TokenNonce []byte
	// KeyVersion is which key sealed TokenEnc (M15): 0 = the legacy pre-M15 rows
	// sealed directly under the KEK, >= 1 = the data key of that version. The
	// caller (keyring) needs it to pick the right key to Open with.
	KeyVersion int32
	Scopes     string
	LinkedAt   time.Time
}

const lichessCols = `steam_id, lichess_id, username, token_enc, token_nonce, key_version, scopes, linked_at`

func scanLink(row pgx.Row) (LichessLink, error) {
	var l LichessLink
	err := row.Scan(&l.SteamID, &l.LichessID, &l.Username, &l.TokenEnc, &l.TokenNonce,
		&l.KeyVersion, &l.Scopes, &l.LinkedAt)
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
// Re-linking the SAME player replaces their row — that is the only renewal path
// lichess offers, since its tokens have no refresh and just expire after ~a
// year.
//
// Linking a lichess account that ANOTHER Steam account already holds is
// ErrLichessIDTaken (→ 409). The UNIQUE(lichess_id) constraint is what makes
// that a race-proof check rather than a check-then-write with a window in it:
// two simultaneous links of the same lichess account can't both win.
func UpsertLichessLink(ctx context.Context, db *pgxpool.Pool, l LichessLink) (LichessLink, error) {
	out, err := scanLink(db.QueryRow(ctx, `
		INSERT INTO lichess_links (steam_id, lichess_id, username, token_enc, token_nonce, key_version, scopes)
		VALUES ($1, $2, $3, $4, $5, $6, $7)
		ON CONFLICT (steam_id) DO UPDATE SET
			lichess_id  = EXCLUDED.lichess_id,
			username    = EXCLUDED.username,
			token_enc   = EXCLUDED.token_enc,
			token_nonce = EXCLUDED.token_nonce,
			key_version = EXCLUDED.key_version,
			scopes      = EXCLUDED.scopes,
			linked_at   = NOW()
		RETURNING `+lichessCols,
		l.SteamID, l.LichessID, l.Username, l.TokenEnc, l.TokenNonce, l.KeyVersion, l.Scopes))

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

// DeleteLichessLink removes a link. Reports whether a row actually went — the
// unlink handler uses that to answer honestly rather than claim success for a
// link that was never there.
func DeleteLichessLink(ctx context.Context, db *pgxpool.Pool, steamID int64) (bool, error) {
	tag, err := db.Exec(ctx, `DELETE FROM lichess_links WHERE steam_id = $1`, steamID)
	if err != nil {
		return false, fmt.Errorf("delete lichess link: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}

// AllLichessLinks returns every link, for the audit sweep — the only fast
// incident lever gamchess owns (lichess has no bulk revoke). Not paginated: it
// exists to be fed to lichess's token test in batches of 1000, and a store big
// enough for that to hurt is a store worth paginating deliberately.
func AllLichessLinks(ctx context.Context, db *pgxpool.Pool) ([]LichessLink, error) {
	rows, err := db.Query(ctx, `SELECT `+lichessCols+` FROM lichess_links ORDER BY linked_at`)
	if err != nil {
		return nil, fmt.Errorf("list lichess links: %w", err)
	}
	defer rows.Close()

	var out []LichessLink
	for rows.Next() {
		l, err := scanLink(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, l)
	}
	return out, rows.Err()
}

// LinksNeedingReEncrypt returns links whose token is NOT sealed under the current
// key version — the work list for the M15 re-encrypt sweep. That is every legacy
// (version 0) row plus anything sealed under a now-superseded data key.
//
// Paged by a steam_id CURSOR (afterSteamID, exclusive), not by OFFSET: the sweep
// advances the cursor past every row it fetches whether or not that row could be
// re-sealed, so a row that fails to decrypt is skipped rather than re-fetched
// forever. A whole giant store never lands in one result set, and one bad row
// can't spin the sweep.
func LinksNeedingReEncrypt(ctx context.Context, db *pgxpool.Pool, current int32, afterSteamID int64, limit int) ([]LichessLink, error) {
	rows, err := db.Query(ctx,
		`SELECT `+lichessCols+` FROM lichess_links
		  WHERE key_version <> $1 AND steam_id > $2 ORDER BY steam_id LIMIT $3`,
		current, afterSteamID, limit)
	if err != nil {
		return nil, fmt.Errorf("list links needing re-encrypt: %w", err)
	}
	defer rows.Close()

	var out []LichessLink
	for rows.Next() {
		l, err := scanLink(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, l)
	}
	return out, rows.Err()
}

// ReEncryptLinkToken rewrites one link's sealed token to a new key version, but
// ONLY if it still carries the version the sweep read (fromVersion). That guard
// is what makes the sweep safe to run against a live server: a player who
// re-links mid-sweep writes a fresh token at the CURRENT version, and this
// conditional update then no-ops on the stale row rather than clobbering their
// new token with a re-seal of the old one. Reports whether the row was updated.
func ReEncryptLinkToken(ctx context.Context, db *pgxpool.Pool, steamID int64,
	tokenEnc, tokenNonce []byte, toVersion, fromVersion int32) (bool, error) {
	tag, err := db.Exec(ctx, `
		UPDATE lichess_links
		   SET token_enc = $1, token_nonce = $2, key_version = $3
		 WHERE steam_id = $4 AND key_version = $5`,
		tokenEnc, tokenNonce, toVersion, steamID, fromVersion)
	if err != nil {
		return false, fmt.Errorf("re-encrypt link token: %w", err)
	}
	return tag.RowsAffected() > 0, nil
}
