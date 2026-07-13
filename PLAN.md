# Gambit — chess/lichess fork of rotaliate-client

## Context

Fork `/home/ai/rotaliate-client` (s&box lobby game, org `gamah`) into `/home/ai/gambit`: keep the social walk-around lobby with stations in a ring, but each station becomes a **chess board** instead of an arcade cabinet, and the custom Go backend is replaced by **lichess**. Logged-in players (lichess OAuth) play real lichess games against anyone on lichess from inside the game; anonymous players play locally at a shared board and get a shareable lichess link afterward via PGN import. The checkerboard floor stays, but its color pops become flat 2D chess-piece glyphs on both square colors. All art must be CC0.

**No feature implementation this session.** On approval, this session does exactly:
1. Copy `/home/ai/rotaliate-client` → `/home/ai/gambit` (excluding `.git/`, Rotaliate thumbnails).
2. Mechanical renames: `rotaliate.sbproj` → `gambit.sbproj` (`Title: Gambit`, `Ident: gambit`, keep `Org: gamah`), `rotaliate/` dir → `gambit/`, `.slnx`/`.csproj` renames, namespace `Rotaliate.*` → `Gambit.*` repo-wide (incl. `.razor` `@namespace` and log prefixes), lobby name → "Gambit v0.1 alpha", set `HttpAllowList: ["https://lichess.org/"]`, rewrite README/CLAUDE.md.
3. Write this plan (sections below) as `PLAN.md` at the repo root.
4. `git init`, commit (pristine copy first, then renames — no AI attribution in commits), create **private** GitHub repo `Gamah/gambit`, push.

Everything from "Milestones" onward is future work executed on later sessions; each milestone gate is tested by the user in the s&box editor (this host has no toolchain — verification is by code review + user testing).

## Research summary

### Lichess API facts

- **Auth**: OAuth2 Authorization Code + **PKCE only** (S256). No client registration or secret — `client_id` is an arbitrary string (use `gambit.gamah`). Desktop pattern: system browser → `http://localhost:{port}` loopback redirect → `POST /api/token`. Tokens live ~1 year, **no refresh tokens** (on 401/expiry: clear + re-auth); `DELETE /api/token` = logout; `POST /api/token/test` = validate. Personal `lip_` tokens (created at lichess.org/account/oauth/token) work as `Authorization: Bearer` — fine for dev, and **token-paste is the guaranteed-shippable fallback** if s&box's sandbox can't open a browser/bind a loopback listener.
- **Scopes**: `board:play`, `challenge:read`, `challenge:write` (+ `puzzle:read` for activity, optional).
- **Board API** (not Bot API) is the only sanctioned way for humans to play from a third-party client; anything else risks bans. **No engine assistance ever during lichess games** — not even an eval bar. Gambit ships no engine at all in v1, eliminating the risk by construction.
- **Time controls**: lobby seeks = Rapid/Classical/Correspondence only. Blitz only via direct challenges / vs AI / bulk pairing. **No Bullet.** Surface this in the UI.
- **Play loop**: `GET /api/stream/event` (account events: challenges, gameStart/gameFinish), `POST /api/board/seek` (**HTTP connection must stay open — closing cancels the seek**), `GET /api/board/game/stream/{id}` (`gameFull` snapshot then `gameState` deltas, ndjson), `POST /api/board/game/{id}/move/{uci}`; chat/abort/resign/draw/takeback endpoints exist. Moves are **UCI**; client tracks board state itself → embedded C# chess rules required.
- **Opponents**: a Board API player is a normal lichess player — seeks match anyone on lichess.org/mobile/any client. `POST /api/challenge/{username}` (direct, blitz OK), `POST /api/challenge/open` (works even unauthenticated; returns `url`/`urlWhite`/`urlBlack` — but those games are played on lichess.org, not through the API), `POST /api/challenge/ai` (Stockfish 1–8, requires auth).
- **Spectating/TV**: `GET /api/tv/channels`, `GET /api/tv/feed` + `GET /api/tv/{channel}/feed` (**no anti-cheat delay**). `GET /api/stream/game/{id}` for arbitrary games is delayed ~3 moves/3–60s — note in UI.
- **Puzzles**: `GET /api/puzzle/daily|next|{id}` to fetch (solutions included in JSON for local validation). **No endpoint to submit solutions** — solving in Gambit can't affect the user's lichess puzzle rating; say so in the UI. Full puzzle DB is downloadable (CC0) if offline puzzles are ever wanted.
- **Anonymous play (confirmed)**: **No API path exists for unauthenticated move-making** — all `/api/board/*` and `challenge/ai` require a token. Fallback (adopted): play locally in-client, then **`POST /api/import`** with the PGN (form field `pgn`) — **works unauthenticated, 100 games/hour anon (200 authed), returns a shareable lichess game URL** with analysis board. Anonymous users can also spectate TV/games freely.
- **Rate limits & streaming gotchas**: adaptive limits; one request at a time; on 429 back off a full 60s (build a single-flight request queue). ~8 concurrent streams/IP. ndjson streams emit a **blank keep-alive line every ~7s** — tolerate blanks, no idle timeout <10s, auto-reconnect and resync from `gameFull`. In .NET: `HttpCompletionOption.ResponseHeadersRead`, infinite timeout on streaming requests.

