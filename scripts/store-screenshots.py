"""Microsoft Store screenshots (1920 x 1080), one set per interface language, from fictional off-screen renders.

Usage: python scripts/store-screenshots.py [exe] [languages...]
The demo build renders each view at 144 DPI without showing a window; Pillow composes the caption and the frame.
Output: artifacts/store/<language>/NN-<name>.png
"""
from pathlib import Path
import subprocess, sys
from PIL import Image, ImageChops, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
EXE = Path(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].endswith(".exe") else ROOT / "src/CodexTracker.App/bin/Release/net10.0-windows10.0.19041.0/CodexTracker.exe"
LANGUAGES = [a for a in sys.argv[1:] if not a.endswith(".exe")] or ["fr", "en", "es", "de", "pt", "ja"]
OUT = ROOT / "artifacts" / "store"
FONTS = ROOT / "src/CodexTracker.App/Assets/Fonts"
W, H = 1920, 1080
BACKGROUND, TEXT, MUTED, ACCENT = (16, 17, 20), (236, 237, 240), (160, 163, 172), (142, 150, 255)

# (render, title, subtitle) per shot; render = view name, or "tray" for the tray preview and the pill.
CAPTIONS = {
    "fr": [("Tous vos comptes, d’un coup d’œil", "Codex et Claude Code côte à côte, avec l’ancienneté de chaque relevé."),
           ("Chaque recharge, à l’heure exacte", "La semaine en un regard : sur 5 heures, à la semaine et resets en réserve."),
           ("Les resets en réserve ne se perdent plus", "Leur expiration, et un rappel avant qu’ils ne disparaissent."),
           ("Dans la barre des tâches", "Le quota dans l’icône, l’aperçu au survol et une pastille quand un compte s’épuise."),
           ("Prévenu au bon moment", "Windows, téléphone avec ntfy (même en Ne pas déranger), Gmail et Google Agenda."),
           ("Pilotable par vos agents", "Serveur MCP local et ligne de commande : Codex ou Claude Code le règlent pour vous.")],
    "en": [("All your accounts at a glance", "Codex and Claude Code side by side, with the age of every reading."),
           ("Every refill, to the minute", "Your week at a glance: 5-hour, weekly and banked resets."),
           ("Never lose a banked reset", "See when each one expires, with a reminder before it does."),
           ("Right in the taskbar", "Quota in the icon, a preview on hover and a pill when an account runs low."),
           ("Reminded at the right time", "Windows, your phone with ntfy (even in Do Not Disturb), Gmail and Google Calendar."),
           ("Driven by your coding agents", "Local MCP server and command line: Codex or Claude Code set it up for you.")],
    "es": [("Todas tus cuentas de un vistazo", "Codex y Claude Code lado a lado, con la antigüedad de cada medición."),
           ("Cada recarga, al minuto", "Tu semana de un vistazo: 5 horas, semanal y reinicios en reserva."),
           ("No pierdas ningún reinicio en reserva", "Mira cuándo caduca cada uno y recibe un aviso antes."),
           ("En la barra de tareas", "La cuota en el icono, vista previa al pasar el ratón y una pastilla si una cuenta se agota."),
           ("Avisado en el momento justo", "Windows, tu móvil con ntfy (incluso en No molestar), Gmail y Google Calendar."),
           ("Controlado por tus agentes", "Servidor MCP local y línea de comandos: Codex o Claude Code lo configuran por ti.")],
    "de": [("Alle Konten auf einen Blick", "Codex und Claude Code nebeneinander, mit dem Alter jeder Messung."),
           ("Jede Auffüllung auf die Minute", "Deine Woche im Überblick: 5 Stunden, wöchentlich und Reserve-Resets."),
           ("Kein Reserve-Reset geht mehr verloren", "Sieh, wann jeder abläuft – mit Erinnerung vorher."),
           ("Direkt in der Taskleiste", "Kontingent im Symbol, Vorschau beim Darüberfahren und ein Hinweis, wenn ein Konto knapp wird."),
           ("Im richtigen Moment erinnert", "Windows, dein Handy mit ntfy (auch bei Nicht stören), Gmail und Google Kalender."),
           ("Von deinen Coding-Agents steuerbar", "Lokaler MCP-Server und Kommandozeile: Codex oder Claude Code richten es für dich ein.")],
    "pt": [("Todas as suas contas num relance", "Codex e Claude Code lado a lado, com a idade de cada leitura."),
           ("Cada recarga, no minuto exato", "Sua semana num relance: 5 horas, semanal e resets em reserva."),
           ("Nunca perca um reset em reserva", "Veja quando cada um expira, com um lembrete antes."),
           ("Direto na barra de tarefas", "Cota no ícone, prévia ao passar o mouse e um aviso quando uma conta está acabando."),
           ("Avisado na hora certa", "Windows, seu celular com ntfy (mesmo em Não incomodar), Gmail e Google Agenda."),
           ("Controlado pelos seus agentes", "Servidor MCP local e linha de comando: Codex ou Claude Code configuram para você.")],
    "ja": [("すべてのアカウントをひと目で", "CodexとClaude Codeを並べて表示。各測定の経過時間もわかります。"),
           ("回復時刻を分単位で", "5時間枠、週間枠、予備リセットを1週間のビューで確認。"),
           ("予備リセットを無駄にしない", "それぞれの期限を表示し、失効前にお知らせします。"),
           ("タスクバーにそのまま", "アイコンに利用枠、ホバーでプレビュー、残りわずかなら画面上のバッジ。"),
           ("ちょうどよいタイミングで通知", "Windows、ntfyでスマートフォンへ（応答不可でも）、Gmail、Googleカレンダー。"),
           ("コーディングエージェントから操作", "ローカルMCPサーバーとコマンドライン：CodexやClaude Codeが設定します。")],
}
SHOTS = [("01-accounts", "Comptes", "dark"), ("02-week", "Semaine", "dark"), ("03-resets", "Resets", "light"),
         ("04-taskbar", "tray", "dark"), ("05-reminders", "Rappels", "dark"), ("06-agents", "Assistants", "dark")]


