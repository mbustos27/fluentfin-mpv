"""Checks a libmpv folder before a release uses it (tools/release/build-release.ps1).

    python -I libmpv_check.py --dir <libmpv DLL folder> --out <json> [--excluded <dll> ...]
    python -I libmpv_check.py --prepare <libmpv DLL folder> --dir <copy> --excluded <dll> ...

- Every DLL is the one BUILD-MANIFEST.txt lists (size, sha256), every listed DLL is there, libmpv-2.dll among them,
  and no DLL is unlisted.
- Every DLL each of them imports (normal and delay-load, read with pefile) is in the folder, or a Windows DLL (in
  System32 and not a Visual C++ redistributable's, or an api-ms-win-/ext-ms-win- set). A driver's DLL (vulkan-1.dll, OpenCL.dll,
  nvcuda.dll, ...) is a requirement on the PC, reported in --out as "drivers" (README.txt says so); anything else not
  found fails.
- Every function a DLL imports from another DLL of the folder is one that DLL exports (by name, or by ordinal).
- --excluded (the release leaves that library out: libdvdcss-2.dll by default) may be in the folder only as a stub: a
  DLL of that name whose BUILD-MANIFEST.txt owner has the word "stub" and is no MSYS2 package (is_stub). It must then
  export everything the folder imports from it (above). The real one fails the check, and so does a DLL importing an
  excluded one that is not in the folder.
--prepare copies a libmpv folder to --dir without the excluded DLLs and their BUILD-MANIFEST.txt lines, keeping a stub of
one (the build then checks the copy).
Writes --out: {"dlls": [...], "bytes": N, "drivers": [...], "stubs": [...]} and exits 1 on a failure.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import sys

WINDOWS = set("""advapi32 avrt bcrypt cfgmgr32 comctl32 comdlg32 crypt32 d3d11 d3d12 d3d9 d3dcompiler_47 dbghelp dwmapi
dwrite dxgi dxva2 gdi32 imm32 iphlpapi kernel32 kernelbase mfplat mfreadwrite mf msvcrt ncrypt ntdll ole32 oleaut32
powrprof psapi secur32 setupapi shell32 shlwapi user32 userenv uuid version winmm ws2_32 ucrtbase normaliz wldap32
winhttp wininet mswsock dnsapi rpcrt4 hid propsys ksuser mmdevapi""".split())
DRIVERS = {"vulkan-1", "opencl", "nvcuda", "nvcuvid", "nvencodeapi64", "amfrt64", "libmfxhw64", "libvpl"}
# in System32 on many PCs, but put there by a redistributable installer, not by Windows: never assumed
REDIST = re.compile(r"^(vcruntime\d+(_\d+)?|msvcp\d+(_\w+)?|vcomp\d+|concrt\d+|vccorlib\d+|mfc\d+\w*|msvcr\d+|ucrtbased)$")
LINE = re.compile(r"^(\S+)\s+(\d+)\s+([0-9a-fA-F]{64})\s+(.+?)\s*$")
# sources.py has the same rule (it runs isolated, without this folder on sys.path); tests/test_libmpv_stub.py holds
# them together
STUB = re.compile(r"\bstub\b", re.IGNORECASE)
MSYS2_OWNER = re.compile(r"^\s*mingw-w64-", re.IGNORECASE)


def is_stub(owner):
    """A DLL the libmpv build made as a stand-in for a library the release leaves out: BUILD-MANIFEST.txt's owner
    says "stub", and it is no MSYS2 package (whose DLL is the real library)."""
    return bool(STUB.search(owner)) and not MSYS2_OWNER.match(owner)


def sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def pe_info(path):
    """(imports {dll name as written: {function name, or "#ordinal"}}, exports {name and "#ordinal"}) of a PE file."""
    import pefile

    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                                           pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"],
                                           pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_EXPORT"]])
    imported = {}
    for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
        for entry in getattr(pe, attr, []):
            names = imported.setdefault(entry.dll.decode("ascii", "replace"), set())
            for imp in entry.imports:
                names.add(imp.name.decode("ascii", "replace") if imp.name else f"#{imp.ordinal}")
    exported = set()
    directory = getattr(pe, "DIRECTORY_ENTRY_EXPORT", None)
    for sym in (directory.symbols if directory else []):
        if sym.name:
            exported.add(sym.name.decode("ascii", "replace"))
        exported.add(f"#{sym.ordinal}")
    pe.close()
    return imported, exported


def read_manifest(folder, problems):
    """BUILD-MANIFEST.txt's DLL lines: {lower-case name: (name, size, sha256, owner)}."""
    manifest = os.path.join(folder, "BUILD-MANIFEST.txt")
    entries = {}
    if not os.path.isfile(manifest):
        problems.append(f"{folder} has no BUILD-MANIFEST.txt")
        return entries
    with open(manifest, encoding="utf-8-sig") as fh:
        text = fh.read()
    for line in text.splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        m = LINE.match(line)
        if not m:
            problems.append(f"BUILD-MANIFEST.txt: a line it cannot read: {line!r}")
            continue
        entries[m.group(1).lower()] = (m.group(1), int(m.group(2)), m.group(3).lower(), m.group(4))
    return entries


