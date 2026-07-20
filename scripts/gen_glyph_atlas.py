#!/usr/bin/env python3
"""Generate the chess-piece glyph atlas for the floor pops (CLAUDE.md D6 / M6).

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

    python3 scripts/gen_glyph_atlas.py          # floor pop atlas (alpha silhouettes)
    python3 scripts/gen_glyph_atlas.py --2d      # M16 2D play-mode atlas (fill + outline, RGBA)

The `--2d` branch writes a SECOND atlas, `Assets/textures/chess_glyphs_2d.png`, for the
M16 flat-board play mode. It is a full-colour 6×2 grid — row 0 white pieces, row 1 black,
columns K Q R B N P — with each glyph rendered as a filled sprite with a contrasting
outline (per-colour, so both read on both the cream and brown squares). Unlike the floor
atlas above (an alpha-only silhouette the shader tints), the flat quad samples this
directly, so the colour has to be baked in.

Requires Pillow (`pip install pillow`).
"""

import os
import sys
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

# ── M16 2D play-mode atlas ──
# A 6×2 grid: columns are K Q R B N P (index 0..5), row 0 = white, row 1 = black.
# BuildFlatPiece maps ChessPieceType → column and picks the row by colour, then UVs the
# quad to (col/6, row/2)…((col+1)/6, (row+1)/2). Full RGBA (fill + outline baked in).
CELL2 = 256                     # px per cell — bigger than the floor atlas; it carries real colour
GLYPHS_2D = "♚♛♜♝♞♟"  # same solid glyphs, column order K Q R B N P
OUT_2D = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "client", "Assets", "textures", "chess_glyphs_2d.png",
)
# fill / outline per colour. A dark outline on the near-white piece and a light outline on
# the near-black piece is what lets each colour read against BOTH square colours.
WHITE_FILL, WHITE_STROKE = (245, 245, 245, 255), (20, 20, 20, 255)
BLACK_FILL, BLACK_STROKE = (20, 20, 20, 255), (235, 235, 235, 255)


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


def render_2d():
    """The M16 flat-board atlas: 6 columns (K Q R B N P) × 2 rows (white, black),
    each a filled glyph with a contrasting outline, full RGBA."""
    # Leave room for the stroke inside the cell so a fat outline never clips the edge.
    stroke = int(CELL2 * 0.06)
    font = find_font(int(CELL2 * 0.78))
    cols = len(GLYPHS_2D)
    atlas = Image.new("RGBA", (CELL2 * cols, CELL2 * 2), (0, 0, 0, 0))
    draw = ImageDraw.Draw(atlas)

    for row, (fill, edge) in enumerate(((WHITE_FILL, WHITE_STROKE), (BLACK_FILL, BLACK_STROKE))):
        for col, ch in enumerate(GLYPHS_2D):
            # Bounding box WITH the stroke, so centring accounts for the outline's spread.
            box = draw.textbbox((0, 0), ch, font=font, stroke_width=stroke)
            gw, gh = box[2] - box[0], box[3] - box[1]
            x = col * CELL2 + (CELL2 - gw) // 2 - box[0]
            y = row * CELL2 + (CELL2 - gh) // 2 - box[1]
            draw.text((x, y), ch, font=font, fill=fill,
                      stroke_width=stroke, stroke_fill=edge)

    os.makedirs(os.path.dirname(OUT_2D), exist_ok=True)
    atlas.save(OUT_2D)
    print(f"wrote {OUT_2D} ({atlas.width}x{atlas.height}, {cols}×2 filled+outlined glyphs)")


if __name__ == "__main__":
    if "--2d" in sys.argv:
        render_2d()
    else:
        render()
