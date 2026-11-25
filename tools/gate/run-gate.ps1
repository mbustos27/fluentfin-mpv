<#
.SYNOPSIS
  Nixie 1.7.5 (design rule 12): the basics gate. Runs every check of tools\gate\manifest\*.psd1 off-screen, at real shape,
  in every theme it names, and exits non-zero on any failure, with a one-line reason per failure.

.DESCRIPTION
  The pattern (manifests, check kinds, slots, the report): tools\gate\README.md.
  - -Exe: the release package's app\Nixie.exe or a Debug build, alike. A release package (its Nixie.dll carries
    FluentFinMpvRelease) never listens to the harness's dev hooks, so a check with Dev = $true runs on -DevExe, or on the
    gate's own build (-Build: this checkout's Debug x64 into -BuildDir, default <Cache>\gate-build, off-screen in a
    kill-on-close job). A Dev check with a release -Exe and neither fails; no check passes by being skipped.
  - Each job (one check in one theme) runs in its own hidden PowerShell (tools\gate\run-job.ps1): a 'then' check through
    tools\ui-harness\run-offscreen.ps1, which takes one of the machine's off-screen slots (NIXIE_HARNESS_PARALLEL) for its
    run; a 'script' check through its own script, which takes its slots the same way. The runner holds no slot; it starts
    at most -Parallel jobs at once. A job that failed while Windows minimized its window (a monitor dropping off the bus:
    result.json's display.events) runs once more (-Retry).
  - Ends only what it started: each job's PowerShell, by the Process object it holds, and only when that job ran past its
    time; hidden_run.py's kill-on-close job ends what the run started.
  Exit 0: every job passed. 1: a job failed. 2: the gate couldn't run (bad input, a manifest error).

.EXAMPLE
  .\tools\gate\run-gate.ps1 -List
  .\tools\gate\run-gate.ps1 -Exe C:\scratch\build\bin\FluentFin\debug\Nixie.exe -Out C:\scratch\gate -Python C:\...\python.exe
  .\tools\gate\run-gate.ps1 -Exe <pkg>\app\Nixie.exe -Build -Out C:\scratch\gate -Python C:\...\python.exe
  .\tools\gate\run-gate.ps1 -Exe <1.7.3 build> -Backend <1.7.3 checkout>\backend -Checks signin-methods -Themes Classic -Out ... -Python ...
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Exe,
    [string]$Out,
    [string]$Python,
    [string]$Backend,
    [string]$DevExe,
    [switch]$Build,
    [string]$BuildDir,
    # the gate's own build: libmpv's folder (else the checkout's vendor\libmpv default): FluentFin's controls need it beside the exe
    [string]$LibmpvDir = $env:NIXIE_LIBMPV_DIR,
    [string]$Cache = (Join-Path $env:TEMP 'fluentfin-mpv-harness'),
    [string[]]$Checks = @(),
    [string[]]$Classes = @(),
    [string[]]$Owners = @(),
    [string[]]$Themes = @(),
    [int]$Parallel = 0,
    [int]$QueueMinutes = 120,
    [int]$Retry = 1,
    [switch]$List,
    # tools\gate\test_gate.ps1's own manifests (default tools\gate\manifest)
    [string]$ManifestDir,
    # 1.7.5 (the 90-minute budget): the jobs run longest first, by each job's own run time measured before: tools\gate\
    # timings.json (committed; -SaveTimings refreshes it from this run) and any -Timings <an earlier gate.json>; a job never
    # measured counts a quarter of its Minutes
    [string[]]$Timings = @(),
    [switch]$SaveTimings,
    # the release build's budget for the gate's real time (minutes; 1.7.5: 2 h, the coordinator's decision): the plan says when it doesn't fit
    [int]$Budget = 120,
    # also the entries marked Tier = 'nightly' (a release build runs the rest)
    [switch]$Nightly,
    # NAME=value for every job (a script's parts for a revert run: -JobEnv GATE_SI_PARTS=qc). The caller's own shell
    # variables never reach a job (below); this is the way to give one
    [string[]]$JobEnv = @()
)

$ErrorActionPreference = 'Stop'
$AllThemes = @('Classic', 'DriveIn', 'Cinematic', 'Editorial')
$EntryKeys = @('Id', 'Class', 'Owner', 'What', 'Kind', 'Then', 'Before', 'Steps', 'SignIn', 'Settings', 'Env', 'Themes', 'Sizes',
    'Dev', 'Minutes', 'Fails173', 'Script', 'Args', 'RunArgs', 'Tier')
# run-offscreen.ps1's other parameters a 'then' entry may set (RunArgs = @{ WithoutMpv = $true })
$RunArgKeys = @('Latency', 'LatencyRule', 'WithoutMpv', 'GlassFollowsActivity', 'TabStops', 'QaServer')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
. (Join-Path $repo 'tools\ui-harness\offscreen\prepare.ps1')

