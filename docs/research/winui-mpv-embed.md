# Embedding mpv (out-of-process, `--wid`) in a WinUI 3 window

Research date: 2026-11-13. Web sources only (WebSearch/WebFetch). Status: COMPLETE (5/5 topics).
Quotes are kept under 15 words; everything marked "inference" was not confirmed by a source and
should be checked in the first spike.

Context: FluentFin (WinUI 3, Windows App SDK 2.x, .NET 10, C#) creates one persistent Win32
`WS_CHILD` window parented to the main window HWND, keeps it over a XAML placeholder on the player
page, and passes its HWND to mpv.exe 0.41 (separate process, driven by a Python child over JSON IPC)
as `--wid`. Controls are mpv's OSC.

## Do this (checklist)

1. **Parent the host to the top-level WinUI HWND** (sibling of `DesktopChildSiteBridge`), never to
   the bridge/island. Create it after `Window.Activate()`. Styles `WS_CHILD | WS_CLIPSIBLINGS |
   WS_CLIPCHILDREN`; **no** `WS_EX_LAYERED`, **no** `WS_EX_NOREDIRECTIONBITMAP`. (§1)
2. **Every reposition: `SetWindowPos(host, HWND_TOP, x, y, w, h, SWP_NOACTIVATE |
   SWP_SHOWWINDOW)`**; hide with `SW_HIDE` (never 0x0) when leaving the player or before any
   in-tree XAML popup/dialog. Make player-area flyouts `ShouldConstrainToRootBounds="False"`. (§1)
3. **Rect = `TransformToVisual(null)` x `XamlRoot.RasterizationScale`, then `MapWindowPoints(bridge,
   top)`**; resync on `SizeChanged`, `LayoutUpdated` (early-out if unchanged), `XamlRoot.Changed`,
   `AppWindow.Changed` (size/presenter). Placeholder `Background="Black"`. (§2)
4. **Pass `--wid` as `(uint)hwnd`** (int64 casts can go negative → detached mpv window). (§3)
5. **Handle input explicitly — mpv disables its embedded window (`EnableWindow(…, 0)`)**, so the
   OSC sees nothing by default. Host `WM_MOUSEMOVE` → `EnableWindow(mpvHwnd, TRUE)`; host
   `WM_MOUSEACTIVATE` → `SetFocus(mpvHwnd)`. mpv HWND = `FindWindowEx(host, 0, "mpv", null)` or
   the `window-id` property. (§3)
6. **`--window-dragging=no`** (mpv's drag hack is not disabled in `--wid` mode); app `input.conf`
   with `q`/`Q` → `ignore`, app actions → `script-message fluentfin …`. (§3)
7. **Hang/shutdown discipline** (input queues are attached across the two processes):
   watchdog `SendMessageTimeout(mpvHwnd, WM_NULL, …, SMTO_ABORTIFHUNG, 1000)` → kill mpv on hang;
   at exit IPC `quit` → wait → kill → then close the window. (§3)
8. **Fullscreen is property-driven:** mpv ignores fullscreen with `--wid` but still flips the
   `fullscreen` property → Python observes → C# `AppWindow.SetPresenter(FullScreen/Overlapped)`
   and collapses chrome. App-side exits set `fullscreen no` back. (§4)
9. **mpv flags:** `--idle=yes --force-window=yes --vo=gpu-next --gpu-api=d3d11
   --gpu-context=d3d11 --hwdec=auto-safe --d3d11-flip=yes --keepaspect=yes
   --background-color=#000000 --cursor-autohide=1000
   --script-opts=osc-idlescreen=no,osc-windowcontrols=no --config-dir=<app>\mpv
   --input-ipc-server=\\.\pipe\fluentfin-mpv-<pid>`. Consider mpv 0.42 (2026-11-05) for the
   Fluent-icon `floating` OSC layout. (§4)
10. **Spike first, on the real Windows 11 build:** (a) host receives clicks/moves (Win11 island
    input caveat), (b) OSC hover/seek works after re-enable, (c) monitor DPI move, (d) presenter
    switch flicker, (e) mpv hang → watchdog recovers. Keep **Plan B = libmpv in-process +
    `--d3d11-output-mode=composition` + `SwapChainPanel`** (mpv ≥ 0.41) if any of (a)–(e) fails. (§5)

## 1. Z-order: keeping the child HWND above the XAML bridge window

### Answer

**Keeping the child above XAML is the easy part; everything else XAML draws under it is the hard part.**

- Window tree of a WinUI 3 window (as reported in the WinUI repo): top-level
  `WinUIDesktopWin32WindowClass` -> child `Microsoft.UI.Content.DesktopChildSiteBridge` (the XAML
  content) -> invisible `InputSiteWindowClass` (zero size, no `WS_VISIBLE`) created by
  Microsoft.UI.Input. A window you create with `CreateWindowEx(WS_CHILD, parent = top-level HWND)`
  is a **sibling** of the bridge window. [#10319, 2025-01-27], [#10050, 2024-10-10]
- Composition rule (DirectComposition docs): each HWND has four layers — GDI/Present content,
  a "behind children" visual tree, **child windows**, then a "topmost" visual tree. WinUI 3 draws
  XAML in the lower composition layer and "doesn't use the upper layer" (DarranRowe, accepted
  answer), so plain child HWNDs end up **on top of** XAML. [DComp CreateTargetForHwnd docs],
  [WindowsAppSDK discussion #3879, 2023-09-25]
- Confirmed in practice: Win32 children created with `CreateWindowEx` draw over XAML (castorix,
  MS Q&A), and the reverse problem shows up — menu dropdowns, tooltips and an expanded
  NavigationView render **behind** the child. [MS Q&A 1115740, 2022-12 to 2023-04], [#3879]
- Sibling z-order among child windows is the order of the parent's child list; a newly created
  child goes to the top. `SetWindowPos(hwnd, HWND_TOP, ...)` re-asserts it. The bridge object
  itself exposes `MoveInZOrderAtTop/AtBottom/Below(WindowId)` (Windows App SDK 1.4+), but
  `Microsoft.UI.Xaml.Window` does not hand you its bridge object, so in practice you use Win32.
  [DesktopChildSiteBridge API, docs updated 2026-07-28]
- No source found that shows the bridge HWND re-ordering itself on resize/activation. One report:
  a child created **after** `Window.Activate()` did not appear until a `SetWindowPos` refresh
  (castorix, 2022-12-07). Treat "re-assert `HWND_TOP` after activation and after each move" as
  cheap insurance, not a proven need. [MS Q&A 1115740]
- `WS_EX_LAYERED`: castorix says a **GDI-painted** child must be layered because since App SDK
  1.1 the composition window paints a background under XAML. That is about GDI content (static
  text, bitmaps). mpv presents through a D3D11 swap chain on **its own** window (a child of
  yours), which DWM composes as child-window content. No source says a D3D swap-chain child needs
  `WS_EX_LAYERED`; layered + DXGI child is an unnecessary risk, so do not add it. The visible cost:
  your host window's own GDI fill (black brush) may not show, so the area can show stale/empty
  pixels until mpv's window covers it. (Inference — verify on the dev box.)
- Do not set `WS_EX_NOREDIRECTIONBITMAP` on the host: it makes GDI windows invisible without a
  compositor path, and a Chromium dev reported child HWNDs breaking after it was put on the
  top-level. [Mozilla bug 1906358], [chromium-dev "No more child windows"]
- **Input caveat that matters more than z-order:** an app that put a Win32 window *inside* the
  island on Windows 11 found keyboard went to `DesktopChildSiteBridge` and mouse went to a
  message-only `MITMessageOnlyWindowClass` window; WinUI owners have not answered. That report is
  for windows parented under the island/bridge. Parent your host to the **top-level** HWND
  (sibling of the bridge), never to the bridge. [discussion #9912, 2024-08-20], [#10050]
- "Proper" hosting APIs: none shipped. `ContentExternalOutputLink` (castorix: WebView2 uses it)
  rendered a *non-interactive* Win32 control inside a XAML element in community tests; it was
  experimental in 2023 and still appears in 2.5.1 fix notes, but gives no input path;
  `DesktopChildSiteBridge.AcceptRemoteEndpoint` (cross-process islands) appears in 2.0 API lists
  but hosts a ContentIsland, not a foreign HWND. A `ThousandIslands`/`HwndHost` proposal is open
  with no Microsoft design reply (team-Reach label added 2026-09-18). [#10050], [#3879],
  [App SDK 2.0 release notes]
- XAML popups that **can** appear above the video: anything shown in its own top-level HWND —
  `Popup`/`FlyoutBase.ShouldConstrainToRootBounds = false` (App SDK 1.4+). `MenuFlyout` and
  `CommandBarFlyout` default to `false` (windowed); plain `Flyout` defaults to `true`. Must be set
  before first open. `ContentDialog`, `InfoBar`, in-window tooltips and any popup left at
  `ShouldConstrainToRootBounds = true` will be hidden under the video.
  [Popup.ShouldConstrainToRootBounds docs]

### Risk for this design: LOW for visibility, MEDIUM for UX

Visibility of the video is reliable. The real cost is that no XAML can draw over the video rect;
that is already accepted (OSC does controls), but dialogs/toasts/flyouts anchored near the
player must be windowed or the host must be hidden while they show.

### Recommended approach

```csharp
// Parent = the WinUI top-level window (sibling of DesktopChildSiteBridge). Create after Activate().
IntPtr top = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
_host = CreateWindowEx(
    0,                                   // no WS_EX_LAYERED, no WS_EX_NOREDIRECTIONBITMAP
    "FluentFinVideoHost",                // own class: CS_DBLCLKS, hbrBackground = black brush,
                                         // WndProc you own (needed for input, see section 3)
    null,
    WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,   // not visible until positioned
    0, 0, 1, 1, top, IntPtr.Zero, hInstance, IntPtr.Zero);

// Every reposition (and on Window.Activated, AppWindow.Changed):
SetWindowPos(_host, HWND_TOP, x, y, w, h,
             SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);

// Leaving the player page, or before showing ContentDialog / in-tree flyouts:
ShowWindow(_host, SW_HIDE);   // keep it alive (persistent), mpv keeps running/paused
```

- Make every flyout that can open over the player `ShouldConstrainToRootBounds="False"`; route
  confirmations through `ContentDialog` only after hiding the host.
- Optional check at startup (debug builds): `FindWindowEx(top, IntPtr.Zero,
  "Microsoft.UI.Content.DesktopChildSiteBridge", null)` and assert `GetWindow(_host, GW_HWNDPREV)`
  walk never finds it above you.
- Do **not** reparent mpv's window yourself (`SetParent` on a foreign window); let mpv create its
  child via `--wid` (see Raymond Chen in section 3).

### Sources

- microsoft-ui-xaml #10319 "Unexpected InputSiteWindowClass window" (2025-01-27):
  https://github.com/microsoft/microsoft-ui-xaml/issues/10319
- microsoft-ui-xaml #10050 "ThousandIslands Window" proposal (2024-10-10, labels 2026-09-18):
  https://github.com/microsoft/microsoft-ui-xaml/issues/10050
- microsoft-ui-xaml discussion #9912 "embed a custom win32 window" (2024-08-20 to 2025-01-27):
  https://github.com/microsoft/microsoft-ui-xaml/discussions/9912
- WindowsAppSDK discussion #3879 "Drawing over an Embedded Process/Window" (2023-09-25):
  https://github.com/microsoft/WindowsAppSDK/discussions/3879
- Microsoft Q&A 1115740 "WinUI3 controls and Win32 controls" (2022-12-05 to 2024-10-15):
  https://learn.microsoft.com/en-us/answers/questions/1115740/can-we-use-both-winui3-controls-and-win32-controls
- IDCompositionDevice::CreateTargetForHwnd (four-layer model):
  https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-idcompositiondevice-createtargetforhwnd
- DesktopChildSiteBridge class (App SDK 1.4 to 2.0, updated 2026-07-28):
  https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.content.desktopchildsitebridge
- Popup.ShouldConstrainToRootBounds (App SDK 1.4+):
  https://learn.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.primitives.popup.shouldconstraintorootbounds
- Windows App SDK 2.0 release notes (2.0.1 2026-04-29 ... 2.6.5 2026-11-02):
  https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
- Mozilla bug 1906358 (NOREDIRECTIONBITMAP invisibility): https://bugzilla.mozilla.org/show_bug.cgi?id=1906358
- chromium-dev "No more child windows in Chrome window?":
  https://groups.google.com/a/chromium.org/g/chromium-dev/c/hIJazxXfUD4

## 2. Positioning: XAML bounds to physical pixels, DPI, title bar

### Answer

- XAML works in effective pixels (epx); `AppWindow` and Win32 work in physical device pixels.
  The conversion factor is `XamlRoot.RasterizationScale` (physical pixels per view pixel). Microsoft
  points to this exact conversion for title-bar drag rects. [Windowing overview, 2026-10-13],
  [XamlRoot.RasterizationScale docs]
- Element rect: `placeholder.TransformToVisual(null).TransformBounds(new Rect(0,0,w,h))` gives the
  rect relative to the XAML root (epx); multiply by `RasterizationScale` to get pixels relative to
  the XAML root's origin. That origin is the client origin of the `DesktopChildSiteBridge` window.
- Title bar: with `ExtendsContentIntoTitleBar = true`, XAML content (and the bridge) starts at the
  top of the client area, so the title bar region is part of your coordinate space and needs **no
  extra offset**; with the default title bar, the caption is non-client and the XAML root also
  starts at client (0,0). Either way the robust rule is: do not assume — map from the bridge's
  client origin to the top-level's client origin with `MapWindowPoints` once per layout pass
  (cost is negligible). Known title-bar quirks were about caption-button size/shadow with
  `Window.ExtendsContentIntoTitleBar` (fixed in App SDK 1.4), not about the coordinate origin.
  [microsoft-ui-xaml #7131, 2022-05-21, closed 2024-02-23]
- DPI: WinUI 3 apps are Per-Monitor-V2 aware. mpv.exe's manifest moved to
  `dpiAwareness = PerMonitorV2,PerMonitor` (late-2025 mirror, so it applies to 0.41); both
  processes agree, so the cross-process child is not bitmap-stretched. [mpv osdep/mpv.exe.manifest
  mirrors 2024 and 2025]
- On a monitor move with a different scale, Windows sends `WM_DPICHANGED` to the top-level, WinUI
  rescales and raises `XamlRoot.Changed` (new `RasterizationScale`). Child HWNDs only get
  `WM_DPICHANGED_BEFOREPARENT/AFTERPARENT` when the top-level is PMv2 (Win10 1703+); WinUI does
  not move your host for you. [WM_DPICHANGED_AFTERPARENT docs], [Win10 Creators Update DPI blog
  2017-04-04], [zakariathr22 blog on XamlRoot.Changed]
- mpv side: in `--wid` mode mpv follows **its parent (your host)** — cross-process it installs a
  `SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, WINEVENT_OUTOFCONTEXT)` and resizes itself with
  `SetWindowPos(..., SWP_ASYNCWINDOWPOS)`; the source comment says these hooks "don't seem to be as
  smooth" as the same-process hook. mpv refreshes its DPI (`GetDpiForMonitor`, effective DPI) on
  `WM_SIZE`/`WM_MOVE`/`WM_DPICHANGED`, so the OSC rescales when your resize lands. A scale change
  keeps the XAML layout in epx, so the placeholder's **physical** size changes, the host is
  resized, and mpv gets its `WM_SIZE`. Corner case to test: a placeholder whose physical size
  happens to stay identical across the move (mpv would keep the old DPI until the next resize).
  [mpv video/out/w32_common.c master], [commit 31f28da, 2016-08-25]
- Pure moves (NavigationView pane opening, page transitions) do not raise `SizeChanged` on the
  placeholder. `LayoutUpdated` does fire, but it fires for any layout pass anywhere, so compare
  against the last rect and only call `SetWindowPos` on change.
- Moving the whole top-level window does **not** require moving the host: child coordinates are
  relative to the parent client area. Only size/layout/DPI/presenter changes matter.
- Rounding: compute left/top with `Math.Round` and right/bottom with `Math.Round` separately
  (width = right − left) so adjacent XAML edges don't drift by 1 px at 125%/175%.

### Risk for this design: LOW

Well-trodden math. The only real risk is lag: the host moves on the UI thread, mpv's window follows
asynchronously via a WinEvent in another process, so during a live window resize the video can trail
by a frame or two (see section 4).

### Recommended approach

```csharp
void SyncHost()
{
    if (!_playerVisible) { ShowWindow(_host, SW_HIDE); _last = default; return; }
    var root  = _placeholder.XamlRoot;                    // null until loaded
    double s  = root.RasterizationScale;
    var r     = _placeholder.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, _placeholder.ActualWidth, _placeholder.ActualHeight));
    var pt    = new POINT { X = (int)Math.Round(r.X * s), Y = (int)Math.Round(r.Y * s) };
    int right = (int)Math.Round((r.X + r.Width) * s), bottom = (int)Math.Round((r.Y + r.Height) * s);
    int w = right - pt.X, h = bottom - pt.Y;
    // XAML root origin = bridge client origin; convert to top-level client coords.
    MapWindowPoints(_bridgeHwnd, _topHwnd, ref pt, 1);
    var rc = new RECT(pt.X, pt.Y, pt.X + w, pt.Y + h);
    if (rc == _last) return;
    _last = rc;
    SetWindowPos(_host, HWND_TOP, rc.Left, rc.Top, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
}

// Hook up (UI thread):
_placeholder.SizeChanged    += (_, _) => SyncHost();
_placeholder.LayoutUpdated  += (_, _) => SyncHost();       // cheap: early-out on same rect
_placeholder.XamlRoot.Changed += (_, _) => SyncHost();     // RasterizationScale / size changes
window.AppWindow.Changed    += (_, e) => { if (e.DidSizeChange || e.DidPresenterChange) SyncHost(); };
// _bridgeHwnd = FindWindowEx(_topHwnd, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null)
```

- Use `GetDpiForWindow(_topHwnd)` (not `GetDpiForMonitor`) if you ever need DPI outside XAML; a
  WinUI resize write-up warns `GetDpiForMonitor` can pick the wrong DPI. [citelao "resize winui
  window part 2"]
- Never size the host to 0x0: hide it instead (`SW_HIDE`). gpu-next/libplacebo treat a 0x0
  resize as a no-op (mpv commit history), so the old frame would just linger.

### Sources

- Windowing overview (epx vs device pixels, RasterizationScale), updated 2026-10-14:
  https://learn.microsoft.com/en-us/windows/apps/develop/ui/windowing-overview
- XamlRoot.RasterizationScale:
  https://learn.microsoft.com/en-us/uwp/API/windows.ui.xaml.xamlroot.rasterizationscale?view=winrt-22621
- AppWindowChangedEventArgs (DidSizeChange/DidPresenterChange/DidZOrderChange...), updated 2026-07-28:
  https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindowchangedeventargs
- WM_DPICHANGED_AFTERPARENT: https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged-afterparent
- High-DPI improvements in Creators Update (2017-04-04):
  https://blogs.windows.com/windowsdeveloper/2017/04/04/high-dpi-scaling-improvements-desktop-applications-Windows-10-creators-update/
- Setting default DPI awareness (dpiAwareness vs dpiAware):
  https://learn.microsoft.com/en-us/windows/win32/hidpi/setting-the-default-dpi-awareness-for-a-process
- mpv manifest mirrors (2024 `True/PM`; late 2025 `PerMonitorV2,PerMonitor`):
  https://git.tjdev.de/mirror/mpv/src/commit/d482bf6ef0cacf58efe70daf4d2d721eff1a7b4f/osdep/mpv.exe.manifest ,
  https://git.quad4.io/Mirrors/mpv/src/commit/ebb0fe68d64f8acac5c20c045f5890a69ace5cd2/osdep/mpv.exe.manifest
- mpv w32_common.c (parent hooks, DPI): https://github.com/mpv-player/mpv/blob/master/video/out/w32_common.c
- mpv commit 31f28da "use Windows hooks for --wid parent resize" (2016-08-25):
  https://github.com/mpv-player/mpv/commit/31f28da
- XamlRoot.Changed on scale change (blog): https://zakariathr22.github.io/blogs/10.html
- WinUI window resize and DPI (blog): https://citelao-github-io-private.pages.dev/posts/resize_winui_window_part_2_resize
- microsoft-ui-xaml #7131 (custom title bar caption buttons; fixed in 1.4), 2022-05-21 to 2024-02-23:
  https://github.com/microsoft/microsoft-ui-xaml/issues/7131

## 3. Cross-process child windows and mpv `--wid` on Windows

### Answer

**What mpv actually does with `--wid` on Windows (master, same since at least v0.29):**

- It creates **its own** window, class `"mpv"`, as a child of your HWND:
  `CreateWindowExW(WS_EX_NOPARENTNOTIFY, ..., WS_CHILD | WS_VISIBLE, 0, 0, parentClientW,
  parentClientH, parent, ...)`, sized to your client rect. [w32_common.c]
- Then it calls **`EnableWindow(w32->window, 0)` whenever a parent is set** — the embedded window
  is disabled. A disabled child gets no keyboard/mouse; Windows hands the child's mouse messages
  to the parent. So by default **your host HWND receives the mouse and keyboard, and mpv's OSC
  sees nothing.** Checked in v0.29.0, v0.35.0 and master source. [w32_common.c v0.29.0 / v0.35.0 /
  master], [MSDN "Disabled Windows"]
- That matches field reports: OSC did not appear with `--wid` on Windows; the fix was to forward
  mouse input yourself (SO 69841076, mpv 0.33, 2021-11-24). Two 2026 embedders hit the same
  thing: "creates its video window disabled when embedded with --wid" — they re-enable it on the
  first `WM_MOUSEMOVE` and focus it on click (argygkou/iptv-player PR #4, 2026-10-30); a WinUI 3
  Rust player found "clicks fall through its window and keyboard focus stays with WinUI" and
  handled keys in its own host window class (kDylanHayes/rust-winui-mpv PR #5, 2026-11-07).
- mpv never calls `SetFocus`, `AttachThreadInput` or handles `WM_MOUSEACTIVATE`; focus is only
  tracked (`WM_SETFOCUS/KILLFOCUS` -> `focused` property). Mouse down does `SetCapture` on its own
  window; mouse move uses `TrackMouseEvent(TME_LEAVE)` (enter/leave drive OSC show/hide);
  `WM_MOUSEWHEEL`/`WM_MOUSEHWHEEL` map to `WHEEL_*`; `WM_SETCURSOR` hides the cursor when the core
  says so (cursor autohide). [w32_common.c]
- **Window dragging is not disabled in `--wid` mode**: `begin_dragging()` does
  `SendMessage(own hwnd, WM_NCLBUTTONDOWN, HTCAPTION, 0)` without checking `w32->parent`. With an
  enabled embedded window a click-drag can try to drag mpv's child window. Set
  `--window-dragging=no`. [w32_common.c]
- `window-id` property returns mpv's HWND (`VOCTRL_GET_WINDOW_ID`); alternatively
  `FindWindowEx(host, 0, "mpv", null)`. Use it to re-enable/focus the window. [input.rst
  properties], [w32_common.c]
- `--input-cursor` (default on, "Necessary to use the OSC") and `--input-vo-keyboard` only matter
  once mpv's window can receive input; the `--input-vo-keyboard` text is about X11 focus grabbing.
  [options.rst]
- HWND value: pass `--wid=` as the HWND **cast through `uint32`**; casting to `int64` can produce a
  negative number on long-uptime machines and mpv then opens a detached top-level window.
  [mpv #10189, 2022-05-13 to 2026-10-21]

**Cross-process parent/child consequences (Win32):**

- Creating a parent/child (or owner/owned) relationship across threads **implicitly attaches the
  input queues**, transitively. Raymond Chen: legal, but consequences "can be very difficult to
  manage". [Old New Thing 2013-04-12]
- Attached queues share focus/active-window/key state and input retrieval: a thread that does not
  pump can leave input stuck for all attached threads; a blocked GUI thread blocks the other via
  synchronously sent messages ("do not block a GUI thread"). [Old New Thing 2013-06-06,
  2013-06-19, 2008-08-01], [SO 16817112, 2013-05-29]
- Concretely for FluentFin: a hung mpv GUI thread can freeze WinUI input; WinUI calls that
  *send* to mpv's window block (`DestroyWindow(host)` sends `WM_DESTROY` to mpv's child,
  `EnableWindow`, `SetFocus`, a non-async `SetWindowPos` on mpv's HWND). mpv itself resizes with
  `SWP_ASYNCWINDOWPOS`, so the resize path is safe. Video rendering runs on mpv's VO thread, not
  the GUI thread, so a busy WinUI UI thread stalls mpv **input**, not playback.
- Parent destruction: mpv treats `WM_DESTROY`/`WM_NCDESTROY` of its window as a close request
  (comment: "This can also happen in --wid mode when the parent window is destroyed"), queues
  `CLOSE_WIN` and exits its loop. With a persistent host that is only at app exit. [w32_common.c],
  [commit 31f28da 2016-08-25: hooks also detect parent destruction]
- DPI change: mpv has no `WM_DPICHANGED_AFTERPARENT` handler; it refreshes DPI on `WM_SIZE`, which
  arrives when you resize the host for the new scale (section 2).
- Windows 11 caveat (unresolved upstream): windows placed **inside** the XAML island got no mouse
  (went to `MITMessageOnlyWindowClass`) [discussion #9912]. Sibling-of-bridge hosts did receive
  clicks in the 2026 WinUI 3 Rust player above. Verify on the target build in the first spike.

### Two workable input models

| | A. Let mpv own input (re-enable its window) | B. Keep mpv disabled, forward over IPC |
|---|---|---|
| How | Host `WM_MOUSEMOVE` -> `EnableWindow(mpvHwnd, TRUE)`; host `WM_MOUSEACTIVATE` (DefWindowProc forwards it to the parent) -> `SetFocus(mpvHwnd)` | Host WndProc gets all input; send `mouse x y`, `keydown/keyup MBTN_LEFT`, `keypress WHEEL_UP`, `keydown/keyup <key>` |
| OSC | Native (hover, drag seekbar, double-click, cursor autohide, wheel) | Works if coordinates/buttons are forwarded; cursor autohide must be done by host (`WM_SETCURSOR`) |
| Keyboard | mpv bindings fire while its window has focus; WinUI KeyboardAccelerators do **not** | WinUI keeps focus; you translate keys |
| Latency | None extra | Every mouse move crosses C# -> pipe (-> Python?) -> mpv |
| Risk | Focus "trap" (focus stays in mpv until user clicks XAML); `q`/`Q` quit bindings | More code; seekbar drag can feel laggy if routed through Python |
| libmpv later | Same | Same (or call `mpv_command` directly) |

### Risk for this design: MEDIUM-HIGH

Not a blocker, but the default mpv behaviour (disabled window) means **the OSC does nothing until
FluentFin handles input explicitly**, and the attached input queues make an mpv hang look like a
FluentFin hang.

### Recommended approach

Model **A**, with guard rails:

```csharp
// Host WndProc (FluentFin UI thread)
case WM_MOUSEMOVE:
    var mpv = FindWindowEx(_host, IntPtr.Zero, "mpv", null);   // or from Python: window-id
    if (mpv != IntPtr.Zero && !IsWindowEnabled(mpv)) EnableWindow(mpv, true);  // once per mpv window
    break;
case WM_MOUSEACTIVATE:                       // sent to mpv's window, DefWindowProc passes it up
    var m = FindWindowEx(_host, IntPtr.Zero, "mpv", null);
    if (m != IntPtr.Zero) SetFocus(m);       // keyboard to mpv while the video is clicked
    return MA_ACTIVATE;
```

- Re-run the enable check whenever mpv recreates its VO window (new mpv process, VO reinit);
  cheapest is "on every host `WM_MOUSEMOVE`, if a disabled `mpv` child exists, enable it".
- mpv options: `--wid=<(uint)hwnd>`, `--window-dragging=no`, `--input-default-bindings=yes`,
  and an `input.conf` that unbinds
  `q`, `Q` (`ignore`; they would quit the persistent mpv) and maps app-level keys to
  `script-message fluentfin <action>` so Python relays them to C# (back, next episode). Leave
  `f`, double-click and `ESC` on the `fullscreen` property; the host observes it (section 4).
- Return focus to XAML: when the user presses Esc/back (`script-message`) or clicks XAML, call
  `placeholderOrPage.Focus(FocusState.Programmatic)` from C#; Python can observe the `focused`
  property to keep C# in sync.
- Hang watchdog (C#, background thread, every 1–2 s while playing):
  `SendMessageTimeout(mpvHwnd, WM_NULL, 0, 0, SMTO_ABORTIFHUNG | SMTO_BLOCK, 1000, out _)`;
  on failure (or `IsHungAppWindow`), kill the mpv process — its windows die, the queues detach,
  FluentFin input recovers; the persistent host survives and the next mpv gets the same `--wid`.
- Shutdown order: IPC `quit` -> wait for mpv exit (2–3 s) -> kill if still alive -> only then
  close/destroy the WinUI window (avoids `WM_DESTROY` being sent into a hung mpv thread).
- Never make other blocking cross-process calls on mpv's HWND from the UI thread (only the
  `EnableWindow`/`SetFocus` above; the watchdog runs off-thread); never `SetParent` mpv's window;
  never `AttachThreadInput` manually.
- `WM_MOUSEACTIVATE` reaching the host relies on mpv's `DefWindowProc` passing it to the parent
  (documented) and on it being sent for clicks into a child window (child windows are never the
  active window; the 2026 Tauri embedder relied on the same). Confirm in the spike; fallback is
  to `SetFocus(mpvHwnd)` from the first host `WM_LBUTTONDOWN` before re-enabling.
- If model A's focus trap is unacceptable, use model B but **open a second IPC connection from C#
  directly to mpv's pipe** for input (mpv names pipes `\\.\pipe\<name>`; JSON commands such as
  `{"command":["mouse",x,y]}`) rather than routing mouse moves through Python. The ipc docs do not
  state the multi-client limit explicitly; verify that two clients can connect to one
  `--input-ipc-server` in 0.41 during the spike (mpv 0.42 also accepts a Windows handle for
  `--input-ipc-client`). [ipc.rst], [v0.42.0 release notes]

### Sources

- mpv video/out/w32_common.c (master; EnableWindow, class "mpv", hooks, drag, wheel, DPI):
  https://github.com/mpv-player/mpv/blob/master/video/out/w32_common.c ;
  v0.35.0: https://github.com/mpv-player/mpv/blob/v0.35.0/video/out/w32_common.c ;
  v0.29.0: https://github.com/mpv-player/mpv/blob/v0.29.0/video/out/w32_common.c
- mpv options manual (--wid, --input-cursor, --input-vo-keyboard, --window-dragging):
  https://github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst
- mpv input manual (mouse/keypress/keydown/keyup commands; window-id, focused, mouse-pos):
  https://github.com/mpv-player/mpv/blob/master/DOCS/man/input.rst
- mpv IPC manual (named pipes, request_id, async): https://github.com/mpv-player/mpv/blob/master/DOCS/man/ipc.rst
- mpv #10189 "libmpv sometimes creates a detached window with wid" (2022-05-13; closed 2023-01-09):
  https://github.com/mpv-player/mpv/issues/10189
- mpv commit search for --wid history (31f28da 2016-08-25, 8d02983 2017-07-03, 7a5ecf1 2025-07-30):
  https://github.com/mpv-player/mpv/commit/31f28da , https://github.com/mpv-player/mpv/commit/8d02983 ,
  https://github.com/mpv-player/mpv/commit/7a5ecf1
- SO 69841076 "mpv osc not working on attached window with wid option" (2021-11-04/24):
  https://stackoverflow.com/questions/69841076
- argygkou/iptv-player PR #4 (libmpv --wid, re-enable on WM_MOUSEMOVE, 2026-10-30):
  https://github.com/argygkou/iptv-player/pull/4
- kDylanHayes/rust-winui-mpv PR #5 (WinUI 3 + libmpv, input via own host class, 2026-11-07):
  https://github.com/kDylanHayes/rust-winui-mpv/pull/5
- Disabled Windows (MSDN archive): https://techshelps.github.io/MSDN/WINUI/devdoc/live/pdui/windows_7bcj.htm
- WM_MOUSEACTIVATE: https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mouseactivate
- Raymond Chen, cross-process parent/child (2013-04-12):
  https://devblogs.microsoft.com/oldnewthing/20130412-00/?p=4683
- Raymond Chen, attached input queues and stuck messages (2013-06-06, 2013-06-19, 2008-08-01):
  https://devblogs.microsoft.com/oldnewthing/20130606-00/?p=4153 ,
  https://devblogs.microsoft.com/oldnewthing/20130619-00/?p=4043 ,
  https://devblogs.microsoft.com/oldnewthing/20080801-00/?p=21393
- SO 16817112 "parent window freezes when child window freezes" (2013-05-29):
  https://stackoverflow.com/questions/16817112
- WinUI discussion #9912 (Windows 11 input to embedded Win32 window, 2024-08-20):
  https://github.com/microsoft/microsoft-ui-xaml/discussions/9912

## 4. Fullscreen and resize flicker; mpv options for embedding

### Answer

- **mpv ignores its own fullscreen in `--wid` mode.** In w32_common.c `update_fullscreen_state()`
  returns early when `w32->parent` is set (so does `update_window_style`, `window_set_pos`,
  geometry/autofit handling). The `fullscreen` **property still toggles** (OSC button,
  `f`, double-click), it just has no visual effect. [w32_common.c master]
- Embedders drive the host window from that property: the Tauri/libmpv player lets "mpv's
  `fullscreen` property drive" the host window; IINA (macOS) bridges its own window state to mpv's
  `fullscreen`; mpv's Cocoa backend likewise disables fullscreen when embedded. [iptv-player PR #4,
  2026-10-30], [IINA wiki], [mpv cocoa commit 69fa956 mirror]
- Host fullscreen in WinUI: `AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen)` (hides
  title bar, covers taskbar) and back with `AppWindowPresenterKind.Overlapped`; react to
  `AppWindow.Changed` with `DidPresenterChange`. FullScreen and Maximized are different states (a
  maximized window keeps the caption). [Windowing overview 2026-10-14],
  [AppWindowChangedEventArgs], [MS Q&A 2104096]
- mpv 0.41 also handles **runtime `--wid` changes**, so an alternative is a separate borderless
  top-level fullscreen window and `set wid <hwnd>`; this avoids relayout of the main page but
  doubles the hosting code. Use only if presenter switching looks bad. [v0.41.0 release notes,
  2025-12-21]
- Flicker/black frames on resize — what to expect:
  - Resize chain is asynchronous across two processes: your `SetWindowPos(host)` -> mpv's
    out-of-context WinEvent (`EVENT_OBJECT_LOCATIONCHANGE`) -> `SetWindowPos(mpv,
    SWP_ASYNCWINDOWPOS)` -> `WM_SIZE` -> VO thread resizes the swap chain and redraws. mpv's own
    comment calls the cross-process hook less smooth; a 2017 commit added `SWP_NOSENDCHANGING` to
    reduce resize glitches. Expect 1–3 frames where the old frame is stretched/cropped or an edge
    of the host shows. [w32_common.c], [commit 8d02983, 2017-07-03]
  - With DXGI flip model (mpv default `--d3d11-flip=yes`), DWM shows the previous buffer scaled
    per the swap chain's `DXGI_SCALING` until the app calls `ResizeBuffers` and presents; flip
    model is required for DirectFlip/Independent Flip, which a fullscreen video can reach when its
    buffers cover the screen. [MS "For best performance, use DXGI flip model", updated 2026-04-15]
  - The uncovered part of your host during a resize shows whatever is under it; your host's GDI
    background may not be drawn (section 1). Make the XAML placeholder `Background="Black"` so
    any gap is black, not page content.
  - Do not resize to 0x0 (gpu-next/libplacebo treat a 0x0 resize as a no-op; Wayland had a
    related race). Hide instead. [mpv vo_gpu_next commit history mirror]
  - No mpv issue found that reports systematic black-frame flicker for d3d11 + `--wid` on
    Windows; most resize-stutter reports are Linux/compositor specific (e.g. mpv #9417, closed
    2023-05-22, fixed in the app by moving mpv calls off the UI thread).
- mpv 0.41 defaults that matter: `gpu-next` is the default VO; swap-chain depth default is 2;
  `--target-colorspace-hint=auto`. mpv 0.42 (2026-11-05) adds the `floating` OSC layout with
  Fluent icons (`--script-opts=osc-layout=floating`), configurable OSC window-control buttons,
  and a right-click context menu — the floating layout suits a Fluent app if you can move to
  0.42. [v0.41.0 and v0.42.0 release notes]
- `--keepaspect-window` / `--auto-window-resize` / `--geometry` / `--autofit` do nothing in
  `--wid` mode (mpv never sizes the parent; `--geometry` is documented as ignored). Setting them
  is harmless. `--keepaspect=yes` (default) letterboxes inside your rect. [options.rst --wid]
- `--force-window=yes` + `--idle=yes`: the VO window exists while idle, so the window (and your
  re-enable logic) survives between items; `immediate` creates it at start but "may cause other
  issues". [options.rst --force-window, --idle]
- `--background` is about alpha blending; the window fill outside the video is
  `--background-color` (default black) in `--background=color` mode. [options.rst]

### Risk for this design: LOW (fullscreen), MEDIUM (resize polish)

Fullscreen is a solved pattern. Resize polish is the visible weak spot of an out-of-process
`--wid` child: expect short stretch/edge artefacts during live window resizes and presenter
switches; acceptable for a media app, not fixable from C#.

### Recommended approach

Fullscreen, property-driven so the OSC button, `f`, double-click and Esc all work:

```csharp
// Python observes mpv "fullscreen" and sends {"event":"fullscreen","value":true|false} to C#.
void OnMpvFullscreen(bool on)
{
    var aw = _window.AppWindow;
    bool isFs = aw.Presenter.Kind == AppWindowPresenterKind.FullScreen;
    if (on == isFs) return;
    _shell.ChromeVisibility = on ? Visibility.Collapsed : Visibility.Visible; // nav, title bar, margins
    aw.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    // SyncHost() runs from AppWindow.Changed(DidPresenterChange) + LayoutUpdated.
}
// App-side exit (XAML back button, window un-fullscreened by system): tell Python "set fullscreen no"
// so mpv's OSC icon state stays in sync.
```

mpv command line for the persistent embedded instance (0.41):

```
mpv --idle=yes --force-window=yes --wid=<(uint)host>
    --input-ipc-server=\\.\pipe\fluentfin-mpv-<pid>
    --vo=gpu-next --gpu-api=d3d11 --gpu-context=d3d11 --hwdec=auto-safe
    --d3d11-flip=yes                       # default; keep for DirectFlip in fullscreen
    --keepaspect=yes --background-color=#000000
    --window-dragging=no                   # see section 3
    --cursor-autohide=1000
    --osc=yes --script-opts=osc-idlescreen=no,osc-windowcontrols=no
    --input-default-bindings=yes --input-conf=<app>\input.conf
    --config-dir=<app>\mpv                 # isolate from a user's own %APPDATA%\mpv config
    --keep-open=yes                        # optional: hold last frame for "next episode" UI
    --log-file=<app logs>\mpv.log
```

- `input.conf` (app copy): keep `ESC set fullscreen no` and `f cycle fullscreen` (they now just
  flip the property the host observes); `q ignore`, `Q ignore`; `MBTN_LEFT_DBL cycle fullscreen`.
- Placeholder `Background="Black"`; never animate the placeholder's size (every animation frame
  becomes a cross-process resize); for page transitions hide the host, animate, then show.
- During live window resizing you can reduce work by showing the host only after
  `WM_EXITSIZEMOVE` — optional; test first, mpv usually keeps up.

### Sources

- mpv w32_common.c (fullscreen early-return, resize hooks): https://github.com/mpv-player/mpv/blob/master/video/out/w32_common.c
- mpv commit 8d02983 "use SWP_NOSENDCHANGING when resizing child" (2017-07-03):
  https://github.com/mpv-player/mpv/commit/8d02983
- mpv v0.41.0 release (2025-12-21): https://github.com/mpv-player/mpv/releases/tag/v0.41.0
- mpv v0.42.0 release (2026-11-05): https://github.com/mpv-player/mpv/releases/tag/v0.42.0
- mpv options manual: https://github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst
- mpv #9417 (libmpv resize/playback stutter, Linux; 2021-11-07 to 2023-05-22):
  https://github.com/mpv-player/mpv/issues/9417
- For best performance, use DXGI flip model (updated 2026-04-15):
  https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model
- Windowing overview / presenters (2026-10-14): https://learn.microsoft.com/en-us/windows/apps/develop/ui/windowing-overview
- MS Q&A 2104096 (FullScreen vs Maximized): https://learn.microsoft.com/en-us/answers/questions/2104096/net-maui-min-max-hide-not-showing
- argygkou/iptv-player PR #4 (fullscreen property drives host, 2026-10-30): https://github.com/argygkou/iptv-player/pull/4
- IINA "MPV Options and Properties" wiki: https://github.com/lhc70000/iina/wiki/MPV-Options-and-Properties
- mpv cocoa "disable some features when embedding" (mirror): https://git.tjdev.de/mirror/mpv/commit/69fa956f5012f92186e123af37b5bbff8554bbdc

## 5. Alternatives: libmpv render API into a SwapChainPanel

### Answer

The airspace problem disappears only if the video becomes **XAML content** (a `SwapChainPanel`).
Every mpv route to that needs the D3D objects in FluentFin's process, i.e. **libmpv in-process**.

| Route | mpv version | What you do | Effort | Notes |
|---|---|---|---|---|
| **A. libmpv + composition mode** | ≥ 0.41 (PR #16285 merged 2025-07-06; resize PR #16804 2025-09-19) | `d3d11-output-mode=composition`, read `display-swapchain` (int64 address of the IDXGISwapChain), hand it to `ISwapChainPanelNative.SetSwapChain`; on panel resize set `d3d11-composition-size=WxH` | Small–medium | The options manual says WinUI apps using the D3D11 backend need composition mode. No window is created, so **no native input**: forward mouse/keys with `mpv_command` (`mouse`, `keypress`, ...). OSC still draws (it is in the swap chain). PR author demoed it on WinUI 2 and WinUI 3. Color space is reported "unknown" in this mode (PR #16795). |
| **B. libmpv render API, D3D11 backend** | 0.42.0 (PR #17764, merged 2026-10-15) | `MPV_RENDER_API_TYPE_D3D11`; pass your `ID3D11Device` (`MPV_RENDER_PARAM_D3D11_INIT_PARAMS`); each frame mpv renders into **your** `ID3D11Texture2D` (`MPV_RENDER_PARAM_D3D11_FBO`); you own swap chain + present | Medium–high | Full control (XAML effects, your own present timing). A reviewer hit `ResizeBuffers` errors from held buffer references (fixed by releasing the wrapped texture per frame). The maintainer: window embedding "may be a better solution on Windows" because the render API is limited. |
| C. libmpv OpenGL render API -> D3D (ANGLE / `WGL_NV_DX_interop`) -> SwapChainPanel | any | Richasy/mpv-winui and `Richasy.MpvKernel.WinUI` (used in Bili.Copilot) | High | Pre-dates A/B; extra copy and interop; mpv-winui last pushed ~2024-09. |
| D. Cross-process swap chain handle | n/a (needs mpv patch) | FluentFin: `DCompositionCreateSurfaceHandle` -> `DuplicateHandle` to mpv -> mpv: `CreateSwapChainForCompositionSurfaceHandle`; FluentFin: `ISwapChainPanelNative2.SetSwapChainHandle` | High + fork | The only way to keep mpv **out of process** and still be XAML content. The API exists for a two-process (VoIP) model, and WinUI 3 ships `ISwapChainPanelNative2`, but stock mpv cannot build its swap chain from a handle. |
| E. Non-mpv players | — | Flyleaf (`FlyleafLib.Controls.WinUI`, FFmpeg + D3D11, LGPL-3.0, v3.11 README); LibVLCSharp (SwapChainPanel); `MediaPlayerElement` | Medium | Gives up mpv's OSC/scripts/config; Flyleaf has its own HDR/D3D11VP pipeline. |

**Why the Python/JSON-IPC architecture cannot do A or B:** `display-swapchain` is a raw pointer valid
only inside the mpv process; the render API needs a shared `ID3D11Device` and texture objects;
none of that crosses a JSON pipe. FluentFin would load `libmpv-2.dll` itself (P/Invoke), and
Python would at most become a client of the *same* libmpv instance through `input-ipc-server`
(libmpv can still open an IPC server). mpv 0.42 also accepts a Windows handle for
`--input-ipc-client`, which makes "C# owns libmpv, Python talks to it over a pipe handle" a
reasonable migration shape.

### Risk for this design: n/a (fallback) — Route A is the realistic Plan B

Route A keeps mpv's OSC, scripts and options, needs only `libmpv-2.dll` + a P/Invoke layer, and
removes airspace, z-order, cross-process input-queue and positioning problems in one go. Its cost
is input forwarding (same work as model B in section 3) and moving mpv into the WinUI process
(an mpv crash then takes FluentFin down; a hung mpv core no longer hangs FluentFin's input via
attached queues, but a deadlock on the UI thread becomes possible if you call blocking libmpv
APIs there — keep libmpv calls off the UI thread, as the 2026 Tauri embedder also did).

### Recommended approach (if/when switching)

1. Keep the `--wid` design for v1 (section 1–4); spike Route A in parallel on mpv ≥ 0.41:
   ```csharp
   // UI thread: XAML <SwapChainPanel x:Name="Video"/>
   mpv = mpv_create();
   mpv_set_option_string(mpv, "vo", "gpu-next");
   mpv_set_option_string(mpv, "gpu-context", "d3d11");
   mpv_set_option_string(mpv, "d3d11-output-mode", "composition");
   mpv_set_option_string(mpv, "force-window", "yes");     // creates the VO/swap chain while idle
   mpv_set_option_string(mpv, "input-ipc-server", @"\\.\pipe\fluentfin-mpv");  // Python stays a client
   mpv_initialize(mpv);
   // after the VO is up (observe "display-swapchain" or poll once):
   long p = mpv_get_property_int64(mpv, "display-swapchain");
   var native = Video.As<ISwapChainPanelNative>();          // declaration from microsoft.ui.xaml.media.dxinterop.h
   native.SetSwapChain((IntPtr)p);                         // on the UI thread
   // Video.SizeChanged / CompositionScaleChanged:
   //   mpv_set_property_string(mpv, "d3d11-composition-size", $"{pxW}x{pxH}");
   ```
2. Forward pointer/keys from the `SwapChainPanel` (`PointerMoved` -> `mouse x y` in physical
   pixels; `PointerPressed/Released` -> `keydown/keyup MBTN_LEFT`; `PointerWheelChanged` ->
   `keypress WHEEL_UP/DOWN`; `KeyDown/KeyUp` -> `keydown/keyup`).
3. Only consider Route B if you need XAML effects on the video itself or exact present control;
   Route D only if keeping mpv out of process is a hard requirement (it means maintaining an mpv
   patch).

### Sources

- mpv PR #16285 "d3d11 composition mode support" (2025-04-26, merged 2025-07-06):
  https://github.com/mpv-player/mpv/pull/16285
- mpv PR #16795 "unknown color space in composition mode" (2025-09-15/16):
  https://github.com/mpv-player/mpv/pull/16795
- mpv PR #16804 "changing size support for composition mode" (2025-09-17, merged 2025-09-19):
  https://github.com/mpv-player/mpv/pull/16804
- mpv PR #17764 "libmpv: add D3D11 render API backend" (2026-04-17, merged 2026-10-15, v0.42.0):
  https://github.com/mpv-player/mpv/pull/17764
- mpv PR #12627 (earlier D3D11 render API attempt, closed 2023-10-15): https://github.com/mpv-player/mpv/pull/12627
- mpv issue #5979 "d3d11 backend for the Render API" (2018-07-09, open):
  https://github.com/mpv-player/mpv/issues/5979
- mpv options manual (`--d3d11-output-mode`, `--d3d11-composition-size`) and input manual
  (`display-swapchain`): https://github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst ,
  https://github.com/mpv-player/mpv/blob/master/DOCS/man/input.rst
- mpv v0.41.0 (2025-12-21) and v0.42.0 (2026-11-05) release notes:
  https://github.com/mpv-player/mpv/releases/tag/v0.41.0 , https://github.com/mpv-player/mpv/releases/tag/v0.42.0
- ISwapChainPanelNative (WinUI 3, App SDK 0.5+):
  https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/win32/microsoft.ui.xaml.media.dxinterop/nn-microsoft-ui-xaml-media-dxinterop-iswapchainpanelnative
- ISwapChainPanelNative2 (WinUI 3) and SetSwapChainHandle (two-process rationale):
  https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/win32/microsoft.ui.xaml.media.dxinterop/nn-microsoft-ui-xaml-media-dxinterop-iswapchainpanelnative2 ,
  https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.xaml.media.dxinterop/nf-windows-ui-xaml-media-dxinterop-iswapchainpanelnative2-setswapchainhandle
- DCompositionCreateSurfaceHandle: https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositioncreatesurfacehandle
- Composition visual layer limits (SwapChainPanel is external content, no acrylic over it):
  https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/composition
- Richasy/mpv-winui (libmpv OpenGL -> SwapChainPanel test project): https://github.com/Richasy/mpv-winui ;
  NuGet Richasy.MpvKernel.WinUI: https://www.nuget.org/packages/Richasy.MpvKernel.WinUI
- Dixeran/mpv-uwp (patched mpv exposing a swap chain to SwapChainPanel): https://github.com/Dixeran/mpv-uwp
- Flyleaf (FFmpeg + D3D11, WinUI 3/WPF/WinForms, LGPL-3.0): https://github.com/SuRGeoNix/Flyleaf ;
  NuGet FlyleafLib.Controls.WinUI: https://www.nuget.org/packages/FlyleafLib.Controls.WinUI/
- Mpv.NET (WinForms/WPF, HWND-based; original hudec117 repo returned 404 on 2026-11-13):
  https://github.com/andrew-mi/Mpv.NET
- NickvisionApps/MPVSharp (C# libmpv bindings incl. render context): https://github.com/NickvisionApps/MPVSharp
