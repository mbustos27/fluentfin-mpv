<#
  Tests of offscreen\uia.ps1's choice of the window posted keys go to: OffscreenUi.PickKeyWindow on made-up child window
  lists, and OffscreenUi.Key itself on windows this script makes in its own process (a popup that is never shown, with a
  bridge child and an input-site grandchild, as FluentFin's window has them): the window Key returns and the one its
  WM_KEYDOWN is queued for. Starts no process, shows no window, and destroys its windows at the end.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-harness\offscreen\test_uia.ps1

  Prints PASS/FAIL per check; exit code 1 when any failed. -Uia <copy of uia.ps1> tests another copy.
#>
param([string]$Uia)
$ErrorActionPreference = 'Stop'
if (-not $Uia) { $Uia = Join-Path $PSScriptRoot 'uia.ps1' }
. $Uia
$script:failed = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if (-not $Ok) { $script:failed++ }
    Write-Host ("{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" }))
}
$bridge = 'Microsoft.UI.Content.DesktopChildSiteBridge'; $site = 'InputSiteWindowClass'
function Pick([string[]]$Classes, [uint32[]]$Pids, [uint32]$AppPid = 10) { [OffscreenUi]::PickKeyWindow($Classes, $Pids, $AppPid) }

# FluentFin's window as it is: the bridge, then its hidden input site (posted to the bridge, F and Esc never reached the
# player page)
$i = Pick @($bridge, $site) @(10, 10)
Check 'the input site is chosen over the bridge before it' ($i -eq 1) "$i"
$i = Pick @('mpv', $bridge, 'Other', $site) @(20, 10, 10, 10)
Check 'the input site is chosen with the video window and other children around it' ($i -eq 3) "$i"
$i = Pick @($site, $bridge, $site) @(20, 10, 10)
Check "another process's input site (mpv's or anything embedded) is never chosen" ($i -eq 2) "$i"
$i = Pick @($bridge, 'Other') @(10, 10)
Check 'no input site: the bridge' ($i -eq 0) "$i"
$i = Pick @($site, $bridge) @(20, 20)
Check 'none of the app''s own: -1 (the caller posts to the main window)' ($i -eq -1) "$i"
$i = Pick @() @()
Check 'no children: -1' ($i -eq -1) "$i"

# --- Key itself, on windows of this process (never shown) --------------------------------------------------------------
if (-not ('UiaTestWindows' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class UiaTestWindows {
  delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WNDCLASSEX { public uint cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
  [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; public uint priv; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
  [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string n);
  static readonly WndProc Proc = DefWindowProc;   // kept alive for the classes' lifetime
  // a window of class cls (registered here; a class already registered is reused): a popup without WS_VISIBLE when there
  // is no parent, else a child (hidden, as its parent)
  public static IntPtr Create(string cls, IntPtr parent) {
    var c = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)), lpfnWndProc = Proc, hInstance = GetModuleHandle(null), lpszClassName = cls };
    RegisterClassEx(ref c);
    return CreateWindowEx(0, cls, "", parent == IntPtr.Zero ? 0x80000000u : 0x40000000u, 0, 0, 10, 10, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
  }
  // takes the WM_KEYDOWN messages queued for window h; returns their virtual-key codes
  public static int[] KeyDowns(IntPtr h) {
    var l = new System.Collections.Generic.List<int>(); MSG m;
    while (PeekMessage(out m, h, 0x0100, 0x0100, 1)) l.Add((int)m.wParam);
    return l.ToArray();
  }
}
"@
}
$made = New-Object System.Collections.ArrayList
function Win([string]$Class, [IntPtr]$Parent = [IntPtr]::Zero) { $h = [UiaTestWindows]::Create($Class, $Parent); if ($h -eq [IntPtr]::Zero) { throw "could not make a $Class window" }; [void]$made.Add($h); $h }
try {
    $main = Win 'UiaTestMain'
    $bridgeWin = Win $bridge $main
    $siteWin = Win $site $bridgeWin
    $sent = [OffscreenUi]::Key($main, $PID, 0x46, $false)
    Check 'Key posts to the input site (the bridge''s hidden child) and returns it' ($sent -eq $siteWin) ("returned {0} ({1})" -f $sent, [OffscreenUi]::ClassOf($sent))
    $onSite = @([UiaTestWindows]::KeyDowns($siteWin)); $onBridge = @([UiaTestWindows]::KeyDowns($bridgeWin)); $onMain = @([UiaTestWindows]::KeyDowns($main))
    Check 'its WM_KEYDOWN is queued for the input site, none for the bridge or the main window' ($onSite.Count -eq 1 -and $onSite[0] -eq 0x46 -and $onBridge.Count -eq 0 -and $onMain.Count -eq 0) "input site $($onSite -join ','); bridge $($onBridge -join ','); main $($onMain -join ',')"
    $main2 = Win 'UiaTestMain'
    $bridge2 = Win $bridge $main2
    Check 'no input site: Key posts to the bridge' (([OffscreenUi]::Key($main2, $PID, 0x46, $false)) -eq $bridge2)
    $main3 = Win 'UiaTestMain'
    Check 'no child windows: Key posts to the main window' (([OffscreenUi]::Key($main3, $PID, 0x46, $false)) -eq $main3)
    Check 'none of the test windows was ever shown' (-not @($made | Where-Object { [UiaTestWindows]::IsWindowVisible($_) }).Count)
    $threw = try { [void][OffscreenUi]::Key($main, $PID + 1, 0x46, $false); $false } catch { $_.Exception.Message -match "not FluentFin's window" }
    Check 'Key refuses a window of another process than the one named' $threw
}
finally {
    foreach ($h in @($made)[($made.Count - 1)..0]) { [void][UiaTestWindows]::DestroyWindow($h) }
}

Write-Host ("{0} failed" -f $script:failed)
if ($script:failed) { exit 1 }
