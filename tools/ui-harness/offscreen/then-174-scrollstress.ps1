# Nixie 1.7.4 integration: scrolls fast where 1.7.3's base build crashed once (the rows stream's run g1-pre-DriveIn, 2026-11-23
# 00:16: APPCRASH 0xc000027b in Microsoft.UI.Xaml.dll, a stowed E_UNEXPECTED, while then-174-rows.ps1 scrolled Drive-in's Movies
# library through UI Automation around its "Jump to a letter" strip; it didn't recur on the rows' fix build). Run with:
#   $env:NIXIE_LAYOUT='DriveIn'; $env:FAKESERVER_BIG_SERIES='24x13'; $env:FAKESERVER_COLLECTIONS='1'
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-174-scrollstress.ps1
# Maximised (2560 x 1392 on this PC's monitor), SS_ROUNDS rounds (3) on the Movies library, then on the big series' Season 1:
# plain-wheel bursts down and up (6 notches a post, no pause but the posted moves'), UI Automation jumps of the page's scroll
# (random percents, 20 ms apart), Page Down/End/Home, tilt-wheel and plain-wheel bursts and the pointer hook's drags 40 ms apart
# (they overlap) on the A to Z strip and the season's rows, and on the season page the season tubes switched while the page
# glides. After each part: Nixie still running. At the end: the [ui] failures and first-chance exceptions logged since the stress
# began, then a close from the season page through then-173-close.ps1's checks (exit code 0). Dot-sourced by drive.ps1: every
# name here starts with ss.

$ssRounds = if ($env:SS_ROUNDS) { [int]$env:SS_ROUNDS } else { 3 }
$ssBs = $fixture.big_series
$ssBegan = [DateTimeOffset]::Now
$ssParts = 0
$ssJumpErrors = 0
$ssRandom = [Random]::new(174)

