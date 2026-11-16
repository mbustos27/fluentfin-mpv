<#
.SYNOPSIS
  Verifies a release zip as a user would get it: unzipped outside the repository, run off-screen against the fake
  Jellyfin server with nothing from a dev checkout, then the self-test. Silent: test media is digital silence and mpv's
  audio output is null.

.DESCRIPTION
  - Checks the zip against its .sha256 and unzips it into -Out\pkg (a fresh folder; -Out must be outside the repo).
  - Runs, each with its own scratch data folder (--data-dir), APPDATA/LOCALAPPDATA pointed into -Out, and a PATH of
    Windows' own folders only (no Python, no mpv), through tools\ui-harness\offscreen\drive.ps1 on a private desktop
    inside a kill-on-close job (tools\backend-bench\hidden_run.py), with no backend command set (FluentFin finds its
    bundled backend by itself):
      FluentFin  "Player controls: FluentFin": sign in, play an episode, stop, close.
      Mpv        "Player controls: mpv", with mpv's folder added to PATH (the documented requirement for mpv's
                 controls): the same steps. Skipped when no mpv.exe is on the Machine/User PATH.
      NoMpv      "Player controls: mpv" without mpv on PATH: sign in and play; playback must stop with "mpv could not be
                 started" and report nothing to the server (the release notes' "needs mpv on PATH"), then close.
      SelfTest   FluentFin.exe --mpv-selftest with FluentFin's controls (no server), tools\release\selftest-offscreen.ps1.
      Kill       the self-test, then FluentFin ended abruptly (a crash): its kill-on-close job must end the backend.
  - Alongside each run, tools\release\proc_audit.py records every process FluentFin starts (image, command line, every
    DLL loaded). A run fails if any of them comes from the repository, a dev checkout, a Python install, the build's
    work folder or (except mpv.exe and its DLLs in the Mpv run) a WinGet mpv; or if a command line names a token.
  - Fails if the run wrote anything into the unzipped folder.
  The harness itself (fake server, hidden_run.py, proc_audit.py) runs with -HarnessPython; FluentFin never sees it.

.EXAMPLE
  .\tools\release\verify-package.ps1 -Zip dist\FluentFinMPV-1.0.0-win-x64.zip -Out C:\scratch\verify
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Zip,
    [Parameter(Mandatory)][string]$Out,
    [string]$HarnessPython,
    [string]$Cache = (Join-Path $env:TEMP 'fluentfin-mpv-harness'),
    [ValidateSet('FluentFin', 'Mpv', 'NoMpv', 'SelfTest', 'Kill')][string[]]$Runs = @('FluentFin', 'Mpv', 'NoMpv', 'SelfTest', 'Kill'),
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
$exe = Join-Path $pkg 'FluentFin.exe'
Check 'unzipped: one folder with FluentFin.exe, the bundled backend and libmpv' ((Test-Path $exe) -and (Test-Path (Join-Path $pkg 'backend\fluentfin-mpv-backend.exe')) -and (Test-Path (Join-Path $pkg 'libmpv-2.dll'))) $pkg
function Get-Listing { Get-ChildItem $pkg -Recurse -File -Force | ForEach-Object { "{0}|{1}|{2}" -f $_.FullName.Substring($pkg.Length), $_.Length, $_.LastWriteTimeUtc.Ticks } }
$before = @(Get-Listing)

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
    @{ what = 'the harness Python'; root = (Split-Path (Split-Path $HarnessPython)) }
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
    Check "$($Name): the backend is the bundled one" ($backend.Count -ge 1) $(if ($backend) { $backend[0].commandLine } else { 'no fluentfin-mpv-backend.exe started' })
    if ($Name -eq 'Mpv') { Check "$($Name): mpv.exe from PATH was used (mpv's controls)" $mpvSeen $mpvDir }
    if ($other.Count) { Write-Host "  outside the package and Windows (drivers, shell extensions; allowed): $(($other | Sort-Object) -join ', ')" }
}

