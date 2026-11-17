# Known issues at the integration baseline

Recorded at the baseline commit (2026-11-14), after seven review/fix rounds on each side.
The final pre-commit reviews found nothing of medium severity or above left open; everything
below is low severity, an accepted cost of the contract, or not verified yet. Each entry
says where it lives so it can be picked up later. Entries marked "fixed" say what is left.

## Never verified
- **Parts of a real Jellyfin server.** The automated tests use fake servers and fake tokens.
  The user's own server (remote, HTTPS, signed in as a user who is not an administrator) has
  been in use since: sign-in, browsing and playback work, played with FluentFin's own player
  controls (the user's report, 2026-11-15). Its first try (2026-11-14) found the slow start
  (one play request per season, 31 s for a 250-episode show), fixed the same day in round 7.
  Still not seen on a real server: a real **transcode** and its restart on a track change,
  and the server's handling of a duplicate Stopped.
- **A real sign-out in the app shell** (Log out / Switch user while a video plays). Covered
  by view-model tests and the end-to-end harness (`tools\ui-harness\e2e`, scenarios
  `signout_*`), not by driving FluentFin itself: its data folder can't be redirected, so a
  fake login would land in the real FluentFin settings.
- **The libmpv path.** The release bundles MSYS2's mpv 0.42.0 as libmpv (`vendor\libmpv\VENDORED.md`),
  tested off-screen and by the user on their server. mpv's controls still run an external mpv (mpv-CI via winget).
- **A rare crash opening a Season page** (seen twice: a dev-harness Debug run on 2026-11-15 and one release
  verification run on 2026-11-16; reruns of the same scenario pass). FluentFin ends with a WinUI stowed exception
  (COMException 0x80004005, 0xc000027b in Microsoft.UI.Xaml.dll via combase.dll) even though
  `App_UnhandledException` marks it handled. No dump was kept; under investigation for 1.0.1.
- **The admin Playback Reporting charts page** uses SkiaSharp's native view, which needs the Microsoft
  Visual C++ runtime (MSVCP140, VCRUNTIME140, VCRUNTIME140_1). The release zip doesn't ship it, so that page
  needs the VC++ Redistributable on PCs without it (found from the DLL imports, not seen failing).
- **Upstream's FFmpeg DLLs in `ui\FluentFin\FFMpeg\`** (for the Flyleaf player, build N-126175-g0056dd32fd) are
  in the repository and its source archive, as upstream FluentFin ships them; the release zip leaves them out.
  Their exact source is upstream FFmpeg at that commit; a SOURCES row or `export-ignore` is due in 1.0.1.
- **With libmpv unavailable and mpv not installed**, the page still says "Switch to FluentFin's controls",
  which is already the setting; a separate message is due in 1.0.1
  (`ui\FluentFin.Core\Mpv\MpvNotFoundMessage.cs`).
- **"Player controls: FluentFin"** (the default; mpv's are the other choice; docs\research\player-controls.md). Built
  and measured off-screen only (`tools\ui-harness\composition-spike`, `tools\ui-harness\fluentfin-controls`).
  Not seen on the real desktop: the real mouse (hover, wheel event, clicks, hidden pointer), Tab order
  and Narrator, menus over the video, light/high-contrast themes; a DPI change, independent flip and
  HDR on an HDR display (Windows HDR is off on every display here). Its libmpv (`vendor\libmpv`) is
  downloaded per checkout, not in git: without it (or with one too old for composition output) FluentFin falls back to
  mpv's controls and says so.

## Player controls: FluentFin's (`ui\FluentFin\Mpv\MpvCompositionVideo.cs` and around)
- **One crash domain**: mpv runs inside FluentFin, so an mpv or GPU-driver crash ends the app (a
  `quit` only ends the core; the next playback makes a new one on a new pipe and restarts the backend).
  The mpv choice keeps mpv out of process.
- **No trickplay or Up Next card yet** with FluentFin's controls. Skip intro/credits is there
  (2026-11-16, skip-ui: a Skip button bottom right, above the seek bar, while the backend's
  `segments` event offers one, also with the bars hidden, and the S key, `skip_segment`), and
  the Next button follows the `next` event (disabled at the end of the queue, the next episode
  named in its tooltip). Which segments are offered is the backend's settings' (ask/always/off); Settings > Playback's *Show Skip intro
  and Skip credits* only hides the button (S still skips). Trickplay tiles still have no bridge
  path (the UI would ask the server). The shim skips by itself the segment types its settings set
  to always.
- **What the shim's command line gave mpv is not there in attached mode**: thumbfast/mouse scripts
  (unused: mpv's OSC is off) and `tls_*` client certificates (a server that needs one will not stream).
  `hwdec` follows the shim's setting as on the other path (its default is `no`).
- **The display is read when a playback starts**: a window moved to a display with another HDR state
  or refresh rate mid-playback keeps the first one's until the next playback.
- **The composition probe can run on the UI thread** (whether libmpv loads and does composition output):
  when no pre-warm ran first, the player page's first start runs it there, about 27 ms once warm.
- **Dev builds only: two FFmpegs with the same DLL names.** A dev build has Flyleaf's `FFMpeg\avcodec-63.dll`
  and the others beside libmpv's own in `libmpv\`; Windows loads a module of a name only once per process,
  so after Flyleaf has played, libmpv can be handed Flyleaf's FFmpeg. The release ships no Flyleaf FFmpeg
  (`tools\release\build-release.ps1`), so it cannot happen there.
- **A pause or volume change can be undone for a moment by a state echo**: volume and mute are held
  for 1.5 s after a change (the wheel's case, measured); pause is not, a stale `state` can flip the
  button back until the next one.
- **A volume above 100** (an mpv.conf's `volume=130`) stays until it is lowered: `set_volume` is 0-100,
  so a step up (wheel, Up) leaves it, only unmuting; a step down sets it within 0-100.
- **Full screen cover** (1.1.3): with FluentFin's controls a black cover lies over the whole window (the title bar row too) while
  the presenter changes, so the half-done layout (old XAML layout in the new window size) never shows; the screen is black for
  about 40-75 ms going in and 65-90 ms going out (it waits for the picture's hold to end), at most 300 ms (the UI thread's limit;
  500 ms the compositor's own, for a stuck UI thread). 1.1.2 cloaked the window instead, which showed whatever is behind it
  for 60-95 ms. To compare on a real screen: `SEAGLASS_FULLSCREEN=cover` (default), `cloak` (1.1.2's) or `none`;
  `FLUENTFIN_NO_CLOAK=1` still means none. Not judged on a real desktop yet (off-screen captures read the window's own surface).
  mpv's own controls are not hidden. Numbers and frame strips: `docs\research\fullscreen-smoothness.md`.
- **Full screen and back**: mpv renders at a new size 2 to 5 frames after the panel took it; until it has
  presented a frame of that size, the panel holds its old frame scaled to where the new picture goes (the user saw
  the picture jump by the title bar's height and then snap). Left: mpv presents on its own thread, so its first new
  frame can meet the hold, or the hold's end come just before it, for up to one frame; and WinAppSDK passes a
  maximized window through its normal size (here 1920x1023) for about 5 ms on the way in and out, seen in one
  picture of the way out. With mpv's own controls the video is mpv's window in the backend's process: that one
  flash, and about 60 ms of mpv's old picture stretched in its resized window, remain there.
  `FLUENTFIN_MPV_VIDEO_TRACE=1` logs, per frame for 2 s after a change, the panel, the swap chain and the hold.
- **Keys off-screen**: a key message posted to FluentFin reaches WinUI only through the window's
  `InputSiteWindowClass` child (the thread's focus window), not its `DesktopChildSiteBridge`; the
  fluentfin-controls harness posts there (Space, F, Escape through the page's key path).

## UI (`ui\FluentFin.Core\Mpv`, `ui\FluentFin`)
- **Play on a season starts at the series' first unplayed episode**, not the season's
  (`MpvQueue`): what FluentFin's own player does, kept so the mpv path starts where the old
  path did. The queue follows the server's series order (`GET /Shows/{id}/Episodes`, as
  jellyfin-web plays a series), where FluentFin's playlist went season folder by folder: it
  differs from the old path only for specials the server places within a season (they play
  there) and for missing episodes (left out). An episode the server's list does not have
  plays alone (the old path started at the first unplayed episode instead).
- **Sign-out with a slow or hung server**: after the 2 s bound the token is revoked while
  the backend's Stopped is still pending, so it fails with 401 (accepted cost, bounded).
- **A cast during a sign-out** (fixed 2026-11-15, signout-ux: no `play` goes out while a
  sign-out runs, and no page that exists when it ends ever plays; the sign-out's stop takes
  whatever generation is newest under the service's gate). Left: the cast still opens a
  player page that shows nothing until the sign-out navigates away (≤ ~2 s), and that page,
  removed with the shell, sends a `stop` to the idle backend after the token is revoked
  (nothing plays, so no server request).
- **Sign-out feedback** (fixed 2026-11-15, signout-ux): a progress ring and "Stopping
  playback…" / "Signing out…" in the title bar from the click until the logout request
  returns; Log out and Switch user are disabled meanwhile, and one invoked anyway does
  nothing. Never seen on screen yet (see the visual check below). A logout request that hangs
  keeps both disabled until it returns (upstream's own command already did that for itself).
- **A track or quality change that cannot be applied** (fixed 2026-11-15, signout-ux): a
  non-fatal `error` once the item plays is shown above the video for 8 s as a dismissable
  warning with the backend's own text, and playback is not touched. That text is the user's
  since osc-polish (the visual check showed the item id and "ServerUnreachable ... HTTP 500"):
  `MPV: The audio track of "Series — S6E4 · Episode" could not be changed: the server
  answered with an error. It goes on playing as before.` The backend says which change it was
  (audio, subtitles, quality; "track or quality change" when it cannot tell) and keeps ids,
  URLs and status codes in its log. The "MPV: " prefix is the UI's. Showing it pushes the video area down while it is up (airspace: XAML cannot
  draw over mpv's window). The same notice is all a failed `next` / auto-advance gets (its
  item never opened, `state: error`): once it hides, the page stays on a stopped video
  until the user leaves.
- **Visual check still to do** (the user was using FluentFin, so neither was seen): sign out
  while a video plays and look at the title bar (ring and text, then the login page), open
  the account flyout again during the wait (both items greyed out); make a track or quality
  change fail and look at the warning above the video (readable, closable, gone after 8 s,
  the video resizes back).
- **A kill that never takes but whose job close does end the backend**: the crash's own
  Stopped from FluentFin can come after the close has returned and be cut off (trade-off of
  not waiting twice for the same exit).
- **Settings > Playback with the MPV player** (2026-11-15): languages and subtitle mode reach
  it through the server (the per-item default tracks the backend falls back to), but four
  settings do not: *Show Skip intro and Skip credits*, *Change the volume with the mouse
  wheel* and *Volume step* are kept on this PC and used by the built-in players and
  FluentFin's controls only (mpv's own controls ignore them: the bridge has no field for them,
  v1), and *Play the next episode automatically* is the
  server's per-user setting, which the backend does not read (it uses its own `auto_play`,
  on). The cards say so. Needs a bridge field or command, or the backend reading the user's
  configuration at `connect`.
- **The mpv loading screen ends when the backend takes `play`**: from then on mpv's window
  covers the page while the item opens (PlaybackInfo, stream start), black until the first
  frame. Keeping the window hidden but sized until `playing` would need an `MpvVideoHost`
  change and a harness check.
- **Test gaps**: nothing automated drives a real root-frame sign-out, and the player page's
  `Unloaded` hook is covered only through its rule (`FluentFin\Mpv\PlayerPageRemoval.cs`,
  tests in `VideoPlayerViewModelMpvTests.PageRemoval.cs`; signout-ux), not through WinUI
  raising `Unloaded` on a real page; four reviewer mutations survive
  (the sign-out wait's 2 s bound, the redundant `_mpvLeaving` early return, the
  `ExitHandled` term in `Owed`, the re-check after subscribing in `WaitForStopReportsAsync`).

## mpv's bar in bridge mode (uosc, `backend\fluentfin`, osc-polish)
- **Seen off-screen only** (`tools\backend-harness\osc\osc_harness.py` on a private desktop,
  PrintWindow shots at 100 % and 150 % text size, windowed and full screen): not yet with the real
  mouse on the user's desktop, nor at a real 150 % display scale (the harness sets uosc's scale;
  mpv's own HiDPI scale on top of it is not measured), nor in light or high-contrast themes.
- **uosc's icons take the accent**: uosc has one foreground colour for icons, sliders and the
  seek bar's fill, so FluentFin's accent fills the elapsed time and also tints the icons (white
  icons would make the fill white too).
- **uosc's own menus** (audio, subtitles, chapters) list mpv's tracks, as the fork's buttons
  cycle them: a track mpv cannot see (a transcode's burned-in subtitle) is not in them, and a
  pick there is mpv's, not a renegotiation (FluentFin's own pickers and remote `set_audio`/
  `set_subtitle` go through the shim). Its subtitle menu also offers Load and Download, which
  need uosc's helper (`ziggy`, not vendored) and fail.
- **The Skip prompt is mpv's OSD message** ("Skip Intro  (S)", top left, 4 s) beside the bar's
  Skip button; uosc has no toast of its own. The button and prompt text come from the shim's own
  translations, the bar's tooltips are uosc's English.
- **Attached mode keeps the fork**: FluentFin's in-process mpv gets no construction option, so no
  `osd-fonts-dir` for uosc's icon font. `MpvInProcessCore` would need it (and `volume-max=100`) to
  run uosc there; with FluentFin's controls mpv's bar is hidden anyway.
- **A frozen backend** (`fluentfin-mpv-backend.exe`, later) must ship `backend\fluentfin\mpv`
  beside the package (`fluentfin_bar.ASSETS`); without it the bar falls back to the fork.
- **A uosc whose start fails** (a broken file) is found 2 s after it loaded and the fork
  loads instead (`fluentfin_bar.load`): for those 2 s, and for a uosc that fails later, there is
  no bar. A uosc that is merely slower than 2 s to start is taken for running.
- **The restore after a hide is deferred 150 ms** (`fluentfin.lua`): a `sub-pos` the bridge writes
  with `set_controls` "ui" lands within that and is kept; a later one is not told apart from the
  shim's. The race it closes was seen once in about ten runs and cannot be forced in a test.

## Start-up and pre-warm (stream `startup-perf`)
- **The window before the automatic sign-in was run, off-screen only**: the real Seaglass.exe
  (Release, private desktop, its own data folder, the dev-harness fake server; before = main with
  the same data-dir change, 5 runs each, interleaved, on a machine busy with other test runs)
  shows its window 910 ms after the process started against 1469 ms before (850 / 1438 ms at
  50 ms per request), and the sign-in request now goes out after the window, not before; the
  first home row is asked for ~100 ms sooner; the pre-warm then takes ~0.6 s in the background
  with no window of mpv's inside FluentFin's. Never looked at on the user's desktop: the
  "Signing in to …" line was timed, not seen. Not run at all: the lazy Flyleaf start (the first
  Flyleaf play now starts FFmpeg: ~128 ms warm, ~0.9 s the first time after an install; unit
  tests only) and time-to-first-frame in the app (the e2e harness times PlayAsync to `playing`,
  below).
- **Pre-warm follows sign-in only**: choosing the MPV player in Settings while signed in does
  not pre-warm (the first play starts the backend, as before; the next sign-in pre-warms).
  Choosing another player leaves an idle pre-warmed backend running until sign-out or close.
- **With FluentFin's player controls (attached) the pre-warm only starts the backend**: the
  `connect` needs FluentFin's in-process mpv, which is made at the first play, so that play
  still waits for the connect (~0.5 s on a real server) and the in-process mpv. The setting is
  read when the pre-warm runs (2 s after sign-in): switching to FluentFin's controls after a
  pre-warm that connected restarts the backend at the next play (the window changed), as any
  switch of that setting does. Never run with libmpv here (this worktree has none): unit tests
  only.
- **A pre-warmed backend's first play** has mpv build its video output then, alongside the
  start's server requests (the price of no mpv window, GPU surface or attached input queues
  while idle): e2e PlayAsync to `playing` 165 ms loopback / 224 ms at 40 ms per request,
  against 66 / 198 ms with the output built at `connect` and ~640 / ~850 ms cold. If that
  first-play difference matters more than ~40 MB and the attached queues while idle, the UI
  can drop `prewarm` from its pre-warm `connect` (one argument in `MpvBackendHost.PrewarmAsync`).
- **mpv before 0.41** keeps the output after the first file only because `force-window` is
  stored (it is not acted on while idle there); only mpv 0.41 is installed, so untested.
- **O7 (players and subscriptions never disposed) not done**: it does not apply to the mpv
  player (no IMediaPlayerController is made; leaving the page stops playback and the backend
  stays, by design), and for VLC, Flyleaf and the Windows player the report's dispose-order
  risk needs its playback matrix and soak on a real server, which could not run here.

## Backend (`backend\jellyfin_mpv_shim\bridge*.py`, marked upstream diffs)
- **A hung trickplay fetch can push `shutdown` past its 8 s deadline** (pre-existing
  upstream: `trickplay.stop()` joins with no timeout, called from `terminate`). Seen at
  ~11 s, after the Stopped was already delivered; the UI kills the backend at 10 s, so no
  server state is lost, but BRIDGE.md §3's ~9 s bound does not hold for that endpoint.
  Fix: run `terminate` on a thread joined to the deadline.
- **A failed track/quality change on a transcoded item plays on only as long as mpv's
  buffer lasts** (upstream ends the old transcode before negotiating the new one; BRIDGE.md
  §5 "Known limit").
- **A `play` that fails leaves a stale `replaced` mark** on the item that plays on; if that
  item is later lost without another mark, `ended.reason` says `replaced` instead of
  `stopped` (cosmetic; the UI acts only on `error`).

- **A held end that runs at a `stop` or shutdown skips the finished callback's
  interrupted-stream branch** (`_end_held`): for a stream that failed mid-item while a `play`
  was pending, the position reported is right but the "server stopped sending it" notice is
  missing.
- **A `log-file=` in the backend's `mpv.conf` writes the access token to that file**
  (`%LOCALAPPDATA%\Seaglass\mpv-backend\mpv.conf`; found 2026-11-15 by the dev-harness review). A log file
  makes mpv log verbosely, and mpv then logs `Set property: http-header-fields=["Authorization: MediaBrowser ...
  Token=\"...\""]` when the shim sets the request headers (reproduced with the fake server's token). Anyone who
  adds `log-file=` for debugging gets the real token in that log, against BRIDGE.md's rule that the token
  stays out of logs. Not fixed (backend): e.g. hide that property from mpv's log, or refuse/strip `log-file`
  from the config the bridge hands mpv. The off-screen harness no longer writes `log-file` and removes one it
  finds in its own data folder's `mpv.conf`.
- **`play_file` (`--dev` only) does not end a held end** (`cmd_play_file`): a `play_file`
  that supersedes a `play` whose item's end is held (the last item at its end) calls only
  `stop()`, which returns early there, so that item stays current under the file with no
  Stopped until something else ends it: a later `play` reports it and a `stop` ends it
  (`_end_held`), but the shutdown reports it with mpv's position in the file, not the
  item's. Fix if it matters: `_end_held(pm)` after that `stop()`.

## Test harnesses (`tools\`)
- **`Send-Keys` checks focus, then sends** (`tools\ui-harness\common.ps1`): the keys (f,
  Esc, q) go to whatever window is in front when they are injected, so a focus change in
  that instant could still send them elsewhere. Run the UI harnesses only when you are not
  typing in another app.
- Process-age matching uses `-ge` on creation times, and CIM times pass through local time
  (`ToFileTime`): during the DST fall-back hour matching fails safe (nothing is tracked and
  cleanup falls back to the job object).
- `backend\tools\bridge_smoke.py` waits only 5 s for `state: paused` after `pause`; with a
  build running alongside it missed once (1 of ~15 runs, 2026-11-14) and passed on rerun.
- `run-dev.ps1` and `rundev-close.ps1` close every running copy of **this checkout's** dev
  build by design: don't run them while you are using the dev build.

## Privacy note (upstream behaviour, unchanged)
- The shim asks `checkip.amazonaws.com` for the public IPv4 address on each play when the
  server address is a public IPv4 (its `is_local` check). Not changed in the baseline;
  removing it was left out of the optimization plan on purpose.

## Release package (`tools\release`, docs\RELEASE.md)
- **Not code-signed**: SmartScreen warns on the first start ("More info", "Run anyway"), and
  `backend\fluentfin-mpv-backend.exe` is an unsigned PyInstaller build. Needs a code-signing certificate.
- **libmpv's DLLs**: the release's `libmpv\` folder is our own build (`tools\release\libmpv`, `vendor\libmpv\VENDORED.md`),
  libmpv-2.dll and every DLL it loads; Libmpv.cs loads them from that folder only. `tools\release\libmpv_check.py`
  checks the folder against its BUILD-MANIFEST.txt and lists any graphics-driver DLL it imports (`vulkan-1.dll` and
  the like), which README.txt then names as a requirement. On a PC where libmpv does not load, FluentFin falls back to
  mpv's controls and says why.
- **mpv's controls need mpv.exe on PATH** (or a WinGet install): the package has no mpv.exe. Without one the
  backend's `connect` answers `mpv_not_found` (BRIDGE.md section 2) and the player page says to switch to
  FluentFin's controls or install mpv, with the winget line (`Core\Mpv\MpvNotFoundMessage`). When FluentFin fell back
  to mpv's controls because libmpv would not load, the first of those does not help on that PC.
- **Only the MPV and Windows Media Player players**: the release leaves out libvlc and Flyleaf's FFmpeg, and FluentFin
  offers only the players whose libraries are there (`Core\Settings\MediaPlayerAvailability`); a saved VLC or Flyleaf
  choice plays with MPV and stays saved. Their managed assemblies (LibVLCSharp, FlyleafLib) still ship: the XAML
  references them. A dev build has both libraries and offers all four.
- **The backend is English only**: the shim's `.mo` catalogs are built by `gen_pkg.sh`, not in git, not shipped.
- **Encrypted DVDs aren't supported** (irrelevant for a Jellyfin client: nothing it plays from a server needs it). The
  release has no libdvdcss (DVD decryption): libmpv-2.dll imports libdvdread-8.dll (and libdvdnav-4.dll), and
  libdvdread-8.dll imports seven functions of `libdvdcss-2.dll`, so the `libmpv\` folder ships our stub of that name
  instead, GPL-3.0, its source in the release's sources. It has libdvdcss's functions and every call fails.
  `build-release.ps1 -LibmpvExclude` (default `libdvdcss-2.dll`) leaves the real one and its source package out and
  keeps only a DLL that BUILD-MANIFEST.txt marks a stub (its owner says "stub" and is no MSYS2 package);
  `libmpv_check.py` fails the build on the real one, or on a stub missing a function the folder imports from it, and
  `verify-package.ps1` checks the unzipped folder again.
- **libmpv's sources**: every DLL of the `libmpv\` folder has its source in the release's sources (`dist\sources\libmpv`:
  the MSYS2 source packages with their signatures, mpv's tarball; `tools\release\sources.py` fails the build when a
  DLL's source is missing). The MSYS2 packages' signatures are copied, not checked by the build.
- **Verified off-screen only**: the zip, unzipped outside the repo, against the fake server with nothing on
  PATH but Windows (`verify-package.ps1`), on this PC. Not yet run on a PC without the .NET SDK or a GPU driver.

## This PC's test environment
- 8 environmental failures in the backend suite: `test_mpvtk_pilfont` ×2 (an installed font
  Pillow cannot read), `test_audio_settings` ×6 (the fake player models the libmpv path
  only).
- The long `test_shell_*`, `test_live_tv`, `test_dpi_matrix` and `test_playlist_edit`
  modules can exceed the parallel runner's 300 s per-module limit when the machine is busy
  (late on 2026-11-14 even when idle); each passes alone with
  `run_suite.py --serial --timeout 1500 -- -k <module>`, and timed side by side they are
  exactly as fast on the baseline as on the pre-round copies.
