# 1.5 fixes, T5 (the top bar review): Home's tab wears its lit underline, not the focus box as well, when no key was pressed.
#   $env:FAKESERVER_EXTRA_VIEWS='4'; $env:FAKESERVER_COLLECTIONS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 -Script wait:1 -NoA11y -Then offscreen\then-t5-launch.ps1     (or -Script relaunch: a start with no input)
# The sign-in and Home steps send no key (UI Automation only), so this is a launch the user has not typed into yet, from the
# Start menu (keyboard) or a click (mouse). With 9 libraries the window is made narrow and wide again, so the tabs fold and
# unfold: each time the strip rebuilds its tabs, which gave Home's tab back as keyboard focus (the box) before 1.5. Then a
# page change and back (Movies, Home) through UI Automation: Home kept its box after a page change too (the coordinator,
# from the updater stream's runs). Measured
# as drawn (PrintWindow): the focus box is a white ring round the tab, so its top and bottom edges are rows of near-white
# pixels across it; the underline is the accent's. Then Tab: the keyboard in use, the box may show where the focus went.
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'chord-keys.ps1')

function T5-Ring([string]$Shot, $Element) {
    $t5Win = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd).Current.BoundingRectangle
    $t5R = [OffscreenUi]::RectOf($Element)
    $t5Bmp = [System.Drawing.Bitmap]::FromFile($Shot)
    try {
        $sx = $t5Bmp.Width / $t5Win.Width; $sy = $t5Bmp.Height / $t5Win.Height
        function T5-White($x, $y) { $p = $t5Bmp.GetPixel($x, $y); $p.R -gt 200 -and $p.G -gt 200 -and $p.B -gt 200 }
        # the window's box in UI Automation has its frame in it and the shot may not: a band of rows round each edge, and the
        # tab's middle only (text rows reach about a third of its width near-white, the ring's rows all of it). A row white
        # outside the tab too (the shot's own white top edge) is not the ring.
        $x0 = [int](($t5R.Left - $t5Win.Left) * $sx) + 12; $x1 = [int](($t5R.Right - $t5Win.Left) * $sx) - 12
        $best = 0.0
        foreach ($edge in @($t5R.Top, $t5R.Bottom)) {
            $yc = [int](($edge - $t5Win.Top) * $sy)
            foreach ($y in ($yc - 14)..($yc + 14)) {
                if ($y -lt 0 -or $y -ge $t5Bmp.Height) { continue }
                $out = 0; $outN = 0
                foreach ($x in @(($x0 - 40)..($x0 - 28)) + @(($x1 + 28)..($x1 + 40))) {
                    if ($x -lt 0 -or $x -ge $t5Bmp.Width) { continue }
                    $outN++; if (T5-White $x $y) { $out++ }
                }
                if ($outN -gt 0 -and $out / $outN -gt 0.2) { continue }
                $bright = 0; $n = 0
                for ($x = [Math]::Max(0, $x0); $x -le [Math]::Min($t5Bmp.Width - 1, $x1); $x++) {
                    $n++; if (T5-White $x $y) { $bright++ }
                }
                if ($n -gt 0) { $best = [Math]::Max($best, $bright / $n) }
            }
        }
        $best
    }
    finally { $t5Bmp.Dispose() }
}

$t5Theme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'default' }
$t5Home = Wait-Ui -Name 'Home' -Type 'TabItem' -Seconds 15
if (-not $t5Home) { Check "T5 ($t5Theme): Home's tab is there" $false; return }
# measured at once: the shot, then Home's tab looked up again (a rebuild replaces the element) and its ring read
function T5-Measure([string]$When) {
    Shot "t5-$t5Theme-$When"
    $t5Tab = Wait-Ui -Name 'Home' -Type 'TabItem' -Seconds 5
    $t5Ring = if ($t5Tab -and -not [OffscreenUi]::RectOf($t5Tab).IsEmpty) { T5-Ring $shots["t5-$t5Theme-$When"] $t5Tab } else { -1 }
    $t5Focus = CK-Focus
    Say ("T5 ({0}, {1}): the focus is {2}; Home's ring rows are {3:P0} near-white" -f $t5Theme, $When, $t5Focus, $t5Ring)
    Check "T5 ($t5Theme, $When): no key pressed yet, Home's tab shows no focus box (only its underline)" ($t5Ring -ge 0 -and $t5Ring -lt 0.5) ("{0:P0} of a ring row near-white; focus {1}" -f $t5Ring, $t5Focus)
    $timings.t5_launch += [ordered]@{ theme = $t5Theme; when = $When; ring = [Math]::Round($t5Ring, 3); focus = $t5Focus }
}
$timings.t5_launch = @()
# 1. as soon as the libraries' tabs are in (the strip rebuilt for them): with -Script wait:1 this is the launch itself
# 1.6 topbar: the Diary is in the account menu now; the favourites' tabs (Movies) come with the libraries
[void](Wait-Until { if ((Find-Ui -Name 'Libraries' -Type 'TabItem') -and (Find-Ui -Name 'Movies' -Type 'TabItem')) { $true } } -Seconds 15 -PollMs 100)
Start-Sleep -Milliseconds 1500
T5-Measure 'launch'
# 2. the window made narrow and wide again: the tabs fold and unfold (a rebuild each time)
foreach ($t5W in @(1100, 1920)) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $t5W, 1080)
    Start-Sleep -Milliseconds 1500
}
T5-Measure 'resized'
# 3. a page change and back, through UI Automation (no key, no click): another tab's page, then Home's again
foreach ($t5Go in @('Movies', 'Home')) {
    $t5Tab = Wait-Ui -Name $t5Go -Type 'TabItem' -Seconds 5
    if ($t5Tab) { [void](Invoke-Element $t5Tab) }
    Start-Sleep -Milliseconds 2500
    T5-Measure "page-$($t5Go.ToLowerInvariant())"
}
# 4. a key first (Esc: nothing on Home), then the same page change: the updater stream's case (a dialog closed with Esc, then
# pages changed); the focus the page change puts on a tab is still the window's, not the user's
CK-Key 'Esc'
# longer than TopTabStrip.KeyCausedPageMs: the Esc is not what changes the page
Start-Sleep -Milliseconds 2500
foreach ($t5Go in @('Movies', 'Home')) {
    $t5Tab = Wait-Ui -Name $t5Go -Type 'TabItem' -Seconds 5
    if ($t5Tab) { [void](Invoke-Element $t5Tab) }
    Start-Sleep -Milliseconds 2500
}
T5-Measure 'key-then-page-home'

# the way into the keyboard: Tab moves the keyboard and the window keeps answering (the box may now show where it went)
CK-Key 'Tab'
Start-Sleep -Milliseconds 600
Shot "t5-$t5Theme-after-tab"
Say ("T5 ({0}): after Tab the focus is {1}" -f $t5Theme, (CK-Focus))
Check "T5 ($t5Theme): Tab after launch puts the keyboard somewhere" ((CK-Focus) -ne 'nothing')
