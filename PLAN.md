# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

**M8 (lichess link + Board API relay), M9 (game sessions + lichess TV) and M10 (draw,
takeback and premove at both kinds of table) are built and merged.** Read CLAUDE.md's
"Lichess" section for the custody decision, the traps and the API-citizen rules. What
remains of them is the open spikes at the bottom of this file.

---

## M11 — how it sounds, and how it feels

Everything in the lobby works and none of it has had a design pass. This milestone is that
pass: the seated menu, the clocks, the wall boards, and the sound. It is the first milestone
whose deliverable is a **judgement** rather than a feature — so it starts by looking.

### Start by looking, and come back with a list

**Do not redesign anything before opening it.** Walk the lobby, sit at a table, press E at
every board, and read every panel against what the game actually does now. Then bring back
proposed changes and **ask** — per panel, with a recommendation. This milestone is taste,
and the taste is the user's, not the model's.

Two reasons that's a rule and not a preference:

- **The panels are the part of this repo that goes stale silently.** Nothing fails, no test
  breaks, nothing looks wrong in a diff, and the only person who finds out is a player
  reading the front door. The east board advertised White-goes-first and coming-soon saves
  long after both were false; the Welcome page announced "RIGHT NOW: 10+0 GAMES ONLY" for
  entire milestones. Reading them all against real behaviour **is** the work, and it will
  turn up lies nobody is looking for.
- **Every board that has ever looked wrong here looked wrong for one reason**: it hand-rolled
  its own scale instead of going through `WallBoardGeometry`. A design pass that doesn't know
  that will reinvent the bug. Read CLAUDE.md's world-scale and UI-gotchas sections **first** —
  px is not world size, `+Y` is left, a new board needs its `YFrac` in `lobby.scene` or it
  lands on top of another one, and panel-rendered chess glyphs do not paint.

### The panels, all of them

Every one is in scope. The point of the list is that nobody gets to miss one.

**Seated (ScreenPanel, only while engaged at a station):**

| Panel | What it is |
|---|---|
| `GameHud.razor` | **The known-bad one — 856 lines.** See below. |
| `Screens/InfoScreen.razor` | Walk up, press E: Welcome / dev notes / lichess. The long version, and the front door. |
| `Screens/SettingsScreen.razor` | The south wall's settings board. |
| `Screens/SpectatorScreen.razor` | The north wall's board: TV channel, follow-the-lobby, on/off. **Every TV control lives here and nowhere else** — read CLAUDE.md before moving one. |
| `Screens/ChatPanel.razor` | |
| `Screens/LobbyOverlay.razor` | |
| `MusicBoardScreen.cs` | The music wall (`gamah.skafinity`). |

**In the world (WorldPanel, display-only, no pointer input):**

| Panel | What it is |
|---|---|
| `CenterInfoPanel.razor` | East wall. Now a signpost — title + "PRESS E FOR HELP / INFO". **Don't restore the summary**; duplicating the Welcome page is what rotted. |
| `LichessBoardPanel.razor` | East wall: link status + the copyable link URL. |
| `DevNotesPanel.razor` | |
| `WallSettingsPanel.razor` | |
| `SpectatorInfoPanel.razor` | North wall: what's on, and what the 64 squares can't show. |
| `SpectatorSeatPanel.razor` | North wall: the seat plaques. |
| `SpectatorFanfarePanel.razor` | North wall: the 3s result banner when a TV game ends. |
| `NameTagPanel.razor` | Over a player's head. |
| `StationScreenPanel.razor` | |
| `MarqueeNumberPanel.razor` | |
| `WallTextPanel.razor` | |
| `WallTheme.cs` / `WallTheme.scss` | The shared palette every board pulls from. |

### The seated menu is the known-bad one

`GameHud.razor` is **856 lines** and does: seat names, both clocks, whose turn, the
time-control picker, ready, the lichess opt-in, the seek controls and their three chips,
draw, takeback, premove, resign, the promotion picker, the move list, and every status and
error line. It grew a control at a time across M2, M7, M8, M9 and M10 and has never been
laid out on purpose. It is a column of buttons in the order they were written.

Things to decide rather than inherit:

- **It's one flat column.** Setup (time control, lichess, seek, ready) and in-game (draw,
  takeback, resign, moves) are different modes of the same panel, separated only by
  `ShowSetup`. Nothing groups them visually.
