"""Writes THIRD-PARTY-NOTICES.txt for a FluentFin MPV release (run by tools/release/build-release.ps1).

    <build venv>/python.exe -I notices.py --out FILE --version 1.0.0 --source-url URL
        --assets <obj>/project.assets.json --requirements requirements-backend.txt --build-requirements requirements-build.txt
        --python-license <python.org runtime>/LICENSE.txt --python-version 3.14.10 [--fluentfin-assets backend/fluentfin]
        [--component NAME|VERSION|LICENCE|SOURCE ...]

Run with the build venv's interpreter, whose installed distributions are the backend's packages. Every package listed
is one that ships in the folder: the NuGet packages restored for FluentFin (win-x64; build-only ones say so), the
backend's Python packages, the Python runtime, and the native components named with --component. Licence texts are
copied from the packages themselves (Python dist-info licence files, licence files in NuGet packages); a NuGet package
that only names its licence gets that name, its copyright line and the licence's URL.
"""

import argparse
import json
import os
import re
import sys
from importlib import metadata

RULE = "=" * 100
THIN = "-" * 100

# The MIT licence's text (SPDX MIT); its copyright line is the package's own.
MIT = """Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE."""

LICENCE_FILE = re.compile(r"^(licen[cs]e|copying|notice|copyright|third[-_]?party)", re.IGNORECASE)


def read_text(path):
    with open(path, "rb") as fh:
        data = fh.read()
    for enc in ("utf-8-sig", "cp1252", "latin-1"):
        try:
            return data.decode(enc).replace("\r\n", "\n").strip("\n")
        except UnicodeDecodeError:
            continue
    return ""


def requirement_names(path):
    names = []
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.split("#", 1)[0].strip()
            if line:
                names.append(re.split(r"[=<>!~ ;\[]", line, maxsplit=1)[0])
    return names


def norm(name):
    return re.sub(r"[-_.]+", "-", name).lower()


def python_package(name):
    dist = metadata.distribution(name)
    meta = dist.metadata
    licence = meta.get("License-Expression") or ""
    if not licence:
        raw = (meta.get("License") or "").strip()
        licence = raw if raw and "\n" not in raw and len(raw) < 80 else ""
    if not licence:
        licence = "; ".join(c.split("::")[-1].strip() for c in meta.get_all("Classifier") or [] if c.startswith("License ::"))
    urls = [u.split(",", 1)[-1].strip() for u in meta.get_all("Project-URL") or []]
    home = meta.get("Home-page") or (urls[0] if urls else "")
    texts = []
    for f in dist.files or []:
        parts = str(f).replace("\\", "/").split("/")
        in_licences = len(parts) >= 2 and parts[-2] in ("licenses", "license_files") or (
            parts[0].endswith(".dist-info") and LICENCE_FILE.match(parts[-1]))
        if in_licences:
            texts.append(("/".join(parts[-2:]), read_text(dist.locate_file(f))))
    return {"name": meta["Name"], "version": dist.version, "licence": licence or "see the licence text below",
            "source": f"https://pypi.org/project/{meta['Name']}/{dist.version}/" + (f" ({home})" if home else ""),
            "texts": texts}


BINARY = (".dll", ".exe", ".winmd")


def binaries_in(folder):
    """The lower-case names of the binaries a NuGet package or runtime pack holds, those inside its .msix files too
    (the Windows App SDK's runtime package carries its DLLs that way)."""
    import zipfile

    names = set()
    for root, _dirs, files in os.walk(folder):
        names.update(f.lower() for f in files if f.lower().endswith(BINARY))
        for f in files:
            if f.lower().endswith((".msix", ".appx")):
                try:
                    with zipfile.ZipFile(os.path.join(root, f)) as zf:
                        names.update(n.rsplit("/", 1)[-1].lower() for n in zf.namelist() if n.lower().endswith(BINARY))
                except zipfile.BadZipFile:
                    pass
    return names


