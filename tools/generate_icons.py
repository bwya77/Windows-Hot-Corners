"""Generate Hot Corners app icons — final design.

Design: a rounded "monitor" screen (light blue gradient) with a blue
quarter-circle hot-zone in each of the four corners. One mark per corner
makes the "hot corners" concept literal at every size.

Outputs:
    assets/icon.ico         - multi-size (16, 20, 24, 32, 40, 48, 64, 128, 256)
    assets/icon-tray.ico    - 16/20/24/32 only, optimized for the system tray
    assets/icon.png         - 512x512 PNG for README / marketing
    assets/icon-{32,64,128,256}.png  - per-size PNGs for docs
"""
from __future__ import annotations
import os
from pathlib import Path
from PIL import Image, ImageDraw, ImageChops, ImageFilter

OUT = Path(__file__).resolve().parent.parent / "assets"
OUT.mkdir(parents=True, exist_ok=True)

# --- Palette ---
SCREEN_TL = (230, 240, 252, 255)
SCREEN_BR = (196, 218, 242, 255)
WEDGE_TL  = (108, 168, 248, 255)
WEDGE_BR  = (28, 80, 196, 255)
SHADOW    = (24, 66, 120)
WEDGE_SHADOW = (14, 40, 110)

# --- primitives ---
def rmask(box, radius, size):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=radius, fill=255)
    return m

def clip(layer, mask):
    out = Image.new("RGBA", layer.size, (0, 0, 0, 0))
    out.paste(layer, (0, 0), mask)
    return out

def vgrad(size, top, bot):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    for y in range(size):
        t = y / max(1, size - 1)
        d.line([(0, y), (size, y)], fill=tuple(int(top[k] + (bot[k] - top[k]) * t) for k in range(4)))
    return g

def dgrad(size, tl, br):
    g = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    px = g.load()
    denom = max(1, 2 * (size - 1))
    for y in range(size):
        for x in range(size):
            t = (x + y) / denom
            px[x, y] = tuple(int(tl[k] + (br[k] - tl[k]) * t) for k in range(4))
    return g

def colored_shadow(mask, off, blur, alpha, tint, size):
    a = mask.point(lambda v: int(v * alpha / 255))
    sh = Image.new("RGBA", (size, size), tint + (0,))
    sh.putalpha(a)
    return ImageChops.offset(sh, off[0], off[1]).filter(ImageFilter.GaussianBlur(blur))

def quarter_mask(box, corner, radius, size):
    L, T, R, B = box
    if corner == "tl":   cx, cy, start, end = L, T, 0, 90
    elif corner == "tr": cx, cy, start, end = R, T, 90, 180
    elif corner == "bl": cx, cy, start, end = L, B, 270, 360
    else:                cx, cy, start, end = R, B, 180, 270
    bbox = [cx - radius, cy - radius, cx + radius, cy + radius]
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).pieslice(bbox, start=start, end=end, fill=255)
    return m

# --- render ---
def render(size: int) -> Image.Image:
    """Render the icon at native `size`. Renders at 4x and downsamples for AA."""
    SS = 4 if size <= 128 else 2
    S = size * SS

    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # screen body
    mx, my = int(S * 0.10), int(S * 0.135)
    box = [mx, my, S - mx, S - my]
    r = int(S * 0.165)
    mask = rmask(box, r, S)
    img.alpha_composite(colored_shadow(mask, (0, int(S * 0.028)), int(S * 0.035), 80, SHADOW, S))
    img.alpha_composite(clip(vgrad(S, SCREEN_TL, SCREEN_BR), mask))
    # top sheen
    sheen = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(sheen).rounded_rectangle(
        [box[0], box[1], box[2], box[1] + int(S * 0.30)], radius=r, fill=(255, 255, 255, 40)
    )
    img.alpha_composite(clip(sheen, mask))

    # four blue quarter-circle hot zones
    wedge_r = int((box[2] - box[0]) * 0.40)
    for corner in ("tl", "tr", "bl", "br"):
        qm = quarter_mask(box, corner, wedge_r, S)
        qm = ImageChops.multiply(qm, mask)  # respect rounded-screen edges
        img.alpha_composite(colored_shadow(qm, (0, int(S * 0.010)), int(S * 0.014), 120, WEDGE_SHADOW, S))
        img.alpha_composite(clip(dgrad(S, WEDGE_TL, WEDGE_BR), qm))

    if S != size:
        img = img.resize((size, size), Image.LANCZOS)
    return img

# --- write outputs ---
def main():
    ico_sizes  = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    tray_sizes = [16, 20, 24, 32]
    png_sizes  = [32, 64, 128, 256]

    print("Rendering masters...")
    masters = {s: render(s) for s in sorted(set(ico_sizes + tray_sizes + png_sizes + [512]))}

    # main .ico (multi-size)
    base = masters[max(ico_sizes)]
    extras = [masters[s] for s in ico_sizes if s != max(ico_sizes)]
    ico_path = OUT / "icon.ico"
    base.save(ico_path, format="ICO", sizes=[(s, s) for s in ico_sizes], append_images=extras)
    print(f"  {ico_path}  ({os.path.getsize(ico_path):,} bytes)")

    # tray-only .ico
    tbase = masters[max(tray_sizes)]
    textras = [masters[s] for s in tray_sizes if s != max(tray_sizes)]
    tray_path = OUT / "icon-tray.ico"
    tbase.save(tray_path, format="ICO", sizes=[(s, s) for s in tray_sizes], append_images=textras)
    print(f"  {tray_path}  ({os.path.getsize(tray_path):,} bytes)")

    # PNGs for docs / marketing
    masters[512].save(OUT / "icon.png")
    for s in png_sizes:
        masters[s].save(OUT / f"icon-{s}.png")
    print(f"  PNGs: icon.png + icon-{{{','.join(map(str, png_sizes))}}}.png")

    print("Done.")

if __name__ == "__main__":
    main()
