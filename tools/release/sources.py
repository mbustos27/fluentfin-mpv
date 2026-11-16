"""Collects the corresponding source a FluentFin MPV release ships, into dist/sources, with SOURCES.txt.

    python -I sources.py --manifest sources.json --out <dist>/sources --cache <dist>/.work/sources-cache
        --repo <checkout> --version 1.0.0 --libmpv-dir <libmpv DLL folder> [--libmpv-sources <dir>] [--status-out <json>]

- FluentFin MPV's own source: ``git archive`` of HEAD (FluentFin, jellyfin-mpv-shim, the libmpv build recipe
  tools/release/libmpv and everything else in the repository, the patched uosc included). A working tree with changes or
  untracked files is refused (exit 1).
- libmpv: the DLL folder (--libmpv-dir) is checked against its BUILD-MANIFEST.txt (every DLL listed, its size and
  sha256, nothing unlisted). Its sources (--libmpv-sources, by default the ``sources`` folder beside it: MSYS2's
  ``msys2/<package>-<version>.src.tar.zst`` with their ``.sig`` and mpv's ``mpv/mpv-<commit>.tar.gz``) are copied into
  dist/sources/libmpv; every DLL must have its source there (an MSYS2 package of the same version, or for what was built
  here mpv's tarball and the packages the manifest names). A SOURCES.txt that comes with them is copied, and every file
  it names with a sha256 is checked.
- Every item of sources.json (uosc, LibVLCSharp, Flyleaf, the GPL/MPL Python sdists): downloaded from its official host
  only (GitHub, code.videolan.org, PyPI), checked against the sha256 its host publishes where there is one (PyPI).

Writes --status-out (the DLLs, their packages and sources) for notices.py. Exits 1 on any failure.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import urllib.parse
import urllib.request

ALLOWED_HOSTS = {"github.com", "codeload.github.com", "code.videolan.org", "files.pythonhosted.org"}
MANIFEST_LINE = re.compile(r"^(\S+)\s+(\d+)\s+([0-9a-fA-F]{64})\s+(.+?)\s*$")
OWNER = re.compile(r"^(mingw-w64-(?:ucrt-x86_64|x86_64|clang-x86_64)-(\S+))\s+(\S+)$")
SOURCE_PACKAGE = re.compile(r"\bmingw-w64-[A-Za-z0-9._+-]+?-\d[A-Za-z0-9._~+:-]*-\d+\b")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for block in iter(lambda: fh.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def download(url, dest):
    host = urllib.parse.urlparse(url).hostname
    if host not in ALLOWED_HOSTS:
        raise SystemExit(f"{url}: {host} is not a host the release downloads from")
    request = urllib.request.Request(url, headers={"User-Agent": "fluentfin-mpv-release"})
    with urllib.request.urlopen(request, timeout=300) as response:
        final = urllib.parse.urlparse(response.geturl()).hostname
        if final not in ALLOWED_HOSTS:
            raise SystemExit(f"{url} redirected to {final}, not a host the release downloads from")
        with open(dest + ".part", "wb") as fh:
            shutil.copyfileobj(response, fh)
    os.replace(dest + ".part", dest)


def read_manifest(libmpv_dir):
    """BUILD-MANIFEST.txt: (its path, its header lines, [(name, size, sha256, owner)], problems)."""
    path = os.path.join(libmpv_dir, "BUILD-MANIFEST.txt")
    if not os.path.isfile(path):
        return None, [], [], [f"{libmpv_dir} has no BUILD-MANIFEST.txt"]
    entries, header, problems = [], [], []
    for line in open(path, encoding="utf-8-sig"):
        line = line.rstrip("\r\n")
        if not line.strip():
            continue
        if line.startswith("#"):
            header.append(line)
            continue
        m = MANIFEST_LINE.match(line)
        if not m:
            problems.append(f"BUILD-MANIFEST.txt: a line it cannot read: {line!r}")
            continue
        entries.append((m.group(1), int(m.group(2)), m.group(3).lower(), m.group(4)))
    listed = {e[0].lower() for e in entries}
    for name, size, digest, _owner in entries:
        f = os.path.join(libmpv_dir, name)
        if not os.path.isfile(f):
            problems.append(f"{name} is in BUILD-MANIFEST.txt and not in the folder")
        elif os.path.getsize(f) != size or sha256(f) != digest:
            problems.append(f"{name} is not the file BUILD-MANIFEST.txt lists (size or sha256)")
    for f in os.listdir(libmpv_dir):
        if f.lower().endswith(".dll") and f.lower() not in listed:
            problems.append(f"{f} is in the folder and not in BUILD-MANIFEST.txt")
    if "libmpv-2.dll" not in listed:
        problems.append("BUILD-MANIFEST.txt has no libmpv-2.dll")
    return path, header, entries, problems


def libmpv_sources(libmpv_dir, sources_dir, out, lines):
    """Copies and checks libmpv's sources; returns (status for notices, problems)."""
    path, header, entries, problems = read_manifest(libmpv_dir)
    if not path:
        return None, problems
    dest = os.path.join(out, "libmpv")
    os.makedirs(dest, exist_ok=True)
    shutil.copyfile(path, os.path.join(dest, "BUILD-MANIFEST.txt"))
    copied = {}
    if not os.path.isdir(sources_dir):
        return None, problems + [f"no libmpv sources folder at {sources_dir}"]
    for root, _dirs, files in os.walk(sources_dir):
        for f in sorted(files):
            src = os.path.join(root, f)
            rel = os.path.relpath(src, sources_dir).replace(os.sep, "/")
            target = os.path.join(dest, rel)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copyfile(src, target)
            copied[rel] = sha256(target)
    # a SOURCES.txt of libmpv's own (beside the sources, or in the DLL folder): kept, and its hashes checked
    for candidate in (os.path.join(sources_dir, "SOURCES.txt"), os.path.join(libmpv_dir, "SOURCES.txt"),
                      os.path.join(os.path.dirname(sources_dir), "SOURCES.txt")):
        if os.path.isfile(candidate):
            text = open(candidate, encoding="utf-8-sig").read()
            shutil.copyfile(candidate, os.path.join(dest, "SOURCES-libmpv.txt"))
            by_name = {rel.rsplit("/", 1)[-1]: digest for rel, digest in copied.items()}
            for line in text.splitlines():
                digests = re.findall(r"\b[0-9a-fA-F]{64}\b", line)
                # whole names only: X.src.tar.zst is not on X.src.tar.zst.sig's line
                tokens = {t.replace("\\", "/").rsplit("/", 1)[-1].strip("`'\"(),;") for t in line.split()}
                names = [n for n in by_name if n in tokens]
                for name in names:
                    if digests and by_name[name] not in (d.lower() for d in digests):
                        problems.append(f"{name}: sha256 {by_name[name]} is not the one {candidate} gives")
            break
    packages = {rel.rsplit("/", 1)[-1][:-len(".src.tar.zst")]: rel for rel in copied if rel.endswith(".src.tar.zst")}
    mpv_tarballs = [rel for rel in copied if re.search(r"(^|/)mpv-[0-9a-f]{40}\.tar\.gz$", rel)]
    built_from = sorted(set(SOURCE_PACKAGE.findall("\n".join(header))))
    commit = (re.search(r"\bmpv ([0-9a-f]{40})\b", "\n".join(header)) or [None, None])[1]
    for package in built_from:
        if package not in packages:
            problems.append(f"BUILD-MANIFEST.txt says the build used {package}; its .src.tar.zst is not in the sources")
    if commit and not any(commit in rel for rel in mpv_tarballs):
        problems.append(f"no mpv-{commit}.tar.gz in the sources")
    dlls = []
    for name, size, digest, owner in entries:
        item = {"name": name, "size": size, "sha256": digest, "owner": owner, "sources": []}
        m = OWNER.match(owner)
        if owner.strip().lower() == "built here":
            item["sources"] = mpv_tarballs + [packages[p] for p in built_from if p in packages]
            item["package"], item["version"] = "built here", ""
        elif m:
            short, version = m.group(2), m.group(3)
            item["package"], item["version"] = m.group(1), version
            exact = f"mingw-w64-{short}-{version}"
            same_version = [p for p in packages if p.endswith("-" + version)]
            chosen = exact if exact in packages else (same_version[0] if len(same_version) == 1 else None)
            if chosen is None and same_version:
                # a split package (libgcc, libstdc++ from gcc): the source whose name is in the binary package's name
                chosen = next((p for p in same_version if p[len("mingw-w64-"):-(len(version) + 1)] in short), None)
            if chosen is None:
                problems.append(f"{name} ({owner}): no source package of version {version} in the sources")
            else:
                item["sources"] = [packages[chosen]]
        else:
            problems.append(f"{name}: BUILD-MANIFEST.txt names an owner it cannot read: {owner!r}")
        dlls.append(item)

    lines += ["2. libmpv (the libmpv folder: libmpv-2.dll and every DLL it loads)", "-" * 100,
              "Built by tools/release/libmpv/build-libmpv.sh (in FluentFin MPV's source above) in MSYS2 UCRT64. Each DLL, the",
              "MSYS2 package it comes from (or 'built here') and its source in libmpv/ (BUILD-MANIFEST.txt there has the",
              "sizes and sha256 of the DLLs):", ""]
    for item in dlls:
        lines.append(f"  {item['name']}  ({item['owner']})")
        for rel in item["sources"]:
            lines.append(f"    libmpv/{rel}  sha256 {copied[rel]}")
    lines += ["", "  Every file in libmpv/:"]
    lines += [f"    {rel}  sha256 {digest}" for rel, digest in sorted(copied.items())]
    lines.append("")
    status = {"dlls": dlls, "builtFrom": built_from, "mpvCommit": commit, "sourcesDir": dest,
              "files": {rel: digest for rel, digest in copied.items()}}
    return status, problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cache", required=True)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--libmpv-dir", required=True)
    ap.add_argument("--libmpv-sources")
    ap.add_argument("--status-out", help="a JSON of libmpv's DLLs, their packages and sources (for notices.py)")
    ap.add_argument("--allow-dirty", action="store_true", help="only to try this script; never for a release")
    args = ap.parse_args()
    manifest = json.load(open(args.manifest, encoding="utf-8"))
    libmpv_sources_dir = args.libmpv_sources or os.path.join(os.path.dirname(os.path.abspath(args.libmpv_dir)), "sources")
    if os.path.isdir(args.out):
        shutil.rmtree(args.out)
    os.makedirs(args.out)
    os.makedirs(args.cache, exist_ok=True)

    lines = [f"FluentFin MPV {args.version}: the corresponding source of what the release ships", "=" * 100, ""]

    # 1. our own source (the libmpv recipe included)
    head = subprocess.run(["git", "-C", args.repo, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    # untracked files count too: the archive is HEAD, and anything else in the tree would not be in it
    dirty = subprocess.run(["git", "-C", args.repo, "status", "--porcelain"],
                           capture_output=True, text=True, check=True).stdout.strip()
    if dirty and not args.allow_dirty:
        print("the working tree has changes HEAD does not have; commit or remove them first:\n" + dirty, file=sys.stderr)
        return 1
    own = f"fluentfin-mpv-{args.version}-source.zip"
    subprocess.run(["git", "-C", args.repo, "archive", "--format=zip", f"--prefix=fluentfin-mpv-{args.version}/",
                    "-o", os.path.join(args.out, own), "HEAD"], check=True)
    lines += ["1. FluentFin MPV (FluentFin.exe and its libraries, the backend, the mpv bar, the libmpv build recipe), GPL-3.0",
              "-" * 100,
              f"{own}  sha256 {sha256(os.path.join(args.out, own))}",
              f"  git archive of commit {head} (https://github.com/mbustos27/fluentfin-mpv/tree/v{args.version})", ""]

    # 2. libmpv and its sources
    status, problems = libmpv_sources(args.libmpv_dir, libmpv_sources_dir, args.out, lines)
    failed = bool(problems)
    for p in problems:
        print(f"libmpv: {p}", file=sys.stderr)

    # 3. the other components, downloaded
    lines += ["3. The other third-party components with a copyleft licence, at the exact revision shipped", "-" * 100]
    for item in manifest["items"]:
        cached = os.path.join(args.cache, item["file"])
        if not os.path.isfile(cached) or ("sha256" in item and sha256(cached) != item["sha256"]):
            print(f"downloading {item['url']}")
            try:
                download(item["url"], cached)
            except Exception as error:  # noqa: BLE001 - every failure is reported and fails the run
                print(f"  FAILED: {error}", file=sys.stderr)
                failed = True
                continue
        digest = sha256(cached)
        if "sha256" in item and digest != item["sha256"]:
            print(f"  {item['file']}: sha256 {digest}, PyPI publishes {item['sha256']}", file=sys.stderr)
            failed = True
            continue
        shutil.copyfile(cached, os.path.join(args.out, item["file"]))
        check = "matches the sha256 PyPI publishes" if "sha256" in item else "an archive the host generates; it publishes no hash"
        lines += [f"{item['file']}  sha256 {digest}",
                  f"  {item['name']} at {item['revision']}", f"  Licence: {item['licence']}",
                  f"  Ships as: {item['shippedAs']}", f"  Why this revision: {item['why']}",
                  f"  From: {item['url']} ({check})", ""]
    lines += ["4. Not needing source here", "-" * 100,
              "  Pillow's bundled libraries (FreeType under its FreeType Licence, the others permissive), CPython and",
              "  the rest of THIRD-PARTY-NOTICES.txt: no copyleft source obligation for what ships.", ""]

    if args.status_out and status:
        with open(args.status_out, "w", encoding="utf-8") as fh:
            json.dump(status, fh, indent=1)
    with open(os.path.join(args.out, "SOURCES.txt"), "w", encoding="utf-8-sig", newline="\r\n") as fh:
        fh.write("\n".join(lines))
    total = 0
    count = 0
    for root, _dirs, files in os.walk(args.out):
        for f in files:
            total += os.path.getsize(os.path.join(root, f))
            count += 1
    print(f"{args.out}: {count} files, {total / 1048576:.1f} MB")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
