# LICHESS.md — playing real lichess games from a table

Gambit plays **real lichess games** (M8): link your account and a game here is a game there,
in your real history. Four ways in — the person opposite you, a named lichess user, a stranger
from the lobby, or a shareable link a browser opponent joins. **Lichess TV** on the north wall
(M9) needs no account and no token at all.

**Provenance.** Everything here was read from the live `lichess-org/api` OpenAPI spec and
`lichess-org/lila` master on **2026-07-15**, the custody change re-derived **2026-08-05**. Not
recalled. Re-read before trusting any of it. **[SOURCE]** marks a fact inferred from lila's
source rather than a documented contract — it can change without notice.

The pre-M7 integration is not the starting point for anything; `lichess-final` holds it for
reference only. Do not restore those files.

---

## Custody: THE CLIENT holds its own token (HTTPFIX)

Read this first — half the repo's folklore is about the old world.

gamchess used to hold every linked player's token, not as a preference but because playing a
lichess game means holding a long-lived ndjson stream open, lichess has no polling substitute
(a poller gets a literal *"Please don't poll this endpoint, it is intended to be streamed"*
429), and the s&box client could not read a stream. **That was an engine bug and it is
ours-fixed** (`facepunch/sbox-public` `42cee680`: `Http.RequestStreamAsync` now sends with
`HttpCompletionOption.ResponseHeadersRead` and returns a `ResponseStream` owning the response).
Verified in the shipped source 2026-08-05: `RequestStreamAsync( uri, method = "GET", content =
null, headers = null, ct = default )` — headers included. Its own doc comment carries the two facts
the design turns on: **"the returned stream owns the response — dispose it or the connection stays
open"**, and **`HttpClient.Timeout` does not bound the body read**, so a `CancellationToken` is what
bounds how long you are willing to read. That is the OPPOSITE of `GamchessApi`'s 8s `CancelAfter`:
a game stream is bounded by the game's life, never by a clock.

So the apparatus that managed a risk we only took because of that bug is **deleted, not
mitigated**: envelope encryption (`internal/keyring`, `lichess_key_versions`, the KEK/DEK
chain), the rotation daemon and runbook, the audit sweep, the play relay
(`internal/api/relay.go`), the Board API client (`internal/lichess/board.go`), and the four
`LICHESS_*` config keys. **gamchess ends with no lichess secrets at all.** (`SESSION_SECRET`
remains — ours, and optional.)

### What gamchess still does — each a thing the client cannot

1. **Holds the redirect URI.** lichess compares `redirect_uri` byte-for-byte between authorize
   and token and the client cannot listen on a socket, so there is no loopback escape.
   `PUBLIC_BASE_URL` derives it once — which is also what keeps the test instance pointed at
   itself rather than prod.
2. **Shows the disclosure page.** Consent belongs somewhere with a URL bar.
3. **Is the directory.** Two seats need each other's lichess usernames to challenge by name,
   and neither client may simply be told the other's.

### The link flow INVERTED, and the shape is the point

It was mint-on-redirect / burn-on-callback because the callback did the exchange. Now: the
client mints a PKCE pair and registers the **challenge** (`POST /api/v1/lichess/link/start`);
the player opens the **constant** `/lichess/link` and consents; the callback **parks** the code
without burning the state; the client collects it (`link/collect`, keyed on the caller's
authenticated SteamID and **never on a state from the body**) and exchanges at lichess itself.
**Burn-on-use moved from callback to collect.** gamchess never sees a verifier, so a parked
code is inert in its hands — by construction, not by policy.

> **Keep `/lichess/link` as the constant the board copies — never show the raw authorize URL.**
> The most likely thing to get wrong, because showing the lichess URL looks simpler. The
> constant is safe *precisely because it carries no secret*: it is Steam-web-session gated, so
> whoever opens it links **their own** accounts, and handing it to a friend just links the
> friend. A raw authorize URL is bound to **your** state and **your** SteamID — a friend who
> opened it would consent on *their* lichess account and **you** would hold a `board:play`
> grant on it. Strictly worse than anything the old custody design could do.

**Identity costs ONE token transit and there is no way round it.** `POST /api/token` returns no
user id; `POST /api/token/test` is anonymous but still needs the token POSTed and returns a
`userId` only. So at link the client POSTs its fresh token to `/api/v1/lichess/claim` once,
gamchess calls **`GET /api/account`** (`security: [OAuth2: []]` — a token but no specific
scope), records what lichess echoes back, and discards it. Never stored, never logged, never in
an error string (there is a Go test for the last; `GamchessApi.Redact` is the client-side
precedent).

- **"gamchess cannot hold your token" is a PROMISE, not a structure** — the copy must not
  overclaim. Owner's call (2026-08-05): token compromise is not what we optimise against.
- **Do NOT "fix" this with a second scopeless token** — it rebuilds the pile of long-lived
  credentials this change exists to delete, in exchange for a username.
