# Shared by qa.ps1, seed.ps1 and smoke.ps1 (dot-sourced). The local QA Jellyfin servers: docs\QA-SERVERS.md.
# Nothing here prints a password or a token.

$ErrorActionPreference = 'Stop'


$script:QaPorts = [ordered]@{ '10.11' = 8196; '12' = 8296 }
$script:QaRoot = if ($env:NIXIE_QA_ROOT) { $env:NIXIE_QA_ROOT } else { (Join-Path $env:USERPROFILE 'Dev\jellyfin-qa') }
$script:QaRoot = [IO.Path]::GetFullPath($script:QaRoot)
$script:QaCredentialsFile = Join-Path $script:QaRoot 'qa-credentials.json'
$script:QaClient = 'Nixie QA tools'

function Resolve-QaVersions([string]$Version) {
    if (-not $Version -or $Version -eq 'all') { return @($script:QaPorts.Keys) }
    if (-not $script:QaPorts.Contains($Version)) { throw "-Version must be 10.11, 12 or all (not '$Version')" }
    @($Version)
}
function Get-QaDir([string]$Version) { Join-Path $script:QaRoot $Version }
function Get-QaUrl([string]$Version) { "http://127.0.0.1:$($script:QaPorts[$Version])" }

# a Python for serve.py (stdlib only): -Python, else pythonw next to FLUENTFIN_MPV_TEST_PYTHON, else pythonw on PATH
function Resolve-QaPython([string]$Python) {
    $candidates = @()
    if ($Python) { $candidates += $Python }
    if ($env:FLUENTFIN_MPV_TEST_PYTHON) { $candidates += (Join-Path (Split-Path $env:FLUENTFIN_MPV_TEST_PYTHON) 'pythonw.exe') }
    $onPath = Get-Command pythonw.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    foreach ($c in $candidates) {
        $w = $c -replace '\\python\.exe$', '\pythonw.exe'
        if (Test-Path -LiteralPath $w) { return [IO.Path]::GetFullPath($w) }
    }
    throw 'no pythonw.exe found: pass -Python <python.exe> (absolute)'
}

# --- network.xml: written before the first start; loopback only, nothing announced ---------------------------------
function Get-QaNetworkXml([string]$Version) {
    $port = $script:QaPorts[$Version]
    $https = $port + 1
    @"
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <BaseUrl />
  <EnableHttps>false</EnableHttps>
  <RequireHttps>false</RequireHttps>
  <CertificatePath />
  <CertificatePassword />
  <InternalHttpPort>$port</InternalHttpPort>
  <InternalHttpsPort>$https</InternalHttpsPort>
  <PublicHttpPort>$port</PublicHttpPort>
  <PublicHttpsPort>$https</PublicHttpsPort>
  <AutoDiscovery>false</AutoDiscovery>
  <EnableUPnP>false</EnableUPnP>
  <EnableIPv4>true</EnableIPv4>
  <EnableIPv6>false</EnableIPv6>
  <EnableRemoteAccess>false</EnableRemoteAccess>
  <LocalNetworkSubnets />
  <LocalNetworkAddresses>
    <string>127.0.0.1</string>
  </LocalNetworkAddresses>
  <KnownProxies />
  <IgnoreVirtualInterfaces>true</IgnoreVirtualInterfaces>
  <VirtualInterfaceNames>
    <string>veth</string>
  </VirtualInterfaceNames>
  <EnablePublishedServerUriByRequest>false</EnablePublishedServerUriByRequest>
  <PublishedServerUriBySubnet />
  <RemoteIPFilter />
  <IsRemoteIPFilterBlacklist>false</IsRemoteIPFilterBlacklist>
</NetworkConfiguration>
"@
}
function Write-QaNetworkXml([string]$Version) {
    $config = Join-Path (Get-QaDir $Version) 'config'
    New-Item -ItemType Directory -Force $config | Out-Null
    [IO.File]::WriteAllText((Join-Path $config 'network.xml'), (Get-QaNetworkXml $Version), [Text.UTF8Encoding]::new($false))
}
# the problems with a server's network.xml (none: loopback only, its port, discovery, UPnP, IPv6 and HTTPS off);
# serve.py checks the same again before it starts anything
function Test-QaNetworkXml([string]$Version) {
    $file = Join-Path (Get-QaDir $Version) 'config\network.xml'
    if (-not (Test-Path -LiteralPath $file)) { return @('no network.xml') }
    [xml]$x = Get-Content -LiteralPath $file -Raw
    $n = $x.NetworkConfiguration
    if (-not $n) { return @('network.xml has no NetworkConfiguration') }
    $port = [string]$script:QaPorts[$Version]
    $problems = @()
    $bind = @($n.SelectNodes('LocalNetworkAddresses/string') | ForEach-Object { $_.InnerText.Trim() })
    if (($bind -join ',') -ne '127.0.0.1') { $problems += "LocalNetworkAddresses is '$($bind -join ',')', not 127.0.0.1" }
    # (name, wanted, whether a missing element is fine: the server's default is the wanted value. 10.11 drops the
    # obsolete EnableUPnP when it saves the file; AutoDiscovery and the ports default to unsafe or other values)
    foreach ($rule in @(@('AutoDiscovery', 'false', $false), @('EnableUPnP', 'false', $true), @('EnableIPv6', 'false', $true),
            @('EnableIPv4', 'true', $true), @('EnableHttps', 'false', $true), @('RequireHttps', 'false', $true),
            @('InternalHttpPort', $port, $false), @('PublicHttpPort', $port, $false))) {
        $node = $n.SelectSingleNode($rule[0])
        if (-not $node -and $rule[2]) { continue }
        $v = if ($node) { $node.InnerText.Trim().ToLowerInvariant() } else { '(missing)' }
        if ($v -ne $rule[1]) { $problems += "$($rule[0]) is '$v', not $($rule[1])" }
    }
    $problems
}

