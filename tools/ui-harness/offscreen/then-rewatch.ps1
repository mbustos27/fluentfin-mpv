# sg-cw (Continue Watching): a show watched again comes back on Home, right after the player closes, without R.
#   Fake server:  run-offscreen.ps1 -Script shot:start -Then offscreen\then-rewatch.ps1 -NoA11y
#   QA server:    run-offscreen.ps1 -QaServer 10.11|12 -Script shot:start -Then offscreen\then-rewatch.ps1 -NoA11y
# The showcase series is marked watched first (all of it), as its ordinary user. Then:
#  1. its first episode plays again from the series page (its first season) for NIXIE_REWATCH_PLAY_SECONDS (QA 30, fake 45), muted, and the
#     player is left (mpv's q, or the Back key with FluentFin's controls); Home is opened at once and polled: what the
#     server will say (Jellyfin's default rules: a resume point between 5 and 90 % of an item of 5 min or more, else
#     watched) must show within NIXIE_REWATCH_WITHIN seconds (8): that episode with its progress, or else the next episode
#     ("Up next": the show being watched again). No R, no other navigation.
#  2. (NIXIE_REWATCH_FROM_HOME, on by default) that card's own Play/Resume button, from Home: played for the same time
#     (QA: to the end of the 60 s episode), left again, and the player's Back lands on Home itself (the row refreshed before
#     the backend's Stopped report reached the server): the next state must show within the same time.
# QA: NIXIE_REWATCH_MIN_RESUME=<seconds> sets the server's MinResumeDurationSeconds as qa-admin for the run (put back at
# the end); the QA episodes are 60 s, under Jellyfin's 300 s default, so without it a play marks the episode watched.
# Fake: NIXIE_REWATCH_LAG_MS=<ms> makes the fake server apply a Stopped report that long after it answered (a server
# that has not caught up when Nixie asks again). Dot-sourced by drive.ps1: its helpers; variables here start with rw.

$rwPlaySeconds = if ($env:NIXIE_REWATCH_PLAY_SECONDS) { [double]$env:NIXIE_REWATCH_PLAY_SECONDS } elseif ($QaUpstream) { 30 } else { 45 }
$rwWithin = if ($env:NIXIE_REWATCH_WITHIN) { [double]$env:NIXIE_REWATCH_WITHIN } else { 8 }
$rwFromHome = $env:NIXIE_REWATCH_FROM_HOME -ne '0'
$rwMinResume = 300   # Jellyfin's default MinResumeDurationSeconds (the fake server's too)
$rwOldMinResume = $null

