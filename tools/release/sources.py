"""Collects the corresponding source a FluentFin MPV release ships, into dist/sources, with SOURCES.txt.

    python -I sources.py --manifest sources.json --out <dist>/sources --cache <dist>/.work/sources-cache
        --repo <checkout> --version 1.0.0

- FluentFin MPV's own source: ``git archive`` of HEAD (FluentFin, jellyfin-mpv-shim and everything else in the
  repository, the patched uosc included). A working tree with changes or untracked files is refused (exit 1).
- Every item of sources.json: downloaded from its official host only (GitHub, code.videolan.org, PyPI), checked
  against the sha256 its host publishes where there is one (PyPI), the sha256 recorded either way.
- libmpv's other libraries: read from the recipe archive's packages/*.cmake (never extracted, nothing run), every
  package mpv depends on, directly or not, that sources.json does not cover is listed as not established, with the
  repository and branch the recipe takes it from.

Exits 1 when a download fails or a published hash does not match.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request

ALLOWED_HOSTS = {"github.com", "codeload.github.com", "code.videolan.org", "files.pythonhosted.org"}


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


def recipe_closure(archive):
    """Every package mpv's build depends on, from the recipe's packages/*.cmake: name -> (repository or url, branch/tag)."""
    packages = {}
    with tarfile.open(archive, "r:gz") as tar:
        for member in tar.getmembers():
            if not (member.isfile() and re.search(r"/packages/[^/]+\.cmake$", member.name)):
                continue
            text = tar.extractfile(member).read().decode("utf-8", "replace")
            for m in re.finditer(r"ExternalProject_Add\((\S+)(.*?)\n\)", text, re.S):
                name, body = m.group(1), m.group(2)
                deps = re.search(r"DEPENDS\s+(.*?)\n\s*(?:GIT_|URL|SOURCE_DIR|DOWNLOAD|PATCH|UPDATE|CONFIGURE|BUILD|INSTALL|LOG)", body, re.S)
                get = lambda key: (re.search(key + r"\s+(\S+)", body) or [None, None])[1]
                packages[name] = {
                    "deps": [d for d in deps.group(1).split() if not d.startswith("$")] if deps else [],
                    "source": get("GIT_REPOSITORY") or get("URL"),
                    "ref": get("GIT_TAG") or ("pinned by hash" if get("URL_HASH") else "the default branch"),
                }
    order, seen = [], set()

    def walk(name):
        if name in seen or name not in packages:
            return
        seen.add(name)
        order.append(name)
        for dep in packages[name]["deps"]:
            walk(dep)

    walk("mpv")
    return {name: packages[name] for name in order}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cache", required=True)
    ap.add_argument("--repo", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--status-out", help="a JSON of the libraries in libmpv-2.dll: with exact source here, or not (for notices.py)")
    args = ap.parse_args()
    manifest = json.load(open(args.manifest, encoding="utf-8"))
    if os.path.isdir(args.out):
        shutil.rmtree(args.out)
    os.makedirs(args.out)
    os.makedirs(args.cache, exist_ok=True)

    lines = [f"FluentFin MPV {args.version}: the corresponding source of what the release ships", "=" * 100, ""]

    # 1. our own source
    head = subprocess.run(["git", "-C", args.repo, "rev-parse", "HEAD"], capture_output=True, text=True, check=True).stdout.strip()
    # untracked files count too: the archive is HEAD, and anything else in the tree would not be in it
    dirty = subprocess.run(["git", "-C", args.repo, "status", "--porcelain"],
                           capture_output=True, text=True, check=True).stdout.strip()
    if dirty:
        print("the working tree has changes HEAD does not have; commit or remove them first:\n" + dirty, file=sys.stderr)
        return 1
    own = f"fluentfin-mpv-{args.version}-source.zip"
    subprocess.run(["git", "-C", args.repo, "archive", "--format=zip", f"--prefix=fluentfin-mpv-{args.version}/",
                    "-o", os.path.join(args.out, own), "HEAD"], check=True)
    lines += ["1. FluentFin MPV (FluentFin.exe and its libraries, the backend, the mpv bar), GPL-3.0", "-" * 100,
              f"{own}  sha256 {sha256(os.path.join(args.out, own))}",
              f"  git archive of commit {head} (https://github.com/mbustos27/fluentfin-mpv/tree/v{args.version})"]
    lines.append("")

    # 2. the components, downloaded
    lines += ["2. Third-party components, at the exact revision shipped", "-" * 100]
    failed = False
    recipe = None
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
        if item.get("recipe"):
            recipe = cached

    # 3. what is not established
    covered = {item["recipePackage"] for item in manifest["items"] if item.get("recipePackage")} | {"mpv"}
    known = {entry["recipePackage"]: entry for entry in manifest["knownNotIncluded"]}
    lines += ["3. Not included: the other libraries built into libmpv-2.dll", "-" * 100,
              "libmpv-2.dll is shinchiro's mpv-winbuild-cmake release 20261111. Its recipe (above) clones most of its",
              "libraries at their branch heads at build time and does not record the revisions; the build's per-package",
              "logs, which named them, were kept by GitHub for one day and are gone. So for the libraries below the exact",
              "source of this build cannot be established, and none is included. Each is listed with what the recipe",
              "names. Of them, only where libmpv-2.dll itself reports a revision is it given.", ""]
    status = {"included": sorted(covered), "notEstablished": []}
    if recipe:
        for name, package in recipe_closure(recipe).items():
            if name in covered:
                continue
            status["notEstablished"].append(name)
            entry = known.get(name)
            detail = f"{entry.get('revision', '')}; " if entry and entry.get("revision") else ""
            if entry:
                reason = entry["reason"]
            elif package["ref"] == "pinned by hash":
                host = urllib.parse.urlparse(package["source"] or "").hostname
                reason = f"exact (the recipe pins this tarball by its hash), not downloaded: {host} is not a host the release downloads from"
            else:
                reason = "revision not recorded by the build"
            lines.append(f"  {name}: {package['source']} ({package['ref']}): {detail}{reason}")
    else:
        lines.append("  (the recipe archive was not downloaded, so the list could not be made)")
    lines += ["", "4. Not needing source here", "-" * 100,
              "  Pillow's bundled libraries (FreeType under its FreeType Licence, the others permissive), CPython and",
              "  the rest of THIRD-PARTY-NOTICES.txt: no copyleft source obligation for what ships.", ""]

    if args.status_out:
        with open(args.status_out, "w", encoding="utf-8") as fh:
            json.dump(status, fh, indent=1)
    with open(os.path.join(args.out, "SOURCES.txt"), "w", encoding="utf-8-sig", newline="\r\n") as fh:
        fh.write("\n".join(lines))
    total = sum(os.path.getsize(os.path.join(args.out, f)) for f in os.listdir(args.out))
    print(f"{args.out}: {len(os.listdir(args.out))} files, {total / 1048576:.1f} MB")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
