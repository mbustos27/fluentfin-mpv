# Nixie 1.7.5, the basics gate (g4: classes 6 to 8): what the checks in this folder that play, restart and go offline share.
# Dot-sourced by a check (itself dot-sourced by drive.ps1, so its names are drive.ps1's scope: every name here starts with g4 or
# G4-, and no top-level line assigns one of drive.ps1's parameters). It dot-sources g1's gate-common.ps1 when it is there.
#
# - G4-Check <case> <what> <ok> <detail>: a check named "<id> [<case>]: <what>" (the gate's names), through drive.ps1's Check.
# - G4-Part <name> { ... }: a part of a check, run when G4_PARTS (comma-separated) is unset or names it; a throw in it is a
#   failed check of its own ("<id> [<name>]: the part ran to its end"), and the next part still runs.
# - G4-SignInRemembered: Account > Switch user, "Remember me" ticked, Quick Connect approved by the fake server's test user,
#   so the next start signs itself in (what a user who restarts has). No password anywhere.
# - G4-Restart <label> [-Quick]: closed (WM_CLOSE, its exit code read) and started again on the same data folder; with -Quick
#   the new copy starts the moment the close is sent, before the old one has gone (a close and a quick start).
# - G4-Page: what the window shows now: 'home' (the top bar), 'server', 'signin', or $null.

$g4Repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$g4GateCommon = Join-Path $PSScriptRoot '..\gate-common.ps1'
if (Test-Path -LiteralPath $g4GateCommon) { . $g4GateCommon }

if (-not ('G4Win' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class G4Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int cx, uint f);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  // the dev build's pointer hook (HarnessPointer): kind (1 click, 2 double click, 4 move, 6 the next layout theme, ...), a
  // point on the screen, a drag's end x
  public static bool Pointer(IntPtr main, int kind, int x, int y, int toX) {
    uint msg = RegisterWindowMessage("NixieHarnessPointer");
    return PostMessage(main, msg, (IntPtr)(long)((uint)(kind & 0xFFFF) | ((uint)(toX & 0xFFFF) << 16)), (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
  // the client area on the screen: x, y, width, height
  public static int[] Client(IntPtr main) {
    RECT c; GetClientRect(main, out c); var p = new POINT(); ClientToScreen(main, ref p);
    return new[] { p.X, p.Y, c.R - c.L, c.B - c.T };
  }
}
"@
}

# ---- naming and parts ---------------------------------------------------------------------------------------------------
function G4-Id { if ($env:GATE_ID) { $env:GATE_ID } else { $script:g4DefaultId } }
function G4-Check([string]$Case, [string]$What, [bool]$Ok, [string]$Detail = '') {
    $name = if ($Case) { "$(G4-Id) [$Case]: $What" } else { "$(G4-Id): $What" }
    # one name, one row: a repeated case gets a number (drive.ps1's checks are keyed by name)
    if ($checks.Contains($name)) { $k = 2; while ($checks.Contains("$name ($k)")) { $k++ }; $name = "$name ($k)" }
    Check $name $Ok $Detail
}
function G4-Wanted([string]$Part) {
    if (-not $env:G4_PARTS) { return $true }
    @($env:G4_PARTS -split ',' | ForEach-Object { $_.Trim() }) -contains $Part
}
function G4-Part([string]$Part, [scriptblock]$Body) {
    if (-not (G4-Wanted $Part)) { Say "$(G4-Id): part $Part left out (G4_PARTS)"; return }
    Say "$(G4-Id): part $Part"
    $t0 = Get-Date
    try { & $Body; $ok = $true; $why = '' }
    catch { $ok = $false; $why = "$($_.Exception.Message) @ $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())" }
    $secs = [int]((Get-Date) - $t0).TotalSeconds
    # Nixie gone under the part (a crash): said as the user meets it, with the app's own last unhandled exception
    $gone = $false; try { $script:ui.Refresh(); $gone = $script:ui.HasExited } catch {}
    if ($gone) { G4-Check $Part 'Nixie is still running' $false "it ended (exit 0x$('{0:X8}' -f $script:ui.ExitCode)): $(G4-LastCrash $t0)" }
    G4-Check $Part 'the part ran to its end' $ok $(if ($ok) { "$secs s" } else { $why })
    if (-not $ok -and -not $gone) {
        try { Shot ("{0}-{1}-failed" -f (G4-Id), $Part) } catch {}
    }
}
# the app's last "Unhandled exception" since a time, with the line under it that says what (its log)
function G4-LastCrash([datetime]$Since) {
    $lines = @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | ForEach-Object { Get-Content -LiteralPath $_.FullName -Encoding utf8 })
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '\[(ERR|FTL)\] Unhandled exception') {
            $what = @($lines[($i + 1)..([math]::Min($lines.Count - 1, $i + 4))] | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' '
            return "$(($lines[$i] -split ' \[')[0]) $what".Substring(0, [math]::Min(300, "$(($lines[$i] -split ' \[')[0]) $what".Length))
        }
    }
    'no unhandled exception in its log'
}

