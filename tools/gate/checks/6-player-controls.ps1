# Nixie 1.7.5, the basics gate, class 6 (the player's controls after each interruption, and windowed playback): after pause,
# Alt+Tab, minimise and restore, full screen in and out (F, a double click, the button; out by Esc, a double click, F), a
# seek, a held key, a theme switch and a fade cut short (1.7.4's friend's bug), the bars are drawn whole or not at all, never
# faint: Play/Pause's accent fill and the title's white as PrintWindow has them (composition included) beside UI Automation;
# hidden, a click where Play/Pause was reaches the video; shown, they span the player. Windowed at the friend's 1906 x 1016
# and at each of the gate's sizes (GATE_SIZES: 1280 x 720 to 2560 x 1392), a live resize, maximise and restore, and a move to
# each other monitor: the bars fill the video area and the picture is centred and fills its side (no black band).
#   run-gate.ps1 ... -Checks player-controls      (manifest\g4.psd1)
#   run-offscreen.ps1 ... -Script season,play -NoA11y -Then tools\gate\checks\6-player-controls.ps1
# Dot-sourced by drive.ps1 after the play step (FluentFin's controls; paused, the bars up). It reads then-174-hud.ps1's helpers
# (run with HUD_PARTS=none: its calibration and its first three checks, then nothing of its own parts). G4_PARTS picks parts:
# pause, alttab, minimise, fullscreen, seek, held, theme, cut, sizes, monitors. Names start with g4c / G4C- (g4-common: g4 / G4-).

$script:g4DefaultId = 'player-controls'
. (Join-Path $PSScriptRoot 'g4-common.ps1')

$env:HUD_PARTS = 'none'
. (Join-Path $g4Repo 'tools\ui-harness\offscreen\then-174-hud.ps1')
$env:HUD_PARTS = $null
KM-ConnectPipe
# from here a row of then-174-hud's helpers is a check of this one, named as the gate names them
function Hd-Row([string]$Case, [string]$What, [bool]$Ok, [string]$Detail) {
    [void]$hdRows.Add([ordered]@{ case = $Case; check = $What; ok = $Ok; detail = $Detail })
    G4-Check $Case $What $Ok $Detail
}
Say "$(G4-Id): theme $(Get-GateTheme), client $((G4-Client) -join ','), dpi $(G4-Dpi), monitors $([OffscreenUi]::Monitors() -join ' | ')"

# the activation away and back, as Alt+Tab does to the window (the harness can't take the foreground on its desktop: the
# window's own activation messages, then the foreground given back)
function G4C-AltTab([int]$AwayMs = 1500) {
    [void][G4Win]::PostMessage($script:hwnd, 0x0086, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_NCACTIVATE, inactive
    [void][G4Win]::PostMessage($script:hwnd, 0x0006, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_ACTIVATE, WA_INACTIVE
    [void][G4Win]::PostMessage($script:hwnd, 0x001C, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_ACTIVATEAPP, away
    Start-Sleep -Milliseconds $AwayMs
    [void][G4Win]::PostMessage($script:hwnd, 0x001C, [IntPtr]1, [IntPtr]::Zero)
    [void][G4Win]::PostMessage($script:hwnd, 0x0086, [IntPtr]1, [IntPtr]::Zero)
    [void][G4Win]::PostMessage($script:hwnd, 0x0006, [IntPtr]1, [IntPtr]::Zero)        # WA_ACTIVE
    [void][G4Win]::SetForegroundWindow($script:hwnd)
}

# the picture fills its side of the player: its lit extent along the video's lower rows against a 16:9 picture fitted into the
# client (Hd-Picture checks it is centred); bars hidden first
function G4C-Fills([string]$Case) {
    KM-SetPaused $false; [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 700
    $bmp = [HudProbe]::Grab($script:hwnd)
    if (-not $bmp) { G4-Check $Case 'the picture fills its side of the player' $false 'no grab'; return }
    try {
        $c = [HudProbe]::Client($script:hwnd); $w = New-Object HudProbe+RECT; [void][HudProbe]::GetWindowRect($script:hwnd, [ref]$w)
        $first = [int]::MaxValue; $last = -1
        foreach ($f in 0.70, 0.73, 0.76, 0.79, 0.82, 0.85) {
            $lit = [HudProbe]::Lit($bmp, $script:hwnd, [int]($c[1] + $c[3] * $f))
            if ($lit[0] -ge 0) { $first = [math]::Min($first, $lit[0]); $last = [math]::Max($last, $lit[1]) }
        }
        $width = $last - $first + 1
        # the video's area: the client under the title bar (windowed; none in full screen)
        # (the app's own title bar, 48 px at 100 %, and Windows' caption under it share the id: the lower edge of either)
        $top = $c[1]
        if (-not (Get-WindowState $script:hwnd).FullScreen) {
            $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'TitleBar')
            foreach ($bar in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond))) {
                try { $br = $bar.Cached.BoundingRectangle; if ($br.Height -gt 0 -and $br.Height -lt 120 -and -not $bar.Cached.IsOffscreen) { $top = [math]::Max($top, [int]($br.Y + $br.Height)) } } catch {}
            }
        }
        $areaH = $c[1] + $c[3] - $top
        $want = [math]::Min($c[2], $areaH * 16 / 9)
        G4-Check $Case 'the picture fills its side of the player (no black band)' ($last -ge 0 -and $width -ge 0.96 * $want) "picture $width px wide; a 16:9 picture fitted into the video's $($c[2])x$areaH area (the $($c[2])x$($c[3]) client under the title bar) is $([int]$want)"
    }
    finally { $bmp.Dispose() }
}
# one size: the bars whole or gone, spanning the player; the picture centred and filling its side
function G4C-Size([string]$Case) {
    Hd-After $Case
    Hd-Picture $Case
    G4C-Fills $Case
}
# a theme switched in place (Settings' choice, the dev hook's kind 6): the bars then drawn as after a clean hide and show in
# that theme (each theme draws them its own way)
function G4C-ThemeCase([string]$Case) {
    KM-OvMove; Start-Sleep -Milliseconds 700
    Hd-Remember
    $now = Hd-Read
    KM-SetPaused $false; [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 700
    KM-OvMove; Start-Sleep -Milliseconds 900
    Hd-Remember
    $bmp = [HudProbe]::Grab($script:hwnd)
    try {
        $r = $script:hdPpRect; $cf = [HudProbe]::Mean($bmp, $script:hwnd, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height * 0.84), 3)
        $r = $script:hdTitleRect; $cw = [HudProbe]::White($bmp, $script:hwnd, [int]$r.X, [int]$r.Y, [int]($r.X + $r.Width), [int]($r.Y + $r.Height))
    }
    finally { $bmp.Dispose() }
    $nowFill = if ($now.Colour -match '^(\d+),(\d+),(\d+)$') { @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) } else { $null }
    $nowWhite = $now.Title * $script:hdRefWhite
    G4-Check $Case 'shown: drawn as after a clean hide and show in the new theme' ($now.Ui -and (KM-OvNear $nowFill $cf) -and $nowWhite -ge 0.6 * $cw) "now: $(Hd-Desc $now); clean: fill $($cf -join ','), title $cw px"
    Hd-After "$Case, then"
}

