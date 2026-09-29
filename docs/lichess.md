# Lichess

Real lichess games played from a table, and lichess TV on the north wall.

Facts here come from the `lichess-org/api` OpenAPI spec and `lichess-org/lila` master. Re-read
them before trusting a sentence. **[SOURCE]** marks a fact inferred from lila's source rather than
the documented contract; it can change without notice.

---

## Custody: the client holds its own token

Playing a lichess game means holding a long-lived ndjson stream open, and lichess has no polling
substitute (a poller gets a *"Please don't poll this endpoint"* 429). `Http.RequestStreamAsync`
returns once the headers are in, and **the returned stream owns the response — dispose it or the
connection stays open**. `HttpClient.Timeout` does not bound the body read, so a
`CancellationToken` bounds how long a stream is read: a game stream lives as long as the game,
never on a timer (the opposite of `GamchessApi`'s 8s `CancelAfter`).

So the client holds the token and speaks the Board API directly. **gamchess holds no lichess
secret of any kind.** What it still does, each a thing the client cannot:

1. **Holds the redirect URI.** lichess compares `redirect_uri` byte-for-byte between authorize and
   token, and the client cannot listen on a socket, so there is no loopback option.
   `PUBLIC_BASE_URL` derives it, which also keeps the test instance pointed at itself.
2. **Shows the disclosure page.** Consent belongs somewhere with a URL bar.
3. **Is the directory.** Two seats need each other's lichess usernames to challenge by name, and
   neither client may simply be told the other's.

`client_id` is `net.gamah.gambit` (`lichess.ClientID`), a constant and not a credential: lichess has
no client registration, and records `clientOrigin` (the redirect URI's scheme+host), not the id.
PKCE secures the exchange; the redirect URI decides who receives a code.

### The link flow

The client mints a PKCE pair (`Pkce`) and registers the **challenge**
(`POST /api/v1/lichess/link/start`); the player opens the constant `/lichess/link` and consents;
the callback **parks** the code without burning the state; the client collects it
(`link/collect`) and exchanges it at lichess itself. Burn-on-use happens at collect. gamchess never
sees a verifier, so a parked code is inert in its hands.

- **`/lichess/link` is the constant the board copies — never show the raw authorize URL.** The
  constant carries no secret: it is Steam-web-session gated, so whoever opens it links **their
  own** accounts. A raw authorize URL is bound to one player's state and SteamID; a friend who
  opened it would consent on *their* lichess account and hand the first player a `board:play`
  grant on it.
- **Collect is keyed on the caller's authenticated SteamID, never on a state from the body.**
- **The callback refuses a slot that already holds a code**, because a browser refresh replays a
  spent one.

### The one token transit

`POST /api/token` returns no user id, and `POST /api/token/test` still needs the token posted. So
at link the client POSTs its fresh token to `/api/v1/lichess/claim` once; gamchess calls
`GET /api/account` (`security: [OAuth2: []]`), records the id lichess echoes, and discards the
token — never stored, logged, or put in an error string (a Go test holds the last).

- **"gamchess cannot hold your token" is a promise, not a structure**; copy must not overclaim.
- **Do not replace this with a second scopeless token** — that is a long-lived credential kept in
  exchange for a username.
- **Do not accept a client-asserted identity.** It would let anyone squat a real account's row and
  lock its owner out. Because the id comes from lichess, plain `UNIQUE(lichess_id)` is safe.

### Where the token lives

`FileSystem.Data`, in its own file, **`lichess.json`** (`LichessTokenStore`), never in
`player.json`: `PlayerData` serialises its whole cache on every settings write, and a separate
file makes "forget my token" one delete. No prompt, no toggle; the copy says where it lives. The
risk: joining an **editor host or a local-project dedicated server** compiles that host's source
into your process, and whitelisted C# can read `FileSystem.Data`. A host running a published
package sends no code.

**gamchess credentials live under a different rule.** `GamchessApi`'s session and `GamchessAuth`'s
token stay memory-only, because a gamchess session is stateless and unrevokable short of rotating
`SESSION_SECRET`, while a lichess token is revokable by its owner at any time. Do not make them
consistent.

### Unlink

`DELETE /api/token` revokes *the token sent as Bearer*, which only the client holds. So the client
**revokes, then deletes the file, then tells gamchess to forget the row** — in that order: a token
deleted first can never be revoked by anyone but the player. The web unlink button can only do the
last step and says so.

| Lever | Real? |
|---|---|
| Player revokes our grant | ✅ on **`/account/security`** — not `/account/oauth/token`, which lists personal tokens only. Copy must name the right page. |
| A lichess password change unlinks us | ❌ It touches web sessions only; `OAuthServer.auth` never reads that flag **[SOURCE]**. Copy says so. |
| We revoke a player's token | ❌ We don't have it. Deleting the game's data drops the key from that PC and revokes nothing; copy says so. |
| lichess kills the whole app | ✅ manually on their side, keyed on `clientOrigin`. |

**A crash mid-game flags; nobody resigns it.** Nobody else holds the token, so a dropped client's
game runs out its clock, as for any Board API client. Standing up keeps the game live; only
quitting drops the stream.

---

## Scopes

`board:play puzzle:read puzzle:write follow:read` (`lichess.Scopes`, mirrored by
`LichessScopes.All`). Tokens are long-lived (~1 year) with **no refresh tokens**, so any scope
change forces every linked player to re-link.

- **`board:play`** plays games. All-or-nothing, no read-only subset. It also satisfies the
  challenge endpoints, whose spec lists `challenge:write`/`bot:play`/`board:play` as alternatives.
- **`puzzle:read|write`** — puzzles on the player's real puzzle record.
- **`follow:read`** — which lichess friends are online.
- **`msg:write` is not requested**: it is send-only (there is no `msg:read`; reading an inbox is
  `AuthOrScoped(_.Web.Mobile)`, lila `app/controllers/Msg.scala`).
- **`web:mobile` and `web:polygon` are not requested, as a rule.** Their descriptions are "Official
  Lichess mobile app" and "Take Take Take"; taking one claims first-party status to pass a gate
  lichess puts on third-party board clients (it is what blitz seeks and quick pairing sit behind).
  `web:mod` is moderator tooling.

**The disclosure copy is decided with the scope set.** `InfoScreen`'s Lichess branch and
`lichess_pages.go`'s consent page enumerate what the grant **can** do. It is the sentence a
cautious player reads before consenting — the highest-risk copy in the repo.

---

## The flows and the event stream

Each client acts with its own token and can only commit itself.

- **Paired (the two seats at a table).** Both seats POST `/api/v1/lichess/rendezvous`; White
  challenges Black by name and publishes the challenge id in `[Sync] LichessGameController.ChallengeId`;
  Black accepts by id. **Black does not open the event stream to learn the id** — both seats are in
  one s&box lobby, so the id rides the station's sync, and this flow is not bound by the
  one-stream-per-token rule.
- **Seek.** Needs the event stream: a real-time seek's response carries no game id (a stream of
  empty lines whose only job is to stay open; closing it cancels the seek), so the game arrives as
  `gameStart`.
- **Challenge a named stranger.** Needs the event stream; nothing else reports an acceptance. No
  `keepAliveStream`: an unanswered real-time challenge is swept after ~20s, and Gambit POSTs an
  explicit `/cancel` (`LichessBoard.CancelChallenge`). The UI states the ~20s.
- **Shareable link.** Needs the event stream. See below.

### The two-intent rule is a directory-disclosure rule

gamchess must not hand a player's lichess username to whoever asks. It reveals seat B's username
to seat A only once **both** seats have posted an intent for the same `client_game_id`, and only
to those two. `client_game_id` is not a secret (it is `[Sync]`ed to the lobby); it is the
rendezvous key. It is not what makes a game consensual — each client commits only itself.

### One event stream per token

Opening a second closes the first, **server-side and silently**: the victim sees a clean EOF,
indistinguishable from "the game ended", so the first flow hangs with no message.
`LichessEventStream` is one refcounted process-wide owner, never one per controller. That rules it
out within a process; across processes lichess's own rule enforces it.

**A stream failure never degrades into a poll.** Reconnects back off exponentially
(`LichessStream.BackoffStartSeconds` 3s, doubling, capped at 60s), and a game stream is never
reopened once lichess reports the game finished.

### Threads and hotload

`Sandbox.WebSocket` delivers messages on the game thread; **a raw `Stream` read completes on a
thread-pool thread**. `LichessStream`'s read loop touches only a lock-guarded queue; every
`Scene`/`[Sync]`/`GameObject` touch happens in `Drain()` from `OnUpdate`.

An orphaned read task (a hotload) leaves a live HTTP connection to lichess, which on the Board API
means both "this player is present" and "this token's event-stream slot is taken". Nothing errors;
the next flow never delivers. Every loop carries a **generation**, checked after each read and
bumped on every start, so an orphan exits on its next line. `Dispose` on every exit path.

### The shareable link

An anonymous browser player can play the linked account.

| endpoint | `security:` | what it does |
|---|---|---|
| `POST /api/challenge/open` | `[]` | mints the link. A `board:play` token 403s it, so it is sent with **no** token. |
| `POST /api/challenge/{id}/accept?color=` | `challenge:write`/`bot:play`/`board:play` | **seats our player** in the open challenge. `color` is only valid for an open challenge. |
| `POST /api/challenge/{username}` | same | the direct challenge. |

Create anonymously → **accept with the player's own token** (without it the creator's seat stays
empty and the game never starts) → publish the opposite colour's url → watch the event stream for
the opponent → stream our side to the board.