def font(language, weight, size):
    if language == "ja":
        return ImageFont.truetype("C:/Windows/Fonts/" + ("YuGothB.ttc" if weight == "bold" else "YuGothM.ttc"), size)
    return ImageFont.truetype(str(FONTS / ("Roboto-Bold.ttf" if weight == "bold" else "Roboto-Regular.ttf")), size)


def render(language, args, file):
    file.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run([str(EXE), "--demo", "--demo-claude", "--demo-resets", "--language", language, *args], check=False, timeout=120)
    if not file.exists():
        raise SystemExit(f"Rendu échoué : {file}")
    return Image.open(file).convert("RGBA")


def rounded(image, radius):
    mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, image.width - 1, image.height - 1), radius, fill=255)
    image = image.copy(); image.putalpha(ImageChops.multiply(image.getchannel("A"), mask))
    return image


def place(canvas, image, x, y, radius=18, blur=30, opacity=150):
    image = rounded(image, radius)
    shadow = Image.new("RGBA", (image.width + blur * 4, image.height + blur * 4), (0, 0, 0, 0))
    shadow.paste(Image.new("RGBA", image.size, (0, 0, 0, opacity)), (blur * 2, blur * 2), image.getchannel("A"))
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(blur)), (x - blur * 2, y - blur * 2 + 18))
    canvas.alpha_composite(image, (x, y))


def wrap(draw, text, face, width, language):
    units = list(text) if language == "ja" else text.split(" ")
    joiner = "" if language == "ja" else " "
    lines, line = [], ""
    for unit in units:
        candidate = line + (joiner if line else "") + unit
        if draw.textlength(candidate, font=face) <= width or not line:
            line = candidate
        else:
            lines.append(line); line = unit
    return lines + [line]


def backdrop():
    canvas = Image.new("RGBA", (W, H), BACKGROUND + (255,))
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse((W - 1250, -350, W + 250, 1050), fill=ACCENT + (46,))
    return Image.alpha_composite(canvas, glow.filter(ImageFilter.GaussianBlur(160)))


def caption(canvas, language, title, subtitle):
    draw = ImageDraw.Draw(canvas)
    logo = Image.open(ROOT / "site/icon.png").convert("RGBA").resize((56, 56), Image.LANCZOS)
    canvas.alpha_composite(logo, (96, 96))
    draw.text((168, 106), "Codex Tracker", font=font("en", "bold", 32), fill=TEXT)
    y = 330
    big = font(language, "bold", 62 if language != "ja" else 54)
    for line in wrap(draw, title, big, 560, language):
        draw.text((96, y), line, font=big, fill=TEXT); y += int(big.size * 1.22)
    draw.rounded_rectangle((96, y + 22, 176, y + 28), 3, fill=ACCENT)
    y += 64
    small = font(language, "regular", 30 if language != "ja" else 27)
    for line in wrap(draw, subtitle, small, 560, language):
        draw.text((96, y), line, font=small, fill=MUTED); y += int(small.size * 1.5)


def compose(language, name, view, theme, title, subtitle):
    raw = OUT / "raw" / language
    canvas = backdrop()
    caption(canvas, language, title, subtitle)
    if view == "tray":
        peek = render(language, ["--theme", theme, "--peek-screenshot", str(raw / "peek.png"), "--dpi", "144", "--smoke-test"], raw / "peek.png")
        pill_file = raw / "pill.png"
        pill = render(language, ["--theme", theme, "--pill-screenshot", str(pill_file), "--dpi", "144", "--smoke-test"], pill_file)
        detail = raw / "pill-detail.png"
        pill = Image.open(detail).convert("RGBA") if detail.exists() else pill
        scale = min(1.15, 900 / peek.width, 600 / peek.height)
        peek = peek.resize((int(peek.width * scale), int(peek.height * scale)), Image.LANCZOS)
        place(canvas, peek, 760, 120)
        pscale = min(1.0, 900 / pill.width)
        pill = pill.resize((int(pill.width * pscale), int(pill.height * pscale)), Image.LANCZOS)
        place(canvas, pill, 760 + max(0, peek.width - pill.width), min(H - pill.height - 80, 120 + peek.height + 70), radius=14)
    else:
        file = raw / f"{view}-{theme}.png"
        shot = render(language, ["--preview", str(file), "--view", view, "--theme", theme, "--size", "normal", "--dpi", "144"], file)
        place(canvas, shot, W - shot.width - 80, (H - shot.height) // 2)
    target = OUT / language / f"{name}.png"
    target.parent.mkdir(parents=True, exist_ok=True)
    canvas.convert("RGB").save(target, optimize=True)
    print(target.relative_to(ROOT), canvas.size)


if __name__ == "__main__":
    for language in LANGUAGES:
        for (name, view, theme), (title, subtitle) in zip(SHOTS, CAPTIONS[language]):
            compose(language, name, view, theme, title, subtitle)
