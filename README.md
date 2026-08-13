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
**[CLAUDE.md](CLAUDE.md)** for how it's built — it indexes the subject docs
([LICHESS.md](LICHESS.md), [GAMCHESS.md](GAMCHESS.md), [SBOX-NOTES.md](SBOX-NOTES.md),
[TERRY-HALFRISE.md](TERRY-HALFRISE.md)). **[PLAN.md](PLAN.md)** is what's left.

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels |
| Backend | gamchess — Go 1.22 + Postgres 16, `server/` |
| Identity | Steam: Facepunch auth token in-game, OpenID 2.0 on the web |
| Lichess | OAuth2 Authorization Code + PKCE, exchanged **client-side**; the client holds its own token and speaks the Board API directly |
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
| `POST /api/v1/matchmaking` | session **or** FP | open an advert: "I'm up for a game". `{mode, …}` |
| `GET /api/v1/matchmaking` | session **or** FP | the directory of open adverts |
| `GET /api/v1/matchmaking/{id}` | session **or** FP | one advert's state |
| `POST /api/v1/matchmaking/{id}/join` | session **or** FP | take an advert. **Colour is assigned at random by the server**, never chosen |
| `DELETE /api/v1/matchmaking/{id}` | session **or** FP | withdraw your own advert |
| `GET /api/v1/relaygame/{id}` | the two players | a cross-lobby game's state. The one game type that REQUIRES gamchess — it is the authority |
| `POST /api/v1/relaygame/{id}/move` | the two players | `{uci}` |
| `POST /api/v1/relaygame/{id}/{action}` | the two players | `resign` · `draw` · … |

*(M19's matchmaking and relaygame routes were missing from this table until HTTPFIX; they are
unaffected by it. **A relay game is not a lichess game** — gamchess is the authority and the
whole exchange is ours.)*

**The archive is private.** You only ever see games you sat in. There is deliberately no
`?steam_id=` — taking the SteamID from the request would make every player's history
enumerable by anyone who could sign in, which is the thing gating it was meant to stop.

`result` is one of `1-0`, `0-1`, `1/2-1/2`, `*`.

`client_game_id` is a UUID the host generates at game start and `[Sync]`s to both seats.
Move history lives in each seated client's own `ChessGame`, not the host's, so the host may
have no PGN to submit — **either seat may POST**, and the second is a no-op that returns the
stored row rather than an overwrite.

### Lichess

Gambit plays **real games on lichess** from a table. **The client holds its own lichess token
and speaks the Board API directly.** gamchess holds no lichess secret of any kind.

**Why it moved (HTTPFIX).** Playing a lichess game requires holding a long-lived ndjson stream
open; lichess has no polling substitute and answers a poller with a 429. The s&box client could
not read a stream, so whoever read it had to hold the token — and that was gamchess. That was
never a preference; it was an engine bug, and it is fixed (`Http.RequestStreamAsync` returns
once the headers are in, `facepunch/sbox-public` 42cee680). **So the risk went away rather than
got managed:** the envelope encryption, the key rotation, the audit sweep and the play relay are
all deleted, along with the four `LICHESS_*` config keys they needed.

**What gamchess still does, and why each one is unavoidable:**

1. **Holds the redirect URI.** lichess compares `redirect_uri` byte-for-byte between authorize
   and token, and the client cannot listen on a socket, so there is no loopback escape.
   `PUBLIC_BASE_URL` derives it once — which is also what keeps the test instance pointing at
   itself rather than at prod.
2. **Shows the disclosure page.** Consent belongs somewhere with a URL bar.
3. **Is the directory.** Two seats at a table need each other's lichess usernames to challenge
   by name, and neither client may simply be told the other's.

**The token transits gamchess exactly once**, at link, so it can call `GET /api/account` and
learn whose it is. It is not stored, logged, or put in an error string. That one transit is
unavoidable: `POST /api/token` returns no user id, and a client-*asserted* identity is a claim —
which would let anyone squat a real account's row and lock its owner out of ever linking. So
"gamchess cannot hold your token" is a **promise**, not a structure, and the copy says so.

**Scopes: `board:play puzzle:read puzzle:write follow:read`.** `board:play` is the one
that plays games — a single all-or-nothing grant with no read-only subset, which also satisfies
the challenge endpoints (their spec lists the acceptable scopes as *alternatives*). The rest were
added when HTTPFIX forced a re-link on everyone anyway, which is the one moment widening costs
nothing extra: lichess tokens are long-lived (~1 year) with **no refresh tokens**, so a scope
change normally means every linked player re-links. **`msg:write` was asked for and then dropped
before HTTPFIX shipped**: it is sending-only and permanently so (there is no `msg:read` scope at
all, and reading an inbox needs `web:mobile`), nothing was built on it, and a scope nothing uses
is one more line on the consent page. **`web:mobile`, `web:polygon` and `web:mod` stay out** (see
PLAN No. 13).

