# Cutting a release

A release is an installer and a portable zip of the same files for Windows 10/11 x64,
`Nixie-<version>-win-x64.msi` and `Nixie-<version>-win-x64.zip`, each with its `.sha256`, built by
`tools\release\build-release.ps1` and attached to a GitHub release on the tag `v<version>`. What it contains and
where each third-party file comes from: `tools\release\VENDORED.md`. The app was called Seaglass in 1.1.x and FluentFin
MPV in 1.0.x (the release files had those names); the MSI's upgrade code never changes, so each installer replaces the
ones before it.

## 1. The version

One version, `<major>.<minor>.<patch>`, set in four places (a test pins them together):

| Where | What |
|---|---|
| `ui\FluentFin\FluentFin.csproj` | `<Version>` (the one without a condition on `BuildVersion`). The title bar, the dashboard and the client version FluentFin and its backend send to Jellyfin come from it (`ui\FluentFin.Core\AppVersion.cs`) |
| `ui\FluentFin\Package.appxmanifest` | `Identity Version`, four parts (`1.0.0.0`) |
| `ui\FluentFin.Tests\AppVersionTests.cs` | `Release` |
| `tools\release\build-release.ps1` | `-Version`'s default (the script also refuses a version the csproj does not have) |

Then a `CHANGELOG.md` section for it. The backend's own `CLIENT_VERSION` (`backend\jellyfin_mpv_shim\constants.py`)
is jellyfin-mpv-shim's version and does not change with ours.

## 2. Before building

- On `main` with everything for the release merged, the working tree clean.
- Both UI test suites, the off-screen backend suite, the bridge smoke test and the end-to-end harness pass
  (`tools\README.md`).
- The libmpv folder from `tools\release\libmpv` (`vendor\libmpv\VENDORED.md`): libmpv-2.dll, its DLLs and BUILD-MANIFEST.txt,
  with its sources beside it (`sources\msys2`, `sources\mpv`). In this checkout's `vendor\libmpv\<build>` (the one
  FluentFin.csproj names), or pass `-LibmpvDir` (and `-LibmpvSources` if they are elsewhere). `-LibmpvExclude` (by
  default `libdvdcss-2.dll`, DVD decryption) leaves those libraries and their sources out. Only a stub of one may ship
  in its place: a DLL of that name whose BUILD-MANIFEST.txt owner says "stub" (no MSYS2 package), with its source and
  licence on its line in the folder's SOURCES.txt; it must export every function the folder imports from it. The build
  stops on the real library, on a stub that lacks a function, or on a DLL importing one that is not there at all.
- The build PC: Windows x64, the .NET 10 SDK, mpv on PATH (for the backend tests the build runs), git, MSYS2 with
  `mingw-w64-ucrt-x86_64-gcc` 16.2.0 in `C:\msys64` (the PyInstaller bootloader is compiled from source with it, see
  "Fewer antivirus false alarms" below), and network access to python.org, PyPI, nuget.org, GitHub and
  code.videolan.org. Nothing else: the build brings its own Python.

## 3. Build

    .\tools\release\build-release.ps1

