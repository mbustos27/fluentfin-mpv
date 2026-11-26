<#
  Tests of the basics gate's runner (tools\gate\run-gate.ps1) with stand-in checks (tests\fixture-check.ps1) that start
  nothing but PowerShell: no app, no off-screen slot. And that offscreen\drive.ps1's end-of-run close checks the exit code
  (1.7.4's Left: a crash at a run's last close passed).

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\gate\test_gate.ps1 -Out <scratch outside the repo>

  Prints PASS/FAIL per test; exit code 1 when any failed.
#>
param(
    [Parameter(Mandatory)][string]$Out,
    # the runner wants a Python that exists (the fixture never runs it): FLUENTFIN_MPV_TEST_PYTHON, as the UI suites take it,
    # else this checkout's backend venv
    [string]$Python = $(if ($env:FLUENTFIN_MPV_TEST_PYTHON) { $env:FLUENTFIN_MPV_TEST_PYTHON } else { Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))) 'backend\.venv\Scripts\python.exe' }),
    # another copy of offscreen\drive.ps1 for tests 7, 13, 14 and 27 (a revert check: main's before the fix)
    [string]$Drive,
    # (1.7.5 gate fix) other copies of then-174-back.ps1, checks\g3-common.ps1 and checks\4-topbar.ps1 for tests 17-20 (a revert
    # check: 9d423e5e's)
    [string]$BackScript,
    [string]$G3Common,
    [string]$Topbar,
    # (1.7.5 gate fix) another copy of checks\6-playback.ps1 for test 25 (a revert check)
    [string]$PlaybackScript,
    # (1.8.1, the mem review) another copy of offscreen\then-175-leak.ps1 for test 26 (a revert check)
    [string]$LeakScript
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
if ($Out.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be outside the repository" }
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force $Out | Out-Null
$gate = Join-Path $PSScriptRoot 'run-gate.ps1'
$ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$failures = 0
function T([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if (-not $Ok) { $script:failures++ }
    Write-Host ("{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" }))
}
# run-gate.ps1 in its own PowerShell: its exit code and what it printed
function Invoke-Gate([string[]]$GateArgs) {
    $text = & $ps -NoProfile -ExecutionPolicy Bypass -File $gate @GateArgs 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text }
}
function Write-Manifest([string]$Name, [string]$Body) {
    $dir = Join-Path $Out "manifests-$Name"
    New-Item -ItemType Directory -Force $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'x.psd1'), $Body, [Text.UTF8Encoding]::new($false))
    $dir
}
$fixture = "Kind = 'script'; Script = 'tools\gate\tests\fixture-check.ps1'; Owner = 'test'; What = 'a stand-in'; Sizes = 'none'; Minutes = 2; Class = 1"
# a stand-in "exe": the runner only checks that it exists (the fixture never starts it); a dev build has no release marker
$devDir = Join-Path $Out 'dev'; $relDir = Join-Path $Out 'release'
New-Item -ItemType Directory -Force $devDir, $relDir | Out-Null
foreach ($d in $devDir, $relDir) { [IO.File]::WriteAllText((Join-Path $d 'Nixie.exe'), 'not an exe') }
[IO.File]::WriteAllText((Join-Path $devDir 'Nixie.dll'), 'a dev build')
[IO.File]::WriteAllText((Join-Path $relDir 'Nixie.dll'), 'metadata FluentFinMpvRelease true')

# 1. every owner's manifest in the repository parses
$r = Invoke-Gate @('-List')
T 'the repository''s manifests parse (-List exits 0)' ($r.Code -eq 0) ($r.Text.Trim() -split "`n" | Select-Object -First 1)

# 2. a manifest's mistakes are refused with exit 2, each named
$noClass = $fixture -replace '; Class = 1$', ''
$bad = Write-Manifest 'bad' "@{ Checks = @( @{ Id = 'one'; $fixture; Colour = 'red' }, @{ Id = 'one'; $fixture }, @{ Id = 'Two_'; $noClass; Class = 9; Themes = @('Neon') } ) }"
$r = Invoke-Gate @('-List', '-ManifestDir', $bad)
T 'a bad manifest: exit 2' ($r.Code -eq 2) "exit $($r.Code)"
foreach ($want in @("unknown key 'Colour'", "Id 'one' is used twice", "Id 'Two_' is not", 'Class must be 1 to 8', "theme 'Neon'")) {
    T "a bad manifest: says $want" ($r.Text -like "*$want*")
}

