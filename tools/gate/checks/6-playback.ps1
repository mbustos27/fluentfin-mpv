# Nixie 1.7.5, the basics gate, class 6 (playback basics): play, pause, seek, audio and subtitle switches, next and previous
# episode, Up next and its way out, stop, the resume point saved and shown, close mid-play then Resume from Continue
# watching. Each step is checked by what the user sees: Play/Pause's name, the elapsed time the bar shows (beside mpv's own
# time-pos, read over the in-process core's IPC pipe), the episode's title in the player's top bar, the track the Audio and
# subtitles menu marks, the season page's row ("20% watched"), Home's Continue watching card, and the server's one Stopped
# report with that position (the backend's, the one reporter).
#   run-gate.ps1 ... -Checks playback-basics      (manifest\g4.psd1)
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then tools\gate\checks\6-playback.ps1
# Dot-sourced by drive.ps1 after the sign-in (FluentFin's controls, the default). G4_PARTS picks parts: playpause, seek,
# tracks, episodes, stop, restart, upnext. Every name here starts with g4p or G4P- (g4-common.ps1: g4 / G4-).

$script:g4DefaultId = 'playback-basics'
. (Join-Path $PSScriptRoot 'g4-common.ps1')

# ---- what the player shows -----------------------------------------------------------------------------------------------
# the bars up (the harness's pointer move over the video), and an element of theirs
function G4P-Bar([string]$Id) {
    $e = Find-Ui -Id $Id
    if (-not $e) { KM-OvMove; $e = Wait-Until { Find-Ui -Id $Id } -Seconds 3 -PollMs 100 }
    $e
}
# "1:02" / "1:02:03" in seconds; $null when it can't be read
function G4P-Secs([string]$Text) {
    $parts = @(($Text -replace '[^\d:]', '') -split ':' | Where-Object { $_ -ne '' })
    if ($parts.Count -lt 2) { return $null }
    $s = 0; foreach ($p in $parts) { $s = $s * 60 + [int]$p }; $s
}
# the elapsed time the bar shows, in seconds
function G4P-Shown { $e = G4P-Bar 'ElapsedText'; if ($e) { G4P-Secs $e.Current.Name } else { $null } }
function G4P-Pos { KM-Num 'time-pos' }
function G4P-Paused { KM-Bool 'pause' }
# the player's top bar: "S1 E2 (middle dot) <title>" (SubtitleText), with the series in TitleText
function G4P-Episode { $e = G4P-Bar 'SubtitleText'; if ($e) { $e.Current.Name } else { '' } }
function G4P-PlayName { $e = G4P-Bar 'PlayPauseButton'; if ($e) { $e.Current.Name } else { '' } }
# the server's reports since an epoch, of a kind (report-stopped, report-playing), with their position in seconds
function G4P-Reports([double]$Since, [string]$Route) {
    @(Get-Requests $Since | Where-Object { $_.route -eq $Route } | ForEach-Object {
            [pscustomobject]@{ Origin = Get-Origin $_; Item = "$($_.report.item)"; Pos = $(if ($null -ne $_.report.position) { [double]$_.report.position / 1e7 } else { $null }) }
        })
}
function G4P-Short([string]$Id) { if ($Id.Length -gt 6) { $Id.Substring(0, 6) } else { $Id } }
function G4P-Desc { "Play/Pause '$(G4P-PlayName)', shown $(G4P-Shown) s, mpv $([math]::Round([double](G4P-Pos), 1)) s, paused $(G4P-Paused)" }
# seconds to a target position, paused there (digits jump to tenths of the episode; Right and Left step)
function G4P-At([string]$Digit) { [void](KM-Press $Digit); Start-Sleep -Milliseconds 700; [void](Wait-Until { if ($null -ne (G4P-Pos)) { $true } } -Seconds 3) }

