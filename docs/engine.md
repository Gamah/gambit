# Engine

s&box lore specific to Gambit. The shared rules (whitelist, `HttpAllowList`, forbidden headers,
stream reads, box sizing, panel layout, snapshot basics) are in `~/.claude/sbox.md` and are not
repeated here.

## Whitelist consequences

- The vendored chess library is patched for the whitelist (`Code/Chess/Vendor/`: regex, `Task`,
  `Span` and reflection stripped, every change marked `GAMBIT VENDOR PATCH`).
- `System.Security.Cryptography.SHA256*` is whitelisted (`engine/Sandbox.Access/Rules/BaseAccess.cs`),
  so `SHA256.HashData` works. `RandomNumberGenerator` is not, which is why `Pkce.New` uses
  `Random.Shared`.

## Patterns

- **Components** hold game logic; `OnUpdate()` for per-frame work.
- **UI** screens are Razor `PanelComponent`s on a `ScreenPanel` GameObject.
- **State**: `[Sync(SyncFlags.FromHost)]` for host-authoritative state, `[Rpc.Host]` requests and
  `[Rpc.Broadcast]` relays (`ChessStation` occupancy is the model).
- **Storage**: `FileSystem.Data.ReadAllText`/`WriteAllText` for JSON player data.
- **HTTP**: `Http.RequestAsync(url, "POST", content, headers)`. The headers dictionary is
  undocumented in `../sbox-docs` but works; a forbidden header name throws rather than being
  dropped.
- **Hotload**: procedural builders rebuild through `[EditorEvent.Hotload]` in
  `Editor/HotloadRebuild.cs`. Register every new builder there.

### Self-attaching UI

`GameHud`, `SpectatorScreen`, `VoiceScreen` + `VoicePanel`, `HudHints` and `BoardSettingsScreen`
attach themselves to the scene `ScreenPanel` at runtime (`LobbyPlayer.EnsureGameHud` /
`EnsureSpectatorScreen` / `EnsureVoiceScreen` / `EnsureBoardSettings`), so a new screen of that
kind needs no scene edit. The voice pair must self-attach: its mute and enabled state is
client-local (`VoicePrefs` cookies) and must stay off every snapshot. `InfoScreen`,
`SettingsScreen`, `ChatPanel` and `LobbyOverlay` are serialized in `lobby.scene`; adding one of
those means editing the scene.

## The host snapshot decides where a component may live

A joining client does not load the scene from disk. `SceneNetworkSystem.OnLoadSceneMsg` destroys
the client's scene and applies the host's snapshot; `GameObject.Serialize.ShouldSave` drops every
`NetworkMode.Never` object and rebuilds every `Snapshot` object from the host's live state. So for
anything authored in `lobby.scene`:

- `Snapshot` carries the host's runtime state (a panel's `Enabled`, an `IsOpen`) onto joiners.
- `Never` means the object never reaches the joiner at all.

A strictly client-local screen or audio object is built in code: self-attached to the scene
`ScreenPanel` (above), or spawned from a `GameObjectSystem` onto a runtime `NetworkMode.Never`
GameObject when it needs its own `ScreenPanel`. `LocalMusicSystem` does the latter for the
Skafinity player and panel. A child of a network-spawned object rides the host's snapshot,
transform and enabled state included, so client-local world objects sit at the scene root.

### Library stylesheets and runtime-loaded files

A joiner of an **editor-hosted** lobby gets library code through the compiled code archive but
loose files only from the host's networked-file table, which walks the game package's `Code/` and
`Assets/` alone. Library folders are never walked, so a library `.razor.scss` styles the host and
404s on the joiner.

Gambit leaves `SkafinityMusicPanel.razor.scss` in the library and accepts that cost: in an
editor-hosted lobby, including "Join via new instance", joiners see the music board unstyled.
That is expected there and is not a regression. A published package is unaffected; its manifest
includes every library's `Code/` path. A panel loads exactly one sheet, at its razor's
compile-time source path plus `.scss` (`PanelComponent.LoadStyleSheet`), so a second copy
elsewhere is a second file at one resolved path with nothing keeping the two equal.

A raw asset loaded at runtime (`Texture.Load(FileSystem.Mounted, …)`, e.g. `chess_glyphs.png`)
reaches joiners only if the `.sbproj` `Resources` field lists it (`textures/*.png`). The editor
generates the real `.sbproj`, so set that field in Project Settings on each dev machine.

`debug_network_files 1` on the host logs every file it offers joiners.

