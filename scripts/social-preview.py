"""Builds docs/social-preview.png (1280x640): GitHub social preview and site og:image. Demo screenshot only."""
import os
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
fonts = os.path.join(root, "src", "CodexTracker.App", "Assets", "Fonts")
W, H = 1280, 640
BG, PANEL, TEXT, MUTED, ACCENT, ACCENT_SOFT, LINE = "#101114", "#17181C", "#ECEDF0", "#989BA4", "#8E96FF", "#1F2244", "#26282E"

def font(name, size): return ImageFont.truetype(os.path.join(fonts, f"Roboto-{name}.ttf"), size)

image = Image.new("RGB", (W, H), BG)
draw = ImageDraw.Draw(image)

# Screenshot on the right, cropped to the panel, with a soft shadow.
shot = Image.open(os.path.join(root, "docs", "hero.png")).convert("RGBA")
scale = 560 / shot.height
shot = shot.resize((round(shot.width * scale), 560), Image.LANCZOS)
x, y = W - shot.width + 120, 40
shadow = Image.new("RGBA", (shot.width + 80, shot.height + 80), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle((40, 40, shot.width + 40, shot.height + 40), 24, fill=(0, 0, 0, 150))
image.paste(shadow.filter(ImageFilter.GaussianBlur(24)), (x - 40, y - 24), shadow.filter(ImageFilter.GaussianBlur(24)))
mask = Image.new("L", shot.size, 0)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, shot.width, shot.height), 18, fill=255)
image.paste(shot, (x, y), mask)

# Fade the screenshot into the background behind the text.
fade = Image.new("RGBA", (W, H), (0, 0, 0, 0))
fd = ImageDraw.Draw(fade)
for i in range(260):
    fd.line([(470 + i, 0), (470 + i, H)], fill=(16, 17, 20, int(255 * (1 - i / 260))))
fd.rectangle((0, 0, 470, H), fill=(16, 17, 20, 255))
image.paste(fade, (0, 0), fade)

draw = ImageDraw.Draw(image)
left = 72
pill = font("Medium", 22)
label = "Free · Windows 11 · Open source"
w = draw.textlength(label, font=pill)
draw.rounded_rectangle((left, 120, left + w + 36, 162), 21, fill=ACCENT_SOFT)
draw.text((left + 18, 128), label, font=pill, fill=ACCENT)
draw.text((left, 196), "Codex Tracker", font=font("Bold", 72), fill=TEXT)
body = font("Regular", 32)
for i, line in enumerate(["Codex & Claude Code usage limits", "in your Windows taskbar.", "Quotas, reset times, reminders."]):
    draw.text((left, 300 + i * 46), line, font=body, fill=TEXT if i < 2 else MUTED)
draw.text((left, 520), "codex-tracker.zqsdev.com", font=font("Medium", 26), fill=ACCENT)

out = os.path.join(root, "docs", "social-preview.png")
image.save(out, optimize=True)
print(out)