def shipped_names(package_dir):
    """The lower-case names of the binaries in the release folder."""
    return binaries_in(package_dir) if package_dir else set()


def runtime_packs(assets_path, shipped):
    """The .NET runtime packs a self-contained publish copies (from project.assets.json's downloadDependencies) whose
    binaries are in the folder, each with the licence files it carries."""
    with open(assets_path, encoding="utf-8-sig") as fh:
        assets = json.load(fh)
    roots = list(assets.get("packageFolders", {}).keys())
    out = []
    for framework in assets.get("project", {}).get("frameworks", {}).values():
        for dep in framework.get("downloadDependencies", []):
            name = dep["name"]
            if not (name.endswith(".win-x64") or name == "Microsoft.Windows.SDK.NET.Ref"):
                continue
            version = dep["version"].strip("[]").split(",")[0].strip()
            folder = next((os.path.join(r, name.lower(), version) for r in roots
                           if os.path.isdir(os.path.join(r, name.lower(), version))), None)
            if not folder or not (binaries_in(folder) & shipped):
                continue
            texts = [(f, read_text(os.path.join(folder, f))) for f in sorted(os.listdir(folder))
                     if os.path.isfile(os.path.join(folder, f)) and LICENCE_FILE.match(f)]
            first = next((t for f, t in texts if f.lower().startswith("license")), "")
            licence = "MIT" if "MIT License" in first[:200] else "see the licence text below"
            out.append({"name": name, "version": version, "licence": licence,
                        "source": f"https://www.nuget.org/packages/{name}/{version}", "ships": True, "texts": texts})
    return out


def source_licences(manifest_path, sources_dir):
    """The licence and copyright files at the top of every source archive in dist/sources (sources.json), as
    (heading, text): the libraries built into libmpv-2.dll, LibVLCSharp, Flyleaf, uosc and the Python sdists."""
    import tarfile

    out = []
    manifest = json.load(open(manifest_path, encoding="utf-8"))
    for item in manifest["items"]:
        path = os.path.join(sources_dir, item["file"])
        if not os.path.isfile(path):
            continue
        with tarfile.open(path) as tar:
            for member in sorted(tar.getmembers(), key=lambda m: m.name):
                parts = member.name.split("/")
                if member.isfile() and len(parts) <= 2 and LICENCE_FILE.match(parts[-1]):
                    text = tar.extractfile(member).read()
                    out.append((f"{item['name']} at {item['revision']}: {parts[-1]}",
                                text.decode("utf-8", "replace").replace("\r\n", "\n").strip("\n")))
    return out


NESTED = (".tar.gz", ".tgz", ".tar.xz", ".tar.bz2", ".tar.zst", ".zip")


def archive_licences(path, cache_dir):
    """The licence and copyright files of a source archive (an MSYS2 .src.tar.zst, mpv's tarball): those at its top, and
    those at the top of the upstream archives inside it. [(where, text)], cached by the archive's sha256."""
    import hashlib
    import io
    import tarfile
    import zipfile

    with open(path, "rb") as fh:
        digest = hashlib.sha256(fh.read()).hexdigest()
    cache = os.path.join(cache_dir, digest + ".json") if cache_dir else None
    if cache and os.path.isfile(cache):
        with open(cache, encoding="utf-8") as fh:
            return [tuple(x) for x in json.load(fh)]
    found = []

    def decode(data):
        return data.decode("utf-8", "replace").replace("\r\n", "\n").strip("\n")

    def take(name, size, read, where, nested):
        parts = name.strip("./").split("/")
        base = parts[-1]
        if len(parts) <= 2 and LICENCE_FILE.match(base) and size < 2_000_000:
            found.append((f"{where}{'/'.join(parts)}", decode(read())))
        elif nested and base.lower().endswith(NESTED):
            data = read()
            try:
                if base.lower().endswith(".zip"):
                    with zipfile.ZipFile(io.BytesIO(data)) as zf:
                        for info in zf.infolist():
                            if not info.is_dir():
                                take(info.filename, info.file_size, lambda i=info: zf.read(i), f"{where}{base}!", False)
                else:
                    with tarfile.open(fileobj=io.BytesIO(data), mode="r:*") as inner:
                        for m in inner:
                            if m.isfile():
                                take(m.name, m.size, lambda m=m: inner.extractfile(m).read(), f"{where}{base}!", False)
            except (tarfile.TarError, zipfile.BadZipFile, EOFError, OSError):
                pass

    with tarfile.open(path, mode="r:*") as tar:
        for member in tar.getmembers():
            if member.isfile():
                take(member.name, member.size, lambda m=member: tar.extractfile(m).read(), "", True)
    if cache:
        os.makedirs(cache_dir, exist_ok=True)
        with open(cache, "w", encoding="utf-8") as fh:
            json.dump(found, fh)
    return found


