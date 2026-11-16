# -*- mode: python -*-
# PyInstaller spec of the bundled backend: backend\fluentfin-mpv-backend.exe beside Seaglass.exe, one folder (the
# interpreter runs inside that exe, no child process, so FluentFin's kill-on-close job holds it from its first
# instruction), a console-subsystem exe like the venv's python.exe (FluentFin starts it with no console window and its
# stdio on pipes; docs/BRIDGE.md section 1). Run by tools\release\build-release.ps1, which passes the version in
# FFM_RELEASE_VERSION. Paths are relative to this file: the repository is two folders up.
import os

from PyInstaller.utils.win32 import versioninfo as vi

REPO = os.path.abspath(os.path.join(SPECPATH, "..", ".."))
BACKEND = os.path.join(REPO, "backend")
PACKAGE = os.path.join(BACKEND, "jellyfin_mpv_shim")
VERSION = os.environ.get("FFM_RELEASE_VERSION", "0.0.0")


def package_data(root, prefix, skip_ext=(".py", ".pyc", ".po")):
    """(source, dest folder) for every file under root that is not code, a cache or a translation source."""
    out = []
    for folder, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d != "__pycache__"]
        rel = os.path.relpath(folder, root)
        dest = prefix if rel == "." else os.path.join(prefix, rel)
        for name in files:
            if not name.startswith(".") and os.path.splitext(name)[1].lower() not in skip_ext:
                out.append((os.path.join(folder, name), dest))
    return out


# the shim's own files (lua scripts, themes, icons, base.pot), found through utils.get_resource under _MEIPASS. The .po
# catalogs are left out: gettext reads compiled .mo files, which are not in git (gen_pkg.sh), so the bridge is English.
datas = package_data(PACKAGE, "jellyfin_mpv_shim")
# Seaglass's mpv assets (the uosc bar, its fonts and licence) when this checkout has them: fluentfin_bar finds them
# two folders above its module, which frozen is _MEIPASS\fluentfin\mpv
FLUENTFIN_ASSETS = os.path.join(BACKEND, "fluentfin")
if os.path.isdir(FLUENTFIN_ASSETS):
    datas += package_data(FLUENTFIN_ASSETS, "fluentfin", skip_ext=(".pyc",))

a = Analysis(
    [os.path.join(SPECPATH, "backend_entry.py")],
    pathex=[BACKEND],
    binaries=[],
    datas=datas,
    hiddenimports=[],
    hookspath=[],
    runtime_hooks=[],
    # never imported in bridge mode (no tray, no Discord presence, no Tk); setuptools is the build venv's, reached only
    # through PyInstaller's own setuptools runtime hook
    excludes=["tkinter", "_tkinter", "pystray", "pypresence", "setuptools", "pkg_resources", "_distutils_hack"],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

parts = [int(p) for p in VERSION.split("-")[0].split(".")[:3]] + [0]
version_info = vi.VSVersionInfo(
    ffi=vi.FixedFileInfo(filevers=tuple(parts[:4]), prodvers=tuple(parts[:4])),
    kids=[
        vi.StringFileInfo([vi.StringTable("040904B0", [
            vi.StringStruct("CompanyName", "Seaglass"),
            vi.StringStruct("FileDescription", "Seaglass playback backend (jellyfin-mpv-shim)"),
            vi.StringStruct("FileVersion", VERSION),
            vi.StringStruct("InternalName", "fluentfin-mpv-backend"),
            vi.StringStruct("LegalCopyright", "GPL-3.0-or-later"),
            vi.StringStruct("OriginalFilename", "fluentfin-mpv-backend.exe"),
            vi.StringStruct("ProductName", "Seaglass"),
            vi.StringStruct("ProductVersion", VERSION),
        ])]),
        vi.VarFileInfo([vi.VarStruct("Translation", [1033, 1200])]),
    ],
)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="fluentfin-mpv-backend",
    console=True,
    upx=False,
    icon=os.path.join(REPO, "ui", "FluentFin", "Assets", "Seaglass.ico"),
    version=version_info,
)
coll = COLLECT(exe, a.binaries, a.datas, strip=False, upx=False, name="backend")
