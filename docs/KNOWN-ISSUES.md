# Known issues at the integration baseline

Recorded at the baseline commit (2026-11-14), after seven review/fix rounds on each side.
The final pre-commit reviews found nothing of medium severity or above left open; everything
below is low severity, an accepted cost of the contract, or not verified yet. Each entry
says where it lives so it can be picked up later. Entries marked "fixed" say what is left.

## Fix queue (next fix release)
Found by the 1.5 reviews (2026-11-20); not fixed in 1.5 or 1.5.1 (1.6.1 takes them).
- **Home's row names are not in sentence case** (button review L6): "Continue Watching" and "Next Up" style names where the
  rest of the app says "Continue watching". Held back in 1.5 because the harness finds rows by name (`then-search.ps1`,
  `drive.ps1`'s Home step, the keys matrix): rename the rows and the scripts' names together. Left out of 1.5.1 too: 61
  names in 16 harness scripts and the keys matrix in four themes, more than a fix release's hour.
- **"/" can, rarely, type itself into the search box it opens** (found at the 1.5 integration). Once in about 60 presses
  (1 of 29 keys-matrix runs across the 1.5 streams; 0 of 30 in a targeted off-screen repro) the search sent "/Signal":
  the "/" that focused the box was also typed into it. `WindowShortcuts` handles "/" in
  PreviewKeyDown and focuses the box at once (`TitleBarControl.FocusSearch`); the key's character (WM_CHAR, made by
  TranslateMessage after the key-down, as for a real keyboard) usually does not reach the box, but sometimes does. Ctrl+F
  and Ctrl+E make no character. Fix: when "/" opens the box, swallow that key press's character (or focus the box once
  the character has been dispatched); the keys matrix should check the box is empty after "/".

From the 1.5.0 bug hunt (`docs\reports\Bug hunt 2026-11-20 (1.5.0).md`): 1.5.1 fixed every finding but this part of one:
- **BH-150-m3 (the rest):** "With <name>" asks only for films and shows, so a guest star credited on episodes only finds
  nothing there (1.5.1 says so in the empty state: "Guest roles on single episodes aren't shown here"). Ask `Episode` too,
  grouped per show ("in 3 episodes").

Found while making 1.5.1:
- **then-140-arrival.ps1 assigns drive.ps1's `$before`** (lines 127 and 148; `test_then_names.ps1` fails on it, on 1.5.0
  too): harmless today (drive.ps1 reads its `-Before` before the -Then script runs); rename the variable.

