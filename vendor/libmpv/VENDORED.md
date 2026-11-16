# libmpv for FluentFin's player controls (in-process, composition mode)

Not in git (`.gitignore` here). It is MSYS2's prebuilt `mpv` package and the packages it depends on, byte for byte:
nothing in it is compiled here. Rebuild it with `tools\release\libmpv\build-libmpv.ps1` (below), then compare the hashes in
`BUILD-MANIFEST.txt`.

| | |
|---|---|
| Build id / folder | `msys2-20261116-mpv-0.42.0-2\` |
| libmpv | `libmpv-2.dll` from `mingw-w64-ucrt-x86_64-mpv 0.42.0-2` (mpv **v0.42.0**, the release tag; not the earlier shinchiro build's master `bcde6b93cc`), SHA-256 `d23909f9cde783edfabd630ee01ae711d4b96840d23f623b263ba6645bc15ac1`, 3,331,744 bytes |
| With it | FFmpeg 9.0.2 (`mingw-w64-ucrt-x86_64-ffmpeg 9.0.2-4`, GPL-3.0-or-later build), libplacebo 7.387.0, libass 0.17.5, LuaJIT, shaderc, SPIRV-Cross, and every other DLL libmpv-2.dll loads |
| Vulkan | MSYS2's libplacebo and mpv import `vulkan-1.dll`; the folder ships MSYS2's `vulkan-loader 1~1.4.363.0-1` (the Khronos loader), so the DLL loads on a PC without a Vulkan driver. FluentFin renders with d3d11. |
| Contents | 140 DLLs + `BUILD-MANIFEST.txt` (each DLL: size, SHA-256, package, version), 192,068,106 bytes |
| Manifest SHA-256 | `6aedf506db0c5e0d0f566ca0351c6aaecc18501f509e12acc717ee3537a3a947` |
| Package versions | `tools\release\libmpv\pins.txt` (the 111 packages the DLLs come from) |
| Sources | `vendor\libmpv\sources\`: `SOURCES.txt` (package, version, licence, DLLs, file, SHA-256), each package's MSYS2 source package `<pkgbase>-<ver>.src.tar.zst` with its `.sig` (checked against MSYS2's keyring), and `licenses\<package>\` |

## How FluentFin loads it

`ui\FluentFin\FluentFin.csproj` copies every `*.dll` of `$(LibmpvDir)` (default: the folder above) into a `libmpv\` folder
next to FluentFin.exe. `ui\FluentFin\Mpv\Libmpv.cs` loads `libmpv\libmpv-2.dll` with `LoadLibraryEx` and
`LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32`: its DLLs come from that folder or System32 only (never
FluentFin's own folder, the working directory or PATH), and the P/Invokes are bound to that handle. A package build passes
`-p:LibmpvDir=<this folder>`; the package must keep the `libmpv\` folder as it is.

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
   tools\release\libmpv\build-libmpv.ps1 -InstallPinned -Out <empty folder>\msys2-20261116-mpv-0.42.0-2 -Sources <folder>\sources
   ```

   `-InstallPinned` installs the versions in `pins.txt` (`pacman -U https://repo.msys2.org/mingw/ucrt64/<pkg>-<ver>-any.pkg.tar.zst`);
   the script then collects `libmpv-2.dll` and every DLL in its import closure (import and delay-import tables) from
   `C:\msys64\ucrt64\bin`, adds `vulkan-1.dll`, writes `BUILD-MANIFEST.txt`, and refuses a DLL whose package is not at its
   pinned version. With `-Sources` it downloads each package's source package and signature, checks the signature, copies
   the licence files and writes `SOURCES.txt`.
3. The DLL hashes must equal `BUILD-MANIFEST.txt`'s.

Every import of every DLL resolves to a DLL in the folder or a Windows DLL (an API set, or one in System32 that no GPU driver
installs); the DLLs hold no path of the build machine. FluentFin.Tests checks both on the copy next to the tests
(`LibmpvProbeTests`).

## Notes

- Composition output works: FluentFin's probe sets `vo=gpu-next`, `gpu-context=d3d11` and `d3d11-output-mode=composition`.
- Name clash to keep in mind: FFmpeg's DLLs here (`avcodec-63.dll`, `avformat-63.dll`, `avutil-61.dll`, ...) have the same
  names as Flyleaf's FFmpeg in FluentFin's `FFMpeg\` folder (a different FFmpeg 9 build). Windows reuses a loaded DLL of the
  same name, so if both players run in one FluentFin session, the second to load can be given the first one's FFmpeg.
- Licences: see `sources\SOURCES.txt` and `sources\licenses\`. FFmpeg is GPL-3.0-or-later as built and mpv
  GPL-2.0-or-later, so the folder as a whole is GPLv3, like FluentFin MPV. Its source is `vendor\libmpv\sources\`. None is
  GPL-incompatible: the Apache-2.0 ones (OpenSSL, shaderc, SPIRV-Cross, vulkan-loader, ...) are compatible with GPLv3, and
  nothing is GPL-2.0-only (jbigkit's metadata says `GPL-2.0`, but its sources say "version 2 ... or any later version";
  uchardet's says `MPL-1.1`, but it is MPL-1.1/GPL-2.0+/LGPL-2.1+). Not a licence matter, but worth knowing: MSYS2's
  libdvdread brings `libdvdcss-2.dll` (CSS decryption for DVDs), and aom/svt-av1/rav1e carry AOMedia's patent licence.
- The earlier record, shinchiro's static `libmpv-2.dll` (release `20261111`, master `git-bcde6b93cc`), imported
  `vulkan-1.dll` from the GPU driver and its bundled libraries' versions were not recorded; it is no longer used.
