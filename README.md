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
- **Play for real on lichess** — link your lichess account and a game at a Gambit table can
  be a real lichess game, in your real lichess history: either against the person sitting
  opposite you, or against a random opponent from lichess's lobby. Rated if you want.
- **Watch** — a live game from the tables mirrors onto the big wall board.

Forked from rotaliate-client; the lobby/station scaffolding is inherited. See
**[CLAUDE.md](CLAUDE.md)** for how it's built. **[PLAN.md](PLAN.md)** is what's left.

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Backend | gamchess — Go 1.22 + Postgres 16, `server/` |
| Identity | Steam: Facepunch auth token in-game, OpenID 2.0 on the web |
| Lichess | OAuth2 Authorization Code + PKCE, `board:play` scope; gamchess relays the Board API |
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

**gamchess is never required.** If it is down the game plays exactly the same — walking the
lobby and playing at a board never touch it. Nothing may block scene load, `OnStart`, or a
game ending. Lichess is likewise never required: unlinked, refused or offline all degrade to
"no lichess", never to a broken game.

### Auth

There are **three ways to prove the same SteamID64**, and every private route accepts any of
them. All three attest identity and nothing else:

| Where | How |
|---|---|
| in-game (s&box client) | a **Facepunch auth token**, verified at `public.facepunch.com/sbox/auth/token` |
| in-game, on the hot path | a **gamchess session** (`gcs_…`), traded for an FP token once and then verified locally (M9) |
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

#### The game session (M9)

**Every FP-gated request costs a live HTTP round-trip to Facepunch.** That is one per player
per poll on a relayed lichess game (~5s), and TV multiplies it by everyone standing at a
wall. `POST /api/v1/session` trades an FP token for a bearer that gamchess verifies with a
**local HMAC and no I/O at all**:

```
Authorization: Bearer gcs_<session>    // no X-Steam-Id — the MAC carries it
```

- **Nothing about it is user-visible.** It is minted from the Facepunch token the client
  already holds. No web sign-in, no lichess link, no prompt — those are unrelated.
- **FP-gated only**, and that is load-bearing: a session may not mint a session, or a client
  would renew itself forever and the TTL below would be a fiction.
- **One hour**, not the web cookie's 30 days. A game session authorises everything that
  SteamID can do (including playing lichess games as them), and sessions are stateless, so
  **there is no way to revoke one** short of rotating `SESSION_SECRET` — which signs every
  player and every browser out at once.
- **The audience is inside the MAC** (`aud|steamID|expiry|MAC`). Without that a web cookie
  and a game bearer would be the same bytes under the same key, so a leaked 30-day cookie
  replayed as `gcs_<value>` would authorise the game API for its full month and the 1-hour
  TTL would be decoration.
