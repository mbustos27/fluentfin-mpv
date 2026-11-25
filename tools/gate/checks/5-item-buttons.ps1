# The basics gate (Nixie 1.7.5, class 5, g3): every item page's buttons give their visible result, and it survives R. On a
# film, a series, a season and an episode (the real-shaped series of FAKESERVER_REAL_SHAPE: long names): the watched toggle and
# the favourite toggle, each by the keyboard (Space) and by the mouse (the dev build's pointer hook), each change drawn (the
# toggle's state) and said (its name: "Mark <item> as unwatched", "Remove <item> from favorites"), sent to the server once,
# and still so after R loads the page again; Play on the film and Resume on the episode in progress open the player, and
# leaving it comes back to the same page. The trailer is then-trailer.ps1's (registered beside this); the "..." menu is g2's.
#   Kind then, Dev (the pointer hook); FAKESERVER_REAL_SHAPE=1; -Script wait:1 -NoA11y -Then tools\gate\checks\5-item-buttons.ps1
# Dot-sourced by drive.ps1: every name here starts with ib5 / Ib5-.
. (Join-Path $PSScriptRoot 'g3-common.ps1')
$ib5Id = if ($env:GATE_ID) { $env:GATE_ID } else { 'item-buttons' }
$ib5Real = if ($fixture.PSObject.Properties['real_shape']) { $fixture.real_shape } else { $null }
if (-not $ib5Real) { Check "${ib5Id}: the fake server has the real-shaped titles (FAKESERVER_REAL_SHAPE=1)" $false 'the fixture has no real_shape'; return }
$ib5Series = $ib5Real.series.name
$ib5Ep = $ib5Real.in_progress

