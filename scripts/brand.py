"""Builds every logo and mascot file from assets/brand.

Run after changing an illustration: python scripts/brand.py
The logo is the vector owl assets/brand/owl.svg (a single path of lines and Bézier curves):
- src/CodexTracker.App/Assets/App.ico: the owl on the dark rounded tile, 16 to 256 px.
- site/icon.png: the same tile at 128 px (favicon, site header, Store screenshots, README).
- installer/Assets/Mark.bmp and Welcome.bmp: the owl instead of the former >_ glyph.
- src/CodexTracker.App/App.xaml: the MascotMain brush draws the same path, so the app shows it as a vector.
The other owls stay drawings: src/CodexTracker.App/Assets/Mascot/*.png are their white silhouettes,
which the app tints with its theme brushes.
"""
import math
import re
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "assets/brand"
LOGO = BRAND / "owl.svg"
MASCOTS = {
    "terminal": "codex_tracker_01_hibou_terminal.png",
    "watching": "codex_tracker_02_hibou_grands_yeux.png",
    "sleeping": "codex_tracker_03_hibou_endormi.png",
    "reading": "codex_tracker_04_hibou_lunettes_code.png",
    "window": "codex_tracker_06_hibou_fenetre_terminal.png",
}
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TILE, EDGE = (32, 32, 32, 255), (73, 73, 73, 255)
INSTALLER_BACKGROUND = (24, 24, 24)
SUPERSAMPLE = 8


def path_data() -> str:
    return re.search(r'\sd="([^"]+)"', LOGO.read_text(encoding="utf-8")).group(1)


