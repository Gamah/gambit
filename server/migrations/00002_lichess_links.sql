-- +goose Up
-- +goose StatementBegin

-- -----------------------------------------------------------------------
-- Lichess account links (M8)
--
-- One Steam account ↔ one lichess account, both ways: PRIMARY KEY(steam_id)
-- stops a player linking two lichess accounts, UNIQUE(lichess_id) stops one
-- lichess account being claimed by two Steam accounts. Re-linking replaces via
-- ON CONFLICT (steam_id) DO UPDATE; a lichess_id already bound to a DIFFERENT
-- steam_id is a 409, never a silent steal.
--
-- THE TOKEN LIVES HERE, and that is a deliberate, analysed decision — the
-- "position 2" custody call recorded in CLAUDE.md. Playing a game on lichess
-- requires holding two long-lived ndjson streams open, and the s&box client
-- cannot read a stream at all (Sandbox.Http buffers the whole body and
-- HttpCompletionOption is off the API whitelist). Whoever reads the stream must
-- hold the token, so today that can only be gamchess.
--
-- Because the token is here, it is encrypted at rest: AES-256-GCM under
-- LICHESS_TOKEN_KEY, with a per-row nonce. There is no plaintext token column
-- and there must never be one. A blank key means the whole lichess feature is
-- off — we never fall back to storing plaintext.
--
-- steam_id is only ever a SteamID64 that Facepunch echoed back (in-game) or that
-- Steam OpenID asserted (on the web) — same rule as every other table here.
-- -----------------------------------------------------------------------
CREATE TABLE lichess_links (
    steam_id    BIGINT      PRIMARY KEY REFERENCES players(steam_id),
    lichess_id  TEXT        NOT NULL UNIQUE,  -- canonical lowercase id from /api/account
    username    TEXT        NOT NULL,         -- display casing, cosmetic only
    token_enc   BYTEA       NOT NULL,         -- board:play token, AES-256-GCM ciphertext
    token_nonce BYTEA       NOT NULL,         -- per-row GCM nonce
    scopes      TEXT        NOT NULL,         -- what lichess actually granted (audit trail)
    linked_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin

DROP TABLE IF EXISTS lichess_links;

-- +goose StatementEnd