From the 1.5.1 review (2026-11-21):
- `ui\FluentFin\ViewModels\TitleBarViewModel.cs:220` `SignInAgain`: the in-place sign-in with a saved password has no test
  (the bug hunt's probe passed it in Classic).
- `ui\FluentFin.Core\Services\JellyfinClient.cs:70`: the websocket keeps redialling with the dead token until the next sign-in.
- `ui\FluentFin.Core\InstallDeviceId.cs:29`: the program folder is part of the device id, so each portable version folder
  (and each drive letter of a USB copy) is a new device; a user the administrator limits to chosen devices is refused
  after the update (the bug hunt's BH-151-m5 too). Store the id in the data folder the first time.
- `ui\FluentFin.Core\Services\SignInWatch.cs:48`: a check that started before a new sign-in can end the new session (a
  tiny window: its 401 is for the old token, the mark is global).

From the 1.5.1 bug hunt (`docs\reports\Bug hunt 2026-11-21 (1.5.1).md`):
- **BH-151-m1** `ui\FluentFin.Core\ViewModels\SeriesViewModel.cs:299`: a series page's season list keeps "Couldn't load the
  episodes" with Try again after the sign-in ended (all four themes' hard-coded Try again).
- **BH-151-m2** `ui\FluentFin.Core\ViewModels\HomeViewModel.cs:752`, `ServerConfigurationViewModel.cs:28`: action
  messages (remove, Save, the toggles) still say "didn't answer" or "check that it is running" after the sign-in ended.
- **BH-151-m3** `ui\FluentFin.Core\InstallDeviceId.cs`: two windows of one copy share a device id (no single-instance
  guard), and each one's in-place Sign in again ends the other's session, back and forth.
- **BH-151-m4** `backend\jellyfin_mpv_shim\intro_sources.py:258` (`_save`): a 1.5.0 backend saving learned-intros.json
  drops 1.5.1's "moves" marks (a 1.5.0 portable copy beside 1.5.1, or a downgrade).
- **BH-151-m5** `ui\FluentFin.Core\InstallDeviceId.cs:29`: each portable version folder and USB drive letter is a new
  device; per-device allow lists refuse it (the review's line above).
- **BH-151-m6** `ui\FluentFin\Services\UpdateNoticeService.cs:314`: the release package's refusal of `--install-update`
  without the marker is only unit-tested; the Sandbox's leg 2 (`upgrade-inside.ps1:269`) proves only the allowed side.
- **BH-151-m7** `ui\FluentFin.Core\ViewModels\LibrariesDisplayViewModel.cs:51`: Display's Save posts a one-field
  `Metadata` over the server's whole metadata configuration, on every Save (a key a later Jellyfin adds would be reset).
- **BH-151-m8** `backend\jellyfin_mpv_shim\player_reporting.py:432` (`send_timeline`): the sign-in ending mid-playback
  refuses the progress and Stopped reports, the player says nothing, and the place and watched state are lost.

Left out of 1.5.1:
- **Music in search, with playback** (BH-151-M2): songs and albums were taken out of search because nothing plays them.

## Hand checks, recorded
From 1.5, one line per check and release: pass, fail, or not run. They need a real desktop and real hands (off-screen
runs post keys and cannot move the real cursor or hear sound).
- **1.5.0**
  - Space before any click in the player, then move the mouse (the controls must come back): not run, for the owner.
  - Esc inside YouTube's player in the trailer theatre (the theatre must close): not run, for the owner.
  - One real trailer plays in the theatre, with sound: not run, for the owner.
  - Restart to update on a real install: not run on the owner's own install; the Sandbox upgrade test below covers it
    off the owner's account.
  - The Sandbox upgrade test (`upgrade-test-sandbox.ps1`): **passed on 2026-11-21, after the release** (1.5.0 shipped
    first, owner: "Publish now"; the first two tries failed on the test script and a stuck Sandbox). As a standard user
    at medium integrity:
    - leg 1: 1.4.1 installs, copies the Seaglass folder at its first start; 1.5.0 installs over it by the old flow,
      reopens nothing, keeps the data, leaves the Seaglass folder alone, and starts;
    - leg 2: Restart to update closes Nixie, the helper installs 1.5.90 quietly and opens Nixie once, without the
      switch, and the reopened Nixie logs the result (installer exit 0).

    Its three failures were the test's own, fixed on main: a start with no server logs nothing (the leg 1 log check
    is gone; leg 2's line proves the log), Windows PowerShell 5.1 threw on `@($checks)` so no results.json was written
    and the Sandbox didn't shut down, and the tester's exit code read empty. The test build is 1.5.0's files in an
    installer numbered 1.5.90, so the reopened app logs "None", not "Updated": the "Updated" notice is first seen for
    real on 1.5.0 to 1.5.1.
- **1.5.1**
  - The Sandbox upgrade test with its new leg 3 (1.5.0's own Restart to update to 1.5.1, the "Updated" line): not run
    yet, for the coordinator at release.
  - Sign in again on a real server after signing Nixie out there (Dashboard > Devices): not run, for the owner (the QA
    servers passed it off-screen: `then-signin.ps1` on 10.11.11 and 12.4.0).

## Nixie 1.5.1: the fix release
- **The release's final pass** (2026-11-21, off-screen, a Debug x64 build of the branch after its review fixes, the fake
  server's strict auth on in every run): the keys matrix 538/538 in Classic, Drive-in and Editorial and 576/576 in
  Cinematic; 44 theme runs (then-140-checks, arrival, then-search in four themes and Cinematic Light, then-update in four
  themes with the full flow and Restart to update, the season switch with its latency rules, rewatch, paging, T5, the Alt
  keys, Classic's states, then-signin in four themes) all green but the skeleton check (Cinematic at 1920, the 1.5.0
  issue below) and one Editorial season run that stopped on a screenshot's GDI+ error and passed 83/83 run again; on the
  QA servers (10.11.11 and 12.4.0) then-search-live 14/14, the playlist and transcode run 44/44, then-signin 16/16 and
  the smoke 20/20 on each; FluentFin.Core.Tests 4,598 and FluentFin.Tests 1,559, all passed; the end-to-end harness 26/26,
  the transitions check 4/4 and the bridge smoke 75/75; the backend suite (5,885 tests in 300 modules) with only the 8
  environment failures (`test_audio_settings` 6, `test_mpvtk_pilfont` 2) once the 20 modules its 300 s limit cut under
  the harness load were run again alone, all passed (`test_shell_playback` 165 in 1,509 s); the release build's own run of
  the suites (and the bridge tests, 472) green, after a first try beside those reruns lost one UI test to the load.
- **What it fixed:** the 1.5.0 bug hunt's three Majors (BH-150-M1 Classic Home's notice under the top bar, M2 a refused
  sign-in read as a dead server and the per-PC device id, M3 Enter, Enter on a show) and its Minors m1, m2, m4 to m8 (m3
  in part), "Skip intro" at 0:00 on shows whose intro moves, Display's date-added choice, and show-level anime ids for
  later seasons. CHANGELOG.md has them in words.
- **Signing in again** (BH-150-M2): the app takes a sign-in as ended only when the server answers 401 to the request and
  again to `GET /Users/Me` (Jellyfin also answers 401 with a good token, for Quick Connect turned off). A proxy in front
  of the server that answers 401 on every path for its own sign-in reads as an ended sign-in too; a 403 never does.
  Pages that loaded before the sign-in ended keep their content until they load again (R); the player and every load
  after it say it.
- **The device id after the update:** each copy is a new device on the server (the PC's id hashed with the copy's data
  and program folders and the Windows user). The 1.5.0 entry stays in the server's Devices list until removed there,
  its token unused (Nixie never stored it). A portable copy unzipped to a new folder for each version is a new device
  each time, and so is a copy run with another `--data-dir`.
- **Intros that move:** the mark is per season, from the viewer's own skips, the first in each episode, in the intro's
  window (the first 10 minutes, a 30 to 150 s jump), compared only with skips of a similar length (within 15 s: a recap
  skipped in one episode is not the intro of the next). A viewer who jumps into a scene in the first 10 minutes, as far
  as they usually skip the intro, marks the season too, and loses its learned spot (the button that never skips by
  itself). A hand skip that overshoots the intro by much more than usual is taken for another part and missed. The 1.6
  skip stream rewrites the learner and keeps the rule.
- **Search's new kind** (Videos: home videos and music videos) is one request more per search. Songs and albums are left
  out (BH-151-M2): an album found would open with Play off, as Nixie plays no music; music search comes with playback.
- **Not checked live:** Display's date-added choice against a real server (`POST /System/Configuration/metadata` changes
  the shared QA servers' settings); the code path is the client's own, unit-tested.

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
  for 60-95 ms. To compare on a real screen: `NIXIE_FULLSCREEN=cover` (default), `cloak` (1.1.2's) or `none`
  (`SEAGLASS_FULLSCREEN`, its name before 1.2.0, still works); `FLUENTFIN_NO_CLOAK=1` still means none. Not judged on a real desktop yet (off-screen captures read the window's own surface).
  mpv's own controls are not hidden. Numbers and frame strips: `docs\research\fullscreen-smoothness.md`.
  **The 300 ms limit can lift the cover early** when mpv is slow to draw its first frame of the new size: the held, scaled
  picture then shows until it does (seen once in 24 off-screen switches, a switch out that waited about 640 ms for mpv).
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

## Nixie 1.5.0: search, updates, fixes
- **The release's final pass** (2026-11-20, off-screen, a Debug x64 build of the merged tree, the fake server's strict
  auth on in every run): the keys matrix 538/538 in Classic, Drive-in and Editorial and 576/576 in Cinematic; the
  contrast matrix's 16 runs, 4,129 labels, none under its floor; 41 theme runs (then-140-checks, arrival, then-search in
  four themes and Cinematic Light, then-update in four themes with the full flow and Restart to update, the season
  switch with its latency rules, rewatch, paging, T5, the Alt keys, Classic's states) all green but the skeleton check
  below; on the QA servers (10.11.11 and 12.4.0) then-search-live 14/14, the playlist and transcode run 44/44 and the
  smoke 20/20 on each; FluentFin.Core.Tests 4,560 and FluentFin.Tests 1,548, all passed; the end-to-end harness 26/26,
  the transitions check 4/4 and the bridge smoke 75/75; the backend suite with only the 8 environment failures v1.4.1
  has too (`test_audio_settings` 6, `test_mpvtk_pilfont` 2) once the 8 modules the suite's 300 s limit cut under that
  load were run again alone: 7 passed (`test_shell_library` 314, `test_live_tv` 258, `test_shell_settings` 170,
  `test_shell_books` 147, `test_list_page` 126, `test_dpi_matrix` 25, `test_playlist_edit` 8), and `test_shell_playback`
  passed all 165 once it ran alone with no time limit (the first run, cut at 20 minutes, had reached 119 with none
  failing; its layout sweeps rebuild the player bar hundreds of times).
  The bug hunt ran beside it; its findings are in the Fix queue above, none fixed in 1.5.0.
- **Cinematic's library placeholders can stand off the grid.** then-140-checks' skeleton check (Cinematic, 1920 wide)
  failed in the final pass and again alone: on the first visit to Movies the placeholders stood 21 px below the cards
  that replaced them, so the cards move up as they land. The app logs the same mismatch ("[layout] library skeleton:
  differs from the grid", 11 to 76 px apart) 3 or 4 times a run, in 1.4.1's runs too, where the check's own visit
  happened to match. The other themes and Cinematic at 1100 wide match.
- **UI timing tests under load:** a few FluentFin.Tests timing tests (`JobProcessTests.ExitedAt` most often; the updater
  stream saw five while the keys matrix ran beside them) can fail on a busy PC and pass alone. All 1,548 passed in the
  final pass beside three harness runs; the release build runs a failed test once more on its own.
- **Season switching, what is left after 1.5** (`then-season-switch.ps1` on the fake server's 10x20 series, four themes,
  with its latency rules; the 1.5 season stream measured before and after):
  - **The stills of a season not asked ahead still come after its rows,** 0.1 to 0.3 s now (0.5 to 1.1 s before 1.5).
    WinUI starts a picture's download only once its row is laid out and drawn; pictures now come 6 at a time (WinINet gave
    the app 2). The seasons either side of the one shown have their stills asked ahead (into WinINet's cache, which WinUI
    reads), so a click to one of them shows its pictures with its rows; a far season (Season 1 to 9) and a season page
    opened from far away do not, and neither does Editorial (it sizes its stills to its layout, so it cannot ask ahead).
  - **Drive-in's rows are still the slowest to build,** though no longer by much: its tube numbers are made of far fewer
    objects (the same drawing), which took most of its GC pauses away; a rare one still lands in a switch.
  - **A season page shows the season it was opened with first:** its name, overview and pictures from the pill or link,
    with the picker and the kept rows, and the server's season replaces it a moment later (cast and details come then). A
    link that carries only the season's id shows the rows first and the header when the season comes.
  - **A far season can still cost a round trip.** A show of at most 12 seasons has every season asked ahead, one at a
    time (1.5); a longer one asks only the seasons either side, so a jump further away waits for the server once. After an
    episode is played (or five minutes), a season further away is not asked ahead again (that was up to 11 requests a
    time): it is asked when shown, one round trip.

## Nixie 1.4.1: Continue Watching and season switching
- Season switching: what is left after 1.5 is under Nixie 1.5.0 above.

## Nixie 1.4.0: the consistency release
- **Editorial has no list view.** Its View menu offers the poster sizes only; the other three themes have the list.
- **Drive-in's chapter bulbs come later.** Drive-in has the Chapters menu in the player's bar like every theme, but no
  bulbs on its console's scrubber yet.
- **To check by hand at each release** (off-screen runs cannot do them):
  - **the lost-overlay fix:** open the player, press Space before any click, then move the mouse: the controls must come
    back. Off-screen, posted mouse moves are not real cursor moves, so the 1.3.1 case was never reproduced there; the fix
    hears every pointer move and polls the cursor every 250 ms, and logs a line if the case recurs;
  - **Esc inside YouTube's player in the trailer theatre:** the web view has the keyboard there, and Esc must still close
    the theatre.
  - **One real trailer plays in the theatre, with sound** (off-screen runs are muted; `then-trailer-live.ps1` is the opt-in
    off-screen check against real YouTube, `NIXIE_LIVE_YOUTUBE=1`).
- **Trailers depend on YouTube's embed rules.** The theatre plays a trailer inside Nixie's own page at
  `https://io.github.mbustos27.nixie` (YouTube's form for an app's identity). If YouTube changes what it accepts again,
  trailers show Nixie's message ("YouTube refused to play it here") with Watch on YouTube, rather than YouTube's error page.
  The app learns the player's state by reading Nixie's page every 300 ms (1 s once it plays); the page has no way to call
  the app. YouTube reports a video it no longer has with the same code as one its owner keeps on YouTube (150), so that
  message says both. No real video that refuses embedding was found for the live check (five official music videos all
  played); the message was checked live with a missing video's 150, and off-screen with the harness's 150 and 153.
- **R in a grid is Refresh first** (the coordinator's rule; the user asked for R to refresh): a first R refreshes, so a
  title that starts with R cannot be typed. Within a second of another letter R is a letter of the title. The R titles are
  on the A to Z's R, and on the A to Z a letter key presses that letter.
- **The skeleton and [perf] probes** (`LibraryProbe`) log one line per library opened; they measure, they never change
  the layout.
- **Still open, from the outside-services bug hunt** (`docs\reports\Bug hunt 2026-11-19 (outside services).md`; none blocks
  1.4.0; planned for 1.4.1, they wait for the next release: 1.4.1 has Continue Watching and season switching only):
  - **M1 (fixed in 1.5: truststore in the backend; libmpv's stream and a transcode's playlists and segments already
    read the Windows store through libcurl's native CA, and FFmpeg's GnuTLS, used only for what libcurl does not open,
    trusts the same stores; pinned by `LibmpvTlsTests`): a certificate Windows trusts but Mozilla's list does not: sign-in
    works, playback fails.** The UI checks the
    server's chain against the Windows store, the backend against certifi's bundle, so a home or private CA (or an
    HTTPS-inspecting antivirus root) browses but nothing plays (`SSLError: CERTIFICATE_VERIFY_FAILED`). Fix: `truststore`
    in the frozen backend (`truststore.inject_into_ssl()` in `backend_entry.py` and the bridge's main).
  - **m1 (fixed in 1.5: no answer for a split season or season 0; ARM has no episode offsets): split-season anime get the wrong part's skip times, or none** ("Look intros up online"): a TVDB season with more
    than one MAL entry (Attack on Titan's Season 3 and 3 Part II) is looked up as the first (`intro_online.py`
    `mal_id_for`). Fix: no answer when a season maps to several entries, or for season 0.
  - **m2 (fixed in 1.5: patched out with the key): mpv's controls offer "Search online" for subtitles,** which fails (uosc's `ziggy` is not shipped), and the
    vendored uosc still carries its OpenSubtitles key. Fix: patch `download_command` out of the subtitles opener.
  - **m3 (fixed in 1.5: the updater follows GitHub's redirect, `UpdateSource.Repository`; 1.1 to 1.4 still do not,
    docs\RELEASE.md section 5): renaming the GitHub repository would silently end one-click updates** in every installed copy (the release
    check accepts only `/mbustos27/fluentfin-mpv/` downloads). Never rename or transfer the repository without a release
    that accepts both paths first.
  - **m4 (fixed in 1.5: strict auth is the fake server's default, `--auth strict`; every run checks no request used a
    legacy form): the fake server accepts legacy auth** (`X-Emby-Token`, `api_key`) that Jellyfin 12 refuses, and reports 10.10.7:
    a regression to a legacy form would pass every harness run. Fix: a strict-auth mode by default in the harness.
  - The sub-path and trailing-slash server run (done in 1.5: `tools\qa-servers\subpath-probe.ps1` against both QA servers,
    behind `/jellyfin` and with a "/" typed at the end).

## Nixie 1.3.0: the layout themes
- **Editorial has no paused side column** (synopsis, cast and chapters beside a paused video), nor its segmented chapter
  scrubber: the theme no longer promises them (1.4.0). Its player is the shared one; chapters are in the bar's Chapters
  menu in every theme (1.4.0).
- **Back after a switch lands at a page's start.** A switch keeps the place on the page it happens on (Settings, or Home
  under "Choose a look"). The pages behind it are rebuilt in the new layout when Back reaches them, so Home comes back
  with the focus on the Home tab and its rows at their start, as Classic's Home always does after Back (seen off-screen:
  Drive-in without a switch returns to the focused card). Keeping each page's place in the history is for a later release.
- **Drive-in's scrubber has no chapter bulbs yet**: the player's view model has no chapters for it.
- **Checked off-screen only (Dark, Full; the switch Drive-in, Cinematic, Editorial, Classic and back on Home and a series
  page: the search text kept, the 200 ms crossfade logged, none with motion reduced); still to check by hand:**
  - high contrast in each theme (the harness cannot force a high-contrast theme; the dictionaries' HighContrast values
    are checked by tests, never seen);
  - the crossfade and Narrator's "Theme changed to ..." on a real screen, by eye and by ear;
  - Light + Solid after changing Glass while the app runs (the fix for dark parts of a light window was seen at start-up
    only), and each theme at 125 to 150 % scaling (Drive-in's small nixie digits).

## Nixie 1.2.0: the rename from Seaglass
- **The MSI upgrade from Seaglass to Nixie was not installed for real** on this PC: `upgrade-test.ps1` installs into the
  account it runs in, so it was not run (also not `-SelfCheck`). What is checked: the MSI keeps the upgrade code, builds,
  and holds exactly the zip's files (`build-release.ps1`), the zip passes `verify-package.ps1`, and the script parses.
  Run `upgrade-test.ps1` on a test account or a VM before publishing: Seaglass 1.1.3's MSI, then Nixie's over it.
- **Seaglass 1.1.x's "Download and install"** only finds an asset named `Seaglass-<version>-win-x64.msi`; the build
  makes one (a hard link of the Nixie MSI) for the release to carry (`docs\RELEASE.md` section 5). Not tried from a
  running Seaglass against a real release.
- **The data folder copy** (Seaglass, else FluentFinMpv, to Nixie) is tested on temporary folders only
  (`LegacyDataFolderTests`), never on a user's real folders; a Seaglass still running during Nixie's first start is
  covered for the journal (a SQLite backup) but its other files are copied as they are at that moment.
- **Pinned shortcuts**: a taskbar or Start pin of Seaglass points at `Programs\Seaglass\app\Seaglass.exe`, which the
  upgrade removes; the pin is left broken until it is pinned again (Windows does not move pins between programs).
- **Internal names that still say Seaglass or FluentFin** are compatibility names only: the `SEAGLASS_*` variables, the
  old data folder and registry key the copy reads, the Seaglass-named MSI the 1.1.x updater looks for, and the package
  identity (`Package.appxmanifest`'s `FluentFinMpv`) and release marker (`FluentFinMpvRelease`), which did not change.

## Nixie 1.2.0: the top bar, the desert look, the button system, the player, the administration
- **Checked off-screen only; still to see on a real screen and by hand:**
  - the glass and the dusk scene in a real, active window (the harness's window is never the active one, so it forces
    the active look), the scene's 20 s of motion and its rest, and the wordmark's glow at real sizes and DPIs;
  - the top bar on a real desktop: dragging the window by its empty middle, the caption buttons, the tabs folding as
    the window narrows (the drag regions are checked with WM_NCHITTEST off-screen);
  - Narrator by ear: the tabs ("Home, tab item, 1 of 6"), "Remove {server}", "Show more" expanded or collapsed, the
    states' announcements;
  - Windows' real high-contrast themes (the HighContrast dictionaries are checked by tests, never seen);
  - the player between episodes with sound (the harness plays digital silence), the Next countdown, and the speed and
    subtitle delay against a real file.
- **The harness ran at 2560 x 1440 and 1920 x 1080** (the display changed during the integration). Four scripts assumed
  the larger screen and were made size-proof (then-continue, then-wheel, then-rows, then-admin's hero menu); a real 4K
  or 125-150 % scaled screen was not tried.
- **The server administration never ran against a real server.** Restart, Shut down and every delete were tried against
  the fake server (each asks first and sends exactly one request), not against Jellyfin.
- **The Windows Media Player bar** (the legacy player's controls) is not driven by any harness script; its new labels
  and Tab stops are checked by tests only.
- **The player's audio and subtitle menus keep the server's track titles** ("English - Dolby Digital - 5.1"): the item
  pages' plain words ("English (5.1)") need the channel count, which the bridge's track list does not carry yet.
- **There is no in-app theme setting:** the app follows Windows' light or dark choice (`NIXIE_THEME` forces one for
  screenshots).
- **Button review verdicts left for a later release:** done in 1.5: the off switch for Home's N and C keys (L1, L2),
  Ctrl+Z in the shortcuts sheet (L3), the Filter button's name "Filter (2 on)" (L25), an Undo after Reset watch progress
  (L38), a visible hint on the disabled Sign in (S2), "Refresh" for an empty folder in the picker (S19), Select disabled
  while loading or failed (S20) and Save disabled until something changed, with "Saved" or why not (S22; the NFO and
  transcoding pages keep their own Save, always on). Still open: sentence case for the row names ("Continue watching",
  L6: "Next Up" became "Up next" in 1.4.0; the rest waits because the search stream's then-search.ps1 finds the row by
  name).
- **Tests that can fail under load and pass alone:** `ItemUndoTests` can take an Undo offer from `ItemToggleTests` running
  at the same time (the offer is a static event); `JobProcessTests.ExitedAt` and a few bridge timing tests. The release
  build runs a failed test once more on its own.

## Seaglass 1.1.3: rail, Settings, item menus, keyboard, glass, player
- **Checks only a real screen and real hands can settle.** The off-screen harness posts keys to the window and reads UI
  Automation (`then-rows`, `then-wheel`, `then-season`, `then-library`, `then-volume`, `fullscreen-burst`); it cannot
  post Shift (WinUI reads the keyboard's real state) or drive a touchpad, a gamepad or Narrator's voice. Still to try by
  hand: Shift+Tab out of a row, a grid
  and an episode list back to where it came from; Shift+F10 on a card and on an episode row (the harness uses the Menu
  key); a touchpad's two-finger swipe and a gamepad's D-pad and A/B in rows, grids and episode lists; Narrator by ear
  (a card's "12 of 320", an episode's "S1E4 Title, watched", "4 of 8"); holding an arrow key in a 320-card grid (real
  key repeat). In the player: the full-screen cover against `SEAGLASS_FULLSCREEN=cloak` in a dark room (which one is
  calmer to the eye), holding F or Esc with the keyboard's real repeat, a real double click on the video while playing
  (posted clicks do nothing off-screen: `VideoClicksTests` only), the wheel-volume bar with a real notched wheel and a
  touchpad, and the player's menus on the shared glass over a playing video.
- **Copy Download URL is removed** (it put the access token on the clipboard). A token copied with an earlier version
  stays valid until that sign-in ends; signing out ends it (`/Sessions/Logout`).
- **Delete's question is tested as behaviour** (`ItemMenu.RunAsync`: nothing runs without the yes); the delete request
  after the yes is upstream FluentFin's, unchanged, and was not run against a real server here.
- **Jellyseerr** (Settings > Integrations) was not tried against a real Jellyseerr: the fake server has none.
- **Menus and tooltips are windows of their own**: their system backdrop never shows in an off-screen capture
  (PrintWindow draws them over black), so their colours were measured as the worst case and the look over real content
  is still to see on a screen, in both themes.
- **The accent on artwork** is wired on cards' and episode rows' progress bars and on the episode row's watched mark.
  The card's own corner badge (watched check or unplayed count, `CardBadgeBackground`) keeps its own colours. In the
  light theme the episode's watched mark is now the light aqua: about 1.8:1 against a very bright thumbnail around it
  (4.2:1 with the old deep aqua), while its dark check has 7.7:1 on it (measured; 4.2:1 before). Measurements:
  `docs\design\DESIGN.md`, 1.1.3.

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
  the account flyout again during the wait (both items greyed out) -- since 1.1.4 the Account
  menu is hidden on the player page, so the title bar no longer offers a sign-out while a
  video plays (the stop-and-report path stays for any other way in); make a track or quality
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
  on); since 1.1.3 its card is shown only with Windows Media Player, which follows it. The cards say so. Needs a bridge field or command, or the backend reading the user's
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
- **The window before the automatic sign-in was run, off-screen only**: the real Nixie.exe
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
- **If you add `log-file=` to the backend's `mpv.conf`, that log gets your access token.** (`%LOCALAPPDATA%\Nixie\mpv-backend\mpv.conf`.)
  mpv's log records the request headers the backend sets, and the token is one of them. Don't share that log, and
  remove `log-file=` when you are done. Since 1.2.0 the backend's own log warns about it at every start (without the
  token). Not fixed: hiding that line from mpv's log needs a change in mpv. The off-screen harness never writes
  `log-file` and removes one it finds in its own data folder's `mpv.conf`.
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
- Fixed in 1.5: the shim asked `checkip.amazonaws.com` for the public IPv4 address on each play when the
  server address was a public IPv4 (its `is_local` check). It now decides from the server's address and this PC's name
  lookup, with no request: loopback, private and link-local addresses, a single-label name, `.local`, `.lan`,
  `.home.arpa`, `.internal` names, and any name that resolves to a private address (split DNS at home) are local;
  anything else is remote.
- **A home server reached by a public address, or by a name that resolves to one (hairpin NAT, no split DNS), counts as
  remote** since 1.5, so its plays keep the remote bitrate cap: the backend's `remote_kbps` (10,000 kbps by default) in `conf.json` in the data folder's
  `mpv-backend` folder. Raise it there if that is wrong for you (Nixie has no setting for it in its own Settings).

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
