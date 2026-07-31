-- +goose Up
-- +goose StatementBegin

-- Self-play (M19): a player may join their OWN advert to play against themselves —
-- the same SteamID on both sides. Two editor instances on one Steam account resolve
-- to one SteamID, so this is also the only way to test matchmaking on one machine;
-- and it is a real feature (play yourself across two windows). The DB already allows
-- white_steam_id = black_steam_id; what was missing is a role→colour mapping that
-- doesn't fall apart when the two SteamIDs are identical.
--
-- opener_color is the colour the OPENER plays ('w'/'b'), decided by the coin flip at
-- join time. The opener learns it from its poll, the joiner learns the opposite from
-- its join response — so each side is told its colour by CLIENT ROLE, never derived
-- from a SteamID comparison that self-play makes ambiguous.
ALTER TABLE matchmaking ADD COLUMN opener_color TEXT NOT NULL DEFAULT '';

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin
ALTER TABLE matchmaking DROP COLUMN IF EXISTS opener_color;
-- +goose StatementEnd