# 3. a run: two checks in two themes, one failing; the report and the exit code
$two = Write-Manifest 'run' "@{ Checks = @( @{ Id = 'fixture-pass'; $fixture; Themes = @('Classic', 'Cinematic') }, @{ Id = 'fixture-fail'; $fixture; Themes = @('Classic', 'Cinematic'); Args = @{ Fail = `$true } } ) }"
$runOut = Join-Path $Out 'run'
$r = Invoke-Gate @('-ManifestDir', $two, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', $runOut, '-Python', $Python, '-Parallel', '3')
T 'a failing check: the gate exits 1' ($r.Code -eq 1) "exit $($r.Code)"
$report = if (Test-Path (Join-Path $runOut 'gate.json')) { Get-Content (Join-Path $runOut 'gate.json') -Raw | ConvertFrom-Json }
$jobs = @($report.jobs)
T 'gate.json: four jobs, two passed' ($jobs.Count -eq 4 -and @($jobs | Where-Object ok).Count -eq 2) "$($jobs.Count) jobs"
$line = ($r.Text -split "`r?`n" | Where-Object { $_ -like 'FAIL fixture-fail `[Classic`]:*' } | Select-Object -Last 1)
T 'the failure''s one line: the first failed check without the id, its detail, and how many more' ($line -eq 'FAIL fixture-fail [Classic]: the second thing shows: the fixture failed on purpose (+1 more)') "'$line'"
$pass = $jobs | Where-Object { $_.id -eq 'fixture-pass' -and $_.theme -eq 'Cinematic' } | Select-Object -First 1
$first = if ($pass) { (Get-Content (Join-Path $pass.out 'result.json') -Raw | ConvertFrom-Json).checks.'fixture-pass: the first thing shows'.detail }
T 'a job gets its theme (NIXIE_LAYOUT) and GATE_SIZES' ($first -eq 'theme Cinematic, layout Cinematic, sizes none') "'$first'"
T 'gate.md: a row per check' ((Test-Path (Join-Path $runOut 'gate.md')) -and ((Get-Content (Join-Path $runOut 'gate.md') -Raw) -match '\| fixture-fail \| 1 \| test \| \*\*FAIL\*\* \| - \| \*\*FAIL\*\* \| - \|'))

# 4. -Checks and -Themes narrow the run; all passing exits 0
$r = Invoke-Gate @('-ManifestDir', $two, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'narrow'), '-Python', $Python, '-Checks', 'fixture-pass', '-Themes', 'Cinematic')
$jobs = @((Get-Content (Join-Path $Out 'narrow\gate.json') -Raw | ConvertFrom-Json).jobs)
T 'only passing jobs: exit 0, one job' ($r.Code -eq 0 -and $jobs.Count -eq 1 -and $jobs[0].theme -eq 'Cinematic') "exit $($r.Code), $($jobs.Count) jobs"

# 5. no result.json, or a script that throws, fails with a reason, never passes
$odd = Write-Manifest 'odd' "@{ Checks = @( @{ Id = 'fixture-none'; $fixture; Themes = @('Classic'); Args = @{ NoResult = `$true } }, @{ Id = 'fixture-throw'; $fixture; Themes = @('Classic'); Args = @{ Throw = `$true } } ) }"
$r = Invoke-Gate @('-ManifestDir', $odd, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'odd'), '-Python', $Python)
T 'a check that left no result.json fails, exit 0 or not' ($r.Code -eq 1 -and $r.Text -match 'FAIL fixture-none \[Classic\]: no result\.json') ''
T 'a check that threw fails with its message' ($r.Text -match 'FAIL fixture-throw \[Classic\]: the fixture threw on purpose \(Classic\)')

# 6. a dev check with a release package and no dev build: fails, never skipped
$dev = Write-Manifest 'dev' "@{ Checks = @( @{ Id = 'fixture-dev'; $fixture; Themes = @('Classic'); Dev = `$true }, @{ Id = 'fixture-any'; $fixture; Themes = @('Classic') } ) }"
$r = Invoke-Gate @('-ManifestDir', $dev, '-Exe', (Join-Path $relDir 'Nixie.exe'), '-Out', (Join-Path $Out 'dev-run'), '-Python', $Python)
T 'a release -Exe: a dev check fails (needs a dev build), the others run on it' ($r.Code -eq 1 -and $r.Text -match 'FAIL fixture-dev \[Classic\]: needs a dev build' -and $r.Text -match 'PASS fixture-any \[Classic\]') "exit $($r.Code)"
$r = Invoke-Gate @('-ManifestDir', $dev, '-Exe', (Join-Path $relDir 'Nixie.exe'), '-DevExe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'dev-run2'), '-Python', $Python)
$devJob = @((Get-Content (Join-Path $Out 'dev-run2\gate.json') -Raw | ConvertFrom-Json).jobs) | Where-Object { $_.id -eq 'fixture-dev' }
T 'with -DevExe the dev check runs on it' ($r.Code -eq 0 -and $devJob.ok) "exit $($r.Code)"
$r = Invoke-Gate @('-ManifestDir', $dev, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-DevExe', (Join-Path $relDir 'Nixie.exe'), '-Out', (Join-Path $Out 'dev-run3'), '-Python', $Python)
T 'a release package as -DevExe is refused (exit 2)' ($r.Code -eq 2) "exit $($r.Code)"

# 7. drive.ps1: a run's last close (no close step) checks the exit code as Step-Close does (1.7.4's Left)
$tokens = $null; $errors = $null
if (-not $Drive) { $Drive = Join-Path $repo 'tools\ui-harness\offscreen\drive.ps1' }
$ast = [System.Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($Drive), [ref]$tokens, [ref]$errors)
$try = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.TryStatementAst] -and $n.Finally -and $n.Finally.Extent.Text -match 'Close-Tracked' }, $true) | Select-Object -First 1
$fin = if ($try) { $try.Finally.Extent.Text } else { '' }
$closeAt = $fin.IndexOf('[OffscreenUi]::Close(')
$checkAt = if ($closeAt -ge 0) { $fin.IndexOf('ExitCode', $closeAt) } else { -1 }
$named = $fin -match "Check 'end of run: [^']*exit code 0"
T 'drive.ps1: the end-of-run close checks the exit code (a crash there fails the run)' ($closeAt -ge 0 -and $checkAt -gt $closeAt -and $named) "close at $closeAt, ExitCode at $checkAt"

