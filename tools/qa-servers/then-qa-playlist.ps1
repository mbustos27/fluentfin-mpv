# 1.5 qacheck: a -Then script of tools\ui-harness\run-offscreen.ps1 for a real QA server (-QaServer 10.11|12): a real
# playlist, the film that needs a transcode and an audio track change, played through Nixie itself, muted.
#   run-offscreen.ps1 -QaServer 12 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script wait:1 `
#       -Then tools\qa-servers\then-qa-playlist.ps1 -NoA11y
#   with -Before tools\qa-servers\before-qa-transcode.ps1: the backend transcodes everything (the shim's
#   always_transcode), so the audio track change restarts a transcode
# Parts (NIXIE_QA_PARTS, comma-separated, in this order; all four by default):
#   playlist  qa-user's "QA Playlist" from the Playlists library: its page shows its items; Play starts the first one not
#             watched; Shift+N goes on to the playlist's next item (not the episode's series); the Back key returns to
#             the playlist's page. The server heard one Playing and one Stopped per item, from the backend only.
#   shuffle   the same page's Shuffle: an item of the playlist plays, Shift+N plays another of its items (not an
#             episode's series), the Back key returns to the page; one Playing and one Stopped per item.
#   tears     Tears of Steel (HEVC Main 10, DTS) from the Movies library: the video shows, direct play or a transcode
#             (said which; with before-qa-transcode.ps1 it must be a transcode), the Back key back to its page.
#   audio     the multi-track episode (the showcase series' first): B changes the audio track while it plays. What the
#             user sees (the Audio and subtitles panel's choice), what the backend reports (AudioStreamIndex), and on a
#             transcode the restart (a PlaybackInfo and a stream for the new track) with the video still playing after it.
# Each part ends with what the server itself recorded, read as qa-admin through the tools (never printed): its activity
# log's "is playing" and "has finished playing" entries for the item, one each. Variables here start with qp.

$qpParts = if ($env:NIXIE_QA_PARTS) { @($env:NIXIE_QA_PARTS -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) } else { @('playlist', 'shuffle', 'tears', 'audio') }
$qpSeconds = if ($env:NIXIE_QA_PLAY_SECONDS) { [double]$env:NIXIE_QA_PLAY_SECONDS } else { 6 }
if (-not $QaUpstream) { throw 'then-qa-playlist.ps1 runs against a QA server only (run-offscreen.ps1 -QaServer 10.11|12)' }
. (Join-Path $PSScriptRoot 'qa-common.ps1')
# keys as a real keyboard sends them, to the focused window with Shift in the UI thread's keyboard state (Shift+N)
if (-not (Get-Command CK-Key -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot '..\ui-harness\offscreen\chord-keys.ps1') }
$qpVersion = @($script:QaPorts.Keys | Where-Object { $QaUpstream.TrimEnd('/') -eq (Get-QaUrl $_) }) | Select-Object -First 1
if (-not $qpVersion) { throw "then-qa-playlist: $QaUpstream is not a QA server" }
# the run's backend transcodes everything (before-qa-transcode.ps1)
$qpConf = Join-Path $DataDir 'mpv-backend\conf.json'
$qpForced = (Test-Path -LiteralPath $qpConf) -and [bool]((Get-Content -LiteralPath $qpConf -Raw | ConvertFrom-Json).always_transcode)
Say "qa: $qpVersion; parts $($qpParts -join ', '); $(if ($qpForced) { 'the backend transcodes everything (always_transcode)' } else { 'the backend''s own play decisions' })"

