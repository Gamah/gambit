# World

The room, the table ring and tabletop layout, wall boards, occupancy, lobby networking.

## The info boards are part of the change

Two places describe the game to a player: **`CenterInfoPanel.razor`** (the east-wall board, short
version) and **`InfoScreen.razor`**'s Welcome branch (walk up, press E; long version). Nothing
fails when they go stale, so any change to seating or turn order, time controls, the spectator
wall's sources, lichess, the archive, or anything a newcomer is told updates both in the same
change. State what the game does now; never promise what it will do.

## The room

- `LobbyRoom` self-provisions: it adds `ChessRing` when the scene lacks one, and
  `EnsureSpectatorWall` builds the **north-wall** spectator board. Neither is in `lobby.scene`, so
  both run on code defaults and are retuned by edit-and-hotload, not in the inspector.
- `FloorCheckerboard` bakes a `PopMap` (checker colour) and a `GlyphMap` (R = glyph index 0–6,
  one texel per cell). `floor_checker.shader` looks the piece up in
  `Assets/textures/chess_glyphs.png` and draws it in the colour **opposite** its square. Pops land
  on both square colours, round-robin over the six piece types. If the atlas fails to load, no
  indices are written and the floor is a plain checker. `scripts/gen_glyph_atlas.py` regenerates
  the atlas.

## The table ring

`ChessRing` builds each table (`BuildChessTable`: table, board frame, 64 cells, two capture
trays, pieces at the start position, two camera anchors per station) and network-spawns the
stations. It owns the screen-rect UI math (`ScreenFractionRect()` / `UiRectStyle()`).

`ChessSetBuilder.BuildPiece` first tries `Model.Load("models/chess/{type}.vmdl")` and falls back to
a lathed procedural mesh, so a real piece set is a drop-in.

### The tabletop margin is allocated

`TopSizeX`/`TopSizeY` (40 × 44) minus the 29-wide board frame leaves 7.5 per side, and every
margin has a job:

- **−Y**: the clock strip, then White's tray.
- **+Y**: Black's tray, with the number plaque hanging below its edge.
- **±X**: kept clear. −X is where White's seat camera looks down the board from, +X the same for
  Black; anything mid-edge there stands in a player's foreground. The one tenant is the seated
  player's BOARD SETTINGS plate, flat on the tabletop in their near-left corner, client-local so
  there is only one (`docs/seat.md`).

The Y budget is **derived**: `ClockBoardGap`, `ClockDepth`, `ClockTrayGap` and `TrayEdgeGap` are
typed; `TrayInnerY`, `TrayCenterY`, `TrayWidth`, `TraySlotPitchY` and `ClockCenterY` follow from
them. Change one and the rest move. Check which margin anything new on the tabletop lands in.

### The clock

A live clock is on the **table**, not the HUD: a low strip in the **−Y** margin carrying two mesh
plates and a mesh material bar (`ChessRing.BuildStationClock`, `World/TableClock.cs`,
`UI/TableClockTextPanel.razor`). White's plate is at −X, Black's at +X. All three share one
upward tilt across the board: neither player is square to it and both look down at it, so one
facing serves both seats.

- **Plate height and tilt trade off and neither is tunable alone.** Tilted up out of the strip, a
  plate's height projects `sin(tilt)` of itself back into Y; tall and steep leans over the board
  and clips the a-file. At `ClockPlateHeight` 2.9 and `ClockFaceTilt` 30° that is ±0.725 inside
  the 1.6-deep strip's ±0.8.
- `ClockPxHeight` derives from the text span's aspect, so the panel and its mesh cannot drift out
  of proportion.
- `ClockPlaneOriginZ` is body top + `h/2·cos(tilt)`: a tilted plate stands on the body rather than
  half inside it, and everything in the plane shares it.
- **`TimeControl.PanicSeconds`** is where a clock reddens, shared with the panic beep so the two
  cannot disagree.
- Clock faces hash their **rendered text** (`BuildHash` on `Text`, `State`), so a panel repaints
  when a digit changes. Hashing the raw float would repaint every live table every frame.
- The HUD has no clock and no panic red.

### Capture trays

