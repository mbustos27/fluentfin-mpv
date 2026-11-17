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
    # a frame is the old layout (A: like the first), the new one (B: like the last) or neither (X: a half-done switch the user would see; C: the window cloaked, nothing shown)
    foreach ($p in $parsed) { $p | Add-Member State $(if ($p.Cloaked) { 'C' } elseif ($p.DE -le 2.0) { 'B' } elseif ($p.DS -le 2.0) { 'A' } else { 'X' }) }
    $clean = $null
    for ($i = $parsed.Count - 1; $i -ge 0; $i--) { if ($parsed[$i].State -eq 'B') { $clean = $parsed[$i] } else { break } }
    $first = $parsed | Where-Object { $_.I -ge $triggerAt -and $_.State -ne 'A' } | Select-Object -First 1
    $cloaked = @($parsed | Where-Object Cloaked)
    $mid = @($parsed | Where-Object { $_.I -ge $triggerAt -and $_.State -eq 'X' })
    $black = @($mid | Where-Object { $_.Black -ge 95 })
    $white = @($mid | Where-Object { $_.White -ge 60 })
    $strip = ($parsed | Where-Object { $_.I -ge ($triggerAt - 1) -and ($null -eq $clean -or $_.I -le $clean.I + 1) } | ForEach-Object { "{0}{1}@{2}" -f $_.State, $_.I, $_.T }) -join ' '
    $line = "{0}: window {1} -> {2} ({3}); first change at {4} ms, clean frame at {5} ms; half-done frames {6} (near-black {7}, near-white {8}); grab {9} ms avg; cloaked frames {11}; frames {10}" -f `
        $Name, $before.Rect, $after.Rect, $(if ($after.FullScreen -eq $WantFull) { 'as wanted' } else { 'NOT as wanted' }),
        $(if ($first) { $first.T } else { 'none' }), $(if ($clean) { $clean.T } else { 'never' }), $mid.Count, $black.Count, $white.Count,
        [int](($parsed | Measure-Object Grab -Average).Average), $strip, $cloaked.Count
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
