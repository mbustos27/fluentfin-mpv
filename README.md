# FluentFin MPV

A Windows Jellyfin client that combines [FluentFin](https://github.com/insomniachi/FluentFin)'s WinUI 3 interface with [jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim) as its playback engine. You browse in FluentFin, and video plays in mpv inside FluentFin's own window. The server sees a single session, FluentFin's.

This is a personal project. It is not affiliated with FluentFin, Jellyfin or mpv. It is tested by automated suites against a fake server and in daily use against a real Jellyfin server; [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md) says what is still unverified.

## Download

**Windows 10 or 11 (x64).** Get the zip from the [Releases](https://github.com/mbustos27/fluentfin-mpv/releases) page, unzip it anywhere and run `FluentFin.exe`. Nothing else needs installing: .NET, the playback backend and libmpv are inside. The app isn't code-signed, so Windows SmartScreen warns on the first start (**More info → Run anyway**). Changes: [CHANGELOG.md](CHANGELOG.md).

Everything below the feature list is for building from source.

## What it adds to FluentFin

- **mpv playback through jellyfin-mpv-shim.** Choose the player "MPV (jellyfin-mpv-shim)". The shim runs headless in a *bridge mode*. It takes care of PlaybackInfo and the transcode decision, track selection, trickplay and skip-intro, and reports progress to the server. FluentFin talks to it over a stdio JSON-lines protocol ([docs/BRIDGE.md](docs/BRIDGE.md)), and mpv draws inside FluentFin's player page.
- **A polished mpv bar.** By default the bar is [uosc](https://github.com/tomasklaen/uosc) in FluentFin's look, with the shim's own bar as the fallback. It shows the series and episode title, fills the seek bar in your accent colour with trickplay previews, offers previous, next and a Skip Intro button, keeps subtitles above the bar, takes FluentFin's keys (k, j/l, arrows, s, Shift+N/P), and the mouse wheel changes the volume.
- **FluentFin's own player controls (the default).** With Settings → Playback → Player controls → FluentFin, libmpv renders in-process into a WinUI composition swap chain. FluentFin then draws its own controls over the video: seek, play/pause, ±10/30 s, Skip intro/credits (button and S key), next episode, volume, audio and subtitle menus, full screen, and auto-hide. This mode needs libmpv: the release zip includes it, a source build needs it in `vendor\libmpv` (see `vendor/libmpv/VENDORED.md`). When libmpv cannot load, FluentFin falls back to mpv's controls and says so.
- **A TV journal.** A Serializd-style diary of what you watched, kept locally in SQLite, one journal per server and user.
  - Episodes are logged automatically when they're watched to the end, and synced from Jellyfin's played state (with exact dates when the Playback Reporting plugin is installed).
  - Each entry can have a half-star rating, a review and a rewatch flag.
  - You can filter entries and export them to CSV or JSON.
  - Ratings are sent to Jellyfin only if you turn that on.
- **Faster Home.** The libraries list is cached per session, rows are requested in parallel with a placeholder each, and the last Home page is saved and shown instantly on the next start. In a test with a 150 ms network delay, the first row appeared in about 0.18 s instead of 1.75 s.
- **Clearer states.** A loading screen with Cancel, plus error and empty states with Try again on Home, libraries, item pages, Search and the server page.
- **Settings → Playback.** Audio and subtitle language and auto-play next are your Jellyfin user settings; the skip prompt and wheel volume are kept on this PC.
- **Keyboard and screen-reader work.**
  - Cards can be reached with Tab and the arrow keys.
  - Narrator reads each card by name, and every icon button has a name.
  - Item pages offer "Resume at…" and "Play from beginning".
  - Long text wraps, with Show more.
  - Colours follow high-contrast themes.
- **Sign-out feedback.** Signing out shows progress, playback stops cleanly first, and nothing can start playing during a sign-out.
- **Faster start and first play.**
  - The window appears before the automatic sign-in finishes and says "Signing in to …" (about 0.9 s after launch instead of 1.5 s in an off-screen test).
  - After sign-in, the mpv backend starts in the background, so Play has less to wait for. With mpv's controls the backend also opens mpv ahead of time: playback started about 0.17 s after Play instead of 0.64 s, against a local test server. With FluentFin's controls (the default), the in-app mpv still starts at the first Play.
  - The Flyleaf engine and the Windows player's codec check run only when those players are used.
  - Start-up and play timings are written to the log as `[perf]` lines.

## Build from source

You need Windows 10 or 11 (x64), the .NET 10 SDK and Python 3.14:

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Python.Python.3.14
```

Then, in the repository folder:

```powershell
py -3.14 -m venv backend\.venv
backend\.venv\Scripts\pip install -e backend[all]
.\run-dev.ps1            # build and launch
```

To play video, a source build needs **one** of these:

- **libmpv**, for FluentFin's own player controls (the default). The quickest way is to copy the `libmpv\` folder out of the release zip into `vendor\libmpv\msys2-20261116-mpv-0.42.0-2\`. To build that folder yourself with MSYS2, see [vendor/libmpv/VENDORED.md](vendor/libmpv/VENDORED.md).
- **mpv**, for mpv's own controls: `winget install mpv-player.mpv-CI.MSVC`. Without libmpv, FluentFin switches to these controls automatically and says so.

`.\run-dev.ps1 -SelfTest` plays mpv's test pattern inside the player page, with no server needed.

Settings, logs and the journal go to `%LOCALAPPDATA%\FluentFinMpv`. For a separate data folder, start it with `FluentFin.exe --data-dir <folder>`.

## Development

- [docs/BRIDGE.md](docs/BRIDGE.md): the protocol between the UI and the backend. This is the contract both sides are tested against.
- [docs/UPSTREAM.md](docs/UPSTREAM.md): the upstream commits this is built from, and how to pull updates.
- [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md): what is open, accepted or not yet verified.
- [docs/JOURNAL.md](docs/JOURNAL.md): the TV journal's design.
- [tools/README.md](tools/README.md): the test harnesses:
  - the off-screen backend suite
  - the bridge smoke test
  - the end-to-end harness against the real backend
  - a fake Jellyfin server
  - an off-screen runner that drives the real app on a hidden desktop and saves screenshots and accessibility scans

Tests: `ui\FluentFin.Core.Tests` and `ui\FluentFin.Tests` (build with `-p:Platform=x64`), and the Python suite under `backend\tests`.

## License

GPL-3.0. FluentFin (`ui/`) is GPLv3 (see `ui/LICENSE.txt`), and jellyfin-mpv-shim (`backend/`) is GPLv3 (see `backend/LICENSE.md` for its own history and credits). The combined work is distributed under the GNU General Public License v3; see [LICENSE](LICENSE).

Credit for the original projects goes to their authors: FluentFin by insomniachi, and jellyfin-mpv-shim by the Jellyfin project and its contributors.
