# HTTPFIX.md — the lichess token moves to the CLIENT

**Status: READY TO START, behind one editor check.** This is the durable design reference for
the change that ends gamchess's custody of lichess OAuth tokens. The "why" lives here; the
player-facing copy lives in the info boards; the gamchess wire contract lives in `README.md`.

This file replaces the ~1,100-word cell that was `PLAN.md` row No. 0. That row is now a pointer
to this file. **Nothing in the design below is new** — the six decisions are the owner's, taken
2026-07-31; what this file adds is the file-by-file teardown list, the build list, the ordering,
and the traps found while surveying the code.

> **Hold the `CLAUDE.md` custody rewrite until this actually ships.** CLAUDE.md's "gamchess
> holds the token" section is **true today** and stays true until the branch lands. Rewriting it
> early would leave the repo's most-trusted file describing a world that doesn't exist yet.

---

## Step zero: two things to verify in the editor before writing any code

This dev host has no s&box toolchain, and s&box bumps no version number we can gate on — it
ships weekly on Wednesdays and the news feed (`sbox.game/rss/news`) is the only signal. So the
gate is manual, and it is the first thing the branch does:

1. **`Http.RequestStreamAsync` returns before the body ends.** Our fix `42cee680` merged to
   `facepunch/sbox-public` master 2026-07-30 03:23Z — and
   `engine/Tools/SboxBuild/Steps/SyncPublicRepo.cs` shows the public repo is a **filtered mirror
   pushed from their internal master**, so being there means it is already in the branch they
   build from. The shipped signature is
   `RequestStreamAsync( uri, method = "GET", content = null, headers = null, ct = default )` —
   **headers included** — and `Stream`/`StreamReader` were already whitelisted. That was the
   *whole* engine blocker: CLAUDE.md's "two small upstream engine changes away" turned out to be
   **one**.
2. **`SHA256.HashData` is callable.** PKCE S256 needs SHA-256 on the client and **there is no
   SHA-256 implementation anywhere in `client/`** — despite `CLAUDE.md:1305` and two comments in
   `Code/Chess/ChessEngine.cs` (`:29`, `:382`) citing "SHA-256 is hand-rolled here" as
   precedent. That claim has no code behind it; do not go looking for something to reuse.
   `System.Security.Cryptography` **is** assembly-whitelisted
   (`engine/Sandbox.Access/Config/AccessRules.Assemblies.cs:33`, read 2026-08-05), so this
   probably just works — but assembly-level whitelisting does not prove the member-level ACL
   allows it. If it is blocked, hand-rolling SHA-256 is Sandbox-free and therefore provable in
   the dotnet harness against the RFC 7636 vector that `internal/lichess/oauth_test.go` already
   uses.

**Neither check is a formality.** If the first fails, the whole branch is blocked; if the second
fails, one extra file is needed before the link flow can exist at all.

---

## Why this exists

Playing a lichess game means holding a long-lived ndjson stream open, and lichess has no polling
substitute — they answer a poller with a literal *"Please don't poll this endpoint, it is
intended to be streamed"* 429. The s&box client could not read a stream, so **whoever read the
stream had to hold the token**, and that was gamchess. That was never a preference; it was the
engine, and CLAUDE.md's custody section documents at length what it cost us:

- Tokens are long-lived (~1 year), there are **no refresh tokens**, and **lichess has no bulk
  revoke** — `DELETE /api/token` kills one token and must be signed by *that* token. An RCE or
  DB dump handed over every linked player's account.
- We built envelope encryption (`internal/keyring`, KEK wrapping rotating DEKs) to buy a
  rotate-without-orphaning capability — while being honest in CLAUDE.md that **it adds no
  secrecy on this deployment**, since the KEK and the DB share a box.
- The audit sweep (`POST /api/token/test`, 1000 tokens/call) was the only fast incident lever we
  owned, and it can only *tell* us which tokens are live — it cannot revoke them.

All of that existed to manage a risk we were only taking because of an engine bug. **The bug is
fixed. The risk goes away rather than gets managed.** gamchess ends the branch holding no
lichess secrets at all.

---

## The six decisions (owner, 2026-07-31)

