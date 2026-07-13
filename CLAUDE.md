# CLAUDE.md — Terry's Gambit s&box Client

**Terry's Gambit** (repo/ident: `gambit`) — chess in a social s&box lobby, backed by lichess. Forked from rotaliate-client:
the walk-around lobby, station ring, and networking scaffolding are inherited;
the arcade game and its Go backend are being replaced by chess boards and the
lichess API.

**Read `PLAN.md` first** — it is the source of truth for the design: architecture
decisions (D1–D8), lichess API facts (auth, Board API, streaming, rate limits,
anonymous play via PGN import), the rotaliate→gambit file mapping, milestones
M0–M6, and risks. This file carries the s&box engineering lore inherited from the
parent project — hard-won gotchas that still apply.

Current status: **M0 done** (repo bootstrap: copy, renames, plan). Legacy
Rotaliate gameplay code (`Code/Game/Board.cs`, `GameController`,
`MultiplayerController`, `Api/ApiClient.cs`, `Ws/`, `CubeBoardView`, leaderboards)
is still present and compiles, but is scheduled for deletion/replacement per the
PLAN.md file mapping — don't build on it.

---

## Project Setup (first time on a new machine)

s&box's package manager tracks local projects in its own registry — cloning the
repo and opening the `.sbproj` directly will fail with
`Unable to find package 'local.gambit#local'`.

**Correct flow:**
1. Open the s&box editor → **New Project** → Game (Empty), pointed at the cloned repo folder
2. The editor writes its own `.sbproj` and registers the project; use that file, not the one in the repo
3. The editor hotloads C# automatically — check the error list for compile errors

```
scripts/               ← dev utilities (not s&box assets); gen_sounds.py needs numpy
gambit/                ← open gambit/gambit.sbproj in the editor
  gambit.sbproj        ← reference template; editor generates the real one locally
  Code/                ← all game C# and Razor files (capital C)
  Editor/              ← editor assembly (HotloadRebuild.cs)
  Assets/scenes/       ← lobby.scene is the only production scene
  Assets/sounds/       ← .sound events referencing compiled .vsnd in sfx/
  ProjectSettings/     ← Input.config, Collision.config, Platform.config
  Libraries/gamah.skafinity/  ← procedural music library (source-committed)
```

**Paths in csproj/slnx** assume Steam at `D:\Steam\`; the editor regenerates them.

This dev host has **no s&box toolchain** — nothing here compiles or runs locally.
Verify by careful review + grep; the user tests in their editor. Standalone
`dotnet` harnesses (plain HttpClient against lichess) may be used to validate
API handling before porting to s&box idioms.

---

## s&box Patterns to Follow

- **Components**: game logic lives in `Component` subclasses; `OnUpdate()` for per-frame work
- **UI**: screens are Razor `PanelComponent`s on a `ScreenPanel` GameObject in the scene
- **State**: `[Sync]` for peer-networked state (host-authoritative with `SyncFlags.FromHost`); `[Rpc.Host]` request / `[Rpc.Broadcast]` relay pattern (see ChessStation occupancy)
- **Storage**: `FileSystem.Data.ReadAllText/WriteAllText` for JSON player data
- **HTTP**: `await Http.RequestStringAsync(url)`; `await Http.RequestAsync(url, "POST", content, headers)` — the trailing headers dictionary is undocumented in `../sbox-docs` but works
- **Hotload**: C# changes hotload in milliseconds. Procedural builders rebuild via `[EditorEvent.Hotload]` in `Editor/HotloadRebuild.cs` — keep new builders registered there
- **Razor usings**: `System`, `Sandbox`, `Sandbox.UI`, `Sandbox.Rendering` are NOT auto-imported in `.razor` — add `@using` explicitly

## s&box API Whitelist

s&box enforces an API whitelist — blocked calls produce `error SB1000`.
See `../sbox-docs/docs/code/code-basics/api-whitelist.md`.

| ❌ Blocked | ✅ Use instead |
|---|---|
| `Array.Clone()` | manual `for` loop copy |
| `Console.WriteLine` | `Log.Info` / `Log.Warning` / `Log.Error` |
| `System.IO.*` | `FileSystem.Data` |

Rule of thumb: avoid `System.Private.CoreLib` reflection/process/threading/IO.
This matters for the vendored chess library (PLAN.md D2) — expect to patch out
regex/events. When in doubt check `https://sbox.game/api/` or file a
false-positive at `https://github.com/Facepunch/sbox-public/issues`.

## World Scale Rules (read before placing/sizing anything)

- **Never trust code defaults or docs for component property values** — the scene
  overrides them and gets retuned in-editor. `grep Assets/scenes/lobby.scene` for
  current values before sizing anything.