# 8. the gate's own 'then' scripts (tools\gate\checks\, Kind then in a manifest) never assign one of drive.ps1's parameters at
# their top level: offscreen\test_then_names.ps1 over copies of them named as it looks for them (then-*.ps1, beside drive.ps1)
$names = Join-Path $Out 'then-names'
New-Item -ItemType Directory -Force $names | Out-Null
Copy-Item (Join-Path $repo 'tools\ui-harness\offscreen\drive.ps1') $names
$thens = @(Get-ChildItem (Join-Path $PSScriptRoot 'manifest') -Filter '*.psd1' | ForEach-Object { @((Import-PowerShellDataFile $_.FullName).Checks) } |
        Where-Object { $_.Kind -ne 'script' -and "$($_.Then)" -like 'tools\gate\checks\*' } | ForEach-Object { $_.Then } | Sort-Object -Unique)
foreach ($t in $thens) { Copy-Item (Join-Path $repo $t) (Join-Path $names ('then-gate-' + [IO.Path]::GetFileName($t))) }
$text = & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\ui-harness\offscreen\test_then_names.ps1') -Dir $names 2>&1 | Out-String
T "the gate's then scripts ($($thens.Count)) assign none of drive.ps1's parameters" ($LASTEXITCODE -eq 0) (($text -split "`r?`n" | Where-Object { $_ -match 'FAIL' }) -join '; ')

# 9. verify-package.ps1 runs the gate by default (a failing gate fails the release build): 'Gate' in -Runs' default, its
# branch calls run-gate.ps1 on the package's exe, and each failed job is a failed check
$tokens = $null; $errors = $null
$vp = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools\release\verify-package.ps1'), [ref]$tokens, [ref]$errors)
$runsParam = $vp.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Runs' }
$default = if ($runsParam -and $runsParam.DefaultValue) { $runsParam.DefaultValue.Extent.Text } else { '' }
$branch = $vp.FindAll({ param($n) $n -is [System.Management.Automation.Language.SwitchStatementAst] }, $true) | ForEach-Object { $_.Clauses } |
    Where-Object { $_.Item1.Extent.Text -eq "'Gate'" } | Select-Object -First 1
$body = if ($branch) { $branch.Item2.Extent.Text } else { '' }
T 'verify-package: the gate is in the default runs' ($default -match "'Gate'") $default
$slotsParam = $vp.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'GateSlots' }
T 'verify-package: the Gate run takes 5 slots when the machine is quiet, else 3 (through NIXIE_HARNESS_PARALLEL)' (
    $slotsParam -and $slotsParam.DefaultValue.Extent.Text -eq '5' -and $body -match "NIXIE_HARNESS_PARALLEL = if \(\`$quiet\) \{ \[string\]\`$GateSlots \} else \{ '3' \}") ''
$budgetParam = (& { $t = $null; $e = $null; [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'run-gate.ps1'), [ref]$t, [ref]$e) }).ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'Budget' }
T 'the gate''s budget is 2 h by default' ($budgetParam -and $budgetParam.DefaultValue.Extent.Text -eq '120') "$($budgetParam.DefaultValue.Extent.Text)"
T 'verify-package: the Gate run calls run-gate.ps1 on the package''s exe and fails a check per failed job' (
    $body -match "tools\\gate\\run-gate\.ps1" -and $body -match "'-Exe', \`$exe" -and $body -match 'foreach \(\$j in \$gateFailed\) \{ Check ' -and $body -match '\$gateCode -eq 0') ''

# 10. longest first: measured run times (-Timings, an earlier gate.json's ran_seconds) order the jobs, a job never measured
# counts a quarter of its Minutes; the plan line gives the real time on the slots and the budget
$order = Write-Manifest 'order' "@{ Checks = @( @{ Id = 'fixture-short'; $noClass; Class = 1; Themes = @('Classic') }, @{ Id = 'fixture-long'; $noClass; Class = 1; Themes = @('Classic', 'Cinematic') }, @{ Id = 'fixture-guess'; $($noClass -replace 'Minutes = 2', 'Minutes = 9'); Class = 1; Themes = @('Classic') } ) }"
$earlier = Join-Path $Out 'earlier-gate.json'
[IO.File]::WriteAllText($earlier, '{ "jobs": [ { "id": "fixture-long", "theme": "Classic", "ran_seconds": 300 }, { "id": "fixture-long", "theme": "Cinematic", "ran_seconds": 200 }, { "id": "fixture-short", "theme": "Classic", "ran_seconds": 20 } ] }')
$r = Invoke-Gate @('-ManifestDir', $order, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'order-run'), '-Python', $Python, '-Parallel', '1', '-Timings', $earlier, '-Budget', '5')
$starts = @($r.Text -split "`r?`n" | Where-Object { $_ -match '^\s+start (\S+) \[(\w+)\]' } | ForEach-Object { if ($_ -match '^\s+start (\S+) \[(\w+)\]') { "$($Matches[1])/$($Matches[2])" } })
T 'longest first: measured 300 s, 200 s, a guess of 9 min / 4 = 135 s, measured 20 s' (($starts -join ',') -eq 'fixture-long/Classic,fixture-long/Cinematic,fixture-guess/Classic,fixture-short/Classic') ($starts -join ',')
$plan = ($r.Text -split "`r?`n" | Where-Object { $_ -like 'gate: plan:*' } | Select-Object -First 1)
T 'the plan: slot-minutes, how many measured, the real time on the slots, over the budget' ($plan -match '10\.9 slot-minutes \(3 of 4 jobs measured before\), about 10\.9 min real time on 1 slots, the longest job 5 min; budget 5 min: OVER by 5\.9 min') $plan
$plan3 = Invoke-Gate @('-ManifestDir', $order, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'order-run3'), '-Python', $Python, '-Parallel', '3', '-Timings', $earlier)
$plan3 = ($plan3.Text -split "`r?`n" | Where-Object { $_ -like 'gate: plan:*' } | Select-Object -First 1)
T 'the plan on 3 slots: the longest job sets the real time (5 min)' ($plan3 -match 'about 5 min real time on 3 slots') $plan3
$final = ($r.Text -split "`r?`n" | Where-Object { $_ -like 'gate: * jobs passed; real time*' } | Select-Object -First 1)
T 'the end line gives the real time beside the plan and the budget' ($final -match 'real time [\d.]+ min \(planned 10\.9, budget 5\)') $final

