<#
  Dot-sourced by offscreen\drive.ps1, which runs ON the private desktop: FluentFin's window there, UI Automation, input
  posted to FluentFin's own input window (SendInput only reaches the input desktop, so nothing here can type into the
  user's desktop), window-only screenshots (PrintWindow with PW_RENDERFULLCONTENT) and the accessibility scan.
  Every window function takes the pid of a FluentFin this script started and refuses windows of any other process.
#>

Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes, WindowsBase
if (-not ('OffscreenUi' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing, UIAutomationClient, UIAutomationTypes, WindowsBase, System.Core @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

public static class OffscreenUi {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder s, int n, out int need);

  public static uint ProcessOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
  public static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
  static string DeskName(IntPtr d) { var s = new StringBuilder(256); int n; GetUserObjectInformationW(d, 2, s, 512, out n); return s.ToString(); }
  public static string ThisDesktop() { return DeskName(GetThreadDesktop(GetCurrentThreadId())); }
  // the desktop the user sees (null when it cannot be opened); this script refuses to run there
  public static string InputDesktop() { var d = OpenInputDesktop(0, false, 0x0001); if (d == IntPtr.Zero) return null; try { return DeskName(d); } finally { CloseDesktop(d); } }

  // FluentFin's main window: a visible WinUI top-level window of process pid with a real size
  public static IntPtr MainWindow(int pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      RECT r;
      if (ProcessOf(h) == (uint)pid && IsWindowVisible(h) && ClassOf(h) == "WinUIDesktopWin32WindowClass" && GetWindowRect(h, out r) && r.R - r.L > 200 && r.B - r.T > 200) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static List<IntPtr> Children(IntPtr parent) { var l = new List<IntPtr>(); EnumChildWindows(parent, (h, x) => { l.Add(h); return true; }, IntPtr.Zero); return l; }

  // the WinUI content's input window (the XAML island bridge) of FluentFin's window
  public static IntPtr InputWindow(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    foreach (var c in Children(main)) if (ProcessOf(c) == (uint)pid && ClassOf(c) == "Microsoft.UI.Content.DesktopChildSiteBridge") return c;
    return main;
  }

  // the window WinUI takes pointer input in: the XAML island's InputSiteWindowClass child (under the bridge), else the
  // bridge
  public static IntPtr PointerWindow(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    foreach (var c in Children(main)) if (ProcessOf(c) == (uint)pid && ClassOf(c) == "InputSiteWindowClass" && IsWindowVisible(c)) return c;
    return InputWindow(main, pid);
  }

  static IntPtr Lp(int x, int y) { return (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)); }

  // a left click at screen point (x, y), posted to FluentFin's input window (never SendInput)
  public static void Click(IntPtr main, int pid, int x, int y) {
    var target = PointerWindow(main, pid);
    var p = new POINT { X = x, Y = y };
    ScreenToClient(target, ref p);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(p.X, p.Y));              // WM_MOUSEMOVE
    System.Threading.Thread.Sleep(60);
    PostMessage(target, 0x0201, (IntPtr)1, Lp(p.X, p.Y));                // WM_LBUTTONDOWN, MK_LBUTTON
    System.Threading.Thread.Sleep(40);
    PostMessage(target, 0x0202, IntPtr.Zero, Lp(p.X, p.Y));              // WM_LBUTTONUP
  }

  // the pointer moving to screen point (x, y) over FluentFin: two posted WM_MOUSEMOVEs a few pixels apart (a move, not
  // just an arrival), to the window WinUI takes pointer input in
  public static void Hover(IntPtr main, int pid, int x, int y) {
    var target = PointerWindow(main, pid);
    var p = new POINT { X = x, Y = y };
    ScreenToClient(target, ref p);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(p.X - 6, p.Y - 6));
    System.Threading.Thread.Sleep(30);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(p.X, p.Y));
  }

  // a wheel notch at screen point (x, y), posted to the window WinUI takes pointer input in (never SendInput):
  // WM_MOUSEWHEEL (0x020A) or, with horizontal, WM_MOUSEHWHEEL (0x020E); wParam = delta (120 per notch, positive = up/right) in
  // the high word and MK_SHIFT (4) in the low word; lParam = the point in SCREEN coordinates (wheel messages carry those)
  public static void Wheel(IntPtr main, int pid, int x, int y, int delta, bool horizontal, bool shift) {
    var target = KeyWindow(main, pid);   // the (hidden) InputSiteWindowClass: the only window WinUI took a posted wheel in (bridge and main window: nothing)
    var p = new POINT { X = x, Y = y };
    var c = p; ScreenToClient(target, ref c);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(c.X - 4, c.Y - 4));
    System.Threading.Thread.Sleep(30);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(c.X, c.Y));
    System.Threading.Thread.Sleep(30);
    long w = ((long)(short)delta << 16) | (shift ? 4L : 0L);
    PostMessage(target, horizontal ? 0x020Eu : 0x020Au, (IntPtr)(uint)(w & 0xFFFFFFFF), Lp(p.X, p.Y));
  }

  // every scrollable element (UI Automation ScrollPattern) inside FluentFin: "name|class|x,y,w,h|h%|v%|hScrollable|vScrollable"
  public static List<string> ScrollState(IntPtr main, int pid) {
    var l = new List<string>();
    foreach (var e in Where(main, pid, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))) {
      try {
        var r = e.Cached.BoundingRectangle; if (r.IsEmpty) continue;
        var sp = (ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern);
        l.Add(string.Format("{0}|{1}|{2:0},{3:0},{4:0},{5:0}|{6:0.0}|{7:0.0}|{8}|{9}", e.Cached.Name, e.Cached.ClassName, r.X, r.Y, r.Width, r.Height,
          sp.Current.HorizontalScrollPercent, sp.Current.VerticalScrollPercent, sp.Current.HorizontallyScrollable, sp.Current.VerticallyScrollable));
      } catch (Exception) { }
    }
    return l;
  }

  // sizes FluentFin's window (a narrow or a wide check); SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
  public static void Resize(IntPtr main, int pid, int w, int h) { if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window"); SetWindowPos(main, IntPtr.Zero, 0, 0, w, h, 0x0002 | 0x0004 | 0x0010); }

  // the window WinUI reads posted keys from: the XAML island's InputSiteWindowClass child. WinUI keeps it hidden and still
  // takes the keys posted to it. Posted to the bridge (DesktopChildSiteBridge) instead, Tab still moved the focus but F
  // and Esc never reached the player page. Else the bridge, else the main window.
  public static IntPtr KeyWindow(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    var kids = Children(main);
    int i = PickKeyWindow(kids.Select(ClassOf).ToArray(), kids.Select(ProcessOf).ToArray(), (uint)pid);
    return i < 0 ? main : kids[i];
  }

  // which of a window's children (their classes and processes, in EnumChildWindows order) takes posted keys: the first
  // InputSiteWindowClass of process pid, visible or not (never one of another process: mpv's video window is a child
  // too), else its first DesktopChildSiteBridge; -1 for neither
  public static int PickKeyWindow(string[] classes, uint[] pids, uint pid) {
    for (int i = 0; i < classes.Length; i++) if (pids[i] == pid && classes[i] == "InputSiteWindowClass") return i;
    for (int i = 0; i < classes.Length; i++) if (pids[i] == pid && classes[i] == "Microsoft.UI.Content.DesktopChildSiteBridge") return i;
    return -1;
  }

  // a key press posted to the window FluentFin reads keys from (KeyWindow; WM_KEYDOWN/WM_KEYUP; shift adds Shift around it);
  // returns the window the key went to (drive.ps1 and offscreen\test_uia.ps1 check its class)
  public static IntPtr Key(IntPtr main, int pid, int vk, bool shift) {
    var target = KeyWindow(main, pid);
    uint scan = MapVirtualKey((uint)vk, 0);
    if (shift) PostMessage(target, 0x0100, (IntPtr)0x10, (IntPtr)(1 | (MapVirtualKey(0x10, 0) << 16)));
    PostMessage(target, 0x0100, (IntPtr)vk, (IntPtr)(1 | (scan << 16)));
    System.Threading.Thread.Sleep(30);
    PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31)));
    if (shift) PostMessage(target, 0x0101, (IntPtr)0x10, (IntPtr)(1 | (MapVirtualKey(0x10, 0) << 16) | (1u << 30) | (1u << 31)));
    return target;
  }

  public static uint ScanCode(int vk) { return MapVirtualKey((uint)vk, 0); }

  public static void Close(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    PostMessage(main, 0x0010, IntPtr.Zero, IntPtr.Zero);                  // WM_CLOSE
  }

  // PrintWindow(PW_RENDERFULLCONTENT) of FluentFin's window into a PNG; returns "WxH, n% not black[, video: m% not black]"
  public static string Shot(IntPtr main, int pid, string file) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    RECT r; GetWindowRect(main, out r);
    int w = r.R - r.L, h = r.B - r.T;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
      bmp.Save(file, ImageFormat.Png);
      string video = "";
      foreach (var c in Children(main)) {
        if (ProcessOf(c) == (uint)pid || !IsWindowVisible(c)) continue;
        RECT cr; GetWindowRect(c, out cr);
        video = string.Format(", video {0}x{1}: {2}% not black", cr.R - cr.L, cr.B - cr.T, Lit(bmp, cr.L - r.L, cr.T - r.T, cr.R - r.L, cr.B - r.T));
        break;
      }
      return string.Format("{0}x{1}, {2}% not black{3}", w, h, Lit(bmp, 0, 0, w, h), video);
    }
  }

  // percentage of not-black pixels of the embedded video (a child window of another process: mpv), -1 when there is none
  public static int VideoLit(IntPtr main, int pid) {
    RECT r; GetWindowRect(main, out r);
    foreach (var c in Children(main)) {
      if (ProcessOf(c) == (uint)pid || !IsWindowVisible(c)) continue;
      RECT cr; GetWindowRect(c, out cr);
      int w = r.R - r.L, h = r.B - r.T;
      using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
        using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
        return Lit(bmp, cr.L - r.L, cr.T - r.T, cr.R - r.L, cr.B - r.T);
      }
    }
    return -1;
  }

  // percentage of not-black pixels in a part of FluentFin's window given as fractions of its size: the video when mpv
  // draws inside FluentFin (composition, no child window to measure)
  public static int RegionLit(IntPtr main, int pid, double fx0, double fy0, double fx1, double fy1) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    RECT r; GetWindowRect(main, out r);
    int w = r.R - r.L, h = r.B - r.T;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
      return Lit(bmp, (int)(w * fx0), (int)(h * fy0), (int)(w * fx1), (int)(h * fy1));
    }
  }

  // the middle of the video (the box from fractions fx0..fx1, fy0..fy1 of the video: mpv's child window with mpv's
  // controls, else FluentFin's window, where mpv draws inside it), captured `frames` times `gapMs` apart: the number of
  // its pixels that changed between two captures in a row (|dR|+|dG|+|dB| > 96), and the box ("x,y,w,h", window
  // pixels). Over a still part of the video, anything that moves there is drawn over it: a spinning ring, an indicator.
  public static int[] MiddleChanges(IntPtr main, int pid, int frames, int gapMs, double fx0, double fy0, double fx1, double fy1) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    RECT r; GetWindowRect(main, out r);
    int w = r.R - r.L, h = r.B - r.T;
    int vx = 0, vy = 0, vw = w, vh = h;
    foreach (var c in Children(main)) {
      if (ProcessOf(c) == (uint)pid || !IsWindowVisible(c)) continue;
      RECT cr; GetWindowRect(c, out cr);
      vx = cr.L - r.L; vy = cr.T - r.T; vw = cr.R - cr.L; vh = cr.B - cr.T;
      break;
    }
    int x0 = Math.Max(0, vx + (int)(vw * fx0)), y0 = Math.Max(0, vy + (int)(vh * fy0));
    int x1 = Math.Min(w, vx + (int)(vw * fx1)), y1 = Math.Min(h, vy + (int)(vh * fy1));
    int bw = Math.Max(0, x1 - x0), bh = Math.Max(0, y1 - y0);
    var changed = new bool[bw * bh];
    int[] previous = null;
    for (int f = 0; f < frames; f++) {
      if (f > 0) System.Threading.Thread.Sleep(gapMs);
      var now = new int[bw * bh];
      using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb)) {
        using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
        if (bw > 0 && bh > 0) {
          var data = bmp.LockBits(new Rectangle(x0, y0, bw, bh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
          try { for (int y = 0; y < bh; y++) Marshal.Copy(data.Scan0 + y * data.Stride, now, y * bw, bw); }
          finally { bmp.UnlockBits(data); }
        }
      }
      if (previous != null) {
        for (int i = 0; i < now.Length; i++) {
          int a = previous[i], b = now[i];
          int d = Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) + Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) + Math.Abs((a & 255) - (b & 255));
          if (d > 96) changed[i] = true;
        }
      }
      previous = now;
    }
    return new[] { changed.Count(x => x), x0, y0, bw, bh };
  }

  // The same inputs as Key and Click, started on a thread of their own and returning at once, so a burst's grabs go on
  // while the key's 30 ms between down and up (or the click's pauses) pass
  public static void KeyAsync(IntPtr main, int pid, int vk) { new System.Threading.Thread(() => Key(main, pid, vk, false)) { IsBackground = true }.Start(); }
  // mpv's own window (mpv's controls): WM_KEYDOWN, WM_CHAR, WM_KEYUP as Send-PlayerKey posts them, on a thread of its own
  public static void KeyToAsync(IntPtr target, int vk, char ch) {
    new System.Threading.Thread(() => {
      long scan = (long)MapVirtualKey((uint)vk, 0) << 16;
      PostMessage(target, 0x0100, (IntPtr)vk, (IntPtr)(1 | scan));
      PostMessage(target, 0x0102, (IntPtr)(int)ch, (IntPtr)(1 | scan));
      System.Threading.Thread.Sleep(30);
      PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(1 | scan | 0xC0000000L));
    }) { IsBackground = true }.Start();
  }
  public static void ClickAsync(IntPtr main, int pid, int x, int y, int times) {
    new System.Threading.Thread(() => { for (int i = 0; i < times; i++) { Click(main, pid, x, y); } }) { IsBackground = true }.Start();
  }
  public static InvokePattern InvokePatternOf(AutomationElement e) { return (InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern); }
  public static void InvokeAsync(InvokePattern p) { new System.Threading.Thread(() => { try { p.Invoke(); } catch (Exception) { } }) { IsBackground = true }.Start(); }

  // A burst of window captures for the full-screen switch (docs/research/fullscreen-smoothness.md): `frames` PrintWindow
  // grabs back to back, the trigger (a posted key, an invoked button: it must only post/invoke, not wait) run just before
  // frame `triggerAt`; each frame keeps its own window rect, so a frame of the wrong size or place shows. Writes
  // <dir>\sheet.png (every frame at 1/6 scale, numbered with its time from the trigger) and returns CSV lines:
  // index, ms since the trigger (start of the grab), grab ms, window x y w h, % near black, % near white, picture box of the
  // not-dark pixels (x0 y0 x1 y1 in the window) and its aspect.
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
  public static bool IsCloaked(IntPtr h) { return Cloaked(h); }
  static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }
  public static List<string> Burst(IntPtr main, int pid, int frames, int triggerAt, Action trigger, string dir) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    System.IO.Directory.CreateDirectory(dir);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var bmps = new List<Bitmap>(); var rows = new List<string>(); var times = new List<double>(); var cloakedAt = new List<bool>();
    double trig = -1;
    for (int i = 0; i < frames; i++) {
      if (i == triggerAt) { trig = sw.Elapsed.TotalMilliseconds; trigger(); }
      double t0 = sw.Elapsed.TotalMilliseconds;
      RECT r; GetWindowRect(main, out r);
      bool cl0 = Cloaked(main);
      int w = Math.Max(1, r.R - r.L), h = Math.Max(1, r.B - r.T);
      var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
      double t1 = sw.Elapsed.TotalMilliseconds;
      cloakedAt.Add(cl0 || Cloaked(main));
      bmps.Add(bmp); times.Add(t0 - (trig < 0 ? t0 : trig));
      int black = 0, white = 0, n = 0, bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
      var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
      try {
        var line = new int[w];
        for (int y = 0; y < h; y += 2) {
          Marshal.Copy(d.Scan0 + y * d.Stride, line, 0, w);
          for (int x = 0; x < w; x += 2) {
            int p = line[x]; int s = ((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255);
            n++; if (s < 24) black++; if (s > 735) white++;
            if (s > 60) { if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y; }
          }
        }
      } finally { bmp.UnlockBits(d); }
      double asp = bx1 > bx0 && by1 > by0 ? (double)(bx1 - bx0) / (by1 - by0) : 0;
      rows.Add(string.Format("{0},{1:0},{2:0},{3} {4} {5}x{6},{7},{8},{9} {10} {11} {12},{13:0.000}", i, times[i], t1 - t0, r.L, r.T, w, h,
        100 * black / n, 100 * white / n, bx1 < 0 ? 0 : bx0, bx1 < 0 ? 0 : by0, bx1, by1, asp));
    }
    // each frame's whole window stretched to 128x72 grey, compared with the first frame (before the trigger) and the last (after
    // it): the mean absolute difference, 0-255. The video is paused, so a frame that is neither is a half-done layout.
    var thumbs = new List<double[]>();
    foreach (var b in bmps) {
      using (var t = new Bitmap(128, 72, PixelFormat.Format32bppRgb)) {
        using (var g = Graphics.FromImage(t)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear; g.DrawImage(b, 0, 0, 128, 72); }
        var v = new double[128 * 72]; var d = t.LockBits(new Rectangle(0, 0, 128, 72), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try { var line = new int[128]; for (int y = 0; y < 72; y++) { Marshal.Copy(d.Scan0 + y * d.Stride, line, 0, 128); for (int x = 0; x < 128; x++) { int p = line[x]; v[y * 128 + x] = (((p >> 16) & 255) + ((p >> 8) & 255) + (p & 255)) / 3.0; } } }
        finally { t.UnlockBits(d); }
        thumbs.Add(v);
      }
    }
    Func<double[], double[], double> diff = (a, b) => { double s = 0; for (int i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]); return s / a.Length; };
    for (int i = 0; i < frames; i++) rows[i] += string.Format(",{0:0.0},{1:0.0},{2}", diff(thumbs[i], thumbs[0]), diff(thumbs[i], thumbs[frames - 1]), cloakedAt[i] ? 1 : 0);
    int cols = 6, tw = 320, th = 190, lab = 14, rowsN = (frames + cols - 1) / cols;
    using (var sheet = new Bitmap(cols * tw, rowsN * (th + lab)))
    using (var g = Graphics.FromImage(sheet)) {
      g.Clear(Color.FromArgb(40, 40, 40));
      g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
      using (var f = new Font("Segoe UI", 8f)) for (int i = 0; i < frames; i++) {
        int cx = (i % cols) * tw, cy = (i / cols) * (th + lab);
        double k = Math.Min((double)(tw - 4) / bmps[i].Width, (double)th / bmps[i].Height);
        g.DrawImage(bmps[i], cx + 2, cy + lab, (int)(bmps[i].Width * k), (int)(bmps[i].Height * k));
        g.DrawString(string.Format("#{0} {1:0} ms {2}x{3}", i, times[i], bmps[i].Width, bmps[i].Height), f, Brushes.White, cx + 2, cy);
      }
      sheet.Save(System.IO.Path.Combine(dir, "sheet.png"), ImageFormat.Png);
    }
    // the first frame before the trigger, the last, and every frame full size is too much: the ones around the switch
    for (int i = 0; i < frames; i++) { if (i >= triggerAt - 1 && i <= triggerAt + 12 || i == frames - 1) bmps[i].Save(System.IO.Path.Combine(dir, string.Format("f{0:00}.png", i)), ImageFormat.Png); bmps[i].Dispose(); }
    return rows;
  }

  static int Lit(Bitmap b, int x0, int y0, int x1, int y1) {
    x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(b.Width, x1); y1 = Math.Min(b.Height, y1);
    if (x1 <= x0 || y1 <= y0) return 0;
    int lit = 0, n = 0;
    for (int i = 0; i < 40; i++) for (int j = 0; j < 40; j++) {
      var p = b.GetPixel(x0 + (x1 - x0 - 1) * i / 39, y0 + (y1 - y0 - 1) * j / 39);
      n++; if (p.R + p.G + p.B > 48) lit++;
    }
    return 100 * lit / n;
  }

  // ---------------------------------------------------------------- UI Automation
  static CacheRequest Cache() {
    var c = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Descendants, AutomationElementMode = AutomationElementMode.Full };
    foreach (var p in new AutomationProperty[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
        AutomationElement.AutomationIdProperty, AutomationElement.ClassNameProperty, AutomationElement.IsKeyboardFocusableProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.BoundingRectangleProperty,
        AutomationElement.LocalizedControlTypeProperty, AutomationElement.IsControlElementProperty, AutomationElement.HelpTextProperty,
        AutomationElement.IsInvokePatternAvailableProperty, AutomationElement.IsTogglePatternAvailableProperty,
        AutomationElement.IsSelectionItemPatternAvailableProperty, AutomationElement.IsExpandCollapsePatternAvailableProperty,
        AutomationElement.IsValuePatternAvailableProperty, AutomationElement.ProcessIdProperty, AutomationElement.HasKeyboardFocusProperty })
      c.Add(p);
    return c;
  }

  public static AutomationElement Root(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    return AutomationElement.FromHandle(main);
  }

  // every element under FluentFin's window, with the properties above cached (one cross-process walk). A walk that
  // fails while the page changes under it (a raw COMException, ElementNotAvailableException, ...) is tried again.
  public static List<AutomationElement> All(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    Exception last = null;
    for (int attempt = 0; attempt < 4; attempt++) {
      try {
        using (Cache().Activate()) {
          var root = AutomationElement.FromHandle(main);
          return root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToList();
        }
      } catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(100 + 150 * attempt); }
    }
    throw new InvalidOperationException("UI Automation could not walk FluentFin's window: " + last.GetType().Name + ": " + last.Message, last);
  }

  // the properties a search caches for each element it finds (enough for the finders and Describe)
  static CacheRequest Small() {
    var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
    foreach (var p in new AutomationProperty[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
        AutomationElement.AutomationIdProperty, AutomationElement.ClassNameProperty, AutomationElement.IsKeyboardFocusableProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, AutomationElement.BoundingRectangleProperty,
        AutomationElement.LocalizedControlTypeProperty, AutomationElement.ProcessIdProperty, AutomationElement.HasKeyboardFocusProperty,
        AutomationElement.IsTogglePatternAvailableProperty, AutomationElement.IsInvokePatternAvailableProperty })
      c.Add(p);
    return c;
  }

  // the elements under FluentFin's window that match cond: UI Automation evaluates the condition while it walks, so this
  // is much cheaper than All() for polling. Tried again when the page changes under the search.
  public static List<AutomationElement> Where(IntPtr main, int pid, Condition cond) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    return Under(AutomationElement.FromHandle(main), cond);
  }

  // the same under one element (a part of the window that is searched often: the navigation pane)
  public static List<AutomationElement> Under(AutomationElement root, Condition cond) {
    Exception last = null;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int attempt = 0; attempt < 4; attempt++) {
      try {
        List<AutomationElement> found;
        using (Small().Activate()) found = root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().ToList();
        Searches++; SearchMs += sw.ElapsedMilliseconds; if (sw.ElapsedMilliseconds > SlowestSearchMs) SlowestSearchMs = sw.ElapsedMilliseconds;
        return found;
      } catch (Exception ex) { last = ex; Retries++; System.Threading.Thread.Sleep(100 + 150 * attempt); }
    }
    throw new InvalidOperationException("UI Automation could not search FluentFin's window: " + last.GetType().Name + ": " + last.Message, last);
  }

  // how the searches went (result.json): how many, their total and slowest time, how many had to be tried again
  public static long Searches, SearchMs, SlowestSearchMs, Retries;

  // 1.2.0: FluentFin's top bar's tab list (TopTabStrip, AutomationId TopTabs; it replaced the rail), for Open-Nav; null before
  // sign-in. An older build's navigation pane (NavigationView's PaneRoot) when there is no tab list.
  public static AutomationElement NavPane(IntPtr main, int pid) {
    return Where(main, pid, new PropertyCondition(AutomationElement.AutomationIdProperty, "TopTabs")).FirstOrDefault()
      ?? Where(main, pid, new PropertyCondition(AutomationElement.AutomationIdProperty, "PaneRoot")).FirstOrDefault();
  }

  // a tab of the tab list by name (an older build's navigation item, a list item)
  public static AutomationElement NavItem(AutomationElement pane, string name) {
    return Under(pane, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name),
      new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))).FirstOrDefault();
  }

  // scrolls the page's main vertical scroller (the largest vertically scrollable element) to percent (0-100); returns
  // where it is after, or -1 when there is nothing to scroll
  public static double ScrollPage(IntPtr main, int pid, double percent) {
    AutomationElement best = null; double area = 0;
    foreach (var e in Where(main, pid, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))) {
      try {
        var r = e.Cached.BoundingRectangle;
        var sp = (ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern);
        if (!sp.Current.VerticallyScrollable || r.IsEmpty) continue;
        if (r.Width * r.Height > area) { area = r.Width * r.Height; best = e; }
      } catch (Exception) { }
    }
    if (best == null) return -1;
    var p = (ScrollPattern)best.GetCurrentPattern(ScrollPattern.Pattern);
    p.SetScrollPercent(ScrollPattern.NoScroll, percent);
    return p.Current.VerticalScrollPercent;
  }

  static Condition AnyOf(AutomationProperty p, IEnumerable<object> values) {
    var conds = values.Select(v => (Condition)new PropertyCondition(p, v)).ToArray();
    return conds.Length == 1 ? conds[0] : new OrCondition(conds);
  }

  static string NameOf(AutomationElement e) { try { return e.Cached.Name ?? ""; } catch (Exception) { return ""; } }

  // which of these names a Text element of FluentFin's window has, on screen or scrolled out of view (one search)
  public static List<string> PresentTexts(IntPtr main, int pid, string[] names) {
    if (names == null || names.Length == 0) return new List<string>();
    var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text), AnyOf(AutomationElement.NameProperty, names));
    return Where(main, pid, cond).Select(NameOf).Where(n => n.Length > 0).Distinct().ToList();
  }

  // the first element of FluentFin with this name and class (e.g. a library tile: ItemContainer), visible or not
  public static AutomationElement FindByNameAndClass(IntPtr main, int pid, string name, string className) {
    return Where(main, pid, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, name),
      new PropertyCondition(AutomationElement.ClassNameProperty, className))).FirstOrDefault();
  }

  // a media card (BaseItemDtoCard: one UIA Button named "<title>, <year>, ..." that opens the item on Invoke), on screen or
  // scrolled out of view; null when none has that title
  public static AutomationElement FindCard(IntPtr main, int pid, string title) {
    return Where(main, pid, new PropertyCondition(AutomationElement.ClassNameProperty, "BaseItemDtoCard"))
      .FirstOrDefault(e => { var n = NameOf(e); return n == title || n.StartsWith(title + ", ", StringComparison.Ordinal); });
  }

  // the first Button whose name starts with prefix and contains part (null or empty: anything), visible or not
  public static AutomationElement FindButtonLike(IntPtr main, int pid, string prefix, string part) {
    return Where(main, pid, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
      .FirstOrDefault(e => { var n = NameOf(e); return n.StartsWith(prefix, StringComparison.Ordinal) && (string.IsNullOrEmpty(part) || n.IndexOf(part, StringComparison.Ordinal) >= 0); });
  }

  // how many elements of each class the window has (one search): media cards, loading placeholders
  public static Dictionary<string, int> CountByClass(IntPtr main, int pid, string[] classes) {
    var counts = classes.ToDictionary(x => x, x => 0);
    foreach (var e in Where(main, pid, AnyOf(AutomationElement.ClassNameProperty, classes))) {
      try { var n = e.Cached.ClassName; if (counts.ContainsKey(n)) counts[n]++; } catch (Exception) { }
    }
    return counts;
  }

  // the window's UI Automation tree (control view) with indentation, one line per element: type 'name' #id [class]
  // x,y wxh flags. One cross-process call (a Subtree cache); for "what is on this page" and failed steps.
  public static string DumpTree(IntPtr main, int pid) {
    if (ProcessOf(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    var c = Cache(); c.TreeScope = TreeScope.Subtree;
    Exception last = null;
    for (int attempt = 0; attempt < 4; attempt++) {
      try {
        AutomationElement root;
        using (c.Activate()) root = AutomationElement.FromHandle(main);
        var sb = new StringBuilder();
        DumpNode(root, 0, sb);
        return sb.ToString();
      } catch (Exception ex) { last = ex; System.Threading.Thread.Sleep(100 + 150 * attempt); }
    }
    return "(UI Automation could not walk the window: " + last.GetType().Name + ": " + last.Message + ")\n";
  }

  static void DumpNode(AutomationElement e, int depth, StringBuilder sb) {
    try {
      var c = e.Cached; var r = c.BoundingRectangle;
      var flags = new List<string>();
      if (c.IsKeyboardFocusable) flags.Add("focusable");
      if (c.HasKeyboardFocus) flags.Add("FOCUSED");
      if (!c.IsEnabled) flags.Add("disabled");
      if (c.IsOffscreen) flags.Add("offscreen");
      foreach (var p in new[] { Tuple.Create(AutomationElement.IsInvokePatternAvailableProperty, "invoke"), Tuple.Create(AutomationElement.IsTogglePatternAvailableProperty, "toggle"),
          Tuple.Create(AutomationElement.IsSelectionItemPatternAvailableProperty, "select"), Tuple.Create(AutomationElement.IsExpandCollapsePatternAvailableProperty, "expand"),
          Tuple.Create(AutomationElement.IsValuePatternAvailableProperty, "value") })
        try { if ((bool)e.GetCachedPropertyValue(p.Item1)) flags.Add(p.Item2); } catch (Exception) { }
      sb.Append(' ', depth * 2).AppendFormat("{0} {1}{2}{3} {4} {5}{6}{7}\n",
        c.ControlType.ProgrammaticName.Replace("ControlType.", ""), Esc(c.Name),
        string.IsNullOrEmpty(c.AutomationId) ? "" : " #" + c.AutomationId,
        string.IsNullOrEmpty(c.ClassName) ? "" : " [" + c.ClassName + "]",
        r.IsEmpty ? "-" : string.Format("{0},{1} {2}x{3}", (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height),
        string.Join(" ", flags), string.IsNullOrEmpty(c.HelpText) ? "" : " help=" + Esc(c.HelpText), "");
    } catch (Exception ex) { sb.Append(' ', depth * 2).Append("(element gone: " + ex.GetType().Name + ")\n"); return; }
    AutomationElementCollection kids;
    try { kids = e.CachedChildren; } catch (Exception) { return; }
    foreach (AutomationElement k in kids) DumpNode(k, depth + 1, sb);
  }

  public static AutomationElement Find(IntPtr main, int pid, string name, string id, string controlType, bool contains) {
    return Find(main, pid, name, id, controlType, contains, false);
  }

  public static AutomationElement Find(IntPtr main, int pid, string name, string id, string controlType, bool contains, bool offscreenToo) {
    // the exact filters go into the search's condition; a name that only has to be contained is checked here
    var conds = new List<Condition> { new PropertyCondition(AutomationElement.ProcessIdProperty, pid) };
    if (!string.IsNullOrEmpty(id)) conds.Add(new PropertyCondition(AutomationElement.AutomationIdProperty, id));
    if (!string.IsNullOrEmpty(name) && !contains) conds.Add(new PropertyCondition(AutomationElement.NameProperty, name));
    if (!string.IsNullOrEmpty(controlType)) {
      ControlType ct = null;
      foreach (var f in typeof(ControlType).GetFields()) if (f.Name == controlType && f.FieldType == typeof(ControlType)) ct = (ControlType)f.GetValue(null);
      if (ct == null) throw new ArgumentException("no control type " + controlType);
      conds.Add(new PropertyCondition(AutomationElement.ControlTypeProperty, ct));
    }
    Condition cond = conds.Count == 1 ? conds[0] : new AndCondition(conds.ToArray());
    foreach (var e in Where(main, pid, cond)) {
      try {
        var c = e.Cached;
        if (!string.IsNullOrEmpty(name) && contains && (c.Name ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
        if (c.IsOffscreen && !offscreenToo) continue;
        return e;
      } catch (Exception) { }
    }
    return null;
  }

  // the Button (or other invokable element) whose own name, or a Text directly inside it, is text
  public static AutomationElement FindButton(IntPtr main, int pid, string text) {
    var walker = TreeWalker.ControlViewWalker;
    var hits = Where(main, pid, new AndCondition(new PropertyCondition(AutomationElement.NameProperty, text),
      new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text))));
    foreach (var e in hits) {
      try {
        var c = e.Cached;
        if (c.IsOffscreen) continue;
        if (c.ControlType == ControlType.Button) return e;
        var parent = walker.GetParent(e);
        if (parent != null && (bool)parent.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)) return parent;
      } catch (Exception) { }
    }
    return null;
  }

  public static System.Windows.Rect RectOf(AutomationElement e) { return e.Current.BoundingRectangle; }

  // the nearest ancestor (up to 4 levels) that can be invoked or selected: the item an ItemsView title sits in
  public static AutomationElement InvokableAncestor(AutomationElement e) {
    var walker = TreeWalker.ControlViewWalker;
    var p = e;
    for (int i = 0; i < 4 && p != null; i++) {
      p = walker.GetParent(p);
      if (p == null) break;
      if ((bool)p.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)
        || (bool)p.GetCurrentPropertyValue(AutomationElement.IsSelectionItemPatternAvailableProperty)) return p;
    }
    return null;
  }

  // one line per element (type | name | automation id | class | rect | focusable), for debugging a failed step
  public static string Dump(IntPtr main, int pid) {
    var sb = new StringBuilder();
    foreach (var e in All(main, pid)) {
      try {
        var c = e.Cached; var r = c.BoundingRectangle;
        sb.AppendFormat("{0} | {1} | {2} | {3} | {4} | {5}{6}\n", c.ControlType.ProgrammaticName.Replace("ControlType.", ""), c.Name, c.AutomationId, c.ClassName,
          r.IsEmpty ? "-" : string.Format("{0},{1} {2}x{3}", (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height), c.IsKeyboardFocusable ? "focusable" : "", c.IsOffscreen ? " offscreen" : "");
      } catch (Exception) { }
    }
    return sb.ToString();
  }

  // the visible element of a control type and class (e.g. Edit / PasswordBox)
  public static AutomationElement FindByClass(IntPtr main, int pid, string controlType, string className) {
    foreach (var e in Where(main, pid, new PropertyCondition(AutomationElement.ClassNameProperty, className))) {
      try {
        var c = e.Cached;
        if (!c.IsOffscreen && c.ControlType.ProgrammaticName == "ControlType." + controlType) return e;
      } catch (Exception) { }
    }
    return null;
  }

  // the window's caption buttons (Minimize, Maximize/Restore, Close): Windows' own, not FluentFin's XAML
  static bool IsCaption(AutomationElement.AutomationElementInformation c) {
    return c.ClassName == "" && (c.AutomationId == "Minimize" || c.AutomationId == "Maximize" || c.AutomationId == "Restore" || c.AutomationId == "Close");
  }

  // ---------------------------------------------------------------- accessibility scan
  static readonly HashSet<string> Interactive = new HashSet<string> { "ControlType.Button", "ControlType.CheckBox", "ControlType.ComboBox",
    "ControlType.Edit", "ControlType.Hyperlink", "ControlType.ListItem", "ControlType.MenuItem", "ControlType.RadioButton",
    "ControlType.Slider", "ControlType.SplitButton", "ControlType.TabItem", "ControlType.TreeItem", "ControlType.DataItem",
    "ControlType.Spinner", "ControlType.ScrollBar", "ControlType.MenuBar", "ControlType.Menu" };

  static string Esc(string s) {
    if (s == null) return "null";
    var b = new StringBuilder("\"");
    foreach (var ch in s) { if (ch == '"' || ch == '\\') b.Append('\\').Append(ch); else if (ch < 32) b.AppendFormat("\\u{0:x4}", (int)ch); else b.Append(ch); }
    return b.Append('"').ToString();
  }

  static string Describe(AutomationElement.AutomationElementInformation c) {
    var r = c.BoundingRectangle;
    return string.Format("{{\"type\":{0},\"localizedType\":{1},\"name\":{2},\"automationId\":{3},\"class\":{4},\"focusable\":{5},\"enabled\":{6},\"rect\":{7}}}",
      Esc(c.ControlType.ProgrammaticName.Replace("ControlType.", "")), Esc(c.LocalizedControlType), Esc(c.Name), Esc(c.AutomationId), Esc(c.ClassName),
      c.IsKeyboardFocusable ? "true" : "false", c.IsEnabled ? "true" : "false",
      r.IsEmpty ? "null" : string.Format("[{0},{1},{2},{3}]", (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height));
  }

  // JSON: the page's elements and its findings (see tools\README.md: the accessibility scan)
  public static string Scan(IntPtr main, int pid, string page) {
    var all = All(main, pid);
    var unnamed = new List<string>(); var notFocusable = new List<string>(); var noType = new List<string>(); var images = new List<string>();
    var unreachableImages = new List<string>();
    int interactive = 0, visible = 0, headings = 0;
    var walker = TreeWalker.ControlViewWalker;
    foreach (var e in all) {
      AutomationElement.AutomationElementInformation c;
      string type; bool clickable;
      try {
        c = e.Cached;
        if ((int)c.ProcessId != pid || c.IsOffscreen || c.BoundingRectangle.IsEmpty || c.BoundingRectangle.Width < 1 || IsCaption(c)) continue;
        type = c.ControlType.ProgrammaticName;
        clickable = Interactive.Contains(type) || (bool)e.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)
          || (bool)e.GetCachedPropertyValue(AutomationElement.IsTogglePatternAvailableProperty)
          || (bool)e.GetCachedPropertyValue(AutomationElement.IsSelectionItemPatternAvailableProperty);
      } catch (Exception) { continue; }   // gone or unreadable while the page changed: not counted
      visible++;
      // HeadingLevel: .NET Framework's client returns its AutomationHeadingLevel enum (None = 0, Level1 = 1, ...), a raw UIA
      // value is 80050 + the level; counted as a heading either way (an int-only check never counted one)
      try { var hp = AutomationProperty.LookupById(30173); if (hp != null) { var level = e.GetCurrentPropertyValue(hp); int lv = (level is Enum || level is int) ? Convert.ToInt32(level) : 0; if (lv > 80050) lv -= 80050; if (lv > 0) headings++; } } catch { }
      if (c.IsControlElement && (type == "ControlType.Custom" || string.IsNullOrEmpty(c.LocalizedControlType))) noType.Add(Describe(c));
      if (!clickable) {
        // a poster or still with no focusable, invokable element around it: a card that opens on a pointer tap only
        if (type == "ControlType.Image" && c.BoundingRectangle.Width >= 80 && c.BoundingRectangle.Height >= 80) {
          images.Add(Describe(c));
          bool reachable = false;
          try {
            for (var p = walker.GetParent(e); p != null && !reachable; p = walker.GetParent(p)) {
              var pi = p.Current;
              if (pi.ControlType == ControlType.Window) break;
              if (pi.IsKeyboardFocusable && ((bool)p.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)
                || (bool)p.GetCurrentPropertyValue(AutomationElement.IsSelectionItemPatternAvailableProperty))) reachable = true;
            }
          } catch (Exception) { }
          if (!reachable) unreachableImages.Add(Describe(c));
        }
        continue;
      }
      interactive++;
      if (string.IsNullOrWhiteSpace(c.Name)) unnamed.Add(Describe(c));
      if (!c.IsKeyboardFocusable && c.IsEnabled) notFocusable.Add(Describe(c));
    }
    var sb = new StringBuilder();
    sb.AppendFormat("{{\"page\":{0},\"elements\":{1},\"visible\":{2},\"interactive\":{3},\"headings\":{4},", Esc(page), all.Count, visible, interactive, headings);
    sb.AppendFormat("\"unnamedInteractive\":[{0}],", string.Join(",", unnamed));
    sb.AppendFormat("\"interactiveNotKeyboardFocusable\":[{0}],", string.Join(",", notFocusable));
    sb.AppendFormat("\"withoutControlType\":[{0}],", string.Join(",", noType));
    sb.AppendFormat("\"imagesNotReachableByKeyboard\":[{0}]}}", string.Join(",", unreachableImages));
    return sb.ToString();
  }

  // FluentFin's focused element as its own UI reports it (HasKeyboardFocus): works on a desktop that is not the input
  // desktop, where the system's focus tracking (AutomationElement.FocusedElement) sees nothing. Null when none has it.
  public static string Focused(IntPtr main, int pid) {
    foreach (var e in Where(main, pid, new PropertyCondition(AutomationElement.HasKeyboardFocusProperty, true))) {
      try {
        var c = e.Cached;
        if ((int)c.ProcessId == pid && c.HasKeyboardFocus && c.ControlType != ControlType.Window) return Describe(c);
      } catch (Exception) { }
    }
    return null;
  }

  // the visible keyboard-focusable elements in tree order (what Tab visits when no TabIndex reorders it)
  public static List<string> FocusableInTreeOrder(IntPtr main, int pid) {
    var l = new List<string>();
    foreach (var e in All(main, pid)) {
      try {
        var c = e.Cached;
        if ((int)c.ProcessId == pid && c.IsKeyboardFocusable && !c.IsOffscreen && c.IsEnabled && !IsCaption(c) && c.ControlType != ControlType.Window) l.Add(Describe(c));
      } catch (Exception) { }
    }
    return l;
  }

  // the first visible keyboard-focusable element of the page content (not the title bar or the navigation pane)
  public static AutomationElement FirstContentFocusable(IntPtr main, int pid, double minX, double minY) {
    foreach (var e in All(main, pid)) {
      try {
        var c = e.Cached; var r = c.BoundingRectangle;
        if ((int)c.ProcessId == pid && c.IsKeyboardFocusable && !c.IsOffscreen && c.IsEnabled && !IsCaption(c) && !r.IsEmpty && r.X >= minX && r.Y >= minY && c.ControlType != ControlType.Window) return e;
      } catch (Exception) { }
    }
    return null;
  }

  public static bool SetFocus(AutomationElement e) { try { e.SetFocus(); return true; } catch (Exception) { return false; } }
}
"@
}
[OffscreenUi]::SetProcessDPIAware() | Out-Null

# --- UIA conveniences ------------------------------------------------------------------------------------------------
function Wait-Until([scriptblock]$Test, [double]$Seconds = 20, [int]$PollMs = 100) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $r = & $Test
        if ($r) { return $r }
        Start-Sleep -Milliseconds $PollMs
    }
    $null
}

function Invoke-Element($Element) {
    $p = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return 'invoke' }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return 'select' }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return 'toggle' }
    throw "element '$($Element.Current.Name)' has no invoke, select or toggle pattern"
}

function Set-ElementValue($Element, [string]$Value) {
    $p = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "element has no value pattern" }
    $p.SetValue($Value)
}

# Epoch seconds now (the fake server's request log uses the same clock).
function Get-Epoch { ([DateTimeOffset]::UtcNow).ToUnixTimeMilliseconds() / 1000.0 }