G4-Part 'pause' {
    foreach ($ms in 0, 150) {
        [void](Hd-InHide $ms { [void](KM-Press 'Space') }); Start-Sleep -Milliseconds 900
        Hd-After "Space $ms ms into the hide" -ExpectShown
    }
    KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 500
    [void](KM-Click 'PlayPauseButton'); Start-Sleep -Milliseconds 900
    Hd-After 'Play/Pause clicked while shown' -ExpectShown
}
G4-Part 'alttab' {
    foreach ($ms in 0, 150) {
        [void](Hd-InHide $ms { G4C-AltTab }); Start-Sleep -Milliseconds 900
        Hd-After "Alt+Tab away and back $ms ms into the hide"
        Hd-InShow $ms { G4C-AltTab }; Start-Sleep -Milliseconds 900
        Hd-After "Alt+Tab away and back $ms ms into the show"
    }
}
G4-Part 'minimise' {
    foreach ($ms in 0, 150) {
        [void](Hd-InHide $ms { [void][HudProbe]::ShowWindow($script:hwnd, 6) }); Start-Sleep -Milliseconds 1500; Hd-Restore
        Hd-After "minimised $ms ms into the hide, restored"
        Hd-InShow $ms { [void][HudProbe]::ShowWindow($script:hwnd, 6) }; Start-Sleep -Milliseconds 1500; Hd-Restore
        Hd-After "minimised $ms ms into the show, restored"
    }
}
G4-Part 'fullscreen' {
    $g4cEnter = @{
        'F'            = { [void](KM-Press 'F') }
        'double click' = { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 2, $p[0], $p[1], 0); Start-Sleep -Milliseconds 300 }
        'the button'   = { KM-ShowBars; [void](KM-Click 'FullScreenButton') }
    }
    $g4cLeave = [ordered]@{ 'F' = @('Esc', { [void](KM-Press 'Esc') }); 'double click' = @('a double click', { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 2, $p[0], $p[1], 0) }); 'the button' = @('F', { [void](KM-Press 'F') }) }
    foreach ($how in $g4cLeave.Keys) {
        Hd-Windowed
        [void](Hd-InHide 120 $g4cEnter[$how])
        $st = Wait-WindowState $script:hwnd $true 6; Start-Sleep -Milliseconds 1200
        G4-Check "full screen by $how" 'the window is full screen' $st.FullScreen $st.Rect
        Hd-After "full screen by $how"
        Hd-Picture "full screen by $how"
        $out = $g4cLeave[$how]
        [void](Hd-InHide 120 $out[1])
        $st = Wait-WindowState $script:hwnd $false 6; Start-Sleep -Milliseconds 1200
        G4-Check "out of full screen by $($out[0])" 'the window is windowed again' (-not $st.FullScreen) $st.Rect
        Hd-After "out of full screen by $($out[0])"
    }
    Hd-Windowed
}
G4-Part 'seek' {
    [void](Hd-InHide 120 { [void](KM-Press 'Right') }); Start-Sleep -Milliseconds 900
    Hd-After 'a seek (Right) in the hide' -ExpectShown
    KM-ShowBars; $sb = Find-Ui -Id 'SeekBar'
    if ($sb) { $r = [OffscreenUi]::RectOf($sb); [void][KeysMatrix]::Pointer($script:hwnd, 1, [int]($r.X + $r.Width * 0.3), [int]($r.Y + $r.Height / 2), 0); Start-Sleep -Milliseconds 900; Hd-After 'a click on the seek bar' -ExpectShown }
    else { G4-Check 'a click on the seek bar' 'the seek bar shows' $false '' }
}
G4-Part 'held' {
    foreach ($ms in 0, 150) {
        [void](Hd-InHide $ms { foreach ($i in 1..10) { [void](KM-Press 'Right' -Repeat) }; [void](KM-Press 'Left') }); Start-Sleep -Milliseconds 900
        Hd-After "Right held and let go $ms ms into the hide" -ExpectShown
    }
}
G4-Part 'theme' {
    # four switches: through every theme and back to the run's own
    $g4cTheme = { [void][KeysMatrix]::Pointer($script:hwnd, 6, 0, 0, 0) }
    foreach ($k in 1..4) {
        if ($k % 2) { [void](Hd-InHide 120 $g4cTheme) } else { Hd-InShow 120 $g4cTheme }
        Start-Sleep -Milliseconds 1500
        $line = @(G4-Log ((Get-Epoch) - 30) '\[harness\] the layout theme chosen: ') | Select-Object -Last 1
        G4C-ThemeCase "a theme switch in the $(if ($k % 2) { 'hide' } else { 'show' }) ($(if ($line -match 'chosen: "?(\w+)') { $Matches[1] } else { "switch $k" }))"
    }
}
G4-Part 'cut' {
    # 1.7.4 (the friend's faint bars): the fades stopped short by the hook (kind 7: what the compositor leaves when it stops one)
    function G4C-Cut { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 7, $p[0], $p[1], 0) }
    Hd-InShow 80 { G4C-Cut }; Start-Sleep -Milliseconds 1200
    Hd-After 'the fade in cut 80 ms into it' -ExpectShown
    [void](Hd-InHide 30 { G4C-Cut }); Start-Sleep -Milliseconds 900
    [void](Hd-Settled 'the fade out cut 30 ms into it' -Click)
    Hd-After 'the fade out cut 30 ms into it, then'
    Hd-InShow 30 { G4C-Cut }
    foreach ($i in 1..6) { Start-Sleep -Milliseconds 250; KM-OvMove }
    Start-Sleep -Milliseconds 300
    Hd-After 'the fade in cut 30 ms into it, the mouse moving on' -ExpectShown
}
G4-Part 'sizes' {
    G4C-Fills 'windowed 1906x1016'
    foreach ($size in @(Get-GateSizes)) {
        $got = Set-GateSize $size
        G4C-Size "windowed $(Format-GateSize $got)"
    }
    # a live resize, in the show and in the hide
    Hd-Windowed
    KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 400
    [void](Invoke-GateLiveResize); Start-Sleep -Milliseconds 1000
    G4C-Size 'a live resize, bars shown'
    [void](Hd-InHide 60 { [void](Invoke-GateLiveResize -SettleMs 0) }); Start-Sleep -Milliseconds 1000
    G4C-Size 'a live resize in the hide'
    # maximise and restore
    [void](Hd-InHide 120 { [void][HudProbe]::ShowWindow($script:hwnd, 3) }); Start-Sleep -Milliseconds 1200
    G4C-Size 'maximised in the hide'
    KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 400; [void][HudProbe]::ShowWindow($script:hwnd, 9); Start-Sleep -Milliseconds 1200
    G4C-Size 'restored, bars shown'
    Hd-Windowed
}
G4-Part 'monitors' {
    $mons = @([OffscreenUi]::Monitors())
    if ($mons.Count -lt 2) { Say "$(G4-Id): one monitor: no move between monitors here" }
    foreach ($m in $mons) {
        if ($m -notmatch '^\S+ (-?\d+),(-?\d+) (\d+)x(\d+)') { continue }
        $g4cX = [int]$Matches[1] + 40; $g4cY = [int]$Matches[2] + 40; $g4cW = [math]::Min(1500, [int]$Matches[3] - 80); $g4cH = [math]::Min(900, [int]$Matches[4] - 80)
        [void](Hd-InHide 120 { [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, $g4cX, $g4cY, $g4cW, $g4cH, 0x14) })
        Start-Sleep -Milliseconds 1500
        G4C-Size "moved to $(($m -split ' ')[0]) in the hide (dpi $(G4-Dpi))"
    }
    Hd-Windowed
}

[KeysMatrix]::Close()