**`client_id` is `net.gamah.gambit`, a constant, and not a credential.** lichess has no client
registration — its own error text is `client_id required (choose any)`. It is not recorded on
the token (lichess stores `clientOrigin`, the scheme+host of our redirect URI), so changing it
revokes and configures nothing. It is public and impersonable by design; PKCE secures the
exchange and the redirect URI decides who receives a code.

**The token is stored on the player's own PC**, in the game's data folder (`lichess.json`),
never in `player.json`. lichess's advice against shipping tokens to users is about *hardcoded
developer* tokens in a bundle; the spec explicitly blesses this model — *"it is fine if the user
themselves can extract `code_verifier`, which will always be possible for fully client-side
apps."*

| Route | Auth | Notes |
|---|---|---|
| `GET /lichess/link` | session | the disclosure page; the constant URL the in-game board copies. 302s to Steam sign-in if needed. Tells you to start from the game if no flow is registered |
| `GET /lichess/start` | session | 302 to lichess's consent screen, with the challenge the client registered |
| `GET /lichess/callback` | the `state` | **parks** the code against that state and renders the result. Does NOT exchange it (gamchess has no verifier) and does NOT burn the state — burning moved to `collect`. Refuses a slot that already holds a code, because a browser refresh replays a spent one |
| `POST /lichess/unlink` | session | makes the server forget the link. Cannot revoke — the key is on the player's PC |
| `POST /api/v1/lichess/link/start` | session **or** FP | `{code_challenge}` → `{state, authorize_url, redirect_uri, link_url}`. Newest flow per SteamID wins |
| `POST /api/v1/lichess/link/collect` | session **or** FP | `{status: none\|waiting\|ready, code, redirect_uri, client_id}`. **Keyed on the caller's authenticated SteamID and never on a state from the body**; `ready` burns the slot |
| `POST /api/v1/lichess/claim` | session **or** FP | `{token}` → `{linked, lichess_id, username}`. gamchess asks lichess whose token it is, records that, and discards it |
| `GET /api/v1/lichess` | session **or** FP | `{linked, lichess_id, username, link_url}`. **Only ever about the caller.** Field names are frozen: gamchess deploys before the s&box package, so an old client polls this for a window |
| `DELETE /api/v1/lichess` | session **or** FP | forget the row. The client revokes |
| `POST /api/v1/lichess/rendezvous` | session **or** FP | `{client_game_id, white_steam_id, black_steam_id}` → `{ready, your_color, opponent, opponent_id}`. **Both seats must POST** before either learns the other's username |

**The two-intent rule survives, and its justification has changed.** It used to be the consent
story: gamchess held both players' tokens, so a one-sided start could have dragged any linked
player into a game from anywhere. That reason is gone — each client acts with its own token and
can only ever commit itself, and a one-sided start now just leaves a challenge in someone's
notifications. What both intents still buy is **directory disclosure**: seat B's lichess username
is revealed to seat A only once both have posted for the same `client_game_id`, and only to those
two. `client_game_id` is not a secret (it is `[Sync]`ed to the lobby); it is the rendezvous key.

**How the four flows work, client-side:**

- **Paired.** Both seats rendezvous; White challenges Black by name and publishes the challenge
  id over the lobby's own `[Sync]`; Black accepts by id. **This flow never opens the event
  stream**, which matters because that stream is one-per-token.
- **Seek.** Needs the event stream: a real-time seek's response carries no game id — it is a
  stream of empty lines whose only job is to stay open, and closing it cancels the seek.
- **Challenge a named stranger.** Needs the event stream (nothing else reports an acceptance).
  Short-lived: lichess sweeps an unanswered real-time challenge after ~20s, and Gambit
  deliberately does not use `keepAliveStream` — closing that stream does **not** withdraw a
  challenge, which then stays acceptable for hours.
- **Shareable link.** Create the open challenge **anonymously** (a `board:play` token 403s
  `POST /api/challenge/open`), then accept it with the player's own token to seat them, then hand
  out the opposite colour's url. Skipping the accept is the bug that shipped in M8.

