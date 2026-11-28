# Nixie 1.9 (the scroll stream; the owner at 10:55 PM on the 1.9 test copy: "scrolling the collections page takes a longgg
# time"): a full scroll of the Collections page, top to bottom and back, by the wheel, by the keys and by a gamepad's D-pad,
# timed in frames, in the theme of NIXIE_LAYOUT at the sizes of GATE_SIZES (default max). Needs a dev build (the frame clock is
# the pointer hook's: HarnessFrames, Kind 9). Run at the owner's library's shape (the scroll190 manifest entry):
#   FAKESERVER_BIG_LIBRARY=152800 FAKESERVER_BIG_SHOWS=37700 FAKESERVER_COLLECTIONS=1 FAKESERVER_FRANCHISES190=1
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-190-scroll.ps1
# The page is measured once the library index is whole (its line said), the Categories row shows and the people step after the
# first read is done (SC_WAIT_PEOPLE=0: while it runs). Parts (SC_PARTS, default wheel,keys,dpad; the gate's: all but notch):
#   notch   how far a notch moves the page, read by UI Automation (any build): 5 notches 400 ms apart, 12 notches 40 ms apart
#   wheel   from the top, a notch every SC_GAP_MS (40: a quick steady spin) over the page's middle, as many as the page's height
#           asks at 96 px a notch and a few more: the page reaches its end, no later than SC_LAG_MS (1000) after the notches it
#           needed were sent, and its slow frames (over 50 ms) take at most SC_BUDGET_MS (2000) together; then back up the same
#   seeall  See all Categories (a grid of every category): the wheel to its end and back, timed; Back
#   pgdn    Page Down and Page Up from a tile: 85 % of the view a press, to the end and
#           back to the top
#   mixed   a quick spin of 3 notches, then before its glide ends Down (the next row brought into view), or Enter on a tile and
#           Back; then one notch moves the page one notch from where that put it, not from the spin's target
#   keys    the keyboard on the first row's first tile, Down every SC_KEY_GAP_MS (150) past the last row: the last row's tile
#           has the keyboard, on screen, and the slow frames are within the budget; then Up back to the first row
#   dpad    the same with a gamepad's D-pad (VK_GAMEPAD_DPAD_DOWN/UP, as rows-gate posts them)
# The budget, 2 s of frames over 50 ms per full scroll (a frame over 50 ms is three missed at 60 Hz, a stall the eye sees): a
# full wheel scroll of this page is 21 to 28 notches and reaches the end 1.4 to 1.8 s after the first, so 2 s of stalls is a
# page frozen longer than it moves. Measured (Classic, 2026-11-26): this branch 0 ms (0 to 187 ms while the index's people step
# reads 1,000-title pages behind it); with ImageEx's lazy loading on and three apps reading at once, up to 1,287 ms (the room left
# for three gate runs at once). Check names: "<GATE_ID, else scroll190> [<theme>, <size>]: ...". Dot-sourced by drive.ps1: every
# name here starts with sc190 / Sc190. ASCII only.