# ---- the start: the series not watched yet, a remembered sign-in, the season page, its first episode -------------------------
$g4pSeries = Invoke-Server '/__harness/played' 'POST' @{ series = $fixture.series.id; played = $false }
$g4pEps = @($g4pSeries.episodes | Where-Object { $_.season -eq 1 } | Sort-Object index)
$g4pE1 = $g4pEps[0]; $g4pE2 = $g4pEps[1]
Say "$(G4-Id): $($fixture.series.name) marked not watched; S1 E1 '$($g4pE1.name)', S1 E2 '$($g4pE2.name)', $([int]$g4pE1.runtime) s each"
G4-SignInRemembered
Step-Season $false
Step-Play
$env:NIXIE_MATRIX_ONLY = 'helpers'
. (Join-Path $g4Repo 'tools\ui-harness\offscreen\then-keys-matrix.ps1')
$env:NIXIE_MATRIX_ONLY = $null
KM-SetWindowed
KM-HomeKeyboard

G4-Part 'playpause' {
    KM-SetPaused $true
    # Space plays: the button offers Pause, the shown time moves
    $t0 = G4P-Shown
    [void](KM-Press 'Space')
    $on = Wait-Until { if (-not (G4P-Paused) -and (G4P-PlayName) -eq 'Pause') { $true } } -Seconds 4 -PollMs 150
    Start-Sleep -Milliseconds 2200
    $t1 = G4P-Shown
    G4-Check 'Space' 'plays: Play/Pause offers Pause and the elapsed time moves' ([bool]$on -and $null -ne $t0 -and $t1 -gt $t0) "$t0 s -> $t1 s; $(G4P-Desc)"
    # Space again pauses: Play offered, the time stays
    [void](KM-Press 'Space')
    $off = Wait-Until { if ((G4P-Paused) -and (G4P-PlayName) -eq 'Play') { $true } } -Seconds 4 -PollMs 150
    $t2 = G4P-Shown; Start-Sleep -Milliseconds 2000; $t3 = G4P-Shown
    G4-Check 'Space again' 'pauses: Play/Pause offers Play and the elapsed time stays' ([bool]$off -and $null -ne $t2 -and $t2 -eq $t3) "$t2 s -> $t3 s; $(G4P-Desc)"
    # the button itself (a click through the dev build's pointer hook), both ways
    [void](KM-Click 'PlayPauseButton')
    $on = Wait-Until { if (-not (G4P-Paused) -and (G4P-PlayName) -eq 'Pause') { $true } } -Seconds 4 -PollMs 150
    G4-Check 'Play/Pause clicked' 'plays' ([bool]$on) (G4P-Desc)
    [void](KM-Click 'PlayPauseButton')
    $off = Wait-Until { if ((G4P-Paused) -and (G4P-PlayName) -eq 'Play') { $true } } -Seconds 4 -PollMs 150
    G4-Check 'Play/Pause clicked again' 'pauses' ([bool]$off) (G4P-Desc)
    # the shown time is mpv's
    $s = G4P-Shown; $p = G4P-Pos
    G4-Check 'paused' 'the elapsed time shown is where the video is' ($null -ne $s -and $null -ne $p -and [math]::Abs($s - $p) -le 1.5) "shown $s s, mpv $([math]::Round([double]$p, 1)) s"
}

