# libmpv for FluentFin's player controls (in-process, composition mode)

Not in git (`.gitignore` here). It is MSYS2's prebuilt `mpv` package and the packages it depends on, byte for byte, but
for one DLL of our own: `libdvdcss-2.dll` is FluentFin MPV's stub (below). Rebuild it with
`tools\release\libmpv\build-libmpv.ps1` (below), then compare the hashes in `BUILD-MANIFEST.txt`.

| | |
|---|---|
| Build id / folder | `msys2-20261116-mpv-0.42.0-2\` |
| libmpv | `libmpv-2.dll` from `mingw-w64-ucrt-x86_64-mpv 0.42.0-2` (mpv **v0.42.0**, the release tag; not the earlier shinchiro build's master `bcde6b93cc`), SHA-256 `d23909f9cde783edfabd630ee01ae711d4b96840d23f623b263ba6645bc15ac1`, 3,331,744 bytes |
| With it | FFmpeg 9.0.2 (`mingw-w64-ucrt-x86_64-ffmpeg 9.0.2-4`, GPL-3.0-or-later build), libplacebo 7.387.0, libass 0.17.5, LuaJIT, shaderc, SPIRV-Cross, and every other DLL libmpv-2.dll loads |
| Vulkan | MSYS2's libplacebo and mpv import `vulkan-1.dll`; the folder ships MSYS2's `vulkan-loader 1~1.4.363.0-1` (the Khronos loader), so the DLL loads on a PC without a Vulkan driver. FluentFin renders with d3d11. |
| libdvdcss | Not shipped (DVD CSS decryption). MSYS2's `libdvdread-8.dll` imports seven functions from `libdvdcss-2.dll`, so the folder has FluentFin MPV's own stub in its place: `tools\release\libmpv\dvdcss-stub\` (GPLv3; every function fails, so no DVD opens), built by its `build.ps1` with WinLibs gcc 16.2.0 in `C:\b\dvdcss-stub`, 15,360 bytes, SHA-256 `5bec7221b992fc1710ee4838a905135f94c2c113f7f17df352ecc06b403c5623` |
| Contents | 140 DLLs + `BUILD-MANIFEST.txt` (each DLL: size, SHA-256, package, version), 191,963,680 bytes |
| Manifest SHA-256 | `74b5dd52eebc0f7223c42025f94727e1ccd336f3d6a3a67398cebb0f8c849c1a` |
| Package versions | `tools\release\libmpv\pins.txt` (the 110 MSYS2 packages the DLLs come from) |
| Sources | `vendor\libmpv\sources\`: `SOURCES.txt` (package, version, licence, DLLs, file, SHA-256), each package's MSYS2 source package `<pkgbase>-<ver>.src.tar.zst` with its `.sig` (checked against MSYS2's keyring), the stub's `fluentfin-dvdcss-stub-1.0.0.tar.gz`, and `licenses\<package>\` |

## How FluentFin loads it

`ui\FluentFin\FluentFin.csproj` copies every `*.dll` of `$(LibmpvDir)` (default: the folder above) into a `libmpv\` folder
next to FluentFin.exe. `ui\FluentFin\Mpv\Libmpv.cs`, before it loads `libmpv\libmpv-2.dll`:

- refuses (FluentFin's controls fall back to mpv's, "restart FluentFin to use its own controls") while a DLL with one of
  its FFmpeg DLLs' names is already loaded from elsewhere: Flyleaf's FFmpeg in `FFMpeg\` has the same names, and Windows
  would hand libmpv that one. `FlyleafEngine.EnsureStarted` refuses the same way the other way round.
- points the libraries' configuration into the `libmpv\` folder (where none of it is shipped, so they keep their
  defaults), in Windows' environment and the C runtime's: `GNUTLS_SYSTEM_PRIORITY_FILE` (MSYS2's GnuTLS otherwise reads
  `D:/M/msys64/ucrt64/etc/gnutls/config`, MSYS2's build path, a folder any user can make on a second drive),
  `OPENSSL_CONF`, `OPENSSL_CONF_INCLUDE`, `OPENSSL_MODULES` (otherwise `etc\ssl\openssl.cnf` and `lib\ossl-modules` next to
  FluentFin.exe: a config there loads any DLL as a provider), `XML_CATALOG_FILES`, `LIBVA_DRIVERS_PATH`, and
  `P11_KIT_NO_USER_CONFIG=1`. The backend and its mpv inherit these; every path is inside FluentFin's folder.

It loads it with `LoadLibraryEx` and `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32` (its DLLs come from
that folder or System32 only, never FluentFin's own folder, the working directory or PATH) with the process's error mode
set to `SEM_FAILCRITICALERRORS` for the call (a DLL missing an export would otherwise show "Entry Point Not Found" and
block; the thread's mode is not enough, Windows resolves imports on loader threads of its own). Then it binds GnuTLS's
translations (bound to `D:/M/msys64/ucrt64/share/locale` as it loads) to the folder's `share\locale`, and the P/Invokes
to that handle. A package build passes `-p:LibmpvDir=<this folder>`; the package must keep the `libmpv\` folder as it is.

Checked with a probe (an exe in a folder of its own, files planted next to and above it, GnuTLS's and p11-kit's debug
output; no Process Monitor without admin): before, GnuTLS read `D:/M/msys64/ucrt64/etc/gnutls/config` and OpenSSL loaded
a provider DLL from an `openssl.cnf` next to the exe; after, GnuTLS's file is inside `libmpv\`, no provider is loaded, and
HTTPS still plays (through curl, mpv's default for http(s), and through FFmpeg/GnuTLS with `curl-enabled=no`).
p11-kit is not initialized while playing. What is left: curl's built-in CA bundle path, `etc\ssl\certs\ca-bundle.crt`
next to FluentFin.exe (no variable moves it; inside the app folder, and curl uses Windows' certificate store too), and
other gettext catalogs relative to the app folder.

## MSYS2 (the build machine)

| | |
|---|---|
| Installer | `msys2-x86_64-20260927.exe`, release `2026-09-27` (the latest dated release on 2026-11-16) |
| URL | https://github.com/msys2/msys2-installer/releases/download/2026-09-27/msys2-x86_64-20260927.exe |
| Size | 85,046,168 bytes |
| SHA-256 | `416d506c839a2da4bc184eeec8cbaaf1055ee698618228c62dfc4232a6970f0e`, equal to the published `msys2-x86_64-20260927.exe.sha256` and GitHub's asset digest; Authenticode signature valid (Christoph Reiter) |
| Installed | 2026-11-16, unattended: `msys2-x86_64-20260927.exe in --confirm-command --accept-messages --root C:/msys64` (its first-run key refresh contacted keyserver.ubuntu.com) |
| Updated | `pacman -Syuu --noconfirm` until nothing was left (msys2-runtime 3.6.12-1, pacman 6.1.0-29) |
| Packages | `pacman -S mingw-w64-ucrt-x86_64-mpv mingw-w64-ucrt-x86_64-vulkan-loader` (and their dependencies) from the default mirrors (mirror.msys2.org / repo.msys2.org) |

## Rebuild exactly

1. Install MSYS2 as above (any later installer works too: the packages are pinned, not the installer).
2. From a PowerShell prompt in the repository:

   ```powershell
   tools\release\libmpv\dvdcss-stub\build.ps1     # C:\b\dvdcss-stub\libdvdcss-2.dll (WinLibs gcc)
   tools\release\libmpv\build-libmpv.ps1 -InstallPinned -Out <empty folder>\msys2-20261116-mpv-0.42.0-2 -Sources <folder>\sources
   ```

   `-InstallPinned` installs the versions in `pins.txt` (`pacman -U https://repo.msys2.org/mingw/ucrt64/<pkg>-<ver>-any.pkg.tar.zst`);
   the script then collects `libmpv-2.dll` and every DLL in its import closure from `C:\msys64\ucrt64\bin`, adds
   `vulkan-1.dll`, puts the stub in place of `libdvdcss-2.dll`, writes `BUILD-MANIFEST.txt`, and refuses a DLL whose
   package is not at its pinned version. With `-Sources` it downloads each package's source package and signature,
   checks the signature, copies the licence files, adds the stub's source tarball and FluentFin MPV's licence, removes
   source packages no shipped DLL comes from, and writes `SOURCES.txt`.