- **Nine controls before you can play.** At a linked table the setup screen shows five time
  controls, a lichess toggle, three seek chips, a seek button and ready. That is a lot of
  front door for "sit down and play chess".
- **Its own comment says the px values are uncalibrated** ("tune in-editor"). Nobody has.
- **`BuildHash` is four nested `HashCode.Combine`s at the 8-arg ceiling.** It is the repaint
  gate and it is load-bearing: a value missing from it is a control that silently never
  appears, which has bitten twice. It is also out of room. If the HUD splits into panels,
  this splits with it.
- **The move list is `overflow: hidden` with a hand-rolled tail** because drag-scroll fights
  clicks (CLAUDE.md). Fine — but the list can never be read past 12 rows.

### The clocks, while you're actually playing

Named separately from the HUD because a clock is not a status line, and right now it is one.
Both clocks render as **text inside `SeatLine`**, in a column pinned to the right edge — and
you are looking at the board, in the middle of the screen. In a 3+0 game that's the wrong
place for the one number that ends the game.

What's already true, so a redesign doesn't undo it (all of this is load-bearing — CLAUDE.md's
`%clk` section has the reasoning):

- **`TimeControl.Format` truncates, never rounds.** A live clock must never read higher than
  the time actually left. `"{seconds:0.0}"` would round 59.96 to "60.0"; that's why it doesn't
  use it.
- **It switches to tenths under `DecimalBelowSeconds`**, and the host tightens its sync from
  `ClockSyncInterval` (0.1s) to `ClockSyncIntervalLow` (0.03s) exactly there — a tenths display
  fed by a 0.1s sync would visibly stutter.
- **Clients never run their own clock**, they render the host's synced copy, so nobody can flag
  on lag. Anything that makes the clock feel smoother must not become a local countdown. (The
  TV wall *does* run one locally, for a different reason — lichess only sends a clock on a move
  — and that's the exception, not the pattern.)
- **`GameHud.PanicSeconds` (10s) already exists** and only changes the text's class.
- **The seat lines are hashed as their rendered STRINGS** in `BuildHash` — that's what makes
  the clock repaint exactly when the visible text changes rather than every frame. Move the
  clock and that goes with it.

Worth deciding: does the clock belong on the table itself (a real mesh, the way the board is
real meshes rather than panel art), on the board frame, or somewhere the eye already is? The
spectator wall already puts clocks on seat plaques — that's a precedent to copy or to reject
deliberately. And **tick/tock currently fire once per move**, not per second, so a table with a
running clock is silent; whether a real chess-clock tick belongs here is a taste call, and a
loud one in a room with six tables.

### The wall boards

"Various changes to the wall boards." Ask which, and look for yourself. Known-shaky, roughly
in the order it'll bite:

- **They share a palette but not a design.** `WallTheme` is a colour set; each board invented
  its own type scale and spacing inside it.
- **The east wall now runs signpost → lichess → dev notes**, and the info board shrank to
  three lines when it became a signpost. Whether the wall still reads as a wall is unknown —
  nobody has stood in front of it since.
- **`SpectatorInfoPanel` says what the 64 squares can't show** (Crazyhouse pockets,
  Three-check counts). If those channels get watched, the pockets need somewhere to live —
  the seat plaques are the obvious candidate.
- **The fanfare is 3 seconds and says how, not how it looked.** No sound, no highlight of the
  mating move, no crown. On UltraBullet a game ends every ~30s, so 3s in every 30 goes to a
  result banner — too much, or exactly right; it has never been watched.
  `LichessTv.FanfareSeconds`, one line.
- **Does the featured TV game changing under you read as jarring?** We render the new game and
  clear the last-move highlight. It might want a beat of "next game…" instead.

### Sound

What exists (`scripts/gen_sounds.py`, numpy, synthesized — CLAUDE.md's Sounds section has the
`.sound` gotchas): **tick/tock** for clocks by side, **pop** for captures, **servo slides** for
station rebuild, **woosh**. 2D at your own board, positional for other people's.

What has no sound at all — and every one of these is a moment:

- **Check.** The board already knows (`ChessGame.IsCheck`, and the king square is already
  tinted red).