function Split-List([string[]]$Values) { @($Values | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

# --- the manifests -----------------------------------------------------------------------------------------------------
# every entry of tools\gate\manifest\*.psd1, checked and with its defaults; throws with every problem found
function Read-GateManifests([string]$Folder) {
    $problems = New-Object System.Collections.Generic.List[string]
    $entries = New-Object System.Collections.Generic.List[object]
    $files = @(Get-ChildItem -LiteralPath $Folder -Filter '*.psd1' -File -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($f in $files) {
        try { $data = Import-PowerShellDataFile -LiteralPath $f.FullName }
        catch { $problems.Add("$($f.Name): not a PowerShell data file: $($_.Exception.Message)"); continue }
        if (-not $data.ContainsKey('Checks')) { $problems.Add("$($f.Name): no Checks = @( ... )"); continue }
        foreach ($c in @($data.Checks)) {
            if ($c -isnot [hashtable]) { $problems.Add("$($f.Name): an entry that isn't @{ ... }"); continue }
            $where = "$($f.Name) $($c.Id)"
            foreach ($k in $c.Keys) { if ($EntryKeys -notcontains $k) { $problems.Add("${where}: unknown key '$k' (keys: $($EntryKeys -join ', '))") } }
            $e = [ordered]@{
                Id = "$($c.Id)"; Class = $c.Class; Owner = "$($c.Owner)"; What = "$($c.What)"; Kind = $(if ($c.Kind) { "$($c.Kind)" } else { 'then' })
                Then = "$($c.Then)"; Before = "$($c.Before)"; Steps = $(if ($c.Steps) { "$($c.Steps)" } else { 'wait:1' })
                SignIn = $(if ($c.SignIn) { "$($c.SignIn)" } else { 'quickconnect' }); Settings = @($c.Settings | Where-Object { $_ })
                Env = $(if ($c.Env) { $c.Env } else { @{} }); Themes = $(if ($c.Themes) { $c.Themes } else { 'all' })
                Sizes = $(if ($c.Sizes) { "$($c.Sizes)" } else { 'gate' }); Dev = [bool]$c.Dev; Minutes = $(if ($c.Minutes) { $c.Minutes } else { 20 })
                Fails173 = "$($c.Fails173)"; Script = "$($c.Script)"; Args = $(if ($c.Args) { $c.Args } else { @{} })
                RunArgs = $(if ($c.RunArgs) { $c.RunArgs } else { @{} }); Tier = $(if ($c.Tier) { "$($c.Tier)" } else { 'release' }); File = $f.Name
            }
            if ($e.Tier -notin @('release', 'nightly')) { $problems.Add("${where}: Tier must be 'release' (every release build, the default) or 'nightly'") }
            if ($e.RunArgs -isnot [hashtable]) { $problems.Add("${where}: RunArgs must be @{ Name = value }") }
            else { foreach ($k in $e.RunArgs.Keys) { if ($RunArgKeys -notcontains $k) { $problems.Add("${where}: RunArgs '$k' (run-offscreen.ps1's: $($RunArgKeys -join ', '))") } } }
            if ($e.Id -notmatch '^[a-z0-9]+(-[a-z0-9]+)*$') { $problems.Add("${where}: Id '$($e.Id)' is not [a-z0-9-]") }
            if (@($entries | Where-Object { $_.Id -eq $e.Id }).Count) { $problems.Add("${where}: Id '$($e.Id)' is used twice") }
            if (-not ($e.Class -is [int] -and $e.Class -ge 1 -and $e.Class -le 8)) { $problems.Add("${where}: Class must be 1 to 8 (the basics sweep's classes)") }
            if (-not $e.Owner) { $problems.Add("${where}: no Owner") }
            if (-not $e.What) { $problems.Add("${where}: no What (what the user sees that it asserts)") }
            if ($e.Kind -notin @('then', 'script')) { $problems.Add("${where}: Kind must be 'then' or 'script'") }
            if ($e.Kind -eq 'then') {
                if (-not $e.Then -or -not (Test-Path -LiteralPath (Join-Path $repo $e.Then))) { $problems.Add("${where}: Then '$($e.Then)' isn't a script in the repository") }
                if ($e.Before -and -not (Test-Path -LiteralPath (Join-Path $repo $e.Before))) { $problems.Add("${where}: Before '$($e.Before)' isn't a script in the repository") }
                if ($e.SignIn -notin @('quickconnect', 'password')) { $problems.Add("${where}: SignIn must be quickconnect or password") }
            }
            else {
                if (-not $e.Script -or -not (Test-Path -LiteralPath (Join-Path $repo $e.Script))) { $problems.Add("${where}: Script '$($e.Script)' isn't a script in the repository") }
                if ($e.Args -isnot [hashtable]) { $problems.Add("${where}: Args must be @{ Name = value }") }
            }
            if ($e.Env -isnot [hashtable]) { $problems.Add("${where}: Env must be @{ NAME = 'value' }") }
            $t = @(Split-List @($e.Themes))
            if ($t.Count -eq 1 -and $t[0] -eq 'all') { $t = $AllThemes }
            foreach ($x in $t) { if ($AllThemes -notcontains $x) { $problems.Add("${where}: theme '$x' (themes: all, $($AllThemes -join ', '))") } }
            $e.ThemeList = $t
            if ($e.Sizes -notin @('gate', 'none') -and $e.Sizes -notmatch '^((\d+x\d+|max)(,|$))+$') { $problems.Add("${where}: Sizes '$($e.Sizes)' (gate, none, or WxH,WxH,max)") }
            if (-not ($e.Minutes -is [int] -and $e.Minutes -ge 1 -and $e.Minutes -le 180)) { $problems.Add("${where}: Minutes must be 1 to 180") }
            $entries.Add([pscustomobject]$e)
        }
    }
    if ($problems.Count) { throw ("the gate's manifests have problems:`n  " + ($problems -join "`n  ")) }
    , $entries.ToArray()
}

# a release package's Nixie.dll carries the FluentFinMpvRelease marker (build-release.ps1 checks it the same way)
function Test-ReleasePackage([string]$ExePath) {
    $dll = Join-Path (Split-Path $ExePath) 'Nixie.dll'
    (Test-Path -LiteralPath $dll) -and [bool](Select-String -LiteralPath $dll -SimpleMatch -Pattern 'FluentFinMpvRelease' -Quiet)
}

# never the installed app or the owner's test copies (their data sits beside them)
function Assert-GateExe([string]$Path, [string]$What) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $full)) { throw "$What $full does not exist" }
    foreach ($root in @((Join-Path $env:LOCALAPPDATA 'Programs\Nixie'), (Join-Path $env:LOCALAPPDATA 'Programs\Seaglass'))) {
        if (Test-Under $full $root) { throw "$What $full is the installed app; give a build or an unzipped package in a scratch folder" }
    }
    $profileRoot = [Environment]::GetFolderPath('UserProfile')
    if ($full -match '^(.+?\\Nixie-[^\\]*test[^\\]*)\\' -and (Split-Path $Matches[1]) -eq $profileRoot) { throw "$What $full is in a test copy of the owner's ($($Matches[1])); give a scratch build" }
    $full
}