# --- the server: the series watched, its first season's episodes ---------------------------------------------------------
if ($QaUpstream) {
    . (Join-Path $PSScriptRoot '..\..\qa-servers\qa-common.ps1')
    $rwVersion = @($script:QaPorts.Keys | Where-Object { $QaUpstream.TrimEnd('/') -eq (Get-QaUrl $_) }) | Select-Object -First 1
    if (-not $rwVersion) { throw "then-rewatch: $QaUpstream is not a QA server" }
    $rwUser = Connect-Qa $rwVersion 'user' 'nixie-rewatch'
    function Rw-Api([string]$Path, [string]$Method = 'GET', $Body = $null) { Invoke-Qa $rwVersion $Path $Method $Body -Token $rwUser.Token -Device 'nixie-rewatch' }
    if ($env:NIXIE_REWATCH_MIN_RESUME) {
        $rwAdmin = Connect-Qa $rwVersion 'admin' 'nixie-rewatch'
        $rwCfg = (Invoke-Qa $rwVersion '/System/Configuration' -Token $rwAdmin.Token -Device 'nixie-rewatch').Json
        $rwOldMinResume = $rwCfg.MinResumeDurationSeconds
        $rwCfg.MinResumeDurationSeconds = [int]$env:NIXIE_REWATCH_MIN_RESUME
        $rwSet = Invoke-Qa $rwVersion '/System/Configuration' 'POST' $rwCfg -Token $rwAdmin.Token -Device 'nixie-rewatch'
        Say "rewatch: the QA server's MinResumeDurationSeconds $rwOldMinResume -> $($env:NIXIE_REWATCH_MIN_RESUME) (HTTP $($rwSet.Status))"
        $rwMinResume = [int]$env:NIXIE_REWATCH_MIN_RESUME
    }
    # the whole show watched, its specials too (the series page's Play starts at the first episode not watched, specials first)
    $rwEpisodes = @((Rw-Api "/Shows/$($fixture.series.id)/Episodes?userId=$($rwUser.UserId)").Json.Items)
    foreach ($rwE in $rwEpisodes) { [void](Rw-Api "/UserPlayedItems/$($rwE.Id)?userId=$($rwUser.UserId)" 'POST') }
    $rwEpisodes = @($rwEpisodes | Where-Object { $_.ParentIndexNumber -ge 1 })
    $rwEpisodes = @($rwEpisodes | ForEach-Object { [pscustomobject]@{ id = $_.Id; name = $_.Name; season = $_.ParentIndexNumber; index = $_.IndexNumber; runtime = [double]$_.RunTimeTicks / 1e7 } })
}
else {
    $rwAnswer = Invoke-Server '/__harness/played' 'POST' @{ series = $fixture.series.id; played = $true }
    $rwEpisodes = @($rwAnswer.episodes | Where-Object { $_.season -ge 1 })
    if ($env:NIXIE_REWATCH_LAG_MS) { [void](Invoke-Server '/__harness/lag' 'POST' @{ stopped_ms = [int]$env:NIXIE_REWATCH_LAG_MS }); Say "rewatch: the fake server applies Stopped reports $($env:NIXIE_REWATCH_LAG_MS) ms late" }
}
$rwEpisodes = @($rwEpisodes | Sort-Object season, index)
Check 'rewatch: the series is marked watched (all of its episodes)' ($rwEpisodes.Count -ge 3) ("{0}: {1} episodes" -f $fixture.series.name, $rwEpisodes.Count)

