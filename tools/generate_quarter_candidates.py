"""Hot Corners — quarter-circle hot-zone candidates.

Monitor = rounded square. Each of the four corners gets a quarter-circle
wedge representing its hot zone. Variants explore size, color, and whether
all four are active or one is highlighted.

Run: python tools/generate_quarter_candidates.py
"""
import os
import math
from PIL import Image, ImageDraw, ImageChops, ImageFilter, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "tools", "candidates")
WS = r"C:\Users\bradleywyatt\OneDrive - Microsoft\Documents\Microsoft Scout"
os.makedirs(OUT_DIR, exist_ok=True)

M = 1024

# screen body
SCREEN_TL = (230, 240, 252, 255)
SCREEN_BR = (196, 218, 242, 255)
# wedge palettes
HOT_LT    = (255, 168, 80, 255)
HOT_DK    = (255, 70, 28, 255)
BLUE_LT   = (108, 168, 248, 255)
BLUE_DK   = (28, 80, 196, 255)
TEAL_LT   = (102, 220, 212, 255)
TEAL_DK   = (20, 158, 168, 255)
PURPLE_LT = (180, 130, 240, 255)
PURPLE_DK = (102, 56, 196, 255)
GREEN_LT  = (138, 224, 130, 255)
GREEN_DK  = (40, 156, 64, 255)
INK       = (24, 32, 52, 255)

# -------- primitives --------
def rmask(box, radius, size=M):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m

def clip(layer, mask):
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0)); out.paste(layer, (0, 0), mask); return out

def vgrad(size, top, bot):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0)); d = ImageDraw.Draw(g)
    for y in range(size):
        t = y / max(1, size - 1)
        d.line([(0, y), (size, y)], fill=tuple(int(top[k] + (bot[k] - top[k]) * t) for k in range(4)))
    return g

def dgrad(size, tl, br):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0)); px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = tuple(int(tl[k] + (br[k] - tl[k]) * t) for k in range(4))
    return g

def colored_shadow(mask, off, blur, alpha, tint, size=M):
    a = mask.point(lambda v: int(v * alpha / 255))
    sh = Image.new("RGBA", (size, size), tint + (0,)); sh.putalpha(a)
    return ImageChops.offset(sh, off[0], off[1]).filter(ImageFilter.GaussianBlur(blur))

