"""Fails when a release file names the machine it was built on: the builder's user name, home, checkout or scratch folders.

    python -I path_gate.py --needle <text> [--needle ...] [--dir <package folder>] [--archive <zip or tar.gz> ...]

Every needle is looked for case-insensitively, as UTF-8 and as UTF-16LE (Windows strings in PE files and PDBs), in every
file under --dir and inside what those files hold: zip files (base_library.zip), and a PyInstaller executable's archive
and its PYZ (the frozen modules are compressed there, so a raw scan would not see them). --archive files (the source
archive, zip or tar.gz) are scanned entry by entry. Run with the build venv's Python (it needs PyInstaller to read the
executable). Prints every hit and exits 1 if there is one.
"""

import argparse
import io
import os
import sys
import tarfile
import zipfile


def patterns(needles):
    out = []
    for needle in needles:
        low = needle.lower()
        variants = {low, low.replace("\\", "/"), low.replace("\\", "\\\\")}
        for v in variants:
            out.append((needle, v.encode("utf-8")))
            out.append((needle, v.encode("utf-16-le")))
    return out


def scan_bytes(label, data, pats, hits):
    low = data.lower()
    for needle, pat in pats:
        at = low.find(pat)
        if at >= 0:
            context = data[max(0, at - 40): at + len(pat) + 40]
            text = context.decode("utf-8", "replace").replace("\x00", "")
            hits.append((label, needle, at, "".join(c if c.isprintable() else "." for c in text)))
    nested(label, data, pats, hits)


def nested(label, data, pats, hits):
    if data[:4] == b"PK\x03\x04":
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as zf:
                for name in zf.namelist():
                    scan_bytes(f"{label}!{name}", zf.read(name), pats, hits)
        except zipfile.BadZipFile:
            pass


def scan_pyinstaller(path, pats, hits):
    """The frozen executable's own archive: its entries, and the modules of its PYZ, decompressed."""
    try:
        from PyInstaller.archive.readers import CArchiveReader, NotAnArchiveError
    except ImportError:
        return False
    try:
        archive = CArchiveReader(path)
    except (NotAnArchiveError, Exception):  # noqa: BLE001 - not a PyInstaller executable
        return False
    for name, entry in archive.toc.items():
        typecode = entry[-1]
        if typecode == "z":
            pyz = archive.open_embedded_archive(name)
            for module in pyz.toc:
                data = pyz.extract(module, raw=True)
                if data:
                    scan_bytes(f"{path}!{name}!{module}", data, pats, hits)
        else:
            scan_bytes(f"{path}!{name}", archive.extract(name), pats, hits)
    return True


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--needle", action="append", required=True)
    ap.add_argument("--dir")
    ap.add_argument("--archive", action="append", default=[])
    args = ap.parse_args()
    pats = patterns([n for n in args.needle if n])
    hits = []
    files = 0
    if args.dir:
        for root, _dirs, names in os.walk(args.dir):
            for name in names:
                path = os.path.join(root, name)
                files += 1
                with open(path, "rb") as fh:
                    scan_bytes(path, fh.read(), pats, hits)
                if name.lower().endswith(".exe"):
                    scan_pyinstaller(path, pats, hits)
    for archive in args.archive:
        if archive.lower().endswith(".zip"):
            with zipfile.ZipFile(archive) as zf:
                for name in zf.namelist():
                    files += 1
                    scan_bytes(f"{archive}!{name}", zf.read(name), pats, hits)
        else:
            with tarfile.open(archive) as tar:
                for member in tar.getmembers():
                    if member.isfile():
                        files += 1
                        scan_bytes(f"{archive}!{member.name}", tar.extractfile(member).read(), pats, hits)
    seen = set()
    for label, needle, at, context in hits:
        key = (label, needle)
        if key not in seen:
            seen.add(key)
            print(f"HIT {label} @{at}: {needle!r}: ...{context}...")
    print(f"path gate: {files} files, {len(seen)} hits")
    return 1 if seen else 0


if __name__ == "__main__":
    sys.exit(main())