### CC0 assets (license-verified)

- **3D pieces (later upgrade)**: Poly Haven "Chess Set" by Riley Queen — https://polyhaven.com/a/chess_set (CC0, glTF/FBX, full board+pieces). Backups on OpenGameArt (all CC0): /content/chess-pieces-0, /content/3d-chess-pieces, /content/chess-set-1, /content/chess.
- **2D glyphs (floor pops, UI icons)**: portablejim 2D chess set on FreeSVG (CC0) — https://freesvg.org/portablejim-2d-chess-set-pieces; also OGA "Chess Pieces and Board squares" (CC0). **Do NOT use the Cburnett/Wikipedia/lichess piece set — it is CC-BY-SA/BSD, not CC0.**
- Kenney has no chess pack. Record asset provenance in `Assets/ATTRIBUTION.md` (even for CC0, for auditability).

## Architecture decisions

**D1 — Anonymous play: two players at one board (user choice).** A station gains two seats. `ChessStation` replaces single `OccupantSteamId` with `[Sync(FromHost)] WhiteSeatSteamId` / `BlackSeatSteamId` (+ names); `[Rpc.Host] RequestEnter(seat)` claims a seat first-request-wins; two camera anchors per station (one per board side, pitched ~35° down over the board). First sitter picks/gets White and sees "waiting for opponent"; a second player walks up and presses Use on the other side to take Black; game starts. Turn enforcement: only the seat whose turn it is has input; the mover's client validates the move with the embedded rules and sends `[Rpc.Broadcast] NetChessMove(uci, fenAfter)`; host folds latest FEN into `[Sync] BoardFen` for late joiners. **Lichess games use one seat only** (occupant + remote opponent); the second seat is disabled while a lichess game runs. After a local game: build PGN, `POST /api/import`, show the lichess URL + copy button to both players. No engine ships in v1 (also kills any fair-play risk); "vs AI" for logged-in users comes via lichess `challenge/ai` instead.

**D2 — Chess rules: vendor pure-C# chess sources behind our own wrapper.** s&box compiles game code from source with an API whitelist — NuGet DLLs can't be referenced. Primary: vendor Geras1mleo "Gera Chess Library" (MIT) into `Code/Chess/Vendor/` (keep MIT headers); expect light patching for whitelist friction (regex, events). Fallback (decide during M2): a compact ~700-line move-gen/validator (8x8 array, pseudo-legal + king-safety, castling/en-passant/promotion, FEN I/O, minimal SAN for PGN). Either way all callers use `Code/Chess/ChessGame.cs` (`ApplyUci`, `LegalMovesFrom`, `Fen`, `Pgn`, `Result`) so the vendor choice is swappable. Correctness gate: perft positions run as an in-game debug command on the user's machine.

