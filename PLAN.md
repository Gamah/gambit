# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

**M8 (lichess link + Board API relay) is built** and is not repeated here — read CLAUDE.md's
"Lichess" section for the custody decision, the traps, and the API-citizen rules. What
remains of it is the open spikes below.

---

## M9: lichess TV on the west wall

Put real lichess games on the spectator wall, streamed through gamchess, loaded lazily so
we only hold a feed while someone is actually watching it.

Done looks like: you walk up to the west wall in an idle lobby and there's a live 2000+
blitz game from lichess on it, with clocks and names; "Next" still steps through your
lobby's own tables; and if you don't want it, you turn it off and it stays off.

### Why this one is easy, and why that's worth saying

**TV is the one lichess feature with no security surface UPSTREAM.** `GET /api/tv/feed`,
`/api/tv/{channel}/feed`, `/api/tv/channels` are `security: []` — **anonymous**. No token,
no scope, no custody question, nothing to encrypt, nothing to revoke, nothing to audit.
None of M8's hard part applies. Do not let it drift into the token machinery: it must keep
working for a player who has never linked and never will.

**That is a fact about lichess's side, not ours.** Our proxy of it is still session-gated —
see "Do this first" below. Anonymous upstream is exactly why an open endpoint here would be
attractive to abuse, and every byte of that abuse would be attributed to our IP and our
User-Agent.

It also lands almost exactly on the shape `SpectatorController` already has. A `fen`
message is `{fen, lm, wc, bc}` — position, last move, and both clocks **in seconds**,
which is the unit `TimeControl.Format` already takes. The wall wants
Fen/LastMoveUci/White/Black/WhiteClock/BlackClock/TickingSeat, and the feed gives all of
it. This is a data-plumbing milestone, not a design one.

### The shape

**Per-client, and that's the existing pattern rather than a new one.** The west wall is
already per-client: `SpectatorController._featuredIndex` / `CycleFeatured()` are local, so
two players at that wall already see different tables today. Everything below just extends
the cycle.

- **TV is one more channel in the cycle.** "Next" steps through each live table and then
  the TV channel. Lobby tables don't get priority — a player who wants lichess can sit on
  it while a game runs at a table.
- **The client decides whether TV exists.** A setting on the *local* settings board
  (`SettingsModel.BuildLocalRows`), **default on**, persisted to `PlayerData` — so only
  someone who turns it OFF has anything saved. Off means TV leaves the cycle; the wall
  still mirrors tables exactly as it does now.
- **The client decides the channel**, one of two ways: *follow the host's suggestion*
  (default) or *pick my own*. Both persisted.
- **The host suggests**, it doesn't dictate: a `[Sync]` suggested channel, **default
  blitz**, admin-gated like the BOARDS row (`LobbyNetworkManager.LocalIsAdmin`, routed via
  the host — remember the admin may not be the network host on a dedi).

### gamchess: one upstream per channel, ref-counted

**This is the whole point of routing TV through gamchess rather than letting clients hit
lichess directly.** lichess advocates exactly this: one stream held by a central server,
fanned out to N sessions. So:

- `GET /api/v1/tv/{channel}?since=N` — long poll, same transport as the M8 relay (held
  ~5s, under the client's 8s ceiling).
- The upstream `GET /api/tv/{channel}/feed` opens **on the first watcher** and is dropped
  after an idle TTL once the last one stops polling. Ref-count by pollers, not by lobbies.
- **N clients cost lichess nothing.** 100 players on blitz = 1 upstream stream. That
  invariant is the deal, and it's why per-client channel choice is affordable: the cost of
  everyone picking differently is bounded by the channel count (~6), not the player count.
- Reuse `internal/lichess`'s stream reader and the etiquette governor — a 429 here must
  back off like everywhere else, and the User-Agent still identifies us.

### Channels: standard only

lichess's channel keys (lcfirst of lila's `Tv.Channel`): `best` ("Top Rated"), `bullet`,
`blitz`, `rapid`, `classical`, `ultraBullet`, `bot`, `computer`, plus the variant channels
`chess960`, `crazyhouse`, `kingOfTheHill`, `threeCheck`, `antichess`, `atomic`, `horde`,
`racingKings`.

