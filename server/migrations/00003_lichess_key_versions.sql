-- +goose Up
-- +goose StatementBegin

-- -----------------------------------------------------------------------
-- Lichess token key rotation (M15) — envelope encryption
--
-- Before this, every row in lichess_links was sealed DIRECTLY under
-- LICHESS_TOKEN_KEY, and there was no way to change that key without turning
-- every stored token into an undecryptable row (the "no rotation path" open
-- spike that CLAUDE.md, the Makefile and .env.example all warned about).
--
-- M15 splits the one key into two levels:
--
--   * LICHESS_TOKEN_KEY becomes a KEY-ENCRYPTION KEY (KEK). It never seals a
--     player token directly anymore; it seals the data keys below. It stays a
--     static env secret and is still the one thing to back up — but it now only
--     ever wraps a handful of rows in THIS table, so re-keying it is cheap
--     (re-wrap these rows) instead of impossible (re-encrypt every token).
--
--   * Each row here is a DATA-ENCRYPTION KEY (DEK): 32 random bytes, generated
--     by gamchess, stored ONLY as ciphertext sealed under the KEK. The plaintext
--     DEK exists only in the server's memory after it unwraps this row at boot.
--     gamchess rotates the DEK on a timer (LICHESS_KEY_ROTATION_DAYS) and a
--     background sweep re-encrypts token rows onto the newest DEK.
--
-- So the chain is KEK (env) -> unwraps -> DEK (here, sealed) -> unwraps ->
-- token (lichess_links, sealed). A DB dump WITHOUT the KEK reveals neither a DEK
-- nor a token. (On this deployment the KEK lives in .env on the same box as the
-- DB, so a full-box compromise still gets everything — the envelope's real value
-- is the rotate-without-orphaning capability, not extra secrecy. Recorded so
-- nobody re-derives a stronger claim from the table's mere existence.)
--
-- version is a small serial we assign in code (1, 2, 3, …), NOT a SERIAL column:
-- the value is meaningful to the application (it is stamped onto every token row
-- as key_version) and generating it in code keeps "which DEK is current" a
-- single source of truth. retired_at marks a DEK that is no longer current AND
-- that no token row still references — informational, and it keeps a retired key
-- from ever being chosen as current again. Retired rows are NOT deleted: the
-- server still loads them so a stray row referencing one can always be opened.
-- -----------------------------------------------------------------------
CREATE TABLE lichess_key_versions (
    version    INT         PRIMARY KEY,   -- assigned in code, 1-based; 0 is reserved (see below)
    dek_enc    BYTEA       NOT NULL,      -- the DEK, AES-256-GCM ciphertext sealed under the KEK
    dek_nonce  BYTEA       NOT NULL,      -- per-row GCM nonce for dek_enc
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    retired_at TIMESTAMPTZ                -- NULL = eligible to be current; set = superseded and unused
);

-- key_version stamps each token with the DEK that sealed it, so a row can always
-- be opened by the right key even mid-rotation (old rows keep their old version
-- until the sweep re-seals them onto the current DEK).
--
-- DEFAULT 0 is the migration's whole trick: 0 means "legacy — sealed directly
-- under the KEK, before M15". Every pre-M15 row gets 0 for free, and the server
-- opens a version-0 row with the KEK directly. No data migration runs at deploy;
-- the background re-encrypt sweep lifts these 0 rows onto the current DEK over
-- time. So the deploy is zero-downtime: old rows keep working, new links seal
-- under a real DEK (version >= 1), and the 0s drain away on their own.
ALTER TABLE lichess_links
    ADD COLUMN key_version INT NOT NULL DEFAULT 0;

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin

ALTER TABLE lichess_links DROP COLUMN IF EXISTS key_version;
DROP TABLE IF EXISTS lichess_key_versions;

-- +goose StatementEnd
