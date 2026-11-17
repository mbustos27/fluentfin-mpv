# Seaglass full-screen smoothness, run with:
#   run-offscreen.ps1 ... -Script season,play -Then offscreen\fullscreen-burst.ps1 [-Setting MpvPlayerControls=Mpv]
# (dot-sourced by drive.ps1 after the steps, on the player page). For each entry (F, F11, double-click, the button, Esc to leave)
# a burst of PrintWindow grabs (OffscreenUi.Burst) around the switch in and the switch out: <Out>\burst\<name>\sheet.png (the
# frames, numbered with ms from the trigger) and burst.csv; a summary line per switch: frames until the window has its final
# rect and the picture its final place ("clean"), frames with a wrong window rect, near-black or near-white frames in
# between, picture-box aspect off 16:9 by more than 4 % (stretched), picture off-centre in the client area.
# 'dbl' (posted clicks) does nothing off-screen: WinUI reads pointer input, not posted mouse messages; the double-tap handler
# (MpvPlayerControls.OnVideoDoubleTapped) calls the same ToggleFullScreen as the button, so it is not measured.
# The grabs run on the UI thread's own time (PrintWindow asks the window to paint), so they slow the switch a little: the
# ms are the grabs', not the screen's. Env FS_BURST_ENTRIES=F,F11,dbl,button,esc picks entries; FS_BURST_REPS repeats each,
# FS_BURST_FRAMES the frames per burst (40; 6 = hardly any grabbing, for the app's own FLUENTFIN_MPV_VIDEO_TRACE log).
# Seaglass 1.1.3: a frame is also K (covered: the black cover, nearly all black); each switch reports how long the app hid
# the window (its log's "shows again after N ms") and the half-done frames shown after the hide ended. FS_BURST_EXTRAS picks
# the checks after the bursts, in this order (default all; 0: none): fheld (F held: auto-repeat), escheld (Esc held in full
# screen), inset (the subtitle inset's timing against the presenter change), dblclick (a posted double click), back (leaving
# the player from full screen with Back; the page is gone after it). A check that leaves the player ends them.

$entries = if ($env:FS_BURST_ENTRIES) { $env:FS_BURST_ENTRIES -split ',' } else { @('F', 'F11', 'button', 'esc') }
$reps = if ($env:FS_BURST_REPS) { [int]$env:FS_BURST_REPS } else { 1 }
# FS_BURST_NORMAL=1: the window is un-maximized and moved first (1500x900 at 120,90): going back must restore exactly that
if ($env:FS_BURST_NORMAL -eq '1') {
    if (-not ('FsBurstWin' -as [type])) {
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FsBurstWin { [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int cx, uint f); }'
    }
    [void][FsBurstWin]::ShowWindow($script:hwnd, 9)
    [void][FsBurstWin]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 120, 90, 1500, 900, 0x14)
    Start-Sleep -Milliseconds 1200
    Say "window made normal: $((Get-WindowState $script:hwnd).Rect)"
}
$burstDir = Join-Path $Out 'burst'
New-Item -ItemType Directory -Force $burstDir | Out-Null
# mpv's own controls leave the video playing: pause it, so a frame that differs from the last is a layout, not the picture moving
if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x20 ' '; Start-Sleep -Milliseconds 1500 }
$frames = if ($env:FS_BURST_FRAMES) { [int]$env:FS_BURST_FRAMES } else { 40 }; $triggerAt = 4

function Burst-Trigger([string]$How) {
    $h = $script:hwnd; $id = $script:ui.Id
    if ($PlayerControls -eq 'Mpv' -and $How -in 'F', 'esc') {
        # mpv's own controls: the key goes to mpv's window (its f / Esc)
        $video = Get-VideoWindow
        if (-not $video) { throw 'no mpv video window' }
        $vk = if ($How -eq 'F') { 0x46 } else { 0x1B }; $ch = if ($How -eq 'F') { [char]'f' } else { [char]27 }
        return [Action]{ [OffscreenUi]::KeyToAsync($video, $vk, $ch) }.GetNewClosure()
    }
    switch ($How) {
        'F'      { return [Action]{ [OffscreenUi]::KeyAsync($h, $id, 0x46) }.GetNewClosure() }
        'F11'    { return [Action]{ [OffscreenUi]::KeyAsync($h, $id, 0x7A) }.GetNewClosure() }
        'esc'    { return [Action]{ [OffscreenUi]::KeyAsync($h, $id, 0x1B) }.GetNewClosure() }
        'button' {
            $b = Find-Ui -Id 'FullScreenButton'
            if (-not $b) { throw 'no Full screen button (the controls are hidden)' }
            $pattern = [OffscreenUi]::InvokePatternOf($b)
            return [Action]{ [OffscreenUi]::InvokeAsync($pattern) }.GetNewClosure()
        }
        'dbl'    {
            $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($h, [ref]$r)
            $x = [int](($r.L + $r.R) / 2); $y = [int](($r.T + $r.B) / 2)
            return [Action]{ [OffscreenUi]::ClickAsync($h, $id, $x, $y, 2) }.GetNewClosure()
        }
    }
}