### ① PKCE, exchanged client-side

gamchess **keeps `/lichess/callback`** — `redirect_uri` must still match byte-for-byte between
authorize and token, and the client cannot listen on a socket, so there is no loopback escape.
But the callback stops being a token exchange and becomes a **parking slot**: it holds
`code` + `state` briefly, bound to the SteamID that started the flow, and the client collects it
FP-authed.

**The client holds the `code_verifier` and does `POST /api/token` itself.** A code without its
verifier is worthless, so **gamchess never sees a secret** — not in transit, not at rest, not in
a log.

This **inverts** the existing `pendingLinks` store in `internal/api/lichess.go` (a state → {
verifier, steamID } map, 10-minute TTL, mint-on-redirect, burn-on-callback, modelled on
`nonceStore` in `web_auth.go`). Keep the shape — mutex, lazy sweep on the write path, check-and-
burn in one method, in-memory because one container and a restart mid-link just means "click the
link again". Change the payload: the client registers `state` + its SteamID *before* showing the
authorize URL, and the slot fills with the `code` when lichess redirects.

**The state is still the credential on the browser hop, and it must still be burned on use.**
`lichessCallback`'s existing fail-closed handling of an unknown / expired / replayed state
(`lichess.go:214-224`) is exactly right and should survive verbatim: no detail to the caller,
because it is either a bug or an attack and neither deserves one.

### ② The token is stored on disk, with no opt-in and verbose copy instead

`FileSystem.Data`, alongside `Code/Game/PlayerData.cs` (the only current user). Not a prompt,
not a toggle — a clear statement of where the key lives.

**The risk was re-derived, not assumed** (this replaces the old, never-closed "rogue lobby host"
spike). Joining an **editor host or a local-project dedicated server** compiles and loads *that
host's* source into your process before the scene loads
(`GameInstanceDll.Network.cs:174`), and whitelisted C# is enough to read `FileSystem.Data` and
POST it anywhere. But a host running a **published** package sends no code at all
(`GameInstanceDll.cs:274`), and memory-only bought little regardless: anyone playing on lichess
is linked *that session*, and injected code shares the process. `FileSystem.Data` is per-package
under the org root (`OrganizationData.CreateSubSystem`).

**Do not misread lichess's "do not ship tokens to users" advice as forbidding this** — that is
about **hardcoded developer** tokens in a bundle. The spec explicitly blesses this model:
*"it is fine if the user themselves can extract `code_verifier`, which will always be possible
for fully client-side apps."*

The copy must say the token lives on this PC, and must keep naming **`/account/security`** — not
`/account/oauth/token`, which lists *personal* tokens only and hides app grants. That trap is
documented and the copy has always got it right; keep it right.

### ③ Clients talk to lichess, not gamchess

Delete the play relay and its long poll. Port `etiquette.go` to C#. See the teardown and build
lists below.

### ④ Abandonment moves to the client, and gets better

See "Abandonment" below — this is the part that closes PLAN No. 3.

### ⑤ Gut the custody machinery

`internal/keyring`, `lichess_key_versions`, the token columns, the audit endpoint,
`LICHESS_AUDIT_KEY`, `LICHESS_TOKEN_KEY` / `_OLD` / `_ROTATION_DAYS`. **gamchess ends with no
lichess secrets.** (`SESSION_SECRET` remains — it is optional and ephemeral-by-default — so say
"no lichess secrets", not "no secrets".)

### ⑥ Keep the archive, TV, and matchmaking

- **The archive** is untouched; clients archive lichess games too, exactly as they do now.
- **TV is untouched and must stay that way** — see below.
- **Matchmaking becomes rendezvous + directory**: mint the `client_game_id`, tell each seat the
  other's username. It stops being anything like a relay.

---

## The identity trap: the callback cannot verify who linked

**Re-derived from the live spec (`2.0.155`, read 2026-07-31). No cleverness fixes this.**

| | |
|---|---|
| `POST /api/token` | returns `{token_type, access_token, expires_in}` — **no user id** |
| `GET /api/account` | `security: [OAuth2: []]` — identity costs a token |
| `POST /api/token/test` | `security: []` (anonymous) — but **you must POST the token**, so gamchess can only introspect one it already holds |

