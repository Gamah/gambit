# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

**M8 (lichess link + Board API relay) is built** and is not repeated here — read CLAUDE.md's
"Lichess" section for the custody decision, the traps, and the API-citizen rules. What
remains of it is the open spikes below.

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