. (Join-Path $PSScriptRoot '..\..\gate\gate-common.ps1')
$sc190Id = if ($env:GATE_ID) { $env:GATE_ID } else { 'scroll190' }
$sc190Theme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$sc190Sizes = @(Get-GateSizes)
if ($sc190Sizes.Count -eq 0) { $sc190Sizes = @('max') }
$sc190Parts = @(("$(if ($env:SC_PARTS) { $env:SC_PARTS } else { 'wheel,keys,dpad' })" -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$sc190Budget = if ($env:SC_BUDGET_MS) { [double]$env:SC_BUDGET_MS } else { 2000 }
$sc190Gap = if ($env:SC_GAP_MS) { [int]$env:SC_GAP_MS } else { 40 }
$sc190KeyGap = if ($env:SC_KEY_GAP_MS) { [int]$env:SC_KEY_GAP_MS } else { 150 }
$sc190Lag = if ($env:SC_LAG_MS) { [double]$env:SC_LAG_MS } else { 1000 }
$sc190IndexSeconds = if ($env:SC_INDEX_SECONDS) { [int]$env:SC_INDEX_SECONDS } else { 1500 }
# (SC_NOTCH_PX: the notch the wheel's count is reckoned at; 96 is RowPaging.PixelsPerNotch)
$sc190NotchPx = if ($env:SC_NOTCH_PX) { [double]$env:SC_NOTCH_PX } else { 96 }
$sc190A = [System.Windows.Automation.AutomationElement]
$sc190Size = ''
$sc190Rows = [System.Collections.Generic.List[object]]::new()

if (-not ('Sc190Ui' -as [type])) {
    Add-Type @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class Sc190Ui {
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  static void Own(IntPtr h, int pid) { uint p; GetWindowThreadProcessId(h, out p); if (p != (uint)pid) throw new InvalidOperationException("not FluentFin's window"); }
  static IntPtr Lp(int x, int y) { return (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)); }
  // the dev build's frame clock (HarnessPointer kind 9): 1 starts timing, 2 stops and logs "[harness] frames: ..."
  public static bool Frames(IntPtr main, int pid, int high) {
    Own(main, pid);
    return PostMessage(main, RegisterWindowMessage("NixieHarnessPointer"), (IntPtr)(9L | ((long)high << 16)), IntPtr.Zero);
  }
  // count wheel notches at screen point (x, y), gapMs apart, posted to the window WinUI takes a posted wheel in (the keys'
  // InputSiteWindowClass); the pointer moved there once first. Returns the ms the posting took
  public static long WheelBurst(IntPtr target, int pid, int x, int y, int delta, int count, int gapMs) {
    Own(target, pid);
    var c = new POINT { X = x, Y = y }; ScreenToClient(target, ref c);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(c.X - 4, c.Y - 4));
    System.Threading.Thread.Sleep(30);
    PostMessage(target, 0x0200, IntPtr.Zero, Lp(c.X, c.Y));
    System.Threading.Thread.Sleep(30);
    var watch = Stopwatch.StartNew();
    long w = ((long)(short)delta << 16);
    for (int i = 0; i < count; i++) {
      PostMessage(target, 0x020Au, (IntPtr)(uint)(w & 0xFFFFFFFF), Lp(x, y));
      if (i < count - 1) System.Threading.Thread.Sleep(gapMs);
    }
    return watch.ElapsedMilliseconds;
  }
}
"@
}