## World scale

- The player is about 72 units tall. `ChessRing.AddBox` is the local `box.vmdl` helper.
- **`+Y` is left.** s&box is X forward, Y left, Z up. Facing the east wall you look along +X, so
  your right is −Y.
- **A tilted object's edge is not half its size from its centre.** Derive the edge through the
  rotation: a plate of height `h` tilted by `t` spans `h·cos(t)` in Z and `h·sin(t)` sideways.
  `ChessRing.ClockPlaneOriginZ` is the worked example. Nothing on this host renders, so check
  where the edges land.
- **A runtime-built component runs on code defaults.** `ChessRing` and `SpectatorWall` are not in
  `lobby.scene`, so they are retuned by edit-and-hotload, not in the inspector. Before trusting a
  scene value, `grep -r "class Foo" client/Code/` to confirm the component still exists.
- `FacePlayer` yaw-billboards a GameObject toward the camera; fronts face +forward.
- There is no API to open a URL or the Steam overlay. Links are copy buttons
  (`DiscordButton.Copy()`, …) with the **full** URL printed beside them, character for character
  what the clipboard gets, because a link a player cannot open is one they have to type.

## UI

- A **board** is a display-only `WorldPanel` in the world; a **screen** is an interactive
  `ScreenPanel` shown while engaged at a station, clipped to the station rect through
  `ChessRing.ScreenFractionRect()` / `UiRectStyle()`.
- Chess glyphs and `⬜`/`⬛` paint as emoji, which is why the spectator board is real
  `ChessSetBuilder` meshes (`SpectatorBoard3D`, with its own raking `SpotLight`). The floor keeps
  its glyph atlas because that is a shader, not a panel.
- If some text on a panel renders and some doesn't, check string lengths first: a flex text div
  without `white-space: nowrap` and `flex-shrink: 0` wraps and clips to a sliver.
- An interactive screen is gated on being engaged at a station and frees the cursor there
  (`UseLookControls = false`, `UseInputControls = false`, restored on close); a free-floating one
  kills roaming mouselook.
- There is no API to add buttons to the built-in escape menu; Escape while engaged is read from
  `Input.EscapePressed`.
- If a board click doesn't land, a HUD panel is eating it: the `Select`/mouse1 action must reach
  the world past the `ScreenPanel`.

### WorldPanels

The house shape for a panel on a mesh plate is `root { width: 100%; height: 100%; pointer-events:
none; }` and nothing else, with one absolutely positioned `left/top: 0; width/height: 100%` child
doing the layout (`MarqueeNumberPanel`, `SpectatorSeatPanel`, `CenterInfoPanel`,
`StationScreenPanel`). The px space is `PanelSize` on the `WorldPanel` component, not the
stylesheet. That shape holds **one** string: `position: absolute` retargets to the nearest
positioned ancestor, so any box between `root` and the text changes what every centring rule
means. A second string is a second panel on a second mesh (the table clock is two plates and a
bar).

That is a house style, not an engine limit. terryball composes multi-element WorldPanels with flex
layout directly on `root`, nothing absolutely positioned, and `nowrap` + `flex-shrink: 0` on every
text div. Keep the one-string shape for anything on a mesh plate; a page-like world board uses the
flex-on-root shape rather than being carved into meshes.

- A world panel's root keeps **`Scale = 2`** (`Sandbox.UI.WorldPanel`'s constructor; its
  `UpdateScale` override is empty), so the CSS layout area is `PanelSize / 2`. A px value compared
  against a raw `PanelSize` is off by 2×.
- **`LocalScale` is not a world size.** World size is `PanelSize × 0.05 × scale`
  (`ScenePanelObject.ScreenToWorldScale`). Derive it: `wanted_world_size / (PanelSize × 0.05)`.
  `ChessRing.PxToWorld` and `SpectatorWall.PxToWorld` each hold that constant.
- **World-space controls go through `WorldInput`**, a component on the camera that feeds a ray
  into the UI system so panels with `pointer-events: auto` are clickable (the cursor ray when
  `Mouse.Active`, the GameObject's forward otherwise, within `WorldPanel.InteractionRange`).
  `LobbyPlayer` adds it; the tabletop settings plate uses it. Do not hand-roll a plane hit test.

## Reading a gamchess ping failure

`gambit_gamchess_ping`:

- **TLS/SSL error**: the request reached a handshake and Caddy has no cert for that host (the
  vhost is down or not configured).
- **Any HTTP status**: gamchess answered; read the status.
