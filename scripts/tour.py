"""Encode the fictional WPF previews as a short README tour (requires Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
PREVIEWS = ROOT / "artifacts" / "previews"
SCENES = [
    ("Comptes-dark", "Vos comptes, d’un coup d’œil"),
    ("Semaine-dark", "Les resets de la semaine"),
    ("Resets-dark", "Les échéances et les réserves"),
    ("Rappels-dark", "Des rappels à votre rythme"),
    ("Général-light", "Un thème adapté à Windows"),
    ("Assistants-dark", "Un assistant connecté, si vous le souhaitez"),
]
font = ImageFont.truetype(str(ROOT / "src" / "CodexTracker.App" / "Assets" / "Fonts" / "Roboto-Regular.ttf"), 13)
slides = []
for index, (name, label) in enumerate(SCENES):
    with Image.open(PREVIEWS / f"{name}-normal-96.png") as capture:
        assert capture.height == 620 and capture.width in (750, 760), "Use normal WPF demo previews at 96 DPI"
        frame = Image.new("RGB", (760, 660), "#101114")
        frame.paste(capture.convert("RGB"), ((760 - capture.width) // 2, 0))
    draw = ImageDraw.Draw(frame)
    draw.text((20, 632), label, font=font, fill="#ecedf0")
    for dot in range(len(SCENES)):
        x = 646 + dot * 16
        draw.ellipse((x, 638, x + 5, 643), fill="#8e96ff" if dot == index else "#3a3d45")
    slides.append(frame)

# A shared palette keeps text and neutral theme colors steady between frames.
# Small state colors (status dot, thresholds, accent) get swatches so the median cut keeps them.
STATES = ["#5fcf9c", "#1c8456", "#e8b65f", "#a4660c", "#f27b7b", "#c94545", "#8e96ff", "#4f57e3", "#5c64f0"]
atlas = Image.new("RGB", (760, 660 * len(slides) + 24 * len(STATES)))
for index, slide in enumerate(slides):
    atlas.paste(slide, (0, 660 * index))
for index, color in enumerate(STATES):
    atlas.paste(color, (0, 660 * len(slides) + 24 * index, 760, 660 * len(slides) + 24 * (index + 1)))
palette = atlas.quantize(colors=256, method=Image.Quantize.MEDIANCUT)
frames, durations = [], []
for index, slide in enumerate(slides):
    frames.append(slide.quantize(palette=palette, dither=Image.Dither.NONE))
    durations.append(1800)
    for alpha in (0.25, 0.5, 0.75):
        blended = Image.blend(slide, slides[(index + 1) % len(slides)], alpha)
        frames.append(blended.quantize(palette=palette, dither=Image.Dither.NONE))
        durations.append(40)

output = ROOT / "docs" / "tour.gif"
frames[0].save(output, save_all=True, append_images=frames[1:], duration=durations,
               loop=0, optimize=True, disposal=2, comment=b"Codex Tracker - fictional demo accounts only")
with Image.open(output) as result:
    assert result.n_frames == len(frames)
    assert result.info["loop"] == 0
    for index in range(result.n_frames):
        result.seek(index)
        assert result.size == (760, 660)
print(f"{output}: {len(frames)} frames, {sum(durations) / 1000:.2f}s, {output.stat().st_size:,} bytes")