# 11. a Tier = 'nightly' entry: left out of a release run, in with -Nightly or by name
$tier = Write-Manifest 'tier' "@{ Checks = @( @{ Id = 'fixture-every'; $fixture; Themes = @('Classic') }, @{ Id = 'fixture-night'; $fixture; Themes = @('Classic'); Tier = 'nightly' } ) }"
$a = Invoke-Gate @('-ManifestDir', $tier, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'tier-a'), '-Python', $Python)
$b = Invoke-Gate @('-ManifestDir', $tier, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'tier-b'), '-Python', $Python, '-Nightly')
$c = Invoke-Gate @('-ManifestDir', $tier, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'tier-c'), '-Python', $Python, '-Checks', 'fixture-night')
T 'a nightly entry: not in a release run, in with -Nightly, and by name' ($a.Text -notmatch 'fixture-night' -and $a.Text -match 'PASS fixture-every' -and $b.Text -match 'PASS fixture-night' -and $c.Text -match 'PASS fixture-night') ''
$bad2 = Write-Manifest 'badtier' "@{ Checks = @( @{ Id = 'fixture-x'; $fixture; Tier = 'weekly' } ) }"
T 'a Tier other than release or nightly is refused' ((Invoke-Gate @('-List', '-ManifestDir', $bad2)).Text -match "Tier must be")

# 12. (the review's Minor) a part selector left in the caller's shell never reaches a job; -JobEnv gives one on purpose
$parts = Write-Manifest 'parts' "@{ Checks = @( @{ Id = 'fixture-parts'; $fixture; Themes = @('Classic') } ) }"
$env:SS_PARTS = 'bs6'
try {
    $null = Invoke-Gate @('-ManifestDir', $parts, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'parts-a'), '-Python', $Python)
    $null = Invoke-Gate @('-ManifestDir', $parts, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'parts-b'), '-Python', $Python, '-JobEnv', 'SS_PARTS=bsN')
}
finally { Remove-Item Env:SS_PARTS -ErrorAction SilentlyContinue }
$partsRuns = foreach ($d in 'parts-a', 'parts-b') { (Get-Content (Join-Path $Out "$d\runs\fixture-parts\Classic\result.json") -Raw | ConvertFrom-Json).checks }
$seen = @($partsRuns | ForEach-Object { $_.'fixture-parts: the parts it was given'.detail })
T 'a part selector set in the caller''s shell is left out of the jobs; -JobEnv gives one' ($seen[0] -eq "parts ''" -and $seen[1] -eq "parts 'bsN'") ($seen -join ' / ')
T 'Windows'' own variables (SystemRoot, LOCALAPPDATA, USERPROFILE) still reach every job' (@($partsRuns | Where-Object { $_."fixture-parts: Windows' own variables reach it".ok }).Count -eq 2) ''

# 13. (the review's Minor) drive.ps1's Check: a later PASS of a name never replaces its FAIL (the function itself, run here)
$dAst = [System.Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($Drive), [ref]$tokens, [ref]$errors)
$checkFn = $dAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Check' }, $true) | Select-Object -First 1
$verdict = & {
    $checks = [ordered]@{}
    function Say([string]$Text) { }
    . ([scriptblock]::Create($checkFn.Extent.Text))
    Check 'the same check' $false 'it failed first'
    Check 'the same check' $true 'then passed'
    $checks['the same check']
}
T 'drive.ps1: a later PASS never replaces a FAIL' ($verdict -and -not $verdict.ok) "$($verdict.detail)"

