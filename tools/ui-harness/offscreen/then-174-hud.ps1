# Nixie 1.7.4 (a friend's report on 1.7.x): the player's own controls stuck almost invisible ("the hud is like almost invis / u
# can see it v er feitnly / and still click it"), playing in a window about 1906x1016. Drives the interrupts that can cut a show
# or a hide of the bars short, windowed first, and after each reads what is drawn: Play/Pause's accent fill and the title's
# white as PrintWindow has them (composition included) beside UI Automation (the bars there = Visible); hidden, a click where
# Play/Pause was must reach the video (the hook's hit test, its log line); shown, the bars must span the player.
#   run-offscreen.ps1 ... -Script season,play -NoA11y -Then offscreen\then-174-hud.ps1
# Dot-sourced by drive.ps1 after the play step (FluentFin's controls; paused, the bars up). Every name here starts with Hd.
# HUD_PARTS (comma-separated; default all but probe): cut (the report's state made on purpose: the hook's kind 7 stops the bars'
# fades short, as the compositor can; fails without MpvPlayerControls.Bars.cs), sweep (the auto-hide, then the mouse 0 to 400 ms
# into its fade), keys (a key in the fade: volume, seek, held seek, Space; a seek by click), minimize, fullscreen (F, double
# click, the button; out by Esc, a double click, F; in the fade too), maximize, resize, stall (the UI thread busy in a fade:
# SuspendThread of the run's own FluentFin only), episode (Shift+N, Shift+P, Next in a fade), theme (Windows' glass settings and
# the layout theme changed in a fade: the hook's kinds 5 and 6), monitors (moves across them), collapse (the player hidden and
# shown again: the hook's kind 8), uia (UI Automation's Invoke and SetFocus in a fade), start (restored from maximized and sized
# to 1906x1016 in a fade), open (the player opened again, HUD_OPENS times); probe (the compositor's opacity as the app reads it
# through a fade in, in the app's log). start, open, collapse and uia check closely (the title's white at 90 % of the
# reference); the rest, more loosely (60 %, and the fill within 60). About 35 minutes for all.
# Results: <Out>\hud\hud.json (a row per check), shots of any failure in <Out>\hud.

$env:NIXIE_MATRIX_ONLY = 'helpers'
. (Join-Path $PSScriptRoot 'then-keys-matrix.ps1')
$env:NIXIE_MATRIX_ONLY = $null
$hdParts = if ($env:HUD_PARTS) { $env:HUD_PARTS -split ',' } else { @('cut', 'collapse', 'uia', 'start', 'open', 'sweep', 'keys', 'minimize', 'fullscreen', 'maximize', 'resize', 'stall', 'episode', 'theme', 'monitors') }
$hdDir = Join-Path $Out 'hud'
New-Item -ItemType Directory -Force $hdDir | Out-Null
$hdRows = New-Object System.Collections.ArrayList

if (-not ('HudProbe' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class HudProbe {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int cx, uint f);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

  // the window as drawn (PrintWindow with PW_RENDERFULLCONTENT: composition included); null when it cannot be had
  public static Bitmap Grab(IntPtr main) {
    RECT r; if (!GetWindowRect(main, out r)) return null;
    int w = r.R - r.L, h = r.B - r.T; if (w <= 0 || h <= 0 || IsIconic(main)) return null;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
    return bmp;
  }
  // the mean colour of a square of side 2*half+1 at a screen point; null outside
  public static int[] Mean(Bitmap bmp, IntPtr main, int x, int y, int half) {
    RECT r; GetWindowRect(main, out r);
    int cx = x - r.L, cy = y - r.T; long sr = 0, sg = 0, sb = 0, n = 0;
    for (int dy = -half; dy <= half; dy++) for (int dx = -half; dx <= half; dx++) {
      int px = cx + dx, py = cy + dy; if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
      var c = bmp.GetPixel(px, py); sr += c.R; sg += c.G; sb += c.B; n++;
    }
    return n == 0 ? null : new[] { (int)(sr / n), (int)(sg / n), (int)(sb / n) };
  }
  // pixels near white (each channel >= 225) in a screen rectangle: white text and glyphs drawn at full strength
  public static int White(Bitmap bmp, IntPtr main, int x0, int y0, int x1, int y1) {
    RECT r; GetWindowRect(main, out r);
    int n = 0;
    for (int y = Math.Max(0, y0 - r.T); y < Math.Min(bmp.Height, y1 - r.T); y++)
      for (int x = Math.Max(0, x0 - r.L); x < Math.Min(bmp.Width, x1 - r.L); x++) {
        var c = bmp.GetPixel(x, y); if (c.R >= 225 && c.G >= 225 && c.B >= 225) n++;
      }
    return n;
  }
  // the picture's extent along a row (screen y): the first and last pixel brighter than 24 in any channel (window x), -1 when none
  public static int[] Lit(Bitmap bmp, IntPtr main, int y) {
    RECT r; GetWindowRect(main, out r);
    int py = y - r.T, first = -1, last = -1;
    if (py < 0 || py >= bmp.Height) return new[] { -1, -1 };
    for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, py); if (c.R > 24 || c.G > 24 || c.B > 24) { if (first < 0) first = x; last = x; } }
    return new[] { first, last };
  }
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
  [DllImport("kernel32.dll")] static extern int SuspendThread(IntPtr h);
  [DllImport("kernel32.dll")] static extern int ResumeThread(IntPtr h);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  // the app's UI thread (its main window's) held still for ms, the compositor going on: a busy UI thread (a collection, a heavy
  // layout). moveAt >= 0: the hook's pointer move posted that far into it (handled once the thread runs again). Only the run's own
  // FluentFin (pid); null when done, else why not
  public static string Stall(IntPtr main, int pid, int ms, int moveAt, int x, int y) {
    uint p; uint tid = GetWindowThreadProcessId(main, out p);
    if (p != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    var h = OpenThread(0x0002, false, tid);
    if (h == IntPtr.Zero) return "OpenThread failed";
    try {
      if (SuspendThread(h) < 0) return "SuspendThread failed";
      try {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (moveAt >= 0) {
          System.Threading.Thread.Sleep(Math.Min(moveAt, ms));
          uint msg = RegisterWindowMessage("NixieHarnessPointer");
          PostMessage(main, msg, (IntPtr)4, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
        }
        int rest = ms - (int)sw.ElapsedMilliseconds; if (rest > 0) System.Threading.Thread.Sleep(rest);
      } finally { ResumeThread(h); }
      return null;
    } finally { CloseHandle(h); }
  }
  // the client area on the screen: x, y, width, height
  public static int[] Client(IntPtr main) {
    RECT c; GetClientRect(main, out c); var p = new POINT(); ClientToScreen(main, ref p);
    return new[] { p.X, p.Y, c.R - c.L, c.B - c.T };
  }
}
"@
}

