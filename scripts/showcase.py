"""Compose the README showcase and refresh the guide captures from fictional off-screen renders (requires Pillow)."""
from pathlib import Path
import shutil
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
SHOTS = ROOT / "artifacts" / "previews"
DOCS = ROOT / "docs"


def rounded(image, radius):
    image = image.convert("RGBA")
    mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, image.width - 1, image.height - 1), radius, fill=255)
    image.putalpha(mask)
    return image


def place(canvas, image, x, y, radius=12, blur=26, offset=16, opacity=110):
    image = rounded(image, radius)
    shadow = Image.new("RGBA", (image.width + blur * 4, image.height + blur * 4), (0, 0, 0, 0))
    silhouette = Image.new("RGBA", image.size, (0, 0, 0, opacity))
    silhouette.putalpha(Image.eval(image.getchannel("A"), lambda a: a * opacity // 255))
    shadow.paste(silhouette, (blur * 2, blur * 2), silhouette)
    shadow = shadow.filter(ImageFilter.GaussianBlur(blur))
    canvas.alpha_composite(shadow, (x - blur * 2, y - blur * 2 + offset))
    canvas.alpha_composite(image, (x, y))


# Light week behind, dark dashboard in front, tray preview (Codex and Claude Code together) on the right.
peek = Image.open(SHOTS / "peek-dark.png")
height = max(1010, 260 + peek.height)
canvas = Image.new("RGBA", (1340, height), (0, 0, 0, 0))
place(canvas, Image.open(SHOTS / "Semaine-light-normal-96.png"), 470, 60)
place(canvas, Image.open(SHOTS / "Comptes-dark-normal-96.png"), 60, 210)
place(canvas, peek, 900, height - peek.height - 70, radius=10, opacity=140)
canvas = canvas.crop(canvas.getbbox())
canvas.save(DOCS / "hero.png", optimize=True)
print("hero.png", canvas.size)

for target, source in {
    "dashboard.png": "Comptes-dark-normal-96.png", "dashboard-light.png": "Comptes-light-normal-96.png",
    "resets.png": "Resets-dark-normal-96.png", "resets-week.png": "Semaine-dark-normal-96.png",
    "reminders.png": "Rappels-dark-normal-96.png", "settings.png": "Général-dark-normal-96.png",
    "assistants.png": "Assistants-dark-normal-96.png", "channels.png": "Canaux-dark-normal-96.png",
    "history.png": "details-dark.png",
}.items():
    shutil.copyfile(SHOTS / source, DOCS / target)
    print(target, Image.open(DOCS / target).size)
