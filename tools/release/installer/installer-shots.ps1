<#
.SYNOPSIS
  Screenshots of the installer's pages, taken with PrintWindow from the window of the msiexec this script started.

.DESCRIPTION
  Run it on a private desktop so nothing appears on yours (docs\RELEASE.md):
    python tools\backend-bench\hidden_run.py --timeout 240 -- powershell -NoProfile -ExecutionPolicy Bypass -File installer-shots.ps1 -Msi <msi> -Out <folder>
  It only ever runs an ISOLATED copy of the installer (upgrade-test.ps1's self-check build: product "SeaglassUpgradeCheck", its
  own upgrade code): it refuses an MSI whose upgrade code is Seaglass's, so an installed Seaglass can not be touched. Pages
  captured: welcome, progress (if it stays up long enough to catch), finish with "Launch Seaglass" ticked and unticked, the
  cancel question and "setup was interrupted". Clicks are BM_CLICK messages posted to the dialog's own buttons (no keyboard or
  mouse input). The test product is uninstalled at the end (by the product code of the MSI it installed).
  Usage: installer-shots.ps1 -Msi <isolated msi> -Out <folder>
#>
param([Parameter(Mandatory)][string]$Msi, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\msi-tools.ps1')
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ShotWin {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
  public static void Init() { SetProcessDPIAware(); }
  public static List<IntPtr> Tops(uint pid) {
    var r = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
  public static List<IntPtr> Kids(IntPtr parent) {
    var r = new List<IntPtr>();
    EnumChildWindows(parent, (h, l) => { r.Add(h); return true; }, IntPtr.Zero);
    return r;
  }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Text(IntPtr h) { var s = new StringBuilder(1024); GetWindowText(h, s, 1024); return s.ToString(); }
  public static void Click(IntPtr h) { PostMessage(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
  public static int Checked(IntPtr h) { return (int)SendMessage(h, 0x00F0, IntPtr.Zero, IntPtr.Zero); }
  public static int[] Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return new int[] { r.L, r.T, r.R - r.L, r.B - r.T }; }
  public static bool Print(IntPtr h, string path) {
    var rc = Rect(h);
    using (var bmp = new System.Drawing.Bitmap(rc[2], rc[3]))
    using (var g = System.Drawing.Graphics.FromImage(bmp)) {
      IntPtr dc = g.GetHdc();
      bool ok = PrintWindow(h, dc, 2);
      g.ReleaseHdc(dc);
      bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
      return ok;
    }
  }
}
"@ -ReferencedAssemblies System.Drawing
[ShotWin]::Init()

$Msi = [IO.Path]::GetFullPath($Msi); $Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null
$upgradeCode = Get-MsiProperty $Msi 'UpgradeCode'
if ($upgradeCode -eq '{612ED1CB-52F7-49B1-9129-A1B04ADB015A}') { throw 'this MSI is the real Seaglass (its upgrade code): installer-shots.ps1 only runs an isolated test build (upgrade-test.ps1 -SelfCheck makes one)' }
$name = Get-MsiProperty $Msi 'ProductName'
$code = Get-MsiProperty $Msi 'ProductCode'
$reasons = $null
if (-not (Test-NothingInstalled $upgradeCode @($name) ([ref]$reasons))) { throw "$name is already installed: $($reasons -join '; ')" }

function Buttons($top) {
    $list = @()
    foreach ($k in [ShotWin]::Kids($top)) { if ([ShotWin]::Cls($k) -eq 'Button') { $list += [pscustomobject]@{ H = $k; Text = ([ShotWin]::Text($k) -replace '&', '') } } }
    return $list
}
# the newest visible window of the process that has buttons, and a signature of its buttons (a page changed when it does)
function Get-Page([int]$procId) {
    foreach ($top in [ShotWin]::Tops([uint32]$procId)) {
        $b = @(Buttons $top)
        if ($b.Count -gt 0) { return [pscustomobject]@{ Top = $top; Buttons = $b; Sig = (($b | ForEach-Object Text | Sort-Object) -join '|') } }
    }
    return $null
}
function Wait-Page([int]$procId, [string]$buttonText, [int]$seconds = 30) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        $p = Get-Page $procId
        if ($p -and ($p.Buttons | Where-Object { $_.Text -eq $buttonText })) { Start-Sleep -Milliseconds 400; return (Get-Page $procId) }
        if ((Get-Process -Id $procId -ErrorAction SilentlyContinue) -eq $null) { return $null }
        Start-Sleep -Milliseconds 100
    }
    throw "no page with a '$buttonText' button within $seconds s"
}
function Shoot($page, [string]$file) {
    $path = Join-Path $Out $file
    [void][ShotWin]::Print($page.Top, $path)
    $names = ($page.Buttons | ForEach-Object Text) -join ', '
    $size = [ShotWin]::Rect($page.Top) -join 'x'
    Write-Host "shot $file  buttons: $names  size $size"
}
function Press($page, [string]$text) { [ShotWin]::Click(($page.Buttons | Where-Object { $_.Text -eq $text } | Select-Object -First 1).H) }

$started = $null
try {
    # --- the install: welcome, progress, finish
    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/norestart') -PassThru
    $started = $p
    $page = Wait-Page $p.Id 'Install'
    Shoot $page '01-welcome.png'
    Press $page 'Install'
    # progress: poll quickly for a page that is neither welcome nor finish
    $progressShot = $false
    $until = (Get-Date).AddSeconds(60)
    $finish = $null
    while ((Get-Date) -lt $until -and -not $finish) {
        $pg = Get-Page $p.Id
        if ($pg -and ($pg.Buttons | Where-Object { $_.Text -eq 'Finish' })) { $finish = $pg; break }
        if ($pg -and -not $progressShot -and -not ($pg.Buttons | Where-Object { $_.Text -eq 'Install' })) {
            Start-Sleep -Milliseconds 300; $pg = Get-Page $p.Id
            if ($pg -and -not ($pg.Buttons | Where-Object { $_.Text -in 'Install', 'Finish' })) { Shoot $pg '02-progress.png'; $progressShot = $true }
        }
        Start-Sleep -Milliseconds 80
    }
    if (-not $finish) { throw 'the finish page never came' }
    Start-Sleep -Milliseconds 500; $finish = Get-Page $p.Id
    $box = $finish.Buttons | Where-Object { $_.Text -eq 'Launch Seaglass' }
    Write-Host ("Launch Seaglass box: {0}" -f $(if ($box -and [ShotWin]::Checked($box.H) -eq 1) { 'ticked' } else { 'NOT ticked' }))
    Shoot $finish '03-finish-launch-ticked.png'
    # untick (so Finish starts nothing), then finish
    if ($box) { [ShotWin]::Click($box.H); Start-Sleep -Milliseconds 500; Shoot (Get-Page $p.Id) '04-finish-unticked.png' }
    Press (Get-Page $p.Id) 'Finish'
    if (-not $p.WaitForExit(30000)) { throw 'msiexec did not end after Finish' }
    Write-Host "install exit code $($p.ExitCode)"
    $started = $null

    # --- uninstall the test product (silent), then the cancel path
    $u = Start-Process msiexec.exe -ArgumentList @('/x', $code, '/qn', '/norestart') -Wait -PassThru -WindowStyle Hidden
    Write-Host "uninstall exit code $($u.ExitCode)"
    $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$Msi`"", '/norestart') -PassThru
    $started = $p
    $page = Wait-Page $p.Id 'Install'
    Press $page 'Cancel'
    $ask = Wait-Page $p.Id 'Yes'
    Shoot $ask '05-cancel-question.png'
    Press $ask 'Yes'
    $page = Wait-Page $p.Id 'Finish'
    Shoot $page '06-interrupted.png'
    Press $page 'Finish'
    [void]$p.WaitForExit(30000)
    Write-Host "cancelled install exit code $($p.ExitCode)"
    $started = $null
}
finally {
    if ($started -and -not $started.HasExited) { try { $started.Kill() } catch { } }   # the process this script started
    if (Get-ProductInfo $code 'ProductName') {
        Write-Host "(cleanup) uninstalling the test product $code"
        [void](Start-Process msiexec.exe -ArgumentList @('/x', $code, '/qn', '/norestart') -Wait -PassThru -WindowStyle Hidden)
    }
}
