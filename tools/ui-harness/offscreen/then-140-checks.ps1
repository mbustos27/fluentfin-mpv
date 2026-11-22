# Nixie 1.4.0, item 15 of the library and collections review's plan: the measurements the review took by hand, as checks
# that run in every theme and in the release proof. Run once per theme (NIXIE_LAYOUT; NIXIE_THEME=Light for a Light pass):
#   $env:FAKESERVER_COLLECTIONS='1'; $env:FAKESERVER_BIG_LIBRARY='1000'
#   run-offscreen.ps1 ... -Script home -Then offscreen\then-140-checks.ps1
# Checks:
#   - every top-bar element, the caption buttons included, centred on y 24 (1920 and 1100 wide);
#   - the loading placeholders laid out as the grid's first cells (the app's "[layout] library skeleton" log line);
#   - every view's item types routable: each library's first item opens a page (films, shows, collections, playlists, the
#     1,000-title Archive), and Back comes back;
#   - the focus never on an unnamed element after a navigation (each library, each item opened, Back, Settings, search);
#   - Narrator names carrying the facts the screen shows: every number shown on a card or a Libraries tile is in its name;
#   - the empty and zero-result states: a library with nothing in it, an empty playlist, a search with no results (shots);
#   - the Archive's first card time ("[perf] library", every theme, Cinematic and Editorial included);
#   - in the player, near the end: no prompt over RemainingText or SeekBar, and the bar's labels at 4.5:1 or more as drawn
#     (composited over the video).
# Shots: <tag>-<name>.png. Dot-sourced by drive.ps1: every name here starts with ic.

$icTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { "$($env:NIXIE_LAYOUT.ToLowerInvariant())$(if ($env:NIXIE_THEME) { '-' + $env:NIXIE_THEME.ToLowerInvariant() })" } else { 'classic' }
$icUia = [System.Windows.Automation.AutomationElement]

if (-not ('IcW' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
public static class IcW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO g);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  static uint ThreadOf(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }
  public static IntPtr FocusWindow(IntPtr main) { var g = new GUITHREADINFO(); g.cbSize = Marshal.SizeOf(g); return GetGUIThreadInfo(ThreadOf(main), ref g) ? g.hwndFocus : IntPtr.Zero; }
  // a key as a keyboard sends it (WM_KEYDOWN, translated to WM_CHAR), to the window that has the keyboard focus
  public static bool Key(IntPtr main, int vk) {
    var target = FocusWindow(main); if (target == IntPtr.Zero) return false;
    uint scan = MapVirtualKey((uint)vk, 0);
    PostMessage(target, 0x0100, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16))); Thread.Sleep(30);
    PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16) | (1u << 30) | (1u << 31)));
    return true;
  }
  public static bool Chars(IntPtr main, string text) {
    var target = FocusWindow(main); if (target == IntPtr.Zero) return false;
    foreach (var c in text) { PostMessage(target, 0x0102, (IntPtr)c, (IntPtr)1); Thread.Sleep(80); }
    return true;
  }
  public static int[] Client(IntPtr h) { RECT r; GetClientRect(h, out r); var p = new POINT(); ClientToScreen(h, ref p); return new[] { p.X, p.Y, r.R - r.L, r.B - r.T }; }
  static double Lum(Color c) {
    Func<double, double> ch = v => { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); };
    return 0.2126 * ch(c.R) + 0.7152 * ch(c.G) + 0.0722 * ch(c.B);
  }
  // the contrast of a label as drawn (composition included, PrintWindow's full content): the ground is the median of the
  // rectangle's edge (no glyph there), the ink the pixel farthest from it; returns { ratio, ink luminance, ground luminance }
  public static double[] Contrast(IntPtr main, int x, int y, int w, int h, string save) {
    RECT r; if (!GetWindowRect(main, out r) || w < 4 || h < 4) return null;
    int ww = r.R - r.L, wh = r.B - r.T;
    using (var bmp = new Bitmap(ww, wh, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
      int cx = x - r.L, cy = y - r.T;
      var edge = new List<double>(); var all = new List<double>();
      for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) {
        int px = cx + i, py = cy + j; if (px < 0 || py < 0 || px >= ww || py >= wh) continue;
        double l = Lum(bmp.GetPixel(px, py)); all.Add(l);
        if (i == 0 || j == 0 || i == w - 1 || j == h - 1) edge.Add(l);
      }
      if (!string.IsNullOrEmpty(save)) { using (var crop = bmp.Clone(new Rectangle(Math.Max(0, cx), Math.Max(0, cy), Math.Min(w, ww - Math.Max(0, cx)), Math.Min(h, wh - Math.Max(0, cy))), bmp.PixelFormat)) crop.Save(save, ImageFormat.Png); }
      if (edge.Count == 0) return null;
      edge.Sort(); double ground = edge[edge.Count / 2];
      double ink = ground, far = 0; foreach (var l in all) { if (Math.Abs(l - ground) > far) { far = Math.Abs(l - ground); ink = l; } }
      double hi = Math.Max(ink, ground), lo = Math.Min(ink, ground);
      return new[] { (hi + 0.05) / (lo + 0.05), ink, ground };
    }
  }
}
"@
}

