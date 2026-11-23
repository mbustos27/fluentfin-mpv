# Nixie 1.7.3, stream C (BH-171-m4): the Collections page while the library index's first read fills it in, and after: the
# rows stay where they are when the reading line goes (1.7.1 moved every row up 40 px under the keyboard and the pointer).
# From the 1.7.1 bug hunt's then-bh171-rows.ps1 (its repro). Run on a first visit (no saved index) with slow index pages:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:FAKESERVER_BIG_LIBRARY='12000'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   $env:ROWS_MODE='keys'   # or 'mouse'
#   run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-173-rows.ps1 `
#       -LatencyRule '^/Items\?.*(ProductionLocations|[Ff]ields=People(&|$))=3000'
# keys: arrive, then Right twice (the keyboard on a tile of the server's row) and hold still while rows fill in. mouse: arrive,
# then the wheel down once over the page and hold still. Every 500 ms: the reading line, the keyboard's element (and where it
# is), and the rows' top (the highest row's scroll area). Checks: the arrival focus is on a tile, the keyboard stays where the
# user put it and doesn't move on the screen, the rows' top doesn't move (while rows fill in, and when the line goes), the
# line counts on, then goes, leaving nothing for Narrator. Dot-sourced by drive.ps1: every name here starts with rw.

$rwTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'rows173' }
$rwMode = if ($env:ROWS_MODE) { $env:ROWS_MODE } else { 'keys' }
function Rw-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function Rw-FocusText { $f = Rw-Focus; if ($f) { "$($f.type) '$($f.name)' at $(@($f.rect) -join ',')" } else { 'none' } }
function Rw-Line { $l = Find-Ui -Name 'Nixie is reading your libraries' -Contains -OffscreenToo; if ($l) { $l.Current.Name } else { '' } }
# the highest row's top: the rows' scroll areas (ShelfRow's horizontal ScrollViewer), not the page's own (no class name)
function Rw-RowsTop {
    $ys = @([OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id) | ForEach-Object {
            $p = $_ -split '\|'
            if ($p.Count -ge 3 -and $p[1] -eq 'ScrollViewer') { [int](($p[2] -split ',')[1]) }
        } | Where-Object { $_ -ge 48 })
    if ($ys.Count) { ($ys | Measure-Object -Minimum).Minimum } else { $null }
}

$rwT0 = Get-Date
Open-Nav 'Collections'
$rwTitle = Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 30
Check "rows173 ($rwTag/$rwMode): the Collections tab opens the page" ([bool]$rwTitle) ''
# (waited for: the tab's title can be found before the page's rows and line are in, under load)
$rwLine0 = Wait-Until { $l = Rw-Line; if ($l) { $l } } -Seconds 15 -PollMs 200
if (-not $rwLine0) { $rwLine0 = '' }
Check "rows173 ($rwTag/$rwMode): the reading line shows during the first read" ($rwLine0 -ne '') "'$rwLine0' after $([int]((Get-Date) - $rwT0).TotalMilliseconds) ms"
# the arrival focus comes once the page has loaded (its server rows: 2 s under load), so it is waited for, up to 10 s
$rwArrive = Wait-Until { $f = Rw-FocusText; if ($f -notmatch "'Back'|TabItem|TopTab" -and $f -ne 'none' -and $f -match 'ListItem|Button') { $f } } -Seconds 10 -PollMs 200
if (-not $rwArrive) { $rwArrive = Rw-FocusText }
Start-Sleep -Milliseconds 500
Check "rows173 ($rwTag/$rwMode): arrival focus on a tile, not Back" ($rwArrive -notmatch "'Back'" -and $rwArrive -ne 'none' -and $rwArrive -match 'ListItem|Button') $rwArrive
Shot "$rwTag-$rwMode-arrive"

if ($rwMode -eq 'keys') {
    Key 0x27; Start-Sleep -Milliseconds 400; Key 0x27; Start-Sleep -Milliseconds 600
}
else {
    $r = [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id))
    [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height * 0.6), -120, $false, $false)
    Start-Sleep -Milliseconds 900
}
$rwChosen = Rw-FocusText
$rwTop0 = Rw-RowsTop
Say "rows173: chosen $rwChosen; rows' top $rwTop0"

$rwLines = [System.Collections.Generic.List[string]]::new()
$rwMoves = [System.Collections.Generic.List[string]]::new()
$rwTops = [System.Collections.Generic.List[string]]::new()
$rwGoneAt = $null
$rwEnd = (Get-Date).AddSeconds(240)
$rwMid = $false
while ((Get-Date) -lt $rwEnd) {
    $l = Rw-Line
    if ($l -and ($rwLines.Count -eq 0 -or $rwLines[$rwLines.Count - 1] -ne $l)) { $rwLines.Add($l); Say "rows173 line: $l" }
    $f = Rw-FocusText
    if ($f -ne $rwChosen -and ($rwMoves.Count -eq 0 -or $rwMoves[$rwMoves.Count - 1] -notlike "* $f")) { $rwMoves.Add("$([int]((Get-Date) - $rwT0).TotalSeconds)s $f"); Say "rows173 keyboard moved: $f" }
    $t = Rw-RowsTop
    if ($t -ne $rwTop0 -and ($rwTops.Count -eq 0 -or $rwTops[$rwTops.Count - 1] -notlike "* $t")) { $rwTops.Add("$([int]((Get-Date) - $rwT0).TotalSeconds)s $t"); Say "rows173 rows' top: $t" }
    if (-not $rwMid -and $rwLines.Count -ge 2) { Shot "$rwTag-$rwMode-filling"; $rwMid = $true }
    if (-not $l -and -not $rwGoneAt) { $rwGoneAt = Get-Date; Say "rows173: the reading line went after $([int]($rwGoneAt - $rwT0).TotalSeconds) s" }
    if ($rwGoneAt -and ((Get-Date) - $rwGoneAt).TotalSeconds -gt 8) { break }
    Start-Sleep -Milliseconds 500
}
Shot "$rwTag-$rwMode-end"
Check "rows173 ($rwTag/$rwMode): Made by Nixie rows arrived" ([bool](Find-Ui -Name 'Made by Nixie' -Contains -OffscreenToo)) ''
Check "rows173 ($rwTag/$rwMode): the reading line moved (more than one count)" ($rwLines.Count -ge 2) "$($rwLines.Count) counts"
Check "rows173 ($rwTag/$rwMode): the reading line went" ([bool]$rwGoneAt) ''
Check "rows173 ($rwTag/$rwMode): nothing of the line is left for Narrator" (-not (Find-Ui -Name 'Nixie is reading your libraries' -Contains -OffscreenToo)) ''
Check "rows173 ($rwTag/$rwMode): the keyboard stayed on the user's tile, in the same place, while rows filled in and the line went" ($rwMoves.Count -eq 0) "chosen $rwChosen; then: $($rwMoves -join ' ; ')"
Check "rows173 ($rwTag/$rwMode): the rows didn't move while they filled in and the line went" ($null -ne $rwTop0 -and $rwTops.Count -eq 0) "top $rwTop0; then: $($rwTops -join ' ; ')"
# after the fill-in: a key still moves along the row (the focus is live, not on a removed tile)
$rwBefore = Rw-FocusText
Key 0x27; Start-Sleep -Milliseconds 600
$rwAfter = Rw-FocusText
Check "rows173 ($rwTag/$rwMode): Right after the fill-in moves the focus along a row" ($rwAfter -ne $rwBefore -and $rwAfter -notmatch "'Back'") "$rwBefore -> $rwAfter"
