<#
  Gate for the mpv self-test (Nixie.exe --mpv-selftest: no server, mpv's test pattern in the player page), with
  mpv's own controls (Settings > Playback > Player controls: mpv; FluentFin's are the default since 2026-11-15, and
  their keys and window are checked by tools\ui-harness\fluentfin-controls and run-offscreen.ps1 instead).
   1. the video plays (screenshot);
   2. mpv's fullscreen key (f) makes the window full screen, Escape restores it (screenshots);
   3. leaving the page while full screen: f, then mpv's stop key (q). Playback stops, the page goes back (to the server
      list the self-test puts under it), and VideoPlayerPage.OnNavigatedFrom -> LeaveFullScreen must restore the window
      and send set_fullscreen false: checked on the window, in FluentFin's log (the value) and in the backend's log
      (the command, before the page's stop);
   4. a normal close: the window hides at once, the process exits, nothing it started survives.
  Starts the exe directly (no build). Whatever fails, the finally closes what this script started and nothing else.
  Prints PASS/FAIL per check; exit code 1 if any check failed.
  Usage: selftest-gate.ps1 -Out <dir> [-Exe <Nixie.exe>]
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe = '',
    [int]$CloseSeconds = 20
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
if (-not $Exe) { $Exe = $script:DefaultExe }
$Exe = [IO.Path]::GetFullPath($Exe)
if (-not (Test-Path $Exe)) { throw "Not built: $Exe" }
New-Item -ItemType Directory -Force $Out | Out-Null
$shot = Join-Path $PSScriptRoot 'shot.ps1'

$results = [ordered]@{}
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $results[$Name] = $Ok
    "{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" } else { '' })
}