# ---- the window -----------------------------------------------------------------------------------------------------------
function G4-Client { [G4Win]::Client($script:hwnd) }
function G4-Dpi { [int][G4Win]::GetDpiForWindow($script:hwnd) }
function G4-Restore {
    if ([G4Win]::IsIconic($script:hwnd) -or [G4Win]::IsZoomed($script:hwnd)) { [void][G4Win]::ShowWindow($script:hwnd, 9) }
    [void](Wait-Until { if (-not [G4Win]::IsIconic($script:hwnd)) { $true } } -Seconds 4 -PollMs 100)
    [void][G4Win]::SetForegroundWindow($script:hwnd)
}

# g1's gate-common.ps1 has these; a copy of the same behaviour until it is there
if (-not (Get-Command Get-GateSizes -ErrorAction SilentlyContinue)) {
    function Get-GateSizes {
        $v = if ($env:GATE_SIZES) { $env:GATE_SIZES } else { 'gate' }
        if ($v -eq 'none') { return @() }
        if ($v -eq 'gate') { $v = '1280x720,1920x1080,max' }
        @($v -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }
}
if (-not (Get-Command Get-GateTheme -ErrorAction SilentlyContinue)) {
    function Get-GateTheme { if ($env:GATE_THEME) { $env:GATE_THEME } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Default' } }
}
if (-not (Get-Command Set-GateSize -ErrorAction SilentlyContinue)) {
    # 'WxH' (the window's outer size, at the screen's origin) or 'max' (maximised); what it got: @{ Label; W; H; ClientW;
    # ClientH; Dpi; Max }, as g1's gate-common.ps1 answers
    function G4-WindowState {
        $r = New-Object G4Win+RECT; [void][G4Win]::GetWindowRect($script:hwnd, [ref]$r); $c = G4-Client
        @{ W = $r.R - $r.L; H = $r.B - $r.T; ClientW = $c[2]; ClientH = $c[3]; Dpi = G4-Dpi; Max = [G4Win]::IsZoomed($script:hwnd) }
    }
    function Set-GateSize([string]$Size, [int]$SettleMs = 1200) {
        if ($Size -eq 'max') { [void][G4Win]::ShowWindow($script:hwnd, 3) }
        elseif ($Size -match '^(\d+)x(\d+)$') {
            $w = [int]$Matches[1]; $h = [int]$Matches[2]
            if ([G4Win]::IsZoomed($script:hwnd)) { [void][G4Win]::ShowWindow($script:hwnd, 9); Start-Sleep -Milliseconds 400 }
            [void][G4Win]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, $w, $h, 0x14)
        }
        else { throw "Set-GateSize: '$Size' is neither WxH nor max" }
        Start-Sleep -Milliseconds $SettleMs
        $st = G4-WindowState; $st.Label = $Size
        $st
    }
    function Invoke-GateLiveResize([int]$Steps = 6, [int]$SettleMs = 1500) {
        $was = G4-WindowState
        if ($was.Max) { [void][G4Win]::ShowWindow($script:hwnd, 9); Start-Sleep -Milliseconds 300 }
        $r = New-Object G4Win+RECT; [void][G4Win]::GetWindowRect($script:hwnd, [ref]$r)
        $sizes = @(@(1280, 720), @(1600, 900), @(1366, 768), @(1920, 1080), @(1100, 800), @(1700, 1000), @(1440, 900), @(1280, 1000))
        for ($i = 0; $i -lt $Steps; $i++) { $z = $sizes[$i % $sizes.Count]; [void][G4Win]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, $z[0], $z[1], 0x14); Start-Sleep -Milliseconds 40 }
        if ($was.Max) { [void][G4Win]::ShowWindow($script:hwnd, 3) } else { [void][G4Win]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, $r.R - $r.L, $r.B - $r.T, 0x14) }
        Start-Sleep -Milliseconds $SettleMs
        $st = G4-WindowState; $st.Label = 'after a live resize'
        $st
    }
}
if (-not (Get-Command Format-GateSize -ErrorAction SilentlyContinue)) {
    function Format-GateSize($State) { "{0} (client {1}x{2}, {3} dpi{4})" -f $State.Label, $State.ClientW, $State.ClientH, $State.Dpi, $(if ($State.Max) { ', maximised' }) }
}