function IcSettle([int]$Ms = 900) { Start-Sleep -Milliseconds $Ms }
function IcWin { $c = [IcW]::Client($script:hwnd); [pscustomobject]@{ Left = $c[0]; Top = $c[1]; Width = $c[2]; Height = $c[3] } }
function IcSize([int]$W, [int]$H) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H); IcSettle 1800 }
function IcShot([string]$Name) { Shot "$icTag-$Name" }
function IcFocus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function IcDesc($F) { if ($F) { "$($F.type) '$($F.name)'$(if ($F.automationId) { " #$($F.automationId)" })" } else { 'nothing' } }
function IcKey([int]$Vk) { [void][IcW]::Key($script:hwnd, $Vk); IcSettle 450 }
function IcBack { IcKey 0xA6 }
function IcLog([double]$Since, [string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match $Pattern } | ForEach-Object {
            $icWhen = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(($_ -split ' \[[A-Z]{3}\] ')[0], [ref]$icWhen) -and $icWhen.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { ($_ -split '\] ', 2)[1] }
        }
    })
}

# the focus after a navigation: once it has settled (an arrival puts it on the page's Primary or first card), never on an
# element without a name (Narrator would say nothing, or "button")
$icUnnamed = New-Object System.Collections.ArrayList
function IcFocusNamed([string]$Where) {
    IcSettle 1500
    $f = Wait-Until { $x = IcFocus; if ($x) { $x } } -Seconds 4 -PollMs 200
    $named = $f -and "$($f.name)".Trim().Length -gt 0
    if (-not $named) { [void]$icUnnamed.Add("$Where -> $(IcDesc $f)") }
    Check "focus ($icTag): after $Where, on a named element" $named (IcDesc $f)
}