- **Do NOT accept a client-asserted identity.** A claim would let anyone squat a real account's
  row and lock its owner out of ever linking. Because the id comes from lichess, the plain
  `UNIQUE(lichess_id)` is safe and there is no claimed-vs-verified split to build.

### Where the token lives

**`FileSystem.Data`, in its OWN file (`lichess.json`), never in `player.json`** —
`PlayerData._cache` is a static blob serialized whole on every settings write, so a token
inside it would eventually land in a log or a dump; a separate file makes "forget my token" a
single delete. **No prompt and no toggle**: verbose copy instead. The risk was re-derived, not
assumed: joining an **editor host or a local-project dedicated server** compiles that host's
source into your process before the scene loads, and whitelisted C# can read `FileSystem.Data`.
A host running a **published** package sends no code at all, and memory-only bought little
anyway — anyone playing on lichess is linked *that session*, and injected code shares the
process.

> **This is NOT the rule gamchess credentials live under.** `GamchessApi._session` and
> `GamchessAuth._token` stay **memory-only**, because a gamchess session is stateless and
> **unrevokable** short of rotating `SESSION_SECRET`, while a lichess token is revokable by its
> owner on `/account/security` at any time. Different credentials, different reasoning — do not
> "make them consistent".

### Unlink, and the division of labour