- **Blitz+ only**: our side plays through the Board API.
- It is a solo flow; `ShareUrl` is the only extra field.
- **Colour** is the side we take; the shared url is the opposite. Random accepts without a colour
  and learns our side from `gameFull`. Picking a colour moves the player's seat
  (`LobbyPlayer.SwitchSeat`).
- Cancellation is best-effort (created anonymously, so `/cancel` may be refused); an unjoined open
  challenge expires in 24h.
- A game at a table is local unless a player picks a lichess flow. `InfoScreen`'s Welcome and
  Lichess branches say so.

---

## Traps

- **lila has two `isBoardCompatible` functions.** `Challenge.isBoardCompatible` is
  `speed >= Blitz` (estimate ≥ 180s) and gates challenges; `lila.core.game.isBoardCompatible` is
  `Speed(clock) >= Rapid` (≥ 480s) and gates seeks (via `SetupForm.boardApiHook`). Estimate is
  scalachess `limit + 40×increment`. **[SOURCE]**

  | Flow | Floor | Presets |
  |---|---|---|
  | direct challenge | ≥ 180s | Blitz 3+0, Rapid 10+0, Classical 30+0, Unlimited |
  | lobby seek | ≥ 480s | Rapid 10+0, Classical 30+0 |

  **Bullet never reaches lichess.** The default Blitz 3+0 table is challengeable but not
  seekable, which is why the direct challenge is the primary flow. `Code/Game/LichessTable.cs`
  encodes both floors and is harness-checked against every `TimeControl.All` preset.
