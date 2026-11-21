<#
.SYNOPSIS
  Opt-in live check (1.5 qacheck, BH-140X-m4): which ways of sending the access token each running QA server takes, next to
  what the fake server's strict mode (tools\ui-harness\fakeserver\server.py --auth strict, its default) refuses; and
  which of the resources the player and the image loader fetch need a token at all.

.DESCRIPTION
  Per running QA server, signed in as qa-user (the tools' generated account, never printed): GET /Users/Me with the token in
  each form a client may send it, one at a time, and the status each gets. Jellyfin 12 reads the legacy forms only while
  its EnableLegacyAuthorization setting is on (off by default from v12): the X-Emby-Authorization, X-Emby-Token and
  X-MediaBrowser-Token headers and the api_key query parameter. Authorization: MediaBrowser Token="..." and ApiKey= are
  read on every version. The table says what each server answered and, for each form, what the strict fake answers; a
  form where 12 and the strict fake disagree is a FAIL (the fake no longer models the real server).
  Then, per server, an image, a static stream (its first KB), a sidecar subtitle, a trickplay tile and the socket, each
  without a token, with the header and with ApiKey, against what the fake answers (both modes): images, streams and
  subtitles answer without one; trickplay (401) and the socket (403) refuse it. Read-only; the session is signed out
  again. Starts and stops nothing.

.EXAMPLE
  .\live-auth.ps1            # both servers, when running
  .\live-auth.ps1 -Version 12
#>
[CmdletBinding(PositionalBinding = $false)]
param([ValidateSet('10.11', '12', 'all')][string]$Version = 'all')
. (Join-Path $PSScriptRoot 'qa-common.ps1')
$device = 'qa-auth-forms'
# (form, how it is sent, what the strict fake answers)
$forms = @(
    @{ Name = 'Authorization: MediaBrowser Token'; Header = 'Authorization'; Value = { param($t) (Get-QaAuthHeader $device $t) }; Strict = 200 },
    @{ Name = 'Authorization: Emby Token'; Header = 'Authorization'; Value = { param($t) (Get-QaAuthHeader $device $t) -replace '^MediaBrowser ', 'Emby ' }; Strict = 401 },
    @{ Name = 'X-Emby-Authorization: MediaBrowser Token'; Header = 'X-Emby-Authorization'; Value = { param($t) (Get-QaAuthHeader $device $t) }; Strict = 401 },
    @{ Name = 'X-Emby-Token'; Header = 'X-Emby-Token'; Value = { param($t) $t }; Strict = 401 },
    @{ Name = 'X-MediaBrowser-Token'; Header = 'X-MediaBrowser-Token'; Value = { param($t) $t }; Strict = 401 },
    @{ Name = '?ApiKey='; Query = 'ApiKey'; Strict = 200 },
    @{ Name = '?apikey= (query keys ignore case)'; Query = 'apikey'; Strict = 200 },
    @{ Name = '?api_key='; Query = 'api_key'; Strict = 401 },
    @{ Name = 'none'; Strict = 401 }
)
function Get-AuthStatus([string]$V, $Form, [string]$Token) {
    $url = (Get-QaUrl $V) + '/Users/Me'
    if ($Form.Query) { $url += "?$($Form.Query)=$([Uri]::EscapeDataString($Token))" }
    $req = [Net.HttpWebRequest]::Create($url)
    $req.Timeout = 15000
    $req.Accept = 'application/json'
    if ($Form.Header) { $req.Headers[$Form.Header] = & $Form.Value $Token }
    try { $resp = $req.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response; if (-not $resp) { return 0 } }
    try { [int]$resp.StatusCode } finally { $resp.Close() }
}
# a resource without a token, with the header, with ApiKey: its status (the first KB of a stream: a range)
function Get-ResourceStatus([string]$V, [string]$Path, [string]$Header) {
    $req = [Net.HttpWebRequest]::Create((Get-QaUrl $V) + $Path)
    $req.Timeout = 20000
    $req.AddRange(0, 1023)
    if ($Header) { $req.Headers['Authorization'] = $Header }
    try { $resp = $req.GetResponse() } catch [Net.WebException] { $resp = $_.Exception.Response; if (-not $resp) { return 0 } }
    try { [int]$resp.StatusCode } finally { $resp.Close() }
}
# the socket's upgrade: the status of its answer
function Get-SocketStatus([string]$V, [string]$Query, [string]$Header) {
    $port = ([Uri](Get-QaUrl $V)).Port
    $tcp = New-Object Net.Sockets.TcpClient('127.0.0.1', $port)
    try {
        $stream = $tcp.GetStream(); $stream.ReadTimeout = 10000
        $head = "GET /socket$Query HTTP/1.1`r`nHost: 127.0.0.1:$port`r`nUpgrade: websocket`r`nConnection: Upgrade`r`n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==`r`nSec-WebSocket-Version: 13`r`n"
        if ($Header) { $head += "Authorization: $Header`r`n" }
        $bytes = [Text.Encoding]::ASCII.GetBytes($head + "`r`n"); $stream.Write($bytes, 0, $bytes.Length)
        $buf = New-Object byte[] 256; $n = $stream.Read($buf, 0, 256)
        [int](([Text.Encoding]::ASCII.GetString($buf, 0, $n) -split ' ')[1])
    }
    finally { $tcp.Close() }
}
# what the fake answers (none, header, ApiKey); trickplay's 404 with a token: the QA servers make no tiles
$fakeResources = [ordered]@{ image = @(200, 200, 200); stream = @(206, 206, 206); subtitle = @(200, 200, 200)
    trickplay = @(401, 404, 404); socket = @(403, 101, 101) }
$rows = New-Object System.Collections.ArrayList
foreach ($v in (Resolve-QaVersions $Version)) {
    $st = Get-QaState $v
    if (-not ($st -and $st.Running)) { [void]$rows.Add([pscustomobject]@{ Server = $v; Form = '-'; Status = '-'; Strict = '-'; Result = 'SKIP' }); continue }
    $token = $null
    try {
        $info = (Invoke-Qa $v '/System/Info/Public').Json
        $user = Connect-Qa $v 'user' $device
        $token = $user.Token
        foreach ($f in $forms) {
            $code = Get-AuthStatus $v $f $token
            $ok = if ($v -eq '12') { $code -eq $f.Strict } else { $true }   # 10.11: reported (legacy on by default there)
            [void]$rows.Add([pscustomobject]@{ Server = "$v ($($info.Version))"; Form = $f.Name; Status = $code; Strict = $f.Strict; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }) })
        }
        $header = Get-QaAuthHeader $device $token
        $key = [Uri]::EscapeDataString($token)
        $ep = @((Invoke-Qa $v "/Items?userId=$($user.UserId)&recursive=true&includeItemTypes=Episode&fields=MediaSources&limit=50" -Token $token -Device $device).Json.Items |
            Where-Object { $_.MediaSources -and @($_.MediaSources[0].MediaStreams | Where-Object { $_.Type -eq 'Subtitle' -and $_.IsExternal }).Count }) | Select-Object -First 1
        if ($ep) {
            $ms = $ep.MediaSources[0]
            $sub = @($ms.MediaStreams | Where-Object { $_.Type -eq 'Subtitle' -and $_.IsExternal })[0].Index
            $paths = [ordered]@{ image = "/Items/$($ep.SeriesId)/Images/Primary"; stream = "/Videos/$($ep.Id)/stream?static=true&MediaSourceId=$($ms.Id)"
                subtitle = "/Videos/$($ep.Id)/$($ms.Id)/Subtitles/$sub/0/Stream.srt"; trickplay = "/Videos/$($ep.Id)/Trickplay/320/0.jpg" }
            foreach ($name in $fakeResources.Keys) {
                $got = if ($name -eq 'socket') {
                    @((Get-SocketStatus $v "?deviceId=$device"), (Get-SocketStatus $v "?deviceId=$device" $header), (Get-SocketStatus $v "?deviceId=$device&ApiKey=$key"))
                }
                else {
                    $p = $paths[$name]; $sep = if ($p.Contains('?')) { '&' } else { '?' }
                    @((Get-ResourceStatus $v $p), (Get-ResourceStatus $v $p $header), (Get-ResourceStatus $v "$p${sep}ApiKey=$key"))
                }
                $want = $fakeResources[$name]
                [void]$rows.Add([pscustomobject]@{ Server = "$v ($($info.Version))"; Form = "$name (none / header / ApiKey)"; Status = $got -join ' / '
                    Strict = $want -join ' / '; Result = $(if (($got -join ',') -eq ($want -join ',')) { 'PASS' } else { 'FAIL' }) })
            }
        }
        else { [void]$rows.Add([pscustomobject]@{ Server = $v; Form = 'resources'; Status = 'no episode with a sidecar subtitle'; Strict = '-'; Result = 'FAIL' }) }
    }
    catch { [void]$rows.Add([pscustomobject]@{ Server = $v; Form = 'sign-in'; Status = $_.Exception.Message; Strict = '-'; Result = 'FAIL' }) }
    finally { if ($token) { try { [void](Invoke-Qa $v '/Sessions/Logout' 'POST' -Token $token -Device $device) } catch {} } }
}
$rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if (@($rows | Where-Object { $_.Result -eq 'FAIL' })) { exit 1 }
