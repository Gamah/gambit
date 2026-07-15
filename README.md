# Terry's Gambit

Chess in a social s&box lobby, backed by [lichess](https://lichess.org).
Published as **Terry's Gambit** (s&box package `gamah.gambit`).

Walk around a shared room with up to 8 players. Sit down at one of the chess
boards arranged in a ring and:

- **Play real lichess games** — sign in with your lichess account and seek/challenge
  anyone on lichess (Board API), with clocks, chat, and spectators at your board.
- **Play anonymously** — two players share a board in the lobby, no account needed;
  when the game ends you get a shareable lichess link (PGN import).
- **Watch** — other boards in the room, or Lichess TV on the big wall board.
- **Solve puzzles** — lichess puzzles at any board (local solving; doesn't affect
  your lichess puzzle rating).

Forked from rotaliate-client; the lobby/station scaffolding is inherited, the game
and backend are being replaced. See **[PLAN.md](PLAN.md)** for the full design,
lichess API notes, milestones, and current status.

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Chess backend | lichess API (OAuth2 PKCE, Board API, NDJSON streams) |
| Lobby networking | s&box multiplayer (`[Sync]`/`[Rpc]`) |

## Assets

All art is CC0 — procedural geometry from engine primitives, with the
[Poly Haven chess set](https://polyhaven.com/a/chess_set) (CC0) as a planned model
upgrade and the portablejim CC0 2D piece set for floor glyphs. Provenance is
recorded in `Assets/ATTRIBUTION.md` as assets land.

## Development

Open `client/gambit.sbproj` in the s&box editor (first time on a new machine: see
"Project Setup" in `CLAUDE.md`). Startup scene is `scenes/lobby.scene`.

## gamchess API contract

`server/` (Go/Postgres, deployed at `chess.gamah.net`) and `client/Code/Api/` hand-mirror
this contract — there is no shared directory and no codegen, so **this section is the one
place it is written down**. A contract change should be one atomic commit across both
halves. Additive fields only; annotate them here.

**gamchess never holds a lichess token.** It relays OAuth *codes* (in memory, single-use,
~2 min) and the client — which alone holds the PKCE verifier — does the exchange against
lichess itself. There is no token column and no exchange path. See `CLAUDE.md` for the
full posture.

### Auth

There are **two ways to prove the same SteamID64**, and every private route accepts either:

| Where | How |
|---|---|
| in-game (s&box client) | a **Facepunch auth token**, verified at `public.facepunch.com/sbox/auth/token` |
| on the web (archive viewer) | **Steam OpenID 2.0** at `steamcommunity.com/openid/login`, then a signed session cookie |

Steam's browser login is **OpenID 2.0, not OAuth2** — there is no Steam OAuth2 endpoint,
whatever it gets called. It is unrelated to the lichess OAuth relay; they only share an
outcome.

FP-gated requests carry both headers:

```
Authorization: Bearer <facepunch-auth-token>   // Sandbox.Services.Auth.GetToken("gamchess")
X-Steam-Id: <steamid64>
```

`X-Steam-Id` is an unverified **claim**. gamchess forwards both to Facepunch and trusts
only the echoed SteamId; a mismatch or any error denies (fail closed). **A SteamID from a
header, body, or query string never authorises anything** — which is why the archive has no
`?steam_id=` parameter.

**SteamIDs cross the wire as strings, always.** A SteamID64 (~7.6e16) exceeds JavaScript's
2^53 safe-integer range, so a bare JSON number is silently corrupted by `JSON.parse`.
`"0"` and `""` both mean *empty seat*.

### Endpoints

| Route | Auth | Notes |
|---|---|---|
| `GET /health` | — | `{status, version}` |
| `POST /api/v1/auth/lichess/begin` | FP | `{state}` → `{redirect_uri}`. state = 32–128 chars `[A-Za-z0-9_-]`, client-generated, high-entropy, **never the SteamID** |
| `GET /callback?code&state` | — | lichess lands the browser here; renders a neutral page and **never the code** |
| `GET /api/v1/auth/lichess/code` | FP | `{code}` once, then 404. 404 = "not yet" — this is a poll |
| `GET /auth/steam/login` | — | 302 to Steam's OpenID provider |
| `GET /auth/steam/return` | — | Steam lands the browser here; verifies, burns the nonce, sets the session cookie |
| `POST /auth/steam/logout` | session | clears the cookie (POST so a stray link can't sign you out) |
| `GET /api/v1/me` | session | `{steam_id}`; 401 when signed out |
| `POST /api/v1/games` | FP | `{client_game_id, pgn, white_steam_id, black_steam_id, result, lichess_game_id?}`. Idempotent on `client_game_id`; **403 unless you sat in the game** |
| `GET /api/v1/games?limit=&offset=` | session **or** FP | **your games only**; `{games:[…]}`, newest first, limit ≤ 200 |
| `GET /api/v1/games/{id}` | session **or** FP | one of your games; **404 (not 403) if you didn't play in it**, so ids aren't probeable |
| `PUT /api/v1/links/lichess` | FP | `{lichess_username}`; 409 if another player claims it |
| `DELETE /api/v1/links/lichess` | FP | idempotent unlink |

**The archive is private.** You only ever see games you sat in. There is deliberately no
`?steam_id=` — taking the SteamID from the request would make every player's history
enumerable by anyone who could sign in, which is the thing gating it was meant to stop.

`result` is one of `1-0`, `0-1`, `1/2-1/2`, `*`.

`client_game_id` is a UUID the host generates at game start and `[Sync]`s to both seats.
Move history lives in each seated client's own `ChessGame`, not the host's, so the host may
have no PGN to submit — **either seat may POST**, and the second is a no-op that returns the
stored row rather than an overwrite.

**gamchess is never required.** If it is unreachable, the client degrades to archive-off and
token-paste sign-in; local play, puzzles and spectating never touch it.
