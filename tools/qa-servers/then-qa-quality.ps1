# 1.6 player: a -Then script of tools\ui-harness\run-offscreen.ps1 for a real QA server (-QaServer 10.11|12): the player's
# Quality menu and Playback stats against a real Jellyfin, muted.
#   $env:NIXIE_LAYOUT = 'Classic'
#   run-offscreen.ps1 -QaServer 12 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script wait:1 `
#       -Then tools\qa-servers\then-qa-quality.ps1 -NoA11y
# Tears of Steel (640 x 360, about 1 Mbps on 12.4 and 1.8 on 10.11.11) plays on Auto: a direct play. Then the Quality
# menu's lowest cap (720 kbps, under the file's bitrate on both): the server converts it (Transcode, its reason the
# bitrate). Then Original: a direct play again. Each time the Playback stats panel's "Playing" row must say what the
# server's own session says (GET /Sessions as qa-admin, through the tools, never printed): DirectPlay or Transcode. One
# viewing for the server: one start and one finish in its activity log, every report from the backend. Variables start qq.

if (-not $QaUpstream) { throw 'then-qa-quality.ps1 runs against a QA server only (run-offscreen.ps1 -QaServer 10.11|12)' }
. (Join-Path $PSScriptRoot 'qa-common.ps1')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$qqVersion = @($script:QaPorts.Keys | Where-Object { $QaUpstream.TrimEnd('/') -eq (Get-QaUrl $_) }) | Select-Object -First 1
if (-not $qqVersion) { throw "then-qa-quality: $QaUpstream is not a QA server" }
$qqDevice = "nixie-qaquality-$PID"
Say "qa quality: Jellyfin $qqVersion"

function Qq-Id([string]$Id) { "$Id".Replace('-', '').ToLowerInvariant() }
# the server's own view of the item playing: its session's play method ("DirectPlay", "DirectStream", "Transcode"), read as
# qa-admin (the admin's session ends with it)
function Qq-ServerMethod([string]$ItemId) {
    $admin = Connect-Qa $qqVersion 'admin' "$qqDevice-admin"
    try {
        $r = Invoke-Qa $qqVersion '/Sessions?ActiveWithinSeconds=120' -Token $admin.Token -Device "$qqDevice-admin"
        $s = @($r.Json | Where-Object { $_.NowPlayingItem -and (Qq-Id $_.NowPlayingItem.Id) -eq (Qq-Id $ItemId) }) | Select-Object -First 1
        if (-not $s) { return $null }
        [pscustomobject]@{ Method = $s.PlayState.PlayMethod; Reasons = @($s.TranscodingInfo.TranscodeReasons) -join ',' }
    }
    finally { [void](Invoke-Qa $qqVersion '/Sessions/Logout' 'POST' -Token $admin.Token -Device "$qqDevice-admin") }
}
# the stats panel's play method ("Direct play", "Transcoding", "Direct stream"); $null when the panel is not there
function Qq-PanelMethod {
    $row = Find-Ui -Name 'Playing: ' -Contains
    if ($row) { $row.Current.Name.Substring('Playing: '.Length) } else { $null }
}
$qqWords = @{ 'DirectPlay' = 'Direct play'; 'DirectStream' = 'Direct stream'; 'Transcode' = 'Transcoding' }
# waits until the panel and the server agree on $Want ('DirectPlay' or 'Transcode'); checks and says both
function Qq-Agree([string]$Label, [string]$ItemId, [string]$Want) {
    $script:qqServer = $null; $script:qqPanel = $null
    $ok = Wait-Until {
        $script:qqServer = Qq-ServerMethod $ItemId
        $script:qqPanel = Qq-PanelMethod
        if ($script:qqServer -and $script:qqServer.Method -eq $Want -and $script:qqPanel -eq $qqWords[$Want]) { $true }
    } -Seconds 30 -PollMs 1500
    $server = if ($script:qqServer) { "$($script:qqServer.Method)$(if ($script:qqServer.Reasons) { " ($($script:qqServer.Reasons))" })" } else { 'no session' }
    Check "qa quality ${qqVersion}: $Label (the panel and the server both say $Want)" ([bool]$ok) "panel '$($script:qqPanel)', server $server"
}
function Qq-OpenMenu {
    $button = Wait-Ui -Id 'QualityButton' -Seconds 6
    if (-not $button) { return $null }
    [void][OffscreenUi]::SetFocus($button)
    Start-Sleep -Milliseconds 300
    Key 0x0D
    Wait-Until { Find-Ui -Name 'Quality' -Type 'List' } -Seconds 6 -PollMs 150
}
# picks the menu's entry named $Name (its list item's name starts with it) by the keyboard: Home (the menu opens on the
# choice in effect), then Down until it has the focus, Enter
function Qq-Pick([string]$Name) {
    $menu = Qq-OpenMenu
    if (-not $menu) { return $false }
    Start-Sleep -Milliseconds 400
    Key 0x24
    Start-Sleep -Milliseconds 200
    foreach ($i in 0..15) {
        $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
        if ($f -and ($f | ConvertFrom-Json).name -like "$Name*") { Key 0x0D; return $true }
        Key 0x28
        Start-Sleep -Milliseconds 150
    }
    Key 0x1B
    $false
}

