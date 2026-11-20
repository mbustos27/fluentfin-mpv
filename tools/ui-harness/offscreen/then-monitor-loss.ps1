<#
  sg-parallel (bug hunt m10's cause): drive.ps1's answer to Windows minimizing FluentFin's window when its monitor drops off the
  bus, on the real app. -Script home -Then offscreen\then-monitor-loss.ps1 (any NIXIE_LAYOUT).
    1. The window minimized and its monitor gone (the monitor it was last seen on made up as one that is not there: a monitor
       cannot be unplugged from here): Shot restores it first, takes a shot of the window at its size before, and the run
       records one display event saying why (result.json display.events; run-offscreen.ps1 prints it).
    2. The window minimized with every monitor there (as the app or a test of its own would): nothing restores it, Shot
       refuses it; then this script restores it so the run goes on.
  Dot-sourced by drive.ps1: no assignment of drive.ps1's parameters at the top level (offscreen\test_then_names.ps1).
#>
if (-not ('MlWindow' -as [type])) {
    Add-Type 'using System; using System.Runtime.InteropServices; public static class MlWindow { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd); [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; } [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r); public static string Size(IntPtr h) { RECT r; GetWindowRect(h, out r); return (r.R - r.L) + "x" + (r.B - r.T); } }'
}
function ML-Minimize {
    [void][MlWindow]::ShowWindow($script:hwnd, 6)   # SW_MINIMIZE, on FluentFin's own window (the run started it)
    [bool](Wait-Until { if ([OffscreenUi]::IsIconic($script:hwnd)) { $true } } -Seconds 5 -PollMs 100)
}

$mlSize = [MlWindow]::Size($script:hwnd)
$mlEvents = $script:display.events.Count
$mlShotsBefore = $shots.Count

# 1. its monitor gone
$mlMin = ML-Minimize
$script:shownMonitor = '\\.\DISPLAY-GONE 0,0 2560x1440 primary'   # what it was last seen on: a monitor that is not there
Shot 'monitor-gone'
$mlEvent = if ($script:display.events.Count -gt $mlEvents) { $script:display.events[$script:display.events.Count - 1] } else { $null }
Check 'monitor loss: the window was minimized for the check' $mlMin
Check 'monitor loss: Shot restored the window first and took its shot at the size it had' (-not [OffscreenUi]::IsIconic($script:hwnd) -and $shots.Contains('monitor-gone') -and [MlWindow]::Size($script:hwnd) -eq $mlSize) "before $mlSize, now $([MlWindow]::Size($script:hwnd))"
Check 'monitor loss: one display event, saying the monitor went away, restored' ($mlEvent -and $script:display.events.Count -eq $mlEvents + 1 -and $mlEvent.why -match 'DISPLAY-GONE' -and $mlEvent.restored) $(if ($mlEvent) { $mlEvent.why } else { 'none' })

# 2. minimized with every monitor there: left alone, Shot refuses it
$mlMin = ML-Minimize
$mlBefore = $checks.Count
$mlResolved = Resolve-WindowsMinimize 'then-monitor-loss: own minimize'
Shot 'own-minimize'
$mlRefusal = $checks['shot own-minimize: the window is shown (not minimized)']
Check 'own minimize: nothing restores a window no monitor explains' ($mlMin -and -not $mlResolved -and [OffscreenUi]::IsIconic($script:hwnd) -and $script:display.events.Count -eq $mlEvents + 1)
Check 'own minimize: Shot refuses it (the FAIL a run would show)' ($mlRefusal -and -not $mlRefusal.ok -and -not $shots.Contains('own-minimize')) $(if ($mlRefusal) { $mlRefusal.detail } else { 'no refusal' })
# that refusal is this script's own doing: kept as a PASS so the run's verdict is the checks above
if ($mlRefusal) { $checks['shot own-minimize: the window is shown (not minimized)'] = [ordered]@{ ok = $true; detail = "expected here (then-monitor-loss): $($mlRefusal.detail)" } }
[void][OffscreenUi]::Restore($script:hwnd, $script:ui.Id)
$mlBack = [bool](Wait-Until { if (-not [OffscreenUi]::IsIconic($script:hwnd)) { $true } } -Seconds 5 -PollMs 100)
Start-Sleep -Milliseconds 800
Check 'own minimize: restored by this script for the rest of the run' ($mlBack -and [MlWindow]::Size($script:hwnd) -eq $mlSize) ([MlWindow]::Size($script:hwnd))
Shot 'after-monitor-loss'