- The player is ~72 units tall — the human-scale yardstick.
- `models/dev/box.vmdl` is **NOT 1×1×1**: to make a box of size S,
  `LocalScale = S / Model.Bounds.Size` per axis — use/copy `ChessRing.AddBox`.
- Never put a `BoxCollider` on a non-uniformly scaled GO — it silently freezes
  physics. Colliders on uniformly-scaled parents, visuals on scaled children.
- A WorldPanel GO's scale is a multiplier on the panel's intrinsic pixel size,
  not world units; the panel plane is local **Y (width) / Z (height)**. World-size
  and text size are coupled — to grow a board without growing text, scale the GO
  up and divide stylesheet px by the same factor.
- `FacePlayer` yaw-billboards a GO toward the camera; fronts face **+forward**.
- There is **no documented API to open a URL / Steam overlay** — show links as
  copyable text (affects the lichess OAuth flow and game-link sharing; see
  PLAN.md D3). Click-to-copy pattern: `DiscordButton.Copy()`.

## UI Gotchas (learned the hard way)

- **Board vs Screen vocabulary**: a *board* is a display-only WorldPanel in the
  world (takes no pointer input); a *screen* is an interactive ScreenPanel shown
  while engaged at a station, clipped to the station rect via
  `ChessRing.ScreenFractionRect()` / `UiRectStyle()` trig.
- Engaged-screen centering must live on an absolutely-positioned full-screen
  child (`.screen-fit` wrapper), NOT on `root` — otherwise content pins top-left.
- `transform: scale` misplaces panel content — use explicitly sized wrappers.
- `pointer-events: all` must be set per interactive element; it does not inherit.
- Panels are flex containers: inline `<span>`s inside a text div become separate
  flex items; source newlines render as literal whitespace — keep each text div's
  content on one line. A div's auto height does not grow for wrapped text — use
  one div per line in a flex column.
- Deriving font sizes from `Panel.Box.Rect` on a WorldPanel doesn't work — use
  fixed px in intrinsic pixel space, calibrated against a known-good panel.
- Don't make a panel `overflow: scroll` if it has draggable controls — s&box
  drag-scrolls it and fights the clicks.
- The citizen `SkinnedModelRenderer` must live on a `Body` child GO, never on the
  PlayerController's own GO (animator writes to it every frame — welds the player
  to world origin otherwise).
- No documented API to add buttons to the built-in escape menu; Escape leaves the
  station via `Input.EscapePressed`.

## Networking Notes

- `LobbyNetworkManager` (`ISceneStartup.OnHostInitialize` → `Networking.CreateLobby`)
  hosts; joining peers never fire that event. Players spawn by cloning the disabled
  in-scene `PlayerTemplate` GO (no `.prefab` asset — hand-authoring the format is
  undocumented) and `NetworkSpawn(connection)`.
- Stations are host-built and NetworkSpawned so `[Sync]` occupancy replicates;
  everything cosmetic is local `NotSaved`/`NotNetworked`, rebuilt per client.
- Same-machine test instances share `FileSystem.Data` (one identity). Test via
  the network status icon → "Join via new instance".
- Small race window (~RTT) if two players press E on the same seat — host picks
  the winner; known limitation.

## Sounds

Synthesized WAVs in `Assets/sounds/sfx/` generated by `scripts/gen_sounds.py`
(numpy). `.sound` gotchas: `"Sounds"` lists `.vsnd` paths (not `.wav`),
`"Volume"`/`"Pitch"` are JSON strings, `"UI": true` for 2D playback,
`"__version": 1`. Planned mapping (PLAN.md M6): tick/tock → clocks, pop →
captures, servo slides → station rebuild.

Music is the `gamah.skafinity` library — source-committed under
`gambit/Libraries/gamah.skafinity/` (s&box pattern: libraries are source and
auto-referenced by living there; do NOT add a `PackageReferences` entry — that
double-registers the compiler). The scene carries a `SkafinityPlayer` +
`SkafinityMusicPanel`; the panel is enabled only while engaged at the music wall
board (a free-floating interactive panel kills roaming mouselook).

## Lichess Integration Rules (non-negotiable)

- Humans play through the **Board API only**; anything else risks account bans.
- **No engine assistance during lichess games, ever** — not even an eval bar.
  v1 ships no engine at all.
- One REST request at a time; on HTTP 429 back off a **full 60 seconds**.
- NDJSON streams: blank keep-alive line every ~7s; auto-reconnect and resync
  from the `gameFull` snapshot; a seek is cancelled the moment its HTTP
  connection closes.
- The lichess token is a secret: never `[Sync]`/RPC it, never log it unredacted
  (follow the old GUID `Redact()` discipline).

See PLAN.md "Research summary" for endpoints, scopes, and time-control limits.
