<#
.SYNOPSIS
  Inside Windows Sandbox only: the upgrade test's two legs (tools\release\upgrade-test-sandbox.ps1 generates the .wsb whose
  logon command runs this). Refuses to run anywhere else: it installs Nixie, so it must never run in a real account.

.DESCRIPTION
  As the Sandbox's own account (WDAGUtilityAccount) it checks where it is, the inputs, and makes a standard user (no
  administrator) with a random password that lives only in this Sandbox; then runs itself again as that user (-AsTester),
  which runs the legs:
    leg 1, the old flow: a Seaglass data folder (a marker in its settings), the previous MSI, the previous Nixie started once
      (it copies Seaglass to Nixie) and closed; then this release over it, as 1.4.1's updater installs it (msiexec /i with
      Nixie closed; nothing reopens it): the version, the Nixie data folder still holding the marker, Seaglass's untouched;
      this release started once and closed;
    leg 2, the new flow: Nixie.exe --install-update <the test build> (Nixie's own switch for this test: "Restart to update"
      with no clicks): Nixie closes, the launcher's helper installs the test build quietly and opens Nixie again;
    the elevation: whoami /groups (no administrator, medium integrity), msiexec's exit codes and its logs' privilege lines.
  Results: results.json (every check, versions, log excerpts) and done.json in the mapped results folder; then the Sandbox
  shuts down. -DryRun checks the inputs and writes plan.json only: nothing installed, no user made, nothing started.

  Exit status: 0 done (see results.json), 2 bad inputs, 3 refused (not in Windows Sandbox).
#>
[CmdletBinding()]
param(
    [string]$Root = 'C:\nixie',
    [string]$NewMsi,
    [string]$PreviousMsi,
    [string]$TestMsi,
    [string]$Results,
    [switch]$DryRun,
    [switch]$AsTester,
    [switch]$NoShutdown
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $Results) { $Results = Join-Path $Root 'results' }

# --- where this runs: Windows Sandbox's own layout, or nothing ------------------------------------------------------------
function Test-InSandbox {
    # the Sandbox's one account, its profile, and the mapped folders under C:\nixie as the .wsb puts them
    $account = if ($AsTester) { Test-Path 'C:\Users\WDAGUtilityAccount' } else { $env:USERNAME -eq 'WDAGUtilityAccount' -and (Test-Path 'C:\Users\WDAGUtilityAccount') }
    $account -and $Root -eq 'C:\nixie' -and (Test-Path 'C:\nixie\scripts\upgrade-inside.ps1') -and (Test-Path 'C:\nixie\results')
}
if (-not $DryRun -and -not (Test-InSandbox)) {
    Write-Host 'upgrade-inside: REFUSED: this is not Windows Sandbox (it installs Nixie; it never runs in a real account)'
    exit 3
}

# --- the inputs ----------------------------------------------------------------------------------------------------------
function Get-Input([string]$Relative, [string]$What) {
    if (-not $Relative) { Write-Host "upgrade-inside: no $What given"; exit 2 }
    $path = Join-Path $Root $Relative
    if (-not (Test-Path -LiteralPath $path)) { Write-Host "upgrade-inside: no $What at $path"; exit 2 }
    if ((Split-Path -Leaf $path) -notmatch '^Nixie-(\d+\.\d+\.\d+)-win-x64\.msi$') { Write-Host "upgrade-inside: $path is not a Nixie installer"; exit 2 }
    [pscustomobject]@{ Path = $path; Version = $Matches[1] }
}
$new = Get-Input $NewMsi 'installer of this release'
$previous = Get-Input $PreviousMsi 'previous installer'
$test = Get-Input $TestMsi 'test build'
if (-not ([version]$previous.Version -lt [version]$new.Version -and [version]$new.Version -lt [version]$test.Version)) {
    Write-Host "upgrade-inside: the versions must rise: $($previous.Version) < $($new.Version) < $($test.Version)"; exit 2
}
New-Item -ItemType Directory -Force $Results | Out-Null

