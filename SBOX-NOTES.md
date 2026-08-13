# SBOX-NOTES.md — engine lore this repo paid for

The generic s&box rules live in `~/.claude/sbox.md` (whitelist, `box.vmdl` is not 1×1×1, no
`BoxCollider` on a non-uniformly scaled GO, WorldPanel scale is a multiplier on intrinsic pixels,
`pointer-events` not inheriting, panels as flex containers, `overflow: scroll` vs drag, the
Body-child renderer). What follows is what **Gambit** paid for on top.

**The whitelist** is in `~/.claude/sbox.md`. Gambit-specific consequences: it is why the vendored
chess library needed patching (`Code/Chess/Vendor/`, regex/Task/Span/reflection stripped). It is
**not** why anything is hand-rolled crypto — `engine/Sandbox.Access/Rules/BaseAccess.cs:362`
whitelists `System.Security.Cryptography.SHA256*` outright (read 2026-08-05), so `SHA256.HashData`
just works. A hand-rolled SHA-256 was written for HTTPFIX's PKCE and then **deleted**; an older
claim in this repo that "SHA-256 is hand-rolled here" had no implementation behind it for the whole
of the repo's life. **`RandomNumberGenerator` is genuinely absent** from that list (only
`HashAlgorithm*`/`MD5*`/`SHA1*`/`SHA256*`/`SHA512*` are there), which is why `Pkce.New` uses
`Random.Shared` and says so.

## Patterns to follow

- **Components**: game logic in `Component` subclasses; `OnUpdate()` for per-frame work.
- **UI**: screens are Razor `PanelComponent`s on a `ScreenPanel` GameObject.
- **State**: `[Sync]` for peer-networked state (host-authoritative with `SyncFlags.FromHost`);
  `[Rpc.Host]` request / `[Rpc.Broadcast]` relay (see `ChessStation` occupancy).
- **Storage**: `FileSystem.Data.ReadAllText/WriteAllText` for JSON player data.
- **HTTP**: `await Http.RequestStringAsync(url)`; `await Http.RequestAsync(url, "POST", content,
  headers)` — the trailing headers dictionary is undocumented in `../sbox-docs` but works,
  **except that a forbidden header name THROWS rather than being dropped** (`User-Agent`,
  `Referer`, `Origin`, `Host`, `Sec-*`, `Proxy-*`, …). This broke the lichess link flow; see
  `LICHESS.md`.
- **Hotload**: C# changes hotload in milliseconds. Procedural builders rebuild via
  `[EditorEvent.Hotload]` in `Editor/HotloadRebuild.cs` — keep new builders registered there.
- **Self-attaching UI**: **GameHud, SpectatorScreen, VoiceScreen + VoicePanel, `HudHints` and
  `BoardSettingsScreen`** — those and no others — attach themselves to the scene ScreenPanel at
  runtime (`LobbyPlayer` walks `Scene.GetAllComponents<ScreenPanel>()` in `EnsureGameHud` /
  `EnsureSpectatorScreen` / `EnsureVoiceScreen` / `EnsureBoardSettings`), so a new screen of that
  kind needs no scene rewire. The voice pair MUST self-attach for a specific reason, not tidiness:
  it is strictly client-local (mute/enabled live in `VoicePrefs` cookies), so hanging it off the
  ScreenPanel keeps it off every networked snapshot. **InfoScreen, SettingsScreen, ChatPanel and
  LobbyOverlay are NOT self-attaching** — they are serialized components in `lobby.scene` and
  adding one means editing the scene. *(This line cited `SplashScreen` as an exemplar for a long
  time; there is no `SplashScreen` — no `.cs`, no `.razor`, only an orphan scene entry.)*

## The network snapshot is a real fork in behaviour (issue #12)

**A joining client does NOT load the scene from disk — it rebuilds it from the host's snapshot.**
Verified in the engine: `SceneNetworkSystem.OnLoadSceneMsg` **destroys** the client's scene and
applies the host's snapshot; `GameObject.Serialize.ShouldSave` **drops every `NetworkMode.Never`
object** from that snapshot and **rebuilds every `Snapshot` object from the host's LIVE state**.

So for anything authored in `lobby.scene`, neither mode is client-local:

