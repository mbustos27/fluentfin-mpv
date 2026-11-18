<#
  Screenshot of one window, rendered by the window itself (PrintWindow with PW_RENDERFULLCONTENT, the DWM content of the
  window and its child windows, mpv's video in its embedded child included), never a copy of the screen: whatever is
  on top of the window (a notification, another app) is not in the picture.
  The window: -Handle, else the main window of -ProcessId, else of the newest process named -ProcessName.
  -Front brings it to the foreground first (for the keys a caller sends next; the capture does not need it).
  Writes a PNG to -Out and prints its rectangle, then one line per child window of another process (the embedded mpv):
  where it is and how much of it is not black in the capture. Throws when there is no window or the capture fails.
  Usage: shot.ps1 -Out <file.png> [-ProcessId <pid> | -Handle <hwnd> | -ProcessName Nixie] [-Front]
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [int]$ProcessId = 0,
    [long]$Handle = 0,
    [string]$ProcessName = 'Nixie',
    [switch]$Front
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('UiHarnessPrint' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UiHarnessPrint {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public const uint PW_RENDERFULLCONTENT = 2;

  public static List<IntPtr> Children(IntPtr parent) {
    var list = new List<IntPtr>();
    EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }

  public static uint ProcessOf(IntPtr h) { uint pid; GetWindowThreadProcessId(h, out pid); return pid; }

  public static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
}
"@
}
# physical pixels, so the rectangle matches what the window renders on a scaled display
[UiHarnessPrint]::SetProcessDPIAware() | Out-Null

if ($Handle -ne 0) {
    $h = [IntPtr]$Handle
}
else {
    $process = if ($ProcessId -ne 0) { Get-Process -Id $ProcessId } else {
        Get-Process -Name $ProcessName | Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero } | Sort-Object StartTime | Select-Object -Last 1
    }
    if (-not $process) { throw "No $ProcessName window" }
    $process.Refresh()
    $h = $process.MainWindowHandle
}
if ($h -eq [IntPtr]::Zero -or -not [UiHarnessPrint]::IsWindow($h)) { throw "No window to capture (handle $h)" }

if ($Front) { [UiHarnessPrint]::SetForegroundWindow($h) | Out-Null; Start-Sleep -Milliseconds 400 }

$r = New-Object UiHarnessPrint+RECT
[UiHarnessPrint]::GetWindowRect($h, [ref]$r) | Out-Null
$width = $r.R - $r.L; $height = $r.B - $r.T
if ($width -le 0 -or $height -le 0) { throw "The window has no area ($width x $height)" }

$directory = Split-Path -Parent ([IO.Path]::GetFullPath($Out))
New-Item -ItemType Directory -Force $directory | Out-Null
# 24 bpp: GDI leaves the alpha of a 32 bpp bitmap at 0, which would save a transparent PNG
$bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $hdc = $graphics.GetHdc()
        try { $printed = [UiHarnessPrint]::PrintWindow($h, $hdc, [UiHarnessPrint]::PW_RENDERFULLCONTENT) } finally { $graphics.ReleaseHdc($hdc) }
    }
    finally { $graphics.Dispose() }
    if (-not $printed) { throw "PrintWindow failed for window $h" }
    $bitmap.Save([IO.Path]::GetFullPath($Out), [System.Drawing.Imaging.ImageFormat]::Png)

    "window $($r.L),$($r.T) ${width}x${height} -> $Out"
    # the embedded video: child windows of another process, sampled on a grid in the capture
    $own = [UiHarnessPrint]::ProcessOf($h)
    $foreign = @([UiHarnessPrint]::Children($h) | Where-Object { [UiHarnessPrint]::ProcessOf($_) -ne $own -and [UiHarnessPrint]::IsWindowVisible($_) })
    if (-not $foreign.Count) { "no visible child window of another process" }
    foreach ($child in $foreign) {
        $c = New-Object UiHarnessPrint+RECT
        [UiHarnessPrint]::GetWindowRect($child, [ref]$c) | Out-Null
        $x0 = [Math]::Max(0, $c.L - $r.L); $y0 = [Math]::Max(0, $c.T - $r.T)
        $x1 = [Math]::Min($width, $c.R - $r.L); $y1 = [Math]::Min($height, $c.B - $r.T)
        $samples = 0; $lit = 0; $colours = @{}
        if ($x1 -gt $x0 -and $y1 -gt $y0) {
            for ($i = 0; $i -lt 40; $i++) {
                for ($j = 0; $j -lt 40; $j++) {
                    $p = $bitmap.GetPixel([int]($x0 + ($x1 - $x0 - 1) * $i / 39), [int]($y0 + ($y1 - $y0 - 1) * $j / 39))
                    $samples++
                    if ($p.R + $p.G + $p.B -gt 48) { $lit++ }
                    $colours[$p.ToArgb() -band 0xF0F0F0] = 1
                }
            }
        }
        $pid_ = [UiHarnessPrint]::ProcessOf($child)
        $name = try { (Get-Process -Id $pid_ -ErrorAction Stop).ProcessName } catch { '?' }
        "child $([UiHarnessPrint]::ClassOf($child)) of $name#$pid_ at $($c.L),$($c.T) $($c.R - $c.L)x$($c.B - $c.T): $([int](100 * $lit / [Math]::Max(1, $samples)))% not black, $($colours.Count) colours"
    }
}
finally {
    $bitmap.Dispose()
}
