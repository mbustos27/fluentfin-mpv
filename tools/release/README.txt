Nixie {{VERSION}} for Windows 10/11 (x64), portable (formerly Seaglass and FluentFin MPV)
=====================================================================================

A Jellyfin client for Windows: FluentFin's interface, with jellyfin-mpv-shim as its playback engine and mpv
drawing the video inside the app's window. Not affiliated with FluentFin, Jellyfin or mpv.

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
Installed with the .msi: start Nixie from the Start menu, then go to step 3. The .msi replaces an
installed Seaglass or FluentFin MPV (pin Nixie to the taskbar again if you had pinned the old one). To
uninstall it, use Settings > Apps. From the zip:
1. Unzip the whole folder to a place you can write to, and keep its files together.
2. Run Nixie.exe, the one next to this README. (The app\ folder holds the app itself; there is
   nothing in it to open.) The app is not code-signed, so Windows SmartScreen may warn the first time:
   choose "More info", then "Run anyway". For a desktop or Start menu shortcut, make one to this
   Nixie.exe.
3. Add your Jellyfin server and sign in. Videos play with the player "MPV (jellyfin-mpv-shim)"
   (Settings > Playback).

Where your data is
------------------
- %LOCALAPPDATA%\Nixie: settings and saved servers, the TV journal, logs (Logs\) and the mpv
  backend's configuration (mpv-backend\: mpv.conf, input.conf, conf.json). Nixie was called Seaglass
  before 1.2.0 (and FluentFin MPV before 1.1.0): the first start copies the old %LOCALAPPDATA%\Seaglass
  folder here (or %LOCALAPPDATA%\FluentFinMpv when there is no Seaglass folder) and leaves it as it was.
  If that copy fails, Nixie says so, uses the old folder for that session and tries again next time.
- Nixie.exe --data-dir <folder> keeps all of it in that folder instead.
- Nothing is written to this folder. From the zip nothing is installed: to remove Nixie, delete
  this folder and %LOCALAPPDATA%\Nixie. Installed with the .msi: uninstall it in Settings > Apps,
  and delete %LOCALAPPDATA%\Nixie too to remove your data. The old %LOCALAPPDATA%\Seaglass and
  %LOCALAPPDATA%\FluentFinMpv, if you have them, can go once Nixie shows your servers and journal.

Reporting a problem
-------------------
https://github.com/mbustos27/fluentfin-mpv/issues
Say which version (shown in the title bar), your Windows version, what you did and what happened.
The logs in %LOCALAPPDATA%\Nixie\Logs help; access tokens are removed from them, but they do
name your server's address and your items, so read them before you attach them.

Source and licence
------------------
Source code of this release: {{SOURCE_URL}}
The source of the third-party code it ships is attached to the release (SOURCES.txt says what is there).
Nixie is free software under the GNU General Public License version 3 (LICENSE).
The components it ships, their licences and sources: THIRD-PARTY-NOTICES.txt.