It stops unless the working tree is clean (`git status --porcelain` prints nothing: no change, no untracked file)
and the tests pass: both UI suites and the backend's bridge tests (off-screen); a test that fails is run once more
on its own, and the build stops if it fails again. It downloads and checks the Python runtime, makes a build venv from
hash-pinned PyPI wheels, freezes the backend with
PyInstaller, publishes FluentFin (Release, x64, self-contained, Windows App SDK self-contained, unpackaged), and writes
`dist\Nixie-<version>-win-x64\`, the `.zip` and the `.sha256`, and collects the corresponding source of the GPL,
LGPL and MPL code it ships into `dist\sources\` with `SOURCES.txt` (`tools\release\sources.py`, `sources.json`: official
hosts only, PyPI hashes checked; what cannot be established exactly is listed, not guessed). Everything stays under
`dist\` (`dist\.work\` for
the intermediate files; `-Reuse` keeps its Python runtime and venv for a second run). No MSBuild node, build server or
compiler server is left running.

### The installer

`installer\Nixie.wxs` and `installer\NixieUI.wxs` (WiX 5, with `WixToolset.UI.wixext` 5.0.2, a build-time NuGet
package the build keeps in `dist\.work\wix-ext`; nothing of it is in the installed app) make a per-user MSI with a welcome
page (Install / Cancel, no licence to accept: it says where the GPL and the source are), the progress page and a finish page
with "Launch Nixie" ticked; `installer\Nixie.en-us.wxl` has the texts and `installer\make-installer-art.ps1` draws
the banner and the side picture (System.Drawing: a dusk panel with the icon's nixie tube). Settings > Apps shows the publisher "Nixie"
(the name of the program: there is no company), the version, the icon, the website (the repository), help (Issues),
update info (Releases) and a one-line comment. Every shipped .exe has company, product, description and copyright
(`Nixie.exe` from FluentFin.csproj, the launcher from `build-release.ps1`, the backend from the PyInstaller spec;
`verify-package.ps1` checks all three). There is no code signing, so SmartScreen still says "Unknown publisher".

`installer\installer-shots.ps1` takes screenshots of each installer page, on a private desktop, and only of an isolated
test build (never of the real product); see its header.

### Fewer antivirus false alarms

PyInstaller's prebuilt bootloader (the front of `fluentfin-mpv-backend.exe`) is a common false-positive trigger, so the
build compiles it from PyInstaller's own sdist (same version, SHA-256 pinned in `build-release.ps1`) with MSYS2's
MinGW-w64 gcc (UCRT64, 16.2.0, checked) and waf, and replaces the wheel's `run.exe` and `runw.exe` in the build venv
with the result before freezing; the build checks the frozen backend carries gcc's marker, and that nothing is
UPX-packed (the spec says `upx=False`; the build fails on a `UPX!` header). `tools\release\VENDORED.md` has the pins.
Check a package locally with Microsoft Defender, which uploads nothing:

    & "$env:ProgramData\Microsoft\Windows Defender\Platform\CurrentVersion\MpCmdRun.exe" -Scan -ScanType 3 -File dist\Nixie-<version>-win-x64 -DisableRemediation

Do not upload the files to VirusTotal or any other online scanner without the owner's OK.

## 4. Verify the zip

    .\tools\release\verify-package.ps1 -Zip dist\Nixie-<version>-win-x64.zip -Out <scratch folder outside the repo>

It unzips the package fresh, then runs it off-screen (a private desktop, a kill-on-close job, scratch data folders,
APPDATA/LOCALAPPDATA redirected, test media that is digital silence) against the fake Jellyfin server, with no backend
command set and a PATH of Windows' own folders only:

- the default settings (a clean data folder: FluentFin's controls on the package's libmpv): sign in, relaunch (the
  automatic sign-in), play an episode, steady playback with nothing over the middle of the video, stop, close;
- mpv's controls with mpv's folder on PATH (skipped when there is no mpv on this PC), and without it (playback must
  stop: the backend answers `connect` with `mpv_not_found` and the page says "mpv isn't installed on this PC, and mpv's
  player controls need it. Switch to FluentFin's controls ... or install mpv: winget install mpv-player.mpv-CI.MSVC",
  and nothing is reported to the server);
- a data folder whose saved player is VLC: the release has no VLC, so it must play with MPV, and opening Settings must
  leave the saved choice as it is;
- the self-test (`--mpv-selftest`, no server), and once more with FluentFin killed instead of closed (its job must end the
  backend);
- the launcher: the folder's top holds only `Nixie.exe` (the launcher, `tools\release\launcher\Launcher.cs`),
  `README.txt`, `LICENSE`, `THIRD-PARTY-NOTICES.txt` and `app\`; the self-test started through that `Nixie.exe`,
  with a data folder whose name has a space, must start `app\Nixie.exe` with the arguments unchanged and pass.

Each run records every process FluentFin starts and every DLL they load (`tools\release\proc_audit.py`) and fails on
anything from a dev checkout, a Python install or the build folder, on a token in a command line, or on a file written
into the package folder. Then look at the screenshots it saved (`<Out>\<run>\shots\`, `<Out>\SelfTest\selftest.png`).

On a second PC (or a VM) without .NET, Python or mpv, unzip and run it once by hand if you can: SmartScreen's warning,
the first sign-in, a playback.

## 4b. The upgrade test

    .\tools\release\upgrade-test.ps1 -Out <scratch folder outside the repo> [-Version <x.y.z>] [-Launch]

**It installs into the Windows account it runs in**: run it only where that is wanted (a test account or a VM), never on
the account of someone who uses the app.

Installs the published Seaglass 1.1.3 MSI (`dist\` or downloaded from its release and checked against its `.sha256`;
`-OldMsi` takes any older one, Seaglass 1.1.0 or FluentFin MPV), runs the new MSI over it, both per user and silent,
and checks: one Apps entry with the new version and name, the new files in place and the old-only ones gone (with the
rename, the old `Programs\Seaglass` folder and its Start menu entry gone), the new Start menu shortcut, the user's data
folders (`Nixie`, `Seaglass`, `FluentFinMpv`) untouched (listed before and after, only read), and that an older MSI
over the newer one is refused; then it uninstalls. **Safety:** before installing anything it asks the Windows
Installer COM API and the Apps list whether Nixie, Seaglass or FluentFin MPV is already installed; if so it installs
nothing and exits 2 ("skipped"). `-SelfCheck` runs the whole test on two tiny isolated MSIs (other names, upgrade
code, folders and registry keys; the old one named SeaglassUpgradeCheck and the new one NixieUpgradeCheck, so the
rename's paths are exercised) so the script itself can be checked on a PC that has Nixie installed. Exit 0 passed,
1 failed, 2 skipped.

## 4c. The upgrade test in Windows Sandbox (from 1.5)

    .\tools\release\upgrade-test-sandbox.ps1 -Out <scratch folder outside the repo> [-PreviousMsi <Nixie-1.4.1-win-x64.msi>] [-DryRun]

The release's upgrade test, run by the coordinating session at release (Windows Sandbox is on on the build PC). **Its
window shows on the desktop, so tell the owner first.** Nothing is installed in this account: everything happens in a
throwaway Sandbox, and the inside script (`tools\release\sandbox\upgrade-inside.ps1`) refuses to run anywhere else.
Agents run it only with `-DryRun` (writes the `.wsb` and `plan.json`, starts nothing); `tools\release\tests\test_upgrade_sandbox.py`
checks the dry runs and the refusals.

- **In:** `dist\` (this release's MSI), the previous release's MSI (`dist\Nixie-1.4.1-win-x64.msi`, or `-PreviousMsi
  <path>`: download it from its GitHub release first, with its `.sha256`, and check it), and a test build numbered higher (`-TestVersion`, default
  the patch number + 90: this release's own files in an MSI WiX builds here, never published), all mapped
  **read-only**; one results folder (`<Out>\results`) read-write. Networking, the GPU, audio and video input, the
  clipboard and printers are off: no step needs GitHub.
- **Leg 1, the old flow:** a Seaglass data folder, the previous MSI, the previous Nixie started once (it copies the
  Seaglass folder to Nixie), then this release over it as 1.4.1's updater installs it (msiexec /i with Nixie closed;
  nothing reopens it): the installed version, the Nixie data folder kept, the Seaglass folder untouched, this release
  starting.
- **Leg 2, the new flow:** this release's Restart to update, driven by `Nixie.exe --install-update <msi>` (no clicks),
  to the test build: Nixie closes, the launcher's helper installs quietly (`msiexec /passive`, logged to
  `Updates\install.log`) and opens Nixie again, without the switch.
- **No administrator:** the legs run as a standard user the script makes inside the Sandbox; `results.json` records
  its integrity level (whoami: medium), every msiexec exit code and the logs' privilege lines.
- Results in `<Out>\results\results.json` (and the logs); the Sandbox shuts itself down. Exit 0 passed, 1 failed or no
  results, 2 bad inputs, 3 Sandbox missing or already running.

## 4d. The baseline (from 1.5)

Each release measures start-up, Home's first paint and memory the same way, beside the release before it, so a change
in any of them shows up at the release that made it:

    .\tools\ui-harness\baseline.ps1 -Out <scratch> -Exe <this build's Nixie.exe>,<the last release's Nixie.exe> `
        -Backend .\backend,<the last release's backend> -Label <version>,<last version> -Runs 5

