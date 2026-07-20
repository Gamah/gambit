# CLAUDE.md — Terry's Gambit s&box Client

**Terry's Gambit** (repo/ident: `gambit`, org `gamah`, namespace `Gambit.*`) — chess
in a social s&box lobby, backed by **gamchess**, our own Go/Postgres service. Forked
from rotaliate-client: the walk-around lobby, station ring, and networking scaffolding
are inherited; the arcade game and its Go backend were replaced by chess boards and
gamchess.

This file is the durable reference: how the game is built and the s&box lore that keeps
biting. **`PLAN.md` is only upcoming work and open issues** — read it for what's left,
not for how things work.

**`PLAN.md` is one flat table of things that need doing, ranked 1–100, highest first.** It
carries no milestone structure and no history: a milestone that shipped leaves nothing behind
in it, because the reasoning that outlives the work belongs *here* and everything else belongs
in git. Two consequences worth knowing before editing either file:

- **The rank is a priority, not a schedule, and rows are not branches.** Group rows into
  branches when you pick them up — several small rows on one wall or one panel are usually one
  branch; one big row (chat, voice, the viewer) is usually its own. The table is flat so it can
  be regrouped freely.
- **A shipped row is deleted, not ticked.** So the way work reaches this file is by hand and on
  purpose: when you close a row, ask what a future session would get wrong without it, and put
  *that* here. Nothing copies itself across.

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

- **Tokens are encrypted at rest under envelope encryption (M15).** `LICHESS_TOKEN_KEY` is now
  the **KEK**: it wraps rotating **data keys** (`internal/keyring`, `lichess_key_versions`), and
  those seal the tokens. A blank KEK still switches lichess **off**; it never falls back to
  plaintext. **Back the KEK up** — it is still the one durable secret. What CHANGED: there is now
  a rotation path. The data key rolls on a timer (`LICHESS_KEY_ROTATION_DAYS`, default 30) with a
  background re-encrypt sweep, and the KEK itself can be re-keyed without orphaning links by
  setting `LICHESS_TOKEN_KEY_OLD` for one deploy (re-wraps a few DEK rows, never the tokens). Old
  rows carry `key_version = 0` = "sealed directly under the KEK, pre-M15" and migrate on their
  own. **The envelope adds no secrecy on this deployment** — KEK and DB share a box, so a full
  compromise reads both; its value is the rotate/re-key-without-orphaning capability, not
  confidentiality vs a DB-only dump. How rotation actually runs, and the KEK-rotation runbook,
  are under **gamchess deployment facts** below.
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
- A **direct challenge to a named user** (`ChallengeKeepAlive`, `/api/challenge/{name}` with
  `keepAliveStream`) is a third relayed flow, one intent like a seek — the named opponent
  consents on lichess, so nobody here is committed. Its trap is documented, not intuitive:
  **closing the keep-alive stream does NOT withdraw the challenge.** lila only stops a 15s
  ping; the challenge then goes Offline and stays acceptable for **hours**, so `relay.Cancel`
  POSTs an explicit `/cancel` and an unanswered one is bounded by `challengeAnswerTTL`. Read
  from lila, not the OpenAPI doc, which says the opposite.

#### The shareable link IS a relayed game — anon browser vs your board (open + accept?color=)

**An anonymous browser player CAN play your authed, board-relayed account. It is a real flow, it
worked before M8, and getting it wrong twice is why this section is long.** The mechanism was
re-derived from the live spec (`lichess-org/api` master, 2026-07-17) after a wrong "it's
impossible" claim sat here:

| endpoint | `security:` | what it does for us |
|---|---|---|
| `POST /api/challenge/open` | **`[]`** (anonymous) | mint the link. A `board:play` token 403s it ("Missing scope: challenge:write"), so we send **no** token. |
| `POST /api/challenge/{id}/accept?color=` | `["challenge:write","bot:play","board:play"]` | **seats our token holder** in the open challenge, on the chosen side. `board:play` is accepted; `color` is "only valid if this is an open challenge". |
| `POST /api/challenge/{username}` | `["challenge:write","bot:play","board:play"]` | the direct challenge — same scope list, which is *why it works on `board:play`* and the open-create 403 looked contradictory. |