# --- the running server: run\state.json (serve.py), checked by pid AND creation time AND command line -------------------
function Get-QaProcess([int]$ProcessId, [long]$Created, [string]$Marker) {
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { return $null }
    try { $c = $p.StartTime.ToFileTimeUtc() } catch { return $null }
    if ([Math]::Abs($c - $Created) -gt 20000) { return $null }   # 2 ms: a reused pid is a different process
    $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId" -ErrorAction SilentlyContinue
    if (-not $cim -or -not $cim.CommandLine -or $cim.CommandLine.IndexOf($Marker, [StringComparison]::OrdinalIgnoreCase) -lt 0) { return $null }
    $p
}
function Get-QaState([string]$Version) {
    $file = Join-Path (Get-QaDir $Version) 'run\state.json'
    if (-not (Test-Path -LiteralPath $file)) { return $null }
    try { $s = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { return $null }
    $dir = Get-QaDir $Version
    $sup = Get-QaProcess $s.supervisor.pid $s.supervisor.created 'serve.py'
    $srv = Get-QaProcess $s.server.pid $s.server.created (Join-Path $dir 'data')
    [pscustomobject]@{ Version = $Version; State = $s; Supervisor = $sup; Server = $srv; Running = [bool]$srv }
}

# --- HTTP ------------------------------------------------------------------------------------------------------------
function Get-QaAuthHeader([string]$Device, [string]$Token) {
    $h = 'MediaBrowser Client="{0}", Device="{1}", DeviceId="{2}", Version="1.0"' -f $script:QaClient, $Device, ("nixie-qa-" + $Device)
    if ($Token) { $h += ', Token="' + $Token + '"' }
    $h
}
# one request; returns @{ Status; Json; Text; Headers }. Never throws for an HTTP status; never prints the token.
function Invoke-Qa {
    param([string]$Version, [string]$Path, [string]$Method = 'GET', $Body = $null, [string]$Token, [string]$Device = 'qa-tools',
        [int]$TimeoutSec = 30, [string]$BaseUrl)
    $base = if ($BaseUrl) { $BaseUrl } else { Get-QaUrl $Version }
    $req = [Net.HttpWebRequest]::Create("$base$Path")
    $req.Method = $Method
    $req.Timeout = $TimeoutSec * 1000
    $req.ReadWriteTimeout = $TimeoutSec * 1000
    $req.Headers['Authorization'] = Get-QaAuthHeader $Device $Token
    $req.Accept = 'application/json'
    if ($null -ne $Body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($(if ($Body -is [string]) { $Body } else { ConvertTo-Json -InputObject $Body -Depth 12 -Compress }))
        $req.ContentType = 'application/json'
        $req.ContentLength = $bytes.Length
        $st = $req.GetRequestStream(); $st.Write($bytes, 0, $bytes.Length); $st.Close()
    }
    elseif ($Method -in @('POST', 'PUT', 'DELETE')) { $req.ContentLength = 0 }
    try { $resp = $req.GetResponse() }
    catch [Net.WebException] {
        $resp = $_.Exception.Response
        if (-not $resp) { return [pscustomobject]@{ Status = 0; Json = $null; Text = $_.Exception.Message; Headers = $null } }
    }
    try {
        $reader = New-Object IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
        $text = $reader.ReadToEnd(); $reader.Close()
        $json = $null
        if ($text -and ($resp.ContentType -like '*json*')) { try { $json = $text | ConvertFrom-Json } catch {} }
        [pscustomobject]@{ Status = [int]$resp.StatusCode; Json = $json; Text = $text; Headers = $resp.Headers }
    }
    finally { $resp.Close() }
}
# 10.11+ answers on its port from a setup page while it starts (/System/Info/Public included, every API call 503):
# ready is the main server's own /health saying Healthy and its anonymous branding answering
function Wait-QaHealthy([string]$Version, [int]$Seconds = 180) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        $h = Invoke-Qa $Version '/health' -TimeoutSec 5
        if ($h.Status -eq 200 -and $h.Text -eq 'Healthy' -and (Invoke-Qa $Version '/Branding/Configuration' -TimeoutSec 5).Status -eq 200) {
            $r = Invoke-Qa $Version '/System/Info/Public' -TimeoutSec 5
            if ($r.Status -eq 200 -and $r.Json -and $r.Json.Version) { return $r.Json }
        }
        Start-Sleep -Milliseconds 500
    }
    $null
}