# ---- what is drawn ------------------------------------------------------------------------------------------------------------
$script:hdPpRect = $null; $script:hdTitleRect = $null; $script:hdRefFill = $null; $script:hdRefWhite = 0; $script:hdShot = 0
function Hd-Remember {
    $pp = Find-Ui -Id 'PlayPauseButton'; if ($pp) { $script:hdPpRect = [OffscreenUi]::RectOf($pp) }
    $t = Find-Ui -Id 'TitleText'; if ($t) { $script:hdTitleRect = [OffscreenUi]::RectOf($t) }
}
# the overlay now: Ui (Play/Pause in UI Automation: the bars Visible), Fill (Play/Pause's accent drawn, within 60 of the
# reference), Title (near-white pixels in the title's box, a share of the reference's), Drawn (both whole), Gone (neither)
function Hd-Read([string]$Save) {
    Hd-Remember
    $ui = [bool](Find-Ui -Id 'PlayPauseButton')
    $bmp = [HudProbe]::Grab($script:hwnd)
    if (-not $bmp) { return [pscustomobject]@{ Ui = $ui; Fill = $false; Title = 0; Colour = 'no grab (minimized?)'; Drawn = $false; Gone = $false } }
    try {
        if ($Save) { $bmp.Save((Join-Path $hdDir ("{0:D3}-{1}.png" -f (++$script:hdShot), ($Save -replace '[^A-Za-z0-9]+', '-'))), [System.Drawing.Imaging.ImageFormat]::Png) }
        $c = $null; $white = 0
        if ($script:hdPpRect) { $r = $script:hdPpRect; $c = [HudProbe]::Mean($bmp, $script:hwnd, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height * 0.84), 3) }
        if ($script:hdTitleRect) { $r = $script:hdTitleRect; $white = [HudProbe]::White($bmp, $script:hwnd, [int]$r.X, [int]$r.Y, [int]($r.X + $r.Width), [int]($r.Y + $r.Height)) }
    }
    finally { $bmp.Dispose() }
    $fill = [bool](KM-OvNear $c $script:hdRefFill)
    $titleShare = if ($script:hdRefWhite -gt 0) { [math]::Round($white / $script:hdRefWhite, 2) } else { 0 }
    [pscustomobject]@{
        Ui = $ui; Fill = $fill; Title = $titleShare; Colour = $(if ($c) { $c -join ',' } else { 'none' })
        Drawn = $fill -and $titleShare -ge 0.6
        Gone = (-not $fill) -and $titleShare -le 0.1
    }
}
function Hd-Desc($S) { "UI Automation $($S.Ui), fill $($S.Fill) ($($S.Colour); ref $($script:hdRefFill -join ',')), title $($S.Title) of the reference" }
function Hd-Row([string]$Case, [string]$What, [bool]$Ok, [string]$Detail) {
    [void]$hdRows.Add([ordered]@{ case = $Case; check = $What; ok = $Ok; detail = $Detail })
    Check "hud | $Case | $What" $Ok $Detail
}
function Hd-Calibrate {
    # after a whole hide and show of their own: bars a cut fade left short at the player's opening are not the reference
    KM-SetPaused $false; [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 700
    KM-OvMove; Start-Sleep -Milliseconds 900
    Hd-Remember
    $bmp = [HudProbe]::Grab($script:hwnd)
    try {
        $r = $script:hdPpRect; $script:hdRefFill = [HudProbe]::Mean($bmp, $script:hwnd, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height * 0.84), 3)
        $r = $script:hdTitleRect; $script:hdRefWhite = [HudProbe]::White($bmp, $script:hwnd, [int]$r.X, [int]$r.Y, [int]($r.X + $r.Width), [int]($r.Y + $r.Height))
        $bmp.Save((Join-Path $hdDir 'reference.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bmp.Dispose() }
    Say "hud: reference Play/Pause fill $($script:hdRefFill -join ','), title $script:hdRefWhite near-white px, client $([HudProbe]::Client($script:hwnd) -join ','), dpi $([HudProbe]::GetDpiForWindow($script:hwnd))"
}

# the last "[harness] Click at x,y: <element>" line of the app's log (what the hook's hit test found)
function Hd-LastHit {
    $f = Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $f) { return $null }
    $m = Select-String -LiteralPath $f.FullName -Pattern '\[harness\] "?Click"? at [-\d]+,[-\d]+: (.+)$' | Select-Object -Last 1
    if ($m) { $m.Matches[0].Groups[1].Value.Trim() } else { $null }
}
# hidden: a click where Play/Pause was is the video's (the hit is the video panel or the player's root, never a bar's part)
function Hd-ClickWhereHidden([string]$Case) {
    if (-not $script:hdPpRect) { return }
    $r = $script:hdPpRect
    $before = Hd-LastHit
    $paused = KM-Bool 'pause'
    [void][KeysMatrix]::Pointer($script:hwnd, 1, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2), 0)
    $hit = Wait-Until { $h = Hd-LastHit; if ($h -and $h -ne $before) { $h } } -Seconds 3 -PollMs 100
    if (-not $hit) { $hit = Hd-LastHit }
    Hd-Row $Case 'hidden: a click where Play/Pause was reaches the video' ($hit -match '^(SwapChainPanel VideoPanel|Grid Root)$') "hit $hit"
    # put the play state back (the click paused or played)
    Start-Sleep -Milliseconds 700
    KM-SetPaused $paused
}

# the bars, as they are now: shown, drawn whole and spanning the player; hidden, nothing drawn and no click on them
function Hd-Settled([string]$Case, [switch]$Click) {
    $s = Hd-Read
    if ($s.Ui) {
        if (-not $s.Drawn) { Start-Sleep -Milliseconds 400; $s = Hd-Read "$Case-shown" }
        Hd-Row $Case 'shown: drawn whole' $s.Drawn (Hd-Desc $s)
        Hd-Span $Case
    }
    else {
        if (-not $s.Gone) { Start-Sleep -Milliseconds 400; $s = Hd-Read "$Case-hidden" }
        Hd-Row $Case 'hidden: nothing drawn' $s.Gone (Hd-Desc $s)
        if ($Click) { Hd-ClickWhereHidden $Case }
    }
    $s
}

# shown: the bars span the player: Back near its left edge, Full screen near its right, the seek bar centred, Play/Pause near the foot
function Hd-Span([string]$Case) {
    $c = [HudProbe]::Client($script:hwnd)
    # all four in UI Automation (an episode change can take one out for a moment)
    $all = Wait-Until { $b = Find-Ui -Id 'BackButton'; $f = Find-Ui -Id 'FullScreenButton'; $s = Find-Ui -Id 'SeekBar'; $p = Find-Ui -Id 'PlayPauseButton'; if ($b -and $f -and $s -and $p) { , @($b, $f, $s, $p) } } -Seconds 2 -PollMs 100
    if (-not $all) { Hd-Row $Case 'shown: the bars span the player' $false "back $([bool](Find-Ui -Id 'BackButton')), full screen $([bool](Find-Ui -Id 'FullScreenButton')), seek $([bool](Find-Ui -Id 'SeekBar')), Play/Pause $([bool](Find-Ui -Id 'PlayPauseButton'))"; return }
    $back = $all[0]; $full = $all[1]; $seek = $all[2]; $pp = $all[3]
    $b = [OffscreenUi]::RectOf($back); $f = [OffscreenUi]::RectOf($full); $s = [OffscreenUi]::RectOf($seek); $p = [OffscreenUi]::RectOf($pp)
    $left = $b.X - $c[0]; $right = ($c[0] + $c[2]) - ($f.X + $f.Width); $foot = ($c[1] + $c[3]) - ($p.Y + $p.Height)
    $skew = [math]::Abs(($s.X - $c[0]) - (($c[0] + $c[2]) - ($s.X + $s.Width)))
    $ok = $left -ge 0 -and $left -le 160 -and $right -ge 0 -and $right -le 200 -and $skew -le 48 -and $foot -ge 0 -and $foot -le 200
    Hd-Row $Case 'shown: the bars span the player' $ok "client $($c -join ','); Back $([int]$left) px from the left, Full screen $([int]$right) px from the right, the seek bar off centre by $([int]$skew) px ($([int]$s.Width) wide), Play/Pause $([int]$foot) px above the foot"
}

# the picture's place along the video's middle row, bars hidden (the black bands either side of a 16:9 picture in a wider player)
function Hd-Picture([string]$Case) {
    KM-SetPaused $false; [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 700
    $bmp = [HudProbe]::Grab($script:hwnd)
    if (-not $bmp) { return }
    try {
        $c = [HudProbe]::Client($script:hwnd); $w = New-Object HudProbe+RECT; [void][HudProbe]::GetWindowRect($script:hwnd, [ref]$w)
        # the widest lit run over rows across the test picture's lower part (its gradient band runs edge to edge)
        $first = [int]::MaxValue; $last = -1
        foreach ($f in 0.70, 0.73, 0.76, 0.79, 0.82, 0.85) {
            $lit = [HudProbe]::Lit($bmp, $script:hwnd, [int]($c[1] + $c[3] * $f))
            if ($lit[0] -ge 0) { $first = [math]::Min($first, $lit[0]); $last = [math]::Max($last, $lit[1]) }
        }
        $leftBand = $first - ($c[0] - $w.L); $rightBand = ($c[0] - $w.L + $c[2] - 1) - $last
        Say "hud: $Case picture: bands $leftBand px left, $rightBand px right of a $($c[2])x$($c[3]) client"
        Hd-Row $Case 'the picture is centred (bands even)' ([math]::Abs($leftBand - $rightBand) -le 4) "left $leftBand, right $rightBand (client $($c -join ','))"
    }
    finally { $bmp.Dispose() }
}

# ---- the interrupts ------------------------------------------------------------------------------------------------------------
# the bars shown, then left alone until the auto-hide begins (the first heartbeat 3 s after the last activity: UI Automation
# loses them at once, while the fade runs 0.3 s more); $Ms into the fade, $Action
function Hd-InHide([int]$Ms, [scriptblock]$Action) {
    KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 600
    $t0 = Get-Date
    KM-OvMove
    [void](Wait-Until { if (-not (Find-Ui -Id 'PlayPauseButton')) { $true } } -Seconds 6 -PollMs 15)
    $seen = [int]((Get-Date) - $t0).TotalMilliseconds
    if ($Ms -gt 0) { Start-Sleep -Milliseconds $Ms }
    & $Action
    $seen
}
# the bars hidden, then shown by the mouse, and $Ms into their fade in, $Action
function Hd-InShow([int]$Ms, [scriptblock]$Action) {
    KM-SetPaused $false
    [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 500
    KM-OvMove
    if ($Ms -gt 0) { Start-Sleep -Milliseconds $Ms }
    & $Action
}
# after a case: the state it left, then a hide by itself and a show by the mouse must both be whole (a stuck state lasting)
function Hd-After([string]$Case, [switch]$ExpectShown) {
    $s = Hd-Settled $Case
    if ($ExpectShown -and -not $s.Ui) { Hd-Row $Case 'the bars are shown' $false (Hd-Desc $s) }
    # where Play/Pause is in this layout (a move or a resize changed it), for the click while hidden
    if (-not $s.Ui) { KM-OvMove; Start-Sleep -Milliseconds 500 }
    Hd-Remember
    KM-SetPaused $false
    $hidden = KM-OvWaitHidden 8; Start-Sleep -Milliseconds 700
    if ($hidden) { [void](Hd-Settled "$Case, then the auto-hide" -Click) } else { Hd-Row $Case 'then they hide by themselves' $false "held by: $((Find-Ui -Id 'PlayPauseButton') -ne $null)" }
    KM-OvMove; Start-Sleep -Milliseconds 900
    $a = Hd-Settled "$Case, then the mouse"
    if (-not $a.Ui) { Hd-Row "$Case, then the mouse" 'the bars are shown' $false (Hd-Desc $a) }
}
function Hd-Windowed {
    if ((Get-WindowState $script:hwnd).FullScreen) { [void](KM-Press 'Esc'); [void](Wait-WindowState $script:hwnd $false 6); Start-Sleep -Milliseconds 800 }
    if ([HudProbe]::IsIconic($script:hwnd) -or [HudProbe]::IsZoomed($script:hwnd)) { [void][HudProbe]::ShowWindow($script:hwnd, 9); Start-Sleep -Milliseconds 800 }
    [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 7, 7, 1906, 1016, 0x14)
    [void][HudProbe]::SetForegroundWindow($script:hwnd)
    Start-Sleep -Milliseconds 800
}
function Hd-Restore {
    [void][HudProbe]::ShowWindow($script:hwnd, 9); [void][HudProbe]::SetForegroundWindow($script:hwnd)
    [void](Wait-Until { if (-not [HudProbe]::IsIconic($script:hwnd)) { $true } } -Seconds 4 -PollMs 100)
    Start-Sleep -Milliseconds 700
}

# ---- run ------------------------------------------------------------------------------------------------------------------------
KM-ConnectPipe
Hd-Windowed
KM-Ready $false
Hd-Calibrate
Say "hud: monitors $([OffscreenUi]::Monitors() -join ' | ')"
Say "hud: child windows of other processes (an embedded mpv window): $(@([OffscreenUi]::Children($script:hwnd) | Where-Object { [OffscreenUi]::ProcessOf($_) -ne $script:ui.Id }).Count)"
[void](Hd-Settled 'windowed, at the start')
[void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 700
[void](Hd-Settled 'windowed, the auto-hide' -Click)
Hd-Picture 'windowed 1906x1016'

if ($hdParts -contains 'sweep') {
    foreach ($ms in 0, 40, 80, 120, 160, 200, 240, 280, 320, 400) {
        $seen = Hd-InHide $ms { KM-OvMove }
        Start-Sleep -Milliseconds 900
        Hd-After "the mouse $ms ms into the hide (seen $seen ms after the last move)" -ExpectShown
    }
}
if ($hdParts -contains 'keys') {
    foreach ($ms in 0, 120, 240) {
        [void](Hd-InHide $ms { [void](KM-Press 'Down'); [void](KM-Press 'Up') }); Start-Sleep -Milliseconds 900
        Hd-After "volume keys $ms ms into the hide" -ExpectShown
        [void](Hd-InHide $ms { [void](KM-Press 'Right') }); Start-Sleep -Milliseconds 900
        Hd-After "a seek key (Right) $ms ms into the hide" -ExpectShown
        [void](Hd-InHide $ms { foreach ($i in 1..8) { [void](KM-Press 'Right' -Repeat) }; [void](KM-Press 'Left') }); Start-Sleep -Milliseconds 900
        Hd-After "Right held (auto-repeat) $ms ms into the hide" -ExpectShown
        [void](Hd-InHide $ms { [void](KM-Press 'Space') }); Start-Sleep -Milliseconds 900
        Hd-After "Space (pause) $ms ms into the hide" -ExpectShown
    }
    # a seek by click: the seek bar clicked while they show, then left to hide
    KM-ShowBars; $sb = Find-Ui -Id 'SeekBar'
    if ($sb) { $r = [OffscreenUi]::RectOf($sb); [void][KeysMatrix]::Pointer($script:hwnd, 1, [int]($r.X + $r.Width * 0.3), [int]($r.Y + $r.Height / 2), 0); Start-Sleep -Milliseconds 900; Hd-After 'a click on the seek bar' -ExpectShown }
}
if ($hdParts -contains 'minimize') {
    foreach ($ms in 0, 100, 200) {
        [void](Hd-InHide $ms { [void][HudProbe]::ShowWindow($script:hwnd, 6) }); Start-Sleep -Milliseconds 1500; Hd-Restore
        Hd-After "minimized $ms ms into the hide, restored"
        KM-OvMove; Start-Sleep -Milliseconds 300
        Hd-InShow $ms { [void][HudProbe]::ShowWindow($script:hwnd, 6) }; Start-Sleep -Milliseconds 1500; Hd-Restore
        Hd-After "minimized $ms ms into the show, restored"
        [void](Hd-InHide $ms { [void][HudProbe]::ShowWindow($script:hwnd, 6); Start-Sleep -Milliseconds 400; KM-OvMove }); Start-Sleep -Milliseconds 1200; Hd-Restore
        Hd-After "minimized $ms ms into the hide, the mouse while minimized, restored"
    }
}
if ($hdParts -contains 'fullscreen') {
    foreach ($how in 'F', 'double click', 'button') {
        foreach ($when in 'shown', 'in the hide', 'hidden') {
            Hd-Windowed
            $enter = switch ($how) {
                'F' { { [void](KM-Press 'F') } }
                'double click' { { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 2, $p[0], $p[1], 0); Start-Sleep -Milliseconds 300 } }
                'button' { { KM-ShowBars; [void](KM-Click 'FullScreenButton') } }
            }
            if ($when -eq 'shown') { KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 500; & $enter }
            elseif ($when -eq 'in the hide') { [void](Hd-InHide 120 $enter) }
            else { [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 600; & $enter }
            $st = Wait-WindowState $script:hwnd $true 6; Start-Sleep -Milliseconds 1200
            Hd-Row "full screen by $how, bars $when" 'the window is full screen' $st.FullScreen $st.Rect
            Hd-After "full screen by $how, bars $when"
            Hd-Picture "full screen by $how"
            # out: Esc, F or a double click, in the hide
            $leave = switch ($how) { 'F' { { [void](KM-Press 'Esc') } } 'double click' { { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 2, $p[0], $p[1], 0) } } 'button' { { [void](KM-Press 'F') } } }
            [void](Hd-InHide 120 $leave)
            $st = Wait-WindowState $script:hwnd $false 6; Start-Sleep -Milliseconds 1200
            $name = switch ($how) { 'F' { 'Esc' } 'double click' { 'a double click' } 'button' { 'F' } }
            Hd-Row "out of full screen by $name in the hide" 'the window is windowed' (-not $st.FullScreen) $st.Rect
            Hd-After "out of full screen by $name in the hide"
        }
    }
    Hd-Windowed
}
if ($hdParts -contains 'maximize') {
    foreach ($when in 'shown', 'in the hide') {
        Hd-Windowed
        if ($when -eq 'shown') { KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 400; [void][HudProbe]::ShowWindow($script:hwnd, 3) } else { [void](Hd-InHide 120 { [void][HudProbe]::ShowWindow($script:hwnd, 3) }) }
        Start-Sleep -Milliseconds 1200
        Hd-After "maximized, bars $when"
        Hd-Picture 'maximized'
        if ($when -eq 'shown') { KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 400; [void][HudProbe]::ShowWindow($script:hwnd, 9) } else { [void](Hd-InHide 120 { [void][HudProbe]::ShowWindow($script:hwnd, 9) }) }
        Start-Sleep -Milliseconds 1200
        Hd-After "restored from maximized, bars $when"
    }
}
if ($hdParts -contains 'resize') {
    $sizes = @(@(1700, 960), @(1500, 900), @(1300, 820), @(1100, 760), @(1300, 820), @(1600, 940), @(1906, 1016))
    foreach ($when in 'shown', 'in the hide', 'in the show') {
        Hd-Windowed
        $drag = { foreach ($z in $sizes) { [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, $z[0], $z[1], 0x16); Start-Sleep -Milliseconds 45 } }
        if ($when -eq 'shown') { KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 400; & $drag }
        elseif ($when -eq 'in the hide') { [void](Hd-InHide 60 $drag) }
        else { Hd-InShow 60 $drag }
        Start-Sleep -Milliseconds 1000
        Hd-After "a live resize, bars $when"
    }
    Hd-Windowed
    Hd-Picture 'windowed again after the resizes'
}
if ($hdParts -contains 'stall') {
    # the UI thread busy while the fade runs on the compositor: a move posted during the stall is handled after it
    function Hd-Stall([int]$Ms, [int]$MoveAt) { $p = KM-VideoPoint; $why = [HudProbe]::Stall($script:hwnd, $script:ui.Id, $Ms, $MoveAt, $p[0], $p[1]); if ($why) { Say "hud: stall: $why" } }
    foreach ($ms in 0, 150, 280) {
        foreach ($len in 150, 600) {
            [void](Hd-InHide $ms { Hd-Stall $len 50 }); Start-Sleep -Milliseconds 900
            Hd-After "the UI thread busy $len ms from $ms ms into the hide, a move in it" -ExpectShown
        }
    }
    # the auto-hide's heartbeat and a move both waiting on a busy UI thread (handled in the same turn)
    foreach ($at in 2700, 2900, 3100) {
        KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 600
        KM-OvMove; Start-Sleep -Milliseconds $at
        Hd-Stall 700 600; Start-Sleep -Milliseconds 900
        Hd-After "the UI thread busy from $at ms after the last move (the auto-hide due in it), a move at its end" -ExpectShown
    }
    # busy in the show
    foreach ($ms in 0, 100, 200) {
        Hd-InShow $ms { Hd-Stall 600 -1 }; Start-Sleep -Milliseconds 600
        Hd-After "the UI thread busy 600 ms from $ms ms into the show"
    }
}
if ($hdParts -contains 'collapse') {
    # the player hidden and shown again while its bars show (the hook's kind 8: what the page does with its video, gone after a
    # crash or at the end and back with the next playback): XAML fades the bars out with the player and in again with it (seen
    # in a trace before the fix); shown again, they must be whole. Paused, so the bars stay up; then playing, so they hide and show.
    foreach ($ms in 60, 150, 250, 600) {
        KM-SetPaused $true; KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 700
        $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 8, $p[0], $p[1], $ms)
        Start-Sleep -Milliseconds ($ms + 1200)
        $s = Hd-Read
        $ok = $s.Ui -and $s.Title -ge 0.9 -and $s.Fill
        if (-not $ok) { [void](Hd-Read "the player hidden $ms ms and shown again") }
        Hd-Row "the player hidden $ms ms and shown again, the bars up" 'shown: as whole as the reference (title 90 %)' $ok (Hd-Desc $s)
        # the mouse moving on over it (the friend's): still whole, or still faint
        foreach ($i in 1..4) { KM-OvMove; Start-Sleep -Milliseconds 250 }
        $s = Hd-Read
        Hd-Row "the player hidden $ms ms and shown again, the bars up, the mouse moving on" 'shown: as whole as the reference (title 90 %)' ($s.Ui -and $s.Title -ge 0.9 -and $s.Fill) (Hd-Desc $s)
        KM-SetPaused $false
        Hd-After "the player hidden $ms ms and shown again, then playing"
    }
}
if ($hdParts -contains 'uia') {
    # UI Automation's Invoke and SetFocus on Play/Pause (Narrator's way, and this script's own start) while the bars fade out:
    # the element found while they showed, used once UI Automation has let them go
    foreach ($ms in 0, 60, 120, 200, 280) {
        foreach ($how in 'Invoke', 'SetFocus and Invoke') {
            KM-SetPaused $false; KM-ShowBars; KM-OvMove; Start-Sleep -Milliseconds 400
            $pp = Find-Ui -Id 'PlayPauseButton'
            [void](Hd-InHide $ms { if ($how -ne 'Invoke') { [void][OffscreenUi]::SetFocus($pp) }; try { ([System.Windows.Automation.InvokePattern]$pp.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() } catch { Say "hud: Invoke: $($_.Exception.Message)" } })
            Start-Sleep -Milliseconds 1000
            $s = Hd-Read
            if (-not $s.Ui) { KM-OvMove; Start-Sleep -Milliseconds 400; $s = Hd-Read }
            $ok = $s.Ui -and $s.Title -ge 0.9 -and $s.Fill
            if (-not $ok) { [void](Hd-Read "uia $how $ms ms into the hide") }
            Hd-Row "UI Automation's $how on Play/Pause $ms ms into the hide" 'shown: as whole as the reference (title 90 %)' $ok (Hd-Desc $s)
            KM-HomeKeyboard
        }
    }
}
if ($hdParts -contains 'start') {
    # the window restored from maximized and sized to 1906x1016 (the friend's) while the bars fade in or out, as this script's
    # own start does; then, shown, they must be as whole as the reference, closely: the title's white at 90 % of it or more (two
    # of eight runs before the fix began with the bars at about 96 %, which the looser check passes)
    function Hd-Strict([string]$Case) {
        Start-Sleep -Milliseconds 1000
        $s = Hd-Read
        if (-not $s.Ui) { KM-OvMove; Start-Sleep -Milliseconds 300; $s = Hd-Read }
        $ok = $s.Ui -and $s.Title -ge 0.9 -and $s.Fill
        if (-not $ok) { [void](Hd-Read $Case) }
        Hd-Row $Case 'shown: as whole as the reference (title 90 %)' $ok (Hd-Desc $s)
    }
    $restore = { [void][HudProbe]::ShowWindow($script:hwnd, 9); [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 7, 7, 1906, 1016, 0x14) }
    foreach ($ms in 0, 40, 80, 140) {
        [void][HudProbe]::ShowWindow($script:hwnd, 3); Start-Sleep -Milliseconds 900
        Hd-InShow $ms $restore
        Hd-Strict "restored from maximized and sized $ms ms into the fade in"
        [void][HudProbe]::ShowWindow($script:hwnd, 3); Start-Sleep -Milliseconds 900
        [void](Hd-InHide $ms $restore); Start-Sleep -Milliseconds 300; KM-OvMove
        Hd-Strict "restored from maximized and sized $ms ms into the fade out, then the mouse"
        # sized only (a drag of the window's edge), in the fade in
        Hd-InShow $ms { [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, 1700, 950, 0x16); Start-Sleep -Milliseconds 40; [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, 0, 0, 1906, 1016, 0x16) }
        Hd-Strict "sized $ms ms into the fade in"
    }
    Hd-Windowed
}
if ($hdParts -contains 'open') {
    # the player opened again and again (a new player page each time: the bars fade in as the video comes): about a second
    # later they must be drawn as whole as the reference, closely (a fade cut short at 93 % passes the looser check above)
    $n = if ($env:HUD_OPENS) { [int]$env:HUD_OPENS } else { 10 }
    foreach ($k in 1..$n) {
        [void](KM-Press 'BackKey')
        $row = Wait-Until { if (-not (Find-Ui -Id 'PlayPauseButton')) { Find-EpisodeRow } } -Seconds 15 -PollMs 200
        if (-not $row) { Hd-Row "opened again ($k)" 'back on the season page' $false ''; break }
        Start-Sleep -Milliseconds 1500
        [void](Invoke-Element (Find-EpisodeRow))
        $shown = Wait-Until { Find-Ui -Id 'PlayPauseButton' } -Seconds 40 -PollMs 50
        Start-Sleep -Milliseconds 1200
        $s = Hd-Read
        $fillClose = $s.Colour -match '^(\d+),(\d+),(\d+)$' -and ([math]::Abs([int]$Matches[1] - $script:hdRefFill[0]) + [math]::Abs([int]$Matches[2] - $script:hdRefFill[1]) + [math]::Abs([int]$Matches[3] - $script:hdRefFill[2])) -le 15
        $ok = [bool]$shown -and $s.Ui -and $fillClose -and $s.Title -ge 0.9
        if (-not $ok) { [void](Hd-Read "opened again $k") }
        Hd-Row "opened again ($k)" 'about a second in: drawn as whole as the reference (fill within 15, title 90 %)' $ok (Hd-Desc $s)
        KM-ConnectPipe
    }
    Hd-Windowed
}
if ($hdParts -contains 'probe') {
    # what the compositor's opacity reads through a fade in (the hook's moves log it every 30 ms; no fade is started by them)
    foreach ($k in 1..2) {
        KM-SetPaused $false; [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 600
        foreach ($i in 0..14) { KM-OvMove; Start-Sleep -Milliseconds 30 }
        Start-Sleep -Milliseconds 500
    }
}
if ($hdParts -contains 'cut') {
    # the report's state made on purpose: the bars' fades stopped short by the hook (kind 7: what the compositor leaves when it
    # stops a fade), in the fade in and in the fade out. Shown, the bars must end whole; hidden, nothing drawn and no click on them.
    function Hd-Cut { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 7, $p[0], $p[1], 0) }
    foreach ($ms in 30, 80, 150) {
        Hd-InShow $ms { Hd-Cut }; Start-Sleep -Milliseconds 1200
        Hd-After "the fade in cut $ms ms into it" -ExpectShown
    }
    foreach ($ms in 30, 150) {
        [void](Hd-InHide $ms { Hd-Cut }); Start-Sleep -Milliseconds 900
        [void](Hd-Settled "the fade out cut $ms ms into it" -Click)
        Hd-After "the fade out cut $ms ms into it, then"
    }
    # cut in the fade in, then the mouse kept moving over the player (as the friend did): still whole
    Hd-InShow 30 { Hd-Cut }
    foreach ($i in 1..6) { Start-Sleep -Milliseconds 250; KM-OvMove }
    Start-Sleep -Milliseconds 300
    Hd-After 'the fade in cut 30 ms into it, the mouse moving on' -ExpectShown
}
if ($hdParts -contains 'theme') {
    # Windows' glass settings changing (transparency effects, Energy saver, high contrast: GlassMode.Changed) and the layout
    # theme chosen in place, while the bars show, fade out and fade in (the hook's kinds 5 and 6)
    function Hd-Glass { [void][KeysMatrix]::Pointer($script:hwnd, 5, 0, 0, 0) }
    function Hd-Theme { [void][KeysMatrix]::Pointer($script:hwnd, 6, 0, 0, 0) }
    foreach ($ms in 0, 60, 150, 250) {
        [void](Hd-InHide $ms { Hd-Glass }); Start-Sleep -Milliseconds 1200
        Hd-After "the glass mode changed $ms ms into the hide"
        Hd-InShow $ms { Hd-Glass }; Start-Sleep -Milliseconds 1200
        Hd-After "the glass mode changed $ms ms into the show"
    }
    # each theme draws its bars its own way: the bars after the switch against the same theme's after a clean hide and show
    function Hd-Raw {
        Hd-Remember
        $bmp = [HudProbe]::Grab($script:hwnd)
        try {
            $r = $script:hdPpRect; $c = [HudProbe]::Mean($bmp, $script:hwnd, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height * 0.84), 3)
            $r = $script:hdTitleRect; $w = [HudProbe]::White($bmp, $script:hwnd, [int]$r.X, [int]$r.Y, [int]($r.X + $r.Width), [int]($r.Y + $r.Height))
        }
        finally { $bmp.Dispose() }
        [pscustomobject]@{ Fill = $c; White = $w }
    }
    function Hd-ThemeCase([string]$Case) {
        KM-OvMove; Start-Sleep -Milliseconds 700
        $now = Hd-Raw
        KM-SetPaused $false; [void](KM-OvWaitHidden 8); Start-Sleep -Milliseconds 700
        KM-OvMove; Start-Sleep -Milliseconds 900
        $clean = Hd-Raw
        $ok = (KM-OvNear $now.Fill $clean.Fill) -and $now.White -ge 0.6 * $clean.White
        Hd-Row $Case 'shown: drawn as after a clean hide and show' $ok "now fill $($now.Fill -join ','), title $($now.White) px; clean fill $($clean.Fill -join ','), title $($clean.White) px"
    }
    foreach ($ms in 0, 60, 150, 250) {
        [void](Hd-InHide $ms { Hd-Theme }); Start-Sleep -Milliseconds 1500
        Hd-ThemeCase "the layout theme changed $ms ms into the hide"
        Hd-InShow $ms { Hd-Theme }; Start-Sleep -Milliseconds 1500
        Hd-ThemeCase "the layout theme changed $ms ms into the show"
    }
    # back to the first theme (four switches a round): the reference holds again
    while ((Hd-Raw).Fill -and -not (KM-OvNear (Hd-Raw).Fill $script:hdRefFill) -and $script:hdThemeTurns++ -lt 3) { Hd-Theme; Start-Sleep -Milliseconds 1500; KM-OvMove; Start-Sleep -Milliseconds 700 }
}
if ($hdParts -contains 'episode') {
    # the next and previous episode (Shift+N, Shift+P, the bar's buttons) while the bars fade out, and while they fade in
    foreach ($ms in 0, 150) {
        $t = [KeysMatrix]::Get('media-title')
        [void](Hd-InHide $ms { [void](KM-Press 'Shift+N') })
        [void](Wait-Until { if ([KeysMatrix]::Get('media-title') -ne $t) { $true } } -Seconds 12 -PollMs 300); Start-Sleep -Seconds 2
        Hd-After "Shift+N $ms ms into the hide"
        $t = [KeysMatrix]::Get('media-title')
        Hd-InShow $ms { [void](KM-Press 'Shift+P') }
        [void](Wait-Until { if ([KeysMatrix]::Get('media-title') -ne $t) { $true } } -Seconds 12 -PollMs 300); Start-Sleep -Seconds 2
        Hd-After "Shift+P $ms ms into the show"
    }
    KM-ShowBars; $t = [KeysMatrix]::Get('media-title'); [void](KM-Click 'NextButton')
    [void](Wait-Until { if ([KeysMatrix]::Get('media-title') -ne $t) { $true } } -Seconds 12 -PollMs 300); Start-Sleep -Seconds 2
    Hd-After 'the Next button'
}
if ($hdParts -contains 'monitors') {
    $mons = @([OffscreenUi]::Monitors())
    if ($mons.Count -lt 2) { Say "hud: one monitor ($($mons -join '')): no move between monitors or DPIs here" }
    else {
        foreach ($m in $mons) {
            if ($m -notmatch '^\S+ (-?\d+),(-?\d+) (\d+)x(\d+)') { continue }
            [void](Hd-InHide 120 { [void][HudProbe]::SetWindowPos($script:hwnd, [IntPtr]::Zero, [int]$Matches[1] + 40, [int]$Matches[2] + 40, 1500, 900, 0x14) })
            Start-Sleep -Milliseconds 1500
            Say "hud: moved to $m (dpi $([HudProbe]::GetDpiForWindow($script:hwnd)))"
            Hd-After "moved to the monitor $m in the hide"
        }
        Hd-Windowed
    }
}

[IO.File]::WriteAllText((Join-Path $hdDir 'hud.json'), (ConvertTo-Json -InputObject @($hdRows) -Depth 4), [Text.UTF8Encoding]::new($false))
$hdBad = @($hdRows | Where-Object { -not $_.ok })
Say "hud: $($hdRows.Count) checks, $($hdBad.Count) failed"
foreach ($b in $hdBad) { Say "hud FAIL: $($b.case) | $($b.check) | $($b.detail)" }
[KeysMatrix]::Close()