**Offer the standard speed channels only** — Top Rated, Bullet, Blitz, Rapid, Classical,
UltraBullet — for the same reason the M8 seek offers no variants: our board can't draw
them. Crazyhouse FENs carry pockets (`…/RNBQKBNR[] w …`) and Chess960 castling is X-FEN
file letters (`HAha`), neither of which the vendored standard-only rules will parse. A
channel that renders an empty board is worse than a channel that isn't there. `bot` and
`computer` would parse fine but are noise on a wall in a chess bar; leave them out unless
someone asks.

**Default: `blitz`.**

### Do this first: gamchess sessions for the game client

**gamchess verifies the FP token against Facepunch on EVERY authed request** — a live HTTP
call per request (`api/auth.go` → `steam.ValidateToken`). That is already wrong for M8's
relay poll (one Facepunch round-trip per player per ~5s of a live game), and TV multiplies
it by everyone standing at a wall: 8 idle spectators is ~1.6 Facepunch calls/second,
forever, for a public feed.

**Fix it by issuing the client a gamchess session token**, and note that *the code already
exists* — `internal/api/session.go` mints exactly this today for the web viewer's cookie:
a stateless HMAC-signed `steamID|expiry|MAC`, verified with no I/O at all. The game client
should carry the same thing as a bearer.

```
POST /api/v1/session   FP-gated  →  { "token": "gcs_…", "expires_at": … }
```

One Facepunch call per session; every later request is a local HMAC check — **zero**
network on the hot path. Better than caching verifications, which still pays Facepunch
once per TTL per player and needs server-side state to do it.

- **Distinguish it from an FP token by prefix** (`gcs_`), not a new header. `callerSteamID`
  already tries session-then-FP; it grows one branch — read the bearer, and if it's ours,
  verify the MAC.
- **Keep the FP path.** It stays the only way to *get* a session, and the console commands
  and any one-shot call can keep using it directly.
- **Short TTL — an hour, not the web's 30 days**, and this is the one real tradeoff to
  understand. A gamchess session authorises everything that SteamID can do, including
  *playing lichess games as them*. An FP token is short-lived by nature; a 30-day bearer
  for the same authority is a much bigger thing to leak, and sessions are stateless so
  **there is no way to revoke one** (short of rotating `SESSION_SECRET`, which signs
  everyone out). An hour still cuts Facepunch calls by ~700× on a polling client, which is
  the entire point — there is no reason to reach for a longer window.
- **Memory only, never `FileSystem.Data`.** `GamchessAuth` already holds the FP token in
  memory and nothing else; the session must live the same way. "Can a rogue lobby host read
  another client's FileSystem.Data?" is still an open spike below — do not hand it a
  long-lived credential to find.
- Re-mint on 401 exactly as `SendAuthed` already re-mints the FP token once. That path is
  built; point it at the session instead.

**TV goes behind the session too — it is NOT a public endpoint.** The tempting shortcut is
that TV is anonymous upstream, so a proxy of it needs no identity; the only thing that ever
argued for gating it was the Facepunch cost, and the session removes that. Two reasons it
stays gated anyway:

1. **We must not become a free unauthed lichess TV relay.** An open `/api/v1/tv/{channel}`
   is a public CDN for someone else's content, pointable by any script, costing us
   bandwidth to serve lichess's feed to people who have never touched Gambit.
2. **The abuse would be attributed to us.** Our IP and our User-Agent are what lichess sees
   — we went out of our way to make that traffic identifiable (`etiquette.go`) precisely so
   they can attribute it. An open relay means anything done through it is done *as Gambit*,
   against the one IP whose limits every real player shares, and it's our standing that
   pays. Being identifiable and being an open relay are a bad combination.

So: session-gated like everything else. A Steam identity to watch TV is a trivial ask for a
Steam-gated game, and it costs one local HMAC.

Decide before building the poll loop, not after.

### Verification

Provable in the container: channel-key validation (an arbitrary string off the wire must
never reach a lichess URL), the ndjson `featured`/`fen` state machine against canned
frames, ref-counting (second watcher doesn't open a second upstream; last one leaving
drops it after the TTL; a new watcher during the TTL reuses it), and that a 429 backs off.
The `LichessTv` channel list is Sandbox-free — put it beside `LichessTable` so the dotnet
harness can check it.

