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
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 800
$names = 'Continue Watching', 'Next Up', 'New in Movies'
for ($i = 0; $i -lt 3; $i++) {
    # the i-th row by its place on screen; re-read after every move: the page scrolls
    $rows = Rows-Now
    if ($rows.Count -le $i) { Check "wheel: row $i found" $false; continue }
    $r = $rows[$i]; $x = [int]($r.Rect[0] + $r.Rect[2] / 2); $y = [int]($r.Rect[1] + $r.Rect[3] / 2)
    $page0 = (Page-State).V; $rowSum0 = (@(Rows-Now) | ForEach-Object { $_.H } | Measure-Object -Sum).Sum
    Wheel-At $x $y -120 $false
    $page1 = (Page-State).V
    $rowsNow = Rows-Now
    $rowSame = ((@($rowsNow) | ForEach-Object { $_.H } | Measure-Object -Sum).Sum) -le $rowSum0 + 0.5   # no row moved further (rows may scroll out of the list)
    Check "wheel: the vertical wheel over $($names[$i]) scrolls the page" ($page1 -gt $page0 + 1) "page $page0 -> $page1"
    Check "wheel: ... and not the row" $rowSame (($rowsNow | ForEach-Object { "{0:0.0}" -f $_.H }) -join ", ")
    # tilt right over the same place (the page moved: find the row again)
    $r = (Rows-Now)[$i]; if (-not $r) { $r = (Rows-Now)[0] }
    $x = [int]($r.Rect[0] + $r.Rect[2] / 2); $y = [int]($r.Rect[1] + $r.Rect[3] / 2)
    $rowBefore = $r.H; $pageB = (Page-State).V
    Wheel-At $x $y 120 $true
    $after = (Rows-Now | Where-Object { [math]::Abs($_.Rect[1] - $r.Rect[1]) -lt 2 } | Select-Object -First 1).H
    Check "wheel: the tilt wheel over $($names[$i]) moves the row" ($after -gt $rowBefore + 0.5) "row $rowBefore -> $after"
    Check "wheel: ... and not the page" ([math]::Abs((Page-State).V - $pageB) -lt 0.5)
    # the row's end: scroll it all the way, then the vertical wheel must still reach the page
    for ($k = 0; $k -lt 14; $k++) { Wheel-At $x $y 120 $true }
    $end = (Rows-Now | Where-Object { [math]::Abs($_.Rect[1] - $r.Rect[1]) -lt 2 } | Select-Object -First 1).H
    $pageE = (Page-State).V
    Wheel-At $x $y -120 $false
    Check "wheel: at the end of $($names[$i]) the vertical wheel still scrolls the page" ((Page-State).V -gt $pageE + 0.5) "row at $end %, page $pageE -> $((Page-State).V)"
    # and back to the top of the page for the next row
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
    Start-Sleep -Milliseconds 700
}
