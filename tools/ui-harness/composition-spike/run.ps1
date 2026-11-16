<#
.SYNOPSIS
  Phase 2a spike: libmpv in-process, composition mode into a XAML SwapChainPanel (or a wid child window to compare),
  off-screen.

.DESCRIPTION
  Builds CompositionSpike.exe from a copy of this folder under -Out (FluentFin.Core is copied under -Out too and built
  there; nothing is written into the repository), makes the test clips once with mpv's encoder (off-screen), then runs
  one scenario per mode through tools\backend-bench\hidden_run.py: on a private desktop (nothing appears on the user's),
  inside a kill-on-close job (everything it starts dies with it), with APPDATA and LOCALAPPDATA under -Out. Every core
  the host makes has ao=null (no sound); the bridge scenario's backend config has ao=null too. Fake server, fake token.

  Scenarios: local (play, resize, full screen, quick resizes; pacing stats), hdr (an HDR10 clip with the HDR hint
  forced), bridge (the real shim attached to the in-process core over IPC, mpv_ext_start=false), quit (a quit over IPC
  ends the core, the process survives, a second core plays). -Mode composition|wid|both.

  Each run writes <Out>\<scenario>-<mode>\host.txt (CHECK, STAT, RESULT lines) and shots\*.png (PrintWindow of its own
  window, never a screen copy). Exit code 0 = every check passed in every run.

.EXAMPLE
  .\run.ps1 -Out C:\scratch\spike -Scenario local -Mode both
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Scenario = 'local',
    [ValidateSet('composition', 'wid', 'both')][string]$Mode = 'composition',
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
if (-not $LibmpvDir) { $LibmpvDir = Join-Path $repo 'vendor\libmpv\20261111-git-bcde6b93cc' }
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be outside the repository" }
$UiDir = [IO.Path]::GetFullPath($UiDir)
if ($Scenario -notmatch '^[a-z_]+$') { throw "-Scenario: '$Scenario' is not a scenario name" }
if (-not (Test-Path (Join-Path $LibmpvDir 'libmpv-2.dll'))) { throw "no libmpv-2.dll in $LibmpvDir (vendor\libmpv\VENDORED.md)" }
New-Item -ItemType Directory -Force $Out | Out-Null
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'

$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$mpvCmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
if (-not $mpvCmd) { throw "mpv.exe is not on PATH (winget install mpv-player.mpv-CI.MSVC): it makes the clips and the bridge scenario's shim checks it" }
$mpvExe = $mpvCmd.Source
$item = Get-Item $mpvExe
if ($item.LinkType -and $item.Target) {
    $target = @($item.Target)[0]
    if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path $mpvExe) $target }
    $mpvExe = [IO.Path]::GetFullPath($target)
}
$mpvDir = Split-Path $mpvExe
$sysRoot = $env:SystemRoot

$src = Join-Path $Out 'spike-src'
$exe = Join-Path $src 'bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\CompositionSpike.exe'
if (-not $NoBuild) {
    New-Item -ItemType Directory -Force $src | Out-Null
    Get-ChildItem $PSScriptRoot -File | Where-Object { $_.Extension -in '.cs', '.xaml', '.csproj', '.py' } | Copy-Item -Destination $src -Force
    Copy-Item (Join-Path $repo 'nuget.config') $src -Force
    $coreRoot = Join-Path $Out 'spike-core'
    $core = Join-Path $coreRoot 'FluentFin.Core'
    New-Item -ItemType Directory -Force $core | Out-Null
    & robocopy (Join-Path $UiDir 'FluentFin.Core') $core /MIR /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying FluentFin.Core failed (robocopy $LASTEXITCODE)" }
    Copy-Item (Join-Path $repo 'nuget.config') $coreRoot -Force
    Write-Host "building the composition spike against $UiDir"
    & dotnet build (Join-Path $src 'CompositionSpike.csproj') -c Debug -p:Platform=x64 "-p:RepoDir=$repo" "-p:UiDir=$UiDir" "-p:CoreDir=$core" "-p:LibmpvDir=$LibmpvDir" -nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "composition spike build failed" }
}
if (-not (Test-Path $exe)) { throw "not built: $exe" }

