"""Makes the showcase library's pictures (art\\) from the downloaded files listed in sources.tsv, and writes their
credits into showcase.json. Run it again only when the downloads or the choices below change.

    python -I make_art.py --downloads <folder of the files in sources.tsv> --mpv <mpv.exe> [--frames <scratch folder>]

- Every download is checked against sources.tsv's size and SHA-256 first; a file that differs stops the run.
- Posters: the poster as it is, 600 px wide, JPEG.
- Backdrops: the four downloaded ones cut to 16:9 (a format change: the picture is not redrawn), 1920 x 1080, JPEG.
- A film with no backdrop of its own gets one made from its poster: the whole poster covered to 16:9 and blurred until
  nothing on it can be read (so no logo is cut out of it), small, since a blur has nothing to lose at 640 px.
- The series: its press photo as the poster, and whole (never enlarged) in the middle of a dark 16:9 frame as its
  backdrop; its first episode's still is a frame of that episode at its own size (sources.tsv's .ogv is 320 x 240;
  mpv's encoder takes the frame, no window). The video itself is never copied here.
- Each credit says what was done: cut, reduced or enlarged, and to what size.
Needs Pillow (the backend's venv has it).
"""

import argparse
import csv
import hashlib
import json
import os
import subprocess
import sys

from PIL import Image, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
ART = os.path.join(HERE, "art")
EPISODE = "episode-beverly-hillbillies-s01e01.ogv"

#: slug -> the work's title, as the credits name it
TITLES = {
    "metropolis": "Metropolis (1927)", "the-general": "The General (1926)",
    "night-of-the-living-dead": "Night of the Living Dead (1968)", "charade": "Charade (1963)",
    "his-girl-friday": "His Girl Friday (1940)", "sherlock-jr": "Sherlock Jr. (1924)",
    "caligari": "The Cabinet of Dr. Caligari (1920)", "a-trip-to-the-moon": "A Trip to the Moon (1902)",
    "plan-9": "Plan 9 from Outer Space (1957)", "the-kid": "The Kid (1921)",
    "big-buck-bunny": "Big Buck Bunny (2008)", "sintel": "Sintel (2010)", "tears-of-steel": "Tears of Steel (2012)",
    "spring": "Spring (2019)", "sprite-fright": "Sprite Fright (2021)", "beverly-hillbillies": "The Beverly Hillbillies (1962)",
}
#: where a 16:9 cut sits on a taller picture: 0 = its top, 1 = its bottom
ANCHOR = {"a-trip-to-the-moon": 0.42, "the-general": 0.55, "night-of-the-living-dead": 0.5, "charade": 0.5}
#: the episode's still: seconds into it, and where its 16:9 cut sits (the video is 320 x 240: never enlarged)
FRAMES = {"s01e01": (200, 0.35)}
#: the series backdrop's frame: Nixie's deep colour
DARK = (0x17, 0x05, 0x08)
CC = {"CC BY 3.0": "https://creativecommons.org/licenses/by/3.0/", "CC BY 4.0": "https://creativecommons.org/licenses/by/4.0/"}


def scaled(source, target):
    """What a resize did, in the credit's words."""
    return "reduced" if target < source else "enlarged" if target > source else "kept at its own size"


def sources(path):
    with open(path, encoding="utf-8", newline="") as f:
        return {row["file"]: row for row in csv.DictReader(f, delimiter="\t")}


def check(rows, downloads):
    for name, row in rows.items():
        path = os.path.join(downloads, name)
        if not os.path.isfile(path):
            raise SystemExit("missing download: %s" % name)
        with open(path, "rb") as f:
            digest = hashlib.sha256(f.read()).hexdigest()
        if os.path.getsize(path) != int(row["bytes"]) or digest != row["sha256"]:
            raise SystemExit("%s differs from sources.tsv (size or SHA-256)" % name)


def cut_16_9(image, anchor=0.5):
    w, h = image.size
    if w / h > 16 / 9:
        nw = round(h * 16 / 9)
        x = (w - nw) // 2
        return image.crop((x, 0, x + nw, h))
    nh = round(w * 9 / 16)
    y = round((h - nh) * anchor)
    return image.crop((0, y, w, y + nh))


def cover_16_9(image, width):
    """The whole picture scaled to cover a 16:9 frame (its middle kept): for the blurred backdrops."""
    height = round(width * 9 / 16)
    k = max(width / image.width, height / image.height)
    scaled = image.resize((round(image.width * k), round(image.height * k)), Image.LANCZOS)
    x, y = (scaled.width - width) // 2, (scaled.height - height) // 2
    return scaled.crop((x, y, x + width, y + height))


def save_jpeg(image, name, quality):
    path = os.path.join(ART, name)
    image = image.convert("RGB")
    image.info = {}   # no comment, EXIF or profile carried over from the download
    image.save(path, "JPEG", quality=quality, optimize=True, progressive=True)
    return name


