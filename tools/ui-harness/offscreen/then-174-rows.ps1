# Nixie 1.7.4, the basics release (the rows stream; a part of the basics gate): every horizontal row reaches its last item
# with every input, and the last item is then in view. The friend's report: on a maximised 2560 x 1392 window a 13-episode
# season's page (Cinematic) showed E1 to E6 with E7 cut off, no arrows, and no way to scroll the row.
# Run per theme (rows-gate.ps1 runs the four and writes the grid):
#   $env:FAKESERVER_BIG_SERIES='24x13'; $env:FAKESERVER_COLLECTIONS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   [$env:RG_SIZES='max,1920x1080,1280x720'] [$env:RG_PAGES='season,cast,series,home,collections,movie,library,search,topbar']
#   [$env:RG_DEPTH='auto'|'full'|'quick'] [$env:RG_ROWS_PER_PAGE='0'] [$env:SG_SHOT_TAG='...']
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-174-rows.ps1
# Sizes: "max" is the window maximised (2560 x 1392 on a 2560 x 1440 monitor with the taskbar, the friend's), WxH the window's
# outer size. Pages: the big series' Season 1 (13 episodes, 16 people with long names), the series page, Home, Collections, the
# showcase film, the Movies library (its presets), search results ("the"); "cast" checks the season page's names; "topbar"
# the bar's lit tab on an item page reached from Home. Every list of UI Automation whose items lie on one line is a row.
# Nixie 1.7.5 (the basics gate, g3): with FAKESERVER_REAL_SHAPE=1, "season24" (a season of 24 episodes with long names) and
# "film" (a long title, a cast of 48); RG_MAX_MIN='2560x1392' fails a "max" smaller than that (the big monitor gone).
# For each row that runs past its end, each input from the row's start:
#   keys      End, then Home and Right to the last (the keyboard on the row's first item, as Tab leaves it)
#   tab       Tab out of the row and Shift+Tab back: the row's remembered (last) item, in view
#   dpad      a gamepad's D-pad right (VK_GAMEPAD_DPAD_RIGHT posted; WinUI takes it as GamepadDPadRight)
#   shoulder  the gamepad's right shoulder pages the row (VK_GAMEPAD_RIGHT_SHOULDER)
#   tilt      WM_MOUSEHWHEEL: a tilt wheel, and a touchpad's sideways swipe as a legacy driver sends it (a precision touchpad
#             pans the row's scroll viewer through DirectManipulation, which nothing posted reaches: a hands-on check)
#   shift     Shift+wheel: WM_MOUSEWHEEL with MK_SHIFT inside a posted Shift key; "n/t" when WinUI took no Shift from it
#   wheel     the plain wheel: the page's while the page can move, then the row's (RowPaging.TargetOf); a row the page
#             scrolls out of view under the pointer is "page" (the wheel went where the rule says)
#   drag      the dev build's pointer hook drags the row (HarnessPointer: the row's own drag steps), right to left, until its end
#   buttons   the hook's pointer over the row: Next shows at the row's right end, Back hidden; clicked until it goes: the last
#             item in view, Back shown; the pointer off the row: neither
# A row that fits shows its last item at rest (and no arrow on hover). The cast row's names: each within its photo's width
# (104), apart from its neighbours, the card's name for Narrator the whole name and role. Rows go to
# <Out>\rows174\rows174.json (theme, size, page, row, input, verdict, ok, detail) and each is a Check; shots per page and size.
# Dot-sourced by drive.ps1: every name here starts with rg / Rg- ($out is its -Out).

$rgTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$rgTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { $rgTheme.ToLowerInvariant() }
$rgSizes = @($(if ($env:RG_SIZES) { $env:RG_SIZES } else { 'max,1920x1080,1280x720' }) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$rgPages = @($(if ($env:RG_PAGES) { $env:RG_PAGES } else { 'season,cast,series,home,collections,movie,library,search,topbar' }) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
# full: every input; quick: no Tab, shoulder or Shift+wheel; auto (the default): full at the first size, quick at the others
$rgDepthAsked = if ($env:RG_DEPTH) { $env:RG_DEPTH } else { 'auto' }
$rgDepth = 'full'
$rgPerPage = if ($env:RG_ROWS_PER_PAGE) { [int]$env:RG_ROWS_PER_PAGE } else { 0 }
$rgDir = Join-Path $Out 'rows174'
New-Item -ItemType Directory -Force $rgDir | Out-Null
$rgResults = New-Object System.Collections.Generic.List[object]
$rgBs = $fixture.big_series
$rgSize = ''
$rgPage = ''
$rgArrowShots = @{}

if (-not ('RgUi' -as [type])) {
    Add-Type -ReferencedAssemblies UIAutomationClient, UIAutomationTypes, WindowsBase, System.Core @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

public static class RgUi {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  static void Own(IntPtr main, int pid) { if (Pid(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window"); }

  // the dev build's pointer hook (HarnessPointer): kind 1 click, 3 drag (to toX on the same line), 4 move; x, y on the screen
  public static bool Hook(IntPtr main, int pid, int kind, int x, int y, int toX) {
    Own(main, pid);
    uint msg = RegisterWindowMessage("NixieHarnessPointer");
    long w = (long)(uint)(kind & 0xFFFF) | ((long)(uint)(toX & 0xFFFF) << 16);
    return PostMessage(main, msg, (IntPtr)w, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }

  public static void Maximize(IntPtr main, int pid) { Own(main, pid); ShowWindow(main, 3); }
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  // the window at the screen's origin, w x h (SWP_NOZORDER | SWP_NOACTIVATE): a posted wheel's point (screen coordinates)
  // lands where it does in a maximised window
  public static void Place(IntPtr main, int pid, int w, int h) { Own(main, pid); SetWindowPos(main, IntPtr.Zero, 0, 0, w, h, 0x0004 | 0x0010); }
  public static void Restore(IntPtr main, int pid) { Own(main, pid); ShowWindow(main, 9); }

  // Shift+wheel: a posted Shift key down, WM_MOUSEWHEEL with MK_SHIFT, Shift up (to the window WinUI reads input from)
  public static void ShiftWheel(IntPtr target, int x, int y, int delta) {
    uint scan = MapVirtualKey(0x10, 0);
    var c = new POINT { X = x, Y = y }; ScreenToClient(target, ref c);
    PostMessage(target, 0x0200, IntPtr.Zero, (IntPtr)(long)((uint)(c.Y & 0xFFFF) << 16 | (uint)(c.X & 0xFFFF)));
    PostMessage(target, 0x0100, (IntPtr)0x10, (IntPtr)(1 | (scan << 16)));
    System.Threading.Thread.Sleep(30);
    long w = ((long)(short)delta << 16) | 4L;
    PostMessage(target, 0x020A, (IntPtr)(uint)(w & 0xFFFFFFFF), (IntPtr)(long)((uint)(y & 0xFFFF) << 16 | (uint)(x & 0xFFFF)));
    System.Threading.Thread.Sleep(30);
    PostMessage(target, 0x0101, (IntPtr)0x10, (IntPtr)(1 | (scan << 16) | (1u << 30) | (1u << 31)));
  }

  static CacheRequest Req() {
    var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
    foreach (var p in new AutomationProperty[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty, AutomationElement.ClassNameProperty,
        AutomationElement.AutomationIdProperty, AutomationElement.BoundingRectangleProperty, AutomationElement.IsOffscreenProperty,
        AutomationElement.HasKeyboardFocusProperty, AutomationElement.IsKeyboardFocusableProperty, AutomationElementIdentifiers.PositionInSetProperty,
        AutomationElementIdentifiers.SizeOfSetProperty }) c.Add(p);
    return c;
  }
  static string R(System.Windows.Rect r) { return (r.IsEmpty || double.IsInfinity(r.X) || double.IsInfinity(r.Width)) ? "-" : string.Format("{0:0},{1:0},{2:0},{3:0}", r.X, r.Y, r.Width, r.Height); }
  static string Clean(string s) { return (s ?? "").Replace("\u001f", " ").Replace("\u001e", " ").Replace("\u001d", " "); }

  // every List of the window with its children: "name US class US rect US kid RS kid ..." where kid is
  // "name GS pos GS size GS rect GS offscreen GS focused GS focusable GS class"
  public static List<string> Lists(IntPtr main, int pid, string name) {
    Own(main, pid);
    var l = new List<string>();
    using (Req().Activate()) {
      var root = AutomationElement.FromHandle(main);
      // lists, tab lists (a theme's season tabs), and named scroll areas of buttons (Drive-in's season tubes, the presets)
      Condition cond = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
        new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane), new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)));
      if (!string.IsNullOrEmpty(name)) cond = new AndCondition(cond, new PropertyCondition(AutomationElement.NameProperty, name));
      foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond)) {
        try {
          // the top bar's tabs fold into a menu: not a row
          if (e.Cached.AutomationId == "TopTabs") continue;
          var pane = e.Cached.ControlType == ControlType.Pane;
          if (pane && (string.IsNullOrEmpty(e.Cached.Name) || e.FindFirst(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab))) != null)) continue;
          // a tab list's tabs may sit under its scroll area: its TabItems wherever they are; a scroll area's buttons
          var kidsFound = e.Cached.ControlType == ControlType.Tab
            ? e.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            : pane ? e.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            : e.FindAll(TreeScope.Children, Condition.TrueCondition);
          var all = kidsFound.Cast<AutomationElement>().ToList();
          var kids = all.Select((k, i) => {
            var c = k.Cached;
            object pos = k.GetCachedPropertyValue(AutomationElementIdentifiers.PositionInSetProperty, true), size = k.GetCachedPropertyValue(AutomationElementIdentifiers.SizeOfSetProperty, true);
            // a scroll area's buttons have no place in a set: their order is theirs
            if (pane && !(pos is int && (int)pos > 0)) { pos = i + 1; size = all.Count; }
            return string.Join("\u001d", new[] { Clean(c.Name), (pos is int ? ((int)pos).ToString() : "-1"), (size is int ? ((int)size).ToString() : "-1"), R(c.BoundingRectangle),
              c.IsOffscreen ? "1" : "0", c.HasKeyboardFocus ? "1" : "0", c.IsKeyboardFocusable ? "1" : "0", Clean(c.ClassName) });
          });
          l.Add(string.Join("\u001f", new[] { Clean(e.Cached.Name), Clean(e.Cached.ClassName), R(e.Cached.BoundingRectangle), string.Join("\u001e", kids) }));
        } catch (Exception) { }
      }
    }
    return l;
  }

  // the List named name whose rect is nearest (x, y) (null: the first); its child at position pos (1-based; by order when the
  // list gives none)
  public static AutomationElement ListNamed(IntPtr main, int pid, string name) {
    Own(main, pid);
    var root = AutomationElement.FromHandle(main);
    var kind = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab),
      new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
    return root.FindFirst(TreeScope.Descendants, new AndCondition(kind, new PropertyCondition(AutomationElement.NameProperty, name)));
  }
  public static AutomationElement Child(AutomationElement list, int pos) {
    // (a tab list's tabs may sit under its scroll area: its TabItems wherever they are)
    var type = list.Current.ControlType;
    var kids = (type == ControlType.Tab
      ? list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
      : type == ControlType.Pane ? list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
      : list.FindAll(TreeScope.Children, Condition.TrueCondition)).Cast<AutomationElement>().ToList();
    if (type == ControlType.Pane) return pos >= 1 && pos <= kids.Count ? kids[pos - 1] : null;
    foreach (var k in kids) { var p = k.GetCurrentPropertyValue(AutomationElementIdentifiers.PositionInSetProperty, true); if (p is int && (int)p == pos) return k; }
    return pos >= 1 && pos <= kids.Count ? kids[pos - 1] : null;
  }

  // the horizontally scrollable element whose rect holds (x, y): "rect US h% US viewsize%" (null: none)
  public static AutomationElement ScrollerAt(IntPtr main, int pid, double x, double y) {
    Own(main, pid);
    var root = AutomationElement.FromHandle(main);
    AutomationElement best = null; double area = double.MaxValue;
    foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))) {
      try {
        var r = e.Current.BoundingRectangle; if (r.IsEmpty) continue;
        var sp = (ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern);
        if (!sp.Current.HorizontallyScrollable) continue;
        if (x >= r.Left - 2 && x <= r.Right + 2 && y >= r.Top - 2 && y <= r.Bottom + 2 && r.Width * r.Height < area) { area = r.Width * r.Height; best = e; }
      } catch (Exception) { }
    }
    return best;
  }
  public static double H(AutomationElement scroller) { try { return ((ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern)).Current.HorizontalScrollPercent; } catch (Exception) { return -2; } }
  public static bool SetH(AutomationElement scroller, double percent) { try { ((ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern)).SetScrollPercent(percent, ScrollPattern.NoScroll); return true; } catch (Exception) { return false; } }

  // what of the page shows: the largest vertically scrollable element's rect ("x,y,w,h"; "-" for none)
  public static string PageRect(IntPtr main, int pid) {
    Own(main, pid);
    var root = AutomationElement.FromHandle(main);
    System.Windows.Rect best = System.Windows.Rect.Empty; double area = 0;
    foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))) {
      try {
        var r = e.Current.BoundingRectangle; var sp = (ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern);
        if (!sp.Current.VerticallyScrollable || r.IsEmpty) continue;
        if (r.Width * r.Height > area) { area = r.Width * r.Height; best = r; }
      } catch (Exception) { }
    }
    return R(best);
  }

  // the page's vertical scroller (the largest vertically scrollable element): its vertical percent, -1 for none
  public static double PageV(IntPtr main, int pid) {
    Own(main, pid);
    var root = AutomationElement.FromHandle(main);
    AutomationElement best = null; double area = 0;
    foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))) {
      try {
        var r = e.Current.BoundingRectangle; var sp = (ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern);
        if (!sp.Current.VerticallyScrollable || r.IsEmpty) continue;
        if (r.Width * r.Height > area) { area = r.Width * r.Height; best = e; }
      } catch (Exception) { }
    }
    if (best == null) return -1;
    try { return ((ScrollPattern)best.GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticalScrollPercent; } catch (Exception) { return -1; }
  }

  // the visible buttons of the window whose name is one of names: "name US rect"
  public static List<string> Buttons(IntPtr main, int pid, string[] names) {
    Own(main, pid);
    var l = new List<string>();
    using (Req().Activate()) {
      var root = AutomationElement.FromHandle(main);
      var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        names.Length == 1 ? (Condition)new PropertyCondition(AutomationElement.NameProperty, names[0]) : new OrCondition(names.Select(n => (Condition)new PropertyCondition(AutomationElement.NameProperty, n)).ToArray()));
      foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond)) {
        var c = e.Cached; if (c.IsOffscreen || c.BoundingRectangle.IsEmpty) continue;
        bool enabled = true; try { enabled = (bool)e.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty); } catch (Exception) { }
        l.Add(c.Name + "\u001f" + R(c.BoundingRectangle) + "\u001f" + (enabled ? "1" : "0"));
      }
    }
    return l;
  }

  // the raw-view texts under a list's child (a cast card's name and role): "name US rect"
  public static List<string> Texts(AutomationElement item) {
    var l = new List<string>();
    var walker = TreeWalker.RawViewWalker;
    var stack = new Stack<AutomationElement>(); stack.Push(item);
    while (stack.Count > 0) {
      var e = stack.Pop();
      for (var c = walker.GetFirstChild(e); c != null; c = walker.GetNextSibling(c)) {
        try { if (c.Current.ControlType == ControlType.Text) l.Add(c.Current.Name + "\u001f" + R(c.Current.BoundingRectangle)); } catch (Exception) { }
        stack.Push(c);
      }
    }
    return l;
  }
}
"@
}

