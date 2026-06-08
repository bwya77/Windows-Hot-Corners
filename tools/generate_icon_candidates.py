"""Generate several Hot Corners icon candidates side-by-side for review.

Each candidate is rendered at 256 for the hero plus tray sizes (16/24/32/48)
on both dark and light backgrounds, mirroring the Swoosh preview sheets.

Run:  python tools/generate_icon_candidates.py
Outputs to: tools/candidates/*.png and a contact sheet at the workspace root.
"""
import os
import math
from PIL import Image, ImageDraw, ImageChops, ImageFilter, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(REPO, "tools", "candidates")
WS = r"C:\Users\bradleywyatt\OneDrive - Microsoft\Documents\Microsoft Scout"
os.makedirs(OUT_DIR, exist_ok=True)

M = 1024  # master render size

# ---- Shared palette (matches the in-app preview gradient + a warm hot color) ----
# Cool screen gradient
SCREEN_TL = (230, 240, 252, 255)
SCREEN_BR = (196, 218, 242, 255)
# Deep blue used for "window" / accent panels
BLUE_LT   = (108, 168, 248, 255)
BLUE_DK   = (40, 92, 196, 255)
# Hot (the lit corner) — warm orange-red
HOT_LT    = (255, 168, 80, 255)
HOT_DK    = (255, 86, 38, 255)
HOT_GLOW  = (255, 120, 50)
NAVY      = (12, 60, 130)
INK       = (24, 32, 52, 255)

# ----------- helpers (same primitives as Swoosh) -----------
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

def radial_grad(size, center, inner, outer, radius):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0)); px = g.load()
    cx, cy = center
    for y in range(size):
        for x in range(size):
            d = math.hypot(x - cx, y - cy) / radius
            d = min(max(d, 0.0), 1.0)
            px[x, y] = tuple(int(inner[k] + (outer[k] - inner[k]) * d) for k in range(4))
    return g

def colored_shadow(mask, off, blur, alpha, tint, size=M):
    a = mask.point(lambda v: int(v * alpha / 255))
    sh = Image.new("RGBA", (size, size), tint + (0,)); sh.putalpha(a)
    return ImageChops.offset(sh, off[0], off[1]).filter(ImageFilter.GaussianBlur(blur))