# what the server will make of a play of $Seconds from $From (Jellyfin's rules with its default percentages)
# (a resume point: the card must show more than halfway from the old percentage to the new one)
function Rw-Expect($Episode, [double]$From, [double]$Seconds) {
    $at = [Math]::Min($Episode.runtime, $From + $Seconds)
    $pct = if ($Episode.runtime -gt 0) { 100.0 * $at / $Episode.runtime } else { 0 }
    $old = if ($Episode.runtime -gt 0) { 100.0 * $From / $Episode.runtime } else { 0 }
    if ($Episode.runtime -ge $rwMinResume -and $pct -ge 5 -and $pct -le 90) { return [pscustomobject]@{ Resumable = $true; Card = $Episode; Pct = $pct; At = $at; MinPct = ($old + $pct) / 2 } }
    $next = $rwEpisodes[[Array]::IndexOf(@($rwEpisodes | ForEach-Object { $_.id }), $Episode.id) + 1]
    [pscustomobject]@{ Resumable = $false; Card = $next; Pct = $pct; At = $at; MinPct = 0 }
}
function Rw-Pct([string]$Name) { if ($Name -match '(\d+)% watched') { [int]$Matches[1] } else { -1 } }
# what Home shows of the series' episodes, in every theme, as [pscustomobject]@{ Name; Where }:
#  - the cards of its rows (list items named "<series>, season 1 episode 1, <episode>, 44% watched") and the rows' own
#    buttons (Editorial's numbered Up next: "<series> S1 E2 <episode>, next episode"), not the "New in <library>" rows'
#    (the same episode can be there, its progress updated in place);
#  - Drive-in's Up next sign ("<series> S1 E2 <episode>");
#  - a hero's Resume: Drive-in and Editorial feature the first Continue Watching item there, not in the row
#    ("Resume <series> S1 E1 <episode>"; its progress is not in its name)
# -Scroll: each row scrolled across too (its cards out of view are not made: Cinematic's one Up next row has every
# Continue Watching item before the next episodes)
function Rw-Seen([switch]$Scroll) {
    $A = [System.Windows.Automation.AutomationElement]; $CT = [System.Windows.Automation.ControlType]
    $listCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::List)
    $inRow = New-Object System.Windows.Automation.OrCondition((New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)))
    $series = $fixture.series.name
    $out = New-Object System.Collections.ArrayList
    $seen = @{}
    $rows = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $listCond) | Where-Object { $_.Cached.Name -notlike 'New in*' })
    $read = {
        foreach ($l in $rows) {
            try { foreach ($e in $l.FindAll([System.Windows.Automation.TreeScope]::Descendants, $inRow)) { $n = $e.Current.Name; if ($n -and $n.Contains($series) -and -not $seen.ContainsKey($n)) { $seen[$n] = 1; [void]$out.Add([pscustomobject]@{ Name = $n; Where = $l.Cached.Name }) } } } catch {}
        }
    }
    & $read
    if ($Scroll) {
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        foreach ($l in $rows) {
            # the row's scroller: the nearest ancestor that scrolls across
            $pat = $null; $up = $l
            for ($d = 0; $d -lt 4 -and $up; $d++) {
                $p = $null
                if ($up.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$p) -and $p.Current.HorizontallyScrollable) { $pat = $p; break }
                $up = $walker.GetParent($up)
            }
            if (-not $pat) { continue }
            foreach ($pct in 20, 40, 60, 80, 100) { try { $pat.SetScrollPercent($pct, [System.Windows.Automation.ScrollPattern]::NoScroll) } catch {}; Start-Sleep -Milliseconds 250; & $read }
            try { $pat.SetScrollPercent(0, [System.Windows.Automation.ScrollPattern]::NoScroll) } catch {}
        }
    }
    $btnCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $CT::Button)
    foreach ($b in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $btnCond))) {
        $n = $b.Cached.Name
        if (-not $n -or $seen.ContainsKey($n)) { continue }
        if ($n.StartsWith("$series S")) { [void]$out.Add([pscustomobject]@{ Name = $n; Where = 'Up next' }) }
        elseif ($n.StartsWith("Resume $series S")) { [void]$out.Add([pscustomobject]@{ Name = $n; Where = 'the hero (featured)' }) }
    }
    $out
}
# a hero that turns its features (Drive-in): the feature of the series brought up, when Home shows nothing else of it
function Rw-ShowFeature {
    $b = [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Feature ', ": $($fixture.series.name)")
    if ($b -and $b.Current.Name.EndsWith(": $($fixture.series.name)")) { try { [void](Invoke-Element $b) } catch {} }
}
# the series' episode an entry names, by its number: "season 1 episode 2" (a card) or "S1 E2" (a button); names repeat
function Rw-EpisodeOf([string]$Name) {
    if (-not $Name -or -not $Name.Contains($fixture.series.name)) { return $null }
    if ($Name -match 'season (\d+) episode (\d+)\b' -or $Name -match '\bS(\d+) E(\d+)\b') {
        $sn = [int]$Matches[1]; $en = [int]$Matches[2]
        $rwEpisodes | Where-Object { $_.season -eq $sn -and $_.index -eq $en } | Select-Object -First 1
    }
}
function Rw-Matches([string]$Name, $Episode) { $Episode -and (Rw-EpisodeOf $Name).id -eq $Episode.id }
# the series' episodes Home shows (the review: one show once: Continue Watching and Up next, or two Up next episodes)
function Rw-Shown($Seen) { @($Seen | ForEach-Object { Rw-EpisodeOf $_.Name } | Where-Object { $_ } | ForEach-Object { 'S{0} E{1}' -f $_.season, $_.index } | Sort-Object -Unique) }
# polls Home until it shows what the server will (a resume point: the episode with its new "% watched", or featured with
# Resume where a theme puts it in the hero; else the next episode), for a watched episode nothing of it is left, and no
# other episode of the show is shown; returns when (and where), or $null
function Rw-WaitHome($Expect, $Played, [double]$Since) {
    $script:rwLastSeen = @()
    Wait-Until {
        $seen = @(Rw-Seen)
        $script:rwLastSeen = $seen
        if (-not @(Rw-Shown $seen).Count) { Rw-ShowFeature }
        $mine = @($seen | Where-Object { Rw-Matches $_.Name $Expect.Card })
        # (@(...) around it all: Windows PowerShell 5.1 gives a lone [pscustomobject] no Count)
        $want = @(if ($Expect.Resumable) {
            $read = @($mine | Where-Object { (Rw-Pct $_.Name) -ge 0 })
            if ($read.Count) { $read | Where-Object { (Rw-Pct $_.Name) -gt $Expect.MinPct } } else { $mine | Where-Object { $_.Name.StartsWith('Resume ') } }
        } else { $mine })
        # a watched episode: nothing of it is left on Home's rows
        $gone = $Expect.Resumable -or -not @($seen | Where-Object { Rw-Matches $_.Name $Played }).Count
        $once = @(Rw-Shown $seen).Count -eq 1
        if ($want.Count -and $gone -and $once) { [pscustomobject]@{ Card = $want[0].Name; Where = $want[0].Where; Read = (Rw-Pct $want[0].Name) -ge 0; Ms = Ms $Since (Get-Epoch) } }
    } -Seconds $rwWithin -PollMs 250
}
function Rw-SeenText { ($script:rwLastSeen | Select-Object -First 14 | ForEach-Object { "$($_.Name) [$($_.Where)]" }) -join ' | ' }
function Rw-Describe($Expect) {
    if (-not $Expect.Card) { return 'nothing (no next episode)' }
    if ($Expect.Resumable) { "S$($Expect.Card.season) E$($Expect.Card.index) with its progress ({0:N0} %)" -f $Expect.Pct } else { "S$($Expect.Card.season) E$($Expect.Card.index) up next (the one played is watched: {0:N0} %)" -f $Expect.Pct }
}
# plays until $Seconds after the backend's Playing report, then leaves the player; returns when it left
function Rw-PlayFor([double]$Seconds, [double]$PlayAt) {
    $lit = if ($PlayerControls -eq 'Mpv') {
        Wait-Until { $v = [OffscreenUi]::VideoLit($script:hwnd, $script:ui.Id); if ($v -gt 20) { $v } } -Seconds 60 -PollMs 100
    } else {
        Wait-Until { $v = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.3, 0.3, 0.7, 0.7); if ($v -gt 20) { $v } } -Seconds 60 -PollMs 100
    }
    $playing = Wait-Until { @(Get-Requests $PlayAt | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 20 -PollMs 200
    Check 'rewatch: the video shows and the backend reported Playing' ($lit -and $playing) "$lit% not black"
    $left = $Seconds - ((Get-Epoch) - $(if ($playing) { $playing.t } else { $PlayAt }))
    if ($left -gt 0) { Start-Sleep -Milliseconds ([int]($left * 1000)) }
    $stopAt = Get-Epoch
    if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x51 'q' } else { Key 0xA6 }
    $stopAt
}

try {
    # --- 0. the review's case: two Up next candidates of one show --------------------------------------------------------------
    # all but the last episode watched, then the second one played again to its end (as on another device): with rewatching the
    # server lists the episode after it AND the last one (QA 10.11 and 12.4: S1 E3 and S2 E2); Home shows the show once, the
    # server's first (the one watched toward most recently)
    if ($rwEpisodes.Count -ge 4) {
        $rwAgain = $rwEpisodes[1]; $rwToward = $rwEpisodes[2]; $rwLast = $rwEpisodes[$rwEpisodes.Count - 1]
        if ($QaUpstream) {
            foreach ($rwE in $rwEpisodes) { [void](Rw-Api "/UserPlayedItems/$($rwE.id)?userId=$($rwUser.UserId)" 'DELETE') }
            for ($rwK = 0; $rwK -lt $rwEpisodes.Count - 1; $rwK++) { [void](Rw-Api "/UserPlayedItems/$($rwEpisodes[$rwK].id)?userId=$($rwUser.UserId)" 'POST'); Start-Sleep -Milliseconds 1100 }
            $rwPs = [guid]::NewGuid().ToString('N'); $rwEnd = [long]($rwAgain.runtime * 1e7)
            [void](Rw-Api '/Sessions/Playing' 'POST' @{ ItemId = $rwAgain.id; MediaSourceId = $rwAgain.id; PlaySessionId = $rwPs; PositionTicks = 0; CanSeek = $true; PlayMethod = 'DirectPlay' })
            [void](Rw-Api '/Sessions/Playing/Stopped' 'POST' @{ ItemId = $rwAgain.id; MediaSourceId = $rwAgain.id; PlaySessionId = $rwPs; PositionTicks = $rwEnd })
        }
        else {
            [void](Invoke-Server '/__harness/played' 'POST' @{ series = $fixture.series.id; played = $false })
            for ($rwK = 0; $rwK -lt $rwEpisodes.Count - 1; $rwK++) { [void](Invoke-Server '/__harness/played' 'POST' @{ id = $rwEpisodes[$rwK].id; played = $true }) }
            Start-Sleep -Milliseconds 100
            [void](Invoke-Server '/__harness/played' 'POST' @{ id = $rwAgain.id; played = $true })
        }
        Say ("rewatch: {0} S{1} E{2} to S{3} E{4} watched, then S{5} E{6} again to its end: the server lists S{7} E{8} and S{9} E{10} up next" -f $fixture.series.name,
            $rwEpisodes[0].season, $rwEpisodes[0].index, $rwEpisodes[$rwEpisodes.Count - 2].season, $rwEpisodes[$rwEpisodes.Count - 2].index,
            $rwAgain.season, $rwAgain.index, $rwToward.season, $rwToward.index, $rwLast.season, $rwLast.index)
        Start-Sleep -Milliseconds 1500   # the server's UserDataChanged: the saved Home page is stale
        Open-Nav $fixture.series.library; Start-Sleep -Milliseconds 800
        Open-Nav 'Home'; $script:page = 'home'
        $rwOnce = { $seen = @(Rw-Seen -Scroll); $script:rwLastSeen = $seen; $shown = @(Rw-Shown $seen); if ($shown.Count -eq 1 -and $shown[0] -eq ('S{0} E{1}' -f $rwToward.season, $rwToward.index)) { $shown } }
        $rwOnly = Wait-Until $rwOnce -Seconds ($rwWithin + 6) -PollMs 300
        $rwHow = 'without R'
        if (-not $rwOnly) { Key 0x52; $rwHow = 'after R'; $rwOnly = Wait-Until $rwOnce -Seconds ($rwWithin + 6) -PollMs 300 }
        Check "rewatch: two Up next episodes of one show: Home shows S$($rwToward.season) E$($rwToward.index) only ($rwHow)" ([bool]$rwOnly) "Home shows: $(Rw-SeenText) (episodes of the show: $((Rw-Shown $script:rwLastSeen) -join ', '))"
        Shot 'rewatch-0-home'
        if ($QaUpstream) { foreach ($rwE in $rwEpisodes) { [void](Rw-Api "/UserPlayedItems/$($rwE.id)?userId=$($rwUser.UserId)" 'POST') } }
        else { [void](Invoke-Server '/__harness/played' 'POST' @{ series = $fixture.series.id; played = $true }) }
    }

    # --- 1. the first episode again, from its season page; then Home --------------------------------------------------------
    $rwFirst = $rwEpisodes[0]
    $rwExpect1 = Rw-Expect $rwFirst 0 $rwPlaySeconds
    Say ("rewatch: {0} S{1} E{2} '{3}' ({4:N0} s) plays {5} s; the server will show {6}" -f $fixture.series.name, $rwFirst.season, $rwFirst.index, $rwFirst.name, $rwFirst.runtime, $rwPlaySeconds, (Rw-Describe $rwExpect1))
    # the series page opens on the season Play starts in (all watched: the first, Specials on the QA server): its season pill
    Step-Series $false
    # (a button in Classic and Drive-in, a tab or a list item in the other themes; "<season>, current season" when it is open)
    # (Editorial's tab is "Season 1, Watched": the name, or the name and what follows a comma; never "Season 1 details")
    $rwPill = Wait-Until { foreach ($rwT in 'Button', 'TabItem', 'ListItem', 'RadioButton') {
            $e = Find-Ui -Name $fixture.season.name -Type $rwT; if ($e) { return $e }
            $e = Find-Ui -Name "$($fixture.season.name), " -Type $rwT -Contains; if ($e -and $e.Current.Name.StartsWith("$($fixture.season.name), ") -and $e.Current.Name -notlike '*current season*') { return $e } } } -Seconds 10 -PollMs 200
    if ($rwPill) { Say "rewatch: the '$($fixture.season.name)' pill ($(try { Invoke-Element $rwPill } catch { 'not invokable' }))" }
    $rwRow = Wait-Until { Find-EpisodeRow } -Seconds 15 -PollMs 200
    if (-not $rwRow) { throw "then-rewatch: no row for '$($fixture.episode.name)' on the series page" }
    $rwPlayAt = Get-Epoch
    [void](Invoke-Element $rwRow)
    $script:page = 'player'
    $rwStopAt = Rw-PlayFor $rwPlaySeconds $rwPlayAt
    $rwBack = Wait-Until { Find-EpisodeRow } -Seconds 20 -PollMs 150
    Check 'rewatch: the player was left (back on the series page)' ([bool]$rwBack)
    $script:page = 'series'
    # the series page the player went back to was made before the Stopped report reached the server: once it has it, its Play
    # names the episode to resume (Jellyfin 10.11 unmarks an episode played again; 12 keeps it watched: Play from the start)
    if ($rwExpect1.Resumable -and $rwVersion -eq '10.11') {
        $rwResume = Wait-Until { $b = [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Resume', "S$($rwFirst.season) E$($rwFirst.index)"); if ($b) { $b.Current.Name } } -Seconds $rwWithin -PollMs 200
        Check "rewatch: the series page's Play resumes S$($rwFirst.season) E$($rwFirst.index) within $rwWithin s of leaving the player, without R" ([bool]$rwResume) `
            $(if ($rwResume) { "'$rwResume' $(Ms $rwStopAt (Get-Epoch)) ms after the stop key" } else { "Play: '$(([OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Play', '')).Current.Name)'" })
        Shot 'rewatch-1-series'
    }
    $rwHomeAt = Get-Epoch
    Open-Nav 'Home'; $script:page = 'home'
    $rwSeen1 = Rw-WaitHome $rwExpect1 $rwFirst $rwStopAt
    $rwStop1 = @(Get-Requests $rwStopAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1
    $rwRows1 = @(Get-Requests $rwHomeAt | Where-Object { (Get-Origin $_) -eq 'ui' -and $_.route -in @('resume', 'nextup') })
    Check "rewatch: Home shows $(Rw-Describe $rwExpect1), and the show once, within $rwWithin s of leaving the player, without R" ([bool]$rwSeen1) `
        $(if ($rwSeen1) { "'$($rwSeen1.Card)' in '$($rwSeen1.Where)' $($rwSeen1.Ms) ms after the stop key (Stopped reported at $(if ($rwStop1) { Ms $rwStopAt $rwStop1.t } else { '?' }) ms; $($rwRows1.Count) row requests)" } else { "Home shows: $(Rw-SeenText) (episodes of the show: $((Rw-Shown $script:rwLastSeen) -join ', '))" })
    Shot 'rewatch-1-home'

    # --- 2. from Home itself: the card's own Play/Resume, and the player's Back lands on Home --------------------------------
    # (where Home shows the card with its "% watched" or the next episode: a hero's Resume names no progress to check, and
    # Cinematic's and Drive-in's cards have no Play of their own; Classic's must)
    $rwClassic = -not $env:NIXIE_LAYOUT -or $env:NIXIE_LAYOUT -eq 'Classic'
    if ($rwFromHome -and $rwSeen1 -and $rwExpect1.Card -and -not ($rwExpect1.Resumable -and -not $rwSeen1.Read)) {
        $rwCardEp = $rwExpect1.Card
        $rwFrom = if ($rwExpect1.Resumable) { $rwExpect1.At } else { 0 }
        # a resume point again: short of 90 % and of the end (at the end the player would go on to the next episode)
        $rwPlay2 = if ($rwExpect1.Resumable) { [Math]::Min($rwPlaySeconds, [Math]::Max(5, 0.8 * (0.9 * $rwCardEp.runtime - $rwFrom))) } else { $rwPlaySeconds }
        $rwExpect2 = Rw-Expect $rwCardEp $rwFrom $rwPlay2
        $rwCard = Find-Ui -Name $rwSeen1.Card -Type 'ListItem' -OffscreenToo
        if ($rwCard) { [void][OffscreenUi]::SetFocus($rwCard); Start-Sleep -Milliseconds 600 }
        $rwPlayBtn = Wait-Until {
            foreach ($p in 'Resume ', 'Play ') { $b = [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, $p, "$($fixture.series.name) S$($rwCardEp.season) E$($rwCardEp.index)"); if ($b) { return $b } }
        } -Seconds $(if ($rwCard) { 4 } else { 1 }) -PollMs 200
        Say ("rewatch: from Home, '{0}' ({1}); the server will show {2}" -f $(if ($rwPlayBtn) { $rwPlayBtn.Current.Name } else { 'no Play button on the card' }), $(if ($rwCard) { 'card focused' } else { 'no card' }), (Rw-Describe $rwExpect2))
        if ($rwClassic) { Check "rewatch: the card on Home has its Play/Resume button" ([bool]$rwPlayBtn) }
        elseif (-not $rwPlayBtn) { Say "rewatch: step 2 skipped: $($env:NIXIE_LAYOUT)'s card has no Play of its own" }
        if ($rwPlayBtn) {
            $rwPlayAt2 = Get-Epoch
            [void](Invoke-Element $rwPlayBtn)
            $script:page = 'player'
            $rwStopAt2 = Rw-PlayFor $rwPlay2 $rwPlayAt2
            # Home's rows are lists named after them (any theme); the player's own "Up next" is a button
            $rwHome2 = Wait-Until { foreach ($n in 'Continue Watching', 'Up next') { if (Find-Ui -Name $n -Type 'List') { return $true } } } -Seconds 20 -PollMs 150
            Check "rewatch: the player's Back landed on Home" ([bool]$rwHome2)
            $script:page = 'home'
            $rwSeen2 = Rw-WaitHome $rwExpect2 $rwCardEp $rwStopAt2
            $rwStop2 = @(Get-Requests $rwStopAt2 | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1
            $rwRows2 = @(Get-Requests $rwStopAt2 | Where-Object { (Get-Origin $_) -eq 'ui' -and $_.route -in @('resume', 'nextup') })
            $rwFirstRow2 = $rwRows2 | Select-Object -First 1
            Check "rewatch: back on Home, it shows $(Rw-Describe $rwExpect2), and the show once, within $rwWithin s, without R" ([bool]$rwSeen2) `
                $(if ($rwSeen2) { "'$($rwSeen2.Card)' in '$($rwSeen2.Where)' $($rwSeen2.Ms) ms after the stop key (Stopped reported at $(if ($rwStop2) { Ms $rwStopAt2 $rwStop2.t } else { '?' }) ms; first row request at $(if ($rwFirstRow2) { Ms $rwStopAt2 $rwFirstRow2.t } else { '?' }) ms; $($rwRows2.Count) row requests)" } else { "Home shows: $(Rw-SeenText) (episodes of the show: $((Rw-Shown $script:rwLastSeen) -join ', '))" })
            Shot 'rewatch-2-home'
        }
    }
    elseif ($rwFromHome -and $rwSeen1) { Say "rewatch: step 2 skipped: Home shows the episode in $($rwSeen1.Where), with no progress in its name to check" }
}
finally {
    # --- put the server back as it was (QA: the setting; the play state goes with qa.ps1 reset) -------------------------------
    if ($QaUpstream -and $null -ne $rwOldMinResume) {
        $rwCfg = (Invoke-Qa $rwVersion '/System/Configuration' -Token $rwAdmin.Token -Device 'nixie-rewatch').Json
        $rwCfg.MinResumeDurationSeconds = $rwOldMinResume
        $rwSet = Invoke-Qa $rwVersion '/System/Configuration' 'POST' $rwCfg -Token $rwAdmin.Token -Device 'nixie-rewatch'
        Say "rewatch: MinResumeDurationSeconds back to $rwOldMinResume (HTTP $($rwSet.Status))"
    }
    if ($QaUpstream) {
        foreach ($rwT in @($rwUser, $(if ($rwAdmin) { $rwAdmin })) | Where-Object { $_ }) { [void](Invoke-Qa $rwVersion '/Sessions/Logout' 'POST' -Token $rwT.Token -Device 'nixie-rewatch') }
    }
    elseif ($env:NIXIE_REWATCH_LAG_MS) { [void](Invoke-Server '/__harness/lag' 'POST' @{ stopped_ms = 0 }) }
}
