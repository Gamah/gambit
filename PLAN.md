# PLAN.md — Terry's Gambit: what's left

**How the game works, what lichess allows, and the s&box lore live in `CLAUDE.md`.**
This file is only upcoming work and open issues.

## Status

**M0–M6 are complete and gate-passed.** M5+M6 (spectate/TV/puzzles, floor glyph pops,
sounds, audits) merged to master 2026-07-15; M4 (Board API play) merged 2026-07-14.
Everything below is deferred, optional, or unresolved.

Milestone history is in the git log — `git log --oneline` and the merge commits carry
the per-milestone detail. Don't re-litigate closed decisions (D1–D8) here; they're
recorded as architecture in CLAUDE.md.

## Untested

- **Featured-sbox-table wall mirror** — the spectator wall's "Show a live table" source
  mirrors a live sbox table onto the west wall via the M4 `[Sync]` relay. The code is
  the proven M4 relay path, just never re-exercised through the wall. Low risk; needs
  two clients and a live table. (Deferred by user call at the M5 gate, not blocking.)

## Deferred / optional (post-v1)

Ordered roughly by value, not commitment — none of these are scheduled.

- **Draw offer/accept + abort-before-first-move** — `/api/board/game/{id}/draw/yes`,
  `/abort`. Resign is already done, so this is the same shape.
- **Rated toggle** on the challenge/head-to-head UI. Quick-match already offers
  rated/casual; direct challenges are hardcoded casual.
- **Lichess online chat** — the endpoints exist; the HUD currently only carries sbox chat.
- **Poly Haven 3D piece import + swap** — drop `models/chess/{type}.vmdl` in and
  `ChessSetBuilder.BuildPiece` picks them up with no code change (D5 was designed for
  this). User-machine work: the import is an editor pass.
- **New thumbnail / branding** — Rotaliate thumbnails were stripped at M0 and nothing
  replaced them.
- **General sound-design revisit** — the synthesized set is functional but worth a
  polish pass (new CC0 clicks, better capture/clock cues). Note lichess's own sounds are
  not license-usable (see CLAUDE.md).

## Open issues

1. **Real-time play needs one of two upgrades — both optional, neither scheduled.**
   Everything polls today because `Http.*` cannot stream (the constraint is documented in
   CLAUDE.md). The cost is ~1.5s move latency, coarse clocks (only your own
   `secondsLeft`), and no blitz. Two ways out:
   - **Facepunch feature request** for a headers-first `Http` read
     (`HttpCompletionOption.ResponseHeadersRead`). Clean fix, removes the need for any
     infra, unblocks live both-sides clocks. Nobody has filed it yet.
   - **A relay** on gamah.net that reads the lichess ndjson stream server-side and
     forwards lines over `wss://` (`Sandbox.WebSocket` is whitelisted and streams
     incrementally; writes stay ordinary short `Http` POSTs). **User decided against a
     hosted relay (2026-07-13)** — it's the only viable route to streaming but costs
     infra, and it is needed for nothing else in the current feature set.

   Bullet stays impossible either way (lichess bars it on the Board API).

2. **A general inbound challenge from anywhere** is only partly covered. Receiving a
   challenge from the web works. The seated head-to-head path works by the challenger's
   client handing the exact challenge id to the opponent's client over `[Rpc.Broadcast]`
   — it does not poll `GET /api/challenge` for arbitrary strangers' challenges.

3. **Nice-to-have: a static OAuth callback page on gamah.net.** Would make the
   advanced/PKCE paste painless — JS reads `?code=` and shows a one-click "Copy code"
   button, so `redirect_uri` lands somewhere clean instead of a connection-refused
   localhost page. The primary token-paste flow doesn't need it.

4. **`UseS256=false` toggle** in `LichessOAuth` exists to test whether lichess accepts
   `plain` (unhashed) PKCE challenges — never actually tested. S256 works, so this is
   only curiosity/simplification.

## Standing constraints on how work gets done here

- This host has **no s&box toolchain** — every gate is user-tested in the editor. Verify
  by review + grep; keep changes pattern-matched to proven code (`AddBox`, the Rpc
  idioms). API-facing code can be exercised on this host with a standalone `dotnet`
  harness (plain HttpClient against lichess) before porting to s&box idioms.
- **Shader work needs in-editor iteration** — it was scheduled last (M6) for exactly this
  reason. Any future shader change carries the same cost.
- **Scene edits can't be made from this host** (the editor owns the format). Prefer
  self-provisioning components (`LobbyRoom` adds `ChessRing` / the spectator wall;
  screens self-attach to the ScreenPanel) over anything that needs a manual rewire pass.