# the toggle whose name starts with one of the prefixes (either state's name)
function Ib5-Find([string[]]$Prefixes, [string]$Part = '') {
    foreach ($p in $Prefixes) { $b = [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, $p, $Part); if ($b -and (G3-State $b)) { return $b } }
    $null
}
# R: the page loads again (FluentFin's "Refresh:" line), then settles
function Ib5-Refresh {
    $at = Get-Epoch
    Key 0x52
    $done = Wait-Until { @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
                Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '\[INF\] Refresh: ' } | ForEach-Object {
                    $w = [DateTimeOffset]::MinValue
                    if ([DateTimeOffset]::TryParse(($_ -split ' \[INF\] ')[0], [ref]$w) -and $w.ToUnixTimeMilliseconds() / 1000.0 -ge $at) { $_ } } }) | Select-Object -First 1 } -Seconds 8 -PollMs 200
    G3-Settle 3000
    [bool]$done
}
# one toggle on the page now: by $First ('keyboard' or 'mouse'), then back by the other; each kept after R
# $Kind 'watched' or 'favorite'; $On/$Off the names it has when on and off; $Route the server's endpoint ('played' or 'favorite')
function Ib5-Toggle([string]$Page, [string]$Kind, [string]$On, [string]$Off, [string]$Route, [string]$First = 'keyboard') {
    $prefixes = @(($On -split ' ')[0..1] -join ' ', ($Off -split ' ')[0..1] -join ' ') | Select-Object -Unique
    $b = Wait-Until { $x = Ib5-Find @($On, $Off); if ($x) { $x } } -Seconds 15 -PollMs 250
    if (-not $b) { G3-Check $ib5Id "$Page, $Kind" 'the page has the toggle' $false "none named '$On' or '$Off'"; return }
    foreach ($how in @($First, $(if ($First -eq 'keyboard') { 'mouse' } else { 'keyboard' }))) {
        $b = Ib5-Find @($On, $Off)
        $s0 = G3-State $b
        $want = if ($s0 -eq 'On') { 'Off' } else { 'On' }
        $wantName = if ($want -eq 'On') { $On } else { $Off }
        $at = Get-Epoch
        if ($how -eq 'keyboard') {
            # 1.8 gate fix (the 1.8.0 package's Editorial run): Space only once the keyboard is on the toggle. The series page
            # was found before its toggle could be (state '' at the start), the focus went nowhere, and the Space went to
            # Play, which takes the keyboard on arrival (ItemPagePlace), so the show played and every later step failed
            $ib5On = Wait-Until {
                $y = Ib5-Find @($On, $Off)
                if ($y) { [void][OffscreenUi]::SetFocus($y); G3-Settle 300 }
                $fy = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
                if ($y -and $fy -and (($fy | ConvertFrom-Json).name -in @($On, $Off))) { $y }
            } -Seconds 8 -PollMs 200
            if (-not $ib5On) {
                $fy = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
                G3-Check $ib5Id "$Page, $Kind, $how" 'the keyboard can be put on the toggle' $false "the keyboard on: $(if ($fy) { ($fy | ConvertFrom-Json).name } else { 'nothing' })"
                continue
            }
            $b = $ib5On; $s0 = G3-State $b
            $want = if ($s0 -eq 'On') { 'Off' } else { 'On' }
            $wantName = if ($want -eq 'On') { $On } else { $Off }
            $at = Get-Epoch
            Key 0x20
        }
        elseif (-not (G3-Click $b)) { G3-Check $ib5Id "$Page, $Kind, $how" 'the toggle can be clicked' $false 'no rect'; continue }
        # (drawn and named together: the state and the name read from one find; Drive-in's board is made again after a
        # season is marked, so the toggle is found afresh until both agree)
        $x = Wait-Until { $y = Ib5-Find @($On, $Off); if ($y -and (G3-State $y) -eq $want -and $y.Current.Name -eq $wantName) { $y } } -Seconds 6 -PollMs 200
        $seen = if ($x) { $x } else { Ib5-Find @($On, $Off) }
        $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
        G3-Check $ib5Id "$Page, $Kind, $how" "the toggle turns $($want.ToLower()) and is named '$wantName'" ([bool]$x) "state $s0 -> $(G3-State $seen); name '$(if ($seen) { $seen.Current.Name })'; the keyboard on: $(if ($f) { ($f | ConvertFrom-Json).name } else { 'nothing' })"
        G3-Settle 800
        $sent = @(Get-Requests $at | Where-Object { $_.route -match $Route -or $_.path -match $Route })
        G3-Check $ib5Id "$Page, $Kind, $how" 'the server is told once' ($sent.Count -eq 1) "$($sent.Count): $(@($sent | ForEach-Object { "$($_.method) $($_.path)" }) -join '; ')"
        # R: the page from the server again, the toggle as it was left (focus first on the toggle: R in a text box is a letter)
        # (a whole season or series marked shows the Undo notice for 10 s, and the window's keys wait for it: the check of
        # that is its own, below; this one is whether the change is kept)
        $undo = [bool](Find-Ui -Name 'Undo' -Type 'Button')
        if ($undo) { [void](Wait-Until { if (-not (Find-Ui -Name 'Undo' -Type 'Button')) { $true } } -Seconds 14 -PollMs 300) }
        $x = Ib5-Find @($On, $Off); if ($x) { [void][OffscreenUi]::SetFocus($x); G3-Settle 300 }
        $refreshed = Ib5-Refresh
        $x = Wait-Until { $y = Ib5-Find @($On, $Off); if ($y) { $y } } -Seconds 10 -PollMs 250
        G3-Check $ib5Id "$Page, $Kind, $how" 'kept after R' ($refreshed -and $x -and (G3-State $x) -eq $want -and $x.Current.Name -eq $wantName) "refreshed: $refreshed; state '$(G3-State $x)'; name '$(if ($x) { $x.Current.Name })'$(if (-not $refreshed -and (Find-Ui -Name 'Undo' -Type 'Button')) { "; R did nothing while the Undo notice showed" })"
    }
}
# Play (or Resume) opens the player; leaving it comes back to the page ($Shown)
# the page's own play button: named as it reads ("Play", "Resume at 4:00": Classic's item pages) or for its item ("Play
# <item>", "Resume <item>": the themes' stages); $Names are exact names, or prefixes when they end in a space
function Ib5-PlayButton([string[]]$Names) {
    foreach ($n in $Names) {
        $b = if ($n.EndsWith(' ')) { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, $n, '') } else { Find-Ui -Name $n -Type 'Button' }
        if ($b) { return $b }
    }
    $null
}
function Ib5-Play([string]$Page, [string[]]$Names, [scriptblock]$Shown) {
    $p = Wait-Until { Ib5-PlayButton $Names } -Seconds 10 -PollMs 250
    if (-not $p) { G3-Check $ib5Id "$Page, $($Names[0])" 'the page has the button' $false "none named $(($Names | ForEach-Object { "'$_'" }) -join ' or ')"; return }
    $name = $p.Current.Name
    $at = Get-Epoch
    [void](Invoke-Element $p)
    $player = Wait-Until { if ((Find-Ui -Id 'PlayPauseButton' -OffscreenToo) -or (Get-VideoWindow)) { $true } } -Seconds 45 -PollMs 250
    $playing = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 30 -PollMs 300
    G3-Check $ib5Id "$Page, $name" 'opens the player, and it plays' ([bool]$player -and [bool]$playing) "player page: $([bool]$player); Playing reported: $([bool]$playing)"
    G3-Settle 1500
    if ($PlayerControls -eq 'Mpv' -and (Get-VideoWindow)) { Send-PlayerKey 0x51 'q' } else { Key 0xA6 }
    $back = G3-Wait $Shown 25
    if (-not $back -and (Find-Ui -Id 'BackButton')) { [void](Invoke-Element (Find-Ui -Id 'BackButton')); $back = G3-Wait $Shown 15 }
    G3-Check $ib5Id "$Page, $name" 'leaving the player comes back to the page' $back ''
    G3-Settle 2000
}