# ---- reading the window ---------------------------------------------------------------------------------------------------
function Rg-Rect([string]$Text) { if (-not $Text -or $Text -eq '-') { $null } else { $p = $Text -split ','; [pscustomobject]@{ X = [double]$p[0]; Y = [double]$p[1]; W = [double]$p[2]; H = [double]$p[3]; R = [double]$p[0] + [double]$p[2]; B = [double]$p[1] + [double]$p[3] } } }
function Rg-Window { [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id)) }
# every list of the window as objects; Horizontal when two or more of its drawn items share a line
function Rg-Lists([string]$Named = '') {
    $us = [char]0x1f; $rs = [char]0x1e; $gs = [char]0x1d
    $rgViewNow = Rg-View
    foreach ($line in [RgUi]::Lists($script:hwnd, $script:ui.Id, $Named)) {
        $p = $line -split $us
        $items = @(if ($p.Count -gt 3 -and $p[3]) { $p[3] -split $rs | ForEach-Object { $k = $_ -split $gs; [pscustomobject]@{ Name = $k[0]; Pos = [int]$k[1]; Size = [int]$k[2]; Rect = (Rg-Rect $k[3]); Off = ($k[4] -eq '1'); Focused = ($k[5] -eq '1'); Focusable = ($k[6] -eq '1'); Class = $k[7] } } })
        # items with a place in a set are the row's (a label or a sign beside them is not one of its items)
        $placed = @($items | Where-Object { $_.Pos -ge 1 })
        if ($placed.Count -and $placed.Count -lt $items.Count) { $items = $placed }
        $drawn = @($items | Where-Object { $_.Rect -and -not $_.Off })
        $count = if ($placed.Count) { [int](@($placed | ForEach-Object { [Math]::Max($_.Size, $_.Pos) }) | Measure-Object -Maximum).Maximum } else { $items.Count }
        # on one line: every drawn item's middle within a quarter of an item's height of the first's (a focused still grows 1.1x about its middle)
        $oneLine = $drawn.Count -ge 2 -and @($drawn | Where-Object { [Math]::Abs(($_.Rect.Y + $_.Rect.H / 2) - ($drawn[0].Rect.Y + $drawn[0].Rect.H / 2)) -gt $drawn[0].Rect.H / 4 }).Count -eq 0
        if (-not $oneLine -and $drawn.Count -ge 2 -and -not @($items | Where-Object { $_.Pos -ge 1 }).Count) {
            # a list of loose texts in columns, no item with a place in a set (1.7.3's Cast & crew: a name, its role under it):
            # its first line is the row
            $line = @($drawn | Where-Object { [Math]::Abs(($_.Rect.Y + $_.Rect.H / 2) - ($drawn[0].Rect.Y + $drawn[0].Rect.H / 2)) -le $drawn[0].Rect.H / 4 })
            if ($line.Count -ge 2) { $items = @($line) + @($items | Where-Object { -not $_.Rect -or $_.Off }); $count = $items.Count; $oneLine = $true }
        }
        [pscustomobject]@{ Name = $p[0]; Class = $p[1]; Rect = (Rg-Rect $p[2]); Items = $items; Count = [int]$count; Horizontal = $oneLine; View = $rgViewNow }
    }
}
function Rg-Row([string]$Name) { @(Rg-Lists $Name | Where-Object { $_.Name -eq $Name -and $_.Horizontal }) | Select-Object -First 1 }
# the row's last item (its position is the count), else the last of its items
# (a list whose items have places but whose last is not in UI Automation's tree yet has its last out of view: never the
# last item UI Automation happens to have)
function Rg-Last($Row) {
    if (-not $Row) { return $null }
    $l = @($Row.Items | Where-Object { $_.Pos -eq $Row.Count }) | Select-Object -First 1
    if ($l) { return $l }
    if (@($Row.Items | Where-Object { $_.Pos -ge 1 }).Count) { return $null }
    $Row.Items[-1]
}
# an item is in view when it is drawn at its whole size (UI Automation clips a rect to what shows) inside the window
function Rg-InView($Row, $Item) {
    if (-not $Item -or -not $Item.Rect -or $Item.Off) { return $false }
    # an item's whole width: the drawn items' middle width (a focused still is 1.1x, a clipped one narrower)
    $widths = @($Row.Items | Where-Object { $_.Rect -and -not $_.Off } | ForEach-Object { $_.Rect.W } | Sort-Object)
    $full = $widths[[int][Math]::Floor($widths.Count / 2)]
    $w = Rg-Window
    # (a list whose children are no items, with no place in a set, e.g. 1.7.3's Cast & crew of loose texts: drawn is enough)
    if ($Item.Pos -lt 1) { $full = 0 }
    # (items of different widths, a library's presets or genre chips: no width to compare with; an item cut by the row's
    # edge ends on it while the row can still go that way)
    # (widths compared among the items clear of the row's edges: one cut by an edge is narrower without being different)
    $clear = @($Row.Items | Where-Object { $_.Rect -and -not $_.Off -and $Row.Rect -and $_.Rect.X -gt $Row.Rect.X + 1 -and $_.Rect.R -lt $Row.Rect.R - 1 } | ForEach-Object { $_.Rect.W } | Sort-Object)
    if ($Item.Pos -ge 1 -and $clear.Count -ge 2 -and $clear[-1] -gt $clear[0] * 1.25 -and $Row.Rect) {
        $full = 0
        $cutLeft = $Item.Rect.X -le $Row.Rect.X + 1
        $cutRight = $Item.Rect.R -ge $Row.Rect.R - 1
        if ($cutLeft -or $cutRight) {
            $h = Rg-H $Row
            if ($h -ge 0 -and (($cutRight -and $h -lt 99.5) -or ($cutLeft -and $h -gt 0.5))) { return $false }
        }
    }
    # and not cut by the page's top or bottom (an item cut there ends on the edge of what shows; items differ in height)
    $view = if ($Row.PSObject.Properties['View'] -and $Row.View) { $Row.View } else { [pscustomobject]@{ Y = $w.Top; B = $w.Bottom + 2 } }
    # (a row outside the page's scroll area, above it: a library's A to Z or its sections, is in the window's)
    if ($Row.Rect -and $Row.Rect.Y -lt $view.Y) { $view = [pscustomobject]@{ Y = $w.Top; B = $w.Bottom + 2 } }
    return $Item.Rect.W -ge $full - 2 -and $Item.Rect.X -ge $w.Left - 1 -and $Item.Rect.R -le $w.Right + 1 -and $Item.Rect.Y -ge $view.Y + 40 -and $Item.Rect.B -le $view.B - 1
}
function Rg-LastInView([string]$Name) { $r = Rg-Row $Name; if (-not $r) { return $false }; Rg-InView $r (Rg-Last $r) }
function Rg-Describe($Row) {
    if (-not $Row) { return 'row gone' }
    $last = Rg-Last $Row
    $shown = @($Row.Items | Where-Object { Rg-InView $Row $_ } | ForEach-Object { $_.Pos })
    "items in view: $(if ($shown.Count) { "$($shown[0])-$($shown[-1])" } else { 'none' }) of $($Row.Count); last '$($last.Name)' at $(if ($last.Rect) { "$([int]$last.Rect.X),$([int]$last.Rect.Y) $([int]$last.Rect.W)x$([int]$last.Rect.H)" } else { 'not drawn' })"
}
function Rg-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function Rg-Scroller($Row) { if (-not $Row.Rect) { return $null }; [RgUi]::ScrollerAt($script:hwnd, $script:ui.Id, $Row.Rect.X + $Row.Rect.W / 2, $Row.Rect.Y + $Row.Rect.H / 2) }
function Rg-H($Row) { $s = Rg-Scroller $Row; if ($s) { [RgUi]::H($s) } else { -2 } }
function Rg-Center($Row) { @([int]($Row.Rect.X + $Row.Rect.W / 2), [int]($Row.Rect.Y + $Row.Rect.H / 2)) }