def contours() -> list[list[tuple[float, float]]]:
    """The path's closed contours, Bézier curves flattened finely enough for any icon size."""
    tokens = re.findall(r"[MLCQZz]|-?\d*\.?\d+(?:[eE]-?\d+)?", path_data())
    result, current, command, numbers, point = [], [], None, [], (0.0, 0.0)
    arity = {"M": 2, "L": 2, "C": 6, "Q": 4}

    def flush():
        nonlocal current
        if len(current) > 2: result.append(current)
        current = []

    def apply(cmd, values):
        nonlocal point, command
        if cmd == "M":
            flush(); point = (values[0], values[1]); current.append(point); command = "L"
        elif cmd == "L":
            point = (values[0], values[1]); current.append(point)
        else:
            controls = [(values[i], values[i + 1]) for i in range(0, len(values), 2)]
            p = [point, *controls]
            for k in range(1, 25):
                t = k / 24
                if cmd == "C":
                    weights = ((1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3)
                    x = sum(q[0] * w for q, w in zip(p, weights))
                    y = sum(q[1] * w for q, w in zip(p, weights))
                else:
                    x = (1 - t) ** 2 * p[0][0] + 2 * (1 - t) * t * p[1][0] + t * t * p[2][0]
                    y = (1 - t) ** 2 * p[0][1] + 2 * (1 - t) * t * p[1][1] + t * t * p[2][1]
                current.append((x, y))
            point = controls[-1]

    for token in tokens:
        if token in "MLCQZz":
            if token in "Zz": flush(); command = None
            else: command = token
            numbers = []
            continue
        numbers.append(float(token))
        if command and len(numbers) == arity[command]:
            apply(command, numbers); numbers = []
    flush()
    return result


def is_sparkle(contour) -> bool:
    """The sparkle is the contour alone in the top right corner, beside the ear."""
    xs, ys = [p[0] for p in contour], [p[1] for p in contour]
    return min(xs) > 740 and max(ys) < 320


def owl(box: int, sparkle: bool = True) -> Image.Image:
    """The vector owl in white on transparent, its larger side `box` pixels; small icons drop the sparkle."""
    shapes = [c for c in contours() if sparkle or not is_sparkle(c)]
    x0, y0 = min(p[0] for c in shapes for p in c), min(p[1] for c in shapes for p in c)
    x1, y1 = max(p[0] for c in shapes for p in c), max(p[1] for c in shapes for p in c)
    scale = box / max(x1 - x0, y1 - y0)
    ss = max(2, min(16, 4096 // box))
    size = (max(1, math.ceil((x1 - x0) * scale)), max(1, math.ceil((y1 - y0) * scale)))
    mask = Image.new("1", (size[0] * ss, size[1] * ss), 0)
    # Contours never overlap: the owl, then the eyes, beak and sparkle. Even-odd filling cuts the holes.
    for contour in shapes:
        layer = Image.new("1", mask.size, 0)
        ImageDraw.Draw(layer).polygon([((x - x0) * scale * ss, (y - y0) * scale * ss) for x, y in contour], fill=1)
        mask = ImageChops.logical_xor(mask, layer)
    image = Image.new("RGBA", size, (255, 255, 255, 0))
    image.putalpha(mask.convert("L").resize(size, Image.LANCZOS))
    return image


def tile(size: int) -> Image.Image:
    """The former icon's rounded tile (5/64 inset, 12/64 radius, thin edge), with the owl in place of >_."""
    big = size * SUPERSAMPLE
    unit = big / 64
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(canvas).rounded_rectangle((5 * unit, 5 * unit, 59 * unit, 59 * unit), radius=12 * unit,
                                             fill=TILE, outline=EDGE, width=max(1, round(unit)))
    small = size < 32
    drawing = owl(round(big * (0.66 if small else 0.58)), sparkle=not small)
    canvas.alpha_composite(drawing, ((big - drawing.width) // 2, (big - drawing.height) // 2 + round(unit * 0.5)))
    return canvas.resize((size, size), Image.LANCZOS)


def write_icon() -> None:
    frames = [tile(size) for size in ICON_SIZES]
    frames[-1].save(ROOT / "src/CodexTracker.App/Assets/App.ico", format="ICO", sizes=[(s, s) for s in ICON_SIZES],
                    append_images=frames[:-1])
    tile(128).save(ROOT / "site/icon.png", optimize=True)


def write_installer() -> None:
    drawing = owl(64)
    mark = Image.new("RGB", (110, 110), INSTALLER_BACKGROUND)
    mark.paste(drawing, ((110 - drawing.width) // 2, (110 - drawing.height) // 2), drawing)
    mark.save(ROOT / "installer/Assets/Mark.bmp")
    welcome_path = ROOT / "installer/Assets/Welcome.bmp"
    welcome = Image.open(welcome_path).convert("RGB")
    # The former glyph occupied the top left corner, above the "Codex Tracker" title.
    welcome.paste(INSTALLER_BACKGROUND, (24, 24, 120, 100))
    small = owl(58)
    welcome.paste(small, (34, 30), small)
    welcome.save(welcome_path)


def write_app_logo() -> None:
    """The MascotMain brush of App.xaml: the same path, framed in a square like the other illustrations."""
    shapes = contours()
    x0, y0 = min(p[0] for c in shapes for p in c), min(p[1] for c in shapes for p in c)
    x1, y1 = max(p[0] for c in shapes for p in c), max(p[1] for c in shapes for p in c)
    side = max(x1 - x0, y1 - y0) * 1.04
    box = f"{(x0 + x1 - side) / 2:.1f},{(y0 + y1 - side) / 2:.1f},{side:.1f},{side:.1f}"
    brush = (f'<DrawingBrush x:Key="MascotMain" Stretch="Uniform" ViewboxUnits="Absolute" Viewbox="{box}">'
             f'<DrawingBrush.Drawing><GeometryDrawing Brush="White" Geometry="{path_data()}"/></DrawingBrush.Drawing></DrawingBrush>')
    app = ROOT / "src/CodexTracker.App/App.xaml"
    text = app.read_text(encoding="utf-8")
    updated, count = re.subn(r'<ImageBrush x:Key="MascotMain"[^>]*/>|<DrawingBrush x:Key="MascotMain".*?</DrawingBrush>', lambda _: brush, text, count=1, flags=re.S)
    if count != 1: raise SystemExit("MascotMain introuvable dans App.xaml")
    app.write_bytes(updated.encode("utf-8"))


def ink(path: Path) -> Image.Image:
    image = Image.open(path).convert("RGBA")
    return image.crop(image.getchannel("A").getbbox())


def write_mascots() -> None:
    target = ROOT / "src/CodexTracker.App/Assets/Mascot"
    target.mkdir(parents=True, exist_ok=True)
    for name, file in MASCOTS.items():
        drawing = ink(BRAND / file)
        side = round(max(drawing.size) * 1.04)
        square = Image.new("RGBA", (side, side), (255, 255, 255, 0))
        square.alpha_composite(drawing, ((side - drawing.width) // 2, (side - drawing.height) // 2))
        # Only the alpha matters: the app uses it as an opacity mask over a theme brush.
        white = Image.new("RGBA", square.size, (255, 255, 255, 255))
        white.putalpha(square.getchannel("A"))
        white.resize((256, 256), Image.LANCZOS).save(target / f"{name}.png", optimize=True)


README_LOGO = {"light": "#4F57E3", "dark": "#8E96FF"}     # AccentBrush of each theme
README_OWLS = {"light": (0x5D, 0x61, 0x6A), "dark": (0x98, 0x9B, 0xA4)}  # MutedBrush of each theme


def write_readme_art() -> None:
    """docs/brand: the logo and the section owls of both READMEs, one file per GitHub theme."""
    target = ROOT / "docs/brand"
    target.mkdir(parents=True, exist_ok=True)
    svg = LOGO.read_text(encoding="utf-8")
    for theme, color in README_LOGO.items():
        (target / f"owl-{theme}.svg").write_bytes(svg.replace('fill="#FFFFFF"', f'fill="{color}"').encode("utf-8"))
    for name, file in MASCOTS.items():
        drawing = ink(BRAND / file)
        drawing = drawing.resize((round(drawing.width * 168 / max(drawing.size)), round(drawing.height * 168 / max(drawing.size))), Image.LANCZOS)
        for theme, color in README_OWLS.items():
            tinted = Image.new("RGBA", drawing.size, color + (255,))
            tinted.putalpha(drawing.getchannel("A"))
            tinted.save(target / f"{name}-{theme}.png", optimize=True)


if __name__ == "__main__":
    write_icon()
    write_installer()
    write_app_logo()
    write_mascots()
    write_readme_art()
    print("Logo, installer artwork and mascots generated.")
