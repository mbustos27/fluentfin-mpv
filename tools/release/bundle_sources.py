"""Packs dist/sources into the archive(s) attached to the GitHub release, each under GitHub's 2 GiB file limit.

    python -I bundle_sources.py --dir <dist>/sources --out <dist> --name FluentFinMPV-1.0.0-sources [--max-bytes N]

One zip (``<name>.zip``) when everything fits in --max-bytes (default 1,900,000,000), else numbered parts
(``<name>.part1.zip`` ...), files never split, each part with SOURCES.txt. The archives store the files as they are
(they are compressed already). The parts, their sizes and SHA-256 are added to SOURCES.txt in the folder and written to
``<name>.txt`` beside the archives (the file to attach next to them). Exits 1 if one file alone is over the limit.
"""

import argparse
import hashlib
import os
import sys
import zipfile


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--max-bytes", type=int, default=1_900_000_000)
    args = ap.parse_args()
    files = []
    for root, dirs, names in os.walk(args.dir):
        dirs.sort()
        for n in sorted(names):
            path = os.path.join(root, n)
            files.append((os.path.relpath(path, args.dir).replace(os.sep, "/"), path, os.path.getsize(path)))
    too_big = [rel for rel, _p, size in files if size > args.max_bytes]
    if too_big:
        print(f"over the limit by themselves: {', '.join(too_big)}", file=sys.stderr)
        return 1
    # SOURCES.txt goes into every part; the rest in order, a new part when the next file would pass the limit
    index = next((f for f in files if f[0] == "SOURCES.txt"), None)
    rest = [f for f in files if f[0] != "SOURCES.txt"]
    budget = args.max_bytes - (index[2] if index else 0) - 1_000_000  # zip headers
    parts, current, used = [], [], 0
    for item in rest:
        if current and used + item[2] > budget:
            parts.append(current)
            current, used = [], 0
        current.append(item)
        used += item[2]
    parts.append(current)
    for name in os.listdir(args.out):
        if name.startswith(args.name) and (name.endswith(".zip") or name.endswith(".txt")):
            os.remove(os.path.join(args.out, name))
    written = []
    for i, items in enumerate(parts, 1):
        archive = os.path.join(args.out, f"{args.name}.zip" if len(parts) == 1 else f"{args.name}.part{i}.zip")
        with zipfile.ZipFile(archive + ".part", "w", compression=zipfile.ZIP_STORED, allowZip64=True) as zf:
            for rel, path, _size in ([index] if index else []) + items:
                zf.write(path, f"{args.name}/{rel}")
        os.replace(archive + ".part", archive)
        written.append((os.path.basename(archive), os.path.getsize(archive), sha256(archive), len(items)))
    record = ["", "Release archives (attached to the GitHub release; each under GitHub's 2 GiB limit):"]
    record += [f"  {n}  {size} bytes  sha256 {digest}  ({count} files and SOURCES.txt)" for n, size, digest, count in written]
    sources_txt = os.path.join(args.dir, "SOURCES.txt")
    if os.path.isfile(sources_txt):
        with open(sources_txt, "a", encoding="utf-8", newline="\r\n") as fh:
            fh.write("\n".join(record) + "\n")
        with open(sources_txt, encoding="utf-8-sig") as src, open(os.path.join(args.out, f"{args.name}.txt"), "w",
                                                                   encoding="utf-8-sig", newline="\r\n") as dst:
            dst.write(src.read())
    for n, size, digest, count in written:
        print(f"{n}: {size / 1048576:.1f} MB, sha256 {digest}, {count} files")
    return 0


if __name__ == "__main__":
    sys.exit(main())