**Verified lichess identity is exactly what custody was buying.** Giving it up is part of the
price, and the answer is not to buy it back.

> **Do NOT "fix" this with a second scopeless token.** `scope` is optional on authorize and a
> zero-scope token really does read `/api/account` — but that rebuilds the pile of long-lived
> credentials this whole change exists to delete, in exchange for a username.

**Do this instead: claimed on link, verified on first game.**

- The client POSTs `{id, username}` from its own `/api/account` as an **unverified claim**.
- gamchess **promotes it to verified** when an archived lichess game's
  **`GET /game/export/{id}`** (`security: []`, anonymous) shows that id as one of the two
  players — no token, no scope, riding a fetch the archive already makes.
- `GET /api/user/{username}/current-game` is **also `security: []`** if a live cross-check is
  ever wanted.

**Guards:** never mark one lichess id verified for two SteamIDs — `store.ErrLichessIDTaken`
(`internal/store/lichess.go:23`) is exactly this guard and should survive, now applied to a
claim; and **never render a claim as confirmed** in any UI.

**A false claim cannot impersonate.** The real account still has to accept on lichess, so a lie
produces *no game* — which is also why "verified on first game" is sufficient rather than merely
convenient.

---

## The flows, redesigned

The old flows were shaped by custody: gamchess held **both** seats' tokens, so it challenged
with White's and accepted by id with Black's, and it required **both seats to POST an intent**
for the same `client_game_id` before it would start anything. That two-intent rule was never a
formality — without it, any linked player could have dragged any other into a game from
anywhere, because gamchess could act as either of them.

**With client custody that premise is gone.** Each client acts with its own token and can only
ever commit itself. The authorisation story collapses into "you did it, with your own
credential".

- **Paired (the two seats at a table).** The seats exchange lichess usernames — which the lobby
  already has a place for, since a link's username is known client-side — and one client issues
  a **direct challenge to a named user** while the other accepts. The named opponent consenting
  on lichess *is* the authorisation. `client_game_id` stays as the rendezvous key.
- **Seek.** One caller, unchanged in shape: nobody else is being committed to anything. It needs
  the **event stream** (a real-time seek's response carries no game id — it is a stream of empty
  lines whose only job is to stay open).
- **Open / shareable link.** Unchanged in mechanism and still the subtlest one: create the open
  challenge **anonymously** (a `board:play` token 403s `POST /api/challenge/open`), then
  `POST /api/challenge/{id}/accept?color=` **with the player's token** to seat them, then
  publish the opposite colour's URL and watch the event stream. **Skipping the accept step is
  the bug that shipped in M8** — the creator's seat stayed empty and the game never started.

**The one-relayed-game-per-player gate now lives entirely client-side**, and its reason changes.
It was enforced server-side (`relay.Join`'s `hasOtherLivePlay`) plus a UI hint. The remaining
hard constraint is that **lichess's event stream is one-per-token** — so a client must never
open two. `SetupPanel`'s existing "playing at Table N, this table plays a local game" treatment
is the right UI and should stay.

**Traps that do not change and must be carried into the C# port** (all from CLAUDE.md, all
`[SOURCE]`-marked or spec-read — re-derive before trusting):

- **Two different `isBoardCompatible` functions with different thresholds**: challenges are
  `speed >= Blitz` (≥180s), seeks are `>= Rapid` (≥480s). `Code/Game/LichessTable.cs` already
  encodes both floors client-side and is already Sandbox-free — **it is the model for the whole
  port.**
- **A seek's `time` is MINUTES; a challenge's `clock.limit` is SECONDS.**
- **Omitting both clock fields** is how you ask for unlimited; sending `0/0` asks for a rejected
  0+0 clock.
- **`clock.limit` has a domain**: 0, 15, 30, 45, 60, 90, or any multiple of 60 up to 10800.
- **Send no `ratingRange` at all.** Omitted means "lichess centres a Gaussian band on your real
  rating", which is strictly better-informed than anything we could compute.
- **An offer POST always answers `200 {"ok":true}`** whether or not lichess took it — the only
  truth is the standing offer on the NEXT `gameState`.