# 14. (the review's Minor) drive.ps1: Nixie ending before the end of the run without the close step fails it
$finText = ($dAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.TryStatementAst] -and $n.Finally -and $n.Finally.Extent.Text -match 'Close-Tracked' }, $true) | Select-Object -First 1).Finally.Extent.Text
$stepClose = ($dAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Step-Close' }, $true) | Select-Object -First 1).Extent.Text
T 'drive.ps1: an exit without the close step fails the run (Step-Close marks its own; Restart to update says so)' (
    $stepClose -match '\$script:closedByStep = \$script:ui' -and $finText -match "HasExited -and -not \[object\]::ReferenceEquals\(\`$script:closedByStep, \`$script:ui\) -and -not \`$script:exitExpected" -and
    $finText -match "Check 'end of run: Nixie was still running \(it ended without the close step\)' \`$false" -and
    (Get-Content -Raw (Join-Path $repo 'tools\ui-harness\offscreen\then-update.ps1')) -match '\$script:exitExpected = \$true') ''

# 15. (the review's Minor) 3-menus: the film page's More options is required for someone who isn't an administrator (Media info)
$menus = Get-Content -Raw (Join-Path $repo 'tools\gate\checks\3-menus.ps1')
T '3-menus: a non-admin''s film page must show More options (Media info is there for everyone)' (
    $menus -match "Test-G2PageMore 'not an administrator, movie page' [^\r\n]*-HasEntries" -and $menus -match '\$want = \$g2Admin -or \$HasEntries; Check "\$\{g2Id\} \[\$Page\]: More options shows" \(-not \$want\)') ''

# 16. (the review's Minor) verify-package refuses to leave the gate out without -NoGate, and checks the package's commit
$vpCode = 0
$vpText = & {
    $ErrorActionPreference = 'Continue'   # the refusal comes on stderr
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\release\verify-package.ps1') -Zip (Join-Path $Out 'no.zip') -Out (Join-Path $Out 'vp') -Runs Default 2>&1 | Out-String
    $script:vpCode = $LASTEXITCODE
}
$vpFlat = ($vpText -replace '\s+', ' ')
T 'verify-package: -Runs without Gate is refused unless -NoGate' ($vpCode -ne 0 -and $vpFlat -match 'leaves out the basics gate \(Gate\)' -and $vpFlat -match 'pass -NoGate') "exit $vpCode"
T 'verify-package: the Gate run checks the checkout is the package''s commit (SOURCES.txt) and says what ran on the Debug build' (
    $body -match 'git archive of commit \(\[0-9a-f\]\{40\}\)' -and $body -match "Check 'Gate: this checkout is the package''s commit" -and $body -match 'ran on a Debug build of this checkout') ''

# 17-20. (1.7.5 gate fix: the release verify's failed cells that were the checks' own) each check's helper, run here
if (-not $BackScript) { $BackScript = Join-Path $repo 'tools\ui-harness\offscreen\then-174-back.ps1' }
if (-not $G3Common) { $G3Common = Join-Path $repo 'tools\gate\checks\g3-common.ps1' }
if (-not $Topbar) { $Topbar = Join-Path $repo 'tools\gate\checks\4-topbar.ps1' }
# a script's function by name, as its text ('' when it has none)
function Get-FunctionText([string]$File, [string]$Name) {
    $t = $null; $e = $null
    $a = [System.Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($File), [ref]$t, [ref]$e)
    $f = $a.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true) | Select-Object -First 1
    if ($f) { $f.Extent.Text } else { '' }
}
$backText = Get-Content -Raw $BackScript
$topbarText = Get-Content -Raw $Topbar

# 17. back's signout: the sign-out's "[back] ... let go" line as 1.7.5 logs it ("the shell went": Release comes first and
# Forgetting finds none kept); the verify run's four signout cells failed on a filter that wanted "sign" in it
$fn = Get-FunctionText $BackScript 'Bk-SignOutLetGo'
$soLines = @('2026-11-24 23:47:53.425 -08:00 [INF] [back] 4 kept page(s) let go: the shell went',
    '2026-11-24 23:47:53.425 -08:00 [INF] [back] 2 kept page(s) let go: a sign-out',
    "2026-11-24 23:47:53.425 -08:00 [INF] [back] 1 kept page(s) let go: Back's reach")
$soGot = if ($fn) { & { . ([scriptblock]::Create($fn)); @(Bk-SignOutLetGo $soLines) } } else { @() }
T 'back signout: a sign-out''s "the shell went" and "a sign-out" lines count, Back''s reach doesn''t' (
    $soGot.Count -eq 2 -and $soGot[0] -like '*the shell went' -and $backText -match '\$dropped = @\(Bk-SignOutLetGo \$said\)') "matched $($soGot.Count) of 3"

# 18. back's measure: the element with the keyboard looked for a few times (one look on a busy PC found none while the shot
# showed the focus ring: Classic's season at 1280 x 720)
$fn = Get-FunctionText $BackScript 'Bk-Retry'
$rtGot = if ($fn) { & { . ([scriptblock]::Create($fn)); $script:rtN = 0; Bk-Retry { $script:rtN++; if ($script:rtN -ge 3) { 'the card' } } 5 10 } } else { $null }
T 'back measure: the focus read retried (null, null, then the card) and Bk-Measure reads it so' (
    $rtGot -eq 'the card' -and $backText -match '\$focus = Bk-Retry \{ Bk-Focus \}') "got '$rtGot'"