if (-not ('SsUi' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SsUi {
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  static void Own(IntPtr main, int pid) { uint p; GetWindowThreadProcessId(main, out p); if (p != (uint)pid) throw new InvalidOperationException("not FluentFin's window"); }
  // the dev build's pointer hook (HarnessPointer): kind 3 a drag from x to toX on the same line; x, y on the screen
  public static bool Drag(IntPtr main, int pid, int x, int y, int toX) {
    Own(main, pid);
    long w = 3L | ((long)(uint)(toX & 0xFFFF) << 16);
    return PostMessage(main, RegisterWindowMessage("NixieHarnessPointer"), (IntPtr)w, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
  public static void Maximize(IntPtr main, int pid) { Own(main, pid); ShowWindow(main, 3); }
}
"@
}

function Ss-Alive([string]$After) {
    $script:ssParts++
    $alive = -not $script:ui.HasExited
    if (-not $alive) { Check "scroll stress: Nixie still running after $After" $false "exit code 0x$('{0:X8}' -f $script:ui.ExitCode)" }
    else { Say "scroll stress: alive after $After" }
    $alive
}
function Ss-Window { [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id)) }
function Ss-Rect([string]$Name) {
    $e = Find-Ui -Name $Name -OffscreenToo
    if (-not $e) { return $null }
    $r = [OffscreenUi]::RectOf($e)
    if ($r.IsEmpty -or $r.Width -lt 40 -or $r.Height -lt 20) { return $null }
    $r
}
# plain or tilt wheel posts at x, y with no pause between them (each post moves the pointer there first: 60 ms)
function Ss-Burst([int]$X, [int]$Y, [int]$Posts, [int]$Delta, [bool]$Horizontal = $false) {
    for ($i = 0; $i -lt $Posts; $i++) { [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, $X, $Y, $Delta, $Horizontal, $false) }
}
# the page's scroll through UI Automation (what the rows stream's run did when it crashed), random percents
function Ss-Jumps([int]$Count) {
    for ($i = 0; $i -lt $Count; $i++) {
        try { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, [double]$ssRandom.Next(0, 101)) } catch { $script:ssJumpErrors++ }
        Start-Sleep -Milliseconds 20
    }
}
# the pointer hook's drags along a row, 40 ms apart: each takes 240 ms, so they overlap
function Ss-Drags($Rect, [int]$Count) {
    if (-not $Rect) { return }
    $y = [int]($Rect.Y + $Rect.Height / 2)
    for ($i = 0; $i -lt $Count; $i++) {
        $from = if ($i % 2) { [int]($Rect.X + 60) } else { [int]($Rect.X + $Rect.Width - 60) }
        $to = if ($i % 2) { [int]($Rect.X + $Rect.Width - 60) } else { [int]($Rect.X + 60) }
        try { [void][SsUi]::Drag($script:hwnd, $script:ui.Id, $from, $y, $to) } catch { }
        Start-Sleep -Milliseconds 40
    }
}
# a row of the page: bursts over it (tilt both ways, plain down and up: the page's, then the row's at the page's end) and drags
function Ss-Row([string]$Name) {
    $r = Ss-Rect $Name
    if (-not $r) { Say "scroll stress: no '$Name' in view"; return $true }
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
    Ss-Burst $x $y 20 -720 $true
    Ss-Burst $x $y 20 720 $true
    Ss-Burst $x $y 25 -720
    Ss-Burst $x $y 25 720
    Ss-Drags (Ss-Rect $Name) 12
    Ss-Alive "the row '$Name'"
}
function Ss-Page([string]$What) {
    $w = Ss-Window
    $cx = [int]($w.X + $w.Width / 2); $cy = [int]($w.Y + $w.Height * 0.6)
    Ss-Burst $cx $cy 40 -720
    Ss-Burst $cx $cy 40 720
    if (-not (Ss-Alive "$What's wheel bursts")) { return $false }
    Ss-Jumps 30
    if (-not (Ss-Alive "$What's UI Automation jumps")) { return $false }
    # a burst and jumps together, and the page keys
    Ss-Burst $cx $cy 15 -720; Ss-Jumps 10; Ss-Burst $cx $cy 15 720
    foreach ($vk in @(0x22, 0x22, 0x22, 0x22, 0x23, 0x24, 0x22, 0x23, 0x24)) { Key $vk; Start-Sleep -Milliseconds 30 }
    Ss-Alive "$What's mixed bursts and keys"
}

# 1.7.5 (fx3): SS_SIZE=<w>x<h> runs it in a window of that size (1280x720); unset or 'max', maximised (2560 x 1392 here)
$ssSize = if ($env:SS_SIZE -and $env:SS_SIZE -ne 'max') { @($env:SS_SIZE -split 'x' | ForEach-Object { [int]$_ }) } else { $null }
Say "scroll stress: Drive-in? NIXIE_LAYOUT=$env:NIXIE_LAYOUT; $ssRounds rounds; $(if ($ssSize) { "$($ssSize[0]) x $($ssSize[1])" } else { 'maximised' })"
if ($ssSize) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $ssSize[0], $ssSize[1]) } else { [SsUi]::Maximize($script:hwnd, $script:ui.Id) }
Start-Sleep -Milliseconds 1500
$ssOk = $true

# ---- the Movies library -----------------------------------------------------------------------------------------------------
Open-Library $fixture.movie.library
Start-Sleep -Seconds 3
Shot 'scrollstress-library'
for ($round = 1; $ssOk -and $round -le $ssRounds; $round++) {
    $ssOk = (Ss-Page "the library (round $round)") -and (Ss-Row 'Jump to a letter')
}