$work = 'C:\Users\Public\nixie-upgrade'
$installFolder = '%LOCALAPPDATA%\Programs\Nixie'
$plan = [ordered]@{
    leg1 = @("seed %LOCALAPPDATA%\Seaglass\LocalSettings.json with a marker",
             "msiexec /i `"$($previous.Path)`" /qn /norestart /l*v leg1-previous.log  (exit 0; version $($previous.Version))",
             "start $installFolder\app\Nixie.exe once: the Seaglass folder copied to %LOCALAPPDATA%\Nixie; close it",
             "msiexec /i `"$($new.Path)`" /passive /norestart /l*v leg1-upgrade.log with Nixie closed, as 1.4.1's updater does (exit 0; version $($new.Version); nothing reopened)",
             'the Nixie data folder keeps the marker; the Seaglass folder is untouched',
             "start $($new.Version) once (it runs); close it")
    leg2 = @("copy the test build and its .sha256 to $work",
             "start $installFolder\app\Nixie.exe --install-update $work\$(Split-Path -Leaf $test.Path)",
             'it closes; the helper (Updates\Nixie-updater.exe) installs with msiexec /passive /norestart /l*v Updates\install.log',
             "the installed version becomes $($test.Version); Nixie is opened again (a new process, without --install-update)",
             "Nixie's log: the install of $($test.Version) from $($new.Version), installer exit 0")
    elevation = @('whoami /groups: not an administrator, Mandatory Label\Medium Mandatory Level',
                  'every msiexec exit 0; the logs'' Privileged / MsiRunningElevated / AdminUser / ALLUSERS lines kept')
}
if ($DryRun) {
    $plan | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $Results 'plan.json')
    Write-Host "upgrade-inside: dry run: nothing installed, no user made, nothing started. Plan: $(Join-Path $Results 'plan.json')"
    exit 0
}

# --- the checks ----------------------------------------------------------------------------------------------------------
# Windows PowerShell 5.1 throws "Argument types do not match" on @($checks) (the 1.5.0 Sandbox run lost its results.json
# to it): take the checks out with $checks.ToArray()
$checks = New-Object System.Collections.Generic.List[object]
$notes = [ordered]@{}
# progress.log: one line per step and check as it happens (opened and closed each time, so it can be copied mid-run)
function Step([string]$What) { Add-Content -LiteralPath (Join-Path $Results 'progress.log') -Value ('{0:HH:mm:ss} {1}' -f (Get-Date), $What) -ErrorAction SilentlyContinue }
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') { $checks.Add([pscustomobject]@{ name = $Name; ok = $Ok; detail = $Detail }); $line = "$(if ($Ok) { 'ok  ' } else { 'FAIL' }) $Name $Detail"; Write-Host $line; Step $line }
function Wait-For([scriptblock]$Test, [int]$Seconds) { $end = (Get-Date).AddSeconds($Seconds); while ((Get-Date) -lt $end) { $r = & $Test; if ($r) { return $r }; Start-Sleep -Milliseconds 500 }; $null }
function Get-Installed {
    # the installed Nixie's version, through the Windows Installer (read only)
    $installer = New-Object -ComObject WindowsInstaller.Installer
    foreach ($code in @($installer.RelatedProducts('{612ED1CB-52F7-49B1-9129-A1B04ADB015A}'))) { return $installer.ProductInfo($code, 'VersionString') }
    $null
}
function Get-LogLines([string]$Log) {
    if (-not (Test-Path $Log)) { return @("(no log at $Log)") }
    @(Select-String -LiteralPath $Log -Pattern 'Privileged|MsiRunningElevated|AdminUser|ALLUSERS|user privileges|Installation success or error status|completed successfully|Windows Installer installed' |
        Select-Object -First 25 | ForEach-Object { $_.Line.Trim() })
}
function Start-Nixie([string[]]$Arguments) {
    $exe = Join-Path $env:LOCALAPPDATA 'Programs\Nixie\app\Nixie.exe'
    Step "start Nixie $($Arguments -join ' ')"
    # Windows PowerShell's Start-Process refuses an empty -ArgumentList (the 1.5.0 release run stopped there)
    if ($Arguments -and $Arguments.Count) { Start-Process -FilePath $exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $exe -PassThru }
}
function Stop-Nixie($Process) {
    if ($Process -and -not $Process.HasExited) {
        Step "close Nixie (pid $($Process.Id))"
        [void]$Process.CloseMainWindow()
        if (-not $Process.WaitForExit(20000)) { Step 'it did not close in 20 s: ended'; try { $Process.Kill() } catch { Step "could not end it: $($_.Exception.Message)" } }
    }
}
function Get-NixieProcesses([datetime]$After) {
    $app = Join-Path $env:LOCALAPPDATA 'Programs\Nixie\app\Nixie.exe'
    @(Get-CimInstance Win32_Process -Filter "Name = 'Nixie.exe'" | Where-Object { $_.ExecutablePath -eq $app -and $_.CreationDate -gt $After })
}
function Snapshot([string]$Folder) { if (Test-Path $Folder) { @(Get-ChildItem -LiteralPath $Folder -Recurse -File | ForEach-Object { '{0}|{1}' -f $_.FullName.Substring($Folder.Length), (Get-FileHash $_.FullName).Hash }) -join "`n" } else { '' } }

