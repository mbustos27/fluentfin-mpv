<#
.SYNOPSIS
  End-to-end crash-rule scenarios (docs/BRIDGE.md §3, §6) against the real backend, off-screen.

.DESCRIPTION
  Builds e2e.exe from a copy of this folder under -Out (against -UiDir, default this repo's ui\; its FluentFin.Core is
  copied under -Out too and built there, so nothing is written into -UiDir), makes a small test video with mpv's encoder
  unless -Mkv is given, then runs drive.py (python -B: no __pycache__ in the repo) through
  tools\backend-bench\hidden_run.py: on a private desktop (nothing appears on the user's), inside a kill-on-close job
  (everything started dies with it), with APPDATA and LOCALAPPDATA in -Out and mpv on PATH. Fake servers and a fake token
  only. Prints each run's verdict and a summary; writes everything (logs, per-run output, summary.json) under -Out. Exit
  code 0 = every run passed.

  -Scenarios takes several names, each name[:reps]: as a PowerShell list (-Scenarios kill_playing,close_playing:3) or as
  one string with commas or spaces ("kill_playing close_playing:3", which is what arrives from cmd or bash). A name given
  without -Scenarios is refused, never bound to another parameter. 'all' = every scenario once.

.EXAMPLE
  .\run.ps1 -Out C:\scratch\e2e -Scenarios all
  .\run.ps1 -Out C:\scratch\e2e -Scenarios kill_playing,signout_playing:3
  .\run.ps1 -Out C:\scratch\e2e-load -Scenarios held_kill_close:5 -Burn 16
  .\run.ps1 -Out C:\scratch\e2e-before -UiDir C:\scratch\before\ui -Scenarios replay_opening_kill
  powershell -File run.ps1 -Out C:\scratch\e2e -Scenarios "kill_playing signout_playing"
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string[]]$Scenarios = @('all'),
    [string]$UiDir,
    [string]$Python,
    [string]$Mkv,
    [int]$Burn = 0,
    [int]$TimeoutSeconds = 3600
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $UiDir) { $UiDir = Join-Path $repo 'ui' }
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be outside the repository" }
$UiDir = [IO.Path]::GetFullPath($UiDir)
if (-not (Test-Path (Join-Path $UiDir 'FluentFin.Core\FluentFin.Core.csproj'))) { throw "-UiDir $UiDir has no FluentFin.Core\FluentFin.Core.csproj" }
# several names, from a PowerShell list or one string with commas or spaces (cmd, bash)
$Scenarios = @($Scenarios | ForEach-Object { $_ -split '[,\s]+' } | Where-Object { $_ })
if ($Scenarios.Count -eq 0) { throw "-Scenarios is empty" }
foreach ($s in $Scenarios) { if ($s -notmatch '^[a-z_]+(:\d+)?$') { throw "-Scenarios: '$s' is not name[:reps]" } }
New-Item -ItemType Directory -Force $Out | Out-Null
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'

# mpv: the official CI build from winget, on the Machine+User PATH (this shell's PATH may be older than the install)
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$mpvCmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
if (-not $mpvCmd) { throw "mpv.exe is not on PATH (winget install mpv-player.mpv-CI.MSVC)" }
$mpvExe = $mpvCmd.Source
$item = Get-Item $mpvExe
if ($item.LinkType -and $item.Target) {
    # a winget Links shim: the package folder holds mpv's DLLs
    $target = @($item.Target)[0]
    if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path $mpvExe) $target }
    $mpvExe = [IO.Path]::GetFullPath($target)
}
$mpvDir = Split-Path $mpvExe
$dotnetDir = Split-Path (Get-Command dotnet.exe).Source
$sysRoot = $env:SystemRoot

# build from a copy, so builds against different ui\ copies never share bin\ or obj\; FluentFin.Core from a copy as well
# (outside e2e-src, whose project would compile its sources): a ProjectReference builds into its own folder, and that must
# not be -UiDir (the repo's ui\, or a copy another run is testing)
$src = Join-Path $Out 'e2e-src'
New-Item -ItemType Directory -Force $src | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'e2e.csproj'), (Join-Path $PSScriptRoot 'Program.cs') $src -Force
Copy-Item (Join-Path $repo 'nuget.config') $src -Force
$coreRoot = Join-Path $Out 'e2e-core'
$core = Join-Path $coreRoot 'FluentFin.Core'
New-Item -ItemType Directory -Force $core | Out-Null
& robocopy (Join-Path $UiDir 'FluentFin.Core') $core /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copying FluentFin.Core failed (robocopy $LASTEXITCODE)" }
Copy-Item (Join-Path $repo 'nuget.config') $coreRoot -Force
Write-Host "building e2e against $UiDir (FluentFin.Core copied to $core)"
& dotnet build (Join-Path $src 'e2e.csproj') -c Debug "-p:UiDir=$UiDir" "-p:CoreDir=$core" -nologo -v:q | Out-Host
if ($LASTEXITCODE -ne 0) { throw "e2e build failed" }
$exe = Join-Path $src 'bin\Debug\net10.0-windows\e2e.exe'

if (-not $Mkv) {
    $Mkv = Join-Path $Out 'testsrc.mkv'
    if (-not (Test-Path $Mkv)) {
        Write-Host "making $Mkv (60 s test pattern, mpv's encoder, off-screen)"
        & $Python $hidden --log (Join-Path $Out 'mkv.log') --timeout 300 --desktop "ui-e2e-mkv-$PID" -- `
            $mpvExe --no-config 'av://lavfi:testsrc=size=640x360:rate=25:duration=60' "--o=$Mkv" --of=matroska --ovc=mpeg4 --ovcopts=b=600k | Out-Host
        if (-not (Test-Path $Mkv)) { throw "could not make $Mkv (see mkv.log)" }
    }
}

$envA = Join-Path $Out 'env\a'; $envL = Join-Path $Out 'env\l'
New-Item -ItemType Directory -Force $envA, $envL | Out-Null
$runOut = Join-Path $Out 'runs'
New-Item -ItemType Directory -Force $runOut | Out-Null
$log = Join-Path $Out 'drive.log'
$driveArgs = @('-B', '-u', (Join-Path $PSScriptRoot 'drive.py'), '--exe', $exe, '--python', $Python, '--out', $runOut, '--mkv', $Mkv, '--burn', $Burn) + $Scenarios
Write-Host "running $($Scenarios -join ' ') off-screen (log: $log)"
& $Python $hidden --log $log --timeout $TimeoutSeconds --desktop "ui-e2e-$PID" `
    --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" `
    --env "PATH=$mpvDir;$sysRoot\System32;$sysRoot;$dotnetDir" --env PYTHONUTF8=1 `
    -- $Python @driveArgs | Out-Host
$code = $LASTEXITCODE
Get-Content $log | Select-String -Pattern 'VERDICT|summary|^\[\s*[\d.]+\]\s+\S+-\d+\s+(PASS|FAIL)' | ForEach-Object { $_.Line }
exit $code