So `relay.runOpen`: **create the open challenge anonymously → `AcceptChallengeColor` with the
player's token (this is the step M8 dropped, leaving the creator's seat empty and the game never
starting) → publish `share_url` (the opposite colour's url) → watch the event stream for the
opponent joining, as a seek does → `streamGame` the player's side to the board.** The browser
opponent needs no lichess account.

- **No `challenge:write`, no re-link.** `board:play` is enough (create is anonymous, accept takes
  board:play). The old pre-M8 code requested `challenge:write` but never needed it; the M8 rule
  "board:play only" was fine — the M8 *bug* was skipping the accept step, not the scope.
- **Blitz+ only**, unlike the old M8 one-shot that allowed bullet: OUR side plays through the
  Board API now (we relay it), which won't play faster than blitz. The link is a *relayed game*
  with a `client_game_id`, polled like a seek — not a bare link.
- **It is a solo flow** (`PlayRequest.Open`, `solo()` true, one pending slot, one event stream).
  `State.seek` is true (the browser opponent is a stranger); `ShareURL` is the only extra field.
- **Colour** is which side WE take; the opponent's `share_url` is the opposite. "random"/"" accepts
  without a colour and we learn our side from `gameFull` (`resolveSoloColor`), same as a seek.
  Client-side, picking the colour **moves the player's seat** (`LobbyPlayer.SwitchSeat`) so the
  board shows them where they'll play; random moves once the game starts.
- **Cancellation is best-effort.** We created anonymously, so a `/cancel` may be refused; an
  unjoined open challenge expires in 24h. `runOpen` bounds the wait by `challengeAnswerTTL` (same
  hazard bound as the keep-alive challenge) and best-effort withdraws on timeout.
- **The consent model still holds:** a game at a table is local unless a player picks a lichess
  flow — `InfoScreen`'s Welcome + Lichess branches say it; keep saying it.

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
    marking provisional (lichess disables its own range control when provisional). Recorded
    because it is the fact that *looks* like it unblocks a rating chip; it doesn't, because the
    chip shouldn't exist.
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
- **Quick pairing and blitz seeks are both locked behind ONE door: the `web:mobile` scope.**
  Re-derived from lila + lila-ws master 2026-07-16, correcting an earlier, blunter claim in
  this file that the API simply forbids them. It doesn't — it gates them on being lichess's
  own app, which amounts to the same thing for us and is a very different reason.
  - **Quick pairing (the homepage pools) is not `POST /api/board/seek`.** They are different
    systems in lila: a seek is a *hook*, quick pairing is a *pool*. `grep -i pool` over
    lila's `conf/routes` returns **nothing** — there is no HTTP endpoint at all. Pools live
    on the **WebSocket lobby** (`poolIn`/`poolOut` in lila-ws's `ClientOut`), and lila-ws's
    bearer auth requires the token's scopes to be **`web:mobile` or `web:polygon`** — a
    `board:play` token cannot authenticate to lila-ws, full stop.
  - **Blitz seeks are not universally refused.** `SetupForm.boardApiHook` takes an
    **`allowFastGames`** flag that skips the Rapid check entirely, and `Setup.boardApiHook`
    passes `ctx.isMobileOauth || ctx.isTakex3 || (ctx.isAnon && isLichessMobile)`. Both
    `isMobileOauth` and `isTakex3` are scope checks (`Web.Mobile` = `web:mobile`,
    `Web.Takex3` = `web:polygon`). So blitz IS seekable — **if you hold the scope whose own
    description reads "Official Lichess mobile app"**.
  - **We do not request it, and this is a rule, not an oversight.** `board:play` is the only
    scope we ever ask for. Taking `web:mobile` would mean claiming to be lichess's first-party
    app to bypass a gate they put on third-party board clients deliberately — against an API
    whose limits our whole playerbase shares on one IP, whose traffic we made attributable
    to us on purpose, and which lichess can kill wholesale on `clientOrigin`. It would also
    force every linked player through a re-link. **Don't "fix" the blitz seek this way.**
  → The consequence stands: **a blitz table can never find a stranger**, and quick pairing is
  not a feature we can have. The direct challenge is the primary flow *because* of this.
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
affordable — the cost is bounded by the channel count (15), not the player count. Ref-counted
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
both to whatever the next one says. lichess stays the only authority, and local drift cannot
outlive one move.

> **The "it can only read low" reasoning was WRONG, and it was written in three places.** This
> file, PLAN.md and the code comment all claimed the local countdown "drifts LOW by roughly the
> network latency". It read **HIGH**, and that inverts the house rule below — the one rule the
> whole clock design exists to satisfy. The mechanism: lichess stamps the clock at the move
> instant **T₀**; the frame reaches us at **T₀ + L** (lichess → gamchess → client); on arrival
> the code set the bank and zeroed its age, so at wall-clock T₀+L we displayed the value as of
> T₀ while the player had already burned **L**. Displayed = true + L, held until the next move.
> "Counting down from a known-good value can only read low" is the step that fails: **the value
> is already stale on arrival**, so the countdown starts late, not early.
>
> **Fixed in M11** — and the fix is not the one this file specified, which is worth keeping.
> The agreed shape was "gamchess stamps receipt time into the state and the client subtracts the
> elapsed since". **That does not work, for two reasons found on building it:**
>
> 1. **We do not share a wall clock with gamchess.** An absolute stamp is meaningless to a
>    client, and a skewed one corrects by the skew — *including upwards*, the one direction the
>    house rule forbids. The correction has to travel as a **duration**, not a timestamp.
> 2. **The stamp alone is a no-op on the common path.** The long poll wakes on the frame, so
>    gamchess sends it instantly and its own staleness is ~0. The bias that actually exists is
>    the **network leg**, and no server-side stamp can express it.
>
> So `TvState` carries **two** durations, both computed at send: `clock_age_ms` (how long the
> value sat with gamchess — ~0 normally, and it earns its keep only on a late or reconnecting
> poll) and `hold_ms` (how long gamchess sat on the request). The client measures its own round
> trip and takes **network = round trip − hold_ms**; without `hold_ms` a 5s long-poll hold reads
> as 5s of latency and the clock runs five seconds fast-forward of the truth — a *bigger* lie
> than the one being fixed. It subtracts the **full** remaining round trip rather than halving
> it for the downstream leg: the house rule is one-directional, so a deliberate undershoot is
> free where a fair estimate is a coin-flip on the forbidden outcome.
>
> **The lichess→gamchess leg survives by construction** — nothing downstream knows T₀ — so a
> small residual high bias remains, documented rather than denied. Its magnitude has never been
> measured; only its direction is certain.
>
> **The lag applies to the TICKING seat only.** The idle side's clock isn't running, so however
> stale the frame is their bank is still exactly right; subtracting from both would invent a
> loss of time that never happened. Anything that re-derives a TV clock must reason from *when
> the value was stamped*, never from *when we received it*.

**The house rule: a live clock must never read HIGHER than the time actually left** — the same
rule that makes `TimeControl.Format` truncate where the PGN writer rounds. Reading low is
explicitly permitted; reading high is not. The rule is one-directional on purpose, which is why
a deliberate undershoot satisfies it where an unbiased estimate would not.

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
- **gamchess only supplies the REASON**: it notices the same swap and fetches the old game's
  result from **`GET /game/export/{id}`** (anonymous, like the feed; `status` + `winner`, where
  a **missing winner means a draw**), publishing it as `last_game_id/last_status/last_winner`.
  The client uses it only if `last_game_id` is the game it was actually showing.

The first version had the client WAIT for `last_game_id` to appear before announcing anything
— which silently made the entire feature depend on the server half being deployed. Against a
gamchess without it, nothing ever fired, and **a fanfare that never fires looks identical to
one that isn't wired up**: it cost two rounds of testing and a wrong diagnosis. Now an
undeployed server costs the *reason* ("Game over") and never the announcement.

The client holds the finished position for `LichessTv.FanfareSeconds` (3s) with a result line,
because lichess TV cuts to the next game instantly and on a wall that reads as a glitch.

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
it only applies when `last_game_id` still names the game it fetched) and **does not bump the
version on that no-op** — a spurious bump would wake every poller and make the client re-snap its
locally-run clocks (the sawtooth the clock section warns about). The client, still holding on the
finished game, **upgrades** the fanfare line from "Game over" to "White wins — out of time" when
the later poll carries the reason (`LichessTvSource`, guarded on `InFanfare && _gameId ==
_fanfareShownFor`). This split is the same "the CLIENT decides a game ended; gamchess only
supplies the REASON" separation stated above — the synchronous fetch was quietly violating it.

**There is no buffer, and nothing to bound.** gamchess keeps only the LATEST state per channel
(one slot, overwritten), so "hold for 3s, then take whatever is current" abandons all but the
latest by construction — no queue, no catch-up, no speed-up logic.

**All 15 channels, variants included** (default `best` — "Top Rated", the best game in
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
installed, and everything under `Code/Chess/` — plus `Code/Game/TimeControl.cs` — has no
engine dependency. A scratch csproj that `<Compile
Include>`s those files runs real games, real PGN, real perft. Two settings matter:
`<TargetFramework>net10.0` (net8 builds but won't launch — only the 10.x runtime is here)
and `<ImplicitUsings>enable`, because the vendored library leans on s&box's global usings
for `System.Collections.Generic`. Verified 2026-07-15. This is also how the vendored rules
were proven originally, how a `[TimeControl]`-bearing PGN was checked against the real
writer, and how `LichessTable`'s challenge/seek floors were checked against every preset in
`TimeControl.All` — prefer it over review whenever the code can be isolated from Sandbox.

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
active source with **one** branch (`Source => Lichess is { Engaged: true } ? Lichess :
Controller`). The seam paid for itself: M8 added a whole second kind of game with no renderer
change at all. Anything that reads the position should go through it — `GameHud` and
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
| `LichessGameController` | **participants poll; spectators MIRROR (M14)** — each participant polls gamchess for itself and `[Rpc.Host]`-reports its observed move list into `[Sync] MirrorMoves/MirrorLive`, from which every non-engaged client rebuilds a display game (`Mirroring`, same IBoardGame seam). Before this a lichess game was INVISIBLE to every non-participant — solo flows (seek/challenge/link) especially | a real lichess game on this table (**M8**). Adjudicates nothing — lichess is the only authority, and the position is rebuilt from the UCI list it sends — but it DOES run the ticking seat's clock down locally between moves (**M12**), because lichess only sends a clock on a move and a frozen clock reads as a stopped game. Same countdown machinery as `LichessTvSource`, house rule and all; a local clock hitting 0 clamps and waits for lichess to call the flag |
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
id, no secret, no API key — so `LICHESS_TOKEN_KEY` (the KEK that wraps the rotating data keys;
blank = lichess off; **back it up**) and `LICHESS_AUDIT_KEY` (gates the audit sweep) are ours.
`make up` / `make testinst` mint any that `.env` lacks and **never overwrite one that has a
value** — regenerating `LICHESS_TOKEN_KEY` mid-life would leave the stored DEK rows undecryptable,
so **re-key it deliberately (below), never by regenerating**. Two optional knobs, both fine blank:
`LICHESS_TOKEN_KEY_OLD` (set for one deploy to rotate the KEK) and `LICHESS_KEY_ROTATION_DAYS`
(data-key cadence, default 30; 0 disables the timer).

**How key rotation runs — the envelope (`internal/keyring`, `lichess_key_versions`).**
`LICHESS_TOKEN_KEY` is the **KEK**; it wraps rotating **data keys** (DEKs), and those seal the
tokens. Every `lichess_links` row carries the `key_version` that sealed it; **`key_version = 0` is
the legacy sentinel** — sealed directly under the KEK before this existed, opened with the KEK, and
migrated onto a real DEK by the boot sweep (that migration is exactly the `re-encrypted lichess
tokens onto the current key, rows:N` log line the first deploy produces).

- **The DATA KEY rotates automatically**, every `LICHESS_KEY_ROTATION_DAYS` (default 30, zero
  maintenance). `KeyRing.Run` does one pass at boot then checks daily; past the cadence it mints a
  new DEK, the sweep re-seals every token onto it, and the drained key is retired (kept loaded, not
  deleted, so no row can ever be unopenable). **The cadence is anchored to the DEK's DB
  `created_at`, not process uptime** — so `make up` deploys neither reset nor trigger it. The
  rotate→re-encrypt→retire log trio is what to look for when it fires.
- **The KEK does NOT auto-rotate** — that is the deliberate manual incident lever. To rotate it:
  new key into `LICHESS_TOKEN_KEY`, the current one into `LICHESS_TOKEN_KEY_OLD`, `make up` once
  (logs `re-wrapped a data key under the new KEK` — it rewrites the few DEK rows, **never the
  tokens**), then **clear `LICHESS_TOKEN_KEY_OLD`** and deploy again. No player re-links. A KEK that
  can't open a DEK with no old key set **fails at boot** by design, rather than run half-broken.
- **One-way after deploy.** Once the boot sweep lifts a row onto a DEK, its token is DEK-sealed and
  pre-M15 code cannot read it — so a *code* rollback after deploy orphans the swept links. Recover
  from a pre-deploy DB backup, never by reverting the binary alone. (`test.sh` at the repo root is
  the host smoke test for all of this: migration is zero-downtime, app bootstraps a DEK, KEK
  rotates, and it fails closed on a bad KEK.)

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
- **Self-attaching UI**: **GameHud, SpectatorScreen, and the M12 voice pair (VoiceScreen +
  VoicePanel)** — those, and no others — attach themselves to the scene ScreenPanel at runtime
  (`LobbyPlayer` walks `Scene.GetAllComponents<ScreenPanel>()` in `EnsureGameHud` /
  `EnsureSpectatorScreen` / `EnsureVoiceScreen`), so a new screen of that kind needs no scene
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
  the snapshot, which is the whole point. **Never author a client-local screen or audio component
  in the scene** — put it on a code-built `Never` object.

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
- **…but confirm the component still has a class, because the scene lies too.** The rule
  above assumes every scene entry is real. `lobby.scene` carried **eight components from the
  rotaliate fork with no class anywhere in `client/Code/`** — every property on them inert,
  and two of them actively contradicting the code that really runs: `ArcadeRing`'s
  `BoardSize: 28` next to the real `ChessRing`'s **26**, and `SpectatorBoard`'s
  `ClearAboveWall: 20` next to the real `SpectatorWall`'s **18**. Grepping the scene and
  believing it got you the wrong number, **the exact inverse of this rule**. They are deleted
  now; the habit is the point — `grep -r "class Foo" client/Code/` before trusting a scene
  value.
- **A runtime-built component runs on code defaults and cannot be retuned in-editor.**
  `SpectatorWall` is not in the scene at all (`LobbyRoom.EnsureSpectatorWall()` builds it), so
  every one of its values is a code default. A design pass on the north wall is an
  edit-and-hotload loop, not a scene-tweak loop — unlike east and south.
- The player is ~72 units tall — the human-scale yardstick.
- `models/dev/box.vmdl` is **NOT 1×1×1**: to make a box of size S,
  `LocalScale = S / Model.Bounds.Size` per axis — use/copy `ChessRing.AddBox`.
- Never put a `BoxCollider` on a non-uniformly scaled GO — it silently freezes physics.
  Colliders on uniformly-scaled parents, visuals on scaled children.
- **A tilted object's EDGE is not half its size from its centre — derive the edge through
  the rotation, never place it by the number that would be right if it were flat.** This has
  now cost two rounds on two different objects. The table plaque dropped its centre by
  `h·cos(tilt)` and forgot the `h·sin(tilt)` the same tilt swings sideways, so its top edge
  was at the right height but tucked under the tabletop. The clock then centred its plates on
  the body's top *surface* — so a box centred on its origin buried half of every plate in the
  body, and buried the shorter material bar **entirely**, where it could never have rendered
  at all. Both times the arithmetic looked obviously right on the page and the room disagreed.
  `ChessRing.ClockPlaneOriginZ` is the worked example: surface + `h/2·cos(tilt)`, derived once
  and shared by everything in the plane, which is also what keeps their bottom edges level for
  free. **Nothing on this host can render, so a placement bug ships unless the edge is computed
  — check where the EDGES land, not where the centre does.**
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
- **`⬜`/`⬛` are emoji too.** The "panel glyphs paint as colour emoji" rule is not only
  about chess pieces — the geometric-shape block characters are the same trap. `GameHud`
  uses them safely at 13px in a HUD; at 76px on a world panel they render as two big
  square blocks that shove the actual content off the face. Use letters.
- `transform: scale` misplaces panel content — use explicitly sized wrappers.
- `pointer-events: all` must be set per interactive element; it does not inherit.
- Panels are flex containers: inline `<span>`s inside a text div become separate flex
  items; source newlines render as literal whitespace — keep each text div's content on
  one line. A div's auto height does not grow for wrapped text — use one div per line in
  a flex column.
- **…which means every text div in a flex row needs `white-space: nowrap` AND
  `flex-shrink: 0`.** These two are one rule, and it is the single most expensive line in
  this file. A flex item's *default* is to shrink when the row is tight; a shrunk text div
  doesn't ellipsize, it **wraps**; and the rule above then clips it to a sliver of its
  first line. The result is not a missing element or an error — it is a **few visible
  pixels of the middle of your text**, which reads as a rendering bug anywhere but the
  stylesheet. `SpectatorSeatPanel` carries `.tag > div { flex-shrink: 0 }` plus `nowrap`
  on every text div for exactly this; the old `TableClockPanel` omitted both and rendered its
  clock as **a single dot** while `gambit_clock` proved the value was `168.1s` the whole
  time. Short strings hide it — "W" cannot wrap, so a one-character label renders fine
  next to a four-character one that doesn't. **If some text on a panel renders and some
  doesn't, check the string lengths before you check anything else.**
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
JSON strings, `"UI": true` for 2D playback, `"__version": 1`.

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
`client/Libraries/gamah.skafinity/` (s&box pattern: libraries are source and
auto-referenced by living there; do NOT add a `PackageReferences` entry — that
double-registers the compiler). The player + panel are built client-local by
`LocalMusicSystem` (never scene-authored — see the #12 rule above); the panel is enabled
only while engaged at the music wall board.

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

`ChatShowUI` is **`true`** in `Platform.config`, and `ChatPanel.razor` is a **thin hint only**
(a keycap glyph telling roaming players which key opens chat). The engine draws the feed and the
input box; messages route/filter through the host as before. This replaced a **288-line** custom
chat box (feed + `TextEntry` + fade + hand-rolled word-wrap) copied from rotaliate and kept alive
*only* by turning the engine overlay off to redraw it worse — terryball threw the same box away
(`8ad9f4b`), and this is Gambit catching up. **Do not re-add a custom chat box.**

- `ChatPanel.IsOpen` is a **stub `=> false`** kept so `LobbyPlayer`'s "don't walk while typing"
  gate compiles. That gate is now **dead code, and that's fine** — the engine's focused text box
  already stops WASD leaking into the world. Don't try to revive it.
- The keycap is read live from `Input.GetButtonOrigin( "Chat" )`, **never hardcoded**. The old
  panel's comment claimed the key was "rebindable in Settings" and resolved it through
  `PlayerData.Bindings` — but **nothing ever wrote `Bindings`**, so it was dead code guarding a
  feature that doesn't exist. `Bindings` is **deleted** from `PlayerData` (old saves drop the
  unknown key on load); `GamepadBindings` is the real, separate thing — don't confuse them.

### Proximity voice, copied from terryball

`GambitVoice` (a `Voice` subclass) rides every avatar, added host-side in
`LobbyNetworkManager.AddVoice` before `NetworkSpawn`. `VoiceScreen` (keyboard driver) + `VoicePanel`
(chip/roster HUD) self-attach to the ScreenPanel — client-local, so the mute/enabled state (cookies
in `Gambit.Game.VoicePrefs`) never rides a snapshot. **Master voice defaults OFF.**

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
  Every continuous setting — brightness, pop rate, voice range — is a `SettingsModel.SliderSpec`
  that `SettingsScreen` renders as a `<SliderControl>` (swatches and toggles stay clickable cells).
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
