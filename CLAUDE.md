# CLAUDE.md — Terry's Gambit s&box Client

**Terry's Gambit** (repo/ident: `gambit`, org `gamah`, namespace `Gambit.*`) — chess in a social
s&box lobby, backed by **gamchess**, our own Go/Postgres service. Forked from rotaliate-client:
the walk-around lobby, station ring and networking scaffolding are inherited; the arcade game and
its Go backend were replaced by chess boards and gamchess.

This file is the durable reference for **how the game is built**. Four siblings carry the detail,
and each is the authority on its subject — **read the one your change touches, in full**:

| File | What's in it |
|---|---|
| **`LICHESS.md`** | Playing real lichess games: token custody, scopes, the four flows, the traps, lichess TV, API etiquette. |
| **`GAMCHESS.md`** | The Go backend: identity/auth, the game session, HTTP facts, deployment. |
| **`SBOX-NOTES.md`** | Engine lore Gambit paid for: the network snapshot, world scale, UI/WorldPanel gotchas. |
| **`TERRY-HALFRISE.md`** | The M14 seated-hands mechanism and its `gambit_terry_*` knob reference. |
| **`PLAN.md`** | **Only upcoming work and open issues** — read it for what's left, never for how things work. |

`PLAN.md` follows the global flat-ranked-backlog format; this repo is where that format came from.
Worth knowing here: **grouping rows into branches is a judgement call** — several small rows on one
wall or panel are usually one branch; one big row (chat, voice, the viewer) is usually its own.

---

## Cutting a release (sbox.game)

Version scheme is **Alpha 0.0.x**. `0.0.1` was 2D play mode; `0.0.2` bundled M17 + M18 plus PR #17.

> **`0.0.3` is unpublished and already carries P99, M19 and HTTPFIX** — look-aim at the board;
> play the computer, cross-lobby matchmaking, the four-tab setup panel, the move-history panel; and
> the lichess token moving onto the player's own PC. **HTTPFIX is player-facing even though it reads
> as plumbing**: everyone re-links, the grant asks for more than it did, the key now lives on their
> machine, and a crash mid-lichess-game flags instead of resigning them. The last two belong under
> Known issues. **PR #18 merged with NO release-notes section**, breaking the roll-up rule below —
> so the cutter writes M19's notes from the commit log, and should not assume the absence of notes
> means the absence of player-facing change. It is the largest chunk of the release.

The "changes" field is written in **five sbox.game categories, in this order**:

> **Added · Improved · Fixed · Removed · Known issues**

- Write **player-facing** notes — what changed in the *game*, not the code. "Play from multiple
  tables at once", not "gated `LocalSeat` on occupancy".
- **Every feature branch's PR carries its own draft notes in these five categories** — a
  `## Release notes (Alpha 0.0.x)` section in the PR body. A release is usually several merged
  branches, so **the session that cuts it rolls up the open PRs' notes** (concatenate + de-dup)
  rather than reconstructing from the commit log.
