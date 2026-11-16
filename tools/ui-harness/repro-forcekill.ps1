<#
  Finding 2 check: start the self-test, wait until mpv plays, end Seaglass.exe the way Stop-Process -Force does
  (TerminateProcess, through the handle it was started with) and check that none of the python/mpv processes it started
  survive. Touches only the processes this run created (common.ps1: held handles, checked start times).
  Usage: repro-forcekill.ps1 [-Runs 3] [-Exe <Seaglass.exe>]
#>
param([int]$Runs = 3, [string]$Exe = '')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Exe) { $Exe = $script:DefaultExe }
if (-not (Test-Path $Exe)) { throw "Not built: $Exe" }

$summary = @()
for ($run = 1; $run -le $Runs; $run++) {
    Reset-Tracked
    $ui = $null
    try {
        $ui = Start-FluentFin $Exe @('--mpv-selftest')
        $null = Wait-Mpv $ui 30
        Start-Sleep -Seconds 2
        $tree = @(Get-Descendants $ui.Id)
        $names = ($tree | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ','
        $null = Stop-Tracked $ui.Id   # TerminateProcess (what Stop-Process -Force does), through the handle it was started with
        Start-Sleep -Seconds 2
        $alive = @($tree | Where-Object { Get-TrackedProcess ([int]$_.ProcessId) })
        $line = "run ${run}: started [$names] survivors after the forced kill: $(if ($alive.Count) { ($alive | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',' } else { 'none' })"
    }
    catch {
        $line = "run ${run}: ERROR $($_.Exception.Message)"
    }
    finally {
        $null = Close-Tracked $ui
    }
    $line; $summary += $line
}
