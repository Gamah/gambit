# CLAUDE.md — Terry's Gambit s&box Client

**Terry's Gambit** (repo/ident: `gambit`, org `gamah`, namespace `Gambit.*`) — chess
in a social s&box lobby, backed by **gamchess**, our own Go/Postgres service. Forked
from rotaliate-client: the walk-around lobby, station ring, and networking scaffolding
are inherited; the arcade game and its Go backend were replaced by chess boards and
gamchess.

This file is the durable reference: how the game is built and the s&box lore that keeps
biting. **`PLAN.md` is only upcoming work and open issues** — read it for what's left,
not for how things work.

### Lichess: real games, and gamchess holds the token

Gambit plays **real lichess games** from a table (M8): link your lichess account, and a game
here is a game there, in your real history. Two ways in — play the person sitting opposite
you (a direct challenge), or play a stranger (a lobby seek). It also puts **lichess TV** on
the north wall (M9) — which needs no account, no link, and no token at all.

The old pre-M7 lichess integration is **still not the starting point** for anything. It was
ripped out on `m7-gamchess-identity` and M8 was a clean-slate rebuild against re-derived API
facts. The `lichess-final` tag holds the old one for reference only; do not restore those
files.

**Re-derive the API facts. This rule keeps earning its keep.** Everything below was read from
the live `lichess-org/api` OpenAPI spec and `lichess-org/lila` master on 2026-07-15, not
recalled. Re-read before trusting any of it — a stale constraint is worse than none. Facts
marked **[SOURCE]** are inferred from lila's source, not a documented contract, and can change
without notice.

#### The custody decision: gamchess holds the token (position 2)

**Why it is not a preference.** Playing a lichess game requires holding a long-lived ndjson
stream open, and lichess has no polling substitute (they answer a poller with a literal
*"Please don't poll this endpoint, it is intended to be streamed"* 429). The s&box client
**cannot read a stream**: `Http.RequestAsync` buffers the whole body before returning,
`HttpCompletionOption` is off the API whitelist, and `Http.RequestStreamAsync` is broken
upstream (it `using`s the response then returns its stream). So whoever reads the stream must
hold the token. `board:play` is also a single all-or-nothing scope — there is no read-only
subset to give gamchess while keeping write capability on the client.

The ideal (client-only token) is **two small upstream engine changes away**. Nothing here may
foreclose that migration.

**What that costs, and what pays it down.** An RCE or DB dump hands over every linked player's
account for up to a year, and **lichess has no bulk revoke** — `DELETE /api/token` kills one
token and must be signed by *that* token. So:

- **Tokens are encrypted at rest** (AES-256-GCM, per-row nonce, `LICHESS_TOKEN_KEY`). A blank
  key switches lichess **off**; it never falls back to plaintext. **No rotation path exists
  yet** — changing the key invalidates every link. Back it up.
- **The audit sweep is the only fast lever we own.** `POST /api/v1/lichess/audit` →
  `POST /api/token/test` (1000 tokens/call) says which of our tokens are still live, in
  seconds. It cannot revoke them. Auditing is the capability; mass revocation is not.
- **Unlink revokes then deletes**, best-effort revoke, never fatal.

**Incident-response reality** (verified against lila):

| Lever | Real? |
|---|---|
| User revokes our grant | ✅ on **`/account/security`** — NOT `/account/oauth/token`, which lists *personal* tokens only and hides app grants. A documented trap; the copy must name the right page. |
| A password change unlinks us | ❌ **does nothing.** Password change / "log out everywhere" touch web sessions only; `OAuthServer.auth` never reads the session flag. The in-game and web copy must say this plainly. |
| We mass-revoke | ❌ no bulk endpoint. N serial signed calls. |
| We audit | ✅ seconds. |
| Lichess kills our whole app | ✅ but manual on their side, keyed on **`clientOrigin`** (our redirect URI's scheme+host). Ask via Discord. |

#### Identity and authorisation

- **`client_id` is `net.gamah.gambit`, a CONSTANT in `internal/lichess`, not config and not a
  credential.** lichess has no client registration (`client_id required (choose any)`), and
  **does not record it on the token** — an `AccessToken` stores `clientOrigin` and has no
  client_id field. So changing it revokes nothing and configures nothing; the player's "revoke
  this app" button and any lichess-side kill both key on the ORIGIN. It is public and
  **impersonable by design** (lichess cannot bind a redirect_uri to an unregistered client_id),
  so it authenticates nothing: PKCE secures the exchange, the redirect URI decides who gets a code.
- **`redirect_uri` is derived once** from `PUBLIC_BASE_URL` (`+ "/lichess/callback"`), exactly as
  `steamReturnURL()` is. lichess compares it **byte-for-byte** between authorize and token, and
  deriving it once is also what keeps the test instance pointing at itself rather than prod.
- **Starting a game against the other seat needs BOTH seats to POST** an intent for the same
  `client_game_id`, each FP-authenticated. This is the whole authorisation story, not a
  formality: gamchess holds a token for every linked player, so a one-sided start would let any
  linked player drag any other into a game from anywhere. `client_game_id` is **not a secret**
  (it is `[Sync]`ed to the lobby) — it is the rendezvous key; the two FP tokens are the
  authority. A **seek** needs one caller: nobody else is being committed to anything.
- gamchess holds both seats' tokens, so it **challenges with White's and accepts by id with
  Black's** — it never watches `/api/stream/event` for the paired flow, and so is not bound by
  the one-event-stream-per-token rule there. A seek DOES need the event stream (a seek's own
  response carries no game id), which is why only one seek per user may run.

#### The traps

- **lila has TWO functions called `isBoardCompatible`, with different thresholds.**
  `Challenge.isBoardCompatible` is `speed >= Blitz` (estimate ≥ 180s) and gates **challenges**;
  `lila.core.game.isBoardCompatible` is `Speed(clock) >= Rapid` (≥ 480s) and gates **seeks**
  (via `SetupForm.boardApiHook`). Same name, different files, different answers. `Speed` comes
  from scalachess's `byTime(limit + 40*increment)`. **[SOURCE]**
  → **Bullet never reaches lichess by any path.** The default table (Blitz 3+0, estimate 180)
  is challengeable but **not** seekable — which is why a direct challenge is the primary flow.
  Unlimited *is* challengeable (no clock → Correspondence speed) but not seekable.
- **A seek's `time` is MINUTES; a challenge's `clock.limit` is SECONDS.** An easy way to ask
  for a ten-second game while meaning ten minutes.
- **Omitting both clock fields is how you ask for an unlimited challenge.** Sending `0/0` asks
  for a rejected 0+0 clock instead.
- **`clock.limit` has a domain**: 0, 15, 30, 45, 60, 90, or any multiple of 60 up to 10800.
- **An offer POST always answers `200 {"ok":true}`, whether or not lichess took it.**
  `setDraw`/`setTakeback` return Unit in lila and the controller wraps them in `fuccess`, so
  the documented `400 "The draw offering failed"` never fires. lichess silently drops a draw
  offered before ply 2, a second draw within 20 ply of your last, and a takeback before both
  sides have moved. **The only truth is the standing offer on the NEXT `gameState`** —
  `wdraw`/`bdraw`/`wtakeback`/`btakeback`, which lichess **omits when false** rather than
  sending false. Nothing may report an offer landed from a status code. This is why the
  takeback button is hidden before move 2 rather than shown and dead.
- **Draw and takeback are ONE endpoint each, not three.** `/draw/{accept}` and
  `/takeback/{accept}`: offering and accepting are the same call, and the path segment is
  parsed by lila's `Form.trueish` (`1|true|True|on|yes`) — so decline is "any non-truthy
  word", not a `no` keyword. Both are on the **Board** API, not just the Bot API.
- **A takeback offer arrives as an ordinary `gameState`.** lila `pushState`s on
  `BoardTakebackOffer` exactly as it does on a move — there is no takeback event type to
  look for. (The offer's classifier subscribes to `BoardTakeback.makeChan`, which *reads*
  like a dead handler until you find `BoardTakebackOffer.makeChan = BoardTakeback.makeChan`.)
- **Premove is not a lichess concept.** There is no API surface for it and no server
  involvement: every `premove` hit in lila is `ui/` TypeScript or a *user preference*
  (`enablePremove`) that lichess's own client reads. A premove is just "POST the move the
  instant it is legal" — so ours is client-only by nature, not by choice.
- **Quick pairing is unreachable, and `POST /api/board/seek` is not the same thing.**
  lichess.org's homepage pools are a **WebSocket lobby** concept (`poolIn`/`poolOut` in
  lila-ws); `grep -i pool` over the whole OpenAPI spec finds one line of prose saying pools
  are off-limits, and lila's `conf/routes` has no pool endpoint at all. The seek is the only
  random-opponent mechanism the Board API has — and it's Rapid+ (see the `isBoardCompatible`
  trap above), so **a blitz table can never find a stranger**, by any path.