# --- credentials (outside the repo; never printed) ----------------------------------------------------------------------
function New-QaPassword {
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    ([Convert]::ToBase64String($bytes) -replace '[+/=]', 'x')
}
function Get-QaCredentials([switch]$Create) {
    if (Test-Path -LiteralPath $script:QaCredentialsFile) { return Get-Content -LiteralPath $script:QaCredentialsFile -Raw | ConvertFrom-Json }
    if (-not $Create) { throw "no $script:QaCredentialsFile: run tools\qa-servers\seed.ps1 first" }
    $c = [ordered]@{
        note  = 'Generated test accounts of the local QA Jellyfin servers (127.0.0.1:8196 and :8296) only. Never commit or print.'
        admin = [ordered]@{ username = 'qa-admin'; password = (New-QaPassword) }
        user  = [ordered]@{ username = 'qa-user'; password = (New-QaPassword) }
    }
    New-Item -ItemType Directory -Force $script:QaRoot | Out-Null
    [IO.File]::WriteAllText($script:QaCredentialsFile, ($c | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Get-Content -LiteralPath $script:QaCredentialsFile -Raw | ConvertFrom-Json
}
# sign in by name; returns @{ Token; UserId; IsAdmin } (the token stays in memory)
function Connect-Qa([string]$Version, [ValidateSet('admin', 'user')][string]$Who, [string]$Device = 'qa-tools') {
    $c = (Get-QaCredentials).$Who
    $r = Invoke-Qa $Version '/Users/AuthenticateByName' 'POST' @{ Username = $c.username; Pw = $c.password } -Device $Device
    if ($r.Status -ne 200 -or -not $r.Json.AccessToken) { throw "sign-in as the QA $Who on $Version failed (HTTP $($r.Status))" }
    [pscustomobject]@{ Token = $r.Json.AccessToken; UserId = $r.Json.User.Id; IsAdmin = [bool]$r.Json.User.Policy.IsAdministrator; Name = $c.username }
}

# --- start / stop -----------------------------------------------------------------------------------------------------
function Start-QaServer([string]$Version, [string]$Python, [double]$MaxMinutes = 240, [int]$HealthSeconds = 180) {
    $dir = Get-QaDir $Version
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'server\jellyfin.exe'))) { throw "$Version is not installed ($dir\server\jellyfin.exe)" }
    $st = Get-QaState $Version
    if ($st -and $st.Running) { return $st }
    $problems = @(Test-QaNetworkXml $Version)
    if ($problems) { throw "$Version not started: network.xml: $($problems -join '; ')" }
    $py = Resolve-QaPython $Python
    $serve = Join-Path $PSScriptRoot 'serve.py'
    New-Item -ItemType Directory -Force (Join-Path $dir 'run'), (Join-Path $dir 'log') | Out-Null
    $stateFile = Join-Path $dir 'run\state.json'
    if (Test-Path -LiteralPath $stateFile) { Remove-Item -LiteralPath $stateFile -Force }
    $argList = @('-I', '-B', ('"' + $serve + '"'), '--root', ('"' + $script:QaRoot + '"'), '--version', $Version, '--max-minutes', $MaxMinutes)
    $sup = Start-Process -FilePath $py -ArgumentList $argList -WindowStyle Hidden -PassThru `
        -RedirectStandardError (Join-Path $dir 'log\serve-stderr.log') -RedirectStandardOutput (Join-Path $dir 'log\serve-stdout.log')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $stateFile) -and $sw.Elapsed.TotalSeconds -lt 20 -and -not $sup.HasExited) { Start-Sleep -Milliseconds 200 }
    $st = Get-QaState $Version
    if (-not $st -or -not $st.Running) {
        throw "$Version did not start (serve.py exit $(if ($sup.HasExited) { $sup.ExitCode } else { 'still running' }); see $dir\log\serve.log and serve-stderr.log)"
    }
    $info = Wait-QaHealthy $Version $HealthSeconds
    # the firewall's prompt comes from a listener beyond loopback: any such listener stops the server at once
    $listen = @(Get-QaListeners $st.State.server.pid)
    $wide = @($listen | Where-Object { $_ -notmatch '^(tcp|udp) 127\.0\.0\.1:' })
    if ($wide) {
        Stop-QaServer $Version -Force | Out-Null
        throw "$Version listened beyond loopback ($($wide -join ', ')): stopped at once"
    }
    if (-not $info) {
        Stop-QaServer $Version -Force | Out-Null
        throw "$Version started but was not ready within $HealthSeconds s: stopped again (see $dir\log)"
    }
    $st
}
function Get-QaListeners([int]$ProcessId) {
    @(Get-NetTCPConnection -OwningProcess $ProcessId -State Listen -ErrorAction SilentlyContinue | ForEach-Object { "tcp $($_.LocalAddress):$($_.LocalPort)" }) +
    @(Get-NetUDPEndpoint -OwningProcess $ProcessId -ErrorAction SilentlyContinue | ForEach-Object { "udp $($_.LocalAddress):$($_.LocalPort)" })
}
# graceful first (the admin's POST /System/Shutdown), then serve.py's stop file (it ends its job), and only then the
# supervisor itself, proven ours by pid, creation time and command line (its job ends the server with it)
function Stop-QaServer([string]$Version, [switch]$Force) {
    $st = Get-QaState $Version
    if (-not $st) { return 'stopped' }
    $dir = Get-QaDir $Version
    $how = $null
    if ($st.Running -and -not $Force -and (Test-Path -LiteralPath $script:QaCredentialsFile)) {
        try {
            $admin = Connect-Qa $Version 'admin' 'qa-stop'
            $r = Invoke-Qa $Version '/System/Shutdown' 'POST' -Token $admin.Token -Device 'qa-stop'
            if ($r.Status -in 200, 204) {
                if ($st.Server.WaitForExit(30000)) { $how = 'shut down' }
            }
        }
        catch {}
    }
    if (-not $how -and $st.Supervisor) {
        [IO.File]::WriteAllText((Join-Path $dir 'run\stop'), 'stop')
        if ($st.Supervisor.WaitForExit(20000)) { $how = 'ended (stop file)' }
    }
    elseif (-not $how -and $st.Server) {
        # no supervisor of ours, yet the server it started still runs: impossible with a kill-on-close job
        throw "$Version server runs without its supervisor; not touching it (pid $($st.State.server.pid))"
    }
    if ($how -and $st.Supervisor) { [void]$st.Supervisor.WaitForExit(20000) }
    if (-not $how) {
        $again = Get-QaState $Version
        if ($again -and $again.Supervisor) { $again.Supervisor.Kill(); [void]$again.Supervisor.WaitForExit(10000); $how = 'ended (supervisor ended, its job with it)' }
        elseif (-not ($again -and $again.Running)) { $how = 'stopped' }
    }
    $stateFile = Join-Path $dir 'run\state.json'
    $after = Get-QaState $Version
    if ($after -and -not $after.Supervisor -and -not $after.Running -and (Test-Path -LiteralPath $stateFile)) { Remove-Item -LiteralPath $stateFile -Force }
    if ($after -and $after.Running) { throw "$Version still running after stop" }
    $how
}
