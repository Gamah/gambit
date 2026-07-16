# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

**M8 (lichess link + Board API relay), M9 (game sessions + lichess TV) and M10 (draw,
takeback and premove at both kinds of table) are built and merged.** Read CLAUDE.md's
"Lichess" section for the custody decision, the traps and the API-citizen rules. What
remains of them is the open spikes at the bottom of this file.

---

## M11 — how it sounds, and how it feels

Everything in the lobby works and none of it had had a design pass. This milestone is that
pass: the seated menu, the clocks, the wall boards, the sound, and what happens to a piece
when it's taken. It is the first milestone whose deliverable is a **judgement** rather than
a feature — so it started by looking, and the looking is done.

**The questions have been answered**, and the answers are here. They were worked through in a
scratch file (`M11.md`) that has since been deleted, on purpose: everything it decided is folded
into this file and CLAUDE.md, and a second copy of the reasoning is exactly the kind of
duplicated prose that rots. Where that file was itself wrong, the correction is called out
below rather than lost with it. **Nothing outside this file and CLAUDE.md is needed to pick the
work up.**

### What the looking actually found

Worth stating plainly, because it inverts the premise this milestone was written on.
PLAN.md predicted the player-facing panels would be full of lies. **They weren't.**
`InfoScreen` and `CenterInfoPanel` were verified line by line against the code and are
honest — M10's draws, takebacks and premove are all described correctly, and COMING SOON
lists only things that are genuinely unbuilt. The process worked.

**The rot was in the places nobody thought to check**, and all three were load-bearing:

- **CLAUDE.md itself** cited `SplashScreen` as the exemplar to copy for self-attaching UI.
  There is no `SplashScreen` — no `.cs`, no `.razor`. The file every session is told to
  trust was pointing at a file that does not exist.
- **`lobby.scene`** carried **eight components with no class in `client/Code/`**, two of
  which stated numbers that contradicted the code that really runs (`ArcadeRing.BoardSize:
  28` vs the real `ChessRing`'s 26; `SpectatorBoard.ClearAboveWall: 20` vs `SpectatorWall`'s
  18). CLAUDE.md's own "grep the scene, don't trust code defaults" rule pointed straight
  into that trap.
- **A clock comment** asserted the TV clock "drifts LOW". It reads **HIGH** — see below.

The lesson is now in CLAUDE.md: **the reference doc and the scene rot exactly like the
panels do, and nothing fails when they do.**

### Done

- **The eight orphan scene components are deleted**, along with `SettingsWall`'s two dead
  keys (`BoardHeightFrac`, `PanelUnitWidth` — no such properties exist on the class).
  CLAUDE.md's world-scale rule now says to confirm a component still has a class, and
  records that a runtime-built component (`SpectatorWall`) runs on code defaults and cannot
  be retuned in-editor at all.
- **CLAUDE.md's self-attaching-UI line is corrected** — and **the first proposed fix for it was
  wrong too**, which is worth keeping: it would have cited `ChatPanel` and `LobbyOverlay` as
  "all verified self-attaching". They aren't. Only **GameHud and SpectatorScreen** self-attach
  (`LobbyPlayer.cs:128-144`); `InfoScreen`, `SettingsScreen`, `ChatPanel` and `LobbyOverlay`
  are serialized in `lobby.scene`. **A correction written without re-checking rots exactly like
  the line it corrects.**
- **The "Near my rating" chip is gone**, and this is the one that got more interesting the
  harder it was looked at. It sent a fixed `1400-1800` to every player — a lie to a 2200.
  But all three fixes on the table (fetch the rating at link time, relabel, drop it) were
  **worse than sending nothing**, because re-deriving from lila showed that **omitting
  `ratingRange` makes lichess centre a Gaussian band on the player's real rating**
  (`Hook.ratingRangeOrDefault` → `RatingRange.defaultFor`). It knows their rating; we don't.
  So: no chip, no `ratingRange`, no `/api/account` fetch, no stored rating — and one true
  sentence in `InfoScreen` where a control used to be. Full chain in CLAUDE.md's ratingRange
  trap, including why lichess's own ±500 UI preset is a decoy lila deliberately discards.
- **`SettingsWall`'s row is fixed.** It was +96 / −96 / −208 — nobody chose that; the scene
  stated Host/World but never gained a `MusicXFrac`, so Music kept its code default. Now an
  even +0.24 / 0 / −0.24, **with the code defaults matching the scene** so the "a new
  `[Property]` gets the code default" hazard can't re-fire here.
- **`woosh` is deleted** (2 dead call sites, 3 unplayed assets, and its generator in
  `gen_sounds.py`), as is **`WallTextPanel.razor`** — a whole component with zero call sites.
- **Captured pieces now have somewhere to go** — see the next section.
- **The sound pass is built** — see Sound, below. The headline: a real lichess game at a
  table had been **completely silent since M8** because sound hung off `LocalGameController`
  instead of the seam. It is now a watcher on `IBoardGame`, which is what makes that class
  of bug impossible rather than merely fixed.
- **A dropped premove says so**, using the bool both controllers were throwing away.

### Taken pieces, and the table that had to grow to hold them

Each player's losses now sit in a tray on their own side of the table, and a captured piece
**travels there** instead of blinking out of existence.

