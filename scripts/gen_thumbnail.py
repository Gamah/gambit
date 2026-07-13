#!/usr/bin/env python3
"""Generate the Rotaliate title art in three aspect ratios.

Writes (at the repo root):
  thumbnail_square.png   512 x 512
  thumbnail_wide.png     910 x 512   (landscape)
  thumbnail_tall.png     512 x 910   (portrait)

Approximates the in-game look without the engine: the `normal` colour palette,
active cells with the vertical-gradient + upper-shine + rounded-corner style, dark
vignetted "solved" cells, and the white 2x2 selector. The board is a *genuinely
legal* mid-game position: a 24-of-each-colour + 4-black start (no pre-solved 2x2),
then real rotation+resolution play, so every colour count stays a multiple of 4
(24 -> 20 -> 16 ...) just as a real game removes 4 cells per match. It is stopped on
the first state that is mid-game, has legal counts, AND is one rotation from a match;
the selector is drawn caught mid-rotation -- the four tiles lifted and turned 45 deg
along their arc, about to snap the group into a match. The same board is used in all
three crops so the art is consistent.

Requires: Pillow (a bold sans TTF; FreeSansBold by default).
Run: python3 scripts/gen_thumbnail.py
"""
import math as m
import os, random
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops

S = 2                      # supersample
BG = (30, 30, 32)          # neutral dark grey
N = 10
PAL = {                    # normal palette (0 = solved/black)
    1: (0xAA, 0x00, 0x00),  # red
    2: (0x00, 0x00, 0xAA),  # blue
    3: (0x00, 0xAA, 0x00),  # green
    4: (0xAA, 0xAA, 0x00),  # yellow
}

FONT_CANDIDATES = [
    "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
]
FB = next((p for p in FONT_CANDIDATES if os.path.exists(p)), FONT_CANDIDATES[0])

def lerp(a, b, t): return tuple(int(round(a[i] + (b[i]-a[i])*t)) for i in range(3))
def scale(c, f): return tuple(max(0, min(255, int(c[i]*f))) for i in range(3))
def font(size): return ImageFont.truetype(FB, int(size))

# module-level drawing surface + geometry, (re)set per render
img = None
W = H = 0

# ---------------------------------------------------------------- cell drawing
def draw_active(d, x, y, w, h, color):
    rad = w*0.18
    top, bot = scale(color, 1.55), scale(color, 0.62)
    strips = int(h)
    grad = Image.new("RGB", (max(1, int(w)), strips)); gp = grad.load()
    for j in range(strips):
        t = j/max(1, strips-1)
        col = lerp(top, color, min(1, t*1.4)) if t < 0.5 else lerp(color, bot, (t-0.5)/0.5)
        for i in range(grad.width):
            gp[i, j] = col
    mask = Image.new("L", (max(1, int(w)), strips), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, grad.width-1, strips-1], radius=rad, fill=255)
    img.paste(grad, (int(x), int(y)), mask)
    sh = Image.new("L", (max(1, int(w)), strips), 0)
    ImageDraw.Draw(sh).rounded_rectangle([w*0.12, h*0.10, w*0.88, h*0.44], radius=rad*0.8, fill=70)
    sh = ImageChops.multiply(sh.filter(ImageFilter.GaussianBlur(w*0.06)), mask)
    img.paste(Image.new("RGB", (max(1, int(w)), strips), (255, 255, 255)), (int(x), int(y)), sh)

def draw_solved(d, x, y, w, h):
    rad = w*0.18
    d.rounded_rectangle([x, y, x+w, y+h], radius=rad, fill=(18, 18, 20))
    vig = Image.new("L", (max(1, int(w)), max(1, int(h))), 0)
    vd = ImageDraw.Draw(vig)
    for i in range(24):
        t = i/24; r = (w/2)*(1-t)
        vd.ellipse([w/2-r, h/2-r, w/2+r, h/2+r], fill=int(50*t))
    mask = Image.new("L", vig.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, vig.width-1, vig.height-1], radius=rad, fill=255)
    img.paste(Image.new("RGB", vig.size, (8, 8, 10)), (int(x), int(y)), ImageChops.multiply(vig, mask))