- `Snapshot` leaks the host's runtime state onto joiners — exactly how the music board came to
  render *open and unstyled*, the panel's live `Enabled`/`IsOpen` riding the wire.
- `Never` means the object **never reaches the joiner at all** — the seductive-looking "minimal
  fix" that cannot work.

**The only way to get a strictly-client-local screen/audio object is to BUILD it in code**: either
self-attach to the scene ScreenPanel (above), or — when it needs its own isolated ScreenPanel —
spawn it from a **`GameObjectSystem`** onto a runtime `NetworkMode.Never` GO. `LocalMusicSystem`
does the latter for the Skafinity trio, mirroring terryball's `LocalHudSystem`; a
`GameObjectSystem` is instantiated locally on every machine independent of the snapshot.

**A library's `.razor.scss` NEVER reaches a joining client** — issue #12's second half. A joiner of
an editor-hosted lobby mounts no package: it gets code via the compiled CodeArchive (so a library
*panel class* always arrives) and loose files only from the host's networked-file table, which is
built by walking **the game package's `Code/` + `Assets/` alone** (plus `.sbproj` `Resources`
globs, which also only filter the game filesystem). Library folders are never walked, so a
stylesheet inside one styles the host and 404s on every joiner.

**Gambit used to patch around this** by keeping `SkafinityMusicPanel.razor.scss` at
`client/Code/UI/` instead of in the library. **That patch is reverted (2026-08-13) and must not be
reinstated without reading what it cost.** The library owns its own stylesheet again, exactly as
skafinity's own project and terryball have it.

> **Why: the patch traded a rare failure for a silent, recurring one.** It created two files at one
> resolved path, and there is no mechanism keeping them equal — so every library update left the
> game copy describing the *previous* version of the board. That does not present as a stale file.
> The sheet loads, parses and applies; it simply names classes the razor no longer emits, so the
> panel renders with **no layout at all** — full-width bleed, stock-blue transport buttons, labels
> scattered across the screen. Indistinguishable at a glance from the issue-#12 unstyled board, and
> reached from the opposite direction. It shipped that way once.
>
> **The accepted cost, stated plainly:** a joiner of an **editor-hosted** gambit lobby gets the
> music board unstyled, because the host's networked-file table still doesn't walk library folders.
> That is a *dev-testing* condition — including "Join via new instance" on one machine — so expect
> it there and don't diagnose it as a regression. **A published package is unaffected**: its
> manifest sweeps every library's `Code/` path, scss included.

**How a panel resolves its sheet, since the old note asserted this rather than reading it**
(`PanelComponent.LoadStyleSheet` → `StyleSheet.FromFile` → `UpdateFromFile`, read 2026-08-13): the
path is `ClassFileLocationAttribute.Path + ".scss"` — the razor's **compile-time source path** —
read from `ctx.FileMount`. The panel never searches; it asks for one exact path. Which mount wins
when two of them expose that same path was **never established**, and the duplicate is deleted
rather than ordered, so nothing depends on the answer any more. **Don't create a second copy of a
panel's stylesheet on the strength of a guess about that ordering.**

The host mounts library content into `FileSystem.Mounted` ONLY in the editor (`GameInstanceDll.cs`
gates it on `Application.IsEditor`), which is why the host always styled while joiners never could.
A *published* package was never affected — the publish manifest sweeps every library's Code path.
Same mechanism, other victim: a raw asset loaded at runtime (`chess_glyphs.png` via `Texture.Load`)
ships to joiners only if listed in the `.sbproj` `Resources` field — and the editor generates the
REAL `.sbproj`, so that field must be set in Project Settings on each dev machine. Diagnose either
with the host console: `debug_network_files 1` logs every file the host offers joiners.

## World scale

- **The scene lies too, and this repo has the scar.** `lobby.scene` carried **eight components
  from the rotaliate fork with no class anywhere in `client/Code/`** — every property inert, and
  two actively contradicting the code that really runs (`ArcadeRing`'s `BoardSize: 28` next to the
  real `ChessRing`'s **26**; `SpectatorBoard`'s `ClearAboveWall: 20` next to `SpectatorWall`'s
  **18**). Grepping the scene and believing it got the wrong number — the inverse of the usual
  rule. They are deleted; the habit is the point: **`grep -r "class Foo" client/Code/` before
  trusting a scene value.**
