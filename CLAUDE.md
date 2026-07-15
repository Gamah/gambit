# CLAUDE.md — Terry's Gambit s&box Client

**Terry's Gambit** (repo/ident: `gambit`, org `gamah`, namespace `Gambit.*`) — chess
in a social s&box lobby, backed by **gamchess**, our own Go/Postgres service. Forked
from rotaliate-client: the walk-around lobby, station ring, and networking scaffolding
are inherited; the arcade game and its Go backend were replaced by chess boards and
gamchess.

This file is the durable reference: how the game is built and the s&box lore that keeps
biting. **`PLAN.md` is only upcoming work and open issues** — read it for what's left,
not for how things work.

### Lichess: gone, and rebuilt from scratch when it returns

There is **no lichess in the tree today**. It was built against the lichess API through
M3–M5 and all of that was ripped out on `m7-gamchess-identity`: no API client, no OAuth,
no puzzles, no TV, no token, no allowlist entry. The codebase stands alone and **must keep
working with no lichess at all** — gamchess is the only backend Gambit depends on.

A lichess integration **is planned again, as a clean-slate rebuild**. Two rules for that
work, whenever it starts:

- **The old implementation is not the starting point.** It lives at the **`lichess-final`**
  tag for reference only. Do not restore those files; do not treat their design as decided.
  It was written around assumptions (an `Http` polling loop, a splash-screen OAuth flow, an
  anonymous display name) that no longer match how Gambit works — we now have Steam
  identity, gamchess, and s&box netcode carrying local play.
- **Re-derive the API facts.** The lichess API details that used to live in this file were
  cut deliberately. Anything about Board API rules, rate limits, or streaming must be
  re-read from lichess's current docs, not recalled from this repo's history or from
  memory. A stale constraint is worse than no constraint.

Until that rebuild starts, a stray lichess mention in code, comment, scene, asset, or doc
is residue — gut it.

Status: gamchess client + server built, **never compiled or deployed** — this host has no
s&box toolchain, no Go and no Docker. Expect a fixup pass on first open in the editor.

---

## Project Setup (first time on a new machine)

s&box's package manager tracks local projects in its own registry — cloning the repo
and opening the `.sbproj` directly will fail with `Unable to find package 'local.gambit#local'`.

**Correct flow:**
1. Open the s&box editor → **New Project** → Game (Empty), pointed at the repo's
   **`client/`** folder (not the repo root — `client/` is the s&box project root)
2. The editor writes its own `.sbproj` and registers the project; use that file, not the one in the repo
3. The editor hotloads C# automatically — check the error list for compile errors

The registry tracks projects **by path**, so the M7 `gambit/` → `client/` rename means
re-running this flow once even on a machine that already had the project.

**Migrating a machine that predates the rename** — do both, or you get a black screen:
1. **Delete the orphan `gambit/` folder.** `git mv` only moves *tracked* files, so
   checking out the rename leaves every gitignored artefact (`.sbox/`, `bin/`, `obj/`,
   `.addon/`, `*_c`/`*_d`, generated `csproj`/`slnx`) behind in the old path. What
   remains is a source-less husk holding a stale compiled assembly. Confirm
   `git ls-files gambit/` and `git status --porcelain gambit/` are both empty first,
   then `rm -rf gambit/`.
