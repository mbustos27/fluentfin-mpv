# Known issues at the integration baseline

Recorded at the baseline commit (2026-11-14), after seven review/fix rounds on each side.
The final pre-commit reviews found nothing of medium severity or above left open; everything
below is low severity, an accepted cost of the contract, or not verified yet. Each entry
says where it lives so it can be picked up later. Entries marked "fixed" say what is left.

## Never verified
- **A real Jellyfin server.** Every test used fake servers and fake tokens. First tried
  2026-11-14 (remote, HTTPS, a user who is not an administrator): login worked, but mpv
  never started: the play request was built from FluentFin's playlist, one request per
  season in turn (31 s for a 250-episode show), the page was left meanwhile and the start
  went on regardless. Fixed the same day (round 7: the queue from one request, each step
  shown, a start whose page is gone stops before the backend, the page's life in the log);
  not yet retried on the real server. Direct play, a real **transcode** (and its restart on
  a track change) and the server's handling of a duplicate Stopped are still untested.
- **A real sign-out in the app shell** (Log out / Switch user while a video plays). Covered
  by view-model tests and the end-to-end harness (`tools\ui-harness\e2e`, scenarios
  `signout_*`), not by driving FluentFin itself: its data folder can't be redirected, so a
  fake login would land in the real FluentFin settings.
- **The libmpv path.** Only external mpv (mpv-CI 0.41 via winget) is installed and tested.

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
  warning with the backend's own text (it names the item and says the change could not be
  applied; the UI cannot say which change, since most come from mpv's OSC), and playback is
  not touched. Showing it pushes the video area down while it is up (airspace: XAML cannot
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
- **Test gaps**: nothing automated drives a real root-frame sign-out, and the player page's
  `Unloaded` hook is covered only through its rule (`FluentFin\Mpv\PlayerPageRemoval.cs`,
  tests in `VideoPlayerViewModelMpvTests.PageRemoval.cs`; signout-ux), not through WinUI
  raising `Unloaded` on a real page; four reviewer mutations survive
  (the sign-out wait's 2 s bound, the redundant `_mpvLeaving` early return, the
  `ExitHandled` term in `Owed`, the re-check after subscribing in `WaitForStopReportsAsync`).

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

## This PC's test environment
- 8 environmental failures in the backend suite: `test_mpvtk_pilfont` ×2 (an installed font
  Pillow cannot read), `test_audio_settings` ×6 (the fake player models the libmpv path
  only).
- The long `test_shell_*`, `test_live_tv`, `test_dpi_matrix` and `test_playlist_edit`
  modules can exceed the parallel runner's 300 s per-module limit when the machine is busy
  (late on 2026-11-14 even when idle); each passes alone with
  `run_suite.py --serial --timeout 1500 -- -k <module>`, and timed side by side they are
  exactly as fast on the baseline as on the pre-round copies.