# FluentFin's log (this run's) and the backend's: lines with their times
function App-Log { Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' | Sort-Object LastWriteTime | Select-Object -Last 1 }
function Line-Time([string]$Line) {
    if ($Line -match '(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)[.,](\d{3})') { [datetime]::ParseExact("$($Matches[1]).$($Matches[2])", 'yyyy-MM-dd HH:mm:ss.fff', $null) }
}
$script:hideLines = 0
# the app's own word on the switch that just ran: "... shows again after N ms (...)" (new lines since the last call)
function Hidden-Ms {
    $lines = @(Select-String -LiteralPath (App-Log).FullName -Pattern 'shows again after' -SimpleMatch)
    $new = @($lines | Select-Object -Skip $script:hideLines); $script:hideLines = $lines.Count
    if (-not $new) { return 'not (no hide logged)' }
    ($new | ForEach-Object { if ($_.Line -match 'shows again after (\d+) ms(.*)$') { "$($Matches[1]) ms$($Matches[2])" } }) -join '; '
}

function Run-Burst([string]$Name, [string]$How, [bool]$WantFull) {
    $trigger = Burst-Trigger $How   # the button is found before the burst (UI Automation is slow: not inside the grabs)
    $before = Get-WindowState $script:hwnd
    $dir = Join-Path $burstDir $Name
    $rows = [OffscreenUi]::Burst($script:hwnd, $script:ui.Id, $frames, $triggerAt, $trigger, $dir)
    Start-Sleep -Milliseconds 800
    $after = Get-WindowState $script:hwnd
    [IO.File]::WriteAllLines((Join-Path $dir 'burst.csv'), [string[]]$rows)
    $parsed = foreach ($line in $rows) {
        $c = $line -split ','; $rect = $c[3] -split '[ x]'; $box = $c[6] -split ' '
        [pscustomobject]@{ I = [int]$c[0]; T = [int]$c[1]; Grab = [int]$c[2]; L = [int]$rect[0]; Top = [int]$rect[1]; W = [int]$rect[2]; H = [int]$rect[3]
            Black = [int]$c[4]; White = [int]$c[5]; X0 = [int]$box[0]; Y0 = [int]$box[1]; X1 = [int]$box[2]; Y1 = [int]$box[3]; Aspect = [double]$c[7]; DS = [double]$c[8]; DE = [double]$c[9]; Cloaked = ($c[10] -eq '1') }
    }
    $final = $parsed[-1]
    # a frame is the old layout (A: like the first), the new one (B: like the last) or neither (X: a half-done switch the user would see; C: the window cloaked, nothing shown; K: covered, all black)
    foreach ($p in $parsed) { $p | Add-Member State $(if ($p.Cloaked) { 'C' } elseif ($p.DE -le 2.0) { 'B' } elseif ($p.DS -le 2.0) { 'A' } elseif ($p.Black -ge 95) { 'K' } else { 'X' }) }
    $clean = $null
    for ($i = $parsed.Count - 1; $i -ge 0; $i--) { if ($parsed[$i].State -eq 'B') { $clean = $parsed[$i] } else { break } }
    $first = $parsed | Where-Object { $_.I -ge $triggerAt -and $_.State -ne 'A' } | Select-Object -First 1
    $cloaked = @($parsed | Where-Object Cloaked)
    $mid = @($parsed | Where-Object { $_.I -ge $triggerAt -and $_.State -eq 'X' })
    $black = @($mid | Where-Object { $_.Black -ge 95 })
    $white = @($mid | Where-Object { $_.White -ge 60 })
    $strip = ($parsed | Where-Object { $_.I -ge ($triggerAt - 1) -and ($null -eq $clean -or $_.I -le $clean.I + 1) } | ForEach-Object { "{0}{1}@{2}" -f $_.State, $_.I, $_.T }) -join ' '
    # what was shown once the hide ended: half-done frames after the last hidden one (C or K)
    $lastHidden = $parsed | Where-Object { $_.I -ge $triggerAt -and $_.State -in 'C', 'K' } | Select-Object -Last 1
    $shownHalf = @($parsed | Where-Object { $_.I -ge $triggerAt -and $_.State -eq 'X' -and (-not $lastHidden -or $_.I -gt $lastHidden.I) })
    $covered = @($parsed | Where-Object { $_.State -eq 'K' })
    $hiddenMs = Hidden-Ms
    $line = "{0}: window {1} -> {2} ({3}); first change at {4} ms, clean frame at {5} ms; half-done frames {6} (near-black {7}, near-white {8}); grab {9} ms avg; cloaked frames {11}; covered frames {12}; half-done shown after the hide {13}; hidden {14}; frames {10}" -f `
        $Name, $before.Rect, $after.Rect, $(if ($after.FullScreen -eq $WantFull) { 'as wanted' } else { 'NOT as wanted' }),
        $(if ($first) { $first.T } else { 'none' }), $(if ($clean) { $clean.T } else { 'never' }), $mid.Count, $black.Count, $white.Count,
        [int](($parsed | Measure-Object Grab -Average).Average), $strip, $cloaked.Count, $covered.Count, $shownHalf.Count, $hiddenMs
    Say "burst $line"
    Add-Content -LiteralPath (Join-Path $burstDir 'summary.txt') -Value $line -Encoding utf8
    Check "burst $Name switched ($How)" ($after.FullScreen -eq $WantFull) $after.Rect
}

foreach ($how in $entries) {
    for ($rep = 1; $rep -le $reps; $rep++) {
        $leave = if ($how -eq 'esc') { 'esc' } else { $how }
        $enter = if ($how -eq 'esc') { 'F' } else { $how }
        $startRect = (Get-WindowState $script:hwnd).Rect
        Run-Burst "$how-in-$rep" $enter $true
        Start-Sleep -Milliseconds 1500
        Run-Burst "$how-out-$rep" $leave $false
        $backRect = (Get-WindowState $script:hwnd).Rect
        Check "burst ${how}-${rep}: back to the window's own rect" ($backRect -eq $startRect) "$startRect -> $backRect"
        Start-Sleep -Milliseconds 1500
    }
}

# the key pressed again within the switch (FluentFin's controls only: posted to its input window): two presses end where they
# started, three end in full screen, each time with the window shown (not cloaked) and nothing half-done left behind
if ($PlayerControls -eq 'FluentFin' -and -not $env:FS_BURST_NO_QUICK) {
    foreach ($presses in 2, 3) {
        $startFull = (Get-WindowState $script:hwnd).FullScreen
        for ($i = 0; $i -lt $presses; $i++) { Key 0x46; Start-Sleep -Milliseconds 25 }
        Start-Sleep -Milliseconds 1500
        $state = Get-WindowState $script:hwnd
        $want = if ($presses % 2) { -not $startFull } else { $startFull }
        $cl = [OffscreenUi]::IsCloaked($script:hwnd)
        Check "F pressed $presses times quickly ends $(if ($want) { 'in full screen' } else { 'windowed' }), window shown" ($state.FullScreen -eq $want -and -not $cl) "$($state.Rect) cloaked=$cl"
    }
    Shot 'after-quick-presses'
    if ((Get-WindowState $script:hwnd).FullScreen) { Key 0x1B; Start-Sleep -Milliseconds 1200 }
}

# ---- after the bursts (FluentFin's controls): held keys, the subtitle inset's timing, a double click, Back from full screen
$extras = if ($env:FS_BURST_EXTRAS) { @($env:FS_BURST_EXTRAS -split ',') } else { @('fheld', 'escheld', 'inset', 'dblclick', 'back') }
if ($PlayerControls -eq 'FluentFin' -and $extras -notcontains '0') {
    if (-not ('FsExtras' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class FsExtras {
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  // a key down (repeat: bit 30, the key was already down) or up, posted
  public static void Down(IntPtr t, int vk, bool repeat) { uint s = MapVirtualKey((uint)vk, 0); PostMessage(t, 0x0100, (IntPtr)vk, (IntPtr)(1 | (s << 16) | (repeat ? (1u << 30) : 0u))); }
  public static void Up(IntPtr t, int vk) { uint s = MapVirtualKey((uint)vk, 0); PostMessage(t, 0x0101, (IntPtr)vk, (IntPtr)(1 | (s << 16) | (1u << 30) | (1u << 31))); }
  // a double click at screen point (x, y): down, up, double-click, up, posted to t
  public static void DoubleClick(IntPtr t, int x, int y) {
    var p = new POINT { X = x, Y = y }; ScreenToClient(t, ref p); var lp = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
    PostMessage(t, 0x0200, IntPtr.Zero, lp); System.Threading.Thread.Sleep(40);
    PostMessage(t, 0x0201, (IntPtr)1, lp); System.Threading.Thread.Sleep(30); PostMessage(t, 0x0202, IntPtr.Zero, lp); System.Threading.Thread.Sleep(60);
    PostMessage(t, 0x0203, (IntPtr)1, lp); System.Threading.Thread.Sleep(30); PostMessage(t, 0x0202, IntPtr.Zero, lp);
  }
}
'@
    }
    $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
    $backendLog = Join-Path $DataDir 'Logs\mpv-backend.log'
    function Count-Log([string]$Pattern) { @(Select-String -LiteralPath (App-Log).FullName -Pattern $Pattern -SimpleMatch).Count }
    function Count-Backend([string]$Pattern) { @(Select-String -LiteralPath $backendLog -Pattern $Pattern -SimpleMatch -ErrorAction SilentlyContinue).Count }
    function Player-There { [bool](Find-Ui -Id 'FullScreenButton') }
    if ((Get-WindowState $script:hwnd).FullScreen) { Key 0x1B; Start-Sleep -Milliseconds 1500 }

    # F held about 1 s (one down, then 30 auto-repeats 33 ms apart): one switch, not one per repeat
    if ($extras -contains 'fheld') {
    $p0 = Count-Log 'setting the presenter'
    [FsExtras]::Down($kw, 0x46, $false)
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 33; [FsExtras]::Down($kw, 0x46, $true) }
    [FsExtras]::Up($kw, 0x46); Start-Sleep -Milliseconds 1500
    $n = (Count-Log 'setting the presenter') - $p0
    Say "extras: F held 1 s: $n presenter changes, full screen $((Get-WindowState $script:hwnd).FullScreen)"
    Check 'extras: F held is one switch (auto-repeat ignored)' ($n -eq 1 -and (Get-WindowState $script:hwnd).FullScreen) "$n presenter changes"
    }

    # Esc held in full screen (0.7 s of repeats): leaves full screen, and the player stays
    if ($extras -contains 'escheld') {
    if (-not (Get-WindowState $script:hwnd).FullScreen) { Key 0x46; Start-Sleep -Milliseconds 1500 }
    [FsExtras]::Down($kw, 0x1B, $false)
    for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 33; [FsExtras]::Down($kw, 0x1B, $true) }
    [FsExtras]::Up($kw, 0x1B); Start-Sleep -Milliseconds 2000
    $there = Player-There; $full = (Get-WindowState $script:hwnd).FullScreen
    Say "extras: Esc held in full screen: full screen $full, player still there $there"
    Check 'extras: Esc held leaves full screen and keeps the player' ($there -and -not $full) "full screen $full, player $there"
    if (-not $there) { Say 'extras: the player was left: the rest is skipped'; return }
    }

    # the subtitle inset (set_controls) against the presenter change, in and out: sent before it, not after the window showed
    if ($extras -contains 'inset') {
    if ((Get-WindowState $script:hwnd).FullScreen) { Key 0x1B; Start-Sleep -Milliseconds 1500 }
    foreach ($leg in @(@{ Key = 0x46; Name = 'in' }, @{ Key = 0x1B; Name = 'out' })) {
        $since = Get-Date
        Key $leg.Key; Start-Sleep -Milliseconds 1500
        $presenter = @(Select-String -LiteralPath (App-Log).FullName -Pattern 'setting the presenter' -SimpleMatch | Where-Object { (Line-Time $_.Line) -ge $since.AddMilliseconds(-50) } | Select-Object -Last 1)
        $shown = @(Select-String -LiteralPath (App-Log).FullName -Pattern 'shows again after' -SimpleMatch | Where-Object { (Line-Time $_.Line) -ge $since.AddMilliseconds(-50) } | Select-Object -Last 1)
        $insets = @(Select-String -LiteralPath $backendLog -Pattern '<- set_controls' -SimpleMatch | Where-Object { (Line-Time $_.Line) -ge $since.AddMilliseconds(-50) })
        if ($presenter) {
            $tp = Line-Time $presenter[0].Line
            $rel = ($insets | ForEach-Object { '{0:+0;-0} ms' -f ((Line-Time $_.Line) - $tp).TotalMilliseconds }) -join ', '
            $rs = if ($shown) { '{0:+0;-0} ms' -f ((Line-Time $shown[0].Line) - $tp).TotalMilliseconds } else { 'none logged' }
            Say "extras: inset going $($leg.Name): set_controls at [$rel] from the presenter change; the window showed at $rs"
            $after = @($insets | Where-Object { (Line-Time $_.Line) -gt $tp })
            Check "extras: the subtitle inset goes before the presenter change ($($leg.Name))" ($insets.Count -gt 0 -and $after.Count -eq 0) "set_controls at [$rel]"
        }
        else { Say "extras: inset going $($leg.Name): no presenter change logged" }
    }
    }
    if ((Get-WindowState $script:hwnd).FullScreen) { Key 0x1B; Start-Sleep -Milliseconds 1500 }

    # a posted double click over the video (WinUI may ignore posted clicks off-screen: then nothing happens at all)
    if ($extras -contains 'dblclick') {
    $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r)
    $t0 = Count-Backend '<- toggle_pause'; $full0 = (Get-WindowState $script:hwnd).FullScreen
    [FsExtras]::DoubleClick([OffscreenUi]::PointerWindow($script:hwnd, $script:ui.Id), [int](($r.L + $r.R) / 2), [int]($r.T + ($r.B - $r.T) * 0.45))
    Start-Sleep -Milliseconds 1500
    $pauses = (Count-Backend '<- toggle_pause') - $t0; $full1 = (Get-WindowState $script:hwnd).FullScreen
    Say "extras: posted double click: full screen $full0 -> $full1, toggle_pause commands $pauses"
    if ($full1 -ne $full0) { Check 'extras: a double click is full screen only (no pause and unpause)' ($pauses -eq 0) "$pauses toggle_pause" }
    else { Say 'extras: the posted double click did nothing (WinUI reads pointer input): the double click is covered by unit tests' }
    if ((Get-WindowState $script:hwnd).FullScreen) { Key 0x1B; Start-Sleep -Milliseconds 1500 }
    }

    # Back (the browser-back key) from full screen: the player is left; the window is never hidden waiting for a layout that does not come
    if ($extras -contains 'back') {
    if (-not (Get-WindowState $script:hwnd).FullScreen) { Key 0x46; Start-Sleep -Milliseconds 1500 }
    $before = Count-Log 'shows again after'
    $sw = [Diagnostics.Stopwatch]::StartNew(); $firstHidden = $null; $lastHidden = $null
    [FsExtras]::Down($kw, 0xA6, $false); [FsExtras]::Up($kw, 0xA6)
    while ($sw.ElapsedMilliseconds -lt 1500) {
        if ([OffscreenUi]::IsCloaked($script:hwnd)) { if ($null -eq $firstHidden) { $firstHidden = $sw.ElapsedMilliseconds }; $lastHidden = $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 5
    }
    $hideLine = @(Select-String -LiteralPath (App-Log).FullName -Pattern 'shows again after' -SimpleMatch | Select-Object -Skip $before)
    $hid = if ($null -ne $firstHidden) { "cloaked from $firstHidden to $lastHidden ms" } else { 'never cloaked' }
    Say "extras: Back from full screen: $hid; the app: $(if ($hideLine) { ($hideLine | ForEach-Object { $_.Line -replace '^.*(shows again after .*)$', '$1' }) -join '; ' } else { 'no hide' }); now $((Get-WindowState $script:hwnd).Rect) full $((Get-WindowState $script:hwnd).FullScreen)"
    Check 'extras: Back from full screen does not hide the window' ($null -eq $firstHidden -and -not $hideLine) $hid
    Shot 'after-back-from-full-screen'
    }
}