- **The table was too small and its own comment knew it.** `ChessRing` had promised "the
  table top is 34 wide; 26 leaves a healthy margin for clocks/captures later" since M1. It
  didn't: 34 minus the 29-wide board frame is a **2.5-unit** margin, two columns of pieces
  need 7.5, and the table plaque was already sitting in what is now Black's tray. The top is
  now **40 × 44**, and each margin has a job: **Y carries the two trays**, **X is the
  walk-up plaque (−X) and the reserved clock strip (+X)**.
- **The plaque moved to the walk-up corner**, which is the only spot that works: mid-edge at
  +Y is inside a tray, and mid-edge at −X is exactly where White's seat camera looks down
  the board from — a plaque there is a lump in White's foreground.
- **Whose tray is whose: your own dead, on your own right.** White faces +X, so White's
  right is −Y (s&box is Y-left).
- **Tray contents are a pure function of the FEN** (`Code/Chess/CapturedMaterial.cs`),
  **never an accumulated tally.** This is the load-bearing decision. `ChessBoardView`
  rebuilds from the FEN alone and has no history: a late joiner or a resync starts empty and
  its first sync is all-additions. A tray fed by capture *events* would be empty for
  everyone who didn't watch the whole game — which is most spectators, and every player at a
  table that resynced. The animation is a **transient overlay** on top: when the diff has
  the dying piece's GameObject in hand the tray adopts it and walks it over; when it doesn't
  the tray just spawns it in place. Same result either way, so nothing depends on having
  seen it happen.
- **Promotion is why the derivation isn't start-minus-current.** A promoted queen drops the
  pawn count with nothing taken, so the naive diff reports a phantom captured pawn *and* a
  negative queen. `CapturedMaterial.Lost` counts the surplus across promotable types and
  forgives that many pawns.
  - **Accepted and documented:** capturing a piece that was *itself* promoted reads as a
    captured pawn. A FEN cannot say which queen was born a pawn. This is the same material
    diff lichess's own UI computes.
- **It is deliberately Sandbox-free so it could actually be tested here**, per CLAUDE.md's
  rule about isolating code from Sandbox — and that earned its keep immediately. 19 cases
  pass in a dotnet harness, including real games driven through the vendored rules: a real
  **en passant** (the victim isn't on the capture square) and a real **capture-promotion**
  (`1.h4 g5 2.hxg5 h5 3.gxh6 d6 4.h7 e6 5.hxg8=Q`), which is the case the naive diff gets
  wrong in both directions at once.

**Checked in the editor and signed off**: the trays read as trays, and the ring still looks
like a ring despite the oblong table. `CaptureSeconds` (0.45) / `CaptureArc` (1.1) stand
un-objected-to. The east wall still reads as a wall.

**The plaque moved again** — it is no longer at the walk-up corner. See the clock section.

### The TV clock reads HIGH, and three places said otherwise

**The house rule is that a live clock must never read higher than the time actually left** —
it is why `TimeControl.Format` truncates where the PGN writer rounds. The TV wall breaks it.

PLAN.md, the code comment and the design rationale all claimed the local countdown "drifts
LOW by roughly the network latency". The reasoning — *"counting down from a known-good value
can only ever read low"* — fails at its first step: **the value is already stale on arrival.**
lichess stamps the clock at the move instant T₀; the frame reaches us at T₀+L; the code sets
the bank and zeroes its age, so we display T₀'s value while the player has burned L. It reads
**high by L** until the next move corrects it.

**Built — and the design in this file was wrong, which is the interesting part.** "gamchess
stamps receipt time and the client subtracts the elapsed since" fails twice: we don't share a
wall clock with gamchess (an absolute stamp is unreadable, and a skewed one corrects *upwards*
— the forbidden direction), and the stamp alone is a **no-op on the common path**, because the
long poll wakes on the frame so gamchess's own staleness is ~0. The bias that actually exists
is the **network leg**, which no server-side stamp can express.

`TvState` now carries two **durations**, computed at send: `clock_age_ms` and `hold_ms`. The
client measures its own round trip and takes network = round trip − hold. Without `hold_ms` a
5s long-poll hold reads as 5s of latency — a bigger lie than the one being fixed. Full round
trip, not half: the rule is one-directional, so an undershoot is free. Lag applies to the
**ticking seat only** — the idle side's bank is exact however stale the frame is. Residual
lichess→gamchess bias survives by construction and is documented. Magnitude still unmeasured;
direction still certain. Four Go tests pin it, including that serving a poll must not write the
timings back into shared channel state.

The version-advance guard at `LichessTvSource.cs:375-378` is **correct and unaffected** —
re-snapping on a non-advanced poll would sawtooth the clock upward by ~5s, a much larger
version of the same sin. That guard is doing its job.

### The clocks, while you're actually playing

Both clocks are text inside `SeatLine`, in a 250px column pinned `right: 24px`, while the
board is in the middle of the screen. In a 3+0 game that is the wrong place for the number
that ends the game.

**Built, then rebuilt twice after actually looking at it** — every failure was a rule this
repo had already written down and I re-derived from first principles instead:

1. **Invisible**, because a `WorldPanel`'s scale was guessed (`0.022` → 0.85 world units on a
   30-unit body) instead of derived from `PanelSize × 0.05 × scale`.
2. **A wall in Black's foreground** at +X — the exact objection that moved the plaque off −X,
   noted in my own comment while building it there anyway because PLAN.md had reserved +X.