# 19. topbar: the lit tab taken once it holds for two looks (the verify run's Cinematic at max read the page before's lit tab,
# then the moment's unlit bar)
$fn = Get-FunctionText $G3Common 'G3-Settled'
$stGot = if ($fn) { & { . ([scriptblock]::Create($fn)); $script:stSeq = @('Shows', '', 'Shows', 'Shows'); $script:stI = 0
        G3-Settled { $v = $script:stSeq[[Math]::Min($script:stI, $script:stSeq.Count - 1)]; $script:stI++; $v } 'Shows' 3 10 } } else { $null }
$stNever = if ($fn) { & { . ([scriptblock]::Create($fn)); G3-Settled { '' } 'Shows' 0.2 20 } } else { 'x' }
T 'topbar: a lit tab that flickers off settles as lit; one never lit stays unlit; Tb4-Expect reads it so' (
    $stGot -eq 'Shows' -and $stNever -eq '' -and $topbarText -match '\$litNow = G3-Settled \{ G3-Lit \} \$Want') "flicker: '$stGot'; never: '$stNever'"

# 20. topbar: Editorial's hero tile is a way into the episode in progress ("Details of <show> S2 E6 <title>"); its Resume isn't
$fn = Get-FunctionText $G3Common 'G3-IsInProgressHero'
$show = "A Very Long-Running Chronicle of the Lighthouse Keeper's Apprentice and the Ferry to Port Ellery"
$hr = if ($fn) { & { . ([scriptblock]::Create($fn))
        @((G3-IsInProgressHero "Details of $show S2 E6 What the Lighthouse Keeper's Apprentice Found Beneath the Old Mill, Part 2" $show),
            (G3-IsInProgressHero "More info about $show" $show), (G3-IsInProgressHero "Resume $show S2 E6 What the" $show), (G3-IsInProgressHero 'Details of Another Show S1 E1' $show)) } } else { @() }
T 'topbar: Editorial''s "Details of <show> S2 E6" and the other heroes'' "More info about <show>" open it; Resume and another show don''t' (
    ($hr -join ',') -eq 'True,True,False,False') "$($hr -join ',')"

# 21. (1.7.5 gate fix) a job that failed while a monitor dropped off the bus (display.removals; no window minimized) runs once
# more, and passes then: the release verify's menus [DriveIn] failed so and was not run again
$rem = Write-Manifest 'removal' "@{ Checks = @( @{ Id = 'fixture-removal'; $fixture; Themes = @('Classic'); Args = @{ RemovalOnce = `$true } } ) }"
$r = Invoke-Gate @('-ManifestDir', $rem, '-Exe', (Join-Path $devDir 'Nixie.exe'), '-Out', (Join-Path $Out 'removal'), '-Python', $Python)
T 'a job failed while a monitor dropped off runs again and passes' ($r.Code -eq 0 -and $r.Text -match 'again fixture-removal \[Classic\]: a monitor dropped off' -and $r.Text -match 'PASS fixture-removal \[Classic\]') "exit $($r.Code)"

# 22. (1.7.5 gate fix) 3-menus: no Play/Pause (mpv's own controls) fails, never leaves the player's menus out and passes (a
# rerun on a build without libmpv passed 161 of 161, the player's 46 cells left out)
T '3-menus: the player''s menus are tried or the check fails (Nixie''s controls must show)' (
    $menus -match "Check `"\`$\{g2Id\} \[player\]: Nixie's controls show \(Play/Pause\), so the player's menus are tried`" \(\[bool\]\`$g2Pp\)") ''

# 23. (1.7.5 gate fix) lifecycle's quick restart holds the old copy once its window has gone, not after a fixed 0.4 s (the close
# takes about 0.2 s now: the release verify's four themes found it exited, and the restart was never one while it closed)
$restartFn = Get-FunctionText (Join-Path $repo 'tools\gate\checks\g4-common.ps1') 'G4-Restart'
T 'lifecycle: a quick restart holds the old copy as soon as its window has gone (no fixed 0.4 s)' (
    $restartFn -match 'while \(\$watch\.ElapsedMilliseconds -lt 3000 -and -not \$old\.HasExited -and \[G4Win\]::IsWindowVisible\(\$oldHwnd\)\)' -and $restartFn -notmatch 'Start-Sleep -Milliseconds 400') ''

# 24. (1.7.5 gate fix) playback-basics: Home's way to the episode in progress is its card or Editorial's hero "Details of",
# never a hero's "Resume ..." button, which plays at once (the release verify's Drive-in and Editorial)
$fn = Get-FunctionText (Join-Path $repo 'tools\gate\checks\6-playback.ps1') 'G4P-ContinuePick'
$cp = if ($fn) { & { . ([scriptblock]::Create($fn))
        $dHero = [pscustomobject]@{ Name = 'Resume A Signal in Winter S1 E1 The Archive Problem'; Button = $true }
        $dCard = [pscustomobject]@{ Name = 'A Signal in Winter, S1 E1 The Archive Problem, 20% watched'; Button = $false }
        $eDet = [pscustomobject]@{ Name = 'Details of A Signal in Winter S1 E1 The Archive Problem'; Button = $true }
        $dMore = [pscustomobject]@{ Name = 'More info about A Signal in Winter'; Button = $true }
        $oMore = [pscustomobject]@{ Name = 'More info about Another Show'; Button = $true }
        @((@(G4P-ContinuePick @($dHero, $dCard))[0].Name -eq $dCard.Name), (@(G4P-ContinuePick @($dHero, $eDet))[0].Name -eq $eDet.Name), (@(G4P-ContinuePick @($dHero)).Count -eq 0),
            (@(G4P-ContinuePick @($oMore, $dHero, $dMore))[0].Name -eq $dMore.Name)) } } else { @() }
