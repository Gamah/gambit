# PLAN.md — Terry's Gambit: what's left

How the game is built and the s&box lore live in **`CLAUDE.md`**. The gamchess API
contract lives in **`README.md`**. This file is only ever upcoming work.

---

## The web viewer needs a lot of work

`server/frontend/` (`index.html` / `app.js` / `chess.js` / `style.css`) — the archive
viewer at chess.gamah.net. It works, but it has never had a design pass, and **nobody has
ever looked at it on anything but a desktop browser**. Treat the list below as observations,
not a spec — the real first step is to open it and decide what it should be.

What's already known to be weak:

- **The CSS has barely been exercised.** The board squares resized to fit whichever piece
  stood on them until 2026-07-15 — an 8×8 grid that wasn't actually holding a grid. That
  bug surviving this long says the styling has had no real scrutiny.
- **Never checked narrow / mobile.** The board is `width: min(28rem, 100%)` next to a
  `flex: 1 1 14rem` side panel, and the games table has four fixed columns. What that does
  under 400px is unknown.
- **The games list shows Played / White / Black / Result only.** No time control, though
  the PGN now carries one. Adding a column means touching `index.html`'s `<thead>` too.
- **Game meta is one text line** — date, then the time control tacked on after a `·`
  (`loadGame` in `app.js`). Fine as a stopgap; not a design.
- **The per-move `%clk` display is brand new and unseen.** `shortClk` trims the leading
  `0:` so a bullet clock reads `0:51.63`; that call was made without ever seeing it
  rendered next to the SAN.
- **Sign-in is a bare button.** The Steam OpenID round trip works, but the signed-out and
  error states have had no thought.

Constraints worth knowing before starting:

- **The frontend is baked into the Docker image** (`COPY --from=builder /src/frontend
  /frontend`). A restart won't pick up CSS changes — the server needs `git pull && make
  rebuild`.
- **Zero image assets, and it should stay that way.** Pieces are Unicode glyphs with
  U+FE0E forcing text presentation; that's what keeps the viewer CC0-clean with nothing to
  attribute. The s&box client can't render these glyphs at all (they come out as colour
  emoji — see CLAUDE.md), but a browser can, so this is the one place they're allowed.
- **`chess.js` is rules code, not view code.** It is gated by
  `node scripts/chess_js_perft.mjs`, which runs on the dev host. Re-run it after touching
  that file — it holds the viewer's rules and PGN parsing to the same reference positions
  and real C# writer output as the client.