**ONE EVENT STREAM PER TOKEN.** Opening a second closes the first server-side, and the victim
reports a clean EOF that is indistinguishable from "the game ended" — so a second stream does
not error, it makes the first flow hang with no message. The client holds it as a process-wide
refcounted singleton.

**Two speed floors, and they are not the same.** lila has two functions named
`isBoardCompatible` with different thresholds:

| Flow | Floor | Which presets |
|---|---|---|
| direct challenge | blitz — estimate ≥ 180s | Blitz 3+0, Rapid 10+0, Classical 30+0, Unlimited |
| lobby seek | rapid — estimate ≥ 480s | Rapid 10+0, Classical 30+0 |

(estimate = `limit + 40×increment`.) **Bullet can never reach lichess from any path.** The
default table is Blitz 3+0, which is challengeable but *not* seekable — which is exactly why a
direct challenge is the primary flow. Note also that a seek's `time` is in **minutes** while a
challenge's `clock.limit` is in **seconds**.

**Rate limits are each player's own now**, because each client spends its own IP. The rules do
not relax: a 429 anywhere stops that client's every outbound call for a full minute, per
lichess's own instruction; lobby seeks are self-limited to 5/minute (lila's `setupPost`) because
mashing the button would otherwise earn a 429 that stops everything, and a household or NAT still
shares an IP; and every request, streams included, identifies the project and a contact. The
string is byte-identical on both halves, but the header is not: gamchess sends a real
`User-Agent`, while a s&box game **cannot** — the engine forbids that header outright and forces
its own — so the client sends the same string as `X-Gambit-Client`.

**An abandoned game is no longer resigned for you.** gamchess used to notice a client that
stopped polling and resign that seat within ~30s. Nobody else holds the token now, so a crash
mid-game means the game flags — exactly as it does for every other Board API client. Standing up
keeps the game live, as it has since M17; only quitting drops the stream.

**gamchess is never required — and less so than before.** A lichess game runs between the client
and lichess, so gamchess being down doesn't touch one in progress. What it does stop is *linking*
and the two-seat directory lookup.

### Lichess TV (M9)

Real lichess games on the north spectator wall. **This is the one lichess feature with no
security surface upstream**: `GET /api/tv/{channel}/feed` is `security: []` — anonymous. No
token, no scope, no custody question, nothing to encrypt, revoke, or audit. **None of M8's
hard part applies, and none of it may creep in — TV must keep working for a player who has
never linked a lichess account and never will.**

| Route | Auth | Notes |
|---|---|---|
| `GET /api/v1/tv/channels` | session/FP | `{default, channels:[{key,label}]}` — what we'll actually serve |
| `GET /api/v1/tv/{channel}` | session/FP | **WebSocket** (M18): one full snapshot pushed per state change |

**The client-facing transport is a WebSocket push (M18).** The `{channel}` route upgrades to a
socket — one socket per channel — and gamchess pushes one **full, self-contained `TvState`
snapshot** whenever that channel's state changes: no deltas, no `?since=` cursor, latest-wins.
Switching channels reconnects (the gap is ~one round trip, filled by the stored latest frame
pushed on connect). The gate runs **before** the upgrade, so a bad session is a plain 401 with
no socket and no upstream. Compression (permessage-deflate) is safe to enable here because TV
data is the public anonymous feed — no secrets — and it cuts egress. This replaced a
version-gated long poll with a `since`/`version` cursor, a `hold_ms` field and a clock
latency-compensation apparatus; all of that is gone.

*(gamchess still reads the lichess ndjson feed itself and stays the sole stream holder — this
is not clients reading lichess directly. Only the gamchess→client hop changed.)*

**gamchess→client wire — the bespoke snapshot** (one message = the whole state):

```
channel, label, error, game_id, url, fen, last_move_uci,
white_name/black_name, white_title/black_title, white_rating/black_rating,
white_clock/black_clock  (SECONDS),  ticking_seat  ("white"|"black"),
age_ms,                                             // clock staleness — see below
last_game_id/last_status/last_winner/last_white_name/last_black_name  // the fanfare
```