function Sc190-Check([string]$What, [bool]$Ok, [string]$Detail = '') {
    $where = if ($script:sc190Size) { "$sc190Theme, $($script:sc190Size)" } else { $sc190Theme }
    Check "$sc190Id [$where]: $What" $Ok $Detail
}
function Sc190-Log([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object {
            Get-Content $_.FullName -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}
function Sc190-List([string]$Row) { Find-Ui -Name $Row -Type 'List' -OffscreenToo }
# the page's rows (ShelfRow names its list after the row's title), top to bottom: UI Automation's tree order (a row out of view
# has an empty rectangle: sorted by it, the rows below the view came last whatever their place)
function Sc190-RowLists {
    $cond = New-Object System.Windows.Automation.PropertyCondition($sc190A::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { try { $_.Cached.Name } catch { $false } })
}
# the page's vertical scroll: { Scrollable (px), Percent, ViewPercent } from UI Automation (the largest vertically scrollable)
function Sc190-PageScroll {
    $best = $null; $area = 0
    foreach ($e in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($sc190A::IsScrollPatternAvailableProperty, $true))))) {
        try {
            $r = $e.Cached.BoundingRectangle
            $sp = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if (-not $sp.Current.VerticallyScrollable -or $r.IsEmpty) { continue }
            if ($r.Width * $r.Height -gt $area) { $area = $r.Width * $r.Height; $best = @{ E = $e; R = $r; S = $sp } }
        } catch { }
    }
    if (-not $best) { return $null }
    $view = $best.S.Current.VerticalViewSize
    $h = $best.R.Height
    [pscustomobject]@{ Scrollable = if ($view -gt 0) { $h * (100 / $view - 1) } else { 0 }; Percent = $best.S.Current.VerticalScrollPercent; View = $view; Height = $h; Rect = $best.R }
}
# the frame clock started, the action run, the clock stopped: its log line parsed
function Sc190-Timed([scriptblock]$Action, [int]$SettleMs = 1800) {
    $before = (Sc190-Log '\[harness\] frames: \d+ in').Count
    [void][Sc190Ui]::Frames($script:hwnd, $script:ui.Id, 1)
    Start-Sleep -Milliseconds 250
    $extra = & $Action
    Start-Sleep -Milliseconds $SettleMs
    [void][Sc190Ui]::Frames($script:hwnd, $script:ui.Id, 2)
    $line = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $lines = @(Sc190-Log '\[harness\] frames: \d+ in')
        if ($lines.Count -gt $before) { $line = $lines[-1]; break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $line) { return [pscustomobject]@{ Ok = $false; Line = 'no "[harness] frames" line (a dev build? NIXIE_HARNESS_POINTER=1?)'; Extra = $extra } }
    $m = [regex]::Match($line, 'frames: (\d+) in (\d+) ms, (\d+) over 50 ms \((\d+) ms together\), longest (\d+) ms, p95 (\d+) ms; page end ([\d.NaN-]+), at ([\d.NaN-]+), reached at (\w+)')
    $more = [regex]::Match($line, '(\d+) layout passes; pictures (\d+) alive, (\d+) in the window')
    $gc = [regex]::Match($line, 'the collector paused (\d+) ms \((\d+) gen 2\); working set (\d+) MB')
    if (-not $m.Success) { return [pscustomobject]@{ Ok = $false; Line = $line; Extra = $extra } }
    $reached = if ($m.Groups[9].Value -eq 'never') { $null } else { [double]$m.Groups[9].Value - 250 }
    [pscustomobject]@{ Ok = $true; Frames = [int]$m.Groups[1].Value; Ms = [double]$m.Groups[2].Value; Slow = [int]$m.Groups[3].Value
        SlowMs = [double]$m.Groups[4].Value; Longest = [double]$m.Groups[5].Value; P95 = [double]$m.Groups[6].Value
        End = $m.Groups[7].Value; At = $m.Groups[8].Value; Reached = $reached; Line = ($line -replace '^.*\[harness\] ', ''); Extra = $extra
        Layouts = if ($more.Success) { [int]$more.Groups[1].Value } else { -1 }; Pictures = if ($more.Success) { [int]$more.Groups[2].Value } else { -1 }
        PicturesShown = if ($more.Success) { [int]$more.Groups[3].Value } else { -1 }
        GcMs = if ($gc.Success) { [int]$gc.Groups[1].Value } else { -1 }; WorkingSet = if ($gc.Success) { [int]$gc.Groups[3].Value } else { -1 } }
}
function Sc190-Record([string]$Part, $T, [hashtable]$More = @{}) {
    $row = [ordered]@{ theme = $sc190Theme; size = $script:sc190Size; part = $Part; frames = $T.Frames; ms = $T.Ms; slow = $T.Slow; slow_ms = $T.SlowMs
        longest = $T.Longest; p95 = $T.P95; reached = $T.Reached; layouts = $T.Layouts; pictures = $T.Pictures; pictures_shown = $T.PicturesShown; gc_ms = $T.GcMs; working_set_mb = $T.WorkingSet; line = $T.Line }
    foreach ($k in $More.Keys) { $row[$k] = $More[$k] }
    $script:sc190Rows.Add([pscustomobject]$row)
    Say "scroll190 $Part [$sc190Theme $($script:sc190Size)]: $($T.Line) $(($More.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')"
}
# the keyboard on a row's first tile (UI Automation's focus, as Tab leaves it), the page at its top
function Sc190-FocusFirstTile {
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 800
    $lists = @(Sc190-RowLists)
    foreach ($l in $lists) {
        $b = @($l.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($sc190A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
            Where-Object { try { $_.Current.IsKeyboardFocusable -and $_.Current.AutomationId -notmatch 'Row(Back|Next)Button' } catch { $false } } | Select-Object -First 1)
        if ($b.Count) { [void][OffscreenUi]::SetFocus($b[0]); Start-Sleep -Milliseconds 800; [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 800; return $l.Current.Name }
    }
    $null
}
# the row the keyboard is in (its list's name) and whether the focused tile is on screen
function Sc190-FocusRow {
    try {
        $cond = New-Object System.Windows.Automation.PropertyCondition($sc190A::HasKeyboardFocusProperty, $true)
        $f = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window }) | Select-Object -First 1
        if (-not $f) { return $null }
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $w = $sc190A::FromHandle($script:hwnd).Current.BoundingRectangle
        $r = $f.Current.BoundingRectangle
        $on = -not $f.Current.IsOffscreen -and $r.Height -gt 0 -and $r.Top -ge $w.Top - 1 -and $r.Bottom -le $w.Bottom + 1
        for ($p = $walker.GetParent($f); $p; $p = $walker.GetParent($p)) {
            if ($p.Current.ControlType -eq [System.Windows.Automation.ControlType]::List) { return [pscustomobject]@{ Row = $p.Current.Name; OnScreen = $on; Name = $f.Current.Name } }
        }
        [pscustomobject]@{ Row = ''; OnScreen = $on; Name = $f.Current.Name }
    } catch { $null }
}

