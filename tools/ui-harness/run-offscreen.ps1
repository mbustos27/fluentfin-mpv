<#
.SYNOPSIS
  Runs the real FluentFin MPV off-screen against the fake Jellyfin server: signs in, goes through the pages (or the
  ones you name), plays an episode, takes window-only screenshots and UI Automation dumps, measures, and scans each
  page for accessibility gaps. Nothing appears on the user's desktop, no key or click reaches it, no sound is made.

.DESCRIPTION
  - FluentFin runs with its own data folder (FluentFin.exe --data-dir <-DataDir>): settings, logs, the mpv backend's
    config. -DataDir may not be or be under %LOCALAPPDATA%\FluentFinMpv or %LOCALAPPDATA%\FluentFin, nor in the repo.
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
  'all' (default) = login..close. -Then <script.ps1> runs after the steps on the private desktop with the driver's
  helpers (see offscreen\drive.ps1). -Setting Name=Value (repeatable) is written into the run's LocalSettings.json
  first (a JSON value, else a string), e.g. -Setting MpvPlayerControls=FluentFin.

.EXAMPLE
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run1
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run2 -Script home,nav:Settings,close -NoA11y
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run3 -Latency 150 -LatencyRule '/Items/Latest=900'
  .\run-offscreen.ps1 -DataDir C:\scratch\ffdata -Out C:\scratch\run4 -Script season,play,leave -Setting MpvPlayerControls=FluentFin
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
    [string]$Exe,
    [switch]$Build,
    [string]$Python,
    [string]$Backend,
    [string]$Cache = (Join-Path $env:TEMP 'fluentfin-mpv-harness'),
    [switch]$NoA11y,
    [int]$TabStops = 60,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
$DataDir = [IO.Path]::GetFullPath($DataDir)
$Cache = [IO.Path]::GetFullPath($Cache)
function Test-Under([string]$Path, [string]$Root) {
    $p = $Path.TrimEnd('\') + '\'; $r = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $p.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)
}
$local = [Environment]::GetFolderPath('LocalApplicationData')
foreach ($forbidden in (Join-Path $local 'FluentFinMpv'), (Join-Path $local 'FluentFin')) {
    if ((Test-Under $DataDir $forbidden) -or (Test-Under $forbidden $DataDir)) { throw "-DataDir $DataDir would touch $forbidden (a real FluentFin data folder); use a scratch folder" }
}
foreach ($pair in @(@('-DataDir', $DataDir), @('-Out', $Out), @('-Cache', $Cache))) {
    if (Test-Under $pair[1] $repo) { throw "$($pair[0]) must be outside the repository ($repo)" }
}
# steps are comma-separated (a navigation item's name may have spaces: nav:Media Library)
$Script = @($Script | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Then) { $Then = [IO.Path]::GetFullPath($Then); if (-not (Test-Path $Then)) { throw "-Then: no script at $Then" } }
if (-not $Exe) { $Exe = Join-Path $repo 'ui\FluentFin\bin\x64\Debug\net10.0-windows10.0.19041.0\FluentFin.exe' }
$Exe = [IO.Path]::GetFullPath($Exe)
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
if (-not (Test-Path $Python)) { throw "no backend venv python at $Python; pass -Python <a venv with the backend's dependencies>\Scripts\python.exe (absolute)" }
$Python = [IO.Path]::GetFullPath($Python)
if (-not $Backend) { $Backend = Join-Path $repo 'backend' }
$Backend = [IO.Path]::GetFullPath($Backend)
if (-not (Test-Path (Join-Path $Backend 'jellyfin_mpv_shim\bridge.py'))) { throw "-Backend $Backend has no jellyfin_mpv_shim\bridge.py" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

if ($Build -or -not (Test-Path $Exe)) {
    Write-Host "building $(Join-Path $repo 'ui\FluentFin.sln')"
    & dotnet build (Join-Path $repo 'ui\FluentFin.sln') -c Debug -p:Platform=x64 -nologo -v:q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
if (-not (Test-Path $Exe)) { throw "not built: $Exe" }

# mpv: the official CI build from winget, on the Machine+User PATH (as tools\ui-harness\e2e\run.ps1 finds it)
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
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'
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

# the run's own data folder: the backend command, the player, -Setting values, and mpv silent
$settingsFile = Join-Path $DataDir 'LocalSettings.json'
$settings = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$pycache = Join-Path $Cache 'pycache'
$backendCommand = '"{0}" -I -X "pycache_prefix={1}" "{2}" "{3}"' -f $Python, $pycache, (Join-Path $PSScriptRoot 'offscreen\run_backend.py'), $Backend
$settings | Add-Member -NotePropertyName MpvBackendCommand -NotePropertyValue $backendCommand -Force
$settings | Add-Member -NotePropertyName MediaPlayerType -NotePropertyValue 'Mpv' -Force
foreach ($pair in $Setting) {
    $name, $value = $pair -split '=', 2
    if (-not $name -or $null -eq $value) { throw "-Setting '$pair': use Name=Value" }
    if ($name -in @('MpvBackendCommand', 'Servers')) { throw "-Setting $name is the harness's (backend command) or a sign-in's (servers)" }
    $parsed = try { $value | ConvertFrom-Json } catch { $value }
    if ($null -eq $parsed) { $parsed = $value }
    $settings | Add-Member -NotePropertyName $name -NotePropertyValue $parsed -Force
}
[IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
$controls = if ($settings.PSObject.Properties['MpvPlayerControls'] -and "$($settings.MpvPlayerControls)" -in @('FluentFin', '1')) { 'FluentFin' } else { 'Mpv' }
$mpvConfDir = Join-Path $DataDir 'mpv-backend'
New-Item -ItemType Directory -Force $mpvConfDir | Out-Null
$mpvConf = Join-Path $mpvConfDir 'mpv.conf'
$kept = if (Test-Path $mpvConf) { @(Get-Content $mpvConf | Where-Object { $_ -notmatch '^\s*(ao|mute|volume|audio-device|log-file)\s*=' -and $_ -notmatch '^# fluentfin-mpv harness' }) } else { @() }
# log-file: mpv's own log in the run's Logs folder, where the driver checks "AO: [null]"
[IO.File]::WriteAllLines($mpvConf, @('# fluentfin-mpv harness: test runs are silent (no audio device)', 'ao=null', 'mute=yes', 'volume=0',
    "log-file=$(Join-Path $DataDir 'Logs\mpv.log')") + $kept, [Text.UTF8Encoding]::new($false))

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
if ($NoA11y) { $driveArgs += '-NoA11y' }
Write-Host "running off-screen (desktop ffh-ui-$PID; log $Out\drive.log)"
& $Python -B -I $hidden --log (Join-Path $Out 'hidden_run.log') --timeout $TimeoutSeconds --desktop "ffh-ui-$PID" `
    --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" `
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
