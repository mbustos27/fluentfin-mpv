# Seaglass scrolling, the wheel (acceptance tests 1 and 2): run with -Script home -Then offscreen\then-wheel.ps1.
# Posts WM_MOUSEWHEEL / WM_MOUSEHWHEEL to FluentFin's input window (the only window WinUI took a posted wheel in) over each row
# and reads which scroller moved (UI Automation ScrollPattern). Shift+wheel cannot be posted: WinUI reads the keyboard's real
# Shift state, not MK_SHIFT, so it is covered by the unit tests (RowPaging.TargetOf) and a hands-on check.
function Scrollers { @([OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id) | ForEach-Object { $p = $_ -split '\|'; [pscustomobject]@{ Rect = ($p[2] -split ',' | ForEach-Object { [double]$_ }); H = [double]$p[3]; V = [double]$p[4]; HScroll = ($p[5] -eq 'True'); VScroll = ($p[6] -eq 'True') } }) }
function Page-State { $best = $null; foreach ($s in @(Scrollers)) { if ($s.VScroll -and (-not $best -or $s.Rect[2] * $s.Rect[3] -gt $best.Rect[2] * $best.Rect[3])) { $best = $s } }; $best }
function Rows-Now { @(Scrollers | Where-Object { $_.HScroll } | Where-Object { $_.Rect[3] -gt 100 } | Sort-Object { $_.Rect[1] }) }
function Wheel-At([int]$X, [int]$Y, [int]$Delta, [bool]$Horizontal) {
    [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, $X, $Y, $Delta, $Horizontal, $false)
    Start-Sleep -Milliseconds 900
}
# 1.2.0: a row by its name (the RowList inside its scroller is named after it), the page scrolled until the row is wholly in
# view: a clipped row's edge is the page's edge, so strictly inside means all of it. The rows were picked by their place on
# screen after scrolling to the top, which in a 1080-pixel window (two rows under the hero) never found the third.
function Row-Named([string]$Name) {
    foreach ($pct in 0..20) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct * 5); Start-Sleep -Milliseconds 450
        $list = Find-Ui -Name $Name -Type 'List'
        $page = Page-State
        if (-not $list -or -not $page) { continue }
        $lr = [OffscreenUi]::RectOf($list)
        if ($lr.Y -le $page.Rect[1] + 1 -or $lr.Y + $lr.Height -ge $page.Rect[1] + $page.Rect[3] - 1) { continue }
        $row = Rows-Now | Where-Object { [math]::Abs($_.Rect[1] - $lr.Y) -lt 3 } | Select-Object -First 1
        if ($row) { return $row }
    }
}
$names = 'Continue Watching', 'Next Up', 'New in Movies'
foreach ($rowName in $names) {
    $r = Row-Named $rowName
    if (-not $r) { Check "wheel: row $rowName found" $false; continue }
    $x = [int]($r.Rect[0] + $r.Rect[2] / 2); $y = [int]($r.Rect[1] + $r.Rect[3] / 2)
    $page0 = (Page-State).V; $rowSum0 = (@(Rows-Now) | ForEach-Object { $_.H } | Measure-Object -Sum).Sum
    Wheel-At $x $y -120 $false
    $page1 = (Page-State).V
    $rowsNow = Rows-Now
    $rowSame = ((@($rowsNow) | ForEach-Object { $_.H } | Measure-Object -Sum).Sum) -le $rowSum0 + 0.5   # no row moved further (rows may scroll out of the list)
    Check "wheel: the vertical wheel over $rowName scrolls the page" ($page1 -gt $page0 + 1) "page $page0 -> $page1"
    Check "wheel: ... and not the row" $rowSame (($rowsNow | ForEach-Object { "{0:0.0}" -f $_.H }) -join ", ")
    # tilt right over the same row (the page moved: bring it back into view)
    $r = Row-Named $rowName
    if (-not $r) { Check "wheel: row $rowName found again" $false; continue }
    $x = [int]($r.Rect[0] + $r.Rect[2] / 2); $y = [int]($r.Rect[1] + $r.Rect[3] / 2)
    $rowBefore = $r.H; $pageB = (Page-State).V
    Wheel-At $x $y 120 $true
    $after = (Rows-Now | Where-Object { [math]::Abs($_.Rect[1] - $r.Rect[1]) -lt 2 } | Select-Object -First 1).H
    Check "wheel: the tilt wheel over $rowName moves the row" ($after -gt $rowBefore + 0.5) "row $rowBefore -> $after"
    Check "wheel: ... and not the page" ([math]::Abs((Page-State).V - $pageB) -lt 0.5)
    # the row's end: scroll it all the way, then the vertical wheel must still reach the page
    for ($k = 0; $k -lt 14; $k++) { Wheel-At $x $y 120 $true }
    $end = (Rows-Now | Where-Object { [math]::Abs($_.Rect[1] - $r.Rect[1]) -lt 2 } | Select-Object -First 1).H
    $pageE = (Page-State).V
    Wheel-At $x $y -120 $false
    Check "wheel: at the end of $rowName the vertical wheel still scrolls the page" ((Page-State).V -gt $pageE + 0.5) "row at $end %, page $pageE -> $((Page-State).V)"
}
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 700