**D3 — Auth: PKCE designed-in, token-paste guaranteed.** `Api/LichessAuth.cs` implements full PKCE. Two sandbox unknowns to spike on the user's machine at M3 start: can s&box open a system browser, and can game code bind a localhost listener for the redirect (likely no)? If loopback is impossible, the shipped flow is: show/copy the pre-filled token-creation URL `https://lichess.org/account/oauth/token/create?scopes[]=board:play&scopes[]=challenge:read&scopes[]=challenge:write&description=Gambit`, user pastes the `lip_` token into the splash screen. Token stored in `PlayerData` (FileSystem JSON) with the same hygiene as the old GUID: never networked, never logged unredacted. On 401: clear token, re-prompt.

**D4 — NDJSON streaming is the project's biggest risk — spike it first.** Board API play requires long-lived ndjson streams and a held-open seek request. Whether s&box's `Http` exposes incremental `ReadAsStreamAsync` (vs buffering the whole response) is undocumented. M3 begins with a ~50-line spike streaming `/api/tv/feed` (no auth needed) that the user runs before any dependent work. If streaming is impossible there is no Board API fallback (lichess has no polling API) — mitigation would be a Facepunch whitelist request; TV/puzzles/import (non-streaming-critical) are sequenced to still be shippable.

**D5 — Pieces: procedural primitives first, Poly Haven swap later (user choice).** `Code/World/ChessSetBuilder.cs` builds each piece type from tinted `models/dev/box.vmdl` boxes (pawn 2 boxes … king 4), matching the codebase's all-procedural aesthetic and the `AddBox` pattern. `BuildPiece(type, color, scale)` returns a GameObject and first tries `Model.Load("models/chess/{type}.vmdl")` with procedural fallback — so importing the Poly Haven set on the user's machine later is a drop-in, one-function swap. Board = 64 tinted cell boxes + rim on a table.

**D6 — Floor pops: flat 2D glyphs via shader glyph atlas (user choice).** Keep `FloorCheckerboard.cs`'s slab, checker bake, and `Repick()` spacing/round-robin logic. Changes: pops occur on **both** square colors (drop the whites-only parity filter), glyph color is **opposite** the square (white glyph on dark cell, dark glyph on light cell). Mechanism: rasterize the portablejim CC0 SVG pieces into a 6-glyph alpha atlas texture (`Assets/textures/chess_glyphs.png`, generated by a small Python script in `scripts/` so it's reproducible); extend the popmap texture so each cell encodes glyph index (R channel, 0=none/1–6=piece); rewrite `floor_checker.shader` to sample the atlas within a popped cell and blend the glyph over the checker color, tinted by parity. Keep bevel/border/roughness attributes. **This needs in-editor shader iteration — flagged as a user-machine-heavy task in M6.**

**D7 — Networking split.** s&box lobby (presence, seats, chat, station rebuild) keeps `LobbyNetworkManager` near-verbatim (delete the `TargetUrl` backend-switcher; keep admin claims + station-count rebuild + host migration). Each player's lichess game streams on **their** client with **their** token — tokens never cross the wire. Spectators see boards via `[Rpc.Broadcast]` relay of `(fen, lastMoveUci, wClockMs, bClockMs, whiteName, blackName)` — direct analog of the old `NetBoardStart/NetBoardSync/NetBoardClear` — with host-folded `[Sync] BoardFen` for late joiners.

**D8 — HTTP allowlist**: set `"HttpAllowList": ["https://lichess.org/"]` in `gambit.sbproj` (covers `/api/*`, `/oauth`, token, import). If s&box rejects it at publish, fall back to `null` with a comment.

## File mapping (rotaliate → gambit, all under `Code/` unless noted)

**Keep near-verbatim** (namespace rename only): `World/LobbyNetworkManager.cs` (minus TargetUrl switcher), `World/LobbyPlayer.cs`, `LobbyRoom.cs`, `FacePlayer.cs`, `RoomLightOrbit.cs`, `Streetlights.cs`, `MarqueeGlow.cs`, `DiscordButton.cs`, Info/Settings walls + stations + wall razor panels, `Game/GamepadBinds.cs`, `Audio/SoundPlayer.cs` + all sounds (tick/tock→clocks, pop→captures, servo→slides), `UI/ChatPanel.razor`, `LobbyOverlay.razor`, `SettingsScreen.razor`, `InfoScreen.razor`, `NameTagPanel.razor`, marquee number panel (board number), `Editor/HotloadRebuild.cs` (retarget types), `Libraries/gamah.skafinity` (music), `Assets/shaders/floor_checker.shader` (until M6 glyph rewrite), `Assets/scenes/lobby.scene` (user re-wires renamed components in-editor once — documented M1 step).

