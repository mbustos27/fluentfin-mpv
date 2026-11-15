<#
  run-dev.ps1 closing a running dev build must give a slow close the time to finish, not kill FluentFin in the middle
  of it. The slow close: the backend's interpreter is suspended, so its `shutdown` is never answered and FluentFin
  waits the full 10 s before it kills the backend (docs\BRIDGE.md section 3), then exits by itself.
  Starts the self-test, suspends the backend's interpreter, runs run-dev.ps1 -NoBuild -SelfTest (with
  -CloseWaitSeconds N if given), and reports whether the first FluentFin exited by itself (exit code 0) or was killed by
  run-dev (-1). Closes the self-test run-dev started and everything else this script started, in a finally.
  Refuses to run while another copy of the build is running (run-dev.ps1 would close it too).
  Usage: rundev-close.ps1                       run-dev's default wait: expect "exited by itself"
         rundev-close.ps1 -CloseWaitSeconds 8   the old 8 s: expect "KILLED by run-dev"
#>
param([int]$CloseWaitSeconds = 0)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$exe = $script:DefaultExe
$runDev = Join-Path $script:RepoRoot 'run-dev.ps1'
if (-not (Test-Path $exe)) { throw "Not built: $exe" }
if (Get-Process FluentFin -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw "A copy of $exe is already running; close it first (run-dev.ps1 would close it too)"
}

$first = $null; $second = $null
try {
    $first = Start-FluentFin $exe @('--mpv-selftest')
    if (-not (Wait-Mpv $first 40)) { throw 'The self-test started no mpv' }
    Start-Sleep -Seconds 3
    $tree = @(Get-Descendants $first.Id)
    # the venv's python.exe is a launcher; the interpreter it starts runs the bridge
    $interpreter = $tree | Where-Object { $_.Name -eq 'python.exe' -and $_.ExecutablePath -notlike '*\.venv\*' } | Select-Object -First 1
    if (-not $interpreter) { $interpreter = $tree | Where-Object Name -eq 'python.exe' | Select-Object -Last 1 }
    "tree: " + (($tree | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',') + "; suspending python.exe#$($interpreter.ProcessId)"
    $null = Suspend-Tracked ([int]$interpreter.ProcessId)

    $arguments = @{ NoBuild = $true; SelfTest = $true }
    if ($CloseWaitSeconds -gt 0) { $arguments.CloseWaitSeconds = $CloseWaitSeconds }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $runDevFt = [UiHarnessNative]::Now()   # anything run-dev starts is newer
    & $runDev @arguments *>&1 | ForEach-Object { "  run-dev: $_" }
    $null = $first.WaitForExit(5000)
    $first.Refresh()
    $code = $first.ExitCode
    $verdict = if ($code -eq 0) { 'exited by itself' } else { 'KILLED by run-dev' }
    "first FluentFin#$($first.Id): exit code $code after about $([int]$watch.Elapsed.TotalSeconds) s of run-dev => $verdict"

    # the FluentFin run-dev.ps1 started (Start-Process in this PowerShell): each candidate's handle is taken at once and
    # held, so its pid cannot be reused after, and everything is checked through it: created after run-dev was called,
    # this PowerShell as its parent (alive, so its pid is not a reused one), this build's exe
    foreach ($candidate in @(Get-Process FluentFin -ErrorAction SilentlyContinue)) {
        $h = try { $candidate.Handle } catch { [IntPtr]::Zero }
        if ($h -ne [IntPtr]::Zero -and $candidate.Id -ne $first.Id -and [UiHarnessNative]::CreationOf($h) -ge $runDevFt -and
            [UiHarnessNative]::ParentOf($h) -eq $PID -and [UiHarnessNative]::ImageOf($h) -eq $exe) {
            Add-Tracked -Process $candidate
            if ($script:Tracked.ContainsKey($candidate.Id)) { $second = $candidate; break }
        }
        $candidate.Dispose()
    }
    if ($second) {
        $null = Wait-Mpv $second 40
        "run-dev started FluentFin#$($second.Id) (self-test); closing it"
    }
    else { "no FluentFin started by run-dev found (none of this PowerShell's, created after it ran, from $exe)" }
}
finally {
    $ended = @(Close-Tracked $second)
    $ended += @(Close-Tracked $first)
    if ($ended.Count) { "cleanup ended: $($ended -join ', ')" }
}