2. **Unregister the old `gambit/` project** in the editor before adding `client/`.
   Otherwise two registry entries both claim ident `gambit`, and the editor may open the
   husk — which builds the world (you'll see `ChessSetBuilder` run) but renders nothing.

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
server/                ← the gamchess Go/Postgres backend (M7, issue #7)
```

Each half ignores its own build output (`client/.gitignore`, `server/.gitignore`); the
root `.gitignore` holds only repo-wide junk. Unanchored `bin/`/`obj/`/`*_c` entries must
never go back in the root file — they match at any depth and would swallow `server/bin/`.

**Paths in csproj/slnx** assume Steam at `D:\Steam\`; the editor regenerates them.

This dev host has **no s&box toolchain** — no *engine* code compiles or runs locally.
Verify by careful review + grep; the user tests in their editor. `node
scripts/chess_js_perft.mjs` DOES run here — it is the gate on the web viewer's chess rules.

**Sandbox-free C# is genuinely testable here**, and worth reaching for: `dotnet` (10.x) is
installed, and everything under `Code/Chess/` except `PerftCommand.cs` — plus
`Code/Game/TimeControl.cs` — has no engine dependency. A scratch csproj that `<Compile
Include>`s those files runs real games, real PGN, real perft. Two settings matter:
`<TargetFramework>net10.0` (net8 builds but won't launch — only the 10.x runtime is here)
and `<ImplicitUsings>enable`, because the vendored library leans on s&box's global usings
for `System.Collections.Generic`. Verified 2026-07-15. This is also how the vendored rules
were proven originally, and how a `[TimeControl]`-bearing PGN was checked against the real
writer — prefer it over review whenever the code in question can be isolated from Sandbox.

---

## Architecture map (what exists and why)

### The world
- `LobbyRoom` self-provisions the world: it adds `ChessRing` to its GO if the scene
  lacks one, and `EnsureSpectatorWall` builds the west-wall spectator board. Both
  are self-healing, so **no scene rewire is needed** for these components.
- `ChessRing` builds the ring of tables (`BuildChessTable`: table, board frame, 64
  cells, pieces at the start position, two camera anchors per station) and
  network-spawns the stations. It also owns the screen-rect UI math
  (`ScreenFractionRect()` / `UiRectStyle()`).
- `ChessSetBuilder` lathes each piece as a runtime mesh. `BuildPiece(type, color, scale)`
  first tries `Model.Load("models/chess/{type}.vmdl")` and falls back to procedural —
  so dropping in a real piece set later is a one-function swap (**D5**).
- `ChessStation` holds two-seat occupancy: `[Sync(FromHost)] WhiteSeatSteamId` /
  `BlackSeatSteamId` (+ Steam names), claimed via `[Rpc.Host] RequestEnter(seat)`
  first-wins with loser-side reconciliation (**D1**). Seat cameras orbit the board
  center (`SeatOrbitRadius`/`SeatPitch`/`SeatLookDownAngle`). You take the side you
  walk up to; leaving a live game is a two-stage resign (Escape/Leave twice).
- `FloorCheckerboard` bakes a `PopMap` (checker colour) plus a `GlyphMap` (R = glyph
  index 0–6, one texel per cell). `floor_checker.shader` looks the piece up in
  `Assets/textures/chess_glyphs.png` and blends it over the square in the **opposite**
  colour (**D6**). Pops land on both square colours, round-robin over the 6 types. If
  the atlas fails to mount, no glyph indices are written → plain checker floor, never
  solid-square artefacts. Atlas is regenerated by `scripts/gen_glyph_atlas.py`
  (our own DejaVu Sans raster — CC0-clean, provenance in `Assets/ATTRIBUTION.md`).

### Chess rules (D2)
- Gera Chess Library (MIT, `d4f3f69`) is vendored+patched in `Code/Chess/Vendor/` —
  regex/Task/Span/reflection stripped for the whitelist, every change marked
  `GAMBIT VENDOR PATCH`. Verified on this host via a dotnet harness mirroring s&box
  compile settings: perft depths 1–4 on six reference positions, upstream's 67 xunit
  tests, 32 wrapper tests.
- **`Code/Chess/ChessGame.cs` is the only seam callers may touch.** It caches
  `Fen`/`LastMoveUci`/`MoveCount` between moves so per-frame polling is free.
  `TryFromPgnAtPly(pgn, ply)` / `TryFromPgn(pgn)` reconstruct a position from movetext
  (feeds puzzles and TV).
- `gambit_perft [depth]` re-proves the rules in-sandbox — run it before trusting a gate.

### Game controllers (per-station, added by ChessRing beside `ChessStation`)
`Game/IBoardGame.cs` is the render/drive abstraction; `ChessBoardView` renders the
active source with no per-source branching. There is only one source now
(`Source => Controller`), but the seam stays: gamchess-backed play slots in there
rather than rewriting the renderer.

| Controller | Networked? | What it does |
|---|---|---|
| `LocalGameController` | host-folded `[Sync] BoardFen`/`Phase`/`ClientGameId` | the two-seat game at a table, and the archive upload (**D7**) |
| `SpectatorController` | reads the host-folded FEN | west wall: mirrors a live table (116 lines; it was 740 before TV went) |

### Networking (D7)
- `LobbyNetworkManager` (`ISceneStartup.OnHostInitialize` → `Networking.CreateLobby`)
  hosts; joining peers never fire that event. Players spawn by cloning the disabled
  in-scene `PlayerTemplate` GO (no `.prefab` asset — hand-authoring the format is
  undocumented) and `NetworkSpawn(connection)`.
- **The host's own avatar spawn must be deferred.** `OnActive` fires for the host
  *during* `Networking.CreateLobby`, before its connection settles, so a spawn there
  never makes it into the snapshot sent to later joiners — joiners saw every client
  but the host. `OnActive` detects `connection == Connection.Local` and defers the
  clone+`NetworkSpawn` to the first `OnUpdate`; joiners still spawn inline.
- Stations are host-built and NetworkSpawned so `[Sync]` occupancy replicates;
  everything cosmetic is local `NotSaved`/`NotNetworked`, rebuilt per client.
- The move relay is `NetChessMove(uci, fenAfter)` (`[Rpc.Broadcast]`, client→all) with
  the host folding the latest FEN into `[Sync] BoardFen` for late joiners. The
  spectator wall and late joiners read that same folded FEN — no second relay.
- Sitting plants the avatar at its side of the board facing it
  (`LobbyPlayer.BeginEngage` → `ChessStation.SeatWorldPosition`); standing restores the
  pre-sit transform so the camera hand-back doesn't snap.
- Same-machine test instances share `FileSystem.Data` (one identity). Test via the
  network status icon → "Join via new instance".
- Small race window (~RTT) if two players press E on the same seat — host picks the
  winner; known limitation.

### Dev console commands
`gambit_perft [depth]` — re-prove the chess rules in-sandbox.
`gambit_gamchess_ping` — is gamchess up, and is the D8 allowlist right?
`gambit_gamchess_signin` — mint an FP token and prove the auth round-trip.
`gambit_gamchess_games` — list your archived games.

---

## gamchess (the backend)

`server/` in this repo. Go/Postgres, deployed at `chess.gamah.net`; the full API
contract is in the root **README** — it is hand-mirrored in C# with no codegen, so a
contract change is one commit across both halves.

- **Identity is only ever what Steam/Facepunch says it is.** In-game: the client mints
  a Facepunch auth token (`Sandbox.Services.Auth.GetToken`), gamchess verifies it at
  `POST https://public.facepunch.com/sbox/auth/token` and trusts **only the echoed
  `SteamId`**. On the web: Steam **OpenID 2.0** (`steamcommunity.com/openid/login` —
  Steam has no OAuth2 endpoint, whatever it gets called). Both **fail closed**. A
  SteamID from a header, body, or query string is an unverified *claim* and authorises
  nothing — which is why the archive has no `?steam_id=`.
- **The archive is private.** You only ever see games you sat in. Seat SteamIDs in a
  POST are claims, so you may only archive a game you sat in; `GET /games/{id}` 404s
  (not 403s) for someone else's game so ids aren't probeable.
- **`client_game_id`** is a UUID the host mints at game start and `[Sync]`s. Move
  history lives in each seated client's own `ChessGame`, not the host's, so the host
  usually has no PGN — **both seats POST** and the second is a no-op. A client whose
  history came from a FEN resync stays quiet rather than archive a stub.
- **SteamIDs cross the wire as strings.** A SteamID64 (~7.6e16) is past JavaScript's
  2^53, so a bare JSON number is silently corrupted by the web viewer.
- **gamchess is never required.** If it's down, the game plays exactly the same —
  `GamchessApi` has an 8s timeout, never throws, and a 60s circuit breaker so a dead
  host costs one timeout rather than one per call. Nothing may block scene load,
  `OnStart`, or a game ending.

### Asset licensing

All art must be **CC0**. Record provenance in `Assets/ATTRIBUTION.md` even for CC0.

Nothing is licensed in today: pieces are runtime meshes from `ChessSetBuilder`, floor
glyphs are our own DejaVu raster, sounds are synthesized by `scripts/gen_sounds.py`, and
the web viewer uses Unicode glyphs (zero image assets).

CC0 sources on file for the D5 3D upgrade: Poly Haven "Chess Set" by Riley Queen
(https://polyhaven.com/a/chess_set, glTF/FBX); portablejim 2D chess set on FreeSVG
(https://freesvg.org/portablejim-2d-chess-set-pieces); OpenGameArt /content/chess-pieces-0,
/content/3d-chess-pieces, /content/chess-set-1, /content/chess. Kenney has no chess pack.

### HTTP allowlist (D8)
`"HttpAllowList": ["https://chess.gamah.net/"]` in `gambit.sbproj` — the only entry.
Any new host needs adding here or every request fails.

Reading a `gambit_gamchess_ping` failure (verified in-editor 2026-07-15):
- **TLS/SSL error** → the request LEFT the sandbox and reached a handshake, so the
  allowlist is **fine**; Caddy has no cert for that host (vhost down/not configured).
- **blocked before connecting** → the allowlist is wrong.
- **any HTTP status** → we reached gamchess; read the status.

Whether the allowlist also gates `Sandbox.WebSocket` is an open spike.

### gamchess deployment facts

**Written, never compiled or deployed** (this host has no Go/Docker).
Ports/hosts are allocated in the server's Caddyfile (host-side, unversioned — not in
this repo):

| | Host | App | Postgres |
|---|---|---|---|
| prod | `chess.gamah.net` | 6464 | 5435 |
| test | `testchess.gamah.net` | 6465 | 5436 |

Both are plain subdomains (a `*.gamah.net` wildcard covers them; a sub-subdomain like
`test.chess.gamah.net` would have needed its own record — DNS wildcards match one label).

All bind `127.0.0.1` only — **never punch through ufw**. Docker's iptables chains are
evaluated *before* ufw, so a `0.0.0.0` publish is internet-reachable even with ufw denying
the port; loopback binding + Caddy fronting is the whole mechanism (rotaliate documents
this at `docker/docker-compose.yml`).

Ports already taken on that host by other services: `1337`, `5432`–`5436`, `6969`, `6970`,
`8080`, `8081`. gamchess's Postgres ports continue the org's increment convention from
that range. Check the host's Caddyfile before allocating anything new.

**Deploying needs only Docker** — every Go make target runs in a container
(`golang:1.22`, module cache in a named volume), because neither the deploy host nor the
dev machine has a Go toolchain. `make up` builds and migrates in-process at startup.
`make dev` is the one target that wants a local Go.

**Add no `log` directive to these vhosts.** Auth returns land on `/auth/steam/return` with
credentials in the query string, and Caddy would write them to disk. Caddy writes no access
log unless configured, so the default is already safe — the job is not to start. Any future
auth-callback route inherits this rule.

### Identity / auth primitives (in use — see `server/internal/steam/`)

Both halves are lifted from `../rotaliate`, essentially verbatim, along with their tests.
Deviating from them is how un-compilable mistakes get in.

- **In-game**: `await Sandbox.Services.Auth.GetToken( "gamchess" )` mints a Facepunch auth
  token; the service-name argument is **cosmetic** (Facepunch validates `{steamid, token}`
  without it). Returns null rather than throwing on non-Steam builds. Verified server-side at
  `POST https://public.facepunch.com/sbox/auth/token` → `{"SteamId", "Status"}` — **no
  persona name comes back, SteamID only**. Two rules: **fail closed** on any error, and
  **trust only the echoed `SteamId`** (`Status == "ok" && vr.SteamID == steamID`), which is
  what stops a valid token for account Y authorising as account X. Confirmed working
  in-editor 2026-07-15; the token's real TTL is still an open spike (we cache 120s and
  re-mint once on a 401).
- **Web**: Steam's browser login is **OpenID 2.0, not OAuth2** — there is no Steam OAuth2
  endpoint. `steamcommunity.com/openid/login`. Keeps rotaliate's `op_endpoint` pinning,
  `return_to` scheme+host+path matching, and single-use nonce (the nonce store is ours —
  `steam.Verify` only shape-checks it and documents that single-use is the caller's job).
- Sessions are stateless HMAC-signed cookies, so a deploy doesn't sign everyone out.
  `SESSION_SECRET` blank = random per-process key (works with no config, dies on restart).
  `SameSite=Lax` is load-bearing: the OpenID return is a top-level cross-site GET and
  Strict would drop the cookie on exactly that hop.
- Display names come from **Steam** (`Connection.DisplayName`) — Gambit has no username of
  its own and no name picking. The FP path returns no name, so a server-side name would need
  `ISteamUser/GetPlayerSummaries/v0002` (Steamworks key, 100k/day — cache it). Not needed:
  the PGN carries the names.
- The same FP token authenticates `Sandbox.WebSocket` — `Connect(uri, headers)` accepts an
  `Authorization` header (sbox-docs `networking/websockets.md`), so one mechanism covers
  both a future relay and ordinary HTTP.

## s&box Patterns to Follow

- **Components**: game logic lives in `Component` subclasses; `OnUpdate()` for per-frame work
- **UI**: screens are Razor `PanelComponent`s on a `ScreenPanel` GameObject in the scene
- **State**: `[Sync]` for peer-networked state (host-authoritative with `SyncFlags.FromHost`);
  `[Rpc.Host]` request / `[Rpc.Broadcast]` relay pattern (see ChessStation occupancy)
- **Storage**: `FileSystem.Data.ReadAllText/WriteAllText` for JSON player data
- **HTTP**: `await Http.RequestStringAsync(url)`; `await Http.RequestAsync(url, "POST", content, headers)` —
  the trailing headers dictionary is undocumented in `../sbox-docs` but works
- **Hotload**: C# changes hotload in milliseconds. Procedural builders rebuild via
  `[EditorEvent.Hotload]` in `Editor/HotloadRebuild.cs` — keep new builders registered there
- **Razor usings**: `System`, `Sandbox`, `Sandbox.UI`, `Sandbox.Rendering` are NOT
  auto-imported in `.razor` — add `@using` explicitly
- **Self-attaching UI**: GameHud, SplashScreen, and SpectatorScreen attach themselves to
  the scene ScreenPanel at runtime — no scene rewire needed for new screens; copy the pattern.

## s&box API Whitelist

s&box enforces an API whitelist — blocked calls produce `error SB1000`.
See `../sbox-docs/docs/code/code-basics/api-whitelist.md`.

| ❌ Blocked | ✅ Use instead |
|---|---|
| `Array.Clone()` | manual `for` loop copy |
| `Console.WriteLine` | `Log.Info` / `Log.Warning` / `Log.Error` |
| `System.IO.*` | `FileSystem.Data` |

Rule of thumb: avoid `System.Private.CoreLib` reflection/process/threading/IO. This is
why the vendored chess library needed patching and why SHA-256 is hand-rolled. When in
doubt check `https://sbox.game/api/` or file a false-positive at
`https://github.com/Facepunch/sbox-public/issues`.

## World Scale Rules (read before placing/sizing anything)

- **Never trust code defaults or docs for component property values** — the scene
  overrides them and gets retuned in-editor. `grep Assets/scenes/lobby.scene` for
  current values before sizing anything.
- The player is ~72 units tall — the human-scale yardstick.
- `models/dev/box.vmdl` is **NOT 1×1×1**: to make a box of size S,
  `LocalScale = S / Model.Bounds.Size` per axis — use/copy `ChessRing.AddBox`.
- Never put a `BoxCollider` on a non-uniformly scaled GO — it silently freezes physics.
  Colliders on uniformly-scaled parents, visuals on scaled children.
- A WorldPanel GO's scale is a multiplier on the panel's intrinsic pixel size, not world
  units; the panel plane is local **Y (width) / Z (height)**. World-size and text size
  are coupled — to grow a board without growing text, scale the GO up and divide
  stylesheet px by the same factor.
- `FacePlayer` yaw-billboards a GO toward the camera; fronts face **+forward**.
- There is **no documented API to open a URL / Steam overlay** — show links as copyable
  text — any future link-sharing has to be click-to-copy.
  Click-to-copy pattern: `DiscordButton.Copy()`.

## UI Gotchas (learned the hard way)

- **Board vs Screen vocabulary**: a *board* is a display-only WorldPanel in the world
  (takes no pointer input); a *screen* is an interactive ScreenPanel shown while engaged
  at a station, clipped to the station rect via `ChessRing.ScreenFractionRect()` /
  `UiRectStyle()` trig.
- **Panel-rendered chess glyphs do not paint.** U+265F renders as a purple emoji, and a
  WorldPanel glyph atlas wouldn't paint either — this is why the spectator board is real
  3D `ChessSetBuilder` meshes (`SpectatorBoard3D`, with its own raking `SpotLight` for
  shadows) rather than a panel. The floor keeps the atlas because that's a shader, not a
  panel. Reach for meshes over panel art.
- Engaged-screen centering must live on an absolutely-positioned full-screen child
  (`.screen-fit` wrapper), NOT on `root` — otherwise content pins top-left.
- `transform: scale` misplaces panel content — use explicitly sized wrappers.
- `pointer-events: all` must be set per interactive element; it does not inherit.
- Panels are flex containers: inline `<span>`s inside a text div become separate flex
  items; source newlines render as literal whitespace — keep each text div's content on
  one line. A div's auto height does not grow for wrapped text — use one div per line in
  a flex column.
- Deriving font sizes from `Panel.Box.Rect` on a WorldPanel doesn't work — use fixed px
  in intrinsic pixel space, calibrated against a known-good panel.
- Don't make a panel `overflow: scroll` if it has draggable controls — s&box drag-scrolls
  it and fights the clicks.
- A free-floating interactive panel kills roaming mouselook — gate interactive screens on
  being engaged at a station, and free the cursor there
  (`UseLookControls=false`+`UseInputControls=false`, restored on close).
- The citizen `SkinnedModelRenderer` must live on a `Body` child GO, never on the
  PlayerController's own GO (animator writes to it every frame — welds the player to
  world origin otherwise).
- No documented API to add buttons to the built-in escape menu; Escape leaves the station
  via `Input.EscapePressed`.
- If a board click doesn't land, a HUD panel is eating it — the `Select`/mouse1 action
  must reach the world past the ScreenPanel.

## Sounds

Synthesized WAVs in `Assets/sounds/sfx/` generated by `scripts/gen_sounds.py` (numpy).
`.sound` gotchas: `"Sounds"` lists `.vsnd` paths (not `.wav`), `"Volume"`/`"Pitch"` are
JSON strings, `"UI": true` for 2D playback, `"__version": 1`. Mapping: tick/tock →
clocks (by side), pop → captures, servo slides → station rebuild. Move sounds fire for
your own board (2D) and other players' boards (positional).

Music is the `gamah.skafinity` library — source-committed under
`client/Libraries/gamah.skafinity/` (s&box pattern: libraries are source and
auto-referenced by living there; do NOT add a `PackageReferences` entry — that
double-registers the compiler). The scene carries a `SkafinityPlayer` +
`SkafinityMusicPanel`; the panel is enabled only while engaged at the music wall board.