# ---- the page, once the index is whole ------------------------------------------------------------------------------------------
Open-Nav 'Collections'
[void](Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 30)
$sc190Began = Get-Date
$sc190Deadline = $sc190Began.AddSeconds($sc190IndexSeconds)
while ((Get-Date) -lt $sc190Deadline -and -not ((Sc190-Log 'Library index: \d+ titles in \d+ libraries').Count -and (Sc190-List 'Categories'))) { Start-Sleep -Seconds 2 }
$sc190Index = @(Sc190-Log 'Library index: \d+ titles in \d+ libraries') | Select-Object -Last 1
# (SC_WAIT_PEOPLE=0: measured while the people step reads every title's people, as after a first read)
if ("$env:SC_WAIT_PEOPLE" -ne '0') {
    while ((Get-Date) -lt $sc190Deadline -and -not (Sc190-Log 'Library index: people pass (done|stopped|failed)').Count) { Start-Sleep -Seconds 2 }
    Say "scroll190: the people step: $((Sc190-Log 'Library index: people pass') | Select-Object -Last 1)"
}
Sc190-Check 'the library index is whole and the Collections page shows its Categories row' ([bool]($sc190Index -and (Sc190-List 'Categories'))) "$($sc190Index -replace '^.*(Library index:)', '$1') after $([int]((Get-Date) - $sc190Began).TotalSeconds) s"
# what the app did after the index (the summaries' pass, a rebuild) settles before the page is measured
Start-Sleep -Seconds $(if ($env:SC_SETTLE_SECONDS) { [int]$env:SC_SETTLE_SECONDS } else { 20 })
# (SC_PAGE=library:<name>: that library's page measured instead, for the notch's comparison)
if ("$env:SC_PAGE" -like 'library:*') { Open-Library ($env:SC_PAGE -replace '^library:', ''); Start-Sleep -Seconds 8 }

