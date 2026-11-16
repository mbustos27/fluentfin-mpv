"""Checks a libmpv folder before a release uses it (tools/release/build-release.ps1).

    python -I libmpv_check.py --dir <libmpv DLL folder> --out <json>

- Every DLL is the one BUILD-MANIFEST.txt lists (size, sha256), every listed DLL is there, libmpv-2.dll among them,
  and no DLL is unlisted.
- Every DLL each of them imports (normal and delay-load, read with pefile) is in the folder, or a Windows DLL (in
  System32 and not a Visual C++ redistributable's, or an api-ms-win-/ext-ms-win- set). A driver's DLL (vulkan-1.dll, OpenCL.dll,
  nvcuda.dll, ...) is a requirement on the PC, reported in --out as "drivers" (README.txt says so); anything else not
  found fails.
Writes --out: {"dlls": [...], "bytes": N, "drivers": [...]} and exits 1 on a failure.
"""

import argparse
import hashlib
import json
import os
import re
import sys

WINDOWS = set("""advapi32 avrt bcrypt cfgmgr32 comctl32 comdlg32 crypt32 d3d11 d3d12 d3d9 d3dcompiler_47 dbghelp dwmapi
dwrite dxgi dxva2 gdi32 imm32 iphlpapi kernel32 kernelbase mfplat mfreadwrite mf msvcrt ncrypt ntdll ole32 oleaut32
powrprof psapi secur32 setupapi shell32 shlwapi user32 userenv uuid version winmm ws2_32 ucrtbase normaliz wldap32
winhttp wininet mswsock dnsapi rpcrt4 hid propsys ksuser mmdevapi""".split())
DRIVERS = {"vulkan-1", "opencl", "nvcuda", "nvcuvid", "nvencodeapi64", "amfrt64", "libmfxhw64", "libvpl"}
# in System32 on many PCs, but put there by a redistributable installer, not by Windows: never assumed
REDIST = re.compile(r"^(vcruntime\d+(_\d+)?|msvcp\d+(_\w+)?|vcomp\d+|concrt\d+|vccorlib\d+|mfc\d+\w*|msvcr\d+|ucrtbased)$")
LINE = re.compile(r"^(\S+)\s+(\d+)\s+([0-9a-fA-F]{64})\s+(.+?)\s*$")


def sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


def imports(path):
    import pefile

    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
                                           pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"]])
    names = set()
    for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
        for entry in getattr(pe, attr, []):
            names.add(entry.dll.decode("ascii", "replace"))
    pe.close()
    return names


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--excluded", action="append", default=[], help="a DLL the release leaves out; nothing may import it")
    args = ap.parse_args()
    excluded = {e.lower() for e in args.excluded}
    problems = []
    manifest = os.path.join(args.dir, "BUILD-MANIFEST.txt")
    entries = {}
    if not os.path.isfile(manifest):
        problems.append(f"{args.dir} has no BUILD-MANIFEST.txt")
    else:
        for line in open(manifest, encoding="utf-8-sig"):
            line = line.rstrip("\r\n")
            if not line.strip() or line.startswith("#"):
                continue
            m = LINE.match(line)
            if not m:
                problems.append(f"BUILD-MANIFEST.txt: a line it cannot read: {line!r}")
                continue
            entries[m.group(1).lower()] = (m.group(1), int(m.group(2)), m.group(3).lower(), m.group(4))
    present = {f.lower(): f for f in os.listdir(args.dir) if f.lower().endswith(".dll")} if os.path.isdir(args.dir) else {}
    for key, (name, size, digest, _owner) in entries.items():
        path = os.path.join(args.dir, name)
        if key not in present:
            problems.append(f"{name} is in BUILD-MANIFEST.txt and not in the folder")
        elif os.path.getsize(path) != size or sha256(path) != digest:
            problems.append(f"{name} is not the file BUILD-MANIFEST.txt lists (size or sha256)")
    for key, name in present.items():
        if key not in entries:
            problems.append(f"{name} is in the folder and not in BUILD-MANIFEST.txt")
    if "libmpv-2.dll" not in present:
        problems.append("no libmpv-2.dll")
    system32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
    drivers = set()
    for key, name in sorted(present.items()):
        for dep in sorted(imports(os.path.join(args.dir, name))):
            low = dep.lower()
            stem = low[:-4] if low.endswith(".dll") else low
            if low in excluded:
                problems.append(f"{name} imports {dep}, which the release leaves out (-LibmpvExclude): without it {name}"
                                " does not load; it needs a libmpv built without it, or to be shipped")
                continue
            if low in present or low.startswith(("api-ms-win-", "ext-ms-win-")):
                continue
            if stem in DRIVERS:
                drivers.add(dep)
            elif (stem in WINDOWS or not REDIST.match(stem)) and os.path.isfile(os.path.join(system32, dep)):
                continue
            else:
                problems.append(f"{name} imports {dep}, which is neither in the folder nor a Windows DLL")
    total = sum(os.path.getsize(os.path.join(args.dir, n)) for n in present.values())
    result = {"dlls": sorted(present.values()), "bytes": total, "drivers": sorted(drivers)}
    json.dump(result, open(args.out, "w", encoding="utf-8"), indent=1)
    for p in problems:
        print(f"libmpv folder: {p}", file=sys.stderr)
    print(f"libmpv folder {args.dir}: {len(present)} DLLs, {total / 1048576:.1f} MB"
          + (f"; needs a driver's {', '.join(sorted(drivers))}" if drivers else "; needs no driver DLL"))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