# ---- signed in, restarts --------------------------------------------------------------------------------------------------
function G4-Shell { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); if ($p -and ([OffscreenUi]::NavItem($p, 'Home') -or (Find-Ui -Id 'TopTabsMenu'))) { $p } }
function G4-Page {
    if (G4-Shell) { 'home' }
    elseif (Find-Ui -Id 'TxtUrl' -Type 'Edit') { 'server' }
    elseif ([OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect')) { 'signin' }
}
function G4-WaitPage([double]$Seconds = 40) { Wait-Until { G4-Page } -Seconds $Seconds -PollMs 150 }

function G4-SignInRemembered {
    $acct = Wait-Until { Find-Ui -Name 'Account, ' -Type 'Button' -Contains } -Seconds 15 -PollMs 150
    if (-not $acct) { throw 'no Account button' }
    [void](Invoke-Element $acct)
    $switch = Wait-Ui -Name 'Switch user' -Type 'Button' -Seconds 8
    if (-not $switch) { throw 'no Switch user in the Account menu' }
    [void](Invoke-Element $switch)
    $quick = Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect') } -Seconds 20 -PollMs 150
    if (-not $quick) { throw 'Switch user: no sign-in page' }
    $remember = Wait-Ui -Name 'Remember me' -Type 'CheckBox' -Seconds 5
    if (-not $remember) { throw 'no Remember me on the sign-in page' }
    $toggle = $remember.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    Start-Sleep -Milliseconds 300
    $at = Get-Epoch
    [void](Invoke-Element $quick)
    $code = Wait-Until { $t = Find-Ui -Id 'QuickConnectCode' -Type 'Text'; if ($t -and ($t.Current.Name -replace '\s', '') -match '^(\d{6})$') { $Matches[1] } } -Seconds 20 -PollMs 100
    if (-not $code) { throw 'Quick Connect showed no code' }
    [void](Invoke-Server '/__harness/quickconnect/approve' 'POST' @{ code = $code })
    $auth = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'authenticate' -and $_.status -eq 200 }) | Select-Object -First 1 } -Seconds 30 -PollMs 100
    $shell = Wait-Until { G4-Shell } -Seconds 30 -PollMs 150
    if (-not ($auth -and $shell)) { throw "the remembered sign-in did not reach Home (server answer $([bool]$auth), Home $([bool]$shell))" }
    $script:navPane = $shell; $script:page = 'home'
    Start-Sleep -Milliseconds 800
}

# the device ids the fake server holds a sign-in for (its harness endpoint: ids, clients, users; never a token)
function G4-Devices { @(Invoke-Server '/__harness/devices' | ForEach-Object { $_ } | Where-Object { $_ }) }