foreach ($sc190S in $sc190Sizes) {
    try { $sc190State = Set-GateSize $sc190S; $script:sc190Size = $sc190S }
    catch { $script:sc190Size = $sc190S; Sc190-Check 'the window took the size' $false $_.Exception.Message; continue }
    Start-Sleep -Seconds 2
    $sc190Lists = @(Sc190-RowLists)
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 800
    $sc190Page = Sc190-PageScroll
    if (-not $sc190Page -or $sc190Page.Scrollable -le 0) { Sc190-Check 'the page scrolls' $false "no vertical scroll ($(Format-GateSize $sc190State))"; continue }
    $sc190Said = "$(Format-GateSize $sc190State); $($sc190Lists.Count) rows, the page $([int]$sc190Page.Scrollable) px past its view"
    Say "scroll190 [$sc190Theme $sc190S]: $sc190Said; rows: $(($sc190Lists | ForEach-Object { $_.Current.Name }) -join ' | ')"

    if ('notch' -in $sc190Parts) {
        # how far a notch moves the page, read by UI Automation (any build, no frame clock): 5 notches 400 ms apart (each glide
        # ends before the next), then 12 notches 40 ms apart (a quick spin), from the top, over the page's middle
        $x = [int]($sc190Page.Rect.Left + $sc190Page.Rect.Width / 2)
        $y = [int]($sc190Page.Rect.Top + $sc190Page.Rect.Height * 0.55)
        $target = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
        foreach ($case in @(@{ N = 5; Gap = 400; Name = 'slow' }, @{ N = 12; Gap = 40; Name = 'quick' })) {
            [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 1500
            $p0 = Sc190-PageScroll
            [void][Sc190Ui]::WheelBurst($target, $script:ui.Id, $x, $y, -120, $case.N, $case.Gap)
            Start-Sleep -Milliseconds 2000
            $p1 = Sc190-PageScroll
            $px = if ($p0 -and $p1) { ($p1.Percent - $p0.Percent) / 100 * $p1.Scrollable } else { -1 }
            $script:sc190Rows.Add([pscustomobject][ordered]@{ theme = $sc190Theme; size = $script:sc190Size; part = "notch-$($case.Name)"; notches = $case.N; gap = $case.Gap
                    px = [Math]::Round($px); px_per_notch = [Math]::Round($px / $case.N, 1); scrollable = [Math]::Round($p1.Scrollable) })
            Say "scroll190 notch-$($case.Name) [$sc190Theme $($script:sc190Size)]: $($case.N) notches $($case.Gap) ms apart moved the page $([Math]::Round($px)) px ($([Math]::Round($px / $case.N, 1)) px a notch; the page $([Math]::Round($p1.Scrollable)) px past its view)"
        }
    }

    if ('wheel' -in $sc190Parts) {
        $needed = [int][Math]::Ceiling($sc190Page.Scrollable / $sc190NotchPx)
        $count = $needed + 6
        $x = [int]($sc190Page.Rect.Left + $sc190Page.Rect.Width / 2)
        $y = [int]($sc190Page.Rect.Top + $sc190Page.Rect.Height * 0.55)
        $target = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
        $sc190Repeat = if ($env:SC_REPEAT) { [int]$env:SC_REPEAT } else { 1 }
        foreach ($dir in @(1..$sc190Repeat | ForEach-Object { 'down', 'up' })) {
            if ($dir -eq 'down') { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0) } else { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100) }
            Start-Sleep -Milliseconds 1200
            $t = Sc190-Timed { [Sc190Ui]::WheelBurst($target, $script:ui.Id, $x, $y, $(if ($dir -eq 'down') { -120 } else { 120 }), $count, $sc190Gap) } 2500
            $after = Sc190-PageScroll
            $pct = if ($after) { [Math]::Round($after.Percent, 1) } else { -1 }
            $want = $needed * $sc190Gap + $sc190Lag
            Sc190-Record "wheel-$dir" $t @{ notches = $count; needed = $needed; gap = $sc190Gap; percent_after = $pct }
            if (-not $t.Ok) { Sc190-Check "the wheel $dir`: the frames were timed" $false $t.Line; continue }
            $endOk = if ($dir -eq 'down') { $pct -ge 99.5 } else { $pct -le 0.5 }
            # down: the clock saw the end reached in time; up: the page back at its top (the clock times the end below only)
            $inTime = if ($dir -eq 'down') { $null -ne $t.Reached -and $t.Reached -le $want } else { $endOk }
            Sc190-Check "the wheel takes the page from $(if ($dir -eq 'down') { 'top to bottom' } else { 'bottom to top' }) in the notches its height asks ($needed at $sc190NotchPx px, a notch every $sc190Gap ms)" ($endOk -and $inTime) `
                "at $pct %; end reached at $(if ($null -ne $t.Reached) { "$([int]$t.Reached) ms" } else { 'never' }) (within $([int]$want) ms); $sc190Said"
            Sc190-Check "the wheel $dir`: slow frames within $([int]$sc190Budget) ms together" ($t.SlowMs -le $sc190Budget) "$($t.Slow) frames over 50 ms, $([int]$t.SlowMs) ms together; longest $([int]$t.Longest) ms; p95 $([int]$t.P95) ms; $($t.Frames) frames in $([int]$t.Ms) ms"
        }
    }

    if ('seeall' -in $sc190Parts) {
        # See all Categories (every category, a grid): the wheel over the grid from its top to its end and back, timed; Back
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 800
        $see = $null
        foreach ($pct in @(0, 25, 50, 75, 100)) {
            [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); Start-Sleep -Milliseconds 400
            $see = Find-Ui -Name 'See all Categories' -Contains -Type 'Hyperlink' -OffscreenToo
            if ($see) { break }
        }
        if (-not $see) { Sc190-Check 'see all: See all Categories is there' $false 'no See all Categories link' }
        else {
            [void](Invoke-Element $see)
            [void](Wait-Ui -Id 'TitleText' -Name 'Categories' -OffscreenToo -Seconds 15)
            Start-Sleep -Milliseconds 2500
            $grid = Sc190-PageScroll
            $gx = if ($grid) { [int]($grid.Rect.Left + $grid.Rect.Width / 2) } else { 0 }
            $gy = if ($grid) { [int]($grid.Rect.Top + $grid.Rect.Height * 0.55) } else { 0 }
            if (-not $grid -or $grid.Scrollable -le 0) { Say "scroll190 see all [$sc190Theme $($script:sc190Size)]: the grid fits the window (nothing to scroll)" }
            else {
                $needed = [int][Math]::Ceiling($grid.Scrollable / $sc190NotchPx)
                $target = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
                foreach ($dir in @('down', 'up')) {
                    $t = Sc190-Timed { [Sc190Ui]::WheelBurst($target, $script:ui.Id, $gx, $gy, $(if ($dir -eq 'down') { -120 } else { 120 }), $needed + 6, $sc190Gap) } 2500
                    $after = Sc190-PageScroll
                    $pct = if ($after) { [Math]::Round($after.Percent, 1) } else { -1 }
                    Sc190-Record "seeall-$dir" $t @{ notches = $needed + 6; needed = $needed; percent_after = $pct }
                    $endOk = if ($dir -eq 'down') { $pct -ge 99.5 } else { $pct -le 0.5 }
                    Sc190-Check "see all Categories: the wheel takes the grid $(if ($dir -eq 'down') { 'to its end' } else { 'back to its top' }) ($needed notches at $sc190NotchPx px)" $endOk "at $pct %; the grid $([int]$grid.Scrollable) px past its view"
                    Sc190-Check "see all Categories, the wheel $dir`: slow frames within $([int]$sc190Budget) ms together" ($t.Ok -and $t.SlowMs -le $sc190Budget) $(if ($t.Ok) { "$($t.Slow) frames over 50 ms, $([int]$t.SlowMs) ms together; longest $([int]$t.Longest) ms" } else { $t.Line })
                }
            }
            Key 0xA6; Start-Sleep -Milliseconds 2500   # Back to the rows
            $sc190Lists = @(Sc190-RowLists)
        }
    }

    if ('mixed' -in $sc190Parts) {
        # mixed input: a quick spin, then before its glide ends a key that moves the page (Down: the next row brought into view)
        # or a tile opened and Back; then one notch moves the page one notch from where that put it (a glide's target left over
        # from the spin would send it elsewhere)
        $x = [int]($sc190Page.Rect.Left + $sc190Page.Rect.Width / 2)
        $y = [int]($sc190Page.Rect.Top + $sc190Page.Rect.Height * 0.55)
        $target = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
        $sc190MixedCases = @(@{ Name = 'a key'; Keys = @(0x28); Wait = 1200 }, @{ Name = 'a tile opened and Back'; Keys = @(0x0D, 0xA6); Wait = 2500 })
        # (SC_MIXED_REPEAT: the two cases that many times, for a crash that comes one time in several)
        foreach ($case in @(1..$(if ($env:SC_MIXED_REPEAT) { [int]$env:SC_MIXED_REPEAT } else { 1 }) | ForEach-Object { $sc190MixedCases })) {
            $first = Sc190-FocusFirstTile
            [void][Sc190Ui]::WheelBurst($target, $script:ui.Id, $x, $y, -120, 3, 40)
            foreach ($k in $case.Keys) { Key $k; Start-Sleep -Milliseconds $case.Wait }
            $a = Sc190-PageScroll
            [void][Sc190Ui]::WheelBurst($target, $script:ui.Id, $x, $y, -120, 1, 40)
            Start-Sleep -Milliseconds 1200
            $b = Sc190-PageScroll
            $aPx = if ($a) { $a.Percent / 100 * $a.Scrollable } else { -1 }
            $bPx = if ($b) { $b.Percent / 100 * $b.Scrollable } else { -1 }
            $moved = $bPx - $aPx
            $atEnd = $b -and $b.Percent -ge 99.5
            Sc190-Check "mixed: after a quick spin and $($case.Name) before its glide ended, a notch moves the page one notch from there" ([bool]($a -and $b -and ([Math]::Abs($moved - $sc190NotchPx) -le 4 -or ($atEnd -and $moved -ge 0 -and $moved -le $sc190NotchPx + 4)))) `
                "from $([int]$aPx) px to $([int]$bPx) px ($([int]$moved) px; a notch is $sc190NotchPx)"
        }
        $sc190Lists = @(Sc190-RowLists)
    }

    if ('pgdn' -in $sc190Parts) {
        # Page Down and Page Up with the keyboard on a tile: each press moves the page 85 % of its view (or to its end), down then
        # back to the top (they moved it 0 px before 1.9's scroll fix). Not a gamepad's triggers: something on the page acts on them
        # already (with the page's handler beside it, a right trigger moved the page 279 px at 1280 x 720 and 984 at max)
        foreach ($pair in @(@{ Name = 'Page Down, Page Up'; Down = 0x22; Up = 0x21 })) {
            $first = Sc190-FocusFirstTile
            $a = Sc190-PageScroll
            $step = if ($a) { $a.Height * 0.85 } else { 0 }
            $presses = if ($a -and $step -gt 0) { [int][Math]::Ceiling($a.Scrollable / $step) } else { 3 }
            Key $pair.Down; Start-Sleep -Milliseconds 900
            $one = Sc190-PageScroll
            for ($i = 1; $i -lt $presses; $i++) { Key $pair.Down; Start-Sleep -Milliseconds 600 }
            Start-Sleep -Milliseconds 600
            $b = Sc190-PageScroll
            for ($i = 0; $i -lt $presses; $i++) { Key $pair.Up; Start-Sleep -Milliseconds 600 }
            Start-Sleep -Milliseconds 600
            $c = Sc190-PageScroll
            $first1 = if ($one) { $one.Percent / 100 * $one.Scrollable } else { -1 }
            $okOne = $one -and ([Math]::Abs($first1 - [Math]::Min($step, $one.Scrollable)) -le 4)
            Sc190-Check "keys: $($pair.Name) move the page most of a view each, to its end and back to its top" ([bool]($okOne -and $b -and $b.Percent -ge 99.5 -and $c -and $c.Percent -le 0.5)) `
                "the first press $([int]$first1) px (85 % of the view: $([int]$step)); after $presses at $(if ($b) { [Math]::Round($b.Percent, 1) }) %, after $presses back at $(if ($c) { [Math]::Round($c.Percent, 1) }) %"
        }
        $sc190Lists = @(Sc190-RowLists)
    }

    foreach ($sc190Part in @('keys', 'dpad')) {
        if ($sc190Part -notin $sc190Parts) { continue }
        $first = Sc190-FocusFirstTile
        $start = Sc190-FocusRow
        if (-not $first -or -not $start) { Sc190-Check "$sc190Part`: the first row's tile takes the keyboard" $false "row '$first'"; continue }
        $last = $sc190Lists[-1].Current.Name
        $presses = $sc190Lists.Count + 2
        $down = if ($sc190Part -eq 'keys') { 0x28 } else { 0xCC }
        $up = if ($sc190Part -eq 'keys') { 0x26 } else { 0xCB }
        $t = Sc190-Timed { for ($i = 0; $i -lt $presses; $i++) { Key $down; Start-Sleep -Milliseconds $sc190KeyGap } } 1500
        $at = Sc190-FocusRow
        Sc190-Record "$sc190Part-down" $t @{ presses = $presses; from = $start.Row; to = $(if ($at) { $at.Row } else { '' }); last = $last }
        Sc190-Check "$sc190Part`: Down from the first row reaches the last row ('$last'), its tile on screen" ([bool]($at -and $at.Row -eq $last -and $at.OnScreen)) "the keyboard in '$(if ($at) { $at.Row })' ($(if ($at -and $at.OnScreen) { 'on screen' } else { 'not on screen' })) after $presses presses from '$($start.Row)'; $sc190Said"
        Sc190-Check "$sc190Part down: slow frames within $([int]$sc190Budget) ms together" ($t.Ok -and $t.SlowMs -le $sc190Budget) $(if ($t.Ok) { "$($t.Slow) frames over 50 ms, $([int]$t.SlowMs) ms together; longest $([int]$t.Longest) ms; $($t.Frames) frames in $([int]$t.Ms) ms" } else { $t.Line })
        $ups = $sc190Lists.Count - 1
        $t = Sc190-Timed { for ($i = 0; $i -lt $ups; $i++) { Key $up; Start-Sleep -Milliseconds $sc190KeyGap } } 1500
        $at = Sc190-FocusRow
        Sc190-Record "$sc190Part-up" $t @{ presses = $ups; to = $(if ($at) { $at.Row } else { '' }) }
        Sc190-Check "$sc190Part`: Up from the last row reaches the first row ('$($start.Row)'), its tile on screen" ([bool]($at -and $at.Row -eq $start.Row -and $at.OnScreen)) "the keyboard in '$(if ($at) { $at.Row })' ($(if ($at -and $at.OnScreen) { 'on screen' } else { 'not on screen' })) after $ups presses"
        Sc190-Check "$sc190Part up: slow frames within $([int]$sc190Budget) ms together" ($t.Ok -and $t.SlowMs -le $sc190Budget) $(if ($t.Ok) { "$($t.Slow) frames over 50 ms, $([int]$t.SlowMs) ms together; longest $([int]$t.Longest) ms" } else { $t.Line })
    }
}

$sc190File = Join-Path $Out 'scroll190.json'
$sc190Rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $sc190File -Encoding utf8
Say "scroll190: rows in $sc190File"
