<#
.SYNOPSIS
  The local QA Jellyfin servers (10.11 on 127.0.0.1:8196, 12 on 127.0.0.1:8296): start, stop, status, reset.

.DESCRIPTION
  start   Starts the server hidden (serve.py: a private desktop, a kill-on-close job; no window, no service), waits until
          it answers, and checks it listens on 127.0.0.1 only: a listener anywhere else stops it at once. Refuses to
          start unless config\network.xml binds 127.0.0.1 only on its port with discovery, UPnP, IPv6 and HTTPS off.
          A server ends by itself after -MaxMinutes (240): servers run only while a check needs them.
  stop    The admin's POST /System/Shutdown, else serve.py's stop file (it ends its job), else serve.py itself (proven
          ours by pid, creation time and command line; its job ends the server with it). Nothing else is touched.
  status  Running or not, the version that answers, the startup wizard, what it listens on, the seeded snapshot.
  reset   Stops it, puts back the seeded state (seed.ps1's snapshot in <version>\seeded: data and config), and starts
          it again if it was running.

  Installs live in %USERPROFILE%\Dev\jellyfin-qa\<version> (NIXIE_QA_ROOT overrides), outside the repo; the generated
  test accounts in qa-credentials.json there. Nothing prints a password or a token. docs\QA-SERVERS.md.

.EXAMPLE
  .\qa.ps1 start -Version 12
  .\qa.ps1 status
  .\qa.ps1 reset -Version all
  .\qa.ps1 stop
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory, Position = 0)][ValidateSet('start', 'stop', 'status', 'reset')][string]$Command,
    [ValidateSet('10.11', '12', 'all')][string]$Version = 'all',
    [string]$Python,
    [double]$MaxMinutes = 240
)
. (Join-Path $PSScriptRoot 'qa-common.ps1')

function Show-Status([string]$V) {
    $dir = Get-QaDir $V
    $st = Get-QaState $V
    $installed = Test-Path -LiteralPath (Join-Path $dir 'server\jellyfin.exe')
    $seeded = Test-Path -LiteralPath (Join-Path $dir 'seeded\seed.json')
    $line = [ordered]@{ Version = $V; Url = (Get-QaUrl $V); Installed = $installed; Seeded = $seeded; Running = $false
        Server = ''; Wizard = ''; Listening = ''; Uptime = '' }
    if ($st -and $st.Running) {
        $line.Running = $true
        $info = (Invoke-Qa $V '/System/Info/Public' -TimeoutSec 5).Json
        if ($info) { $line.Server = "Jellyfin $($info.Version)"; $line.Wizard = if ($info.StartupWizardCompleted) { 'done' } else { 'not done' } }
        $line.Listening = (Get-QaListeners $st.State.server.pid) -join ' '
        $line.Uptime = '{0:N0} min' -f (([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $st.State.started) / 60)
    }
    [pscustomobject]$line
}

$versions = Resolve-QaVersions $Version
switch ($Command) {
    'start' {
        foreach ($v in $versions) {
            $st = Start-QaServer $v $Python $MaxMinutes
            $info = (Invoke-Qa $v '/System/Info/Public').Json
            Write-Host ("{0}: up on {1} (Jellyfin {2}, wizard {3}), listening {4}" -f $v, (Get-QaUrl $v), $info.Version,
                $(if ($info.StartupWizardCompleted) { 'done' } else { 'not done' }), ((Get-QaListeners $st.State.server.pid) -join ' '))
        }
    }
    'stop' {
        foreach ($v in $versions) { Write-Host ("{0}: {1}" -f $v, (Stop-QaServer $v)) }
    }
    'status' {
        $versions | ForEach-Object { Show-Status $_ } | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    }
    'reset' {
        foreach ($v in $versions) {
            $dir = Get-QaDir $v
            $snap = Join-Path $dir 'seeded'
            if (-not (Test-Path -LiteralPath (Join-Path $snap 'seed.json'))) { throw "$v has no seeded state ($snap): run seed.ps1 -Version $v" }
            $st = Get-QaState $v
            $wasRunning = [bool]($st -and $st.Running)
            [void](Stop-QaServer $v)
            foreach ($d in 'data', 'config', 'cache') {
                $p = Join-Path $dir $d
                if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
            }
            foreach ($d in 'data', 'config') { Copy-Item -LiteralPath (Join-Path $snap $d) -Destination (Join-Path $dir $d) -Recurse }
            New-Item -ItemType Directory -Force (Join-Path $dir 'cache') | Out-Null
            Write-QaNetworkXml $v
            Write-Host "${v}: reset to the seeded state of $((Get-Content -LiteralPath (Join-Path $snap 'seed.json') -Raw | ConvertFrom-Json).seeded)"
            if ($wasRunning) { [void](Start-QaServer $v $Python $MaxMinutes); Write-Host "${v}: started again" }
        }
    }
}