- **The builds:** both are the harness's Debug x64 build (`--artifacts-path`, with `LibmpvDir`), the last release's from
  its tag (`git archive v<last> | tar -x -C <scratch>`), each run with its own backend checkout. Release builds start
  faster than these: compare the two rows with each other, not with a release build.
- **The runs:** off-screen against the fake server (strict auth), the default layout theme, a new data folder each run,
  5 runs per build, the builds taking turns, nothing else running on the PC; the median of each.
- **Start-up:** process start (as Windows recorded it) to the main window shown (`window_ms`); the app's own
  `[perf] startup: window shown` line beside it.
- **First Home paint:** the server's answer to the sign-in to Home's first cards on screen (`home_first_cards_ms`).
- **Memory:** Nixie.exe's private bytes after a fixed browse: Home, Movies, the showcase film, Back, Shows, the showcase
  series, its Season 1 page, Back, then 5 s.

**1.5.0 beside 1.4.1** (2026-11-20 on the build PC, Windows 11 x64; Debug x64 builds of 364f8b0 and v1.4.1; all
10 runs passed every check, strict auth included). Medians, with the 5 runs' range:

| Build | Start-up: window shown (ms) | The app's own "window shown" (ms) | First Home paint (ms) | Private bytes (MB) |
|---|---|---|---|---|
| 1.4.1 | 962 (939 to 985) | 967 | 1,132 (1,077 to 1,198) | 575 (569 to 582) |
| 1.5.0 | 1,016 (976 to 1,046) | 1,014 | 1,138 (1,049 to 1,242) | 584 (582 to 587) |

