<#
.SYNOPSIS
  Runs the real Nixie off-screen against the fake Jellyfin server: signs in, goes through the pages (or the
  ones you name), plays an episode, takes window-only screenshots and UI Automation dumps, measures, and scans each
  page for accessibility gaps. Nothing appears on the user's desktop, no key or click reaches it, no sound is made.

.DESCRIPTION
  - FluentFin runs with its own data folder (Nixie.exe --data-dir <-DataDir>): settings, logs, the mpv backend's
    config. -DataDir may not be a drive root, be or hold %LOCALAPPDATA%\Nixie, %LOCALAPPDATA%\Seaglass, %LOCALAPPDATA%\FluentFinMpv or %LOCALAPPDATA%\FluentFin, nor be
    in the repo. A reused -DataDir keeps only saved servers on this machine (loopback); others are removed.
  - Nixie.exe: -Exe, else the build in -BuildDir (default -Cache\build). Nothing is built unless -Build is given;
    -Build builds ui\FluentFin into -BuildDir (its own bin and obj, not the checkout's) off-screen in a kill-on-close job,
    without MSBuild node reuse or the shared compiler, so nothing of the build keeps running.
  - Everything runs on a private desktop (CreateDesktopW) inside a kill-on-close job, through
    tools\backend-bench\hidden_run.py: the fake server (tools\ui-harness\fakeserver, 127.0.0.1 only), FluentFin, its
    backend and mpv. When the run ends (or -TimeoutSeconds runs out) the job ends whatever is left; nothing else is
    ever ended. APPDATA and LOCALAPPDATA point into -Out\env for the run, and whatever lands there is listed at the end.
  - Sign-in: -SignIn quickconnect (default): FluentFin's Quick Connect, the code it shows approved through the fake
    server's harness endpoint; the server issues a fresh random token. No password is typed. -SignIn password types the
    fake server's generated test credentials (fakeserver\credentials.json) instead. Never a real server or account.
  - Silent: the test video's audio tracks are digital silence and the run's mpv-backend\mpv.conf says ao=null (no audio
    device is opened; FluentFin's in-process mpv reads the same mpv.conf).
  - The driver (offscreen\drive.ps1) uses UI Automation patterns, and where there is none, key/mouse messages posted to
    FluentFin's own windows or to the video window of the mpv it started. SendInput is never used.
  - The backend: -Backend's checkout (default this repo's backend\) run by -Python (a venv with the backend's
    dependencies; default backend\.venv\Scripts\python.exe of this repo) through offscreen\run_backend.py.

  Output in -Out: shots\NN-<page>.png, uia\<page>.txt (the page's UI Automation tree), a11y\<page>.json (unnamed
  interactive elements, interactive elements not keyboard-focusable, elements without a control type, images no
  keyboard reaches) and a11y\<page>-tab.json (the Tab order on home and the item pages), result.json (timings in ms,
  checks, file paths), server\requests.jsonl and server\summary.json (every request, unhandled endpoints), drive.log,
  and the run's FluentFin logs in -DataDir\Logs.

  -Script (steps, comma-separated, in order; sign-in always comes first): login, home, library, movie, series, season,
  play, fullscreen, windowed, leave, close, nav:<navigation item> (e.g. nav:Settings), shot:<label>, wait:<seconds>;
  'all' (default) = login..close. Not in 'all': relaunch (close, start again: the automatic sign-in with the saved
  server) and steady (after play: playback running, nothing may move over the middle of the video; with relaunch first
  it catches the 1.0 test build's stuck startup ring: -Script relaunch,play,steady,close). -Then <script.ps1> runs after the steps on the private desktop with the driver's
  helpers (see offscreen\drive.ps1); -Before <script.ps1> the same way on the server page, before the sign-in (the
  server page's and sign-in page's states: offscreen\before-states.ps1). -Setting Name=Value (repeatable) is written into the run's LocalSettings.json
  first (a JSON value, else a string), e.g. -Setting MpvPlayerControls=Mpv for mpv's own controls (FluentFin's are the
  default, with libmpv\libmpv-2.dll next to the exe); MpvBackendCommand, Servers and MediaPlayerType are the harness's and
  refused (the run plays with the mpv player only).

.EXAMPLE
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run1 -Build
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run2 -Script home,nav:Settings,close -NoA11y
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run3 -Latency 150 -LatencyRule '/Items/Latest=900'
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run4 -Script season,play,leave -Setting MpvPlayerControls=Mpv
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Out,
    [double]$Latency = 0,
    [string[]]$LatencyRule = @(),
    [Alias('Steps')][string[]]$Script = @('all'),
    [ValidateSet('quickconnect', 'password')][string]$SignIn = 'quickconnect',
    [string[]]$Setting = @(),
    [string]$Then,
    [string]$Before,
    [string]$Exe,
    [switch]$Build,
    [string]$BuildDir,
    [string]$Python,
    [string]$Backend,
    [string]$Cache = (Join-Path $env:TEMP 'fluentfin-mpv-harness'),
    [switch]$NoA11y,
    # the private desktop has no foreground lock: other processes' windows take activation, and the app would draw and measure
    # its background state throughout. By default the glass keeps its active state (FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE=1, honoured
    # by dev builds only); -GlassFollowsActivity leaves the window's real activation in charge.
    [switch]$GlassFollowsActivity,
    [int]$TabStops = 60,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offscreen\prepare.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
$DataDir = Resolve-HarnessDataDir $DataDir $repo
$Cache = [IO.Path]::GetFullPath($Cache)
if (-not $BuildDir) { $BuildDir = Join-Path $Cache 'build' }
$BuildDir = [IO.Path]::GetFullPath($BuildDir)
foreach ($pair in @(@('-Out', $Out), @('-Cache', $Cache), @('-BuildDir', $BuildDir))) {
    if (Test-Under $pair[1] $repo) { throw "$($pair[0]) must be outside the repository ($repo)" }
}
# steps are comma-separated (a navigation item's name may have spaces: nav:Media Library)
$Script = @($Script | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Then) { $Then = [IO.Path]::GetFullPath($Then); if (-not (Test-Path $Then)) { throw "-Then: no script at $Then" } }
if ($Before) { $Before = [IO.Path]::GetFullPath($Before); if (-not (Test-Path $Before)) { throw "-Before: no script at $Before" } }
$Exe = Resolve-HarnessExe $Exe $BuildDir ([bool]$Build)
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
if (-not (Test-Path $Python)) { throw "no backend venv python at $Python; pass -Python <a venv with the backend's dependencies>\Scripts\python.exe (absolute)" }
$Python = [IO.Path]::GetFullPath($Python)
if (-not $Backend) { $Backend = Join-Path $repo 'backend' }
$Backend = [IO.Path]::GetFullPath($Backend)
if (-not (Test-Path (Join-Path $Backend 'jellyfin_mpv_shim\bridge.py'))) { throw "-Backend $Backend has no jellyfin_mpv_shim\bridge.py" }
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'

if (-not $Exe) {
    # -Build: into -BuildDir (bin and obj there, not in the checkout), off-screen in a kill-on-close job, no MSBuild
    # node reuse and no shared compiler: nothing of the build is left running
    $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { throw "dotnet.exe is not on PATH" }
    New-Item -ItemType Directory -Force $Out, $BuildDir | Out-Null
    $buildLog = Join-Path $Out 'build.log'
    Write-Host "building FluentFin into $BuildDir (log $buildLog)"
    & $Python -B -I $hidden --log $buildLog --timeout 1800 --desktop "ffh-build-$PID" -- $dotnet @(Get-HarnessBuildArgs $repo $BuildDir) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "the build failed (see $buildLog)" }
    $Exe = Find-HarnessBuiltExe $BuildDir
    if (-not $Exe) { throw "the build made no Nixie.exe under $BuildDir\bin\FluentFin (see $buildLog)" }
}