G4-Part 'seek' {
    KM-SetPaused $true
    G4P-At '2'
    $a = G4P-Shown
    [void](KM-Press 'Right')
    $b = Wait-Until { $x = G4P-Shown; if ($null -ne $x -and $x -ge $a + 3) { $x } } -Seconds 3 -PollMs 150
    G4-Check 'Right' 'seeks forward: the elapsed time shown jumps ahead' ([bool]$b -and $b -le $a + 15) "$a s -> $b s"
    $a = G4P-Shown
    [void](KM-Press 'Left')
    $b = Wait-Until { $x = G4P-Shown; if ($null -ne $x -and $x -le $a - 3) { $x } } -Seconds 3 -PollMs 150
    G4-Check 'Left' 'seeks back: the elapsed time shown jumps back' ([bool]$b -and $b -ge $a - 15) "$a s -> $b s"
    # a click on the seek bar at 60 %: the time shown near 60 % of the episode
    $bar = G4P-Bar 'SeekBar'
    if (-not $bar) { throw 'no seek bar' }
    $r = [OffscreenUi]::RectOf($bar)
    [void][KeysMatrix]::Pointer($script:hwnd, 1, [int]($r.X + $r.Width * 0.6), [int]($r.Y + $r.Height / 2), 0)
    $want = 0.6 * $g4pE1.runtime
    $b = Wait-Until { $x = G4P-Shown; if ($null -ne $x -and [math]::Abs($x - $want) -le 25) { $x } } -Seconds 4 -PollMs 200
    G4-Check 'a click on the seek bar at 60 %' 'the elapsed time shown is near 60 % of the episode' ([bool]$b) "shown $(G4P-Shown) s, want about $([int]$want) s; mpv $([math]::Round([double](G4P-Pos), 1)) s"
    $s = G4P-Shown; $p = G4P-Pos
    G4-Check 'after the seeks' 'the elapsed time shown is where the video is' ($null -ne $s -and $null -ne $p -and [math]::Abs($s - $p) -le 1.5) "shown $s s, mpv $([math]::Round([double]$p, 1)) s"
}