# --- the runs -------------------------------------------------------------------------------------------------------
function Invoke-ServerRun([string]$Name, [string]$Controls, [bool]$WithMpv, [string]$Steps, [bool]$ExpectPlay = $true) {
    $runOut = Join-Path $Out $Name
    $data = Join-Path $Out "data-$Name"
    foreach ($d in $runOut, $data) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
    New-Item -ItemType Directory -Force $runOut, $data | Out-Null
    # no backend command: FluentFin resolves its bundled backend itself (MpvBackendCommand.Resolve)
    $prepared = Initialize-HarnessSettings $data '' @("MpvPlayerControls=$Controls")
    [void](Initialize-HarnessMpvConf $data)
    $envA = Join-Path $runOut 'env\appdata'; $envL = Join-Path $runOut 'env\localappdata'
    New-Item -ItemType Directory -Force $envA, $envL | Out-Null
    $path = if ($WithMpv) { "$mpvDir;$windowsPath" } else { $windowsPath }
    $stepsFile = Join-Path $runOut 'steps.txt'
    [IO.File]::WriteAllText($stepsFile, $Steps, [Text.UTF8Encoding]::new($false))
    $driveArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $harness 'offscreen\drive.ps1'),
        '-Exe', $exe, '-DataDir', $data, '-Out', $runOut, '-Python', $HarnessPython, '-Media', $media,
        '-SignIn', 'quickconnect', '-PlayerControls', $prepared.PlayerControls, '-StepsFile', $stepsFile, '-NoA11y')
    $audit = Start-Audit $Name
    Write-Host "`n== $Name (controls $Controls, mpv on PATH: $WithMpv; $runOut)"
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
        'FluentFin' { [void](Invoke-ServerRun 'FluentFin' 'FluentFin' $false 'season,play,leave,close') }
        'Mpv' {
            if (-not $mpvDir) { Write-Host "`n== Mpv: skipped (no mpv.exe on the Machine/User PATH)"; continue }
            [void](Invoke-ServerRun 'Mpv' 'Mpv' $true 'season,play,leave,close')
        }
        'NoMpv' {
            $data = Invoke-ServerRun 'NoMpv' 'Mpv' $false 'season,play,close' $false
            # the player page's error bar shows this exception's text (look at NoMpv\shots\*-player.png)
            $logs = @(Get-ChildItem (Join-Path $data 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)
            $said = $logs | Select-String -Pattern 'MPV playback could not start .*mpv_failed \(mpv could not be started' | Select-Object -First 1
            Check "NoMpv: without mpv on PATH, mpv's controls stop with 'mpv could not be started'" ([bool]$said) $(if ($said) { $said.Line.Trim() } else { 'no such line in FluentFin''s log' })
        }
        { $_ -in 'SelfTest', 'Kill' } {
            # Kill: the self-test again, but FluentFin is ended abruptly (a crash): its job must end the bundled backend
            $runOut = Join-Path $Out $run
            $data = Join-Path $Out "data-$run"
            foreach ($d in $runOut, $data) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
            New-Item -ItemType Directory -Force $runOut, $data | Out-Null
            [void](Initialize-HarnessSettings $data '' @('MpvPlayerControls=FluentFin'))
            [void](Initialize-HarnessMpvConf $data)
            $envA = Join-Path $runOut 'env\appdata'; $envL = Join-Path $runOut 'env\localappdata'
            New-Item -ItemType Directory -Force $envA, $envL | Out-Null
            $audit = Start-Audit $run
            Write-Host "`n== $run (FluentFin.exe --mpv-selftest, FluentFin's controls, no mpv on PATH$(if ($run -eq 'Kill') { ', then FluentFin killed' }); $runOut)"
            $selftestArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot 'selftest-offscreen.ps1'), '-Exe', $exe, '-DataDir', $data, '-Out', $runOut)
            if ($run -eq 'Kill') { $selftestArgs += '-Kill' }
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