# Narrator's names carry the facts: every number drawn on an element (its texts, the raw view) is in its name
function IcTexts($Element, [int]$Depth = 7) {
    $out = New-Object System.Collections.ArrayList
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $stack = New-Object System.Collections.Stack
    $stack.Push(@($Element, 0))
    while ($stack.Count) {
        $pair = $stack.Pop(); $e = $pair[0]; $d = $pair[1]
        try {
            $c = $walker.GetFirstChild($e)
            while ($c) {
                try { if ($c.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and -not $c.Current.IsOffscreen -and $c.Current.Name) { [void]$out.Add($c.Current.Name) } } catch { }
                if ($d + 1 -lt $Depth) { $stack.Push(@($c, ($d + 1))) }
                $c = $walker.GetNextSibling($c)
            }
        } catch { }
    }
    , $out
}
function IcFacts([string]$Where, $Elements) {
    $bad = New-Object System.Collections.ArrayList; $seen = 0
    foreach ($e in @($Elements | Select-Object -First 8)) {
        try { $name = $e.Current.Name } catch { continue }
        $texts = IcTexts $e
        if (-not $texts.Count) { continue }
        $seen++
        $numbers = @($texts | ForEach-Object { [regex]::Matches(($_ -replace '(?<=\d)[,.](?=\d{3})', ''), '\d+') | ForEach-Object { $_.Value } } | Select-Object -Unique)
        $plain = ($name -replace '(?<=\d)[,.](?=\d{3})', '')
        $missing = @($numbers | Where-Object { $plain -notmatch "(?<!\d)$_(?!\d)" })
        if ($missing.Count) { [void]$bad.Add("'$name' lacks $($missing -join ', ') (shown: $($texts -join ' | '))") }
    }
    Check "names ($icTag): $Where, every number shown is in the name" ($bad.Count -eq 0) "$seen read; $($bad -join '; ')"
}
# 1.6 integration: -Kind 'collection' or 'playlist' gives the Collections page's tiles instead (buttons named "<name>,
# collection, ..." or "<name>, playlist, ...", CollectionTile): the server's Collections and Playlists views open that page
function IcCards([double]$MinY = 140, [string]$Kind) {
    $win = IcWin
    $icCardType = if ($Kind) { [System.Windows.Automation.ControlType]::Button } else { [System.Windows.Automation.ControlType]::ListItem }
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($icUia::ControlTypeProperty, $icCardType))) |
        Where-Object { try { -not $_.Current.IsOffscreen -and ($_.Current.BoundingRectangle.Top - $win.Top) -gt $MinY -and $_.Current.Name -and (-not $Kind -or $_.Current.Name -like "*, $Kind*") } catch { $false } } |
        Sort-Object { $_.Current.BoundingRectangle.Top }, { $_.Current.BoundingRectangle.Left })
}