[void](G3-Size '1920x1080')

# ---- the film: watched (keyboard first), favourite (mouse first), Play ----------------------------------------------------------
$ib5Film = $fixture.movie.name
Open-Library $fixture.movie.library
Open-Card $ib5Film
if (G3-Wait { G3-OnItem $ib5Film } 25) {
    Ib5-Toggle 'film' 'watched' "Mark $ib5Film as unwatched" "Mark $ib5Film as watched" 'playeditems' 'keyboard'
    Ib5-Toggle 'film' 'favorite' "Remove $ib5Film from favorites" "Add $ib5Film to favorites" 'favoriteitems' 'mouse'
    Ib5-Play 'film' @('Play', "Play $ib5Film") { G3-OnItem $ib5Film }
    Shot (Safe-Name "$ib5Id-$g3Theme-film")
}
else { G3-Check $ib5Id 'film' 'the film page opens' $false $ib5Film }

# ---- the series (long name): watched marks it all, favourite ---------------------------------------------------------------
Open-Library $ib5Real.series.library
Open-Card $ib5Series
if (G3-Wait { G3-OnSeries $ib5Series } 25) {
    Ib5-Toggle 'series' 'favorite' "Remove $ib5Series from favorites" "Add $ib5Series to favorites" 'favoriteitems' 'keyboard'
    Shot (Safe-Name "$ib5Id-$g3Theme-series")
    # the season: Season 1 is watched; unwatched and back
    # (the show is in progress: its page opens on Season 2; Season 1's tab in the picker first)
    $ib5Link = Wait-Until { Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo } -Seconds 4 -PollMs 200
    if (-not $ib5Link) {
        $ib5Tab = G3-SeasonTab 1
        if ($ib5Tab) { [void](Invoke-Element $ib5Tab) }
        $ib5Link = Wait-Until { Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo } -Seconds 10 -PollMs 200
    }
    if ($ib5Link) { [void](Invoke-Element $ib5Link) }
    if (G3-Wait { G3-OnSeason $ib5Series 1 } 20) {
        Ib5-Toggle 'season' 'watched' "Mark Season 1 of $ib5Series as unwatched" "Mark Season 1 of $ib5Series as watched" 'playeditems' 'mouse'
        Shot (Safe-Name "$ib5Id-$g3Theme-season")
        # a whole season marked shows the Undo notice for 10 s (UndoToast): the window's keys work meanwhile (1.7.4 and before:
        # the notice counted as an open menu, WindowShortcuts.HasOpenPopup, and Back and R did nothing): R loads the page
        # again, then Back goes up; each pressed while the notice shows
        $ib5S = Ib5-Find @("Mark Season 1 of $ib5Series as unwatched", "Mark Season 1 of $ib5Series as watched")
        if ($ib5S) {
            [void][OffscreenUi]::SetFocus($ib5S); G3-Settle 300; Key 0x20; G3-Settle 1200
            $ib5Undo = [bool](Find-Ui -Name 'Undo' -Type 'Button')
            $ib5S = Ib5-Find @("Mark Season 1 of $ib5Series as unwatched", "Mark Season 1 of $ib5Series as watched")
            if ($ib5S) { [void][OffscreenUi]::SetFocus($ib5S); G3-Settle 200 }
            $ib5R = Ib5-Refresh
            G3-Check $ib5Id 'season, watched' 'R works while the Undo notice shows' ($ib5Undo -and $ib5R) "the Undo notice showing: $ib5Undo; the page loaded again: $ib5R"
            $ib5Undo = [bool](Find-Ui -Name 'Undo' -Type 'Button')
            G3-Back
            $ib5Up = G3-Wait { G3-OnSeries $ib5Series } 5
            G3-Check $ib5Id 'season, watched' 'Back works while the Undo notice shows' ($ib5Undo -and $ib5Up) "the Undo notice showing: $ib5Undo; on the series page within 5 s: $ib5Up"
            if (-not $ib5Up) { Shot (Safe-Name "$ib5Id-$g3Theme-season-back-under-undo"); [void](G3-Wait { G3-OnSeries $ib5Series } 12) }
        }
    }
    else { G3-Check $ib5Id 'season' 'the season page opens' $false 'Season 1' }
}
else { G3-Check $ib5Id 'series' 'the series page opens' $false $ib5Series }

