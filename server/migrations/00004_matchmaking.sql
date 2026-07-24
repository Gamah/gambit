-- +goose Up
-- +goose StatementBegin

-- -----------------------------------------------------------------------
-- Matchmaking directory (M19)
--
-- Lets solo players in SEPARATE s&box lobbies find each other — same-lobby play
-- already works, the gap is discovery across sessions. gamchess is only the
-- DIRECTORY here: it lists who is open and pairs them, assigning a RANDOM colour
-- so the opener can't default to White. It does not run the game — see the two
-- modes:
--   'join'  — the joiner Networking.Connect()s into the opener's lobby (lobby_id)
--             and they play the existing two-seat game. gamchess is done once paired.
--   'relay' — a gamchess-authoritative live game (relay_games below); both players
--             stay in their own lobbies and POST/poll moves. game_id points at it.
--
-- opener_steam_id / joiner_steam_id are always the FP-verified caller of the POST
-- that set them, never a body claim (same rule as the archive). white/black are
-- gamchess's coin-flip at join time, so neither client picks a side.
-- -----------------------------------------------------------------------
CREATE TABLE matchmaking (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    opener_steam_id BIGINT      NOT NULL REFERENCES players (steam_id),
    opener_name     TEXT        NOT NULL DEFAULT '',
    -- The opener's s&box lobby is its host's SteamId as a string. 'join' only;
    -- handed to a joiner on success, never listed, so it isn't a connect target
    -- an idle browser can harvest.
    lobby_id        TEXT        NOT NULL DEFAULT '',
    mode            TEXT        NOT NULL CHECK (mode IN ('join', 'relay')),
    time_control    TEXT        NOT NULL DEFAULT '-',
    status          TEXT        NOT NULL DEFAULT 'open'
                                CHECK (status IN ('open', 'matched', 'closed')),
    white_steam_id  BIGINT,
    black_steam_id  BIGINT,
    joiner_steam_id BIGINT,
    game_id         UUID,       -- the relay_games row, 'relay' mode only
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- One OPEN advertisement per opener: a second POST replaces the first rather than
-- littering the list. Partial, so a player's old closed/matched rows don't block it.
CREATE UNIQUE INDEX matchmaking_one_open_per_opener
    ON matchmaking (opener_steam_id) WHERE status = 'open';

-- The list query is "open rows, newest first".
CREATE INDEX matchmaking_open ON matchmaking (status, created_at DESC);

-- -----------------------------------------------------------------------
-- Relay games (M19, 'relay' mode)
--
-- A gamchess-authoritative live game between two accounts who never share a
-- lobby. This is the ONE game type that cannot run without gamchess — the whole
-- rest of Gambit degrades gracefully when gamchess is down, this doesn't, which
-- is why it is a deliberate mode a player opts into, not the default.
--
-- gamchess does NOT run a chess engine: it trusts the mover's client (which ran
-- the vendored rules) exactly as the local two-seat game trusts the mover's
-- NetChessMove, and carries `fen` as the checksum both clients reconcile against.
-- The CLOCK is the one thing gamchess is authoritative over — it stamps the
-- mover's remaining time and flags lazily on a poll, the same shape as the
-- lichess relay (there lichess is the authority; here it is us).
-- -----------------------------------------------------------------------
CREATE TABLE relay_games (
    id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    white_steam_id BIGINT      NOT NULL REFERENCES players (steam_id),
    black_steam_id BIGINT      NOT NULL REFERENCES players (steam_id),
    time_control   TEXT        NOT NULL DEFAULT '-',
    -- Clocks in milliseconds. -1 initial_ms marks an untimed game (no flagging).
    initial_ms     BIGINT      NOT NULL,
    increment_ms   BIGINT      NOT NULL DEFAULT 0,
    white_ms       BIGINT      NOT NULL,
    black_ms       BIGINT      NOT NULL,
    -- Whose clock is running. Also who may move next.
    turn           TEXT        NOT NULL DEFAULT 'w' CHECK (turn IN ('w', 'b')),
    -- Space-separated UCI, in order. The client rebuilds the position from this,
    -- exactly as the spectator mirror does; `fen` is the latest-position checksum.
    moves          TEXT        NOT NULL DEFAULT '',
    fen            TEXT        NOT NULL DEFAULT '',
    status         TEXT        NOT NULL DEFAULT 'live'
                               CHECK (status IN ('live', 'white_won', 'black_won', 'draw', 'aborted')),
    -- End reason, for the HUD ("Checkmate", "Out of time"…). '' while live.
    reason         TEXT        NOT NULL DEFAULT '',
    -- Standing draw offer: '' none, 'w' White offering, 'b' Black offering.
    draw_offer     TEXT        NOT NULL DEFAULT '',
    -- When the side to move started thinking — the anchor the ticking clock is
    -- computed against (remaining = stored - (now - last_move_at)).
    last_move_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin
DROP TABLE IF EXISTS relay_games;
DROP TABLE IF EXISTS matchmaking;
-- +goose StatementEnd
