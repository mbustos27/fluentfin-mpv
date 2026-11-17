# Seaglass: smooth full screen (measured, then fixed)

Branch `wip/sg-fullscreen`, from `main` (Seaglass 1.1.1). The user: "the smoothness of fullscreening the window". An earlier
change (`17b0d3b`, the picture's hold, `Mpv\MpvCompositionVideo.Settle`) already removed the picture jumping by the title bar's height.

## How it was measured
`tools\ui-harness\offscreen\fullscreen-burst.ps1` (run with `-Script season,play -Then ...`; `C:\b\sgfs\run.ps1` in the session
wraps it) plays the test pattern **paused** (so any frame that differs from the last is a layout, not the picture moving) in the
real Seaglass.exe off-screen, and for each entry takes 40 back-to-back `PrintWindow(PW_RENDERFULLCONTENT)` grabs of the window,
the key posted (or the button invoked) from a thread of its own just before grab 4. Each grab keeps its own window rect, its
time from the trigger, and a 128x72 grey copy of the whole window; a frame is **A** (like the first, before the trigger),
**B** (like the last), **C** (the window cloaked: nothing shown) or **X** (neither: a half-done switch). Contact sheets
(`<entry>\sheet.png`, every frame, numbered with its ms) and CSV per switch; the summary line gives the first change, the clean frame
(from which every frame is B) and the strip.

Limits, said plainly:
- A grab takes about 30 ms, so the resolution is 30 ms, and a grab that straddles the change (the frame at t = 0) is a mix
  that may not be a frame the screen ever showed. PrintWindow asks the window to paint, which also slows the switch a little.
  The times below are on the grabs' clock; the app's own log gives the unloaded times (`FLUENTFIN_MPV_VIDEO_TRACE=1`).
- It reads the window's own composition surface, not the screen: what a cloaked window leaves on the desktop (the
  windows behind it) is not in it. That part could not be looked at off-screen.
- Entries: F, F11, Esc to leave (keys posted to FluentFin's input window), the full-screen **button** (UI Automation Invoke:
  its latency was above the 1.2 s burst, so its switch was not caught; it calls the same `ToggleFullScreen` as the keys), and the
  **double click** (posted mouse messages do nothing off-screen: WinUI reads pointer input; `OnVideoDoubleTapped` calls the same
  `ToggleFullScreen`). Three runs of each switch, two directions.
- Window: 2560x1440 monitor, window maximized (-8,-8 2576x1408), controls up and paused.

## Before (main, FluentFin's controls, in-process libmpv)
Strips are `State index@ms`. Two typical switches (`before-F-in.png`, `before-F-out.png`):

    F in   A3@0 X4@0 X5@39 B6@66 B7@94
    F out  A3@0 X4@0 B5@78 B6@104

| | switch in | switch out |
|---|---|---|
| time to a clean frame (median, range) | 48 ms (39-211) | 85 ms (78-100) |
| half-done frames per switch | 1.3 | 1.0 |
| black or white frames | none | none |
| window rect | final from the first frame after the trigger | same |

What the half-done frames are (`F-in-3` frames 4 and 5, `F-out-3` frame 4): going **in**, the window already has its new size and the
XAML layout still the old one: the title bar and the controls of the small window, the picture's old frame zoomed at the top-left
corner with a black area to its right and below (the first picture of the page, then the hold's scale of it); going **out**, the
caption buttons and the picture still at the full-screen layout for a frame, then the held picture a few percent off its final scale.
The app's own trace explains the order: inside `SetPresenter` the window changes size (+7 ms) and presenter (+21 ms), the
panel gets its new size only at the next XAML frame (+25 ms), the hold at +26 ms, the first new-size frame of mpv at +45 ms. WinAppSDK
also passes the maximized window through its normal size (1920x1023) for about 5 ms on the way in and out. No flash of black or white
anywhere, no frame with the picture stretched out of its aspect after the hold is in.

mpv's own controls (child window in the backend's process, `mpv-controls-F-in.png`): the same first half-done frame (the picture
small at the top-left, black elsewhere), then a stretched picture for 2 to 4 more frames (mpv's old picture in its resized window, about
60 ms): the same problems, worse by the stretched frames. The video was not still there (the pause key did nothing), so only the sheet
can be compared, not the numbers.

