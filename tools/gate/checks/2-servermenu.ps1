<#
.SYNOPSIS
  The basics gate, class 2 (Nixie 1.7.5, g2; manifest\g2.psd1 id "servermenu", Kind script): 1.7.4's
  tools\ui-harness\offscreen\before-174-servermenu.ps1 as it is, registered. A saved server's More options with two saved servers,
  one of them offline (a closed loopback port): a click, Enter, Space, gamepad A and the menu key each open a menu with
  Remove and nothing else (the page stays, the server is not asked), Escape closes it, Remove removes it (row and file).

.DESCRIPTION
  The offline server has to be saved before the app starts, so this wrapper writes it into the run's data folder first
  (run-offscreen.ps1 keeps loopback servers), then runs run-offscreen.ps1 -Before before-174-servermenu.ps1 in the theme
  given, and writes <Out>\result.json with the run's checks named "servermenu: ...". Exit 0 when every check passed.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Python,
    [string]$Cache,
    [string]$Backend,
    [string]$Theme = 'Classic'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$Out = [IO.Path]::GetFullPath($Out)
$data = Join-Path $Out 'data'
$run = Join-Path $Out 'run'
New-Item -ItemType Directory -Force $data | Out-Null
$settings = Join-Path $data 'LocalSettings.json'
$spare = [ordered]@{ DisplayName = 'Spare (closed port)'; LocalUrl = 'http://127.0.0.1:9'; PublicUrl = 'http://127.0.0.1:9'; LocalNetworkNames = @(); Id = 'spare-closed-port'; Users = @() }
[IO.File]::WriteAllText($settings, (ConvertTo-Json -Depth 5 -InputObject ([ordered]@{ Servers = @($spare) })), [Text.UTF8Encoding]::new($false))

$env:NIXIE_LAYOUT = $Theme
# the runner's own queue time for a 'then' check (other streams share the 3 slots)
$a = @{ DataDir = $data; Out = $run; Exe = $Exe; Python = $Python; Script = @('login', 'close'); NoA11y = $true; QueueMinutes = 120; TimeoutSeconds = 600
    Before = (Join-Path $repo 'tools\ui-harness\offscreen\before-174-servermenu.ps1') }
if ($Cache) { $a.Cache = $Cache }
if ($Backend) { $a.Backend = $Backend }
& (Join-Path $repo 'tools\ui-harness\run-offscreen.ps1') @a | Out-Host
$code = $LASTEXITCODE

$checks = [ordered]@{}
$file = Join-Path $run 'result.json'
if (Test-Path -LiteralPath $file) {
    $r = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json
    foreach ($p in $r.checks.PSObject.Properties) { $checks["servermenu: $($p.Name)"] = [ordered]@{ ok = [bool]$p.Value.ok; detail = [string]$p.Value.detail } }
}
else { $checks['servermenu: the run wrote its result'] = [ordered]@{ ok = $false; detail = "no $file (run-offscreen.ps1 exit $code)" } }
$failed = @($checks.Values | Where-Object { -not $_.ok }).Count
[IO.File]::WriteAllText((Join-Path $Out 'result.json'), (ConvertTo-Json -Depth 5 -InputObject ([ordered]@{ checks = $checks; theme = $Theme; run = $run })), [Text.UTF8Encoding]::new($false))
if ($failed -or $code -ne 0) { exit 1 }
exit 0