- **Memory only on the client**, never `FileSystem.Data` — the same rule the FP token lives
  under, and for the same reason ("can a rogue lobby host read another client's
  `FileSystem.Data`?" is still an open spike).
- **Never required.** A mint failure falls back to the FP token, which works identically and
  just costs a Facepunch round-trip per request. It degrades performance, never function.

> Changing the payload format invalidated existing web cookies once, at the M9 deploy —
> everyone signed in on the web signs in again. There is no migration and none is wanted.

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
| `POST /api/v1/session` | **FP only** | `{token: "gcs_…", expires_at}` — a 1h bearer verified with no I/O. A session may not mint one (see above) |
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

### Lichess (M8)

Gambit plays **real games on lichess** from a table. The client holds **no lichess token and
speaks no lichess protocol** — it authenticates to gamchess with its Facepunch token, and
gamchess acts on lichess with the token it stores.

**Why gamchess holds the token.** Playing a lichess game requires holding a long-lived ndjson
stream open (`/api/board/game/stream/{id}`); lichess has no polling substitute and answers a
poller with a 429. The s&box client cannot read a stream at all — `Sandbox.Http` buffers the
whole body before returning, and `HttpCompletionOption` is off the API whitelist. So whoever
reads the stream must hold the token, and today that can only be gamchess. This is the
"position 2" custody decision; see CLAUDE.md for the blast radius and what mitigates it.

**Scope: `board:play`, and nothing else.** It is a single all-or-nothing grant — there is no
read-only subset. It also satisfies the challenge endpoints (their spec lists the acceptable
scopes as *alternatives*), so the play flow needs no second scope. Widening it would force
every linked player through a full re-link: lichess tokens are long-lived (~1 year) and there
are **no refresh tokens**.

**`client_id` is `net.gamah.gambit`, a constant, and not a credential.** lichess has no client
registration — its own error text is `client_id required (choose any)`. It is not recorded on
the token (lichess stores `clientOrigin`, the scheme+host of our redirect URI), so changing it
revokes and configures nothing. It is public and impersonable by design; PKCE secures the
exchange and the redirect URI decides who receives a code.

**The token is encrypted at rest** (AES-256-GCM, per-row nonce, `LICHESS_TOKEN_KEY`). A blank
key switches lichess off entirely rather than storing plaintext.

| Route | Auth | Notes |
|---|---|---|
| `GET /lichess/link` | session | the disclosure page; the constant URL the in-game board copies. 302s to Steam sign-in if needed |
| `GET /lichess/start` | session | mints the PKCE pair, 302 to lichess's consent screen |
| `GET /lichess/callback` | the `state` (burned on use) | exchanges the code, stores the encrypted token, renders the result |
| `POST /lichess/unlink` | session | the web unlink button (POST, so no prefetch can unlink you) |
| `GET /api/v1/lichess` | session **or** FP | `{linked, lichess_id, username, link_url}`. **Only ever about the caller** |
| `DELETE /api/v1/lichess` | session **or** FP | revoke at lichess (best-effort), then delete the row |
| `POST /api/v1/lichess/play` | FP | play the person opposite you. `{client_game_id, white_steam_id, black_steam_id, limit_seconds, increment_seconds, unlimited}` — **both seats must POST** |
| `POST /api/v1/lichess/seek` | FP | play a random opponent. `{client_game_id, time_minutes, increment_seconds, rated, rating_range, color}` — one caller |
| `GET /api/v1/lichess/play/{id}?since=N` | FP | **long poll** (held ~5s) for game state; 404 if you aren't in it |
| `POST /api/v1/lichess/play/{id}/{action}` | FP | `move` (body `{uci}`) · `resign` · `draw` · `draw-decline` · `abort` |
| `DELETE /api/v1/lichess/play/{id}` | FP | withdraw a seek / drop a pending pairing |
| `POST /api/v1/lichess/audit` | `LICHESS_AUDIT_KEY` | sweep our token store against lichess. 404 when unconfigured |

**Starting a game against the person opposite needs BOTH seats to POST** `/play` with the same
`client_game_id`, each with their own Facepunch token, agreeing on seats and clock. That is the
whole authorisation story, not a formality: gamchess holds a token for every linked player, so
if one seat could start a game alone, any linked player could drag any other into a lichess game
at will. `client_game_id` is **not a secret** — it is `[Sync]`ed to the lobby — it is only the
rendezvous key; the two FP tokens are the authority. A **seek** needs one caller, because there
is nobody to get consent from: you spend your own grant on a stranger who opted in on lichess.

**Two speed floors, and they are not the same.** lila has two functions named
`isBoardCompatible` with different thresholds:

| Flow | Floor | Which presets |
|---|---|---|
| direct challenge | blitz — estimate ≥ 180s | Blitz 3+2, Rapid 10+0, Classical 30+0, Unlimited |
| lobby seek | rapid — estimate ≥ 480s | Rapid 10+0, Classical 30+0 |

(estimate = `limit + 40×increment`.) **Bullet can never reach lichess from any path.** The
default table is Blitz 3+2, which is challengeable but *not* seekable — which is exactly why a
direct challenge is the primary flow. Note also that a seek's `time` is in **minutes** while a
challenge's `clock.limit` is in **seconds**.

**Rate limits are shared by the whole playerbase**, because gamchess is one IP and lichess's
limits are per-IP. Lobby seeks are ~5/minute for *all* of Gambit (lila's `setupPost`), so
gamchess self-limits and refuses locally with a reason rather than earning a 429. A 429 anywhere
stops every outbound call for a full minute, per lichess's own instruction. Every request —
streams included — carries a `User-Agent` naming the project and a contact, which is how lichess
can attribute our traffic (they record a `userAgent` per token).

**The state transport is a long poll, not a WebSocket.** s&box *can* speak WebSocket, but the Go
side would need a WS library and this repo cannot add a dependency (the machine that writes the
server has neither Go nor Docker to regenerate `go.sum`). Each `gameState` carries the **whole**
UCI move list from the start, so the client rebuilds rather than reconciles and a dropped or
duplicated poll costs nothing.

**gamchess is never required.** If it is unreachable, the client degrades to archive-off and
lichess-off; local play and spectating tables never touch it.

### Lichess TV (M9)

Real lichess games on the west spectator wall. **This is the one lichess feature with no
security surface upstream**: `GET /api/tv/{channel}/feed` is `security: []` — anonymous. No
token, no scope, no custody question, nothing to encrypt, revoke, or audit. **None of M8's
hard part applies, and none of it may creep in — TV must keep working for a player who has
never linked a lichess account and never will.**

| Route | Auth | Notes |
|---|---|---|
| `GET /api/v1/tv/channels` | session/FP | `{default, channels:[{key,label}]}` — what we'll actually serve |
| `GET /api/v1/tv/{channel}?since=N` | session/FP | **long poll** (held ~5s) for the channel's featured game |

**One upstream stream per CHANNEL, however many are watching.** 100 players on blitz cost
lichess exactly one stream. That invariant is the entire reason TV is proxied rather than hit
from each client (lichess advocates precisely this), and it is what makes per-client channel
choice affordable: the cost is bounded by the channel count (6), not the player count. The
stream opens on the first watcher and is dropped ~45s after the last one stops polling —
ref-counted by **pollers**, via a last-polled timestamp rather than a counter, because a
counter needs a decrement on every exit path including the ones a dropped connection never
gives us, and one missed decrement leaks a stream to lichess forever.

**It is session-gated even though it's anonymous upstream**, and the reason is not cost:

1. An open `/api/v1/tv/{channel}` is a **free CDN for someone else's content**, pointable by
   any script.
2. lichess sees **our IP and our User-Agent** — we went out of our way to make that traffic
   attributable so they *can* attribute it. Anything done through an open relay is done *as
   Gambit*, against the one IP whose limits every real player shares. Being identifiable and
   being an open relay is a bad combination.

**Channels: all 16 of them**, default `blitz` — the six speeds (`best` = "Top Rated",
`bullet`, `blitz`, `rapid`, `classical`, `ultraBullet`), the eight variants (`chess960`,
`crazyhouse`, `kingOfTheHill`, `threeCheck`, `antichess`, `atomic`, `horde`, `racingKings`)
and `bot`/`computer`.

This shipped as six, on the reasoning that the vendored rules are standard-only so a variant
FEN can't be drawn. **That was wrong.** The standard-only rule governs *playing* — where
`ChessGame` parses the FEN and validates moves — and the wall does neither: `SpectatorBoard3D`
reads the piece-placement field alone and walks its characters under a `file < 8 && rank >= 0`
guard. So Chess960's X-FEN castling (`HDhd`) is never read, Crazyhouse's pockets
(`…/RNBQKBNR[Pp]`) fall off the guard, Three-check's counters ride at the end of the FEN, and
the rest are plain standard placement. Verified against every variant's real starting FEN.

Two channels keep state the 64 squares can't hold — Crazyhouse's pockets, Three-check's
counts — and the settings board says so rather than let a viewer think the board is broken.

The **channel allowlist is a security boundary, not a menu**: the key arrives off the wire and
becomes a lichess URL, so nothing may build one from a key that didn't come out of
`ValidChannel`. Holding every channel lichess offers doesn't make it decoration — the point is
that the set is closed and ours. The client mirrors it by hand, and a Go test reads
`LichessTv.cs` to hold the two lists together.

**Wire shape** (read off the live feed 2026-07-15, not recalled — the envelope is `{"t":…,
"d":…}`, *not* the `{"type":…}` the Board API stream uses):

```
{"t":"featured","d":{"id":…,"orientation":…,"players":[{"color":"white","user":{"name":…,"title":…},"rating":…,"seconds":…}],"fen":…}}
{"t":"fen","d":{"fen":…,"lm":"d7f6","wc":56,"bc":51}}
```

Note `players[]` nests name/title under `user` (absent for anon/AI) with rating/seconds as
siblings, and **`wc`/`bc` are SECONDS** — where the Board API sends the same idea in
milliseconds. Two endpoints, two units.

**A clock only arrives on a move**, so the client counts the side-to-move's down locally from
the last frame and snaps both to the next one. It only ever spends time, never invents it,
which keeps a live clock from reading higher than what's actually left. lichess remains the
only authority.

**TV is per-client and off-able.** It's one more entry in the west wall's existing cycle
(which was already per-client), with no priority over real tables. A local setting, default
on, removes it. The lobby admin **suggests** a channel; a client that has picked its own
keeps it. Turn TV off, or kill gamchess, and the wall mirrors real tables exactly as it did
before M9 — which was its original job.