try {
    $manifestDir = if ($ManifestDir) { [IO.Path]::GetFullPath($ManifestDir) } else { Join-Path $PSScriptRoot 'manifest' }
    $entries = Read-GateManifests $manifestDir
    $Checks = Split-List $Checks; $Owners = Split-List $Owners; $Themes = Split-List $Themes
    $classFilter = @(Split-List $Classes | ForEach-Object { [int]$_ })
    foreach ($t in $Themes) { if ($AllThemes -notcontains $t) { throw "-Themes: '$t' is no theme ($($AllThemes -join ', '))" } }
    foreach ($id in $Checks) { if (-not @($entries | Where-Object { $_.Id -eq $id }).Count) { throw "-Checks: no check '$id' in $manifestDir (run-gate.ps1 -List)" } }
    # a nightly entry runs when asked by name (-Checks) or with -Nightly
    $chosen = @($entries | Where-Object {
            (-not $Checks.Count -or $Checks -contains $_.Id) -and (-not $Owners.Count -or $Owners -contains $_.Owner) -and
            (-not $classFilter.Count -or $classFilter -contains $_.Class) -and ($_.Tier -eq 'release' -or $Nightly -or $Checks -contains $_.Id) })

    # each job's own run time, measured before (seconds): "<id>|<theme>" -> seconds, from timings.json and -Timings gate.json files
    $timingsFile = Join-Path $PSScriptRoot 'timings.json'
    $measured = @{}
    foreach ($tf in @(@($timingsFile | Where-Object { Test-Path -LiteralPath $_ }) + @($Timings | ForEach-Object { [IO.Path]::GetFullPath($_) }))) {
        if (-not (Test-Path -LiteralPath $tf)) { throw "-Timings: no file $tf" }
        $tj = Get-Content -LiteralPath $tf -Raw -Encoding utf8 | ConvertFrom-Json
        if ($tj.PSObject.Properties['jobs']) { foreach ($j in @($tj.jobs)) { if ($j.ran_seconds) { $measured["$($j.id)|$($j.theme)"] = [double]$j.ran_seconds } } }
        else { foreach ($p in $tj.PSObject.Properties) { if ($p.Name -match '\|' -and $p.Value) { $measured[$p.Name] = [double]$p.Value } } }
    }
    function Get-Expect($Entry, [string]$Theme) { $k = "$($Entry.Id)|$Theme"; if ($measured.ContainsKey($k)) { $measured[$k] } else { $Entry.Minutes * 60 * 0.25 } }

    if ($List) {
        Write-Host ("{0} checks in {1}" -f $entries.Count, $manifestDir)
        foreach ($e in $chosen) {
            $mins = [Math]::Round((@($e.ThemeList | ForEach-Object { Get-Expect $e $_ }) | Measure-Object -Sum).Sum / 60.0, 1)
            Write-Host ("  {0,-28} class {1}  {2,-5} {3,-6} {4}{5}{6}  ~{7} min  {8}" -f $e.Id, $e.Class, $e.Owner, $e.Kind, ($e.ThemeList -join ','), $(if ($e.Dev) { '  dev' }),
                $(if ($e.Tier -ne 'release') { "  $($e.Tier)" }), $mins, $e.What)
        }
        exit 0
    }

    if (-not $Out) { throw "-Out: a scratch folder outside the repository" }
    $Out = [IO.Path]::GetFullPath($Out).TrimEnd('\')
    if (Test-Under $Out $repo) { throw "-Out must be outside the repository ($repo)" }
    if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
    if (-not [IO.Path]::IsPathRooted($Python)) { throw "-Python must be absolute (a relative one fails under another working folder): $Python" }
    if (-not (Test-Path -LiteralPath $Python)) { throw "no Python at $Python (-Python: a venv with the backend's dependencies)" }
    if (-not $Backend) { $Backend = Join-Path $repo 'backend' }
    $Backend = [IO.Path]::GetFullPath($Backend)
    if (-not (Test-Path -LiteralPath (Join-Path $Backend 'jellyfin_mpv_shim\bridge.py'))) { throw "-Backend $Backend has no jellyfin_mpv_shim\bridge.py" }
    $Cache = [IO.Path]::GetFullPath($Cache)
    if (-not $chosen.Count) { throw "no check matches -Checks/-Owners/-Classes" }
    if ($Parallel -le 0) { $Parallel = Get-HarnessRunSlots }

    # the gate's own build (-Build): this checkout, Debug x64, off-screen in a kill-on-close job, nothing of it left running
    if ($Build) {
        if (-not $BuildDir) { $BuildDir = Join-Path $Cache 'gate-build' }
        $BuildDir = [IO.Path]::GetFullPath($BuildDir)
        if (Test-Under $BuildDir $repo) { throw "-BuildDir must be outside the repository ($repo)" }
        $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
        $dotnet = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
        if (-not $dotnet) { throw "dotnet.exe is not on PATH" }
        New-Item -ItemType Directory -Force $Out, $BuildDir | Out-Null
        $buildLog = Join-Path $Out 'build.log'
        $buildArgs = @(Get-HarnessBuildArgs $repo $BuildDir)
        if ($LibmpvDir) { $buildArgs += "-p:LibmpvDir=$([IO.Path]::GetFullPath($LibmpvDir))" }
        $hidden = Join-Path $repo 'tools\backend-bench\hidden_run.py'
        $built = Use-HarnessFolderLock $BuildDir ([TimeSpan]::FromMinutes(30)) {
            Write-Host "gate: building this checkout into $BuildDir (log $buildLog)"
            & $Python -B -I $hidden --log $buildLog --timeout 1800 --desktop "ffg-build-$PID" -- $dotnet @buildArgs | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "the gate's build failed (see $buildLog)" }
            Find-HarnessBuiltExe $BuildDir
        }
        if (-not $built) { throw "the build made no Nixie.exe under $BuildDir\bin\FluentFin (see $buildLog)" }
        if (-not (Test-Path (Join-Path (Split-Path $built) 'libmpv\libmpv-2.dll'))) {
            Write-Warning "the gate's build has no libmpv beside it (-LibmpvDir or NIXIE_LIBMPV_DIR): its runs use mpv's controls, not FluentFin's, which the release has"
        }
        if ($DevExe) { throw "-Build and -DevExe: give one" }
        $DevExe = $built
    }
    if (-not $Exe) { if ($DevExe) { $Exe = $DevExe } else { throw "-Exe: the release package's app\Nixie.exe or a Debug build (or -Build)" } }
    $Exe = Assert-GateExe $Exe '-Exe'
    $release = Test-ReleasePackage $Exe
    if ($DevExe) {
        $DevExe = Assert-GateExe $DevExe '-DevExe'
        if (Test-ReleasePackage $DevExe) { throw "-DevExe $DevExe is a release package; a dev check needs a Debug build of the same code (-Build)" }
    }
    elseif (-not $release) { $DevExe = $Exe }
}
catch {
    Write-Host "gate: couldn't run: $($_.Exception.Message)"
    exit 2
}

