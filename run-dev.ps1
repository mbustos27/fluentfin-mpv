<#
.SYNOPSIS
  Builds FluentFin (ui\) and starts it for development, without Visual Studio and without MSIX packaging.

.DESCRIPTION
  FluentFin is released unpackaged (dotnet publish + Inno Setup, see ui\publish.ps1 and ui\Installer), so the plain
  build output runs as is: FluentFin.exe in ui\FluentFin\bin\x64\<Configuration>\net10.0-windows10.0.19041.0\.
  The Windows App SDK is self-contained in that folder, nothing gets registered and no upstream file changes.
  Data goes to %LOCALAPPDATA%\FluentFinMpv (settings, logs, logs\mpv-backend.log, mpv-backend\ config).

  -SelfTest starts FluentFin.exe --mpv-selftest: no login, the player page opens at once, the mpv backend starts with
  --dev, connects with only the video window and plays av://lavfi:testsrc. It checks the video without a Jellyfin
  server: with FluentFin's player controls (the default) in FluentFin's own mpv, with mpv's (Settings > Playback) in
  mpv's embedded window. It needs the backend's dev checkout (backend\.venv\Scripts\python.exe and
  backend\jellyfin_mpv_shim\bridge.py), or a backend command set in FluentFin's Settings.

  A running copy of this build is closed first and given -CloseWaitSeconds (16) to exit before it is killed: closing
  FluentFin can take ~12 s (the backend's final report, up to 10 s before the backend is killed, docs\BRIDGE.md
  section 3, then up to 2 s for a crash report), and killing it sooner cuts that report off.

.EXAMPLE
  .\run-dev.ps1
  .\run-dev.ps1 -SelfTest
  .\run-dev.ps1 -SelfTest -NoBuild
#>
param(
    [switch]$SelfTest,
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateRange(1, 300)][int]$CloseWaitSeconds = 16
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'ui\FluentFin\FluentFin.csproj'
$exe = Join-Path $root "ui\FluentFin\bin\x64\$Configuration\net10.0-windows10.0.19041.0\FluentFin.exe"

# a fresh .NET SDK install is not on the PATH of shells that were open before it
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

# a running copy of this build locks its files. Each candidate's handle is taken first and kept (the Process object
# caches it), so its pid cannot go to another process while it is checked, closed, waited for and killed: the path is
# read for that pid, and WaitForExit and Kill use that same handle
$running = @(Get-Process FluentFin -ErrorAction SilentlyContinue | Where-Object {
        try { $null = $_.Handle; $_.Path -eq $exe } catch { $false }
    })
foreach ($process in $running) {
    Write-Host "Closing the running dev build (pid $($process.Id))"
    $null = $process.CloseMainWindow()
    if (-not $process.WaitForExit($CloseWaitSeconds * 1000)) {
        Write-Warning "The running dev build did not exit within $CloseWaitSeconds s; killing it"
        try { $process.Kill() } catch [InvalidOperationException] {}   # it exited meanwhile
    }
}

if (-not $NoBuild) {
    # restore uses ..\nuget.config (nuget.org and the CommunityToolkit Labs feed)
    & dotnet build $project -c $Configuration -p:Platform=x64 -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
}

if (-not (Test-Path $exe)) { throw "Not built: $exe" }

$arguments = @()
if ($SelfTest) {
    $python = Join-Path $root 'backend\.venv\Scripts\python.exe'
    $bridge = Join-Path $root 'backend\jellyfin_mpv_shim\bridge.py'
    foreach ($needed in $python, $bridge) {
        if (-not (Test-Path $needed)) { Write-Warning "The self-test needs $needed (or a backend command in Settings); the player page will show the error." }
    }
    $arguments += '--mpv-selftest'
}

$started = if ($arguments.Count -gt 0) { Start-Process -FilePath $exe -ArgumentList $arguments -PassThru } else { Start-Process -FilePath $exe -PassThru }
Write-Host "Started FluentFin (pid $($started.Id))$(if ($SelfTest) { ' in mpv self-test mode' })"
Write-Host "Logs: $env:LOCALAPPDATA\FluentFinMpv\Logs (FluentFin log*.txt with the backend's stderr, mpv-backend.log from the backend)"
