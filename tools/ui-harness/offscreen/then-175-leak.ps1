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
# Nixie 1.8.1 (BH-175-M2): LK_CASE picks the rounds (series, the default, as above; film; collection) and LK_MB_PER_ROUND
# bounds the app's private bytes (the memory the user's PC gives Nixie alone) over them:
#   film        a film's page, its first More like this card given the keyboard, then LK_ROUNDS (20) times Enter (the
#               similar film) and Back (the film again, its view model kept, the keyboard back on the card)
#   collection  FAKESERVER_COLLECTIONS=1, FAKESERVER_LONG_TITLE=1: "Anthology of Twenty-Four" (24 films), its second card given
#               the keyboard, then LK_ROUNDS (20) times Enter (the film) and Back (the collection, its view model kept)
# LK_DRIVE: keys (the default) goes round by keys alone (Enter, Back); uia also reads the page's cards through UI Automation
# each round, as Narrator or any assistive tool does (every card's name, the card given the keyboard by UI Automation), the
# driver's own references let go each round. Checks: every round went there and back (the app's "[leak]" lines: two
# navigations a round; the keyboard back on the card it started on), and the private bytes after round LK_MB_FROM (5) grow at
# most LK_MB_PER_ROUND MB a round (3) to the last (the lowest of three readings at each end, after the app's full
# collection). On 1.8.0 a kept page's cards stayed alive with their whole trees (this check on a 1.8.0 Debug build, Classic):
# by keys, the film rounds grew 5.67 MB a round (x:Bind's tracker held the card's toggles while it heard the kept item); read
# by UI Automation, the collection rounds grew 11.5 (the card's peer held its card while UI Automation held the peer).
# The app's log rolls over at midnight: the "[leak]" lines are read from every log written since Nixie started (Lk-Logs).

$env:BK_PAGES = 'none'
. (Join-Path $PSScriptRoot 'then-174-back.ps1')
$lkRounds = if ($env:LK_ROUNDS) { [int]$env:LK_ROUNDS } else { 50 }
$lkMost = if ($env:LK_MOST) { [int]$env:LK_MOST } else { 2 }
$lkDir = Join-Path $Out 'leak175'
New-Item -ItemType Directory -Force $lkDir | Out-Null

# Nixie 1.8.1 (the mem review): the app's logs written since it started, oldest first (its log rolls over at midnight, so a
# run that crosses it has two; one left by an earlier run in the same data folder is older than the app and not read)
function Lk-Logs {
    $since = try { $script:ui.StartTime.AddSeconds(-2) } catch { [datetime]::MinValue }
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $since } | Sort-Object LastWriteTime)
}
# the app's last "[leak] live pages" line: the newest log that has one
function Lk-LastLine {
    $files = @(Lk-Logs)
    for ($k = $files.Count - 1; $k -ge 0; $k--) {
        $hit = Select-String -LiteralPath $files[$k].FullName -Pattern '\[leak\] live pages: (.*)$' -ErrorAction SilentlyContinue | Select-Object -Last 1
        if ($hit) { return $hit.Matches[0].Groups[1].Value.Trim() }
    }
    $null
}
function Lk-Count([string]$Line, [string]$Pattern) {
    $n = 0
    foreach ($part in @("$Line" -split ',\s*')) { if ($part -match "^(\w*$Pattern)\s+(\d+)$") { $n += [int]$Matches[2] } }
    $n
}

