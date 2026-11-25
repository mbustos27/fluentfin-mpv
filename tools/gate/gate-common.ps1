<#
  Nixie 1.7.5 (the basics gate, tools\gate\README.md): helpers a gate check's -Then script dot-sources. It runs inside
  tools\ui-harness\offscreen\drive.ps1 (its $script:hwnd, $script:ui, Say and Check) on the run's private desktop.

    . (Join-Path $PSScriptRoot '..\gate-common.ps1')     # from tools\gate\checks\

  Get-GateSizes           GATE_SIZES as a list: 'gate' (or unset) = 1280x720, 1920x1080, max; 'none' = none
  Set-GateSize <size>     'WxH' (the window's outer size, at the screen's origin) or 'max' (maximised: 2560 x 1392 on a
                          2560 x 1440 monitor); the layout settled; returns @{ Label; W; H; ClientW; ClientH; Dpi; Max }
  Invoke-GateLiveResize   the window's size dragged through several sizes in a row without waiting, then back
  Get-GateTheme           the run's theme (GATE_THEME, else NIXIE_LAYOUT)
  Format-GateSize $s      "1280x720 (client 1264x681, 96 dpi)" for a check's detail

  DPI: the private desktop has the monitors' own scale; 150% can't be set off-screen without changing the user's display,
  so a check runs at the PC's scale and names it (Format-GateSize).
#>

if (-not ('GateWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class GateWindow {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  // only Nixie's own window, the one this run started
  static void Own(IntPtr h, int pid) { uint p; GetWindowThreadProcessId(h, out p); if (p != (uint)pid) throw new InvalidOperationException("not this run's Nixie window"); }
  public static void Restore(IntPtr h, int pid) { Own(h, pid); ShowWindow(h, 9); }
  public static void Maximize(IntPtr h, int pid) { Own(h, pid); ShowWindow(h, 3); }
  // SWP_NOZORDER | SWP_NOACTIVATE
  public static void Size(IntPtr h, int pid, int w, int hh) { Own(h, pid); SetWindowPos(h, IntPtr.Zero, 0, 0, w, hh, 0x0004 | 0x0010); }
  public static int[] Client(IntPtr h) { RECT r; GetClientRect(h, out r); return new[] { r.R - r.L, r.B - r.T }; }
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  // the client area on the screen: left, top, right, bottom (UI Automation's rectangles are on the screen)
  public static int[] ClientOnScreen(IntPtr h) {
    RECT r; GetClientRect(h, out r); var p = new POINT(); ClientToScreen(h, ref p);
    return new[] { p.X, p.Y, p.X + r.R - r.L, p.Y + r.B - r.T };
  }
  public static int[] Outer(IntPtr h) { RECT r; GetWindowRect(h, out r); return new[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  public static bool Maximized(IntPtr h) { return IsZoomed(h); }
  public static uint Dpi(IntPtr h) { return GetDpiForWindow(h); }
}
'@
}

function Get-GateSizes {
    $v = "$env:GATE_SIZES".Trim()
    # 1.7.5 (the 90-minute budget): the sizes that matter, the smallest and the largest window
    if (-not $v -or $v -eq 'gate') { return @('1280x720', 'max') }
    if ($v -eq 'none') { return @() }
    @($v -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-GateTheme { if ($env:GATE_THEME) { $env:GATE_THEME } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'default' } }

function Get-GateWindowState {
    $c = [GateWindow]::Client($script:hwnd); $o = [GateWindow]::Outer($script:hwnd)
    @{ W = $o[2]; H = $o[3]; ClientW = $c[0]; ClientH = $c[1]; Dpi = [int][GateWindow]::Dpi($script:hwnd); Max = [GateWindow]::Maximized($script:hwnd) }
}

function Format-GateSize($State) {
    "{0} (client {1}x{2}, {3} dpi{4})" -f $State.Label, $State.ClientW, $State.ClientH, $State.Dpi, $(if ($State.Max) { ', maximised' })
}

# the window at a gate size, the layout given time to settle; what it got
function Set-GateSize([string]$Size, [int]$SettleMs = 1200) {
    if ($Size -eq 'max') {
        if (-not [GateWindow]::Maximized($script:hwnd)) { [GateWindow]::Maximize($script:hwnd, $script:ui.Id) }
    }
    elseif ($Size -match '^(\d+)x(\d+)$') {
        $w = [int]$Matches[1]; $h = [int]$Matches[2]
        if ([GateWindow]::Maximized($script:hwnd)) { [GateWindow]::Restore($script:hwnd, $script:ui.Id); Start-Sleep -Milliseconds 400 }
        [GateWindow]::Size($script:hwnd, $script:ui.Id, $w, $h)
    }
    else { throw "Set-GateSize: '$Size' is neither WxH nor max" }
    Start-Sleep -Milliseconds $SettleMs
    $s = Get-GateWindowState
    $s.Label = $Size
    Say "gate: window at $(Format-GateSize $s)"
    $s
}

# a live resize: the size changed $Steps times in a row, 40 ms apart (as a drag of the window's edge does), then back to
# where it was (maximised again if it was), the layout given time to settle
function Invoke-GateLiveResize([int]$Steps = 6, [int]$SettleMs = 1500) {
    $was = Get-GateWindowState
    if ($was.Max) { [GateWindow]::Restore($script:hwnd, $script:ui.Id); Start-Sleep -Milliseconds 300 }
    $from = [GateWindow]::Outer($script:hwnd)
    $sizes = @(@(1280, 720), @(1600, 900), @(1366, 768), @(1920, 1080), @(1100, 800), @(1700, 1000), @(1440, 900), @(1280, 1000))
    for ($i = 0; $i -lt $Steps; $i++) {
        $s = $sizes[$i % $sizes.Count]
        [GateWindow]::Size($script:hwnd, $script:ui.Id, $s[0], $s[1])
        Start-Sleep -Milliseconds 40
    }
    if ($was.Max) { [GateWindow]::Maximize($script:hwnd, $script:ui.Id) } else { [GateWindow]::Size($script:hwnd, $script:ui.Id, $from[2], $from[3]) }
    Start-Sleep -Milliseconds $SettleMs
    $s = Get-GateWindowState
    $s.Label = 'after a live resize'
    Say "gate: live resize ($Steps sizes), back at $(Format-GateSize $s)"
    $s
}
