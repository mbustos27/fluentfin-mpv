# Nixie

*Nixie, formerly Seaglass and FluentFin MPV.* Named for the 1950s nixie tube, whose digits glow neon orange-pink. The old names and the GitHub repository name (`fluentfin-mpv`) stay in links and history.

A Windows Jellyfin client with a glassy, Apple TV-inspired look. It combines [FluentFin](https://github.com/insomniachi/FluentFin)'s WinUI 3 interface with [jellyfin-mpv-shim](https://github.com/jellyfin/jellyfin-mpv-shim) as its playback engine: you browse in Nixie, and video plays in mpv inside Nixie's own window. The server sees a single session, Nixie's.

This is a personal project. It is not affiliated with FluentFin, Jellyfin or mpv. It is tested by automated suites against a fake server and in daily use against a real Jellyfin server; [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md) says what is still unverified.

## Download

**Windows 10 or 11 (x64).** From the [Releases](https://github.com/mbustos27/fluentfin-mpv/releases) page:

- **Installer** (`.msi`): installs for your Windows user, with no administrator rights, and adds Nixie to the Start menu. Uninstall it in Settings → Apps.
- **Portable** (`.zip`): unzip it anywhere and run `Nixie.exe` at the top of the folder (the app's own files are in `app\`).

Nothing else needs installing: .NET, the playback backend and libmpv are inside. Either way your settings and data go to `%LOCALAPPDATA%\Nixie`. The app isn't code-signed, so Windows SmartScreen warns the first time (**More info → Run anyway**). Changes: [CHANGELOG.md](CHANGELOG.md). Release files are named `Nixie-<version>-win-x64.zip` and `.msi`. Coming from Seaglass 1.1.x or FluentFin MPV 1.0.x: installing the Nixie `.msi` replaces it (pin Nixie to the taskbar again if you had pinned the old one), and the first start copies your old data folder (`%LOCALAPPDATA%\Seaglass`, or `%LOCALAPPDATA%\FluentFinMpv` when there is no Seaglass folder) to `%LOCALAPPDATA%\Nixie`, leaving the old one in place. Your saved servers and sign-ins come along: the server knows this PC as the same device.

Everything below the feature list is for building from source.

## What Nixie adds to FluentFin

- **The glass look.** Frosted glass on the navigation, title bar, search, menus, tooltips, dialogs and page sections, with the current title's artwork softly tinting the whole window. Posters and artwork stay solid. Settings → Appearance → Glass: **Automatic** (follows Windows' transparency, animation and high-contrast settings), **Full**, **Reduced** (no motion) or **Solid** (no translucency). The design and the research behind it: [docs/design/DESIGN.md](docs/design/DESIGN.md).
- **Home, item pages and the player, redesigned.**
  - **Home:** a hero of up to five items that never autoplays trailers, and poster rows whose cards lift on hover.
  - **Item pages:** a full-width backdrop with the title's logo, info chips (year, rating, runtime, resolution, HDR, audio), and Play as the one highlighted action.
  - **The player's own controls:** on a dark gradient, with Skip intro/credits and Next as buttons, and a subtitle **Text size** slider (50–200 %, remembered).
- **Mouse and keyboard.**
  - Mouse buttons 4 and 5, Alt+←/→ and the keyboard's Back and Forward keys go back and forward between pages.
  - Shortcuts everywhere: search (Ctrl+F or /), Settings (Ctrl+,), and the usual player keys (Space/K, J/L, ←/→, M, C, V, B, F, 0–9 and more).
  - Press **?** or **F1** (or Settings → About → Keyboard shortcuts) for the full list, or see [docs/SHORTCUTS.md](docs/SHORTCUTS.md).
- **mpv playback through jellyfin-mpv-shim.** The player is "mpv" (the default). The shim runs headless in a *bridge mode*. It takes care of PlaybackInfo and the transcode decision, track selection, trickplay and skip-intro, and reports progress to the server. FluentFin talks to it over a stdio JSON-lines protocol ([docs/BRIDGE.md](docs/BRIDGE.md)), and mpv draws inside FluentFin's player page.
- **A polished mpv bar.** By default the bar is [uosc](https://github.com/tomasklaen/uosc) in FluentFin's look, with the shim's own bar as the fallback. It shows the series and episode title, fills the seek bar in your accent colour with trickplay previews, offers previous, next and a Skip Intro button, keeps subtitles above the bar, takes FluentFin's keys (k, j/l, arrows, s, Shift+N/P), and the mouse wheel changes the volume.
- **Nixie's own player controls (the default).** With Settings → Playback → Player controls → Nixie, libmpv renders in-process into a WinUI composition swap chain, and Nixie draws its own controls over the video: seek, play/pause, ±10/30 s, Skip intro/credits (button and S key), next episode, volume, audio and subtitle menus (with text size), full screen, and auto-hide. This mode needs libmpv: the release includes it, and a source build needs it in `vendor\libmpv` (see `vendor/libmpv/VENDORED.md`). When libmpv cannot load, Nixie falls back to mpv's controls and says so.
- **A TV journal.** A Serializd-style diary of what you watched, kept locally in SQLite, one journal per server and user.
  - Episodes are logged automatically when they're watched to the end, and synced from Jellyfin's played state (with exact dates when the Playback Reporting plugin is installed).
  - Each entry can have a half-star rating, a review and a rewatch flag.
  - You can filter entries and export them to CSV or JSON.
  - Ratings are sent to Jellyfin only if you turn that on.
- **Faster Home.** The libraries list is cached per session, rows are requested in parallel with a placeholder each, and the last Home page is saved and shown instantly on the next start. In a test with a 150 ms network delay, the first row appeared in about 0.18 s instead of 1.75 s.
- **Clearer states.** A loading screen with Cancel, plus error and empty states with Try again on Home, libraries, item pages, Search and the server page.
- **Settings in titled groups.** Appearance, Playback, Audio and subtitles, Servers and accounts, Integrations, Advanced and About. Audio and subtitle language are your Jellyfin user settings; the skip prompt and wheel volume are kept on this PC. **Settings → About** has the version and the licence, Check for updates, Keyboard shortcuts, Report a problem and Contribute; **Settings → Integrations** opens Jellyseerr.
- **The navigation rail** holds only places: Home, Libraries, Diary and Settings.
- **Item menus that only offer what works.** The More menu lists only entries that do something; the editing tools (metadata, images, identify, refresh) are shown to server administrators only. **Delete** asks first and names the item, and removing a server or a saved sign-in names it too; Cancel is the default.
- **Keyboard and screen-reader work.**
  - Rows, season pages' episode lists and library grids are one Tab stop each, and Tab comes back to the card or episode you left; the arrow keys, Home/End (and PageUp/PageDown in a grid) move inside them, and Up/Down leave them for what is above or below.
  - An episode is one stop: Enter plays it, and the Menu key or Shift+F10 opens its menu.
  - Narrator reads each card by name with its place ("12 of 320"), each episode as "S1E4 Title, watched", every icon button has a name, and an item page's title logo is read as its heading.
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

- **libmpv**, for FluentFin's own player controls (the default). The quickest way is to copy the release zip's `app\libmpv\` folder into `vendor\libmpv\msys2-20261116-mpv-0.42.0-2\`. To build that folder yourself with MSYS2, see [vendor/libmpv/VENDORED.md](vendor/libmpv/VENDORED.md).
- **mpv**, for mpv's own controls: `winget install mpv-player.mpv-CI.MSVC`. Without libmpv, FluentFin switches to these controls automatically and says so.

`.\run-dev.ps1 -SelfTest` plays mpv's test pattern inside the player page, with no server needed.

Settings, logs and the journal go to `%LOCALAPPDATA%\Nixie`. For a separate data folder, start it with `Nixie.exe --data-dir <folder>`.

## Development

- [docs/BRIDGE.md](docs/BRIDGE.md): the protocol between the UI and the backend. This is the contract both sides are tested against.
- [docs/UPSTREAM.md](docs/UPSTREAM.md): the upstream commits this is built from, and how to pull updates.
- [docs/KNOWN-ISSUES.md](docs/KNOWN-ISSUES.md): what is open, accepted or not yet verified.
- [docs/JOURNAL.md](docs/JOURNAL.md): the TV journal's design.
- [docs/design/DESIGN.md](docs/design/DESIGN.md): the glass design rules (tokens, glass modes, motion).
- [docs/SHORTCUTS.md](docs/SHORTCUTS.md): every keyboard and mouse shortcut.
- [docs/RELEASE.md](docs/RELEASE.md): how a release is built, verified and published.
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
