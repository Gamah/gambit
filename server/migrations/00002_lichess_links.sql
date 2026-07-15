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
--
-- THE GUARDED DROP BELOW IS DELIBERATE, AND SO IS THE GUARD.
--
-- Some databases carry an ORPHANED lichess_links from the abandoned first M8
-- design — (steam_id, lichess_username, linked_at): an identity-only link with
-- no token, no lichess_id, no scopes. goose never recorded it (chess.gamah.net
-- had exactly this, with goose still at version 1), so a plain CREATE dies with
-- 42P07 and takes the server into a startup restart loop.
--
-- The guard is what makes this safe to leave in the tree forever. It drops the
-- table ONLY when it lacks token_enc — i.e. only when it is provably the dead
-- design, which cannot hold anything the current code could ever read. A real
-- lichess_links, with real encrypted tokens in it, can never match and can never
-- be dropped by this. That matters more than it looks: goose runs a migration
-- once, so an unconditional DROP would normally be safe too — but "normally"
-- stops being true the moment anyone resets goose_db_version, and a migration
-- that would nuke every player's link in that case is a landmine, not a fix.
--
-- What we deliberately do NOT do is `CREATE TABLE IF NOT EXISTS`. That would let
-- the token-less table survive, the migration would "succeed", and the server
-- would start and then fail on every query against a column that doesn't exist —
-- trading a loud crash at startup for a silent one at runtime.
-- -----------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.tables
        WHERE table_schema = 'public' AND table_name = 'lichess_links'
    ) AND NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'lichess_links'
          AND column_name = 'token_enc'
    ) THEN
        RAISE NOTICE 'dropping the abandoned token-less lichess_links';
        DROP TABLE lichess_links;
    END IF;
END $$;

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
