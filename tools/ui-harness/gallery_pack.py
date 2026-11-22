"""Packs the gallery's screenshots (gallery.ps1's runs) for the README: the newest shot of each name as WebP under 300 KB,
a 640 px wide thumbnail of each, and a contact sheet to look at before anything is committed.

    python -I gallery_pack.py --runs <scratch>\\runs --out <scratch>\\gallery [--publish <repo>\\docs\\screenshots]

Reads <runs>\\*\\shots\\NN-gallery-<what>.png. Writes <out>\\<name>.webp, <out>\\thumbs\\<name>.webp, <out>\\contact.png
and <out>\\sizes.json. --publish copies the .webp files (and thumbs) into that folder; nothing else is touched. Needs
Pillow (the backend's venv has it).
"""

import argparse
import glob
import json
import os
import re
import shutil
import sys

from PIL import Image, ImageDraw

LIMIT = 300_000
THUMB_W = 640
NAMES = {"DriveIn": "drive-in", "Cinematic": "cinematic", "Editorial": "editorial", "Classic": "classic"}
SHOT = re.compile(r"^\d+-gallery-(?P<what>.+)\.png$", re.I)


def out_name(what):
    """'home-DriveIn' -> 'home-drive-in'; 'player-stats' stays."""
    head, _, theme = what.rpartition("-")
    if head and theme in NAMES:
        return "%s-%s" % (head, NAMES[theme])
    return what.lower()


def newest_shots(runs):
    found = {}
    for path in glob.glob(os.path.join(runs, "*", "shots", "*.png")):
        m = SHOT.match(os.path.basename(path))
        if not m:
            continue
        name = out_name(m.group("what"))
        if name not in found or os.path.getmtime(path) > os.path.getmtime(found[name]):
            found[name] = path
    return dict(sorted(found.items()))


def client_area(image, most=16):
    """The window shot without its frame: the run's shots are the window's rectangle, whose invisible resize borders come
    out black at the sides and bottom and light along the top (8 px each at 100 %). Each edge is cut only while every
    sampled pixel of its line is black (or, at the top, the same light grey), at most `most` px."""
    w, h = image.size
    px = image.load()

    def line(points):
        return [px[x, y] for x, y in points]

    def black(points):
        return all(max(p) <= 2 for p in line(points))

    def light(points):
        values = line(points)
        return all(min(p) >= 200 for p in values) and max(map(sum, values)) - min(map(sum, values)) <= 12

    # sampled away from the corners, where the light top strip meets the black sides
    xs, ys = range(most, w - most, max(1, w // 64)), range(most, h - most, max(1, h // 64))
    # the light strip along the top says how thick the frame is; no strip, no frame. A side is cut no deeper than
    # that, so a black edge in the picture itself (a letterboxed video) is never eaten
    top = next((i for i in range(most) if not light([(x, i) for x in xs])), most)
    if top == 0:
        return image
    left = next((i for i in range(top) if not black([(i, y) for y in ys])), top)
    right = next((i for i in range(top) if not black([(w - 1 - i, y) for y in ys])), top)
    bottom = next((i for i in range(top) if not black([(x, h - 1 - i) for x in xs])), top)
    return image.crop((left, top, w - right, h - bottom))


def webp(image, path, limit=LIMIT, start=82, floor=50):
    quality = start
    while True:
        image.save(path, "WEBP", quality=quality, method=6)
        size = os.path.getsize(path)
        if size <= limit or quality <= floor:
            return quality, size
        quality -= 6


def pack(runs, out, publish=None):
    shots = newest_shots(runs)
    os.makedirs(os.path.join(out, "thumbs"), exist_ok=True)
    sizes, thumbs = [], []
    for name, path in shots.items():
        image = client_area(Image.open(path).convert("RGB"))
        full = os.path.join(out, name + ".webp")
        quality, size = webp(image, full)
        thumb = image.resize((THUMB_W, round(image.height * THUMB_W / image.width)), Image.LANCZOS)
        tpath = os.path.join(out, "thumbs", name + ".webp")
        _, tsize = webp(thumb, tpath, limit=60_000, start=80)
        sizes.append({"name": name, "from": path, "size": list(image.size), "bytes": size, "quality": quality,
                      "thumb_bytes": tsize, "over_limit": size > LIMIT})
        thumbs.append((name, thumb))
    if thumbs:
        cols = 3
        tw, th = THUMB_W, max(t.height for _, t in thumbs)
        rows = (len(thumbs) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * (tw + 16) + 16, rows * (th + 40) + 16), (24, 8, 14))
        draw = ImageDraw.Draw(sheet)
        for i, (name, thumb) in enumerate(thumbs):
            x, y = 16 + (i % cols) * (tw + 16), 16 + (i // cols) * (th + 40)
            sheet.paste(thumb, (x, y))
            draw.text((x, y + th + 8), name, fill=(248, 239, 240))
        sheet.save(os.path.join(out, "contact.png"))
    with open(os.path.join(out, "sizes.json"), "w", encoding="utf-8") as f:
        json.dump(sizes, f, indent=2)
    if publish:
        os.makedirs(os.path.join(publish, "thumbs"), exist_ok=True)
        for s in sizes:
            shutil.copyfile(os.path.join(out, s["name"] + ".webp"), os.path.join(publish, s["name"] + ".webp"))
            shutil.copyfile(os.path.join(out, "thumbs", s["name"] + ".webp"), os.path.join(publish, "thumbs", s["name"] + ".webp"))
    return sizes


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--publish")
    a = ap.parse_args(argv)
    sizes = pack(a.runs, a.out, a.publish)
    for s in sizes:
        print("%-24s %4d KB (q %d)%s  thumb %d KB" % (s["name"], s["bytes"] // 1000, s["quality"],
                                                   "  OVER 300 KB" if s["over_limit"] else "", s["thumb_bytes"] // 1000))
    if not sizes:
        print("no gallery shots under %s" % a.runs)
        return 1
    return 2 if any(s["over_limit"] for s in sizes) else 0


if __name__ == "__main__":
    sys.exit(main())