- **A seek's `time` is minutes; a challenge's `clock.limit` is seconds.**
- **Omit both clock fields for an unlimited challenge**; `0/0` asks for a rejected 0+0.
- **`clock.limit`'s domain** is 0, 15, 30, 45, 60, 90, or a multiple of 60 up to 10800. 100 is a
  400.
- **Gambit sends no `ratingRange`, and has no rating chip.** The field is absolute
  (`^\d{3,4}-\d{3,4}$`, both ends 400–2900, `min < max`; invalid is a 400). Omitted, a real-time
  hook falls back to `RatingRange.defaultFor(rating)`, a Gaussian band around the player's real
  rating (`Hook.scala`) **[SOURCE]** — better informed than anything we could compute. A seek
  therefore cannot mean "anyone"; sending `400-2899` to dodge lila's "±500 = no preference" check
  games an implementation detail for worse pairings. Correspondence seeks have no Gaussian
  fallback, so the default range is really unbounded there. The ±500 clamp is web-UI-only
  (`Setup.boardApiHook` never calls `HookConfig.withinLimits`). **[SOURCE]**
- **An offer POST always answers `200 {"ok":true}`.** lichess silently drops a draw before ply 2, a
  second draw within 20 plies of your last, and a takeback before both sides have moved. The only
  truth is the standing offer on the next `gameState` (`wdraw`/`bdraw`/`wtakeback`/`btakeback`,
  omitted when false). The takeback button is hidden before move 2.
- **A declined draw is invisible on the Board API; a declined takeback is not.** `Takebacker.no`
  publishes a `gameState`; `Drawer.no` emits only a web round-socket event, and a move's
  `gameState` is built before the clear, so the flag reads false only on a later move. **[SOURCE]**
  `GameHud` says "Draw offered. Lichess won't signal a decline." and keeps "waiting for your
  opponent" for takebacks and local draws.