function Rg-Record([string]$Row, [string]$What, [string]$Verdict, [string]$Detail = '') {
    $ok = $Verdict -in @('pass', 'page', 'fits', 'n/t', 'n/a')
    $rgResults.Add([pscustomobject]@{ theme = $rgTheme; size = $rgSize; page = $rgPage; row = $Row; input = $What; verdict = $Verdict; ok = $ok; detail = $Detail })
    if ($Verdict -ne 'n/t') { Check "rows174 ($rgTag $rgSize $rgPage): '$Row' $What" $ok "$Verdict; $Detail" } else { Say "rows174 ($rgTag $rgSize $rgPage): '$Row' $What n/t: $Detail" }
}
# (Windows PowerShell's ConvertTo-Json fails on @() of a generic List: "Argument types do not match")
function Rg-Save { [IO.File]::WriteAllText((Join-Path $rgDir 'rows174.json'), (ConvertTo-Json -InputObject ([object[]]$rgResults.ToArray()) -Depth 4), [Text.UTF8Encoding]::new($false)) }

# ---- moving the row back, the keyboard onto it ------------------------------------------------------------------------------
function Rg-Reset([string]$Name) {
    $r = Rg-Row $Name
    if (-not $r) { return }
    $s = Rg-Scroller $r
    if ($s) { [void][RgUi]::SetH($s, 0) }
    Start-Sleep -Milliseconds 450
}
# the keyboard on the row's first item (UI Automation's focus, which the row then scrolls into view); false when it has none
function Rg-FocusFirst([string]$Name) {
    $list = [RgUi]::ListNamed($script:hwnd, $script:ui.Id, $Name)
    if (-not $list) { return $false }
    $first = [RgUi]::Child($list, 1)
    if (-not $first -or -not $first.Current.IsKeyboardFocusable) { return $false }
    [void][OffscreenUi]::SetFocus($first)
    Start-Sleep -Milliseconds 700
    $f = Rg-Focus
    return [bool]($f -and $f.name -eq $first.Current.Name)
}
# the page moved until the row is wholly in view (a row the page drew below or above the window)
# what of the page shows (UI Automation clips every rect to it): the page's scroll area, else the window
function Rg-View {
    $p = Rg-Rect ([RgUi]::PageRect($script:hwnd, $script:ui.Id))
    if ($p) { return $p }
    $w = Rg-Window
    [pscustomobject]@{ X = $w.Left; Y = $w.Top; W = $w.Width; H = $w.Height; R = $w.Right; B = $w.Bottom }
}
# wholly in what shows, below the top bar (a rect cut at the page's bottom ends exactly on it)
function Rg-Whole($Rect, $View) {
    if (-not $Rect) { return $false }
    # (a row above the page's scroll area, a library's A to Z or sections: in the window, below the top bar)
    if ($Rect.Y -lt $View.Y) { $w = Rg-Window; return $Rect.Y -ge $w.Top + 40 -and $Rect.B -le $w.Bottom }
    $Rect.Y -ge $View.Y + 56 -and $Rect.B -le $View.B - 2
}
function Rg-Show([string]$Name) {
    $r = Rg-Row $Name
    if ($r -and (Rg-Whole $r.Rect (Rg-View))) { return $r }
    foreach ($pct in 0..20) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct * 5); Start-Sleep -Milliseconds 350
        $r = Rg-Row $Name
        if ($r -and (Rg-Whole $r.Rect (Rg-View))) { return $r }
    }
    Rg-Row $Name
}
function Rg-Keys([int[]]$Vks, [int]$GapMs = 110) { foreach ($vk in $Vks) { Key $vk; Start-Sleep -Milliseconds $GapMs } }

# ---- the inputs -------------------------------------------------------------------------------------------------------------
function Rg-TestKeys([string]$Name, $Row) {
    if (-not (Rg-FocusFirst $Name)) { foreach ($i in 'keys', 'tab', 'dpad', 'shoulder') { Rg-Record $Name $i 'fail' 'no item of the row takes the keyboard' }; return }
    Rg-Keys @(0x23); Start-Sleep -Milliseconds 900
    $r = Rg-Row $Name; $f = Rg-Focus; $last = Rg-Last $r
    $endOk = $f -and $last -and $f.name -eq $last.Name -and (Rg-InView $r $last)
    Rg-Keys @(0x24); Start-Sleep -Milliseconds 700
    Rg-Keys (@(1..([Math]::Max(1, $Row.Count - 1))) | ForEach-Object { 0x27 }) 90
    # (a busy page takes the keys at its own pace: up to 4 s for the focus to get to the last, as a held key does)
    [void](Wait-Until { $x = Rg-Focus; $l = Rg-Last (Rg-Row $Name); $x -and $l -and $x.name -eq $l.Name } -Seconds 4 -PollMs 300); Start-Sleep -Milliseconds 800
    $r = Rg-Row $Name; $f = Rg-Focus; $last = Rg-Last $r
    # (a strip whose last item hands Right on to the next control, XY focus: past the end with its last item in view)
    $rightOk = $last -and (Rg-InView $r $last) -and $f -and ($f.name -eq $last.Name -or -not @($r.Items | Where-Object { $_.Name -eq $f.name }).Count)
    Rg-Record $Name 'keys' $(if ($endOk -and $rightOk) { 'pass' } else { 'fail' }) "End: $(if ($endOk) { 'last in view' } else { 'no' }); Right x$($Row.Count - 1): $(if ($rightOk) { 'last in view' } else { "focus '$($f.name)'" }); $(Rg-Describe $r)"
    if ($rgDepth -eq 'full') {
        # Tab into the row from the page's first stop: one stop for the row, its remembered item (the last), in view. (A
        # posted Shift does not reach WinUI, so Shift+Tab cannot be posted.)
        $lastName = (Rg-Last (Rg-Row $Name)).Name
        $w = Rg-Window
        # from the top bar's search box: the page's stops come after it (the page's first stop may be in the row itself)
        $start = Find-Ui -Name 'Search' -Type 'Edit' -Contains
        if (-not $start) { $start = [OffscreenUi]::FirstContentFocusable($script:hwnd, $script:ui.Id, $w.Left, $w.Top + 48) }
        $tabs = 0; $f = $null; $path = New-Object System.Collections.Generic.List[string]
        if ($start) {
            [void][OffscreenUi]::SetFocus($start); Start-Sleep -Milliseconds 500
            for (; $tabs -lt 60; $tabs++) {
                $f = Rg-Focus
                if ($f -and (@((Rg-Row $Name).Items | Where-Object { $_.Name -eq $f.name }).Count)) { break }
                if ($f) { $path.Add($f.name) }
                Key 0x09; Start-Sleep -Milliseconds 350
            }
            Start-Sleep -Milliseconds 800
        }
        # Tab lands on the row's one stop, and that item is brought into view (which one is the row's: its remembered card,
        # a choice strip's chosen one; the detail says whether it was the remembered one)
        $r = Rg-Row $Name; $f = Rg-Focus
        $landed = if ($f -and $r) { @($r.Items | Where-Object { $_.Name -eq $f.name }) | Select-Object -First 1 } else { $null }
        $tabOk = $landed -and (Rg-InView $r $landed)
        Rg-Record $Name 'tab' $(if ($tabOk) { 'pass' } else { 'fail' }) "$tabs Tabs from '$(if ($start) { $start.Current.Name })' to '$($f.name)'$(if ($landed) { ', in view: ' + (Rg-InView $r $landed) }) (the row's remembered item: '$lastName'); $(Rg-Describe $r)"
    }
    # the gamepad: D-pad right to the last, then the right shoulder pages the row
    [void](Rg-FocusFirst $Name); Rg-Keys @(0x24); Start-Sleep -Milliseconds 600
    Rg-Keys (@(1..([Math]::Max(1, $Row.Count - 1))) | ForEach-Object { 0xCE }) 90
    # (a busy page takes the keys at its own pace: up to 4 s for the focus to get to the last, as a held key does)
    [void](Wait-Until { $x = Rg-Focus; $l = Rg-Last (Rg-Row $Name); $x -and $l -and $x.name -eq $l.Name } -Seconds 4 -PollMs 300); Start-Sleep -Milliseconds 800
    $r = Rg-Row $Name; $f = Rg-Focus; $last = Rg-Last $r
    $padOk = $last -and (Rg-InView $r $last) -and $f -and ($f.name -eq $last.Name -or -not @($r.Items | Where-Object { $_.Name -eq $f.name }).Count)
    Rg-Record $Name 'dpad' $(if ($padOk) { 'pass' } else { 'fail' }) "focus '$($f.name)'; $(Rg-Describe $r)"
    if ($rgDepth -eq 'full') {
        Rg-Keys @(0x24); Start-Sleep -Milliseconds 700
        for ($k = 0; $k -lt 14 -and -not (Rg-LastInView $Name); $k++) { Key 0xC7; Start-Sleep -Milliseconds 650 }
        $r = Rg-Row $Name
        Rg-Record $Name 'shoulder' $(if (Rg-InView $r (Rg-Last $r)) { 'pass' } else { 'fail' }) "$k presses; $(Rg-Describe $r)"
    }
}