- **A real-time seek's response carries no game id** — it is a stream of empty lines whose
  only job is to stay open (closing it cancels the seek), which is why the seek flow needs
  the event stream and the paired flow doesn't. A **correspondence** seek is the exception:
  plain JSON, returns `{"id":…}` immediately, no held connection. There is also an
  undocumented `DELETE /api/board/seek` (`Setup.boardApiHookCancel`) that the spec omits.
- Tokens are long-lived (~1 year) with **no refresh tokens**. A scope change forces a full
  re-link for everyone, so `board:play` is the only scope we ever request.
- **Imported games are unrated and attributed to NOBODY** — `[White]`/`[Black]` are display
  strings, never account links. `POST /api/import` makes a game *viewable*, not *counted*. It
  is a strictly weaker outcome than playing live, and the copy must not imply otherwise.

#### TV is the exception: no token, no custody, no security surface (M9)

**`GET /api/tv/{channel}/feed` is `security: []` — anonymous.** No token, no scope, nothing
to encrypt, revoke, or audit. **None of the custody story above applies to TV, and none of it
may creep in**: TV must keep working for a player who has never linked and never will. The
shared ndjson reader takes a BLANK token for exactly this, and a test asserts no
`Authorization` header goes out — attaching a player's `board:play` token to an endpoint that
never asked for it, on a stream held open for hours, would be a real leak.

**The invariant that pays for the proxy: one upstream stream per CHANNEL.** 100 players on
blitz cost lichess one stream. That is why TV goes through gamchess rather than each client
hitting lichess (lichess advocates precisely this), and why per-client channel choice is
affordable — the cost is bounded by the channel count (6), not the player count. Ref-counted
by **pollers via a last-polled timestamp, not a counter**: a counter needs a decrement on
every exit path including the ones a dropped HTTP connection never gives us, and one missed
decrement leaks a stream forever. A timestamp cannot leak.

**It is still session-gated, and not for cost reasons.** An open `/api/v1/tv/{channel}` is a
free CDN for someone else's content, and lichess sees our IP and our User-Agent — we made
that traffic attributable on purpose, so anything done through an open relay is done *as
Gambit*, against the one IP whose limits every player shares. Being identifiable and being an
open relay is a bad combination.

**The wire shape is NOT the Board API's** (read off the live feed 2026-07-15; every part of
this was wrong when recalled from memory first):

- The envelope is **`{"t":…,"d":{…}}`** — not the `{"type":…}`-with-fields-inline that
  `/api/board/game/stream` uses. Two lichess streams, two envelopes.
- `players[]` **nests name/title under `user`** (absent for anon/AI — hence `Name()`
  returning "Anonymous" rather than dereferencing), with `rating`/`seconds` as siblings.
  `seconds` is the STARTING clock, and it's what stops the wall reading 0:00 until the first
  move.
- **`wc`/`bc` are SECONDS.** The Board API sends the same idea in **milliseconds**. Seconds
  happens to be what `TimeControl.Format` takes, so nothing converts — don't generalise it.

**lichess only sends a clock when a MOVE happens**, so a TV clock rendered raw sits frozen
through every think and reads as a broken board rather than a thinking player.
`LichessTvSource` runs the side-to-move's clock down locally from the last frame and snaps
both to whatever the next one says. It never invents time, only spends it — which keeps the
house rule that **a live clock must never read HIGHER than the time actually left** (the same
rule that makes `TimeControl.Format` truncate where the PGN writer rounds). lichess stays the
only authority, and local drift cannot outlive one move.

> The snap is gated on the **version advancing**, and that guard is the feature. A long poll
> that reaches its hold answers with the current state at the *same* version, every ~5s
> through any think — re-snapping on that restarts the countdown from an already-stale value,
> so the clock ticks down 5s and jumps back UP, forever. That sawtooth is worse than the
> frozen clock it replaced, because it reads HIGH.

**The feed NEVER says a game ended.** Ninety-five seconds of `ultraBullet` is 5 `featured` and
203 `fen` and nothing else: a game ending is just a swap to a new `featured`. There is no
gameOver frame — don't go looking for one.

So the wall's fanfare splits the job in two, and **which half does what is the whole lesson**:

- **The CLIENT decides a game ended**, from the featured id changing away from the one on its
  board. Nothing else can mean that, and it needs nothing from the server.
- **gamchess only supplies the REASON**: it notices the same swap, fetches the old game's
  result from **`GET /game/export/{id}`** (anonymous, like the feed; `status` + `winner`, where
  a **missing winner means a draw**) and publishes it as `last_game_id/last_status/last_winner`
  *atomically with* the new game. The client uses it only if `last_game_id` is the game it was
  actually showing.

The first version had the client WAIT for `last_game_id` to appear before announcing anything
— which silently made the entire feature depend on the server half being deployed. Against a
gamchess without it, nothing ever fired, and **a fanfare that never fires looks identical to
one that isn't wired up**: it cost two rounds of testing and a wrong diagnosis. Now an
undeployed server costs the *reason* ("Game over") and never the announcement.

The client holds the finished position for `LichessTv.FanfareSeconds` (3s) with a result line,
because lichess TV cuts to the next game instantly and on a wall that reads as a glitch.

That fetch is **one request per game END per channel**, not per move, and it goes through the
same governor as everything else. It is synchronous inside the stream reader on purpose: it
happens once per game, the frames behind it just wait in the socket, and it means the ending
and its replacement land in one state so the client can never show the new game first.

**There is no buffer, and nothing to bound.** gamchess keeps only the LATEST state per channel
(one slot, overwritten), so "hold for 3s, then take whatever is current" abandons all but the
latest by construction — no queue, no catch-up, no speed-up logic.

**All 16 channels, variants included** (default `best` — "Top Rated", the best game in
progress whatever the speed; a wall wants something worth looking up at, and blitz is a fine
game but an arbitrary one). This was **six** at first, excluded on the reasoning that the
vendored rules are standard-only so a variant FEN can't be drawn —
**that was wrong, and the mistake is instructive**: the standard-only rule governs *playing*
(`ChessGame` parses the FEN and validates moves) and was carried over to the wall, which
parses nothing. `SpectatorBoard3D` takes the placement field alone and walks its characters
under a `file < 8 && rank >= 0` guard, so Chess960's X-FEN castling (`HDhd`) is never read,
Crazyhouse's pockets (`…/RNBQKBNR[Pp]`) fall off the guard, Three-check's counters ride at
the end of the FEN, and the rest are plain standard placement. Proven against every variant's
real starting FEN in the dotnet harness. **Before excluding something for "the board can't
draw it", check what actually reads the FEN.**

Two channels hide state the 64 squares can't hold — Crazyhouse's pockets and Three-check's
counts (`LichessTv.HidesState`) — and the spectator board says so, because a viewer who can't
see the pockets should know they exist rather than conclude the board is broken.