$lkCase = if ($env:LK_CASE) { $env:LK_CASE.Trim().ToLowerInvariant() } else { 'series' }
if ($lkCase -ne 'series') {
    if ($lkCase -notin @('film', 'collection')) { throw "LK_CASE=${lkCase}: series, film or collection" }
    $lkId = if ($env:GATE_ID) { $env:GATE_ID } else { 'leak175' }
    $lkN = if ($env:LK_ROUNDS) { [int]$env:LK_ROUNDS } else { 20 }
    $lkFrom = if ($env:LK_MB_FROM) { [int]$env:LK_MB_FROM } else { 5 }
    $lkMb = if ($env:LK_MB_PER_ROUND) { [double]$env:LK_MB_PER_ROUND } else { 3 }
    $lkStep = if ($env:LK_STEP_MS) { [int]$env:LK_STEP_MS } else { 3000 }
    $lkDrive = if ($env:LK_DRIVE) { $env:LK_DRIVE.Trim().ToLowerInvariant() } else { 'keys' }
    if ($lkDrive -notin @('keys', 'uia')) { throw "LK_DRIVE=${lkDrive}: keys or uia" }
    $lkTag = "$bkTag, $lkCase, $lkDrive"
    if ($lkFrom -lt 1 -or $lkFrom -ge $lkN) { throw "LK_MB_FROM=${lkFrom}: from 1 to LK_ROUNDS - 1 ($lkN)" }
    # the app's private bytes in MB: the lowest of three readings 400 ms apart (a picture decoding meanwhile counts once)
    function Lk-Private {
        $low = [double]::MaxValue
        for ($k = 0; $k -lt 3; $k++) { if ($k) { Start-Sleep -Milliseconds 400 }; $script:ui.Refresh(); $low = [Math]::Min($low, $script:ui.PrivateMemorySize64 / 1MB) }
        [Math]::Round($low, 1)
    }
    # the navigations so far: the app's "[leak]" lines (one a navigation, NIXIE_HARNESS_LEAKS=1) in every log since it started
    function Lk-Navigations {
        $n = 0
        foreach ($f in @(Lk-Logs)) { $n += @(Select-String -LiteralPath $f.FullName -Pattern '\[leak\] live pages: ' -ErrorAction SilentlyContinue).Count }
        $n
    }
    $lkMbAt = @{}; $lkDone = 0; $lkStart = $null; $lkEnd = $null; $lkNav = 0; $lkWhy = ''
    try {
        if ($lkCase -eq 'film') {
            Open-Library $fixture.movie.library
            Open-Card $fixture.movie.name
            if (-not (Wait-Until { Bk-OnFilm $fixture.movie.name } -Seconds 20 -PollMs 200)) { throw "the film's page did not open" }
            Start-Sleep -Milliseconds 2500
            $lkCard = @(Bk-Cards) | Select-Object -First 1
            if (-not $lkCard) { throw 'the film has no More like this card' }
            Bk-Take $lkCard
            $lkCard = $null
            $lkStart = Bk-Focus
        }
        else {
            Bk-OpenCollection 'Collections' $bkLongSet
            $lkStart = Bk-FocusCard 2
        }
        if (-not $lkStart -or $lkStart.Class -notin @('BaseItemDtoCard', 'EditorialPoster', 'EditorialStill')) { throw "the keyboard isn't on a card: $(Bk-Describe $lkStart)" }
        $lkStartName = $lkStart.Name
        $lkStart = $null
        # the driver's references to the app's elements let go before the rounds (the app keeps a remote element while they live)
        [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect()
        $lkNav0 = Lk-Navigations
        for ($i = 1; $i -le $lkN; $i++) {
            if ($lkDrive -eq 'uia') {
                # as an assistive tool reads the page: every card's name (asked of the app now), the card given the keyboard
                $lkCards = @(Bk-Cards)
                $lkMine = $null
                foreach ($c in $lkCards) { try { if ($c.Current.Name -eq $lkStartName -and -not $lkMine) { $lkMine = $c } } catch { } }
                if ($lkMine) { Bk-Take $lkMine }
                $lkCards = $null; $lkMine = $null; $c = $null
            }
            Key 0x0D   # Enter: the card's film
            Start-Sleep -Milliseconds $lkStep
            Key 0xA6   # Back
            Start-Sleep -Milliseconds $lkStep
            # (uia) the driver's references let go: what stays alive is the app's
            if ($lkDrive -eq 'uia') { [GC]::Collect(); [GC]::WaitForPendingFinalizers(); [GC]::Collect() }
            $lkDone = $i
            if ($i -eq $lkFrom -or $i -eq $lkN) { $lkMbAt[$i] = Lk-Private }
            if ($i % 5 -eq 0) { Say ("{0} ({1}): round {2}; private bytes {3} MB" -f $lkId, $lkTag, $i, $(if ($lkMbAt.ContainsKey($i)) { $lkMbAt[$i] } else { Lk-Private })) }
        }
        $lkNav = (Lk-Navigations) - $lkNav0
        $lkEnd = Bk-Focus
    }
    catch {
        $lkWhy = $_.Exception.Message
        Say "${lkId} ($lkTag): stopped: $lkWhy"
        Shot "leak-$lkCase-$lkDrive-$bkTag-stopped"
    }
    $script:ui.Refresh()
    $lkLine = Lk-LastLine
    Check "${lkId} ($lkTag): Nixie still running after the rounds" (-not $script:ui.HasExited) $lkWhy
    $lkThere = [bool]($lkDone -eq $lkN -and $lkNav -ge 2 * $lkN -and $lkEnd -and $lkEnd.Name -eq $lkStartName)
    Check "${lkId} ($lkTag): $lkN times Enter on a card and Back, each there and back to the card" $lkThere ("{0} of {1} rounds; {2} navigations (2 a round); the keyboard at the end on {3}, at the start on '{4}'{5}" -f $lkDone, $lkN, $lkNav, (Bk-Describe $lkEnd), $lkStartName, $(if ($lkWhy) { "; $lkWhy" }))
    $lkA = $lkMbAt[$lkFrom]; $lkB = $lkMbAt[$lkN]
    $lkPer = if ($null -ne $lkA -and $null -ne $lkB) { [Math]::Round(($lkB - $lkA) / ($lkN - $lkFrom), 2) } else { $null }
    Check "${lkId} ($lkTag): private bytes grow at most $lkMb MB a round after round $lkFrom" ($null -ne $lkPer -and $lkPer -le $lkMb) ("round {0}: {1} MB; round {2}: {3} MB; {4} MB a round; the app's last line: {5}" -f $lkFrom, $lkA, $lkN, $lkB, $lkPer, $lkLine)
    $lkRow = [pscustomobject][ordered]@{ theme = $bkTheme; case = $lkCase; drive = $lkDrive; rounds = $lkDone; navigations = $lkNav; mbFrom = $lkA; mbLast = $lkB; mbPerRound = $lkPer; line = $lkLine }
    [IO.File]::WriteAllText((Join-Path $lkDir "leak175-$lkCase-$lkDrive.json"), (ConvertTo-Json -InputObject @($lkRow) -Depth 3), [Text.UTF8Encoding]::new($false))
    return
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
