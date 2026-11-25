<#
  tools\gate\test_gate.ps1's stand-in check (a 'script' check that starts nothing): writes <Out>\result.json with three
  checks, the last two failing with -Fail, and exits 1 with -Fail, else 0. -NoResult leaves no result.json; -Throw throws.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out, [string]$Exe, [string]$Python, [string]$Cache, [string]$Backend, [string]$Theme,
    [switch]$Fail, [switch]$NoResult, [switch]$Throw,
    # (1.7.5 gate fix) the first run fails with a monitor removal in its display (drive.ps1's Kernel-PnP), the next one passes
    [switch]$RemovalOnce
)
$ErrorActionPreference = 'Stop'
if ($Throw) { throw "the fixture threw on purpose ($Theme)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$runs = Join-Path (Split-Path $Out) "$(Split-Path $Out -Leaf).runs"
$removal = $false
if ($RemovalOnce -and -not (Test-Path $runs)) { [IO.File]::WriteAllText($runs, '1'); $removal = $true; $Fail = [switch]$true }
$id = $env:GATE_ID
$checks = [ordered]@{
    "${id}: the first thing shows" = [ordered]@{ ok = $true; detail = "theme $Theme, layout $env:NIXIE_LAYOUT, sizes $env:GATE_SIZES" }
    "${id}: the second thing shows" = [ordered]@{ ok = -not $Fail; detail = 'the fixture failed on purpose' }
    "${id}: the third thing shows" = [ordered]@{ ok = -not $Fail; detail = 'and again' }
    # test_gate.ps1 test 12: what of a part selector reaches a job (the caller's shell must not)
    "${id}: the parts it was given" = [ordered]@{ ok = $true; detail = "parts '$env:SS_PARTS'" }
    # ... while Windows' own (no registry scope lists SystemRoot or LOCALAPPDATA) always do
    "${id}: Windows' own variables reach it" = [ordered]@{ ok = [bool]$env:SystemRoot -and [bool]$env:LOCALAPPDATA -and [bool]$env:USERPROFILE; detail = 'SystemRoot, LOCALAPPDATA, USERPROFILE' }
}
$result = [ordered]@{ checks = $checks }
if ($removal) { $result.display = [ordered]@{ events = @(); removals = @('23:30:21.288 DISPLAY\AUS24AC\7&a2a444a&7&UID520') } }
if (-not $NoResult) { [IO.File]::WriteAllText((Join-Path $Out 'result.json'), ($result | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false)) }
Write-Host "fixture: $id [$Theme] exe $Exe"
if ($Fail) { exit 1 }
exit 0
