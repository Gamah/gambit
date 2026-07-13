# CLAUDE.md — Rotaliate s&box Client

s&box reimplementation of [Rotaliate](https://github.com/Gamah/rotaliate) — a competitive puzzle game.
Builds as a standalone s&box game using C# on the Source 2 engine.

Read this file fully before making any changes.

---

## What is Rotaliate?

Players rotate 2×2 blocks on a 10×10 grid to form same-color groups — solved groups go black.
Race the clock, minimize moves, or eliminate your color before opponents do.

### Core Rules
- 10×10 grid, 4 active colors: Red=1, Blue=2, Green=3, Yellow=4 (24 cells each); Black=0
- A 2×2 block of identical active color → all four cells become 0 (solved)
- A move = CW (dir=0) or CCW (dir=1) rotation of a 2×2 selection
- Selection point is the top-left corner of the 2×2 block (0-indexed)
- Starting board is randomized so no 2×2 group is pre-solved

### Game Modes
| Mode | Description |
|---|---|
| Daily Puzzle | Shared daily board; compete for time or moves |
| Hourly Puzzle | Shared hourly board; compete for time or moves |
| Free Play | Seeded solo session |
| 2-Player | WebSocket — each player owns 2 colors; first to clear both wins |
| 4-Player | WebSocket — each player owns 1 color; first to clear theirs wins |

---

## Stack

| Layer | Tech |
|---|---|
| Engine | s&box (Source 2) |
| Language | C# |
| UI | s&box Razor Panels + HudPainter |
| Networking | `Http` (REST) + `WebSocket` (s&box built-ins) |
| Storage | `FileSystem.Data` (persistent local JSON) |
| Backend | Go server — github.com/Gamah/rotaliate |

s&box docs are at `../sbox-docs/docs/`. Reference them for API details.

---

## Planned Project Structure

```
rotaliate/
  rotaliate.sbproj
  Code/
    Game/
      Board.cs                    # GameBoard — flat 100-cell grid, rotation, resolution
      GameController.cs           # Solo game state + timer (Component)
      MultiplayerController.cs    # WS lobby + game state (Component)
      PlayerData.cs               # Persisted player settings (JSON via FileSystem.Data)
    Api/
      ApiClient.cs                # REST client using Http class
      Models.cs                   # Response types: PuzzleResponse, LeaderboardEntry, etc.
    Ws/
      WsClient.cs                 # WebSocket wrapper (Component)
    UI/
      GameHud.razor               # Board painting (HudPainter) + in-game overlays
      ArcadeScreenPanel.razor     # WorldPanel attract screen on cabinet (display-only)
      ButtonGlyphPanel.razor      # CW/CCW glyph labels on cabinet buttons
      CenterInfoPanel.razor       # East-wall info board (WorldPanel, hung by InfoWall)
      DevNotesPanel.razor         # East-wall dev-notes board (fetches /api/v1/devnotes)
      DemoInfoPanel.razor         # Demo caption floating in front of the demo cabinet
      NameTagPanel.razor          # Steam + Rotaliate name floating over remote players
      WallCountdownPanel.razor    # Wall-mounted countdown display
      WallLeaderboardPanel.razor  # Wall-mounted leaderboard display
      SettingsModel.cs            # Shared settings rows/cells + SettingsVersion
      WallSettingsPanel.razor     # South-wall settings status boards (display-only)
      WallTextPanel.razor         # Generic wall text panel
      Screens/
        SplashScreen.razor        # First-time onboarding (username enrollment)
        ModePickerScreen.razor    # All in-station menus (see sub-views below)
        MultiplayerScreen.razor   # Live multiplayer HUD overlay
        LobbyOverlay.razor        # Always-on "Press E" / "Leave Screen" overlay
        ChatPanel.razor           # Lobby text chat (platform Chat API, custom UI)
        SettingsScreen.razor      # Engaged world/host settings editor (cursor UI)
    Theme/
      Colors.cs                   # Color palettes (matches Go server and JS client)
    World/
      ArcadeRing.cs               # Procedural N-gon station layout + UiRectStyle()
      ArcadeStation.cs            # Per-station occupancy, Enter/Leave, NetworkSpawn
      CubeBoardView.cs            # 3D physical cube board (slide-out, rotation, explode)
      DiscordButton.cs
      FacePlayer.cs               # Yaw-billboard GO toward camera (used by name tags)
      InfoWall.cs                 # East-wall info + dev-notes boards + DiscordButton
      LeaderboardWall.cs
      LobbyNetworkManager.cs      # ISceneStartup host init + player spawning
      LobbyPlayer.cs              # Third-person camera, proximity engage, proxy rules
      LobbyRoom.cs                # Procedural floor + 4 walls
      MarqueeGlow.cs              # Marquee spot color owner (user tint × duck)
      RemoteBoard.cs              # Spectator board sim driven by relayed move stream
      SettingsStation.cs          # Engage target per settings board (camera anchor)
      SettingsWall.cs             # South-wall settings boards + room-light applier
    Audio/
      SoundPlayer.cs
  Assets/
    scenes/
      lobby.scene                 # Main scene: 3D lobby with arcade stations
    sounds/                       # .sound event assets
      sfx/                        # WAV sources + compiled .vsnd files
  ProjectSettings/
    Input.config                  # Action bindings
```

### ModePickerScreen sub-views

`ModePickerScreen` is a single Razor component implementing all in-station UI as a `MenuView` enum:

| View | Content |
|---|---|
| `Main` | Mode buttons (Daily, Hourly, Free Play, 4P, 2P), Profile/Settings secondary buttons, replay-by-ID input |
| `Leaderboard` | Daily/Hourly/Multiplayer tabs, time/moves toggle, paginated leaderboard with replay links |
| `Profile` | Username edit, Player ID copy, color scheme selector (4 palettes) |
| `Settings` | Explodiness toggle (None/Finish only/Match), Gravity toggle, keybinding rebind per action, Reset to Defaults |
| `MpLobby` | Create Lobby / Join by code; shows lobby code + player list once in lobby |
| `MpWaiting` | Match-found room: own color swatches, player list with ready state, Ready button |

**Keyboard navigation:** Up/Down/Left/Right move a focused-button index; Enter/Space activates. The `.focused` CSS class (white outline) marks the focused item. `IsRebinding` gates navigation off while waiting for a rebind keypress.

---

## API

Base URL: configurable constant in `ApiClient.cs` (default `https://rotaliate.io`).
WS URL derived by replacing `https://` → `wss://` and `http://` → `ws://`.
API reference: `https://test.rotaliate.io/apidoc`.

**Every request sends `X-Player-ID: <player guid>`** — solo sessions are keyed to it.

### Endpoints

| Method | Path | Notes |
|---|---|---|
| POST | `/api/v1/players` | Create player → `{guid, player_tag}` |
| GET | `/api/v1/players/{guid}` | Validate GUID on startup → `{guid, username, player_tag}` |
| PUT | `/api/v1/players/{guid}/username` | Set username → `{username, player_tag}` (tag recomputed); 409 `{error}` if name taken |
| GET | `/api/v1/puzzle/daily` | → `{seed, grid, session_id}` |
| GET | `/api/v1/puzzle/hourly` | → `{seed, grid, session_id}` |
| POST | `/api/v1/puzzle/freeplay` | Body: `{seed}` (number, 0 = random) → `{seed, grid, session_id}` |
| POST | `/api/v1/sessions/{id}/moves` | Body: `{move}` → `{move_count, solved[, duration_ms]}` |
| GET | `/api/v1/leaderboard/daily/time` | |
| GET | `/api/v1/leaderboard/daily/moves` | |
| GET | `/api/v1/leaderboard/hourly/time` | `?puzzle_id=` optional |
| GET | `/api/v1/leaderboard/hourly/moves` | `?puzzle_id=` optional |
| GET | `/api/v1/leaderboard/multiplayer/{size}` | size = 2 or 4 |
| GET | `/api/v1/lobbies/open` | Public match browser — open lobbies with a free slot `[{code, mode, count, max, host, created_at}]`; `?mode=2\|4` filters by room size |
| GET | `/api/v1/hourly/recent` | 5 most recent hourly puzzles |
| GET | `/api/v1/sessions/{id}/replay` | Replay data |
| POST | `/api/v1/feedback` | `X-Player-ID` header optional |
| WS | `/ws/matchmaking?player_id=` | 4-player matchmaking |
| WS | `/ws/matchmaking2?player_id=` | 2-player matchmaking |

### Solo session move stream (anticheat backend)

- `session_id` is an in-memory server session (evicted after 6h idle); empty if no
  `X-Player-ID` was sent (e.g. regenerating a grid for replay).
- **Every move is streamed as it happens** to `POST /sessions/{id}/moves`, strictly
  serially in play order — each send awaits the previous response (`GameController`
  chains tasks; no fire-and-forget).
- Move encoding (one byte 0–242): rotation = `direction*81 + row*9 + col` (0–161,
  dir 0=CW 1=CCW, row/col = 2×2 top-left 0–8); selector reposition =
  `162 + row*9 + col` (162–242, destination only) — sent whenever the selector lands
  on a new 2×2.
- **Selector moves count as moves**: the move counter includes them and the timer
  starts on the first move of any kind.
- The server timestamps moves on arrival and persists the session itself on solve —
  there is **no completion call** and no local duration/totals are submitted. The
  solving move's response carries the authoritative `duration_ms`.
- Rate limit: token bucket per session, sustained 1 move/30ms, burst 10 → client
  throttles sends to 60ms apart. 429 = rate limited (server skipped the move → local
  board desync), 404 = session unknown/expired or X-Player-ID mismatch, 422 = invalid
  move; on any error the client stops streaming and warns that the run is unrecorded.
- Replay entries may carry `"selector": true` — cursor reposition, board unchanged,
  still counts in the move counter. `played_at` is server arrival time.
- Multiplayer WS `move` messages use the same 0–242 encoding: rotations (0–161)
  apply to the shared board; selector repositions (162–242) update the server-tracked
  cursor and broadcast `selector_sync` without changing the board or move count.
  The old `selector_move` message type is gone.

### player_tag (issue #46)

- 8-hex-char public player identifier: `hex(sha256(lowercase(guid)+username))[:8]`,
  **server-computed only** — not derivable locally, and it changes on every username
  change. It replaces the GUID everywhere other players can see it: leaderboard rows
  and `GET /sessions/{id}` carry `player_tag` (no `player_id`), and the WS messages
  `lobby_created`/`lobby_update`/`room_ready` players, `player_ready`, and
  `player_left` carry `player_tag`; `game_over` carries `winner_tag`.
- Self-identification ("You" labels, win check) compares `player_tag == myTag`
  (`MultiplayerController.MyTag`), never the GUID.
- Own tag is cached in `PlayerData.PlayerTag`, refreshed from every endpoint that
  returns it (enroll, username save, GUID import) and backfilled on cabinet entry /
  MP connect for pre-migration identities.
- Unchanged: `X-Player-ID` header and the `?player_id=<guid>` WS query param (client
  authenticating itself to the server).
- Legacy leaderboard rows with no username display the `player_tag`.

s&box HTTP: `await Http.RequestAsync(url, method, content, headers)` — the trailing
`Dictionary<string,string> headers` parameter carries `X-Player-ID`. **It is not in
`../sbox-docs`** (only `WebSocket.Connect` documents headers) — if it fails to compile,
that's the first thing to check.
s&box WebSocket: `new WebSocket()` — see `../sbox-docs/docs/networking/websockets.md`

---

## Player Identity

GUID created via `POST /api/v1/players` on first launch; stored in `FileSystem.Data` as JSON.
On startup: validate GUID with `GET /api/v1/players/{guid}`; if 404, create new.

### Persisted fields (JSON in `FileSystem.Data`)
| Key | Values |
|---|---|
| `guid` | player UUID |
| `username` | display name |
| `playerTag` | 8-hex public id (server-computed; changes with username) |
| `colorScheme` | `normal` \| `deuteranopia` \| `protanopia` \| `tritanopia` |
| `layoutSwap` | `standard` \| `swap` |
| `completeEffect` | `slide` (no explosion) \| `explode` (finish only) \| `match` (resolved cells shatter mid-game — **default**) |
| `explodeGravity` | `false` (default) — whether physics debris falls |
| `worldLightColor` | `#RRGGBB` room-light hue; `""` (default) = scene hue. Also drives the wall-board UI theme (`WallTheme.cs`) |
| `worldLightBrightness` | `0`–`1.5` multiplier on the scene room light (default `1`) |
| `marqueeLightBrightness` | `0`–`1.5` multiplier on `ArcadeRing.MarqueeBrightness`; `0` = off (default `1`). Marquee hue is hardcoded pure white (no color option) |
| `myCabinetSounds` | `true` (default) — 2D sounds from the engaged cabinet |
| `remoteCabinetSounds` | `true` (default) — positional sounds from other cabinets |
| `demoSkip` | `false` (default) — never run the attract demo |
| `musicEnabled` | `true` (default) — play the procedural ska track |
| `musicVolume` | `0`–`1.5` multiplier on MusicController's baseline volume (default `1`) |
| `bindings` | `Dictionary<string,string>` action → key overrides |

---

## Game Logic (`code/Game/Board.cs`)

`GameBoard` stores the grid as a flat 100-element `int[]` (row-major: `index = row*10 + col`).

**Rotation — must match Go server (`internal/game/`) exactly:**
- CW (dir=0): `newTL=BL, newTR=TL, newBR=TR, newBL=BR`
- CCW (dir=1): `newTL=TR, newTR=BR, newBR=BL, newBL=TL`

**Group resolution:** after each rotation, scan all 2×2 positions repeatedly until no more
same-color groups exist. Each pass sets matching non-zero groups to 0.

Unit tests should verify rotation and resolution produce identical output to the Go server.

---

## Board Rendering (`code/UI/GameHud.razor` + HudPainter)

Use `Scene.Camera.Hud` (HudPainter) each frame to draw the board. See `../sbox-docs/docs/ui/hudpainter.md`.

Replicate the cell rendering style:
- **Active cells:** vertical gradient (lightened top → base mid → darkened bottom),
  shine highlight (upper 44%), rounded rect ~18% of cell size
- **Solved cells (color=0):** deep dark fill + radial vignette
- **Flash:** white overlay on resolved cells for 350ms
- **Selector:** solid white 3px outline over the 2×2 selection
- **Remote selectors:** dashed colored outline (multiplayer)
- **Rotation animation:** 90ms ease-out cubic — animate selected cells along their arc
  while the rest of the grid renders statically; apply move only after animation completes

Color palettes in `code/Theme/Colors.cs` must match the values below exactly.

---

## Board Animation Detail

Mirror the Flutter board animation model in `GameController`:
- Store a `RotateAnimRequest` with pre-rotation cell colors on `RequestRotate(dir)`
- Start the 90ms animation; call `ApplyMove()` only in the animation-complete callback
- One move may be queued while animation is in flight; drop moves that arrive while a queued move is already waiting

---

## Multiplayer (`code/Ws/WsClient.cs` + `code/Game/MultiplayerController.cs`)

`WsClient` wraps `WebSocket`. Sends a `ping` every 30s. Exposes a message-received event and a done event.

`MultiplayerController` state machine:
```
connecting → lobby → waiting → playing → gameOver
                                       ↘ disconnected (on WS close/error)
```

Key rules:
- `MinMoveInterval` = 125ms — don't send rotations faster
- Selector repositions are sent as encoded moves (`162 + row*9 + col`) through the
  same `move` message, throttled to ~20/s (50ms) and only when the position changed
  (server enforces a 40ms minimum)
- Board state comes from `state_sync` — no client-side resolution in multiplayer
- `state_sync` carries `last_move {move, color}` (the rotation that produced it;
  absent for elimination syncs). Opponent rotations are animated from it: when not
  already animating, start a 90ms anim from the pre-sync grid and apply the synced
  grid on completion (woosh on start). Own rotations animate locally at send time;
  their sync is buffered by the existing `Animating`/`_pendingGrid` path
- Public matches: the `create_lobby` payload `{public}` is required — `true` lists
  the lobby in the public match browser (`GET /api/v1/lobbies/open`); joining a
  browsed match uses the normal `join_lobby {code}` flow. `lobby_created` echoes
  `public`. The MpLobby view shows Public Match / Private Match create buttons plus
  the open-lobby list, polled every 5s (`ModePickerScreen.RefreshOpenLobbies`) with
  a manual ⟳ refresh; polling pauses while in a lobby (`LobbyCode` set)
- Lobby/waiting-room rosters: `lobby_created`/`lobby_update` carry `players`
  `[{player_tag, username}]`; `room_ready` players include `username`; each ready click
  broadcasts `player_ready {player_tag, username, ready_count, player_count}` to the room.
  Client tracks `RoomPlayers`/`ReadyIds`/`SentReady`; the Ready button becomes "Waiting…"
  after clicking

---

## Color Palettes (`code/Theme/Colors.cs`)

| Name | Red | Blue | Green | Yellow |
|---|---|---|---|---|
| normal | `#AA0000` | `#0000AA` | `#00AA00` | `#AAAA00` |
| deuteranopia | `#8E3F00` | `#004C77` | `#00694D` | `#AAAA00` |
| protanopia | `#8E3F00` | `#004C77` | `#1F8379` | `#AAAA00` |
| tritanopia | `#AA0000` | `#0000AA` | `#00AA00` | `#AA8F07` |

Updated 2026-06-12 (PR #50): primaries for normal, all palettes scaled to 2/3
intensity (full 255 too harsh on the unlit cubes); colorblind schemes
Okabe-Ito-based, simulation-verified (min pairwise ΔE ≥ 36 under the target
deficiency). The Go server / JS client still use the old palette — update them
to match if cross-client visual parity matters (board colors are render-only,
no protocol impact).

Background / solved cell: `#07051a` (all palettes).

---

## Keyboard Controls

| Key | Action |
|---|---|
| Arrow keys | Move selector |
| Z or , | Rotate CCW |
| X or . | Rotate CW |

Use `Input.Pressed("action")` in `OnUpdate()`. Define bindings in project settings.
See `../sbox-docs/docs/gameplay/input/index.md`.

---

## s&box Patterns to Follow

- **Components**: game logic lives in `Component` subclasses; override `OnUpdate()` for per-frame work
- **UI**: screens are Razor `PanelComponent` files added to a `ScreenPanel` GameObject in the scene
- **State**: use `[Sync]` only for s&box's built-in peer networking (not needed here — we talk to our own Go backend via WS/HTTP)
- **Storage**: `FileSystem.Data.ReadAllText` / `WriteAllText` for JSON player data
- **HTTP**: `await Http.RequestStringAsync(url)` for GET; `await Http.RequestAsync(url, "POST", Http.CreateJsonContent(body))` for POST — see `../sbox-docs/docs/networking/http-requests.md`
- **WebSocket**: `new WebSocket()` component — see `../sbox-docs/docs/networking/websockets.md`
- **Hotload**: s&box hotloads C# changes in milliseconds; no restart needed during dev
- **HudPainter type**: use `@using Sandbox.Rendering;` in Razor files — the full type is `Sandbox.Rendering.HudPainter`
- **Razor usings**: `System`, `Sandbox`, `Sandbox.UI`, and `Sandbox.Rendering` are NOT auto-imported in `.razor` files; add `@using` directives explicitly

---

## s&box API Whitelist

s&box enforces an API whitelist — blocked calls produce `error SB1000`. See `../sbox-docs/docs/code/code-basics/api-whitelist.md`.

| ❌ Blocked | ✅ Use instead |
|---|---|
| `Array.Clone()` | Manual `for` loop copy into `new T[]` |
| `Console.WriteLine` | `Log.Info` / `Log.Warning` / `Log.Error` |
| `System.IO.*` | `FileSystem.Data` (already used for player data) |

**Rule of thumb:** avoid `System.Private.CoreLib` methods that touch reflection, process, threading, or IO. When in doubt, check the s&box API reference at `https://sbox.game/api/` or file a false-positive report at `https://github.com/Facepunch/sbox-public/issues`.

---

## Implementation Order

1. `Board.cs` + unit tests — get rotation + resolution logic correct before touching UI
2. `ApiClient.cs` + `Models.cs` — REST calls and JSON deserialization
3. `WsClient.cs` — WebSocket wrapper
4. `GameController.cs` — solo game state machine
5. Board HudPainter rendering — static first, then animation
6. Solo game UI: ModePickerScreen → GameScreen → LeaderboardScreen
7. Player identity: SplashScreen, ProfileScreen, persistent storage
8. `MultiplayerController.cs` + MultiplayerScreen
9. FaqScreen, polish

---

## 3D Lobby (`feature/fable-lobby`)

The game is now a 3D third-person experience. Startup scene is `Assets/scenes/lobby.scene`:
a bedroom-sized room (240×240×80 units, no ceiling, walls/floor `#426967`) with a PointLight
above and a wall-mounted screen the player plays Rotaliate on. The earlier `feature/lobby`
branch was abandoned as unusable — do not build on it.

**UI vocabulary (wall thing vs. pop-up thing) — use these terms consistently:**
- **Board** = a display-only `WorldPanel` mounted on a wall/cabinet (`Wall*Panel.razor`,
  `ArcadeScreenPanel`, `CenterInfoPanel`, …). Takes no pointer input; sized by GO scale +
  fixed px in the panel's intrinsic pixel space.
- **Screen** = an interactive `ScreenPanel` (`*Screen.razor`) shown only while the player is
  **engaged** at a station (press E). Plain cursor input works. Each is gated on its station's
  `Active` and confined to a full-screen centering wrapper.
- **Engaged-screen centering gotcha:** the centering (`align-items/justify-content: center`)
  must live on an **absolutely-positioned full-screen child** (`.board-fit` /
  `.screen-fit` / `.arcade-fit`), NOT on `root` — `root` with align/justify alone leaves the
  panel pinned top-left at content size (bit LeaderboardScreen twice). Mirror
  SettingsScreen/MusicScreen, and wrap content in an explicit `<root>` element.

**Design: hybrid screen.** Mouse input on WorldPanels is undocumented, so the WorldPanel on the
cabinet (`ArcadeScreenPanel.razor`) is display-only (attract screen / "▶ name" while occupied).
The real game UI is the proven ScreenPanel UI, shown only while the player is locked into the
station — the camera is locked facing the cabinet screen and the game UI is confined to an
`.arcade-fit` wrapper div (SplashScreen / ModePickerScreen / MultiplayerScreen / GameHud) sized
each frame by `ArcadeRing.UiRectStyle()` / `ScreenFractionRect()`: no projection API is
documented, but the locked camera is square-on at `CameraDistance` from a screen of half-extent
`18 * CabinetScale * ScreenScale`, so the screen's viewport rect is plain trig from the camera
FOV (assumed horizontal; `ArcadeRing.UiFit` calibrates). The HudPainter board sizes itself from
the same rect (`GameHud.ComputeBoardLayout`, `BoardFraction` of the rect). The rect feeds each
screen's `BuildHash` so resizes re-render. Cursor works because that's plain ScreenPanel input.
**`transform: scale` misplaces panel content (tried and reverted) — use explicitly sized
wrappers.** The local player's body SkinnedModelRenderer is
disabled while engaged (LobbyPlayer.Engage/Disengage) so the avatar doesn't block the locked
camera, and Disengage blends the camera back out along the reverse path before re-enabling the
PlayerController.

**Components (`Code/World/`):**
- `LobbyRoom.cs` — builds floor + 4 walls at runtime (`OnEnabled`, `ExecuteInEditor`, spawned
  GOs flagged `NotSaved`). Colliders on uniformly-scaled parents (`BoxCollider.Scale` explicit);
  visuals on non-uniformly-scaled children (`models/dev/box.vmdl` sized via `Model.Bounds`).
  Never put a BoxCollider on a non-uniformly scaled GO — it silently freezes physics.
- `ArcadeStation.cs` — one per wall screen. `Enter()`/`Leave()`; tracks `OccupantGuid`
  (Rotaliate player UUID) + `OccupantName`; `static Active` = station the local player is in.
  `Leave()` resets GameController + MultiplayerController and clears occupant, so the screen
  state is clean for the next session (multiplayer-lobby ready).
- `LobbyPlayer.cs` — on the Player GO. Proximity + `use` (E) to engage; disables the
  `PlayerController` while engaged (no movement/mouselook) and eases the camera to the
  station's `CameraAnchor` over 0.35s.
- **The citizen `SkinnedModelRenderer` must live on a `Body` child GO, never on the
  PlayerController's own GO.** The controller's animator module treats the renderer's GO as
  the player body and writes local position 0 + smoothed eye-yaw rotation to it every frame —
  on the controller's GO this welds the player to world origin (symptoms: never falls, GO yaw
  tracks the mouse with a hands-off judder, transform edits snap back to 0,0,0). Escape leaves the screen (consumes `Input.EscapePressed`
  — there is **no documented API to add buttons to the built-in s&box escape menu**), except
  while Settings key-rebinding is listening (`ModePickerScreen.IsRebinding`).

**UI:**
- `LobbyOverlay.razor` — always-on-top ScreenPanel: "Press E" prompt when near a free screen;
  always-visible "✕ Leave Screen" button while engaged.
- `SplashScreen` / `ModePickerScreen` razor roots are gated on
  `ArcadeStation.Active != null && LobbyPlayer.Local?.CameraSettled` — hidden while roaming.
- Keyboard game input is gated by `GameController.InputActive` (set by `ArcadeStation`).

**Needs tuning in the editor (couldn't verify on this machine):**
- `WorldPanel.PanelSize` property name/format in `lobby.scene` ("1280 720") — if wrong, panel
  is default-sized; fix in inspector.
- WorldPanel facing: Screen GO has yaw-180 rotation assuming the panel faces its GO's +forward;
  if it renders away from the room, rotate the Screen GO 180°.
- `CameraAnchor` local position (-75, 0, 52) sets how much of the FOV the wall screen fills.

**Confirmed working in the editor (2026-06-10):** spawn position, third-person camera, WASD
movement via the built-in PlayerController input module (manual WishVelocity drive removed).

**Known lobby bugs:**
- Camera goes first-person when leaving the station (controller re-enable loses ThirdPerson view)
- ~~Player model draws in front of the game UI while playing~~ fixed: body renderer hidden while engaged

---

## Networked Lobby (`feat/multiplayer`)

The lobby is a networked space (s&box built-in peer networking) with **N arcade stations
arranged as an N-gon in the middle of the room** (default 8, octagon), screens facing each
other across the center; players stand inside the ring. Rotaliate gameplay is still
per-player against the Go backend — only avatars and station occupancy are networked.

**`ArcadeRing` (`Code/World/ArcadeRing.cs`, on the Room GO, `feature/expandlobby`):** builds
the stations procedurally — `StationCount`, `Radius`, `ScreenHeight`, `CameraDistance`,
`BuildCabinets` properties. Stations are named `ArcadeStation0..N-1`. Each station root holds
the `ArcadeStation` component plus children: `Screen` (WorldPanel + ArcadeScreenPanel, yaw
180, facing ring center), `CameraAnchor` (local `(-CameraDistance, 0, ScreenHeight)`), and
`Cabinet` — box primitives (Base/Head/Marquee) + one BoxCollider, swappable for a real model
later. Build paths: `OnEnabled` (ExecuteInEditor) builds a NotSaved editor preview;
play-only `OnStart` clears that preview; the **host** rebuilds via
`LobbyNetworkManager.OnHostInitialize` → `ring.Build()` and NetworkSpawns each station, so
clients get exactly one networked copy. WorldPanel size is left at default in code
(`PanelSize` is undocumented) — tune in editor if attract text is mis-scaled.
Room/ring/cabinet dimensions are **scene-tuned, not code defaults** — see scale rules below.
Earlier branches (`feature/multiplayer`, `feature/lobby`) are abandoned — do not build on them.

**World scale rules — read before placing/sizing anything in the scene:**
- **Never trust code defaults or this doc for component property values** (RoomSize,
  StationCount, Radius, scales, …) — the scene overrides them and gets retuned in the
  editor. `grep` `Assets/scenes/lobby.scene` for the current values before sizing anything.
- The player is ~72 units tall; use that as the human-scale yardstick.
- `models/dev/box.vmdl` is **NOT 1×1×1 unit**. To make a box of size S, divide by the model's
  bounds: `LocalScale = S / Model.Bounds.Size` per axis — use/copy `ArcadeRing.AddBox`.
  Setting raw LocalScale as if it were a size produces geometry thousands of units across.
- A WorldPanel GO's scale is a **multiplier on the panel's intrinsic size, not world units**.
  The cabinet screens use ~1.5 total; the center info board uses `InfoBoardScale` ≈ 3.5.
  Scaling by e.g. (80, 1, 120) makes the panel building-sized with invisible text. The
  panel plane is local **Y (width) / Z (height)** — mild non-uniform scale on those axes
  changes the aspect (info board uses (1, 1.3, 2) × InfoBoardScale).
- `FacePlayer` (`Code/World/FacePlayer.cs`) yaw-billboards a GO toward the local camera;
  panel fronts face the GO's **+forward** (verified in editor; set `Flip` if backwards).
- There is **no documented s&box API to open a URL / Steam overlay** (tried
  `Sandbox.Services.Overlay` — doesn't exist). Show links as plain text instead.
- `OnValidate` fires on editor property changes and after deserialization — LobbyRoom and
  ArcadeRing rebuild their previews there, so generated geometry shows on editor load
  without entering play mode (ArcadeRing guards on `_runtimeBuilt` to protect the host's
  networked build).
- A WorldPanel's intrinsic pixel size is fixed (PanelSize undocumented), so a panel's
  world-size and its text size are coupled: to grow a board without growing its text,
  scale the GO up and divide the stylesheet px values by the same factor
  (CenterInfoPanel uses halved px against a 2× InfoBoardScale).

**Create-or-join:** `LobbyNetworkManager` (`Code/World/LobbyNetworkManager.cs`) implements
`ISceneStartup.OnHostInitialize` → `Networking.CreateLobby()`. That event never fires when
joining someone else's lobby, so no extra logic is needed.

**Player spawning:** no `.prefab` asset (hand-authoring the format is undocumented). The old
Player GO lives in `lobby.scene` as a **disabled `PlayerTemplate`** child of the `Network` GO;
`LobbyNetworkManager` (`Component.INetworkListener.OnActive`, host-side, fires for every
connection incl. local) clones it, enables it, and `NetworkSpawn( connection )`s it. Spawns
fan out along Y by `SpawnSpacing` so joins don't stack.

**Proxy rules (`LobbyPlayer`):** `Local` set in `OnStart` only when `!IsProxy`; proxies
destroy their Camera child (only one `IsMainCamera` per client) and early-return in `OnUpdate`.

**Station occupancy:** host network-spawns every `ArcadeStation` GO in `OnHostInitialize`
([Sync] needs `NetworkMode.Object`). Occupant fields (`OccupantSteamId`/`Guid`/`Name`) are
`[Sync( SyncFlags.FromHost )]`, driven by `[Rpc.Host] RequestEnter/RequestLeave` — host is
authoritative, first enter wins, no ownership transfer. `OnDisconnected` frees stations the
leaver occupied. `LobbyPlayer.FindNearbyStation` skips occupied stations, so the "Press E"
prompt only shows on free screens. Small race window (~RTT) if two players press E on the
same screen simultaneously — host picks the winner but the loser's client thinks it engaged;
known limitation.

**Testing:** run lobby.scene → network status icon in header → "Join via new instance".
Note: same-machine instances share `FileSystem.Data`, so they share one Rotaliate GUID.

---

## Project Setup (first time on a new machine)

s&box's package manager tracks local projects in its own registry — cloning the repo and opening the `.sbproj` directly will fail with `Unable to find package 'local.rotaliate#local'`.

**Correct flow:**
1. Open the s&box editor → **New Project** → Game (Empty), pointed at the cloned repo folder
2. The editor writes its own `.sbproj` and registers the project; use that file, not the one in the repo
3. The editor hotloads C# automatically — check the error list for compile errors

**Project structure** (matches what the editor generates):
```
scripts/                ← dev utilities (not s&box assets)
  gen_sounds.py         ← synthesize WAV source files (requires numpy)
rotaliate/              ← open rotaliate/rotaliate.sbproj in the editor
  rotaliate.sbproj
  rotaliate.slnx
  Code/                 ← all game C# and Razor files live here (capital C)
    Audio/SoundPlayer.cs
  Editor/               ← editor assembly
  Assets/
    scenes/             ← scene files
    sounds/             ← .sound event assets (reference .vsnd inside sfx/)
      sfx/              ← source WAVs + compiled .vsnd files
  ProjectSettings/      ← Input.config, Collision.config, Platform.config
  .sbox/                ← editor state (gitignored, machine-specific)
```

**Paths in csproj/slnx** assume Steam is at `D:\Steam\` (four levels up from `rotaliate/Code/`). If Steam is elsewhere, update the relative paths in `rotaliate.csproj` and `rotaliate.editor.csproj`, or let the editor regenerate them.

The `.sbproj` committed to the repo is a reference template; always use the editor-generated one when working locally.

---

## Known TODOs

- Replay screen (server supports it, client not built)
- Drag-to-rotate control style
- Input action bindings defined in `rotaliate/ProjectSettings/Input.config` — verify they appear in the editor's Project Settings → Input after loading

---

## Sound Effects (`Code/Audio/SoundPlayer.cs`)

Four synthesized sounds, all played 2D via `SoundPlayer` static helpers:

| Sound | Trigger | Frequency |
|---|---|---|
| `tick` | Local selector move | 1100 Hz sine |
| `tock` | Opponent selector move (MP) | 770 Hz sine |
| `woosh` | Rotation start | 420→140 Hz sawtooth sweep |
| `pop` | Group resolved | C-major arpeggio, triangle |

**Asset pipeline:**
- Source WAVs live in `rotaliate/Assets/sounds/sfx/` — generated by `scripts/gen_sounds.py` (requires `numpy`)
- s&box compiles WAVs → `.vsnd` on first access in the editor
- `.sound` event files in `rotaliate/Assets/sounds/` reference the `.vsnd` paths

**`.sound` file format gotchas** (hand-editing these is error-prone — use the editor if possible):
- `"Sounds"` is an array of `.vsnd` paths, not `.wav`
- `"Volume"` and `"Pitch"` are JSON strings (`"1"`), not numbers or arrays
- `"UI": true` for 2D/flat playback (no distance attenuation)
- `"__version": 1`

`Mouse.Visible` is obsolete in current s&box — removed from both controllers.

---

## CLAUDE.md Review History

- Initial s&box scaffold committed on `feature/initial-sbox-project`.
- API whitelist section added after `Array.Clone()` hit SB1000; Razor using-directive rules added after HudPainter namespace errors.
- Project restructured into `rotaliate/` subfolder after discovering s&box generates that layout; `code/` → `Code/`, `scenes/` → `Assets/scenes/`.
- UI pointer events: `pointer-events: all` must be set on each interactive element individually — it does not inherit in s&box.
- Panels are flex containers: inline `<span>`s mixed with text inside a paragraph div become separate flex items and garble the paragraph — keep paragraphs plain text.
- Source newlines/indentation inside a text div render as literal whitespace (a blank first line that pushes text out of its box) — keep each text div's content on one line with no surrounding whitespace.
- A div's auto height does NOT grow for wrapped text (it sizes to ~one line and further lines spill out), and an explicit `height` doesn't fix it either (text stays vertically centered and still overflows). For multi-line text in a styled box, use one div per line inside a flex column.
- WorldPanel layout happens in the panel's fixed intrinsic pixel space; the GO scale only sizes/stretches the rendered quad — fix blank space/overflow with CSS px values, not GO scale.
- Sound effects added on `feature/audio`; `.sound` format learned empirically — see Sound Effects section above.
- Anticheat backend migration (`feat/session-api`): completion endpoints replaced by per-move streaming — see "Solo session move stream". `Http.RequestAsync`'s headers parameter is used but undocumented in `../sbox-docs`; verify in editor.
- Deriving font sizes from `Panel.Box.Rect` on a WorldPanel doesn't work (north-wall leaderboards rendered ~1.5× too wide and clipped) — use fixed px values in the intrinsic pixel space, calibrated against a known-good panel (cabinet attract title: 28px bold + 6px letter-spacing fits 9 chars with margin). Non-uniform GO scale stretches glyphs; keep WorldPanel scales uniform and shape content with a CSS sub-rect instead.
- Planned project structure (Planned Project Structure section) reflected aspirational paths; actual layout documented in the updated Project Structure section above.
- `CompleteEffect` default changed from `"explode"` to `"match"` (issue #19); `ExplodeCell` shards now use random palette colors (confetti) instead of the source cell's color.
- Leaderboard removed from ModePickerScreen main view (issue #20); keyboard nav (Up/Down/Left/Right + Enter) added to all menu sub-views.
- Wall leaderboard aim-to-copy (issue #38): WorldPanels take no pointer input, so LobbyPlayer ray-casts the camera at WallLeaderboardPanel quads (plane trig + Box.Rect ratios for the row pick) and E copies the aimed row's gameID; replay HUD shows the replayed game's username/mode/seed from `GET /sessions/{id}`.
- Hotload staleness (issue #36): procedural builders (LobbyRoom/ArcadeRing/LeaderboardWall) build in OnEnabled/OnValidate, and a code hotload re-runs neither — generated geometry kept reflecting old code, which presented as "many changes require restarting the editor". Fixed with `[EditorEvent.Hotload]` in `rotaliate/Editor/HotloadRebuild.cs` calling each builder's public `RebuildPreview()` (ArcadeRing keeps its `_runtimeBuilt` guard). Related engine pitfall: hotload copies runtime field values, so changing a field's code default needs a restart to show — moot here, scene values override code defaults anyway.
- South-wall settings boards (issue #49): `SettingsWall` hangs two display-only
  `WallSettingsPanel` status boards (world / host settings) on the south wall. Editing
  uses the cabinet engage flow, not aim-and-E (per-cell aim in WorldPanel pixel space
  was tried and replaced — too fiddly, and the rows overflowed the intrinsic panel
  space): each board gets a `SettingsStation` + camera anchor; walk up, "Press E to
  change world/host settings" locks the camera on (LobbyPlayer.EngageBoard) and the
  interactive `SettingsScreen` ScreenPanel opens with a normal cursor, confined to a
  `.board-fit` div sized by `SettingsStation.UiRectStyle()` (same trig as
  `ArcadeRing.ScreenFractionRect`, reusing the ring's UiFit). Rows/cells live in the
  shared `SettingsModel`; no scrollbar needed — the engaged rect has ample space.
  Light settings: `SettingsWall` retints the scene `RoomLight` (play-mode only, gated
  by an OnStart flag since OnStart never runs in editor); `MarqueeGlow` (on each
  marquee light GO) is the single writer of the marquee SpotLight color (user tint ×
  brightness × duck) — CubeBoardView now drives its `Duck` instead of writing
  `LightColor`. Host cabinet-count change (2–16): the pick is held while the host is
  still on the settings panel (re-pickable; picking the current count cancels), then
  0.5s after the panel closes the ring re-checks occupancy, slides the stations down
  through the floor (`ArcadeRing` OnUpdate state machine), rebuilds + NetworkSpawns
  below ground, and slides back up. The ring radius scales with the count to keep
  neighbor spacing at the scene-tuned 8-station chord (`ArcadeRing.RingRadius`),
  clamped so camera anchors stay inside the room. Wall leaderboards still use
  aim-and-E — porting them to the engage flow is a tracked follow-up issue (#53).
- Name tags (issue #51): proxies get a `NameTagPanel` WorldPanel above the head
  (Steam display name + smaller synced `LobbyPlayer.RotaliateName` beneath),
  yaw-billboarded by `FacePlayer` with `SpinSeconds = 0`.
- Procedural ska / reggae-rock music (issue: procedural music): `Code/Audio/MusicGen.cs`
  turns a player's 8-hex `player_tag` into a deterministic ~60–90s track via a portable
  PRNG (xmur3 → mulberry32) driving every musical choice. Same tag = same song;
  web-client parity needs the same PRNG + call order + `MusicGen.Config` values
  (composition is RNG-driven, synthesis can differ). Synthesis is subtractive:
  unison-detuned oscillators → resonant low-pass SVF with a cutoff envelope (warm, not
  "8-bit") + full synth drum kit (kick/snare/toms/hats/crash + phrase-end fills).
  Voices: varied reggae/ska bass patterns, a loud offbeat **skank guitar** + reggae
  **organ bubble** (centered), chord-tone-locked **lead** (RNG trumpet/sax/organ/
  trombone, knob-weighted, panned), a panned backing **horn section**, centered drums.
  Default voicing targets a **Sublime** vibe (laid-back tempo, bass-forward, clean).
  `MusicGen.GenerateSamples()` returns interleaved stereo 16-bit PCM; `Generate()`
  returns WAV bytes (debug only).
  **Infinite sequence:** each tag seeds an endless list of songs (PRNG seed = `"tag:n"`).
  Each song plays `LoopsPerSong` (default 2) full loops then **crossfades** into the next
  song; `n` auto-advances and is persisted (`PlayerData.MusicN`), so playback resumes
  across sessions. The seed tag is a separate persisted field (`PlayerData.MusicTag`;
  empty = own player tag) so a chosen tag sticks. Length adapts bar count to tempo
  (`Config.TargetSeconds` ~80s, multiple of 8 → clean loop).
  **Vibe string (`Code/Audio/VibeCodec.cs`):** the important generator knobs (tempo,
  swing, voice mix, key cutoffs/resonance, feel chances, lead instrument, horns) encode
  to one base-36 char each → a short fixed-length code. The full shareable seed is now
  **`vibe:tag:n`** (`MusicController.CurrentSeed`). `BuildConfig` = inspector knobs with
  the persisted `PlayerData.MusicVibe` applied over them (empty ⇒ vibe is just the
  encoded knobs, so it always round-trips). PRNG seed stays `tag:n` — the vibe only
  changes the `Config`, so `vibe:tag:n` fully determines the song. `MusicController.PlaySeed`
  parses `vibe:tag:n` / `tag:n` / `tag`; `UseOwn` clears tag+vibe.
  **Subdivisions (sixteenths/triplets):** the grid is eighth-based (`EighthsPerBar`), but
  `Config.DrumBusy` (vibe `DRUM BUSY`, default 0.6, effect scaled ×0.75 so the knob ceiling
  isn't frantic) layers quieter 16th-note hats + ghost
  snares and scales kick syncopation, and `Config.TripletChance` (vibe `TRIPLETS`, range 0–0.1,
  default 0.06 — potent, narrow band) turns drum fills into 3- or 6-note triplet rolls and
  inserts lead ornaments at varied rates: 16th pair, tight 16th-triplet (1 eighth), eighth-note
  triplet (1 beat), and wide quarter-note triplet (2 beats). The **bass** borrows the same
  16th/triplet feel well under the lead rate (`ornChance = TripletChance*0.25 + DrumBusy*0.05`,
  on standalone non-sustaining notes) for "long long short short" variety, and the kit gets a
  per-song-constant **push/pull** timing bias (`_drumPush`, biased toward pushing so the
  backbeat isn't "super late"); both are driven by **dedicated PRNG streams** (`"bass:"+tag`,
  `"push:"+tag`) so the main composition order — and every existing song — is unchanged
  (skafinity #1 ports both). `VibeCodec.Fields` is **append-only**; the code is now **30 chars**.
  Appended knobs: `DRUMS` (master kit gain, `Config.DrumVol`, a straight 0..1.5 multiplier in
  parity with the other five voice volumes — all six default to 1.0, flat mix; the kit also
  carries a `KitPresence` baseline boost ≈2× so transient drums sit against the sustained bed
  at slider 1.0), then the **instrument-matrix fills** — `BASS TRIPLETS` (`BassTriplets`, bass's own
  ornament rate, decoupled from lead `TRIPLETS`), `SKANK BITE` (`SkankHighpass`), `SKANK CHOP`
  (`SkankChop`, chop length), `ORGAN TONE` (`OrganCutoff`), `ORGAN VIBRATO` (`OrganVibrato`),
  `HORN TONE` (`HornCutoff`, was shared with lead), `DRUM PUSH` (`DrumPush`, push/pull variance
  magnitude on `_drumPush`). `Apply` ignores trailing fields a shorter string lacks and
  `LooksLikeVibe` now accepts **≥16 chars** (was `Fields.Count-4`) so older 20/22/23-char shared
  seeds still load (new knobs keep defaults); floor stays far above an 8-char player tag.
  **MusicScreen vibe UI = an instrument mixer matrix** (`Matrix` in `MusicScreen.razor`): rows
  are voices (Bass/Skank/Organ/Lead/Horns/Drums), columns VOLUME | TONE | CHARACTER | EXTRA;
  non-instrument knobs (tempo/swing/fast/resonance/stereo) sit in a GLOBAL strip below. Display
  order is decoupled from the append-only wire order — cells reference fields by name via
  `FieldIndex`, so `VibeCodec.Fields` can keep appending without reshuffling the grid.
  **Playback = `SoundStream` (raw PCM from memory), NOT MusicPlayer** — `MusicPlayer.Play`
  from `FileSystem.Data` returned a live handle but produced no audio. `MusicController`
  (Component on the Room GO in `lobby.scene`, per-client singleton, inspector-authorable
  knobs) downmixes to mono and pushes to one `SoundStream`: `StartSequence` writes the
  first song (LoopsPerSong passes, fade-in, tail held back) and `PushTransition` (in
  OnUpdate when buffered audio < 2s) queues the crossfade + next song's passes, advances
  `n`. Look-ahead keeps `AheadCount` (default 5) songs pre-generated, built **one per tick**
  in OnUpdate so a fill never hitches a frame. Crossfade window = the *current* song's
  reserved tail (`Crossfade` s, default 3.75); the two songs only overlap (both audible)
  for `CrossfadeOverlap` (default 0.5) of that window, centred — the rest plays in the
  clear, so each song is heard fully before the next takes over.
  **2D playback + mixer:** `_handle.SpacialBlend = 0` alone did NOT hold for the stream
  handle (volume/pan tracked the camera), so `ConfigureFlat()` (once per handle) sets
  SpacialBlend=0 **and** parents the handle to the camera with `FollowParent` (the
  `GameObject.PlaySound` follow mechanism) so the listener can't attenuate/pan it, **and**
  routes `_handle.TargetMixer = Mixer.FindMixerByName("Music")` so music isn't on the Game
  mixer (`Mixer` assumed `Sandbox.Audio.Mixer`; verify). Mono for now. (No .ogg: s&box
  decodes ogg/opus but exposes no encoder.)
  **Dedicated MUSIC wall board:** `SettingsStation.Kind` is an enum (World/Host/Music);
  `SettingsWall` hangs a 3rd board (`WallSettingsPanel.Kind=Music` summary, short `tag:n`).
  Engaging it opens `MusicScreen.razor` (its own ScreenPanel on the UI GO, gated on
  `SettingsStation.Active.Music`, **near-full-screen** 96%×94% to fit the vibe grid) —
  shows the full `vibe:tag:n` seed, Prev/Next song (`StepN`), a seed field (`PlaySeed` /
  "Use mine" = `UseOwn`), copy-seed, **MUSIC on/off + VOLUME** (`SettingsModel.BuildMusicRows`,
  moved here off the world board), **Save song to s&box folder** (`SaveCurrentToFile`
  writes the raw loop, no fade, to `<tag>_<n>.wav`), and a **VIBE grid** that edits the
  `VibeCodec.Fields` knobs live — segmented tick cells / labeled choice cells (s&box has no
  slider widget; `MusicController.SetVibe(i, 0..1)` re-encodes the vibe immediately but
  restarts on a **0.35s debounce** so a drag across ticks isn't a generation storm). The
  panel must NOT be `overflow: scroll` — s&box drag-scrolls a scrollable panel, which fought
  the slider clicks; it's sized to fit the near-full-screen panel instead. Generation is
  offloaded via `GameTask.RunInThreadAsync`; the editor's "task running without yielding
  >1000ms" line is the worker-thread yield advisory (per hotloading.md), not a main-thread
  freeze.
  All generator knobs are `[Property]`/`[Range]`/`[Group]` (Music /
  Output / Tempo / Mix / Tone / Feel / Stereo / Instrument / Horns); `LiveReload` restarts
  the sequence after a 0.5s settle on a knob change in play mode.
  `SoundStream` API assumed: `new SoundStream(int sampleRate)`, `WriteData(short[])`,
  `Play()` → `SoundHandle` (`.SpacialBlend`/`.Volume`/`.Position`/`.OcclusionEnabled`) —
  not in `../sbox-docs`; verify in editor.
- Lobby text chat: built on the s&box platform chat (`Sandbox.Platform.Chat`, `../sbox-docs/docs/networking/chat.md`) — host-routed, sanitized, rate-limited, Steam-filtered for free. `ChatShowUI` is off in Platform.config; `ChatPanel.razor` (on the LobbyOverlay ScreenPanel GO) renders the feed via `IChatEvent` and sends with `Chat.Say`. Opens with the rebindable `Chat` action (default T) while roaming only — never while engaged, so it can't collide with the game keys, menu TextEntries, or the Settings rebind scan. `LobbyPlayer` disables the PlayerController while `ChatPanel.IsOpen` so typing doesn't walk the avatar. Note: `IChatEvent` may fire twice on the host (pre-broadcast + delivery); ChatPanel dedupes identical back-to-back lines under 1s apart.
- Follow-admin music: a player can opt to play the lobby admin's music instead of
  their own (`PlayerData.FollowAdminMusic`; "Admin DJ: YES/NO" toggle left of the
  Prev/Next song buttons in `MusicScreen`). The admin client pushes its `CurrentSeed`
  (`vibe:tag:n`) via `[Rpc.Host] LobbyNetworkManager.RequestSetAdminMusic` → the
  host-authoritative `[Sync(FromHost)] AdminMusicSeed` replicates to everyone (late
  joiners get the last value for free). `MusicController.SyncWithAdmin` (per frame, acts
  only on change) pushes when admin and, for followers, applies the seed via an
  **in-memory override** (`_following`/`_followTag`/`_followVibe`/`_followN`) that beats
  PlayerData in `SeedTag`/`BuildConfig`/`EffectiveStartN` — so the player's own saved
  seed is untouched and restored on un-follow; the follower's n is not persisted while
  following. The admin can't follow itself (`LocalIsAdmin` guard); not sample-synced —
  same song selection/vibe, re-aligned on each admin change (incl. auto-advance). Prev/
  Next/seed are disabled while following.
- Skafinity library extraction: the music engine + playback now live in the standalone
  **`Libraries/Skafinity/`** s&box code library (`namespace Skafinity`: `MusicGen`,
  `VibeCodec`, `SkafinityPlayer`) — the single source of truth, also compiled to wasm in
  the separate `skafinity` web repo. The duplicated `Code/Audio/MusicGen.cs` +
  `VibeCodec.cs` are **deleted**; `MusicController` (still `Rotaliate.Audio`, on the Room
  GO) is now thin game-glue that runtime-creates a `SkafinityPlayer` on its GO
  (`Components.GetOrCreate`, `AutoPlay=false`, `PersistProgress=false`, `MixerName="Music"`)
  and drives it: `Instance` singleton, PlayerData-backed seed/persistence, follow-admin,
  volume/enable. All generator tuning + the playback/crossfade/look-ahead loop are the
  library's; **no inspector knobs on `MusicController` anymore** (skafinity defaults are
  the canonical tuning — the old `DrumPush` knob is gone, superseded). `VibeCodec` is now
  **genre-aware**: `VibeCodec.Fields(genre)` (Ska/Rock/Country/Metal; genre rides in the
  vibe's first base-36 char), **16 levels/knob**, each field carrying `Voice`/`Column`
  (0 vol / 1 tone / 2 character / 3 extra). `MusicScreen.razor` builds its genre picker +
  per-instrument mixer matrix + GLOBAL strip entirely from that metadata (16 tick cells
  per knob), mirroring the web client's generic layout — no hardcoded field table. Open
  the editor once so it references the local library (regenerates csproj).
- Achievements (10) + stats. Two helpers in `Code/Game/`: `Achievements.cs` (deduped
  wrapper over `Sandbox.Services.Achievements.Unlock`, **manual idents only**) and
  `PlayerStats.cs` (wrapper over `Sandbox.Services.Stats.Increment`). Most achievements
  are **stat-based** — configured in the s&box dashboard (Aggregation: Sum, Max=threshold)
  against a stat ident and auto-unlock, so code only emits `Stats.Increment`; no backend.
  **Manual (3):** `comfy`/`dj`/`discordmod` — `LobbyPlayer.EngageBoard` per
  `SettingsStation.Kind` (World/Music/Host). **Stat-based (7):** `showedup`←`matches`≥1,
  `extracredit`←`solves`≥1, `goingsteady`←`daily_solves`≥5, `dedicated`←`hourly_solves`≥3,
  `adventurer`←`deaths`≥1, `goldnova`←`mp_matches`≥1, `globalelite`←`mp_wins`≥1.
  **Stat sites:** `sp_matches` on `GameController.StartGame`; `matches` (= 2×2 groups
  cleared, `resolvedCells/4`) and `solves` (+ distinct `daily_solves`/`hourly_solves`
  gated by `PlayerData.MarkBoardCounted`, a capped seed cache keyed `mode:seed`) in
  `FinishAnim`; `mp_matches` on MP `game_start`, `mp_wins` on MP `game_over` win; `deaths`
  in `LobbyPlayer.Respawn` (fall below `FallKillZ`=−150, floor top Z=0, teleport to spawn).
  Note: `showedup`/`extracredit` fire on **any SP** play/finish (incl. freeplay), and
  `goldnova` on match **begin** — the stats aren't mode-scoped; only goingsteady/dedicated
  keep daily/hourly distinctness. Stats are client-reported (spoofable) — fine for
  cosmetic achievements; Stats API batches sends, so per-group `matches` is cheap.
- Controller bindings + rebinding (gamepad remap): the 6 remappable game actions
  (MoveUp/Down/Left/Right, RotateCCW/CW) carry **no GamepadCode** in `Input.config`.
  s&box exposes no public per-button gamepad read (`InputRouter.IsButtonDown` is
  `internal`), so each offered physical button gets its own gamepad-only **probe
  action** (`PadUp`/`PadDown`/`PadLeft`/`PadRight`/`PadA`/`PadB`/`PadX`/`PadY`/`PadLB`/
  `PadRB`, group "Gamepad"). `Code/Game/GamepadBinds.cs` maps game action → probe
  (`PlayerData.GamepadBindings` override, else the default map) and `Pressed(action)`
  reads `Input.Pressed(probe)`. Capturing a rebind = scanning the probe actions with
  `Input.Pressed` (the one that fires is the pressed button). Read sites OR the probe
  in: `GameController.IsActionPressed`, `MultiplayerController.HandleInput`, and the
  `ModePickerScreen` menu nav. The Settings KEYBINDINGS UI now has two columns
  (Keyboard | Controller), both rebindable (`_rebindingController` flag picks which
  listener runs). Non-remappable pad buttons keep direct codes: `use`=X (engage),
  `Back`=SwitchLeftMenu (leave; Start/SwitchRightMenu is engine auto-escape),
  `Jump`=A (menu confirm). Dropped the alt rotate keys (`,`/`.`): rotate is Z/X only.
- Skafinity full drop-in (`feat/skafinity-lib-swap`): replaced the old in-repo
  `Libraries/Skafinity/` extraction with the **`gamah.skafinity` library, installed via the
  editor's Library Manager and committed as source** under `rotaliate/Libraries/gamah.skafinity/`
  (s&box pattern, `sbox-docs/docs/code/libraries.md`: libraries are source, not compiled
  assemblies; you download into `<project>/Libraries/` and commit them; they are
  **auto-referenced just by living there — do NOT add a `PackageReferences` entry**, that
  double-registers the compiler → `Compiler named gamah.skafinity already exists`). The
  committed folder must be **clean source only** — strip any `.bin/` (compiled package), the
  nested `Libraries/gamah.skafinity/Libraries/…` duplicate, and `ProjectSettings/`/`.version`
  that the publish bundle carries; keep `Code/`, `Skafinity.sbproj`, `README.md`,
  `skafinity.config.json`. To update: pull the new version in the Library Manager and re-commit
  the folder. Canonical dev source lives in `../skafinity/sbox-library/Skafinity/`. The library
  ships its own drop-in UI panel, `Skafinity.SkafinityMusicPanel`. Deleted the client's
  game-glue layer: `MusicController`
  and the custom `MusicScreen.razor` are **gone**. The scene now carries a bare
  `Skafinity.SkafinityPlayer` on the Room GO (`MixerName="Music"`, `AutoPlay`,
  `PersistProgress` — the library handles its own seed/progress persistence via `SaveSlot`,
  no PlayerData) and a `SkafinityMusicPanel` on the `UI` ScreenPanel (auto-finds the
  player). **Admin DJ / follow-admin is removed** (dropped
  `LobbyNetworkManager.AdminMusicSeed` + `RequestSetAdminMusic`); PlayerData lost all
  `Music*`/`FollowAdminMusic` fields (the library persists seed/progress itself).
- Skafinity panel via the engage flow (camera fix): the library panel's floating ♪ button is
  an always-on interactive element — over the roaming lobby it keeps the cursor released and
  **kills third-person mouselook**. So the panel is NOT used free-floating. The **south-wall
  music board is kept** (`SettingsStation.StationKind.Music`, `SettingsWall` MusicBoard,
  `WallSettingsPanel` Music case — themed to echo the panel; reads now-playing from the scene
  `SkafinityPlayer`, no PlayerData; `dj` achievement still awarded on engage). The
  `SkafinityMusicPanel` starts **disabled** in the scene; `Code/UI/MusicBoardScreen.cs` (on the
  UI ScreenPanel GO, always enabled) enables + force-opens it only while engaged at the music
  board (`SettingsStation.Active.Music`) — where the cursor is already free — so roaming has no
  interactive panel and mouselook works. `SettingsScreen` excludes the music board
  (`!Active.Music`). `SettingsModel.BuildMusicRows` stays gone (mute/volume live on the panel).
- Info/dev-notes engage flow + Discord click-to-copy: the east-wall info and dev-notes
  WorldPanels (`CenterInfoPanel`/`DevNotesPanel`, still display-only) each get an
  `InfoStation` (`Code/World/InfoStation.cs`, `Kind` = Info/DevNotes) at their foot —
  "Press E to view" opens the interactive `Code/UI/Screens/InfoScreen.razor` ScreenPanel
  (on the UI GO, gated on `InfoStation.Active`, screen-space `.screen-fit` centering, same
  engage pattern as `LeaderboardStation`/`LeaderboardScreen`: camera stays put, cursor
  freed). The Discord link is now **click-to-copy** in `InfoScreen` (was the walk-up
  `DiscordButton` proximity-E). `DiscordButton` is now a **static helper** (`InviteCode`/
  `InviteUrl`/`Copy()`/`SinceCopied`), no longer a Component — `InfoWall` stops spawning it
  and `LobbyPlayer` lost `FindNearbyDiscordButton`/`DiscordButton.Nearby`, gaining
  `NearbyInfo`/`FindNearbyInfo`/`EngageInfo`. `DevNotesPanel` exposes `static Notes`/
  `HasNotes` so `InfoScreen` and the "read dev notes" prompt reuse the fetched notes (the
  dev-notes station is skipped when there are none).
