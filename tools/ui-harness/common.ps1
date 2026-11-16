<#
  Dot-sourced by the UI harness scripts: the processes a script started (FluentFin and its descendants), closing them,
  FluentFin's window state, keys, and the two logs.

  Process safety: a script ends, suspends or sends keys only to a process it can prove it started, and acts on it only
  through the handle it checked. Every tracked process is held by one handle, opened once and kept (FluentFin's is the
  one Start-Process started it with). Through that handle it must have been created after the script began
  (GetProcessTimes) and, for a descendant found in a process snapshot, at exactly the creation time the snapshot showed,
  so a pid Windows reused after the snapshot is refused. While the handle is open Windows cannot hand the pid to another
  process, so the pid-based window lookups that follow (CloseMainWindow, the keys' target) reach that same process.
  Suspend, resume, wait and terminate go through the handle itself, never through a pid opened again.
#>

Add-Type -AssemblyName System.Windows.Forms, UIAutomationClient, UIAutomationTypes
if (-not ('UiHarnessNative' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class UiHarnessNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
  [StructLayout(LayoutKind.Sequential)] public struct PROCESS_BASIC_INFORMATION { public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO info);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
  [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
  [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, out PROCESS_BASIC_INFORMATION info, int length, out int returned);
  [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);
  [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr h, uint code);
  [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll")] static extern void GetSystemTimeAsFileTime(out long time);

  // the clock the kernel stamps a process's creation with (FILETIME UTC)
  public static long Now() { long t; GetSystemTimeAsFileTime(out t); return t; }
  // creation time (FILETIME UTC) of the process behind handle h, 0 if unknown
  public static long CreationOf(IntPtr h) { long c, e, k, u; return GetProcessTimes(h, out c, out e, out k, out u) ? c : 0; }
  // parent pid recorded at creation of the process behind handle h, -1 if unknown
  public static long ParentOf(IntPtr h) {
    PROCESS_BASIC_INFORMATION i; int n;
    return NtQueryInformationProcess(h, 0, out i, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out n) == 0 ? i.InheritedFromUniqueProcessId.ToInt64() : -1;
  }
  // full path of the executable of the process behind handle h, "" if unknown
  public static string ImageOf(IntPtr h) { var s = new StringBuilder(1024); uint n = (uint)s.Capacity; return QueryFullProcessImageNameW(h, 0, s, ref n) ? s.ToString() : ""; }
  public static bool IsRunning(IntPtr h) { return WaitForSingleObject(h, 0) == 0x102; }   // WAIT_TIMEOUT
  public static uint WindowProcess(IntPtr w) { uint pid; GetWindowThreadProcessId(w, out pid); return pid; }
}
"@
}
[UiHarnessNative]::SetProcessDPIAware() | Out-Null

$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:DefaultExe = Join-Path $script:RepoRoot 'ui\FluentFin\bin\x64\Debug\net10.0-windows10.0.19041.0\Seaglass.exe'
$script:LogDir = Join-Path $env:LOCALAPPDATA 'Seaglass\Logs'

# pid -> [pscustomobject]@{ Process = the Process object whose handle is held; Created = its creation time; Name }
$script:Tracked = @{}
# when the script began, on the clock process creation times are stamped with: nothing it starts is older
$script:HarnessStartedFt = [UiHarnessNative]::Now()
$script:HarnessStartedAt = [DateTime]::FromFileTimeUtc($script:HarnessStartedFt).ToLocalTime()

# Tracks a process: -Process (a Process object; Start-Process's keeps the handle it started the process with) or -Id
# (opened now, once). Through that handle it must have been created at or after the script began and, with -Created
# (FILETIME UTC as a snapshot showed it, to the microsecond), at that very time; else it is not tracked, so it is never
# touched. Outputs nothing: $script:Tracked.ContainsKey tells.
function Add-Tracked([int]$Id = 0, [long]$Created = 0, $Process = $null) {
    if ($Process) { $Id = $Process.Id }
    if ($script:Tracked.ContainsKey($Id)) { return }
    $p = $Process
    try {
        if (-not $p) { $p = Get-Process -Id $Id -ErrorAction Stop }
        $h = $p.Handle   # opened once (if Start-Process did not already hold it) and cached by the Process object
        $at = [UiHarnessNative]::CreationOf($h)
        if ($at -ne 0 -and $at -ge $script:HarnessStartedFt -and ($Created -eq 0 -or [Math]::Abs($at - $Created) -lt 10)) {
            $script:Tracked[$Id] = [pscustomobject]@{ Process = $p; Created = $at; Name = [IO.Path]::GetFileNameWithoutExtension([UiHarnessNative]::ImageOf($h)) }
            return
        }
    }
    catch {}
    if ($p -and -not $Process) { $p.Dispose() }
}

# The tracked Process object of pid $Id while it still runs (asked through its held handle), else $null.
function Get-TrackedProcess([int]$Id) {
    $entry = $script:Tracked[$Id]
    if ($entry -and [UiHarnessNative]::IsRunning($entry.Process.Handle)) { return $entry.Process }
    $null
}

# Forgets every tracked process and releases its handle (scripts that run several times call it between runs).
function Reset-Tracked {
    foreach ($entry in @($script:Tracked.Values)) { try { $entry.Process.Dispose() } catch {} }
    $script:Tracked = @{}
}

# Descendants of $Root, which must be tracked (CIM objects with ProcessId, ParentProcessId, Name, ExecutablePath,
# CreationDate); all of them get tracked and only the tracked ones are returned. A process is a child only if it is not
# older than its parent: Windows keeps a process's parent pid after the parent exits, and that pid can be reused by a
# process this script started, which made an unrelated older process (and its children) look like ours, and
# Close-Tracked ended them (it ended the user's Copilot app on 2026-11-14). Within one snapshot every pid is one process;
# Add-Tracked then requires the snapshot's creation time through the handle it opens.
function Get-Descendants([int]$Root) {
    $rootEntry = $script:Tracked[$Root]
    if (-not $rootEntry) { return @() }
    $all = @(Get-CimInstance Win32_Process | Where-Object { $_.CreationDate } | Select-Object ProcessId, ParentProcessId, Name, ExecutablePath, CreationDate)
    $rootProcess = $all | Where-Object { [int]$_.ProcessId -eq $Root } | Select-Object -First 1
    if (-not $rootProcess -or [Math]::Abs($rootProcess.CreationDate.ToFileTime() - $rootEntry.Created) -ge 10) { return @() }
    $found = @(); $frontier = @($rootProcess)
    while ($frontier.Count -gt 0) {
        $next = @()
        foreach ($parent in $frontier) {
            foreach ($p in $all) {
                if ([int]$p.ParentProcessId -eq [int]$parent.ProcessId -and [int]$p.ProcessId -ne $Root -and
                    $found.ProcessId -notcontains $p.ProcessId -and $p.CreationDate -ge $parent.CreationDate) {
                    $found += $p; $next += $p
                }
            }
        }
        $frontier = $next
    }
    foreach ($p in $found) { Add-Tracked -Id ([int]$p.ProcessId) -Created $p.CreationDate.ToFileTime() }
    @($found | Where-Object { $script:Tracked.ContainsKey([int]$_.ProcessId) })
}

# Starts $Exe and tracks it. One that fails the tracking check is ended before the throw, through the handle Start-Process
# started it with (never its pid): untracked, nothing later -- Close-Tracked included -- would ever close it.
function Start-FluentFin([string]$Exe, [string[]]$Arguments) {
    $p = Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru
    Add-Tracked -Process $p   # the handle Start-Process started it with; it also keeps ExitCode readable after the exit
    if (-not $script:Tracked.ContainsKey($p.Id)) {
        $id = $p.Id
        $what = 'ended'
        try {
            $h = $p.Handle
            if (-not [UiHarnessNative]::IsRunning($h)) { $what = 'already exited' }
            elseif (-not [UiHarnessNative]::TerminateProcess($h, 1)) { $what = 'NOT ended (TerminateProcess failed)' }
            elseif ([UiHarnessNative]::WaitForSingleObject($h, 5000) -ne 0) { $what = 'NOT seen to exit within 5 s' }
        }
        catch { $what = "NOT ended ($($_.Exception.Message))" }
        try { $p.Dispose() } catch {}
        throw "FluentFin#$id did not check out as started by this script; $what"
    }
    $p
}

# Waits for the mpv.exe under $Ui; $null after $Seconds.
function Wait-Mpv($Ui, [int]$Seconds = 30) {
    for ($i = 0; $i -lt $Seconds * 2; $i++) {
        Start-Sleep -Milliseconds 500
        $ui.Refresh(); if ($Ui.HasExited) { return $null }
        $mpv = Get-Descendants $Ui.Id | Where-Object Name -eq 'mpv.exe' | Select-Object -First 1
        if ($mpv) { return $mpv }
    }
    $null
}

# NtSuspendProcess through the tracked handle; its NTSTATUS (0 = suspended).
function Suspend-Tracked([int]$Id) {
    $p = Get-TrackedProcess $Id
    if (-not $p) { throw "pid $Id is not a running process this script started" }
    [UiHarnessNative]::NtSuspendProcess($p.Handle)
}

# TerminateProcess through the tracked handle; $true when it was ended.
function Stop-Tracked([int]$Id) {
    $p = Get-TrackedProcess $Id
    if (-not $p) { throw "pid $Id is not a running process this script started" }
    [UiHarnessNative]::TerminateProcess($p.Handle, 1)
}

# Closes $Ui the normal way (up to $Seconds), then ends every tracked process still running (resumed first, in case a
# script suspended it). $Ui is acted on only if it is tracked. Returns the names of the processes that had to be ended.
# Never throws.
function Close-Tracked($Ui, [int]$Seconds = 16) {
    $ended = @()
    try {
        $p = if ($Ui) { Get-TrackedProcess ([int]$Ui.Id) } else { $null }
        if ($p) {
            $p.Refresh()   # its main window looked up now (by the pid its held handle keeps), not a cached one
            $null = $p.CloseMainWindow()
            if ([UiHarnessNative]::WaitForSingleObject($p.Handle, [uint32]($Seconds * 1000)) -ne 0) {
                $ended += "FluentFin#$($p.Id) (did not close in $Seconds s)"
                $null = [UiHarnessNative]::TerminateProcess($p.Handle, 1)
            }
        }
    }
    catch {}
    Start-Sleep -Milliseconds 800
    foreach ($id in @($script:Tracked.Keys)) {
        $p = Get-TrackedProcess $id
        if (-not $p) { continue }
        try {
            $h = $p.Handle
            $null = [UiHarnessNative]::NtResumeProcess($h)
            if ([UiHarnessNative]::TerminateProcess($h, 1)) { $ended += "$($script:Tracked[$id].Name)#$id" }
        }
        catch {}
    }
    $ended
}

# Window rectangle and whether it covers its whole monitor (the full-screen presenter does).
function Get-WindowState([IntPtr]$Hwnd) {
    $r = New-Object UiHarnessNative+RECT
    [UiHarnessNative]::GetWindowRect($Hwnd, [ref]$r) | Out-Null
    $info = New-Object UiHarnessNative+MONITORINFO
    $info.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][UiHarnessNative+MONITORINFO])
    [UiHarnessNative]::GetMonitorInfo([UiHarnessNative]::MonitorFromWindow($Hwnd, 2), [ref]$info) | Out-Null
    $m = $info.rcMonitor
    [pscustomobject]@{
        Rect       = "$($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"
        Monitor    = "$($m.L),$($m.T) $($m.R - $m.L)x$($m.B - $m.T)"
        FullScreen = ($r.L -le $m.L -and $r.T -le $m.T -and $r.R -ge $m.R -and $r.B -ge $m.B)
        Visible    = [UiHarnessNative]::IsWindowVisible($Hwnd)
    }
}

function Wait-WindowState([IntPtr]$Hwnd, [bool]$FullScreen, [int]$Seconds = 6) {
    for ($i = 0; $i -lt $Seconds * 4; $i++) {
        $state = Get-WindowState $Hwnd
        if ($state.FullScreen -eq $FullScreen) { return $state }
        Start-Sleep -Milliseconds 250
    }
    Get-WindowState $Hwnd
}

# Sends $Keys (SendKeys syntax) to window $Hwnd of a process this script started, and only while a window of that
# process is the foreground window: SendKeys types into whatever window is in front, so nothing is sent while another
# app is there (the user's, a notification). Brings $Hwnd to the front and checks again for up to $Seconds; returns
# $true when the keys were sent, $false (nothing sent) otherwise.
function Send-Keys([IntPtr]$Hwnd, [string]$Keys, [double]$Seconds = 3) {
    $owner = [int][UiHarnessNative]::WindowProcess($Hwnd)
    if ($owner -eq 0 -or -not (Get-TrackedProcess $owner)) {
        Write-Warning "Send-Keys: window $Hwnd is not a window of a running process this script started; '$Keys' not sent"
        return $false
    }
    [UiHarnessNative]::SetForegroundWindow($Hwnd) | Out-Null
    Start-Sleep -Milliseconds 300
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ($true) {
        $front = [UiHarnessNative]::GetForegroundWindow()
        if ($front -ne [IntPtr]::Zero -and ($front -eq $Hwnd -or [UiHarnessNative]::WindowProcess($front) -eq $owner)) {
            [System.Windows.Forms.SendKeys]::SendWait($Keys)
            return $true
        }
        if ([DateTime]::UtcNow -ge $deadline) { break }
        [UiHarnessNative]::SetForegroundWindow($Hwnd) | Out-Null
        Start-Sleep -Milliseconds 150
    }
    Write-Warning "Send-Keys: the foreground window is not FluentFin's (pid $owner); '$Keys' not sent"
    $false
}

# First UI Automation name in $ProcessId's windows that contains $Needle, else $null.
function Find-Text([int]$ProcessId, [string]$Needle) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    foreach ($w in $windows) {
        $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($e in $all) { try { if ($e.Current.Name -like "*$Needle*") { return $e.Current.Name } } catch {} }
    }
    $null
}

# FluentFin's log of today (Serilog, log<date>.txt) and the backend's own log.
function Get-FluentFinLog { Get-ChildItem $script:LogDir -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName }
function Get-BackendLog { Join-Path $script:LogDir 'mpv-backend.log' }

# A position in a log: its path and line count now.
function Get-LogMark([string]$Path) {
    $count = if ($Path -and (Test-Path $Path)) { @(Get-Content -LiteralPath $Path).Count } else { 0 }
    [pscustomobject]@{ Path = $Path; Lines = $count }
}

# The lines written after $Mark (the whole file if it was rotated meanwhile).
function Get-LinesSince($Mark, [string]$Path = $null) {
    $file = if ($Path) { $Path } else { $Mark.Path }
    if (-not $file -or -not (Test-Path $file)) { return @() }
    $lines = @(Get-Content -LiteralPath $file)
    if ($file -ne $Mark.Path -or $lines.Count -lt $Mark.Lines) { return $lines }
    $lines | Select-Object -Skip $Mark.Lines
}