def prepare(origin, dest, excluded):
    """Copies origin's files to dest (made empty first), without each excluded DLL and its BUILD-MANIFEST.txt line,
    unless that line marks it a stub. Returns (left out, stubs kept): DLL names."""
    if os.path.isdir(dest):
        shutil.rmtree(dest)
    os.makedirs(dest)
    with open(os.path.join(origin, "BUILD-MANIFEST.txt"), encoding="utf-8-sig") as fh:
        text = fh.read()
    kept_lines, left_out, stubs = [], [], set()
    for line in text.splitlines():
        m = LINE.match(line) if line.strip() and not line.startswith("#") else None
        if m and m.group(1).lower() in excluded:
            if is_stub(m.group(4)):
                stubs.add(m.group(1).lower())
            else:
                left_out.append(m.group(1))
                continue
        kept_lines.append(line)
    with open(os.path.join(dest, "BUILD-MANIFEST.txt"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(kept_lines) + "\n")
    for f in sorted(os.listdir(origin)):
        src = os.path.join(origin, f)
        if not os.path.isfile(src) or f == "BUILD-MANIFEST.txt":
            continue
        if f.lower() in excluded and f.lower() not in stubs:
            if f not in left_out:
                left_out.append(f)
            continue
        shutil.copyfile(src, os.path.join(dest, f))
    return sorted(set(left_out), key=str.lower), sorted(stubs)


def check(folder, excluded):
    """(result for --out, problems)."""
    problems = []
    entries = read_manifest(folder, problems)
    present = {f.lower(): f for f in os.listdir(folder) if f.lower().endswith(".dll")} if os.path.isdir(folder) else {}
    for key, (name, size, digest, _owner) in entries.items():
        path = os.path.join(folder, name)
        if key not in present:
            problems.append(f"{name} is in BUILD-MANIFEST.txt and not in the folder")
        elif os.path.getsize(path) != size or sha256(path) != digest:
            problems.append(f"{name} is not the file BUILD-MANIFEST.txt lists (size or sha256)")
    for key, name in present.items():
        if key not in entries:
            problems.append(f"{name} is in the folder and not in BUILD-MANIFEST.txt")
    if "libmpv-2.dll" not in present:
        problems.append("no libmpv-2.dll")
    stubs = {key for key in present if key in entries and is_stub(entries[key][3])}
    for key in sorted((set(present) & excluded) - stubs):
        owner = entries[key][3] if key in entries else "not in BUILD-MANIFEST.txt"
        problems.append(f"{present[key]} ({owner}) is the library the release leaves out (-LibmpvExclude); only a stub of it"
                        " may ship (its BUILD-MANIFEST.txt owner says 'stub')")
    info = {key: pe_info(os.path.join(folder, name)) for key, name in sorted(present.items())}
    system32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
    drivers = set()
    imported_from_stub = {key: {} for key in stubs}
    missing_from_stub = {key: {} for key in stubs}
    for key, name in sorted(present.items()):
        imported, _exports = info[key]
        for dep in sorted(imported):
            low = dep.lower()
            stem = low[:-4] if low.endswith(".dll") else low
            if low in excluded and low not in present:
                problems.append(f"{name} imports {dep}, which the release leaves out (-LibmpvExclude): without it {name}"
                                " does not load; it needs a libmpv built without it, a stub of it, or to be shipped")
                continue
            if low in present:
                missing = sorted(imported[dep] - info[low][1])
                if missing:
                    problems.append(f"{name} imports {', '.join(missing)} from {dep}, which {dep} does not export"
                                    + (" (a stub must export everything the folder imports from it)" if low in stubs else ""))
                if low in stubs:
                    imported_from_stub[low][name] = sorted(imported[dep])
                    if missing:
                        missing_from_stub[low][name] = missing
                continue
            if low.startswith(("api-ms-win-", "ext-ms-win-")):
                continue
            if stem in DRIVERS:
                drivers.add(dep)
            elif (stem in WINDOWS or not REDIST.match(stem)) and os.path.isfile(os.path.join(system32, dep)):
                continue
            else:
                problems.append(f"{name} imports {dep}, which is neither in the folder nor a Windows DLL")
    stub_list = [{"name": present[key], "owner": entries[key][3], "exports": len([e for e in info[key][1] if not e.startswith("#")]),
                  "importedBy": imported_from_stub[key], "missing": missing_from_stub[key]} for key in sorted(stubs)]
    total = sum(os.path.getsize(os.path.join(folder, n)) for n in present.values())
    result = {"dlls": sorted(present.values()), "bytes": total, "drivers": sorted(drivers), "stubs": stub_list}
    return result, problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out")
    ap.add_argument("--prepare", metavar="ORIGIN", help="copy ORIGIN to --dir without the excluded DLLs (a stub stays)")
    ap.add_argument("--excluded", action="append", default=[], help="a library the release leaves out; only a stub of it may ship")
    args = ap.parse_args()
    excluded = {e.lower() for e in args.excluded}
    if args.prepare:
        left_out, stubs = prepare(args.prepare, args.dir, excluded)
        print(f"libmpv folder copied to {args.dir}; left out: {', '.join(left_out) or 'nothing'}"
              + (f"; the stub kept: {', '.join(stubs)}" if stubs else ""))
        return 0
    if not args.out:
        ap.error("--out is required")
    result, problems = check(args.dir, excluded)
    with open(args.out, "w", encoding="utf-8") as fh:
        json.dump(result, fh, indent=1)
    for p in problems:
        print(f"libmpv folder: {p}", file=sys.stderr)
    print(f"libmpv folder {args.dir}: {len(result['dlls'])} DLLs, {result['bytes'] / 1048576:.1f} MB"
          + (f"; needs a driver's {', '.join(result['drivers'])}" if result["drivers"] else "; needs no driver DLL"))
    for stub in result["stubs"]:
        users = stub["importedBy"]
        print(f"  {stub['name']} is a stub ({stub['owner']}): {stub['exports']} functions; "
              + ("; ".join(f"{dll} imports {len(names)}, "
                           + (f"{len(stub['missing'][dll])} not exported" if dll in stub["missing"] else "all exported")
                           for dll, names in sorted(users.items()))
                 if users else "nothing in the folder imports it"))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