**Transform**:
- `World/ArcadeRing.cs` → `World/ChessRing.cs` — keep ring math/preview/network-spawn/slide-rebuild/screen-rect UI math; `BuildCabinet()` → `BuildChessTable()` (table, board frame, 64 cells, `ChessSetBuilder` pieces at start position, two camera anchors).
- `World/ArcadeStation.cs` → `World/ChessStation.cs` — keep occupancy RPC pattern but two seats (D1), camera lock flow, relay-tap events; new relay payloads (D7); delete demo/attract replay (Go-backed; "TV attract" is stretch) and GUID `ValidateIdentity`.
- `World/FloorCheckerboard.cs` — pops → glyph popmap (D6).
- `Game/PlayerData.cs` — drop GUID/Steam-link identities; add `LichessToken` (never logged/synced) + `LichessUsername`; keep cosmetic settings.
- `Theme/Colors.cs` — colorblind palettes → board/piece themes.
- `UI/ArcadeScreenPanel.razor` → `UI/StationScreenPanel.razor` (same screen-router pattern, floating above the table).
- `UI/Screens/SplashScreen.razor` — rewrite: "Sign in with lichess" (PKCE or token paste) / "Play anonymously" (display name only).
- `UI/Screens/ModePickerScreen.razor` — rewrite: Seek (rapid/classical, rated toggle) / Challenge lichess user / Challenge AI 1–8 / Open-challenge link / Puzzles / Watch TV; anonymous users see local-board play + puzzles + TV only.
- `UI/GameHud.razor` — rewrite: clocks, SAN move list, draw/resign/abort, opponent name+rating, chat (lichess chat online, s&box chat local), game-over + import-link panel.

**Delete** (Go-backend era): `Api/ApiClient.cs`, `Api/Models.cs`, `Ws/WsClient.cs`, `Game/MultiplayerController.cs`, `GameController.cs`, `Board.cs`, `PlayerStats.cs`, `Achievements.cs`, `World/CubeBoardView.cs`, `RemoteBoard.cs`, `LeaderboardWall.cs`, `LeaderboardStation.cs`, `UI/WallLeaderboardPanel.razor`, `UI/Screens/MultiplayerScreen.razor`, `LeaderboardScreen.razor`, demo/button/rotate glyph panels, `scripts/gen_thumbnail.py`, Rotaliate thumbnails.

**New**:
- `Api/LichessApi.cs` — REST (account, seek, move, resign/draw/abort, challenges, puzzles, TV channels, import) with single-flight queue + 60s 429 backoff.
- `Api/LichessModels.cs` — DTOs. `Api/NdjsonClient.cs` — streaming reader (keep-alive tolerant, auto-reconnect/resync). `Api/LichessAuth.cs` — PKCE + token-paste (D3).
- `Chess/ChessGame.cs` wrapper, `Chess/Vendor/` (or `MoveGen.cs` fallback), `Chess/Pgn.cs`, `Chess/Clock.cs` (interpolating display clock).
- `Game/LichessGameController.cs` — state machine Idle→Seeking→Playing→GameOver; owns event+game streams, applies `gameState`, posts moves, clocks, chat.
- `Game/LocalGameController.cs` — anonymous two-seat local game + PGN import. `Game/PuzzleController.cs` — fetch/solve-locally.
- `World/ChessBoardView.cs` — renders `ChessGame`/FEN on station board: piece placement, move lerp, capture removal, last-move + legal-move highlights, promotion picker; cursor-ray cell picking while seated.
- `World/ChessSetBuilder.cs` (D5), `World/SpectatorBoard.cs` rewrite (giant wall board: featured station mirror + lichess TV mode).
- `Assets/textures/chess_glyphs.png` + `scripts/gen_glyph_atlas.py` (M6).

