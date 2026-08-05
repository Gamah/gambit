-- +goose Up
-- +goose StatementBegin

-- -----------------------------------------------------------------------
-- HTTPFIX: the lichess token moves to the CLIENT, and gamchess stops being a
-- custodian.
--
-- 00002 stored a board:play token here because the s&box client could not read
-- a long-lived HTTP stream, and playing a lichess game means holding one open.
-- That was an engine bug, not a preference. It is fixed (our own
-- Http.RequestStreamAsync fix, facepunch/sbox-public 42cee680), so the client
-- holds its own token now and the risk goes away rather than gets managed.
--
-- What is left is an IDENTITY row: this Steam account plays lichess as this
-- lichess account. No token, no ciphertext, no key version, no scope string —
-- gamchess ends with no lichess secrets at all.
--
-- The identity is still AUTHORITATIVE, not a claim. At link the client hands
-- gamchess its fresh token exactly once; gamchess calls GET /api/account with
-- it, records what lichess echoes back, and discards the token. So the plain
-- UNIQUE(lichess_id) inherited from 00002 stays safe: the id was resolved by
-- lichess from a bearer, never asserted by a caller. (An earlier design had the
-- client ASSERT its id, which would have needed a partial index over a
-- verified flag — asserted ids let anyone squat a real user's account and lock
-- them out of ever linking. That design is dead; do not reintroduce the flag.)
--
-- ── EVERY ROW IS DELETED, AND THERE IS ONE MANUAL STEP BEFORE IT ──
--
-- Each row described a token gamchess holds. Deleting the row does NOT revoke
-- that token: it stays live on lichess for up to a year, revocable only by the
-- player. So before this deploy, every linked player must UNLINK IN-GAME (which
-- still revokes, while the old binary can still sign it) or revoke Gambit on
-- lichess's own /account/security.
--
-- No sweep tool was built for this, deliberately. lichess has no bulk revoke —
-- DELETE /api/token is signed BY the token it kills (verified against the live
-- spec 2026-08-05: "Revokes the access token sent as Bearer for this request",
-- 204) — so a sweep is N serial signed calls. gamchess had ONE linked account
-- when this shipped, so N was 1 and a resumable operator command would have been
-- code written for a population that doesn't exist.
--
-- Migrations run in-process at boot, so there is no ordering in which the deploy
-- can do the revoking for you. Once this DELETE has run, the token is
-- unrevokable from our side.
--
-- Players re-auth. Deliberate and unapologetic: the new link puts the token on
-- their own machine.
-- -----------------------------------------------------------------------

DELETE FROM lichess_links;

ALTER TABLE lichess_links
    DROP COLUMN IF EXISTS token_enc,
    DROP COLUMN IF EXISTS token_nonce,
    DROP COLUMN IF EXISTS key_version,
    DROP COLUMN IF EXISTS scopes;

-- The envelope existed only to manage custody: a KEK wrapping rotating data
-- keys that sealed the tokens. With no tokens there is nothing to seal, nothing
-- to rotate, and no LICHESS_TOKEN_KEY to back up.
DROP TABLE IF EXISTS lichess_key_versions;

-- 00002 and 00003 stay in the tree on purpose. goose keeps a version ledger, and
-- removing an applied migration breaks every existing database.

-- +goose StatementEnd

-- +goose Down
-- +goose StatementBegin

-- Irreversible by design: the tokens these columns held were revoked at lichess
-- before this ran, so restoring the columns would restore empty custody of dead
-- grants. Recover from a pre-deploy database backup instead — the same rule M15
-- already imposed for its one-way boot sweep.
SELECT 1;

-- +goose StatementEnd