# mpv: the official CI build from winget, on the Machine+User PATH (as tools\ui-harness\e2e\run.ps1 finds it)
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
New-Item -ItemType Directory -Force $Out, $DataDir, $Cache | Out-Null

# the test video (cached: building it takes about a minute), made off-screen
$media = Join-Path $Cache 'media'
$mediaJson = Join-Path $media 'media.json'
$needMedia = -not (Test-Path $mediaJson) -or -not ((Get-Content $mediaJson -Raw | ConvertFrom-Json).silent)
if ($needMedia) {
    Write-Host "building the test video into $media (mpv's encoder, off-screen, silent)"
    & $Python -B -I $hidden --log (Join-Path $Out 'media-build.log') --timeout 900 --desktop "ffh-media-$PID" -- `
        $Python -B -I (Join-Path $PSScriptRoot 'fakeserver\media.py') --mpv $mpvExe --out $media | Out-Host
    if (-not (Test-Path $mediaJson)) { throw "the test video was not built (see $Out\media-build.log)" }
}

# the run's own data folder: the backend command, the mpv player, -Setting values, only loopback saved servers, and mpv
# silent with no log file (offscreen\prepare.ps1)
$pycache = Join-Path $Cache 'pycache'
$backendCommand = '"{0}" -I -X "pycache_prefix={1}" "{2}" "{3}"' -f $Python, $pycache, (Join-Path $PSScriptRoot 'offscreen\run_backend.py'), $Backend
$prepared = Initialize-HarnessSettings $DataDir $backendCommand $Setting -LibmpvThere (Test-Path (Join-Path (Split-Path $Exe) 'libmpv\libmpv-2.dll'))
if ($prepared.RemovedServers) { Write-Host "removed $($prepared.RemovedServers) saved server(s) that are not on this machine from $DataDir\LocalSettings.json" }
$controls = $prepared.PlayerControls
[void](Initialize-HarnessMpvConf $DataDir)

$envA = Join-Path $Out 'env\appdata'; $envL = Join-Path $Out 'env\localappdata'
New-Item -ItemType Directory -Force $envA, $envL | Out-Null
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$driveArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'offscreen\drive.ps1'),
    '-Exe', $Exe, '-DataDir', $DataDir, '-Out', $Out, '-Python', $Python, '-Media', $media, '-Latency', $Latency,
    '-SignIn', $SignIn, '-PlayerControls', $controls, '-TabStops', $TabStops)
# the steps go in a file: a name with spaces or commas arrives whole
$stepsFile = Join-Path $Out 'steps.txt'
[IO.File]::WriteAllText($stepsFile, ($Script -join ','), [Text.UTF8Encoding]::new($false))
$driveArgs += @('-StepsFile', $stepsFile)
if ($LatencyRule.Count) {
    $rulesFile = Join-Path $Out 'latency-rules.json'
    [IO.File]::WriteAllText($rulesFile, (ConvertTo-Json -InputObject @($LatencyRule)), [Text.UTF8Encoding]::new($false))
    $driveArgs += @('-LatencyRulesFile', $rulesFile)
}
if ($Then) { $driveArgs += @('-Then', $Then) }
if ($Before) { $driveArgs += @('-Before', $Before) }
if ($NoA11y) { $driveArgs += '-NoA11y' }
Write-Host "running off-screen (desktop ffh-ui-$PID; log $Out\drive.log)"
$glassEnv = if ($GlassFollowsActivity) { @() } else { @('--env', 'FLUENTFIN_MPV_GLASS_ALWAYS_ACTIVE=1') }
& $Python -B -I $hidden --log (Join-Path $Out 'hidden_run.log') --timeout $TimeoutSeconds --desktop "ffh-ui-$PID" `
    --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" @glassEnv `
    --env "PATH=$mpvDir;$env:SystemRoot\System32;$env:SystemRoot;$env:SystemRoot\System32\WindowsPowerShell\v1.0" `
    -- $ps @driveArgs | Out-Host
$code = $LASTEXITCODE

$resultFile = Join-Path $Out 'result.json'
if (Test-Path $resultFile) {
    $result = Get-Content $resultFile -Raw | ConvertFrom-Json
    Write-Host "`n--- timings (ms)"
    $result.timings.PSObject.Properties | ForEach-Object { Write-Host ("  {0,-28} {1}" -f $_.Name, $_.Value) }
    Write-Host "--- checks"
    $result.checks.PSObject.Properties | ForEach-Object { Write-Host ("  {0} {1}{2}" -f $(if ($_.Value.ok) { 'PASS' } else { 'FAIL' }), $_.Name, $(if ($_.Value.detail) { ": $($_.Value.detail)" })) }
    Write-Host "--- screenshots and UI Automation dumps"
    $result.shots.PSObject.Properties | ForEach-Object { Write-Host "  $($_.Value)" }
    $result.uia.PSObject.Properties | ForEach-Object { Write-Host "  $($_.Value)" }
}
else { Write-Warning "no result.json (see $Out\hidden_run.log and $Out\drive.log)" }
$a11y = @(Get-ChildItem (Join-Path $Out 'a11y') -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*-tab.json' })
if ($a11y) {
    Write-Host "--- accessibility (per page: unnamed interactive / not keyboard-focusable / no control type / images no keyboard reaches)"
    foreach ($f in $a11y) {
        $s = Get-Content $f.FullName -Raw | ConvertFrom-Json
        Write-Host ("  {0,-14} {1,3} / {2,3} / {3,3} / {4,3}   {5}" -f $s.page, @($s.unnamedInteractive).Count, @($s.interactiveNotKeyboardFocusable).Count,
            @($s.withoutControlType).Count, @($s.imagesNotReachableByKeyboard).Count, $f.FullName)
    }
}
$stray = @(Get-ChildItem $envA, $envL -Recurse -File -Force -ErrorAction SilentlyContinue)
Write-Host "--- written to the run's APPDATA/LOCALAPPDATA (should be nothing of FluentFin's): $(if ($stray) { ($stray | ForEach-Object { $_.FullName.Substring($Out.Length) }) -join ', ' } else { 'nothing' })"
$serverSummary = Join-Path $Out 'server\summary.json'
if (Test-Path $serverSummary) {
    $u = (Get-Content $serverSummary -Raw | ConvertFrom-Json).unhandled
    Write-Host "--- endpoints the fake server does not handle: $(if ($u.PSObject.Properties.Count) { ($u.PSObject.Properties | ForEach-Object { "$($_.Name) x$($_.Value)" }) -join '; ' } else { 'none' })"
}
exit $code
