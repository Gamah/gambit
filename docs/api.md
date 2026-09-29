# API

The gamchess HTTP contract: identity, the game session, the archive, matchmaking and relay
games.

`server/` (Go) and `client/Code/Api/` (C#) each hand-mirror this contract: there is no shared
directory and no codegen, so this file is the one place it is written down, and a contract change
is one commit across both halves. Fields are additive only. The lichess and TV routes are in
`docs/lichess.md`; matchmaking's design is in `docs/matchmaking.md`.

## gamchess is never required

Walking the lobby and playing at a board never touch gamchess. Nothing may block scene load,
`OnStart`, or a game ending. `GamchessApi` never throws: every call is bounded by an 8s
`Timeout`, and a 5xx or transport failure opens a 60s circuit breaker (`BreakerSeconds`), so a dead
host costs one timeout rather than one per call. A 4xx is a real answer and leaves the breaker
closed. The relay game is the one game type that needs gamchess (`docs/matchmaking.md`).

## Identity

Three ways to prove the same SteamID64. All three attest identity and nothing else.

| Where | How |
|---|---|
| in-game | a **Facepunch auth token**, verified at `public.facepunch.com/sbox/auth/token` |
| in-game, per request | a **game session** (`gcs_…`), traded for a Facepunch token and verified locally |
| web (archive viewer) | **Steam OpenID 2.0** at `steamcommunity.com/openid/login`, then a signed cookie |

Steam's browser login is OpenID 2.0; Steam has no OAuth2 endpoint.

A Facepunch-gated request carries both headers:

```
Authorization: Bearer <facepunch-auth-token>   // Sandbox.Services.Auth.GetToken("gamchess")
X-Steam-Id: <steamid64>
```

`X-Steam-Id` is a claim. gamchess forwards both to Facepunch and accepts only when
`Status == "ok"` and the echoed `SteamId` equals the claim, so a valid token for account Y cannot
authorise as account X. Any error denies. **A SteamID from a header, body or query string never
authorises anything.** Facepunch returns no persona name; display names come from Steam
(`Connection.DisplayName`) and the PGN carries them.

- `GetToken`'s service-name argument is cosmetic: Facepunch validates `{steamid, token}` without
  it. It returns null rather than throwing on a non-Steam build. `GamchessAuth` caches the token
  for 120s and re-mints once on a 401.
- `internal/steam/` is lifted from `../rotaliate` with its tests: OpenID keeps `op_endpoint`
  pinning, `return_to` scheme+host+path matching, and a single-use nonce (`steam.Verify` only
  shape-checks the nonce; the store in `web_auth.go` enforces single use).
- Web sessions are stateless HMAC-signed cookies, so a deploy signs nobody out. `SameSite=Lax` is
  required: the OpenID return is a top-level cross-site GET, which `Strict` would strip the cookie
  from.
- The same Facepunch token authenticates `Sandbox.WebSocket`: `Connect(uri, headers)` accepts an
  `Authorization` header.

### The game session

`POST /api/v1/session` trades a Facepunch token for a bearer that gamchess verifies with a local
HMAC and no I/O, so a polling client pays one Facepunch round-trip an hour instead of one per
request.

```
Authorization: Bearer gcs_<session>    // no X-Steam-Id: the MAC carries it
```

- **Facepunch-gated only** (`requireFacepunch`). A session that could mint a session would renew
  itself forever.
- **One hour.** A session authorises everything its SteamID can do and sessions are stateless, so
  none can be revoked short of rotating `SESSION_SECRET`, which signs every player and browser out.
- **The audience is inside the MAC** (`aud|steamID|expiry|MAC`, audiences `web` and `game`). Without
  it a 30-day web cookie replayed as `gcs_<value>` would authorise the game API for a month.
- **Memory only on the client**, never `FileSystem.Data`, the same as the Facepunch token.
- **Never required.** A failed mint falls back to the Facepunch path, which works identically at one
  round-trip per request; `GamchessApi` stops trying to mint for 120s after a failure.

### Which gate a route uses

| Gate | Accepts |
|---|---|
| `requireFacepunch` | a live Facepunch token only |
| `requireSteam` | a `gcs_` bearer, else a Facepunch token. A prefixed but invalid bearer is a 401 (the client re-mints) |
| `callerSteamID` | the web cookie, else whatever `requireSteam` accepts |

**SteamIDs cross the wire as strings, always.** A SteamID64 (~7.6e16) is past JavaScript's 2^53, so
a bare JSON number is corrupted by `JSON.parse`. `"0"` and `""` both mean an empty seat.

## Endpoints

| Route | Gate | Notes |
|---|---|---|
| `GET /health` | — | `{status, version}` |
| `GET /auth/steam/login` | — | 302 to Steam's OpenID provider |
| `GET /auth/steam/return` | — | verifies, burns the nonce, sets the cookie |
| `POST /auth/steam/logout` | cookie | clears the cookie (POST so a stray link can't sign you out) |
| `GET /api/v1/me` | cookie | `{steam_id}`; 401 when signed out |
| `POST /api/v1/session` | `requireFacepunch` | `{token: "gcs_…", expires_at}` (unix seconds) |
| `POST /api/v1/games` | `requireSteam` | `{client_game_id, pgn, white_steam_id, black_steam_id, result}`. Idempotent on `client_game_id`; 403 unless you sat in the game |
| `GET /api/v1/games?limit=&offset=` | `callerSteamID` | your games only, `{games:[…]}`, newest first, limit ≤ 200 |
| `GET /api/v1/games/{id}` | `callerSteamID` | one of your games; 404 (not 403) if you didn't play in it |
| `POST /api/v1/matchmaking` | `requireSteam` | open an advert, `{mode, lobby_id, time_control, opener_name}` → `{id}` |
| `GET /api/v1/matchmaking` | `requireSteam` | open adverts, `{matches:[…]}` |
| `GET /api/v1/matchmaking/{id}` | `requireSteam` | one advert; participants only (404 otherwise). Heartbeats the opener's open row |
| `POST /api/v1/matchmaking/{id}/join` | `requireSteam` | take an advert; the server assigns colour. 409 if no longer open |
| `DELETE /api/v1/matchmaking/{id}` | `requireSteam` | withdraw your own advert |
| `GET /api/v1/relaygame/{id}?since=N` | `requireSteam`, the two players | state, moves from ply N, live clocks |
| `POST /api/v1/relaygame/{id}/move` | `requireSteam`, the two players | `{uci, fen, over, result, reason}` |
| `POST /api/v1/relaygame/{id}/{action}` | `requireSteam`, the two players | `resign` · `abort` · `draw-offer` · `draw-accept` · `draw-decline` |

## The archive

**Private.** You see only games you sat in. There is no `?steam_id=`: taking the SteamID from the
request would make every player's history enumerable. The seat SteamIDs in a POST are claims, so
you may archive only a game you sat in, and a game you didn't play in is a 404 so ids can't be
probed.

`result` is one of `1-0`, `0-1`, `1/2-1/2`, `*`. `pgn` is non-empty and under 256KB.

`client_game_id` is a UUID the host mints at game start and `[Sync]`s to both seats. Move history
lives in each seated client's own `ChessGame`, not the host's, so the host may have no PGN: **either
seat may POST**, and the second is a no-op that returns the stored row. A client whose history came
from a FEN resync stays quiet rather than archive a stub.