- **Draw and takeback are one endpoint each**: `/draw/{accept}` and `/takeback/{accept}`, where the
  segment is parsed by `Form.trueish` (`1|true|True|on|yes`); decline is any other word.
- **A takeback offer arrives as an ordinary `gameState`.** There is no takeback event.
- **Premove is not a lichess concept.** It is "POST the move the instant it is legal", client-only.
- **Quick pairing and blitz seeks both sit behind `web:mobile`.** Quick pairing is a *pool* with no
  HTTP endpoint (pools live on lila-ws, whose bearer auth requires `web:mobile` or `web:polygon`).
  `SetupForm.boardApiHook`'s `allowFastGames` skips the Rapid check only for those scopes.
  **[SOURCE]** A blitz table can never find a stranger.
- **A correspondence seek returns `{"id":…}` immediately**, no held connection. An undocumented
  `DELETE /api/board/seek` exists (`Setup.boardApiHookCancel`). Gambit implements neither.
- **Closing a challenge keep-alive stream does not withdraw the challenge**: it goes Offline and
  stays acceptable for hours (lila; the OpenAPI doc says otherwise). **[SOURCE]**
- **Imported games are unrated and attributed to nobody.** `POST /api/import` makes a game
  viewable, not counted; copy must not imply otherwise.

---

## TV

`GET /api/tv/{channel}/feed` is `security: []`. **TV needs no account and no token, and must keep
working for a player who never links.** The shared ndjson reader takes a blank token for it, and a
test (`TestStreamTvSendsNoAuthorization`) asserts no `Authorization` header goes out.

| Route | Auth | Notes |
|---|---|---|
| `GET /api/v1/tv/channels` | session/FP | `{default, channels:[{key,label}]}` |
| `GET /api/v1/tv/{channel}` | session/FP | WebSocket. The gate runs before the upgrade, so a bad session is a plain 401. |

**One upstream stream per channel, however many watch.** That is why TV goes through gamchess, and
why per-client channel choice is affordable. `tv.watch` increments `tvChannel.conns`; a deferred
`leave` in the socket handler decrements it on every exit path. The sweeper is the only thing that
closes an upstream, `tvLingerTTL` (10s) after the count reaches zero, so an A→B→A switch doesn't
flap it; correctness rests on the decrement, not the linger.

**Session-gated, not for cost.** An open relay is a free CDN for someone else's content, and lichess
sees our IP and identity string: anything done through it is done as Gambit.

### Wire

Upstream (lichess → gamchess) is **not** the Board API's shape:

```
{"t":"featured","d":{"id":…,"orientation":…,"players":[{"color":"white","user":{"name":…,"title":…},"rating":…,"seconds":…}],"fen":…}}
{"t":"fen","d":{"fen":…,"lm":"d7f6","wc":56,"bc":51}}
```

- Envelope `{"t":…,"d":…}`, not `{"type":…}`.
- `players[].user` is absent for anonymous/AI players, hence "Anonymous". `seconds` is the
  starting clock.
- **`wc`/`bc` are seconds**; the Board API sends clocks in milliseconds.

Downstream (gamchess → client) is one full, self-contained `TvState` per state change — no deltas,
no cursor, latest wins; the stored latest frame is pushed on connect:

```
channel, label, error, game_id, url, fen, last_move_uci,
white_name/black_name, white_title/black_title, white_rating/black_rating,
white_clock/black_clock (seconds), ticking_seat ("white"|"black"), age_ms,
last_game_id/last_status/last_winner/last_white_name/last_black_name
```

permessage-deflate is on: the data is the public feed.

### The clock

**A live clock never reads higher than the time actually left**; reading low is allowed. lichess
sends a clock only on a move, so `LichessTvSource` runs the side to move down locally and snaps
both to the next frame.

A live push is fresh, and flooring the displayed second absorbs transport latency. A frame replayed
on connect is stale by `age_ms`, which the client subtracts from the **ticking seat only** (the idle
bank is exact). `age_ms` is a duration, not a timestamp: we share no wall clock with gamchess. A
fixed `ClockLeadSeconds` (0.25s) undershoot covers the lichess→gamchess leg, which nothing
downstream can measure.

### The fanfare

The feed never says a game ended; it swaps to a new `featured`.

