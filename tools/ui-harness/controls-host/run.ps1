<#
.SYNOPSIS
  The player controls' test host: FluentFin's controls window over the real backend's embedded mpv, in a window of its
  own (never Seaglass.exe), off-screen.

.DESCRIPTION
  Builds ControlsHost.exe from a copy of this folder under -Out against -UiDir (default this repo's ui\; its
  FluentFin.Core is copied under -Out and built there, so nothing is written into -UiDir), then runs one scenario through
  tools\backend-bench\hidden_run.py: on a private desktop (nothing appears on the user's), inside a kill-on-close job
  (everything it starts, the backend and mpv included, dies with it), with APPDATA and LOCALAPPDATA under -Out and mpv on
  PATH. Fake servers and fake tokens only. The host writes host.txt (CHECK lines, RESULT line) and shots\*.png (pictures
  of its own windows: PrintWindow, never a screen copy) under -Out\<scenario>. Exit code 0 = every check passed.

  -Visible runs it on the user's desktop instead (a small window for a few seconds), for when the private desktop does not
  render.

.EXAMPLE
  .\run.ps1 -Out C:\scratch\controls -Scenario spike
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Scenario = 'spike',
    [string]$UiDir,
    [string]$Python,
    [switch]$Visible,
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $UiDir) { $UiDir = Join-Path $repo 'ui' }
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be outside the repository" }
$UiDir = [IO.Path]::GetFullPath($UiDir)
if ($Scenario -notmatch '^[a-z_]+$') { throw "-Scenario: '$Scenario' is not a scenario name" }
New-Item -ItemType Directory -Force $Out | Out-Null
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$mpvCmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
if (-not $mpvCmd) { throw "mpv.exe is not on PATH (winget install mpv-player.mpv-CI.MSVC)" }
$mpvExe = $mpvCmd.Source
$item = Get-Item $mpvExe
if ($item.LinkType -and $item.Target) {
    $target = @($item.Target)[0]
    if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path $mpvExe) $target }
    $mpvExe = [IO.Path]::GetFullPath($target)
}
$mpvDir = Split-Path $mpvExe
$sysRoot = $env:SystemRoot

$src = Join-Path $Out 'host-src'
$exe = Join-Path $src 'bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ControlsHost.exe'
if (-not $NoBuild) {
    New-Item -ItemType Directory -Force $src | Out-Null
    Get-ChildItem $PSScriptRoot -File | Where-Object { $_.Extension -in '.cs', '.xaml', '.csproj' } | Copy-Item -Destination $src -Force
    Copy-Item (Join-Path $repo 'nuget.config') $src -Force
    $coreRoot = Join-Path $Out 'host-core'
    $core = Join-Path $coreRoot 'FluentFin.Core'
    New-Item -ItemType Directory -Force $core | Out-Null
    & robocopy (Join-Path $UiDir 'FluentFin.Core') $core /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying FluentFin.Core failed (robocopy $LASTEXITCODE)" }
    Copy-Item (Join-Path $repo 'nuget.config') $coreRoot -Force
    Write-Host "building the controls host against $UiDir"
    & dotnet build (Join-Path $src 'ControlsHost.csproj') -c Debug -p:Platform=x64 "-p:UiDir=$UiDir" "-p:CoreDir=$core" -nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "controls host build failed" }
}
if (-not (Test-Path $exe)) { throw "not built: $exe" }

$runOut = Join-Path $Out $Scenario
if (Test-Path $runOut) { Remove-Item -Recurse -Force $runOut }
New-Item -ItemType Directory -Force $runOut | Out-Null
$envA = Join-Path $Out 'env\a'; $envL = Join-Path $Out 'env\l'
New-Item -ItemType Directory -Force $envA, $envL | Out-Null
$hostArgs = @('--python', $Python, '--out', $runOut, '--scenario', $Scenario)
$log = Join-Path $runOut 'run.log'
if ($Visible) {
    # the user's desktop, but still inside a kill-on-close job: hidden_run's job without its private desktop is not
    # available, so the process is started here and waited for through its own handle
    $env:APPDATA = $envA; $env:LOCALAPPDATA = $envL; $env:Path = "$mpvDir;$sysRoot\System32;$sysRoot"
    $process = Start-Process -FilePath $exe -ArgumentList $hostArgs -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill(); throw "the host did not finish in $TimeoutSeconds s (killed)" }
    $code = $process.ExitCode
}
else {
    & $Python $hidden --log $log --timeout $TimeoutSeconds --desktop "ui-controls-$PID" `
        --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" `
        --env "PATH=$mpvDir;$sysRoot\System32;$sysRoot" --env PYTHONUTF8=1 `
        -- $exe @hostArgs | Out-Host
    $code = $LASTEXITCODE
}
Get-Content (Join-Path $runOut 'host.txt') | Select-String -Pattern 'CHECK|RESULT|FAILED|UNHANDLED' | ForEach-Object { $_.Line }
Write-Host "pictures: $(Join-Path $runOut 'shots')"
exit $code
