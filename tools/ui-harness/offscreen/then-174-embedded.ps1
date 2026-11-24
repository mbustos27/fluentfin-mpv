# Nixie 1.7.4 (the friend's report on 1.7.x, in a window about 1906x1016 with a black band down the left): with mpv's own
# controls, mpv draws in a child window FluentFin keeps over the player (MpvVideoHost). After each window change it must cover
# the player whole: the client's width, from under the title bar (none in full screen) to the client's foot, never a stale place.
#   run-offscreen.ps1 ... -Script season,play -NoA11y -Setting MpvPlayerControls=Mpv -Then offscreen\then-174-embedded.ps1
# Dot-sourced by drive.ps1 after the play step. Every name here starts with He.

if (-not ('HeWin' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class HeWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int cx, uint f);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public static int[] Client(IntPtr main) {
    RECT c; GetClientRect(main, out c); var p = new POINT(); ClientToScreen(main, ref p);
    return new[] { p.X, p.Y, p.X + c.R - c.L, p.Y + c.B - c.T };
  }
}
"@
}

# the video window against the client: left, right and foot flush (2 px), its top under the title bar (0 to 80 px down; flush in full screen)
function He-Check([string]$Case) {
    Start-Sleep -Milliseconds 1200
    $video = Get-VideoWindow
    if (-not $video) { Check "embedded | $Case | the video window is shown" $false 'no child window of the run''s mpv'; return }
    $v = New-Object HeWin+RECT; [void][HeWin]::GetWindowRect($video, [ref]$v)
    $c = [HeWin]::Client($script:hwnd)
    $full = (Get-WindowState $script:hwnd).FullScreen
    $top = $v.T - $c[1]
    $ok = [math]::Abs($v.L - $c[0]) -le 2 -and [math]::Abs($v.R - $c[2]) -le 2 -and [math]::Abs($v.B - $c[3]) -le 2 -and $(if ($full) { [math]::Abs($top) -le 2 } else { $top -ge 0 -and $top -le 80 })
    Check "embedded | $Case | the video window covers the player" $ok ("video {0},{1}-{2},{3}; client {4}; full screen {5}; dpi {6}" -f $v.L, $v.T, $v.R, $v.B, ($c -join ','), $full, [HeWin]::GetDpiForWindow($script:hwnd))
}

[void][HeWin]::ShowWindow($script:hwnd, 9)
[void][HeWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 7, 7, 1906, 1016, 0x14)
He-Check 'windowed 1906x1016'
foreach ($z in @(@(1700, 960), @(1500, 900), @(1300, 820), @(1100, 760), @(1600, 940), @(1906, 1016))) {
    [void][HeWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, $z[0], $z[1], 0x16); Start-Sleep -Milliseconds 45
}
He-Check 'after a live resize'
[void][HeWin]::ShowWindow($script:hwnd, 3); He-Check 'maximized'
[void][HeWin]::ShowWindow($script:hwnd, 9); He-Check 'restored'
Send-PlayerKey 0x46 'f'; [void](Wait-WindowState $script:hwnd $true 8); He-Check 'full screen (f)'
Send-PlayerKey 0x46 'f'; [void](Wait-WindowState $script:hwnd $false 8); He-Check 'windowed again (f)'
[void][HeWin]::ShowWindow($script:hwnd, 6); Start-Sleep -Milliseconds 1200; [void][HeWin]::ShowWindow($script:hwnd, 9); He-Check 'minimized and restored'
foreach ($m in @([OffscreenUi]::Monitors())) {
    if ($m -notmatch '^\S+ (-?\d+),(-?\d+) (\d+)x(\d+)') { continue }
    [void][HeWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, [int]$Matches[1] + 40, [int]$Matches[2] + 40, 1500, 900, 0x14)
    He-Check "moved to $m"
}
[void][HeWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 7, 7, 1906, 1016, 0x14)
He-Check 'windowed 1906x1016 again'
