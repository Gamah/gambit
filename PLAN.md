# PLAN.md — Terry's Gambit: what's left

**How the game is built and the s&box lore live in `CLAUDE.md`. The gamchess API
contract lives in `README.md`.** This file is only upcoming work and open issues.

M7 (gamchess + the lichess rip-out) is merged to `master`. The recovery point for
everything lichess is the **`lichess-final`** tag.

---

## Where this is

Gambit is **independent of lichess** (see CLAUDE.md "There is no lichess here"). The whole
lichess surface — API client, OAuth, puzzles, TV, tokens, the splash screen, the anonymous
display name — was ripped out. Puzzles and TV come back **much later**, not now.

**If you find a lichess reference anywhere — code, comment, scene, asset, doc — it is
residue and should be gutted.** The **`lichess-final`** tag is the last commit that had the
full implementation — that's the recovery point, not `master`.

**Nothing on this branch has ever been compiled or run.** This host has no s&box toolchain,
no Go, and no Docker. ~2,000 lines came out of the client and ~1,700 lines of Go went in
without a compiler ever seeing either. Expect a fixup pass — that is planned for, not a
surprise.

The one thing that IS executable-verified: `node scripts/chess_js_perft.mjs` (node is on
the dev host). It holds the web viewer's chess rules to the same reference positions and
node counts as `client/Code/Chess/PerftCommand.cs`. All pass to depth 4.

---

## The target loop

Everything below serves one goal, and nothing else matters until it works:

> **Two editor instances join a lobby → sit at a board → set a time control → both ready
> up → play a game with clocks → it lands in the gamchess archive → replay it on
> chess.gamah.net signed in with Steam.**

---

## 1. Get it compiling  ← START HERE

The client rip-out was done blind. Open `client/gambit.sbproj` in the editor and work the
error list.

- Known-good sweep already done: every deleted type/member was grepped across
  `client/Code/` and came back clean. That catches dangling refs, **not** Razor markup
  errors, unused-using warnings, or anything the compiler alone sees.
- Deleted this pass: all `Lichess*.cs`, `GamchessSignIn`, `PuzzleController`,
  `SplashScreen`, `SpectatorResultPanel`, `LichessGameController`, `LichessPlayController`.
- `SpectatorController` was rewritten 740 → 116 lines (Featured-only).
- Regression gate: **`gambit_perft`** must pass before trusting anything else.

## 2. Deploy gamchess

On the server (`~/gambit/server`). **Docker only — no Go needed**, every Go make target
runs in a container.

```bash
cp .env.example .env     # SESSION_SECRET blank is fine (random per-process key)
make test                # runs in golang:1.22
make up                  # builds, migrates in-process at startup
curl -s localhost:6464/health
```

- `make tidy` only if `make up` fails on a module error — `go.sum` was seeded from
  rotaliate's superset because no machine here has Go.
- **Caddy vhosts are configured but were never restarted.** Verified 2026-07-15:
  `chess.gamah.net` resolves to the box and Caddy is alive (`gamah.net` → 200), but the
  TLS handshake fails with alert 80 / no certificate — i.e. no vhost for that SNI yet.
  Restart Caddy, then `gambit_gamchess_ping` should stop erroring.
- Add **no `log` directive** to the gamchess vhosts (Caddy logs nothing by default; keep
  it that way).

Then in the editor: `gambit_gamchess_ping`, `gambit_gamchess_signin`,
`gambit_gamchess_games`.

## 3. Board panel: time control + ready  ← the next real feature

**Rip out the current seated board panel entirely and replace it.** It is M2-era cruft.
The seated panel should offer exactly two things:

- **Time control** — pick one (see below). Settable while the table is idle.
- **Ready** — a per-seat ready button, shown **only when both seats are occupied**. The
  game starts when *both* players are ready, not the moment two people sit down.

This replaces the current auto-start: today `LocalGameController.HostUpdate` starts a game
the instant `whiteSeated && blackSeated`. That has to go — you can't pick a time control if
the game already started.

Design notes:

- **Host-authoritative.** Ready flags and the chosen time control are
  `[Sync(SyncFlags.FromHost)]` on `LocalGameController` (or `ChessStation`), set via
  `[Rpc.Host]` requests. The host decides; a client never asserts "the game started".
  Mirror the `ChessStation.RequestEnter` idiom — the host reads the caller from
  `Rpc.Caller`, never from an argument.
- **Ready must clear** when a seat empties, when the time control changes, and when a game
  ends. Otherwise a stale ready from the last occupant auto-starts a game on the next.
