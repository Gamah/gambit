-- +goose Up
-- +goose StatementBegin

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- -----------------------------------------------------------------------
-- Players
--
-- SteamID64 is the universal player key, exactly as it already is in the
-- s&box client (ChessStation.WhiteSeatSteamId / BlackSeatSteamId, where 0
-- means "empty seat", never "identity-less player"). gamchess has no username
-- of its own — names come from Steam and lichess.
--
-- steam_id here is ONLY ever the SteamID64 echoed back by Facepunch's verify
-- endpoint. A SteamID from a request body or query string is an unverified
-- claim and must never authorise anything.
-- -----------------------------------------------------------------------
CREATE TABLE players (
    steam_id   BIGINT      PRIMARY KEY,
    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- -----------------------------------------------------------------------
-- Lichess links
--
-- There is deliberately NO token column here, and there never will be.
-- gamchess relays OAuth authorization codes only: the PKCE code_verifier is
-- generated on the client, never leaves it, and the client exchanges the code
-- for the bearer itself against lichess.org. This is a structural guarantee,
-- not a policy one — there is no column to leak and no server-side exchange
-- path to abuse.
--
-- Vaulting was rejected (lichess issues no refresh tokens, so these are
-- ~1-year credentials with no rotation story). Proxying was disqualified (all
-- users' lichess traffic from one IP looks like a bot farm and endangers THEIR
-- accounts, against Gambit's first non-negotiable).
--
-- The link itself is durable identity data Gambit has never held before:
-- never log the (steam_id, lichess_username) pair, and keep the unlink path
-- (DELETE /api/v1/links/lichess) working.
-- -----------------------------------------------------------------------
CREATE TABLE lichess_links (
    steam_id         BIGINT      PRIMARY KEY REFERENCES players(steam_id) ON DELETE CASCADE,
    lichess_username TEXT        NOT NULL,
    linked_at        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Lichess usernames are case-insensitive, so the link must be unique on the
-- folded name — otherwise "Foo" and "foo" could link to two different Steam
-- accounts.
CREATE UNIQUE INDEX lichess_links_username_uniq ON lichess_links (lower(lichess_username));

-- -----------------------------------------------------------------------
-- Game archive
--
-- client_game_id is generated host-side at game start and [Sync]ed to both
-- seats. It exists because move history lives in each seated client's own
-- ChessGame, not the host's (a resync or late join keeps the position but
-- loses the history), so the host may have no PGN to submit. Both seats may
-- POST; the UNIQUE constraint makes the second one a no-op 200.
-- -----------------------------------------------------------------------
CREATE TABLE games (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    client_game_id  UUID        NOT NULL UNIQUE,
    pgn             TEXT        NOT NULL,
    white_steam_id  BIGINT      REFERENCES players(steam_id),
    black_steam_id  BIGINT      REFERENCES players(steam_id),
    result          TEXT        NOT NULL,  -- '1-0' | '0-1' | '1/2-1/2' | '*'
    played_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    lichess_game_id TEXT,                  -- set iff the player imported it to lichess
    submitted_by    BIGINT      NOT NULL REFERENCES players(steam_id)
);

CREATE INDEX games_white_idx ON games (white_steam_id, played_at DESC);
CREATE INDEX games_black_idx ON games (black_steam_id, played_at DESC);

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin

DROP TABLE IF EXISTS games;
DROP TABLE IF EXISTS lichess_links;
DROP TABLE IF EXISTS players;

-- +goose StatementEnd