$qqT = $fixture.transcode
Check "qa quality ${qqVersion}: the server has Tears of Steel" ([bool]$qqT) $(if ($qqT) { $qqT.name })
if ($qqT) {
    Open-Library $qqT.library
    Open-Card $qqT.name
    [void](Wait-ItemPage $qqT.name)
    $qqPlay = Wait-Ui -Id 'PlayButton' -Type 'Button' -Seconds 15
    if ($qqPlay) {
        $qqAt = Get-Epoch
        [void](Invoke-Element $qqPlay)
        $script:page = 'player'
        $lit = Wait-Until { $v = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.3, 0.3, 0.7, 0.7); if ($v -gt 20) { $v } } -Seconds 90 -PollMs 100
        Check "qa quality ${qqVersion}: the video shows" ([bool]$lit) "$lit% not black"
        [void](Wait-Until { @(Get-Requests $qqAt | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 30 -PollMs 200)
        Start-Sleep -Seconds 2
        # silent: mpv's audio output is the null one
        $ao = @(Get-Content -LiteralPath (Join-Path $DataDir 'Logs\mpv-backend.log') -Encoding utf8 | Where-Object { $_ -match 'AO: \[' -and $_ -notmatch 'Setting option' })
        Check "qa quality ${qqVersion}: silent (AO: [null])" ($ao -and -not @($ao | Where-Object { $_ -notmatch 'AO: \[null\]' })) $(if ($ao) { $ao[0] } else { 'no AO line' })

        # the stats panel (I) stays open throughout: it follows each restart
        Key 0x49
        $panel = Wait-Ui -Id 'StatsPanel' -Seconds 8
        Check "qa quality ${qqVersion}: I opens Playback stats" ([bool]$panel)
        Qq-Agree 'Auto plays the file directly' $qqT.id 'DirectPlay'
        Shot "qa-quality-$qqVersion-auto"

        $picked = Qq-Pick '720 kbps'
        Check "qa quality ${qqVersion}: the 720 kbps cap is picked by the keyboard" $picked
        Qq-Agree 'a cap under the file is converted by the server' $qqT.id 'Transcode'
        $why = Wait-Until { $w = Find-Ui -Name 'Why: ' -Contains; if ($w) { $w } } -Seconds 6 -PollMs 200
        Check "qa quality ${qqVersion}: the panel says why (the bitrate limit)" ($why -and $why.Current.Name -match 'bitrate limit') $(if ($why) { $why.Current.Name })
        $header = $null
        if (Qq-OpenMenu) { Start-Sleep -Milliseconds 400; $header = Find-Ui -Id 'QualityNowText'; Shot "qa-quality-$qqVersion-transcoding-menu"; Key 0x1B }
        Check "qa quality ${qqVersion}: the menu's header says Transcoding" ($header -and $header.Current.Name -like "Transcoding $([char]0x00B7) *") $(if ($header) { $header.Current.Name })
        Shot "qa-quality-$qqVersion-transcoding"

        $picked = Qq-Pick 'Original'
        Check "qa quality ${qqVersion}: Original is picked by the keyboard" $picked
        Qq-Agree 'Original plays the file directly again' $qqT.id 'DirectPlay'

        Key 0x1B   # the panel closes; the Back key leaves
        Start-Sleep -Milliseconds 500
        Key 0xA6
        [void](Wait-Until { Find-Ui -Id 'PlayButton' -Type 'Button' } -Seconds 20 -PollMs 150)
        $script:page = 'movie'
        [void](Wait-Until { @(Get-Requests $qqAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
        Start-Sleep -Seconds 2
        $reqs = @(Get-Requests $qqAt | Where-Object { $_.route -like 'report-*' })
        $from = @($reqs | ForEach-Object { Get-Origin $_ } | Sort-Object -Unique)
        $starts = @($reqs | Where-Object { $_.route -eq 'report-playing' }).Count
        $stops = @($reqs | Where-Object { $_.route -eq 'report-stopped' }).Count
        Check "qa quality ${qqVersion}: one Playing and one Stopped for the viewing, from the backend only" ($starts -eq 1 -and $stops -eq 1 -and ($from -join ',') -eq 'backend') "$starts Playing, $stops Stopped; from $($from -join ', ')"
        # the server's activity log: one start and one finish for the film
        $admin = Connect-Qa $qqVersion 'admin' "$qqDevice-admin"
        try {
            $min = [DateTimeOffset]::FromUnixTimeMilliseconds([long](($qqAt - 2) * 1000)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
            $act = $null
            $ok = Wait-Until {
                $r = Invoke-Qa $qqVersion "/System/ActivityLog/Entries?minDate=$([Uri]::EscapeDataString($min))&limit=500" -Token $admin.Token -Device "$qqDevice-admin"
                $script:qqAct = @($r.Json.Items | Where-Object { (Qq-Id $_.ItemId) -eq (Qq-Id $qqT.id) -and $_.Type -in 'VideoPlayback', 'VideoPlaybackStopped' })
                if (@($script:qqAct | Where-Object Type -eq 'VideoPlayback').Count -eq 1 -and @($script:qqAct | Where-Object Type -eq 'VideoPlaybackStopped').Count -eq 1) { $true }
            } -Seconds 10 -PollMs 1000
            Check "qa quality ${qqVersion}: the server's activity log has one start and one finish" ([bool]$ok) (@($script:qqAct | ForEach-Object { $_.Type }) -join ', ')
        }
        finally { [void](Invoke-Qa $qqVersion '/Sessions/Logout' 'POST' -Token $admin.Token -Device "$qqDevice-admin") }
    }
    else { Check "qa quality ${qqVersion}: Play on its page" $false 'no Play button' }
}
