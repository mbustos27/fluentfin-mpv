<#
.SYNOPSIS
  Verifies a release zip as a user would get it: unzipped outside the repository, run off-screen against the fake
  Jellyfin server with nothing from a dev checkout, then the self-test. Silent: test media is digital silence and mpv's
  audio output is null.

.DESCRIPTION
  - Checks the zip against its .sha256 and unzips it into -Out\pkg (a fresh folder; -Out must be outside the repo).
  - Checks that a skip rule snaps to the cut with only what the package ships (1.6.1): the bundled backend's
    --nixie-skip-check, off-screen, with no mpv.exe on PATH.
  - Runs, each with its own scratch data folder (--data-dir), APPDATA/LOCALAPPDATA pointed into -Out, and a PATH of
    Windows' own folders only (no Python, no mpv), through tools\ui-harness\offscreen\drive.ps1 on a private desktop
    inside a kill-on-close job (tools\backend-bench\hidden_run.py), with no backend command set (FluentFin finds its
    bundled backend by itself). The runs start app\Nixie.exe, except the first, which checks the launcher in front:
      Launcher   the self-test (below) started through the top Nixie.exe, the one people run, with a data folder
                 whose name has a space: the launcher must end by itself and start app\Nixie.exe with its
                 arguments unchanged, and the self-test then passes on that app
      SavedVlc   a data folder whose saved player is VLC (as a dev build may leave): the release has no VLC, so it
                 must play with MPV, without a crash, and keep the saved choice, Settings opened included
      Default    a clean data folder with no player-controls setting (what a new user gets: FluentFin's controls on the
                 bundled libmpv): sign in, relaunch (the automatic sign-in), play an episode with nothing over the middle of
                 the video once it plays steadily, stop, close; the logs must show the in-process mpv
      Mpv        "Player controls: mpv", with mpv's folder added to PATH (the documented requirement for mpv's
                 controls): the same steps. Skipped when no mpv.exe is on the Machine/User PATH.
      NoMpv      "Player controls: mpv" without mpv on PATH: sign in and play; playback must stop with "mpv could not be
                 started" and report nothing to the server (the release notes' "needs mpv on PATH"), then close.
      SelfTest   Nixie.exe --mpv-selftest with FluentFin's controls (no server), tools\release\selftest-offscreen.ps1.
      Kill       the self-test, then FluentFin ended abruptly (a crash): its kill-on-close job must end the backend.
      InstallSwitch  1.6.1 (BH-151-m6): Nixie.exe --install-update <a decoy installer> refused for want of the upgrade
                 test's marker (NIXIE_UPGRADE_TEST unset), and let through the gate with it, tools\release\
                 installswitch-offscreen.ps1. Nothing is installed: the decoy is no installer, and the unzipped copy is
                 portable.
  - Alongside each run, tools\release\proc_audit.py records every process FluentFin starts (image, command line, every
    DLL loaded). A run fails if any of them comes from the repository, a dev checkout, a Python install, the build's
    work folder or (except mpv.exe and its DLLs in the Mpv run) a WinGet mpv; or if a command line names a token.
  - Fails if the run wrote anything into the unzipped folder.
  The harness itself (fake server, hidden_run.py, proc_audit.py) runs with -HarnessPython; FluentFin never sees it.

.EXAMPLE
  .\tools\release\verify-package.ps1 -Zip dist\Nixie-1.0.0-win-x64.zip -Out C:\scratch\verify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Zip,
    [Parameter(Mandatory)][string]$Out,
    [string]$HarnessPython,
    [string]$Cache = (Join-Path $env:TEMP 'fluentfin-mpv-harness'),
    [ValidateSet('Launcher', 'Default', 'SavedVlc', 'Mpv', 'NoMpv', 'SelfTest', 'Kill', 'InstallSwitch')][string[]]$Runs = @('Launcher', 'Default', 'SavedVlc', 'Mpv', 'NoMpv', 'SelfTest', 'Kill', 'InstallSwitch'),
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$harness = Join-Path $repo 'tools\ui-harness'
. (Join-Path $harness 'offscreen\prepare.ps1')
$Zip = [IO.Path]::GetFullPath($Zip)
$Out = [IO.Path]::GetFullPath($Out).TrimEnd('\')
if (Test-Under $Out $repo) { throw "-Out must be outside the repository ($repo)" }
$devRoot = Split-Path $repo   # the folder holding this checkout (and its sibling worktrees)
if (-not $HarnessPython) { $HarnessPython = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
$HarnessPython = [IO.Path]::GetFullPath($HarnessPython)
if (-not (Test-Path $HarnessPython)) { throw "no harness Python at $HarnessPython (-HarnessPython: a venv with Pillow, for the fake server)" }
$hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$windowsPath = "$env:SystemRoot\System32;$env:SystemRoot;$env:SystemRoot\System32\Wbem;$env:SystemRoot\System32\WindowsPowerShell\v1.0"

$results = [ordered]@{}
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results[$Name] = [ordered]@{ ok = $Ok; detail = $Detail }
    Write-Host ("{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" }))
}

