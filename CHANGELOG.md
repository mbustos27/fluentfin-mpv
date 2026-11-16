# Changelog

## Unreleased

- **The program is easy to find in the release folder.** Its top now holds only `FluentFin.exe`, `README.txt`, `LICENSE`
  and `THIRD-PARTY-NOTICES.txt`, plus an `app\` folder with everything else (in 1.0.0, `FluentFin.exe` was one of 675
  files and folders). The top `FluentFin.exe` is a small launcher that starts `app\FluentFin.exe` with the same
  arguments; shortcuts go to it. Settings and data are where they were.

## 1.0.0 (first public release)

A portable zip for Windows 10/11 x64: unzip it and run `FluentFin.exe`. .NET, the Windows App SDK, the playback
backend (with its own Python 3.14.10) and libmpv are inside; nothing is installed. Its `README.txt` says what it needs
and where it keeps your data (`%LOCALAPPDATA%\FluentFinMpv`, or the folder given with `--data-dir`).

### What it is

FluentFin's WinUI 3 interface with [jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim) as its playback
engine, running headless as a child process and talking to FluentFin over a stdio protocol (`docs/BRIDGE.md`). The
server sees one session, FluentFin's, and only the backend reports playback.

- **Two kinds of player controls** (Settings > Playback > Player controls). By default FluentFin's own, drawn over an
  mpv that runs inside FluentFin (the libmpv in the zip: MSYS2's mpv 0.42.0 package and its pinned dependencies, whose
  exact sources ship with the release). Or mpv's own bar, [uosc](https://github.com/tomasklaen/uosc)
  in FluentFin's look (the shim's bar as its fallback), with mpv in a window inside FluentFin's: chosen in Settings, or
  used automatically, with a notice, when libmpv cannot load on the PC. mpv's controls run mpv.exe from PATH.
  FluentFin's controls: seek, play/pause, ±10/30 s, Skip intro and Skip credits (a button above the seek bar while the
  server marks a segment, and the S key), Next episode (with the next episode's name, off at the end of the queue),
  volume, audio and subtitle menus, full screen.
- **The shim's playback**: PlaybackInfo and the direct play / transcode decision, track selection and remembered
  tracks, next episode, the media segments behind Skip intro/credits (which ones are offered is the shim's setting),
  trickplay previews with mpv's controls, progress reporting.
- **A TV journal**: what you watched, per server and user, with ratings, reviews, rewatches, filters and CSV/JSON
  export; kept on this PC.
- **A faster Home**, loading, error and empty states with Try again, Settings > Playback, keyboard and Narrator work,
  and feedback while signing out. README.md has the full list.

### Verified

- The automated suites: the UI tests (`FluentFin.Core.Tests`, `FluentFin.Tests`), the backend's bridge tests
  (the full backend suite last ran before the final backend change, with known module timeouts under load), the
  bridge smoke test and the end-to-end harness, which drives the real backend against a fake Jellyfin server.
- The release zip itself (`tools\release\verify-package.ps1`): unzipped outside the repository and run off-screen
  against the fake server, with a PATH of Windows' own folders only (no Python, no mpv) and no backend configured:
  sign-in, an episode played and stopped with the default settings (FluentFin's controls on the bundled libmpv), the
  same with mpv's controls (mpv on PATH), mpv's controls without mpv (the page says to switch to FluentFin's controls
  or install mpv, nothing is reported), a data folder whose saved player is VLC (it plays with MPV), and the
  self-test. Every process the app
  started and every DLL they loaded came from the package or from Windows, the backend was the bundled one, nothing
  was written into the package folder, and closing the app (or killing it) ended the backend.
- A real Jellyfin server: the developer's own (remote, HTTPS, signed in as a user who is not an administrator) has
  been used with it: sign-in, browsing and playback, with FluentFin's controls.

### Known limits

- **Not code-signed**: Windows SmartScreen warns on the first start ("More info", "Run anyway").
- **Real-server paths not seen yet**: a real transcode, and its restart on an audio, subtitle or quality change; how a
  server handles a duplicate Stopped report.
- **Not verified on screen** with FluentFin's controls: HDR on an HDR display, a DPI change, independent flip.
- **When libmpv cannot load on a PC, FluentFin falls back to mpv's controls** and says why (README.txt names any
  graphics-driver DLL libmpv needs). **mpv's controls need mpv.exe on PATH** (`winget install mpv-player.mpv-CI.MSVC`);
  the package does not include it, and without it the player says so.
- **Two players**: MPV (jellyfin-mpv-shim) and Windows Media Player. Upstream FluentFin's VLC and Flyleaf players are
  not in the release (their libraries are left out); a saved choice of either plays with MPV.
- With the mpv player: no SyncPlay, and remote "play next" / "play last" are not supported.
- The backend's own messages are in English.
- **Encrypted DVDs aren't supported** (nothing played from a Jellyfin server needs it): the release has no libdvdcss.
  The `libmpv\libdvdcss-2.dll` it ships is FluentFin MPV's stub, which lets libmpv's DVD libraries load and fails every
  call.
- **With FluentFin's controls, no trickplay previews or Up Next card yet** (Skip and Next are there); mpv's controls
  have both.
- More, with where each lives: `docs/KNOWN-ISSUES.md`.