- Suggested controls: **Bullet 1+0, Blitz 3+2, Rapid 10+0, Classical 30+0, Unlimited**.
  (No external constraint on this any more — pick what feels right. Bullet is fine: local
  play runs on s&box's own netcode with no polling in the way.)
- The panel is `client/Code/UI/GameHud.razor`. It currently renders the board title, seat
  names, a status line, the move list, and the promotion picker — keep those; the
  time-control + ready block is what's new.
- **Read CLAUDE.md's UI Gotchas first.** Two traps already bit this file: an inline `<b>`
  inside a text div becomes its own flex item, and a div's auto height doesn't grow for
  wrapped text — either one silently draws the next block on top of this one. One line per
  div, no inline markup.

## 4. Clocks, and time data everywhere

Depends on §3 (the time control has to exist first).

- **`LocalGameController`**: per-seat remaining time, host-authoritative, `[Sync]`ed.
  Decrement the side to move; apply the increment on move. Flag-fall is a game end
  (`HostEnd`) with result `1-0`/`0-1` — reuse the existing resign/abandon path.
- **HUD**: both clocks while seated; accent the ticking side.
- **Spectator seat tags**: `SpectatorSeatPanel` used to show `rating | clock` and lost that
  line with lichess. Bring the clock half back, driven by *our* clock.
- **PGN**: a `[TimeControl "600+0"]` header, and `{[%clk 0:09:58]}` comments per move.
  `BuildPgn()` in `LocalGameController` is where headers are set; the vendored PGN writer
  may need a path for move comments — check `Code/Chess/Vendor/Conversions/Pgn.cs`.
- **Archive**: the PGN carries it, so the DB may need nothing. If you want to list/sort by
  time control, add a `time_control TEXT` column (migration `00002_*.sql`) rather than
  parsing PGN server-side.
- **Viewer**: `server/frontend/` — show the time control, and clock per move if present.
  **The JS PGN parser already strips `{}` comments**, so `%clk` annotations will not break
  replay (covered by the malformed-PGN test in the perft script). Re-run
  `node scripts/chess_js_perft.mjs` after touching `chess.js`.

## 5. Prove the loop

Two editor instances (network status icon → "Join via new instance"). Note both share
`FileSystem.Data`, so they're one identity locally — fine for the archive, which keys on
the FP-verified SteamID.

Play a game to mate. Expect:
- the game ends normally, and
- one archive POST succeeds (`gambit_gamchess_games` lists it), and
- chess.gamah.net shows it after Steam sign-in.

---

## Deferred / not scheduled

- **Puzzles and TV** — both were lichess features. "Much later" per the user. The old
  implementation is at the `lichess-final` tag; any rebuild should come from gamchess.
- **gamchess-backed online play** (browser play, cross-lobby sbox play). `IBoardGame` and
  `ChessBoardView.Source` were deliberately kept as a seam for exactly this — a new
  controller slots in without touching the renderer.
- **Poly Haven 3D piece import** — drop `models/chess/{type}.vmdl` in and
  `ChessSetBuilder.BuildPiece` picks them up with no code change (D5). Editor-side work.
- **New thumbnail / branding** — Rotaliate's were stripped at M0, nothing replaced them.
- **Sound-design polish** — the synthesized set is functional.
- **Featured-table wall mirror** — never re-exercised end-to-end with two clients.

## Open questions

1. **FP auth token TTL** — undocumented. We cache 120s and re-mint once on a 401 (the real
   safety net). Confirmed minting works in-editor 2026-07-15; the expiry behaviour isn't
   confirmed.
2. **Does `Sandbox.Services.Auth.GetToken` behave the same in a built game as in the
   editor?** Only the editor is proven.
3. **Does `HttpAllowList` gate `Sandbox.WebSocket`**, or only `Http.*`? Decides the cost of
   a future gamchess streaming relay.
4. **`SESSION_SECRET`** is blank by default — sessions die on restart. Set it if that's
   annoying.

## Standing constraints on how work gets done here

- **No s&box toolchain, no Go, no Docker on this host.** Verify by careful review + grep;
  the user tests in the editor and on the server. `node` IS available and runs the viewer's
  perft gate.
- **Scene edits can't be made from this host** (the editor owns the format). Prefer
  self-provisioning components (`LobbyRoom` adds `ChessRing` / the spectator wall; screens
  self-attach to the ScreenPanel) over anything needing a manual rewire.
- **Shader work needs in-editor iteration.**
- `lobby.scene` still names some deleted Rotaliate components (`ArcadeRing`,
  `LeaderboardWall`, `GameController`, …). s&box drops unknown components with a warning;
  they are harmless and pre-date all of this.