if (-not $AsTester) {
    # --- as the Sandbox's account: the standard user, then the legs as that user ----------------------------------------
    # The 1.5.0 release run ended here after copying one log, with no results.json, done.json or shutdown. Now: a transcript
    # straight into the mapped results folder, the tester's own process watched (not its children, which a Nixie or helper
    # left running would hold) with its results copied across every 10 seconds, and results.json, done.json and the
    # shutdown whatever happens.
    try { Start-Transcript -LiteralPath (Join-Path $Results 'outer-transcript.txt') -Force | Out-Null } catch { }
    $testerResults = Join-Path $work 'results'
    try {
        $me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        $isAdmin = $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        $notes.sandbox_account = "$env:USERNAME (administrator: $isAdmin)"
        New-Item -ItemType Directory -Force $work | Out-Null
        Copy-Item -LiteralPath $test.Path, "$($test.Path).sha256" -Destination $work
        New-Item -ItemType Directory -Force $testerResults | Out-Null
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts\upgrade-inside.ps1'), '-AsTester', '-Root', $Root,
            '-NewMsi', $NewMsi, '-PreviousMsi', $PreviousMsi, '-TestMsi', $TestMsi, '-Results', $testerResults)
        if ($isAdmin) {
            # a standard user that lives only in this Sandbox; its password is random and never leaves it
            $bytes = New-Object byte[] 24
            [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
            $password = [Convert]::ToBase64String($bytes) + 'a1!'
            $secure = ConvertTo-SecureString $password -AsPlainText -Force
            New-LocalUser -Name 'nixietest' -Password $secure -PasswordNeverExpires -AccountNeverExpires | Out-Null
            Add-LocalGroupMember -Group 'Users' -Member 'nixietest' -ErrorAction SilentlyContinue
            icacls $work /grant 'nixietest:(OI)(CI)M' | Out-Null
            $cred = New-Object System.Management.Automation.PSCredential('nixietest', $secure)
            $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Credential $cred -LoadUserProfile -WorkingDirectory $work -PassThru
            $who = 'nixietest (made here, standard user)'
        }
        else {
            $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WorkingDirectory $work -PassThru
            $who = "$env:USERNAME (already a standard user)"
        }
        $null = $p.Handle   # held open now, or ExitCode reads empty once it has exited (the 1.5.0 run's "exit ")
        $notes.tester = "$who, pid $($p.Id)"
        Write-Host "tester started: $($notes.tester)"
        $deadline = (Get-Date).AddMinutes(35)
        while (-not $p.HasExited -and (Get-Date) -lt $deadline) {
            Copy-Item -Path (Join-Path $testerResults '*') -Destination $Results -Recurse -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 10
        }
        if ($p.HasExited) {
            $code = try { $p.ExitCode } catch { 'unknown' }
            $notes.tester = "$who, exit $code"
        }
        else { $notes.tester = "$who, still running after 35 minutes"; Check 'the legs finished within 35 minutes' $false }
    }
    catch { Check 'the outer script ran to the end' $false "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))" }
    finally {
        Copy-Item -Path (Join-Path $testerResults '*') -Destination $Results -Recurse -Force -ErrorAction SilentlyContinue
        $r = $null
        try { if (Test-Path (Join-Path $Results 'results.json')) { $r = Get-Content -Raw (Join-Path $Results 'results.json') | ConvertFrom-Json } }
        catch { Check "the tester's results.json reads" $false $_.Exception.Message }
        if (-not $r) { Check 'the legs ran and wrote their results' $false "$($notes.tester)"; $r = [pscustomobject]@{ checks = @() } }
        $r | Add-Member -NotePropertyName checks -NotePropertyValue (@($r.checks) + $checks.ToArray()) -Force
        $r | Add-Member -NotePropertyName outer -NotePropertyValue $notes -Force
        try { $r | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $Results 'results.json') }
        catch { Write-Host "results.json not written: $($_.Exception.Message)" }
        @{ finished = (Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $Results 'done.json')
        try { Stop-Transcript | Out-Null } catch { }
        if (-not $NoShutdown) { shutdown.exe /s /t 15 /c 'Nixie upgrade test done' }
    }
    exit 0
}