def pkgbuild_licence(path):
    """The licence a source package's PKGBUILD declares (its license=(...) array), or ''."""
    import tarfile

    try:
        with tarfile.open(path, mode="r:*") as tar:
            for member in tar.getmembers():
                if member.isfile() and member.name.rsplit("/", 1)[-1] == "PKGBUILD":
                    text = tar.extractfile(member).read().decode("utf-8", "replace")
                    m = re.search(r"^\s*license=\((.*?)\)", text, re.S | re.M)
                    if m:
                        names = re.findall(r"['\"]([^'\"]+)['\"]|(\S+)", m.group(1))
                        return " AND ".join(a or b for a, b in names).replace("spdx:", "")
    except (tarfile.TarError, OSError):
        pass
    return ""


def libmpv_section(status, cache_dir):
    """The libmpv folder's DLLs (sources.py's status), each with its package and licence, and the licence texts: from the
    folder's licenses/<package>/ when it has them, else from that package's source archive. (lines, [(heading, text)])."""
    lines = ["5. libmpv (the libmpv folder): libmpv-2.dll and every DLL it loads", THIN,
             "Each DLL, the MSYS2 package it comes from and the licence that package declares; their sources are in the",
             "release's sources (libmpv/, SOURCES.txt) and their licence texts in section 6.", ""]
    stubs = [dll for dll in status["dlls"] if dll.get("stub")]
    if any(dll["name"].lower().startswith("libdvdcss") for dll in stubs):
        lines += ["Encrypted DVDs aren't supported (nothing a Jellyfin client plays from a server needs it): the release has",
                  "no libdvdcss, the library that decrypts them. The libmpv folder's libdvdcss-2.dll is FluentFin MPV's",
                  "stub, so that the DVD libraries libmpv uses load: it has libdvdcss's functions and every call fails.", ""]
    declared = status.get("declared") or {}
    licensesDir = status.get("licensesDir")
    texts, done = [], set()
    for dll in status["dlls"]:
        found = {declared.get(rel, "") for rel in dll["sources"]} - {""}
        if not found and dll.get("licence"):
            found = {dll["licence"]}
        if not found:
            found = {pkgbuild_licence(os.path.join(status["sourcesDir"], rel)) for rel in dll["sources"]
                     if rel.endswith(".src.tar.zst")} - {""}
        lines.append(f"{dll['name']} ({dll['owner']})")
        if dll.get("stub"):
            lines.append("  A stub, not the library of that name: it has that library's functions and every call fails.")
        licence = '; '.join(sorted(found)) or 'see the licence texts of its package below'
        if dll.get("stub") and "GPL-3.0" in licence:
            licence += " (its text: LICENSE in this folder)"
        lines.append(f"  Licence: {licence}")
        lines.append("  Source:  " + ", ".join([f"libmpv/{rel}" for rel in dll["sources"]]
                                              + [f"{path} in FluentFin MPV's source" for path in dll.get("repoSources", [])]))
        lines.append("")
        package = dll.get("package", "")
        if package in done:
            continue
        done.add(package)
        folder = os.path.join(licensesDir, package) if licensesDir and package else None
        if folder and os.path.isdir(folder):
            for root, dirs, files in os.walk(folder):
                dirs.sort()
                for f in sorted(files):
                    rel = os.path.relpath(os.path.join(root, f), folder).replace(os.sep, "/")
                    texts.append((f"{package} {dll.get('version', '')}: {rel}", read_text(os.path.join(root, f))))
        else:
            import tarfile

            for rel in dll["sources"]:
                path = os.path.join(status["sourcesDir"], rel)
                if not tarfile.is_tarfile(path):
                    continue  # a single file (a stub's source): no licence files inside
                for where, text in archive_licences(path, cache_dir):
                    texts.append((f"{package or dll['name']} (from libmpv/{rel}): {where}", text))
    return lines, texts