def screen_base(size=M):
    """Returns (img, screen_box, screen_mask)."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    mx, my = int(size * 0.10), int(size * 0.135)
    box = [mx, my, size - mx, size - my]
    r = int(size * 0.165)
    mask = rmask(box, r, size)
    img.alpha_composite(colored_shadow(mask, (0, int(size * 0.028)), int(size * 0.035), 80, (24, 66, 120), size))
    img.alpha_composite(clip(vgrad(size, SCREEN_TL, SCREEN_BR), mask))
    # gentle sheen
    sheen = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(sheen).rounded_rectangle(
        [box[0], box[1], box[2], box[1] + int(size * 0.30)], radius=r, fill=(255, 255, 255, 40)
    )
    img.alpha_composite(clip(sheen, mask))
    return img, box, mask

def quarter_mask(box, corner, radius, size=M):
    """corner in {'tl','tr','bl','br'}. Returns L mask of the wedge."""
    L, T, R, B = box
    if corner == "tl":
        cx, cy = L, T
        bbox = [cx - radius, cy - radius, cx + radius, cy + radius]
        start, end = 0, 90
    elif corner == "tr":
        cx, cy = R, T
        bbox = [cx - radius, cy - radius, cx + radius, cy + radius]
        start, end = 90, 180
    elif corner == "bl":
        cx, cy = L, B
        bbox = [cx - radius, cy - radius, cx + radius, cy + radius]
        start, end = 270, 360
    else:  # br
        cx, cy = R, B
        bbox = [cx - radius, cy - radius, cx + radius, cy + radius]
        start, end = 180, 270
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).pieslice(bbox, start=start, end=end, fill=255)
    return m

def quarter_ring_mask(box, corner, outer_r, inner_r, size=M):
    """Hollow quarter-ring (arc band) at the given corner."""
    outer = quarter_mask(box, corner, outer_r, size)
    inner = quarter_mask(box, corner, inner_r, size)
    return ImageChops.subtract(outer, inner)

def draw_wedge(img, box, mask, corner, radius, tl_color, br_color, *,
               with_shadow=True, shadow_alpha=130, size=M):
    qm = quarter_mask(box, corner, radius, size)
    qm = ImageChops.multiply(qm, mask)  # clip to screen rounding
    if with_shadow:
        img.alpha_composite(colored_shadow(qm, (0, int(size * 0.010)), int(size * 0.014),
                                           shadow_alpha, (br_color[0] // 2, br_color[1] // 4, br_color[2] // 4), size))
    img.alpha_composite(clip(dgrad(size, tl_color, br_color), qm))

def draw_wedge_outline(img, box, corner, radius, color, width, size=M):
    L, T, R, B = box
    if corner == "tl":   cx, cy = L, T; start, end = 0, 90
    elif corner == "tr": cx, cy = R, T; start, end = 90, 180
    elif corner == "bl": cx, cy = L, B; start, end = 270, 360
    else:                cx, cy = R, B; start, end = 180, 270
    bbox = [cx - radius, cy - radius, cx + radius, cy + radius]
    d = ImageDraw.Draw(img)
    d.arc(bbox, start=start, end=end, fill=color, width=width)

# ============================================================
# Candidate 1 — Four orange wedges, equal size, all active
# ============================================================
def cand_all_orange(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.40)
    for c in ["tl", "tr", "bl", "br"]:
        draw_wedge(img, box, mask, c, R, HOT_LT, HOT_DK, size=size)
    return img

# ============================================================
# Candidate 2 — Four brand colors (one per corner)
# ============================================================
def cand_four_colors(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.40)
    palettes = {
        "tl": (HOT_LT, HOT_DK),
        "tr": (BLUE_LT, BLUE_DK),
        "br": (PURPLE_LT, PURPLE_DK),
        "bl": (GREEN_LT, GREEN_DK),
    }
    for c, (a, b) in palettes.items():
        draw_wedge(img, box, mask, c, R, a, b, size=size)
    return img

# ============================================================
# Candidate 3 — One active (TL orange), three ghost outlines
# ============================================================
def cand_one_active(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.42)
    draw_wedge(img, box, mask, "tl", R, HOT_LT, HOT_DK, size=size)
    lw = max(2, size // 90)
    for c in ["tr", "bl", "br"]:
        draw_wedge_outline(img, box, c, R, (40, 80, 150, 220), lw, size=size)
    return img

# ============================================================
# Candidate 4 — Four small orange wedges (tighter, less coverage)
# ============================================================
def cand_small_orange(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.28)
    for c in ["tl", "tr", "bl", "br"]:
        draw_wedge(img, box, mask, c, R, HOT_LT, HOT_DK, size=size)
    return img

# ============================================================
# Candidate 5 — Four blue wedges (monochrome, matches screen)
# ============================================================
def cand_all_blue(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.40)
    for c in ["tl", "tr", "bl", "br"]:
        draw_wedge(img, box, mask, c, R, BLUE_LT, BLUE_DK, size=size)
    return img

# ============================================================
# Candidate 6 — Four concentric arc rings (no fill)
# ============================================================
def cand_arc_rings(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.42)
    bandw = int((box[2] - box[0]) * 0.045)
    for c in ["tl", "tr", "bl", "br"]:
        # outer arc band
        m1 = ImageChops.multiply(quarter_ring_mask(box, c, R, R - bandw, size), mask)
        img.alpha_composite(clip(dgrad(size, HOT_LT, HOT_DK), m1))
        # inner arc band
        R2 = int(R * 0.60); b2 = max(2, int(bandw * 0.7))
        m2 = ImageChops.multiply(quarter_ring_mask(box, c, R2, R2 - b2, size), mask)
        img.alpha_composite(clip(dgrad(size, HOT_LT, HOT_DK), m2))
    return img

# ============================================================
# Candidate 7 — Four orange filled + bright corner dot at the apex
# ============================================================
def cand_wedge_plus_dot(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.36)
    for c in ["tl", "tr", "bl", "br"]:
        draw_wedge(img, box, mask, c, R, HOT_LT, HOT_DK, size=size)
    # tiny white dot at each apex for snap
    L, T, RR, B = box
    d = ImageDraw.Draw(img)
    dot = max(2, int(size * 0.012))
    for cx, cy in [(L, T), (RR, T), (L, B), (RR, B)]:
        d.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=(255, 255, 255, 230))
    return img

# ============================================================
# Candidate 8 — Larger wedges meeting in the middle (cross negative space)
# ============================================================
def cand_big_wedges(size=M):
    img, box, mask = screen_base(size)
    R = int((box[2] - box[0]) * 0.48)
    for c in ["tl", "tr", "bl", "br"]:
        draw_wedge(img, box, mask, c, R, HOT_LT, HOT_DK, size=size)
    return img

CANDIDATES = [
    ("Q1_all_orange",   "All-4 orange wedges",      cand_all_orange),
    ("Q2_four_colors",  "Multi-color wedges",       cand_four_colors),
    ("Q3_one_active",   "1 active + 3 ghost",       cand_one_active),
    ("Q4_small_orange", "All-4, smaller wedges",    cand_small_orange),
    ("Q5_all_blue",     "All-4 blue (mono)",        cand_all_blue),
    ("Q6_arc_rings",    "Concentric arc rings",     cand_arc_rings),
    ("Q7_wedge_dot",    "Wedges + apex dot",        cand_wedge_plus_dot),
    ("Q8_big_wedges",   "Wedges meet in middle",    cand_big_wedges),
]

# -------- render + contact sheet --------
try:
    font_h = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 26)
    font_b = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 18)
    font_s = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 15)
except Exception:
    font_h = font_b = font_s = ImageFont.load_default()

masters = {}
for key, label, fn in CANDIDATES:
    m = fn()
    masters[key] = m
    m.save(os.path.join(OUT_DIR, f"{key}_master.png"))
    m.resize((256, 256), Image.LANCZOS).save(os.path.join(WS, f"hotcorners-icon-{key}.png"))

# 2 cols x 4 rows contact sheet
cell_w, cell_h = 780, 380
pad = 24
cols, rows = 2, 4
sheet_w = cols * cell_w + (cols + 1) * pad
sheet_h = rows * cell_h + (rows + 1) * pad + 96
sheet = Image.new("RGBA", (sheet_w, sheet_h), (250, 251, 254, 255))
ds = ImageDraw.Draw(sheet)
ds.text((pad, 24), "Hot Corners — quarter-circle candidates", font=font_h, fill=(18, 22, 36, 255))
ds.text((pad, 58), "Monitor with a quarter-circle hot zone in each corner. 256 hero + 16/24/32/48 tray on dark and light.",
        font=font_b, fill=(70, 80, 100, 255))

for i, (key, label, _) in enumerate(CANDIDATES):
    col = i % cols
    row = i // cols
    x = pad + col * (cell_w + pad)
    y = 96 + pad + row * (cell_h + pad)
    ds.rounded_rectangle([x, y, x + cell_w, y + cell_h], radius=14,
                         fill=(255, 255, 255, 255), outline=(220, 224, 232, 255), width=1)
    m = masters[key]
    sheet.alpha_composite(m.resize((256, 256), Image.LANCZOS), (x + 24, y + 64))
    ds.text((x + 300, y + 24), f"{key.split('_', 1)[0]} — {label}", font=font_h, fill=(20, 22, 28, 255))
    def strip(bgc, y0):
        s = Image.new("RGBA", (440, 64), bgc); xx = 16
        for sz in [16, 24, 32, 48]:
            s.alpha_composite(m.resize((sz, sz), Image.LANCZOS), (xx, (64 - sz) // 2))
            xx += sz + 22
        sheet.alpha_composite(s, (x + 300, y + y0))
    strip((38, 38, 44, 255), 100)
    strip((234, 236, 240, 255), 180)
    ds.text((x + 300, y + 254), "tray @ 16 / 24 / 32 / 48", font=font_s, fill=(70, 74, 82, 255))

out_sheet = os.path.join(OUT_DIR, "quarter_candidates_sheet.png")
sheet.convert("RGB").save(out_sheet)
sheet.convert("RGB").save(os.path.join(WS, "hotcorners-quarter-candidates.png"))

print(f"Wrote {len(CANDIDATES)} candidates")
print(f"Contact sheet: {out_sheet}")
