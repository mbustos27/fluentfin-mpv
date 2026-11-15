# FluentFin's own player controls over the embedded mpv

Date: 2026-11-15. Stream `wip/player-controls`. Status: decision taken, increment 1 (the window the
controls draw in) built and measured; the controls themselves follow in increments 2-4.

## Problem

With the mpv player, mpv's on-screen controller (the stock `osc.lua`) is the only UI over the video.
The user finds it hard to use: subtitles over the bar, "1/2" "3/3" track counters, a cache readout,
mpv's look. The target is Plex/Netflix-style Fluent controls: a top info bar and a bottom transport
bar over the video, auto-hiding, with named track pickers, trickplay, skip intro/credits and Up Next
(see the UX audit, `docs\research\ux-review.md` in the main repo, §1.2 and §4.6, for the baseline).

The constraint is **airspace**: mpv draws into a Win32 child window of the main window
(`MpvVideoHost`), and a child window sits above all XAML of that window
(`winui-mpv-embed.md` §1). Nothing in the player page can draw over the video.

## Decision: an owned, transparent top-level window over the video (the audit's option A2)

`ui\FluentFin\Mpv\MpvControlsOverlay.cs`: a WinUI `Window` of its own that hosts the controls'
XAML and is kept exactly over the video window.

| Property | How | Why |
|---|---|---|
| Above the video | owned by the main window (`GWLP_HWNDPARENT`) | an owned window is always above its owner and the owner's children; it minimizes and restores with the owner, and moves in z-order with it |
| No taskbar button, no Alt+Tab entry | `WS_EX_TOOLWINDOW`, `WS_EX_APPWINDOW` removed | `AppWindow.IsShownInSwitchers` throws without Explorer's taskbar (a private desktop); the styles do the same everywhere |
| See-through | `WinUIEx.TransparentTintBackdrop`, transparent root | the video shows wherever the controls draw nothing; gradients keep their alpha |
| No frame | window sized to the video **plus its frame**, clipped to its client area with a window region | the presenter without border and title bar still leaves `WS_DLGFRAME` (WinUI puts it back when removed: measured), and Windows draws that 3 px frame light over the video's edge. `DWMWA_BORDER_COLOR = none` and square corners are set too, but do not remove it. The frame is measured on each placement (it grows with DPI) |
| Placement | `MpvVideoHost.ScreenRectChanged`: the video window's rectangle on the screen, published on every layout, size, DPI, presenter and window-position change, and null when it hides | one source of truth: the controls are where the video is, and hidden when it is (a page popup open, the page left, nothing playing) |
| No focus theft | shown with `SW_SHOWNOACTIVATE`, moved with `SWP_NOACTIVATE` | showing the controls never takes the keyboard from the page |
| Closing | Alt+F4 in it is passed to the main window (`WM_CLOSE`); it closes itself when the main window closes | a WinUI window left open would keep the app running |

### Measured (tools\ui-harness\controls-host, off-screen, real backend and mpv 0.41)

Scenario `spike` (68 checks): over the playing test pattern in windowed mode, after a resize, a move,
full screen, back to windowed, minimize and restore:

- the controls' client area equals the video window's rectangle and mpv's window rectangle, to the
  pixel; the owner is the main window; same DPI scale as the main window;
- `WindowFromPoint` at the bottom bar answers the controls window (it is on top of mpv);
- composed alone by DWM (a DWM thumbnail of it in a window of the host's own, over black and over
  white; the difference is its alpha): mean alpha **0.0** in the middle (the video shows through),
  ~169 along the bottom gradient, ~175 at the top gradient, **0.0** on its left and right edges;
- minimized: hidden with the owner; restored: back and placed;
- a flyout of the page opens: the video window and the controls step aside; closed: both back;
- the main window closes: the controls window is gone.

Without the frame fix (a scratch copy of the sources with the inflate-and-clip removed) the same
scenario fails: the client area is 3 px inside the video on each side and the edges read alpha 227
(left) and 105 (right).

Pictures: the host's `shots\` folder (`windowed.png`, `fullscreen.png`, ... = the main window's
PrintWindow with the controls window composed over it as DWM has it).

### Why not the others

- **A1, a windowed XAML `Popup` of the page** (`ShouldConstrainToRootBounds=false`), the audit's first
  choice. Measured in the host (scenario `popup`): it does get a window of its own
  (`PopupWindowSiteBridge`, owned, `WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP`), it is on top, and
  it follows the main window's moves by itself. Its appeal is real: one XAML tree, one focus scope, no
  activation of another window. But neither PrintWindow nor a DWM thumbnail renders a
  no-redirection popup window, so its transparency and what it draws cannot be verified off-screen
  (both read empty), and the video window currently hides for every open popup, so it needs an
  exemption that every later popup check must keep. A2 is measured end to end and needs neither.
  If A2's separate focus scope turns out to bother users (below), A1 is the next thing to try, on the
  real desktop.
- **B, a strip reserved under the video** in windowed mode: no airspace risk, but not the
  streaming-app look, and full screen would still need A1/A2 or a video resize on every show/hide
  (cross-process resize, `winui-mpv-embed.md` §4).
- **C, libmpv in-process into a `SwapChainPanel`**: removes airspace for good, but libmpv is not
  installed here (out of scope) and it moves mpv into FluentFin's process. The XAML built for A2 would
  carry over unchanged.
- **D, styling mpv's OSC**: the other stream (`wip/osc-polish`) does that; it stays the fallback
  behind the setting.

## How the controls work on top of it (increments 2-4)

- **Setting** "Player controls: FluentFin / mpv". With FluentFin's, mpv's OSC is turned off through
  the bridge (`set_controls`, docs\BRIDGE.md) and FluentFin owns all input over the video.
- **Input, model B** (`winui-mpv-embed.md` §3): mpv's window stays disabled, so its mouse input goes
  to FluentFin's video window; the keyboard stays in FluentFin (the page, or the controls window once
  clicked). One key map serves both windows: Space/K, ←/→ (5 s), Ctrl+←/→ (10 s), ↑/↓ volume, F, M,
  S (skip segment), Esc.
- **The controls window is shown only while there is something to show** (the bars, a menu, the
  Skip button, Up Next, paused). While hidden, the video is not composed under a window of ours
  (full-screen video can go back to direct/independent flip), and a mouse move or click over the
  video reaches FluentFin's video window, which shows the controls again.
- **Subtitles never sit under the bar**: the UI tells the backend how much of the video's height the
  bottom bar covers while it shows (`set_controls.subtitleInset`); the backend moves mpv's subtitles up
  by that much (`sub-pos`), and back when the bar hides.
- **Data**: state, position, item (series, season, episode) and tracks already come over the bridge.
  Trickplay tiles and media segments FluentFin asks the server for itself (it already does for its
  own player), so the bridge does not have to carry them.

## Open, for the user's eyes

- **DPI change**: moving the window to a monitor with another scale. The code re-measures the frame
  and follows `XamlRoot.Changed`, but the private desktop has one monitor at 100 %, so it is untested.
- **The main window's caption looks inactive** while the controls window has the keyboard (after a
  click on a control), in windowed mode only. Cosmetic; A1 would not have it.
- **Light theme**: the controls are dark on purpose; the backdrop is transparent in both themes in
  WinUIEx's code, not measured in light.