- **Known issues** = deliberate limitations and shipped-but-unverified items worth flagging — *not*
  a bug backlog (that's PLAN.md). Say what a player would actually hit, and whether it's by design.
- This dev host has no s&box toolchain, so **client changes are review-only until tested in the
  editor**. Each PR lists its "needs editor verification" items; the cutter confirms that list was
  walked before publishing.

---

## What runs on this host

No *engine* code compiles or runs here. **Four things do, and all four are gates worth using:**

- **The Go server.** Go 1.22 in `~/.local/share/toolchains/`; `go build/vet/test ./... -race` all
  pass. `server/` is fully testable — do not claim otherwise.
- **`node scripts/chess_js_perft.mjs`** — the web viewer's chess rules.
- **Sandbox-free C#** via a scratch csproj. `dotnet` (10.x) lives at
  `~/.local/share/toolchains/dotnet10/` (put it on PATH for the command; not on the default PATH).
  Everything under `Code/Chess/` — plus `Code/Game/TimeControl.cs` and `Code/Game/LichessTable.cs`
  — has no engine dependency. Two settings matter: `<TargetFramework>net10.0` (net8 builds but
  won't launch — only the 10.x runtime is here) and `<ImplicitUsings>enable`, because the vendored
  library leans on s&box's global usings for `System.Collections.Generic`.
- **A SHIM**, when the engine surface is small. `Code/World/SeatAim.cs` touches `Mouse`, `Input` and
  `Screen`, so it can never move under `Code/Chess/` — but ~50 lines of stand-in compile the **real
  file** verbatim and run its whole truth table. Committed as `scripts/seataim_harness/`
  (`dotnet run`). Worth doing when the logic is a state machine; worth NOT doing when the shim would
  have to reimplement engine behaviour to be meaningful, because then it only tests the shim.

**This cuts both ways: it is worth MOVING code to make it testable.** `Code/Chess/
CapturedMaterial.cs` takes a plain `char[64]` specifically so it can run in a harness — driving
real games through `ChessGame` immediately proved a capture-promotion line that the naive
start-minus-current diff gets wrong in both directions at once. Left as a private method on a
`Component`, none of it could have been run here.

---

## Project setup (first time on a new machine)

s&box's package manager tracks local projects in its own registry — cloning the repo and opening
the `.sbproj` directly fails with `Unable to find package 'local.gambit#local'`.

1. Editor → **New Project** → Game (Empty), pointed at the repo's **`client/`** folder (not the
   repo root — `client/` is the s&box project root).
2. The editor writes its own `.sbproj` and registers the project; use that file, not the one in the
   repo.
3. The editor hotloads C# automatically — check the error list.

The registry tracks projects **by path**, so the M7 `gambit/` → `client/` rename means re-running
this once even on a machine that already had the project. **Migrating a machine that predates the
rename — do both, or you get a black screen:** delete the orphan `gambit/` folder (`git mv` only
moves *tracked* files, so the rename leaves every gitignored artefact behind as a source-less husk
holding a stale compiled assembly — confirm `git ls-files gambit/` and `git status --porcelain
gambit/` are both empty first), and unregister the old `gambit/` project in the editor before adding
`client/` (two registry entries both claiming ident `gambit`, and the editor may open the husk —
which builds the world but renders nothing).

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
server/                ← the gamchess Go/Postgres backend
```

Each half ignores its own build output; the root `.gitignore` holds only repo-wide junk. Unanchored
`bin/`/`obj/`/`*_c` entries must never go back in the root file — they match at any depth and would
swallow `server/bin/`. **Paths in csproj/slnx** assume Steam at `D:\Steam\`; the editor regenerates
them.

### The vendored skafinity library

`client/Libraries/gamah.skafinity/` is source-committed and updated by **installing the published
package in the editor**, not by hand-copying from `../skafinity` — that sibling repo may be ahead
of, or diverged from, whatever version is actually published.

**The known failure is an APPEND-ONLY install**: the new version's new files land beside the old
version's stale ones, and the compiler reports a wall of "does not contain a definition for" —
the new half naming members the old half doesn't have yet. It has happened **twice** (the flat
`MusicGen.cs` beside `Code/Engine/`, then the board rewrite beside the pre-rewrite
`GenreProfile`/`Pattern`/`DrumGroove`/`VibeCodec`).

**Recovery is the same either way: delete the whole folder, commit the deletion, then install.**
With nothing on disk there is nothing for the download loop to skip, so the manifest lands whole.
That is a reliable fix and does not depend on knowing the cause — which is just as well:

> **WHY the SECOND one appended is still unexplained, and the obvious answer has been ruled out.**
> `LibrarySystem.Install` (`sbox-public`, `engine/Sandbox.Tools/Editor/LibrarySystem/`, read
> 2026-08-13) gates its prune pass on `gamah.skafinity/.version` existing, and the download loop
> `continue`s past any file already on disk — so "no `.version`, therefore append-only" is the
> natural reading, and it explains the FIRST incident. **It does not explain the second: `.version`
> was present and tracked** (`6b41555`). Nor is the mechanism itself suspect —
> `../rotaliate-client` ran the same update from the same one-revision-behind state with its
> `.version` in place and **reconciled cleanly** (23 files replaced, 5 added, `1.0.338456` →
> `1.0.341414`), so the prune does what the code says. Whatever went wrong was local to this
> machine or this checkout: something else gated the pass, or the `.version` the installer consults
> was not the one in the repo.
>
> **So: do not add a second `.version` if it happens again, and do not blame the mechanism.**
> Delete the folder, commit, install — the recovery above works without a diagnosis. If you want
> one, the thing to establish first is which `.version` path the installer actually reads, since
> that is the only untested link left.

**ONE file deliberately differs from the package**: `Code/Skafinity.csproj` is not committed (the
solution generator rewrites it per machine with absolute Steam paths, and `.gitignore` already
excludes `Code/*.csproj`). **Everything else is the package as installed — commit it verbatim.**

> **`SkafinityMusicPanel.razor.scss` used to be moved out to `client/Code/UI/`. It isn't any more
> (2026-08-13) — leave it in the library.** That patch created two files at one resolved path with
> nothing keeping them equal, so every update left the game copy describing the *previous* board
> and the panel drew with no layout at all. **The reasoning, and the editor-hosted-join cost the
> revert accepts, are in `SBOX-NOTES.md` — read that before reinstating it.**

---

## Architecture map

### The world

> **If you change how the world behaves, the info boards are part of the change.** Two places
> describe the game to a player, and a change that doesn't update them ships a lie:
> **`CenterInfoPanel.razor`** (the east-wall board, short version) and **`InfoScreen.razor`**'s
> Welcome branch (walk up, press E — long version).
>
> Both drifted for entire milestones: the Welcome page announced **"RIGHT NOW: 10+0 GAMES ONLY"**
> and listed on-board clocks under **COMING SOON** long after both shipped. Nothing else fails — no
> test breaks, nothing looks wrong in a diff, and the only person who finds out is a player reading
> the front door. Ask explicitly whenever you touch: seating or turn order, time controls, the
> spectator wall's sources, lichess, the archive, or anything a newcomer is told. **"COMING SOON"
> is the highest-risk copy in the repo** — a promise with an expiry date and no alarm.

- `LobbyRoom` self-provisions: it adds `ChessRing` if the scene lacks one, and
  `EnsureSpectatorWall` builds the **north-wall** spectator board (it was the west wall until M5;
  `SpectatorWall`'s own comment is the truth, and the player-facing copy said "west wall" long
  after it wasn't). Both self-heal, so **no scene rewire is needed** for these components.
- `ChessRing` builds the ring of tables (`BuildChessTable`: table, board frame, 64 cells, two
  capture trays, pieces at the start position, two camera anchors per station) and network-spawns
  the stations. It owns the screen-rect UI math (`ScreenFractionRect()` / `UiRectStyle()`).
- **The tabletop margin is allocated, not slack** (M11), and the Y margin's budget is **DERIVED,
  not typed**. `TopSizeX`/`TopSizeY` (40 × 44) minus the 29-wide board frame gives every margin a
  job: **−Y is the clock strip then White's tray**, **+Y is Black's tray** (number plaque hanging
  below its edge), **±X are kept clear — they are the seat cameras' sightlines.**
  `ClockBoardGap` / `ClockDepth` / `ClockTrayGap` / `TrayEdgeGap` are the whole Y budget;
  `TrayInnerY`, `TrayCenterY`, `TrayWidth`, `TraySlotPitchY` and `ClockCenterY` derive from them.
  Not tidiness: the tray slab used to be `TrayCols * cell + 1`, which at these numbers is *exactly*
  the 7.5 margin, so it ran flush from frame to table edge with no gap anywhere, on both sides.
  **Nobody chose that; it is what the expression happened to equal.** Change one constant now and
  everything else moves. **Don't put anything new on the tabletop without checking which margin it
  lands in.**
- **Neither X margin is neutral ground**: −X is exactly where White's seat camera looks down the
  board from, +X the same for Black. Anything mid-edge there is in a player's foreground. The clock
  was built at +X with a face per seat and read as a wall in Black's face; it is now **beside** the
  board at −Y with **one** face angled up across it — which is where a real chess clock goes, and
  why one face serves both seats: neither is square to it, both are looking down at the table.
  → **One thing lives in an X margin** (issue #28): the seated player's own BOARD SETTINGS plate,
  in their **near-left corner**. The sightline rule is about anything standing MID-EDGE in a
  player's foreground. This is 0.3 thick, flat on the tabletop, behind the near rank, and
  client-local so there is only ever one. The trays and clock strip run 26 in X (±13), so nothing
  else is out there.
- **The clock plates' HEIGHT is a geometric constraint, not a style choice.** Tilted up out of a
  1.4-deep strip, a plate's height projects `sin(tilt)` of itself back into Y: tall + steep leans
  out over the board and clips the a-file. `ClockPlateHeight` and `ClockFaceTilt` trade off exactly
  and **neither is tunable alone** — 2.4 at 30° spends ±0.6 of the strip's own ±0.7. The plate's
  **pixel** space is not a second knob: `ClockPxHeight` is *derived* from the plate's aspect, so the
  panel and its mesh cannot drift out of proportion.
- **Each player's captured pieces sit in a tray on their own right** (White faces +X, so White's
  right is −Y — s&box is Y-left). `ChessRing.TraySlotLocalPosition` owns the geometry;
  `ChessBoardView` the ordering; **`Code/Chess/CapturedMaterial.cs` what's in it, derived from the
  FEN alone — never from a tally of captures.** Load-bearing: `ChessBoardView` rebuilds from the FEN
  and has no history, so an event-counted tray would be empty for every late joiner and every
  resync. The capture animation is a transient overlay; the tray adopts the dying piece's
  GameObject when the diff has one and spawns it in place when it doesn't. **Tray geometry must
  never be named `Cell …`** — `ChessBoardView.ResolveCells` prefix-scans the Table's children for
  exactly that.
- `ChessSetBuilder` lathes each piece as a runtime mesh. `BuildPiece(type, color, scale)` first
  tries `Model.Load("models/chess/{type}.vmdl")` and falls back to procedural — dropping in a real
  piece set later is a one-function swap (**D5**).
- `ChessStation` holds two-seat occupancy: `[Sync(FromHost)] WhiteSteamId`/`BlackSteamId` (+
  `WhiteName`/`BlackName`), claimed via `[Rpc.Host] RequestEnter(seat)` first-wins with loser-side
  reconciliation (**D1**). Seat cameras orbit the board centre
  (`SeatOrbitRadius`/`SeatPitch`/`SeatLookDownAngle`). You take the side you walk up to; leaving a
  live game is a two-stage resign (Escape/Leave twice).
- **Wall boards go through `WallBoardGeometry` — all of them.** It owns the size (`BoardScale`),
  the aspect (`Stretch`) and the shared floor anchor (`FloorAnchor`, called per-frame from each
  board's own `OnUpdate`). Boards match each other because they share a default `PanelSize` (hence
  one intrinsic pixel space, hence copyable px font values), lay out `height:auto`, and anchor their
  content's BOTTOM edge — break any one and the board stops matching. **Every board that has ever
  looked wrong here looked wrong because it hand-rolled its own scale instead** (the M8 lichess
  board shipped with an invented `(1, 1.3, 1.1)`), and a board that skips the seam cannot be fixed
  from it. Floor *clearance* is deliberately NOT in there — it really does differ per wall (east 30,
  the others 60) — so it stays a per-board `[Property]` the wall passes in. **Adding a board means
  adding its `YFrac` to `lobby.scene` too**: `InfoWall` is a serialized component, so a new
  `[Property]` gets the code default while the ones already in the scene get the scene's — which is
  how the lichess board came to sit on top of the dev-notes board.
- **Every outbound link lives on the MORE board**, on the south wall in the slot the music board
  used to hold (`SettingsWall.MakeMoreBoard`, `MoreBoardPanel`, `InfoScreen`'s `StationKind.More`
  page). It carries an `InfoStation`, not a `SettingsStation`: it sits on that wall because of where
  it is, not because it is a setting, and reusing `InfoStation` means E, the freed cursor and Escape
  come for free. The welcome board used to end with the Discord invite and no longer does — one
  place to reach us beats a link stapled to the end of a how-to-play page. Three links: the invite,
  our other games, and **music by Skafinity** (`https://github.com/gamah/skafinity`). Each has its
  OWN `SinceCopied` clock (`DiscordButton`, `GamesButton`, `SkafinityButton`), because a shared one
  flashes the wrong confirmation. **There is no API to open a URL** — see `SBOX-NOTES.md` for why
  the full URL must be printed beside every copy button.
- `FloorCheckerboard` bakes a `PopMap` (checker colour) plus a `GlyphMap` (R = glyph index 0–6, one
  texel per cell). `floor_checker.shader` looks the piece up in `Assets/textures/chess_glyphs.png`
  and blends it over the square in the **opposite** colour (**D6**). Pops land on both square
  colours, round-robin over the 6 types. If the atlas fails to mount, no glyph indices are written →
  plain checker floor, never solid-square artefacts. Regenerated by `scripts/gen_glyph_atlas.py`
  (our own DejaVu Sans raster — CC0-clean, provenance in `Assets/ATTRIBUTION.md`).
- **Seated bodies (M13) and the hands that play the moves (M14) both SHIPPED** (M14 passed the
  owner 2026-07-19); what's left is knob tuning, a PLAN row. Gated behind **`ChessRing.TerrySeated`**
  — false is a full revert to the pre-M13 "don't draw the local avatar while seated" world, and it
  must stay a kill switch (commit `0f68c91` is why); the hands add `gambit_terry_hands` →
  `gambit_terry_rise` under it. Seat/chair knobs are **code defaults on a runtime-built `ChessRing`**
  (edit-and-hotload, not scene tweaks); hand knobs live on **TerryTuning in lobby.scene** — **scene
  values RULE there**, so a new code default on a serialized slider silently does nothing.
  **Mechanism, geometry, the reach/half-rise reasoning and the full knob reference are in
  `TERRY-HALFRISE.md`.** The bodies and hands are cosmetic — no player-facing copy describes them,
  so none went stale.

### Chess rules (D2)

- Gera Chess Library (MIT, `d4f3f69`) is vendored+patched in `Code/Chess/Vendor/` — regex/Task/
  Span/reflection stripped for the whitelist, every change marked `GAMBIT VENDOR PATCH`. Verified
  here via a dotnet harness mirroring s&box compile settings: perft depths 1–4 on six reference
  positions, upstream's 67 xunit tests, 32 wrapper tests.
- Most vendor patches only *remove* off-whitelist constructs, but **two add behaviour**:
  `Move.Comment` and the `PgnBuilder.BoardToPgn` line that emits it, which is how `{[%clk]}` reaches
  the PGN. Both are marked and both no-op when no comment is set, so an un-annotated game still
  serialises byte-for-byte as upstream did.
- **`Code/Chess/ChessGame.cs` is the only seam callers may touch.** It caches
  `Fen`/`LastMoveUci`/`MoveCount` between moves so per-frame polling is free.
  `TryFromPgnAtPly(pgn, ply)` / `TryFromPgn(pgn)` reconstruct a position from movetext;
  `SetMoveComment(ply, text)` / `ClkField(seconds)` write the clock annotations.
- Re-prove the rules before trusting a gate via the dotnet harness or `chess_js_perft.mjs`
  (`ChessGame.Perft` is still there; only the in-sandbox `gambit_perft` command was dropped).

### The built-in engine (M19: play the computer)

`Code/Chess/ChessEngine.cs` is a `partial ChessGame` — negamax + alpha-beta over the SAME vendored
move generation, so it is **the one place allowed to touch `_board` and `UciOf`**. Sandbox-free, so
a full game of it runs in a scratch csproj in seconds; keep it that way.

> **The trap that cost a crash: the vendored board REFUSES to move in a drawn position, and a draw
> rule can fire while legal moves remain.** `ChessBoard.Move` throws `ChessGameEndedException`
> whenever `IsEndGame`, and `EndGameProvider.ResolveDrawRules` sets that for **repetition,
> fifty-move and insufficient material** — none of which empty the move list. So `moves.Length == 0`
> does NOT mean "no more moves are playable", and any code that plays moves on `_board` in a loop
> must check `IsEndGame` too. The search hit it in quiescence first (resolving captures reaches
> repetitions and bare kings fastest) and threw at move 36 of an ordinary middlegame. Both recursion
> points now score such a node as **0**, which is what a draw is worth — a search improvement, not a
> guard. **It survived review and shipped in the merged branch** because the harness only ever ran
> short tactical positions, never a game long enough to repeat: if you touch the search, play FULL
> games, not puzzles.

- **The bot is a host-driven virtual seat** (SteamId 0, difficulty `[Sync]`ed on `ChessStation`) —
  which is why the seat/ready/abandon logic counts a bot as filled and ready, and why a bot clears
  when the human stands (a bot never sits alone).
- **The search runs off the main thread** (`GameTask.RunInThreadAsync`) over a **THROWAWAY position
  rebuilt from the FEN**, never the live `Game`, so it cannot race the main thread's reads. Bounded
  two ways — fixed depth per level and a hard node cap — so it can't hang a frame; Hard's worst
  measured ~700ms in a busy midgame.
- **That think time is charged to the bot's own clock**, so a Bullet-vs-Hard game may occasionally
  flag the bot. Fair and beatable-on-time, by design.
- **A bot game is never a lichess game** and runs at any speed, Bullet included — it clears none of
  lichess's speed floors because it never reaches lichess. The difficulty ladder lives in
  `ChessEngine.Config`, the think-pose in `LocalGameController.BotPoseSeconds`.

### PGN clock annotations (`%clk`)

`{[%clk H:MM:SS[.ff]]}` per move, plus a `[TimeControl "180+2"]` header (seconds+increment; `-`
when untimed). **The one format Gambit shares with the outside world**, so it follows lichess's
rather than inventing one. Verified 2026-07-15 against two independent implementations that agree —
lichess-org's own **dartchess** and **python-chess**:

- Hours unpadded, minutes/seconds zero-padded to two, fraction optional, **trailing zeros
  stripped** — a whole second is plain `0:03:00`, and `.70` is written `.7`.
- Both readers cap the fraction at **three** decimals. We emit at most **two** (centiseconds): a
  third digit is false precision when the clock is decremented by a ~16ms frame delta, and lichess
  itself keeps clocks in centiseconds. Two is a strict subset, so both still parse it.
- `ChessGame.ClkField` **rounds**; `TimeControl.Format` (every live clock) **truncates**. Not an
  inconsistency — **a live clock must never read higher than the time actually left**, whereas the
  archive should match the reference writers.

**A live clock is rendered on the TABLE, not the HUD** (M11): a low strip in each table's **−Y**
margin (never +X — that is Black's seat camera's sightline), carrying **two mesh plates and a mesh
material bar** that all share one upward facing across the board (`ChessRing.BuildStationClock` +
`World/TableClock.cs` + `UI/TableClockTextPanel.razor`). One facing serves both seats because
neither player is square to it and both are looking down at the table — two dials on one body, as a
real chess clock is. It was text in a 250px column pinned to the right of the screen while the board
sat in the middle of it — in a 3+0 game, the wrong place for the number that ends the game.

Two things moved with it and must move back if it ever does: **`TimeControl.PanicSeconds`** (where a
clock reddens — shared with the panic beep so the two can't disagree, which is why it lives on
`TimeControl` rather than on a panel), and **the string-hashing** — clock faces are hashed as their
RENDERED TEXT, so a panel repaints when a digit changes rather than every frame. Hash the raw float
and every live table in the ring repaints continuously. The HUD now has no clock on it and no panic
red: reddening a *name* next to no number is an alarm about something that isn't on the screen.

Clocks are stamped by the **host** (`NetClockStamp`), never read from a client's own synced copy —
that copy lags the increment. The `chess_js_perft.mjs` gate holds the JS parser to real C# writer
output, including a sub-second bullet fixture; both fixtures were captured from the dotnet harness,
so regenerate them there rather than hand-editing.

### Game controllers (per-station, added by ChessRing beside `ChessStation`)

`Game/IBoardGame.cs` is the render/drive abstraction; `ChessBoardView` renders the active source
through **one** shared resolver, `BoardGame.Source( local, lichess, relay )`. The seam paid for
itself twice: M8 added a whole second kind of game with no renderer change at all, and M19 a third
by growing that resolver one argument.

> **`relay` is an OPTIONAL argument, and that is a live hazard.** A two-argument call still compiles
> and silently answers `LocalGameController` during a relay game — a shell, exactly as it is during
> a lichess game — so a feature reading it is wrong by construction with nothing looking wrong in
> the diff. That is how P99's look-aim gate shipped dead for relay games (fixed in `3d02a8b`).
> **Pass all three, always.** Still on two at the time of writing, each a known cosmetic gap rather
> than a decision: `SeatedTerry` (the hands don't animate in a relay game),
> `LobbyPlayer.PremoveAt`/`GamesInPlay` (the roaming premove reminder misses one), and
> `GamchessCommands` (dev console).

Anything reading the position should go through it — `GameHud` and `Audio/TableSounds` do too, all
three resolving `Source` with that identical expression on purpose: **what you see, what the HUD
says and what you hear must be the same game.**

**But the seam only protects what is actually ON it.** Sound wasn't — it hung off
`LocalGameController`, and so a real lichess game at a table was completely silent from M8 to M11
with nothing looking wrong in any diff. `GameOver`, `LocalSeatClock` and `PremoveDropped` are on the
seam for that reason: each is something a reactive feature would otherwise read off
`LocalGameController`, where during a lichess game it is **wrong by construction**.

| Controller | Networked? | What it does |
|---|---|---|
| `LocalGameController` | host-folded `[Sync] BoardFen`/`Phase`/`ClientGameId` | the two-seat game at a table, and the archive upload (**D7**) |
| `LichessGameController` | **participants STREAM lichess directly; spectators MIRROR (M14)** — each participant holds its own `/api/board/game/stream/{id}` and `[Rpc.Host]`-reports its observed move list into `[Sync] MirrorMoves/MirrorLive`, from which every non-engaged client rebuilds a display game via the same IBoardGame seam. Before this a lichess game was INVISIBLE to every non-participant, solo flows especially. **The mirror was UNTOUCHED by HTTPFIX**, because it was always fed by the participant's own observations rather than by gamchess | a real lichess game on this table (**M8**, rebuilt by HTTPFIX). Adjudicates nothing — lichess is the only authority, and the position is rebuilt from the UCI list it sends — but it DOES run the ticking seat's clock down locally between moves (**M12**), because lichess only sends a clock on a move and a frozen clock reads as a stopped game. The staleness apparatus went with the poll (`_version`, `_bankLag`, `_lastRoundTrip`, `clock_age_ms`/`hold_ms`): a stream has no hold to measure and no cursor to reconcile, and keeping it would reintroduce the M11 sawtooth. What is left is a fixed `ClockLeadSeconds` undershoot; a local clock hitting 0 clamps and waits for lichess to call the flag |
| `RelayGameController` | polls gamchess's relay for a live cross-lobby game | a game against someone in ANOTHER lobby, paired through gamchess's directory (**M19**) — or yourself across two hosts. Not lichess: gamchess is the authority and the whole exchange is ours. Colour is **assigned at random by the server**, never chosen |
| `SpectatorController` | reads the host-folded FEN; holds the TV socket | north wall: cycles live tables, then lichess TV (**M9**) |

**While a lichess game runs, the local controller is a shell** holding the seats and the
`ClientGameId`. Its `ChessGame` never advances (moves go to lichess, not `NetChessMove`), so its
clocks and result are stale by construction — the host stops ticking them (`HostTickClocks`
early-returns on `LichessGame`) precisely so it can't flag a player who is fine on lichess's clock.
**Anything reading a clock, a turn or a result during a lichess game must read the lichess source,
not `ctrl`.**

### Networking (D7)

- `LobbyNetworkManager` (`ISceneStartup.OnHostInitialize` → `Networking.CreateLobby`) hosts;
  joining peers never fire that event. Players spawn by cloning the disabled in-scene
  `PlayerTemplate` GO (no `.prefab` asset — hand-authoring the format is undocumented) and
  `NetworkSpawn(connection)`.
- **The host's own avatar spawn must be deferred.** `OnActive` fires for the host *during*
  `Networking.CreateLobby`, before its connection settles, so a spawn there never makes it into the
  snapshot sent to later joiners — joiners saw every client but the host. `OnActive` detects
  `connection == Connection.Local` and defers the clone+`NetworkSpawn` to the first `OnUpdate`;
  joiners still spawn inline.
- Stations are host-built and NetworkSpawned so `[Sync]` occupancy replicates; everything cosmetic
  is local `NotSaved`/`NotNetworked`, rebuilt per client.
- The move relay is `NetChessMove(uci, fenAfter)` (`[Rpc.Broadcast]`, client→all) with the host
  folding the latest FEN into `[Sync] BoardFen` for late joiners. The spectator wall and late
  joiners read that same folded FEN — no second relay.
- Sitting plants the avatar at its side of the board facing it (`LobbyPlayer.BeginEngage` →
  `ChessStation.SeatWorldPosition`); standing restores the pre-sit transform so the camera hand-back
  doesn't snap.
- Same-machine test instances share `FileSystem.Data` (one identity). Test via the network status
  icon → "Join via new instance".
- Small race window (~RTT) if two players press E on the same seat — host picks the winner; known
  limitation.

**The snapshot rules that govern where a component may live are in `SBOX-NOTES.md`. Read them
before adding anything to `lobby.scene`.**

### Where a setting lives: the ROOM on the wall, the BOARD in your hand (issue #28)

Two settings panels, and which one a row belongs on is a question about **audience**, not space:

- **WORLD SETTINGS**, the south-wall board (`SettingsStation` → `SettingsScreen`, summarised by
  `WallSettingsPanel`) — the ROOM: theme, room and table light brightness, the checkerboard floor
  and its pop rate, both voice-range sliders.
- **BOARD SETTINGS** (`UI/Screens/BoardSettingsScreen.razor`) — a **chess board**: BOARD SOUNDS,
  PLAY MODE, MOVE MODE, SHOW LEGAL MOVES, SPEAK MOVES AT MY BOARD, the TTS voice pill and volume.

**Both render `SettingsModel` rows** — `BuildLocalRows()` and `BuildBoardRows()`, one model, two
lists. A third consumer is a third `Build*Rows`, never a second copy of the row types, and
**anything that mutates must go through `Mutate`**: it bumps `SettingsVersion`, which is the repaint
key for both panels *and* the trigger for `ChessRing.ApplyPlayModeSetting` — a setter that skips it
changes a stored value and nothing in the world.

- **Why it split.** The wall panel had outgrown the screen and clipped at **both** ends, so the top
  row was as unreachable as the bottom. Size was the symptom. Already tried and rejected on
  #26/#27, so don't reach for them again: scroll fights the sliders' drag (repo rule),
  `FitToHeight` only shrinks a list that was two unrelated jobs deep, folding the two sound toggles
  into one `MultiToggleRow` landed but didn't fix it, and a local ~0.8× type/spacing rule was
  **rolled back** — a font size local to one board makes that board quietly different from every
  other one.
- **Two doors, one editor.** Seated: a **plate on the tabletop**, near-left corner of your own seat
  (`World/SeatSettingsPlate.cs`). At the wall: a BOARD SETTINGS row on the world panel, so someone
  who isn't sitting down can still reach these. Both call `BoardSettingsScreen.Open()`. **Nothing in
  the panel may read `ChessStation.Active`** — every row is client-local and the wall door has no
  seat.
- **The tabletop plate is the repo's FIRST world-space control, and it is the `WorldInput` path.**
  P99 built two world-space buttons and deleted both — but read *why*: they were an extra thing to
  find, aim at and click **in the mode whose whole point is that you are not pointing at anything**,
  and they needed a plate per seat plus a yaw-180 flip to keep the words unmirrored. Neither
  objection survives here: this opens a settings panel you use *with* a cursor, and it is
  **client-local, so there is exactly ONE of it**, moved to whichever seat the local player is in.
  `LobbyPlayer` hangs `Sandbox.WorldInput` on the camera, which is what `SBOX-NOTES.md` names as
  the thing to reach for. **Do not rebuild the hand-rolled tilted-plane hit test.**
  → **Unparented on purpose.** A `ChessStation` is NetworkSpawned, so a child of one rides the
  host's snapshot, transform and enabled state included (issue #12's lesson). The plate is a
  `NotSaved | NotNetworked` GO at the scene root, placed in world space off the station's transform
  every frame — which also keeps it with the table when the ring SLIDES on a board-count change.
  → **One string, one plate, one font size.** Both states are 14 characters ("BOARD SETTINGS" /
  "ESC FOR CURSOR") so the inert state is **colour only** — the same rule `TableClockTextPanel`
  keeps, and the reason a second string would mean a second plate.
  → **"The world-panel text is too big" is fixed in PIXEL space, never in the span.** This cost a
  round in the room. Shrinking `SettingsTextSpanLength` scales the panel AND the glyphs together, so
  the text hangs off the plate by exactly the same proportion, only further away. The knob is
  `SettingsCharAdvanceEm` / `SettingsTextFitFraction`: a WIDER pixel space means SMALLER glyphs on
  the same plate. Turn the span height with it, or a shorter string leaves a thin line of words
  centred on a fat slab — the span's aspect IS the panel's pixel aspect.
  → **Its advance estimate is its OWN, not the clock's.** Reusing `ClockCharAdvanceEm` looked right
  — one measured number beats two — and overflowed the plate at both ends: the clock measured
  DIGITS, this draws bold CAPS in a proportional fallback face, and the formula has **no term at all
  for `letter-spacing`**, which at 14 characters is real width that was simply not being counted.
  Round the advance UP; under-stating it is the failure that shows.
  → **In LOOK aim it stops offering a click and says `ESC FOR CURSOR`.** The pointer is hidden; a
  live-looking control there is the "reads as broken" failure the aim hint exists for.
- **It is MODAL for `SeatAim`**, exactly like the promotion picker, and the wiring is deliberate:
  `LobbyPlayer.UpdateSeatAim` folds `IsOpen` into the modal flag rather than the panel touching
  `SeatAim` — that is what makes it release the cursor and take aim back **by itself** on close,
  without clearing a suspend the player asked for with Escape. Escape is routed at it in
  `LobbyPlayer` (the one place that reads `EscapePressed` while engaged), ahead of both the aim
  toggle and the stand-up.
- **The wall's own panel HIDES itself while the board panel is open** rather than stacking under it.
  Not a z-order bug and not translucency (`WallTheme.Bg` is opaque, and the board panel's root
  carries `z-index: 60`): both cards are 620px and centred, the wall's is much taller, so its top
  and bottom rows poke out past the board card and the pair reads as one garbled panel. The station
  stays engaged underneath, so closing the board panel brings the wall's straight back — which is
  what "opened it from here" should mean.
- **The bottom-left column is unchanged** — `HudHints` at 44, `VoicePanel`'s roster at 100. The
  first version of the seated door was a pill in that corner and pushed both up; it is on the
  tabletop now. Don't reintroduce a third tenant there.
- **The wall board summarises only what it edits.** PLAY MODE / BOARD SOUNDS / SPEAK MOVES status
  lines went with their rows; a status line for a setting that lives elsewhere is a line nobody
  re-reads when that setting changes shape.

### Cursor vs LOOK aim at the board (P99)

A BOARD SETTINGS picker (**MOVE MODE — CURSOR / LOOK**) chooses how a seated player picks a square.
CURSOR is everything before P99 and stays the default. LOOK hides the pointer, turns the seated view
with the mouse, and picks whatever is under the **centre of the screen**. `World/SeatAim.cs` is the
whole state machine and the only thing that decides; two places act on it (`ChessBoardView` for the
ray, `LobbyPlayer` for the camera offset and Escape) and `GameHud` only DRAWS it.
`PlayerData.LookAimAtBoard` says whether the player wants it at all.

- **The cursor is still the default state, even with LOOK on.** Aim engages only while a game is
  **Playing** (off the `IBoardGame` seam, so a lichess game aims like a local one — reading
  `LocalGameController` here would be the M8-silence mistake again). An empty seat, the setup panel
  and a finished game all need a pointer, so they keep one. **This is the feature's shape, not a
  caveat**: the ask was "the cursor is active until a game is playing".
- **The crosshair is part of the mechanism, not decoration.** With the pointer hidden the pick point
  is invisible, so `GameHud` draws a dot-in-a-ring at dead centre (`.crosshair`, gated on
  `SeatAim.Aiming` alone and deliberately OUTSIDE the HUD's own `Visible()` block and its corner
  panel — it marks a point in the world, not a line of HUD). 50%/50% because that is literally what
  `SeatAim.PickPixel` returns; centred with negative margins rather than a `transform`. A ring
  around a dot so it survives both a white square and a dark one.
- **Three ways back to the cursor, and they differ.** The player's own (Escape) **sticks**; a
  **modal** (the promotion picker, an offer standing against you) releases and **restores by
  itself** without undoing a suspend the player asked for; and the game **ending** clears
  everything, so the next game starts in aim rather than remembering a key pressed twenty minutes
  ago.
- **ESCAPE IS THE WHOLE CONTROL, and it CYCLES: cursor off, on, off.** While `SeatAim.Toggleable`
  (the setting on, a game live, no modal), Escape switches the pointer and **does not stand you
  up** — the HUD's Leave button does, and one Escape puts a cursor on the screen to click it with.
  That trade is deliberate: Escape is the only key that works with the pointer hidden, so it belongs
  to the mode it can be used in, and leaving is something you do with a cursor anyway. Everywhere
  else — roaming, an idle seat, a finished game, the setting off, a picker open — Escape is the
  plain stand-up it has always been. **`GameHud` says both halves out loud** ("Esc for the cursor" /
  "Esc to aim again, Leave to stand up"), because with one key doing two things and no pointer to
  explore with, an unsaid rule is an unfindable one.
  → **There is no world-space button, and two were built and thrown away** — see the settings-plate
  note above for what survived that lesson. **The aim toggle is still Escape and still has no
  button.**
- **The mechanism is one engine switch, and that is why it can't get out of step.**
  `Mouse.Visibility = Hidden` locks the pointer AND is exactly what makes `Input.AnalogLook` report
  movement (the engine zeroes AnalogLook whenever a cursor is visible). **Never set `Visible` — set
  `Auto`**: Auto already shows a cursor while clickable UI is up, and this is a global, so a
  forgotten reset would leave a roaming player with no pointer and no mouselook. `Disengage` clears
  it first thing and the roaming path re-asserts it, because not every way to stop being seated goes
  through `Disengage`.
- **The camera offset composes in EULER space**, not as a quaternion post-multiply: a seat anchor is
  already pitched steeply down (the 2D nadir one looks straight down), so turning about its own
  tilted up-axis would roll the horizon. The offset is clamped (±45° yaw, ±30° pitch) — the board is
  the point of the view — and **persists** when the cursor comes back by ESCAPE, so releasing it
  hands you a pointer rather than snapping the view.
- **…but it is CLEARED when look aim stops being AVAILABLE, and that distinction is a bug fix.** The
  offset used to survive everything short of standing up. Right for Escape — a suspend leaves aim
  one keypress away — and wrong for the two ways aim actually ends: the player switches MOVE MODE to
  CURSOR, and the game finishes. Both leave the view turned as much as 45° off the board with
  *nothing left that can turn it back*. `SeatAim` watches the falling edge of `Enabled && playing`
  (deliberately NOT `Toggleable`, which a modal also clears — a modal hands aim back by itself),
  zeroes the offset and raises a one-shot `Recentred`; `LobbyPlayer.UpdateLockedCamera` consumes it
  through the same re-blend as an anchor swap so the view EASES back rather than cutting.
  **`TakeRecentred` must be called even when the anchor also swapped** (2D + LOOK → 2D + CURSOR does
  both at once) or the one-shot survives to fire on an unrelated later frame.
- **LOOK aim keeps the SEAT camera, even in 2D — and `LocalNadir` means the CAMERA now, not the
  render mode** (issue #28). The composition above is degenerate at the 2D nadir anchor and no
  tuning fixes it: yaw is applied about world up, and at a straight-down camera world up IS the view
  direction, so mouse-left/right ROLLS the board image; pitch is applied in camera space, and the
  nadir anchor's local axes come from a per-seat `farDir` that flips sign between White and Black,
  so mouse-forward tips the view the opposite way for the two colours. The fix is to never be there:
  `ChessStation.LocalNadir` is now `Active && FlatMode && !SeatAim.Enabled`, and
  `LobbyPlayer.UpdateLockedCamera` picks its anchor off **that same property**, so the two cannot
  disagree. **2D + LOOK is flat pieces, seated view** — a real thing, because `FlatMode` is a
  *render* gate independent of which anchor is live. The **flat glyphs stay flat** under it (no
  billboarding): they are the same lie-in-the-plane sprites the north-wall spectator board is read
  from the floor at a steeper angle, so there is evidence the foreshortening is a non-issue, and a
  "which camera is live" rule in the render path would be a real cost for a speculative one.
  → Gate on `SeatAim.Enabled` (the SETTING), never on `SeatAim.Aiming`: aiming goes false on every
  modal, every Escape and every finished game, and an anchor that followed it would swing the camera
  between two entirely different views mid-game.
  → `LocalNadir` is also what `NameTagPanel` and `StationScreenPanel` hide themselves on. That is
  why the redefinition is load-bearing rather than cosmetic: on the old "FlatMode = top-down"
  inference they would have stayed hidden for a 2D+LOOK player who can now see the world they belong
  in.
- **A mid-seat anchor change blends, and it did NOT before.** `UpdateLockedCamera`'s comment claimed
  the existing lerp eased between anchors "for free"; it could not — `_engageTime` is long past
  `CamBlendTime` by then, so `t` is pinned at 1 and the write is a hard cut. It never showed because
  PLAY MODE could only be changed at the wall, and you sit down *after*, which runs the engage
  blend. Now that both PLAY MODE and the aim setting are changeable **from the seat**,
  `_lastSeatAnchor` spots the swap and re-blends **from the camera's live transform** — which also
  keeps a non-zero `SeatAim.LookOffset` from snapping, since the offset is already baked into where
  the camera is. `BeginEngage` and the seat-switch schwoop clear it so those adopt the new anchor
  silently.
- **P99 added nothing to the world in the end** — no tabletop geometry, no margin spent. One state
  machine, one key, one crosshair and a HUD line.
- Proven on this host: `SeatAim` runs its whole truth table in a shim harness, **including which way
  Escape goes** — `Toggleable` is asserted true for a live game with the setting on and false for a
  modal, a dead game, the setting off and standing up, because getting it wrong in either direction
  either traps the player in their seat or takes the toggle away. What it can't prove is the FEEL:
  whether the crosshair reads over both square colours, and whether losing Escape-to-stand mid-game
  is annoying in practice. Both are **room** checks.

---

## Sounds

Synthesized WAVs in `Assets/sounds/sfx/` generated by `scripts/gen_sounds.py` (numpy). `.sound`
gotchas: `"Sounds"` lists `.vsnd` paths (not `.wav`), `"Volume"`/`"Pitch"` are JSON strings,
`"UI": true` for 2D playback, `"__version": 1`.

**The whole set was heard in-room and is good** (M17), including the ones synthesized blind that
PLAN.md tracked as unheard. Treat the current `gen_sounds.py` output as intended unless a specific
one is called out.

**`tick`/`tock` are MOVE sounds, not clock sounds** — once per move, by side. There *is* one
per-second sound now (`panic`) and it is the only one.

**Every board sound goes through `Gambit.Audio.TableSounds`, which watches the `IBoardGame` seam.**
That is the whole design, and it is worth knowing why it isn't a call in the controllers. Sound used
to hang off `LocalGameController`, and so **a real lichess game at a table — the M8 headline feature
— was completely silent from M8 to M11**: no move, no capture, nothing. Nothing looked wrong in any
diff, because the code that was there was correct; it just only ever covered half the tables. A
watcher on the seam makes that class of bug impossible rather than merely fixed — a third kind of
game gets these sounds by existing. **Don't add a `Sound.Play` to a game controller.** If a new
reactive feature has you typing `LocalGameController`, that is the same mistake starting again.

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
cross the room is decided in `SoundPlayer`, not at the call site**: every method there takes `mine`
rather than letting a caller pick between the 2D and 3D asset, because a call site that gets to
choose is one that can get it half right.

**A 3D variant is a `.sound` file with `"UI": false` pointing at the SAME `.vsnd`** — no second WAV.
This is why `tock3d` finally exists: it was recorded for a whole milestone as "an unmade asset",
which was wrong — it was six lines of JSON reusing the `tock.wav` already there. **Check what an
asset actually costs before recording it as a decision.**

**There is no separate flag sound** — a clock running out *is* the game ending, and firing both
would be the same sound with a grace note. Still silent, deliberately: **resign** (it's a game over,
and it makes that sound) and **sit / stand** (you know you sat).

**No sound may be fired from a FEN diff alone.** `Code/Chess/BoardDiff.cs` classifies a position
change as `Move` / `Rewind` / `None` and is Sandbox-free so it can be run here — a FEN change on its
own also means a takeback, a table reset, or a late joiner's first sync, and only the **ply
direction** separates those from a move. Proven in a dotnet harness against real games through the
vendored rules (en passant, capture-promotion, castling, the resync). That extraction is the
`CapturedMaterial` lesson again.

**Spoken moves / TTS (M12) ride the SAME seam, gated on `Mine`.** An opt-in world setting reads out
the notation of moves played on the board *you are seated at* — never the TV wall, never another
player's table. `TableSounds.WatchMove` calls `MoveTts.SpeakLastMove(game)` only when `Mine`, so it
inherits the move classification for free and covers a lichess game the same as a local one
(`ChessBoard.Move` fills in SAN on execution, so `SanMoves` is real for both).

- **`Sandbox.Speech.Synthesizer` is SAPI-backed and Windows-only.** On Mac/Linux/dedicated it has no
  voices; `MoveTts` catches that and the feature is a silent no-op. It is **never required**, exactly
  like gamchess — every path fails closed to silence. `Code/Chess/MoveSpeech.cs` (SAN → "knight f
  3") is Sandbox-free and dotnet-tested here; the speaking half isn't.
- **Voices are enumerated once and cached** (`MoveTts.Voices`) because constructing a `Synthesizer`
  queries the OS and the settings panel rebuilds its rows every frame it's open. A *speak* still
  constructs a fresh one (the wrapper accumulates its text and can't be reset) and runs synthesis
  synchronously — a per-move main-thread cost, paid only when the feature is on. If that ever
  stutters noticeably, a background `GameTask` is the escape hatch, but mind that `SoundStream`/SAPI
  may not be thread-happy.
- The picker is a **tap-to-cycle pill**, not one cell per voice: a machine can have many voices and
  a row of full names would overflow the panel. It stores the full name (`TrySetVoice` needs it) and
  shows a short one.

Music is the `gamah.skafinity` library, source-committed under `client/Libraries/gamah.skafinity/`.
The player + panel are built client-local by `LocalMusicSystem` — never scene-authored.

**The music board is KEYS, not a wall — `N` opens it, `M` mutes** (`UI/MusicScreen.cs`, the shape
terryball has always had and rotaliate-client now shares). There is no music board on the south wall
and no `StationKind.Music`: the wall row is World + Host, re-centred at ±0.13. The panel stays
*disabled* while closed, because its own floating ♪ button is a pointer-events element that would
hold the cursor released and kill roaming mouselook — that hazard is why the panel was gated on an
engage flow in the first place, and the gate is now a key rather than a place you walk to. **`N`
only opens while ROAMING** (engaged already owns the screen and owns Escape); `M` works anywhere
because it draws nothing. While the board is up, `Mouse.Visibility` is `Visible` — deliberately not
`SeatAim`'s `Auto`, because Visible also zeroes `Input.AnalogLook` so clicking around the board can't
spin the camera — and every exit path (✕, Escape, N, engaging, `OnDisabled`/`OnDestroy`) puts `Auto`
back.

---

## Lobby chat and proximity voice (M12)

### Chat is the ENGINE'S overlay now, not ours

`ChatShowUI` is **`true`** in `Platform.config`. The engine draws the feed and the input box;
messages route/filter through the host as before. This replaced a **288-line** custom chat box (feed
+ `TextEntry` + fade + hand-rolled word-wrap) copied from rotaliate and kept alive *only* by turning
the engine overlay off to redraw it worse — terryball threw the same box away (`8ad9f4b`), and this
is Gambit catching up. **Do not re-add a custom chat box.**

**The chat WINDOW's position and size are the engine's and cannot be moved from game code** (checked
against `sbox-public`'s `menu` addon 2026-07). It is that addon's `ChatOverlay`, laid out by *its
own* stylesheet (`left: 64px; bottom: 20%; width: 600px` in-game; messages `justify-content:
flex-end` up to `max-height: 400px`, then scroll — so it already grows UP from the bottom). It
renders in the menu overlay tree, outside our `ScreenPanel`, and `Platform.config` exposes only
`ChatEnabled`/`ChatShowUI`/`ChatMaxMessageLength` — **no position, no API**. So "align the chat box
to the glyphs / grow it to the top" is not doable without re-adding our own box, which is the
forbidden path above. The keycap HINT is ours and movable; the window is not.

- `ChatPanel.IsOpen` is a **stub `=> false`** kept so `LobbyPlayer`'s "don't walk while typing" gate
  compiles. That gate is now **dead code, and that's fine** — the engine's focused text box already
  stops WASD leaking into the world. Don't try to revive it. `ChatPanel` draws nothing and survives
  only for the stub; it is a serialized `lobby.scene` component, so the class can't be deleted
  without orphaning that scene entry.
- The chat keycap lives in **`UI/Screens/HudHints.razor`**, now the ONE bottom-left keycap stack —
  Chat · M mute music · N music · G voice · B mute players — in one style, each keycap **read live
  from its binding** (`Input.GetButtonOrigin`), never hardcoded; there is no glyph-ICON API in this
  build, so an input "glyph" is the binding string in a keycap box. **All three s&box repos draw it
  this way** (gambit, terryball, rotaliate-client): a stack is a list a player reads top to bottom,
  and three panels in three corners was not. `VoicePanel` keeps only the mute ROSTER, which opens
  above the stack (`bottom: 100px` vs `44px` — keep the two in step).
- The old ChatPanel comment claimed the key was "rebindable in Settings" and resolved it through
  `PlayerData.Bindings` — but **nothing ever wrote `Bindings`**, so it was dead code guarding a
  feature that doesn't exist. `Bindings` is **deleted** from `PlayerData` (old saves drop the unknown
  key on load); `GamepadBindings` is the real, separate thing — don't confuse them.

### Proximity voice, copied from terryball

`GambitVoice` (a `Voice` subclass) rides every avatar, added host-side in
`LobbyNetworkManager.AddVoice` before `NetworkSpawn`. `VoiceScreen` (keyboard driver) + `VoicePanel`
(the mute roster) + `HudHints` self-attach to the ScreenPanel — client-local, so the mute/enabled
state (cookies in `Gambit.Game.VoicePrefs`) never rides a snapshot. **Master voice defaults OFF.**

- **Playback gates on the RECEIVER**: `ShouldHearVoice(Connection c) => VoicePrefs.VoiceEnabled &&
  !VoicePrefs.IsMuted(c.SteamId)`, called with the *sender's* connection — so mute needs no sync, no
  authority, no server state. Transmit is gated owner-locally via `Voice.Mode` (AlwaysOn + `"Voice"`
  PTT binding when on; Manual + `NoVoiceInput` unbound sentinel when off). **Never touch a networked
  Enabled flag.**
- **Hearing RANGE is a receive-side, per-client value** — the 3D falloff is applied on the receiver
  off each proxy's `Voice.Distance`/`Falloff`, so "how far voices carry to me" is my choice, not the
  speaker's. That is *why* it lives on the world-settings board (two `PlayerData` sliders:
  `VoiceRangeAtTable` / `VoiceRangeRoaming`) and needs no networking.
  `VoiceScreen.ApplyHearingRange` writes `Distance` onto **every** avatar's voice each frame, keyed
  on the LOCAL player's engage state (tighter seated, wider roaming — both tunable). Enabled/muted
  stay cookie-light in `VoicePrefs`; only range is on the board, because range is a room-tuning knob.
- **The world-settings board uses real `SliderControl`s (M12), not the rotaliate tick bars.** Every
  continuous setting — brightness, pop rate, voice range, move-voice volume — is a
  `SettingsModel.SliderSpec` that both panels render identically: one model, one look, whichever
  panel a row lands on. Sliders are **continuous, no `Step`** by request; `OnChange` persists on
  every change (the file is tiny). The label carries the formatted value and recomputes because
  `Mutate` bumps `SettingsVersion` and the screen rebuilds — the same reason a `SliderControl`
  survives the mid-drag rebuild (s&box diffs it; terryball proved this). **`gamah.skafinity` is
  exempt** — it is a vendored library with its own music sliders; do not "upgrade" those.
- **Gotchas that were the reason to copy** (all in the code comments): `Voice.OnUpdate` is **sealed**
  (only the hear/exclude hooks are virtual); the engine's default `Falloff` is savagely front-loaded
  (~4% by 20% of range) so we use a **linear** `Curve` + `Volume = 2f`; the default `Distance`
  (15,000u) is wrong for the 800u room, hence the sliders; **V is the engine's push-to-talk**, so
  the master toggle is **G** and the mute roster is **B** (both free in Gambit's `Input.config`; a
  new `"Voice"` action bound to V is the PTT key). `Voice.IsListening` honours the user's s&box
  `voip_mode`, which game code can't change — the chip surfaces it, never claims to be the switch.
- **Stripped from terryball**: the first-run help pop-up (Gambit's welcome board is its own thing),
  `LocalIsBowling`, `TerryAvatar`. The `INetworkListener`-fires-on-every-component trap that gave
  terryball N avatars per joiner **does not apply** — Gambit has one `LobbyNetworkManager` and one
  `AddVoice` call site.

---

## Dev console commands

| Command | What it answers |
|---|---|
| `gambit_gamchess_ping` | is gamchess up? |
| `gambit_gamchess_signin` | mint an FP token and prove the auth round-trip |
| `gambit_gamchess_games` | list your archived games |
| `gambit_lichess` | am I linked? Prints **this PC first** (the token is local now, so the answer needs no network at all), then gamchess's opinion, and names the case where they DISAGREE — a token here that gamchess doesn't know about breaks only the two-seat directory lookup, which would otherwise be discovered at the moment two people sit down to play |
| `gambit_lichess_unlink` | revoke at lichess with the token, delete it from this PC, then tell gamchess to forget the row — **in that order**: the revoke must be signed by the token, so a token already deleted can never be revoked by anyone but the player |
| `gambit_tv` | why is the TV wall doing that? Prints the whole chain: the local setting, the channel, what the wall thinks it's showing, and gamchess's raw state. Exists because "nothing is showing" was twice diagnosed by guesswork and once wrongly — none of the chain is visible from outside, and a feature that never fires looks exactly like one that isn't wired up |
| `gambit_terry_*` (35) | the M14 hand-tuning harness — **dev tools, not player-facing** (session-local knobs on `SeatedHandSpikes`; the shipped values live on `TerryTuning` in `lobby.scene`). Full reference in `TERRY-HALFRISE.md`; gate or drop them before a public ship |

**Dropped for public ship** (recover from git history if needed): `gambit_perft` — re-prove the rules
via the dotnet harness or `chess_js_perft.mjs` instead; and `gambit_music`, the issue-#12
music-topology dump.

---

## Asset licensing

Provenance goes in `Assets/ATTRIBUTION.md`, CC0 included. Nothing else is licensed in: pieces are
runtime meshes from `ChessSetBuilder`, floor glyphs are our own DejaVu raster, sounds are synthesized
by `scripts/gen_sounds.py`, and the web viewer uses Unicode glyphs (zero image assets).

**The one documented exception is the lichess logo** on the web button that leaves for lichess — see
`LICHESS.md` for the terms and the hard rules they imply.

CC0 sources on file for the D5 3D upgrade: Poly Haven "Chess Set" by Riley Queen
(https://polyhaven.com/a/chess_set, glTF/FBX); portablejim 2D chess set on FreeSVG
(https://freesvg.org/portablejim-2d-chess-set-pieces); OpenGameArt /content/chess-pieces-0,
/content/3d-chess-pieces, /content/chess-set-1, /content/chess. Kenney has no chess pack.

---

## Status

gamchess client + server built. **HTTPFIX moved the lichess token to the client**, so the server's
lichess surface is a link flow, a disclosure page and a directory; the C# lichess client is new and
large. The **Go half compiles and its tests pass** (Go 1.22 from `~/.local/share/toolchains/`;
`go test ./... -race`), and the **Sandbox-free half of the C# lichess client passes its harness**
(`scripts/lichess_harness`, `dotnet run`). **HTTPFIX merged 2026-08-06 (PR #31) having been opened
in the editor**: it compiles, and a real lichess game was linked and played end to end through the
shareable-link flow — so the streaming premise the whole branch rests on is proven in the room, not
just argued. **The PAIRED flow (two seats at one table) has still never been run**, along with seek
and challenge-by-name; PLAN carries the row. Nothing is deployed (no Docker here).
