# sg-qa (1.5): a -Then script of tools\ui-harness\run-offscreen.ps1 for a real QA server (-QaServer 10.11|12):
#   $env:NIXIE_LAYOUT = 'Classic'
#   run-offscreen.ps1 -QaServer 12 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script movie `
#       -Then tools\qa-servers\then-qa-play-movie.ps1 -NoA11y
# On the showcase movie's page (the `movie` step): Play, about -QaPlaySeconds (10) of playback, the Back key, and what
# the user and the server see: the video showing, the window back on the movie page with its Play button, mpv silent
# (AO: [null]), and the server hearing one Playing and one Stopped for the session, both from Nixie's backend (the one
# reporter), at about the position played. Dot-sourced by drive.ps1: its helpers, and no top-level names of its own
# variables (prefix qm).

$qmSeconds = if ($env:NIXIE_QA_PLAY_SECONDS) { [double]$env:NIXIE_QA_PLAY_SECONDS } else { 10 }
if ($script:page -ne 'movie') { Step-Movie }
$qmPlay = Wait-Ui -Id 'PlayButton' -Type 'Button' -Seconds 15
Check 'qa movie: the Play button is on the movie page' ([bool]$qmPlay) $(if ($qmPlay) { "'$($qmPlay.Current.Name)' on $($fixture.movie.name)" })
if ($qmPlay) {
    $qmAt = Get-Epoch
    [void](Invoke-Element $qmPlay)
    Say "qa movie: Play invoked on '$($fixture.movie.name)' (player controls: $PlayerControls)"
    $script:page = 'player'
    $qmLit = if ($PlayerControls -eq 'Mpv') {
        Wait-Until { $v = [OffscreenUi]::VideoLit($script:hwnd, $script:ui.Id); if ($v -gt 20) { $v } } -Seconds 60 -PollMs 100
    }
    else {
        Wait-Until { $v = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.3, 0.3, 0.7, 0.7); if ($v -gt 20) { $v } } -Seconds 60 -PollMs 100
    }
    $timings.qa_first_frame_ms = Ms $qmAt (Get-Epoch)
    Check 'qa movie: the video shows' ([bool]$qmLit) "$qmLit% of the picture not black after $($timings.qa_first_frame_ms) ms"
    $qmPlaying = Wait-Until { @(Get-Requests $qmAt | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 20 -PollMs 200
    $timings.qa_playing_report_ms = if ($qmPlaying) { Ms $qmAt $qmPlaying.t } else { $null }
    Start-Sleep -Seconds 3
    Capture-Page 'qa-player'
    $qmAo = Wait-Until {
        $f = Join-Path $DataDir 'Logs\mpv-backend.log'
        $l = @(if (Test-Path $f) { Get-Content -LiteralPath $f -Encoding utf8 | Where-Object { $_ -match 'AO: \[' -and $_ -notmatch 'Setting option' } })
        if ($l) { , $l }
    } -Seconds 10 -PollMs 250
    $qmLoud = @($qmAo | Where-Object { $_ -notmatch 'AO: \[null\]' })
    Check 'qa movie: silent (mpv opened no audio device, AO: [null])' ($qmAo -and -not $qmLoud) $(if ($qmLoud) { $qmLoud[0] } elseif ($qmAo) { 'AO: [null]' } else { 'no AO line in mpv-backend.log' })
    # the rest of the time playing, then the Back key (FluentFin's controls) or mpv's q
    $qmLeft = $qmSeconds - ((Get-Epoch) - $(if ($qmPlaying) { $qmPlaying.t } else { $qmAt }))
    if ($qmLeft -gt 0) { Start-Sleep -Milliseconds ([int]($qmLeft * 1000)) }
    $qmStopAt = Get-Epoch
    if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x51 'q' } else { Key 0xA6 }
    $qmBack = Wait-Until { Find-Ui -Id 'PlayButton' -Type 'Button' } -Seconds 20 -PollMs 150
    $qmHow = if ($PlayerControls -eq 'Mpv') { "mpv's q" } else { 'the Back key' }
    Check "qa movie: $qmHow left the player (the movie page and its Play button back)" ([bool]$qmBack) $(if ($qmBack) { "'$($qmBack.Current.Name)'" })
    $script:page = 'movie'
    [void](Wait-Until { @(Get-Requests $qmStopAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
    Start-Sleep -Seconds 2
    $qmReqs = @(Get-Requests $qmAt)
    $qmStarts = @($qmReqs | Where-Object { $_.route -eq 'report-playing' })
    $qmStops = @($qmReqs | Where-Object { $_.route -eq 'report-stopped' })
    $qmReporters = @($qmReqs | Where-Object { $_.route -like 'report-*' } | ForEach-Object { Get-Origin $_ } | Sort-Object -Unique)
    $qmPos = if ($qmStops.Count -and $qmStops[0].PSObject.Properties['report'] -and $qmStops[0].report.position) { [double]$qmStops[0].report.position / 1e7 } else { $null }
    $qmSessions = @($qmStarts + $qmStops | Where-Object { $_.PSObject.Properties['report'] } | ForEach-Object { $_.report.play_session } | Sort-Object -Unique)
    Check 'qa movie: the server heard one Playing and one Stopped, from the backend only' ($qmStarts.Count -eq 1 -and $qmStops.Count -eq 1 -and ($qmReporters -join ',') -eq 'backend' -and $qmSessions.Count -eq 1) `
        ("{0} Playing, {1} Stopped; reports from {2}; play sessions {3}" -f $qmStarts.Count, $qmStops.Count, $(if ($qmReporters) { $qmReporters -join ', ' } else { 'nobody' }), $qmSessions.Count)
    Check "qa movie: stopped at about the time played (${qmSeconds} s)" ($null -ne $qmPos -and $qmPos -ge ($qmSeconds - 3) -and $qmPos -le ($qmSeconds + 8)) $(if ($null -ne $qmPos) { '{0:N1} s' -f $qmPos } else { 'no position in the Stopped report' })
    # the server's own view, as its ordinary user: the movie's resume point (what Nixie's Resume shows next time)
    $qmStream = @($qmReqs | Where-Object { $_.route -eq 'stream' })
    Say ("qa movie: {0} stream request(s) through the tap, {1:N1} MB; first frame {2} ms; Playing reported {3} ms after Play" -f $qmStream.Count,
        (($qmStream | Measure-Object bytes -Sum).Sum / 1MB), $timings.qa_first_frame_ms, $timings.qa_playing_report_ms)
    Capture-Page 'qa-after-player'
}
