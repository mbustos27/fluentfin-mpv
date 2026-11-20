"""Generate the QA servers' test media with a jellyfin-ffmpeg (seed.ps1 runs this off-screen through hidden_run.py).

    python -I -B media.py --ffmpeg <server>\\ffmpeg.exe --out %USERPROFILE%\\Dev\\jellyfin-qa\\media [--list]

Every file is a test pattern (testsrc2, a different hue per title) with silent audio: nothing copied from any film, and
no sound. The names are real public titles so the servers' own metadata lookup (TMDb, public data, no account) gives
real TMDb ids, a TmdbCollection id shared by The Matrix and The Matrix Reloaded, and TMDb keywords as Tags.
Idempotent: <out>\\media.json keeps each file's spec; a file is rebuilt only when it is missing or its spec changed,
files not in the spec are removed. Stdlib only.
"""

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile

CREATE_NO_WINDOW = 0x08000000
SPEC_VERSION = 1

# (relative path, seconds, options)
# options: hue (degrees), v ("h264" | "hevc10"), a (list of (language, title)), acodec ("aac" | "dca"),
#          s (embedded subtitles: list of (language, title, forced)), sidecar (list of (suffix, language))
ENG = [("eng", "English")]
MOVIES = [
    ("Movies/The Matrix (1999)/The Matrix (1999).mkv", 90, {"hue": 120}),
    ("Movies/The Matrix Reloaded (2003)/The Matrix Reloaded (2003).mkv", 90, {"hue": 150}),
    ("Movies/Big Buck Bunny (2008)/Big Buck Bunny (2008).mkv", 90, {"hue": 60,
                                                                   "s": [("eng", "English", False)]}),
    ("Movies/Sintel (2010)/Sintel (2010).mkv", 90, {"hue": 200, "sidecar": [(".en.srt", "eng")]}),
    ("Movies/Night of the Living Dead (1968)/Night of the Living Dead (1968).mkv", 90, {"hue": 0}),
    # the long, unicode title
    ("Movies/Le Fabuleux Destin d'Amélie Poulain (2001)/Le Fabuleux Destin d'Amélie Poulain (2001).mkv", 90,
     {"hue": 330, "a": [("fra", "Français")]}),
    # the one that needs a transcode: HEVC Main 10 with DTS audio
    ("Movies/Tears of Steel (2012)/Tears of Steel (2012).mkv", 90, {"hue": 260, "v": "hevc10", "acodec": "dca"}),
]
TZ = "Shows/The Twilight Zone (1959)"
MULTI = {"a": [("eng", "English"), ("spa", "Español"), ("jpn", "日本語 (commentary)")],
         "s": [("eng", "English", False), ("spa", "Español", False), ("eng", "English (forced)", True)],
         "sidecar": [(".fr.srt", "fra")]}
SHOWS = [
    (TZ + "/Season 01/The Twilight Zone (1959) - S01E01.mkv", 60, dict(MULTI, hue=30)),
    (TZ + "/Season 01/The Twilight Zone (1959) - S01E02.mkv", 60, dict(MULTI, hue=45)),
    (TZ + "/Season 01/The Twilight Zone (1959) - S01E03.mkv", 60, {"hue": 75}),
    (TZ + "/Season 02/The Twilight Zone (1959) - S02E01.mkv", 60, {"hue": 90}),
    (TZ + "/Season 02/The Twilight Zone (1959) - S02E02.mkv", 60, {"hue": 105}),
    (TZ + "/Specials/The Twilight Zone (1959) - S00E01.mkv", 60, {"hue": 15}),
]
# anime style: one TMDb season aired in two parts (cours); the files of part 2 go on numbering (E13...), with a gap
SPY = "Anime/Spy x Family (2022)"
JA = {"a": [("jpn", "日本語"), ("eng", "English")], "s": [("eng", "English", False), ("eng", "Signs & Songs", True)]}
ANIME = [
    (SPY + "/Season 01/Spy x Family (2022) - S01E01.mkv", 60, dict(JA, hue=170)),
    (SPY + "/Season 01/Spy x Family (2022) - S01E02.mkv", 60, dict(JA, hue=180)),
    (SPY + "/Season 01/Spy x Family (2022) - S01E13.mkv", 60, dict(JA, hue=190)),
    (SPY + "/Season 01/Spy x Family (2022) - S01E14.mkv", 60, dict(JA, hue=210)),
    (SPY + "/Season 02/Spy x Family (2022) - S02E01.mkv", 60, dict(JA, hue=230)),
]
OTHER = [
    # the long unicode name that stays (home videos get no metadata lookup; TMDb renamed the Amelie file's title)
    ("Home Videos/Sommerferien an der Côte d'Azur – Strandtag mit den Großeltern, Teil 1 von 3 (2019).mp4", 45,
     {"hue": 40}),
    ("Music Videos/Test Pattern Orchestra - Bars and Tone.mkv", 45, {"hue": 280}),
    ("Music Videos/Test Pattern Orchestra - Colour Burst.mkv", 45, {"hue": 300}),
]
SPEC = MOVIES + SHOWS + ANIME + OTHER


