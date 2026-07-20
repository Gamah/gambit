package store

import (
	"context"
	"fmt"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
)

// The lichess_key_versions table (M15): the rotating data keys (DEKs), each
// stored ONLY as ciphertext sealed under the KEK. Same rule as the rest of this
// package — every SQL statement lives here, and this layer never sees a plaintext
// key. The DEK arrives already sealed and leaves still sealed; unwrapping it is
// keyring's job, not the store's.

// KeyVersion is one row of lichess_key_versions: a data key, sealed under the
// KEK, and the metadata that decides which one is current.
type KeyVersion struct {
	Version   int32
	DEKEnc    []byte // the 32-byte data key, AES-256-GCM ciphertext under the KEK
	DEKNonce  []byte
	CreatedAt time.Time
	RetiredAt *time.Time // nil = eligible to be current; set = superseded and unused
}

const keyVersionCols = `version, dek_enc, dek_nonce, created_at, retired_at`

// AllKeyVersions returns every data-key row, oldest first. The caller unwraps
// each under the KEK at boot and keeps them all — including retired ones — so a
// token row referencing any past version can always be opened.
func AllKeyVersions(ctx context.Context, db *pgxpool.Pool) ([]KeyVersion, error) {
	rows, err := db.Query(ctx, `SELECT `+keyVersionCols+` FROM lichess_key_versions ORDER BY version`)
	if err != nil {
		return nil, fmt.Errorf("list key versions: %w", err)
	}
	defer rows.Close()

	var out []KeyVersion
	for rows.Next() {
		var k KeyVersion
		if err := rows.Scan(&k.Version, &k.DEKEnc, &k.DEKNonce, &k.CreatedAt, &k.RetiredAt); err != nil {
			return nil, fmt.Errorf("scan key version: %w", err)
		}
		out = append(out, k)
	}
	return out, rows.Err()
}

// InsertKeyVersion stores a freshly generated data key. The version is assigned
// by the caller (keyring), not by the database, because "which version is
// current" is application state that keyring owns; the PRIMARY KEY just enforces
// that two callers can't both claim the same number.
func InsertKeyVersion(ctx context.Context, db *pgxpool.Pool, version int32, dekEnc, dekNonce []byte) error {
	_, err := db.Exec(ctx,
		`INSERT INTO lichess_key_versions (version, dek_enc, dek_nonce) VALUES ($1, $2, $3)`,
		version, dekEnc, dekNonce)
	if err != nil {
		return fmt.Errorf("insert key version: %w", err)
	}
	return nil
}

// ReWrapKeyVersion replaces a data key's ciphertext in place — same plaintext
// DEK, sealed under a new KEK. This is the KEK-rotation path (M15): when
// LICHESS_TOKEN_KEY_OLD unwraps a row that LICHESS_TOKEN_KEY can't, keyring
// re-seals it under the new KEK and calls this so the old KEK can be dropped on
// the next deploy. It never touches the token rows — that is the whole point of
// the envelope: re-keying the KEK rewrites these few rows, not every token.
func ReWrapKeyVersion(ctx context.Context, db *pgxpool.Pool, version int32, dekEnc, dekNonce []byte) error {
	_, err := db.Exec(ctx,
		`UPDATE lichess_key_versions SET dek_enc = $1, dek_nonce = $2 WHERE version = $3`,
		dekEnc, dekNonce, version)
	if err != nil {
		return fmt.Errorf("re-wrap key version: %w", err)
	}
	return nil
}

// RetireUnusedKeyVersions marks every data key that is neither current nor still
// referenced by a token row. It is informational and cheap: it keeps a drained
// key from being reconsidered as current and gives an operator a clear "this key
// no longer protects anything" signal. It deliberately does NOT delete the row —
// the key is still loaded at boot so a stray reference can always be opened.
// Reports how many were newly retired.
func RetireUnusedKeyVersions(ctx context.Context, db *pgxpool.Pool, current int32) (int64, error) {
	tag, err := db.Exec(ctx, `
		UPDATE lichess_key_versions
		   SET retired_at = NOW()
		 WHERE version <> $1
		   AND retired_at IS NULL
		   AND version NOT IN (SELECT DISTINCT key_version FROM lichess_links)`,
		current)
	if err != nil {
		return 0, fmt.Errorf("retire unused key versions: %w", err)
	}
	return tag.RowsAffected(), nil
}
