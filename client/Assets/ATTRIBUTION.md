# Asset attribution

All bundled art/audio is CC0 or self-generated, per the project's all-CC0 constraint
(CLAUDE.md "Asset licensing"), with **exactly one documented exception** — the lichess
logo, below. Recorded here for auditability even when no attribution is legally required.

## Exception to the all-CC0 rule

- **The lichess logo** (`sadsnake1`) — inlined as an SVG path in
  `server/internal/api/lichess_pages.go`, on the button that leaves for lichess.

  **Not CC0, and not free.** lila's own
  [`COPYING.md`](https://github.com/lichess-org/lila/blob/master/COPYING.md) lists
  `public/logo` under *"Exceptions (non-free)"* with the terms: **"Only use to refer to
  lichess.org"**. Lichess publishes no brand-guidelines page; that line is the whole of
  the grant, and it is the reason this is allowed here — the mark appears only on a
  control whose entire purpose is to navigate to lichess.org, which is precisely
  "referring to lichess.org".

  **Rules for it, which are not negotiable:**
  - Only ever on something that goes to lichess. Never as decoration, never as a bullet
    or an icon, never in the s&box client.
  - Never anywhere it could read as endorsement or affiliation. **Lichess has not
    endorsed Terry's Gambit**, and there is no relationship to imply.
  - Never presented as a Gambit mark, and never modified beyond taking the button's
    colour via `currentColor`.

  Copied from `public/logo/lichess.svg` (a single path, ~613 bytes). Inlined rather than
  vendored as a file so the web viewer keeps its zero-image-assets property.

  If this ever becomes uncomfortable, it is one `const` to delete: the button falls back
  to text and nothing else breaks.

## Textures

- **`textures/chess_glyphs.png`** — floor-pop chess-piece glyph atlas (M6 / D6).
  A fresh rasterisation of the Unicode solid chess pieces (U+265A–U+265F) rendered
  from the system **DejaVu Sans** font (Bitstream Vera / public-domain-equivalent
  license) by `scripts/gen_glyph_atlas.py`. This is our own raster output — **not**
  the Cburnett/Wikipedia/lichess piece set (those are CC-BY-SA/GPL, not CC0). CC0 /
  public-domain-clean. Regenerate with `python3 scripts/gen_glyph_atlas.py`.

## Sounds

- **`sounds/sfx/*.wav`** — synthesised in-repo by `scripts/gen_sounds.py` (numpy).
  Original, CC0.

## Music

- **`Libraries/gamah.skafinity`** — the procedural music library, source-committed.