- **Game over** — mate, resignation, a flag falling. A game ends in silence today.
- **A draw offered, or a takeback asked for.** New in M10 and easy to miss completely: the
  opponent's request is a line of text in a column you may not be looking at.
- **A premove firing.** It plays itself while you're not on move — the one event you are by
  definition not watching for.
- **Your clock about to flag.** `GameHud.PanicSeconds` already exists for the visual.
- **Sitting down / standing up, and the TV fanfare.**

Two real constraints: sounds are **generated, not sourced** (the CC0 rule — everything here is
synthesized by our own script, and a sourced sample would need attribution), and **move sounds
already fire per-board positionally**, so anything new must decide whether it's yours (2D) or
the room's (3D). Don't make the lobby a slot machine: six tables means six of everything.

### Open questions carried over — all of them are UI/UX, which is why they're here

- **Nothing shows a premove was dropped.** It just doesn't happen, which from the seat is
  identical to it never arming. A beat of "premove cancelled" may be worth it.
- **Is "any click cancels a premove" right?** It matches the click-to-move flow, but a
  misclick while idly watching silently disarms you. Right-click-only cancel is the
  alternative and costs a binding.
- **Is the local TV clock close enough?** It counts down from the last frame and resnaps on
  every move, so it drifts LOW by roughly the network latency. Should be invisible; worth a
  glance on UltraBullet, where a fraction of a second shows most.
- **Is 45s the right idle TTL for a TV upstream?** Guessed, not measured.

### There is no proximity gate, and there must not be one

Recorded here because a UI/UX pass is exactly when someone re-invents it. TV briefly only
streamed while a viewer was near the board. **That is gone.** It cost three attempts, each of
which looked fine in a diff:

- a range of 1200 in an 800-unit room, which nowhere in the lobby could exceed — it gated
  nothing while looking exactly like a gate;
- measuring from the controller's own GO, which sits on **LobbyRoom** at the room centre, not
  at the wall;
- measuring in 3D against a board that floats ~390 up, so a third of the distance was vertical
  before the player moved.

And what it bought was a wall that went blank when you stepped back from it. The cost it was
guarding is already bounded by better things: TV polls only while it's the featured source on
that client, and gamchess holds one upstream per channel however many watch.

---

## Still open from M8 / M9 — resolve, don't guess

- **`LICHESS_TOKEN_KEY` rotation.** No path exists. Changing the key orphans every link. Needs
  a re-encrypt migration before there are real users worth not annoying. **Do not forget this.**
- **`code_verifier` length.** lichess has a `CodeVerifierTooShort` error whose threshold is
  undocumented. Ours is 43 chars — exactly RFC 7636's floor. If linking fails at the exchange,
  this is the first suspect; widen to 64 bytes and re-test.
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
no API branch in their contact form. Do not ask pre-emptively. The ask is only credible with
real numbers, so: ship, measure actual 429s, and bring the specific limit hit. The one to watch
is the lobby seek — **5/min per IP, which is 5/min for the entire playerbase** (lila
`Limiters.setupPost`). Outcome is discretionary; there is no blessing process.

---

## Known gaps in what shipped

- **A relayed lichess game is never archived to gamchess.** It lives on lichess and nowhere
  else. The PGN + `%clk` writer already exists — wiring the relay's final state into
  `POST /api/v1/games` would put it in the web viewer too.
- **The rating-range filter is a guess.** gamchess can't read a player's lichess rating (no
  scope for it), so "near my rating" asks for a fixed 1400-1800 band rather than one centred
  on them. Either fetch the rating at link time (it comes back from `/api/account` — no new
  scope needed) or drop the control.
- **No berserk, no chat on lichess games.** Both exist on the Board API; neither is wired up.
- **"Quick pairing" and blitz seeks are both behind the `web:mobile` scope — and we won't take
  it.** Re-derived from lila + lila-ws master 2026-07-16 (CLAUDE.md has the full chain): quick
  pairing is a **pool**, not a seek; pools have **no HTTP endpoint at all**; and lila-ws's
  bearer auth requires scope `web:mobile` or `web:polygon`, so a `board:play` token cannot
  authenticate there. Blitz seeks aren't universally refused either — `boardApiHook`'s
  `allowFastGames` skips the Rapid check for those same two scopes. `web:mobile`'s own
  description is **"Official Lichess mobile app"**. Taking it would mean claiming to be
  lichess's first-party client to get past a gate aimed at third-party board clients, on an API
  where our whole playerbase shares one IP and lichess can kill the app on `clientOrigin` — and
  it would force every linked player to re-link. **Don't re-open this without new facts from
  lichess's side**; the ask is "may we seek blitz", not a scope we help ourselves to.
