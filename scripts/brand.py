"""Builds every logo and mascot file from the owl illustrations in assets/brand.

Run after changing an illustration: python scripts/brand.py
- src/CodexTracker.App/Assets/App.ico: the main owl (05) on the dark rounded tile, 16 to 256 px.
- site/icon.png: the same tile at 128 px (favicon, site header, Store screenshots).
- installer/Assets/Mark.bmp and Welcome.bmp: the owl instead of the former >_ glyph.
- src/CodexTracker.App/Assets/Mascot/*.png: white silhouettes the app tints with its theme brushes.
"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
BRAND = ROOT / "assets/brand"
MAIN = BRAND / "codex_tracker_05_hibou_mascotte_PRINCIPAL.png"
MASCOTS = {
    "terminal": "codex_tracker_01_hibou_terminal.png",
    "watching": "codex_tracker_02_hibou_grands_yeux.png",
    "sleeping": "codex_tracker_03_hibou_endormi.png",
    "reading": "codex_tracker_04_hibou_lunettes_code.png",
    "mascot": "codex_tracker_05_hibou_mascotte_PRINCIPAL.png",
    "window": "codex_tracker_06_hibou_fenetre_terminal.png",
}
ICON_SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
TILE, EDGE = (32, 32, 32, 255), (73, 73, 73, 255)
INSTALLER_BACKGROUND = (24, 24, 24)
SUPERSAMPLE = 8


def ink(path: Path, without_sparkle: bool = False) -> Image.Image:
    """The white drawing cropped to its visible ink; small icons drop the sparkle, which would only add noise."""
    image = Image.open(path).convert("RGBA")
    if without_sparkle:
        # The sparkle sits alone in the top right corner, to the right of the owl's ear.
        image.paste((0, 0, 0, 0), (780, 0, image.width, 330))
    return image.crop(image.getchannel("A").getbbox())


def fit(drawing: Image.Image, box: int) -> Image.Image:
    scale = box / max(drawing.size)
    return drawing.resize((max(1, round(drawing.width * scale)), max(1, round(drawing.height * scale))), Image.LANCZOS)


def tile(size: int) -> Image.Image:
    """The former icon's rounded tile (5/64 inset, 12/64 radius, thin edge), with the owl in place of >_."""
    big = size * SUPERSAMPLE
    unit = big / 64
    canvas = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    ImageDraw.Draw(canvas).rounded_rectangle((5 * unit, 5 * unit, 59 * unit, 59 * unit), radius=12 * unit,
                                             fill=TILE, outline=EDGE, width=max(1, round(unit)))
    small = size < 32
    owl = fit(ink(MAIN, without_sparkle=small), round(big * (0.66 if small else 0.58)))
    canvas.alpha_composite(owl, ((big - owl.width) // 2, (big - owl.height) // 2 + round(unit * 0.5)))
    return canvas.resize((size, size), Image.LANCZOS)


def write_icon() -> None:
    frames = [tile(size) for size in ICON_SIZES]
    frames[-1].save(ROOT / "src/CodexTracker.App/Assets/App.ico", format="ICO", sizes=[(s, s) for s in ICON_SIZES],
                    append_images=frames[:-1])
    tile(128).save(ROOT / "site/icon.png", optimize=True)


def write_installer() -> None:
    owl = fit(ink(MAIN), 64)
    mark = Image.new("RGB", (110, 110), INSTALLER_BACKGROUND)
    mark.paste(owl, ((110 - owl.width) // 2, (110 - owl.height) // 2), owl)
    mark.save(ROOT / "installer/Assets/Mark.bmp")
    welcome_path = ROOT / "installer/Assets/Welcome.bmp"
    welcome = Image.open(welcome_path).convert("RGB")
    # The former glyph occupied the top left corner, above the "Codex Tracker" title.
    welcome.paste(INSTALLER_BACKGROUND, (24, 24, 120, 100))
    small = fit(ink(MAIN), 58)
    welcome.paste(small, (34, 30), small)
    welcome.save(welcome_path)


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


if __name__ == "__main__":
    write_icon()
    write_installer()
    write_mascots()
    print("Logo, installer artwork and mascots generated.")
