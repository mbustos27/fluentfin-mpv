<#
  The basics gate (tools\gate\README.md), a 'script' check: the mpv self-test (Nixie.exe --mpv-selftest, no server: mpv's
  test pattern in the player page with FluentFin's controls, then a normal close), off-screen through
  tools\release\selftest-offscreen.ps1, as verify-package.ps1's SelfTest run does. tools\ui-harness\selftest-gate.ps1 sends
  keys on the user's desktop, so it can't be part of the gate.
  It holds one of the machine's off-screen slots (offscreen\prepare.ps1's Enter-HarnessRunSlot, run-offscreen.ps1's lock)
  for its run. A release package plays with its bundled backend; a dev build with -Backend's, through the harness's
  run_backend.py, as run-offscreen.ps1 sets it. Writes <Out>\result.json (drive.ps1's shape); exit 0 when every check passed.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$Cache,
    [Parameter(Mandatory)][string]$Backend,
    [string]$Theme,
    [int]$QueueMinutes = 120,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $repo 'tools\ui-harness\offscreen\prepare.ps1')
$Out = [IO.Path]::GetFullPath($Out)
$data = Join-Path $Out 'data'
$envA = Join-Path $Out 'env\appdata'; $envL = Join-Path $Out 'env\localappdata'
New-Item -ItemType Directory -Force $Out, $data, $envA, $envL | Out-Null
$dll = Join-Path (Split-Path $Exe) 'Nixie.dll'
$release = (Test-Path -LiteralPath $dll) -and [bool](Select-String -LiteralPath $dll -SimpleMatch -Pattern 'FluentFinMpvRelease' -Quiet)
$backendCommand = if ($release) { '' } else {
    '"{0}" -I -X "pycache_prefix={1}" "{2}" "{3}"' -f $Python, (Join-Path $Cache 'pycache'), (Join-Path $repo 'tools\ui-harness\offscreen\run_backend.py'), $Backend
}
[void](Initialize-HarnessSettings $data $backendCommand @('MpvPlayerControls=FluentFin'))
[void](Initialize-HarnessMpvConf $data)
$windowsPath = "$env:SystemRoot\System32;$env:SystemRoot;$env:SystemRoot\System32\Wbem;$env:SystemRoot\System32\WindowsPowerShell\v1.0"
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$selftestArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'tools\release\selftest-offscreen.ps1'), '-Exe', $Exe, '-DataDir', $data, '-Out', $Out)

$slot = Enter-HarnessRunSlot (Get-HarnessRunSlots) ([TimeSpan]::FromMinutes($QueueMinutes))
if (-not $slot) { throw "the other off-screen runs kept the harness for $QueueMinutes min" }
$started = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0   # the run's own time starts with its slot (result.json's started)
try {
    Write-Host "selftest: off-screen slot $($slot.Slot); $Exe$(if ($release) { ' (a release package: its bundled backend)' })"
    & $Python -B -I $hidden --log (Join-Path $Out 'hidden_run.log') --timeout $TimeoutSeconds --desktop "ffg-selftest-$PID" `
        --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" --env "PATH=$windowsPath" -- $ps @selftestArgs | Out-Host
}
finally { Exit-HarnessRunSlot $slot }

$checks = [ordered]@{}
$json = Join-Path $Out 'selftest.json'
if (Test-Path -LiteralPath $json) {
    foreach ($p in (Get-Content -LiteralPath $json -Raw | ConvertFrom-Json).PSObject.Properties) { $checks["selftest: $($p.Name)"] = [ordered]@{ ok = [bool]$p.Value.ok; detail = "$($p.Value.detail)" } }
}
else { $checks['selftest: it ran'] = [ordered]@{ ok = $false; detail = "no selftest.json (see $Out\hidden_run.log)" } }
$stray = @(Get-ChildItem $envA, $envL -Recurse -File -Force -ErrorAction SilentlyContinue)
$checks['selftest: nothing written to APPDATA or LOCALAPPDATA'] = [ordered]@{ ok = ($stray.Count -eq 0); detail = (($stray | ForEach-Object { $_.FullName.Substring($Out.Length) }) -join ', ') }
$failed = @($checks.Keys | Where-Object { -not $checks[$_].ok })
[IO.File]::WriteAllText((Join-Path $Out 'result.json'), ([ordered]@{ started = $started; checks = $checks; failed = $failed } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
foreach ($k in $checks.Keys) { Write-Host ("{0} {1}{2}" -f $(if ($checks[$k].ok) { 'PASS' } else { 'FAIL' }), $k, $(if ($checks[$k].detail) { ": $($checks[$k].detail)" })) }
if ($failed.Count) { exit 1 }
exit 0