## What was changed
- **Cloak**: `Services\WindowCloak` (DWM's `DWMWA_CLOAK`; `FLUENTFIN_NO_CLOAK=1` turns it off). `VideoPlayerPage.ApplyFullScreen` cloaks the
  window before `SetPresenter` and shows it when the panel has taken its new size (`MpvCompositionVideo.PanelSizeChanged`), XAML has rendered
  two frames since, and at least 70 ms have passed since the change began; never later than 150 ms (a stalled UI thread must not leave it
  hidden); also when the page is unloaded. Only with FluentFin's controls (the in-process video); mpv's own controls are not cloaked.
  Why 70 ms and not "two rendered frames": at two frames (the first try) the window was shown 0-16 ms after `SetPresenter` returned and the
  half-done layout still showed 40 ms in (X5@43); 50 ms still let some through; 70 ms did not. DWM shows what XAML committed a frame or two later.
- **One switch at a time**: `Core\Mpv\FullScreenSwitch` (state logic, 8 tests): a request that arrives while one runs is queued and the last one
  wins when the running one has settled (F twice, F then Esc); asking for the state the window is in changes nothing. Every entry (F, F11, the
  button, the double click, Esc, mpv's own request, leaving the page) already ended in `VideoPlayerPage.SetMpvFullScreen`; it is now the one
  path in and out. The FluentFin-player (Flyleaf) page's `MediaPlayerHost` is not part of it.
- **Restore**: `AppWindow`'s Overlapped presenter restores the window's rect and its maximized state exactly (checked maximized, and
  un-maximized at 120,90 1500x900: both come back to the pixel). Nothing needed.
- Not done: a crossfade with `GlassMode.MotionAllowed` (it needs a copy of the old frame under the new layout; the cloak shows what is behind
  the window for its 70 ms instead), debouncing the swap-chain resize (the hold already makes one resize visible, once), freezing or fading the controls
  (they take part in the same half-done layout, so the cloak hides their jump too).

## After (cloak, same run)
Typical (`after-F-in.png`, `after-F-out.png`; C = cloaked):

    F in   A3@0 C4@0 C5@43 C6@70 B7@96 B8@126
    F out  A3@0 C4@0 C5@84 B6@111 B7@137

| | switch in | switch out |
|---|---|---|
| half-done frames per switch | 0.0 (was 1.3) | 0.67 (was 1.0) |
| of them later than 20 ms after the trigger | 0 (was 3 in 9) | 1 in 9 (was 0) |
| time to a clean frame, the grabs' clock (median, range) | 86 ms (66-108) | 106 ms (90-221) |
| cloaked for (app log, under the grabs) | 63-79 ms | 63-79 ms |

The remaining half-done frames going out are the grab at t = 0 (it began before the cloak: the key reaches the UI thread only when the grab
that is painting the window lets it) and once a held frame after the window showed again (`F-out-2`, X5@78: the old frame at the hold's scale, a
few percent off the final one, which the hold is meant to be). Going in no half-done frame is left.
**The cost**: the window is not on the screen for those 60-80 ms, so whatever is behind it shows. That is a flash of the desktop or of
another window; it could not be looked at off-screen. Compare on the real desktop with and without `FLUENTFIN_NO_CLOAK=1`; if the gap is worse than the one
half-done frame, set `WindowCloak.Enabled` false (the rest, one switch at a time, stays).
With almost no grabbing (5 frames, the app's log) the window was shown 47-79 ms after the presenter change began (the 70 ms minimum plus timer lag).

## Proof
- `FluentFin.Core.Tests` (665 pass, 8 of them `FullScreenSwitchTests`), `FluentFin.Tests` (540: all pass but `JobProcessTests.ExitedAt` under load,
  16/16 alone with `FLUENTFIN_MPV_TEST_PYTHON` set).
- Harness `-Script home,season,play,fullscreen,windowed,leave,close`: 16 checks, all pass.
- Burst run with the window un-maximized (`FS_BURST_NORMAL=1`): back to `120,90 1500x900` after F and after Esc.
- Quick presses (`F` two and three times 25 ms apart): ends windowed / in full screen, window not cloaked. (main passes it too: each
  `SetPresenter` is synchronous; the queue matters while a switch is in flight, which the unit tests cover.)

## Seaglass 1.1.3: a black cover instead of the cloak, and the review's six findings

Branch `wip/113-player`. An independent review of 1.1.2 (`78481b0`) found six problems; each was measured here before (1.1.2) and
after with `tools\ui-harness\offscreen\fullscreen-burst.ps1`, extended: a frame can also be **K** (covered: nearly all black), each
switch reports the app's own hide time (its log's "shows again after N ms") and the half-done frames shown after the hide ended, and
`FS_BURST_EXTRAS` runs checks after the bursts (F and Esc held, the subtitle inset's timing, a posted double click, Back from full
screen). Three runs each of F, F11, the button and Esc: 12 switches each way, the window maximized (2576x1408 on a 2560x1440 private
desktop), FluentFin's controls, paused. Before: scratch runs `fs-before1`, `fs-before2`; after: `fs-after2` (cover) and `fs-cloak`
(`SEAGLASS_FULLSCREEN=cloak`).

| finding | 1.1.2 | 1.1.3 |
|---|---|---|
| 1. what the screen shows while the window is hidden | the window cloaked 62-94 ms: whatever is behind it (in a dark room, a bright flash) | a black cover over the whole window, title bar row included: in 37-76 ms, out 65-91 ms (it waits for the hold, below); 0 cloaked frames |
| 2. leaving the player from full screen (Back) | hidden 172 ms, 0 frames rendered (cloaked 22-177 ms in the watch): it waited for a layout of a detached page | not hidden; the presenter changes at once |
| 3. going out, the held picture shown | 4 of 12 switches out showed 2 half-done frames after the uncloak (the picture ~7 % small, at the top) | cover: 0 of 12. Cloak (`SEAGLASS_FULLSCREEN=cloak`): 0 of 12, hidden longer (in 91-100 ms, out 116-151 ms). In an earlier run one switch out waited ~640 ms for mpv's first frame of the new size: the limit (then 400 ms, now 300) showed the held picture |
| 4. F held 1 s / Esc held in full screen | 17 presenter changes / left full screen, then the player (playback stopped) | 1 / leaves full screen, the player stays |
| 5. the subtitle inset (`set_controls`) | sent +18 ms (in) and +56 ms (out) after the presenter change, around the time the window showed (+76, +86 ms) | sent 5 ms (in) and 2 ms (out) before it, under the cover |
| 6. double click to full screen | the first click paused, the second unpaused (playback stuttered) | the click's pause waits the double-click time and a double click cancels it; posted clicks do nothing off-screen (WinUI reads pointer input), so this one is covered by unit tests only (`VideoClicksTests`) |

What changed:
- **The cover** (`SEAGLASS_FULLSCREEN=cover`, the default): a popup with a black border larger than any window, over all of the
  window's XAML. The presenter changes right after the first frame that draws it, and it goes once the panel has its new size,
  the picture's hold (`MpvCompositionVideo.IsHolding`) has ended and XAML has rendered two frames since. It needs no minimum time:
  it is removed in the same stream of XAML commits as the layout it hides, unlike DWM's cloak, which takes effect at once while
  DWM still shows what XAML committed a frame or two before (hence the cloak's 70 ms). Limits: 300 ms from the start on the UI
  thread; 500 ms in the compositor (an opacity animation on the cover runs there even if the UI thread never comes back).
- **The cloak** stays for comparison (`SEAGLASS_FULLSCREEN=cloak`): its 70 ms minimum now counts from the presenter change's return,
  on a `Stopwatch` (1.1.2 counted from before the blocking 44-103 ms change, on `TickCount64`, which moves in 15.6 ms steps), it
  also waits for the hold, and a thread-pool timer uncloaks at 500 ms. `SEAGLASS_FULLSCREEN=none` or `FLUENTFIN_NO_CLOAK=1`: nothing
  hidden.
- A switch waits (and hides) only while the page shows the in-process video (`FullScreenCloak.WaitsForLayout`): the page forgets
  the composition video when it detaches it, so leaving from full screen switches at once. Leaving goes in a fixed order
  (`PlayerPageLeave`): the video is detached, then the running switch ends, then the window is restored. (A first try ended the
  switch first: a request queued behind it, F then Esc then Back within one switch, then started a covered switch on the page
  being left, and the restore saw its target and did nothing: the cover sat 208 ms over the next page, or with the page unloaded
  first the window stayed full screen. `fullscreen-burst.ps1`'s `fescleave` check: FAIL before, PASS after.)
- The UI thread's limit is its timer's tick (it fired up to 0.26 ms before the switch's own clock reached 300 ms, and a check of
  the clock then left the switch running and F/Esc dead); the cloak's thread-pool limit acts only for its own cloak
  (`HideGeneration`). A click's waiting play/pause is dropped by any other play/pause (Space, K, the button, a new item, a pause
  from the backend) and when the controls go.
- The subtitles' inset for the height the video is about to have (the display's, or the one it had in the window) goes before the
  presenter change (`MpvPlayerControls.ReportBottomBarFor`).
- F, F11 and Escape ignore their auto-repeat (`MpvPlayerKeys.IgnoresRepeat`; Escape through a window-wide `PreviewKeyDown` the page
  adds, since its accelerator is the window's).
- The whole switch is `Core\Mpv\FullScreenTransition` (the page only wires it to the window): `FullScreenTransitionTests` cover the
  hide condition (detached, mpv's controls), the cover's frame before the presenter, the hold, frames before the presenter not
  counting, the cloak's minimum from the presenter's return, the limit, the page going mid-switch (a queued presenter change is
  dropped), a presenter change that throws (now or after the frame). Reverting the page's use of it no longer passes silently.
