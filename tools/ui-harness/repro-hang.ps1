<#
  Finding 1 repro: start the self-test, suspend the mpv.exe it starts (NtSuspendProcess), and watch whether FluentFin
  survives the watchdog kill and shows the "stopped responding" message. Closes FluentFin afterwards and checks that
  none of the processes this run created survive. Each run closes what it started in a finally, whatever fails; only
  processes this script started (checked by start time) are touched.
  Usage: repro-hang.ps1 -Out <dir> [-Runs 12] [-Exe <Seaglass.exe>] [-WatchSeconds 14]
#>
param([int]$Runs = 1, [Parameter(Mandatory)][string]$Out, [string]$Exe = '', [int]$WatchSeconds = 14)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Exe) { $Exe = $script:DefaultExe }
if (-not (Test-Path $Exe)) { throw "Not built: $Exe" }
New-Item -ItemType Directory -Force $Out | Out-Null
$shot = Join-Path $PSScriptRoot 'shot.ps1'

$summary = @()
for ($run = 1; $run -le $Runs; $run++) {
    Reset-Tracked
    $ui = $null
    $line = "run ${run}: "
    try {
        $ui = Start-FluentFin $Exe @('--mpv-selftest')
        $mpv = Wait-Mpv $ui 30
        if (-not $mpv) { $line += 'NO MPV'; continue }
        Start-Sleep -Seconds 3
        $created = @(Get-Descendants $ui.Id | ForEach-Object { [int]$_.ProcessId })
        $rc = Suspend-Tracked ([int]$mpv.ProcessId)
        $t0 = Get-Date; $mpvGoneAt = $null; $uiDied = $false
        for ($i = 0; $i -lt $WatchSeconds * 2; $i++) {
            Start-Sleep -Milliseconds 500
            $ui.Refresh()
            if ($ui.HasExited) { $uiDied = $true; break }
            if (-not $mpvGoneAt -and -not (Get-TrackedProcess ([int]$mpv.ProcessId))) { $mpvGoneAt = ((Get-Date) - $t0).TotalSeconds }
        }
        $bar = if (-not $uiDied) { Find-Text $ui.Id 'stopped responding' } else { $null }
        if ($run -le 2 -and -not $uiDied) { & $shot -Out (Join-Path $Out "hang-run$run.png") -ProcessId $ui.Id | Out-Null }
        # close the app the normal way, then check that nothing this run created survives
        $closed = $true
        if (-not $uiDied) { $null = $ui.CloseMainWindow(); $closed = $ui.WaitForExit(16000) }
        Start-Sleep -Milliseconds 800
        $left = @($created | Where-Object { Get-TrackedProcess $_ })
        $line += "suspend rc=$rc mpv=$($mpv.ProcessId) uiSurvived=$(-not $uiDied) mpvGoneAfter=$(if ($mpvGoneAt) { '{0:N1}s' -f $mpvGoneAt } else { 'never' }) bar=$(if ($bar) { "'$bar'" } else { 'none' }) closedNormally=$closed survivorsAfterClose=$($left.Count)"
    }
    catch {
        $line += "ERROR $($_.Exception.Message)"
    }
    finally {
        $ended = @(Close-Tracked $ui)
        if ($ended.Count) { $line += " (cleanup ended: $($ended -join ', '))" }
        $line
        $summary += $line
    }
}
"---"
$summary