# the Audio and subtitles menu: a track picked there plays, and the menu marks it when opened again
# (1.7.5 gate fix) Cinematic has its own panel for them (CinematicPlayerPanel, 1.4.0's C-m9), not the shared flyout
# (TracksMenu): buttons named "Audio: <track>" and "Subtitles: <track>", ", in use" on the one playing. The release verify's
# Cinematic waited for TracksMenu and failed its four track cases.
function G4P-PanelName([string]$Group, [string]$Starts) { "$(if ($Group -eq 'AudioTracks') { 'Audio' } else { 'Subtitles' }): $Starts" }
function G4P-TracksOpen { [bool](Find-Ui -Id 'TracksMenu') -or [bool](Find-Ui -Name 'Audio: ' -Type 'Button' -Contains) }
function G4P-OpenTracks {
    $b = G4P-Bar 'TracksButton'
    if (-not $b) { throw 'no Audio and subtitles button' }
    [void](Invoke-Element $b)
    Wait-Until { if (G4P-TracksOpen) { $true } } -Seconds 4 -PollMs 150
}
function G4P-Radio([string]$Group, [string]$Starts) {
    $g = Find-Ui -Id $Group
    if (-not $g) {
        # Cinematic's panel: its button for the track
        $want = G4P-PanelName $Group $Starts
        return @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
                Where-Object { try { $_.Cached.Name.StartsWith($want) -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1
    }
    @($g.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton))) |
            Where-Object { $_.Current.Name.StartsWith($Starts) }) | Select-Object -First 1
}
function G4P-Marked($Radio) {
    $p = $null
    if ($Radio -and $Radio.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { return $p.Current.IsSelected }
    if ($Radio -and $Radio.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { return $p.Current.ToggleState -eq 'On' }
    # Cinematic's panel: the button of the track playing says so in its name
    if ($Radio) { try { return [bool]($Radio.Current.Name -match ', in use$') } catch { } }
    $false
}
function G4P-CloseMenu {
    [void](KM-Press 'Esc')
    [void](Wait-Until { if (-not (G4P-TracksOpen)) { $true } } -Seconds 3 -PollMs 150)
}
function G4P-Pick([string]$Case, [string]$Group, [string]$Starts, [string]$Prop, [scriptblock]$Good) {
    if (-not (G4P-OpenTracks)) { G4-Check $Case 'the Audio and subtitles menu opens' $false ''; return }
    $before = [KeysMatrix]::Get($Prop)
    $radio = G4P-Radio $Group $Starts
    if (-not $radio) { G4-Check $Case "the menu lists '$Starts'" $false ''; G4P-CloseMenu; return }
    [void](Invoke-Element $radio)
    $now = Wait-Until { $v = [KeysMatrix]::Get($Prop); if (& $Good $v $before) { $v } } -Seconds 6 -PollMs 200
    G4-Check $Case "plays the track picked ('$Starts')" ([bool]$now) "mpv $Prop $before -> $([KeysMatrix]::Get($Prop))"
    G4P-CloseMenu
    $closed = -not (G4P-TracksOpen)
    [void](G4P-OpenTracks)
    $marked = G4P-Marked (G4P-Radio $Group $Starts)
    G4-Check $Case "the menu marks '$Starts' when opened again (and Esc had closed it)" ($marked -and $closed) "marked $marked, closed by Esc $closed"
    G4P-CloseMenu
}

G4-Part 'tracks' {
    KM-SetPaused $true
    G4P-Pick 'audio' 'AudioTracks' 'Japanese' 'aid' { param($v, $b) $v -and $v -ne $b -and $v -ne 'no' }
    G4P-Pick 'audio back' 'AudioTracks' 'English' 'aid' { param($v, $b) $v -and $v -ne $b -and $v -ne 'no' }
    G4P-Pick 'subtitles' 'SubtitleTracks' 'Spanish' 'sid' { param($v, $b) $v -and $v -ne $b -and $v -notin @('no', 'false') }
    G4P-Pick 'subtitles off' 'SubtitleTracks' 'Off' 'sid' { param($v, $b) $v -in @('no', 'false') }
}

G4-Part 'episodes' {
    KM-SetPaused $false
    $at = Get-Epoch
    [void](KM-Press 'Shift+N')
    $e2 = Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE2.name)") { $t } } -Seconds 20 -PollMs 300
    G4-Check 'Shift+N' 'the next episode plays: its title in the player''s top bar' ([bool]$e2) "shown '$(G4P-Episode)', want '... $($g4pE2.name)'"
    [void](Wait-Until { @(G4P-Reports $at 'report-playing' | Where-Object { $_.Item -eq $g4pE2.id }).Count } -Seconds 15 -PollMs 300)
    $stops = @(G4P-Reports $at 'report-stopped'); $plays = @(G4P-Reports $at 'report-playing')
    G4-Check 'Shift+N' 'the server hears E1 stopped and E2 playing, from the backend only' (@($stops | Where-Object { $_.Item -eq $g4pE1.id }).Count -eq 1 -and @($plays | Where-Object { $_.Item -eq $g4pE2.id }).Count -ge 1 -and @(@($stops) + @($plays) | Where-Object { $_.Origin -ne 'backend' }).Count -eq 0) "stopped: $(($stops | ForEach-Object { "$(G4P-Short $_.Item) by $($_.Origin)" }) -join ', '); playing: $(($plays | ForEach-Object { "$(G4P-Short $_.Item) by $($_.Origin)" }) -join ', ') (E1 $(G4P-Short $g4pE1.id), E2 $(G4P-Short $g4pE2.id))"
    Start-Sleep -Seconds 2
    [void](KM-Press 'Shift+P')
    $e1 = Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE1.name)") { $t } } -Seconds 20 -PollMs 300
    G4-Check 'Shift+P' 'the previous episode plays again' ([bool]$e1) "shown '$(G4P-Episode)'"
    Start-Sleep -Seconds 2
    $next = G4P-Bar 'NextButton'
    G4-Check 'Next button' "named after what it plays" ($next -and $next.Current.Name -like "Next episode:*$($g4pE2.name)") "$(if ($next) { $next.Current.Name })"
    if ($next) {
        [void](KM-Click 'NextButton')
        $e2 = Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE2.name)") { $t } } -Seconds 20 -PollMs 300
        G4-Check 'Next button' 'clicked, the next episode plays' ([bool]$e2) "shown '$(G4P-Episode)'"
        Start-Sleep -Seconds 2
        [void](KM-Press 'Shift+P')
        [void](Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE1.name)") { $t } } -Seconds 20 -PollMs 300)
        Start-Sleep -Seconds 2
    }
}