def make_tile(color, w):
    """Standalone RGBA tile in the active-cell style (gradient + shine + rounded)."""
    w = int(w); rad = w*0.18
    top, bot = scale(color, 1.55), scale(color, 0.62)
    grad = Image.new("RGB", (w, w)); gp = grad.load()
    for j in range(w):
        t = j/max(1, w-1)
        col = lerp(top, color, min(1, t*1.4)) if t < 0.5 else lerp(color, bot, (t-0.5)/0.5)
        for i in range(w):
            gp[i, j] = col
    mask = Image.new("L", (w, w), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, w-1, w-1], radius=rad, fill=255)
    tile = Image.new("RGBA", (w, w), (0, 0, 0, 0)); tile.paste(grad, (0, 0), mask)
    sh = Image.new("L", (w, w), 0)
    ImageDraw.Draw(sh).rounded_rectangle([w*0.12, w*0.10, w*0.88, w*0.44], radius=rad*0.8, fill=70)
    sh = ImageChops.multiply(sh.filter(ImageFilter.GaussianBlur(w*0.06)), mask)
    return Image.composite(Image.new("RGBA", (w, w), (255, 255, 255, 255)), tile, sh)

def draw_socket(x, y, w):
    """Recessed dark square left where a tile was lifted out for rotation."""
    d = ImageDraw.Draw(img, "RGBA")
    d.rounded_rectangle([x, y, x+w, y+w], radius=w*0.18, fill=(12, 12, 14, 255))
    d.rounded_rectangle([x+1.5*S, y+1.5*S, x+w-1.5*S, y+w-1.5*S],
                        radius=w*0.18, outline=(0, 0, 0, 150), width=int(3*S))

# ---------------------------------------------------------------- board logic
def rotate(g, r, c, d):
    tl, tr, bl, br = g[r][c], g[r][c+1], g[r+1][c], g[r+1][c+1]
    if d == 0:   # CW
        g[r][c], g[r][c+1], g[r+1][c+1], g[r+1][c] = bl, tl, tr, br
    else:        # CCW
        g[r][c], g[r][c+1], g[r+1][c+1], g[r+1][c] = tr, br, bl, tl

def resolve(g):
    while True:
        kill = set()
        for r in range(N-1):
            for c in range(N-1):
                v = g[r][c]
                if v != 0 and g[r][c+1] == v and g[r+1][c] == v and g[r+1][c+1] == v:
                    kill.update({(r, c), (r, c+1), (r+1, c), (r+1, c+1)})
        if not kill:
            return
        for (r, c) in kill:
            g[r][c] = 0

def presolved(g):
    for r in range(N-1):
        for c in range(N-1):
            v = g[r][c]
            if v != 0 and g[r][c+1] == v and g[r+1][c] == v and g[r+1][c+1] == v:
                return True
    return False

def one_rot_from_match(g, r, c):
    # rotating a 2x2 only permutes its own 4 cells, so the match it creates always
    # forms in an overlapping neighbour -> scan the whole grid after the rotation.
    for d in (0, 1):
        t = [row[:] for row in g]
        rotate(t, r, c, d)
        if presolved(t):
            return d
    return None

def find_match_spot(g):
    """A non-black 2x2 one rotation from a match, preferring the board centre."""
    spots = []
    for r in range(N-1):
        for c in range(N-1):
            if 0 in (g[r][c], g[r][c+1], g[r+1][c], g[r+1][c+1]):
                continue
            d = one_rot_from_match(g, r, c)
            if d is not None:
                spots.append((r, c, d))
    if not spots:
        return None
    ctr = (N-1)/2
    return min(spots, key=lambda s: (s[0]-ctr)**2 + (s[1]-ctr)**2)

def counts(g):
    from collections import Counter
    c = Counter(v for row in g for v in row)
    return [c.get(k, 0) for k in (0, 1, 2, 3, 4)]

def legal_counts(g):
    # Start is 24 of each colour + 4 black; a match removes 4 of a colour, so every
    # colour count must stay a multiple of 4 (24 -> 20 -> 16 ...) and black >= 4.
    blk, *cols = counts(g)
    return blk >= 4 and all(c % 4 == 0 and c <= 24 for c in cols)