T 'playback-basics: Continue watching''s card, Editorial''s Details or Drive-in''s screen''s More info, never a hero''s Resume button' (($cp -join ',') -eq 'True,True,True,True') "$($cp -join ',')"

# 25. (1.7.5 gate fix) playback-basics' track cases know Cinematic's own panel ("Audio: <track>", ", in use"), not only the
# shared flyout (TracksMenu): the release verify's Cinematic failed all four
if (-not $PlaybackScript) { $PlaybackScript = Join-Path $repo 'tools\gate\checks\6-playback.ps1' }
$pbText = Get-Content -Raw $PlaybackScript
$fn = Get-FunctionText $PlaybackScript 'G4P-PanelName'
$pn = if ($fn) { & { . ([scriptblock]::Create($fn)); @((G4P-PanelName 'AudioTracks' 'Japanese'), (G4P-PanelName 'SubtitleTracks' 'Off')) } } else { @() }
T 'playback-basics: the tracks cases open, pick and read Cinematic''s panel too' (
    ($pn -join '|') -eq 'Audio: Japanese|Subtitles: Off' -and $pbText -match 'Wait-Until \{ if \(G4P-TracksOpen\) \{ \$true \} \}' -and
    $pbText -match "\`$closed = -not \(G4P-TracksOpen\)" -and $pbText -match ", in use\$'") "$($pn -join '|')"

# 26. (1.8.1, the mem review) the leak check reads the app's "[leak]" lines from every log written since Nixie started: its log
# rolls over at midnight, and a run that crossed it counted only the new day's navigations (each round "not there and back")
if (-not $LeakScript) { $LeakScript = Join-Path $repo 'tools\ui-harness\offscreen\then-175-leak.ps1' }
$lkFns = @('Lk-Logs', 'Lk-LastLine', 'Lk-Navigations') | ForEach-Object { Get-FunctionText $LeakScript $_ }
$lkData = Join-Path $Out 'leak-logs'
New-Item -ItemType Directory -Force (Join-Path $lkData 'Logs') | Out-Null
$lkStarted = (Get-Date).AddMinutes(-10)
$lkOld = Join-Path $lkData 'Logs\log20261124.txt'; $lkDay1 = Join-Path $lkData 'Logs\log20261125.txt'; $lkDay2 = Join-Path $lkData 'Logs\log20261126.txt'
[IO.File]::WriteAllText($lkOld, "[leak] live pages: HomePage 9`r`n")   # an earlier run's, before this one started
[IO.File]::WriteAllText($lkDay1, "x`r`n[leak] live pages: HomePage 1`r`n[leak] live pages: MoviePage 1`r`n")
[IO.File]::WriteAllText($lkDay2, "[leak] live pages: CollectionPage 1, HomePage 1`r`nafter midnight`r`n")
(Get-Item $lkOld).LastWriteTime = $lkStarted.AddDays(-1); (Get-Item $lkDay1).LastWriteTime = $lkStarted.AddMinutes(5); (Get-Item $lkDay2).LastWriteTime = $lkStarted.AddMinutes(9)
$lkGot = if ($lkFns[1] -and $lkFns[2]) {   # (an older script has no Lk-Logs: it read the newest log only)
    & { $DataDir = $lkData; $script:ui = [pscustomobject]@{ StartTime = $lkStarted }; foreach ($f in @($lkFns | Where-Object { $_ })) { . ([scriptblock]::Create($f)) }; @((Lk-Navigations), (Lk-LastLine)) }
} else { @() }
T 'leak check: its "[leak]" lines come from every log since Nixie started (the midnight rollover), none from before' (
    $lkGot.Count -eq 2 -and $lkGot[0] -eq 3 -and $lkGot[1] -eq 'CollectionPage 1, HomePage 1') "navigations $($lkGot[0]) (want 3), last line '$($lkGot[1])'"

# 27. (1.8.1, BH-181-M1) drive.ps1's end of run: a line in the app's log of an exception that reached XAML's last handler, or of
# a failed index read, stamped after the run began, fails the run; one before it doesn't; a gate check that expects one by
# name (AppLogExpected) passes its own line and no other. The first start's settings race passed 18 of collections' 28 checks
$alAst = [System.Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($Drive), [ref]$tokens, [ref]$errors)
$alText = @(@('AppLogFailures', 'AppLogExpected') | ForEach-Object { $n = $_
        $alAst.FindAll({ param($x) $x -is [System.Management.Automation.Language.AssignmentStatementAst] -and $x.Left.Extent.Text -eq "`$$n" }, $false) | Select-Object -First 1 } |
    Where-Object { $_ } | ForEach-Object { $_.Extent.Text }) + @(@('Get-AppLogFailures', 'Test-AppLogFailures') | ForEach-Object { Get-FunctionText $Drive $_ } | Where-Object { $_ })
