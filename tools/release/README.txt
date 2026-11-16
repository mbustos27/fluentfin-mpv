FluentFin MPV {{VERSION}} for Windows 10/11 (x64), portable
===========================================================

A Jellyfin client for Windows: FluentFin's interface, with jellyfin-mpv-shim as its playback engine and mpv
drawing the video inside FluentFin's window. Not affiliated with FluentFin, Jellyfin or mpv.

Requirements
------------
- Windows 10 version 1809 or later, or Windows 11, on an x64 PC.
- Nothing to install. .NET, the Windows App SDK, the playback backend (with its own Python) and libmpv
  are all in this folder.
{{DRIVER_NOTE}}- mpv's controls (Settings > Playback > Player controls > mpv, also used when libmpv cannot load) run
  mpv.exe, which is not included: install mpv so mpv.exe is on PATH, for example with
      winget install mpv-player.mpv-CI.MSVC
  Without it the player says so and plays nothing.
- The players offered are MPV (jellyfin-mpv-shim) and Windows Media Player. Upstream FluentFin's VLC
  and Flyleaf players are not in this release; a saved choice of either plays with MPV.

First run
---------
1. Unzip the whole folder to a place you can write to, and keep its files together.
2. Run FluentFin.exe. The app is not code-signed, so Windows SmartScreen may warn the first time:
   choose "More info", then "Run anyway".
3. Add your Jellyfin server and sign in. Videos play with the player "MPV (jellyfin-mpv-shim)"
   (Settings > Playback).

Where your data is
------------------
- %LOCALAPPDATA%\FluentFinMpv: settings and saved servers, the TV journal, logs (Logs\) and the mpv
  backend's configuration (mpv-backend\: mpv.conf, input.conf, conf.json).
- FluentFin.exe --data-dir <folder> keeps all of it in that folder instead.
- Nothing is written to this folder, and nothing is installed. To remove FluentFin MPV, delete this
  folder and %LOCALAPPDATA%\FluentFinMpv.

Reporting a problem
-------------------
https://github.com/mbustos27/fluentfin-mpv/issues
Say which version (shown in the title bar), your Windows version, what you did and what happened.
The logs in %LOCALAPPDATA%\FluentFinMpv\Logs help; access tokens are removed from them, but they do
name your server's address and your items, so read them before you attach them.

Source and licence
------------------
Source code of this release: {{SOURCE_URL}}
The source of the third-party code it ships is attached to the release (SOURCES.txt says what is there).
FluentFin MPV is free software under the GNU General Public License version 3 (LICENSE).
The components it ships, their licences and sources: THIRD-PARTY-NOTICES.txt.