def nuget_packages(assets_path, shipped=frozenset()):
    with open(assets_path, encoding="utf-8-sig") as fh:
        assets = json.load(fh)
    roots = list(assets.get("packageFolders", {}).keys())
    target = next((t for k, t in assets["targets"].items() if k.endswith("/win-x64")), None) or next(iter(assets["targets"].values()))
    out = []
    for key, lib in sorted(assets["libraries"].items(), key=lambda kv: kv[0].lower()):
        if lib.get("type") != "package":
            continue
        pid, ver = key.split("/", 1)
        info = target.get(key, {})
        ships = any(info.get(k) for k in ("runtime", "native", "runtimeTargets", "resource", "contentFiles"))
        folder = next((os.path.join(r, lib["path"]) for r in roots if os.path.isdir(os.path.join(r, lib["path"]))), None)
        # a package whose binaries are in the folder ships, however the build copied them (the Windows App SDK's targets)
        ships = ships or bool(folder and binaries_in(folder) & shipped)
        # the Windows App SDK ships self-contained: its packages together make the runtime in the folder (some carry
        # the files, others the deployment that copies them)
        ships = ships or (bool(shipped) and pid.startswith("Microsoft.WindowsAppSDK"))
        licence, licence_url, copyright_, project = "", "", "", ""
        texts = []
        if folder:
            nuspec = next((os.path.join(folder, f) for f in os.listdir(folder) if f.lower().endswith(".nuspec")), None)
            if nuspec:
                spec = read_text(nuspec)
                m = re.search(r"<license[^>]*type=\"expression\"[^>]*>([^<]+)</license>", spec)
                licence = m.group(1).strip() if m else ""
                m = re.search(r"<license[^>]*type=\"file\"[^>]*>([^<]+)</license>", spec)
                if m:
                    path = os.path.join(folder, m.group(1).strip().replace("/", os.sep))
                    if os.path.isfile(path):
                        texts.append((m.group(1).strip(), read_text(path)))
                        licence = licence or "see the licence text below"
                m = re.search(r"<licenseUrl>([^<]+)</licenseUrl>", spec)
                licence_url = m.group(1).strip() if m else ""
                m = re.search(r"<copyright>([^<]+)</copyright>", spec)
                copyright_ = m.group(1).strip() if m else ""
                m = re.search(r"<projectUrl>([^<]+)</projectUrl>", spec)
                project = m.group(1).strip() if m else ""
            if not texts:
                for f in sorted(os.listdir(folder)):
                    full = os.path.join(folder, f)
                    if os.path.isfile(full) and LICENCE_FILE.match(f) and not f.lower().endswith((".nupkg", ".p7s", ".nuspec")):
                        texts.append((f, read_text(full)))
        if not licence and licence_url:
            licence = licence_url
        if licence.upper() == "MIT" and not texts:
            texts.append(("MIT", (copyright_ + "\n\n" if copyright_ else "") + MIT))
        elif licence and not texts and licence_url:
            texts.append(("licence", f"{licence}: {licence_url}"))
        out.append({"name": pid, "version": ver, "licence": licence or "not stated in the package",
                    "source": f"https://www.nuget.org/packages/{pid}/{ver}" + (f" ({project})" if project else ""),
                    "copyright": copyright_, "ships": ships, "texts": texts})
    return out