$alLogs = Join-Path $Out 'applog\Logs'
New-Item -ItemType Directory -Force $alLogs | Out-Null
$alStart = [DateTimeOffset]::new(2026, 11, 25, 18, 31, 0, [TimeSpan]::FromHours(-8))
$alStamp = { param($s) $alStart.AddSeconds($s).ToString('yyyy-MM-dd HH:mm:ss.fff zzz') }
[IO.File]::WriteAllText((Join-Path $alLogs 'log20261125.txt'), (@(
            "$(& $alStamp -30) [ERR] [crash-trail] XAML's unhandled exception: an earlier run's",
            "$(& $alStamp 5) [INF] [crash-trail] listening for unhandled errors (UnhandledErrorDetected)",
            "$(& $alStamp 53) [WRN] Library index: the read failed",
            "System.ArgumentNullException: Value cannot be null. (Parameter 'propertyName')",
            "   at System.Text.Json.Nodes.JsonObject.WriteTo",
            "$(& $alStamp 90) [ERR] [crash-trail] XAML's unhandled exception: System.ArgumentNullException (0x80004003): Value cannot be null.") -join "`r`n") + "`r`n")
$alRun = { param($GateId, $Expected, $Logs)
    $checks = [ordered]@{}
    function Say([string]$Text) { }
    . ([scriptblock]::Create($checkFn.Extent.Text))
    foreach ($t in $alText) { . ([scriptblock]::Create($t)) }
    if ($Expected) { $AppLogExpected = $Expected }
    if (Get-Command Test-AppLogFailures -ErrorAction SilentlyContinue) { Test-AppLogFailures $Logs ($alStart.ToUnixTimeMilliseconds() / 1000.0) $GateId }
    $checks
}
$alAll = & $alRun '' $null $alLogs
$alXaml = @($alAll.Keys | Where-Object { $_ -like '*XAML*' })
$alIndex = @($alAll.Keys | Where-Object { $_ -like '*index read*' })
$alOk = { param($c, $keys) @($keys | ForEach-Object { $c[$_].ok }) }
T 'drive.ps1: the app log''s "XAML''s unhandled exception" and "the read failed" after the run began each fail the run, with the exception' (
    $alXaml.Count -eq 1 -and $alIndex.Count -eq 1 -and -not $alAll[$alXaml[0]].ok -and -not $alAll[$alIndex[0]].ok -and
    $alAll[$alIndex[0]].detail -match "^1 line\(s\), first at 6:31:53\.000 PM: Library index: the read failed: System\.ArgumentNullException" -and
    $alAll[$alXaml[0]].detail -match '^1 line\(s\)') "$(@($alAll.Keys | ForEach-Object { "$_ = $($alAll[$_].ok) ($($alAll[$_].detail))" }) -join '; ')"
$alClean = Join-Path $Out 'applog-clean\Logs'
New-Item -ItemType Directory -Force $alClean | Out-Null
[IO.File]::WriteAllText((Join-Path $alClean 'log20261125.txt'), "$(& $alStamp -30) [WRN] Library index: the read failed`r`n$(& $alStamp 2) [INF] Library index: 468 titles in 3 libraries`r`n")
$alNone = & $alRun '' $null $alClean
$alMissing = & $alRun '' $null (Join-Path $Out 'applog-none\Logs')
T 'drive.ps1: an earlier run''s lines, and no Logs folder, pass' ($alNone.Count -eq 2 -and (@(& $alOk $alNone @($alNone.Keys)) -notcontains $false) -and
    $alMissing.Count -eq 2 -and (@(& $alOk $alMissing @($alMissing.Keys)) -notcontains $false)) ''
$alExp = & $alRun 'fixture-expects' @{ 'fixture-expects' = @('Library index: the read failed') } $alLogs
$alOther = & $alRun 'another-check' @{ 'fixture-expects' = @('Library index: the read failed') } $alLogs
T 'drive.ps1: a gate check that expects "the read failed" by name passes its own line, and no other check or line' (
    $alExp.Count -eq 2 -and $alExp[$alIndex[0]].ok -and $alExp[$alIndex[0]].detail -like 'expected by fixture-expects: 1 line(s)*' -and -not $alExp[$alXaml[0]].ok -and
    -not $alOther[$alIndex[0]].ok) ''
T 'drive.ps1: the end of run reads the app log with the gate check''s name (GATE_ID)' ($finText -match "Test-AppLogFailures \(Join-Path \`$DataDir 'Logs'\) \`$runStarted \`$env:GATE_ID") ''

# 1.8 (BH-180-m3): a release's own harness checks are in the gate: every offscreen\then-18x-*.ps1 is a manifest entry's Then
# (release or nightly). 1.8's were in the repo and in no manifest
$m3Then = @(Get-ChildItem (Join-Path $repo 'tools\gate\manifest') -Filter '*.psd1' | ForEach-Object { (Import-PowerShellDataFile $_.FullName).Checks } | ForEach-Object { "$($_.Then)" })
$m3Missing = @(Get-ChildItem (Join-Path $repo 'tools\ui-harness\offscreen') -Filter 'then-18*.ps1' | Where-Object { $m3Then -notcontains "tools\ui-harness\offscreen\$($_.Name)" } | ForEach-Object { $_.Name })
T 'every then-18x check is in a gate manifest (release or nightly)' ($m3Missing.Count -eq 0) "not in one: $($m3Missing -join ', ')"

Write-Host ("--- test_gate: {0}" -f $(if ($failures) { "$failures failed" } else { 'all passed' }))
if ($failures) { exit 1 }
exit 0
