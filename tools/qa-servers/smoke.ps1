<#
.SYNOPSIS
  Checks a seeded QA Jellyfin server through its API, as Nixie uses it, and prints a pass/fail table (no tokens).

.DESCRIPTION
  Per server (started hidden if it is not running, and stopped again afterwards):
  - it answers, on 127.0.0.1 only;
  - qa-user signs in (an ordinary user, not an administrator);
  - Quick Connect: a new device's code, approved by qa-user, signs that device in;
  - the libraries are listed (Movies, Shows, Anime, Home Videos, Music Videos);
  - the multi-audio/subtitle episode shows its 3 audio, 3 embedded and 1 sidecar subtitle tracks;
  - the server's play decision: the h264/AAC film direct-plays, the HEVC 10-bit/DTS film needs a transcode, which
    starts (HLS: master, variant, first segment);
  - a track change mid-stream: a transcode with audio track A, stopped (DELETE /Videos/ActiveEncodings) and asked again
    with track B, as Nixie restarts it; the new transcode's first segment arrives and its ffmpeg maps track B;
  - qa-user's playlist reads back its 3 items;
  - an admin action (running the "Clean Log Directory" task) works as qa-admin and is refused (403) for qa-user;
  - TmdbCollection ids (The Matrix and The Matrix Reloaded share one) and Tags on the movies, and the box set's 2 films.
  Exit code 0 only when every check passed. Sessions it opened are signed out at the end.

.EXAMPLE
  .\smoke.ps1               # both servers
  .\smoke.ps1 -Version 12
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('10.11', '12', 'all')][string]$Version = 'all',
    [string]$Python
)
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$versions = Resolve-QaVersions $Version
$rows = New-Object System.Collections.ArrayList
$device = 'qa-smoke'