- **No correspondence.** `SeekCorrespondence` exists in the lichess package and has no route:
  it's the one seek shape that costs the relay nothing (buffered, no held stream, no per-IP
  seek cap), but days-per-move doesn't fit sitting down at a table.
- **Variants can never be PLAYED** without replacing the vendored rules library, which is
  standard-only: `ChessGame` would have to parse the FEN and validate moves it has no rules
  for. Don't offer what can't be played.
  **Note the word.** This constraint is about *playing* and nothing else. It was carried over
  to lichess TV — which parses nothing, and just walks a FEN's placement field onto 64 squares
  — and cost M9 ten channels on a premise nobody checked. M9 now serves all 16. **"The board
  can't draw it" is a claim about whatever actually reads the FEN; go and look at that.**
- **The relay is in-memory.** A gamchess restart mid-game drops the relay's state and the board
  goes quiet, though the lichess game itself carries on (and can be finished on lichess.org).
  Acceptable; worth knowing.

---

## The web viewer needs a lot of work

`server/frontend/` (`index.html` / `app.js` / `chess.js` / `style.css`) — the archive viewer at
chess.gamah.net. It works, but it has never had a design pass, and **nobody has ever looked at
it on anything but a desktop browser**. Treat the list below as observations, not a spec — the
real first step is to open it and decide what it should be. If M11 produces a design vocabulary,
this should inherit it rather than invent a second one.

What's already known to be weak:

- **The CSS has barely been exercised.** The board squares resized to fit whichever piece stood
  on them until 2026-07-15 — an 8×8 grid that wasn't actually holding a grid. That bug
  surviving this long says the styling has had no real scrutiny.
- **Never checked narrow / mobile.** The board is `width: min(28rem, 100%)` next to a
  `flex: 1 1 14rem` side panel, and the games table has four fixed columns. What that does under
  400px is unknown.
- **The games list shows Played / White / Black / Result only.** No time control, though the PGN
  now carries one. Adding a column means touching `index.html`'s `<thead>` too.
- **Game meta is one text line** — date, then the time control tacked on after a `·` (`loadGame`
  in `app.js`). Fine as a stopgap; not a design.
- **The per-move `%clk` display is brand new and unseen.** `shortClk` trims the leading `0:` so a
  bullet clock reads `0:51.63`; that call was made without ever seeing it rendered next to the SAN.
- **Sign-in is a bare button.** The Steam OpenID round trip works, but the signed-out and error
  states have had no thought.
- **The lichess pages are server-rendered and unstyled beyond `/style.css`.** `/lichess/link` and
  the callback page are `html/template` in `internal/api/lichess_pages.go`, deliberately (the
  callback has to name the account it just linked). They reuse the viewer's stylesheet and a small
  inline block. If the viewer gets a design pass, they should come with it — and the disclosure
  copy in them is load-bearing, not decoration: **do not trim the two warnings** (a lichess
  password change does NOT unlink; `/account/oauth/token` does NOT list this grant).
- **The viewer says nothing about lichess.** A linked player has no way to see or manage the link
  from the web except by knowing the `/lichess/link` URL.

Constraints worth knowing before starting:

- **The frontend is baked into the Docker image** (`COPY --from=builder /src/frontend /frontend`).
  A restart won't pick up CSS changes — the server needs `git pull && make rebuild`.
- **Zero image assets, and it should stay that way.** Pieces are Unicode glyphs with U+FE0E
  forcing text presentation; that's what keeps the viewer CC0-clean with nothing to attribute.
  The s&box client can't render these glyphs at all (they come out as colour emoji — see
  CLAUDE.md), but a browser can, so this is the one place they're allowed.
- **`chess.js` is rules code, not view code.** It is gated by `node scripts/chess_js_perft.mjs`,
  which runs on the dev host. Re-run it after touching that file — it holds the viewer's rules and
  PGN parsing to the same reference positions and real C# writer output as the client.