Needs the user, in the editor: the wall shows a real blitz game; "Next" still cycles
tables; TV off removes it from the cycle and survives a restart; follow-host tracks the
admin's suggestion; **kill gamchess → the wall falls back to mirroring tables and local
chess is untouched.**

### Open questions

- **Does "TV off" hide the whole west wall, or just the TV channel?** Taken as: just the
  channel. The wall keeps mirroring tables, because that's its original job and it predates
  TV. Say so if you meant the board itself.
- **Does the featured game changing under you need anything?** lichess swaps the featured
  game when it ends and sends a fresh `featured` message. Probably just render it; worth a
  look in case it's jarring mid-watch.

---

## M8 follow-ups — none of this is verified in a real editor yet

The Go half compiles and its tests pass (197 cases, `-race`). **The engine half has never
been compiled**, and nothing has been deployed. Everything below needs the user.

### First-run verification

1. `make testinst BRANCH=m8-lichess` → `testchess.gamah.net`. `TEST_PUBLIC_BASE_URL` must be
   the test URL or the OAuth callback returns to prod. `make up` mints the two lichess keys
   into `.env` if they're absent and prints a note; **back `LICHESS_TOKEN_KEY` up** — there is
   no rotation path, so losing it forces every player to re-link.
2. Walk to the lichess board on the east wall, copy, paste in a browser: Steam sign-in →
   the disclosure page → lichess consent naming **`board:play`** → success page → the panel
   flips to "linked as <username>" within ~3s.
3. Two linked players at a **Blitz 3+2** table both press "play on lichess", both ready up:
   a real blitz game runs on lichess between their accounts, mirrors to the board, and lands
   in both players' lichess history. Moves/resign/draw round-trip.
4. One linked player at a **Rapid 10+0** table presses "find a game": a real opponent from
   lichess's lobby. Test **cancel** too — the seek must actually disappear from lichess's
   lobby, because the held connection *is* the seek.
5. `[ unlink ]` → "not linked"; confirm on lichess the token is gone; re-linking works.
6. **Kill gamchess → local chess still plays.** Non-negotiable.

### Open spikes — resolve, don't guess

- **`code_verifier` length.** lichess has a `CodeVerifierTooShort` error whose threshold is
  undocumented. Ours is 43 chars — exactly RFC 7636's floor. If linking fails at the exchange,
  this is the first suspect; widen to 64 bytes and re-test.
- **`LICHESS_TOKEN_KEY` rotation.** No path exists. Changing the key orphans every link. Needs
  a re-encrypt migration before there are real users worth not annoying. **Do not forget this.**
- **The upstream engine streaming fix.** `Http.RequestStreamAsync` is broken (it `using`s the
  response, then returns its stream) and `HttpCompletionOption` is off the whitelist. Fixing
  both upstream is what would let the token move to the client and delete the custody problem
  entirely. File the PR: drop the `using`, pass `ResponseHeadersRead`, add the missing test.
  Not a dependency for anything shipped.
- **Can a rogue lobby host read another client's `FileSystem.Data`?** Only matters if the token
  ever moves client-side. Facepunch platform question — confirm, don't assume.
- **Does the long poll hold up under real latency?** 5s server hold vs the client's 8s ceiling
  is not much headroom. If polls start reading as timeouts and tripping the breaker, shorten
  the hold before reaching for WebSocket.

### Talking to lichess about limits

**Discord `#lichess-api-support`** (`https://discord.gg/MS9MejQqha`), **not email** — there is
no API branch in their contact form, and `contact@lichess.org` is the general/commercial
address. Do not ask pre-emptively.

The ask is only credible with real numbers, so: ship, measure actual 429s, and bring the
specific limit hit. The one to watch is the lobby seek — **5/min per IP, which is 5/min for
the entire playerbase** (lila `Limiters.setupPost`). gamchess already self-limits to it and
identifies itself by User-Agent on every request, which is what makes the traffic auditable
from their side. Outcome is discretionary; there is no registration or blessing process.

### Known gaps in what shipped

- **Every authed request re-verifies the FP token against Facepunch.** A live HTTP call on
  *each* request (`api/auth.go` → `steam.ValidateToken`), so a relayed game costs one
  Facepunch round-trip per player per poll (~5s). Nothing has run yet, so nobody has felt
  it. The fix is a gamchess session token for the game client — the minter already exists
  (`session.go`, the web cookie) — spelled out under M9, which makes the problem materially
  worse. Do it there, or here first.