# ---- 1. the top bar: every element and the caption buttons centred on y 24 ------------------------------------------------
function IcBar([int]$W, [int]$H) {
    IcSize $W $H
    $win = IcWin
    if ([Math]::Abs($win.Width - $W) -gt 24) { Say "bar $($W): skipped, the window is $($win.Width) px wide at most here"; return }
    $cys = [ordered]@{}
    $pane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id)
    if ($pane) {
        $cond = New-Object System.Windows.Automation.PropertyCondition($icUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
        foreach ($t in @($pane.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })) {
            $r = $t.Current.BoundingRectangle; $cys["tab:$($t.Current.Name)"] = [Math]::Round($r.Top + $r.Height / 2 - $win.Top, 1)
        }
    }
    foreach ($pair in @(@('search', (Find-Ui -Name 'Search' -Type 'Edit' -Contains)), @('searchIcon', (Find-Ui -Name 'Search' -Type 'Button')),
                         @('account', (Find-Ui -Name 'Account, ' -Type 'Button' -Contains)), @('minimize', (Find-Ui -Name 'Minimize' -Type 'Button')),
                         @('maximize', (Find-Ui -Name 'Maximize' -Type 'Button' -Contains)), @('close', (Find-Ui -Name 'Close' -Type 'Button')))) {
        if ($pair[1]) { $r = $pair[1].Current.BoundingRectangle; if (($r.Top - $win.Top) -lt 48) { $cys[$pair[0]] = [Math]::Round($r.Top + $r.Height / 2 - $win.Top, 1) } }
    }
    $back = @(Find-Ui -Name 'Back' -Type 'Button') | Where-Object { $_ -and ($_.Current.BoundingRectangle.Top - $win.Top) -lt 48 } | Select-Object -First 1
    if ($back) { $r = $back.Current.BoundingRectangle; $cys.back = [Math]::Round($r.Top + $r.Height / 2 - $win.Top, 1) }
    $off = @($cys.GetEnumerator() | Where-Object { [Math]::Abs($_.Value - 24) -gt 1.5 })
    Check "top bar ($icTag, $W): every element and the caption buttons centred on y 24" ($off.Count -eq 0 -and $cys.Contains('close')) (($cys.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')
}
Open-Nav 'Home'; IcSettle 2500
IcBar 1920 1080
IcBar 1100 900
IcSize 1920 1080

# ---- 2. the Libraries page: its tiles' names carry their counts; the focus on arrival ---------------------------------
Open-Nav 'Libraries'
IcFocusNamed 'opening the Libraries page'
[void](Wait-Until { if (@(IcCards 100).Count -ge 4) { $true } } -Seconds 15 -PollMs 300)
IcSettle 1500
IcFacts 'the Libraries tiles' (IcCards 100)
IcShot 'libraries'

# ---- 3. the skeleton: the placeholders where the grid's cells will be (the items held back 2.5 s) -------------------------
foreach ($icW in @(1920, 1100)) {
    IcSize $icW $(if ($icW -eq 1920) { 1080 } else { 900 })
    Open-Nav 'Home'; IcSettle 1500
    [void](Invoke-Server '/__harness/latency' 'POST' @{ rules = @(, @('^/(Users/[^/]+/)?Items$', 2500)) })
    $icAt = Get-Epoch
    Open-Library $fixture.movie.library
    $icLay = Wait-Until { IcLog $icAt '\[layout\] library skeleton' | Select-Object -First 1 } -Seconds 15 -PollMs 250
    [void](Invoke-Server '/__harness/latency' 'POST' @{ rules = @() })
    Check "skeleton ($icTag, $icW): the placeholders stand where the grid's cells are" ([bool]$icLay -and $icLay -match 'the same cells') "$icLay"
}
IcSize 1920 1080

# ---- 4. every view's items open, the focus named after each step; the numbers on the cards in their names ------------------
$icViews = @($fixture.views | Where-Object { $_ })
foreach ($icView in $icViews) {
    try { Open-Nav 'Home'; IcSettle 800; Open-Library $icView } catch { Check "views ($icTag): $icView opens" $false $_.Exception.Message; continue }
    $icAt = Get-Epoch
    # 1.6 integration: Collections (the top bar's) and Playlists (the Libraries page's tile) open the Collections page, its
    # tiles of that kind; any other view is its library page's cards
    $icKind = ''
    if ($icView -in @('Collections', 'Playlists')) {
        $icTry = if ($icView -eq 'Playlists') { 'playlist' } else { 'collection' }
        if (Wait-Until { IcCards -Kind $icTry | Select-Object -First 1 } -Seconds 15 -PollMs 250) { $icKind = $icTry }
    }
    $icType = if ($icKind) { 'Button' } else { 'ListItem' }
    $icFirst = Wait-Until { IcCards -Kind $icKind | Select-Object -First 1 } -Seconds 30 -PollMs 250
    if (-not $icFirst) {
        IcShot "view-$(Safe-Name $icView)-empty"
        Check "views ($icTag): $icView shows its items" $false 'no card'
        continue
    }
    IcFocusNamed "opening the $icView library"
    IcFacts "the $icView cards" (IcCards -Kind $icKind)
    if ($icView -eq 'Archive') {
        $icPerf = Wait-Until { IcLog $icAt '\[perf\] library: first card' | Select-Object -Last 1 } -Seconds 10 -PollMs 250
        Check "archive ($icTag): the first card's time is logged ([perf])" ([bool]$icPerf) "$icPerf"
        IcShot 'archive-1920'
        IcKey 0x23; IcSettle 2500   # End: the last titles
        IcShot 'archive-end-1920'
        IcKey 0x24; IcSettle 1500
    }
    $icFirst = IcCards -Kind $icKind | Select-Object -First 1
    $icName = $icFirst.Current.Name
    $icTitle = ($icName -split ', ')[0]
    [void][OffscreenUi]::SetFocus($icFirst); IcSettle 400
    IcKey 0x0D
    # (Editorial: the details in place first; their Full details opens the page)
    $icFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
    if ($icFull) { [void][OffscreenUi]::SetFocus($icFull); IcSettle 300; IcKey 0x0D }
    $icOpened = Wait-Until { if (-not (Find-Ui -Name $icName -Type $icType) -and (Find-Ui -Name $icTitle -Contains)) { $true } } -Seconds 15 -PollMs 250
    Check "views ($icTag): $icView's first item ('$icTitle') opens its page" ([bool]$icOpened) "$(IcDesc (IcFocus))"
    if ($icOpened) {
        IcFocusNamed "opening '$icTitle' from $icView"
        IcShot "view-$(Safe-Name $icView)-item"
        IcBack
        $icBack = Wait-Until { Find-Ui -Name $icName -Type $icType -OffscreenToo } -Seconds 12 -PollMs 250
        Check "views ($icTag): Back from '$icTitle' comes back to $icView" ([bool]$icBack) ''
        if ($icBack) { IcFocusNamed "Back to $icView" }
    }
}

# ---- 5. the empty and zero-result states -------------------------------------------------------------------------------------
# a playlist with nothing in it (FAKESERVER_COLLECTIONS: "Nothing Yet")
if ($icViews -contains 'Playlists') {
    Open-Nav 'Home'; IcSettle 800; Open-Library 'Playlists'
    $icEmpty = Wait-Until { Find-MediaCard 'Nothing Yet' } -Seconds 20 -PollMs 250
    if ($icEmpty) {
        [void](Invoke-Element $icEmpty)
        # (Editorial: the details in place first; their Full details opens the page)
        $icFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
        if ($icFull) { [void](Invoke-Element $icFull) }
        $icSays = Wait-Until { Find-Ui -Name 'This playlist is empty' -Type 'Text' -Contains } -Seconds 12 -PollMs 250
        IcSettle 1200
        IcShot 'empty-playlist'
        Check "empty ($icTag): an empty playlist says so" ([bool]$icSays) $(if ($icSays) { $icSays.Current.Name } else { '' })
        IcFocusNamed 'opening an empty playlist'
    }
    else { Check "empty ($icTag): an empty playlist says so" $false 'no "Nothing Yet" playlist' }
}
# a library with nothing in it (added on the server: the clients hear LibraryChanged)
[void](Invoke-Server '/__harness/libraries' 'POST' @{ add = @('Empty Shelf') })
IcSettle 3000
try {
    Open-Nav 'Home'; IcSettle 800; Open-Library 'Empty Shelf'
    $icSays = Wait-Until { Find-Ui -Name 'This library is empty' -Type 'Text' -Contains } -Seconds 15 -PollMs 250
    IcSettle 1200
    IcShot 'empty-library'
    Check "empty ($icTag): a library with nothing in it says so" ([bool]$icSays) $(if ($icSays) { $icSays.Current.Name } else { '' })
    IcFocusNamed 'opening an empty library'
}
catch { Check "empty ($icTag): a library with nothing in it says so" $false $_.Exception.Message }
# a search with no results
Open-Nav 'Home'; IcSettle 1000
IcKey 0xBF   # /
$icBox = Wait-Until { $f = IcFocus; if ($f -and $f.type -eq 'Edit') { $f } } -Seconds 4 -PollMs 150
if ($icBox) {
    [void][IcW]::Chars($script:hwnd, 'qqzxv'); IcSettle 900
    IcKey 0x0D
    $icNone = Wait-Until { Find-Ui -Name 'No results for' -Type 'Text' -Contains } -Seconds 12 -PollMs 250
    IcSettle 1000
    IcShot 'search-no-results'
    Check "empty ($icTag): a search with no results says so" ([bool]$icNone) $(if ($icNone) { $icNone.Current.Name } else { '' })
    IcFocusNamed 'a search with no results'
}
else { Check "empty ($icTag): a search with no results says so" $false 'the search box did not take the keyboard' }
# Settings
Open-Nav 'Settings'
IcFocusNamed 'opening Settings'

# ---- 6. the player near the end: no prompt over the time or the seek bar; the bar's labels legible as drawn ----------------
try {
    Step-Season $false
    Step-Play
    IcSettle 1500
    $icSeek = Wait-Ui -Id 'SeekBar' -Seconds 8
    $icMax = $null; $icRv = $null
    if ($icSeek -and $icSeek.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$icRv)) { $icMax = $icRv.Current.Maximum }
    if ($icSeek -and $icMax) {
        # 20 s before the end (the fake server's media is 600 s): the end credits (its last 40 s) and the next episode's prompt are on
        $icRv.SetValue([double]$icMax * (1 - 20.0 / 600))
        $icPromptShown = { $p = @(Find-Ui -Id 'NextPill') | Where-Object { $_ -and -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 }; if ($p) { $p } }
        $icPrompt = Wait-Until $icPromptShown -Seconds 6 -PollMs 250
        if (-not $icPrompt) {
            # the seek bar's value from UI Automation did not seek: 9 (90 %, 60 s before the end), play until the prompt, pause
            IcKey 0x39; IcKey 0x20
            $icPrompt = Wait-Until $icPromptShown -Seconds 45 -PollMs 250
            IcKey 0x20
        }
        IcKey 0x28; IcKey 0x26   # the bars up (volume down and up again)
        IcSettle 1200
        IcShot 'player-prompt'
        $icRemain = Find-Ui -Id 'RemainingText'; $icSeek = Find-Ui -Id 'SeekBar'
        $icOver = New-Object System.Collections.ArrayList
        $icPrompts = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($icUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
            Where-Object { try { $n = $_.Current.Name; -not $_.Current.IsOffscreen -and ($n -match '^(Next episode|Up next|Watch credits|Keep watching|Skip (intro|credits))') } catch { $false } })
        foreach ($p in $icPrompts) {
            $pr = $p.Current.BoundingRectangle
            foreach ($t in @(@('RemainingText', $icRemain), @('SeekBar', $icSeek))) {
                if (-not $t[1]) { continue }
                $tr = $t[1].Current.BoundingRectangle
                if ($pr.IntersectsWith($tr)) { [void]$icOver.Add("'$($p.Current.Name)' over $($t[0])") }
            }
        }
        Check "player ($icTag): a prompt shows near the end" ($icPrompts.Count -gt 0) (@($icPrompts | ForEach-Object { $_.Current.Name }) -join ', ')
        Check "player ($icTag): no prompt over RemainingText or SeekBar" ($icPrompts.Count -gt 0 -and $icOver.Count -eq 0 -and $icRemain -and $icSeek) "$($icOver -join '; ')"
        # the bar's labels as drawn: the elapsed and remaining times, the title
        foreach ($icId in @('ElapsedText', 'RemainingText', 'TitleText')) {
            $e = Find-Ui -Id $icId
            if (-not $e -or $e.Current.IsOffscreen) { Say "contrast: no $icId shown"; continue }
            $r = $e.Current.BoundingRectangle
            $c = [IcW]::Contrast($script:hwnd, [int]$r.Left - 2, [int]$r.Top - 2, [int]$r.Width + 4, [int]$r.Height + 4, (Join-Path $Out "shots\$icTag-contrast-$icId.png"))
            Check "player ($icTag): $icId at 4.5:1 or more as drawn" ($c -and $c[0] -ge 4.5) $(if ($c) { "{0:N2}:1 (ink {1:N3}, ground {2:N3})" -f $c[0], $c[1], $c[2] } else { 'not sampled' })
        }
    }
    else { Check "player ($icTag): a prompt shows near the end" $false 'no seek bar to move' }
}
catch { Check "player ($icTag): the end of an episode" $false $_.Exception.Message }

Say "focus after navigation: $(if ($icUnnamed.Count) { 'unnamed: ' + ($icUnnamed -join '; ') } else { 'always named' })"