`DELETE /api/token` must be signed BY the token being revoked (live spec 2026-08-05: *"Revokes
the access token sent as Bearer for this request"*, 204), which only the client holds. So the
**client revokes and then deletes**, and gamchess just forgets the row. The web unlink button
says plainly that it can only do the second half.

| Lever | Real? |
|---|---|
| User revokes our grant | ✅ on **`/account/security`** — NOT `/account/oauth/token`, which lists *personal* tokens only and hides app grants. A documented trap; the copy must name the right page. |
| A password change unlinks us | ❌ **does nothing.** Password change / "log out everywhere" touch web sessions only; `OAuthServer.auth` never reads the session flag. In-game and web copy must say this plainly. |
| We revoke someone's token | ❌ **not any more, and that is the trade.** We don't have it. Deleting the game's data drops the key from that PC and revokes nothing — the copy says so. |
| Lichess kills our whole app | ✅ but manual on their side, keyed on **`clientOrigin`** (our redirect URI's scheme+host). Ask via Discord. |

**The honest regression: a crash mid-game is no longer resigned within ~30s.**
`relay.watchAbandonment` resigned a silent seat with its own token; nobody else holds the token
now, so a dropped client's game just **flags**, exactly as for every other Board API client.
Standing up still keeps the game live (M17); only quitting drops the stream. The copy says so.

---

## Scopes

**`board:play puzzle:read puzzle:write follow:read`** (owner 2026-08-05; `msg:write` dropped
2026-08-06). The old rule — *`board:play` is the only scope we ever request* — is retired, but
read why before widening again: a scope change forces a full re-link for everyone (tokens are
long-lived, there are no refresh tokens), and **HTTPFIX forced that re-link anyway**, which made
this the one moment in the project's life when widening cost nothing extra. **The next one costs
a re-link from every player.**

- **`board:play`** plays games. All-or-nothing, no read-only subset; it also satisfies the
  challenge endpoints, whose spec lists challenge:write/bot:play/board:play as ALTERNATIVES.
- **`puzzle:read|write`** — puzzles in the lobby, on the player's real puzzle record.
- **`follow:read`** — which lichess friends are online.
- **`msg:write` was asked for and then DROPPED.** It is **SENDING ONLY, permanently**: there is
  no `msg:read` scope at all, and reading an inbox is `AuthOrScoped(_.Web.Mobile)` (lila
  `app/controllers/Msg.scala`, read 2026-08-05). So anything built on it is fire-and-forget by
  nature, and nothing was built on it. A scope nothing uses is one more line on the consent
  page a cautious player reads.

**`web:mobile` and `web:polygon` stay out for a different reason than risk.** Their own
descriptions are "Official Lichess mobile app" and "Take Take Take"; taking one means claiming
first-party status to bypass a gate lichess put on third-party board clients deliberately —
which is what blitz seeks and quick pairing are behind. **The owner's "token compromise is not
what we optimise against" call does not reach this**: that is about blast radius, this is about
honesty toward lichess. `web:mod` is moderator tooling and not ours to ask for.

> **The disclosure copy is part of the scope set and must be decided with it.** `InfoScreen`'s
> Lichess branch and `lichess_pages.go`'s consent page used to promise the grant "cannot read
> your email, read or send messages, see who you follow, or change your account" — two of those
> four became false. The copy now enumerates what the grant **can** do rather than reciting a
> shorter list of what it can't. **This is the highest-risk copy in the repo**: it is the
> sentence a cautious player reads before consenting.

---

## The flows, and which one touches the event stream

Each client acts with its own token now and can only ever commit itself, so the authorisation
story collapses into "you did it, with your own credential".

- **Paired (the two seats at a table).** Both seats POST `/api/v1/lichess/rendezvous`; White
  challenges Black **by name** and publishes the challenge id; Black accepts by id.
  **How Black learns the id is the thing a next session will get wrong.** The reflex is "Black
  opens the event stream and waits for a `challenge` event". **Don't.** White's challenge
  response carries the id, both seats are in the *same s&box lobby*, and the station already
  `[Sync]`s state — so the id rides `LichessGameController.ChallengeId`. That preserves the
  property the deleted `relay.go` documented: **the paired flow never watches
  `/api/stream/event`**, so it is not bound by the one-per-token rule.
- **Seek.** NEEDS the event stream — a real-time seek's response carries no game id (it is a
  stream of empty lines whose only job is to stay open; closing it cancels the seek), so
  lichess's own instruction is to learn about the game from `gameStart`.
- **Challenge a named stranger.** NEEDS the event stream: nothing else reports an acceptance.
  **`ChallengeKeepAlive` was deliberately NOT ported** — its only benefit is lichess's 15s ping,
  its trap has bitten once, and an explicit `/cancel` on a plain buffered challenge is simpler
  and removes a whole third stream shape. The cost is real and stated in the UI: an unanswered
  real-time challenge is swept after **~20 seconds**.
- **Open / shareable link.** NEEDS the event stream. See below.

> ### THE TWO-INTENT RULE SURVIVES, WITH A DIFFERENT JUSTIFICATION
> It existed because gamchess held everyone's tokens: a one-sided start would have let any
> linked player drag any other into a game from anywhere. **That reason is gone** — a one-sided
> start now just leaves a challenge in someone's notifications.
>
> What it still does is a **DIRECTORY-DISCLOSURE rule**: gamchess must not hand out a player's
> lichess username to whoever asks, so it reveals seat B's username to seat A only once **both**
> seats have posted an intent for the same `client_game_id`, and only to those two.
> `client_game_id` is not a secret (it is `[Sync]`ed to the lobby) — it is the rendezvous key.
> **The old wording "two independently-authenticated intents are what make it consent" is FALSE
> and must not be carried forward.**

> ### ONE EVENT STREAM PER TOKEN — a process-wide singleton
> Opening a second closes the first, **server-side, silently**. The victim reports a clean EOF,
> indistinguishable from "the game ended" — so a second stream does not error, it makes the
> first flow hang with **no message**. With client custody the hazard is newly live: a second
> table, a second s&box instance, a hotload orphan.
>
> `LichessEventStream` is therefore one owner with refcounting, never one per controller. That
> makes it impossible within a process; lichess's own rule enforces a partial version across
> processes, which is the backstop that makes the one-lichess-game-at-a-time gate tolerable as
> **advisory** (it was `relay.Join`'s `hasOtherLivePlay` server-side, and gamchess can no longer
> know). `SetupPanel`'s "playing at Table N, this table plays a local game" treatment stays.
>
> **And a stream failure must never degrade into a poll** — lichess answers a poller with a
> "please don't poll" 429. Exponential backoff (3s → 6s → 12s, capped), and never reopen a game
> stream once lichess has reported the game finished.

> ### THREAD AFFINITY AND HOTLOAD — what `LichessTvSource` does NOT teach
> `LichessTvSource` is the model for the connection LIFECYCLE (backoff, reconnect,
> unhook-before-Dispose) and teaches nothing about threads, because `Sandbox.WebSocket` hands
> you each message on the game thread. **A raw `Stream` read completes on a THREAD-POOL
> thread.** So `LichessStream`'s read loop touches only a lock-guarded queue, and every `Scene`
> / `[Sync]` / `GameObject` touch happens in `Drain()` from `OnUpdate`.
>
> **Hotload is the matching hazard and it fails silently.** An orphaned read task leaves a LIVE
> HTTP CONNECTION to lichess — which on the Board API means both "this player is present" and
> "this token's one event-stream slot is taken". Nothing errors; the next flow just never
> delivers. Every loop carries a GENERATION, checked after each read and bumped on every start,
> so an orphan exits on its next line. `Dispose` on **every** exit path.

### The shareable link IS a relayed game (open + accept?color=)

**An anonymous browser player CAN play your authed, board-relayed account.** Getting this wrong
twice is why the section exists; re-derived from the live spec 2026-07-17.

| endpoint | `security:` | what it does for us |
|---|---|---|
| `POST /api/challenge/open` | **`[]`** (anonymous) | mint the link. A `board:play` token 403s it ("Missing scope: challenge:write"), so we send **no** token. |
| `POST /api/challenge/{id}/accept?color=` | `["challenge:write","bot:play","board:play"]` | **seats our token holder** in the open challenge, on the chosen side. `color` is "only valid if this is an open challenge". |
| `POST /api/challenge/{username}` | same list | the direct challenge — which is *why* it works on `board:play`, and why the open-create 403 looked contradictory. |

Flow: create anonymously → **accept with the player's own token** (the step M8 dropped, leaving
the creator's seat empty so the game never started) → publish the opposite colour's url → watch
the event stream for the opponent joining → stream the player's side to the board. The browser
opponent needs no lichess account.

- **No `challenge:write` needed.** The pre-M8 code requested it and never needed it; the M8
  *bug* was skipping the accept step, not the scope.
- **Blitz+ only** — our side plays through the Board API, which won't play faster than blitz.
- **It is a solo flow.** `State.seek` is true; `ShareUrl` is the only extra field.
- **Colour** is which side WE take; the opponent's `share_url` is the opposite. "random"/""
  accepts without a colour and we learn our side from `gameFull`. Client-side, picking the
  colour **moves the player's seat** (`LobbyPlayer.SwitchSeat`); random moves at game start.
- **Cancellation is best-effort** — we created anonymously, so `/cancel` may be refused; an
  unjoined open challenge expires in 24h.
- **The consent model holds:** a game at a table is local unless a player picks a lichess flow.
  `InfoScreen`'s Welcome + Lichess branches say it; keep saying it.

---

## The traps

- **lila has TWO functions called `isBoardCompatible`, with different thresholds.**
  `Challenge.isBoardCompatible` is `speed >= Blitz` (estimate ≥ 180s) and gates **challenges**;
  `lila.core.game.isBoardCompatible` is `Speed(clock) >= Rapid` (≥ 480s) and gates **seeks** (via
  `SetupForm.boardApiHook`). Same name, different files, different answers. `Speed` comes from
  scalachess's `byTime(limit + 40*increment)`. **[SOURCE]**
  → **Bullet never reaches lichess by any path.** The default table (Blitz 3+0, estimate 180)
  is challengeable but **not** seekable — which is why a direct challenge is the primary flow.
  Unlimited *is* challengeable (no clock → Correspondence speed) but not seekable.
  → `Code/Game/LichessTable.cs` encodes both floors client-side, is Sandbox-free, and is
  harness-checked against every preset in `TimeControl.All`. It was the model for the whole C#
  port and survived HTTPFIX untouched.
- **A seek's `time` is MINUTES; a challenge's `clock.limit` is SECONDS.** An easy way to ask for
  a ten-second game while meaning ten minutes.
- **Omitting both clock fields is how you ask for an unlimited challenge.** Sending `0/0` asks
  for a rejected 0+0 clock instead.
- **`clock.limit` has a domain**: 0, 15, 30, 45, 60, 90, or any multiple of 60 up to 10800. Not
  a smooth range — a 100-second clock is a 400.
- **An omitted `ratingRange` does NOT mean "pair me with anyone" — it means "lichess picks a
  band centred on me", and that is the best matchmaking available to us.** Re-derived from lila
  + the spec 2026-07-16. This inverts the obvious reading, so the chain matters:
  - The field is **absolute** (`"1500-1800"`), never a delta — `^\d{3,4}-\d{3,4}$`, both ends
    within **400–2900**, `min < max` strictly. An invalid string is a **400**, not a silent
    default (`Mappings.scala` verifies before `orDefault` can fire).
  - Omitted → `RatingRange.default` = **`400-2900`** — nominally unbounded. **But a real-time
    hook never uses it.** `Hook.scala:46-54` computes `manualRatingRange =
    ratingRange.ifNotDefault` and where empty falls back to `RatingRange.defaultFor(rating)`, a
    **Gaussian band** (`Gaussian(1500, 350)`, percentile `0.2`) around **your real rating**,
    clamped 400–2900. **[SOURCE]**
  - So lichess centres on your true rating for free: **no scope, no `/api/account` fetch, no
    rating stored on the link row.** Anything we compute is worse-informed than lila is. Hence
    Gambit sends **no `ratingRange` at all** and has **no rating chip** — the old "Near my
    rating" chip sent a fixed `1400-1800` to every player, which was a lie on its face and
    *narrower and less accurate* than doing nothing.
  - **A real-time seek therefore cannot mean "anyone".** lila filters out a range equal to your
    rating ±500 as "no preference" — exactly what its own UI slider defaults to. Asking for a
    genuinely open pool would mean sending `400-2899` to dodge that equality check. **Don't** —
    it games an implementation detail to get worse pairings.
  - **Correspondence is the exception**: `Seek.scala` uses `ratingRange.ifNotDefault` with no
    Gaussian fallback, so a correspondence seek with the default range really is unbounded.
  - The **±500 clamp is web-UI-only**: `HookConfig.withinLimits` is applied by `Setup.hook`, and
    **`Setup.boardApiHook` never calls it**. **[SOURCE]**
  - `GET /api/account` is **`security: [OAuth2: []]`** — a token but no specific scope — and
    ratings live at **`perfs.<speed>.rating`**, `prov: true` marking provisional. Recorded
    because it is the fact that *looks* like it unblocks a rating chip; it doesn't, because the
    chip shouldn't exist.
- **An offer POST always answers `200 {"ok":true}`, whether or not lichess took it.**
  `setDraw`/`setTakeback` return Unit and the controller wraps them in `fuccess`, so the
  documented `400 "The draw offering failed"` never fires. lichess silently drops a draw offered
  before ply 2, a second draw within 20 ply of your last, and a takeback before both sides have
  moved. **The only truth is the standing offer on the NEXT `gameState`** —
  `wdraw`/`bdraw`/`wtakeback`/`btakeback`, which lichess **omits when false** rather than sending
  false. Nothing may report an offer landed from a status code. This is why the takeback button
  is hidden before move 2 rather than shown and dead.
- **A declined DRAW is INVISIBLE on the Board API — and this is where draw and takeback
  DIVERGE.** Re-derived from lila master 2026-07-22. Both offers push a `gameState` on the
  *offer*. On the *decline*, only **takeback** publishes: `Takebacker.no` calls
  `publishTakebackOffer`, so a declined takeback clears its flag on the next `gameState`.
  **`Drawer.no` does NOT** — it clears `isOfferingDraw` and emits only `Event.DrawOffer(by =
  none)`, which feeds lila's own web round-socket, never a bus channel the Board stream
  subscribes to. **The CLEAR is never its own event either**: a move routes through `Drawer.no`
  too (`MovePlayer.scala:77`), but that move's `gameState` is built one line *before* the clear
  (`notifyMove`, `:73`), so it still carries `wdraw:true`; the flag only reads false on a LATER
  move's full snapshot. Earliest the offerer's own next move (~2 plies out); on a bare decline
  **never**. So no client may show "waiting for a reply" on a lichess draw — `GameHud` says
  **"Draw offered. Lichess won't signal a decline."** and keeps the honest "waiting for your
  opponent" only for a takeback (whose decline IS pushed) and for a LOCAL draw.
- **Draw and takeback are ONE endpoint each, not three.** `/draw/{accept}` and
  `/takeback/{accept}`: offering and accepting are the same call, and the path segment is parsed
  by lila's `Form.trueish` (`1|true|True|on|yes`) — so decline is "any non-truthy word", not a
  `no` keyword. Both are on the **Board** API, not just the Bot API.
- **A takeback offer arrives as an ordinary `gameState`.** There is no takeback event type.
- **Premove is not a lichess concept.** No API surface, no server involvement — every `premove`
  hit in lila is `ui/` TypeScript or a *user preference*. A premove is just "POST the move the
  instant it is legal", so ours is client-only by nature, not by choice.
- **Quick pairing and blitz seeks are both locked behind ONE door: the `web:mobile` scope.**
  Re-derived from lila + lila-ws master 2026-07-16, correcting an earlier claim that the API
  simply forbids them. It doesn't — it gates them on being lichess's own app.
  - **Quick pairing is not `POST /api/board/seek`.** A seek is a *hook*, quick pairing is a
    *pool*. `grep -i pool` over lila's `conf/routes` returns **nothing** — no HTTP endpoint at
    all. Pools live on the **WebSocket lobby** (`poolIn`/`poolOut` in lila-ws's `ClientOut`),
    and lila-ws's bearer auth requires **`web:mobile` or `web:polygon`** — a `board:play` token
    cannot authenticate to lila-ws, full stop.
  - **Blitz seeks are not universally refused.** `SetupForm.boardApiHook` takes an
    **`allowFastGames`** flag that skips the Rapid check, and `Setup.boardApiHook` passes
    `ctx.isMobileOauth || ctx.isTakex3 || (ctx.isAnon && isLichessMobile)`. Both are scope checks.
  - **We do not request it, and that is a rule, not an oversight.**
  → The consequence stands: **a blitz table can never find a stranger**, and quick pairing is
  not a feature we can have. The direct challenge is the primary flow *because* of this.
- **A real-time seek's response carries no game id.** A **correspondence** seek is the exception:
  plain JSON, returns `{"id":…}` immediately, no held connection. There is also an undocumented
  `DELETE /api/board/seek` (`Setup.boardApiHookCancel`) the spec omits.
- **Tokens are long-lived (~1 year) with no refresh tokens.**
- **Closing a challenge keep-alive stream does NOT withdraw the challenge.** lila only stops a
  15s ping; the challenge goes Offline and stays acceptable for **hours** (a later ping revives
  it). Read from lila, not the OpenAPI doc, which says the opposite. Gambit doesn't use the
  keep-alive stream at all and still POSTs an explicit `/cancel`.
- **Imported games are unrated and attributed to NOBODY** — `[White]`/`[Black]` are display
  strings, never account links. `POST /api/import` makes a game *viewable*, not *counted*. A
  strictly weaker outcome than playing live, and the copy must not imply otherwise.

---

## TV: no token, no custody, no security surface (M9)

**`GET /api/tv/{channel}/feed` is `security: []` — anonymous.** Nothing to encrypt, revoke or
audit. **None of the custody story above applies to TV, and none of it may creep in**: TV must
keep working for a player who has never linked. The shared ndjson reader takes a BLANK token for
exactly this, and a test asserts no `Authorization` header goes out — attaching a player's
`board:play` token to an endpoint that never asked for it, on a stream held open for hours,
would be a real leak.

**The invariant that pays for the proxy: one upstream stream per CHANNEL.** 100 players on blitz
cost lichess one stream. That is why TV goes through gamchess rather than each client hitting
lichess (lichess advocates precisely this), and why per-client channel choice is affordable — the
cost is bounded by the channel count (15), not the player count. **Since M18 this is ref-counted
by a CONNECTION COUNT** (`tvChannel.conns`): `watch` increments, and a `defer h.tv.leave(...)` in
the socket handler decrements on **every** exit path. The **sweeper is the only thing that closes
an upstream**, dropping a channel a short `tvLingerTTL` (~10s) after its count reaches zero so an
A→B→A switch doesn't flap it; correctness rests on the guaranteed decrement, not the linger.
*(Pre-M18 it was a last-polled timestamp, because a long poll's handler had exit paths a dropped
connection never ran. A WebSocket handler holds the connection for its whole life, so a `defer`
is both possible and leak-proof.)*

**Still session-gated, and not for cost reasons.** An open `/api/v1/tv/{channel}` is a free CDN
for someone else's content, and lichess sees our IP and User-Agent — we made that traffic
attributable on purpose, so anything done through an open relay is done *as Gambit*, against the
one IP whose limits every player shares.

**The wire shape is NOT the Board API's** (read off the live feed 2026-07-15; every part of this
was wrong when recalled from memory first):

- The envelope is **`{"t":…,"d":{…}}`** — not the `{"type":…}`-with-fields-inline that
  `/api/board/game/stream` uses. Two lichess streams, two envelopes.
- `players[]` **nests name/title under `user`** (absent for anon/AI — hence `Name()` returning
  "Anonymous" rather than dereferencing), with `rating`/`seconds` as siblings. `seconds` is the
  STARTING clock, and it's what stops the wall reading 0:00 until the first move.
- **`wc`/`bc` are SECONDS.** The Board API sends the same idea in **milliseconds**. Seconds
  happens to be what `TimeControl.Format` takes, so nothing converts — don't generalise it.

**The client-facing transport is a WebSocket push (M18), one socket per channel.** gamchess still
reads the lichess ndjson upstream and stays the sole stream holder. What changed is the
gamchess→client hop: it was a version-gated long poll with a `since`/`version` cursor, a
`hold_ms` field and a clock-latency apparatus bolted on to make a 5s-held poll feel live. It is
now `wss://…/api/v1/tv/{channel}` pushing **one full, self-contained `TvState` snapshot per state
change** — no deltas, no cursor, latest-wins. `LichessTvSource` holds the socket, applies each
snapshot, and reconnects on a drop; switching channels reconnects and the stored latest frame is
pushed on connect. `tvState` (the long-poll handler) is gone; `tvSocket` upgrades.

### The clock

**lichess only sends a clock when a MOVE happens**, so a TV clock rendered raw sits frozen
through every think and reads as a broken board. `LichessTvSource` runs the side-to-move's clock
down locally from the last frame and snaps both to whatever the next one says. lichess stays the
only authority, and local drift cannot outlive one move.

**The house rule: a live clock must never read HIGHER than the time actually left.** Reading low
is explicitly permitted. One-directional on purpose — which is why a deliberate undershoot
satisfies it where an unbiased estimate would not. (Same rule that makes `TimeControl.Format`
truncate where the PGN writer rounds.)

M18 made the mechanism tiny. A moving game's frames are FRESH, so the client flooring the
displayed second absorbs sub-second transport latency for free and the steady-state clock reads
low with **no correction at all**. The one case the floor can't cover is a client that CONNECTS
mid-think: gamchess hands the new socket its stored frame, already stale. So `TvState` carries
**one** field, `age_ms` (~0 on a live push, the real staleness on connect/replay), and the client
subtracts it — a **duration, not a timestamp**, because we do not share a wall clock with
gamchess and a skewed absolute stamp would correct in either direction, *including up*. On top, a
fixed `ClockLeadSeconds` (~0.25s) deliberate undershoot: the **lichess→gamchess** leg is
irrecoverable (nothing downstream knows lichess's move-instant T₀), so a small residual HIGH bias
survives there, and shaving a quarter-second is free insurance in the one permitted direction.

**The correction applies to the TICKING seat only** — the idle side's bank is exactly right
however stale the frame, and subtracting from both would invent a loss of time that never
happened.

*This replaced the M11 apparatus wholesale* (`clock_age_ms` **and** `hold_ms`, `LagOf`,
`_bankLag`, `MaxLagSeconds`, a measured round trip): a push has no hold to measure and no cursor
to reconcile. **No sawtooth to guard against either** — the old long poll re-delivered the current
state at the same version every ~5s through a think, so re-snapping restarted the countdown from
an already-stale value and the clock ticked down 5s then jumped back UP, forever. A push only
fires on a real change. The version cursor that gated it is gone.

### The fanfare — which half does what is the whole lesson

**The feed NEVER says a game ended.** Ninety-five seconds of `ultraBullet` is 5 `featured` and
203 `fen` and nothing else: a game ending is just a swap to a new `featured`. There is no
gameOver frame — don't go looking for one.

- **The CLIENT decides a game ended.** Two ways, and the fast one is the point:
  - **From the POSITION** — a checkmate or stalemate is IN the FEN, so
    `LichessTv.TryPositionResult` reads it off the frame the mating move lands on and the wall
    freezes and announces **instantly**, deriving who+why locally. No server, no wait. This is
    **the one place the TV path reads the rules**, gated to `IsStandardRules` (`Group.Speed`) —
    a Crazyhouse/Atomic/Antichess "mate" isn't one, and the vendored rules are standard-only. The
    spectator BOARD still parses nothing; this is `LichessTvSource` calling `ChessGame.TryFromFen`,
    harness-proven. Without it the wall runs the mated side's clock down for **~2s** until the swap.
  - **From the featured id changing** — the fallback for everything the position can't show: a
    resign, a flag, a draw agreement, every variant. Needs nothing from the server.
- **gamchess supplies the REASON for that fallback**: it notices the same swap and fetches the old
  game's result from **`GET /game/export/{id}`** (anonymous, like the feed; `status` + `winner`,
  where a **missing winner means a draw**), publishing it as `last_game_id/last_status/last_winner`.
  The client uses it only if `last_game_id` is the game it was showing, and only if the position
  path didn't already announce the end.

The first version had the client WAIT for `last_game_id` before announcing anything — which
silently made the whole feature depend on the server half being deployed, and **a fanfare that
never fires looks identical to one that isn't wired up**: two rounds of testing and a wrong
diagnosis.

The client holds the finished position for `LichessTv.FanfareSeconds` (3s), because lichess cuts
to the next game instantly and on a wall that reads as a glitch. **The HOLD (freeze position +
clocks) is decoupled from the BANNER (who+why).** The hold starts the instant we know the game
ended; the banner shows a real WHO-WON / WHY line and nothing else — **no bare "Game over"
placeholder** (`LichessTv.Result` returns a null headline when it can't say who or why, and the
wall shows no banner rather than a placeholder that rewrites itself).

**The swap and the reason are published SEPARATELY, and getting that wrong is what made the
fanfare arrive late.** The export fetch is one request per game END per channel, but it was
originally **synchronous inside the stream reader**, blocking the featured swap until it returned
(up to `tvResultTimeout`, 3s). Because the client starts the fanfare purely from the game id
changing, that blocked the *whole announcement*. The synchronous version justified itself as
"the ending and its replacement land in one state so the client can never show the new game
first" — but the client already refuses to advance during its own 3s hold, so ordering was never
at risk. Now gamchess **publishes the swap immediately** (with `last_game_id` set, `last_status`
empty) and fetches the reason in a **background goroutine**, folding it in with
`tvChannel.setLastResult`. That method **drops a stale answer** (a fast channel can swap again
mid-fetch) and on that no-op **does not close `changed`** — a spurious wake would re-push the full
snapshot to every client and make them re-snap their locally-run clocks off an unchanged value.
The client, still holding, **upgrades** the fanfare line from "Game over" to "White wins — out of
time" when the later push carries the reason (guarded on `InFanfare && _gameId ==
_fanfareShownFor`).

**There is no buffer and nothing to bound.** gamchess keeps only the LATEST state per channel (one
slot, overwritten), so "hold 3s, then take whatever is current" abandons all but the latest by
construction — no queue, no catch-up, no speed-up logic.

### Channels

**All 15, variants included** (default `best` — "Top Rated", the best game in progress whatever
the speed; a wall wants something worth looking up at). This was **six** at first, excluded
because the vendored rules are standard-only so a variant FEN "can't be drawn" — **that was
wrong, and the mistake is instructive**: the standard-only rule governs *playing* and was carried
over to the wall's DRAWING, which parses nothing. `SpectatorBoard3D` takes the placement field
alone and walks its characters under a `file < 8 && rank >= 0` guard, so Chess960's X-FEN
castling (`HDhd`) is never read, Crazyhouse's pockets (`…/RNBQKBNR[Pp]`) fall off the guard,
Three-check's counters ride at the end, and the rest are plain standard placement. Proven against
every variant's real starting FEN in the dotnet harness. **Before excluding something for "the
board can't draw it", check what actually reads the FEN.**

Two channels hide state the 64 squares can't hold — Crazyhouse's pockets and Three-check's counts
(`LichessTv.HidesState`) — and the spectator board says so, because a viewer who can't see the
pockets should know they exist rather than conclude the board is broken.

**Every TV control lives on the SPECTATOR board (`SpectatorScreen`) and nowhere else** — channel,
follow-the-lobby, on/off. They were briefly split onto the south-wall settings board, which meant
picking a channel on one wall for a board on another. One board: the one you're standing at when
you care what's on. **The admin uses the same picker** — theirs moves the lobby's suggestion
instead of setting a personal override, which is why `FollowingLobbyTv` is unconditionally true
for an admin and the toggle is hidden for them rather than shown dead.
`RequestSetSuggestedTvChannel` still re-checks host-side; `LocalIsAdmin` is a UI hint, never
authority.

**The channel allowlist (`lichess.ValidChannel`) is a security boundary, not a menu**: the key
comes off the wire and becomes a lichess URL, so nothing may build one from a key that didn't come
out of it. That it now holds every channel lichess offers doesn't make it decoration — the set is
closed and ours. `LichessTv` mirrors it client-side for the UI only; if they disagree the server
wins, and a Go test reads `LichessTv.cs` and holds the two lists together so they can't drift.

---

## Being a good API citizen — now in TWO places

The rules live in `server/internal/lichess/etiquette.go` **and**
`client/Code/Api/Lichess/LichessEtiquette.cs`, because both halves talk to lichess. They are the
same rules and the client's are harness-proven (`scripts/lichess_harness`).

**What changed:** Gambit's relay used to be ONE IP, so every player shared one budget. Each client
spends its own IP now, and the only Gambit traffic left on the server's IP is **TV**.

**What did NOT change, and must not be "simplified" on the grounds that it's my own budget now:**

- **Identify ourselves on every request, streams included** — and the two halves cannot do it the
  same way, which cost a broken link flow to find out. Server-side a RoundTripper sets a real
  `User-Agent` and no call site can forget. **The client cannot set `User-Agent` AT ALL**: it is
  on `Http.ForbiddenHeaders`, so `Http.CreateRequest` **throws**
  `InvalidOperationException("Not allowed to set header 'User-Agent'")` — and even past that,
  `SboxHttpHandler.HandleRequestAsync` `Remove`s the header and re-adds `"facepunch-sbox"` on
  every send, redirects included (read from the shipped engine 2026-08-06; `Referer` and `Origin`
  are forced/forbidden the same way, and `WebSocket.Connect` applies the same list). **There is no
  bypass and `TryAddWithoutValidation` is not one.** So the client sends the same string under
  **`X-Gambit-Client`** (`LichessEtiquette.IdentityHeader`) — honest and attributable even though
  nothing reads it, and what lets someone reading lichess's logs join our TV traffic to our game
  traffic. Fixing it properly is an upstream change (let a game APPEND to the engine UA) and the
  forced UA is deliberate, so ask first.
  **The single-seam rule matters more now**: every lichess request is built in `LichessClient`,
  exactly as `GamchessApi.SendAuthed` is the only `Http.RequestAsync` call site for our backend. A
  `Http.Request*` to lichess.org anywhere else in `client/` is a bug.
- **A 429 anywhere stops everything for a full minute.** Their words. Per-IP.
- **Self-limit lobby seeks** to lila's own 5/min/IP (`Limiters.setupPost` **[SOURCE]**). **Keeps
  its number, loses its reason:** a player mashing the button now earns a 429 that arms *their
  own* 60-second stop, so refusing locally with a legible reason is still strictly better — and a
  household, LAN party or NAT still shares an IP. **Delete every string saying the budget is
  shared by the whole playerbase**; they are gone from the code, README and copy.
- **Never retry into a throttle.** Report the reason; let the player decide.
- **Dispose every stream on every path.** New with client custody and it fails silently: a leaked
  game stream means "this player is present" to lichess (so the opponent gets no away signal) and
  a leaked event stream holds this token's one slot.
- **ONE lichess game per player at a time; further tables play locally.** You can sit at N tables
  and play N games (M17 decoupled seat/camera/relay), but only the FIRST is on lichess. **A
  deliberate gate, not a code limit reached:** lichess does not document permission to play
  concurrent games through the Board API (the event stream is one-per-token, the docs are silent
  on multiple board games, and the one public thread reports the streams *interfere* and was closed
  privately — checked 2026-07). If lichess ever documents an allowance, this is what to lift. **It
  is ADVISORY since HTTPFIX** — it was `relay.Join`'s `hasOtherLivePlay` server-side, and gamchess
  can no longer know. What enforces a partial version for free is lichess's own one-event-stream
  rule; what shows it is `SetupPanel`'s "playing at Table N" (`LobbyPlayer.LichessGameElsewhere`),
  which cannot see a second s&box instance. **Local two-seat games have no such limit.**

**An abandoned game is no longer resigned for you** — see the custody section. Standing up keeps
the game live (M17); only quitting drops the stream.

The accommodation channel is **Discord `#lichess-api-support`**
(`https://discord.gg/MS9MejQqha`) — **not email**; there is no API branch in their contact form.
Bring real traffic numbers and the specific limit hit; outcome is discretionary. **There is
probably nothing to ask for any more** — see PLAN.

---

## Asset exception: the lichess logo

Inlined on the web button that leaves for lichess. **Explicitly non-free** — lila's `COPYING.md`
files `public/logo` under "Exceptions (non-free)" with the terms *"Only use to refer to
lichess.org"*, and lichess publishes no brand guidelines beyond that line. That grant is exactly
what the button does, and the limits it implies are hard rules: only on a control that navigates
to lichess, never as decoration, never in the s&box client, and never anywhere it could read as
endorsement — **lichess has not endorsed Gambit**. Full terms in `Assets/ATTRIBUTION.md`.
