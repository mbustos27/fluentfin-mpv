"""Fails when a release file names the machine it was built on: the builder's user name, home, checkout or scratch folders.

    python -I path_gate.py --needle <text> [--needle ...] [--dir <package folder>] [--archive <zip or tar.gz> ...]

Every needle is looked for case-insensitively, as UTF-8 and as UTF-16LE (Windows strings in PE files and PDBs), in every
file under --dir and inside what those files hold: zip files (base_library.zip), and a PyInstaller executable's archive
and its PYZ (the frozen modules are compressed there, so a raw scan would not see them). --archive files (the source
archive, zip or tar.gz) are scanned entry by entry. Run with the build venv's Python (it needs PyInstaller to read the
executable). Prints every hit and exits 1 if there is one.

1.7 trending: --secret-env NAME (repeatable) adds the value of the environment variable NAME as a needle that is never
printed: its hits say only the file and the variable's name (the Trakt Client ID: build-release.ps1 hands it over in the
environment, never on a command line). --allow-secret-in PATH (relative to --dir) is a file where a secret may be (Nixie.dll
carries the Client ID by design), and --expect-secret-in PATH one where it must be (exit 1 when it isn't).
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


def secret_needles(names):
    """1.7 trending: {variable name: value} for each --secret-env that is set (a missing one has nothing to find)."""
    out = {}
    for name in names:
        value = (os.environ.get(name) or "").strip()
        if value:
            out[name] = value
    return out


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    ap = argparse.ArgumentParser()
    ap.add_argument("--needle", action="append", default=[])
    ap.add_argument("--secret-env", action="append", default=[], help="an environment variable whose value is a needle never printed")
    ap.add_argument("--allow-secret-in", action="append", default=[], help="a file under --dir where a secret may be")
    ap.add_argument("--expect-secret-in", action="append", default=[], help="a file under --dir where each secret must be")
    ap.add_argument("--dir")
    ap.add_argument("--archive", action="append", default=[])
    ap.add_argument("--file", action="append", default=[], help="a single file to scan (as it is, and a zip inside it)")
    args = ap.parse_args()
    if not [n for n in args.needle if n] and not args.secret_env:
        ap.error("give --needle or --secret-env")
    secrets = secret_needles(args.secret_env)
    # a secret's needles are named by the variable, never by the value; a hit's context is never printed for them
    secret_labels = {"$" + name: value for name, value in secrets.items()}
    pats = patterns([n for n in args.needle if n])
    secret_pats = [(label, pat) for label, value in secret_labels.items() for _needle, pat in patterns([value])]
    pats += secret_pats
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
    for path in args.file:
        files += 1
        with open(path, "rb") as fh:
            scan_bytes(path, fh.read(), pats, hits)
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
    def where(path):
        return os.path.normcase(os.path.normpath(os.path.abspath(path)))

    allowed = {where(os.path.join(args.dir, p)) for p in args.allow_secret_in} if args.dir else set()
    expected = {where(os.path.join(args.dir, p)): set(secret_labels) for p in args.expect_secret_in} if args.dir else {}
    for label, needle, at, context in hits:
        if needle in secret_labels:
            top = where(label.split("!", 1)[0])
            if top in expected:
                expected[top].discard(needle)
            if top in allowed and "!" not in label:
                continue
            key = (label, needle)
            if key not in seen:
                seen.add(key)
                print(f"SECRET HIT {label}: the value of {needle} (not shown)")
            continue
        key = (label, needle)
        if key not in seen:
            seen.add(key)
            print(f"HIT {label} @{at}: {needle!r}: ...{context}...")
    missing = [(path, name) for path, names in expected.items() for name in sorted(names)]
    for path, name in missing:
        print(f"MISSING {path}: the value of {name} is not in it")
    print(f"path gate: {files} files, {len(seen)} hits" + (f", {len(missing)} missing" if missing else ""))
    return 1 if seen or missing else 0


if __name__ == "__main__":
    sys.exit(main())