def frame(mpv, video, seconds, out):
    subprocess.run([mpv, "--no-config", "--msg-level=all=warn", "--force-window=no", video, "--start=%d" % seconds,
                    "--frames=1", "--no-audio", "--o=" + out, "--of=image2", "--ovc=png"],
                   check=True, stdin=subprocess.DEVNULL, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    return Image.open(out)


def credit(row, file, what, changes):
    licence = row["licence"] if row["licence"].startswith("CC") else "Public domain"
    entry = {"file": file, "from": row["file"], "work": "%s, %s" % (TITLES[slug_of(row["file"])], what),
             "author": row["credit"], "licence": licence, "source": row["source page"], "changes": changes}
    if licence != "Public domain":
        entry["licence_url"] = CC[licence]
    else:
        entry["licence_tag"] = row["licence"]
    return entry


def slug_of(download):
    stem = os.path.splitext(download)[0]
    for prefix in ("poster-", "backdrop-", "series-", "episode-"):
        if stem.startswith(prefix):
            stem = stem[len(prefix):]
    return stem.replace("-s01e01", "")


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--downloads", required=True)
    ap.add_argument("--mpv", required=True)
    ap.add_argument("--frames", help="a scratch folder for the episode's frames (default: --downloads)")
    a = ap.parse_args(argv)
    rows = sources(os.path.join(HERE, "sources.tsv"))
    check(rows, a.downloads)
    os.makedirs(ART, exist_ok=True)
    for old in os.listdir(ART):
        os.remove(os.path.join(ART, old))
    credits = []
    open_ = lambda name: Image.open(os.path.join(a.downloads, name))
    backdrops = {slug_of(n) for n in rows if n.startswith("backdrop-")}
    for name, row in sorted(rows.items()):
        slug = slug_of(name)
        if name.startswith("poster-") or name.startswith("series-"):
            picture = open_(name).convert("RGB")
            small = picture.resize((600, round(picture.height * 600 / picture.width)), Image.LANCZOS)
            what = "poster" if name.startswith("poster-") else "press photo"
            credits.append(credit(row, save_jpeg(small, slug + "-poster.jpg", 80), what,
                                  "%s to 600 px wide" % scaled(picture.width, 600)))
            if name.startswith("series-"):
                # the series' backdrop: the photo whole, never enlarged, in the middle of a dark 16:9 frame
                k = min(1.0, 1080 / picture.height, 1920 / picture.width)
                fitted = picture.resize((round(picture.width * k), round(picture.height * k)), Image.LANCZOS)
                frame_ = Image.new("RGB", (1920, 1080), DARK)
                frame_.paste(fitted, ((1920 - fitted.width) // 2, (1080 - fitted.height) // 2))
                credits.append(credit(row, save_jpeg(frame_, slug + "-backdrop.jpg", 80), what,
                                      "%s to %d px high, in the middle of a dark 1920 x 1080 frame"
                                      % (scaled(picture.height, fitted.height), fitted.height)))
            elif slug not in backdrops:
                blurred = cover_16_9(picture, 640).filter(ImageFilter.GaussianBlur(26))
                credits.append(credit(row, save_jpeg(blurred, slug + "-backdrop.jpg", 74), what,
                                      "reduced to cover 640 x 360 (16:9) and blurred, as a backdrop"))
        elif name.startswith("backdrop-"):
            cut = cut_16_9(open_(name).convert("RGB"), ANCHOR.get(slug, 0.5))
            wide = cut.resize((1920, 1080), Image.LANCZOS)
            credits.append(credit(row, save_jpeg(wide, slug + "-backdrop.jpg", 80), "still",
                                  "cut to 16:9, %s to 1920 x 1080" % scaled(cut.width, 1920)))
        elif name == EPISODE:
            seconds, anchor = FRAMES["s01e01"]
            still = cut_16_9(frame(a.mpv, os.path.join(a.downloads, name), seconds,
                                   os.path.join(a.frames or a.downloads, "showcase-frame-%d.png" % seconds)).convert("RGB"),
                             anchor)
            credits.append(credit(row, save_jpeg(still, slug + "-s01e01.jpg", 85), "episode 1 (frame at %d s)" % seconds,
                                  "a frame, cut to 16:9, at its own size (%d x %d)" % still.size))
    manifest_path = os.path.join(HERE, "showcase.json")
    manifest = {}
    if os.path.exists(manifest_path):
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    manifest["credits"] = sorted(credits, key=lambda c: c["file"])
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")
    total = 0
    for c in manifest["credits"]:
        size = os.path.getsize(os.path.join(ART, c["file"]))
        total += size
        print("%-40s %4d KB  %s" % (c["file"], size // 1000, c["changes"]))
    print("%d files, %d KB" % (len(credits), total // 1000))
    return 0


if __name__ == "__main__":
    sys.exit(main())