# --- as the tester (a standard user) ---------------------------------------------------------------------------------------
$started = Get-Date
$groups = (whoami /groups) -join "`n"
$me = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$notes.whoami = $env:USERNAME
$notes.integrity = (($groups -split "`n") | Where-Object { $_ -match 'Mandatory Level' } | ForEach-Object { ($_ -split '\s{2,}')[0] }) -join '; '
Check 'the tester is not an administrator' (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) $notes.whoami
Check 'the tester runs at medium integrity (no elevation)' ($groups -match 'Medium Mandatory Level' -and $groups -notmatch 'High Mandatory Level') $notes.integrity

try {
    # ---- leg 1: the old flow
    $seaglass = Join-Path $env:LOCALAPPDATA 'Seaglass'
    $nixieData = Join-Path $env:LOCALAPPDATA 'Nixie'
    $marker = [guid]::NewGuid().ToString('N')
    New-Item -ItemType Directory -Force $seaglass | Out-Null
    [IO.File]::WriteAllText((Join-Path $seaglass 'LocalSettings.json'), "{ `"UpgradeTestMarker`": `"$marker`", `"CheckForUpdates`": false }", [Text.UTF8Encoding]::new($false))
    $seaglassBefore = Snapshot $seaglass

    Step "leg 1: install $($previous.Version)"
    $log = Join-Path $Results 'leg1-previous.log'
    $exit = (Start-Process msiexec.exe -ArgumentList @('/i', "`"$($previous.Path)`"", '/qn', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru).ExitCode
    Check "leg 1: the previous MSI ($($previous.Version)) installs as a standard user (exit 0)" ($exit -eq 0) "exit $exit"
    Check "leg 1: installed version $($previous.Version)" ((Get-Installed) -eq $previous.Version) "$(Get-Installed)"
    $notes.leg1_previous_log = Get-LogLines $log

    $old = Start-Nixie @()
    $copied = Wait-For { $f = Join-Path $nixieData 'LocalSettings.json'; (Test-Path $f) -and ((Get-Content -Raw $f) -match $marker) } 90
    Check "leg 1: $($previous.Version) copies the Seaglass data folder to Nixie at its first start" ([bool]$copied)
    Start-Sleep -Seconds 5
    Stop-Nixie $old

    $beforeUpgrade = Get-Date
    Step "leg 1: install $($new.Version) over it (the old flow)"
    $log = Join-Path $Results 'leg1-upgrade.log'
    $exit = (Start-Process msiexec.exe -ArgumentList @('/i', "`"$($new.Path)`"", '/passive', '/norestart', '/l*v', "`"$log`"") -Wait -PassThru).ExitCode
    Check "leg 1: $($new.Version) installs over $($previous.Version) (exit 0)" ($exit -eq 0) "exit $exit"
    Check "leg 1: installed version $($new.Version)" ((Get-Installed) -eq $new.Version) "$(Get-Installed)"
    Start-Sleep -Seconds 5
    Check 'leg 1: the old flow opens nothing afterwards' ((Get-NixieProcesses $beforeUpgrade).Count -eq 0)
    Check 'leg 1: the Nixie data folder keeps what was copied' ((Get-Content -Raw (Join-Path $nixieData 'LocalSettings.json')) -match $marker)
    Check 'leg 1: the Seaglass data folder is untouched' ((Snapshot $seaglass) -eq $seaglassBefore)
    $notes.leg1_upgrade_log = Get-LogLines $log

    $logs = Join-Path $nixieData 'Logs'
    $one = Start-Nixie @()
    Start-Sleep -Seconds 20
    Check "leg 1: $($new.Version) starts and keeps running" (-not $one.HasExited)
    # no log check here: a start with no server and nothing to copy logs nothing (the 1.5.0 run's one false failure);
    # leg 2's "[update] the install of" line proves the log in the Nixie folder
    Stop-Nixie $one

    # ---- leg 2: Restart to update, driven by Nixie's own switch
    Step "leg 2: Restart to update to $($test.Version)"
    $testCopy = Join-Path $work (Split-Path -Leaf $test.Path)
    $leg2At = Get-Date
    $first = Start-Nixie @('--install-update', "`"$testCopy`"")
    $closed = Wait-For { $first.HasExited } 90
    Check 'leg 2: Restart to update closes Nixie' ([bool]$closed)
    $installed = Wait-For { (Get-Installed) -eq $test.Version } 300
    Check "leg 2: the helper installs $($test.Version) quietly" ([bool]$installed) "$(Get-Installed)"
    $reopened = Wait-For { Get-NixieProcesses $leg2At | Where-Object { $_.ProcessId -ne $first.Id } | Select-Object -First 1 } 120
    Check 'leg 2: and opens Nixie again' ([bool]$reopened) $(if ($reopened) { $reopened.CommandLine })
    Check 'leg 2: without the install switch (it does not install twice)' ($reopened -and $reopened.CommandLine -notmatch '--install-update')
    $updates = Join-Path $nixieData 'Updates'
    $notes.leg2_install_log = Get-LogLines (Join-Path $updates 'install.log')
    Check 'leg 2: the helper logged the install (msiexec /l*v)' (@($notes.leg2_install_log -match 'success or error status: 0|completed successfully').Count -gt 0)
    Start-Sleep -Seconds 10
    $line = Get-ChildItem $logs -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 | ForEach-Object { Select-String -LiteralPath $_.FullName -Pattern '\[update\] the install of' | Select-Object -Last 1 }
    Check "leg 2: the reopened Nixie read the helper's result (installer exit 0)" ($line -and $line.Line -match 'installer exit 0') $(if ($line) { $line.Line.Trim() })
    if ($reopened) { Stop-Nixie (Get-Process -Id $reopened.ProcessId -ErrorAction SilentlyContinue) }
}
catch {
    Check 'the legs ran to the end' $false "$($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))"
}

$result = [ordered]@{ versions = [ordered]@{ previous = $previous.Version; new = $new.Version; test = $test.Version }; started = $started.ToUniversalTime().ToString('o')
    checks = $checks.ToArray(); notes = $notes }
try { $result | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $Results 'results.json') }
catch { Step "results.json not written: $($_.Exception.Message)"; exit 1 }
exit 0
