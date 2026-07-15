# PLAN.md — Terry's Gambit: what's left

**How the game works, what lichess allows, and the s&box lore live in `CLAUDE.md`.**
This file is only upcoming work and open issues.

## Status

**M0–M6 are complete and gate-passed.** M5+M6 (spectate/TV/puzzles, floor glyph pops,
sounds, audits) merged to master 2026-07-15; M4 (Board API play) merged 2026-07-14.

**M7 (gamchess) is scoped and approved — it is the only scheduled work.** Everything
else below is deferred, optional, or unresolved.

Milestone history is in the git log — `git log --oneline` and the merge commits carry
the per-milestone detail. Don't re-litigate closed decisions (D1–D8) here; they're
recorded as architecture in CLAUDE.md.

## Next: M7 — gamchess (issue #7)

**The full spec lives in GitHub issue #7** — read it before starting. It is the single
epic; #5 was folded into it and closed. Branch: **`m7-gamchess-identity`**.

One-paragraph version: add a Go/Postgres backend (`server/`) to this repo, restructured
into a splitclicker-style monorepo (`git mv gambit client`). v1 delivers paste-free lichess
sign-in via a Facepunch-auth-token-gated **OAuth code relay**, plus a durable game archive
keyed on SteamID64. **The lichess token never reaches the server** — gamchess relays codes
only and has no token column; the client keeps the PKCE verifier and does the exchange.
The WebSocket streaming relay is explicitly **out of v1** (local blitz/bullet already works
on s&box netcode, so it's a nice-to-have — still being weighed).

Deployment facts (ports, hosts, ufw/Caddy posture) are in CLAUDE.md. This host has no Go,
Docker, or psql — the server is review-only here and the user runs `make test` /
`make testinst`; port rotaliate's proven code rather than composing fresh.

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

1. **Real-time play/spectating needs one of two upgrades — both optional, neither scheduled.**
   Everything polls today because `Http.*` cannot stream (the constraint is documented in
   CLAUDE.md). The cost is ~1.5s move latency, coarse clocks (only your own
   `secondsLeft`), no blitz play, and unwatchable bullet on the spectator wall. Two ways out:
   - **gamchess** (issue #7) — a small always-on backend on gamah.net that opens the
     lichess ndjson feed server-side and forwards lines over `wss://` (`Sandbox.WebSocket`
     is whitelisted and streams incrementally; writes stay ordinary short `Http` POSTs).
     The 2026-07-13 "no hosted relay" call was made when a relay bought us streaming and
     nothing else; gamchess **rescopes it as a general backend**, so the infra cost is now
     amortised across several features rather than charged to streaming alone:
     - real-time spectating via `/api/tv/feed` (no anti-cheat delay, bullet included) and
       `/api/stream/game/{id}` (lichess delays these ~3 moves — surface in UI);
     - the only viable route to live moves-on-the-board Board API play;
     - persisting local/anonymous sbox games that never hit lichess, which today vanish
       when the lobby ends — backs a game-history/archive feature and the spectator
       "Featured" source;
     - **server-side identity** (#7 §3) — Steam via FP token in-game + OpenID on the web,
       linked to lichess OAuth; this is what makes the paste-free sign-in in #3 possible;
     - the OAuth callback in issue #5 folds in cheaply once the service exists (see #3).

     Bonus: the relay *reduces* lichess load — one server-side TV feed fans out to N
     spectators, replacing N independent client polls.
   - **Facepunch feature request** for a headers-first `Http` read
     (`HttpCompletionOption.ResponseHeadersRead`). Clean fix, removes the need for any
     infra, unblocks live both-sides clocks — but only covers streaming, not gamchess's
     other jobs. Nobody has filed it yet; worth filing in parallel since it's out of our hands.

   Bullet *play* stays impossible either way (lichess bars it on the Board API); bullet
   *spectating* is exactly what gamchess unlocks.

2. **A general inbound challenge from anywhere** is only partly covered. Receiving a
   challenge from the web works. The seated head-to-head path works by the challenger's
   client handing the exact challenge id to the opponent's client over `[Rpc.Broadcast]`
   — it does not poll `GET /api/challenge` for arbitrary strangers' challenges.

3. **Paste-free sign-in via gamchess** (issue #5) — explored, not committed. Three tiers,
   each strictly better than the last:
   - *Static page*: JS reads `?code=` and shows a one-click "Copy code" button, so
     `redirect_uri` lands somewhere clean instead of a connection-refused localhost page.
     Still a return paste.
   - *Callback relay, in-game-first*: the client mints a **Facepunch auth token**
     (`Sandbox.Services.Auth.GetToken`), gamchess verifies it at
     `POST https://public.facepunch.com/sbox/auth/token` and keys the pending code by the
     **echoed SteamId** — so the authenticated poll replaces the `state` key in #5's
     original design (nothing guessable, no CSRF window). Removes the return paste.
   - *Web-first*: add Steam **OpenID 2.0** (`steamcommunity.com/openid/login` — Steam has
     no OAuth2) so the browser proves the same SteamID the FP token proves in-game. The
     player opens gamchess, clicks Steam + lichess, and the game picks it up with
     **nothing typed or pasted either way**.

   **The token exchange stays client-side in all tiers** — gamchess relays only the OAuth
   code, which under PKCE is low-value (single-use, ~1 min, useless without the
   `code_verifier` that never leaves the client). Where the lichess token lives is the one
   real open decision; see #7 §3 for the vault/proxy/code-relay comparison (code-relay
   preferred; proxying every user's lichess traffic from one IP is probably disqualifying
   under our own ban-risk rule).

   Both auth halves are **already proven in rotaliate** and can be lifted rather than
   designed: `rotaliate/internal/steam/auth.go` (FP token — note it fails closed, and
   compares the echoed SteamId against the claim so a token for another account can't
   authorise), `internal/steam/openid.go` + `internal/api/steam_auth.go` (OpenID, with
   `op_endpoint` pinning / `return_to` scheme+host+path matching / single-use nonce worth
   copying verbatim). Unlike rotaliate there is **no username picking** — names come from
   Steam and lichess. Token-paste remains the guaranteed fallback, so all of this is additive.

   New responsibility to weigh: a persisted SteamID↔lichess link is durable identity data
   Gambit has never held. Needs an unlink/delete path and no logging.

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
