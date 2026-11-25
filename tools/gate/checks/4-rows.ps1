<#
.SYNOPSIS
  The basics gate (Nixie 1.7.5, class 4, g3): every horizontal row reaches its last item with every input, in one theme,
  through tools\ui-harness\rows-gate.ps1 (then-174-rows.ps1), at real shape.

.DESCRIPTION
  Kind script (tools\gate\README.md): -Theme maps to rows-gate.ps1 -Themes; the fixture is real-shaped: FAKESERVER_BIG_SERIES
  =24x13 (rows-gate's: 13-episode seasons, 16 long names), FAKESERVER_COLLECTIONS=1 and FAKESERVER_REAL_SHAPE=1 (a season of
  24 episodes with long names, a film with a cast of 48). Pages: rows-gate's and season24 and film. "max" must be at least
  2560 x 1392 (RG_MAX_MIN): with the 2560 x 1440 monitor gone the run fails and says so (1.7.4's integration ran Cinematic
  and Editorial at 1936 x 1048 and passed). Each row x input x size is one check in <Out>\result.json, named
  "rows [<size> <page>]: '<row>' <input>"; exit 0 when every one passed.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Python,
    [string]$Cache,
    [string]$Backend,
    [string]$Theme = 'Classic',
    [string]$Sizes = 'max,1280x720',
    [string]$Pages = 'season,season24,cast,series,film,home,collections,movie,library,search,topbar',
    [ValidateSet('auto', 'full', 'quick')][string]$Depth = 'auto',
    [int]$RowsPerPage = 2,
    [string]$MaxMin = '2560x1392',
    # (rows-gate.ps1 waits for a slot as long as this, in minutes: other runs share the 3 slots)
    [int]$TimeoutSeconds = 7200
)
$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null
$id = if ($env:GATE_ID) { $env:GATE_ID } else { 'rows' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$env:FAKESERVER_REAL_SHAPE = '1'
$env:RG_MAX_MIN = $MaxMin
$rgArgs = @{ Out = (Join-Path $Out 'rows'); Exe = $Exe; Python = $Python; Themes = @($Theme); Sizes = $Sizes; Pages = $Pages; Depth = $Depth
    RowsPerPage = $RowsPerPage; TimeoutSeconds = $TimeoutSeconds }
if ($Cache) { $rgArgs.Cache = $Cache }
# (a run that could not start, its slot never free within the queue's time, throws: said as the gate's reason, never lost)
$crash = $null
try { & (Join-Path $repo 'tools\ui-harness\rows-gate.ps1') @rgArgs | Out-Host; $code = $LASTEXITCODE }
catch { $crash = $_.Exception.Message; $code = 2; Write-Host "rows-gate failed: $crash" }
Remove-Item Env:RG_MAX_MIN -ErrorAction SilentlyContinue

$file = Join-Path $Out "rows\$Theme\rows174\rows174.json"
$rows = if (Test-Path $file) { @(Get-Content $file -Raw -Encoding utf8 | ConvertFrom-Json | ForEach-Object { $_ }) } else { @() }
$checks = [ordered]@{}
foreach ($r in $rows) {
    $name = "${id} [$($r.size) $($r.page)]: '$($r.row)' $($r.input)"
    $n = 2; $base = $name; while ($checks.Contains($name)) { $name = "$base #$n"; $n++ }
    $checks[$name] = [ordered]@{ ok = [bool]$r.ok; detail = "$($r.verdict); $($r.detail)" }
}
if (-not $rows.Count) { $checks["${id}: the rows were checked"] = [ordered]@{ ok = $false; detail = "no rows174.json (rows-gate exit $code$(if ($crash) { ": $crash" }); see $(Join-Path $Out 'rows'))" } }
# the run's own checks (sign-in, the auth form, a crash at the close) are drive.ps1's: in its result.json
$run = Join-Path $Out "rows\$Theme\result.json"
if (Test-Path $run) {
    $r = Get-Content $run -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($p in $r.checks.PSObject.Properties) { if ($p.Name -notlike 'rows174 *' -and -not $p.Value.ok) { $checks["${id}: $($p.Name)"] = [ordered]@{ ok = $false; detail = "$($p.Value.detail)" } } }
}
$failed = @($checks.Keys | Where-Object { -not $checks[$_].ok })
[IO.File]::WriteAllText((Join-Path $Out 'result.json'), ([ordered]@{ checks = $checks; failed = $failed } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Host ("{0} ({1}): {2}/{3} checks passed" -f $id, $Theme, ($checks.Count - $failed.Count), $checks.Count)
if ($failed.Count -or $code -ne 0) { exit 1 }
exit 0
