# FluentFin's own player controls over mpv

Date: 2026-11-15. Stream `wip/player-controls`. Status: decided by the deep-research report
(`docs\reports\WinUI mpv player controls approach.md` in the main repository, "Ship uosc now, bring
mpv in-process next"); phase 1 shipped; phase 2a spiked (results below); phase 2b behind a setting.

## Problem

With the mpv player, mpv's on-screen controller is the only UI over the video. The target is
Plex/Netflix-style Fluent controls: a top info bar and a bottom transport bar over the video,
auto-hiding, with named track pickers, the wheel as volume, and later trickplay, skip
intro/credits and Up Next, reachable from the keyboard and by Narrator.

The constraint is **airspace**: today mpv runs in the backend's process and draws into a Win32
child window of the main window (`MpvVideoHost`, `--wid`); a child window sits above all XAML of
its window (`winui-mpv-embed.md` §1), so nothing on the player page can draw over the video.

## Decision

| | What | Status |
|---|---|---|
| **Phase 1** | mpv's controls, polished: the shim's OSC fork with FluentFin's look, the title, track names, subtitles above the bar, the wheel as volume (`backend/docs/bridge.md` §4a) | merged to main through osc-polish (`e2b09cc`) |
| **Destination: B1** | **libmpv in FluentFin's process, rendering in composition mode** (`vo=gpu-next`, `gpu-context=d3d11`, `d3d11-output-mode=composition`) into a WinUI `SwapChainPanel`; FluentFin's XAML controls drawn over it in the same tree; the shim **attached** to that core over its IPC pipe (`mpv_ext_start=False`), still the only one that plays, picks tracks and reports | phase 2a spike: go (table below); phase 2b behind the setting |
| **Fallback: lighter A** | keep `--wid`; a **bar-sized, opaque windowed popup** (no per-pixel transparency, hidden while the window moves or resizes) plus native `MenuFlyout` pickers | only if B1 fails in use (crashes, HDR, pacing) |
| Rejected: **A2** | a transparent owned top-level window over the whole video (built and measured in increment 1, `cde10d0`) | the record below stays as the basis for lighter A |
| Rejected: **B2** | libmpv's render API | every render-API backend runs the legacy `vo=gpu`: no gpu-next, no Dolby Vision, HDR by hand |

Why B1 (the report, §"Option B"): it removes airspace for good, everything else becomes ordinary
XAML (flyouts, keyboard accelerators, Tab order, Narrator), mpv still renders and presents with
gpu-next and d3d11va, and two WinUI 3 players ship it (Nami, Mio). Its costs: one crash domain (an
mpv or driver crash takes FluentFin with it), HDR becomes the app's call (mpv cannot see the display
in composition mode), and the shim no longer builds mpv, so FluentFin sets mpv's startup options.

## Phase 2a: the spike (`tools\ui-harness\composition-spike`)

`run.ps1 -Out <scratch> -Scenario local|hdr|bridge|quit -Mode composition|wid|both`: a WinUI window of
its own (never FluentFin.exe) with shinchiro's `libmpv-2.dll` (`vendor\libmpv\VENDORED.md`) loaded
in-process, off-screen (private desktop, kill-on-close job), every core with `ao=null`. Scenarios:
`local` plays a 1080p60 H.264 clip, resizes, goes full screen and back, resizes six times quickly;
`hdr` plays a 10-bit BT.2020 clip with the HDR hint forced; `bridge` runs the real backend from this
checkout attached to the in-process core (`mpv_ext`, `mpv_ext_start=false`, `mpv_ext_ipc`) against a
fake server; `quit` sends `quit` over IPC. Results: see the go/no-go table below (filled by the spike).

## Phase 2b: FluentFin's controls over the composition video

- **Setting**: Settings → Playback, "Player controls: FluentFin / mpv". `mpv` is today's path,
  unchanged (`--wid`, the shim's OSC). `FluentFin` is B1. The default is FluentFin only if every row
  of the go/no-go table passed on this PC.
- **Protocol** (docs\BRIDGE.md): `connect.window` gains `{"mode": "attached", "ipc": "<pipe>"}`: the
  UI's own mpv listens on that pipe before `connect` and lives as long as the backend; the backend
  attaches through the shim's external backend and never starts, quits, sizes or fullscreens it.
  `set_controls` (`"ui"`, `subtitleInset`) hides the shim's OSC and keeps the subtitles above the bar.
  Tracks carry `streamTitle` and `isHearingImpaired` for the pickers' labels.
- **Input**: mpv has no window, so all input is FluentFin's: keys on the page
  (`MpvControlsViewModel.ActionFor`), click to pause, double-click full screen, pointer moves show the
  controls, the wheel is volume (±5 a notch, 0–100, with a brief indicator; it never seeks).
- **Commands** go over the bridge (pause, seek, volume, tracks, next), so the backend stays the only
  one that reports playback to the server.

## The record behind the fallback: A2 measured (increment 1, `cde10d0`)

`ui\FluentFin\Mpv\MpvControlsOverlay.cs`, a WinUI window of its own kept over the video window
(owned by the main window, `WS_EX_TOOLWINDOW`, `WinUIEx.TransparentTintBackdrop`, placed from
`MpvVideoHost.ScreenRectChanged`, shown with `SW_SHOWNOACTIVATE`). Measured in
`tools\ui-harness\controls-host` (scenario `spike`, 68 checks, off-screen, real backend, mpv 0.41):
client area equal to the video window's to the pixel through resize, move, full screen, minimize and
restore; on top (`WindowFromPoint`); see-through (DWM thumbnail alpha 0.0 mid-video, ~170 on the
gradients); hidden with a page flyout and with the owner; gone when the main window closes. It needed
a frame fix: WinUI keeps `WS_DLGFRAME` on a borderless presenter, and the window had to be inflated by
its frame and clipped to its client area (without it, a 3 px light frame over the video's edge).
The scenario `popup` measured option A1, a windowed XAML `Popup`: it gets its own owned window
(`PopupWindowSiteBridge`, `WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`) that follows the main
window's moves by itself, but neither PrintWindow nor a DWM thumbnail renders it, so it cannot be
verified off-screen.

What that means for lighter A: a **bar-sized opaque** popup avoids the transparency trick and the
frame fix entirely (nothing has to be see-through) and needs no click-through; it must be exempted
from `MpvVideoHost`'s "hide for any open popup" rule, hidden during moves and resizes, and verified on
the real desktop, since the off-screen tools cannot see a no-redirection window.

## Open, for the user's eyes

- HDR on an HDR display, independent flip, and a DPI change are not verifiable off-screen (table).
- The caption of the main window, the light theme and Narrator over the new bar have not been seen
  on the real desktop.