# ---- a long show's season (24 seasons of 13 episodes) -----------------------------------------------------------------------
if ($ssOk) {
    Open-Library $ssBs.library
    Start-Sleep -Milliseconds 1200
    $ssCard = Wait-Until { Find-MediaCard $ssBs.name } -Seconds 30 -PollMs 200
    if ($ssCard) {
        [void](Invoke-Element $ssCard)
        $ssFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 4 -PollMs 150
        if ($ssFull) { [void](Invoke-Element $ssFull) }
    }
    $ssLink = Wait-Until { Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo } -Seconds 20 -PollMs 200
    Check 'scroll stress: the big series page opened' ([bool]$ssLink)
    if ($ssLink) {
        try { [void](Invoke-Element $ssLink) } catch { Start-Sleep -Milliseconds 500; $ssLink = Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo; if ($ssLink) { [void](Invoke-Element $ssLink) } }
        $ssSeason = [bool](Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Mark Season 1 of ', '') } -Seconds 20 -PollMs 200)
        Check 'scroll stress: Season 1 opened' $ssSeason
        Start-Sleep -Seconds 3
        Shot 'scrollstress-season'
        for ($round = 1; $ssOk -and $round -le $ssRounds; $round++) {
            $ssOk = Ss-Page "the season page (round $round)"
            foreach ($row in @('Seasons', 'Episodes', 'Cast & crew')) { if ($ssOk) { $ssOk = Ss-Row $row } }
            if (-not $ssOk) { break }
            # the season tubes switched while the page still glides from a burst
            $w = Ss-Window
            for ($s = 2; $s -le 6; $s++) {
                Ss-Burst ([int]($w.X + $w.Width / 2)) ([int]($w.Y + $w.Height * 0.6)) 4 -720
                $tube = Find-Ui -Name "Season $s" -Type 'Button' -OffscreenToo
                if (-not $tube) { $tube = Find-Ui -Name "Season $s" -Contains -OffscreenToo }
                if ($tube) { try { [void](Invoke-Element $tube) } catch { } }
                Start-Sleep -Milliseconds 120
            }
            $ssOk = Ss-Alive "the season tubes switched while the page glided (round $round)"
        }
    }
}

# ---- what the app logged meanwhile --------------------------------------------------------------------------------------------
$ssLines = [System.Collections.Generic.List[string]]::new()
foreach ($ssFile in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)) {
    $ssAfter = $false
    foreach ($ssLine in @(Get-Content -LiteralPath $ssFile.FullName -Encoding utf8)) {
        if ($ssLine -match '^(\S+ \S+ [+-]\d\d:\d\d) \[') {
            $ssWhen = [DateTimeOffset]::MinValue
            $ssAfter = [DateTimeOffset]::TryParse($Matches[1], [ref]$ssWhen) -and $ssWhen -ge $ssBegan
        }
        if ($ssAfter) { $ssLines.Add($ssLine) }
    }
}
$ssUiFailed = @($ssLines | Where-Object { $_ -match '\[ui\] .* failed' })
$ssFirst = @($ssLines | Where-Object { $_ -match '\[first-chance\]' })
foreach ($l in ($ssFirst | Select-Object -First 6)) { Say "app: $l" }
# 1.7.5 (fx3): each first-chance entry whole (its stack and the last things done in the window), the first 3, so the run's
# own log says what threw where without the app's data folder
$ssShown = 0
for ($ssI = 0; $ssI -lt $ssLines.Count -and $ssShown -lt 3; $ssI++) {
    if ($ssLines[$ssI] -notmatch '\[first-chance\]') { continue }
    $ssShown++
    # the first frames of its stack, then the line with the last things done in the window
    for ($ssJ = $ssI + 1; $ssJ -lt $ssLines.Count -and $ssLines[$ssJ] -notmatch '^\S+ \S+ [+-]\d\d:\d\d \['; $ssJ++) {
        if ($ssJ -le $ssI + 10 -or $ssLines[$ssJ] -match '^last in the window: ') { Say "app:   $($ssLines[$ssJ].Trim())" }
    }
}
# 1.7.5 (fx3): what reached the app's last handlers (XAML's unhandled exception, an unhandled error), with the last UI action
foreach ($l in @($ssLines | Where-Object { $_ -match '\[crash-trail\] (?!listening|UnhandledErrorDetected isn)' } | Select-Object -First 4)) { Say "app: $l" }
Say "scroll stress: $ssParts parts; $ssJumpErrors UI Automation jumps refused; $($ssFirst.Count) first-chance lines; $($ssUiFailed.Count) [ui] failures"
Check 'scroll stress: Nixie never crashed' $ssOk "$ssParts parts"
Check 'scroll stress: no queued UI work failed ([ui] ... failed)' ($ssUiFailed.Count -eq 0) $(if ($ssUiFailed) { ($ssUiFailed | Select-Object -First 2) -join ' | ' } else { 'none' })

# ---- and a clean close from the season page -------------------------------------------------------------------------------------
if ($ssOk) {
    $cxFrom = "the season page after the scroll stress"
    . (Join-Path $PSScriptRoot 'then-173-close.ps1')
}