def make_board(seed):
    rnd = random.Random(seed)
    flat = [1]*24 + [2]*24 + [3]*24 + [4]*24 + [0]*4
    while True:
        rnd.shuffle(flat)
        g = [flat[i*N:(i+1)*N][:] for i in range(N)]
        for _ in range(2000):
            if not presolved(g):
                break
            for r in range(N-1):
                for c in range(N-1):
                    v = g[r][c]
                    if v != 0 and g[r][c+1] == v and g[r+1][c] == v and g[r+1][c+1] == v:
                        r2, c2 = rnd.randrange(N), rnd.randrange(N)
                        g[r][c], g[r2][c2] = g[r2][c2], g[r][c]
        if not presolved(g):
            break
    for _ in range(6000):
        blk = sum(1 for row in g for v in row if v == 0)
        if 12 <= blk <= 36 and legal_counts(g):
            spot = find_match_spot(g)
            if spot:
                return g, spot
        rotate(g, rnd.randrange(N-1), rnd.randrange(N-1), rnd.randrange(2))
        resolve(g)
    return None

# ---------------------------------------------------------------- one board, reused
BOARD = None
for _seed in range(400):
    res = make_board(_seed)
    if res:
        BOARD = (_seed, *res)
        break
assert BOARD, "no legal mid-game match board found"
SEED, GRID, (SR, SC, SDIR) = BOARD
print(f"seed={SEED} counts(0/R/B/G/Y)={counts(GRID)} legal={legal_counts(GRID)} "
      f"presolved={presolved(GRID)} selector=({SR},{SC}) dir={'CW' if SDIR == 0 else 'CCW'}")

# ---------------------------------------------------------------- responsive render
def fit_size(text, maxw, spacing_frac=0.0, ref=200.0):
    f = font(ref)
    tot = sum(f.getlength(ch) for ch in text) + (ref*spacing_frac)*(len(text)-1)
    return maxw * ref / tot