- **A relayed lichess game is never archived to gamchess.** It lives on lichess and nowhere
  else. The PGN + `%clk` writer already exists — wiring the relay's final state into
  `POST /api/v1/games` would put it in the web viewer too.
- **The rating-range filter is a guess.** gamchess can't read a player's lichess rating (no
  scope for it), so "near my rating" asks for a fixed 1400-1800 band rather than one centred
  on them. Either fetch the rating at link time (it comes back from `/api/account` — no new
  scope needed) or drop the control.
- **No takeback, no berserk, no chat.** All exist on the Board API and none are wired up.
- **No correspondence.** `SeekCorrespondence` exists in the lichess package and has no route:
  it's the one seek shape that costs the relay nothing (buffered, no held stream, no per-IP
  seek cap), but days-per-move doesn't fit sitting down at a table.
- **Variants can never work** without replacing the vendored rules library, which is
  standard-only. A Crazyhouse game would arrive as moves the board can't render. Don't offer
  what can't be drawn.
- **The relay is in-memory.** A gamchess restart mid-game drops the relay's state and the
  board goes quiet, though the lichess game itself carries on (and can be finished on
  lichess.org). Acceptable; worth knowing.

---

## The web viewer needs a lot of work

`server/frontend/` (`index.html` / `app.js` / `chess.js` / `style.css`) — the archive viewer
at chess.gamah.net. It works, but it has never had a design pass, and **nobody has ever
looked at it on anything but a desktop browser**. Treat the list below as observations,
not a spec — the real first step is to open it and decide what it should be.

What's already known to be weak:

- **The CSS has barely been exercised.** The board squares resized to fit whichever piece
  stood on them until 2026-07-15 — an 8×8 grid that wasn't actually holding a grid. That
  bug surviving this long says the styling has had no real scrutiny.
- **Never checked narrow / mobile.** The board is `width: min(28rem, 100%)` next to a
  `flex: 1 1 14rem` side panel, and the games table has four fixed columns. What that does
  under 400px is unknown.
- **The games list shows Played / White / Black / Result only.** No time control, though
  the PGN now carries one. Adding a column means touching `index.html`'s `<thead>` too.
- **Game meta is one text line** — date, then the time control tacked on after a `·`
  (`loadGame` in `app.js`). Fine as a stopgap; not a design.
- **The per-move `%clk` display is brand new and unseen.** `shortClk` trims the leading
  `0:` so a bullet clock reads `0:51.63`; that call was made without ever seeing it
  rendered next to the SAN.
- **Sign-in is a bare button.** The Steam OpenID round trip works, but the signed-out and
  error states have had no thought.
- **The lichess pages are server-rendered and unstyled beyond `/style.css`.** `/lichess/link`
  and the callback page are `html/template` in `internal/api/lichess_pages.go`, deliberately
  (the callback has to name the account it just linked). They reuse the viewer's stylesheet
  and a small inline block. If the viewer gets a design pass, they should come with it — and
  the disclosure copy in them is load-bearing, not decoration: **do not trim the two warnings**
  (a lichess password change does NOT unlink; `/account/oauth/token` does NOT list this grant).
- **The viewer says nothing about lichess.** A linked player has no way to see or manage the
  link from the web except by knowing the `/lichess/link` URL.

Constraints worth knowing before starting:

- **The frontend is baked into the Docker image** (`COPY --from=builder /src/frontend
  /frontend`). A restart won't pick up CSS changes — the server needs `git pull && make
  rebuild`.
- **Zero image assets, and it should stay that way.** Pieces are Unicode glyphs with
  U+FE0E forcing text presentation; that's what keeps the viewer CC0-clean with nothing to
  attribute. The s&box client can't render these glyphs at all (they come out as colour
  emoji — see CLAUDE.md), but a browser can, so this is the one place they're allowed.
- **`chess.js` is rules code, not view code.** It is gated by
  `node scripts/chess_js_perft.mjs`, which runs on the dev host. Re-run it after touching
  that file — it holds the viewer's rules and PGN parsing to the same reference positions
  and real C# writer output as the client.