- **The client decides a game ended.** From the position first: a mate or stalemate is in the FEN,
  so `LichessTv.TryPositionResult` announces on the frame the mating move lands, gated to
  `LichessTv.IsStandardRules` (variant "mates" aren't). Otherwise from the featured id changing.
  **The announcement never waits on the server.**
- **gamchess supplies the reason** by fetching `GET /game/export/{id}` (anonymous; a missing
  `winner` is a draw) in a background goroutine after publishing the swap immediately, then
  folding in `last_status/last_winner` via `tvChannel.setLastResult`. That drops a stale answer and
  does not wake sockets on a no-op, since a spurious push makes clients re-snap their clocks.
- The client holds the finished position for `LichessTv.FanfareSeconds` (3s). The hold starts when
  the end is known; the banner shows a real who/why line or nothing (`LichessTv.Result` returns a
  null headline rather than a placeholder), upgraded when the reason arrives.
- gamchess keeps only the latest state per channel, so there is no queue to drain after a hold.

### Channels

All 16 (`lichess.ValidChannel`): six speeds (`best`, `bullet`, `blitz`, `rapid`, `classical`,
`ultraBullet`), eight variants (`chess960`, `crazyhouse`, `kingOfTheHill`, `threeCheck`,
`antichess`, `atomic`, `horde`, `racingKings`), `bot`, `computer`. Default `best` ("Top Rated").

Variants draw correctly because the wall parses no rules: `SpectatorBoard3D` walks the placement
field under a `file < 8 && rank >= 0` guard, so X-FEN castling, Crazyhouse pockets and Three-check
counters fall outside it. The standard-only rule governs *playing*. Crazyhouse and Three-check
hide state the 64 squares can't hold (`LichessTv.HidesState`), and the board says so.

**The channel allowlist is a security boundary, not a menu**: the key comes off the wire and
becomes a lichess URL, so nothing builds one from a key that didn't pass `ValidChannel`.
`LichessTv` mirrors the list for the UI; a Go test reads `LichessTv.cs` to hold them together.

**Every TV control is on the spectator board (`SpectatorScreen`)** — channel, follow-the-lobby,
on/off. The admin uses the same picker to move the lobby's *suggestion*, so `FollowingLobbyTv` is
always true for an admin and the toggle is hidden. `RequestSetSuggestedTvChannel` re-checks
host-side; `LocalIsAdmin` is a UI hint.

---

## API etiquette, in two places

`server/internal/lichess/etiquette.go` and `client/Code/Api/Lichess/LichessEtiquette.cs` hold the
same rules, because both halves talk to lichess. Each client spends its own IP; the server's IP
carries only TV.

- **Identify on every request, streams included.** Server-side a RoundTripper sets `User-Agent`. A
  s&box game **cannot set `User-Agent`** (it is in `Http.ForbiddenHeaders` and the engine forces
  its own), so the client sends the same string as **`X-Gambit-Client`**
  (`LichessEtiquette.IdentityHeader`).
- **Every lichess request goes through `LichessClient`.** An `Http.Request*` to lichess.org
  anywhere else in `client/` is a bug.
- **A 429 anywhere stops every outbound call for 60s** (`LichessEtiquette.BackoffSeconds`). Per-IP.
- **Lobby seeks are self-limited to 5/minute** (`SeeksPerMinute`, lila `Limiters.setupPost`
  **[SOURCE]**), refused locally with a reason: mashing the button would arm the player's own
  60s stop, and a household or NAT shares an IP.
- **Never retry into a throttle.** Report the reason; the player decides.
- **Dispose every stream on every path.** A leaked game stream tells lichess the player is present;
  a leaked event stream holds the token's one slot.
- **One lichess game per player at a time; further tables play locally.** lichess does not document
  concurrent Board API games, and the event stream is one per token. The gate is advisory:
  `SetupPanel` shows "playing at Table N" via `LobbyPlayer.LichessGameElsewhere`, which cannot see
  a second s&box instance; lichess's one-stream rule backs it. Local two-seat games have no limit.

The accommodation channel is Discord `#lichess-api-support` (`https://discord.gg/MS9MejQqha`), not
email. Bring real traffic numbers.

---

## Asset exception: the lichess logo

Inlined as an SVG in `server/internal/api/lichess_pages.go`, on the button that leaves for
lichess. **Non-free**: lila's `COPYING.md` lists `public/logo` under "Exceptions (non-free)" —
*"Only use to refer to lichess.org"*. So: only on a control that navigates to lichess, never as
decoration, never in the s&box client, never where it could read as endorsement. Full terms in
`client/Assets/ATTRIBUTION.md`.