function Rg-TestTilt([string]$Name) {
    Rg-Reset $Name
    $r = Rg-Show $Name; if (-not $r) { Rg-Record $Name 'tilt' 'fail' 'row not found'; return }
    $c = Rg-Center $r
    for ($k = 0; $k -lt 30 -and -not (Rg-LastInView $Name); $k++) { [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, $c[0], $c[1], 960, $true, $false); Start-Sleep -Milliseconds 350 }
    $r = Rg-Row $Name
    Rg-Record $Name 'tilt' $(if (Rg-InView $r (Rg-Last $r)) { 'pass' } else { 'fail' }) "$k posts of 8 notches; $(Rg-Describe $r)"
}

function Rg-TestShift([string]$Name) {
    Rg-Reset $Name
    # from the page's top, where the plain wheel is the page's: only a Shift that reached WinUI moves the row
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 500
    $r = Rg-Show $Name; if (-not $r) { Rg-Record $Name 'shift' 'fail' 'row not found'; return }
    if ([RgUi]::PageV($script:hwnd, $script:ui.Id) -ge 99.5) { Rg-Record $Name 'shift' 'n/t' 'the page is at its end where the row shows: the plain wheel moves the row too'; return }
    $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
    $v0 = [RgUi]::PageV($script:hwnd, $script:ui.Id); $h0 = Rg-H $r
    $c = Rg-Center $r
    [RgUi]::ShiftWheel($kw, $c[0], $c[1], -360); Start-Sleep -Milliseconds 600
    $r1 = Rg-Row $Name; $h1 = if ($r1) { Rg-H $r1 } else { -2 }; $v1 = [RgUi]::PageV($script:hwnd, $script:ui.Id)
    if ($h1 -le $h0 + 0.1) {
        # WinUI took no Shift from the posted key: the page moved, or nothing did (unknowable off-screen; RowPaging.TargetOf's unit test)
        Rg-Record $Name 'shift' 'n/t' "the posted Shift did not reach WinUI (row $h0 -> $h1 %, page $v0 -> $v1 %)"
        return
    }
    for ($k = 1; $k -lt 30 -and -not (Rg-LastInView $Name); $k++) { $r = Rg-Row $Name; $c = Rg-Center $r; [RgUi]::ShiftWheel($kw, $c[0], $c[1], -360); Start-Sleep -Milliseconds 450 }
    $r = Rg-Row $Name
    Rg-Record $Name 'shift' $(if (Rg-InView $r (Rg-Last $r)) { 'pass' } else { 'fail' }) "$k posts; $(Rg-Describe $r)"
}

# the plain wheel: the page's while it can move, the row's once it cannot (Nixie 1.7.4)
function Rg-TestWheel([string]$Name) {
    Rg-Reset $Name
    # from the page's top: the page takes the wheel first, while it can
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 500
    $r = Rg-Show $Name; if (-not $r) { Rg-Record $Name 'wheel' 'fail' 'row not found'; return }
    $pageMoves = 0; $rowMoves = 0; $rowWhilePage = 0; $left = $false
    $fullHeight = $r.Rect.H
    for ($k = 0; $k -lt 40; $k++) {
        $r = Rg-Row $Name
        $w = Rg-Window
        # the row going out of view (under the top bar or past the bottom: UI Automation clips its rect): the pointer is off it
        if (-not $r -or -not $r.Rect -or $r.Rect.H -lt $fullHeight * 0.8 -or -not (Rg-Whole $r.Rect (Rg-View))) { $left = $true; break }
        if (Rg-InView $r (Rg-Last $r)) { break }
        $c = Rg-Center $r
        $v0 = [RgUi]::PageV($script:hwnd, $script:ui.Id); $h0 = Rg-H $r
        [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, $c[0], $c[1], -720, $false, $false); Start-Sleep -Milliseconds 400
        $v1 = [RgUi]::PageV($script:hwnd, $script:ui.Id); $r1 = Rg-Row $Name; $h1 = if ($r1) { Rg-H $r1 } else { $h0 }
        # Nixie 1.9 (c190's rows190): a row the notch took out of view is read at the centre of what is left of it, which can
        # be another scroller (Editorial's top bar over it): its move counts only while it is still wholly in view
        $inView = $r1 -and $r1.Rect -and $r1.Rect.H -ge $fullHeight * 0.8 -and (Rg-Whole $r1.Rect (Rg-View))
        $pm = $v0 -ge 0 -and [Math]::Abs($v1 - $v0) -gt 0.05; $rm = $inView -and [Math]::Abs($h1 - $h0) -gt 0.05
        if ($pm) { $pageMoves++ }; if ($rm) { $rowMoves++ }; if ($pm -and $rm) { $rowWhilePage++ }
        if (-not $pm -and -not $rm) { break }
    }
    $r = Rg-Row $Name
    $end = $r -and (Rg-InView $r (Rg-Last $r))
    $detail = "$k posts of 6 notches: the page moved $pageMoves times, the row $rowMoves$(if ($left) { ', then the row left the view' }); $(Rg-Describe $r)"
    $verdict = if ($rowWhilePage) { 'fail' } elseif ($end) { 'pass' } elseif ($left -and $pageMoves) { 'page' } else { 'fail' }
    Rg-Record $Name 'wheel' $verdict $detail
}