# --- the caller's shell variables (1.7.5, the review's Minor) --------------------------------------------------------------
# A part selector left in the caller's shell (GATE_SI_PARTS, SS_PARTS, HUD_PARTS, G4_PARTS, NIXIE_MATRIX_ONLY, a fake server's
# FAKESERVER_ shape...) quietly narrowed every job. Every variable the gate's scripts and the fake server read is blanked for
# a job unless its entry's Env or -JobEnv sets it; the machine's and the user's own variables (PATH, TEMP...) and the
# harness's settings (NIXIE_HARNESS_PARALLEL...) stay
$jobEnvPairs = [ordered]@{}
foreach ($pair in @(Split-List $JobEnv)) {
    if ($pair -notmatch '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { Write-Host "gate: couldn't run: -JobEnv '$pair' is not NAME=value"; exit 2 }
    $jobEnvPairs[$Matches[1]] = $Matches[2]
}
$read = New-Object System.Collections.Generic.HashSet[string]([StringComparer]::OrdinalIgnoreCase)
$sources = @(Get-ChildItem (Join-Path $repo 'tools\gate') -Recurse -Filter '*.ps1' -File) + @(Get-ChildItem (Join-Path $repo 'tools\ui-harness') -Filter '*.ps1' -File) +
    @(Get-ChildItem (Join-Path $repo 'tools\ui-harness\offscreen') -Filter '*.ps1' -File) + @(Get-ChildItem (Join-Path $repo 'tools\ui-harness\fakeserver') -Filter '*.py' -File)
foreach ($s in $sources) {
    $text = [IO.File]::ReadAllText($s.FullName)
    foreach ($m in [regex]::Matches($text, '\$env:([A-Za-z_][A-Za-z0-9_]*)')) { [void]$read.Add($m.Groups[1].Value) }
    foreach ($m in [regex]::Matches($text, 'os\.environ(?:\.get)?\s*[\(\[]\s*[''"]([A-Za-z_][A-Za-z0-9_]*)[''"]')) { [void]$read.Add($m.Groups[1].Value) }
}
# only the harness's own kind of name (a part, a shape, a switch of a script or of the app): never Windows' (SystemRoot,
# LOCALAPPDATA... which no registry scope lists either), the harness's settings, or one set for the machine or the user
$selector = '^(GATE|SS|SI|HUD|G\d|RG|UP|SG|TB|IB|CX|CH|CN|QA|FAKESERVER|NIXIE|FLUENTFIN|SEAGLASS)_'
$harnessOwn = '^(NIXIE_HARNESS_|FLUENTFIN_MPV_TEST_PYTHON$)'
$blanked = @($read | Where-Object {
        $n = $_
        $n -match $selector -and $n -notmatch $harnessOwn -and (Test-Path -LiteralPath "Env:$n") -and
        $null -eq [Environment]::GetEnvironmentVariable($n, 'Machine') -and $null -eq [Environment]::GetEnvironmentVariable($n, 'User')
    } | Sort-Object)
if ($blanked.Count) { Write-Host "gate: set in this shell, left out of every job (an entry's Env or -JobEnv gives one): $($blanked -join ', ')" }

# --- the jobs --------------------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $Out, (Join-Path $Out 'runs'), (Join-Path $Out 'data'), (Join-Path $Out 'jobs') | Out-Null
$started = Get-Date
$psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runJob = Join-Path $PSScriptRoot 'run-job.ps1'
$runner = Join-Path $repo 'tools\ui-harness\run-offscreen.ps1'
$queue = New-Object System.Collections.Generic.List[object]
$done = New-Object System.Collections.Generic.List[object]
foreach ($e in $chosen) {
    $jobThemes = if ($Themes.Count) { @($e.ThemeList | Where-Object { $Themes -contains $_ }) } else { $e.ThemeList }
    foreach ($theme in $jobThemes) {
        $queue.Add([pscustomobject]@{ Entry = $e; Theme = $theme; Attempt = 0; Exe = $(if ($e.Dev) { $DevExe } else { $Exe }); Process = $null; Started = $null
                Out = (Join-Path $Out "runs\$($e.Id)\$theme"); Console = (Join-Path $Out "runs\$($e.Id)\$theme.console.txt"); Data = (Join-Path $Out "data\$($e.Id)-$theme")
                Result = $null; Seconds = 0; TimedOut = $false; Expect = (Get-Expect $e $theme); Measured = $measured.ContainsKey("$($e.Id)|$theme") })
    }
}
if (-not $queue.Count) { Write-Host "gate: couldn't run: no check runs in -Themes $($Themes -join ',')"; exit 2 }
# longest first (each job's own measured run time, else a quarter of its limit): the long ones start at once and the short
# ones fill the slots behind them, so the real time is close to the total over the slots
$sorted = @($queue.ToArray() | Sort-Object -Property @{ Expression = { $_.Expect }; Descending = $true }, @{ Expression = { $_.Entry.Id } }, @{ Expression = { $_.Theme } })
$queue.Clear(); foreach ($j in $sorted) { $queue.Add($j) }
# the plan: the jobs dealt longest first to -Parallel slots, the real time the last slot ends (the same as the run does)
$ends = New-Object double[] ([Math]::Max(1, $Parallel))
foreach ($j in $sorted) { $i = [Array]::IndexOf($ends, ($ends | Measure-Object -Minimum).Minimum); $ends[$i] += $j.Expect }
$planMinutes = [Math]::Round(($ends | Measure-Object -Maximum).Maximum / 60.0, 1)
$slotMinutes = [Math]::Round((@($sorted | ForEach-Object { $_.Expect }) | Measure-Object -Sum).Sum / 60.0, 1)
$measuredCount = @($sorted | Where-Object Measured).Count
Write-Host ("gate: {0} jobs ({1} checks), {2} at once; -Exe {3}{4}{5}" -f $queue.Count, $chosen.Count, $Parallel, $Exe,
    $(if ($release) { ' (a release package)' } else { '' }), $(if ($DevExe -and $DevExe -ne $Exe) { "; dev checks on $DevExe" } else { '' }))
if ($release) {
    # 1.7.5 (the review's Minor): say plainly what ran on the package and what on a Debug build
    $devChecks = @($chosen | Where-Object Dev)
    Write-Host ("gate: {0} of {1} checks ({2} of {3} jobs) need the harness's dev hooks and run on a Debug build of this checkout{4}, not on the package; the other {5} run on the package" -f
        $devChecks.Count, $chosen.Count, @($queue.ToArray() | Where-Object { $_.Entry.Dev }).Count, $queue.Count, $(if ($DevExe) { " ($DevExe)" } else { ' (none given: they fail)' }), ($chosen.Count - $devChecks.Count))
}
Write-Host ("gate: plan: {0} slot-minutes ({1} of {2} jobs measured before), about {3} min real time on {4} slots, the longest job {5} min; budget {6} min{7}" -f
    $slotMinutes, $measuredCount, $sorted.Count, $planMinutes, $Parallel, [Math]::Round($sorted[0].Expect / 60.0, 1), $Budget,
    $(if ($planMinutes -gt $Budget) { ": OVER by $([Math]::Round($planMinutes - $Budget, 1)) min" } else { '' }))
# the machine's slots free now (each taken and given back at once): runs of other checkouts or the TV's emulator share them
$freeSlots = 0
foreach ($i in 1..(Get-HarnessRunSlots)) {
    $m = New-Object System.Threading.Mutex($false, (Get-HarnessRunSlotName $i))
    try { $got = $false; try { $got = $m.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $got = $true }; if ($got) { $freeSlots++; $m.ReleaseMutex() } }
    finally { $m.Dispose() }
}
if ($freeSlots -lt $Parallel) { Write-Warning "only $freeSlots of the machine's $(Get-HarnessRunSlots) off-screen slots are free now (other runs hold them): the gate's real time grows with the wait" }

# the job's file for run-job.ps1, and its PowerShell started hidden (no window: CreateNoWindow; its console is its transcript)
function Start-GateJob($Job) {
    $e = $Job.Entry
    $Job.Attempt++
    $runOut = $Job.Out; $data = $Job.Data; $console = $Job.Console
    foreach ($d in @($runOut, $data)) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    New-Item -ItemType Directory -Force (Split-Path $runOut) | Out-Null
    foreach ($f in @($console, "$console.error.txt")) { if (Test-Path -LiteralPath $f) { Remove-Item -LiteralPath $f -Force } }
    # 'gate' passed as its sizes (the 90-minute budget: the smallest window and the largest), so a check that reads GATE_SIZES
    # itself gets the same list as gate-common.ps1's Get-GateSizes
    $envs = [ordered]@{}
    foreach ($n in $blanked) { $envs[$n] = '' }   # run-job.ps1: an empty value removes the variable from the job
    $envs.NIXIE_LAYOUT = $Job.Theme; $envs.GATE_ID = $e.Id; $envs.GATE_THEME = $Job.Theme; $envs.GATE_OUT = $runOut
    $envs.GATE_SIZES = $(if ($e.Sizes -eq 'gate') { '1280x720,max' } else { $e.Sizes })
    foreach ($k in $e.Env.Keys) { $envs[$k] = [string]$e.Env[$k] }
    foreach ($k in $jobEnvPairs.Keys) { $envs[$k] = $jobEnvPairs[$k] }
    if (-not $Job.Exe) {
        $Job.Process = $null
        $Job.Result = [pscustomobject]@{ ok = $false; reason = 'needs a dev build of the same code (-DevExe or -Build): -Exe is a release package, which never listens to the harness''s dev hooks'; passed = 0; checks = 0; exit = $null; env_events = 0 }
        return
    }
    if ($e.Kind -eq 'then') {
        $args_ = [ordered]@{ Exe = $Job.Exe; DataDir = $data; Out = $runOut; Script = $e.Steps; Then = (Join-Path $repo $e.Then); SignIn = $e.SignIn
            Python = $Python; Backend = $Backend; Cache = $Cache; TimeoutSeconds = $e.Minutes * 60; QueueMinutes = $QueueMinutes; NoA11y = $true }
        if ($e.Before) { $args_.Before = Join-Path $repo $e.Before }
        if ($e.Settings.Count) { $args_.Setting = @($e.Settings) }
        foreach ($k in $e.RunArgs.Keys) { $args_[$k] = $e.RunArgs[$k] }
        $script = $runner
    }
    else {
        $args_ = [ordered]@{ Out = $runOut; Exe = $Job.Exe; Python = $Python; Cache = $Cache; Backend = $Backend; Theme = $Job.Theme }
        foreach ($k in $e.Args.Keys) { $args_[$k] = $e.Args[$k] }
        $script = Join-Path $repo $e.Script
    }
    $jobFile = Join-Path $Out ("jobs\{0}-{1}.json" -f $e.Id, $Job.Theme)
    $spec = [ordered]@{ script = $script; args = $args_; env = $envs; console = $console }
    [IO.File]::WriteAllText($jobFile, ($spec | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    $si = New-Object System.Diagnostics.ProcessStartInfo
    $si.FileName = $psExe
    $si.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$runJob`" -JobFile `"$jobFile`""
    $si.UseShellExecute = $false
    $si.CreateNoWindow = $true
    $si.WorkingDirectory = $Out
    $Job.Process = [System.Diagnostics.Process]::Start($si)
    $null = $Job.Process.Handle   # held: the wait and any end are on this process, never on a pid
    $Job.Started = Get-Date
    Write-Host ("  start {0} [{1}]{2}" -f $e.Id, $Job.Theme, $(if ($Job.Attempt -gt 1) { " (attempt $($Job.Attempt))" }))
}

function Get-OneLine([string]$Text, [int]$Max = 300) { $t = ($Text -replace '\s+', ' ').Trim(); if ($t.Length -gt $Max) { $t.Substring(0, $Max) + '...' } else { $t } }

# what the job left: pass only with exit 0, a result.json, at least one check, and every check passing
function Read-GateJob($Job) {
    $e = $Job.Entry
    $code = $null
    try { $code = $Job.Process.ExitCode } catch { $code = $null }
    $resultFile = Join-Path $Job.Out 'result.json'
    $r = $null
    if (Test-Path -LiteralPath $resultFile) { try { $r = Get-Content -LiteralPath $resultFile -Raw -Encoding utf8 | ConvertFrom-Json } catch { $r = $null } }
    $all = if ($r -and $r.checks) { @($r.checks.PSObject.Properties) } else { @() }
    $failed = @($all | Where-Object { -not $_.Value.ok })
    # (1.7.5 gate fix) and the monitors that dropped off the bus during the run (drive.ps1's Kernel-PnP removals): the
    # release verify's menus [DriveIn] lost its player buttons' bounds as a monitor went at 23:30:21, no window minimized
    $envEvents = if ($r -and $r.display) { @($r.display.events | Where-Object { $_ }).Count + @($r.display.removals | Where-Object { $_ }).Count } else { 0 }
    $ok = ($code -eq 0) -and $r -and $all.Count -gt 0 -and $failed.Count -eq 0
    $reason = ''
    if ($failed.Count) {
        $f = $failed[0]
        $n = $f.Name; if ($n.StartsWith("$($e.Id): ")) { $n = $n.Substring($e.Id.Length + 2) }
        $reason = Get-OneLine ("{0}{1}{2}" -f $n, $(if ($f.Value.detail) { ": $($f.Value.detail)" }), $(if ($failed.Count -gt 1) { " (+$($failed.Count - 1) more)" }))
    }
    elseif (-not $r) {
        $err = "$($Job.Console).error.txt"
        $reason = if (Test-Path -LiteralPath $err) { Get-OneLine (Get-Content -LiteralPath $err -Raw) } elseif ($Job.TimedOut) { "ran past its time ($($e.Minutes) min + the queue's $QueueMinutes)" } else { "no result.json (exit $code; see $($Job.Console))" }
    }
    elseif (-not $all.Count) { $reason = "no check ran (exit $code; see $($Job.Out)\drive.log)" }
    elseif ($code -ne 0) { $reason = "exit $code with every check passing (see $($Job.Console))" }
    # the run's own time, without its wait for a slot: drive.ps1's start (result.json's started, epoch s) to its result
    $ran = $null
    if ($r -and $r.started) { try { $ran = [int]((Get-Item -LiteralPath $resultFile).LastWriteTimeUtc - [DateTimeOffset]::FromUnixTimeMilliseconds([long]([double]$r.started * 1000)).UtcDateTime).TotalSeconds } catch { $ran = $null } }
    [pscustomobject]@{ ok = [bool]$ok; reason = $reason; passed = $all.Count - $failed.Count; checks = $all.Count; exit = $code; env_events = $envEvents; ran = $ran }
}

$running = New-Object System.Collections.Generic.List[object]
while ($queue.Count -or $running.Count) {
    while ($queue.Count -and $running.Count -lt $Parallel) {
        $j = $queue[0]; $queue.RemoveAt(0)
        Start-GateJob $j
        if ($j.Process) { $running.Add($j) } else { $j.Seconds = 0; $done.Add($j); Write-Host ("FAIL {0} [{1}]: {2}" -f $j.Entry.Id, $j.Theme, $j.Result.reason) }
    }
    Start-Sleep -Seconds 2
    # ToArray: Windows PowerShell's @() over a List[object] of these objects throws "Argument types do not match"
    foreach ($j in $running.ToArray()) {
        if (-not $j.Process.HasExited) {
            # a job past its time (its queue, its run and a margin): its PowerShell ended (ours, by its Process object); the
            # run's hidden_run.py has its own --timeout and kill-on-close job for what the run started
            if (((Get-Date) - $j.Started).TotalMinutes -gt ($QueueMinutes + $j.Entry.Minutes + 10)) {
                $j.TimedOut = $true
                try { $j.Process.Kill() } catch {}
                [void]$j.Process.WaitForExit(10000)
            }
            else { continue }
        }
        [void]$running.Remove($j)
        $j.Seconds = [int]((Get-Date) - $j.Started).TotalSeconds
        $j.Result = Read-GateJob $j
        if (-not $j.Result.ok -and $j.Result.env_events -gt 0 -and $j.Attempt -le $Retry) {
            Write-Host ("  again {0} [{1}]: a monitor dropped off or Windows minimized its window during the run, and it failed: {2}" -f $j.Entry.Id, $j.Theme, $j.Result.reason)
            $queue.Insert(0, $j)
            continue
        }
        $done.Add($j)
        Write-Host ("{0} {1} [{2}] ({3}/{4}, {5}{6} s){7}" -f $(if ($j.Result.ok) { 'PASS' } else { 'FAIL' }), $j.Entry.Id, $j.Theme, $j.Result.passed, $j.Result.checks,
            $(if ($null -ne $j.Result.ran) { "ran $($j.Result.ran) s of " } else { '' }), $j.Seconds, $(if (-not $j.Result.ok) { ": $($j.Result.reason)" }))
    }
}

# --- the report ------------------------------------------------------------------------------------------------------------
$minutes = [Math]::Round(((Get-Date) - $started).TotalMinutes, 1)
$jobs = @($done | ForEach-Object {
        [ordered]@{ id = $_.Entry.Id; class = $_.Entry.Class; owner = $_.Entry.Owner; theme = $_.Theme; dev = $_.Entry.Dev; exe = $_.Exe; ok = $_.Result.ok; reason = $_.Result.reason
            passed = $_.Result.passed; checks = $_.Result.checks; seconds = $_.Seconds; ran_seconds = $_.Result.ran; exit = $_.Result.exit; attempts = $_.Attempt; out = $_.Out }
    })
# the runs' own time (no slot wait): what the gate takes on a quiet machine with -Parallel slots, about
$ranTotal = (@($jobs | ForEach-Object { if ($_.ran_seconds) { $_.ran_seconds } else { 0 } }) | Measure-Object -Sum).Sum
$report = [ordered]@{ exe = $Exe; dev_exe = $DevExe; release_package = $release; backend = $Backend; started = $started.ToString('o'); minutes = $minutes
    run_minutes = [Math]::Round($ranTotal / 60.0, 1); planned_minutes = $planMinutes; budget = $Budget; slots_free_at_start = $freeSlots
    slots = (Get-HarnessRunSlots); parallel = $Parallel; jobs = $jobs }
[IO.File]::WriteAllText((Join-Path $Out 'gate.json'), ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

$failedJobs = @($jobs | Where-Object { -not $_.ok })
$md = New-Object System.Collections.Generic.List[string]
$md.Add('# The basics gate')
$md.Add('')
$md.Add("Run $($started.ToString('yyyy-MM-dd HH:mm')), $minutes min, $Parallel at once; -Exe $Exe$(if ($release) { ' (a release package)' })$(if ($DevExe -and $DevExe -ne $Exe) { "; dev checks on $DevExe" }).")
$md.Add('')
$md.Add('| Check | Class | Owner | ' + ($AllThemes -join ' | ') + ' |')
$md.Add('|---|---|---|' + (($AllThemes | ForEach-Object { '---' }) -join '|') + '|')
foreach ($e in $chosen) {
    $cells = foreach ($t in $AllThemes) { $j = $jobs | Where-Object { $_.id -eq $e.Id -and $_.theme -eq $t } | Select-Object -First 1; if (-not $j) { '-' } elseif ($j.ok) { 'PASS' } else { '**FAIL**' } }
    $md.Add(('| {0} | {1} | {2} | {3} |' -f $e.Id, $e.Class, $e.Owner, ($cells -join ' | ')))
}
if ($failedJobs.Count) {
    $md.Add(''); $md.Add('## Failures'); $md.Add('')
    foreach ($j in $failedJobs) { $md.Add("- $($j.id) [$($j.theme)]: $($j.reason)") }
}
[IO.File]::WriteAllLines((Join-Path $Out 'gate.md'), [string[]]$md, [Text.UTF8Encoding]::new($false))

Write-Host ''
Write-Host ("gate: {0}/{1} jobs passed; real time {2} min (planned {3}, budget {4}); the runs themselves {5} slot-minutes ({6}\gate.md)" -f ($jobs.Count - $failedJobs.Count), $jobs.Count, $minutes,
    $planMinutes, $Budget, $report.run_minutes, $Out)
# -SaveTimings: this run's own run times into tools\gate\timings.json (the next run's order and plan), kept beside the others
if ($SaveTimings) {
    $keep = [ordered]@{}
    if (Test-Path -LiteralPath $timingsFile) { foreach ($p in (Get-Content -LiteralPath $timingsFile -Raw -Encoding utf8 | ConvertFrom-Json).PSObject.Properties) { $keep[$p.Name] = $p.Value } }
    foreach ($j in $jobs) { if ($j.ran_seconds) { $keep["$($j.id)|$($j.theme)"] = $j.ran_seconds } }
    $sortedKeep = [ordered]@{}; foreach ($k in @($keep.Keys | Sort-Object)) { $sortedKeep[$k] = $keep[$k] }
    [IO.File]::WriteAllText($timingsFile, ($sortedKeep | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "gate: run times saved in $timingsFile"
}
foreach ($j in $failedJobs) { Write-Host ("FAIL {0} [{1}]: {2}" -f $j.id, $j.theme, $j.reason) }
if ($failedJobs.Count) { exit 1 }
exit 0
