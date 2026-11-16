<#
.SYNOPSIS
  Phase 2b harness: FluentFin's own player controls over FluentFin's in-process mpv, the real backend attached to it,
  off-screen.

.DESCRIPTION
  Builds FluentFinControls.exe from a copy of this folder under -Out against -UiDir (default this repo's ui\; its
  FluentFin.Core is copied under -Out and built there; nothing is written into the repository), makes a 60 s 1080p60
  test clip once with mpv's encoder, then runs the host through tools\backend-bench\hidden_run.py: on a private desktop
  (nothing appears on the user's), inside a kill-on-close job (everything it starts dies with it), with APPDATA and
  LOCALAPPDATA under -Out. The in-process mpv has ao=null (no sound). Fake server (fakejf2 serving the clip as two
  episodes), fake token. Writes <Out>\run\host.txt (CHECK and RESULT lines) and shots\*.png (PrintWindow of its own
  window). Exit code 0 = every check passed.

.EXAMPLE
  .\run.ps1 -Out C:\scratch\fluentfin-controls
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Clip,
    [string]$UiDir,
    [string]$Python,
    [string]$LibmpvDir,
    [switch]$NoBuild,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $UiDir) { $UiDir = Join-Path $repo 'ui' }
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
if (-not $LibmpvDir) { $LibmpvDir = Join-Path $repo 'vendor\libmpv\msys2-20261116-mpv-0.42.0-2' }
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be outside the repository" }
$UiDir = [IO.Path]::GetFullPath($UiDir)
if (-not (Test-Path (Join-Path $LibmpvDir 'libmpv-2.dll'))) { throw "no libmpv-2.dll in $LibmpvDir (vendor\libmpv\VENDORED.md)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$mpvCmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
if (-not $mpvCmd) { throw "mpv.exe is not on PATH (winget install mpv-player.mpv-CI.MSVC): it makes the clip and the shim checks it" }
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
$exe = Join-Path $src 'bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentFinControls.exe'
if (-not $NoBuild) {
    New-Item -ItemType Directory -Force $src | Out-Null
    Get-ChildItem $PSScriptRoot -File | Where-Object { $_.Extension -in '.cs', '.xaml', '.csproj', '.py' } | Copy-Item -Destination $src -Force
    Copy-Item (Join-Path $repo 'nuget.config') $src -Force
    $coreRoot = Join-Path $Out 'host-core'
    $core = Join-Path $coreRoot 'FluentFin.Core'
    New-Item -ItemType Directory -Force $core | Out-Null
    & robocopy (Join-Path $UiDir 'FluentFin.Core') $core /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying FluentFin.Core failed (robocopy $LASTEXITCODE)" }
    Copy-Item (Join-Path $repo 'nuget.config') $coreRoot -Force
    Write-Host "building the player controls host against $UiDir"
    & dotnet build (Join-Path $src 'FluentFinControls.csproj') -c Debug -p:Platform=x64 "-p:RepoDir=$repo" "-p:UiDir=$UiDir" "-p:CoreDir=$core" "-p:LibmpvDir=$LibmpvDir" -nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "player controls host build failed" }
}
if (-not (Test-Path $exe)) { throw "not built: $exe" }

if (-not $Clip) {
    $Clip = Join-Path $Out 'clips\h264-1920x1080-60.mkv'
    if (-not (Test-Path $Clip)) {
        New-Item -ItemType Directory -Force (Split-Path $Clip) | Out-Null
        Write-Host "making $Clip (mpv's encoder, off-screen)"
        & $Python $hidden --log (Join-Path $Out 'clips\clip.log') --timeout 600 --desktop "ffcontrols-clip-$PID" -- `
            $mpvExe --no-config --ao=null 'av://lavfi:testsrc2=size=1920x1080:rate=60:duration=60' "--o=$Clip" --of=matroska --ovc=h264_mf --ovcopts=b=12000000 | Out-Host
        if (-not (Test-Path $Clip)) { throw "could not make the clip (see clips\clip.log)" }
    }
}
$Clip = [IO.Path]::GetFullPath($Clip)

$runOut = Join-Path $Out 'run'
if (Test-Path $runOut) { Remove-Item -Recurse -Force $runOut }
New-Item -ItemType Directory -Force $runOut | Out-Null
$envA = Join-Path $Out 'env\a'; $envL = Join-Path $Out 'env\l'
New-Item -ItemType Directory -Force $envA, $envL | Out-Null
$hostArgs = @('--out', $runOut, '--clip', $Clip, '--python', $Python, '--fakejf2', (Join-Path $repo 'tools\backend-harness\rr'), '--backend', (Join-Path $repo 'backend'))
Write-Host "running the player controls host off-screen"
& $Python $hidden --log (Join-Path $runOut 'run.log') --timeout $TimeoutSeconds --desktop "ffcontrols-$PID" `
    --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" `
    --env "PATH=$mpvDir;$sysRoot\System32;$sysRoot" --env PYTHONUTF8=1 `
    -- $exe @hostArgs | Out-Host
$code = $LASTEXITCODE
$hostTxt = Join-Path $runOut 'host.txt'
if (Test-Path $hostTxt) { Get-Content $hostTxt | Select-String -Pattern 'CHECK|RESULT|FAILED|UNHANDLED' | ForEach-Object { $_.Line } }
else { Write-Host "no host.txt (exit $code)" }
Write-Host "pictures: $(Join-Path $runOut 'shots')"
exit $code
