<#
.SYNOPSIS
  1.5 qacheck (opt-in): Nixie against a QA Jellyfin server behind a reverse proxy on a sub-path, and with the address
  typed with a "/" at its end: sign-in, browsing, images, playback and the backend's URLs.

.DESCRIPTION
  Each case is one off-screen run (tools\ui-harness\run-offscreen.ps1 -QaServer, silent, the harness's lock), on its own
  data folder under -Out:
    subpath        http://127.0.0.1:<tap>/jellyfin   (qaproxy.py --base-path /jellyfin: a path outside it is a 404,
                                                      a path under it goes on to the server without it, as nginx's
                                                      `location /jellyfin/ { proxy_pass http://server/; }`)
    subpath-slash  http://127.0.0.1:<tap>/jellyfin/  (before-qa-slash.ps1)
    slash          http://127.0.0.1:<tap>/
  The steps: sign-in (Quick Connect), home, library, movie, then then-qa-subpath.ps1: the movie plays for 6 s and the
  tap's log is checked (nothing outside the sub-path, no "//", no failed request but the ones every ordinary user gets,
  images and the backend and the stream through it). A table of the cases' checks at the end; exit 1 if any failed.

.EXAMPLE
  tools\qa-servers\subpath-probe.ps1 -Version 12 -Exe <Nixie.exe> -Out <scratch>\subpath
  tools\qa-servers\subpath-probe.ps1 -Version all -Cases subpath-slash -Exe <Nixie.exe> -Out <scratch>\subpath -Python <venv python.exe>
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Out,
    [ValidateSet('10.11', '12', 'all')][string]$Version = '12',
    [ValidateSet('subpath', 'subpath-slash', 'slash')][string[]]$Cases = @('subpath', 'subpath-slash', 'slash'),
    [string]$Python,
    [string]$Backend,
    # how long each case waits for an off-screen slot (run-offscreen.ps1 -QueueMinutes)
    [int]$QueueMinutes = 60
)
$ErrorActionPreference = 'Stop'
$run = Join-Path $PSScriptRoot '..\ui-harness\run-offscreen.ps1'
$Out = [IO.Path]::GetFullPath($Out)
$versions = if ($Version -eq 'all') { @('10.11', '12') } else { @($Version) }
$extra = @{}
if ($Python) { $extra.Python = $Python }
if ($Backend) { $extra.Backend = $Backend }
$rows = New-Object System.Collections.ArrayList
$old = $env:NIXIE_QA_BASE_PATH
try {
    foreach ($v in $versions) {
        foreach ($case in $Cases) {
            $name = "$case-$v"
            $dir = Join-Path $Out $name
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
            if ($case -like 'subpath*') { $env:NIXIE_QA_BASE_PATH = '/jellyfin' } else { Remove-Item Env:NIXIE_QA_BASE_PATH -ErrorAction SilentlyContinue }
            $before = if ($case -like '*slash') { @{ Before = (Join-Path $PSScriptRoot 'before-qa-slash.ps1') } } else { @{} }
            Write-Host "=== $name"
            & $run -QaServer $v -DataDir (Join-Path $dir 'data') -Out (Join-Path $dir 'run') -Exe $Exe -Script 'home,library,movie' `
                -Then (Join-Path $PSScriptRoot 'then-qa-subpath.ps1') -NoA11y -TimeoutSeconds 480 -QueueMinutes $QueueMinutes @before @extra | Out-Host
            $result = Join-Path $dir 'run\result.json'
            if (-not (Test-Path -LiteralPath $result)) { [void]$rows.Add([pscustomobject]@{ Case = $name; Check = 'run'; Result = 'FAIL'; Detail = 'no result.json' }); continue }
            $r = Get-Content -LiteralPath $result -Raw | ConvertFrom-Json
            foreach ($p in $r.checks.PSObject.Properties) {
                [void]$rows.Add([pscustomobject]@{ Case = $name; Check = $p.Name; Result = $(if ($p.Value.ok) { 'PASS' } else { 'FAIL' }); Detail = "$($p.Value.detail)" })
            }
        }
    }
}
finally { if ($null -ne $old) { $env:NIXIE_QA_BASE_PATH = $old } else { Remove-Item Env:NIXIE_QA_BASE_PATH -ErrorAction SilentlyContinue } }
$rows | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
$failed = @($rows | Where-Object { $_.Result -ne 'PASS' })
Write-Host ("{0} of {1} checks passed" -f ($rows.Count - $failed.Count), $rows.Count)
if ($failed) { exit 1 }