def srt(lang, title, seconds):
    def ts(s):
        return "%02d:%02d:%02d,000" % (s // 3600, (s // 60) % 60, s % 60)
    return "\n".join("%d\n%s --> %s\n[%s] %s, line %d\n" % (i + 1, ts(i * 5 + 1), ts(i * 5 + 4), lang, title, i + 1)
                     for i in range(seconds // 5))


def spec_key(rel, seconds, opts):
    blob = json.dumps([SPEC_VERSION, rel, seconds, opts], sort_keys=True, ensure_ascii=False)
    return hashlib.sha256(blob.encode("utf-8")).hexdigest()[:16]


def build(ffmpeg, out, rel, seconds, opts, tmp):
    dst = os.path.join(out, *rel.split("/"))
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    audio = opts.get("a") or ENG
    subs = opts.get("s") or []
    mp4 = dst.lower().endswith(".mp4")
    cmd = [ffmpeg, "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
           "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24:duration=%d" % seconds]
    for _ in audio:
        cmd += ["-f", "lavfi", "-t", str(seconds), "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]
    for n, (lang, title, _forced) in enumerate(subs):
        f = os.path.join(tmp, "s%d.srt" % n)
        with open(f, "w", encoding="utf-8") as fh:
            fh.write(srt(lang, title, seconds))
        cmd += ["-i", f]
    cmd += ["-map", "0:v"] + sum((["-map", "%d:a" % (i + 1)] for i in range(len(audio))), []) + \
        sum((["-map", "%d:s" % (len(audio) + 1 + i)] for i in range(len(subs))), [])
    cmd += ["-vf", "hue=h=%d" % opts.get("hue", 0)]
    if opts.get("v") == "hevc10":
        cmd += ["-c:v", "libx265", "-preset", "fast", "-crf", "34", "-pix_fmt", "yuv420p10le",
                "-x265-params", "log-level=error"]
    else:
        cmd += ["-c:v", "libx264", "-preset", "veryfast", "-crf", "30", "-pix_fmt", "yuv420p", "-g", "48"]
    if opts.get("acodec") == "dca":
        cmd += ["-c:a", "dca", "-strict", "-2", "-b:a", "768k"]
    else:
        cmd += ["-c:a", "aac", "-b:a", "48k"]
    if subs:
        cmd += ["-c:s", "mov_text" if mp4 else "srt"]
    for i, (lang, title) in enumerate(audio):
        cmd += ["-metadata:s:a:%d" % i, "language=" + lang, "-metadata:s:a:%d" % i, "title=" + title]
    for i, (lang, title, forced) in enumerate(subs):
        cmd += ["-metadata:s:s:%d" % i, "language=" + lang, "-metadata:s:s:%d" % i, "title=" + title,
                "-disposition:s:%d" % i, "forced" if forced else "0"]
    cmd += ["-disposition:a:0", "default"]
    part = dst + ".part" + (".mp4" if mp4 else ".mkv")
    cmd += [part]
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace",
                       creationflags=CREATE_NO_WINDOW)
    if r.returncode != 0:
        raise RuntimeError("ffmpeg failed for %s: %s" % (rel, (r.stderr or "")[-800:]))
    os.replace(part, dst)
    for suffix, lang in opts.get("sidecar") or []:
        with open(os.path.splitext(dst)[0] + suffix, "w", encoding="utf-8") as fh:
            fh.write(srt(lang, "sidecar", seconds))
    return dst


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ffmpeg", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--list", action="store_true", help="print the spec and exit")
    a = ap.parse_args()
    if a.list:
        for rel, seconds, opts in SPEC:
            print(rel, seconds, json.dumps(opts, ensure_ascii=False))
        return 0
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    manifest_file = os.path.join(out, "media.json")
    try:
        with open(manifest_file, encoding="utf-8") as f:
            manifest = json.load(f)
    except (OSError, ValueError):
        manifest = {}
    files = manifest.get("files", {})
    built, kept = 0, 0
    with tempfile.TemporaryDirectory(prefix="qa-media-") as tmp:
        for rel, seconds, opts in SPEC:
            key = spec_key(rel, seconds, opts)
            dst = os.path.join(out, *rel.split("/"))
            if files.get(rel, {}).get("key") == key and os.path.exists(dst):
                kept += 1
                continue
            build(a.ffmpeg, out, rel, seconds, opts, tmp)
            files[rel] = {"key": key, "bytes": os.path.getsize(dst)}
            built += 1
            print("built %s (%d bytes)" % (rel, files[rel]["bytes"]), flush=True)
    wanted = {rel for rel, _, _ in SPEC}
    sidecars = {os.path.normcase(os.path.splitext(os.path.join(out, *rel.split("/")))[0] + suffix)
                for rel, _, opts in SPEC for suffix, _ in opts.get("sidecar") or []}
    removed = 0
    for rel in [r for r in files if r not in wanted]:
        del files[rel]
    for d, _, names in os.walk(out):
        for n in names:
            p = os.path.join(d, n)
            relp = os.path.relpath(p, out).replace(os.sep, "/")
            if relp == "media.json" or relp in wanted or os.path.normcase(p) in sidecars:
                continue
            os.remove(p)
            removed += 1
    manifest = {"spec_version": SPEC_VERSION, "files": files,
                "total_bytes": sum(v["bytes"] for v in files.values())}
    with open(manifest_file, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=1, ensure_ascii=False)
    print("media: %d built, %d kept, %d stray removed, %d files, %.1f MB" % (
        built, kept, removed, len(files), manifest["total_bytes"] / 1e6), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
