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
font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 13)
slides = []
for index, (name, label) in enumerate(SCENES):
    with Image.open(PREVIEWS / f"{name}-normal-96.png") as capture:
        assert capture.height == 620 and capture.width in (750, 760), "Use normal WPF demo previews at 96 DPI"
        frame = Image.new("RGB", (760, 660), "#181818")
        frame.paste(capture.convert("RGB"), ((760 - capture.width) // 2, 0))
    draw = ImageDraw.Draw(frame)
    draw.text((18, 632), label, font=font, fill="#ecece8")
    for dot in range(len(SCENES)):
        x = 646 + dot * 16
        draw.ellipse((x, 638, x + 5, 643), fill="#ecece8" if dot == index else "#535353")
    slides.append(frame)

# A shared palette keeps text and neutral theme colors steady between frames.
atlas = Image.new("RGB", (760, 660 * len(slides)))
for index, slide in enumerate(slides):
    atlas.paste(slide, (0, 660 * index))
palette = atlas.quantize(colors=192, method=Image.Quantize.MEDIANCUT)
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
