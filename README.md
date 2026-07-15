# Terry's Gambit

Chess in a social s&box lobby, backed by **gamchess** — our own Go/Postgres service
at [chess.gamah.net](https://chess.gamah.net). Published as **Terry's Gambit** (s&box
package `gamah.gambit`).

Walk around a shared room with up to 8 players. Sit down at one of the chess boards
arranged in a ring and:

- **Play** — two players share a board. You're already signed in: s&box is Steam-gated,
  so your name and identity come with you.
- **Keep your games** — every finished game is archived to gamchess and replayable at
  [chess.gamah.net](https://chess.gamah.net), signed in with Steam. Your archive is
  private: you only ever see games you sat in.
- **Watch** — a live game from the tables mirrors onto the big wall board.

Forked from rotaliate-client; the lobby/station scaffolding is inherited. See
**[CLAUDE.md](CLAUDE.md)** for how it's built and **[PLAN.md](PLAN.md)** for what's left.

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Backend | gamchess — Go 1.22 + Postgres 16, `server/` |
| Identity | Steam: Facepunch auth token in-game, OpenID 2.0 on the web |
| Lobby networking | s&box multiplayer (`[Sync]`/`[Rpc]`) |

## Assets

All art is CC0. Nothing is licensed in today: pieces are runtime meshes, floor glyphs are
our own DejaVu raster, sounds are synthesized (`scripts/gen_sounds.py`), and the web
viewer uses Unicode glyphs. The [Poly Haven chess set](https://polyhaven.com/a/chess_set)
(CC0) is the planned model upgrade. Provenance is recorded in `Assets/ATTRIBUTION.md`.

## Development

Open `client/gambit.sbproj` in the s&box editor (first time on a new machine: see
"Project Setup" in `CLAUDE.md`). Startup scene is `scenes/lobby.scene`.

## gamchess API contract

`server/` (Go/Postgres, deployed at `chess.gamah.net`) and `client/Code/Api/` hand-mirror
this contract — there is no shared directory and no codegen, so **this section is the one
place it is written down**. A contract change should be one atomic commit across both
halves. Additive fields only; annotate them here.

**There is no lichess here.** Gambit was built against the lichess API through M3–M5 and
all of it was ripped out — no API client, no OAuth, no puzzles, no TV, no token. Any
lichess reference left anywhere is residue and should be gutted. The `lichess-final` tag is
the last commit that had it.

### Auth

There are **two ways to prove the same SteamID64**, and every private route accepts either:

| Where | How |
|---|---|
| in-game (s&box client) | a **Facepunch auth token**, verified at `public.facepunch.com/sbox/auth/token` |
| on the web (archive viewer) | **Steam OpenID 2.0** at `steamcommunity.com/openid/login`, then a signed session cookie |

Steam's browser login is **OpenID 2.0, not OAuth2** — there is no Steam OAuth2 endpoint,
whatever it gets called.

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
| `GET /auth/steam/login` | — | 302 to Steam's OpenID provider |
| `GET /auth/steam/return` | — | Steam lands the browser here; verifies, burns the nonce, sets the session cookie |
| `POST /auth/steam/logout` | session | clears the cookie (POST so a stray link can't sign you out) |
| `GET /api/v1/me` | session | `{steam_id}`; 401 when signed out |
| `POST /api/v1/games` | FP | `{client_game_id, pgn, white_steam_id, black_steam_id, result}`. Idempotent on `client_game_id`; **403 unless you sat in the game** |
| `GET /api/v1/games?limit=&offset=` | session **or** FP | **your games only**; `{games:[…]}`, newest first, limit ≤ 200 |
| `GET /api/v1/games/{id}` | session **or** FP | one of your games; **404 (not 403) if you didn't play in it**, so ids aren't probeable |

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