3. **Nothing legible**, which was four things at once and none of them the camera angle:
   `root` carried a fixed px size and the centering (CLAUDE.md's documented gotcha —
   content pins top-left; all four working WorldPanels use one identical `root`); `⬜`/`⬛`
   rendered as big emoji squares (the glyph rule isn't only about chess pieces); the faces
   read "—" on an idle table (`SeatClock` is null when nothing is live, and idle is the
   state you find every table in — it shows the bank now, and "∞" when untimed); and
   finally **the clock text was wrapping**. No `white-space: nowrap` and no
   `flex-shrink: 0`, so "2:48" at 76px wrapped in its auto-width div, and "a div's auto
   height does not grow for wrapped text" clipped it to a sliver — a dot, seen end-on down
   the strip. "W" and "+19" rendered because they were too short to wrap. **If some text
   on a panel renders and some doesn't, check the string lengths first.**

**Two wrong theories worth recording, both diagnosed off a screenshot.** First: that a −Y
strip could never be seat-legible (text baseline down the sightline), and it should move
back to +X — wrong, the text wasn't illegible, it wasn't *rendering*. Second: that the
table must be Unlimited — wrong, and `gambit_clock` printed `Blitz 3+0 … SEAM W=168.1s`
to prove it. **Both were redesigns proposed around a bug.** The lesson is the command:
`gambit_clock` now prints the whole chain, and it settled in one line what two rounds of
reading pixels got backwards. Reach for it before reasoning about geometry.

**Every one of the four failures was a rule already written in CLAUDE.md.** The
WorldPanel-scale constant, the one true `root`, the emoji trap, the nowrap/flex-shrink
pair. Reading that file once per session is evidently not the same as applying it — when a
panel misbehaves, diff it against the panel that works rather than reasoning from
principles.

It is now a **thin low strip beside the board at −Y**, opposite the plaque, with **one** face
angled up across the board — where a real chess clock goes, and why one face serves both
seats: neither is square to it, both are looking down at the table. Single row
(`⬜ 3:00 · bar · ⬛ 2:47`), because the face is tilted out of a 1.4-deep strip and a taller
one leans over the a-file. `ChessRing.BuildStationClock` + `TableClockPanel`.

**The HUD no longer has a clock on it at all**, and the repaint hashing moved with it — that
was the load-bearing part (see below). `SeatClass` also lost its panic red: reddening a *name*
on a HUD with no clock on it is an alarm about a number that isn't on the same screen.

**The material bar** is `CapturedMaterial.Advantage`, and it is counted from the pieces **on
the board** rather than by valuing `Lost`. Not a detail: `Lost` carries a documented lie (a
captured piece that had itself been promoted reads as a captured pawn — a FEN can't say which
queen was born one), so valuing it would report a player 8 points poorer than they are.
Summing the board has no such problem: a promoted queen simply *is* a queen. So the tray and
the bar are derived two different ways from the same position, deliberately — the tray answers
"what did you lose" (history it can't fully know), the bar answers "who is ahead now" (which
the position states outright). Proven in the harness, including the case where the two must
disagree.

**The plaque hangs off the left (+Y) edge**, turned a quarter clockwise so its face looks
outward at the room rather than inward at White's seat. It first read as **inset under an
overhang**, because the drop was computed and the matching inward offset wasn't: a plate
tilted 45° swings its top corner inward exactly as far as it lowers it, and at 45° the two
terms are equal — which is why the missing one was invisible in the arithmetic and obvious in
the room. Both derive from `PlaqueHeight`/`PlaqueTilt` now.

**The trays had no gap anywhere** — the slab was `TrayCols * cell + 1`, which at these numbers
is *exactly* the 7.5 margin, so it ran flush from the board frame to the table edge. Nobody
chose that. The whole Y budget is now derived from four constants (`ClockBoardGap`,
`ClockDepth`, `ClockTrayGap`, `TrayEdgeGap`): clock strip 14.7–16.1, tray 16.3–21.0, then 1.0
of bare tabletop. Tray slots pitch 2.35 across Y against the board's 3.25, so captured pieces
sit closer together than they did on the board — the price of the clock sharing this margin.
**Worth a look**: if that reads as crowded, `TrayEdgeGap` is the knob.

Load-bearing constraints a redesign must not break (all verified, all in CLAUDE.md's `%clk`
section):

- **`TimeControl.Format` truncates, never rounds.** `"{seconds:0.0}"` renders 59.96 as
  `"60.0"`. Explicitly commented.
- **Tenths below `DecimalBelowSeconds` (60f)**, and the host tightens sync from
  `ClockSyncInterval` 0.1f to `ClockSyncIntervalLow` 0.03f exactly there — a tenths display
  on a 0.1s sync visibly stutters.
- **Clients never run their own clock.** They render the host's `[Sync(FromHost)]` copy;
  nobody can flag on lag. Anything that makes the clock feel smoother must not become a
  local countdown. (The TV wall is the documented exception, and the section above is what
  it cost.)
- **Seat lines are hashed as rendered STRINGS** — that is what made the clock repaint on
  visible-text change instead of every frame. **Moved with the clock**: `TableClockPanel`
  hashes its faces as text and the HUD no longer repaints on a ticking digit at all.
- **`PanicSeconds` (10f) is deliberately not `DecimalBelowSeconds`** — one decides when
  tenths are legible, the other when you're in trouble.
- **`SeatLine` checks lichess first**, because a seek leaves `ctrl.Playing` false for its
  whole duration and the host freezes its copy.

Open, and genuinely a taste call: the spectator wall already puts clocks on seat plaques at
28px, green when ticking, **with no panic state at all** — so the wall and the HUD disagree
about whether a clock turns red. Pick one.

### The seated menu (`GameHud.razor`, 856 lines)

**2.1× the next-largest panel** (InfoScreen, 414), rendering 20 distinct elements. It grew a
control at a time across M2, M7, M8, M9 and M10 and has never been laid out on purpose.

**Decided: extract the promotion picker and the setup block, and stop there for now.** Those
two are ~40% of the file and have near-zero coupling:

| Seam | Size | Coupling |
|---|---|---|
| **Promotion picker** (`:268-282`, `:439-474`) | ~15 markup + ~36 CSS | **Zero.** Already a *sibling* of `.hud`. Reads only `View()`. 1 bool in BuildHash. |
| **Setup block** (`:112-191`) | ~80 markup + ~60 code + ~55 CSS | Sole owner of `_seekRated`/`_seekColor`, `TimeControl.All`, every `LichessTable` call, and the `.tc*`/`.chip`/`.seek-row` CSS. Gated by one predicate. |

**What resists splitting, and shouldn't be forced**: the status line (`:37-68`) is one
if/else chain reading `ctrl`, `Lichess()` and `Source()` in the same expression — it is
where the "lichess vs local" axis and the "setup vs in-game" axis genuinely cross. And
`Source()`/`OnLichess()`/`Lichess()`/`Controller()`/`View()`/`Station()` are a shared spine
every extracted panel needs; they become a static helper or every panel re-resolves them.
**The clocks seam is entangled with the mesh-clock decision above and shouldn't be split
before it.**

Two corrections to what this file used to say:

- **It is not "nine controls before you can play"** — that line then listed eleven. Counted
  from the markup it depends on the table, and **the default is the mild one**: Blitz 3+0
  linked shows **7** (`CanSeek` is false at 180s, so the chips and the seek button don't
  render); Rapid/Classical linked shows **10** (was 11 before the rating chip went); not
  linked, **6**. **Design against 10, but the 7-control default is why nobody has felt this
  yet.**
- **`BuildHash` is not "out of room"** — it is out of room *in the two places you'd want to
  add to*, which is a real constraint but a different one:

```
L1  Combine(8)  — FULL (7 values + the nest)
└── L2  Combine(4)  — 4 of 8 used, ROOM FOR FOUR MORE
    ├── L3a table    Combine(7)  — 1 spare
    ├── L3b lichess  Combine(8)  — FULL
    ├── L3c offers   Combine(6)  — 2 spare
    └── L3d seek     Combine(2)  — 6 spare
```

**And there is a live instance of the documented hazard.** `LichessText()` renders
`LichessLinkState.Username`, but **L3b is full and only hashes `.Linked`** — so a username
that changed while linked would render stale. In practice the username arrives as `Linked`
flips, so it repaints. This is exactly the "a value missing from BuildHash is a control that
silently never appears" failure, caught before it bit a third time. **The split is the fix**:
extracting the setup block moves the lichess values into a panel that hashes them flat, which
gives `Username` a home. Don't treat it as separate work.

### The move list

`MoveRows` hard-caps at 12 rows, truncating from the front. `.moves` also has
`max-height: 180px` — **the two caps are independent and neither is derived from the other**
(12 × 13px ≈ 156px + padding, so they agree by luck). `overflow: scroll` is correctly ruled
out (drag-scroll fights clicks — CLAUDE.md).

**Decided: keep the cap, derive the two numbers from each other, and stop calling it a move
list — label it "recent moves".** A full list wants the wall or the archive viewer, not a
250px column. Low priority.

### The wall boards

- **They share a palette but not a design — and not even the palette's own radius.**
  `WallTheme.scss` defines exactly **two** tokens (`$wall-radius: 4px`, `$wall-font`). The
  east-wall trio and `SpectatorInfoPanel` share one scale (7.5px radius, 9/12 padding, 4 gap,
  0.75px dividers). **`WallSettingsPanel` shares none of it** despite the same `BoardScale`
  and clearance — 12/16 padding, 6 gap, 12px body (~2.2×), 1.5px dividers — and is the
  **only panel in the repo that uses `$wall-radius`**.
  **Decided: the east-wall trio's scale is canonical** (it is the majority, and it is what
  `WallBoardGeometry`'s "copyable px" promise assumes). Bring `WallSettingsPanel` onto it and
  **put the type scale in `WallTheme.scss`** next to the two tokens already there.
- **Three different title sizes on one wall** (CenterInfo 13px, Lichess 10px, DevNotes 10px,
  plus Settings' 15px on the south wall), and `CenterInfoPanel`'s `.prompt` is 15px bold —
  *larger than its own title*. Arguably right (it's a signpost; the instruction **is** the
  content) but nobody's decision on record. **Ask separately — that's taste.**
- **`WallTheme` is not a fixed palette, and this constrains every colour decision.** Every
  token derives from `Accent` = `PlayerData.WorldLightColor`, the player-settable room theme
  (fallback `#2f9450`). **Any hardcoded colour added here will not retint with the room.**
  Several already don't: `LichessBoardPanel`'s grey `.title` and green linked state,
  `SpectatorSeatPanel`, `SpectatorFanfarePanel`, `StationScreenPanel`. **Decide per colour.**
  `DevNotesPanel`'s amber is deliberate and documented; `GameHud`'s lichess black/white is
  deliberate ("so it reads as 'this leaves Gambit'") and stays; `LichessBoardPanel`'s grey
  title looks like an oversight. Also unused: **`Cell`, `CellFill`, `AccentBg` are consumed
  by no wall board at all.**
- **Two north-wall panels bypass `WallBoardGeometry`** — `SpectatorSeatPanel` (`PanelSize
  760×200`, own `fitScale`, `PxToWorld = 0.05`) and `SpectatorFanfarePanel` (`4096×1024`,
  pushed off the board face). **Decided: record the exemption in `WallBoardGeometry`'s doc
  rather than "fix" it** — a plaque and a banner genuinely aren't wall boards, and
  `SpectatorInfoBoard` does conform. But note the retune cost: **`SpectatorWall` is not in
  the scene at all**, so a north-wall design pass is an edit-and-hotload loop, not a
  scene-tweak loop, unlike east and south.
- **The fanfare stays at 3s until someone watches it** (`LichessTv.FanfareSeconds`, one
  line). It is 160px headline / 96px reason on a 4096×1024 panel — by far the loudest thing
  on the wall — and it has no sound. On UltraBullet a game ends every ~30s.
- **`SpectatorScreen.razor` hardcodes the channel count in prose twice** ("the channel grid
  is 16 cells over three rows", "there are sixteen"). The Go test that holds the client and
  server channel lists together **does not cover the prose or the `.button.chan` sizing it
  justifies**. **Derive the grid from `LichessTv.All.Count`.**
- **Does the east wall still read as a wall?** Signpost (`YFrac 0.1`) → lichess (`-0.1`) →
  dev notes (`-0.3`), all at `FloorClearance 30` where other walls run 60. `+Y` is left, so
  the signpost is leftmost. The signpost is three lines tall while the other two are full
  boards, and every board bottom-anchors, so the tops are ragged by design. **This one needs
  you standing in the room. No recommendation.**

### Sound — done, and it needs listening to

**Built.** Every board sound now goes through `Gambit.Audio.TableSounds`, a watcher on the
`IBoardGame` seam — so a real lichess game at a table stopped being silent, and a third kind
of game would get all of it for free. The full map is in CLAUDE.md's Sounds section; the
decisions were: check 2D-only, game over 2D + quiet 3D, offers 2D-only, panic 1/sec 2D-only,
`tock3d` added (it was six lines of JSON, not an unmade asset), TV fanfare quiet 3D at the
wall, no separate flag sound (a flag *is* a game over).

**Nothing here has been heard.** All four new sounds are synthesized blind
(`scripts/gen_sounds.py`) and every one is a `gen_*` function away from a retune:

- **`panic` is the risky one** — the first per-second sound in the game. Ten seconds of it
  is either pressure or an alarm, and which one is not a thing anyone can tell from numpy.
- **`check` lands ON TOP of tick/tock** (the move that gives check plays both). It's two
  pips to stay separable from a move by shape; does that work, or is it just clutter?
- **`gameover3d` at 45%** across the room, and on the TV wall — where UltraBullet ends a
  game every ~30s. If the wall is annoying, that number is one line in `gameover3d.sound`.
- **The room with six tables** is the whole gate and can only be judged standing in it.

**Known gap, and it's inherited rather than new: at a LICHESS table the panic beep fires
about once, not once a second.** lichess only sends a clock when a **move** happens, so
`LocalSeatClock` is frozen at the opponent's last move for the whole of your think and the
second never advances. **This is exactly the staleness the HUD's red clock already has
there** — it isn't a regression, it's the same fact becoming audible. The fix is not a local
countdown: that is what the TV wall does, and it read **HIGH** for two milestones while three
places claimed it read low. Beeping at a player who has more time than we think is that
mistake with worse consequences. If it's worth fixing, it wants the same receipt-stamp shape
as the TV clock fix (gamchess stamps, client subtracts), and it wants doing once for both.

**Still open — a taste call in the same family**: the spectator wall's clocks are green with
**no panic state at all**, while the HUD reddens under `TimeControl.PanicSeconds` and now
beeps there too. The wall and the HUD disagree about whether a clock is ever urgent.

### Chat: delete ours and use the engine's — copy `../terryball`, not `../rotaliate-client`

**`../rotaliate-client` is not the reference for chat. `../terryball` is.** This is not a
style preference: **terryball built rotaliate's model, shipped it, and deleted it** — commit
`8ad9f4b`, *"Drop the rotaliate-copied custom chat box; use the built-in chat"*, whose message
reads *"a hand-rolled T-to-open TextEntry chat input hastily copied from rotaliate, which
duplicated the engine's built-in chat overlay… Gut the custom box."* **Gambit is running the
code terryball threw away**, near-verbatim: `client/Code/UI/Screens/ChatPanel.razor` differs
from `../rotaliate-client/rotaliate/Code/UI/Screens/ChatPanel.razor` only by its namespace and
one comment.

**What the two models actually are:**

| | Gambit today (rotaliate's) | terryball's |
|---|---|---|
| `ChatShowUI` | **false** — engine overlay off | **true** — engine overlay on |
| Who draws the feed | us (`_feed`, per-line wrap, timed fade) | the engine |
| Who draws the typing box | us (`TextEntry`, multiline, 256/255 clamp) | the engine |
| Open key | our own `Chat` action read manually (T) | the engine reads `Chat` (Enter) |
| `ChatPanel` | **288 lines** | **73 lines** — a hint label and nothing else |
| Networking | `Chat.Say()` + `IChatEvent.OnChatMessage` | none. No game code at all. |

**Decided: adopt terryball's.** Flip `ChatShowUI` to **true** in `ProjectSettings/Platform.config`,
gut the feed/TextEntry/fade machinery, and leave a hint that points at the built-in overlay.
Gambit already has a `Chat` action (bound to **T**); the engine's overlay reads that action, so
it works as-is — terryball just rebound it to Enter. Take the hint's key label from
**`Input.GetButtonOrigin( "Chat" )`** rather than hardcoding, as terryball does, so the keycap
follows the player's own s&box keybind.

**Keep `ChatPanel.IsOpen` as a stub returning `false`.** terryball did exactly this, on purpose:
the built-in overlay captures input itself while you type, so there is no game-side box to be
open, and every existing input gate keeps compiling. Gambit's one caller is `LobbyPlayer.cs:219`,
which today keeps the controller off while typing — **that gate becomes dead, and that is fine**;
the deleted rotaliate box's own comment records that *"we DON'T disable the player controller
while typing — a focused TextEntry already swallows the WASD keystrokes"*, so it was arguably
never needed.

**Two lies to clear out while in there** (both are exactly the rot this milestone is about):

- **`ChatPanel.razor:16` says the chat key is "rebindable in Settings". It isn't.** `Chat` appears
  nowhere in `SettingsScreen.razor` or `SettingsModel.cs`, and **nothing anywhere writes
  `PlayerData.Bindings`** — the dictionary exists and only ChatPanel ever reads it. So
  `IsChatPressed()`/`ChatKeyLabel()`'s PlayerData branch is dead code guarding a feature that
  does not exist. (`GamepadBindings` is real — `GamepadBinds.cs` reads it. Don't confuse them.)
- The panel's header comment describes a feed and an input box that this change deletes.

**Don't copy terryball's chat *code* — there is none to copy.** The whole deliverable is 73 lines
of hint plus a config flag. If a custom box is ever wanted again, the deleted one is at
`git show 8ad9f4b^:Code/ChatPanel.razor` in terryball and documents two real traps: a multiline
`TextEntry` makes Enter insert `\n` instead of firing `onsubmit` (so "send" was detected by
scanning the text for a newline), and cursor freeing needed `Mouse.Visibility` re-asserted every
frame. **But the reason it was deleted still applies here** — Gambit has the engine overlay
switched off purely so it can redraw it worse.

**Unrelated, despite the word:** "no chat on lichess games" under Known gaps is about relaying a
*lichess* game's chat through gamchess (`board.go` can send; `relay.go` ignores inbound
`chatLine`). It has nothing to do with lobby chat and this change doesn't touch it.

### Premove

- **A dropped premove now says so** — done. Both `FirePremove()`s use the bool they were
  discarding; `IBoardGame.PremoveDropped` stands for `BoardGame.PremoveDroppedSeconds` (4s)
  and the HUD prints "Premove dropped — it wasn't legal any more" in the existing
  `.reason.premove` style. Note it is in `BuildHash` — it self-clears on a timer rather
  than on an event, so it is the one value there that changes with nothing else changing,
  and left out the notice would appear and never leave.
- **"Any click cancels" stays.** It matches click-to-move and **the copy is already
  accurate**: `ChessBoardView.cs:380-383` only clears after `square >= 0`, so a click that
  misses the board does *not* cancel — the HUD's "Click the board to cancel" is true, and
  this file's old "a misclick while idly watching silently disarms you" was wrong. The
  right-click alternative also costs more than a binding: **`Select` (mouse1) is the only
  mouse binding in the entire `Input.config`** — no mouse2, no `Attack2`, no `Cancel` — so
  it means a new input action, a settings row, a rebinding entry and a gamepad story.
  Revisit only if the "premove dropped" line shows people losing them this way.

### Settled, no work

- **45s is the right idle TTL for a TV upstream.** `tvIdleTTL = 45s` swept every
  `tvSweepEvery = 15s`, so the real drop lands 45–60s after the last poll. There is no client
  poll *interval* — it's a continuous long-poll chain (`pollHold = 5s` server-side, under the
  client's 8s timeout), so the normal gap between touches is **~5s + RTT (9 cycles of
  headroom)** and a failing client is **8s + `PollBackoffSeconds` ≈ 11s (4 consecutive
  failures)**. That last case is the one that matters: a viewer who is still there but whose
  polls are failing must not have their stream dropped. Subtlety if it is ever tuned:
  `watch()` touches at poll **start**, not end, so the effective quiet window is ~40s, not 45
  — and tune `tvSweepEvery` with it, since the 15s sweep is what makes 45 mean 45–60.
- **The lichess disclosure copy is intact** (`lichess_pages.go`) — both load-bearing warnings
  present. Don't trim them.

### The music board was busted on joined instances only — fixed, needs confirming

Reported from M10 testing: a **joined** instance (never the first editor instance) pressing E
at the music wall got a completely broken skafinity panel.

**Mechanism, found by reading:** `SkafinityMusicPanel.OnStart` resolves its player **once** —
`Player ??= Scene.GetAllComponents<SkafinityPlayer>().FirstOrDefault()` — and
`GetAllComponents` is **enabled-only**. Miss that instant and `Player` is null for the rest of
the session, at which point every field in the panel renders `Player?.X ?? default`: seed "—",
N 0, empty queue, dead buttons. A whole board of nothing.

**Why joiners only** — and this is the trap CLAUDE.md already documents for M12's voice work,
arriving early: `/UI` (the panel) and `/GameController` (the player) are both **`NetworkMode`
2 = Snapshot**, so a joining client **rebuilds them from the host's snapshot** instead of
loading them the way the first instance does. Different construction order, different answer
from a one-shot first-frame lookup.

**Fixed in `MusicBoardScreen`, not in the library**: it now retries `_panel.Player ??= …` every
frame until one exists — the same shape as the `_panel` lookup directly above it, which already
retries for exactly this kind of reason. The library is source-committed but it is a drop-in,
and its one-shot resolve is only wrong for hosts whose panel outlives an absent player.

**Confirm it**: the old failure logs `SkafinityMusicPanel: no SkafinityPlayer found in the
scene` on the joined instance. If that line is absent and the board still looks wrong, this
diagnosis is wrong and the snapshot is corrupting something else.

### Open — needs a decision

- **Wall board title sizes**: three on one wall. Taste.
- **Panic red vs the spectator wall's green-only clocks**: they disagree — and now the table
  clock reddens *and* beeps, so the wall is the only place a clock is never urgent.
- **The four new sounds**, none of which anyone has heard — see Sound above.
- **The table clock in the room**: it lands in Black's near foreground. See above.
- **One action, two buttons.** `GameHud` and `LobbyOverlay` both render a resign control
  simultaneously while seated in a live game, both calling `LobbyPlayer.Local?.RequestLeave()`,
  both off the same `LeaveArmed` — with **different labels** ("Resign & stand up" / "Sure? This
  resigns" vs "Stand Up" / "Resign & Stand Up?"). Both paths tested and working; not a bug —
  two vocabularies for one action, in a milestone about how it feels.

### Dead code found while reading — proposed for deletion

The ones not already done above. All verified zero-reference.

| Thing | Where |
|---|---|
| `.status.wait` | `GameHud.razor:402` — markup only emits `.status`, `.status turn`, `.status over` |
| `.button.disabled` | `GameHud.razor:431` |
| `.button.ghost` | `GameHud.razor:436` — never emitted *here* (lives in SpectatorScreen) |
| `.note`, `.prizes-notice` | `InfoScreen.razor:231, 326` — `prizes` is a rotaliate fossil |
| `.input` | `SpectatorScreen.razor:161` |
| stale CSS comment | `InfoScreen.razor:321` — `/* The 10+0-only caveat stands out in amber. */` sits above `.section-header.warn`, but the only `warn` markup today is the **unlink** section. A fossil of the exact "RIGHT NOW: 10+0 GAMES ONLY" lie this milestone exists to catch. |

### Small copy items

- **`InfoScreen:118` says "the board next door"; `GameHud:134` says "the east wall"** — same
  board, two descriptions. Neither is false. Pick one.

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

## M12 or later — proximity voice, copied from `../terryball`

A chess lobby is a social room and people sitting opposite each other should be able to talk.
**terryball has a complete, working proximity-voice implementation and it is the reference** —
the same rule as chat above: copy terryball, not rotaliate-client (which has no voice at all).

**It is small.** Four files, ~350 lines total, and one of them is 23 lines:

| File | Lines | What it is |
|---|---|---|
| `Code/TerryVoice.cs` | 23 | `sealed class TerryVoice : Voice` — the entire networking model |
| `Code/VoicePrefs.cs` | 61 | `Cookie`-backed, client-local prefs. Never networked. |
| `Code/VoiceScreen.cs` | 222 | client-local keyboard driver; pushes transmit config each frame |
| `Code/VoicePanel.razor` | 246 | the roster + speaking dots |
| `Code/AlleyServer.cs:88-95` | 8 | host-side: create the component on the avatar, then `NetworkSpawn` |

**The model, and why it's the elegant part: game code networks nothing.** Playback is gated on
the **receiver**, in the only two virtual hooks the engine gives you:

```csharp
protected override bool ShouldHearVoice( Connection c )
    => VoicePrefs.VoiceEnabled && !VoicePrefs.IsMuted( c.SteamId );
```

`ShouldHearVoice` runs on the **receiver** when a packet arrives, carrying the **sender's**
connection — so reading *our own* prefs there is exactly right, and mute needs no sync, no
authority, and no server state. Transmit is gated separately and owner-locally by driving
`Voice.Mode`; **never touch a networked Enabled flag.**

**Fits Gambit cleanly.** Voice hangs off the player avatar, and `LobbyNetworkManager` already
clones + `NetworkSpawn`s `PlayerTemplate` per connection — that is the same seam as terryball's
`SpawnAvatar`, so the component goes on there. Identity is SteamID, which Gambit already has
everywhere.

**The gotchas, which are the reason to copy rather than write:**

- **`Voice.OnUpdate` is sealed in the engine.** You cannot override it. Only the hear/exclude
  hooks are virtual — don't design around intercepting the update.
- **The default `Falloff` curve is a trap.** It is *savagely* front-loaded — down to ~4% volume
  by 20% of range — so voices go near-inaudible a couple of body-lengths apart, which reads as
  broken voice rather than distant voice. terryball replaces it with a **linear** curve
  (`new Curve( new Curve.Frame( 0f, 1f ), new Curve.Frame( 1f, 0f ) )`), plus a scale-appropriate
  `Distance` and `Volume = 2f` for headroom. **The engine's default `Distance` of 15,000u is also
  wrong for any room-sized scene** — Gambit's room is 800 units, so this needs its own number,
  not terryball's 3000.
- **`V` is the engine's default push-to-talk key**, so it cannot be your toggle. terryball uses
  `G`. Gambit's free keys need checking against `Input.config` — note `use` is E and `view` is C.
- **A never-bound sentinel action is how you hard-gate transmit closed.** `PushToTalkInput` set
  to an action that does not exist means `Input.Down` is always false. Cleaner than trusting a
  flag.
- **The engine has the last word on the mic.** `Voice.IsListening` honours the user's s&box
  `voip_mode`, which game code cannot change — so a UI here surfaces and gates our side of it and
  must not claim to be the switch.
- **Menu keys leak into the world.** While a roster is open, Enter would open the built-in chat
  overlay (it reads the action in a later UI tick — `Input.Clear( "chat" )`) and Space would jump
  (`UseInputControls = false`, which gates movement only; look and camera stay live). Track the
  restore with its own flag and undo it in `OnDisabled`/`OnDestroy`, so a destroy mid-roster still
  hands control back.
- **`WorldspacePlayback = true`** is what makes it positional; `Renderer = <the citizen renderer>`
  gets lip-sync visemes free.
- **Speaking indicator** is just `voice.LastPlayed < 0.3f`.
- **Master default is OFF.** Do not turn a chess lobby's mics on by default.

**Don't copy terryball's comments blindly — some are stale.** `VoiceScreen.cs:10`,
`VoicePanel.razor:17` and `LocalHud.cs:59` all say "V toggles voice" while the code binds **G**;
`AlleyServer.cs:93` sets `Distance = 3000f` while its own commit message claims 4000u. Read the
code, not the prose — the same rule this repo keeps re-learning.

**What to strip when copying:** the first-run help pop-up in `VoiceScreen` (`RequestOpenHelp`/
`HelpOpen`) is unrelated to voice; `LocalIsBowling()` exists only because G is overloaded as the
bowler's exit key; `TerryAvatar`/`ControlsOpen` lookups become Gambit's `LobbyPlayer`. Also note
terryball's warning that `INetworkListener` **fires on every component that implements it** — with
N lanes, each joiner got N avatars. Gambit has one `LobbyNetworkManager`, so this is a trap to
avoid re-introducing, not one to fix.

**And the HUD-parenting rule, which cost terryball real bugs:** a client-local HUD must not hang
off a networked object, or the host's mute/panel state rides its network snapshot onto every
client. But a hand-placed `NetworkMode.Never` object never reaches joiners either, because a
joining client rebuilds the scene from the host's snapshot and that **excludes `Never` objects** —
terryball solves it with a `GameObjectSystem`. Gambit's screens self-attach at runtime
(`LobbyPlayer` → `ScreenPanel`), which sidesteps this, but **anything voice-related that gets
parented into the scene needs this thought through.**

**Open before starting:** whether voice is proximity-only or also table-scoped (two players seated
opposite each other are ~50 units apart in an 800-unit room — proximity may already be exactly
right, or may make the whole ring one conversation). That is a taste call and a loud one, in the
same family as the sound decisions in M11.

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
- **No berserk, no chat on lichess games.** Both exist on the Board API; neither is wired up —
  gamchess *can* send (`board.go`) but inbound `chatLine` is explicitly ignored (`relay.go`) with
  no client UI. **This is not lobby chat** and has nothing to do with the M11 chat change above:
  it is relaying a *lichess game's* chat between the two players of that game.
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
  seek cap), but days-per-move doesn't fit sitting down at a table. Note it is also the one
  seek whose `ratingRange` really is unbounded by default — `Seek.scala` has no Gaussian
  fallback, unlike a real-time hook.
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
real first step is to open it and decide what it should be.

**M11 does the client only, and the viewer inherits afterwards — that ordering is a
constraint, not a preference.** "Inherit the client's vocabulary" **cannot mean inheriting its
colours**: the client's `WallTheme` is *dynamic* (every colour derives from the player's room
theme) and the viewer's palette is fixed gold `#c9a227` on `#14151a`. The client has no fixed
colours to inherit. It can only inherit the **type scale, spacing and radius** — which is
exactly the part `WallTheme.scss` doesn't define yet. **So landing that type scale is the
prerequisite**, and doing both halves at once would mean designing a vocabulary and applying it
twice before anyone has looked at either.

What's already known to be weak:

- **The CSS has barely been exercised.** The board squares resized to fit whichever piece stood
  on them until 2026-07-15 — an 8×8 grid that wasn't actually holding a grid. That bug
  surviving this long says the styling has had no real scrutiny.
- **Never checked narrow / mobile — and there is literally no narrow branch to check.** All 217
  lines of `style.css` contain **zero `@media` queries**. `tbody td { white-space: nowrap }`
  across four fixed columns means the games table will **overflow horizontally rather than
  reflow** under 400px. The board itself is fine (`width: min(28rem, 100%)`, and `.piece` uses
  `clamp(1rem, 6.5cqw, 2.4rem)` container-query units) — so someone did do responsive work
  there, just not on the table.
- **`color-scheme: light dark` is declared but every var is a fixed dark value** — the
  declaration is inert and misleading.
- **The games list shows Played / White / Black / Result only.** No time control, though the PGN
  now carries one. Adding a column means touching `index.html`'s `<thead>` too.
- **Game meta is one text line** — date, then the time control tacked on after a `·`. Fine as a
  stopgap; not a design.
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