Each player's captured pieces sit in a tray on their own right (White faces +X, so White's right
is −Y). `ChessRing.TraySlotLocalPosition` owns the geometry, `ChessBoardView` the ordering, and
**`Code/Chess/CapturedMaterial.cs` the contents, derived from the FEN alone**, never from a tally
of captures: `ChessBoardView` rebuilds from the FEN and has no history, so an event-counted tray
would be empty for every late joiner and every resync. The capture animation is a transient
overlay; the tray adopts the dying piece's GameObject when the diff has one and spawns it in place
when it doesn't. **Tray geometry must never be named `Cell …`**: `ChessBoardView.ResolveCells`
prefix-scans the table's children for exactly that.

## Occupancy

`ChessStation` holds two-seat occupancy: `[Sync(FromHost)] WhiteSteamId`/`BlackSteamId` (+
`WhiteName`/`BlackName`, `WhiteBot`/`BlackBot`), claimed through `[Rpc.Host] RequestEnter(seat)`.
First claim wins, one player cannot hold both sides, and a client that loses the race stands back
up in `OnUpdate`. Two players pressing E on one seat within about an RTT is resolved by the host.

- You take the side you walk up to. Seat cameras orbit the board centre (`SeatOrbitRadius`,
  `SeatPitch`, `SeatLookDownAngle`).
- **Standing up from a live game does not resign it.** `LeaveCameraKeepSeat` drops the camera and
  keeps the claim, so the game keeps running; walk back and sit to resume. Anything that is not a
  live game is a full leave. Resign is its own two-press button on the seated HUD.

## Wall boards

**Every wall board goes through `WallBoardGeometry`**: the size (`BoardScale`), the aspect
(`Stretch`) and the shared floor anchor (`FloorAnchor`, called per frame from each board's own
`OnUpdate`). Boards match because they share the default `PanelSize` (one intrinsic pixel space,
so px font values copy between them), lay out `height:auto`, and anchor their content's bottom
edge. A board that hand-rolls its own scale cannot be fixed from the seam. Floor clearance is a
per-board `[Property]` the wall passes in.

`SpectatorSeatPanel` and `SpectatorFanfarePanel` bypass the seam: a plaque and a banner are not
wall boards.

**Adding a board to a wall means adding its position fraction to `lobby.scene`** (`InfoWall`'s
`*YFrac`, `SettingsWall`'s `*XFrac`). Both walls are serialized, so a new `[Property]` takes the
code default while existing ones take the scene's. `+Y` is left: facing the east wall, a higher
`YFrac` sits further left.

### The MORE board

Every outbound link is on the MORE board on the south wall (`SettingsWall.MakeMoreBoard`,
`MoreBoardPanel`, `InfoScreen`'s `StationKind.More` page): the Discord invite, our other games,
and music by Skafinity (`https://github.com/gamah/skafinity`). It carries an `InfoStation`, not a
`SettingsStation`, so E, the freed cursor and Escape come for free. Each link has its own
`SinceCopied` clock (`DiscordButton`, `GamesButton`, `SkafinityButton`) so a copy confirms the
right link. There is no API to open a URL, so the full URL is printed beside every copy button
(`docs/engine.md`).

## Lobby networking

- `LobbyNetworkManager` hosts through `ISceneStartup.OnHostInitialize` → `Networking.CreateLobby`.
  Players spawn by cloning the disabled in-scene `PlayerTemplate` GameObject and
  `NetworkSpawn(connection)`.
- **The host's own avatar spawn is deferred.** `OnActive` fires for the host during
  `CreateLobby`, before its connection settles, and a spawn there never reaches the snapshot later
  joiners get. `OnActive` detects `connection == Connection.Local` and defers the clone and spawn
  to the first `OnUpdate`; joiners spawn inline.
- Stations are host-built and network-spawned so `[Sync]` occupancy replicates; everything
  cosmetic is local `NotSaved`/`NotNetworked`, rebuilt per client.
- Moves relay through `NetChessMove(uci, fenAfter)` (`[Rpc.Broadcast]`, client to all), and the
  host folds the latest FEN into `[Sync] BoardFen` for late joiners. The spectator wall reads that
  same FEN.
- Sitting plants the avatar at its side of the board facing it (`LobbyPlayer.BeginEngage` →
  `ChessStation.SeatWorldPosition`); standing restores the pre-sit transform so the camera
  hand-back doesn't snap.
- Same-machine test instances share `FileSystem.Data`, and so one identity. Test through the
  network status icon → "Join via new instance".

Where a component may live given the host snapshot is in `docs/engine.md`; read it before adding
anything to `lobby.scene`.