function Rg-TestDrag([string]$Name) {
    Rg-Reset $Name
    $r = Rg-Show $Name; if (-not $r) { Rg-Record $Name 'drag' 'fail' 'row not found'; return }
    $page0 = (Rg-Focus); $h0 = Rg-H $r
    for ($k = 0; $k -lt 14 -and -not (Rg-LastInView $Name); $k++) {
        $r = Rg-Row $Name; $c = Rg-Center $r
        [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 3, [int]($r.Rect.R - 60), $c[1], [int]($r.Rect.X + 60)); Start-Sleep -Milliseconds 700
        if ($k -eq 0 -and [Math]::Abs((Rg-H (Rg-Row $Name)) - $h0) -lt 0.05) { $k++; break }
    }
    $r = Rg-Row $Name
    $still = [bool]$r
    Rg-Record $Name 'drag' $(if ($still -and (Rg-InView $r (Rg-Last $r))) { 'pass' } else { 'fail' }) "$k drags (the hook's); the page stayed: $still; $(Rg-Describe $r)"
}

function Rg-TestButtons([string]$Name) {
    Rg-Reset $Name
    $r = Rg-Show $Name; if (-not $r) { Rg-Record $Name 'buttons' 'fail' 'row not found'; return }
    $next = "Scroll $Name right"; $back = "Scroll $Name left"
    # a theme's own chevron for the row ("More genres", Editorial's chips: shown whenever there is more): followed, as the
    # brief says; clicked until the row's last item shows
    $theme = "More $($Name.ToLowerInvariant())"
    $own = @([RgUi]::Buttons($script:hwnd, $script:ui.Id, @($theme)))
    if ($own.Count) {
        for ($k = 0; $k -lt 12 -and -not (Rg-LastInView $Name); $k++) {
            $o = Rg-Rect (($own[0] -split [char]0x1f)[1])
            [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 1, [int]($o.X + $o.W / 2), [int]($o.Y + $o.H / 2), 0); Start-Sleep -Milliseconds 800
        }
        $r = Rg-Row $Name
        Rg-Record $Name 'buttons' $(if (Rg-InView $r (Rg-Last $r)) { 'pass' } else { 'fail' }) "the theme's own '$theme': $k clicks; $(Rg-Describe $r)"
        return
    }
    function Rg-Btns { $o = @{}; foreach ($b in [RgUi]::Buttons($script:hwnd, $script:ui.Id, @($next, $back))) { $p = $b -split [char]0x1f; if ($p[2] -eq '1') { $o[$p[0]] = Rg-Rect $p[1] } }; $o }
    # away from the row first, then over it (the hook's pointer: the row's hover)
    $wa = Rg-Window
    $awayY = if ($r.Rect.Y + $r.Rect.H / 2 -lt $wa.Top + $wa.Height / 2) { [int]($wa.Bottom - 30) } else { [int]($wa.Top + 90) }
    if ($awayY -ge $r.Rect.Y -and $awayY -le $r.Rect.B) { $awayY = [int]($wa.Top + 60) }
    [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 4, [int]($wa.Left + 30), $awayY, 0); Start-Sleep -Milliseconds 300
    $away0 = Rg-Btns
    $c = Rg-Center $r
    [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 4, $c[0], $c[1], 0); Start-Sleep -Milliseconds 500
    $b0 = Rg-Btns
    if (-not $rgArrowShots.ContainsKey("$rgSize $rgPage")) { $rgArrowShots["$rgSize $rgPage"] = $true; Shot (Safe-Name "$rgTag-$rgSize-$rgPage-arrows-$Name") }
    $atStart = $b0.ContainsKey($next) -and -not $b0.ContainsKey($back)
    $atEnds = $b0.ContainsKey($next) -and $b0[$next].X -ge $r.Rect.R - 120 -and $b0[$next].Y -ge $r.Rect.Y -and $b0[$next].B -le $r.Rect.B + 1
    $clicks = 0
    for (; $clicks -lt 16; $clicks++) {
        $b = Rg-Btns
        if (-not $b.ContainsKey($next)) { break }
        $n = $b[$next]
        [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 1, [int]($n.X + $n.W / 2), [int]($n.Y + $n.H / 2), 0); Start-Sleep -Milliseconds 800
        if ($clicks -gt 0 -and (Rg-LastInView $Name)) { $b = Rg-Btns; if (-not $b.ContainsKey($next)) { $clicks++; break } }
    }
    $r = Rg-Row $Name
    $b1 = Rg-Btns
    $endOk = $r -and (Rg-InView $r (Rg-Last $r))
    $atEnd = -not $b1.ContainsKey($next) -and $b1.ContainsKey($back)
    [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 4, [int]($wa.Left + 30), $awayY, 0); Start-Sleep -Milliseconds 500
    $away1 = Rg-Btns
    $ok = $away0.Count -eq 0 -and $atStart -and $atEnds -and $endOk -and $atEnd -and $away1.Count -eq 0
    Rg-Record $Name 'buttons' $(if ($ok) { 'pass' } else { 'fail' }) ("pointer away: {0}; over the row: {1}{2}; {3} clicks of Next: last in view {4}, then {5}; pointer away: {6}; {7}" -f `
        $(if ($away0.Count) { ($away0.Keys -join '+') + ' shown' } else { 'none' }), $(if ($b0.Count) { ($b0.Keys -join '+') } else { 'none' }), $(if ($b0.ContainsKey($next) -and -not $atEnds) { ' (Next not at the row''s end)' } else { '' }),
        $clicks, $endOk, $(if ($b1.Count) { ($b1.Keys -join '+') + ' shown' } else { 'none shown' }), $(if ($away1.Count) { ($away1.Keys -join '+') + ' shown' } else { 'none' }), (Rg-Describe $r))
}

# one row with every input
function Rg-TestRow([string]$Name) {
    $r = Rg-Show $Name
    if (-not $r) {
        # a list on one line at one scroll position and on more at another is a grid that wraps (search results), not a row
        $any = @(Rg-Lists $Name)
        if ($any.Count -and -not @($any | Where-Object { $_.Horizontal }).Count) { Rg-Record $Name 'all' 'n/a' 'a grid that wraps, not a row'; return }
        Rg-Record $Name 'found' 'fail' 'no such row'; return
    }
    Rg-Reset $Name
    $r = Rg-Row $Name
    if (-not $r) {
        $seen = @(Rg-Lists | Where-Object { $_.Name -eq $Name } | ForEach-Object { "$($_.Count) items, $(@($_.Items | Where-Object { $_.Rect -and -not $_.Off }).Count) drawn, one line: $($_.Horizontal)" })
        Rg-Record $Name 'found' 'fail' "the row was not on one line after going back to its start: $($seen -join '; ')"; return
    }
    if (Rg-InView $r (Rg-Last $r)) {
        Rg-Record $Name 'all' 'fits' "the last item in view at rest; $(Rg-Describe $r)"
        # no arrow for a row that fits, even with the pointer over it
        $c = Rg-Center $r
        [void][RgUi]::Hook($script:hwnd, $script:ui.Id, 4, $c[0], $c[1], 0); Start-Sleep -Milliseconds 400
        $shown = @([RgUi]::Buttons($script:hwnd, $script:ui.Id, @("Scroll $Name right", "Scroll $Name left")))
        Rg-Record $Name 'buttons' $(if ($shown.Count -eq 0) { 'pass' } else { 'fail' }) "a row that fits shows $($shown.Count) arrows on hover"
        return
    }
    Say "rows174: '$Name' runs past its end: $(Rg-Describe $r)"
    Rg-TestKeys $Name $r
    Rg-TestTilt $Name
    if ($rgDepth -eq 'full') { Rg-TestShift $Name }
    Rg-TestWheel $Name
    Rg-TestDrag $Name
    Rg-TestButtons $Name
    Rg-Reset $Name
    Rg-Save
}

# every horizontal row of the page (named), in their order down the page
function Rg-PageRows {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($pct in 0, 25, 50, 75, 100) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); Start-Sleep -Milliseconds 500
        foreach ($l in Rg-Lists) { if ($l.Horizontal -and $l.Name -and -not $names.Contains($l.Name)) { $names.Add($l.Name) } }
    }
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 400
    $out = @($names)
    # (the item pages' rows all count: the season's strip, its episodes, its cast; the limit is for Home and the like)
    if ($rgPerPage -gt 0 -and $rgPage -notin @('season', 'series', 'season24', 'film')) { $out = @($out | Select-Object -First $rgPerPage) }
    Say "rows174 ($rgTag $rgSize $rgPage): rows $($out -join ' | ')"
    $out
}
function Rg-TestPage {
    $shot = Safe-Name "$rgTag-$rgSize-$rgPage"
    Shot $shot
    if ($env:RG_DUMP -eq '1') { Dump-Tree $shot }
    $names = @(Rg-PageRows)
    if (-not $names.Count) { Rg-Record '(none)' 'found' 'n/a' 'no horizontal row on the page'; return }
    foreach ($n in $names) { Rg-TestRow $n }
}

# ---- the pages --------------------------------------------------------------------------------------------------------------
# (Nixie 1.7.5, g3: -Series the real-shaped series of FAKESERVER_REAL_SHAPE, -Season its Season 2 of 24 episodes)
function Rg-OpenSeries($Series = $rgBs) {
    Open-Library $Series.library
    Start-Sleep -Milliseconds 1200
    $card = Wait-Until { Find-MediaCard $Series.name } -Seconds 30 -PollMs 200
    if (-not $card) { return $false }
    [void](Invoke-Element $card)
    $full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 4 -PollMs 150
    if ($full) { [void](Invoke-Element $full) }
    # (a show in progress opens on its Up next's season: any season's link)
    [bool](Wait-Until { Rg-SeasonLink } -Seconds 20 -PollMs 200)
}
function Rg-SeasonLink([int]$Season = 0) {
    $l = Find-Ui -Name 'Season ' -Type 'Hyperlink' -Contains -OffscreenToo
    if ($l -and $l.Current.Name -match $(if ($Season) { "^Season $Season details$" } else { '^Season \d+ details$' })) { $l }
}
function Rg-OpenSeason($Series = $rgBs, [int]$Season = 1) {
    if (-not (Rg-OpenSeries $Series)) { return $false }
    # (the link can be remade as the page fills in: found again, and tried twice)
    for ($try = 0; $try -lt 2; $try++) {
        $link = Wait-Until { Rg-SeasonLink $Season } -Seconds 4 -PollMs 200
        if (-not $link) {
            # another season shown: its tab in the season picker first ("Season 2", Editorial's "Season 2, 5 of 24 watched")
            $tab = $null
            foreach ($type in 'TabItem', 'ListItem', 'Button') {
                $ct = [System.Windows.Automation.ControlType]::$type
                $tab = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct))) |
                        Where-Object { $_.Cached.Name -match "^Season $Season(,|$)" }) | Select-Object -First 1
                if ($tab) { break }
            }
            if ($tab) { [void](Invoke-Element $tab) }
            $link = Wait-Until { Rg-SeasonLink $Season } -Seconds 10 -PollMs 200
        }
        if (-not $link) { return $false }
        try { [void](Invoke-Element $link); break } catch { Start-Sleep -Milliseconds 500 }
    }
    $ok = [bool](Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark Season $Season of ", '') } -Seconds 20 -PollMs 200)
    Start-Sleep -Seconds 3
    $ok
}

# the cast row's names: within the photo's width, apart from each other; the card's name for Narrator whole
function Rg-TestCast {
    $r = Rg-Show 'Cast & crew'
    if (-not $r) { Rg-Record 'Cast & crew' 'names' 'fail' 'no Cast & crew row on the season page'; return }
    Rg-Reset 'Cast & crew'
    $list = [RgUi]::ListNamed($script:hwnd, $script:ui.Id, 'Cast & crew')
    $castNames = @($rgCast | ForEach-Object { $_.name })
    # the people drawn in the row, in order: a card's first text is its name (1.7.4); 1.7.3's children were the texts themselves
    $names = New-Object System.Collections.Generic.List[object]
    $spoken = New-Object System.Collections.Generic.List[string]
    $kids = @($list.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition))
    foreach ($item in $kids) {
        $texts = @([RgUi]::Texts($item))
        if ($texts.Count) {
            $t = $texts[0] -split [char]0x1f
            $names.Add([pscustomobject]@{ Name = $t[0]; Rect = (Rg-Rect $t[1]) })
            $spoken.Add($item.Current.Name)
        }
        elseif ($castNames -contains $item.Current.Name) {
            $br = $item.Current.BoundingRectangle
            $ir = if ($br.IsEmpty -or [double]::IsInfinity($br.X) -or [double]::IsInfinity($br.Width)) { $null } else { [pscustomobject]@{ X = $br.X; Y = $br.Y; W = $br.Width; H = $br.Height; R = $br.X + $br.Width; B = $br.Y + $br.Height } }
            $names.Add([pscustomobject]@{ Name = $item.Current.Name; Rect = $ir })
            $spoken.Add($item.Current.Name)
        }
        if ($names.Count -ge 10) { break }
    }
    $drawn = @($names | Where-Object { $_.Rect })
    $wide = @($drawn | Where-Object { $_.Rect.W -gt 105 } | ForEach-Object { "'$($_.Name)' $([int]$_.Rect.W) px" })
    $close = @(for ($i = 1; $i -lt $drawn.Count; $i++) { $a = $drawn[$i - 1]; $b = $drawn[$i]; if ([Math]::Abs($a.Rect.Y - $b.Rect.Y) -lt 30 -and $b.Rect.X - $a.Rect.R -lt 8) { "'$($a.Name)' and '$($b.Name)' $([int]($b.Rect.X - $a.Rect.R)) px apart" } })
    Rg-Record 'Cast & crew' 'names' $(if (-not $wide.Count -and -not $close.Count -and $drawn.Count -ge 2) { 'pass' } else { 'fail' }) "$($drawn.Count) names drawn; wider than the photo (104): $(if ($wide.Count) { $wide -join ', ' } else { 'none' }); too close: $(if ($close.Count) { $close -join ', ' } else { 'none' })"
    $whole = @(for ($i = 0; $i -lt $spoken.Count; $i++) { $p = $rgCast[$i]; if ($p) { $want = if ($p.role) { "$($p.name), $($p.role)" } else { $p.name }; if ($spoken[$i] -ne $want) { "'$($spoken[$i])' (want '$want')" } } })
    Rg-Record 'Cast & crew' 'narrator' $(if (-not $whole.Count -and $spoken.Count) { 'pass' } else { 'fail' }) "each person read as their whole name and role: $(if ($whole.Count) { ($whole | Select-Object -First 3) -join '; ' } else { 'yes' })"
    Shot (Safe-Name "$rgTag-$rgSize-cast")
}

function Rg-Lit { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); if (-not $p) { return '' }; $sel = @($p.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $sp = $null; $_.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp) -and $sp.Current.IsSelected }); ($sel | ForEach-Object { $_.Current.Name }) -join ',' }

# the season's cast as the fake server lists it (FAKESERVER_BIG_SERIES: its long names, library.py's LONG_CAST)
$rgCast = @(if ($rgBs -and $rgBs.PSObject.Properties['cast']) { $rgBs.cast })
# Nixie 1.7.5 (g3): the real-shaped titles (FAKESERVER_REAL_SHAPE=1; pages season24 and film), $null when off
$rgReal = if ($fixture.PSObject.Properties['real_shape']) { $fixture.real_shape } else { $null }

if (-not $rgBs) { Check "rows174 ($rgTag): the fake server's big series (FAKESERVER_BIG_SERIES=24x13)" $false 'the fixture has no big_series'; return }
Say "rows174 ($rgTag): sizes $($rgSizes -join ', '); pages $($rgPages -join ', '); depth $rgDepthAsked"
foreach ($rgSize in $rgSizes) {
    $rgDepth = if ($rgDepthAsked -eq 'auto') { if ($rgSize -eq $rgSizes[0]) { 'full' } else { 'quick' } } else { $rgDepthAsked }
    if ($rgSize -eq 'max') { [RgUi]::Maximize($script:hwnd, $script:ui.Id) }
    else { [RgUi]::Restore($script:hwnd, $script:ui.Id); Start-Sleep -Milliseconds 300; $wh = $rgSize -split 'x'; [RgUi]::Place($script:hwnd, $script:ui.Id, [int]$wh[0], [int]$wh[1]) }
    Start-Sleep -Milliseconds 1500
    $wr = Rg-Window
    Say "rows174 ($rgTag): window $rgSize -> $([int]$wr.Width) x $([int]$wr.Height)"
    # Nixie 1.7.5 (the basics gate, g3): "max" is the friend's 2560 x 1392 only on a 2560 x 1440 monitor; when that monitor is
    # gone (1.7.4's integration: Cinematic and Editorial never ran at 2560) the run says so and fails, never passes smaller
    if ($rgSize -eq 'max' -and $env:RG_MAX_MIN) {
        $rgMin = $env:RG_MAX_MIN -split 'x'
        $rgPage = '(window)'
        Rg-Record '(window)' 'size' $(if ($wr.Width -ge [int]$rgMin[0] - 24 -and $wr.Height -ge [int]$rgMin[1] - 24) { 'pass' } else { 'fail' }) "maximised: $([int]$wr.Width) x $([int]$wr.Height) (at least $($env:RG_MAX_MIN) wanted: the 2560 x 1440 monitor)"
    }
    foreach ($rgPage in $rgPages) {
        try {
            switch ($rgPage) {
                'season' { if (Rg-OpenSeason) { Rg-TestPage } else { Rg-Record 'Episodes' 'found' 'fail' 'the season page did not open' } }
                # Nixie 1.7.5 (g3, FAKESERVER_REAL_SHAPE): a season of 24 episodes with long names, and a film with a cast of 48
                'season24' {
                    if (-not $rgReal) { Rg-Record 'Episodes' 'found' 'fail' 'the fixture has no real_shape (FAKESERVER_REAL_SHAPE=1)' }
                    elseif (Rg-OpenSeason $rgReal.series 2) { Rg-TestPage } else { Rg-Record 'Episodes' 'found' 'fail' 'the 24-episode season page did not open' }
                }
                'film' {
                    if (-not $rgReal) { Rg-Record 'Cast & crew' 'found' 'fail' 'the fixture has no real_shape (FAKESERVER_REAL_SHAPE=1)' }
                    else { Open-Library $rgReal.film.library; Open-Card $rgReal.film.name; [void](Wait-ItemPage $rgReal.film.name); Start-Sleep -Seconds 3; Rg-TestPage }
                }
                'cast' { if (Rg-OpenSeason) { Rg-TestCast } else { Rg-Record 'Cast & crew' 'found' 'fail' 'the season page did not open' } }
                'series' { if (Rg-OpenSeries) { Start-Sleep -Seconds 2; Rg-TestPage } else { Rg-Record '(series)' 'found' 'fail' 'the series page did not open' } }
                'home' { Open-Nav 'Home'; Start-Sleep -Seconds 4; Rg-TestPage }
                'collections' {
                    Open-Nav 'Collections'; [void](Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 20); Start-Sleep -Seconds 6
                    # Nixie 1.9 (c190's rows190): RG_WAIT_ROW names a row a first read of a big library brings (the Categories row
                    # on 25,000 films): it is waited for, then the index's line, so the page's rows are all in when they are tested
                    if ($env:RG_WAIT_ROW) {
                        [void](Wait-Ui -Name $env:RG_WAIT_ROW -Type 'List' -OffscreenToo -Seconds 300 -PollMs 700)
                        [void](Wait-Until { @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Select-String -Pattern 'Library index: \d+ titles in \d+ libraries' -ErrorAction SilentlyContinue).Count } -Seconds 120 -PollMs 1000)
                        Start-Sleep -Seconds 3
                    }
                    Rg-TestPage
                }
                'movie' { Open-Library $fixture.movie.library; Open-Card $fixture.movie.name; [void](Wait-ItemPage $fixture.movie.name); Start-Sleep -Seconds 3; Rg-TestPage }
                'library' { Open-Library $fixture.movie.library; Start-Sleep -Seconds 3; Rg-TestPage }
                'search' {
                    $box = Find-Ui -Name 'Search' -Type 'Edit' -Contains
                    if ($box) {
                        [void][OffscreenUi]::SetFocus($box); Start-Sleep -Milliseconds 400
                        $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
                        foreach ($ch in 'the'.ToCharArray()) { [void][OffscreenUi]::PostMessage($kw, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 80 }
                        Start-Sleep -Milliseconds 400; Key 0x0D; Start-Sleep -Seconds 4
                    }
                    Rg-TestPage
                }
                'topbar' {
                    # an item page reached from Home lights its section (Shows) or nothing: never Home (the friend's screenshot)
                    Open-Nav 'Home'; Start-Sleep -Seconds 3
                    # a show's card of Home's row of new shows (it opens the show's page; an Up next still would play)
                    $shows = $null
                    foreach ($pct in 0, 30, 60, 100) {
                        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); Start-Sleep -Milliseconds 500
                        $shows = @(Rg-Lists | Where-Object { $_.Name -match 'Shows' -and $_.Items.Count }) | Select-Object -First 1
                        if ($shows) { break }
                    }
                    $nu = if ($shows) { [RgUi]::ListNamed($script:hwnd, $script:ui.Id, $shows.Name) } else { $null }
                    $ep = if ($nu) { [RgUi]::Child($nu, 1) } else { $null }
                    if (-not $ep) { Rg-Record 'top bar' 'lit' 'fail' "no row of shows on Home ($(@(Rg-Lists | ForEach-Object { $_.Name }) -join ', '))"; break }
                    $litHome = Rg-Lit
                    [void](Invoke-Element $ep)
                    $full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 3 -PollMs 150
                    if ($full) { [void](Invoke-Element $full) }
                    Start-Sleep -Seconds 3
                    $lit = Rg-Lit
                    Shot (Safe-Name "$rgTag-$rgSize-topbar")
                    Rg-Record 'top bar' 'lit' $(if ($litHome -eq 'Home' -and $lit -ne 'Home' -and ($lit -eq '' -or $lit -eq 'Shows')) { 'pass' } else { 'fail' }) "Home lit '$litHome'; the show's page opened from Home lit '$lit' (Shows or nothing)"
                }
            }
        }
        catch { Rg-Record "($rgPage)" 'run' 'fail' "the script failed: $($_.Exception.Message) $($_.ScriptStackTrace -replace '\s+', ' ')" }
        Rg-Save
    }
}
[RgUi]::Maximize($script:hwnd, $script:ui.Id)
Rg-Save
$rgFails = @($rgResults | Where-Object { -not $_.ok })
Say "rows174 ($rgTag): $($rgResults.Count - $rgFails.Count) of $($rgResults.Count) row checks passed -> $(Join-Path $rgDir 'rows174.json')"