G4-Part 'stop' {
    # E1 at 20 %, paused; Back: the season page, one Stopped with that position, the row says how much is watched. The series
    # not watched again first (as from another device): a part before may have played E1 past 90 % (watched, as Jellyfin keeps it)
    [void](Invoke-Server '/__harness/played' 'POST' @{ series = $fixture.series.id; played = $false })
    $title = G4P-Episode
    if ($title -notlike "*$($g4pE1.name)") { [void](KM-Press 'Shift+P'); [void](Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE1.name)") { $t } } -Seconds 20 -PollMs 300); Start-Sleep -Seconds 2 }
    KM-SetPaused $true
    G4P-At '2'
    $p = G4P-Pos
    $at = Get-Epoch
    [void](KM-Press 'BackKey')
    $row = Wait-Until { Find-EpisodeRow } -Seconds 15 -PollMs 200
    $script:page = 'season'
    G4-Check 'Back' 'the player closes: the season page' ([bool]$row) ''
    [void](Wait-Until { @(G4P-Reports $at 'report-stopped').Count } -Seconds 15 -PollMs 200)
    Start-Sleep -Seconds 1
    $stops = @(G4P-Reports $at 'report-stopped')
    G4-Check 'Back' 'one Stopped, from the backend, at the position left' ($stops.Count -eq 1 -and $stops[0].Origin -eq 'backend' -and $stops[0].Item -eq $g4pE1.id -and [math]::Abs($stops[0].Pos - $p) -le 3) "$($stops.Count) Stopped: $(($stops | ForEach-Object { "$($_.Origin) at $([math]::Round([double]$_.Pos, 1)) s" }) -join ', '); left at $([math]::Round([double]$p, 1)) s"
    $pct = [int][math]::Round(100 * $p / $g4pE1.runtime)
    # (1.7.5 gate fix: the row as it settles within 10 s, not its first "% watched": the release verify read '1% watched' at
    # once, the page kept for Back shown before the player's Stopped report; whether the row then follows is what this says)
    $named = Wait-Until { $r = Find-Ui -Name $g4pE1.name -Type 'ListItem' -Contains -OffscreenToo; if ($r -and $r.Current.Name -match '(\d+)% watched' -and [math]::Abs([int]$Matches[1] - $pct) -le 2) { $r } } -Seconds 10 -PollMs 300
    if (-not $named) { $named = Find-Ui -Name $g4pE1.name -Type 'ListItem' -Contains -OffscreenToo }
    $shownPct = if ($named -and $named.Current.Name -match '(\d+)% watched') { [int]$Matches[1] } else { $null }
    $rowNow = Find-Ui -Name $g4pE1.name -Type 'ListItem' -Contains -OffscreenToo
    G4-Check 'the season page after Back' 'the episode''s row says how much is watched' ($null -ne $shownPct -and [math]::Abs($shownPct - $pct) -le 2) "row: '$(if ($rowNow) { $rowNow.Current.Name } else { 'none' })'; left at $pct %"
    if ($null -eq $shownPct) {
        Capture-Page "$(G4-Id)-season-after-back"
        # R loads the page again: what the server says now
        Key 0x52
        $again = Wait-Until { $r = Find-Ui -Name $g4pE1.name -Type 'ListItem' -Contains -OffscreenToo; if ($r -and $r.Current.Name -match '\d+% watched') { $r } } -Seconds 10 -PollMs 300
        Say "$(G4-Id): after R the row reads '$(if ($again) { $again.Current.Name } else { (Find-Ui -Name $g4pE1.name -Type 'ListItem' -Contains -OffscreenToo).Current.Name })'"
    }
    $script:g4pLeftAt = $p
}

# which of Home's elements named after the episode leads to its page, best first: a card saying its share watched, a hero's
# "Details of ..." (Editorial), a non-button "Resume ..." (a card named so); never a button "Resume ..." (a hero's: it plays)
# Drive-in's screen features it (its row leaves it out): the screen's "More info about <show>" opens the episode's page
function G4P-ContinuePick($Found) {
    $show = @($Found | Where-Object { $_.Button -and $_.Name -match '^Resume (.+?) S\d+ E\d+' } | ForEach-Object { if ($_.Name -match '^Resume (.+?) S\d+ E\d+') { $Matches[1] } }) | Select-Object -First 1
    @(@($Found | Where-Object { $_.Name -match '\d+% watched' }) + @($Found | Where-Object { $_.Name -match '^Details of ' }) +
        @($Found | Where-Object { $_.Name -match '^Resume' -and -not $_.Button }) + @($Found | Where-Object { $show -and $_.Name -eq "More info about $show" }))
}
# Home's Continue watching: the episode's card (any theme's: its name has the title and a share watched, or Resume)
function G4P-ContinueCard([string]$Title) {
    Wait-Until {
        foreach ($k in 0, 1) {
            $all = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
                    try { $n = $_.Cached.Name; $n -and (($n.Contains($Title) -and ($n -match '\d+% watched' -or $n -match '^Resume' -or $n -match '^Details of ')) -or $n -like 'More info about *') } catch { $false } })
            # (1.7.5 gate fix) a card before a hero's own buttons: Drive-in's and Editorial's hero feature the episode in progress,
            # and its "Resume ..." button plays at once (the release verify pressed it: "opened no page with Resume"); Editorial's
            # card for it is its hero's "Details of ..." (the episode is left out of Up next)
            $pick = @(G4P-ContinuePick @($all | ForEach-Object { [pscustomobject]@{ Name = $_.Cached.Name; Button = ($_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::Button); El = $_ } }))
            if ($pick) { return $pick[0].El }
            [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $(if ($k -eq 0) { 40 } else { 0 })); Start-Sleep -Milliseconds 400
        }
    } -Seconds 20 -PollMs 300
}
# from Home's card to playing: the card (its item's page, or Editorial's details in place), then Resume there
function G4P-ResumeFromHome([string]$Case, [string]$Title, [double]$From) {
    Open-Nav 'Home'; Start-Sleep -Seconds 3
    $card = G4P-ContinueCard $Title
    G4-Check $Case "Home's Continue watching shows the episode with how much is watched" ([bool]$card) "$(if ($card) { $card.Current.Name } else { "nothing on Home names '$Title' with a share watched" })"
    if (-not $card) { return }
    $how = Invoke-Element $card
    # Editorial opens a card's details in place: Full details opens the page
    $full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
    if ($full) { [void](Invoke-Element $full) }
    # the episode's own page (its title), so Home's hero's Resume is never the one pressed
    if (-not (Find-Ui -Id 'PlayPauseButton') -and -not (Wait-ItemPage $Title 15)) { G4-Check $Case 'the card opens the episode' $false "the card ($how) opened no page titled '$Title'"; return }
    $resume = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Cached.Name -match '^Resume' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1 } -Seconds 12 -PollMs 300
    if (-not (Find-Ui -Id 'PlayPauseButton')) {
        if (-not $resume) { G4-Check $Case 'its page offers Resume' $false "the card ($how) opened no page with Resume"; return }
        Say "$(G4-Id): ${Case}: '$($resume.Current.Name)'"
        [void](Invoke-Element $resume)
    }
    $script:page = 'player'
    $shown = Wait-Until { Find-Ui -Id 'PlayPauseButton' } -Seconds 40 -PollMs 200
    KM-ConnectPipe
    $pos = Wait-Until { $x = G4P-Pos; if ($null -ne $x -and $x -gt 1) { $x } } -Seconds 30 -PollMs 300
    G4-Check $Case 'Resume plays from where it was left' ($shown -and $null -ne $pos -and [math]::Abs($pos - $From) -le 8) "mpv at $([math]::Round([double]$pos, 1)) s, left at $([math]::Round($From, 1)) s"
    $s = G4P-Shown
    G4-Check $Case 'and the elapsed time shown says so' ($null -ne $s -and [math]::Abs($s - $From) -le 10) "shown $s s"
    KM-HomeKeyboard
}

