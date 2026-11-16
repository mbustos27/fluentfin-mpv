# What a release ships that is not built from this repository

Recorded for 1.0.0 (2026-11-15). `build-release.ps1` checks every hash below before it uses the file; a mismatch stops
the build. Nothing here is committed: the build downloads it into `dist\.work\` (git-ignored).

## Python runtime (inside `backend\_internal`)

| | |
|---|---|
| What | CPython 3.14.10, x64, the full runtime zip the Python install manager uses (not the embeddable one) |
| Source | python.org only: https://www.python.org/ftp/python/3.14.10/python-3.14.10-amd64.zip |
| Listed in | https://www.python.org/ftp/python/index-windows.json (`pythoncore-3.14-64`, `sort-version` 3.14.10) |
| SHA-256 | `cc28026a650970cc1570bb127312f1f9634eb67abfe157ae6ca1acbbc627d091`, the value that index publishes |
| Signature | `python.exe` and `python314.dll` Authenticode-signed by the Python Software Foundation (checked by the build) |
| Why this one | the latest 3.14 (the dev venv is 3.14.0; 3.14.10 carries the security fixes since). PyInstaller freezes from a venv of it |

## Python packages (PyPI only)

Wheels only (`--only-binary :all:`), every file checked against the sha256 that PyPI publishes for it
(`pip --require-hashes`); the pins and hashes are in `requirements-backend.txt` (shipped) and `requirements-build.txt`
(build only). The shipped versions are the dev venv's, which every test ran against:

| Package | Version | Licence |
|---|---|---|
| certifi | 2026.10.23 | MPL-2.0 |
| charset-normalizer | 3.5.2 | MIT |
| idna | 3.20 | BSD-3-Clause |
| jellyfin-apiclient-python | 1.21.0 | GPL-3.0 |
| pillow | 13.0.0 | MIT-CMU (its wheel's bundled libraries: in its licence file) |
| python-mpv | 1.0.8 | GPL-2.0-or-later or LGPL-2.1-or-later |
| python-mpv-jsonipc | 1.5.0 | Apache-2.0 |
| pywin32 | 312 | PSF-2.0 |
| requests | 2.34.2 | Apache-2.0 |
| urllib3 | 2.8.0 | MIT |
| websocket-client | 1.9.2 | Apache-2.0 |

Left out on purpose: `pystray` and `pypresence` (`backend[all]` extras the bridge never imports) and setuptools.

Build only: pyinstaller 6.22.3 (its bootloader is in `fluentfin-mpv-backend.exe`: GPL-2.0-or-later with the bootloader
exception), pyinstaller-hooks-contrib 2026.10, altgraph 0.17.5, pefile 2024.8.26, pywin32-ctypes 0.2.3, packaging 26.3,
setuptools 84.2.1.

To move a pin: take the version's `urls[].digests.sha256` from `https://pypi.org/pypi/<name>/<version>/json` for the
`cp314-win_amd64` or `py3-none-any` wheel, update the requirements file and the table above, rebuild, run the
verification (`docs\RELEASE.md`).

## libmpv (the `libmpv\` folder beside FluentFin.exe)

Our own build: `tools\release\libmpv` (the recipe, in this repository) builds libmpv-2.dll from pinned sources in MSYS2
UCRT64 and collects every non-Windows DLL it loads, with a BUILD-MANIFEST.txt (each DLL, its size, sha256 and the MSYS2
package it comes from, or "built here"); `vendor\libmpv\VENDORED.md` has the build and where its sources are. The
release takes that folder (`build-release.ps1 -LibmpvDir`, by default the one FluentFin.csproj names):
`libmpv_check.py` checks every DLL against the manifest and that each import is in the folder (every function imported
from the folder exported there), Windows' own, or a graphics driver's (named in README.txt); the publish copies the folder into `libmpv\`, where Libmpv.cs loads it from and
from nowhere else. Its sources (`-LibmpvSources`, by default `sources` beside the folder: `msys2\*.src.tar.zst` with
their `.sig`, `mpv\mpv-<commit>.tar.gz`) go into `dist\sources\libmpv`; `sources.py` fails the build when a DLL's
source is not there, and `notices.py` takes each DLL's licence from the folder's SOURCES.txt (or its source package's
PKGBUILD) and the licence files from its `licenses\<package>\` (or the source archive). `-LibmpvExclude` (default
`libdvdcss-2.dll`) leaves those libraries, their source packages and licence folders out (`libmpv_check.py --prepare`,
`sources.py --excluded`). A stub of one stays: the DLL of that name whose BUILD-MANIFEST.txt owner has the word "stub"
and is no MSYS2 package (`is_stub`); its source and licence come from its line in the folder's SOURCES.txt (a file of
the sources, or of this repository), and it must export every function the folder imports from it. libdvdread-8.dll
imports seven from libdvdcss-2.dll, so the release ships the stub; THIRD-PARTY-NOTICES.txt then says that encrypted
DVDs aren't supported. The build stops on the real library, a stub lacking a function, or an import of one not there.
`tests\` has the tests of these rules (fixture DLLs from `tests\pe_fixture.py`); the build runs them.
`bundle_sources.py` packs `dist\sources` into one archive under GitHub's 2 GiB file limit, or numbered parts.

## The mpv bar (uosc, inside `backend\_internal\fluentfin`)

`backend\fluentfin\` as it is in the repository (uosc 5.13.0 and its fonts, `fluentfin.lua`, their licences):
`backend\fluentfin\VENDORED.md` and `THIRD-PARTY.md` there. The build checks every file `uosc-files.txt` lists is in the
frozen backend, and THIRD-PARTY-NOTICES.txt carries THIRD-PARTY.md and the licence texts.

## Not shipped

- **mpv.exe.** Only mpv's own controls need it (FluentFin's controls use libmpv inside FluentFin); the user installs mpv
  and puts it on PATH. Shipping it would add a second mpv build and a backend change to find it.
- **The shim's translations** (`.mo`, built by `gen_pkg.sh`): the bridge shows English text.

## From the repository as is

- The NuGet packages: restored and copied by `dotnet publish`, listed in THIRD-PARTY-NOTICES.txt.
- Left out of the release: upstream FluentFin's VLC and Flyleaf players' native libraries (`libvlc\` from
  VideoLAN.LibVLC.Windows, `-p:VlcWindowsX64Enabled=false`; `ui\FluentFin\FFMpeg\*.dll`, removed after the publish).
  Their managed assemblies (LibVLCSharp, FlyleafLib, LGPL) still ship; their source is in `dist\sources`.

## The corresponding source (`dist\sources`)

`sources.json` lists the components besides libmpv whose source must ship (uosc, LibVLCSharp, Flyleaf, the GPL and MPL
Python packages), at the exact revision, with why; `sources.py` downloads each from its
official host (GitHub, code.videolan.org, PyPI; PyPI's published sha256 checked), archives this repository's HEAD,
copies libmpv's own sources (above), and writes `SOURCES.txt`: every file with its sha256, and for each libmpv DLL the
source it comes from.