# --- the zip, unzipped fresh ------------------------------------------------------------------------------------------
$sumFile = "$Zip.sha256"
if (-not (Test-Path $Zip) -or -not (Test-Path $sumFile)) { throw "need $Zip and $sumFile" }
$expected = ((Get-Content $sumFile -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -Algorithm SHA256 $Zip).Hash.ToLowerInvariant()
Check 'zip matches its .sha256' ($expected -eq $actual) $actual
New-Item -ItemType Directory -Force $Out | Out-Null
$pkgRoot = Join-Path $Out 'pkg'
if (Test-Path $pkgRoot) { Remove-Item -Recurse -Force $pkgRoot }
New-Item -ItemType Directory $pkgRoot | Out-Null
& tar.exe -x -f $Zip -C $pkgRoot
if ($LASTEXITCODE -ne 0) { throw "unzipping $Zip failed" }
$pkg = @(Get-ChildItem $pkgRoot -Directory)[0].FullName
# the top holds the launcher (the Nixie.exe people run), the three texts and app\; the app itself is in app\
$launcherExe = Join-Path $pkg 'Nixie.exe'
$app = Join-Path $pkg 'app'
$exe = Join-Path $app 'Nixie.exe'
$top = @(Get-ChildItem $pkg -Force | ForEach-Object { $_.Name } | Sort-Object)
Check 'the folder''s top holds only Nixie.exe (the launcher), README.txt, LICENSE, THIRD-PARTY-NOTICES.txt and app\' (($top -join ',') -eq 'app,LICENSE,Nixie.exe,README.txt,THIRD-PARTY-NOTICES.txt') ($top -join ', ')
Check 'unzipped: one folder with the launcher, and app\ with Nixie.exe, the bundled backend and libmpv' ((Test-Path $launcherExe) -and (Test-Path $exe) -and (Test-Path (Join-Path $app 'backend\fluentfin-mpv-backend.exe')) -and (Test-Path (Join-Path $app 'libmpv\libmpv-2.dll'))) $pkg
$languageDirs = @(Get-ChildItem $app -Directory | Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$' -and $_.Name -notmatch '^en(-|$)' })
# Properties > Details on every program the package ships: company, product, description and copyright filled in, the product
# name Nixie (the installer's Apps entry says the same), and no empty field that would look unfinished
foreach ($prog in @(@('Nixie.exe (launcher)', $launcherExe), @('app\Nixie.exe', $exe), @('backend\fluentfin-mpv-backend.exe', (Join-Path $app 'backend\fluentfin-mpv-backend.exe')))) {
    $vi = (Get-Item $prog[1]).VersionInfo
    $empty = @(@{ n = 'CompanyName'; v = $vi.CompanyName }, @{ n = 'ProductName'; v = $vi.ProductName }, @{ n = 'FileDescription'; v = $vi.FileDescription }, @{ n = 'LegalCopyright'; v = $vi.LegalCopyright }, @{ n = 'ProductVersion'; v = $vi.ProductVersion } | Where-Object { -not $_.v } | ForEach-Object { $_.n })
    Check "version details: $($prog[0]) has company, product, description, copyright and version" ($empty.Count -eq 0 -and $vi.CompanyName -eq 'Nixie' -and $vi.ProductName -eq 'Nixie') "company '$($vi.CompanyName)', product '$($vi.ProductName)', description '$($vi.FileDescription)', copyright '$($vi.LegalCopyright)'; empty: $($empty -join ', ')"
}
Check 'English only: app\ has no language folders but English' ($languageDirs.Count -eq 0) $(if ($languageDirs) { $languageDirs.Name -join ', ' } else { (@(Get-ChildItem $app -Directory | Where-Object Name -match '^en(-|$)') | ForEach-Object Name) -join ', ' })
Check 'the release has no VLC or Flyleaf libraries (libvlc\, FFMpeg\)' (-not (Test-Path (Join-Path $app 'libvlc')) -and -not (Test-Path (Join-Path $app 'FFMpeg'))) $app
# slim package (docs/research/slim-package.md): the .NET desktop
# runtime's Windows Forms and Visual Basic are not shipped (Flyleaf's and LibVLC's libraries must stay: the XAML type table loads them), and app\ stays under a file-count ceiling (raise it only with a reason)
$slimGone = @('System.Windows.Forms.dll', 'Microsoft.VisualBasic.Forms.dll') | Where-Object { Test-Path (Join-Path $app $_) }
$appTopFiles = @(Get-ChildItem $app -File).Count
Check 'slim: no Windows Forms/Visual Basic assemblies, and app\ has at most 510 files at its top' ($slimGone.Count -eq 0 -and $appTopFiles -le 510) "$appTopFiles files at the top of app\$(if ($slimGone) { '; present: ' + ($slimGone -join ', ') })"
# libmpv\ as its BUILD-MANIFEST.txt lists it, and no libdvdcss (DVD decryption): at most the stub of it, which the manifest
# marks (its owner says "stub" and is no MSYS2 package; tools\release\libmpv_check.py is_stub)
$libmpvPkg = Join-Path $app 'libmpv'
$manifestRows = @{}
foreach ($line in @(Get-Content (Join-Path $libmpvPkg 'BUILD-MANIFEST.txt') -ErrorAction SilentlyContinue)) {
    if ($line -match '^(\S+\.dll)\s+(\d+)\s+([0-9a-fA-F]{64})\s+(.+?)\s*$') {
        $manifestRows[$Matches[1].ToLowerInvariant()] = @{ sha = $Matches[3].ToLowerInvariant(); owner = $Matches[4] }
    }
}
$dllsHere = @(Get-ChildItem $libmpvPkg -Filter '*.dll' -File -ErrorAction SilentlyContinue)
$notListed = @($dllsHere | Where-Object { -not $manifestRows.ContainsKey($_.Name.ToLowerInvariant()) -or
        (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant() -ne $manifestRows[$_.Name.ToLowerInvariant()].sha })
Check 'libmpv\: every DLL is the one its BUILD-MANIFEST.txt lists' ($dllsHere.Count -gt 0 -and $notListed.Count -eq 0 -and $manifestRows.Count -eq $dllsHere.Count) `
    ("{0} DLLs, {1} listed{2}" -f $dllsHere.Count, $manifestRows.Count, $(if ($notListed) { "; not as listed: $($notListed.Name -join ', ')" }))
$css = $manifestRows['libdvdcss-2.dll']
Check 'libmpv\: no libdvdcss (DVD decryption), at most its stub' ((-not $css) -or ($css.owner -match '\bstub\b' -and $css.owner -notmatch '^\s*mingw-w64-')) `
    $(if ($css) { "libdvdcss-2.dll: $($css.owner)" } else { 'no libdvdcss-2.dll' })
function Get-Listing { Get-ChildItem $pkg -Recurse -File -Force | ForEach-Object { "{0}|{1}|{2}" -f $_.FullName.Substring($pkg.Length), $_.Length, $_.LastWriteTimeUtc.Ticks } }
$before = @(Get-Listing)

# --- Nixie 1.6.1 (BH-160-M1): a skip rule snaps to the cut with only what the package ships ----------------------------
# 1.6.0 analysed with a separate mpv.exe, which the package doesn't have. The bundled backend's --nixie-skip-check
# (skip_snap.self_check): the package's libmpv encodes a check clip (a test pattern and a tone, black and silent at 9.0 to
# 9.6 s), the backend analyses it in a process of its own (the same exe with --nixie-skip-analyse, as a skip does) and
# snaps a rule's edge to the clip's cut. Off-screen and silent (libmpv with vo=null and ao=null, on a private desktop in a
# kill-on-close job), with a PATH of Windows' own folders only (no mpv.exe, no Python), and TEMP and the data folders in -Out.
$skipDir = Join-Path $Out 'skip-check'
if (Test-Path $skipDir) { Remove-Item -Recurse -Force $skipDir }
New-Item -ItemType Directory -Force (Join-Path $skipDir 'temp'), (Join-Path $skipDir 'local'), (Join-Path $skipDir 'roaming') | Out-Null
$skipLog = Join-Path $skipDir 'run.log'
& $HarnessPython -I $hidden --log $skipLog --timeout 180 --desktop "ffr-skipcheck-$PID" --env "PATH=$windowsPath" `
    --env "TEMP=$skipDir\temp" --env "TMP=$skipDir\temp" --env "LOCALAPPDATA=$skipDir\local" --env "APPDATA=$skipDir\roaming" `
    -- (Join-Path $app 'backend\fluentfin-mpv-backend.exe') --nixie-skip-check | Out-Null
$skipCode = $LASTEXITCODE
$skipLine = @(Get-Content $skipLog -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\{.*"analyser"' }) | Select-Object -Last 1
$skip = $null
if ($skipLine) { try { $skip = $skipLine | ConvertFrom-Json } catch { $skip = $null } }
$skipLibmpv = [IO.Path]::GetFullPath((Join-Path $app 'libmpv\libmpv-2.dll'))
$skipOk = ($skipCode -eq 0) -and $skip -and $skip.ok -and $skip.analyser -eq 'libmpv' -and $skip.path -and
    ([IO.Path]::GetFullPath($skip.path) -eq $skipLibmpv)
Check 'skip rules snap with only the package: the bundled backend analyses with app\libmpv in a process of its own and lands on the cut' $skipOk `
    $(if ($skip) { "exit $skipCode; analyser $($skip.analyser) $($skip.path); snapped $($skip.snapped) s; $($skip.error)" } else { "exit $skipCode, no result line (log $skipLog)" })

# --- what the runs need: mpv for the Mpv run and for the test video, the test video ------------------------------------
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$mpvDir = $null
$mpvCmd = Get-Command mpv.exe -ErrorAction SilentlyContinue
if ($mpvCmd) {
    $mpvExe = $mpvCmd.Source
    $item = Get-Item $mpvExe
    if ($item.LinkType -and $item.Target) {
        $target = @($item.Target)[0]
        if (-not [IO.Path]::IsPathRooted($target)) { $target = Join-Path (Split-Path $mpvExe) $target }
        $mpvExe = [IO.Path]::GetFullPath($target)
    }
    $mpvDir = Split-Path $mpvExe
}
$media = Join-Path $Cache 'media'
$mediaJson = Join-Path $media 'media.json'
if (-not (Test-Path $mediaJson) -or -not ((Get-Content $mediaJson -Raw | ConvertFrom-Json).silent)) {
    if (-not $mpvDir) { throw "the test video is not built in $media and there is no mpv.exe to build it" }
    New-Item -ItemType Directory -Force $Cache | Out-Null
    & $HarnessPython -B -I $hidden --log (Join-Path $Out 'media-build.log') --timeout 900 --desktop "ffv-media-$PID" -- `
        $HarnessPython -B -I (Join-Path $harness 'fakeserver\media.py') --mpv $mpvExe --out $media | Out-Host
    if (-not (Test-Path $mediaJson)) { throw "the test video was not built (see $Out\media-build.log)" }
}

# --- the process audit ----------------------------------------------------------------------------------------------
$forbidden = @(
    @{ what = 'a dev checkout or worktree'; root = $devRoot },
    @{ what = 'the build''s work folder'; root = (Join-Path $repo 'dist\.work') },
    @{ what = 'a Python install'; root = 'C:\Python3' },
    @{ what = 'a Python install'; root = (Join-Path $env:LOCALAPPDATA 'Programs\Python') },
    @{ what = 'the harness Python'; root = (Split-Path (Split-Path $HarnessPython)) },
    @{ what = 'an MSYS2 install (libmpv''s DLLs must be the package''s)'; root = 'C:\msys64' }
)
function Start-Audit([string]$Name) {
    $stop = Join-Path $Out "$Name\audit.stop"
    if (Test-Path $stop) { Remove-Item $stop }
    $file = Join-Path $Out "$Name\audit.json"
    $p = Start-Process -FilePath $HarnessPython -PassThru -WindowStyle Hidden -ArgumentList @('-B', '-I', ('"' + (Join-Path $PSScriptRoot 'proc_audit.py') + '"'),
        '--root-exe', ('"' + $exe + '"'), '--out', ('"' + $file + '"'), '--stop-file', ('"' + $stop + '"'), '--max-seconds', ($TimeoutSeconds + 120))
    $null = $p.Handle   # held: the wait below is on this process, not on a pid
    @{ Process = $p; Stop = $stop; File = $file }
}
function Complete-Audit($Audit, [string]$Name, [bool]$MpvAllowed) {
    New-Item -ItemType File -Force $Audit.Stop | Out-Null
    if (-not $Audit.Process.WaitForExit(30000)) { Write-Warning "proc_audit.py (pid $($Audit.Process.Id)) did not stop; it ends by its own --max-seconds" }
    if (-not (Test-Path $Audit.File)) { Check "$($Name): process audit" $false 'no audit.json'; return }
    $audit = Get-Content $Audit.File -Raw | ConvertFrom-Json
    $procs = @($audit.processes)
    $bad = [Collections.Generic.List[string]]::new()
    $other = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $mpvSeen = $false
    foreach ($p in $procs) {
        foreach ($path in @($p.image) + @($p.modules)) {
            if (-not $path) { continue }
            $full = $path -replace '^\\\\\?\\', ''
            if (Test-Under $full $pkg) { continue }
            $isMpv = $mpvDir -and (Test-Under $full $mpvDir)
            if ($isMpv) { $mpvSeen = $true; if ($MpvAllowed) { continue } }
            foreach ($f in $forbidden) { if (Test-Under $full $f.root) { $bad.Add("$full ($($f.what), in $([IO.Path]::GetFileName($p.image))#$($p.pid))") } }
            if ($isMpv) { $bad.Add("$full (mpv from PATH/WinGet, in $([IO.Path]::GetFileName($p.image))#$($p.pid))") }
            elseif (-not (Test-Under $full $env:SystemRoot)) { [void]$other.Add($full) }
        }
        if ($p.commandLine -match '(?i)(access_?token|api_?key=|x-emby-token|authorization|token=)') { $bad.Add("a command line names a token: $([IO.Path]::GetFileName($p.image))#$($p.pid)") }
    }
    $summary = ($procs | ForEach-Object { "$([IO.Path]::GetFileName($_.image))#$($_.pid) ($(@($_.modules).Count) DLLs) = $($_.image)" }) -join '; '
    Check "$($Name): every process FluentFin started, and every DLL they loaded, is the package's or Windows'" ($bad.Count -eq 0 -and $procs.Count -gt 0) $(if ($bad.Count) { ($bad | Select-Object -Unique) -join '; ' } else { $summary })
    $backend = @($procs | Where-Object { $_.image -like '*\backend\fluentfin-mpv-backend.exe' })
    # InstallSwitch never signs in, so nothing starts the backend there (it starts with the shell)
    if ($Name -ne 'InstallSwitch') { Check "$($Name): the backend is the bundled one" ($backend.Count -ge 1) $(if ($backend) { $backend[0].commandLine } else { 'no fluentfin-mpv-backend.exe started' }) }
    if ($Name -eq 'Mpv') { Check "$($Name): mpv.exe from PATH was used (mpv's controls)" $mpvSeen $mpvDir }
    if ($Name -in 'Default', 'Launcher', 'SelfTest', 'Kill') {
        $own = Join-Path $app 'libmpv\libmpv-2.dll'
        $loaded = @($procs | Where-Object { $_.image -like '*\Nixie.exe' } | ForEach-Object { @($_.modules) } | Where-Object { ($_ -replace '^\\\\\?\\', '') -ieq $own })
        Check "$($Name): FluentFin loaded the package's libmpv-2.dll" ($loaded.Count -gt 0) $own
        # every DLL of the libmpv folder that FluentFin loaded came from that folder (Libmpv.cs loads them from nowhere else)
        $folder = Join-Path $app 'libmpv'
        $names = @(Get-ChildItem $folder -Filter '*.dll' | ForEach-Object { $_.Name.ToLowerInvariant() })
        $elsewhere = @($procs | Where-Object { $_.image -like '*\Nixie.exe' } | ForEach-Object { @($_.modules) } |
            ForEach-Object { $_ -replace '^\\\\\?\\', '' } | Where-Object { $names -contains [IO.Path]::GetFileName($_).ToLowerInvariant() -and -not (Test-Under $_ $folder) })
        Check "$($Name): libmpv's DLLs were loaded from the package's libmpv folder only" ($elsewhere.Count -eq 0) $(if ($elsewhere) { $elsewhere -join ', ' } else { $folder })
    }
    if ($other.Count) { Write-Host "  outside the package and Windows (drivers, shell extensions; allowed): $(($other | Sort-Object) -join ', ')" }
}

# --- the runs -------------------------------------------------------------------------------------------------------
function Invoke-ServerRun([string]$Name, [string]$Controls, [bool]$WithMpv, [string]$Steps, [bool]$ExpectPlay = $true, [string]$SavedPlayer) {
    $runOut = Join-Path $Out $Name
    $data = Join-Path $Out "data-$Name"
    foreach ($d in $runOut, $data) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
    New-Item -ItemType Directory -Force $runOut, $data | Out-Null
    # no backend command: FluentFin resolves its bundled backend itself (MpvBackendCommand.Resolve)
    $prepared = Initialize-HarnessSettings $data '' @(if ($Controls) { "MpvPlayerControls=$Controls" })
    [void](Initialize-HarnessMpvConf $data)
    if ($SavedPlayer) {
        # a player saved by another build (the harness itself only ever writes Mpv)
        $settingsFile = Join-Path $data 'LocalSettings.json'
        $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $saved.MediaPlayerType = $SavedPlayer
        [IO.File]::WriteAllText($settingsFile, ($saved | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    }
    $envA = Join-Path $runOut 'env\appdata'; $envL = Join-Path $runOut 'env\localappdata'
    New-Item -ItemType Directory -Force $envA, $envL | Out-Null
    $path = if ($WithMpv) { "$mpvDir;$windowsPath" } else { $windowsPath }
    $stepsFile = Join-Path $runOut 'steps.txt'
    [IO.File]::WriteAllText($stepsFile, $Steps, [Text.UTF8Encoding]::new($false))
    $driveArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $harness 'offscreen\drive.ps1'),
        '-Exe', $exe, '-DataDir', $data, '-Out', $runOut, '-Python', $HarnessPython, '-Media', $media,
        '-SignIn', 'quickconnect', '-PlayerControls', $prepared.PlayerControls, '-StepsFile', $stepsFile, '-NoA11y')
    $audit = Start-Audit $Name
    Write-Host "`n== $Name (controls $(if ($Controls) { $Controls } else { "not set: the default" }), mpv on PATH: $WithMpv; $runOut)"
    & $HarnessPython -B -I $hidden --log (Join-Path $runOut 'hidden_run.log') --timeout $TimeoutSeconds --desktop "ffv-$Name-$PID" `
        --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" --env "PATH=$path" -- $ps @driveArgs | Out-Host
    Complete-Audit $audit $Name ($Name -eq 'Mpv')
    $resultFile = Join-Path $runOut 'result.json'
    if (Test-Path $resultFile) {
        $r = Get-Content $resultFile -Raw | ConvertFrom-Json
        foreach ($c in $r.checks.PSObject.Properties) {
            if (-not $ExpectPlay -and $c.Name -match '^(video shows|playback reported|silent:)') {
                # nothing can play: what drive.ps1 counts as a failure is the expected outcome here
                if ($c.Name -match '^playback reported') { Check "$($Name): nothing played, so nothing was reported to the server" ("$($c.Value.detail)" -match 'nobody') "$($c.Value.detail)" }
                continue
            }
            Check "$($Name): $($c.Name)" ([bool]$c.Value.ok) "$($c.Value.detail)"
        }
    }
    else { Check "$($Name): drive.ps1 result" $false "no result.json (see $runOut\drive.log)" }
    $backendLog = Join-Path $data 'Logs\mpv-backend.log'
    Check "$($Name): the bundled backend logged its start" ((Test-Path $backendLog) -and (Select-String -Path $backendLog -Pattern 'Bridge .* starting' -Quiet)) $backendLog
    $stray = @(Get-ChildItem $envA, $envL -Recurse -File -Force -ErrorAction SilentlyContinue)
    Check "$($Name): nothing written to APPDATA/LOCALAPPDATA" ($stray.Count -eq 0) (($stray | ForEach-Object { $_.FullName.Substring($runOut.Length) }) -join ', ')
    $data
}

foreach ($run in $Runs) {
    switch ($run) {
        'Default' {
            # no MpvPlayerControls in the settings: the app's own default decides
            # relaunch first (the automatic sign-in at start-up), then steady playback: nothing over the middle of the video
            # (the sign-in ring stayed there; wip/ring-fix's check in drive.ps1)
            $data = Invoke-ServerRun 'Default' $null $false 'relaunch,play,steady,leave,close'
            $appLog = @(Get-ChildItem (Join-Path $data 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)
            $core = $appLog | Select-String -Pattern 'Started the in-process mpv core \(composition mode\)' | Select-Object -First 1
            $attached = (Test-Path (Join-Path $data 'Logs\mpv-backend.log')) -and (Select-String -Path (Join-Path $data 'Logs\mpv-backend.log') -Pattern 'connect: mpv started in .* \(attached window\)' -Quiet)
            Check "Default: FluentFin's controls by default (the in-process mpv, the backend attached to it)" ([bool]$core -and $attached) $(if ($core) { $core.Line.Trim() } else { 'no in-process mpv core in FluentFin''s log' })
            $client = $appLog | Select-String -Pattern 'Connecting the mpv backend: .* as Nixie ([0-9.]+) ' | Select-Object -First 1
            Check 'Default: the client version sent to Jellyfin is the package''s' ([bool]$client -and $pkg -like "*-$($client.Matches[0].Groups[1].Value)-win-x64") $(if ($client) { 'Nixie ' + $client.Matches[0].Groups[1].Value } else { 'no connect line' })
        }
        'SavedVlc' {
            # a data folder whose saved player is VLC (from a dev build): the release has no VLC, so it plays with MPV
            # and Settings opened after the play: it shows MPV and must not save it over the saved VLC
            $data = Invoke-ServerRun 'SavedVlc' $null $false 'season,play,leave,nav:Settings,close' -SavedPlayer 'Vlc'
            $appLog = @(Get-ChildItem (Join-Path $data 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)
            $core = $appLog | Select-String -Pattern 'Started the in-process mpv core' | Select-Object -First 1
            $crash = $appLog | Select-String -Pattern 'Unhandled exception|\[FTL\]|LibVLC|libvlc' | Select-Object -First 1
            Check 'SavedVlc: a saved VLC choice plays with MPV, no crash' ([bool]$core -and -not $crash) $(if ($crash) { $crash.Line.Trim() } elseif ($core) { 'MPV (FluentFin''s controls)' } else { 'no in-process mpv in FluentFin''s log' })
            $stillSaved = (Get-Content (Join-Path $data 'LocalSettings.json') -Raw | ConvertFrom-Json).MediaPlayerType
            Check 'SavedVlc: the saved choice is kept after playing and opening Settings (a dev build still finds it)' ("$stillSaved" -eq 'Vlc') "MediaPlayerType = $stillSaved"
        }
        'Mpv' {
            if (-not $mpvDir) { Write-Host "`n== Mpv: skipped (no mpv.exe on the Machine/User PATH)"; continue }
            $data = Invoke-ServerRun 'Mpv' 'Mpv' $true 'season,play,leave,close'
            # mpv's controls are the uosc bar when the package carries it (backend\_internal\fluentfin); the backend says so
            $bundledUosc = Test-Path (Join-Path $app 'backend\_internal\fluentfin\uosc-files.txt')
            if ($bundledUosc) {
                $log = Join-Path $data 'Logs\mpv-backend.log'
                $uosc = (Test-Path $log) -and (Select-String -Path $log -Pattern 'the bar is uosc' -Quiet) -and -not (Select-String -Path $log -Pattern 'uosc is not complete|the bar is mpv' -Quiet)
                Check "Mpv: the bar is the bundled uosc" $uosc $log
            }
        }
        'NoMpv' {
            $data = Invoke-ServerRun 'NoMpv' 'Mpv' $false 'season,play,close' $false
            # the backend answers connect with mpv_not_found (BRIDGE.md section 2), and the player page says what to do
            # (Core\Mpv\MpvNotFoundMessage): read from the page's UI Automation dump, as the user would see it
            $backendLog = Join-Path $data 'Logs\mpv-backend.log'
            $code = (Test-Path $backendLog) -and (Select-String -Path $backendLog -Pattern 'connect \(id \d+\) failed: mpv_not_found' -Quiet)
            Check 'NoMpv: the backend answers connect with mpv_not_found' $code $backendLog
            $dump = Join-Path $Out 'NoMpv\uia\player.txt'
            # 1.2.0: the app's own name (Nixie's controls), never upstream's
            $shown = if (Test-Path $dump) { Select-String -Path $dump -Pattern "mpv isn't installed on this PC.*Switch to Nixie's controls.*winget install mpv-player\.mpv-CI\.MSVC" | Select-Object -First 1 }
            Check "NoMpv: the player page says to switch to Nixie's controls or install mpv (winget)" ([bool]$shown) $(if ($shown) { $shown.Line.Trim() } else { "no such text in $dump" })
        }
        'InstallSwitch' {
            # 1.6.1 (BH-151-m6): the release package's gate on --install-update, for real (the Sandbox proves only the allowed side)
            $runOut = Join-Path $Out $run
            $data = Join-Path $Out "data-$run"
            foreach ($d in $runOut, $data) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
            foreach ($leg in 'no-marker', 'marker') {
                New-Item -ItemType Directory -Force (Join-Path $data $leg) | Out-Null
                [void](Initialize-HarnessSettings (Join-Path $data $leg) '' @('MpvPlayerControls=FluentFin'))
                [void](Initialize-HarnessMpvConf (Join-Path $data $leg))
            }
            New-Item -ItemType Directory -Force $runOut | Out-Null
            $envA = Join-Path $runOut 'env\appdata'; $envL = Join-Path $runOut 'env\localappdata'
            New-Item -ItemType Directory -Force $envA, $envL | Out-Null
            $audit = Start-Audit $run
            Write-Host "`n== $run (Nixie.exe --install-update <a decoy>, without and with the upgrade test's marker; $runOut)"
            $switchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'installswitch-offscreen.ps1'), '-Exe', $exe, '-DataDir', $data, '-Out', $runOut)
            & $HarnessPython -B -I $hidden --log (Join-Path $runOut 'hidden_run.log') --timeout 240 --desktop "ffv-$run-$PID" `
                --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" --env "PATH=$windowsPath" -- $ps @switchArgs | Out-Host
            Complete-Audit $audit $run $false
            $json = Join-Path $runOut 'installswitch.json'
            if (Test-Path $json) { (Get-Content $json -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { Check "$($run): $($_.Name)" ([bool]$_.Value.ok) "$($_.Value.detail)" } }
            else { Check "$($run): result" $false "no installswitch.json (see $runOut\hidden_run.log)" }
        }
        { $_ -in 'Launcher', 'SelfTest', 'Kill' } {
            # Kill: the self-test again, but FluentFin is ended abruptly (a crash): its job must end the bundled backend
            # Launcher: the self-test started through the top Nixie.exe, with a data folder whose name has a space
            $runOut = Join-Path $Out $run
            $data = Join-Path $Out $(if ($run -eq 'Launcher') { 'data-Launcher x' } else { "data-$run" })
            foreach ($d in $runOut, $data) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
            New-Item -ItemType Directory -Force $runOut, $data | Out-Null
            [void](Initialize-HarnessSettings $data '' @('MpvPlayerControls=FluentFin'))
            [void](Initialize-HarnessMpvConf $data)
            $envA = Join-Path $runOut 'env\appdata'; $envL = Join-Path $runOut 'env\localappdata'
            New-Item -ItemType Directory -Force $envA, $envL | Out-Null
            $audit = Start-Audit $run
            Write-Host "`n== $run (Nixie.exe --mpv-selftest, FluentFin's controls, no mpv on PATH$(if ($run -eq 'Kill') { ', then FluentFin killed' })$(if ($run -eq 'Launcher') { ', started through the launcher' }); $runOut)"
            $selftestArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'selftest-offscreen.ps1'), '-Exe', $exe, '-DataDir', $data, '-Out', $runOut)
            if ($run -eq 'Kill') { $selftestArgs += '-Kill' }
            if ($run -eq 'Launcher') { $selftestArgs += @('-Launcher', $launcherExe) }
            & $HarnessPython -B -I $hidden --log (Join-Path $runOut 'hidden_run.log') --timeout 180 --desktop "ffv-$run-$PID" `
                --env "APPDATA=$envA" --env "LOCALAPPDATA=$envL" --env "PATH=$windowsPath" -- $ps @selftestArgs | Out-Host
            Complete-Audit $audit $run $false
            $json = Join-Path $runOut 'selftest.json'
            if (Test-Path $json) { (Get-Content $json -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { Check "$($run): $($_.Name)" ([bool]$_.Value.ok) "$($_.Value.detail)" } }
            else { Check "$($run): result" $false "no selftest.json (see $runOut\hidden_run.log)" }
            if ($run -eq 'Kill') {
                # who ended the backend: its job (the log stops mid-playback) or its own stdin-EOF shutdown (the log says
                # "Shutting down"). Both end it; the first is what a crash relies on. Not a check: the two can race.
                $tail = @(Get-Content (Join-Path $data 'Logs\mpv-backend.log') -ErrorAction SilentlyContinue | Select-Object -Last 3)
                $byJob = -not ($tail | Where-Object { $_ -match 'Shutting down|Bridge stopped' })
                Write-Host "  the backend was ended by $(if ($byJob) { 'its job (its log stops mid-playback)' } else { 'its own stdin-EOF shutdown' }): $($tail[-1])"
            }
        }
    }
}

$after = @(Get-Listing)
$changed = @(Compare-Object $before $after | ForEach-Object { ($_.InputObject -split '\|')[0] } | Select-Object -Unique)
Check 'nothing was written into the unzipped folder' ($changed.Count -eq 0) (($changed | Select-Object -First 10) -join ', ')

[IO.File]::WriteAllText((Join-Path $Out 'verify.json'), (ConvertTo-Json -Depth 5 -InputObject $results), [Text.UTF8Encoding]::new($false))
$failed = @($results.Keys | Where-Object { -not $results[$_].ok })
Write-Host ("`n{0} checks, {1} failed{2}" -f $results.Count, $failed.Count, $(if ($failed) { ': ' + ($failed -join '; ') }))
if ($failed) { exit 1 }