G4-Part 'restart' {
    if ($null -eq $script:g4pLeftAt) { throw 'the stop part left no position' }
    G4P-ResumeFromHome 'Continue watching' $g4pE1.name $script:g4pLeftAt
    # close Nixie mid-play: one Stopped at the position, a clean exit; started again, Resume from Home goes on from there
    KM-SetPaused $false; Start-Sleep -Seconds 3; KM-SetPaused $true
    $p = G4P-Pos
    [KeysMatrix]::Close()
    $r = G4-Restart 'close mid-play'
    G4-Check 'close mid-play' 'Nixie exits cleanly (exit code 0)' ($r.Code -eq 0) "$(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { 'still running' }) after $($r.ExitMs) ms"
    $stops = @(G4P-Reports $r.Since 'report-stopped')
    G4-Check 'close mid-play' 'one Stopped, from the backend, at the position left' ($stops.Count -eq 1 -and $stops[0].Origin -eq 'backend' -and [math]::Abs($stops[0].Pos - $p) -le 3) "$($stops.Count) Stopped: $(($stops | ForEach-Object { "$($_.Origin) at $([math]::Round([double]$_.Pos, 1)) s" }) -join ', '); left at $([math]::Round([double]$p, 1)) s"
    G4-Check 'close mid-play' 'started again: Home, still signed in' ($r.Page -eq 'home') "page: $($r.Page)"
    if ($r.Page -eq 'home') { G4P-ResumeFromHome 'after the restart' $g4pE1.name $p }
}

