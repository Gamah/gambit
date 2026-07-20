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
    python3 scripts/gen_glyph_atlas.py --2d      # M16 2D play-mode piece sprites (12 PNGs)

The `--2d` branch writes TWELVE per-piece PNGs, `Assets/textures/chess2d_{w,b}_{type}.png`,
for the M16 flat-board play mode. Each is a full-colour sprite — a filled glyph with a
contrasting per-colour outline (so both read on both the cream and brown squares) — drawn
by the engine's built-in SpriteRenderer, which takes one texture per sprite (not an atlas).
Unlike the floor atlas above (an alpha-only silhouette the shader tints), these carry their
final colour, so the fill+outline are baked in.

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

# ── M16 2D play-mode piece sprites ──
# TWELVE individual RGBA PNGs (not an atlas): the flat play mode renders each piece with the
# engine's built-in SpriteRenderer, which takes ONE texture per sprite (Sprite.FromTexture),
# so per-piece files are the natural fit — no atlas UV math, and crucially no custom shader
# (SpriteRenderer does unlit + alpha-cutoff itself). Files: chess2d_{w|b}_{type}.png in
# Assets/textures/, where type is the lower-cased ChessPieceType. Each is a filled glyph with
# a contrasting per-colour outline so both colours read on both the cream and brown squares.
CELL2 = 256                     # px per sprite — carries real fill+outline colour
GLYPHS_2D = "♚♛♜♝♞♟"  # solid glyphs, in the order below
PIECE_NAMES = ["king", "queen", "rook", "bishop", "knight", "pawn"]  # matches GLYPHS_2D order
TEX_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "client", "Assets", "textures",
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
    """The M16 flat-board sprites: 12 individual RGBA PNGs (white+black × K Q R B N P),
    each a filled glyph with a contrasting outline, centred in a CELL2×CELL2 image."""
    # Leave room for the stroke inside the cell so a fat outline never clips the edge.
    stroke = int(CELL2 * 0.06)
    font = find_font(int(CELL2 * 0.78))
    os.makedirs(TEX_DIR, exist_ok=True)

    count = 0
    for color, (fill, edge) in (("w", (WHITE_FILL, WHITE_STROKE)), ("b", (BLACK_FILL, BLACK_STROKE))):
        for ch, name in zip(GLYPHS_2D, PIECE_NAMES):
            img = Image.new("RGBA", (CELL2, CELL2), (0, 0, 0, 0))
            draw = ImageDraw.Draw(img)
            # Bounding box WITH the stroke, so centring accounts for the outline's spread.
            box = draw.textbbox((0, 0), ch, font=font, stroke_width=stroke)
            gw, gh = box[2] - box[0], box[3] - box[1]
            x = (CELL2 - gw) // 2 - box[0]
            y = (CELL2 - gh) // 2 - box[1]
            draw.text((x, y), ch, font=font, fill=fill, stroke_width=stroke, stroke_fill=edge)

            out = os.path.join(TEX_DIR, f"chess2d_{color}_{name}.png")
            img.save(out)
            count += 1

    print(f"wrote {count} piece sprites to {TEX_DIR}/chess2d_*.png ({CELL2}x{CELL2} each)")


if __name__ == "__main__":
    if "--2d" in sys.argv:
        render_2d()
    else:
        render()