def screen_base(size=M, body_top=SCREEN_TL, body_bot=SCREEN_BR, with_sheen=True):
    """Return (img_with_screen, screen_box, screen_mask, corner_radius)."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    mx, my = int(size * 0.10), int(size * 0.135)
    box = [mx, my, size - mx, size - my]
    r = int(size * 0.165)
    mask = rmask(box, r, size)
    img.alpha_composite(colored_shadow(mask, (0, int(size * 0.028)), int(size * 0.035), 80, (24, 66, 120), size))
    img.alpha_composite(clip(vgrad(size, body_top, body_bot), mask))
    if with_sheen:
        sheen = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        ImageDraw.Draw(sheen).rounded_rectangle(
            [box[0], box[1], box[2], box[1] + int(size * 0.30)], radius=r, fill=(255, 255, 255, 40)
        )
        img.alpha_composite(clip(sheen, mask))
    return img, box, mask, r

# ============================================================
# Candidate A — Quadrant: top-left quadrant lit orange
# ============================================================
def candidate_quadrant(size=M):
    img, box, mask, r = screen_base(size)
    L, T, R, B = box
    midx = (L + R) // 2
    midy = (T + B) // 2
    # hot quadrant
    qmask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(qmask).rectangle([L, T, midx, midy], fill=255)
    qmask = ImageChops.multiply(qmask, mask)
    img.alpha_composite(colored_shadow(qmask, (0, int(size * 0.012)), int(size * 0.020), 110, (180, 50, 0), size))
    img.alpha_composite(clip(dgrad(size, HOT_LT, HOT_DK), qmask))
    # divider lines
    if size >= 32:
        d = ImageDraw.Draw(img)
        lw = max(1, size // 256)
        d.line([(midx, T + int(size * 0.02)), (midx, B - int(size * 0.02))], fill=(255, 255, 255, 90), width=lw)
        d.line([(L + int(size * 0.02), midy), (R - int(size * 0.02), midy)], fill=(255, 255, 255, 90), width=lw)
    return img

# ============================================================
# Candidate B — Cursor in corner: a glowing dot/cursor approaching top-left
# ============================================================
def candidate_cursor(size=M):
    img, box, mask, r = screen_base(size)
    L, T, R, B = box
    # warm glow radiating from top-left corner
    glow_radius = int((R - L) * 0.55)
    glow = radial_grad(size, (L + int(size * 0.03), T + int(size * 0.03)),
                       HOT_LT, (HOT_DK[0], HOT_DK[1], HOT_DK[2], 0), glow_radius)
    img.alpha_composite(clip(glow, mask))
    # cursor arrow sitting in the corner
    d = ImageDraw.Draw(img)
    cx, cy = L + int(size * 0.075), T + int(size * 0.075)
    s = int(size * 0.13)
    # arrow body (classic Windows cursor shape, rotated to point up-left)
    pts = [
        (cx,             cy),
        (cx + s,         cy + int(s * 0.45)),
        (cx + int(s*0.55), cy + int(s*0.55)),
        (cx + int(s*0.80), cy + s),
        (cx + int(s*0.60), cy + int(s*1.05)),
        (cx + int(s*0.40), cy + int(s*0.78)),
        (cx + int(s*0.05), cy + s),
    ]
    d.polygon(pts, fill=(255, 255, 255, 255), outline=INK)
    # tiny ping ring
    if size >= 48:
        rr = int(size * 0.21)
        for i, alpha in enumerate([150, 70]):
            off = i * int(size * 0.04)
            d.ellipse([cx - rr - off, cy - rr - off, cx + rr + off, cy + rr + off],
                      outline=(255, 200, 120, alpha), width=max(1, size // 220))
    return img

# ============================================================
# Candidate C — Corner ribbon: a bright orange "L" hugging the TL corner
# ============================================================
def candidate_ribbon(size=M):
    img, box, mask, r = screen_base(size)
    L, T, R, B = box
    w = int((R - L) * 0.40)   # length of each ribbon arm
    th = int((R - L) * 0.15)  # thickness
    # ribbon shape: two rounded rects forming an L, with the elbow rounded into the corner radius
    ribbon = Image.new("L", (size, size), 0)
    rd = ImageDraw.Draw(ribbon)
    rd.rounded_rectangle([L, T, L + w, T + th], radius=th // 2, fill=255)     # horizontal arm
    rd.rounded_rectangle([L, T, L + th, T + w], radius=th // 2, fill=255)     # vertical arm
    # clip to screen so it hugs the rounded corner cleanly
    ribbon = ImageChops.multiply(ribbon, mask)
    img.alpha_composite(colored_shadow(ribbon, (0, int(size * 0.012)), int(size * 0.018), 140, (180, 50, 0), size))
    img.alpha_composite(clip(dgrad(size, HOT_LT, HOT_DK), ribbon))
    # tiny dots in the other 3 corners to imply "available corners"
    if size >= 48:
        d = ImageDraw.Draw(img)
        dot = max(2, int(size * 0.022))
        pad = int(size * 0.045)
        for cx, cy in [(R - pad, T + pad), (L + pad, B - pad), (R - pad, B - pad)]:
            d.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=(80, 110, 160, 200))
    return img

# ============================================================
# Candidate D — Four dots: one big orange dot TL, three small navy elsewhere
# ============================================================
def candidate_fourdots(size=M):
    img, box, mask, r = screen_base(size)
    L, T, R, B = box
    pad = int(size * 0.085)
    big = int(size * 0.085)
    small = int(size * 0.045)
    d = ImageDraw.Draw(img)
    # active corner: big orange dot with halo
    cx, cy = L + pad, T + pad
    halo = int(size * 0.18)
    glow = radial_grad(size, (cx, cy), (255, 140, 70, 180), (255, 86, 38, 0), halo)
    img.alpha_composite(clip(glow, mask))
    d.ellipse([cx - big, cy - big, cx + big, cy + big], fill=(255, 86, 38, 255), outline=(180, 40, 10, 220), width=max(1, size // 220))
    # inactive corners: small navy dots
    for cx2, cy2 in [(R - pad, T + pad), (L + pad, B - pad), (R - pad, B - pad)]:
        d.ellipse([cx2 - small, cy2 - small, cx2 + small, cy2 + small],
                  fill=(70, 100, 156, 230), outline=(28, 56, 110, 230), width=max(1, size // 220))
    return img

# ============================================================
# Candidate E — Diagonal split: a triangle of the TL corner is hot
# ============================================================
def candidate_diagonal(size=M):
    img, box, mask, r = screen_base(size)
    L, T, R, B = box
    # diagonal triangle cutting the top-left corner
    tri = Image.new("L", (size, size), 0)
    rd = ImageDraw.Draw(tri)
    # cut ratio — about 1/3 of the way along each axis
    cut = 0.40
    pts = [(L, T), (L + int((R - L) * cut), T), (L, T + int((B - T) * cut))]
    rd.polygon(pts, fill=255)
    tri = ImageChops.multiply(tri, mask)
    img.alpha_composite(colored_shadow(tri, (0, int(size * 0.012)), int(size * 0.018), 130, (180, 50, 0), size))
    img.alpha_composite(clip(dgrad(size, HOT_LT, HOT_DK), tri))
    # diagonal seam highlight
    if size >= 32:
        d = ImageDraw.Draw(img)
        lw = max(1, size // 220)
        d.line([(L + int((R - L) * cut), T), (L, T + int((B - T) * cut))],
               fill=(255, 255, 255, 140), width=lw)
    return img

# ============================================================
# Candidate F — Floating window pulled into the corner (Swoosh DNA)
# ============================================================
def candidate_windowflick(size=M):
    img, box, mask, r = screen_base(size)
    L, T, R, B = box
    # a small blue "window" anchored to the top-left, with rounded corners
    inset = int(size * 0.06)
    cw = int((R - L) * 0.50)
    ch = int((B - T) * 0.50)
    cbox = [L + inset, T + inset, L + inset + cw, T + inset + ch]
    cr = int(size * 0.07)
    cardmask = rmask(cbox, cr, size)
    img.alpha_composite(colored_shadow(cardmask, (int(size * 0.012), int(size * 0.018)),
                                       int(size * 0.024), 170, NAVY, size))
    img.alpha_composite(clip(dgrad(size, BLUE_LT, BLUE_DK), cardmask))
    # window titlebar dots
    if size >= 64:
        d = ImageDraw.Draw(img)
        dot = max(2, int(size * 0.012))
        y = cbox[1] + int(size * 0.022)
        x = cbox[0] + int(size * 0.025)
        for i, c in enumerate([(255, 95, 86, 255), (255, 189, 46, 255), (39, 201, 63, 255)]):
            d.ellipse([x - dot, y - dot, x + dot, y + dot], fill=c)
            x += int(size * 0.032)
    # hot-corner accent: small orange dot in the very TL corner of the screen
    d = ImageDraw.Draw(img)
    px, py = L + int(size * 0.035), T + int(size * 0.035)
    rad = int(size * 0.028)
    glow = radial_grad(size, (px, py), (255, 150, 80, 220), (255, 86, 38, 0), int(size * 0.10))
    img.alpha_composite(clip(glow, mask))
    d.ellipse([px - rad, py - rad, px + rad, py + rad], fill=(255, 86, 38, 255))
    return img

# ============================================================
# Render & contact sheet
# ============================================================
CANDIDATES = [
    ("A_quadrant",     "Quadrant lit",        candidate_quadrant),
    ("B_cursor",       "Cursor in corner",    candidate_cursor),
    ("C_ribbon",       "Corner ribbon (L)",   candidate_ribbon),
    ("D_fourdots",     "Four corner dots",    candidate_fourdots),
    ("E_diagonal",     "Diagonal slice",      candidate_diagonal),
    ("F_windowflick",  "Window + hot dot",    candidate_windowflick),
]

def checker(w, h, c=16):
    bg = Image.new("RGBA", (w, h), (255, 255, 255, 255)); dr = ImageDraw.Draw(bg)
    for y in range(0, h, c):
        for x in range(0, w, c):
            if (x // c + y // c) % 2 == 0:
                dr.rectangle([x, y, x + c, y + c], fill=(216, 220, 226, 255))
    return bg

try:
    font_h = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 28)
    font_b = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 20)
    font_s = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 16)
except Exception:
    font_h = font_b = font_s = ImageFont.load_default()

# Render each master and save per-candidate previews
masters = {}
for key, label, fn in CANDIDATES:
    m = fn()
    masters[key] = m
    m.save(os.path.join(OUT_DIR, f"{key}_master.png"))
    # individual preview tile (hero + tray sizes on dark + light)
    tile = Image.new("RGBA", (760, 360), (245, 247, 251, 255))
    tile.alpha_composite(m.resize((256, 256), Image.LANCZOS), (28, 52))
    d = ImageDraw.Draw(tile)
    d.text((300, 24), label, font=font_h, fill=(20, 22, 28, 255))
    def strip(bgc, y0):
        s = Image.new("RGBA", (380, 70), bgc); x = 16
        for sz in [16, 24, 32, 48]:
            s.alpha_composite(m.resize((sz, sz), Image.LANCZOS), (x, (70 - sz) // 2))
            x += sz + 18
        tile.alpha_composite(s, (320, y0))
    strip((38, 38, 44, 255), 100)
    strip((234, 236, 240, 255), 190)
    d.text((320, 275), "tray @ 16/24/32/48 — dark + light", font=font_s, fill=(70, 74, 82, 255))
    tile.convert("RGB").save(os.path.join(OUT_DIR, f"{key}_preview.png"))

# Big contact sheet: 2 columns x 3 rows
cell_w, cell_h = 780, 380
pad = 24
cols, rows = 2, 3
sheet_w = cols * cell_w + (cols + 1) * pad
sheet_h = rows * cell_h + (rows + 1) * pad + 80
sheet = Image.new("RGBA", (sheet_w, sheet_h), (250, 251, 254, 255))
ds = ImageDraw.Draw(sheet)
ds.text((pad, 24), "Hot Corners — icon candidates", font=font_h, fill=(18, 22, 36, 255))
ds.text((pad, 56), "256 hero + 16/24/32/48 tray sizes on dark and light backgrounds.",
        font=font_b, fill=(70, 80, 100, 255))

for i, (key, label, _) in enumerate(CANDIDATES):
    col = i % cols
    row = i // cols
    x = pad + col * (cell_w + pad)
    y = 80 + pad + row * (cell_h + pad)
    # frame
    ds.rounded_rectangle([x, y, x + cell_w, y + cell_h], radius=14,
                         fill=(255, 255, 255, 255), outline=(220, 224, 232, 255), width=1)
    m = masters[key]
    sheet.alpha_composite(m.resize((256, 256), Image.LANCZOS), (x + 24, y + 64))
    ds.text((x + 300, y + 24), f"{key.split('_')[0]} — {label}", font=font_h, fill=(20, 22, 28, 255))
    # tray strips
    def strip(bgc, y0):
        s = Image.new("RGBA", (440, 64), bgc); xx = 16
        for sz in [16, 24, 32, 48]:
            s.alpha_composite(m.resize((sz, sz), Image.LANCZOS), (xx, (64 - sz) // 2))
            xx += sz + 22
        sheet.alpha_composite(s, (x + 300, y + y0))
    strip((38, 38, 44, 255), 100)
    strip((234, 236, 240, 255), 180)
    ds.text((x + 300, y + 254), "tray @ 16 / 24 / 32 / 48", font=font_s, fill=(70, 74, 82, 255))

out_sheet = os.path.join(OUT_DIR, "candidates_contact_sheet.png")
sheet.convert("RGB").save(out_sheet)
# also drop a copy in the Microsoft Scout workspace so it renders inline in chat
ws_copy = os.path.join(WS, "hotcorners-icon-candidates.png")
sheet.convert("RGB").save(ws_copy)

# per-candidate 256 hero PNGs into the workspace so each can render individually
for key, _, _ in CANDIDATES:
    masters[key].resize((256, 256), Image.LANCZOS).save(
        os.path.join(WS, f"hotcorners-icon-{key}.png")
    )

print(f"Wrote {len(CANDIDATES)} candidates")
print(f"Contact sheet: {out_sheet}")
print(f"Inline preview: {ws_copy}")