- **A runtime-built component runs on code defaults and cannot be retuned in-editor.**
  `SpectatorWall` is not in the scene at all (`LobbyRoom.EnsureSpectatorWall()` builds it), so the
  north wall is an edit-and-hotload loop, not a scene-tweak loop — unlike east and south.
- The player is ~72 units tall — the human-scale yardstick. `ChessRing.AddBox` is the local
  `box.vmdl` helper.
- **A tilted object's EDGE is not half its size from its centre — derive the edge through the
  rotation.** This has cost two rounds on two different objects. The table plaque dropped its
  centre by `h·cos(tilt)` and forgot the `h·sin(tilt)` the same tilt swings sideways, so its top
  edge was at the right height but tucked under the tabletop. The clock then centred its plates on
  the body's top *surface* — so a box centred on its origin buried half of every plate in the body,
  and buried the shorter material bar **entirely**. Both times the arithmetic looked obviously
  right on the page and the room disagreed. `ChessRing.ClockPlaneOriginZ` is the worked example:
  surface + `h/2·cos(tilt)`, derived once and shared by everything in the plane, which also keeps
  their bottom edges level for free. **Nothing on this host can render, so check where the EDGES
  land, not the centre.**
- **`+Y` is LEFT.** s&box is Source-handed (X forward, Y left, Z up). A player facing the east wall
  looks along +X, so their RIGHT is −Y and a higher `YFrac` sits further LEFT. A comment in
  `InfoWall` claimed the opposite for a long time and put a board on the wrong side.
- `FacePlayer` yaw-billboards a GO toward the camera; fronts face **+forward**.
- There is **no documented API to open a URL / Steam overlay** — show links as copyable text
  (`DiscordButton.Copy()` / `GamesButton.Copy()`). **Print the URL in full next to the button.** A
  link a player cannot open is one they have to type, and they can only type what they can read —
  so a shortened or ellipsised display string is a link nobody can follow. Board and clipboard must
  carry the same characters.

## UI gotchas

- **Board vs Screen vocabulary**: a *board* is a display-only WorldPanel in the world (takes no
  pointer input); a *screen* is an interactive ScreenPanel shown while engaged at a station,
  clipped to the station rect via `ChessRing.ScreenFractionRect()` / `UiRectStyle()`.
- **Panel-rendered chess glyphs do not paint.** U+265F renders as a purple emoji, and a WorldPanel
  glyph atlas wouldn't paint either — which is why the spectator board is real 3D
  `ChessSetBuilder` meshes (`SpectatorBoard3D`, with its own raking `SpotLight`). The floor keeps
  its atlas because that's a shader, not a panel. **Reach for meshes over panel art.**
- **`⬜`/`⬛` are emoji too.** Not just chess pieces — the geometric-shape block characters are the
  same trap. `GameHud` uses them safely at 13px; at 76px on a world panel they render as two big
  square blocks that shove the content off the face. Use letters.
- Engaged-screen centering must live on an absolutely-positioned full-screen child (`.screen-fit`
  wrapper), NOT on `root` — otherwise content pins top-left.
- **Every text div in a flex row needs `white-space: nowrap` AND `flex-shrink: 0`.** These two are
  one rule and it is the single most expensive line in the repo. A flex item's *default* is to
  shrink when the row is tight; a shrunk text div doesn't ellipsize, it **wraps**; and the clip then
  shows **a few visible pixels of the middle of your text**, which reads as a rendering bug anywhere
  but the stylesheet. `SpectatorSeatPanel` carries `.tag > div { flex-shrink: 0 }` plus `nowrap` on
  every text div; the old `TableClockPanel` omitted both and rendered its clock as **a single dot**
  while `gambit_clock` proved the value was `168.1s` the whole time. Short strings hide it. **If
  some text on a panel renders and some doesn't, check the string lengths first.**
- A free-floating interactive panel kills roaming mouselook — gate interactive screens on being
  engaged at a station and free the cursor there (`UseLookControls=false` + `UseInputControls=false`,
  restored on close).