# ---- the episode in progress (from Home's Continue watching): Resume, watched, favourite --------------------------------------
$ib5Card = G3-OpenInProgress $ib5Series
if ($ib5Card) { Say "${ib5Id}: from Home '$ib5Card'" }
$ib5OnEp = { $s = Find-Ui -Id 'TrailSeason'; $s -and $s.Current.Name -eq "Go to Season $($ib5Ep.season)" }
if ($ib5Card -and (G3-Wait $ib5OnEp 25)) {
    # (the episode's name in its buttons: "<series> S2 E6 <title>")
    $ib5Fav = Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Add $ib5Series ", $ib5Ep.name) } -Seconds 10 -PollMs 250
    $ib5Target = if ($ib5Fav) { $ib5Fav.Current.Name -replace '^Add (.+) to favorites$', '$1' } else { "$ib5Series S$($ib5Ep.season) E$($ib5Ep.index) $($ib5Ep.name)" }
    Ib5-Play 'episode' @('Resume at ', "Resume $ib5Target") $ib5OnEp
    Ib5-Toggle 'episode' 'favorite' "Remove $ib5Target from favorites" "Add $ib5Target to favorites" 'favoriteitems' 'mouse'
    Ib5-Toggle 'episode' 'watched' "Mark $ib5Target as unwatched" "Mark $ib5Target as watched" 'playeditems' 'keyboard'
    Shot (Safe-Name "$ib5Id-$g3Theme-episode")
}
else { G3-Check $ib5Id 'episode' 'the episode in progress opens from Home' $false "opened from Home: $(if ($ib5Card) { $ib5Card } else { 'nothing' })" }