3. The DLL hashes must equal `BUILD-MANIFEST.txt`'s.

Every import of every DLL resolves to a DLL in the folder or a Windows DLL (an API set, or one in System32 that no GPU driver
installs): FluentFin.Tests checks that, import and delay-import tables, on the copy next to the tests (`LibmpvProbeTests`),
and that `libdvdcss-2.dll` is the stub. No DLL holds a path of this PC; they do hold MSYS2's own build paths
(`D:/W/B/src/...`, `D:/M/msys64/...`, `C:\Users\runneradmin\.cargo\...` in the Rust ones). No test checks paths; the release's
path gate does.

## Notes

- Composition output works: FluentFin's probe sets `vo=gpu-next`, `gpu-context=d3d11` and `d3d11-output-mode=composition`.
- Licences: see `sources\SOURCES.txt` and `sources\licenses\`. FFmpeg is GPL-3.0-or-later as built and mpv
  GPL-2.0-or-later, so the folder as a whole is GPLv3, like FluentFin MPV. Its source is `vendor\libmpv\sources\`. None is
  GPL-incompatible: the Apache-2.0 ones (OpenSSL, shaderc, SPIRV-Cross, vulkan-loader, ...) are compatible with GPLv3, and
  nothing is GPL-2.0-only (jbigkit's metadata says `GPL-2.0`, but its sources say "version 2 ... or any later version";
  uchardet's says `MPL-1.1`, but it is MPL-1.1/GPL-2.0+/LGPL-2.1+). aom/svt-av1/rav1e carry AOMedia's patent licence.
- The earlier record, shinchiro's static `libmpv-2.dll` (release `20261111`, master `git-bcde6b93cc`), imported
  `vulkan-1.dll` from the GPU driver and its bundled libraries' versions were not recorded; it is no longer used.