**Every TV control lives on the SPECTATOR board (`SpectatorScreen`) and nowhere else** — the
channel, follow-the-lobby, and the on/off. They were briefly split onto the south-wall
settings board, which meant picking a channel on one wall for a board on another, with the
lobby's suggestion as a third control in a third place. One board: the one you're standing at
when you care what's on. **The admin uses the same picker** — theirs moves the lobby's
suggestion instead of setting a personal override, which is why `FollowingLobbyTv` is
unconditionally true for an admin (it's all they can be doing) and the toggle is hidden for
them rather than shown dead. `RequestSetSuggestedTvChannel` still re-checks host-side;
`LocalIsAdmin` is a UI hint, never authority.

**The channel allowlist (`lichess.ValidChannel`) is a security boundary, not a menu**: the key
comes off the wire and becomes a lichess URL, so nothing may build one from a key that didn't
come out of it. That it now holds every channel lichess offers doesn't make it decoration —
the point is that the set is closed and ours. `LichessTv` mirrors it client-side for the UI
only; if they disagree the server wins, and a Go test reads `LichessTv.cs` and holds the two
lists to each other so they can't drift silently.

#### The game session: one Facepunch call an hour, not one per request (M9)

**gamchess verified the FP token against Facepunch on EVERY authed request** — a live HTTP
call per request. That was already wrong for M8's relay poll and TV would have multiplied it
by everyone at a wall. `POST /api/v1/session` (**FP-gated only**) trades an FP token for a
`gcs_` bearer verified with a local HMAC and **zero** network.

- **Nothing about it is user-visible, and it adds no dependency.** It is minted from the
  Facepunch token the client already holds — no web sign-in, no lichess link. Those are
  unrelated things. A mint failure falls back to the FP path, which works identically and
  just costs a round-trip: **it degrades performance, never function.**
- **A session may not mint a session** (`requireFacepunch`, separate from `requireSteam`), or
  a client renews itself forever and the TTL is a fiction.
- **The audience is inside the MAC** (`aud|steamID|expiry|MAC`). Sign `steamID|expiry` alone
  and a 30-day web cookie and a 1h game bearer are the same bytes under the same key — a
  leaked cookie replayed as `gcs_<value>` would authorise the game API for a month. This is
  the reason the payload format changed, and why the M9 deploy signs every web session out
  once.
- **One hour, and it's the real tradeoff**: a session authorises everything that SteamID can
  do, including playing lichess games as them, and sessions are stateless — **there is no
  revoking one** short of rotating `SESSION_SECRET`, which signs everyone out.
- **Memory only, never `FileSystem.Data`** — same rule as the FP token, same reason (the
  rogue-lobby-host spike is still open).

#### Being a good API citizen (`internal/lichess/etiquette.go`)

**Gambit's whole relay is ONE IP**, and lichess's limits are per-IP — so every player shares one
budget, and misbehaving breaks the feature for everyone rather than throttling one user. Their
published rules (`lichess.org/page/api-tips`) are short and we follow all of them:

- **User-Agent on every request, streams included**, via a RoundTripper so no call site can
  forget. It names the project, URL and a contact — lichess records a `userAgent` per token, so
  this is how they can attribute or reach us. This matters if we ever ask for headroom.
- **A 429 anywhere stops everything for a full minute.** Their words: "wait a full minute before
  resuming API usage". Per-IP means a 429 on one call means we are collectively too fast.
- **Self-limit lobby seeks** to lila's own 5/min/IP (`Limiters.setupPost` **[SOURCE]**), refusing
  locally with a legible reason rather than spending the shared budget to earn a 429.
- **Never retry into a throttle.** Report the reason; let the player decide.

The accommodation channel is **Discord `#lichess-api-support`** (`https://discord.gg/MS9MejQqha`)
— **not email**; there is no API branch in their contact form. Bring real traffic numbers and the
specific limit hit; outcome is discretionary.

#### Corrections to old repo folklore (verified against `sbox-public` @ `ca96c2a9`)

Three long-standing claims in this file were **wrong** and are now removed:

1. **`HttpAllowList` gates nothing — the "D8 allowlist" mechanism does not exist.**
   `Http.IsAllowed` checks only scheme, loopback-port rules, IP-literals and DNS-rebinding.
   There is no per-package host allowlist in the engine. The entry in `gambit.sbproj` is inert;
   "add a host to the allowlist" is a non-step, and the old "blocked before connecting →
   allowlist is wrong" diagnostic diagnosed a mechanism that isn't there.
2. **The client cannot read a stream** — see the custody decision above. This is a fact about
   the engine, not a preference, and it is what forces server-side custody.
3. **`Sandbox.WebSocket` streams fine**, supports custom headers and incremental receive, and
   its `Connect` goes through the **same** `Http.IsAllowedAsync` — which closes the old open
   spike: yes, the URL policy covers WS, and since that policy is just scheme/IP checks,
   `wss://chess.gamah.net` is allowed. We still use a long poll, for a Go-side reason: gamchess
   would need a WebSocket library, and this repo cannot add a dependency (no Go, no Docker here
   to regenerate `go.sum`). If that changes, the transport is one function each side.

Status: gamchess client + server built, plus the M8 lichess link + Board API relay. The
**Go half compiles and its tests pass** (fetch a Go 1.22 toolchain into scratch; `go test
./... -race`). The **engine half has never been compiled** — this host has no s&box toolchain
— so expect a fixup pass on first open in the editor. Nothing is deployed (no Docker here).

---

## Project Setup (first time on a new machine)

s&box's package manager tracks local projects in its own registry — cloning the repo
and opening the `.sbproj` directly will fail with `Unable to find package 'local.gambit#local'`.

**Correct flow:**
1. Open the s&box editor → **New Project** → Game (Empty), pointed at the repo's
   **`client/`** folder (not the repo root — `client/` is the s&box project root)
2. The editor writes its own `.sbproj` and registers the project; use that file, not the one in the repo
3. The editor hotloads C# automatically — check the error list for compile errors

The registry tracks projects **by path**, so the M7 `gambit/` → `client/` rename means
re-running this flow once even on a machine that already had the project.

**Migrating a machine that predates the rename** — do both, or you get a black screen:
1. **Delete the orphan `gambit/` folder.** `git mv` only moves *tracked* files, so
   checking out the rename leaves every gitignored artefact (`.sbox/`, `bin/`, `obj/`,
   `.addon/`, `*_c`/`*_d`, generated `csproj`/`slnx`) behind in the old path. What
   remains is a source-less husk holding a stale compiled assembly. Confirm
   `git ls-files gambit/` and `git status --porcelain gambit/` are both empty first,
   then `rm -rf gambit/`.
2. **Unregister the old `gambit/` project** in the editor before adding `client/`.
   Otherwise two registry entries both claim ident `gambit`, and the editor may open the
   husk — which builds the world (you'll see `ChessSetBuilder` run) but renders nothing.

```
scripts/               ← dev utilities (not s&box assets); gen_sounds.py needs numpy
client/                ← the s&box project — open client/gambit.sbproj in the editor
  gambit.sbproj        ← reference template; editor generates the real one locally
  Code/                ← all game C# and Razor files (capital C)
  Editor/              ← editor assembly (HotloadRebuild.cs)
  Assets/scenes/       ← lobby.scene is the only production scene
  Assets/sounds/       ← .sound events referencing compiled .vsnd in sfx/
  ProjectSettings/     ← Input.config, Collision.config, Platform.config
  Libraries/gamah.skafinity/  ← procedural music library (source-committed)
server/                ← the gamchess Go/Postgres backend (M7, issue #7)
```

Each half ignores its own build output (`client/.gitignore`, `server/.gitignore`); the
root `.gitignore` holds only repo-wide junk. Unanchored `bin/`/`obj/`/`*_c` entries must
never go back in the root file — they match at any depth and would swallow `server/bin/`.

**Paths in csproj/slnx** assume Steam at `D:\Steam\`; the editor regenerates them.

This dev host has **no s&box toolchain** — no *engine* code compiles or runs locally.
Verify by careful review + grep; the user tests in their editor.

**Three things DO run here, and all three are gates worth using:**
- `node scripts/chess_js_perft.mjs` — the web viewer's chess rules.
- **The Go server.** No toolchain is installed, but one can be fetched into scratch
  (`go1.22.x.linux-amd64.tar.gz`) and `go build/vet/test ./... -race` all pass. `server/` is
  fully testable; do not claim otherwise.
- **Sandbox-free C#** via a scratch csproj (see below) — which now includes `Code/Game/
  LichessTable.cs`, the client's copy of lichess's speed floors.

**Sandbox-free C# is genuinely testable here**, and worth reaching for: `dotnet` (10.x) is
installed, and everything under `Code/Chess/` except `PerftCommand.cs` — plus
`Code/Game/TimeControl.cs` — has no engine dependency. A scratch csproj that `<Compile
Include>`s those files runs real games, real PGN, real perft. Two settings matter:
`<TargetFramework>net10.0` (net8 builds but won't launch — only the 10.x runtime is here)
and `<ImplicitUsings>enable`, because the vendored library leans on s&box's global usings
for `System.Collections.Generic`. Verified 2026-07-15. This is also how the vendored rules
were proven originally, how a `[TimeControl]`-bearing PGN was checked against the real
writer, and how `LichessTable`'s challenge/seek floors were checked against every preset in
`TimeControl.All` — prefer it over review whenever the code can be isolated from Sandbox.

---

## Architecture map (what exists and why)

### The world

> **If you change how the world behaves, the info boards are part of the change.**
> Two places describe the game to a player, and a change that doesn't update them
> ships a lie:
>
> - **`CenterInfoPanel.razor`** — the east-wall board. The short version.
> - **`InfoScreen.razor`**'s Welcome branch — walk up, press E. The long version.
>
> This is not housekeeping. Both drifted for entire milestones: the Welcome page
> announced **"RIGHT NOW: 10+0 GAMES ONLY"** and listed on-board clocks and time
> controls under **COMING SOON** long after both shipped, and the wall board's own
> "COMING SOON" list was three features that already existed. The panels say what is
> true when nobody re-reads them against the game, and nothing else fails — no test
> breaks, nothing looks wrong in a diff, and the only person who finds out is a player
> reading the front door.
>
> Ask it explicitly whenever you touch: seating or turn order, time controls, the
> spectator wall's sources, lichess, the archive, or anything a newcomer is told.
> **"COMING SOON" is the highest-risk copy in the repo** — it is a promise with an
> expiry date and no alarm.

- `LobbyRoom` self-provisions the world: it adds `ChessRing` to its GO if the scene
  lacks one, and `EnsureSpectatorWall` builds the **north-wall** spectator board (it
  was the west wall until M5 moved it one wall clockwise — `SpectatorWall`'s own
  comment is the truth, and the player-facing copy said "west wall" long after it
  wasn't). Both are self-healing, so **no scene rewire is needed** for these
  components.
- `ChessRing` builds the ring of tables (`BuildChessTable`: table, board frame, 64
  cells, pieces at the start position, two camera anchors per station) and
  network-spawns the stations. It also owns the screen-rect UI math
  (`ScreenFractionRect()` / `UiRectStyle()`).
- `ChessSetBuilder` lathes each piece as a runtime mesh. `BuildPiece(type, color, scale)`
  first tries `Model.Load("models/chess/{type}.vmdl")` and falls back to procedural —
  so dropping in a real piece set later is a one-function swap (**D5**).
- `ChessStation` holds two-seat occupancy: `[Sync(FromHost)] WhiteSeatSteamId` /
  `BlackSeatSteamId` (+ Steam names), claimed via `[Rpc.Host] RequestEnter(seat)`
  first-wins with loser-side reconciliation (**D1**). Seat cameras orbit the board
  center (`SeatOrbitRadius`/`SeatPitch`/`SeatLookDownAngle`). You take the side you
  walk up to; leaving a live game is a two-stage resign (Escape/Leave twice).
- **Wall boards go through `WallBoardGeometry` — all of them.** It owns the size
  (`BoardScale`), the aspect (`Stretch`), and the shared floor anchor (`FloorAnchor`, which
  every board calls per-frame from its own `OnUpdate`). Boards match each other because
  they share a default `PanelSize` (hence one intrinsic pixel space, hence copyable px
  font values), lay out `height:auto`, and anchor their content's BOTTOM edge — break any
  one and the board stops matching. **Every board that has ever looked wrong here looked
  wrong because it hand-rolled its own scale instead** (the M8 lichess board shipped with
  an invented `(1, 1.3, 1.1)`), and a board that skips the seam cannot be fixed from it.
  `InfoWall` used to carry a duplicate `BoardScale` knob; it doesn't now, on purpose.
  Floor *clearance* is deliberately NOT in there — it really does differ per wall (east
  runs 30, the others 60) — so it stays a per-board `[Property]` the wall passes in.
  **Adding a board means adding its `YFrac` to `lobby.scene` too**: `InfoWall` is a
  serialized component, so a new `[Property]` gets the code default while the ones
  already in the scene get the scene's — which is how the lichess board came to sit on
  top of the dev-notes board (see the rule below; it bites even when you know it).
- **`+Y` is LEFT.** s&box is Source-handed (X forward, Y left, Z up). A player facing the
  east wall looks along +X, so their RIGHT is -Y and a higher `YFrac` sits further LEFT.
  A comment in `InfoWall` claimed the opposite for a long time and put a board on the
  wrong side.
- `FloorCheckerboard` bakes a `PopMap` (checker colour) plus a `GlyphMap` (R = glyph
  index 0–6, one texel per cell). `floor_checker.shader` looks the piece up in
  `Assets/textures/chess_glyphs.png` and blends it over the square in the **opposite**
  colour (**D6**). Pops land on both square colours, round-robin over the 6 types. If
  the atlas fails to mount, no glyph indices are written → plain checker floor, never
  solid-square artefacts. Atlas is regenerated by `scripts/gen_glyph_atlas.py`
  (our own DejaVu Sans raster — CC0-clean, provenance in `Assets/ATTRIBUTION.md`).

### Chess rules (D2)
- Gera Chess Library (MIT, `d4f3f69`) is vendored+patched in `Code/Chess/Vendor/` —
  regex/Task/Span/reflection stripped for the whitelist, every change marked
  `GAMBIT VENDOR PATCH`. Verified on this host via a dotnet harness mirroring s&box
  compile settings: perft depths 1–4 on six reference positions, upstream's 67 xunit
  tests, 32 wrapper tests.
- Most vendor patches only *remove* off-whitelist constructs, but **two add behaviour**:
  `Move.Comment` and the `PgnBuilder.BoardToPgn` line that emits it, which is how
  `{[%clk]}` reaches the PGN. Both are marked and both no-op when no comment is set, so
  an un-annotated game still serialises byte-for-byte as upstream did.
- **`Code/Chess/ChessGame.cs` is the only seam callers may touch.** It caches
  `Fen`/`LastMoveUci`/`MoveCount` between moves so per-frame polling is free.
  `TryFromPgnAtPly(pgn, ply)` / `TryFromPgn(pgn)` reconstruct a position from movetext.
  `SetMoveComment(ply, text)` / `ClkField(seconds)` write the clock annotations.
- `gambit_perft [depth]` re-proves the rules in-sandbox — run it before trusting a gate.

### PGN clock annotations (`%clk`)

`{[%clk H:MM:SS[.ff]]}` per move, plus a `[TimeControl "180+2"]` header (seconds+increment;
`-` when untimed). **This is the one format Gambit shares with the outside world**, so it
follows lichess's rather than inventing one. Verified 2026-07-15 against two independent
implementations that agree — lichess-org's own **dartchess** (`lib/src/pgn.dart`) and
**python-chess** (`chess/pgn.py`):

- Hours unpadded, minutes/seconds zero-padded to two, fraction optional, **trailing zeros
  stripped** — so a whole second is plain `0:03:00`, and `.70` is written `.7`.
- Both readers cap the fraction at **three** decimals. We emit at most **two**
  (centiseconds): a third digit is false precision when the clock is decremented by a
  ~16ms frame delta, and lichess itself keeps clocks in centiseconds. Two is a strict
  subset, so both still parse it.
- `ChessGame.ClkField` **rounds**; `TimeControl.Format` (the live HUD) **truncates**. Not
  an inconsistency: a live clock must never read higher than the time actually left,
  whereas the archive should match the reference writers.

Clocks are stamped by the **host** (`NetClockStamp`), never read from a client's own
synced copy — that copy lags the increment. The `chess_js_perft.mjs` gate holds the JS
parser to real C# writer output, including a sub-second bullet fixture; both fixtures were
captured from the dotnet harness, so regenerate them there rather than hand-editing.

### Game controllers (per-station, added by ChessRing beside `ChessStation`)
`Game/IBoardGame.cs` is the render/drive abstraction; `ChessBoardView` renders the
active source with **one** branch (`Source => Lichess is { Engaged: true } ? Lichess :
Controller`). The seam paid for itself: M8 added a whole second kind of game with no renderer
change at all. Anything that reads the position should go through it — `GameHud` does too, for
the same reason.

| Controller | Networked? | What it does |
|---|---|---|
| `LocalGameController` | host-folded `[Sync] BoardFen`/`Phase`/`ClientGameId` | the two-seat game at a table, and the archive upload (**D7**) |
| `LichessGameController` | **no** — each client polls gamchess for itself | a real lichess game on this table (**M8**). Runs no clock and adjudicates nothing: lichess is the only authority, and the position is rebuilt from the UCI list it sends |
| `SpectatorController` | reads the host-folded FEN; **polls gamchess for TV** | north wall: cycles live tables, then lichess TV (**M9**) |

**While a lichess game runs, the local controller is a shell** holding the seats and the
`ClientGameId`. Its `ChessGame` never advances (moves go to the relay, not `NetChessMove`), so
its clocks and result are stale by construction — the host stops ticking them
(`HostTickClocks` early-returns on `LichessGame`) precisely so it can't flag a player who is
fine on lichess's clock. Anything reading a clock, a turn or a result during a lichess game
must read the lichess source, not `ctrl`.

### Networking (D7)
- `LobbyNetworkManager` (`ISceneStartup.OnHostInitialize` → `Networking.CreateLobby`)
  hosts; joining peers never fire that event. Players spawn by cloning the disabled
  in-scene `PlayerTemplate` GO (no `.prefab` asset — hand-authoring the format is
  undocumented) and `NetworkSpawn(connection)`.
- **The host's own avatar spawn must be deferred.** `OnActive` fires for the host
  *during* `Networking.CreateLobby`, before its connection settles, so a spawn there
  never makes it into the snapshot sent to later joiners — joiners saw every client
  but the host. `OnActive` detects `connection == Connection.Local` and defers the
  clone+`NetworkSpawn` to the first `OnUpdate`; joiners still spawn inline.
- Stations are host-built and NetworkSpawned so `[Sync]` occupancy replicates;
  everything cosmetic is local `NotSaved`/`NotNetworked`, rebuilt per client.
- The move relay is `NetChessMove(uci, fenAfter)` (`[Rpc.Broadcast]`, client→all) with
  the host folding the latest FEN into `[Sync] BoardFen` for late joiners. The
  spectator wall and late joiners read that same folded FEN — no second relay.
- Sitting plants the avatar at its side of the board facing it
  (`LobbyPlayer.BeginEngage` → `ChessStation.SeatWorldPosition`); standing restores the
  pre-sit transform so the camera hand-back doesn't snap.
- Same-machine test instances share `FileSystem.Data` (one identity). Test via the
  network status icon → "Join via new instance".
- Small race window (~RTT) if two players press E on the same seat — host picks the
  winner; known limitation.

### Dev console commands
`gambit_perft [depth]` — re-prove the chess rules in-sandbox.
`gambit_gamchess_ping` — is gamchess up?
`gambit_gamchess_signin` — mint an FP token and prove the auth round-trip.
`gambit_gamchess_games` — list your archived games.
`gambit_lichess` — am I linked, and where do I link/revoke?
`gambit_lichess_unlink` — revoke at lichess and forget the token.
`gambit_tv` — why is the TV wall doing that? Prints the whole chain: the local setting,
the channel, what the wall thinks it's showing, and gamchess's raw state. Exists because
"nothing is showing" was twice diagnosed by guesswork and once wrongly — none of the chain
is visible from outside, and a feature that never fires looks exactly like one that isn't
wired up.

---

## gamchess (the backend)

`server/` in this repo. Go/Postgres, deployed at `chess.gamah.net`; the full API
contract is in the root **README** — it is hand-mirrored in C# with no codegen, so a
contract change is one commit across both halves.

- **Identity is only ever what Steam/Facepunch says it is.** In-game: the client mints
  a Facepunch auth token (`Sandbox.Services.Auth.GetToken`), gamchess verifies it at
  `POST https://public.facepunch.com/sbox/auth/token` and trusts **only the echoed
  `SteamId`**. On the web: Steam **OpenID 2.0** (`steamcommunity.com/openid/login` —
  Steam has no OAuth2 endpoint, whatever it gets called). Both **fail closed**. A
  SteamID from a header, body, or query string is an unverified *claim* and authorises
  nothing — which is why the archive has no `?steam_id=`.
- **The archive is private.** You only ever see games you sat in. Seat SteamIDs in a
  POST are claims, so you may only archive a game you sat in; `GET /games/{id}` 404s
  (not 403s) for someone else's game so ids aren't probeable.
- **`client_game_id`** is a UUID the host mints at game start and `[Sync]`s. Move
  history lives in each seated client's own `ChessGame`, not the host's, so the host
  usually has no PGN — **both seats POST** and the second is a no-op. A client whose
  history came from a FEN resync stays quiet rather than archive a stub.
- **SteamIDs cross the wire as strings.** A SteamID64 (~7.6e16) is past JavaScript's
  2^53, so a bare JSON number is silently corrupted by the web viewer.
- **gamchess is never required.** If it's down, the game plays exactly the same —
  `GamchessApi` has an 8s timeout, never throws, and a 60s circuit breaker so a dead
  host costs one timeout rather than one per call. Nothing may block scene load,
  `OnStart`, or a game ending.

### Asset licensing

All art must be **CC0**, with **one documented exception** (below). Record provenance in
`Assets/ATTRIBUTION.md` even for CC0.

Nothing else is licensed in: pieces are runtime meshes from `ChessSetBuilder`, floor
glyphs are our own DejaVu raster, sounds are synthesized by `scripts/gen_sounds.py`, and
the web viewer uses Unicode glyphs (zero image assets).

**The exception: the lichess logo**, inlined on the web button that leaves for lichess.
It is explicitly non-free — lila's `COPYING.md` files `public/logo` under "Exceptions
(non-free)" with the terms *"Only use to refer to lichess.org"*, and lichess publishes no
brand guidelines beyond that line. That grant is exactly what the button does, and the
limits it implies are hard rules: only on a control that navigates to lichess, never as
decoration, never in the s&box client, and never anywhere it could read as endorsement —
**lichess has not endorsed Gambit**. Full terms in `Assets/ATTRIBUTION.md`.

CC0 sources on file for the D5 3D upgrade: Poly Haven "Chess Set" by Riley Queen
(https://polyhaven.com/a/chess_set, glTF/FBX); portablejim 2D chess set on FreeSVG
(https://freesvg.org/portablejim-2d-chess-set-pieces); OpenGameArt /content/chess-pieces-0,
/content/3d-chess-pieces, /content/chess-set-1, /content/chess. Kenney has no chess pack.

### HTTP: there is no allowlist (the old "D8" was folklore)

**`HttpAllowList` gates nothing.** Verified by reading the shipped engine
(`sbox-public` @ `ca96c2a9`): `Http.IsAllowed` checks only the scheme (http/https/ws/wss),
loopback-port rules, IP-literal rejection, and DNS-rebinding into private ranges. **There is
no per-package host allowlist anywhere in the engine.** The
`"HttpAllowList": ["https://chess.gamah.net/"]` entry in `gambit.sbproj` is inert — the client
can already reach any host — and "add a host to the allowlist" is a zero-cost non-step.

The entry is kept as a **declaration of intent** (it documents the only backend we mean to
talk to), not as a control. Do not rely on it, and do not diagnose against it: the old
"blocked before connecting → the allowlist is wrong" advice diagnosed a mechanism that does
not exist.

`Sandbox.WebSocket.Connect` goes through the **same** `Http.IsAllowedAsync`, which closes the
old open spike: the URL policy does cover WS, and since that policy is only scheme/IP checks,
`wss://` to our own host is allowed.

Reading a `gambit_gamchess_ping` failure (verified in-editor 2026-07-15):
- **TLS/SSL error** → the request reached a handshake; Caddy has no cert for that host
  (vhost down/not configured).
- **any HTTP status** → we reached gamchess; read the status.

### gamchess deployment facts

**Never deployed** (this host has no Docker). The Go DOES now compile and test here when a
toolchain is fetched into scratch — `make test` runs `go test ./... -race` in a container on a
machine that has Docker, and the same suite passes locally with a downloaded Go 1.22. If you
are changing the server, run the tests; "can't build it here" is no longer true for Go.

**Secrets live in `.env` and are generated, not requested.** lichess issues nothing — no client
id, no secret, no API key — so `LICHESS_TOKEN_KEY` (encrypts every stored lichess token; blank
= lichess off; **no rotation path, back it up**) and `LICHESS_AUDIT_KEY` (gates the audit sweep)
are ours. `make up` / `make testinst` mint any that `.env` lacks and **never overwrite one that
has a value** — regenerating `LICHESS_TOKEN_KEY` would silently orphan every link.

Test and prod **share** the token key deliberately: same host, same `.env`, so a second key
isolates nothing, and each only ever decrypts its own already-separate database. What does
differ is the redirect ORIGIN — lichess records `clientOrigin` per token, so `testchess` and
`chess` are **two separate apps** to lichess. A player who links on both has two grants and two
`/account/security` entries. **Linking on test is a real grant against a real account**, not a
sandbox.

Ports/hosts are allocated in the server's Caddyfile (host-side, unversioned — not in
this repo):

| | Host | App | Postgres |
|---|---|---|---|
| prod | `chess.gamah.net` | 6464 | 5435 |
| test | `testchess.gamah.net` | 6465 | 5436 |

Both are plain subdomains (a `*.gamah.net` wildcard covers them; a sub-subdomain like
`test.chess.gamah.net` would have needed its own record — DNS wildcards match one label).

All bind `127.0.0.1` only — **never punch through ufw**. Docker's iptables chains are
evaluated *before* ufw, so a `0.0.0.0` publish is internet-reachable even with ufw denying
the port; loopback binding + Caddy fronting is the whole mechanism (rotaliate documents
this at `docker/docker-compose.yml`).

Ports already taken on that host by other services: `1337`, `5432`–`5436`, `6969`, `6970`,
`8080`, `8081`. gamchess's Postgres ports continue the org's increment convention from
that range. Check the host's Caddyfile before allocating anything new.

**Deploying needs only Docker** — every Go make target runs in a container
(`golang:1.22`, module cache in a named volume), because neither the deploy host nor the
dev machine has a Go toolchain. `make up` builds and migrates in-process at startup.
`make dev` is the one target that wants a local Go.

**Add no `log` directive to these vhosts.** Auth returns land on `/auth/steam/return` **and
`/lichess/callback`** with credentials in the query string (a Steam assertion, an OAuth code),
and Caddy would write them to disk. Caddy writes no access log unless configured, so the
default is already safe — the job is not to start. Any future auth-callback route inherits
this rule.

### Identity / auth primitives (in use — see `server/internal/steam/`)

Both halves are lifted from `../rotaliate`, essentially verbatim, along with their tests.
Deviating from them is how un-compilable mistakes get in.

- **In-game**: `await Sandbox.Services.Auth.GetToken( "gamchess" )` mints a Facepunch auth
  token; the service-name argument is **cosmetic** (Facepunch validates `{steamid, token}`
  without it). Returns null rather than throwing on non-Steam builds. Verified server-side at
  `POST https://public.facepunch.com/sbox/auth/token` → `{"SteamId", "Status"}` — **no
  persona name comes back, SteamID only**. Two rules: **fail closed** on any error, and
  **trust only the echoed `SteamId`** (`Status == "ok" && vr.SteamID == steamID`), which is
  what stops a valid token for account Y authorising as account X. Confirmed working
  in-editor 2026-07-15; the token's real TTL is still an open spike (we cache 120s and
  re-mint once on a 401).
- **Web**: Steam's browser login is **OpenID 2.0, not OAuth2** — there is no Steam OAuth2
  endpoint. `steamcommunity.com/openid/login`. Keeps rotaliate's `op_endpoint` pinning,
  `return_to` scheme+host+path matching, and single-use nonce (the nonce store is ours —
  `steam.Verify` only shape-checks it and documents that single-use is the caller's job).
- Sessions are stateless HMAC-signed cookies, so a deploy doesn't sign everyone out.
  `SESSION_SECRET` blank = random per-process key (works with no config, dies on restart).
  `SameSite=Lax` is load-bearing: the OpenID return is a top-level cross-site GET and
  Strict would drop the cookie on exactly that hop.
- Display names come from **Steam** (`Connection.DisplayName`) — Gambit has no username of
  its own and no name picking. The FP path returns no name, so a server-side name would need
  `ISteamUser/GetPlayerSummaries/v0002` (Steamworks key, 100k/day — cache it). Not needed:
  the PGN carries the names.
- The same FP token authenticates `Sandbox.WebSocket` — `Connect(uri, headers)` accepts an
  `Authorization` header (sbox-docs `networking/websockets.md`), so one mechanism covers
  both a future relay and ordinary HTTP.

## s&box Patterns to Follow

- **Components**: game logic lives in `Component` subclasses; `OnUpdate()` for per-frame work
- **UI**: screens are Razor `PanelComponent`s on a `ScreenPanel` GameObject in the scene
- **State**: `[Sync]` for peer-networked state (host-authoritative with `SyncFlags.FromHost`);
  `[Rpc.Host]` request / `[Rpc.Broadcast]` relay pattern (see ChessStation occupancy)
- **Storage**: `FileSystem.Data.ReadAllText/WriteAllText` for JSON player data
- **HTTP**: `await Http.RequestStringAsync(url)`; `await Http.RequestAsync(url, "POST", content, headers)` —
  the trailing headers dictionary is undocumented in `../sbox-docs` but works
- **Hotload**: C# changes hotload in milliseconds. Procedural builders rebuild via
  `[EditorEvent.Hotload]` in `Editor/HotloadRebuild.cs` — keep new builders registered there
- **Razor usings**: `System`, `Sandbox`, `Sandbox.UI`, `Sandbox.Rendering` are NOT
  auto-imported in `.razor` — add `@using` explicitly
- **Self-attaching UI**: GameHud, SplashScreen, and SpectatorScreen attach themselves to
  the scene ScreenPanel at runtime — no scene rewire needed for new screens; copy the pattern.

## s&box API Whitelist

s&box enforces an API whitelist — blocked calls produce `error SB1000`.
See `../sbox-docs/docs/code/code-basics/api-whitelist.md`.

| ❌ Blocked | ✅ Use instead |
|---|---|
| `Array.Clone()` | manual `for` loop copy |
| `Console.WriteLine` | `Log.Info` / `Log.Warning` / `Log.Error` |
| `System.IO.*` | `FileSystem.Data` |

Rule of thumb: avoid `System.Private.CoreLib` reflection/process/threading/IO. This is
why the vendored chess library needed patching and why SHA-256 is hand-rolled. When in
doubt check `https://sbox.game/api/` or file a false-positive at
`https://github.com/Facepunch/sbox-public/issues`.

## World Scale Rules (read before placing/sizing anything)

- **Never trust code defaults or docs for component property values** — the scene
  overrides them and gets retuned in-editor. `grep Assets/scenes/lobby.scene` for
  current values before sizing anything.
- The player is ~72 units tall — the human-scale yardstick.
- `models/dev/box.vmdl` is **NOT 1×1×1**: to make a box of size S,
  `LocalScale = S / Model.Bounds.Size` per axis — use/copy `ChessRing.AddBox`.
- Never put a `BoxCollider` on a non-uniformly scaled GO — it silently freezes physics.
  Colliders on uniformly-scaled parents, visuals on scaled children.
- A WorldPanel GO's scale is a multiplier on the panel's intrinsic pixel size, not world
  units; the panel plane is local **Y (width) / Z (height)**. World-size and text size
  are coupled — to grow a board without growing text, scale the GO up and divide
  stylesheet px by the same factor.
- `FacePlayer` yaw-billboards a GO toward the camera; fronts face **+forward**.
- There is **no documented API to open a URL / Steam overlay** — show links as copyable
  text — any future link-sharing has to be click-to-copy.
  Click-to-copy pattern: `DiscordButton.Copy()`.

## UI Gotchas (learned the hard way)

- **Board vs Screen vocabulary**: a *board* is a display-only WorldPanel in the world
  (takes no pointer input); a *screen* is an interactive ScreenPanel shown while engaged
  at a station, clipped to the station rect via `ChessRing.ScreenFractionRect()` /
  `UiRectStyle()` trig.
- **Panel-rendered chess glyphs do not paint.** U+265F renders as a purple emoji, and a
  WorldPanel glyph atlas wouldn't paint either — this is why the spectator board is real
  3D `ChessSetBuilder` meshes (`SpectatorBoard3D`, with its own raking `SpotLight` for
  shadows) rather than a panel. The floor keeps the atlas because that's a shader, not a
  panel. Reach for meshes over panel art.
- Engaged-screen centering must live on an absolutely-positioned full-screen child
  (`.screen-fit` wrapper), NOT on `root` — otherwise content pins top-left.
- `transform: scale` misplaces panel content — use explicitly sized wrappers.
- `pointer-events: all` must be set per interactive element; it does not inherit.
- Panels are flex containers: inline `<span>`s inside a text div become separate flex
  items; source newlines render as literal whitespace — keep each text div's content on
  one line. A div's auto height does not grow for wrapped text — use one div per line in
  a flex column.
- Deriving font sizes from `Panel.Box.Rect` on a WorldPanel doesn't work — use fixed px
  in intrinsic pixel space, calibrated against a known-good panel.
- Don't make a panel `overflow: scroll` if it has draggable controls — s&box drag-scrolls
  it and fights the clicks.
- A free-floating interactive panel kills roaming mouselook — gate interactive screens on
  being engaged at a station, and free the cursor there
  (`UseLookControls=false`+`UseInputControls=false`, restored on close).
- The citizen `SkinnedModelRenderer` must live on a `Body` child GO, never on the
  PlayerController's own GO (animator writes to it every frame — welds the player to
  world origin otherwise).
- No documented API to add buttons to the built-in escape menu; Escape leaves the station
  via `Input.EscapePressed`.
- If a board click doesn't land, a HUD panel is eating it — the `Select`/mouse1 action
  must reach the world past the ScreenPanel.

## Sounds

Synthesized WAVs in `Assets/sounds/sfx/` generated by `scripts/gen_sounds.py` (numpy).
`.sound` gotchas: `"Sounds"` lists `.vsnd` paths (not `.wav`), `"Volume"`/`"Pitch"` are
JSON strings, `"UI": true` for 2D playback, `"__version": 1`. Mapping: tick/tock →
clocks (by side), pop → captures, servo slides → station rebuild. Move sounds fire for
your own board (2D) and other players' boards (positional).

Music is the `gamah.skafinity` library — source-committed under
`client/Libraries/gamah.skafinity/` (s&box pattern: libraries are source and
auto-referenced by living there; do NOT add a `PackageReferences` entry — that
double-registers the compiler). The scene carries a `SkafinityPlayer` +
`SkafinityMusicPanel`; the panel is enabled only while engaged at the music wall board.