# closed and started again on the same data folder; the old copy's exit code, the page the new one opens on. -Quick: the new
# copy starts while the old one is still closing: the old one is held (suspended, through the handle this run started it
# with) 0.4 s into its close, as a slow close on a busy PC holds it, until the new one is on Home and $WhileClosing has run
function G4-Restart([string]$Label, [switch]$Quick, [scriptblock]$WhileClosing) {
    $old = $script:ui
    $tree = @(Get-Descendants $old.Id)
    $at = Get-Epoch
    $closeAt = Get-Date
    $script:g4ExitSeen = $null
    [OffscreenUi]::Close($script:hwnd, $old.Id)
    $held = $false
    if ($Quick) {
        # held in its close: once its window has gone (the close under way) while the process is still in it. (1.7.5 gate fix:
        # the close takes about 0.2 s now, its log's "[close] the process: exiting after 171 ms"; a fixed 0.4 s found it
        # gone in the release verify's four themes, and the restart was never one while it closed)
        $oldHwnd = $script:hwnd
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.ElapsedMilliseconds -lt 3000 -and -not $old.HasExited -and [G4Win]::IsWindowVisible($oldHwnd)) { Start-Sleep -Milliseconds 1; $old.Refresh() }
        if (-not $old.HasExited) { $held = (Suspend-Tracked $old.Id) -eq 0 }
    }
    else {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $CloseSeconds -and -not $old.HasExited) { Start-Sleep -Milliseconds 25; $old.Refresh() }
    }
    $script:navPane = $null
    $script:hwnd = [IntPtr]::Zero
    Start-App "g4_$($Label -replace '[^A-Za-z0-9]+', '_')_window_ms"
    $startedWhileClosing = -not $old.HasExited
    $page = G4-WaitPage
    if ($page -eq 'home') { $script:navPane = G4-Shell; $script:page = 'home' }
    if ($Quick) {
        try { if ($WhileClosing -and $page -eq 'home') { & $WhileClosing } }
        finally {
            if ($held) { $p = Get-TrackedProcess $old.Id; if ($p) { [void][UiHarnessNative]::NtResumeProcess($p.Handle) } }
            $watch = [Diagnostics.Stopwatch]::StartNew()
            while ($watch.Elapsed.TotalSeconds -lt $CloseSeconds -and -not $old.HasExited) { Start-Sleep -Milliseconds 25; $old.Refresh() }
            $script:g4ExitSeen = Get-Date
        }
    }
    $code = try { if ($old.HasExited) { $old.ExitCode } else { $null } } catch { $null }
    $exitMs = try { if ($old.HasExited) { [int]($old.ExitTime - $closeAt).TotalMilliseconds } else { $null } } catch { $null }
    if ($null -eq $exitMs -and $old.HasExited -and $script:g4ExitSeen) { $exitMs = [int]($script:g4ExitSeen - $closeAt).TotalMilliseconds }
    $alive = @($tree | Where-Object { [int]$_.ProcessId -ne $script:ui.Id -and (Get-TrackedProcess ([int]$_.ProcessId)) })
    [pscustomobject]@{ Code = $code; ExitMs = $exitMs; Page = $page; StartedWhileClosing = $startedWhileClosing; Held = $held
        Alive = $alive; Since = $at }
}

# Home's rows: a card of the item, by its name (any theme's card), scrolled into reach
function G4-HomeCard([string]$Title, [double]$Seconds = 20) {
    Wait-Until {
        $c = Find-MediaCard $Title
        if (-not $c) { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Start-Sleep -Milliseconds 200; $c = Find-MediaCard $Title; [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0) }
        $c
    } -Seconds $Seconds -PollMs 400
}

# the app's log lines since an epoch (its own clock), matching a pattern
function G4-Log([double]$Since, [string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match $Pattern } | ForEach-Object {
                $g4When = [DateTimeOffset]::MinValue
                if ([DateTimeOffset]::TryParse(($_ -split ' \[')[0], [ref]$g4When) -and $g4When.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { $_ }
            }
        })
}