def entry(lines, item, extra=None):
    lines.append(f"{item['name']} {item['version']}")
    lines.append(f"  Licence: {item['licence']}")
    lines.append(f"  Source:  {item['source']}")
    if item.get("copyright"):
        lines.append(f"  {item['copyright']}")
    if extra:
        lines.append(f"  {extra}")
    lines.append("")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--source-url", required=True)
    ap.add_argument("--assets", required=True)
    ap.add_argument("--requirements", required=True)
    ap.add_argument("--build-requirements", required=True)
    ap.add_argument("--python-license", required=True)
    ap.add_argument("--python-version", required=True)
    ap.add_argument("--fluentfin-assets")
    ap.add_argument("--component", action="append", default=[], help="NAME|VERSION|LICENCE|SOURCE|NOTE")
    ap.add_argument("--not-shipped", action="append", default=[], help="a NuGet package id restored but left out of the folder")
    ap.add_argument("--package-dir", help="the release folder: a package ships if its binaries are there")
    ap.add_argument("--sources-manifest", help="tools/release/sources.json")
    ap.add_argument("--sources-dir", help="dist/sources: licence files are taken from its archives")
    ap.add_argument("--libmpv-status", help="the JSON sources.py writes: the libmpv folder's DLLs, their packages and sources")
    ap.add_argument("--cache", help="a folder for the licence files found in source archives (by their sha256)")
    args = ap.parse_args()

    py_packages = [python_package(n) for n in requirement_names(args.requirements)]
    pyinstaller = python_package("pyinstaller")
    shipped = shipped_names(args.package_dir)
    nuget = nuget_packages(args.assets, shipped)
    packs = runtime_packs(args.assets, shipped) if args.package_dir else []
    for item in nuget:
        if item["name"] in args.not_shipped:
            item["ships"], item["left_out"] = False, True

    lines = [
        f"FluentFin MPV {args.version}: third-party notices",
        RULE,
        "",
        "FluentFin MPV is free software under the GNU General Public License version 3 (LICENSE in this folder).",
        f"Its source code, for this release: {args.source_url}",
        "",
        "It combines two GPLv3 projects, and ships the components listed below with them. Each is listed with its",
        "version, its licence and where its source is; the licence texts follow at the end of this file.",
        "",
        "1. The two projects FluentFin MPV is built from",
        THIN,
        "FluentFin (the user interface, folder ui/ of the source)",
        "  Licence: GPL-3.0. Upstream: https://github.com/insomniachi/FluentFin (by insomniachi)",
        "",
        "jellyfin-mpv-shim (the playback backend, folder backend/ of the source; app\\backend\\fluentfin-mpv-backend.exe here)",
        "  Licence: GPL-3.0-or-later. Upstream: https://github.com/jellyfin/jellyfin-mpv-shim (the Jellyfin project and",
        "  contributors)",
        "",
        "2. Native components",
        THIN,
    ]
    for spec in args.component:
        name, version, licence, source, *note = spec.split("|")
        entry(lines, {"name": name, "version": version, "licence": licence, "source": source}, note[0] if note else None)
    lines += [
        f"Python {args.python_version} (CPython runtime inside app\\backend\\_internal: python314.dll, the standard library and its",
        "  DLLs, among them OpenSSL, libffi, SQLite, zlib, bzip2, liblzma, expat and mpdecimal)",
        "  Licence: PSF-2.0, with the licences of the libraries it includes (the text below)",
        f"  Source:  https://www.python.org/downloads/release/python-{args.python_version.replace('.', '')}/",
        "",
        f"PyInstaller {pyinstaller['version']} bootloader (the start-up code inside fluentfin-mpv-backend.exe)",
        f"  Licence: {pyinstaller['licence']}; the bootloader exception lets it be distributed with any program",
        f"  Source:  {pyinstaller['source']}",
        "",
    ]
    third_party = os.path.join(args.fluentfin_assets or "", "THIRD-PARTY.md")
    if args.fluentfin_assets and os.path.isfile(third_party):
        # backend/fluentfin/THIRD-PARTY.md, verbatim: the mpv bar's components (uosc, its fonts, the code inside it)
        lines += ["FluentFin MPV's mpv bar: app\\backend\\_internal\\fluentfin (backend/fluentfin in the source). Its",
                  "THIRD-PARTY.md, with paths relative to that folder; VENDORED.md there has versions and hashes:", "",
                  read_text(third_party), ""]
    lines += ["3. Python packages in the backend (app\\backend\\_internal)", THIN]
    for item in py_packages:
        entry(lines, item)
    lines += ["4. .NET runtime and NuGet packages (win-x64)", THIN,
              "The .NET runtime (MIT) and the Windows App SDK runtime ship self-contained in app\\. The .NET runtime",
              "packs are listed first with their own licence files; the Windows App SDK's packages carry Microsoft's licence",
              "terms, reproduced in section 6 as shipped. Packages marked [build only] add no file of their own to app\\.", ""]
    for item in packs:
        entry(lines, item)
    for item in nuget:
        note = ("[restored for the build, left out of this release]" if item.get("left_out")
                else None if item["ships"] else "[build only]")
        entry(lines, item, note)
    libmpv_texts = []
    if args.libmpv_status:
        section, libmpv_texts = libmpv_section(json.load(open(args.libmpv_status, encoding="utf-8")), args.cache)
        lines += [""] + section

    lines += ["", "6. Licence texts", RULE,
              "Each text once; when several packages carry the same file, its heading names them all.", ""]
    texts = [(f"Python {args.python_version} (LICENSE.txt of the python.org runtime)", read_text(args.python_license))]
    for item in [pyinstaller] + py_packages + packs + nuget:
        texts += [(f"{item['name']} {item['version']}: {name}", text) for name, text in item["texts"]]
    if args.sources_manifest and args.sources_dir:
        texts += source_licences(args.sources_manifest, args.sources_dir)
    texts += libmpv_texts
    if args.fluentfin_assets:
        for root, dirs, files in os.walk(args.fluentfin_assets):
            dirs.sort()
            for f in sorted(files):
                if LICENCE_FILE.match(f) and f != "THIRD-PARTY.md":
                    rel = os.path.relpath(os.path.join(root, f), args.fluentfin_assets).replace(os.sep, "/")
                    texts.append((f"FluentFin MPV's mpv bar: {rel}", read_text(os.path.join(root, f))))
        # a licence that lives in a file's own header (THIRD-PARTY.md: "in the file")
        fzy = os.path.join(args.fluentfin_assets, "mpv", "scripts", "uosc", "lib", "fzy.lua")
        if os.path.isfile(fzy):
            m = re.match(r"--\[\[(.*?)\]\]", read_text(fzy), re.S)
            if m:
                texts.append(("fzy (Lua port), in uosc: the header of mpv/scripts/uosc/lib/fzy.lua", m.group(1).strip()))
    unique = {}   # the text with its whitespace normalised -> (owners, text)
    for owner, text in texts:
        owners, _ = unique.setdefault(" ".join(text.split()), ([], text))
        owners.append(owner)
    for owners, text in unique.values():
        lines += ["; ".join(owners), THIN, text, ""]

    text = "\n".join(lines).rstrip() + "\n"
    # UTF-8 with a BOM: copyright signs and authors' names read right in any Windows viewer
    with open(args.out, "w", encoding="utf-8-sig", newline="\r\n") as fh:
        fh.write(text)
    print(f"{args.out}: {len(py_packages)} Python packages, {len(nuget)} NuGet packages "
          f"({sum(1 for n in nuget if n['ships'])} shipping files), {len(args.component)} native components")
    no_licence = [f"{i['name']} {i['version']}" for i in py_packages + nuget if i["licence"] in ("", "not stated in the package")]
    if no_licence:
        print("no licence found for: " + ", ".join(no_licence), file=sys.stderr)


if __name__ == "__main__":
    main()
