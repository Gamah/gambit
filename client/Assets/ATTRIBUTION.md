# Asset attribution

All bundled art/audio is CC0 or self-generated, per the project's all-CC0 constraint
(CLAUDE.md "Asset licensing"). Recorded here for auditability even when no attribution
is legally required.

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
