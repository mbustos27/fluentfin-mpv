# Nixie 1.7.5 (review Major 3): Back to a series page must not keep the page it left alive. Its kept view model outlives the
# page; handlers the page (or a control on it) added to it held the page, one more for every Back. Run per theme with the app
# counting its live pages after each navigation (NIXIE_HARNESS_LEAKS=1: a full collection, then "[leak] live pages: ..." in
# its log; a release build never reads it):
#   $env:NIXIE_HARNESS_LEAKS='1'; $env:FAKESERVER_BIG_SERIES='24x13'; $env:NIXIE_LAYOUT='<Classic|DriveIn|...>'
#   [$env:LK_ROUNDS='50'] [$env:LK_MOST='2']
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-175-leak.ps1
# The big series' page is opened, then LK_ROUNDS times: its S1 E5 opened (the row's menu, Episode details), Back. Then the
# app's last "[leak]" line: the series pages still alive (the one shown, at most LK_MOST in all) and the episode pages.
# Uses then-174-back.ps1's helpers (BK_PAGES=none: none of its cases run). Rows in <Out>\leak175\leak175.json.

$env:BK_PAGES = 'none'
. (Join-Path $PSScriptRoot 'then-174-back.ps1')
$lkRounds = if ($env:LK_ROUNDS) { [int]$env:LK_ROUNDS } else { 50 }
$lkMost = if ($env:LK_MOST) { [int]$env:LK_MOST } else { 2 }
$lkDir = Join-Path $Out 'leak175'
New-Item -ItemType Directory -Force $lkDir | Out-Null

function Lk-LastLine {
    $file = Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    $hit = Select-String -LiteralPath $file.FullName -Pattern '\[leak\] live pages: (.*)$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($hit) { $hit.Matches[0].Groups[1].Value.Trim() } else { $null }
}
function Lk-Count([string]$Line, [string]$Pattern) {
    $n = 0
    foreach ($part in @("$Line" -split ',\s*')) { if ($part -match "^(\w*$Pattern)\s+(\d+)$") { $n += [int]$Matches[2] } }
    $n
}

$lkRows = New-Object System.Collections.Generic.List[object]
$lkDone = 0
try {
    Bk-OpenSeries
    for ($i = 1; $i -le $lkRounds; $i++) {
        [void](Bk-EpisodeDetails 'S1 E5 ')
        Start-Sleep -Milliseconds 700
        Key 0xA6
        if (-not (Wait-Until { Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo } -Seconds 20 -PollMs 200)) { throw "round ${i}: Back did not show the series page" }
        Start-Sleep -Milliseconds 500
        $lkDone = $i
        if ($i % 10 -eq 0) { Say ("leak175 ({0}): {1} rounds, {2}" -f $bkTag, $i, (Lk-LastLine)) }
    }
} catch {
    Say "leak175 ($bkTag): stopped: $($_.Exception.Message)"
}
$lkLine = Lk-LastLine
$lkSeries = Lk-Count $lkLine 'SeriesPage'
$lkEpisodes = Lk-Count $lkLine 'EpisodePage'
$lkOk = [bool]($lkDone -eq $lkRounds -and $lkLine -and $lkSeries -ge 1 -and $lkSeries -le $lkMost)
$lkDetail = "{0} of {1} rounds; series pages alive {2} (at most {3}), episode pages {4}; the app's last line: {5}" -f $lkDone, $lkRounds, $lkSeries, $lkMost, $lkEpisodes, $(if ($lkLine) { $lkLine } else { 'none (NIXIE_HARNESS_LEAKS=1?)' })
Check "leak175 ($bkTag): series <-> episode" $lkOk $lkDetail
$lkRows.Add([pscustomobject][ordered]@{ theme = $bkTheme; rounds = $lkDone; series = $lkSeries; episodes = $lkEpisodes; line = $lkLine; ok = $lkOk })
[IO.File]::WriteAllText((Join-Path $lkDir 'leak175.json'), (ConvertTo-Json -InputObject @($lkRows.ToArray()) -Depth 3), [Text.UTF8Encoding]::new($false))
