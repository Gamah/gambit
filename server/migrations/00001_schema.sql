-- +goose Up
-- +goose StatementBegin

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- -----------------------------------------------------------------------
-- Players
--
-- SteamID64 is the universal player key, exactly as it already is in the
-- s&box client (ChessStation.WhiteSeatSteamId / BlackSeatSteamId, where 0
-- means "empty seat", never "identity-less player"). gamchess has no username
-- of its own — names come from Steam.
--
-- steam_id here is ONLY ever the SteamID64 echoed back by Facepunch's verify
-- endpoint (in-game), or asserted by Steam OpenID (on the web). A SteamID from a
-- request body or query string is an unverified claim and must never authorise
-- anything.
-- -----------------------------------------------------------------------
CREATE TABLE players (
    steam_id   BIGINT      PRIMARY KEY,
    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- -----------------------------------------------------------------------
-- Game archive
--
-- client_game_id is generated host-side at game start and [Sync]ed to both
-- seats. It exists because move history lives in each seated client's own
-- ChessGame, not the host's (a resync or late join keeps the position but
-- loses the history), so the host may have no PGN to submit. Both seats may
-- POST; the UNIQUE constraint makes the second one a no-op 200.
--
-- The archive is PRIVATE: reads are gated on the caller (Steam OpenID session or
-- a Facepunch token) and only ever return games that caller sat in.
-- -----------------------------------------------------------------------
CREATE TABLE games (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    client_game_id  UUID        NOT NULL UNIQUE,
    pgn             TEXT        NOT NULL,
    white_steam_id  BIGINT      REFERENCES players(steam_id),
    black_steam_id  BIGINT      REFERENCES players(steam_id),
    result          TEXT        NOT NULL,  -- '1-0' | '0-1' | '1/2-1/2' | '*'
    played_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    submitted_by    BIGINT      NOT NULL REFERENCES players(steam_id)
);

CREATE INDEX games_white_idx ON games (white_steam_id, played_at DESC);
CREATE INDEX games_black_idx ON games (black_steam_id, played_at DESC);

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin

DROP TABLE IF EXISTS games;
DROP TABLE IF EXISTS players;

-- +goose StatementEnd
