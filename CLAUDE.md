# CLAUDE.md — Terry's Gambit s&box Client

**Terry's Gambit** (repo/ident: `gambit`, org `gamah`, namespace `Gambit.*`) — chess
in a social s&box lobby, backed by **gamchess**, our own Go/Postgres service. Forked
from rotaliate-client: the walk-around lobby, station ring, and networking scaffolding
are inherited; the arcade game and its Go backend were replaced by chess boards and
gamchess.

This file is the durable reference: how the game is built and the s&box lore that keeps
biting. **`PLAN.md` is only upcoming work and open issues** — read it for what's left,
not for how things work.

`PLAN.md` follows the global flat-ranked-backlog format (see the global instructions) — this
repo is where that format came from. What is worth knowing here: **grouping rows into branches
is a judgement call** — several small rows on one wall or one panel are usually one branch; one
big row (chat, voice, the viewer) is usually its own. The table is flat so it can be regrouped
freely.

### Cutting a release (sbox.game)

Gambit publishes to **sbox.game** with a version and a **"changes"** field — the changelog
players see. Version scheme is **Alpha 0.0.x** (2D play mode was `0.0.1`; **`0.0.2` bundles M17
and M18** — multi-table play + relay/archive/HUD polish, then the lichess-TV WebSocket push +
wall-clock panic + instant checkmate fanfare — **plus PR #17, the lichess draw-decline honest-copy
fix (PLAN #2)** folded in before it published — so the next release is `0.0.3`). The changes
field is written in **five sbox.game categories, in this order**:

> **`0.0.3` is unpublished and already carries P99, M19 and HTTPFIX** (look-aim at the board;
> play the computer, cross-lobby matchmaking, the four-tab setup panel, the move-history panel;
> and the lichess token moving onto the player's own PC). **HTTPFIX is a player-facing change
> even though it reads as plumbing** — everyone re-links, the grant asks for more than it did,
> the key now lives on their machine, and a crash mid-lichess-game flags instead of being
> resigned for them. All four belong in the notes, and the last two belong under Known issues. **PR #18
> merged with NO release-notes section**, breaking the roll-up rule below — so the cutter has to
> write M19's notes from the commit log rather than concatenating them, and should not assume the
> absence of notes means the absence of player-facing change. It is the largest chunk of the
> release.

> **Added · Improved · Fixed · Removed · Known issues**

- Write **player-facing** notes — what changed in the *game*, not the code. "Play from multiple
  tables at once", not "gated `LocalSeat` on occupancy". The code reasoning stays in git/CLAUDE.md.
- **Every feature branch's PR carries its own draft notes in these five categories** — a
  `## Release notes (Alpha 0.0.x)` section in the PR body. A release is usually several merged
  branches, so **the session that actually cuts it just rolls up the open PRs' notes** into one
  "changes" block (concatenate + de-dup) rather than reconstructing them from the commit log.
- **Known issues** = deliberate limitations and shipped-but-unverified items worth flagging — *not*
  a bug backlog (that's PLAN.md). Say what a player would actually hit, and whether it's by design.
- This dev host has no s&box toolchain, so **client changes are review-only until tested in the
  editor**. Each PR should list its "needs editor verification" items; the cutter confirms that
  list was walked before publishing.

### Lichess: real games, and THE CLIENT holds the token

Gambit plays **real lichess games** from a table (M8): link your lichess account, and a game
here is a game there, in your real history. Four ways in — play the person sitting opposite
you, play a named lichess user, take a stranger from lichess's lobby, or hand out a shareable
link a browser opponent joins. It also puts **lichess TV** on the north wall (M9) — which needs
no account, no link, and no token at all.

The old pre-M7 lichess integration is **still not the starting point** for anything. The
`lichess-final` tag holds it for reference only; do not restore those files.

**Provenance for everything below:** the flows and traps were read from the live
`lichess-org/api` OpenAPI spec and `lichess-org/lila` master on **2026-07-15**, with the custody
change re-derived **2026-08-05**, not recalled. Re-read before trusting any of it. Facts marked
**[SOURCE]** are inferred from lila's source, not a documented contract, and can change without
notice.

#### The custody decision was REVERSED (HTTPFIX). The client holds its own token.

**Read this before anything else, because half the repo's folklore is about the old world.**

gamchess used to hold every linked player's `board:play` token, and it was never a preference.
Playing a lichess game means holding a long-lived ndjson stream open, lichess has no polling
substitute (they answer a poller with a literal *"Please don't poll this endpoint, it is
intended to be streamed"* 429), and **the s&box client could not read a stream**:
`Http.RequestStreamAsync` was broken upstream — it `using`d the response and then returned its
stream, so the body was buffered and the task never resolved for a stream that never ends. So
whoever read the stream had to hold the token.

**That was an engine bug, and it is ours-fixed** (`facepunch/sbox-public` `42cee680`,
`Http.RequestStreamAsync` now sends with `HttpCompletionOption.ResponseHeadersRead` and returns
a `ResponseStream` that owns the response). CLAUDE.md used to say the ideal was "two small
upstream engine changes away"; **it was one**.

So the whole apparatus that existed to manage a risk we only took because of that bug is
**deleted, not mitigated**: the envelope encryption (`internal/keyring`, `lichess_key_versions`,
the KEK/DEK chain), the rotation daemon and its runbook, the audit sweep, the play relay
(`internal/api/relay.go`), the Board API client (`internal/lichess/board.go`), and the four
`LICHESS_*` config keys. **gamchess ends with no lichess secrets at all.** (`SESSION_SECRET`
remains — it is ours, and optional.)

**What gamchess still does, and each is a thing the client genuinely cannot:**

1. **Holds the redirect URI.** lichess compares `redirect_uri` byte-for-byte between authorize
   and token, and the client cannot listen on a socket, so there is no loopback escape.
   `PUBLIC_BASE_URL` derives it once — which is also what keeps the test instance pointing at
   itself rather than at prod.
2. **Shows the disclosure page.** Consent belongs somewhere with a URL bar.
3. **Is the directory.** Two seats need each other's lichess usernames to challenge by name, and
   neither client may simply be told the other's.

**The link flow INVERTED, and the shape is the point.** It was mint-on-redirect /
burn-on-callback because the callback did the exchange. Now: the client mints a PKCE pair and
registers the CHALLENGE (`POST /api/v1/lichess/link/start`); the player opens the **constant**
`/lichess/link` and consents; the callback **parks** the code without burning the state; the
client collects it (`link/collect`, keyed on the caller's authenticated SteamID and **never on a
state from the body**) and exchanges at lichess itself. **Burn-on-use moved from callback to
collect.** gamchess never sees a verifier, so a parked code is inert in its hands — not by
policy, by construction.

> **Keep `/lichess/link` as the constant the board copies — do NOT show the raw authorize URL.**
> This is the call most likely to be got wrong, because showing the lichess URL directly looks
> simpler. The constant is safe *precisely because it carries no secret*: it is Steam-web-session
> gated, so whoever opens it links **their own** accounts, and handing it to a friend just links
> the friend. A raw authorize URL is bound to **your** state and **your** SteamID — a friend who
> opened it would consent on *their* lichess account and **you** would end up holding a
> `board:play` grant on it. That is strictly worse than anything the old custody design could do.

**Identity costs ONE token transit, and there is no way round it.** `POST /api/token` returns no
user id; `POST /api/token/test` is anonymous but you must still POST the token, and it returns a
`userId` only (no display username, so a second request). So at link the client POSTs its fresh
token to `/api/v1/lichess/claim` once, gamchess calls **`GET /api/account`** — `security:
[OAuth2: []]`, a token but **no specific scope** — records what lichess echoes back, and
discards it. Never stored, never logged, never in an error string (there is a Go test for the
last one; `GamchessApi.Redact` is the client-side precedent).

- **"gamchess cannot hold your token" is a PROMISE here, not a structure**, and the copy must not
  overclaim it. The owner's call (2026-08-05): token compromise is not what we optimise against.
- **Do NOT "fix" this with a second scopeless token.** It would rebuild the pile of long-lived
  credentials this change exists to delete, in exchange for a username.
- **Do NOT accept a client-asserted identity.** A claim would let anyone squat a real account's
  row and permanently lock its owner out of ever linking. Because the id comes from lichess, the
  plain `UNIQUE(lichess_id)` is safe and there is no claimed-vs-verified split to build.

**The token lives in `FileSystem.Data`, in its OWN file** (`lichess.json`), never in
`player.json` — `PlayerData._cache` is a static blob serialized whole on every settings write, so
a token inside it would eventually land in a log or a dump, and a separate file makes "forget my
token" a single delete. **No prompt and no toggle**: verbose copy instead. The risk was
re-derived rather than assumed (this replaces the never-closed "rogue lobby host" spike): joining
an **editor host or a local-project dedicated server** compiles that host's source into your
process before the scene loads, and whitelisted C# can read `FileSystem.Data`; a host running a
**published** package sends no code at all, and memory-only bought little anyway, since anyone
playing on lichess is linked *that session* and injected code shares the process.

> **This is NOT the rule gamchess credentials live under.** `GamchessApi._session` and
> `GamchessAuth._token` stay **memory-only**, because a gamchess session is stateless and
> **unrevokable** short of rotating `SESSION_SECRET`, while a lichess token is revokable by its
> owner on `/account/security` at any time. Different credentials, different reasoning — do not
> "make them consistent".

**Unlink is finally correct rather than best-effort, and the division of labour is the point.**
`DELETE /api/token` must be signed BY the token being revoked (verified against the live spec
2026-08-05: *"Revokes the access token sent as Bearer for this request"*, 204), which only the
client holds. So the **client revokes and then deletes**, and gamchess just forgets the row. The
web unlink button says plainly that it can only do the second half.

| Lever | Real? |
|---|---|
| User revokes our grant | ✅ on **`/account/security`** — NOT `/account/oauth/token`, which lists *personal* tokens only and hides app grants. A documented trap; the copy must name the right page. |
| A password change unlinks us | ❌ **does nothing.** Password change / "log out everywhere" touch web sessions only; `OAuthServer.auth` never reads the session flag. The in-game and web copy must say this plainly. |
| We revoke someone's token | ❌ **not any more, and that is the trade.** We don't have it. Deleting the game's data drops the key from that PC and revokes nothing — the copy says so. |
| Lichess kills our whole app | ✅ but manual on their side, keyed on **`clientOrigin`** (our redirect URI's scheme+host). Ask via Discord. |

**The honest regression: a crash mid-game is no longer resigned within ~30s.** `relay.watchAbandonment`
tracked each seat's poll and resigned a silent one with its own token; nobody else holds the
token now, so a dropped client's game just flags — exactly as it does for every other Board API
client. Standing up still keeps the game live (M17 behaviour); only quitting drops the stream.
The player-facing copy says this rather than burying it.

#### Scopes: no longer `board:play` alone

**`board:play puzzle:read puzzle:write follow:read`** (owner, 2026-08-05, `msg:write` dropped
2026-08-06). The old
rule — *`board:play` is the only scope we ever request* — is retired, but read WHY before
widening again. One of its two reasons was that a scope change forces a full re-link for
everyone (tokens are long-lived, there are no refresh tokens); **HTTPFIX forced that re-link
anyway**, which made this the one moment in the project's life when widening cost nothing extra.
It is not licence to widen casually — the next one costs a re-link from every player.

- **`board:play`** plays games. A single all-or-nothing grant with no read-only subset; it also
  satisfies the challenge endpoints, whose spec lists challenge:write/bot:play/board:play as
  ALTERNATIVES.
- **`puzzle:read|write`** — puzzles in the lobby, on the player's real puzzle record.
- **`follow:read`** — which lichess friends are online.
- **`msg:write` was asked for and then DROPPED before HTTPFIX shipped**, and the reasoning is
  worth keeping so it isn't re-asked for casually. It is **SENDING ONLY, permanently**: there is
  no `msg:read` scope at all, and reading an inbox is `AuthOrScoped(_.Web.Mobile)` (lila
  `app/controllers/Msg.scala`, read 2026-08-05). So anything built on it is fire-and-forget by
  nature — Gambit could message an opponent and never see a reply — and nothing was built on it.
  A scope nothing uses is one more line on the consent page a cautious player reads.

**`web:mobile` and `web:polygon` stay out, and for a different reason than risk.** Their own
descriptions are "Official Lichess mobile app" and "Take Take Take"; taking one means claiming
first-party status to bypass a gate lichess put on third-party board clients deliberately. That
is what blitz seeks and quick pairing are behind (PLAN's "recorded, not to fix" row). **The
owner's "token compromise is not what we optimise against" decision does not reach this** — that
call is about blast radius; this is about honesty toward lichess. `web:mod` is moderator tooling
and is not ours to ask for.

> **The disclosure copy is part of the scope set, and must be decided with it.** `InfoScreen`'s
> Lichess branch and `lichess_pages.go`'s consent page used to promise the grant "cannot read
> your email, read or send messages, see who you follow, or change your account" — two of those
> four clauses became false. The copy now enumerates what the grant **can** do rather than
> reciting a shorter list of what it can't. **This is the highest-risk copy in the repo**: it is
> the sentence a cautious player reads before consenting.

#### The flows, and which one touches the event stream

The old flows were shaped by custody: gamchess held **both** seats' tokens, so it challenged
with White's and accepted by id with Black's. Each client acts with its own token now and can
only ever commit itself, so the authorisation story collapses into "you did it, with your own
credential".

- **Paired (the two seats at a table).** Both seats POST `/api/v1/lichess/rendezvous`; White then
  challenges Black **by name** and publishes the challenge id; Black accepts by id.
  **How Black learns the id is the thing a next session will get wrong.** The reflex is "Black
  opens the event stream and waits for a `challenge` event". **Don't.** White's challenge response
  carries the id, both seats are in the *same s&box lobby*, and the station already `[Sync]`s
  state — so the id rides `LichessGameController.ChallengeId` and Black accepts by id. That
  preserves exactly the property the deleted `relay.go` documented: **the paired flow never
  watches `/api/stream/event`**, and so is not bound by the one-per-token rule. Worth having when
  a server held the stream; worth much more now a client does.
- **Seek.** NEEDS the event stream — a real-time seek's response carries no game id (it is a
  stream of empty lines whose only job is to stay open; closing it cancels the seek), so lichess's
  own instruction is to learn about the game from `gameStart`.
- **Challenge a named stranger.** NEEDS the event stream: nothing else reports an acceptance.
  **`ChallengeKeepAlive` was deliberately NOT ported.** Its only benefit is lichess's 15s ping,
  its trap has already bitten once, and an explicit `/cancel` on a plain buffered challenge is
  strictly simpler — dropping it removes a whole third stream shape from the client. The cost is
  real and stated in the UI: an unanswered real-time challenge is swept after **~20 seconds**.
- **Open / shareable link.** Unchanged in mechanism and still the subtlest. NEEDS the event stream.

> ### THE TWO-INTENT RULE SURVIVES, WITH A DIFFERENT JUSTIFICATION
>
> It existed because gamchess held everyone's tokens: a one-sided start would have let any linked
> player drag any other into a game from anywhere. **That reason is gone** — a one-sided start now
> just leaves a challenge sitting in someone's notifications.
>
> What it still does is a **DIRECTORY-DISCLOSURE rule**: gamchess must not hand out a player's
> lichess username to whoever asks, so it reveals seat B's username to seat A only once **both**
> seats have posted an intent for the same `client_game_id`, and only to those two.
> `client_game_id` is not a secret (it is `[Sync]`ed to the lobby) — it is the rendezvous key.
> **The old wording "two independently-authenticated intents are what make it consent" is FALSE
> and must not be carried forward.**

> ### ONE EVENT STREAM PER TOKEN — a process-wide singleton
>
> Opening a second closes the first, **server-side, silently**. The victim reports a clean EOF,
> which is indistinguishable from "the game ended" — so a second stream does not error, it makes
> the first flow hang with **no message**. With client custody the hazard is newly live: a second
> table, a second s&box instance, a hotload orphan.
>
> `LichessEventStream` is therefore one owner with refcounting, never one per controller. That
> makes it impossible within a process; lichess's own rule then enforces a partial version for
> free across processes, which is the backstop that makes the one-lichess-game-at-a-time gate
> tolerable as **advisory** (it was `relay.Join`'s `hasOtherLivePlay` server-side, and gamchess
> can no longer know). `SetupPanel`'s "playing at Table N, this table plays a local game"
> treatment stays.
>
> **And a stream failure must never degrade into a poll** — lichess answers a poller with a
> "please don't poll" 429. Exponential backoff (3s → 6s → 12s, capped), and never reopen a game
> stream once lichess has reported the game finished.

> ### THREAD AFFINITY AND HOTLOAD — what `LichessTvSource` does NOT teach
>
> `LichessTvSource` is the model for the connection LIFECYCLE (backoff, reconnect,
> unhook-before-Dispose) and teaches nothing about threads, because `Sandbox.WebSocket` hands you
> each message on the game thread. **A raw `Stream` read completes on a THREAD-POOL thread.** So
> `LichessStream`'s read loop touches only a lock-guarded queue, and every `Scene` / `[Sync]` /
> `GameObject` touch happens in `Drain()` from `OnUpdate`.
>
> **Hotload is the matching hazard and it fails silently.** An orphaned read task leaves a LIVE
> HTTP CONNECTION to lichess — which on the Board API means both "this player is present" and
> "this token's one event-stream slot is taken". Nothing errors; the next flow just never
> delivers. Every loop carries a GENERATION, checked after each read and bumped on every start,
> so an orphan exits on its next line. `Dispose` on **every** exit path.

#### The traps

- **lila has TWO functions called `isBoardCompatible`, with different thresholds.**
  `Challenge.isBoardCompatible` is `speed >= Blitz` (estimate ≥ 180s) and gates **challenges**;
  `lila.core.game.isBoardCompatible` is `Speed(clock) >= Rapid` (≥ 480s) and gates **seeks**
  (via `SetupForm.boardApiHook`). Same name, different files, different answers. `Speed` comes
  from scalachess's `byTime(limit + 40*increment)`. **[SOURCE]**
  → **Bullet never reaches lichess by any path.** The default table (Blitz 3+0, estimate 180)
  is challengeable but **not** seekable — which is why a direct challenge is the primary flow.
  Unlimited *is* challengeable (no clock → Correspondence speed) but not seekable.
  → `Code/Game/LichessTable.cs` encodes both floors client-side, is Sandbox-free, and is
  harness-checked against every preset in `TimeControl.All`. **It was the model for the whole
  C# port** and it survived HTTPFIX untouched.
- **A seek's `time` is MINUTES; a challenge's `clock.limit` is SECONDS.** An easy way to ask
  for a ten-second game while meaning ten minutes.
- **Omitting both clock fields is how you ask for an unlimited challenge.** Sending `0/0` asks
  for a rejected 0+0 clock instead.
- **`clock.limit` has a domain**: 0, 15, 30, 45, 60, 90, or any multiple of 60 up to 10800.
  Not a smooth range — a 100-second clock is a 400.
- **An omitted `ratingRange` does NOT mean "pair me with anyone" — it means "lichess picks a band
  centred on me", and that is the best matchmaking available to us.** Re-derived from lila +
  the OpenAPI spec 2026-07-16. This one inverts the obvious reading, so the chain matters:
  - The field is **absolute** (`"1500-1800"`), never a delta — `^\d{3,4}-\d{3,4}$`, both ends
    within **400–2900**, `min < max` strictly. An invalid string is a **400**, not a silent
    default (`Mappings.scala` verifies before `orDefault` can fire).
  - Omitted → `RatingRange.default` = **`400-2900`** (`core/rating.scala`) — nominally
    unbounded. **But a real-time hook never uses it.** `Hook.scala:46-54` computes
    `manualRatingRange = ratingRange.ifNotDefault`, and where that is empty falls back to
    `RatingRange.defaultFor(rating)` — a **Gaussian band** (`Gaussian(1500, 350)`, percentile
    `0.2`) around **your real rating**, clamped to 400–2900. **[SOURCE]**
  - So lichess centres on your true rating for free: **no scope, no `/api/account` fetch, no
    rating stored on the link row.** Anything we compute is worse-informed than lila is. This
    is why Gambit sends **no `ratingRange` at all** and has **no rating chip** — the old
    "Near my rating" chip sent a fixed `1400-1800` to every player regardless of strength,
    which was both a lie on its face and *narrower and less accurate* than doing nothing.
  - **A real-time seek therefore cannot mean "anyone".** lila filters out a range equal to your
    rating ±500 as "no preference" — which is exactly what its own UI slider defaults to
    (`setupCtrl.ts:71`, delta→absolute at `:229`). So lichess's ±500 preset is a decoy: lila
    recognises and discards it. Asking for a genuinely open pool would mean sending `400-2899`
    to dodge that equality check. **Don't** — it games an implementation detail to get worse
    pairings.
  - **Correspondence is the exception**: `Seek.scala` uses `ratingRange.ifNotDefault` with **no
    Gaussian fallback**, so a correspondence seek with the default range really is unbounded.
  - The **±500 clamp is web-UI-only**: `HookConfig.withinLimits` is applied by
    `Setup.hook`, and **`Setup.boardApiHook` never calls it** — a Board API seek may set a range
    further than 500 from its own rating. **[SOURCE]**
  - `GET /api/account` is **`security: [OAuth2: []]`** — a token, but **no specific scope**, so
    `board:play` reads it — and ratings live at **`perfs.<speed>.rating`**, with `prov: true`
    marking provisional. Recorded because it is the fact that *looks* like it unblocks a rating
    chip; it doesn't, because the chip shouldn't exist. (It is also the call the link flow
    makes, for identity — see the custody section.)
- **An offer POST always answers `200 {"ok":true}`, whether or not lichess took it.**
  `setDraw`/`setTakeback` return Unit in lila and the controller wraps them in `fuccess`, so
  the documented `400 "The draw offering failed"` never fires. lichess silently drops a draw
  offered before ply 2, a second draw within 20 ply of your last, and a takeback before both
  sides have moved. **The only truth is the standing offer on the NEXT `gameState`** —
  `wdraw`/`bdraw`/`wtakeback`/`btakeback`, which lichess **omits when false** rather than
  sending false. Nothing may report an offer landed from a status code. This is why the
  takeback button is hidden before move 2 rather than shown and dead.
- **A declined DRAW is INVISIBLE on the Board API — and this is where draw and takeback
  DIVERGE.** Re-derived from lila master 2026-07-22. Both offers push a `gameState` on the
  *offer* (`Drawer.yes`/`Takebacker.yes` → `publishDrawOffer`/`publishTakebackOffer` →
  `BoardDrawOffer`/`BoardTakebackOffer` bus → `GameStateStream.pushState`). But on the
  *decline*, only **takeback** publishes: `Takebacker.no` calls `publishTakebackOffer`, so a
  declined takeback clears `wtakeback`/`btakeback` on the next `gameState`. **`Drawer.no` does
  NOT** — it clears `isOfferingDraw` and emits only `Event.DrawOffer(by = none)`, which feeds
  lila's own web round-socket, never a bus channel the Board/Bot stream subscribes to. So a
  declined draw pushes NOTHING to the board stream. **The CLEAR is never its own event
  either** — lila clears the flag only via `Drawer.no`, and a move routes through it too
  (`MovePlayer.scala:77` fires an implicit decline on the opponent's reply). Even that
  declining move's own `gameState` is built one line *before* the clear (`notifyMove`, `:73`),
  so it still carries `wdraw:true`; the flag only reads false on a LATER move's full snapshot.
  Earliest the offerer's own next move (~2 plies out), and on a bare decline **never**. So no
  client may show "waiting for a reply" on a lichess draw — `GameHud` says **"Draw offered.
  Lichess won't signal a decline."** for a lichess game, and keeps the honest "waiting for your
  opponent" only for a takeback (whose decline IS pushed) and for a LOCAL draw.
- **Draw and takeback are ONE endpoint each, not three.** `/draw/{accept}` and
  `/takeback/{accept}`: offering and accepting are the same call, and the path segment is
  parsed by lila's `Form.trueish` (`1|true|True|on|yes`) — so decline is "any non-truthy
  word", not a `no` keyword. Both are on the **Board** API, not just the Bot API.
- **A takeback offer arrives as an ordinary `gameState`.** lila `pushState`s on
  `BoardTakebackOffer` exactly as it does on a move — there is no takeback event type to
  look for.
- **Premove is not a lichess concept.** There is no API surface for it and no server
  involvement: every `premove` hit in lila is `ui/` TypeScript or a *user preference*. A premove
  is just "POST the move the instant it is legal" — so ours is client-only by nature, not by
  choice, and custody never had anything to do with it.
- **Quick pairing and blitz seeks are both locked behind ONE door: the `web:mobile` scope.**
  Re-derived from lila + lila-ws master 2026-07-16, correcting an earlier, blunter claim that
  the API simply forbids them. It doesn't — it gates them on being lichess's own app, which
  amounts to the same thing for us and is a very different reason.
  - **Quick pairing (the homepage pools) is not `POST /api/board/seek`.** They are different
    systems in lila: a seek is a *hook*, quick pairing is a *pool*. `grep -i pool` over
    lila's `conf/routes` returns **nothing** — there is no HTTP endpoint at all. Pools live
    on the **WebSocket lobby** (`poolIn`/`poolOut` in lila-ws's `ClientOut`), and lila-ws's
    bearer auth requires the token's scopes to be **`web:mobile` or `web:polygon`** — a
    `board:play` token cannot authenticate to lila-ws, full stop.
  - **Blitz seeks are not universally refused.** `SetupForm.boardApiHook` takes an
    **`allowFastGames`** flag that skips the Rapid check entirely, and `Setup.boardApiHook`
    passes `ctx.isMobileOauth || ctx.isTakex3 || (ctx.isAnon && isLichessMobile)`. Both
    are scope checks. So blitz IS seekable — **if you hold the scope whose own description
    reads "Official Lichess mobile app"**.
  - **We do not request it, and this is a rule, not an oversight.** See the scopes section:
    the argument is honesty toward lichess, and HTTPFIX did not weaken it.
  → The consequence stands: **a blitz table can never find a stranger**, and quick pairing is
  not a feature we can have. The direct challenge is the primary flow *because* of this.
- **A real-time seek's response carries no game id** — it is a stream of empty lines whose
  only job is to stay open (closing it cancels the seek), which is why the seek flow needs
  the event stream and the paired flow doesn't. A **correspondence** seek is the exception:
  plain JSON, returns `{"id":…}` immediately, no held connection. There is also an
  undocumented `DELETE /api/board/seek` (`Setup.boardApiHookCancel`) that the spec omits.
- **Tokens are long-lived (~1 year) with no refresh tokens.** A scope change forces a full
  re-link for everyone — which is what made HTTPFIX the moment to widen, and what makes the
  next widening expensive again.
- **Closing a challenge keep-alive stream does NOT withdraw the challenge.** lila only stops a
  15s ping; the challenge then goes Offline and stays acceptable for **hours** (a later ping
  revives it outright). Read from lila, not the OpenAPI doc, which says the opposite. Gambit
  doesn't use the keep-alive stream at all, and still POSTs an explicit `/cancel`.
- **Imported games are unrated and attributed to NOBODY** — `[White]`/`[Black]` are display
  strings, never account links. `POST /api/import` makes a game *viewable*, not *counted*. It
  is a strictly weaker outcome than playing live, and the copy must not imply otherwise.

#### The shareable link IS a relayed game — anon browser vs your board (open + accept?color=)

**An anonymous browser player CAN play your authed, board-relayed account. It is a real flow, it
worked before M8, and getting it wrong twice is why this section exists.** The mechanism was
re-derived from the live spec (`lichess-org/api` master, 2026-07-17) after a wrong "it's
impossible" claim sat here:

| endpoint | `security:` | what it does for us |
|---|---|---|
| `POST /api/challenge/open` | **`[]`** (anonymous) | mint the link. A `board:play` token 403s it ("Missing scope: challenge:write"), so we send **no** token. |
| `POST /api/challenge/{id}/accept?color=` | `["challenge:write","bot:play","board:play"]` | **seats our token holder** in the open challenge, on the chosen side. `board:play` is accepted; `color` is "only valid if this is an open challenge". |
| `POST /api/challenge/{username}` | `["challenge:write","bot:play","board:play"]` | the direct challenge — same scope list, which is *why it works on `board:play`* and the open-create 403 looked contradictory. |

So the flow is: **create the open challenge anonymously → accept it with the player's own token
(this is the step M8 dropped, leaving the creator's seat empty so the game never started) →
publish the opposite colour's url → watch the event stream for the opponent joining → stream the
player's side to the board.** The browser opponent needs no lichess account.

- **No `challenge:write` needed.** Create is anonymous; accept takes `board:play`. The old
  pre-M8 code requested `challenge:write` and never needed it; the M8 *bug* was skipping the
  accept step, not the scope.
- **Blitz+ only** — OUR side plays through the Board API, which won't play faster than blitz.
- **It is a solo flow.** `State.seek` is true (the opponent is a stranger); `ShareUrl` is the
  only extra field.
- **Colour** is which side WE take; the opponent's `share_url` is the opposite. "random"/""
  accepts without a colour and we learn our side from `gameFull`, same as a seek. Client-side,
  picking the colour **moves the player's seat** (`LobbyPlayer.SwitchSeat`); random moves once
  the game starts.
- **Cancellation is best-effort.** We created anonymously, so a `/cancel` may be refused; an
  unjoined open challenge expires in 24h.
- **The consent model still holds:** a game at a table is local unless a player picks a lichess
  flow — `InfoScreen`'s Welcome + Lichess branches say it; keep saying it.

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
affordable — the cost is bounded by the channel count (15), not the player count. **Since M18
this is ref-counted by a CONNECTION COUNT** (`tvChannel.conns`): `watch` increments it, and a
`defer h.tv.leave(...)` in the socket handler decrements it on **every** exit path — clean
close, write error, rude TCP drop. The **sweeper is the only thing that closes an upstream**,
dropping a channel a short `tvLingerTTL` (~10s) after its count reaches zero so an A→B→A channel
switch doesn't flap it; correctness rests on the guaranteed decrement, not the linger. *(Pre-M18
it was a last-polled TIMESTAMP, because a long poll's HTTP handler had exit paths a dropped
connection never ran — a counter would have leaked. A WebSocket handler holds the connection for
its whole life, so a `defer` decrement is both possible and simpler, and it is the leak-proof
core rather than a timestamp that merely can't leak.)*

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

**The client-facing transport is a WebSocket push (M18), one socket per channel.** gamchess
still reads the lichess ndjson upstream and stays the sole stream holder — this did not become
clients reading lichess. What changed is the gamchess→client hop: it was a version-gated long
poll over a latest-state slot, with a `since`/`version` cursor, a `hold_ms` field and a whole
clock-latency apparatus bolted on to make a 5s-held poll feel live. It is now `wss://…/api/v1/tv/
{channel}` pushing **one full, self-contained `TvState` snapshot per state change** — no deltas,
no cursor, latest-wins. `LichessTvSource` holds the socket, applies each pushed snapshot, and
reconnects on a drop; switching channels reconnects and the stored latest frame is pushed on
connect. `tvState` (the long-poll handler) is gone; `tvSocket` upgrades.

**lichess only sends a clock when a MOVE happens**, so a TV clock rendered raw sits frozen
through every think and reads as a broken board rather than a thinking player.
`LichessTvSource` runs the side-to-move's clock down locally from the last frame and snaps
both to whatever the next one says. lichess stays the only authority, and local drift cannot
outlive one move.

> **The clock favours reading LOW, and M18 made the mechanism tiny.** The house rule below is
> that a live clock must never read HIGHER than the time actually left; reading low is fine.
> With a push, a moving game's frames are FRESH — a change wakes the writer and it sends at once
> — so the client flooring the displayed second (`TimeControl.Format` already truncates) absorbs
> the sub-second transport latency for free, and the steady-state clock reads low with **no
> correction at all**. The one case the floor can't cover is a client that CONNECTS mid-think:
> gamchess hands the new socket its stored frame, already stale, and without a subtraction the
> clock would read HIGH by that staleness.
>
> So `TvState` carries **one** field, `age_ms` (~0 on a live push, the real staleness on the
> connect/replay path), and the client subtracts it — a duration, not a timestamp, because we do
> not share a wall clock with gamchess and a skewed absolute stamp would correct in either
> direction, *including up*, the one direction the house rule forbids. On top, a fixed
> `ClockLeadSeconds` (~0.25s) deliberate undershoot: the **lichess→gamchess** leg is
> irrecoverable (nothing downstream knows lichess's move-instant T₀), so a small residual HIGH
> bias survives on that leg, and shaving a quarter-second is free insurance in the one permitted
> direction where a fair estimate would be a coin-flip on the forbidden outcome.
>
> **This replaced the M11 apparatus wholesale.** That version carried TWO durations — `clock_age_ms`
> *and* `hold_ms` — because a long poll's round trip is mostly gamchess *waiting*, so the client
> had to subtract `hold_ms` to recover the network leg, plus `LagOf`/`_bankLag`/`MaxLagSeconds`
> and a measured round trip. A push has no hold to measure and no cursor to reconcile, so all of
> that is **deleted**. (The earlier "it can only read low" reasoning was itself wrong and written
> in three places before M11 fixed it; that lesson is in git — the point that survives is that a
> re-derived clock must reason from *when the value was stamped*, never from *when we received it*.)
>
> **The correction applies to the TICKING seat only.** The idle side's clock isn't running, so
> however stale the frame is their bank is still exactly right; subtracting from both would invent
> a loss of time that never happened.

**The house rule: a live clock must never read HIGHER than the time actually left** — the same
rule that makes `TimeControl.Format` truncate where the PGN writer rounds. Reading low is
explicitly permitted; reading high is not. The rule is one-directional on purpose, which is why
a deliberate undershoot satisfies it where an unbiased estimate would not.

> **No sawtooth to guard against, with a push.** The old long poll re-delivered the current state
> at the *same* version every ~5s through a think, so the snap was gated on the version advancing
> — re-snapping on a timed-out poll restarted the countdown from an already-stale value and the
> clock ticked down 5s then jumped back UP, forever (worse than a frozen clock, because it read
> HIGH). A push only ever fires on a real change, so there is no duplicate: the client applies
> each snapshot exactly once and snaps on it. The version cursor that gated it is gone.

**The feed NEVER says a game ended.** Ninety-five seconds of `ultraBullet` is 5 `featured` and
203 `fen` and nothing else: a game ending is just a swap to a new `featured`. There is no
gameOver frame — don't go looking for one.

So the wall's fanfare splits the job in two, and **which half does what is the whole lesson**:

- **The CLIENT decides a game ended.** Two ways, and the fast one is the point:
  - **From the POSITION** — a checkmate or stalemate is IN the FEN, so
    `LichessTv.TryPositionResult` reads it off the frame the mating move lands on and the wall
    freezes and announces **instantly**, deriving who+why locally ("White wins — checkmate"). No
    server, no wait. This is **the one place the TV path reads the rules**, and it is gated to
    `IsStandardRules` (`Group.Speed`) — a Crazyhouse/Atomic/Antichess "mate" isn't one, and the
    vendored rules are standard-only. The spectator BOARD still parses nothing; this is
    `LichessTvSource` calling `ChessGame.TryFromFen`, harness-proven. It exists because without
    it the wall runs the mated side's clock down for **~2s** until the swap below (the feed sends
    no game-over event, and lichess lingers before featuring the next game).
  - **From the featured id changing** — the fallback for everything the position can't show: a
    resign, a flag, a draw agreement, and every variant. Needs nothing from the server.
- **gamchess supplies the REASON for that fallback**: it notices the same swap and fetches the
  old game's result from **`GET /game/export/{id}`** (anonymous, like the feed; `status` +
  `winner`, where a **missing winner means a draw**), publishing it as
  `last_game_id/last_status/last_winner`. The client uses it only if `last_game_id` is the game
  it was actually showing — and only if the position path didn't already announce the end.

The first version had the client WAIT for `last_game_id` to appear before announcing anything
— which silently made the entire feature depend on the server half being deployed. Against a
gamchess without it, nothing ever fired, and **a fanfare that never fires looks identical to
one that isn't wired up**: it cost two rounds of testing and a wrong diagnosis.

The client holds the finished position for `LichessTv.FanfareSeconds` (3s), because lichess TV
cuts to the next game instantly and on a wall that reads as a glitch. **The HOLD (freeze the
position + clocks) is decoupled from the BANNER (who+why).** The hold starts the instant we
know the game ended; the banner shows a real WHO-WON / WHY line and nothing else — **no bare
"Game over" placeholder** (`LichessTv.Result` returns a null headline when it can't say who or
why, and the wall shows no banner rather than a placeholder that then rewrites itself). The
position path sets both at once (it knows the result); the swap path freezes first and fills the
banner a beat later when the export returns.

**The swap and the reason are published SEPARATELY, and getting that wrong is what made the
fanfare arrive late.** The export fetch is **one request per game END per channel**, not per
move, through the same governor as everything else — but it was originally **synchronous inside
the stream reader**, blocking the featured swap until it returned (up to `tvResultTimeout`, 3s,
plus lichess's own latency). Because the client starts the fanfare purely from the game id
changing, that blocked the *whole announcement*: the board sat frozen on the finished position
with **no** fanfare for the length of the fetch, then jumped to the fanfare and the next game.
The synchronous version justified itself as "the ending and its replacement land in one state so
the client can never show the new game first" — but the client already refuses to advance during
its own 3s hold, so ordering was never at risk; the coupling bought nothing and cost the delay.
Now gamchess **publishes the swap immediately** (with `last_game_id` set, `last_status` empty)
and fetches the reason in a **background goroutine**, folding it in with `tvChannel.setLastResult`
when it returns. That method **drops a stale answer** (a fast channel can swap again mid-fetch, so
it only applies when `last_game_id` still names the game it fetched) and on that no-op **does not
close `changed`** — a spurious wake would re-push the full snapshot to every connected client and
make them re-snap their locally-run clocks off an unchanged value. (Pre-M18 the same guard read
"does not bump the version"; the version is gone, but the property — a no-op stays a no-op on the
wire — is identical.) The client, still holding on the finished game, **upgrades** the fanfare
line from "Game over" to "White wins — out of time" when the later push carries the reason
(`LichessTvSource`, guarded on `InFanfare && _gameId == _fanfareShownFor`). This split is the same
"the CLIENT decides a game ended; gamchess only supplies the REASON" separation stated above — the
synchronous fetch was quietly violating it.

**There is no buffer, and nothing to bound.** gamchess keeps only the LATEST state per channel
(one slot, overwritten), so "hold for 3s, then take whatever is current" abandons all but the
latest by construction — no queue, no catch-up, no speed-up logic.

**All 15 channels, variants included** (default `best` — "Top Rated", the best game in
progress whatever the speed; a wall wants something worth looking up at, and blitz is a fine
game but an arbitrary one). This was **six** at first, excluded on the reasoning that the
vendored rules are standard-only so a variant FEN can't be drawn —
**that was wrong, and the mistake is instructive**: the standard-only rule governs *playing*
(`ChessGame` parses the FEN and validates moves) and was carried over to the wall's DRAWING,
which parses nothing. `SpectatorBoard3D` takes the placement field alone and walks its characters
under a `file < 8 && rank >= 0` guard, so Chess960's X-FEN castling (`HDhd`) is never read,
Crazyhouse's pockets (`…/RNBQKBNR[Pp]`) fall off the guard, Three-check's counters ride at
the end of the FEN, and the rest are plain standard placement. Proven against every variant's
real starting FEN in the dotnet harness. **Before excluding something for "the board can't
draw it", check what actually reads the FEN.**

*(The wall's DRAWING parses nothing; its end-DETECTION does, and only for standard channels —
`LichessTvSource` runs `ChessGame.TryFromFen` on standard-rules frames to spot a checkmate/
stalemate instantly. That's the one exception, gated by `IsStandardRules`, and it never touches
`SpectatorBoard3D`. See the fanfare split above.)*

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

#### Being a good API citizen — now in TWO places

The rules live in `server/internal/lichess/etiquette.go` **and**
`client/Code/Api/Lichess/LichessEtiquette.cs`, because both halves talk to lichess now. They are
the same rules and the client's are harness-proven (`scripts/lichess_harness`).

**What changed:** Gambit's whole relay used to be ONE IP, so every player shared one budget and
one player mashing a button could break the feature for everyone. Each client spends its own IP
now, and the only Gambit traffic left on the server's IP is **TV** — one upstream stream per
channel plus one `game/export` per game end, bounded by the channel count rather than the player
count.

**What did NOT change, and must not be "simplified" on the grounds that it's my own budget now:**

- **Identify ourselves on every request, streams included** — and the two halves cannot do it
  the same way, which cost a broken link flow to find out. Server-side a RoundTripper sets a real
  `User-Agent` and no call site can forget. **The client cannot set `User-Agent` AT ALL**: it is
  on `Http.ForbiddenHeaders`, so `Http.CreateRequest` **throws**
  `InvalidOperationException("Not allowed to set header 'User-Agent'")` before the request leaves
  — and even past that, `SboxHttpHandler.HandleRequestAsync` `Remove`s the header and re-adds
  `"facepunch-sbox"` on every send, redirects included (read from the shipped engine 2026-08-06;
  `Referer` and `Origin` are forced/forbidden the same way, and `WebSocket.Connect` applies the
  same list). **There is no bypass and `TryAddWithoutValidation` is not one** — every client
  request reaches lichess as `facepunch-sbox`. So the client sends the same string under
  **`X-Gambit-Client`** (`LichessEtiquette.IdentityHeader`), which is honest and attributable
  even though nothing reads it, and is what lets someone reading lichess's logs join our TV
  traffic to our game traffic. Fixing it properly is an upstream change (let a game APPEND to the
  engine UA) and the forced UA is deliberate, so ask first.
  **The single-seam rule is unchanged and matters more now**: every lichess request is built in
  `LichessClient`, exactly as `GamchessApi.SendAuthed` is the only `Http.RequestAsync` call site
  for our own backend. A `Http.Request*` to lichess.org anywhere else in `client/` is a bug.
- **A 429 anywhere stops everything for a full minute.** Their words. Per-IP, so a 429 on one
  call means that machine is going too fast.
- **Self-limit lobby seeks** to lila's own 5/min/IP (`Limiters.setupPost` **[SOURCE]**). **It
  keeps its number and loses its reason:** a player mashing the button now earns a 429 that arms
  *their own* 60-second stop-everything, so refusing locally with a legible reason is still
  strictly better — and a household, LAN party or NAT still shares an IP. **Delete every string
  saying the budget is shared by the whole playerbase**; they are gone from the code, the README
  and the copy.
- **Never retry into a throttle.** Report the reason; let the player decide.
- **Dispose every stream on every path.** New with client custody and the one that fails
  silently: a leaked game stream means "this player is present" to lichess (so the opponent gets
  no away signal) and a leaked event stream holds this token's one slot. See the thread-affinity
  box above.
- **ONE lichess game per player at a time; further tables play locally.** You can sit at N tables
  and play N games (M17 decoupled seat/camera/relay), but only the FIRST is on lichess. **A
  deliberate gate, not a code limit reached:** lichess does not document permission to play
  concurrent games through the Board API (the event stream is one-per-token, the docs are silent
  on multiple board games, and the one public thread reports the streams *interfere* and was
  closed privately — checked 2026-07). If lichess ever documents an allowance, this is what to
  lift. **It is ADVISORY since HTTPFIX** — it was enforced server-side by `relay.Join`'s
  `hasOtherLivePlay`, and gamchess can no longer know. What enforces a partial version for free
  is lichess's own one-event-stream-per-token rule; what shows it is `SetupPanel`'s
  "playing at Table N, this table plays a local game" (`LobbyPlayer.LichessGameElsewhere`), which
  cannot see a second s&box instance. **Local two-seat games have no such limit.**

**An abandoned game is no longer resigned for you.** `relay.watchAbandonment` tracked each seat's
poll and resigned a silent one with that seat's token within ~30s, because a game only ends on
lichess when someone resigns, flags or draws. Nobody else holds the token now, so a crash
mid-game means the game **flags** — as it does for every other Board API client. Standing up
keeps the game live (M17); only quitting drops the stream. The player-facing copy says so.

The accommodation channel is **Discord `#lichess-api-support`** (`https://discord.gg/MS9MejQqha`)
— **not email**; there is no API branch in their contact form. Bring real traffic numbers and the
specific limit hit; outcome is discretionary. **There is probably nothing to ask for any more** —
see PLAN.

#### Corrections to old repo folklore (verified against `sbox-public`)

1. **`HttpAllowList` gates nothing — the "D8 allowlist" mechanism does not exist.**
   `Http.IsAllowed` checks only scheme, loopback-port rules, IP-literals and DNS-rebinding.
   There is no per-package host allowlist in the engine. The entries in `gambit.sbproj` are
   inert; "add a host to the allowlist" is a non-step, and the old "blocked before connecting →
   allowlist is wrong" diagnostic diagnosed a mechanism that isn't there. (`https://lichess.org/`
   was added there by HTTPFIX anyway, as a declaration of intent — the client acquired a second
   host it means to talk to.)
2. **~~The client cannot read a stream~~ — FIXED, and it is what HTTPFIX is built on.**
   `Http.RequestStreamAsync` used to `using` the response and return its stream, so the body was
   buffered and a stream that never ends never resolved. Our fix (`facepunch/sbox-public`
   `42cee680`) sends with `HttpCompletionOption.ResponseHeadersRead` and returns a
   `ResponseStream` that owns the response. Verified in the shipped source 2026-08-05:
   `RequestStreamAsync( uri, method = "GET", content = null, headers = null, ct = default )` —
   headers included. Its own doc comment carries the two facts the design turns on: **"the
   returned stream owns the response — dispose it or the connection stays open"**, and
   **`HttpClient.Timeout` does not bound the body read**, so a `CancellationToken` is what bounds
   how long you are willing to read. (That is the OPPOSITE of `GamchessApi`'s 8s `CancelAfter`: a
   game stream is bounded by the game's life, never by a clock.)
3. **`Sandbox.WebSocket` streams fine**, supports custom headers and incremental receive, and
   its `Connect` goes through the **same** `Http.IsAllowedAsync` — the URL policy does cover WS,
   and since that policy is just scheme/IP checks, `wss://chess.gamah.net` is allowed. M18 moved
   TV to a WebSocket push on the strength of it. **The game relay's long poll is not "still" a
   poll — it is DELETED**: the client streams the Board API directly.
4. **~~SHA-256 is hand-rolled here~~ — there is no such code, and none is needed.** This claim
   had no implementation behind it for the whole of the repo's life (it was cited in two comments
   in `ChessEngine.cs` as precedent for something that did not exist). PKCE needs SHA-256, one
   was hand-rolled for HTTPFIX and then **deleted**:
   `engine/Sandbox.Access/Rules/BaseAccess.cs:362` whitelists
   `System.Security.Cryptography.SHA256*` outright (read 2026-08-05), so
   `SHA256.HashData` just works. **`RandomNumberGenerator` is genuinely absent** from that list
   (only `HashAlgorithm*`/`MD5*`/`SHA1*`/`SHA256*`/`SHA512*` are there), which is why
   `Pkce.New` uses `Random.Shared` and says so.

Status: gamchess client + server built. **HTTPFIX moved the lichess token to the client**, so
the server's lichess surface is a link flow, a disclosure page and a directory; the C# lichess
client is new and large. The **Go half compiles and its tests pass** (Go 1.22 from
`~/.local/share/toolchains/`; `go test ./... -race`), and the **Sandbox-free half of the C#
lichess client passes its harness** (`scripts/lichess_harness`, `dotnet run`). **HTTPFIX merged
2026-08-06 (PR #31) having been opened in the editor**: it compiles, and a real lichess game was
linked and played end to end through the shareable-link flow — so the streaming premise the whole
branch rests on is proven in the room, not just argued. **The PAIRED flow (two seats at one
table) has still never been run**, along with seek and challenge-by-name; PLAN carries the row.
Nothing is deployed (no Docker here).

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

No *engine* code compiles or runs on this host. **Three things DO run here, and all three
are gates worth using:**
- `node scripts/chess_js_perft.mjs` — the web viewer's chess rules.
- **The Go server.** Go 1.22 lives in `~/.local/share/toolchains/` (fetch
  `go1.22.x.linux-amd64.tar.gz` there once if it is missing); `go build/vet/test ./... -race`
  all pass. `server/` is fully testable; do not claim otherwise.
- **Sandbox-free C#** via a scratch csproj (see below) — which now includes `Code/Game/
  LichessTable.cs`, the client's copy of lichess's speed floors.

**Sandbox-free C# is genuinely testable here**, and worth reaching for: `dotnet` (10.x) lives
at **`~/.local/share/toolchains/dotnet10/`** (put it on PATH for the command; it is NOT on the
default PATH, and it was absent altogether on 2026-07-29 — refetch with `dot.net/v1/dotnet-install.sh
--channel 10.0 --install-dir ~/.local/share/toolchains/dotnet10` if it goes missing again, and
don't unpack it in the working tree). Everything under `Code/Chess/` — plus
`Code/Game/TimeControl.cs` — has no engine dependency. A scratch csproj that `<Compile
Include>`s those files runs real games, real PGN, real perft. Two settings matter:
`<TargetFramework>net10.0` (net8 builds but won't launch — only the 10.x runtime is here)
and `<ImplicitUsings>enable`, because the vendored library leans on s&box's global usings
for `System.Collections.Generic`. Verified 2026-07-15. This is also how the vendored rules
were proven originally, how a `[TimeControl]`-bearing PGN was checked against the real
writer, and how `LichessTable`'s challenge/seek floors were checked against every preset in
`TimeControl.All` — prefer it over review whenever the code can be isolated from Sandbox.

**A SHIM is a legitimate third option, and P99 is the worked example.** `Code/World/SeatAim.cs`
(the cursor-vs-look-aim state machine) touches `Mouse`, `Input` and `Screen`, so it is not
Sandbox-free and can never move under `Code/Chess/` — but the three symbols it uses are tiny, and
~50 lines of stand-in (`MouseVisibility`, an `Input.AnalogLook` that reproduces the engine's own
"zero while a cursor is visible" gate, a `PlayerData` holding one bool) compile the **real file**
verbatim and run its whole truth table: aim engages on Playing, Escape suspends and the suspend
sticks, a modal releases and restores without eating the player's own suspend, the offset clamps
and never rolls, standing up always hands the mouse back. It is committed as
`scripts/seataim_harness/` (`dotnet run`) so the claim stays checkable. Worth doing when the
logic is a state machine and the engine surface is small — and worth NOT doing when the shim would have to
reimplement engine behaviour to be meaningful, because then it only tests the shim.

**This cuts both ways: it is worth MOVING code to make it testable.** `Code/Chess/
CapturedMaterial.cs` (the captured-piece trays' material derivation, M11) lives under
`Code/Chess/` and takes a plain `char[64]` specifically so it can run in a harness — the
promotion arithmetic in it is exactly the kind of thing that reads as obviously correct and
isn't. Driving real games through `ChessGame` in the harness immediately proved a real
capture-promotion line that the naive start-minus-current diff gets wrong in both directions
at once. Had it stayed a private method on `ChessBoardView` (a `Component`), none of it could
have been run here at all.

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
  cells, two capture trays, pieces at the start position, two camera anchors per
  station) and network-spawns the stations. It also owns the screen-rect UI math
  (`ScreenFractionRect()` / `UiRectStyle()`).
- **The tabletop margin is allocated, not slack** (M11), and **the Y margin's budget is
  DERIVED, not typed**. `TopSizeX`/`TopSizeY` (40 × 44 base units) minus the 29-wide board
  frame gives every margin a job: **−Y is the clock strip then White's tray**, **+Y is
  Black's tray** (with the number plaque hanging below its edge), and **±X are kept clear
  — they are the seat cameras' sightlines.**
  → **One thing lives in an X margin now** (issue #28): the seated player's own BOARD SETTINGS
  plate, in their **near-left corner**. The sightline rule is about anything standing MID-EDGE in
  a player's foreground — the clock tower that read as a wall in Black's face. This is 0.3 thick,
  flat on the tabletop, in the corner behind the near rank, and it is client-local so there is
  only ever one. The trays and the clock strip run 26 in X (±13), so nothing else is out there.
  It was 34 square (a 2.5 margin) while a comment promised "a healthy margin for
  clocks/captures later" — it wasn't one, and the plaque was standing in what is now
  Black's tray. **Don't put anything new on the tabletop without checking which margin
  it lands in.**
- **`ClockBoardGap` / `ClockDepth` / `ClockTrayGap` / `TrayEdgeGap` are the whole Y
  budget**; `TrayInnerY`, `TrayCenterY`, `TrayWidth`, `TraySlotPitchY` and `ClockCenterY`
  all derive from them. This is not tidiness. The tray slab used to be
  `TrayCols * cell + 1`, which at these numbers is *exactly* the 7.5 margin — so it ran
  flush from the board frame to the table edge with no gap anywhere, on both sides.
  **Nobody chose that; it is what the expression happened to equal**, the same accident as
  the "healthy margin" that wasn't. Change one constant now and everything else moves.
- **Neither X margin is neutral ground**: −X is exactly where White's seat camera looks
  down the board from, and +X is the same for Black. Anything mid-edge there is in a
  player's foreground. The clock was built at +X with a face per seat and read as a wall in
  Black's face; it is now **beside** the board at −Y with **one** face angled up across it —
  which is where a real chess clock goes, and why one face serves both seats: neither is
  square to it, both are looking down at the table anyway.
- **A `WorldPanel`'s `LocalScale` is not a world size and cannot be eyeballed.** World size
  is `PanelSize × 0.05 × scale` — the 0.05 is the engine's
  `ScenePanelObject.ScreenToWorldScale`, and the default `PanelSize` is 512 square. The
  clock face was guessed at `0.022` and rendered **0.85 world units on a 30-unit body**: an
  invisible speck that read as "the panel is broken". Derive it —
  `wanted_world_size / (PanelSize × 0.05)`. `ChessRing.PxToWorld` and `SpectatorSeatPanel`
  each keep a copy of that constant for this reason.
- **The clock plates' HEIGHT is a geometric constraint, not a style choice.** They are tilted
  up out of a 1.4-deep strip, so a plate's height projects `sin(tilt)` of itself back into Y:
  a tall plate at a steep tilt leans out over the board and clips the a-file. `ClockPlateHeight`
  and `ClockFaceTilt` therefore trade off exactly and **neither is tunable alone** — 2.4 at 30°
  spends ±0.6 of the strip's own ±0.7. Nothing stacks on a clock this thin, and it is thin
  because it shares the margin with a tray. The plate's **pixel** space is not a second knob:
  `ClockPxHeight` is *derived* from the plate's aspect, so the panel and the mesh it sits on
  cannot drift out of proportion.
- **Each player's captured pieces sit in a tray on their own right** (White faces +X, so
  White's right is −Y — s&box is Y-left). `ChessRing.TraySlotLocalPosition` owns the
  geometry; `ChessBoardView` owns the ordering; **`Code/Chess/CapturedMaterial.cs` owns
  what's in it, and derives it from the FEN alone — never from a tally of captures.**
  That is load-bearing: `ChessBoardView` rebuilds from the FEN and has no history, so an
  event-counted tray would be empty for every late joiner and every resync. The capture
  animation is a transient overlay on top; when the diff happens to have the dying piece's
  GameObject the tray adopts it, and when it doesn't the tray just spawns it in place.
  **Tray geometry must never be named `Cell …`** — `ChessBoardView.ResolveCells`
  prefix-scans the Table's children for exactly that.
- `ChessSetBuilder` lathes each piece as a runtime mesh. `BuildPiece(type, color, scale)`
  first tries `Model.Load("models/chess/{type}.vmdl")` and falls back to procedural —
  so dropping in a real piece set later is a one-function swap (**D5**).
- `ChessStation` holds two-seat occupancy: `[Sync(FromHost)] WhiteSteamId` /
  `BlackSteamId` (+ `WhiteName`/`BlackName`), claimed via `[Rpc.Host] RequestEnter(seat)`
  first-wins with loser-side reconciliation (**D1**). Seat cameras orbit the board
  center (`SeatOrbitRadius`/`SeatPitch`/`SeatLookDownAngle`). You take the side you
  walk up to; leaving a live game is a two-stage resign (Escape/Leave twice).
- **Seated bodies are M13's deliverable; the hands that play the moves are M14's — both
  SHIPPED (M14 passed the owner 2026-07-19, merged to master); what's left is knob tuning,
  a PLAN.md room row.** When you sit, your Citizen is planted at its side facing
  the board (`LobbyPlayer` sit pose `sit=1`, `SetSeatedPhysics` un-plant so the tabletop
  can't shove you off your chair, `TrimSeatedAvatar` to keep the seat camera out of your own
  skull, and `StationChair` under each seat). Gated behind **`ChessRing.TerrySeated`** —
  false is a full revert to the pre-M13 "don't draw the local avatar while seated" world,
  and it must stay a kill switch (git commit `0f68c91` is why); the hands add
  `gambit_terry_hands` → `gambit_terry_rise` under it. The seat/chair knobs are **code
  defaults on a runtime-built `ChessRing`** (edit-and-hotload, not scene tweaks); the hand
  knobs live on **TerryTuning in lobby.scene** — scene values RULE there, so a new code
  default on a serialized slider silently does nothing.
  → **Reach**: a seated Citizen's ~20u arm tops out ~rank 2 on a 34u shared board and no
  seated lever moves it further (torso lean and per-bone scale both prototyped in-editor;
  the reach-waived attempt 4 re-proved it — two-bone IK cannot move the shoulder). The
  answer is the **half-rise**: a PELVIS translation override (translations carry the whole
  subtree exactly; rotations do NOT carry children) bounded by the legs — feet planted via
  the engine's own `foot_left`/`foot_right` IK, every IK target pre-compensated by the
  override its chain rides (the animgraph solves IK BEFORE overrides apply), plus a
  closed-loop servo for the residual ~5u native warp (horizontal channel gated on a stable
  ask; vertical channel always on — the ask's Z is locked, so vertical error IS warp).
  Geometry in `Code/Chess/HalfRise.cs`, harness-proven, reach sphere always sliced at the
  target's Z (a hand that can't reach stops SHORT, never floats ABOVE).
  → **The architecture that survived the look pass, each point an owner decision:** hands
  rest on the table unless a move is CONFIRMED (no hover/selection tracking — that wire
  state is deleted); **ONE clock** — the view's hold-then-slide is the only authority and
  the **wrist is a CHILD of the piece** (derived live from the performed piece's GO; the
  old carry/grab/piece-led-placement glue is deleted, not tuned); gestures are **budgeted
  deadline stages** (Reach/Lift/Carry/Drop ≈ 0.85s — arrive per stage or snap; grasp
  height comes from the piece's own bounds top); a capture is the same gesture as a move
  (the victim slides to its tray on its own, in parallel); and **reality always wins** — a
  new diff snaps stale board slides forward, a premove reply does NOT abandon the trigger
  move's gesture (the one ply change that continues), and a same-frame collapse fires BOTH
  hands via `ChessGame.UciFromEnd`. **What remains is TUNING** (timing/positions —
  PLAN.md's row); **`TERRY-HALFRISE.md` is the mechanism doc** (the attempts' history that
  fed it is in git — the milestone shipped, the doc distilled it). The bodies and hands are
  cosmetic — no player-facing copy
  (`CenterInfoPanel`/`InfoScreen`) describes them, so none went stale.
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
- Re-prove the rules before trusting a gate via the dotnet harness or `chess_js_perft.mjs`
  (`ChessGame.Perft` is still there; only the in-sandbox `gambit_perft` console command was
  dropped for ship).

### The built-in engine (M19: play the computer)

`Code/Chess/ChessEngine.cs` is a `partial ChessGame` — negamax + alpha-beta over the SAME
vendored move generation, so it is **the one place allowed to touch `_board` and `UciOf`**.
Being under `Code/Chess/` with no Sandbox dependency, it is provable on this host, and it
should stay that way: a full game of it runs in a scratch csproj in seconds.

> **The trap that cost a crash: the vendored board REFUSES to move in a drawn position, and
> a draw rule can fire while legal moves remain.** `ChessBoard.Move` throws
> `ChessGameEndedException` whenever `IsEndGame`, and `EndGameProvider.ResolveDrawRules` sets
> that for **repetition, fifty-move and insufficient material** — none of which empty the move
> list. So `moves.Length == 0` does NOT mean "no more moves are playable", and any code that
> plays moves on `_board` in a loop must check `IsEndGame` too. The search hit it in
> quiescence first (resolving captures reaches repetitions and bare kings fastest) and threw
> at move 36 of an ordinary middlegame. Both recursion points now score such a node as **0**,
> which is what a draw is worth — a search improvement, not a guard. **It survived review and
> shipped in the merged branch** because the harness only ever ran short tactical positions,
> never a game long enough to repeat: if you touch the search, play FULL games, not puzzles.

- **The bot is a host-driven virtual seat** (SteamId 0, difficulty `[Sync]`ed on
  `ChessStation`) — which is why the seat/ready/abandon logic counts a bot as filled and
  ready, and why a bot clears when the human stands (a bot never sits alone).
- **The search runs off the main thread** (`GameTask.RunInThreadAsync`) over a **THROWAWAY
  position rebuilt from the FEN**, never the live `Game`, so it cannot race the main thread's
  reads. Bounded two ways — fixed depth per level and a hard node cap — so it can't hang a
  frame; Hard's worst measured ~700ms in a busy midgame.
- **That think time is charged to the bot's own clock**, so a Bullet-vs-Hard game may
  occasionally flag the bot. Fair and beatable-on-time, by design.
- **A bot game is never a lichess game** and runs at any speed, Bullet included — it clears
  none of lichess's speed floors because it never reaches lichess. The difficulty ladder lives
  in `ChessEngine.Config`, the think-pose in `LocalGameController.BotPoseSeconds`.

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
- `ChessGame.ClkField` **rounds**; `TimeControl.Format` (every live clock) **truncates**.
  Not an inconsistency: a live clock must never read higher than the time actually left,
  whereas the archive should match the reference writers.

**Where a live clock is rendered (M11): on the TABLE, not the HUD.** A low strip in each
table's **−Y** margin (never +X — that is Black's seat camera's sightline, and a clock there
read as a wall in their face), carrying **two mesh plates and a mesh material bar** that all
share one upward facing across the board: `ChessRing.BuildStationClock` + `World/TableClock.cs`
+ `UI/TableClockTextPanel.razor`. One facing serves both seats because neither player is square
to it and both are looking down at the table — two dials on one body, as a real chess clock is.
It was text in a 250px column pinned to the right of the screen while the board sat in the
middle of it — in a 3+0 game, the wrong place for the number that ends the game. Two things
moved with it and must move back if it ever does: **`TimeControl.PanicSeconds`** (where a clock reddens — shared with the panic beep so the
two can't disagree, which is why it lives on `TimeControl` rather than on a panel), and **the
string-hashing** — clock faces are hashed as their RENDERED TEXT, so a panel repaints when a
digit changes rather than every frame. Hash the raw float and every live table in the ring
repaints continuously. The HUD now has no clock on it and no panic red: reddening a *name* next
to no number is an alarm about something that isn't on the screen.

Clocks are stamped by the **host** (`NetClockStamp`), never read from a client's own
synced copy — that copy lags the increment. The `chess_js_perft.mjs` gate holds the JS
parser to real C# writer output, including a sub-second bullet fixture; both fixtures were
captured from the dotnet harness, so regenerate them there rather than hand-editing.

### Game controllers (per-station, added by ChessRing beside `ChessStation`)
`Game/IBoardGame.cs` is the render/drive abstraction; `ChessBoardView` renders the
active source through **one** shared resolver, `BoardGame.Source( local, lichess, relay )`.
The seam paid for itself twice: M8 added a whole second kind of game with no renderer
change at all, and M19 added a third by growing that resolver one argument.

> **`relay` is an OPTIONAL argument, and that is a live hazard.** A two-argument call still
> compiles and silently answers `LocalGameController` during a relay game — a shell, exactly
> as it is during a lichess game — so a feature reading it is wrong by construction with
> nothing looking wrong in the diff. That is how P99's look-aim gate shipped dead for relay
> games (fixed in `3d02a8b`). **Pass all three, always.** Still on two at the time of
> writing, each a known cosmetic gap rather than a decision: `SeatedTerry` (the hands don't
> animate in a relay game), `LobbyPlayer.PremoveAt`/`GamesInPlay` (the roaming premove
> reminder misses one), and `GamchessCommands` (dev console). Anything that reads the position should go through it — `GameHud` and
`Audio/TableSounds` do too, and all three resolve `Source` with that identical expression on
purpose: **what you see, what the HUD says and what you hear must be the same game.**

**But the seam only protects what is actually ON it.** Sound wasn't — it hung off
`LocalGameController`, and so a real lichess game at a table was completely silent from M8 to
M11 with nothing looking wrong in any diff (see Sounds). `GameOver`, `LocalSeatClock` and
`PremoveDropped` are on the seam for that reason: each is something a reactive feature would
otherwise read off `LocalGameController`, where during a lichess game it is **wrong by
construction** — the host freezes that controller's clocks and its `ChessGame` never advances.

| Controller | Networked? | What it does |
|---|---|---|
| `LocalGameController` | host-folded `[Sync] BoardFen`/`Phase`/`ClientGameId` | the two-seat game at a table, and the archive upload (**D7**) |
| `LichessGameController` | **participants STREAM lichess directly; spectators MIRROR (M14)** — each participant holds its own `/api/board/game/stream/{id}` and `[Rpc.Host]`-reports its observed move list into `[Sync] MirrorMoves/MirrorLive`, from which every non-engaged client rebuilds a display game (`Mirroring`, same IBoardGame seam). Before this a lichess game was INVISIBLE to every non-participant — solo flows especially. **The mirror was UNTOUCHED by HTTPFIX**, because it was always fed by the participant's own observations rather than by gamchess; the path just got more direct | a real lichess game on this table (**M8**, rebuilt by HTTPFIX). Adjudicates nothing — lichess is the only authority, and the position is rebuilt from the UCI list it sends — but it DOES run the ticking seat's clock down locally between moves (**M12**), because lichess only sends a clock on a move and a frozen clock reads as a stopped game. **The staleness apparatus is GONE with the poll** (`_version`, `_bankLag`, `_lastRoundTrip`, `clock_age_ms`/`hold_ms`): a stream has no hold to measure and no cursor to reconcile, exactly as M18 found for TV, and keeping it would reintroduce the M11 sawtooth. What is left is a fixed `ClockLeadSeconds` undershoot; a local clock hitting 0 clamps and waits for lichess to call the flag |
| `RelayGameController` | polls gamchess's relay for a live cross-lobby game | a game against someone in ANOTHER lobby, paired through gamchess's directory (**M19**) — or yourself across two hosts. Not lichess: gamchess is the authority and the whole exchange is ours. Colour is **assigned at random by the server**, never chosen |
| `SpectatorController` | reads the host-folded FEN; **polls gamchess for TV** | north wall: cycles live tables, then lichess TV (**M9**) |

**While a lichess game runs, the local controller is a shell** holding the seats and the
`ClientGameId`. Its `ChessGame` never advances (moves go to lichess, not `NetChessMove`), so
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

### Where a setting lives: the ROOM on the wall, the BOARD in your hand (issue #28)

There are two settings panels, and which one a row belongs on is a question about **audience**,
not about space:

- **WORLD SETTINGS**, the south-wall board (`SettingsStation` → `SettingsScreen`, summarised by
  `WallSettingsPanel`) — the ROOM: theme, room and table light brightness, the checkerboard floor
  and its pop rate, both voice-range sliders.
- **BOARD SETTINGS** (`UI/Screens/BoardSettingsScreen.razor`) — a **chess board**: BOARD SOUNDS,
  PLAY MODE, MOVE MODE, SHOW LEGAL MOVES, SPEAK MOVES AT MY BOARD, the TTS voice pill and
  its volume.

**Both render `SettingsModel` rows** — `BuildLocalRows()` and `BuildBoardRows()`, one model, two
lists. A third consumer is a third `Build*Rows`, never a second copy of the row types, and
**anything that mutates must go through `Mutate`**: it bumps `SettingsVersion`, which is the
repaint key for both panels *and* the trigger for `ChessRing.ApplyPlayModeSetting` — a setter that
skips it changes a stored value and nothing in the world.

- **Why it split.** The wall panel had outgrown the screen and clipped at **both** ends, so the
  top row was as unreachable as the bottom. Size was the symptom. Scroll fights the sliders' drag
  (the repo rule) and `FitToHeight` only shrinks a list that was two unrelated jobs deep. Already
  tried and rejected on #26/#27, so don't reach for them again: folding the two sound toggles into
  one `MultiToggleRow` (landed, didn't fix it) and a local ~0.8× type/spacing rule (**rolled back**
  — a font size local to one board makes that board quietly different from every other one).
- **Two doors, one editor.** Seated: a **plate on the tabletop**, near-left corner of your own
  seat (`World/SeatSettingsPlate.cs`). At the wall: a BOARD SETTINGS row on the world panel, so
  someone who isn't sitting down can still reach these. Both call `BoardSettingsScreen.Open()`.
  **Nothing in the panel may read `ChessStation.Active`** — every row is client-local and the wall
  door has no seat.
- **The tabletop plate is the repo's FIRST world-space control, and it is the `WorldInput` path.**
  It was a screen-space pill first; the owner wanted it on the board. P99 built two world-space
  buttons and deleted both — but read *why*: they were an extra thing to find, aim at and click
  **in the mode whose whole point is that you are not pointing at anything**, and they needed a
  plate per seat plus a yaw-180 flip to keep the words unmirrored. Neither objection survives here:
  this opens a settings panel you use *with* a cursor, and the plate is **client-local, so there is
  exactly ONE of it** — moved to whichever seat the local player is in. Clicking works because
  `LobbyPlayer` hangs `Sandbox.WorldInput` on the camera, which is what CLAUDE.md already named as
  the thing to reach for; **do not rebuild the hand-rolled tilted-plane hit test.** Neither can be
  proven on this host — only one of them is the engine's own.
  → **It is unparented on purpose.** A `ChessStation` is NetworkSpawned, so a child of one rides
  the host's snapshot, transform and enabled state included (issue #12's lesson). The plate is a
  `NotSaved | NotNetworked` GO at the scene root, placed in world space off the station's transform
  every frame — which also keeps it with the table when the ring SLIDES on a board-count change.
  → **It fits in an X margin, which the budget calls a sightline** — and that is checked, not
  ignored: the warning is about anything standing MID-EDGE in a player's foreground (the clock
  tower that read as a wall in Black's face). This is 0.3 thick, flat on the table, pushed into the
  near-left corner behind the near rank. The trays and the clock strip are 26 long in X (±13), so
  the corner is genuinely empty.
  → **One string, one plate, one font size.** Both states are 14 characters ("BOARD SETTINGS" /
  "ESC FOR CURSOR") so the inert state is **colour only** — the same rule `TableClockTextPanel`
  keeps, and the reason a second string would mean a second plate.
  → **"The world-panel text is too big" is fixed in PIXEL space, never in the span.** This cost a
  round in the room. The world size of a plate and of its text span are fixed by the span
  constants, so shrinking `SettingsTextSpanLength` scales the panel AND the glyphs together and
  the text hangs off the plate by exactly the same proportion, only further away. The knob is
  `SettingsCharAdvanceEm` / `SettingsTextFitFraction`: a WIDER pixel space means SMALLER glyphs on
  the same plate. (Turn the span height with it, or a shorter string leaves a thin line of words
  centred on a fat slab — the span's aspect IS the panel's pixel aspect.)
  → **Its advance estimate is its OWN, not the clock's.** Reusing `ClockCharAdvanceEm` looked
  right — one measured number beats two — and overflowed the plate at both ends: the clock
  measured DIGITS, this draws bold CAPS in a proportional fallback face, and the formula has **no
  term at all for `letter-spacing`**, which at 14 characters is real width that was simply not
  being counted. Round the advance UP; under-stating it is the failure that shows.
  → **In LOOK aim it stops offering a click and says `ESC FOR CURSOR`.** The pointer is hidden;
  a live-looking control there is the "reads as broken" failure the aim hint exists for.
- **It is MODAL for `SeatAim`, exactly like the promotion picker**, and the wiring is
  deliberate: `LobbyPlayer.UpdateSeatAim` folds `IsOpen` into the modal flag rather than the panel
  touching `SeatAim` — that is what makes it release the cursor and take aim back **by itself** on
  close, without clearing a suspend the player asked for with Escape. Escape is routed at it in
  `LobbyPlayer` (the one place that reads `EscapePressed` while engaged), ahead of both the aim
  toggle and the stand-up.
- **The wall's own panel HIDES itself while the board panel is open** rather than stacking under
  it. Not a z-order bug and not translucency (`WallTheme.Bg` is opaque, and the board panel's root
  carries `z-index: 60` against the wall panel's none): both cards are 620px and centred, and the
  wall's is much taller, so its top and bottom rows poke out past the board card and the pair reads
  as one garbled panel. The station stays engaged underneath, so closing the board panel brings the
  wall's straight back — which is what "opened it from here" should mean.
- **The bottom-left column is unchanged** — `HudHints` at 44, `VoicePanel`'s roster at 100. The
  first version of the seated door was a pill in that corner and pushed both up; it is on the
  tabletop now, so those numbers are back to what they were. Don't reintroduce a third tenant there.
- **The wall board summarises only what it edits.** PLAY MODE / BOARD SOUNDS / SPEAK MOVES status
  lines went with their rows; a status line for a setting that lives elsewhere is a line nobody
  re-reads when that setting changes shape.

### Cursor vs LOOK aim at the board (P99)

A BOARD SETTINGS picker (**MOVE MODE — CURSOR / LOOK**) chooses how a seated player
picks a square. CURSOR is everything before P99 and stays the default. LOOK hides the pointer,
turns the seated view with the mouse, and picks whatever is under the **centre of the screen**.
`World/SeatAim.cs` is the whole state machine and the only thing that decides; two places act on
it (`ChessBoardView` for the ray, `LobbyPlayer` for the camera offset and Escape) and `GameHud`
only DRAWS it (the crosshair, and which way Escape goes next).
`PlayerData.LookAimAtBoard` says whether the player wants it at all.

- **The cursor is still the default state, even with LOOK on.** Aim engages only while a game
  is **Playing** (off the `IBoardGame` seam, so a lichess game aims like a local one — reading
  `LocalGameController` here would be the M8-silence mistake again). An empty seat, the setup
  panel and a finished game all need a pointer, so they keep one. **This is the feature's
  shape, not a caveat**: the ask was "the cursor is active until a game is playing".
- **The crosshair is part of the mechanism, not decoration.** With the pointer hidden the pick
  point is invisible, so `GameHud` draws a dot-in-a-ring at dead centre (`.crosshair`, gated on
  `SeatAim.Aiming` alone and deliberately OUTSIDE the HUD's own `Visible()` block and its
  corner panel — it marks a point in the world, not a line of HUD). 50%/50% because that is
  literally what `SeatAim.PickPixel` returns; centred with negative margins rather than a
  `transform`. A ring around a dot so it survives both a white square and a dark one.
- **Three ways back to the cursor, and they differ.** The player's own (Escape) **sticks**; a
  **modal** (the promotion picker, an offer standing against you) releases and **restores by
  itself** without undoing a suspend the player asked for; and the game **ending** clears
  everything, so the next game starts in aim rather than remembering a key pressed twenty
  minutes ago.
- **ESCAPE IS THE WHOLE CONTROL, and it CYCLES: cursor off, on, off.** While `SeatAim.Toggleable`
  (the setting on, a game live, no modal), Escape switches the pointer and **does not stand you
  up** — the HUD's Leave button does, and one Escape puts a cursor on the screen to click it
  with. That trade is deliberate: Escape is the only key that works with the pointer hidden, so
  it belongs to the mode it can be used in, and leaving is something you do with a cursor anyway.
  Everywhere else — roaming, an idle seat, a finished game, the setting off, a picker open —
  Escape is the plain stand-up it has always been. **`GameHud` says both halves out loud**
  ("Esc for the cursor" / "Esc to aim again, Leave to stand up"), because with one key doing two
  things and no pointer to explore with, an unsaid rule is an unfindable one.
  → **There is no world-space button, and two were built and thrown away.** First two flat plates
  in each seat's near-left tabletop corner (a corner is a different world axis per seat, so it
  needed one each plus a yaw-180 flip to keep the words unmirrored); then ONE plate floating over
  the clock in the clock's own facing, which fixed the handedness and the duplication and was
  still wrong — **a control you have to find, aim at and click is a poor answer in the mode whose
  whole point is that you are not pointing at anything.** It cost `AimToggleButton`, a tilted-plane
  hit test (`SeatAim.PlateHit`) and its harness coverage, all deleted. If a world-space control
  is ever wanted again, the thing to reach for is `WorldInput` (see the UI Gotchas section), not
  a rebuild of that plane arithmetic — and the reason to want one has to be better than "there
  should be a button".
  → **Issue #28 then wanted one, and took that advice**: the BOARD SETTINGS plate is in the
  near-left tabletop corner these attempts used, clicked through `WorldInput`. It survives both
  objections rather than ignoring them — it is used *with* a cursor (it opens a settings panel,
  and in LOOK aim it stops offering a click), and being client-local there is only ever ONE of it
  to place. **The aim toggle is still Escape and still has no button.**
- **The mechanism is one engine switch, and that is why it can't get out of step.**
  `Mouse.Visibility = Hidden` locks the pointer AND is exactly what makes `Input.AnalogLook`
  report movement (the engine zeroes AnalogLook whenever a cursor is visible). **Never set
  `Visible` — set `Auto`**: Auto already shows a cursor while clickable UI is up, and this is a
  global, so a forgotten reset would leave a roaming player with no pointer and no mouselook.
  `Disengage` clears it first thing and the roaming path re-asserts it, because not every way
  to stop being seated goes through `Disengage`.
- **The camera offset composes in EULER space**, not as a quaternion post-multiply: a seat
  anchor is already pitched steeply down (the 2D nadir one looks straight down), so turning
  about its own tilted up-axis would roll the horizon. The offset is clamped (±45° yaw, ±30°
  pitch) — the board is the point of the view — and **persists** when the cursor comes back by
  ESCAPE, so releasing it hands you a pointer rather than snapping the view.
- **…but it is CLEARED when look aim stops being AVAILABLE, and that distinction is a bug fix.**
  The offset used to survive everything short of standing up. That is right for Escape — a
  suspend leaves aim one keypress away, so the offset is still yours to move — and wrong for the
  two ways aim actually ends: **the player switches MOVE MODE to CURSOR**, and **the game
  finishes**. Both leave the view turned as much as 45° off the board with *nothing left that can
  turn it back*: Escape is a plain stand-up again and the mouse only moves a pointer, so part of
  the board is off screen until you stand up. `SeatAim` watches the falling edge of
  `Enabled && playing` (deliberately NOT `Toggleable`, which a modal also clears — a modal hands
  aim back by itself), zeroes the offset and raises a one-shot `Recentred`;
  `LobbyPlayer.UpdateLockedCamera` consumes it through the same re-blend as an anchor swap so the
  view EASES back rather than cutting. **`TakeRecentred` must be called even when the anchor also
  swapped** (2D + LOOK → 2D + CURSOR does both at once) or the one-shot survives to fire on an
  unrelated later frame.
- **LOOK aim keeps the SEAT camera, even in 2D — and `LocalNadir` means the CAMERA now, not the
  render mode** (issue #28). The composition above is degenerate at the 2D nadir anchor and there
  is no tuning that fixes it: yaw is applied about world up, and at a straight-down camera world
  up IS the view direction, so mouse-left/right ROLLS the board image; pitch is applied in camera
  space, and the nadir anchor's local axes come from a per-seat `farDir` that flips sign between
  White and Black, so mouse-forward tips the view the opposite way for the two colours. The fix
  is to never be there: `ChessStation.LocalNadir` is now `Active && FlatMode && !SeatAim.Enabled`,
  and `LobbyPlayer.UpdateLockedCamera` picks its anchor off **that same property**, so the two
  cannot disagree. **2D + LOOK is flat pieces, seated view** — a real thing, because `FlatMode` is
  a *render* gate independent of which anchor is live. The **flat glyphs stay flat** under it (no
  billboarding): they are the same lie-in-the-plane sprites the north-wall spectator board is read
  from the floor at a steeper angle, so there is evidence the foreshortening is a non-issue, and a
  "which camera is live" rule in the render path would be a real cost for a speculative one.
  → Gate on `SeatAim.Enabled` (the SETTING), never on `SeatAim.Aiming`: aiming goes false on every
  modal, every Escape and every finished game, and an anchor that followed it would swing the
  camera between two entirely different views mid-game.
  → `LocalNadir` is also what `NameTagPanel` and `StationScreenPanel` hide themselves on. That is
  why the redefinition is load-bearing rather than cosmetic: on the old "FlatMode = top-down"
  inference they would have stayed hidden for a 2D+LOOK player who can now see the world they
  belong in.
- **A mid-seat anchor change blends, and it did NOT before.** `UpdateLockedCamera`'s comment
  claimed the existing lerp eased between anchors "for free"; it could not — `_engageTime` is long
  past `CamBlendTime` by then, so `t` is pinned at 1 and the write is a hard cut. It never showed
  because PLAY MODE could only be changed at the wall, and you sit down *after*, which runs the
  engage blend. Now that both PLAY MODE and the aim setting are changeable **from the seat**,
  `_lastSeatAnchor` spots the swap and re-blends **from the camera's live transform** — which is
  also what keeps a non-zero `SeatAim.LookOffset` from snapping, since the offset is already baked
  into where the camera is. `BeginEngage` and the seat-switch schwoop clear it so those adopt the
  new anchor silently rather than re-blending a blend they are already running.
- **P99 added nothing to the world in the end** — no tabletop geometry, no margin spent, and the
  Y-margin budget note is untouched. The whole feature is one state machine, one key, one
  crosshair and a HUD line.
- Proven on this host: `SeatAim` runs its whole truth table in a shim harness (see the
  Sandbox-free C# section), **including which way Escape goes** — `Toggleable` is asserted true
  for a live game with the setting on and false for a modal, a dead game, the setting off and
  standing up, because getting it wrong in either direction either traps the player in their
  seat or takes the toggle away. What it can't prove is the FEEL: whether the crosshair reads
  over both square colours, and whether losing Escape-to-stand mid-game is annoying in practice.
  Both are **room** checks.

### Dev console commands
`gambit_gamchess_ping` — is gamchess up?
`gambit_gamchess_signin` — mint an FP token and prove the auth round-trip.
`gambit_gamchess_games` — list your archived games.
`gambit_lichess` — am I linked? Prints **this PC first** (the token is local now, so the answer
needs no network at all), then gamchess's opinion, and names the case where they DISAGREE — a
token here that gamchess doesn't know about breaks only the two-seat directory lookup, which
would otherwise be discovered at the moment two people sit down to play.
`gambit_lichess_unlink` — revoke at lichess with the token, delete it from this PC, then tell
gamchess to forget the row. In that order: the revoke must be signed by the token, so a token
already deleted can never be revoked by anyone but the player.
`gambit_tv` — why is the TV wall doing that? Prints the whole chain: the local setting,
the channel, what the wall thinks it's showing, and gamchess's raw state. Exists because
"nothing is showing" was twice diagnosed by guesswork and once wrongly — none of the chain
is visible from outside, and a feature that never fires looks exactly like one that isn't
wired up.
The 35 `gambit_terry_*` commands are the M14 hand-tuning harness — **dev tools, not
player-facing** (session-local knobs on `SeatedHandSpikes`; the shipped values live on
`TerryTuning` in `lobby.scene`). Full reference table in **`TERRY-HALFRISE.md`**; gate or drop
them before a public ship.

**Dropped for public ship** (recover from git history if needed): `gambit_perft` —
the in-sandbox perft gate is gone; re-prove the rules via the dotnet harness or
`chess_js_perft.mjs` instead (see "Three things DO run here"). `gambit_music` — the issue-#12
music-topology dump; recover from git if that leak resurfaces.

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

Provenance goes in `Assets/ATTRIBUTION.md`, CC0 included. This repo has **one documented
exception** to the CC0 rule (below).

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

**Never deployed** (this host has no Docker). The Go DOES compile and test here with the
shared toolchain — `make test` runs `go test ./... -race` in a container on a
machine that has Docker, and the same suite passes locally with Go 1.22. If you
are changing the server, run the tests; "can't build it here" is no longer true for Go.

**gamchess holds NO lichess secrets.** lichess issues nothing — no client id, no secret, no API
key, no client registration at all — and since HTTPFIX there is nothing of our own to hold
either: the token lives on the player's machine, so there is no ciphertext to key, no data key to
rotate and no store to audit. `LICHESS_TOKEN_KEY`, `LICHESS_TOKEN_KEY_OLD`,
`LICHESS_KEY_ROTATION_DAYS` and `LICHESS_AUDIT_KEY` are **retired and ignored**; `make keys` is a
deliberate no-op (kept because `up`/`rebuild`/`testinst` depend on it) and `keys-note` now says
there is nothing to back up. `internal/keyring`, `lichess_key_versions` and the whole KEK→DEK→
token envelope are deleted, along with `test.sh`, which existed only to validate key rotation.

**The only lichess-relevant config left is `PUBLIC_BASE_URL`**, and it is load-bearing: it
derives the byte-for-byte `redirect_uri`, and it is what keeps the test instance pointing at
itself rather than at prod. `SESSION_SECRET` is ours, optional, and unaffected.

> **If you are rolling BACK to a pre-HTTPFIX binary you need `LICHESS_TOKEN_KEY` again** — keep a
> copy somewhere until you're sure. But note the migration DELETED every link row, so a rollback
> gets you a working old binary and no links; players re-link either way.
>
> **The deploy has one manual step, and it must happen BEFORE it.** Every linked player should
> unlink in-game (which still revokes, while the old binary can still sign it) or revoke Gambit
> on lichess's `/account/security`. Deleting the rows does not revoke anything, and once they are
> gone the grants stay live for up to a year, revokable only by each player. No sweep tool was
> built: lichess has no bulk revoke — `DELETE /api/token` is signed BY the token it kills — so a
> sweep is N serial calls, and N was 1.

Test and prod share `.env` and now have no lichess key to share. What differs, and always
mattered more, is the redirect ORIGIN — lichess records `clientOrigin` per token, so `testchess` and
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
  the trailing headers dictionary is undocumented in `../sbox-docs` but works, **except that a
  forbidden header name THROWS rather than being dropped** (`User-Agent`, `Referer`, `Origin`,
  `Host`, `Sec-*`, `Proxy-*`, …). See the etiquette section: it broke the lichess link flow
- **Hotload**: C# changes hotload in milliseconds. Procedural builders rebuild via
  `[EditorEvent.Hotload]` in `Editor/HotloadRebuild.cs` — keep new builders registered there
- **Self-attaching UI**: **GameHud, SpectatorScreen, the M12 voice pair (VoiceScreen +
  VoicePanel), `HudHints` and `BoardSettingsScreen`** — those, and no others — attach themselves to the scene ScreenPanel at runtime
  (`LobbyPlayer` walks `Scene.GetAllComponents<ScreenPanel>()` in `EnsureGameHud` /
  `EnsureSpectatorScreen` / `EnsureVoiceScreen` / `EnsureBoardSettings`), so a new screen of that kind needs no scene
  rewire; copy that pattern. The voice pair MUST self-attach for a specific reason, not just
  tidiness: it is strictly client-local (mute/enabled live in `VoicePrefs` cookies), so hanging it
  off the ScreenPanel keeps it off every networked snapshot — the HUD-parenting trap. **InfoScreen,
  SettingsScreen, ChatPanel and LobbyOverlay are NOT self-attaching** — they are serialized
  components in `lobby.scene` and adding one means editing the scene. This line cited `SplashScreen`
  as an exemplar for a long time; **there is no `SplashScreen`** — no `.cs`, no `.razor`, only an
  orphan scene entry (see the scene-orphan rule below). It was pointing at a file that does not
  exist, in the file every session is told to trust.
- **A joining client does NOT load the scene from disk — it rebuilds it from the host's
  snapshot, and that snapshot's `NetworkMode` filter is a real fork in behaviour.** Verified in
  the engine (issue #12): `SceneNetworkSystem.OnLoadSceneMsg` **destroys** the client's scene and
  applies the host's snapshot; `GameObject.Serialize.ShouldSave` **drops every `NetworkMode.Never`
  object** from that snapshot and **rebuilds every `Snapshot` object from the host's LIVE state**.
  So for anything authored in `lobby.scene`, neither mode is client-local: `Snapshot` leaks the
  host's runtime state onto joiners (this is exactly how the music board came to render *open and
  unstyled* — the panel's live `Enabled`/`IsOpen` rode the wire), and `Never` means the object
  **never reaches the joiner at all** (setting the scene GO to `Never` made the board vanish on
  clients — the seductive-looking "minimal fix" that cannot work). The **only** way to get a
  strictly-client-local screen/audio object is to BUILD it in code: either self-attach to the
  scene ScreenPanel (the pattern above), or — when it needs its own isolated ScreenPanel — spawn
  it from a **`GameObjectSystem`** onto a runtime `NetworkMode.Never` GO. `LocalMusicSystem` does
  the latter for the Skafinity trio (player + board + `MusicBoardScreen`), mirroring terryball's
  `LocalHudSystem`; a `GameObjectSystem` is instantiated locally on every machine independent of
  the snapshot, which is the whole point.

## s&box API Whitelist

The whitelist itself is in `~/.claude/sbox.md`. Gambit-specific consequence: it is why the
vendored chess library needed patching and why SHA-256 is hand-rolled here.

## World Scale Rules (read before placing/sizing anything)

The generic ones — never trust code defaults over the scene, `box.vmdl` is not 1x1x1,
no `BoxCollider` on a non-uniformly scaled GO, WorldPanel scale is a multiplier on
intrinsic pixels — are in `~/.claude/sbox.md`. Gambit-specific:

- **The scene lies too, and this repo has the scar.** `lobby.scene` carried **eight
  components from the rotaliate fork with no class anywhere in `client/Code/`** — every
  property on them inert, and two actively contradicting the code that really runs:
  `ArcadeRing`'s `BoardSize: 28` next to the real `ChessRing`'s **26**, and
  `SpectatorBoard`'s `ClearAboveWall: 20` next to the real `SpectatorWall`'s **18**. So
  grepping the scene and believing it got the wrong number — the exact inverse of the
  usual rule. They are deleted now; the habit is the point:
  `grep -r "class Foo" client/Code/` before trusting a scene value.
- **A runtime-built component runs on code defaults and cannot be retuned in-editor.**
  `SpectatorWall` is not in the scene at all (`LobbyRoom.EnsureSpectatorWall()` builds
  it), so every one of its values is a code default. A design pass on the north wall is
  an edit-and-hotload loop, not a scene-tweak loop — unlike east and south.
- The player is ~72 units tall — the human-scale yardstick. `ChessRing.AddBox` is the
  local `box.vmdl` helper.
- **A tilted object's EDGE is not half its size from its centre — derive the edge through
  the rotation, never place it by the number that would be right if it were flat.** This
  has now cost two rounds on two different objects. The table plaque dropped its centre by
  `h·cos(tilt)` and forgot the `h·sin(tilt)` the same tilt swings sideways, so its top edge
  was at the right height but tucked under the tabletop. The clock then centred its plates
  on the body's top *surface* — so a box centred on its origin buried half of every plate
  in the body, and buried the shorter material bar **entirely**, where it could never have
  rendered at all. Both times the arithmetic looked obviously right on the page and the
  room disagreed. `ChessRing.ClockPlaneOriginZ` is the worked example: surface +
  `h/2·cos(tilt)`, derived once and shared by everything in the plane, which is also what
  keeps their bottom edges level for free. **Nothing on this host can render, so a
  placement bug ships unless the edge is computed — check where the EDGES land, not the
  centre.**
- `FacePlayer` yaw-billboards a GO toward the camera; fronts face **+forward**.
- There is **no documented API to open a URL / Steam overlay** — show links as copyable
  text; any link-sharing has to be click-to-copy (`DiscordButton.Copy()` /
  `GamesButton.Copy()`). **Print the URL in full next to the button.** A link a player
  cannot open is one they have to type, and they can only type what they can read — so a
  shortened or ellipsised display string is a link nobody can follow. The board and the
  clipboard must carry the same characters.
- **Every outbound link lives on the MORE board**, on the south wall in the slot the music
  board used to hold (`SettingsWall.MakeMoreBoard`, `MoreBoardPanel`, `InfoScreen`'s
  `StationKind.More` page). It carries an `InfoStation`, not a `SettingsStation`: it sits on
  that wall because of where it is, not because it is a setting, and reusing `InfoStation`
  means E, the freed cursor and Escape all come for free. The welcome board used to end with
  the Discord invite and no longer does — one place to reach us beats a link stapled to the
  end of a how-to-play page. Three links now: the invite, our other games, and **music by
  Skafinity** (`https://github.com/gamah/skafinity` — the library that writes the soundtrack;
  N opens its board, M mutes). Each has its OWN `SinceCopied` clock (`DiscordButton`,
  `GamesButton`, `SkafinityButton`), because a shared one flashes the wrong confirmation.

## UI Gotchas (learned the hard way)

The generic panel rules — `pointer-events` not inheriting, panels as flex containers,
`transform: scale`, `overflow: scroll` vs drag, font sizes from `Box.Rect`, the Body-child
renderer — are in `~/.claude/sbox.md`. What follows is what this repo paid for on top.

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
- **The same rule governs every WorldPanel, and there is exactly one shape that works.**
  `root { width: 100%; height: 100%; pointer-events: none; }` and *nothing else*, with an
  absolutely-positioned `left/top: 0; width/height: 100%` child doing the layout.
  `MarqueeNumberPanel`, `SpectatorSeatPanel`, `CenterInfoPanel` and `StationScreenPanel`
  are all byte-for-byte this. The old `TableClockPanel` was written with a fixed px size and
  the centering on `root` and rendered wrong — **copy the working one rather than reasoning
  about what root ought to accept.** Note `root` takes `100%`, not the panel's px size:
  the px space is set by `PanelSize` on the `WorldPanel` component, not in the stylesheet.
- **…and that shape CANNOT BE COMPOSED. It holds ONE string. A second string is a second
  panel on a second mesh — build it in 3D, not in CSS.** This is the most expensive lesson
  in this file, and it was learned five times before it was learned once. The table clock
  tried to draw two times and a material bar in one WorldPanel and cost **five rounds, five
  bugs, every one of them layout and not one of them data** — the world scale, the `root`
  rule, nowrap/flex-shrink, plate-vs-text centring, and finally `position: absolute`
  retargeting to an ancestor box instead of `root`. `gambit_clock` proved the seam correct
  the entire time. The mechanism is mechanical, not bad luck: **the moment a box sits between
  `root` and the text div, `position: absolute` retargets to that box and every centring rule
  silently means something else.** The working panels have no such box because they have
  nothing to compose.
  → So the clock is now **the table-plaque pattern twice** (mesh plate + one-string panel)
  **plus a mesh bar**: `ChessRing.BuildClockPlate`/`BuildClockBar` + `World/TableClock.cs` +
  `UI/TableClockTextPanel.razor`. It is the same instinct as the spectator board being real
  meshes, and the same rule two bullets up — *reach for meshes over panel art*. It also moves
  the design out of the domain this host cannot test and into **arithmetic**, which is the
  domain where the M11 pass got the margin budget, the tilt/height tradeoff and the plaque
  corner right on the first attempt every time.
  → It buys correctness, not just tidiness: a mesh plate sits at table-local `x = −7` for
  White, so **a wrong side is visible in the diff**. The panel had to reason about a
  WorldPanel's content-space handedness for the same fact and got it backwards, rendering
  each player their opponent's clock.
  → **Checked against the siblings and the engine (2026-07-29, at the owner's ask), and the
  rule is narrower than "one string".** terryball composes multi-element WorldPanels that
  work: `PinReadout.razor` lays a title over a count over a ten-pin triangle, and
  `LaneConsole.razor` a whole scorecard — both by putting flex layout **directly on `root`**
  (`width/height`, `flex-direction: column`, padding, background) with **no absolutely
  positioned anything**, and `flex-shrink: 0` + `nowrap` on every text div. So the real,
  mechanical rule is the one already stated above: ***`position: absolute` retargets to the
  nearest positioned ancestor.*** Gambit's shape is absolute-positioned, and *that* is what
  cannot be composed — a second child breaks it. A flex-only root composes fine.
  **Both are true, and the choice is a real one:** ours survives a WorldPanel whose pixel
  space is set from code and needs nothing centred by hand; terryball's costs a layout pass
  this host cannot see. **Keep copying the one-string shape for anything that goes on a mesh
  plate** (it is also how the arithmetic stays checkable here) — but "a second string needs a
  second mesh" is a house style, not an engine limit, and a genuinely page-like world board
  should use terryball's flex-on-root shape rather than being carved into five meshes.
  → Two engine facts worth having while doing either, read from `sbox-public` the same day:
  a world panel's root keeps **`Scale = 2`** (`Sandbox.UI.WorldPanel`'s ctor; its
  `UpdateScale` override is deliberately empty), so **the CSS layout area is `PanelSize / 2`**
  — proportions are unaffected (px lengths cascade by the same Scale, which is why the clock's
  chars×advance÷width arithmetic still holds), but a px value compared against a raw
  `PanelSize` is off by 2×. And **`WorldInput` exists**: a `Component` you hang on the camera
  that feeds a RAY into the UI system, so world panels with `pointer-events: auto` become
  clickable by looking at them (it uses the cursor ray when `Mouse.Active`, the GameObject's
  forward otherwise, and honours `WorldPanel.InteractionRange`, default 1000). P99 built two
  world-space buttons with hand-rolled plane arithmetic instead (an unproven input path cannot be
  tested on this host) and then **deleted them both**, keeping the key. **Issue #28 is the first
  thing here to actually use `WorldInput`**: `LobbyPlayer` adds one to the local camera, and the
  tabletop BOARD SETTINGS plate is the one panel in the lobby with `pointer-events: auto` — every
  other WorldPanel is `none` by the one-string root rule, so nothing else changed behaviour by its
  existing. It is still unproven on this host, and it is still the right call: a third hand-rolled
  ray test would be equally unproven and not the engine's. **Any future world-space control goes
  through it.**
- **`⬜`/`⬛` are emoji too.** The "panel glyphs paint as colour emoji" rule is not only
  about chess pieces — the geometric-shape block characters are the same trap. `GameHud`
  uses them safely at 13px in a HUD; at 76px on a world panel they render as two big
  square blocks that shove the actual content off the face. Use letters.
- **Every text div in a flex row needs `white-space: nowrap` AND `flex-shrink: 0`.**
  These two are one rule, and it is the single most expensive line in this file. A flex item's *default* is to shrink when the row is tight; a shrunk text div
  doesn't ellipsize, it **wraps**; and the rule above then clips it to a sliver of its
  first line. The result is not a missing element or an error — it is a **few visible
  pixels of the middle of your text**, which reads as a rendering bug anywhere but the
  stylesheet. `SpectatorSeatPanel` carries `.tag > div { flex-shrink: 0 }` plus `nowrap`
  on every text div for exactly this; the old `TableClockPanel` omitted both and rendered its
  clock as **a single dot** while `gambit_clock` proved the value was `168.1s` the whole
  time. Short strings hide it — "W" cannot wrap, so a one-character label renders fine
  next to a four-character one that doesn't. **If some text on a panel renders and some
  doesn't, check the string lengths before you check anything else.**
- A free-floating interactive panel kills roaming mouselook — gate interactive screens on
  being engaged at a station, and free the cursor there
  (`UseLookControls=false`+`UseInputControls=false`, restored on close).
- No documented API to add buttons to the built-in escape menu; Escape leaves the station
  via `Input.EscapePressed`.
- If a board click doesn't land, a HUD panel is eating it — the `Select`/mouse1 action
  must reach the world past the ScreenPanel.

## Sounds

Synthesized WAVs in `Assets/sounds/sfx/` generated by `scripts/gen_sounds.py` (numpy).
`.sound` gotchas: `"Sounds"` lists `.vsnd` paths (not `.wav`), `"Volume"`/`"Pitch"` are
JSON strings, `"UI": true` for 2D playback, `"__version": 1`.

**The whole set was heard in-room and is good** (M17) — including the ones synthesized blind
that PLAN.md tracked as unheard: `panic` (the per-second one), `check` landing over tick/tock,
and `gameover3d` at the TV wall. No retune needed; treat the current `gen_sounds.py` output as
the intended sound unless a specific one is called out.

**`tick`/`tock` are MOVE sounds, not clock sounds.** This line read "tick/tock → clocks (by
side)" for a long time, which describes a ticking clock that did not then exist. They fire
**once per move**, by side. (There *is* one per-second sound now — `panic` — and it is the only
one; see below.)

**Every board sound goes through `Gambit.Audio.TableSounds`, which watches the `IBoardGame`
seam.** That is the whole design, and it is worth knowing why it isn't a call in the
controllers. Sound used to hang off `LocalGameController`, and so **a real lichess game at a
table — the M8 headline feature — was completely silent from M8 to M11**: no move, no capture,
nothing. Nothing looked wrong in any diff, because the code that was there was correct; it just
only ever covered half the tables. A watcher on the seam makes that class of bug impossible
rather than merely fixed — a third kind of game gets these sounds by existing.

**Don't add a `Sound.Play` to a game controller.** If a new reactive feature has you typing
`LocalGameController`, that is the same mistake starting again.

| Moment | Sound | Yours (2D) | The room's (3D) |
|---|---|---|---|
| Move | `tick` (White) / `tock` (Black) | ✅ | ✅ `tick3d`/`tock3d` |
| Capture | `pop` | ✅ | ✅ `pop3d` |
| Check | `check` | ✅ | ❌ — six tables checking is noise, and the king is already tinted red |
| Game over (incl. a flag) | `gameover` | ✅ | ✅ `gameover3d` at 45% — worth a glance up, not the room's attention |
| Draw / takeback offered | `offer` | ✅ | ❌ — only the player being asked |
| Clock under `TimeControl.PanicSeconds` | `panic`, **1/sec** | ✅ | ❌ — the first per-second sound in the game, and it stays at one table |
| lichess TV game ends | `gameover3d` | — | ✅ at the north wall |
| Ring rebuild | servo slide, follows the cabinet | — | ✅ (`ChessRing.cs` → `ChessStation.cs`) |
| Settings click | `tick` | ✅ | — |

The gate is **your table is 2D, the room's tables are 3D, and the room must not become a slot
machine with six tables** — which is why the right-hand column has three ❌ in it. Gates are
`MyCabinetSounds` / `RemoteCabinetSounds` (`SoundPlayer.cs`), both default true. **Which sounds
cross the room is decided in `SoundPlayer`, not at the call site**: every method there takes
`mine` rather than letting a caller pick between the 2D and 3D asset, because a call site that
gets to choose is one that can get it half right.

**A 3D variant is a `.sound` file with `"UI": false` pointing at the SAME `.vsnd`** — no second
WAV. This is why `tock3d` finally exists: it was recorded here for a whole milestone as "an
unmade asset", which was wrong — it was six lines of JSON reusing the `tock.wav` that was
already there. **Check what an asset actually costs before recording it as a decision.**

**There is no separate flag sound.** A clock running out *is* the game ending, and the
game-over sound covers it; firing both would be the same sound with a grace note.

Still silent, deliberately: **resign** (it's a game over, and it makes that sound) and **sit /
stand** (you know you sat).

**No sound may be fired from a FEN diff alone.** `Code/Chess/BoardDiff.cs` classifies a
position change as `Move` / `Rewind` / `None` and is Sandbox-free so it can be run here — a FEN
change on its own also means a takeback, a table reset, or a late joiner's first sync, and only
the **ply direction** separates those from a move. It is proven in a dotnet harness against
real games through the vendored rules (en passant, capture-promotion, castling, the resync).
That extraction is the `CapturedMaterial` lesson again: left as a private method on the
watcher `Component`, none of it could have been executed on this host.

**Spoken moves / TTS (M12) ride the SAME seam, gated on `Mine`.** An opt-in world setting
reads out the notation of moves played on the board *you are seated at* — never the TV wall,
never another player's table. `TableSounds.WatchMove` calls `MoveTts.SpeakLastMove(game)` only
when `Mine`, so it inherits the move classification for free and covers a lichess game the same
as a local one (`ChessBoard.Move` fills in SAN on execution, so `SanMoves` is real for both).
Three facts worth keeping:
- **`Sandbox.Speech.Synthesizer` is SAPI-backed and Windows-only.** On Mac/Linux/dedicated it
  has no voices; `MoveTts` catches that and the feature is a silent no-op. It is **never
  required**, exactly like gamchess — every path fails closed to silence. `Code/Chess/
  MoveSpeech.cs` (SAN → "knight f 3") is Sandbox-free and dotnet-tested here; the speaking half
  isn't.
- **Voices are enumerated once and cached** (`MoveTts.Voices`) because constructing a
  `Synthesizer` queries the OS, and the settings panel rebuilds its rows every frame it's open.
  A *speak* still constructs a fresh one (the wrapper accumulates its text and can't be reset)
  and runs synthesis synchronously — a per-move main-thread cost, paid only when the feature is
  on and a move is seconds away. If that ever stutters noticeably, a background `GameTask` is
  the escape hatch, but mind that `SoundStream`/SAPI may not be thread-happy.
- The picker is a **tap-to-cycle pill**, not one cell per voice: a machine can have many voices
  and a row of full names would overflow the panel. It stores the full name (`TrySetVoice`
  needs it) and shows a short one.

Music is the `gamah.skafinity` library — source-committed under
`client/Libraries/gamah.skafinity/`. The player + panel are built client-local by
`LocalMusicSystem` (never scene-authored — see the #12 rule above).

**The music board is KEYS, not a wall — `N` opens it, `M` mutes** (`UI/MusicScreen.cs`, the shape
terryball has always had and rotaliate-client now shares). There is no music board on the south
wall and no `StationKind.Music`: the wall row is World + Host, re-centred at ±0.13. The panel stays
*disabled* while closed, because its own floating ♪ button is a pointer-events element that would
hold the cursor released and kill roaming mouselook — that hazard is why the panel was gated on an
engage flow in the first place, and the gate is now a key rather than a place you walk to. **`N`
only opens while ROAMING** (engaged already owns the screen and owns Escape); `M` works anywhere
because it draws nothing. While the board is up, `Mouse.Visibility` is `Visible` — deliberately not
`SeatAim`'s `Auto`, because Visible also zeroes `Input.AnalogLook` so clicking around the board
can't spin the camera — and every exit path (✕, Escape, N, engaging, `OnDisabled`/`OnDestroy`) puts
`Auto` back.

**A library's `.razor.scss` NEVER reaches a joining client — issue #12's second half.**
A joiner of an editor-hosted lobby mounts no package: it gets code via the compiled
CodeArchive (so a library *panel class* always arrives) and loose files only from the
host's networked-file table, which is built by walking **the game package's `Code/` +
`Assets/` alone** (auto-including `.scss`/`.ttf`/compiled assets, plus `.sbproj`
`Resources` globs — which also only filter the game filesystem). Library folders are
never walked, so a stylesheet living in one styles the host and 404s on every joiner —
the "open + unstyled splayed board" that survived every NetworkMode fix, because it was
never a networking bug. Hence the vendor patch: `SkafinityMusicPanel.razor.scss` lives at
`client/Code/UI/` (the panel resolves it by mounted path, `UI/SkafinityMusicPanel.razor.scss`,
which both locations map to — keep exactly ONE copy). **The library update ritual must
re-delete it**: syncing the vendored library from upstream (or an editor install/update from
sbox.game) brings the scss back beside the razor, where it shadows the game copy at the same
mounted path — silently, since both parse. The host mounts library content into
`FileSystem.Mounted` ONLY in the editor (`GameInstanceDll.cs` gates it on
`Application.IsEditor`), which is exactly why the host always styled while joiners never
could, and why nothing short of moving the file works. A *published* package was never
affected — the publish manifest sweeps every library's Code path, scss included; this hole
is specific to editor-hosted joins. Same mechanism, other victim: a raw
asset loaded at runtime (`chess_glyphs.png` via `Texture.Load`) ships to joiners only if
listed in the `.sbproj` `Resources` field — and the editor generates the REAL `.sbproj`,
so that field must be set in the editor's Project Settings on each dev machine (the repo
template documents the intent). Diagnose either with the host console:
`debug_network_files 1` logs every file the host offers joiners.

## Lobby chat and proximity voice (M12)

### Chat is the ENGINE'S overlay now, not ours

`ChatShowUI` is **`true`** in `Platform.config`. The engine draws the feed and the input box;
messages route/filter through the host as before. This replaced a **288-line** custom chat box
(feed + `TextEntry` + fade + hand-rolled word-wrap) copied from rotaliate and kept alive *only* by
turning the engine overlay off to redraw it worse — terryball threw the same box away (`8ad9f4b`),
and this is Gambit catching up. **Do not re-add a custom chat box.**

**The chat WINDOW's position and size are the engine's, not ours, and cannot be moved from game
code** (checked against `sbox-public`'s `menu` addon 2026-07). The feed/input is that addon's
`ChatOverlay`, laid out by *its own* stylesheet (`left: 64px; bottom: 20%; width: 600px` in-game;
messages `justify-content: flex-end` up to `max-height: 400px`, then scroll — so it already grows
UP from the bottom). It renders in the menu overlay tree, outside our `ScreenPanel`, and
`Platform.config` exposes only `ChatEnabled`/`ChatShowUI`/`ChatMaxMessageLength` — **no position, no
API**. So "align the chat box to the glyphs / grow it to the top" is not doable without re-adding
our own box, which is the forbidden path above. The keycap HINT is ours and movable; the window is
not.

`ChatPanel.razor` used to draw the "which key opens chat" hint; **that hint moved to `VoicePanel`
(M17) and then out again into `UI/Screens/HudHints.razor`**, which is now the ONE bottom-left
keycap stack — Chat · M mute music · N music · G voice · B mute players — in one style, each
keycap read live from its binding where one exists. **All three s&box repos draw it this way now**
(gambit, terryball, rotaliate-client): a stack is a list a player reads top to bottom, and three
panels in three corners was not. `VoicePanel` keeps only the mute ROSTER, which opens above the
stack (`bottom: 100px` vs `44px` — keep the two in step). `ChatPanel` now **draws nothing** and survives
only for its `IsOpen` stub (below) — a serialized `lobby.scene` component, so the class can't be
deleted without orphaning that scene entry.

- `ChatPanel.IsOpen` is a **stub `=> false`** kept so `LobbyPlayer`'s "don't walk while typing"
  gate compiles. That gate is now **dead code, and that's fine** — the engine's focused text box
  already stops WASD leaking into the world. Don't try to revive it.
- The chat keycap (now in `HudHints` beside the Music/Voice/Mute keycaps) is read live from
  `Input.GetButtonOrigin( "Chat" )`, **never hardcoded** — there is no glyph-ICON API in this
  build, so an input "glyph" is the binding string in a keycap box. The old ChatPanel's comment
  claimed the key was "rebindable in Settings" and resolved it through
  `PlayerData.Bindings` — but **nothing ever wrote `Bindings`**, so it was dead code guarding a
  feature that doesn't exist. `Bindings` is **deleted** from `PlayerData` (old saves drop the
  unknown key on load); `GamepadBindings` is the real, separate thing — don't confuse them.

### Proximity voice, copied from terryball

`GambitVoice` (a `Voice` subclass) rides every avatar, added host-side in
`LobbyNetworkManager.AddVoice` before `NetworkSpawn`. `VoiceScreen` (keyboard driver) + `VoicePanel`
(the mute roster) + `HudHints` (the G/B keycaps, with chat's and music's) self-attach to the
ScreenPanel — client-local, so the mute/enabled state (cookies in `Gambit.Game.VoicePrefs`) never
rides a snapshot. **Master voice defaults OFF.**

- **Playback gates on the RECEIVER**: `ShouldHearVoice(Connection c) => VoicePrefs.VoiceEnabled &&
  !VoicePrefs.IsMuted(c.SteamId)`, called with the *sender's* connection — so mute needs no sync,
  no authority, no server state. Transmit is gated owner-locally via `Voice.Mode` (AlwaysOn +
  `"Voice"` PTT binding when on; Manual + `NoVoiceInput` unbound sentinel when off). **Never touch
  a networked Enabled flag.**
- **Hearing RANGE is a receive-side, per-client value** — the 3D falloff is applied on the receiver
  off each proxy's `Voice.Distance`/`Falloff`, so "how far voices carry to me" is my choice, not the
  speaker's. That is *why* it lives on the **world-settings board** (two `PlayerData` sliders:
  `VoiceRangeAtTable` / `VoiceRangeRoaming`) and needs no networking. `VoiceScreen.ApplyHearingRange`
  writes `Distance` onto **every** avatar's voice each frame, keyed on the LOCAL player's engage
  state (tighter seated, wider roaming — both tunable). Enabled/muted stay cookie-light in
  `VoicePrefs`; only range is on the board, because range is a room-tuning knob.
- **The world-settings board uses real `SliderControl`s now (M12), not the rotaliate tick bars.**
  Every continuous setting — brightness, pop rate, voice range, move-voice volume — is a
  `SettingsModel.SliderSpec` that `SettingsScreen` renders as a `<SliderControl>` (swatches and
  toggles stay clickable cells), and `BoardSettingsScreen` renders the identical spec the identical
  way — one model, one look, whichever panel a row lands on.
  Sliders are **continuous, no `Step`** by request; `OnChange` persists on every change (the file is
  tiny). The label carries the formatted value and recomputes because `Mutate` bumps
  `SettingsVersion` and the screen rebuilds — the same reason a `SliderControl` survives the
  mid-drag rebuild (s&box diffs it, terryball proved this). **`gamah.skafinity` is exempt** — it is
  a vendored library with its own music sliders; do not "upgrade" those.
- **Gotchas that were the reason to copy** (all live in the code comments): `Voice.OnUpdate` is
  **sealed** (only the hear/exclude hooks are virtual); the engine's default `Falloff` is savagely
  front-loaded (~4% by 20% of range) so we use a **linear** `Curve` + `Volume = 2f`; the default
  `Distance` (15,000u) is wrong for the 800u room, hence the sliders; **V is the engine's
  push-to-talk**, so the master toggle is **G** and the mute roster is **B** (both free in Gambit's
  `Input.config`; a new `"Voice"` action bound to V is the PTT key). `Voice.IsListening` honours the
  user's s&box `voip_mode`, which game code can't change — the chip surfaces it, never claims to be
  the switch.
- **Stripped from terryball**: the first-run help pop-up (Gambit's welcome board is its own thing),
  `LocalIsBowling`, `TerryAvatar`. The `INetworkListener`-fires-on-every-component trap that gave
  terryball N avatars per joiner **does not apply** — Gambit has one `LobbyNetworkManager` and one
  `AddVoice` call site.