$appLog = Get-FluentFinLog
$appMark = Get-LogMark $appLog
$backendMark = Get-LogMark (Get-BackendLog)
$script:ui = $null
function Invoke-Gate {
    $script:ui = Start-FluentFin $Exe @('--mpv-selftest')
    $ui = $script:ui
    $mpv = Wait-Mpv $ui 40
    Check 'mpv started' ([bool]$mpv) "under FluentFin#$($ui.Id)"
    if (-not $mpv) { return }
    Start-Sleep -Seconds 4
    $ui.Refresh(); $hwnd = $ui.MainWindowHandle
    "started: " + ((Get-Descendants $ui.Id | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',')
    & $shot -Out (Join-Path $Out 'selftest-playing.png') -ProcessId $ui.Id -Front

    # 2. mpv's fullscreen key, then Escape. Send-Keys sends nothing unless FluentFin's window is in front: the step
    # fails instead, so a key never lands in another app
    if (-not (Send-Keys $hwnd 'f')) { Check 'f sent to FluentFin' $false 'FluentFin was not the foreground window; nothing sent'; return }
    $state = Wait-WindowState $hwnd $true
    Check 'f -> full screen' $state.FullScreen "$($state.Rect) on $($state.Monitor)"
    Start-Sleep -Seconds 1   # past the presenter's transition, which a capture can catch half-faded
    & $shot -Out (Join-Path $Out 'selftest-fullscreen.png') -ProcessId $ui.Id
    if (-not (Send-Keys $hwnd '{ESC}')) { Check 'Escape sent to FluentFin' $false 'FluentFin was not the foreground window; nothing sent'; return }
    $state = Wait-WindowState $hwnd $false
    Check 'Escape -> windowed' (-not $state.FullScreen) $state.Rect
    & $shot -Out (Join-Path $Out 'selftest-windowed-again.png') -ProcessId $ui.Id

    # 3. leave the page while full screen
    if (-not (Send-Keys $hwnd 'f')) { Check 'f (again) sent to FluentFin' $false 'FluentFin was not the foreground window; nothing sent'; return }
    $state = Wait-WindowState $hwnd $true
    Check 'f again -> full screen' $state.FullScreen $state.Rect
    $appBeforeLeave = Get-LogMark (Get-FluentFinLog)
    $backendBeforeLeave = Get-LogMark (Get-BackendLog)
    if (-not (Send-Keys $hwnd 'q')) { Check 'q sent to FluentFin' $false 'FluentFin was not the foreground window; nothing sent'; return }
    $state = Wait-WindowState $hwnd $false 8
    Check 'leaving the page in full screen -> window restored' (-not $state.FullScreen) $state.Rect
    $left = $null
    for ($i = 0; $i -lt 16 -and -not $left; $i++) { Start-Sleep -Milliseconds 500; $left = Find-Text $ui.Id 'Detect' }
    Check 'the page was left (server list shown)' ([bool]$left)
    & $shot -Out (Join-Path $Out 'selftest-left-page.png') -ProcessId $ui.Id
    Start-Sleep -Seconds 1
    $appLines = @(Get-LinesSince $appBeforeLeave (Get-FluentFinLog))
    $leaveLine = $appLines | Where-Object { $_ -match 'Leaving the player page in full screen: .*set_fullscreen false' } | Select-Object -First 1
    Check 'FluentFin log: set_fullscreen false on leaving' ([bool]$leaveLine) ("$leaveLine".Trim())
    $backendLines = @(Get-LinesSince $backendBeforeLeave)
    $commands = @($backendLines | Where-Object { $_ -match 'bridge: <- (\w+)' } | ForEach-Object { if ($_ -match 'bridge: <- (\w+)') { $Matches[1] } })
    $fullscreenAt = [array]::IndexOf($commands, 'set_fullscreen'); $stopAt = [array]::IndexOf($commands, 'stop')
    Check 'backend log: set_fullscreen received, before the page''s stop' ($fullscreenAt -ge 0 -and ($stopAt -lt 0 -or $fullscreenAt -lt $stopAt)) ("commands after q: " + ($commands -join ', '))

    # 4. a normal close
    $tree = @(Get-Descendants $ui.Id)
    $ui.Refresh(); $hwnd = $ui.MainWindowHandle   # its window now (CloseMainWindow below posts to it)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = $ui.CloseMainWindow()
    $hiddenAt = $null
    while ($watch.Elapsed.TotalSeconds -lt $CloseSeconds -and -not $ui.HasExited) {
        if (-not $hiddenAt -and -not [UiHarnessNative]::IsWindowVisible($hwnd)) { $hiddenAt = $watch.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25; $ui.Refresh()
    }
    Check 'close: window hidden at once' ($null -ne $hiddenAt -and $hiddenAt -lt 1500) "after $hiddenAt ms"
    Check 'close: process exited' $ui.HasExited "after $([int]$watch.Elapsed.TotalMilliseconds) ms"
    Start-Sleep -Milliseconds 800
    $alive = @($tree | Where-Object { Get-TrackedProcess ([int]$_.ProcessId) })
    Check 'close: nothing it started survives' ($alive.Count -eq 0) $(if ($alive.Count) { ($alive | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',' } else { 'none' })
}

try {
    Invoke-Gate
}
finally {
    $ended = @(Close-Tracked $script:ui)
    if ($ended.Count) { "cleanup ended: $($ended -join ', ')" }
    "--- FluentFin log lines of this run (bridge commands, full screen, errors):"
    Get-LinesSince $appMark (Get-FluentFinLog) | Where-Object { $_ -match 'bridge: <- |full screen|fullscreen|\[ERR\]|\[WRN\]|Stopping the mpv backend|exited with code' } |
        ForEach-Object { $_.Substring(0, [Math]::Min(220, $_.Length)) }
}

$failed = @($results.Keys | Where-Object { -not $results[$_] })
"--- gate: $($results.Count - $failed.Count)/$($results.Count) passed$(if ($failed.Count) { '; FAILED: ' + ($failed -join '; ') })"
if ($failed.Count) { exit 1 }