def render(out_w, out_h, path):
    global img, W, H
    W, H = out_w*S, out_h*S
    img = Image.new("RGB", (W, H), BG)

    # ---- title sizing (fit to width, capped by height) ----
    title = "ROTALIATE"
    sub = "ROTATE  ·  MATCH  ·  CLEAR"
    sp_frac = 0.10
    t_size = min(fit_size(title, W*0.90, sp_frac), H*0.14)
    s_size = min(t_size*0.32, fit_size(sub, W*0.92))
    spacing = t_size*sp_frac
    top = H*0.05
    gap_ts = t_size*0.20
    block_bottom = top + t_size + gap_ts + s_size

    # ---- board geometry (centre in the space below the title) ----
    bottom_margin = H*0.05
    avail_h = H - block_bottom - bottom_margin
    avail_w = W*0.92
    board_px = min(avail_w, avail_h)
    gap = max(1, int(board_px*0.012))
    bx = (W - board_px)/2
    by = block_bottom + max(0, (avail_h - board_px)/2)
    cell = (board_px - gap*(N-1))/N
    step = cell + gap

    # ---- radial glow centred on the board ----
    glow = Image.new("L", (W, H), 0); gd = ImageDraw.Draw(glow)
    gcx, gcy = W*0.5, by + board_px*0.5
    maxr = m.hypot(W, H)*0.5
    for i in range(60):
        t = i/60; r = maxr*(1-t)
        gd.ellipse([gcx-r, gcy-r, gcx+r, gcy+r], fill=int(60*t))
    glow = glow.filter(ImageFilter.GaussianBlur(40*S))
    img = Image.composite(Image.new("RGB", (W, H), (66, 66, 70)), img, glow)

    # ---- board (skip the selected 2x2 -> sockets) ----
    sel = {(SR, SC), (SR, SC+1), (SR+1, SC), (SR+1, SC+1)}
    d = ImageDraw.Draw(img, "RGBA")
    for r in range(N):
        for c in range(N):
            x, y = bx + c*step, by + r*step
            if (r, c) in sel:
                draw_socket(x, y, cell); continue
            v = GRID[r][c]
            (draw_solved if v == 0 else draw_active)(d, x, y, cell, cell, *( () if v == 0 else (PAL[v],) ))

    # ---- selector caught mid-rotation: 4 tiles lifted + turned 45 deg ----
    cxs = bx + SC*step + cell + gap/2
    cys = by + SR*step + cell + gap/2
    sign = 1 if SDIR == 0 else -1          # +1 = CW (clockwise on screen)
    a = m.radians(45)*sign
    tile_spin = -45*sign                   # PIL rotate is CCW-positive
    def rot(dx, dy):
        ca, sa = m.cos(a), m.sin(a)
        return dx*ca - dy*sa, dx*sa + dy*ca
    half = step/2
    tiles = [((-half, -half), GRID[SR][SC]),    ((half, -half), GRID[SR][SC+1]),
             ((half, half),  GRID[SR+1][SC+1]), ((-half, half), GRID[SR+1][SC])]
    rendered = []
    for (dx, dy), col in tiles:
        rx, ry = rot(dx, dy)
        rt = make_tile(PAL[col], int(cell)).rotate(tile_spin, resample=Image.BICUBIC, expand=True)
        rendered.append((rt, int(cxs+rx-rt.width/2), int(cys+ry-rt.height/2)))

    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    for rt, px, py in rendered:
        shadow.paste(Image.new("RGBA", rt.size, (0, 0, 0, 130)),
                     (px+int(5*S), py+int(8*S)), rt.split()[3])
    shadow = shadow.filter(ImageFilter.GaussianBlur(6*S))
    img = Image.alpha_composite(img.convert("RGBA"), shadow).convert("RGB")
    for rt, px, py in rendered:
        img.paste(rt, (px, py), rt)

    corners = [rot(-half-cell*0.1, -half-cell*0.1), rot(half+cell*0.1, -half-cell*0.1),
               rot(half+cell*0.1, half+cell*0.1), rot(-half-cell*0.1, half+cell*0.1)]
    poly = [(cxs+x, cys+y) for x, y in corners]
    lw = max(2, int(3*S*1.4))
    glowsel = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(glowsel).polygon(poly, outline=(255, 255, 255, 170), width=lw+int(6*S))
    glowsel = glowsel.filter(ImageFilter.GaussianBlur(6*S))
    img = Image.alpha_composite(img.convert("RGBA"), glowsel).convert("RGB")
    d = ImageDraw.Draw(img, "RGBA")
    d.polygon(poly, outline=(255, 255, 255, 255), width=lw)

    ar = step*0.32
    lo, hi = (-60, 200) if sign == 1 else (-20, 240)   # ~260 deg loop with a gap
    d.arc([cxs-ar, cys-ar, cxs+ar, cys+ar], start=lo, end=hi,
          fill=(255, 255, 255, 235), width=max(2, int(3*S)))
    # arrowhead at the swept end, aligned to the arc's tangent (PIL angles go CW)
    head = m.radians(hi if sign == 1 else lo)
    hx, hy = cxs+ar*m.cos(head), cys+ar*m.sin(head)
    tang = (-m.sin(head), m.cos(head))                 # CW tangent (increasing angle)
    ux, uy = (tang if sign == 1 else (-tang[0], -tang[1]))
    nx, ny = -uy, ux                                   # perpendicular
    L, Wd = step*0.26, step*0.14
    # base midpoint sits on the arc end (hx,hy); tip points L ahead along the tangent
    tip = (hx+ux*L, hy+uy*L)
    d.polygon([tip, (hx+nx*Wd, hy+ny*Wd), (hx-nx*Wd, hy-ny*Wd)], fill=(255, 255, 255, 235))

    # ---- title + subtitle ----
    title_f, sub_f = font(t_size), font(s_size)
    widths = [title_f.getlength(ch) for ch in title]
    total = sum(widths) + spacing*(len(title)-1)
    pen, ty = W*0.5 - total/2, top
    off = max(2, t_size*0.03)
    for i, ch in enumerate(title):
        d.text((pen+off, ty+off), ch, font=title_f, fill=(0, 0, 0, 160))
        d.text((pen, ty), ch, font=title_f, fill=(245, 245, 250))
        pen += widths[i] + spacing
    sw2 = sub_f.getlength(sub)
    sy = top + t_size + gap_ts
    d.text((W*0.5-sw2/2+2*S, sy+2*S), sub, font=sub_f, fill=(0, 0, 0, 140))
    d.text((W*0.5-sw2/2, sy), sub, font=sub_f, fill=(150, 150, 170))

    img.resize((out_w, out_h), Image.LANCZOS).save(path)
    print("saved", path, f"{out_w}x{out_h}")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
render(512, 512, os.path.join(ROOT, "thumbnail_square.png"))
render(910, 512, os.path.join(ROOT, "thumbnail_wide.png"))
render(512, 910, os.path.join(ROOT, "thumbnail_tall.png"))
