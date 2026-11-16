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


def nuget_packages(assets_path):
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
    args = ap.parse_args()

    py_packages = [python_package(n) for n in requirement_names(args.requirements)]
    pyinstaller = python_package("pyinstaller")
    nuget = nuget_packages(args.assets)
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
        "jellyfin-mpv-shim (the playback backend, folder backend/ of the source; fluentfin-mpv-backend.exe here)",
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
        f"Python {args.python_version} (CPython runtime inside backend\\_internal: python314.dll, the standard library and its",
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
        lines += ["FluentFin MPV's mpv bar: backend\\_internal\\fluentfin (backend/fluentfin in the source). Its",
                  "THIRD-PARTY.md, with paths relative to that folder; VENDORED.md there has versions and hashes:", "",
                  read_text(third_party), ""]
    lines += ["3. Python packages in the backend (backend\\_internal)", THIN]
    for item in py_packages:
        entry(lines, item)
    lines += ["4. .NET (NuGet) packages restored for FluentFin (win-x64)", THIN,
              "The .NET runtime and the Windows App SDK runtime ship self-contained in this folder (both MIT,",
              "https://github.com/dotnet/runtime, https://github.com/microsoft/WindowsAppSDK). Packages marked",
              "[build only] add no file of their own to this folder.", ""]
    for item in nuget:
        note = ("[restored for the build, left out of this release]" if item.get("left_out")
                else None if item["ships"] else "[build only]")
        entry(lines, item, note)

    lines += ["", "5. Licence texts", RULE,
              "Each text once; when several packages carry the same file, its heading names them all.", ""]
    texts = [(f"Python {args.python_version} (LICENSE.txt of the python.org runtime)", read_text(args.python_license))]
    for item in [pyinstaller] + py_packages + nuget:
        texts += [(f"{item['name']} {item['version']}: {name}", text) for name, text in item["texts"]]
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