# the clips, made once with mpv's encoder (no window: an encode has no video output), off-screen anyway
function New-Clip([string]$name, [string]$lavfi, [string[]]$encode) {
    $path = Join-Path $Out "clips\$name"
    if (Test-Path $path) { return $path }
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    Write-Host "making $name (mpv's encoder, off-screen)"
    & $Python $hidden --log (Join-Path $Out "clips\$name.log") --timeout 600 --desktop "spike-clip-$PID" -- `
        $mpvExe --no-config --ao=null "av://lavfi:$lavfi" "--o=$path" --of=matroska @encode | Out-Host
    if (-not (Test-Path $path) -or (Get-Item $path).Length -lt 10000) { Remove-Item -Force $path -ErrorAction SilentlyContinue; throw "could not make $name (see clips\$name.log)" }
    return $path
}

if (-not $Clip) {
    $Clip = switch ($Scenario) {
        'hdr' {
            # 10-bit PQ/BT.2020 (HDR10 signalling). hevc_mf here takes 8-bit only and hevc_vulkan needs a device mpv's
            # encoder does not give it; libaom's AV1 (realtime, CPU) encodes 1080p30 at about real time. d3d11va decodes it.
            New-Clip 'av1-1920x1080-hdr10-30.mkv' 'testsrc2=size=1920x1080:rate=30:duration=25' `
                @('--vf=format=fmt=yuv420p10:colormatrix=bt.2020-ncl:colorlevels=limited:primaries=bt.2020:gamma=pq', '--ovc=libaom-av1', '--ovcopts=cpu-used=8,usage=realtime,b=6000000')
        }
        default {
            New-Clip 'h264-1920x1080-60.mkv' 'testsrc2=size=1920x1080:rate=60:duration=60' `
                @('--ovc=h264_mf', '--ovcopts=b=12000000')
        }
    }
}
$Clip = [IO.Path]::GetFullPath($Clip)

$modes = if ($Mode -eq 'both') { @('composition', 'wid') } else { @($Mode) }
$envA = Join-Path $Out 'env\a'; $envL = Join-Path $Out 'env\l'
New-Item -ItemType Directory -Force $envA, $envL | Out-Null
$worst = 0
foreach ($m in $modes) {
    $runOut = Join-Path $Out "$Scenario-$m"
    if (Test-Path $runOut) { Remove-Item -Recurse -Force $runOut }
    New-Item -ItemType Directory -Force $runOut | Out-Null
    $hostArgs = @('--out', $runOut, '--mode', $m, '--scenario', $Scenario, '--clip', $Clip,
        '--python', $Python, '--fakejf2', (Join-Path $repo 'tools\backend-harness\rr'), '--backend', (Join-Path $repo 'backend'))
    $log = Join-Path $runOut 'run.log'
    Write-Host "running $Scenario ($m) off-screen"
    # mpv on PATH only for the shim's own checks; the host's core is libmpv-2.dll next to the exe
    & $Python $hidden --log $log --timeout $TimeoutSeconds --desktop "spike-$Scenario-$m-$PID" `
        --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" `
        --env "PATH=$mpvDir;$sysRoot\System32;$sysRoot" --env PYTHONUTF8=1 `
        -- $exe @hostArgs | Out-Host
    $code = $LASTEXITCODE
    if ($code -ne 0) { $worst = $code }
    $hostTxt = Join-Path $runOut 'host.txt'
    if (Test-Path $hostTxt) {
        Get-Content $hostTxt | Select-String -Pattern 'CHECK|STAT|RESULT|FAILED|UNHANDLED|display:' | ForEach-Object { $_.Line }
    }
    else { Write-Host "no host.txt (exit $code; see $log)" }
}
exit $worst