## Milestones (future sessions; every gate is user-tested in-editor)

- **M0 — Repo bootstrap** *(done on approval this session)*: copy, strip, rename, PLAN.md, allowlist, commit, push private. Gate: project opens and compiles in the user's s&box editor. Compile strategy: delete dead call sites rather than stubbing.
- **M1 — Chess board world**: `ChessRing.BuildChessTable`, `ChessSetBuilder`, `ChessStation` two-seat occupancy + camera lock (no game logic — sit down at either side, see a set-up board, stand up). User re-wires `lobby.scene` components once. Gate: walk lobby, 8 boards render with pieces, both seats work, slide-rebuild works.
- **M2 — Local anonymous chess**: vendor/patch chess lib (or fallback) + perft debug command; `ChessBoardView` input/render; `LocalGameController` + seat/turn RPCs; FEN spectator relay + late-join; PGN build + `POST /api/import` (first lichess call — validates allowlist); minimal GameHud. Gate: two clients play a full legal game at one board, third client spectates, import link opens on lichess.
- **M3 — Lichess auth**: Spike 1 FIRST: ndjson streaming via `/api/tv/feed` (D4 — project-critical). Spike 2: browser-open + loopback feasibility (D3). Then `LichessAuth`, token storage, SplashScreen, `GET /api/account` name/rating in name tag. Gate: sign in, token persists across restart, 401 → re-prompt.
- **M4 — Board API play**: hardened `NdjsonClient`; `LichessGameController` (event stream, held-open seek, game stream, moves, clocks, chat, draw/resign/abort); ModePicker seek/challenge/AI UI (rapid/classical seeks; blitz only direct; no bullet — say so); spectator relay of live lichess games. Gate: play a rated rapid game vs a random lichess opponent from inside the lobby while another client watches.
- **M5 — Spectate / TV / puzzles**: SpectatorBoard wall rewrite (featured station + TV via `/api/tv/channels`+`feed`); puzzles (`daily`/`next`, local solve with retry/reveal, "doesn't affect your lichess puzzle rating" copy); watch a specific game by ID (delay noted in UI). Gate: TV on the wall; puzzles solvable at any board.
- **M6 — Floor glyph pops + polish**: glyph atlas + shader rewrite (D6, user-machine-heavy); optional Poly Haven piece import + swap; sound mapping; new thumbnail/branding; settings trim; rate-limit audit (single-flight + 429 everywhere); token-hygiene audit (grep logs/RPCs). Gate: full playtest.

## Risks / open questions

1. **NDJSON streaming under s&box `Http`** — project-critical, no fallback for Board API; spiked first in M3 (TV feed, no auth).
2. **Loopback/browser for PKCE** — token-paste fallback is guaranteed shippable.
3. **Chess library vs s&box whitelist** — compact hand-written move-gen budgeted as fallback (verified with perft).
4. **No toolchain on this host** — all verification is review + user-run gates; keep changes pattern-matched to proven code (AddBox, Rpc idioms).
5. **Shader glyph pops (D6)** need in-editor iteration — scheduled last (M6) so it never blocks gameplay.
6. **Scene rewire** — component renames require one manual in-editor pass (M1, documented).
7. **Two-seat station** breaks the old single-occupant invariant — touches enter/leave, camera, disconnect-cleanup, HUD routing; the main structural rework of the fork.
8. **Clock drift** — clocks interpolate between stream updates; resync from `gameFull` on reconnect.
9. **Attract/demo mode removed** (was Go-replay-based); "lichess TV on station 0" is unscoped stretch.

## Verification

- This session: after push, `gh repo view Gamah/gambit` confirms private repo; user clones/opens in s&box editor and confirms compile (M0 gate).
- Ongoing: each milestone gate above is the end-to-end test, run by the user in-editor/in-game; API-facing code can additionally be exercised on this host with a standalone `dotnet` harness (plain HttpClient against lichess) to validate endpoint handling before porting into s&box idioms.
