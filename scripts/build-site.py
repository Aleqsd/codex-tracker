"""Builds the public site into artifacts/site (local or on Cloudflare Workers Builds, Linux or Windows, standard library only).

Injects the latest published release version (Directory.Build.props when GitHub is unreachable) and today's date, copies the demo screenshots from docs/ and the
embedded Roboto fonts, then writes robots.txt and sitemap.xml. With --indexnow (or on a Workers Builds build of main)
it notifies IndexNow (Bing, Yandex, Seznam, Naver) of the main pages.
"""
import datetime
import json
import os
import re
import shutil
import sys
import urllib.request
import xml.etree.ElementTree as ET

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ORIGIN = "https://codex-tracker.zqsdev.com"
OUT = os.path.join(ROOT, "artifacts", "site")
# Cloudflare Web Analytics: no cookie, no personal data; the token only identifies the site (it is public in every page).
ANALYTICS = ("<script defer src=\"https://static.cloudflareinsights.com/beacon.min.js\" "
             "data-cf-beacon='{\"token\": \"4cd5319e0190490894e7c0748ee1495f\"}'></script>")
IMAGES = ["hero.png", "dashboard.png", "resets-week.png", "calendar.png", "social-preview.png", "advice.png", "history.png",
          "reminders.png", "channels.png", "assistants.png", "tray-minimal.png", "tour.gif"]
# Translations of one page share their hreflang alternates; any other page is listed on its own.
GROUPS = [
    {"en": "/", "fr": "/fr/", "es": "/es/", "de": "/de/", "pt-BR": "/pt/", "ja": "/ja/"},
    {"en": "/privacy/", "fr": "/fr/confidentialite/"},
]


def version():
    """The latest published release, so download links never point to a version still being released."""
    try:
        request = urllib.request.Request("https://api.github.com/repos/Aleqsd/codex-tracker/releases/latest",
                                         headers={"Accept": "application/vnd.github+json", "User-Agent": "codex-tracker-site"})
        with urllib.request.urlopen(request, timeout=10) as response:
            tag = json.load(response)["tag_name"]
        if re.fullmatch(r"v\d+\.\d+\.\d+", tag):
            return tag[1:]
    except Exception as error:  # Offline or rate limited: the version being developed.
        print(f"Dernière release introuvable ({error}) : version de Directory.Build.props.")
    props = ET.parse(os.path.join(ROOT, "Directory.Build.props")).getroot()
    return next(e.text for e in props.iter("Version"))


def build():
    ver, date = version(), datetime.date.today().isoformat()
    shutil.rmtree(OUT, ignore_errors=True)
    shutil.copytree(os.path.join(ROOT, "site"), OUT)
    for folder, _, files in os.walk(OUT):
        for name in files:
            if name.endswith((".html", ".txt")):
                path = os.path.join(folder, name)
                with open(path, encoding="utf-8", newline="") as f:
                    text = f.read()
                text = text.replace("{{VERSION}}", ver).replace("{{DATE}}", date)
                if name.endswith(".html") and "</body>" in text:
                    text = text.replace("</body>", ANALYTICS + "\n</body>", 1)
                with open(path, "w", encoding="utf-8", newline="") as f:
                    f.write(text)
    os.makedirs(os.path.join(OUT, "img"), exist_ok=True)
    for name in IMAGES:
        shutil.copy(os.path.join(ROOT, "docs", name), os.path.join(OUT, "img"))
    fonts = os.path.join(ROOT, "src", "CodexTracker.App", "Assets", "Fonts")
    os.makedirs(os.path.join(OUT, "fonts"), exist_ok=True)
    for name in os.listdir(fonts):
        if name.endswith(".ttf"):
            shutil.copy(os.path.join(fonts, name), os.path.join(OUT, "fonts"))

    with open(os.path.join(OUT, "robots.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(f"User-agent: *\nAllow: /\n\nSitemap: {ORIGIN}/sitemap.xml\n")
    published = sorted("/" + os.path.relpath(folder, OUT).replace("\\", "/").strip(".").strip("/") + "/"
                       for folder, _, files in os.walk(OUT) if "index.html" in files)
    published = ["/" if p == "//" else p for p in published]
    entries, grouped = [], set()
    for group in GROUPS:
        present = {lang: path for lang, path in group.items() if path in published}
        grouped.update(group.values())
        entries += [(path, present) for path in present.values()]
    entries += [(path, None) for path in published if path not in grouped]
    urls = []
    for path, alternates in entries:
        priority = "1.0" if path == "/" else "0.9" if re.fullmatch(r"/(fr|es|de|pt|ja)/", path) else "0.8" if path.startswith("/guides/") else "0.5"
        links = ""
        if alternates:
            links = "".join(f'\n    <xhtml:link rel="alternate" hreflang="{lang}" href="{ORIGIN}{p}"/>' for lang, p in alternates.items())
            links += f'\n    <xhtml:link rel="alternate" hreflang="x-default" href="{ORIGIN}{next(iter(alternates.values()))}"/>'
        urls.append(f"  <url>\n    <loc>{ORIGIN}{path}</loc>\n    <lastmod>{date}</lastmod>\n    <priority>{priority}</priority>{links}\n  </url>")
    with open(os.path.join(OUT, "sitemap.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write('<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" '
                'xmlns:xhtml="http://www.w3.org/1999/xhtml">\n' + "\n".join(urls) + "\n</urlset>\n")

    # Cloudflare adds the trailing slash with a 307; keep the permanent 301 for every folder page.
    folders = sorted("/" + os.path.relpath(folder, OUT).replace(os.sep, "/") for folder, _, files in os.walk(OUT)
                     if folder != OUT and "index.html" in files)
    with open(os.path.join(OUT, "_redirects"), "a", encoding="utf-8", newline="\n") as f:
        f.write("".join(f"{path} {path}/ 301\n" for path in folders))

    for folder, _, files in os.walk(OUT):
        for name in files:
            if name.endswith((".html", ".txt")) and "{{" in open(os.path.join(folder, name), encoding="utf-8").read():
                sys.exit(f"Variables non remplacées : {os.path.join(folder, name)}")
    print(f"Site {ver} construit dans {OUT} ({len(published)} pages)")
    return published


def indexnow(published):
    key = next((n[:-4] for n in os.listdir(os.path.join(ROOT, "site")) if re.fullmatch(r"[0-9a-f]{32}\.txt", n)), None)
    if not key:
        return
    pages = [p for p in published if p in ("/", "/fr/", "/es/", "/de/", "/pt/", "/ja/") or p.startswith("/guides/") or p == "/privacy/"]
    body = json.dumps({"host": ORIGIN.split("//")[1], "key": key, "keyLocation": f"{ORIGIN}/{key}.txt",
                       "urlList": [ORIGIN + p for p in pages] + [ORIGIN + "/llms.txt"]}).encode()
    request = urllib.request.Request("https://api.indexnow.org/indexnow", body, {"Content-Type": "application/json; charset=utf-8"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            print(f"IndexNow : HTTP {response.status}")
    except Exception as error:  # A ping failure never fails the deploy.
        print(f"IndexNow indisponible : {error}")


if __name__ == "__main__":
    pages = build()
    if "--indexnow" in sys.argv or os.environ.get("WORKERS_CI_BRANCH") == "main":
        indexnow(pages)