- No documented API to add buttons to the built-in escape menu; Escape leaves the station via
  `Input.EscapePressed`.
- If a board click doesn't land, a HUD panel is eating it — the `Select`/mouse1 action must reach
  the world past the ScreenPanel.

### WorldPanels: the one-string shape, and what it really forbids

The house shape for a panel on a mesh plate is `root { width: 100%; height: 100%; pointer-events:
none; }` and *nothing else*, with an absolutely-positioned `left/top: 0; width/height: 100%` child
doing the layout. `MarqueeNumberPanel`, `SpectatorSeatPanel`, `CenterInfoPanel` and
`StationScreenPanel` are byte-for-byte this. Note `root` takes `100%`, not the panel's px size: the
px space is set by `PanelSize` on the `WorldPanel` component, not in the stylesheet.

**That shape holds ONE string. A second string is a second panel on a second mesh — build it in 3D,
not in CSS.** The table clock tried to draw two times and a material bar in one WorldPanel and cost
**five rounds, five bugs, every one of them layout and not one of them data**. The mechanism is
mechanical, not bad luck: ***`position: absolute` retargets to the nearest positioned ancestor*** —
so the moment a box sits between `root` and the text div, every centring rule silently means
something else. The working panels have no such box because they have nothing to compose.

So the clock is **the table-plaque pattern twice** (mesh plate + one-string panel) **plus a mesh
bar**: `ChessRing.BuildClockPlate`/`BuildClockBar` + `World/TableClock.cs` +
`UI/TableClockTextPanel.razor`. It buys correctness, not just tidiness: a mesh plate sits at
table-local `x = −7` for White, so **a wrong side is visible in the diff**. The panel had to reason
about a WorldPanel's content-space handedness for the same fact and got it backwards, rendering each
player their opponent's clock.

**Checked against the siblings and the engine (2026-07-29): the rule is narrower than "one
string".** terryball composes multi-element WorldPanels that work (`PinReadout.razor`,
`LaneConsole.razor`) by putting flex layout **directly on `root`** with **no absolutely positioned
anything**, plus `flex-shrink: 0` + `nowrap` on every text div. Ours is absolute-positioned, and
*that* is what cannot be composed. **Both are true and the choice is real**: ours survives a
WorldPanel whose pixel space is set from code and needs nothing centred by hand; terryball's costs
a layout pass this host cannot see. **Keep copying the one-string shape for anything on a mesh
plate** — but "a second string needs a second mesh" is a house style, not an engine limit, and a
genuinely page-like world board should use terryball's flex-on-root shape rather than being carved
into five meshes.

Two engine facts worth having while doing either (read from `sbox-public`, 2026-07-29):

- A world panel's root keeps **`Scale = 2`** (`Sandbox.UI.WorldPanel`'s ctor; its `UpdateScale`
  override is deliberately empty), so **the CSS layout area is `PanelSize / 2`**. Proportions are
  unaffected (px lengths cascade by the same Scale), but a px value compared against a raw
  `PanelSize` is off by 2×.
- **`WorldInput` exists**: a `Component` you hang on the camera that feeds a RAY into the UI system,
  so world panels with `pointer-events: auto` become clickable by looking at them (cursor ray when
  `Mouse.Active`, the GameObject's forward otherwise, honouring `WorldPanel.InteractionRange`,
  default 1000). P99 built two world-space buttons with hand-rolled plane arithmetic instead and
  then **deleted them both**. Issue #28's tabletop BOARD SETTINGS plate is the first thing here to
  actually use `WorldInput`. **Any future world-space control goes through it** — do not rebuild the
  plane test.

**A `WorldPanel`'s `LocalScale` is not a world size and cannot be eyeballed.** World size is
`PanelSize × 0.05 × scale` — the 0.05 is the engine's `ScenePanelObject.ScreenToWorldScale`, and the
default `PanelSize` is 512 square. The clock face was guessed at `0.022` and rendered **0.85 world
units on a 30-unit body**: an invisible speck that read as "the panel is broken". Derive it —
`wanted_world_size / (PanelSize × 0.05)`. `ChessRing.PxToWorld` and `SpectatorSeatPanel` each keep a
copy of that constant.
