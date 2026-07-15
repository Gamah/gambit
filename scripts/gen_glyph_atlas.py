#!/usr/bin/env python3
"""Generate the chess-piece glyph atlas for the floor pops (PLAN.md D6 / M6).

Renders the six piece silhouettes into a single horizontal alpha atlas
`Assets/textures/chess_glyphs.png` (6 cells wide, 1 tall). The floor shader
(`floor_checker.shader`) samples one cell per popped floor square and tints the
glyph the opposite colour to the square.

Atlas cell order (left→right) — the glyph index the shader/`FloorCheckerboard`
use, 1-based (0 = no glyph):

    1 King   2 Queen   3 Rook   4 Bishop   5 Knight   6 Pawn

The glyphs are our OWN render of the Unicode solid chess pieces via the system
DejaVu Sans font — a fresh raster, so the atlas is CC0-clean (no lichess/Cburnett
piece art, matching the project's all-CC0 constraint). Re-run to regenerate:

    python3 scripts/gen_glyph_atlas.py

Requires Pillow (`pip install pillow`).
"""

import os
from PIL import Image, ImageDraw, ImageFont

CELL = 128                      # px per glyph cell (square)
GLYPHS = "♚♛♜♝♞♟"  # ♚♛♜♝♞♟ (solid), K Q R B N P
FONT_CANDIDATES = [
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "/usr/share/fonts/truetype/freefont/FreeSerif.ttf",
    "/Library/Fonts/Arial Unicode.ttf",
]

OUT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "client", "Assets", "textures", "chess_glyphs.png",
)


def find_font(size):
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise SystemExit("No suitable Unicode font found; edit FONT_CANDIDATES.")


def render():
    # Size the font so a glyph fills most of the cell with a small margin.
    font = find_font(int(CELL * 0.86))
    atlas = Image.new("RGBA", (CELL * len(GLYPHS), CELL), (255, 255, 255, 0))
    draw = ImageDraw.Draw(atlas)

    for i, ch in enumerate(GLYPHS):
        # Centre the glyph in its cell using its bounding box.
        box = draw.textbbox((0, 0), ch, font=font)
        gw, gh = box[2] - box[0], box[3] - box[1]
        x = i * CELL + (CELL - gw) // 2 - box[0]
        y = (CELL - gh) // 2 - box[1]
        # White fill; the alpha channel is what the shader uses as coverage.
        draw.text((x, y), ch, font=font, fill=(255, 255, 255, 255))

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    atlas.save(OUT)
    print(f"wrote {OUT} ({atlas.width}x{atlas.height}, {len(GLYPHS)} glyphs)")


if __name__ == "__main__":
    render()