function Add-Row([string]$V, [string]$Check, [bool]$Ok, [string]$Detail = '') {
    [void]$rows.Add([pscustomobject]@{ Server = $V; Check = $Check; Result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); Detail = $Detail })
}
# GET a URL (a server path with its query), reading the body to the end; returns @{ Status; Bytes; Text }
function Get-QaBody([string]$V, [string]$PathAndQuery, [string]$Token, [int]$TimeoutSec = 90, [switch]$AsText) {
    $req = [Net.HttpWebRequest]::Create((Get-QaUrl $V) + $PathAndQuery)
    $req.Timeout = $TimeoutSec * 1000; $req.ReadWriteTimeout = $TimeoutSec * 1000
    $req.Headers['Authorization'] = Get-QaAuthHeader $device $Token
    try { $resp = $req.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response; if (-not $resp) { return [pscustomobject]@{ Status = 0; Bytes = 0; Text = '' } } }
    try {
        $ms = New-Object IO.MemoryStream
        $resp.GetResponseStream().CopyTo($ms)
        $text = if ($AsText) { [Text.Encoding]::UTF8.GetString($ms.ToArray()) } else { '' }
        [pscustomobject]@{ Status = [int]$resp.StatusCode; Bytes = $ms.Length; Text = $text }
    }
    finally { $resp.Close() }
}
# the server's relative URL, made absolute to its path (an HLS playlist's lines are relative to the playlist)
function Join-Hls([string]$Base, [string]$Line) {
    if ($Line -match '^https?://') { return ([Uri]$Line).PathAndQuery }
    $dir = $Base.Split('?')[0]; $dir = $dir.Substring(0, $dir.LastIndexOf('/') + 1)
    $dir + $Line
}
# master.m3u8 -> variant -> first segment; returns the first segment's size (0 when anything failed)
function Get-FirstSegment([string]$V, [string]$Url, [string]$Token) {
    $m = Get-QaBody $V $Url $Token -AsText
    if ($m.Status -ne 200) { return 0 }
    $variant = @($m.Text -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })[0]
    if (-not $variant) { return 0 }
    $vUrl = Join-Hls $Url $variant
    $p = Get-QaBody $V $vUrl $Token -AsText
    if ($p.Status -ne 200) { return 0 }
    $seg = @($p.Text -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })[0]
    if (-not $seg) { return 0 }
    $s = Get-QaBody $V (Join-Hls $vUrl $seg) $Token
    if ($s.Status -ne 200) { return 0 }
    $s.Bytes
}
function New-Profile([int]$MaxBitrate) {
    [ordered]@{
        Name = 'Nixie QA smoke (h264/AAC only)'; MaxStreamingBitrate = $MaxBitrate; MaxStaticBitrate = $MaxBitrate
        DirectPlayProfiles = @([ordered]@{ Container = 'mkv,mp4'; Type = 'Video'; VideoCodec = 'h264'; AudioCodec = 'aac' })
        TranscodingProfiles = @([ordered]@{ Container = 'ts'; Type = 'Video'; VideoCodec = 'h264'; AudioCodec = 'aac'; Protocol = 'hls'
                Context = 'Streaming'; MaxAudioChannels = '2'; MinSegments = 1; BreakOnNonKeyFrames = $true })
        ContainerProfiles = @(); CodecProfiles = @()
        SubtitleProfiles = @([ordered]@{ Format = 'vtt'; Method = 'External' }, [ordered]@{ Format = 'srt'; Method = 'External' })
    }
}
# PlaybackInfo as a client asks it. A track (AudioStreamIndex) counts only with the MediaSourceId it belongs to: Jellyfin
# applies the indexes to the named source alone and otherwise picks its default track (as Nixie's backend sends both).
function Get-PlaybackInfo([string]$V, $Who, [string]$ItemId, [int]$MaxBitrate, $AudioIndex = $null, [string]$MediaSourceId, [switch]$NoDirect) {
    $body = [ordered]@{ UserId = $Who.UserId; MaxStreamingBitrate = $MaxBitrate; StartTimeTicks = 0; AutoOpenLiveStream = $true
        EnableDirectPlay = (-not $NoDirect); EnableDirectStream = (-not $NoDirect); EnableTranscoding = $true
        AllowVideoStreamCopy = $true; AllowAudioStreamCopy = $true; DeviceProfile = (New-Profile $MaxBitrate) }
    if ($null -ne $AudioIndex) { $body.AudioStreamIndex = $AudioIndex; $body.SubtitleStreamIndex = -1 }
    if ($MediaSourceId) { $body.MediaSourceId = $MediaSourceId }
    Invoke-Qa $V "/Items/$ItemId/PlaybackInfo?userId=$($Who.UserId)" 'POST' $body -Token $Who.Token -Device $device
}
# whether an ffmpeg transcode log of the server written after $Since maps the file's stream $Index (ffmpeg's numbering:
# the file's own streams, not Jellyfin's, which puts sidecar subtitle files first)
function Test-TranscodeMapsAudio([string]$V, [datetime]$Since, [int]$Index) {
    $logs = @(Get-ChildItem -LiteralPath (Join-Path (Get-QaDir $V) 'log') -Filter 'FFmpeg.Transcode-*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $Since } | Sort-Object LastWriteTime -Descending)
    foreach ($l in $logs) {
        $head = (Get-Content -LiteralPath $l.FullName -TotalCount 5 -Encoding utf8) -join ' '
        if ($head -match "-map 0:$Index(\s|$)") { return $true }
    }
    $false
}

foreach ($v in $versions) {
    $started = $false
    $tokens = @()
    try {
        $st = Get-QaState $v
        if (-not ($st -and $st.Running)) { $st = Start-QaServer $v $Python; $started = $true }
        $info = (Invoke-Qa $v '/System/Info/Public').Json
        $listen = @(Get-QaListeners $st.State.server.pid)
        Add-Row $v 'answers, on 127.0.0.1 only' ($info.Version -and -not @($listen | Where-Object { $_ -notmatch ' 127\.0\.0\.1:' })) "Jellyfin $($info.Version); $($listen -join ' ')"
        $seed = Get-Content -LiteralPath (Join-Path (Get-QaDir $v) 'seeded\seed.json') -Raw | ConvertFrom-Json

        # --- the ordinary user, by name ---
        $user = Connect-Qa $v 'user' $device
        $tokens += $user.Token
        Add-Row $v 'sign in as the ordinary user (API)' ($user.Token -and -not $user.IsAdmin) "$($user.Name), administrator: $($user.IsAdmin)"

        # --- Quick Connect: another device's code, approved by qa-user ---
        $qcDevice = 'qa-smoke-quickconnect'
        $init = Invoke-Qa $v '/QuickConnect/Initiate' 'POST' -Device $qcDevice
        $ok = $false; $detail = "initiate HTTP $($init.Status)"
        if ($init.Status -eq 200 -and $init.Json.Code) {
            $auth = Invoke-Qa $v "/QuickConnect/Authorize?code=$($init.Json.Code)" 'POST' -Token $user.Token -Device $device
            $conn = Invoke-Qa $v "/QuickConnect/Connect?secret=$([Uri]::EscapeDataString($init.Json.Secret))" -Device $qcDevice
            $signed = Invoke-Qa $v '/Users/AuthenticateWithQuickConnect' 'POST' @{ Secret = $init.Json.Secret } -Device $qcDevice
            if ($signed.Json.AccessToken) { $tokens += $signed.Json.AccessToken }
            $me = if ($signed.Json.AccessToken) { (Invoke-Qa $v '/Users/Me' -Token $signed.Json.AccessToken -Device $qcDevice).Json } else { $null }
            $ok = ($auth.Status -eq 200 -and $conn.Json.Authenticated -and $me -and $me.Name -eq $user.Name)
            $detail = "approve HTTP $($auth.Status), connect authenticated: $($conn.Json.Authenticated), signed in as $(if ($me) { $me.Name } else { '-' })"
        }
        Add-Row $v 'Quick Connect: code approved by qa-user signs a device in' $ok $detail

        # --- libraries ---
        $views = @((Invoke-Qa $v "/UserViews?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.Items | ForEach-Object { $_.Name })
        $want = @('Movies', 'Shows', 'Anime', 'Home Videos', 'Music Videos')
        Add-Row $v 'libraries listed' (-not @($want | Where-Object { $views -notcontains $_ })) ($views -join ', ')

        $all = (Invoke-Qa $v "/Items?userId=$($user.UserId)&recursive=true&includeItemTypes=Movie,Episode,BoxSet,Playlist&fields=ProviderIds,Tags,Path,MediaStreams" -Token $user.Token -Device $device).Json.Items
        # 1.5 search: the QA Search libraries' titles come from .nfo files only (no TMDb ids, no Tags, no media streams of
        # their own): the checks below are about the seeded media, so they leave out everything under search-media
        $searchMedia = (Join-Path $script:QaRoot 'search-media') + [IO.Path]::DirectorySeparatorChar
        $all = @($all | Where-Object { -not ($_.Path -and $_.Path.StartsWith($searchMedia, [StringComparison]::OrdinalIgnoreCase)) })
        $movies = @($all | Where-Object { $_.Type -eq 'Movie' })
        $byPath = { param($like) @($all | Where-Object { $_.Path -like $like }) | Select-Object -First 1 }
        $multi = & $byPath '*Twilight Zone (1959) - S01E01.mkv'
        $bbb = & $byPath '*Big Buck Bunny (2008).mkv'
        $tears = & $byPath '*Tears of Steel (2012).mkv'

        # --- the tracks of the multi-track episode ---
        $pi = Get-PlaybackInfo $v $user $multi.Id 20000000
        $streams = @($pi.Json.MediaSources[0].MediaStreams)
        $audio = @($streams | Where-Object { $_.Type -eq 'Audio' })
        $subs = @($streams | Where-Object { $_.Type -eq 'Subtitle' })
        $ext = @($subs | Where-Object { $_.IsExternal })
        Add-Row $v 'multi-track episode: 3 audio, 3 embedded + 1 sidecar subtitles' ($audio.Count -eq 3 -and $subs.Count -eq 4 -and $ext.Count -eq 1) `
            ("audio {0}; subtitles {1}" -f (($audio | ForEach-Object { "$($_.Index):$($_.Language)" }) -join ','), (($subs | ForEach-Object { "$($_.Index):$($_.Language)$(if ($_.IsExternal) { '(file)' })$(if ($_.IsForced) { '(forced)' })" }) -join ','))

        # --- the play decision, and a transcode that starts ---
        $direct = (Get-PlaybackInfo $v $user $bbb.Id 20000000).Json.MediaSources[0]
        $hevc = Get-PlaybackInfo $v $user $tears.Id 20000000
        $hsrc = $hevc.Json.MediaSources[0]
        $seg = if ($hsrc.TranscodingUrl) { Get-FirstSegment $v $hsrc.TranscodingUrl $user.Token } else { 0 }
        $reasons = if ($hsrc.TranscodingUrl -match 'TranscodeReasons=([^&]+)') { [Uri]::UnescapeDataString($Matches[1]) } else { '-' }
        Add-Row $v 'h264/AAC film direct-plays; HEVC 10-bit/DTS film transcodes (first HLS segment)' ($direct.SupportsDirectPlay -and -not $hsrc.SupportsDirectPlay -and $seg -gt 0) `
            ("direct play {0}/{1}; reasons {2}; first segment {3} bytes" -f $direct.SupportsDirectPlay, $hsrc.SupportsDirectPlay, $reasons, $seg)
        if ($hevc.Json.PlaySessionId) { [void](Invoke-Qa $v "/Videos/ActiveEncodings?deviceId=nixie-qa-$device&playSessionId=$($hevc.Json.PlaySessionId)" 'DELETE' -Token $user.Token -Device $device) }

        # --- a track change mid-stream: transcode with track A, stop it, ask again with track B ---
        $a1 = $audio[0].Index; $a2 = $audio[1].Index
        $source = $pi.Json.MediaSources[0].Id
        # ffmpeg's number for track B: its place among the file's own (not external) streams
        $fileOrder = @($streams | Where-Object { -not $_.IsExternal } | Sort-Object Index | ForEach-Object { [int]$_.Index })
        $b = [array]::IndexOf($fileOrder, [int]$a2)
        $first = Get-PlaybackInfo $v $user $multi.Id 120000 $a1 $source -NoDirect
        $u1 = $first.Json.MediaSources[0].TranscodingUrl
        $seg1 = if ($u1) { Get-FirstSegment $v $u1 $user.Token } else { 0 }
        $stop = Invoke-Qa $v "/Videos/ActiveEncodings?deviceId=nixie-qa-$device&playSessionId=$($first.Json.PlaySessionId)" 'DELETE' -Token $user.Token -Device $device
        $mid = (Get-Date).AddMilliseconds(-200)
        $second = Get-PlaybackInfo $v $user $multi.Id 120000 $a2 $source -NoDirect
        $u2 = $second.Json.MediaSources[0].TranscodingUrl
        $seg2 = if ($u2) { Get-FirstSegment $v $u2 $user.Token } else { 0 }
        Start-Sleep -Milliseconds 500
        $mapsB = Test-TranscodeMapsAudio $v $mid $b
        $ok = ($u1 -match "AudioStreamIndex=$a1(&|$)" -and $seg1 -gt 0 -and $stop.Status -eq 204 -and $u2 -match "AudioStreamIndex=$a2(&|$)" -and $seg2 -gt 0 -and $mapsB)
        Add-Row $v 'track change mid-stream: transcode stopped and restarted on the new audio track' $ok `
            ("track {0} ({1}): segment {2} bytes; stop HTTP {3}; track {4} ({5}): segment {6} bytes, its ffmpeg maps 0:{7}: {8}" -f
                $a1, $audio[0].Language, $seg1, $stop.Status, $a2, $audio[1].Language, $seg2, $b, $mapsB)
        if ($second.Json.PlaySessionId) { [void](Invoke-Qa $v "/Videos/ActiveEncodings?deviceId=nixie-qa-$device&playSessionId=$($second.Json.PlaySessionId)" 'DELETE' -Token $user.Token -Device $device) }

        # --- the playlist ---
        $plItems = @((Invoke-Qa $v "/Playlists/$($seed.playlist)/Items?userId=$($user.UserId)" -Token $user.Token -Device $device).Json.Items)
        Add-Row $v 'playlist read (qa-user''s QA Playlist)' ($plItems.Count -eq $seed.playlist_items) (($plItems | ForEach-Object { $_.Name }) -join ', ')

        # --- an admin action, as the admin and as the ordinary user ---
        $admin = Connect-Qa $v 'admin' $device
        $tokens += $admin.Token
        $task = @((Invoke-Qa $v '/ScheduledTasks?isHidden=false' -Token $admin.Token -Device $device).Json | Where-Object { $_.Key -eq 'CleanLogFiles' }) | Select-Object -First 1
        $asAdmin = Invoke-Qa $v "/ScheduledTasks/Running/$($task.Id)" 'POST' -Token $admin.Token -Device $device
        $asUser = Invoke-Qa $v "/ScheduledTasks/Running/$($task.Id)" 'POST' -Token $user.Token -Device $device
        Add-Row $v 'admin action (run Clean Log Directory): admin yes, ordinary user refused' ($task -and $asAdmin.Status -eq 204 -and $asUser.Status -eq 403) `
            "qa-admin HTTP $($asAdmin.Status); qa-user HTTP $($asUser.Status)"

        # --- TmdbCollection ids, Tags, the box set ---
        $noTmdb = @($movies | Where-Object { -not $_.ProviderIds.Tmdb } | ForEach-Object { $_.Name })
        $matrix = @($movies | Where-Object { $_.Name -like 'The Matrix*' })
        $coll = @($matrix | ForEach-Object { $_.ProviderIds.TmdbCollection } | Sort-Object -Unique)
        $tagged = @($movies | Where-Object { @($_.Tags).Count -gt 0 })
        $boxSet = @($all | Where-Object { $_.Type -eq 'BoxSet' -and $_.Name -eq 'QA Box Set' }) | Select-Object -First 1
        $boxItems = if ($boxSet) { @((Invoke-Qa $v "/Items?userId=$($user.UserId)&parentId=$($boxSet.Id)" -Token $user.Token -Device $device).Json.Items) } else { @() }
        $ok = ($movies.Count -ge 7 -and -not $noTmdb -and $matrix.Count -eq 2 -and $coll.Count -eq 1 -and $coll[0] -and $tagged.Count -ge 5 -and $boxItems.Count -eq 2)
        Add-Row $v 'TmdbCollection ids and Tags on the movies; the box set' $ok `
            ("{0} movies, {1} without a TMDb id; The Matrix pair TmdbCollection {2}; {3} with Tags; QA Box Set {4} films" -f $movies.Count, $noTmdb.Count, ($coll -join '/'), $tagged.Count, $boxItems.Count)
    }
    catch { Add-Row $v 'run' $false $_.Exception.Message }
    finally {
        foreach ($t in $tokens) { try { [void](Invoke-Qa $v '/Sessions/Logout' 'POST' -Token $t -Device $device) } catch {} }
        if ($started) { try { [void](Stop-QaServer $v) } catch { Add-Row $v 'stop' $false $_.Exception.Message } }
    }
}

$rows | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
$failed = @($rows | Where-Object { $_.Result -ne 'PASS' })
Write-Host ("{0} of {1} checks passed" -f ($rows.Count - $failed.Count), $rows.Count)
if ($failed) { exit 1 }