1.5.0 opens its window about 50 ms later (its fastest run is slower than 1.4.1's median; not traced to one change),
paints Home as fast (the ranges overlap), and holds about 9 MB more after the browse. None of it is a release gate; the
next release measures itself beside this one.

## 5. Publish (the coordinating session, after review)

1. Tag the commit the zip was built from: `git tag -a v<version> -m "Nixie <version>"`, push the tag.
2. Create the GitHub release on that tag: title `Nixie <version>`, the notes from `CHANGELOG.md`'s section
   (what it is, what is verified, the known limits), the installer (`.msi`) and the `.zip`, each with its `.sha256`, as
   assets (every release has an installer; the zip is the portable copy of the same files), and every file of
   `dist\Nixie-<version>-sources.zip` (or its numbered parts, each under GitHub's 2 GiB limit) with
   `dist\Nixie-<version>-sources.txt` (SOURCES.txt with the parts and their SHA-256): the source of what the
   zip ships.
   For the 1.2.x releases also `dist\Seaglass-<version>-win-x64.msi` and its `.sha256` (the same MSI, a hard link the
   build makes): Seaglass 1.1.x's "Download and install" only takes an asset of that name, so with it Seaglass users
   update to Nixie from the app. Without it they are told about the release and download it from its page.
3. The package's README.txt links the source at `https://github.com/mbustos27/fluentfin-mpv/tree/v<version>`: the tag
   must exist under that name.
4. **The updater reads the release** (1.5): the installer and its `.sha256` must be assets of the release itself, named
   `Nixie-<version>-win-x64.msi`; GitHub's API reports each asset's SHA-256 (`digest`), and Nixie 1.5 refuses an installer
   whose `.sha256` disagrees with it. The release body is what "What's new" shows (simple Markdown: headings, bullets,
   links). Keep publishing the `Seaglass-<version>-win-x64.msi` copy (the build makes it): older updaters only take
   that name.
5. **Never rename or transfer the repository lightly** (bug hunt BH-140X-m3). Nixie 1.5 and later follow GitHub's
   redirect to the new name (`UpdateSource.Repository`, one constant that `tools\release` names too; a test pins them
   together), but 1.1 to 1.4 only accept `/mbustos27/fluentfin-mpv/`: renamed, they fall back to opening the release
   page with no word why. If a rename is ever wanted, wait until most people run 1.5 or later, then change the constant,
   `build-release.ps1`'s source link and the installer's links together.

## Moving a pinned dependency

- Python runtime: the new version's `url` and `hash.sha256` from https://www.python.org/ftp/python/index-windows.json
  (`pythoncore-3.14-64`) into `build-release.ps1`.
- PyPI packages: `tools\release\VENDORED.md` says how; keep the shipped ones at the versions the tests ran against.
- libmpv: rebuild it (`tools\release\libmpv`, `vendor\libmpv\VENDORED.md`) and point FluentFin.csproj's `LibmpvDir` (or
  `-LibmpvDir`) at the new folder; the build checks it against its BUILD-MANIFEST.txt and its sources.
- A new Python package or package folder in the frozen backend stops the build until it is in the notices
  (`build-release.ps1` lists the folders it knows).
