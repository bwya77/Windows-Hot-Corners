"""Generate Hot Corners app icons.

Design: a rounded "screen" with the top-left quadrant lit up in warm orange,
the other three quadrants resting in cool blue. Same blue palette as the
in-app screen preview so the icon and the settings UI feel related. Reads
clearly from 16 px (where it's basically "blue tile with hot corner") all
the way up to 256 px (where the inner divider lines and gloss highlights
add depth).

Outputs:
    assets/icon.ico         - multi-size icon (16, 20, 24, 32, 40, 48, 64, 128, 256)
    assets/icon.png         - 512x512 standalone PNG for README/marketing
    assets/icon-tray.ico    - 16/20/24/32 only, optimized for the system tray
"""
from __future__ import annotations
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# Colors (sRGB)
BLUE_TL = (0x3A, 0x4C, 0x82)   # outer "screen" gradient start
BLUE_BR = (0x8D, 0xA3, 0xD6)   # outer "screen" gradient end
HOT_TL  = (0xFF, 0x6E, 0x2E)   # hot corner gradient start (saturated orange)
HOT_BR  = (0xFF, 0x3D, 0x1A)   # hot corner gradient end (deep red-orange)
HIGHLIGHT = (255, 255, 255, 38)
DIVIDER  = (255, 255, 255, 22)

OUT = Path(__file__).resolve().parent.parent / "assets"
OUT.mkdir(parents=True, exist_ok=True)


def diagonal_gradient(size: int, c1: tuple, c2: tuple) -> Image.Image:
    yy, xx = np.indices((size, size))
    t = (xx + yy) / max(1, 2 * (size - 1))
    arr = np.empty((size, size, 3), dtype=np.uint8)
    for i in range(3):
        arr[..., i] = np.clip(c1[i] + (c2[i] - c1[i]) * t, 0, 255).astype(np.uint8)
    return Image.fromarray(arr, mode="RGB")


def rounded_mask(size: int, radius: int) -> Image.Image:
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size - 1, size - 1), radius=radius, fill=255)
    return m


def render(size: int, *, with_divider: bool, with_highlight: bool) -> Image.Image:
    scale = 4
    s = size * scale
    radius = max(1, int(s * 0.205))

    canvas = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # Outer rounded blue "screen".
    blue = diagonal_gradient(s, BLUE_TL, BLUE_BR).convert("RGBA")
    canvas.paste(blue, (0, 0), rounded_mask(s, radius))

    # Hot top-left quadrant.
    hot = diagonal_gradient(s, HOT_TL, HOT_BR).convert("RGBA")
    half = s // 2
    quadrant = Image.new("L", (s, s), 0)
    ImageDraw.Draw(quadrant).rectangle((0, 0, half, half), fill=255)
    qm = np.minimum(np.array(quadrant), np.array(rounded_mask(s, radius)))
    canvas.paste(hot, (0, 0), Image.fromarray(qm, mode="L"))

    draw = ImageDraw.Draw(canvas, "RGBA")

    if with_divider:
        line_w = max(1, int(s * 0.008))
        draw.line([(half, max(1, int(s * 0.04))), (half, s - max(1, int(s * 0.04)))],
                  fill=DIVIDER, width=line_w)
        draw.line([(max(1, int(s * 0.04)), half), (s - max(1, int(s * 0.04)), half)],
                  fill=DIVIDER, width=line_w)

    if with_highlight:
        gloss = Image.new("RGBA", (s, s), (0, 0, 0, 0))
        gd = ImageDraw.Draw(gloss)
        gd.rounded_rectangle(
            (int(s * 0.06), int(s * 0.05), s - int(s * 0.06), int(s * 0.18)),
            radius=int(s * 0.06), fill=HIGHLIGHT,
        )
        gloss = gloss.filter(ImageFilter.GaussianBlur(radius=s * 0.018))
        canvas = Image.alpha_composite(canvas, gloss)

    # Outer 1px-ish inner stroke for definition.
    edge = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle(
        (0, 0, s - 1, s - 1),
        radius=radius,
        outline=(255, 255, 255, 36),
        width=max(1, int(s * 0.006)),
    )
    canvas = Image.alpha_composite(canvas, edge)

    return canvas.resize((size, size), Image.LANCZOS)


def build_ico(sizes: list[int], path: Path):
    images = [render(sz, with_divider=sz >= 32, with_highlight=sz >= 96) for sz in sizes]
    base = max(images, key=lambda im: im.size[0])
    base.save(
        path,
        format="ICO",
        sizes=[im.size for im in images],
        append_images=[im for im in images if im is not base],
    )


def main():
    full_sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    tray_sizes = [16, 20, 24, 32]

    print(f"Writing {OUT / 'icon.ico'}")
    build_ico(full_sizes, OUT / "icon.ico")

    print(f"Writing {OUT / 'icon-tray.ico'}")
    build_ico(tray_sizes, OUT / "icon-tray.ico")

    print(f"Writing {OUT / 'icon.png'} (512)")
    render(512, with_divider=True, with_highlight=True).save(OUT / "icon.png")

    for sz in (256, 128, 64, 32):
        p = OUT / f"icon-{sz}.png"
        print(f"Writing {p}")
        render(sz, with_divider=sz >= 32, with_highlight=sz >= 96).save(p)


if __name__ == "__main__":
    main()