**The clock favours reading LOW, on one field.** A push arrives the instant a move lands, so a
live game's frames are FRESH and the client flooring the displayed second (it already
truncates) absorbs the sub-second transport latency for free — the steady-state clock reads low
with no correction at all. The one case the floor can't cover is a client that **connects
mid-think**: gamchess hands the new socket its stored frame, already `age_ms` milliseconds
stale, and the client subtracts `age_ms` so that replay reads low, not high. `age_ms` is a
duration, not a timestamp (we don't share a wall clock with the client; a skewed absolute stamp
would correct in either direction, including up). On top, the client shaves a fixed
`ClockLeadSeconds` (~0.25s) undershoot — belt-and-braces in the one permitted direction, since
the **lichess→gamchess** leg is irrecoverable (nothing downstream knows when lichess stamped
it), so a small residual HIGH bias survives on that leg and is documented rather than denied.
The whole of the old `clock_age_ms` + `hold_ms` + measured-round-trip machinery collapses to
this: a push has no hold to measure and no cursor to reconcile.

**One upstream stream per CHANNEL, however many are watching.** 100 players on blitz cost
lichess exactly one stream. That invariant is the entire reason TV is proxied rather than hit
from each client (lichess advocates precisely this), and it is what makes per-client channel
choice affordable: the cost is bounded by the channel count (15), not the player count. Now
that each viewer holds a socket for its whole life, the ref count is a **persistent per-channel
registry** with a connection count: `watch` increments it, and a `defer leave` in the socket
handler decrements it on **every** exit path — a clean close, a write error, a rude TCP drop.
The sweeper (the *only* thing that closes an upstream) drops a channel a short `tvLingerTTL`
(~10s) after its count reaches zero, so an A→B→A channel switch doesn't flap the upstream.
Correctness doesn't depend on the linger — the guaranteed decrement means we never leak — only
cost does. *(Pre-M18 this was a last-polled timestamp, because a long poll's handler had exit
paths a dropped connection never ran; a socket handler holds the connection for its whole life,
so a `defer` decrement is both possible and simpler.)*

**Traffic sizing.** Upstream from lichess is fixed — one stream per channel, ≤15 channels,
independent of audience. Only gamchess→client egress scales: ~1 KB per move per viewer (a full
snapshot), so a ~40-move blitz game ≈ 40 KB/viewer (~10–15 KB with permessage-deflate). At tens
of concurrent spectators that's single-digit MB/hour; it only matters at thousands of viewers.

**It is session-gated even though it's anonymous upstream**, and the reason is not cost:

1. An open `/api/v1/tv/{channel}` is a **free CDN for someone else's content**, pointable by
   any script.
2. lichess sees **our IP and our User-Agent** — we went out of our way to make that traffic
   attributable so they *can* attribute it. Anything done through an open relay is done *as
   Gambit*, against the one IP whose limits every real player shares. Being identifiable and
   being an open relay is a bad combination.

**Channels: all 16 of them**, default `best` ("Top Rated" — the best game in progress on
lichess at any moment, whatever the speed, which is what a wall wants). The six speeds (`best`,
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
counts — and the spectator board says so rather than let a viewer think the board is broken.

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
the last frame and snaps both to the next one. A push only ever fires on a real change, so —
unlike the old long poll, which could re-deliver the same state on a timed-out hold and needed
a version guard against re-snapping a stale value into an *upward* sawtooth — there is no
duplicate to guard against: the client applies each snapshot exactly once and snaps on it. It
only ever spends time, never invents it, which keeps a live clock from reading higher than
what's actually left. lichess remains the only authority.

**The feed never says a game ended** — it just swaps to a new `featured`. So on a swap
gamchess publishes the new game *immediately* (with `last_game_id` set) and fetches the old
game's result from `GET /game/export/{id}` (anonymous; `status` + `winner`, a missing winner
meaning a draw) in the background, folding `last_status/last_winner` in a beat later. The client
starts its fanfare from the game id changing alone — so the fetch must NOT block the swap, or
the wall freezes with no fanfare until it returns — and matches `last_game_id` against the game
on its own board, holding the finished position for 3s with a result line (upgraded from "Game
over" to the reason when it lands), because lichess TV cuts to the next game instantly. One
request per game *end* per channel, through the same governor. No buffer accumulates: the relay
keeps only the latest state, so the hold drops everything in between by construction.

**TV is per-client and off-able.** It's one more entry in the north wall's existing cycle
(which was already per-client), with no priority over real tables. Turn TV off, or kill
gamchess, and the wall mirrors real tables exactly as it did before M9 — which was its
original job.

**Every TV control is on the spectator board itself** (walk up, press E) — channel,
follow-the-lobby, on/off. Not the settings board: picking a channel on one wall for a board
on another is what the first attempt did, and it was wrong. The lobby admin **suggests** a
channel using that same picker, so a client that has picked its own keeps it, and the admin's
own follow-the-lobby is meaningless (their pick *is* the lobby's) and isn't shown.