# near the end (the fake server marks the last 40 s as credits): the Up next prompt, its countdown, and Keep watching
function G4P-NearEnd([int]$Before) {
    KM-SetPaused $true
    G4P-At '9'
    # from 90 % to $Before seconds ahead of the credits, by Right (5 s steps) and Left
    $want = $g4pE1.runtime - 40 - $Before
    for ($i = 0; $i -lt 12 -and (G4P-Pos) -lt $want - 3; $i++) { [void](KM-Press 'Right'); Start-Sleep -Milliseconds 300 }
    KM-SetPaused $false
}
function G4P-Pill { $p = Find-Ui -Id 'NextPill'; if ($p -and -not $p.Current.IsOffscreen) { $p } }
G4-Part 'upnext' {
    $e = G4P-Episode
    G4P-NearEnd 6
    $pill = Wait-Until { G4P-Pill } -Seconds 20 -PollMs 200
    $name = if ($pill) { $pill.Current.Name } else { '' }
    G4-Check 'Up next' 'near the end the Next prompt shows, counting down, named after the next episode' ($pill -and $name -match '^Next episode in \d+' -and $name -like "*$($g4pE2.name)*") "'$name' (playing '$e')"
    $keep = Find-Ui -Id 'CancelNextButton'
    G4-Check 'Up next' 'Keep watching (or Watch credits) shows beside it' ([bool]$keep) "$(if ($keep) { $keep.Current.Name })"
    $at = Get-Epoch
    $e2 = Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE2.name)") { $t } } -Seconds 25 -PollMs 300
    G4-Check 'Up next' 'the countdown plays the next episode by itself' ([bool]$e2) "shown '$(G4P-Episode)' $([int]((Get-Epoch) - $at)) s after the prompt"
    $gone = -not (G4P-Pill)
    G4-Check 'Up next' 'the prompt is gone once the next episode plays' $gone ''
    # the way out: Keep watching stops the countdown, and the episode plays on to its end with no next one started
    Start-Sleep -Seconds 2
    [void](KM-Press 'Shift+P'); [void](Wait-Until { $t = G4P-Episode; if ($t -like "*$($g4pE1.name)") { $t } } -Seconds 20 -PollMs 300)
    Start-Sleep -Seconds 2
    G4P-NearEnd 6
    $pill = Wait-Until { G4P-Pill } -Seconds 20 -PollMs 200
    $keep = Wait-Until { Find-Ui -Id 'CancelNextButton' } -Seconds 5 -PollMs 150
    if ($keep) { [void](Invoke-Element $keep) }
    Start-Sleep -Seconds 14
    $still = G4P-Episode
    $counting = Find-Ui -Id 'CancelNextButton'
    G4-Check 'Up next, Keep watching' 'the countdown stops: the same episode plays on' ($pill -and $keep -and $still -like "*$($g4pE1.name)" -and -not $counting) "shown '$still', Keep watching still offered $([bool]$counting)"
}

[KeysMatrix]::Close()