- **A declined DRAW is invisible on the Board API; a declined takeback is not.** `GameHud`'s
  "Draw offered. Lichess won't signal a decline." (`GameHud.razor:160`) is a **lichess** fact,
  not a custody fact — it stays true and stays put.
- **Closing a challenge keep-alive stream does NOT withdraw the challenge** — POST an explicit
  `/cancel`.

---

## Abandonment: what actually improves, and what it costs

**Today:** `relay.watchAbandonment` (`internal/api/relay.go:1317`) tracks each seat's `lastPoll`
and, on a live game whose seat has been silent past `abandonTTL` (30s, checked every 10s,
deliberately well clear of `pollHold` 5s plus a reconnect/hotload grace), resigns **that** seat
with **that seat's** token. It exists because a game only ends on lichess when someone resigns,
flags, or draws — and a client that vanishes does none of those.

**After:** the client holds its own stream, so **dropping the stream is itself the signal**. On
the Board API the open stream *is* presence — which is precisely why PLAN No. 3 ("make a
roamed-away player's light go out") was stuck: gamchess held the stream open continuously, so
our relayed player was **always present** and `opponentGone` could never fire for them. That
row closes as a side effect. Note `opponentGone` is **currently ignored** on both sides
(`relay.go:1282`, `board.go:196`); inbound handling is new work, and it finally has something
truthful to report.

**What the client must do:**

| event | action |
|---|---|
| stand up / disengage | keep the game live (M17 behaviour) but cancel cleanly if the player is leaving the game, not just the seat |
| quit / scene teardown | dispose the stream — which closes the connection, because **the returned stream owns the response** |
| hotload | the stream must not survive a hotload half-alive; re-establish deliberately |
| hard crash | **nothing.** Nobody else holds the token. |

**The honest cost:** a hard crash can no longer be resigned within ~30s. The game flags — as it
does for every other Board API client. That is a real regression against today's behaviour and
should be stated plainly rather than buried, including in the player-facing copy that currently
promises the 30s resign.

---

## TV is untouched, and this is a hard boundary

`GET /api/tv/{channel}/feed` is `security: []` — anonymous. No token, no scope, nothing to
encrypt, revoke or audit. **None of the custody story applies to TV and none of it may creep
in**: TV must keep working for a player who has never linked and never will. A test already
asserts no `Authorization` header goes out (`internal/lichess/tv_test.go`,
`TestStreamTvSendsNoAuthorization`) — **that test must survive the branch.**

The proxy invariant stays too: **one upstream stream per CHANNEL**, ref-counted by connection
count with a `defer` decrement, swept after a short linger. 100 players on blitz cost lichess
one stream. Clients do **not** start reading lichess TV directly just because they now can.

> ### The teardown's sharpest trap
>
> `internal/lichess/tv.go` **depends on helpers that live in the files being deleted** —
> `apiBase`, `client`, `streamClient`, `stream`, `streamReq`, `maxStreamLine`, `guard`, and the
> `governor` / `agentTransport` from `etiquette.go`. Delete `oauth.go` and `board.go` naively
> and **TV stops compiling**.
>
> Those ~120 lines must be **relocated** — a new `internal/lichess/http.go` — as the **first**
> commit of the branch, before anything is deleted. `etiquette.go` survives in full: gamchess is
> still a lichess API client for TV, still one IP, still obliged to send a User-Agent and to
> back off for a full minute on any 429.

---

## The teardown list

Verified 2026-08-05. Line counts are for judging size, not for citing later.

**Deleted outright — server (~5,000 lines) plus its tests (~2,600):**

| File | Lines | |
|---|---|---|
| `internal/lichess/oauth.go` | 321 | PKCE, `Exchange`, `Account`, `Revoke`, `TokenTest` |
| `internal/lichess/crypto.go` | 132 | AES-256-GCM primitive |
| `internal/lichess/board.go` | 918 | the whole Board API — **but see the relocation trap above** |
| `internal/keyring/keyring.go` | 407 | KEK→DEK→token, rotation, re-encrypt sweep |
| `internal/keyring/ephemeral.go` | 117 | in-memory keyStore for tests |
| `internal/store/lichess.go` | 180 | `lichess_links` SQL — **keep `ErrLichessIDTaken`'s idea** |
| `internal/store/keys.go` | 98 | `lichess_key_versions` SQL |
| `internal/api/lichess.go` | 1033 | link flow + play/seek/challenge/open + audit |
| `internal/api/lichess_pages.go` | 277 | server-rendered consent/linked/error pages |
| `internal/api/relay.go` | 1554 | the relay engine, four flow drivers, abandonment |
| tests | ~2,600 | `oauth_test` 355, `crypto_test` 173, `board_test` 821, `keyring_test` 245, `api/lichess_test` 1005 |
| `/test.sh` (repo root) | 210 | exists **only** to validate key rotation — delete, don't edit |

**Routes deleted:** `POST /api/v1/lichess/play`, `/seek`, `/challenge`, `/open`;
`GET` and `DELETE /api/v1/lichess/play/{id}`; `POST /api/v1/lichess/play/{id}/{action}`;
`POST /api/v1/lichess/audit`.

**Routes that survive but change meaning:** `GET /lichess/link`, `GET /lichess/start`,
`GET /lichess/callback`, `POST /lichess/unlink`, `GET /api/v1/lichess`,
`DELETE /api/v1/lichess`. **Unlink is finally correct rather than best-effort** —
`DELETE /api/token` must be signed by that token, which only the client now holds, so the
client revokes and gamchess just forgets the row.

**Untouched:** `/health`, `/auth/steam/*`, `/api/v1/me`, `/api/v1/session`, `/api/v1/games*`,
both `/api/v1/tv/*`, and all of matchmaking + relaygame.

**Config — gamchess ends with none of these:** `LICHESS_TOKEN_KEY`, `LICHESS_TOKEN_KEY_OLD`,
`LICHESS_KEY_ROTATION_DAYS`, `LICHESS_AUDIT_KEY`. Read in `cmd/server/main.go`; minted by
`server/Makefile`'s `keys` / `keys-note` targets, which `up`, `rebuild` and `testinst` all
depend on; documented across ~60 lines of `server/.env.example`; passed through both
`docker/docker-compose*.yml`. **`PUBLIC_BASE_URL` stays load-bearing** — it still derives the
byte-for-byte `redirect_uri`, and it is still what keeps the test instance pointing at itself.

---

## The migration, and the revoke sweep that runs first

**Order of operations, and the order matters:**

1. **Revoke sweep, before anything is dropped.** A one-shot command walks every stored token
   through lichess's `DELETE /api/token`, each signed by itself — the only way it can be done,
   since there is no bulk revoke. It reuses `lichess.Revoke` and the store, both of which this
   branch deletes, so **the sweep must run while that code still exists**: land it as a
   throwaway command early in the branch, run it against prod, then delete it with the rest.
   `Revoke` already treats a 401 as success, which is the right behaviour for a token the player
   revoked themselves.
2. **Then the migration.** A new migration drops `lichess_key_versions`, and drops or reshapes
   `lichess_links` — `token_enc`, `token_nonce`, `scopes` and `key_version` all go; what remains
   is the claimed-vs-verified lichess id. `00002` and `00003` **stay in the tree**: goose keeps a
   version ledger and removing applied migrations breaks existing databases.
3. **Players re-auth.** Deliberate and unapologetic. Everyone links again, and the new link puts
   the token on their own machine.

**Why the sweep is worth the ops step even though we're re-authing anyway:** dropping the rows
does not revoke anything. Without the sweep, every currently-linked player keeps a live,
year-long grant issued under our `clientOrigin`, revocable only by them, for an app that can no
longer use it. We would be leaving our own mess on other people's accounts.

**One-way after deploy**, as ever: recover from a pre-deploy DB backup, never by reverting the
binary alone.

---

## The build list (client)

**The shape of it:** `Code/Api/LichessApi.cs` (279 lines) is today a thin URL table over
gamchess — its own header says *"Every call here goes to gamchess, never to lichess."* It
becomes a real lichess client: the C# port of the useful half of `board.go`.

**Three facts about `RequestStreamAsync`, read from the shipped engine 2026-08-05**
(`engine/Sandbox.Engine/Utility/Web/Http.Requests.cs:65`, tests at
`engine/Tests/Sandbox.Test.Engine/Web/HttpStream.cs`). Each one changes the design:

1. **The returned stream OWNS the response — "dispose it or the connection stays open."** The
   old bug was disposing the response early and getting away with it because the body was
   buffered; the fix wraps both in a `ResponseStream`. So the game-stream lifecycle is a
   **disposal discipline**, and `Code/Game/LichessTvSource.cs` (624 lines, the client's only
   long-lived connection today) is the model to copy: backoff, reconnect, and
   **unhook-before-Dispose**.
2. **`HttpClient.Timeout` does NOT bound the body read** — pass a `CancellationToken` to bound
   how long you are willing to read. This is the **opposite** of `GamchessApi`'s 8s
   `CancelAfter`: a game stream is bounded by the *game's life*, cancelled on stand-up /
   disengage / teardown, never by a timeout.
3. **A non-2xx throws `HttpRequestException`; a disallowed URI or header throws
   `InvalidOperationException`.** The house style is `GamchessApi.Result`, which never throws.
   The lichess client must **convert** to that shape, not inherit it.

**What to port from `etiquette.go`, and what not to:**

- **Port the User-Agent, verbatim in spirit.** `etiquette.go:38` names the project, a URL and a
  contact, and **lichess records a `userAgent` per access token** — it is how they attribute
  traffic and how they reach us, which is exactly what a future conversation about limits needs.
  Do not make it generic. Send it on **every** request, streams included, via one seam so no
  call site can forget.
- **Port the 429 rule.** "Wait a full minute before resuming API usage" — a 429 anywhere stops
  everything for 60s. Also port "only make one request at a time".
- **The seek self-limit changes premise.** It was 5/min because that is lila's per-IP limit
  (`Limiters.setupPost`) and **our whole playerbase shared one IP**. Now each player spends
  their own. A per-client limiter is still worth keeping — refusing locally with a legible
  reason beats earning a 429 — but it is no longer a shared budget, and PLAN No. 8's premise
  ("5/min for the entire playerbase") dies with it.

**Other client work:**

- `Code/Game/LichessGameController.cs` (1200 lines) stops long-polling gamchess every ~5s and
  rebuilds-from-a-move-list, and instead holds the game stream. Its local clock countdown, its
  never-read-HIGH discipline, and its archive-on-finish all stay.
- **The spectator mirror survives untouched** — `MirrorMoves` / `MirrorLive` are
  `[Sync(FromHost)]` fields fed by the **participant's own observations**, not by gamchess, so
  non-participants keep seeing lichess games and the path gets *more* direct.
- The token at rest goes beside `Code/Game/PlayerData.cs`. Note `GamchessApi`'s `_session` and
  `GamchessAuth`'s `_token` stay **memory-only** — that rule is about gamchess credentials and
  is unaffected.
- **The lichess calls must not inherit the gamchess circuit breaker.** `GamchessApi`'s static
  `_breaker` exists so a dead gamchess costs one timeout rather than one per call; lichess being
  slow must not open the breaker on our own backend, or vice versa.
- **Add `https://lichess.org/` to `client/gambit.sbproj`'s `HttpAllowList`.** It gates nothing —
  the engine has no per-package host allowlist, and CLAUDE.md is emphatic that diagnosing
  against it is diagnosing a mechanism that does not exist — but it is kept as a **declaration
  of intent**, and the client is about to acquire a second host it means to talk to.

**Prove what can be proved here.** `dotnet` 10.x lives at
`~/.local/share/toolchains/dotnet10/` (not on the default PATH). The repo's own precedent for
pulling logic out from behind Sandbox so it can be executed is long: `TimeControl.cs`,
`CapturedMaterial.cs`, `BoardDiff.cs`, `MoveSpeech.cs`, `HalfRise.cs`, and the `SeatAim` shim.
**`LichessTable.cs` is the closest precedent of all** — lichess's speed floors, already
Sandbox-free, already harness-checked against every preset in `TimeControl.All`.

Harness-able: clock-domain and speed-floor validation, the ndjson frame decode, the PKCE
verifier/challenge (against RFC 7636's vector), the `gameState` offer-flag reading. **Not**
harness-able: the stream lifecycle, cancellation, hotload behaviour — all editor work.

> **The size risk, stated plainly:** this ports ~900 lines of Go into a codebase that **cannot be
> compiled on this host**. Expect a real fixup pass on first open in the editor, and push
> early rather than accumulating an unverifiable pile.

---

## What this closes, and what it costs

**Closes:**

- **PLAN No. 3** (signal that a roamed-away player is away) — a dead client drops its own
  stream, so lichess's native presence and `opponentGone` finally tell the truth. No outbound
  "away" signal is needed; the row was stuck *because* gamchess held the stream open forever.
- **PLAN No. 4** (does the long poll hold up under real latency) — the game relay's long poll is
  deleted outright, and TV already moved to a WebSocket push in M18.

**Costs, honestly:**

- **A hard crash can no longer be resigned within ~30s.** The game flags, as it does for every
  other Board API client. Nobody else holds the token.
- **Verified lichess identity is gone**, replaced by claimed-on-link / verified-on-first-game.
- **The seek self-limit stops being a shared 5/min** — which is mostly an improvement, but it
  means PLAN No. 8's "bring lichess real numbers about a playerbase-wide limit" argument no
  longer describes reality.

---

## The doc and copy surface — all of it, in the same branch

Player-facing copy is part of the change, not housekeeping. A change here that doesn't update
these **ships a lie**, and the only person who finds out is a player reading the front door.

**Copy:**

- **`client/Code/UI/Screens/InfoScreen.razor`**, the `StationKind.Lichess` branch. The section
  headed **"WHERE THE KEY LIVES"** (line ~83) currently reads *"On our server, encrypted. It has
  to: playing a lichess game means holding a live connection open for the whole game, which this
  client can't do — so our server plays on your behalf while you sit here."* **Every clause of
  that becomes false.** It must say the token lives on this PC, keep naming `/account/security`,
  and keep the two load-bearing warnings (a password change does NOT unlink; `/account/oauth/token`
  does NOT list this grant).
- `client/Code/UI/LichessBoardPanel.razor` — the east-wall mirror of that page.
- `client/Code/UI/GameHud.razor` — the draw-decline line at `:160` is a lichess fact and stays;
  check the rest, and add whatever the new abandonment reality requires.
- `client/Code/Api/GamchessCommands.cs` — `gambit_lichess` prints custody guidance.
- `client/Code/UI/SetupPanel.razor` — the one-lichess-game-at-a-time explanation.
- `server/internal/api/lichess_pages.go` — the browser consent/disclosure page. Whether a
  rendered page survives at all depends on the new handoff; if one does, its **disclosure copy
  is load-bearing and must not be trimmed** (PLAN No. 7 says so already).

**Docs:**

- **`README.md`** — the contract section is the one place the wire format is written down. The
  lichess endpoint table, the "client holds no lichess token and speaks no lichess protocol"
  prose, the envelope-encryption paragraph, the "both seats must POST" rationale, the
  "rate limits are shared because gamchess is one IP" paragraph, the long-poll paragraph, and
  the Stack row that says "gamchess relays the Board API". **Also close the known gap**: the
  endpoint table is *already* missing the eight M19 matchmaking/relaygame routes registered in
  `router.go:157-168`.
- **`CLAUDE.md`** — the whole custody section, the incident-response lever table, the
  "client cannot read a stream" correction, and the deployment-facts KEK runbook. **Held until
  the branch actually ships.** Fix `:77`'s "two small upstream engine changes away" (it was one)
  and `:1305`'s "SHA-256 is hand-rolled here" (there is no such code) at the same time.
- **`MATCHMAKING.md`** — its analogies point at `relay.go` and `LichessGameController` as
  structural templates. Both change; the file needs a pass so it stops pointing at deleted code.
- `server/.env.example`, `server/Makefile`, `server/docker/docker-compose*.yml`.
- **`PLAN.md`** — No. 0 becomes a pointer to this file (already done); No. 3 and No. 4 are
  deleted when they close; No. 8's premise is rewritten.
