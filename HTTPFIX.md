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

3. **`Sandbox.Http` lets us SET the `User-Agent` header rather than overriding it.** lichess
   records a `userAgent` per access token; sending one is our standing obligation and the only
   reason they can attribute or reach us. On the server a `RoundTripper` guarantees it
   (`etiquette.go`'s `init()`); the client has no such mechanism, only a per-call dictionary.
   **If the engine refuses the header, we cannot keep the obligation** — that is a day-one
   blocker, not a polish item, so check it in the same sitting as the other two.

**None of these is a formality.** If the first fails the branch is blocked; if the second fails
one extra file is needed before the link flow can exist; if the third fails, stop and talk to
lichess before building anything.

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

> **Keep `/lichess/link` as the constant the board copies — do NOT show the raw authorize URL.**
> This is the call most likely to be got wrong, because showing the lichess URL directly looks
> simpler. `LichessApi.LinkUrl` is safe *precisely because it is a constant with no secret in
> it*: it is Steam-web-session gated, so whoever opens it links **their own** accounts, and
> handing it to a friend just links the friend. A raw authorize URL is bound to **your** state
> and **your** SteamID — a friend who opened it would consent on *their* lichess account and
> **you** would end up holding a `board:play` grant on it. That is strictly worse than today.
> Keeping the page in the middle is also what preserves the load-bearing disclosure copy and the
> byte-exact `redirect_uri`.

**The five steps.** ① `POST /api/v1/lichess/link/start` (FP/session authed, body
`{code_challenge}`) mints the state, stores the slot, evicts any older slot for that SteamID
(newest wins), and returns `{state, authorize_url, redirect_uri}` — the server builds the
authorize URL so `redirect_uri` derivation stays server-side and byte-exact. ② The player opens
the constant `/lichess/link`, which finds their slot **by their Steam session's SteamID** and
shows the disclosure + Continue. ③ `/lichess/callback` parks `{code}` and — unlike today —
**does not burn the state**, because burning here makes collection impossible; it refuses if a
code is already parked, since a browser refresh replays a spent code. ④
`POST /api/v1/lichess/link/collect`, keyed on the **caller's authenticated SteamID** and never
on a state from the body, answers `none` / `waiting` / `ready` — and `ready` **burns the whole
slot atomically**. Burn-on-use moves from callback to collect. ⑤ The client exchanges at
lichess using **the `redirect_uri` gamchess returned** (a hardcoded one silently breaks the test
instance), reads `/api/account`, and POSTs its claim.

**The client must return `redirect_uri` from the server, not hardcode it** — this is the same
reason `steamReturnURL()` is derived once, and it is what keeps `testchess` pointing at itself.

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

### RESOLVED (owner, 2026-08-05): verify at link with `POST /api/token/test`

**This supersedes the claimed-on-link / verified-on-first-game design below.** Re-derived from
lila master 2026-08-05 — `app/controllers/OAuth.scala`:

```scala
def testTokens = AnonBodyOf(parse.tolerantText): body =>
  val bearers = Bearer.from(body.trim.split(',').view.take(1000).toList)
  ... Json.obj("userId" -> t.userId, "scopes" -> ..., "expires" -> t.expires)
```

**`AnonBodyOf` = no authentication**, and it returns **`userId`** per token. So gamchess can
learn a token's authoritative owner **holding no credential of its own**. The catch is inherent:
you must POST the token, so the token transits gamchess once.

**The flow:** at link, the client exchanges the code itself (it still holds the verifier), then
POSTs its fresh token to gamchess over the FP-authed channel. gamchess calls `token/test`,
records the returned `userId` as **verified**, and discards the token. The token still lives
only on the player's disk.

**The tradeoff, stated honestly:** "gamchess cannot hold your token" drops from a *structural*
guarantee to a *promise* — it sees the token for the length of one call. The owner's call
(2026-08-05) is that this is acceptable: token compromise is not a risk we are optimising
against. **But note what still bites us and not the player:** lichess records `clientOrigin` per
token, so abuse of stolen Gambit tokens is attributable to Gambit, and their lever is killing
the whole app on that origin. That is an argument for not logging the token, not for refusing
the design — so: never log it, never persist it, never put it in an error string.
`GamchessApi.Redact` is the existing precedent.

**What this collapses** — all of it becomes unnecessary:

- the claimed-vs-verified split (every row is verified at link),
- the partial unique index (a plain `UNIQUE(lichess_id)` is safe again, because the id is now
  authenticated rather than asserted),
- the `games.lichess_game_id` plumbing, which existed *only* to drive promotion,
- the promotion path and its `game/export` parsing,
- the open question about promoting only from paired games.

**Keep anyway:** the paired-flow guard (Black accepts only a challenge whose challenger id
matches the id gamchess published for the other seat). It costs nothing and it is defence in
depth against a bug rather than against a liar.

**Rate limit note:** `testTokens` is IP-limited with `cost = bearers.size`, and gamchess is one
IP — trivial at one token per link, but do not batch-verify in a loop.

---

### Superseded: the claimed-on-link design (kept for the reasoning, not the plan)

The following was the design before `token/test` was re-derived. It is retained because the
*constraints* it documents are still true and still worth knowing — in particular why the
callback cannot identify anyone.

**Claimed on link, verified on first game.**

- The client POSTs `{id, username}` from its own `/api/account` as an **unverified claim**.
- gamchess **promotes it to verified** when an archived lichess game's
  **`GET /game/export/{id}`** (`security: []`, anonymous) shows that id as one of the two
  players — no token, no scope, riding a fetch the archive already makes.
- `GET /api/user/{username}/current-game` is **also `security: []`** if a live cross-check is
  ever wanted.

### The plumbing this needs and does not have

**The archive carries no lichess game id.** Verified 2026-08-05: `gamePost`
(`internal/api/games.go:37`) has no such field, the `games` table (`migrations/00001_schema.sql`)
has no such column, and `BuildArchivePgn` sets `Site = lichess.org` but never the id. **The whole
verification story cannot run until this is added** — three small pieces, easy to miss because
nothing fails without them:

- a migration adding `games.lichess_game_id TEXT`,
- `gamePost.LichessGameID`,
- `LichessGameController.TryArchiveFinished` passing the game id it already holds.

The fetch itself already exists and survives: `internal/lichess/tv.go:303` does exactly this
anonymous `game/export` call through the governor for TV results. **Copy it; do not write a
second one.** *(Unverified: whether `players[].user.id` comes back under the query params
`tv.go` uses — it asks for `moves=false&opening=false&clocks=false&evals=false&literate=false`.
Re-derive from the live spec before writing the parser.)*

### The DB shape, and the index that must NOT be carried over

```sql
CREATE TABLE lichess_accounts (
    steam_id    BIGINT PRIMARY KEY REFERENCES players(steam_id),
    lichess_id  TEXT NOT NULL,
    username    TEXT NOT NULL,
    verified    BOOLEAN NOT NULL DEFAULT FALSE,
    claimed_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    verified_at TIMESTAMPTZ
);
CREATE UNIQUE INDEX lichess_accounts_verified_id
    ON lichess_accounts (lichess_id) WHERE verified;
```

**The partial index is the whole trick.** `00002`'s plain `UNIQUE(lichess_id)` must **not** be
carried over. Under custody the id came from a token, so uniqueness was free and safe. A *claim*
is unauthenticated — a hard `UNIQUE` would let anyone squat a real user's id and permanently
lock them out of ever linking. The partial index enforces "never one lichess id **verified** for
two SteamIDs" while letting N SteamIDs harmlessly *claim* the same id. On promotion, an index
conflict means "already verified for another Steam account": leave the claim unverified and
**never steal**. Re-claiming a different id resets `verified` to false.

`store.ErrLichessIDTaken` (`internal/store/lichess.go:23`) is the right error to keep — it now
fires on promotion, not on claim.

### Verification is weaker than it looks — and one guard is mandatory

"This lichess id appears in this game" does **not** prove "this SteamID is that lichess id": a
liar can point at any public game the real account played. The defence that a lie produces *no
game* is sound, **but it only holds for the paired flow**, where the challenge genuinely has to
be accepted by that account. A seek or an open game has no such property.

> **Owner decision needed:** promote to `verified` only from a **paired** game whose export
> shows *both* seats' claimed ids. That makes verification mean something; solo flows would
> leave a claim unverified forever, which is fine because nothing depends on it.

**The guard that is not optional.** The paired flow **challenges by username**, so a false claim
sends a real lichess challenge to a real, innocent third party. Today that is impossible. Three
guards, and the second is the one that actually closes it:

1. `SetupPanel` shows the opposite seat's claimed name with an explicit **unconfirmed** marker
   before either seat commits.
2. **Black accepts only a challenge whose challenger id matches the claim gamchess published for
   the other seat.** Cheap, strong, and it is a rule, not a suggestion.
3. **Never render a claim as confirmed** — `LichessBoardPanel.razor`'s "✓ linked as {Username}"
   becomes "linked as X (unconfirmed)" until `verified`.

---

## Scopes: `board:play` is no longer necessarily the only one

**The standing rule changes here.** CLAUDE.md says `board:play` is the only scope we ever
request, and one of its two reasons was that *a scope change forces a full re-link for everyone*
(tokens are long-lived, there are no refresh tokens). **This branch already forces that re-link**
— so that cost is paid, and adding scopes is free on that axis. This is the one moment in the
project's life when widening scope costs nothing extra, which is exactly why it is being decided
now rather than later.

The other reason — blast radius — is an **explicit owner decision as of 2026-08-05**: token
compromise is not what we are optimising against, and more permissions mean more features.

**The authoritative scope list**, re-derived from lila master 2026-08-05
(`modules/oauth/src/main/OAuthScope.scala`): `preference:read|write`, `email:read`,
`challenge:read|write|bulk`, `study:read|write`, `tournament:write`, `racer:write`,
`puzzle:read|write`, `team:read|write|lead`, `follow:read|write`, `msg:write`, `board:play`,
`bot:play`, `engine:read|write`, `web:mobile`, `web:polygon`, `web:mod`.

**Two things do not change:**

- **`web:mobile` and `web:polygon` stay out**, and for a different reason than the rest — not
  risk, but honesty. Their own descriptions are "Official Lichess mobile app" and "Take Take
  Take"; taking one means claiming to be a first-party client to bypass a gate lichess put on
  third-party board clients deliberately. PLAN No. 12 records this as decided. `web:mod` is
  moderator tooling and is not ours to ask for.
- **The disclosure copy enumerates what we CANNOT do, so every added scope invalidates a
  clause.** `InfoScreen.razor`'s Lichess branch currently promises the token "cannot read your
  email, read or send messages, see who you follow, or change your account". Add `email:read`,
  `msg:write` or `follow:read` and that sentence becomes a lie. **The scope set and the
  disclosure copy must be decided together**, in this branch, and the copy must list what we
  now *can* do rather than reciting a shorter list of what we can't.

### The scope set (owner, 2026-08-05)

**`board:play` + `puzzle:read` + `puzzle:write` + `follow:read` + `msg:write`.**

- **`puzzle:read|write`** — puzzles in the lobby (a wall board, or a puzzle at a table between
  games), solved against the player's real lichess record. Self-contained; touches no social
  surface.
- **`follow:read`** — which lichess friends are online.
- **`msg:write`** — message an opponent you just played. **A feature, not the identity
  mechanism** (see below).

**The disclosure copy must be rewritten in the same breath**, because it currently promises the
opposite: *"It cannot read your email, read or send messages, see who you follow, or change your
account."* With this set, two of those four clauses become false. The new copy states what the
grant **can** do, in plain terms, rather than reciting a shorter list of what it can't. This is
the highest-risk copy in the change — it is the sentence a cautious player reads before
consenting.

### Recorded, not to fix: two verification schemes that were considered and rejected

Both look like solutions. Neither beats `token/test`, and both will be re-invented by someone
who hasn't read this.

1. **Persistent-state proofs — "join our team", "follow us"** (`team:write` / `follow:write`,
   checked via the anonymous `GET /api/team/{teamId}/users`). **Replayable, therefore not a
   proof.** Once the real account is a member, any liar claiming that id passes the identical
   check, because gamchess only ever observes a persistent boolean. A proof must bind a fresh
   nonce or demonstrate possession of a fresh secret.
2. **Nonce-by-message to a Gambit account** — the client `msg:write`s a secret to an account we
   own; the same secret goes to gamchess over the FP-authed channel; gamchess matches them.
   **This one genuinely verifies** — the nonce is fresh and only the token holder could have
   sent it as that account, so it is not replayable. It is rejected on cost, not correctness:
   - **Reading the inbox means authenticating as the receiving account**, so gamchess must hold
     a long-lived lichess credential again — decision ⑤ inverted. Worse than per-player custody
     in one specific way: it is a **single shared credential**, so its compromise is an app-wide
     event rather than one player's problem, and no player can revoke it.
   - **There is no `msg:read` scope** — the scope list has only `msg:write`. The read routes
     (`GET /inbox/:username`, `controllers.Msg.convo`, lila `conf/routes` 2026-08-05) are **web
     endpoints with no documented `security:` contract**, so they are plausibly session-only and
     can change without notice. **[SOURCE]**
   - It needs an account we own and maintain, and it puts a nonce in a real inbox.

   `token/test` buys the same proof anonymously, with no account, no secret, and no extra scope.

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

  **The two-intent rule survives, but its justification changes and must be restated.** It
  existed because gamchess held everyone's tokens; it can no longer stop anyone being dragged
  anywhere, because a one-sided start now just leaves a challenge sitting in someone's
  notifications. What it still does is a **directory-disclosure rule**: gamchess must not hand
  out a player's lichess username to whoever asks, so it reveals seat B's claimed username to
  seat A only once **both** seats have posted an intent for the same `client_game_id`, and only
  to those two. `LichessApi.Play`'s current doc-comment ("two independently-authenticated
  intents are what make it consent") becomes false and must not be carried over.

  **How Black learns the challenge id — the thing a next session will get wrong.** The reflex is
  "Black opens the event stream and waits for a `challenge` event". **Don't.** White's challenge
  response carries the id, both seats are in the *same s&box lobby*, and the station already
  `[Sync]`s `client_game_id` — so `[Sync]` the challenge id and let Black accept by id. That
  preserves exactly the property `relay.go:1233` documents today: the paired flow **never
  watches `/api/stream/event`, and so is not bound by the one-event-stream-per-token rule**.
  That was worth having when a server held the stream; it is worth much more now the client
  does. Event stream as fallback only.
- **Seek.** One caller, unchanged in shape: nobody else is being committed to anything. It needs
  the **event stream** (a real-time seek's response carries no game id — it is a stream of empty
  lines whose only job is to stay open).
- **Open / shareable link.** Unchanged in mechanism and still the subtlest one: create the open
  challenge **anonymously** (a `board:play` token 403s `POST /api/challenge/open`), then
  `POST /api/challenge/{id}/accept?color=` **with the player's token** to seat them, then
  publish the opposite colour's URL and watch the event stream. **Skipping the accept step is
  the bug that shipped in M8** — the creator's seat stayed empty and the game never started.

**The one-relayed-game-per-player gate now lives entirely client-side**, and its reason changes.
It was enforced server-side (`relay.Join`'s `hasOtherLivePlay`) plus a UI hint; gamchess can no
longer know. `SetupPanel`'s existing "playing at Table N, this table plays a local game"
treatment is the right UI and should stay, and `LobbyPlayer.LichessGameElsewhere` still covers
the in-lobby case — but **the gate becomes advisory**, since it cannot see a second s&box
instance.

> ### ONE EVENT STREAM PER TOKEN — make it a process-wide singleton
>
> `board.go:277` already records the symptom: a clean EOF means lichess closed it — game over,
> **or the token opened a second event stream elsewhere.** With client custody, a second table,
> a second s&box instance, or a hotload orphan will silently kill the first stream, and the flow
> depending on it hangs with **no error**.
>
> So the event stream must be **one process-wide owner with refcounting**, never one per
> `LichessGameController`. This is not optional, and it is also the backstop that makes the
> advisory gate above tolerable: lichess's own rule enforces a partial version for free, and a
> singleton is what lets us *notice* rather than break silently.
>
> **And a stream failure must never degrade into a poll** — lichess answers a poller with a
> "please don't poll" 429. Exponential backoff (3s → 6s → 12s, cap 60s), and **never reconnect
> the game stream once lichess has reported the game finished**.

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

> **But it does NOT close PLAN No. 3 for free, and row 0 currently overclaims.** No. 3's actual
> wish is that a player who **roams away** has their light go out. A roaming player is still
> running the client and still holding the stream, so lichess still sees them present. It closes
> only if the client **drops the game stream on stand-up** — a product decision, not a
> consequence of the architecture.
>
> **Owner decision needed:** drop the stream ~10s after stand-up (a grace, so a trip to the
> fridge doesn't flap it) and re-open on sit-down. A reconnect's `gameFull` carries the whole
> move list, so nothing is lost. With it, No. 3 genuinely closes; without it, No. 3 stays open.
>
> Also flagged: **that `opponentGone` fires for a Board API opponent who closes their game
> stream is inferred, not read.** It is the single most load-bearing lichess fact in this
> design, and PLAN No. 3 already says to re-derive from lila master before building anything.
> Do that, and **do not state a claim-window duration** without reading it.

**What the client must do:**

| event | action |
|---|---|
| stand up / disengage | keep the game live (M17 behaviour) but cancel cleanly if the player is leaving the game, not just the seat |
| quit / scene teardown | dispose the stream — which closes the connection, because **the returned stream owns the response**. **Do not auto-resign**: a quit is not a stated forfeit. Offer an explicit "resign and leave" in the HUD instead |
| hotload | the stream must not survive a hotload half-alive; generation-counter guard, re-establish deliberately |
| hard crash | **nothing.** Nobody else holds the token. |

Cancel **before** dispose and null the reference **before** the task can observe it — the same
discipline as `LichessTvSource.DisposeSocket`'s "unhook BEFORE Dispose".

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
> `internal/lichess/tv.go` **depends on helpers that live in the files being deleted**, and they
> span **both** of them — `apiBase`, `client`, `maxBody` are in `oauth.go` (`:49`, `:85`, `:89`);
> `streamClient`, `maxStreamLine`, `stream`, `streamReq`, `APIError`, `truncate` are in
> `board.go` (`:33`, `:37`). Delete either naively and **TV stops compiling**.
>
> Worse, **`etiquette.go` is not self-contained either**: its `init()` (`:157-162`) assigns
> `client.Transport` and `streamClient.Transport`, so it references symbols in both deleted
> files.
>
> Those ~120 lines must be **relocated** — a new `internal/lichess/http.go` — as the **first**
> commit of the branch, a pure move with zero behaviour change, before anything is deleted.
> Keep `apiBase` a **`var`**, not a `const`: `tv_test.go` repoints it at an `httptest` server.
>
> **Compile failure is the good outcome here.** The bad outcome is the tempting fix — dropping a
> fresh `&http.Client{}` into `tv.go`. That compiles, TV appears to work, and it silently strips
> the User-Agent off every TV request (breaking the attribution obligation) and stops TV's 429s
> from arming the governor. **Put a comment in `http.go` saying exactly that.**
>
> `etiquette.go` otherwise survives in full: gamchess is still a lichess API client for TV, still
> one IP, still obliged to send a User-Agent and to back off for a full minute on any 429.

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

**Two things the teardown must not disturb:**

- **`internal/api/tv_test.go` reads `../../../client/Code/Game/LichessTv.cs`** (`:183`) to hold
  the server's channel allowlist and the client's list to each other so they cannot drift
  silently. It must keep passing across the client reshuffle — it is a cross-repo-half test and
  the kind that gets "fixed" by deletion.
- **gamchess deploys before the s&box package updates**, so for a window a shipped OLD client
  will 404 on `/api/v1/lichess/play`. `LichessGameController.Poll` already handles a 404 by
  clearing and reporting, which is acceptable degradation — but **keep the JSON field names
  `linked` and `username` on `GET /api/v1/lichess`** so an old `LichessLinkState.Fetch`
  deserializes rather than nulling out. The branch is otherwise all-or-nothing on deploy: state
  that in the PR body.

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
   > **The ordering trap, and it is a sharp one: migrations run in-process at boot via goose.**
   > If the drop ships in the same binary as the teardown, **goose drops `lichess_links` before
   > anyone can revoke anything.** The sweep is therefore an **operator step run against the
   > OLD binary's database, with the KEK still present** — not a boot hook, and not something
   > the deploy does for you. Say so in the PR body, in the tool's own `--help`, and here.
   > It should also be **serial and resumable**: a single 429 arms the governor's 60s
   > process-wide backoff, so it will pause, and it must be safe to re-run.

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
- **The seek self-limit keeps its number and loses its reason.** It was 5/min because that is
  lila's per-IP limit (`Limiters.setupPost`, **[SOURCE]** — re-derive) and **our whole
  playerbase shared one IP**. Now each player spends their own. Keep 5/min anyway: a player
  mashing the button now earns a 429 that arms *their own* 60s stop-everything, so refusing
  locally with a legible reason is still strictly better — and a household, LAN party or NAT
  still shares an IP, where 5/min per client is not conservative. **Delete every string saying
  the budget is shared by the whole playerbase** (`LichessApi.Seek`'s doc-comment, PLAN No. 8,
  CLAUDE.md's etiquette section).
- **Do NOT port "only make one request at a time" as a global mutex** — it would serialise a
  move behind a held stream. It is advice about request rate, not a lock.
- **The structural loss worth naming:** `agentTransport` is a `RoundTripper`, which is *why* no
  call site can forget the User-Agent. s&box has no equivalent — headers are a per-call
  dictionary. Replace the guarantee with a **single seam**, exactly as `GamchessApi.Send` is the
  only `Http.RequestAsync` call site in the client today. Everything else builds a request
  *description* and hands it over; that is also what makes the builders harness-provable.

> ### The one thing `LichessTvSource` does NOT model: thread affinity
>
> `Sandbox.WebSocket`'s `OnMessageReceived` hands you the message on the game thread. A raw
> `Stream` read completes on a **thread-pool** thread. So the read loop may only touch a plain
> latest-wins slot; **every `Scene` / `[Sync]` / `GameObject` touch must happen in `Pump()` from
> `OnUpdate`.** `LichessTvSource` gets this for free and therefore teaches nothing about it.
>
> **Hotload is the matching hazard.** An orphaned read task can leave a *live HTTP connection to
> lichess*, which on the Board API means both "still present" and "your event stream slot is
> taken" — a leak that fails silently. Guard the loop with a **generation counter** checked
> every line and bumped on every start, so an orphan exits on its next read.

**Delete the staleness apparatus wholesale — do not port it.** `LichessGameController` carries
`_version` (labelled, in the code, "long-poll cursor"), `_bankLag`, `_lastRoundTrip`, plus the
`clock_age_ms` / `hold_ms` reconciliation. **A stream has no hold to measure and no cursor to
reconcile**, exactly as M18 found when TV moved off its long poll — that migration deleted the
same machinery for the same reason, and it is the precedent to cite. Leaving it in against a
stream **reintroduces the M11 sawtooth**, where the clock ticked down and then jumped back *up* —
the one direction the house rule forbids.

**Other client work:**

- `Code/Game/LichessGameController.cs` (1200 lines) stops long-polling gamchess every ~5s and
  rebuilds-from-a-move-list, and instead holds the game stream. Its local clock countdown, its
  never-read-HIGH discipline, and its archive-on-finish all stay.
- **The spectator mirror survives untouched** — `MirrorMoves` / `MirrorLive` are
  `[Sync(FromHost)]` fields fed by the **participant's own observations**, not by gamchess, so
  non-participants keep seeing lichess games and the path gets *more* direct.
- **The token gets its OWN file — not `player.json`.** `PlayerData._cache` is a static shared
  object serialized whole on every settings write, so a token riding inside it will eventually
  land in a log or a dump. A separate `lichess.json` also makes "forget my token" a single file
  delete. `GamchessApi`'s `_session` and `GamchessAuth`'s `_token` stay **memory-only** — that
  rule is about gamchess credentials and is unaffected.
- **`LichessLinkState` should stop polling gamchess for "am I linked".** With the token on disk
  that is answerable locally, instantly, and offline. The 3s poll should only fetch the
  claim/verified state, which is advisory.
- **Don't port `ChallengeKeepAlive`** (`board.go:537`, ~60 lines). Its only benefit is lichess's
  15s ping, its trap has already bitten once (closing the stream does **not** withdraw the
  challenge), and an explicit `/cancel` on a plain buffered challenge is strictly simpler.
  Dropping it removes an entire third stream shape from the client.
- **The anonymous-create trap.** A single-seam client attaches `Authorization` by default, and
  `board:play` **403s** `POST /api/challenge/open`. The seam needs an explicit anonymous mode,
  and the harness must assert no `Authorization` on that builder — re-creating the invariant
  `board_test.go`'s `TestOpenChallengeIsAnonymous` holds today and which is otherwise **simply
  deleted** with the Go.
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
  is load-bearing and must not be trimmed** (PLAN No. 7 says so already). Three lines are
  **new**: the token is stored on this PC and gamchess never sees it; playing from another PC
  means linking again there; and **deleting the game's data removes the token from this PC but
  does not revoke it at lichess** — use `/account/security`. That last one is a genuinely new
  hole (gamchess used to hold the only copy, so unlink was always available) and it belongs in
  the copy *and* in the honest-costs section above.
  **One correction while you're there:** the callback page can no longer name the account it
  just linked, because gamchess has no token — which makes PLAN No. 7's parenthetical ("the
  callback has to name the account it just linked") false. The pages stay server-rendered
  anyway, for the disclosure and the byte-exact `redirect_uri`.

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