function Qp-Id([string]$Id) { "$Id".Replace('-', '').ToLowerInvariant() }
# a device of this run's own for the tools' sign-ins: Jellyfin ends a device's older session when the same user signs in
# from it again, and two runs at once with one device id signed each other out
$qpDevice = "nixie-qacheck-$PID"
# the server's activity log entries of playback since $Since (epoch s), as qa-admin; the admin's session ends with it
function Qp-Activity([double]$Since) {
    $admin = Connect-Qa $qpVersion 'admin' "$qpDevice-admin"
    try {
        $min = [DateTimeOffset]::FromUnixTimeMilliseconds([long](($Since - 2) * 1000)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
        $r = Invoke-Qa $qpVersion "/System/ActivityLog/Entries?minDate=$([Uri]::EscapeDataString($min))&limit=500" -Token $admin.Token -Device "$qpDevice-admin"
        @($r.Json.Items | Where-Object { $_.Type -in @('VideoPlayback', 'VideoPlaybackStopped', 'AudioPlayback', 'AudioPlaybackStopped') })
    }
    finally { [void](Invoke-Qa $qpVersion '/Sessions/Logout' 'POST' -Token $admin.Token -Device "$qpDevice-admin") }
}
# what the server recorded for each item: one start and one stop each (polled a few seconds: the log is written after the answer)
function Qp-CheckRecorded([string]$Label, [double]$Since, $Items) {
    $want = @($Items | ForEach-Object { Qp-Id $_.id })
    $ok = Wait-Until {
        $script:qpAct = @(Qp-Activity $Since)
        $good = $true
        foreach ($w in $want) {
            $s = @($script:qpAct | Where-Object { (Qp-Id $_.ItemId) -eq $w -and $_.Type -like '*Playback' })
            $e = @($script:qpAct | Where-Object { (Qp-Id $_.ItemId) -eq $w -and $_.Type -like '*Stopped' })
            if ($s.Count -ne 1 -or $e.Count -ne 1) { $good = $false }
        }
        if ($good) { $true }
    } -Seconds 8 -PollMs 1000
    $detail = @(foreach ($it in $Items) {
            $w = Qp-Id $it.id
            $s = @($script:qpAct | Where-Object { (Qp-Id $_.ItemId) -eq $w -and $_.Type -like '*Playback' }).Count
            $e = @($script:qpAct | Where-Object { (Qp-Id $_.ItemId) -eq $w -and $_.Type -like '*Stopped' }).Count
            "$($it.name): $s started, $e finished"
        }) -join '; '
    $others = @($script:qpAct | Where-Object { (Qp-Id $_.ItemId) -notin $want }) | ForEach-Object { $_.Name }
    Check "qa ${Label}: the server's activity log has one start and one finish per item" ([bool]$ok) ($detail + $(if ($others) { "; also: $($others -join ' | ')" }))
}
# the tap's reports since $Since, per item: Playing and Stopped counts, all from the backend
function Qp-CheckReports([string]$Label, [double]$Since, $Items) {
    $reqs = @(Get-Requests $Since | Where-Object { $_.route -like 'report-*' })
    $from = @($reqs | ForEach-Object { Get-Origin $_ } | Sort-Object -Unique)
    $ok = ($from -join ',') -eq 'backend'
    $detail = @(foreach ($it in $Items) {
            $w = Qp-Id $it.id
            $p = @($reqs | Where-Object { $_.route -eq 'report-playing' -and $_.PSObject.Properties['report'] -and (Qp-Id $_.report.item) -eq $w }).Count
            $s = @($reqs | Where-Object { $_.route -eq 'report-stopped' -and $_.PSObject.Properties['report'] -and (Qp-Id $_.report.item) -eq $w }).Count
            if ($p -ne 1 -or $s -ne 1) { $ok = $false }
            "$($it.name): $p Playing, $s Stopped"
        }) -join '; '
    Check "qa ${Label}: one Playing and one Stopped per item, from the backend only" $ok ("$detail; reports from $(if ($from) { $from -join ', ' } else { 'nobody' })")
}
# the video showing in FluentFin's player (the middle of the window not black), or mpv's own window
function Qp-WaitVideo([double]$Seconds = 60) {
    if ($PlayerControls -eq 'Mpv') { Wait-Until { $v = [OffscreenUi]::VideoLit($script:hwnd, $script:ui.Id); if ($v -gt 20) { $v } } -Seconds $Seconds -PollMs 100 }
    else { Wait-Until { $v = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.3, 0.3, 0.7, 0.7); if ($v -gt 20) { $v } } -Seconds $Seconds -PollMs 100 }
}
function Qp-WaitPlaying([double]$Since, [double]$Seconds = 30) {
    Wait-Until { @(Get-Requests $Since | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds $Seconds -PollMs 200
}
function Qp-Leave([scriptblock]$Back) {
    if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x51 'q' } else { Key 0xA6 }
    Wait-Until $Back -Seconds 20 -PollMs 150
}
# the stream requests since $Since: "transcode (HLS)" or "direct play"
function Qp-StreamKind([double]$Since) {
    $s = @(Get-Requests $Since | Where-Object { $_.route -eq 'stream' })
    if (-not $s) { return 'no stream request' }
    if (@($s | Where-Object { $_.path -match '\.m3u8$|/hls\d*/' }).Count) { return 'transcode (HLS)' }
    'direct play'
}

# --- playlist ------------------------------------------------------------------------------------------------------------
if ($qpParts -contains 'playlist') {
    $qpPl = $fixture.playlist
    Check 'qa playlist: the server has qa-user''s "QA Playlist" with its items' ($qpPl -and @($qpPl.items).Count -ge 2) $(if ($qpPl) { (@($qpPl.items) | ForEach-Object { "$($_.name) ($($_.type)$(if ($_.played) { ', watched' }))" }) -join ', ' })
    if ($qpPl -and @($qpPl.items).Count -ge 2) {
        $qpItems = @($qpPl.items)
        # from a known state: no item watched (earlier runs watched them: Play then started at the last, with no next
        # item for Shift+N), as qa-user, whose play state is the run's to change
        $qpU = Connect-Qa $qpVersion 'user' $qpDevice
        try {
            foreach ($qpIt in $qpItems) {
                $qpR = Invoke-Qa $qpVersion "/UserPlayedItems/$($qpIt.id)?userId=$($qpU.UserId)" 'DELETE' -Token $qpU.Token -Device $qpDevice
                if ($qpR.Status -lt 300) { $qpIt.played = $false }
            }
        }
        finally { [void](Invoke-Qa $qpVersion '/Sessions/Logout' 'POST' -Token $qpU.Token -Device $qpDevice) }
        Open-Library 'Playlists'
        $qpCard = Wait-Until { Find-MediaCard $qpPl.name } -Seconds 30 -PollMs 200
        Check 'qa playlist: the Playlists library shows its card' ([bool]$qpCard) $(if ($qpCard) { "'$($qpCard.Current.Name)'" })
        Capture-Page 'qa-playlists'
        Open-Card $qpPl.name
        $qpTitle = Wait-Until { $t = Find-Ui -Id 'TitleText'; if ($t -and $t.Current.Name -eq $qpPl.name) { $t } } -Seconds 20 -PollMs 150
        Check 'qa playlist: its page opened (the title says its name)' ([bool]$qpTitle) $qpPl.name
        # every item of the playlist is on its page (by its card's name)
        # (an episode's card is named "<series>, season 1 episode 1, <episode>")
        $qpShown = @($qpItems | Where-Object { $n = $_.name; Wait-Until { $c = Find-MediaCard $n; if (-not $c) { $c = Find-Ui -Name ", $n" -Type 'ListItem' -Contains -OffscreenToo }; $c } -Seconds 8 -PollMs 200 })
        Check 'qa playlist: its page shows every item' ($qpShown.Count -eq $qpItems.Count) ("{0} of {1}: {2}" -f $qpShown.Count, $qpItems.Count, (($qpShown | ForEach-Object { $_.name }) -join ', '))
        Capture-Page 'qa-playlist-page'
        # Play starts the first item not watched (all watched: the first, from the start)
        $qpStart = 0
        for ($i = 0; $i -lt $qpItems.Count; $i++) { if (-not $qpItems[$i].played) { $qpStart = $i; break } }
        if (@($qpItems | Where-Object { -not $_.played }).Count -eq 0) { $qpStart = 0 }
        $qpPlay = Wait-Ui -Id 'PlayButton' -Type 'Button' -Seconds 10
        Check 'qa playlist: Play names the item it starts' ($qpPlay -and $qpPlay.Current.Name -like "*$($qpItems[$qpStart].name)*") $(if ($qpPlay) { "'$($qpPlay.Current.Name)'" } else { 'no Play button' })
        if ($qpPlay) {
            $qpAt = Get-Epoch
            [void](Invoke-Element $qpPlay)
            $script:page = 'player'
            $qpLit = Qp-WaitVideo
            Check 'qa playlist: the video shows' ([bool]$qpLit) "$qpLit% not black"
            $qpFirst = Qp-WaitPlaying $qpAt
            Check 'qa playlist: Play started the first item not watched' ($qpFirst -and (Qp-Id $qpFirst.report.item) -eq (Qp-Id $qpItems[$qpStart].id)) `
                ("expected '{0}'; Playing for {1}; {2}" -f $qpItems[$qpStart].name, $(if ($qpFirst) { $qpFirst.report.item } else { 'nothing' }), (Qp-StreamKind $qpAt))
            Start-Sleep -Seconds $qpSeconds
            Capture-Page 'qa-playlist-player'
            # Shift+N: the playlist's next item
            $qpNextAt = Get-Epoch
            # (the last item: the playlist has no next one, and Shift+N plays nothing)
            $qpWantNext = if ($qpStart + 1 -lt $qpItems.Count) { $qpItems[$qpStart + 1] } else { $null }
            if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x3E '>' } else { CK-Key 'Shift+N' }
            $qpNext = Qp-WaitPlaying $qpNextAt $(if ($qpWantNext) { 30 } else { 8 })
            $qpNextName = if ($qpNext) { $n = Qp-Id $qpNext.report.item; $hit = $qpItems | Where-Object { (Qp-Id $_.id) -eq $n } | Select-Object -First 1; if ($hit) { $hit.name } else { "an item not in the playlist ($($qpNext.report.item))" } } else { 'nothing' }
            $qpNextOk = if ($qpWantNext) { $qpNext -and (Qp-Id $qpNext.report.item) -eq (Qp-Id $qpWantNext.id) } else { -not $qpNext }
            Check 'qa playlist: Shift+N plays the playlist''s next item' $qpNextOk "expected '$(if ($qpWantNext) { $qpWantNext.name } else { 'nothing (the last item)' })'; got $qpNextName"
            $qpLit2 = Qp-WaitVideo 30
            Check 'qa playlist: the next item''s video shows' ([bool]$qpLit2) "$qpLit2% not black"
            Start-Sleep -Seconds $qpSeconds
            $qpStopAt = Get-Epoch
            $qpBack = Qp-Leave { $t = Find-Ui -Id 'TitleText'; if ($t -and $t.Current.Name -eq $qpPl.name -and (Find-Ui -Id 'PlayButton' -Type 'Button')) { $t } }
            Check 'qa playlist: the Back key returns to the playlist''s page' ([bool]$qpBack)
            $script:page = 'playlist'
            [void](Wait-Until { @(Get-Requests $qpStopAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
            Start-Sleep -Seconds 2
            $qpPlayed = @($qpItems[$qpStart])
            if ($qpNext) { $qpPlayed += @($qpItems | Where-Object { (Qp-Id $_.id) -eq (Qp-Id $qpNext.report.item) }) }
            Qp-CheckReports 'playlist' $qpAt $qpPlayed
            Qp-CheckRecorded 'playlist' $qpAt $qpPlayed
            Capture-Page 'qa-playlist-after'
        }
    }
}

# --- Shuffle ------------------------------------------------------------------------------------------------------------
if ($qpParts -contains 'shuffle' -and $fixture.playlist -and @($fixture.playlist.items).Count -ge 2) {
    $qpPl = $fixture.playlist
    $qpIds = @($qpPl.items | ForEach-Object { Qp-Id $_.id })
    if ($script:page -ne 'playlist') {
        Open-Library 'Playlists'
        Open-Card $qpPl.name
        [void](Wait-Until { $t = Find-Ui -Id 'TitleText'; if ($t -and $t.Current.Name -eq $qpPl.name) { $t } } -Seconds 20 -PollMs 150)
    }
    $qpShuffle = Wait-Ui -Id 'ShuffleButton' -Type 'Button' -Seconds 10
    Check 'qa shuffle: the playlist''s page offers Shuffle' ([bool]$qpShuffle) $(if ($qpShuffle) { "'$($qpShuffle.Current.Name)'" })
    if ($qpShuffle) {
        $qpAt = Get-Epoch
        [void](Invoke-Element $qpShuffle)
        $script:page = 'player'
        $qpFirst = Qp-WaitPlaying $qpAt
        $qpLit = Qp-WaitVideo 60
        Check 'qa shuffle: an item of the playlist plays' ($qpFirst -and $qpIds -contains (Qp-Id $qpFirst.report.item) -and $qpLit) `
            $(if ($qpFirst) { "Playing for $($qpFirst.report.item); $qpLit% not black" } else { 'no Playing' })
        Start-Sleep -Seconds $qpSeconds
        $qpNextAt = Get-Epoch
        if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x3E '>' } else { CK-Key 'Shift+N' }
        $qpNext = Qp-WaitPlaying $qpNextAt
        $qpOk = $qpNext -and $qpIds -contains (Qp-Id $qpNext.report.item) -and (Qp-Id $qpNext.report.item) -ne (Qp-Id $qpFirst.report.item)
        Check 'qa shuffle: Shift+N plays another item of the playlist' $qpOk $(if ($qpNext) { "Playing for $($qpNext.report.item)" } else { 'nothing' })
        Start-Sleep -Seconds $qpSeconds
        $qpStopAt = Get-Epoch
        $qpBack = Qp-Leave { $t = Find-Ui -Id 'TitleText'; if ($t -and $t.Current.Name -eq $qpPl.name) { $t } }
        Check 'qa shuffle: the Back key returns to the playlist''s page' ([bool]$qpBack)
        $script:page = 'playlist'
        [void](Wait-Until { @(Get-Requests $qpStopAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
        Start-Sleep -Seconds 2
        $qpPlayed = @($qpPl.items | Where-Object { $i = Qp-Id $_.id; ($qpFirst -and $i -eq (Qp-Id $qpFirst.report.item)) -or ($qpNext -and $i -eq (Qp-Id $qpNext.report.item)) })
        Qp-CheckReports 'shuffle' $qpAt $qpPlayed
        Qp-CheckRecorded 'shuffle' $qpAt $qpPlayed
    }
}

# --- Tears of Steel ------------------------------------------------------------------------------------------------------
if ($qpParts -contains 'tears') {
    $qpT = $fixture.transcode
    Check 'qa tears: the server has Tears of Steel' ([bool]$qpT) $(if ($qpT) { $qpT.name })
    if ($qpT) {
        Open-Library $qpT.library
        Open-Card $qpT.name
        $qpPage = Wait-ItemPage $qpT.name
        Check 'qa tears: its page opened' ([bool]$qpPage) $qpT.name
        $script:page = 'movie-tears'
        $qpPlay = Wait-Ui -Id 'PlayButton' -Type 'Button' -Seconds 15
        if ($qpPlay) {
            $qpAt = Get-Epoch
            [void](Invoke-Element $qpPlay)
            $script:page = 'player'
            $qpLit = Qp-WaitVideo 90
            $timings.qa_tears_first_frame_ms = Ms $qpAt (Get-Epoch)
            Check 'qa tears: the video shows' ([bool]$qpLit) "$qpLit% not black after $($timings.qa_tears_first_frame_ms) ms"
            [void](Qp-WaitPlaying $qpAt)
            Start-Sleep -Seconds $qpSeconds
            Capture-Page 'qa-tears-player'
            $qpBack = Qp-Leave { Find-Ui -Id 'PlayButton' -Type 'Button' }
            Check 'qa tears: the Back key returns to its page' ([bool]$qpBack)
            $script:page = 'movie-tears'
            [void](Wait-Until { @(Get-Requests $qpAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
            Start-Sleep -Seconds 2
            # (after the stop: the tap logs a request when its body has been sent, and a direct play is one long GET)
            $qpKind = Qp-StreamKind $qpAt
            if ($qpForced) { Check 'qa tears: played as a transcode (the backend transcodes everything)' ($qpKind -eq 'transcode (HLS)') $qpKind }
            else { Say "qa tears: $qpKind (the backend's own decision; mpv plays HEVC Main 10 and DTS)" }
            Qp-CheckReports 'tears' $qpAt @($qpT)
            Qp-CheckRecorded 'tears' $qpAt @($qpT)
        }
        else { Check 'qa tears: Play on its page' $false 'no Play button' }
    }
}

# --- an audio track change while it plays -------------------------------------------------------------------------------
if ($qpParts -contains 'audio') {
    $qpEp = $fixture.episode
    # the episode not watched first (as qa-user), so what the viewing leaves on the server shows: its watched state and
    # resume point after the restart (qa-user's play state is the run's to change; nothing else is)
    $qpUser = Connect-Qa $qpVersion 'user' $qpDevice
    [void](Invoke-Qa $qpVersion "/UserPlayedItems/$($qpEp.id)?userId=$($qpUser.UserId)" 'DELETE' -Token $qpUser.Token -Device $qpDevice)
    # the series page shows the season of the next episode (on a QA server played before: Specials, or season 2): its
    # season picker's "Season 1" first, then the season's own page
    Step-Series $false
    $qpTube = Wait-Ui -Name $fixture.season.name -Type 'Button' -Seconds 10
    if ($qpTube) { Say "qa audio: the series page's '$($fixture.season.name)' ($(Invoke-Element $qpTube))"; Start-Sleep -Milliseconds 800 }
    Step-Season $false
    $qpAt = Get-Epoch
    Step-Play
    # the play step paused (FluentFin's controls) so they stay up: playing again
    if ($PlayerControls -eq 'FluentFin') {
        $pp = Find-Ui -Id 'PlayPauseButton'
        if ($pp -and $pp.Current.Name -eq 'Play') { [void](Invoke-Element $pp) }
    }
    $qpKind = Qp-StreamKind $qpAt
    if ($qpForced) { Check 'qa audio: the episode plays as a transcode' ($qpKind -eq 'transcode (HLS)') $qpKind } else { Say "qa audio: $qpKind" }
    Start-Sleep -Seconds 3
    $qpBefore = @(Get-Requests $qpAt | Where-Object { $_.route -in @('report-playing', 'report-progress') -and $_.PSObject.Properties['report'] }) | Select-Object -Last 1
    $qpFromAudio = if ($qpBefore) { $qpBefore.report.audio } else { $null }
    # B: the next audio track (keyboard); the keyboard was left on the controls by the play step
    $qpChangeAt = Get-Epoch
    if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x23 '#' } else { CK-Key 'B' }
    Say "qa audio: B pressed (audio track $qpFromAudio before)"
    # the backend's reports carry the new track
    $qpAfter = Wait-Until { @(Get-Requests $qpChangeAt | Where-Object { $_.route -in @('report-playing', 'report-progress') -and $_.PSObject.Properties['report'] -and $null -ne $_.report.audio -and $_.report.audio -ne $qpFromAudio }) | Select-Object -First 1 } -Seconds 30 -PollMs 300
    Check 'qa audio: the backend reports the new audio track' ([bool]$qpAfter) ("{0} -> {1}" -f $qpFromAudio, $(if ($qpAfter) { $qpAfter.report.audio } else { 'unchanged' }))
    $qpNewAudio = if ($qpAfter) { $qpAfter.report.audio } else { $null }
    $qpRestart = @(Get-Requests $qpChangeAt | Where-Object { $_.route -eq 'playbackinfo' })
    $qpRestartStream = @(Get-Requests $qpChangeAt | Where-Object { $_.route -eq 'stream' -and $_.path -match 'master\.m3u8$' })
    if ($qpForced) {
        $qpAsked = @($qpRestart | Where-Object { $_.PSObject.Properties['report'] -and $_.report.audio -eq $qpNewAudio })
        $qpStreamAudio = @($qpRestartStream | ForEach-Object { if ($_.query.PSObject.Properties['audiostreamindex']) { $_.query.audiostreamindex[0] } })
        Check 'qa audio: the transcode restarted on the new track (PlaybackInfo and a new stream for it)' ($qpAsked.Count -ge 1 -and $qpStreamAudio -contains "$qpNewAudio") `
            ("{0} PlaybackInfo (audio {1}); {2} new master.m3u8 (AudioStreamIndex {3})" -f $qpRestart.Count, (($qpRestart | ForEach-Object { if ($_.PSObject.Properties['report']) { $_.report.audio } }) -join ','), $qpRestartStream.Count, ($qpStreamAudio -join ','))
    }
    else { Say ("qa audio: {0} PlaybackInfo and {1} HLS stream request(s) after the change (direct play: mpv switches the track itself)" -f $qpRestart.Count, $qpRestartStream.Count) }
    Start-Sleep -Seconds 2
    $qpLit = Qp-WaitVideo 30
    Check 'qa audio: the video still shows after the change' ([bool]$qpLit) "$qpLit% not black"
    # what the user sees: the Audio and subtitles panel's audio choice (FluentFin's controls)
    if ($PlayerControls -eq 'FluentFin') {
        $qpTracks = Find-Ui -Id 'TracksButton'
        if (-not $qpTracks) { Key 0x10; Start-Sleep -Milliseconds 400; $qpTracks = Find-Ui -Id 'TracksButton' }
        if ($qpTracks) {
            [void](Invoke-Element $qpTracks)
            $qpRadio = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)
            $qpSel = Wait-Until {
                $group = Find-Ui -Id 'AudioTracks'
                if ($group) {
                    $radios = @([OffscreenUi]::Under($group, $qpRadio))
                    $on = @($radios | Where-Object { $p = $null; $_.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p) -and $p.Current.IsSelected })
                    if ($radios) { , @(@($radios | ForEach-Object { $_.Current.Name }), @($on | ForEach-Object { $_.Current.Name })) }
                }
            } -Seconds 5 -PollMs 200
            Shot 'qa-audio-panel'
            # (@() around the if: an if's single-item output arrives unrolled, a lone string, whose [0] is its first letter)
            $qpOnNames = @(if ($qpSel) { $qpSel[1] })
            # the track the backend reports, by its place among the item's audio tracks (the panel lists them in order)
            # (by -eq, not [Array]::IndexOf: pipeline output is wrapped in PSObject, which Equals never matches)
            function Qp-Pos($List, $Value) { $l = @($List); for ($i = 0; $i -lt $l.Count; $i++) { if ("$($l[$i])" -eq "$Value") { return $i } }; -1 }
            $qpWantPos = if ($null -ne $qpNewAudio -and $fixture.episode.PSObject.Properties['audio']) { Qp-Pos $fixture.episode.audio $qpNewAudio } else { -1 }
            $qpOnPos = if ($qpSel -and $qpOnNames.Count -eq 1) { Qp-Pos $qpSel[0] $qpOnNames[0] } else { -2 }
            Check 'qa audio: the Audio and subtitles panel shows the new track chosen' ($qpOnNames.Count -eq 1 -and ($qpWantPos -lt 0 -or $qpWantPos -eq $qpOnPos)) `
                ("tracks: {0}; chosen: {1}" -f $(if ($qpSel) { $qpSel[0] -join ' | ' } else { 'none found' }), $(if ($qpOnNames) { $qpOnNames -join ' | ' } else { 'none' }))
            Key 0x1B
            Start-Sleep -Milliseconds 400
        }
        else { Check 'qa audio: the Audio and subtitles button is there' $false 'not found' }
    }
    Start-Sleep -Seconds 2
    Step-Leave
    Start-Sleep -Seconds 2
    Qp-CheckReports 'audio' $qpAt @($qpEp)
    Qp-CheckRecorded 'audio' $qpAt @($qpEp)
    # what the viewing left: Jellyfin's rule (MinResumePct, MaxResumePct, MinResumeDurationSeconds, readable by an
    # ordinary user) applied to the Stopped report's position, against the item's play state now
    try {
        $qpStop = @(Get-Requests $qpAt | Where-Object { $_.route -eq 'report-stopped' -and $_.PSObject.Properties['report'] }) | Select-Object -Last 1
        $qpCfg = (Invoke-Qa $qpVersion '/System/Configuration' -Token $qpUser.Token -Device $qpDevice).Json
        $qpItem = (Invoke-Qa $qpVersion "/Items/$($qpEp.id)?userId=$($qpUser.UserId)" -Token $qpUser.Token -Device $qpDevice).Json
        $qpRun = [double]$qpItem.RunTimeTicks / 1e7
        $qpPos = if ($qpStop -and $null -ne $qpStop.report.position) { [double]$qpStop.report.position / 1e7 } else { $null }
        $qpPct = if ($qpRun -gt 0 -and $null -ne $qpPos) { 100 * $qpPos / $qpRun } else { 0 }
        $qpWatched = $qpPct -gt $qpCfg.MaxResumePct -or ($qpRun -lt $qpCfg.MinResumeDurationSeconds -and $qpPct -ge $qpCfg.MinResumePct)
        $qpResumable = -not $qpWatched -and $qpPct -ge $qpCfg.MinResumePct
        $qpNowPos = [double]$qpItem.UserData.PlaybackPositionTicks / 1e7
        $qpOk = $null -ne $qpPos -and [bool]$qpItem.UserData.Played -eq $qpWatched -and $(if ($qpResumable) { [Math]::Abs($qpNowPos - $qpPos) -lt 2 } else { $qpNowPos -eq 0 })
        Check 'qa audio: the server kept the viewing''s watched state and resume point (Jellyfin''s rule on the Stopped position)' $qpOk `
            ("stopped at {0:N1} s of {1:N0} s ({2:N0} %); watched {3} (expected {4}); resume point {5:N1} s" -f $qpPos, $qpRun, $qpPct, $qpItem.UserData.Played, $qpWatched, $qpNowPos)
    }
    finally { [void](Invoke-Qa $qpVersion '/Sessions/Logout' 'POST' -Token $qpUser.Token -Device $qpDevice) }
}
