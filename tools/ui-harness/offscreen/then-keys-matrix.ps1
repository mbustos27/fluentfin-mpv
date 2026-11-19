# Nixie 1.3.1, the keyboard matrix: every documented player key, in every context, checked through the player's own state.
# Run once per layout theme (tools\ui-harness\keys-matrix.ps1 runs all four and writes shots\keys-matrix.md):
#   NIXIE_LAYOUT=<Classic|DriveIn|Cinematic|Editorial> run-offscreen.ps1 ... -Script season,play -Then offscreen\then-keys-matrix.ps1
# Dot-sourced by drive.ps1 after the play step (FluentFin's controls; paused, the bars up). Every name here starts with KM.
#
# How it presses keys: as a real keyboard does, to the window that has the keyboard focus of FluentFin's UI thread
# (GetGUIThreadInfo's hwndFocus), never straight to the XAML island's input window (that hid where real keys go). Modifiers
# (Ctrl, Shift, Alt) are set in the UI thread's keyboard state for the key (AttachThreadInput + SetKeyboardState), as WinUI
# reads them there. How it clicks: posted clicks never reach WinUI off-screen, so a dev build's own pointer hook
# (ui\FluentFin\Services\HarnessPointer.cs, NIXIE_HARNESS_POINTER=1, set by run-offscreen.ps1) does what a click does through
# the same code: WinUI's focus on a press, a button's click, the seek bar's press and drag, the video's tap and double tap.
# The wheel is a real posted WM_MOUSEWHEEL (WinUI takes those). What it reads: mpv's own state over the in-process core's IPC
# pipe (pause, time-pos, volume, mute, speed, sid, aid, sub-delay), the window's full-screen state, and UI Automation (the bars
# shown, a panel open, the focused element). Results: <Out>\keys-matrix.json (one row per check) and a Check per row.

$kmTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Default' }
$kmRows = New-Object System.Collections.ArrayList
$kmDir = Join-Path $Out 'keys-matrix'
New-Item -ItemType Directory -Force $kmDir | Out-Null

if (-not ('KeysMatrix' -as [type])) {
    Add-Type @"
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class KeysMatrix {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO g);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
  [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

  public static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static uint ProcessOf(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  static uint ThreadOf(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }

  // the window FluentFin's UI thread has the keyboard focus on (where a real key goes); zero when none
  public static IntPtr FocusWindow(IntPtr main) {
    var g = new GUITHREADINFO(); g.cbSize = Marshal.SizeOf(g);
    return GetGUIThreadInfo(ThreadOf(main), ref g) ? g.hwndFocus : IntPtr.Zero;
  }

  // a key press posted to target (FluentFin's focus window), with ctrl/shift/alt held in the UI thread's keyboard state for
  // it; repeat marks it as auto-repeat (the key already down). Returns false when the modifiers could not be set.
  public static bool Key(IntPtr target, int vk, bool ctrl, bool shift, bool alt, bool repeat) {
    bool mods = ctrl || shift || alt, attached = false;
    uint ui = ThreadOf(target), me = GetCurrentThreadId();
    byte[] before = new byte[256];
    if (mods) {
      attached = AttachThreadInput(me, ui, true);
      if (!attached) return false;
      GetKeyboardState(before);
      var st = (byte[])before.Clone();
      if (ctrl) { st[0x11] = 0x80; st[0xA2] = 0x80; }
      if (shift) { st[0x10] = 0x80; st[0xA0] = 0x80; }
      if (alt) { st[0x12] = 0x80; st[0xA4] = 0x80; }
      SetKeyboardState(st);
      Thread.Sleep(60);
    }
    try {
      uint scan = MapVirtualKey((uint)vk, 0);
      uint down = alt ? 0x0104u : 0x0100u, up = alt ? 0x0105u : 0x0101u;
      uint l = 1u | (scan << 16) | (alt ? (1u << 29) : 0u) | (repeat ? (1u << 30) : 0u);
      PostMessage(target, down, (IntPtr)vk, (IntPtr)(long)l);
      Thread.Sleep(mods ? 500 : 30);   // held until the UI thread has surely read it (a busy thread read a plain N once)
      uint lu = 1u | (scan << 16) | (alt ? (1u << 29) : 0u) | (1u << 30) | (1u << 31);
      PostMessage(target, up, (IntPtr)vk, (IntPtr)(long)lu);
      if (mods) Thread.Sleep(300);
    } finally {
      if (mods) {
        var clear = (byte[])before.Clone();
        foreach (var k in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) clear[k] = 0;
        SetKeyboardState(clear);
        AttachThreadInput(me, ui, false);
      }
    }
    return true;
  }

  // Ctrl/Shift/Alt held in the UI thread's keyboard state until Release (KM-Key holds them until the key has done its job: a
  // UI thread busy for longer than a fixed hold once read a Shift+Right as a plain Right)
  static byte[] _before; static uint _heldUi, _heldMe;
  public static bool Hold(IntPtr target, bool ctrl, bool shift, bool alt) {
    Release();
    uint ui = ThreadOf(target), me = GetCurrentThreadId();
    if (!AttachThreadInput(me, ui, true)) return false;
    var before = new byte[256]; GetKeyboardState(before);
    var st = (byte[])before.Clone();
    if (ctrl) { st[0x11] = 0x80; st[0xA2] = 0x80; }
    if (shift) { st[0x10] = 0x80; st[0xA0] = 0x80; }
    if (alt) { st[0x12] = 0x80; st[0xA4] = 0x80; }
    SetKeyboardState(st);
    _before = before; _heldUi = ui; _heldMe = me;
    Thread.Sleep(60);
    return true;
  }
  public static void Release() {
    if (_before == null) return;
    var clear = (byte[])_before.Clone();
    foreach (var k in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }) clear[k] = 0;
    SetKeyboardState(clear);
    AttachThreadInput(_heldMe, _heldUi, false);
    _before = null;
  }
  // the key alone (its modifiers already held), WM_SYSKEYDOWN/UP with alt
  public static void Post(IntPtr target, int vk, bool alt, bool repeat) {
    uint scan = MapVirtualKey((uint)vk, 0);
    uint down = alt ? 0x0104u : 0x0100u, up = alt ? 0x0105u : 0x0101u;
    uint l = 1u | (scan << 16) | (alt ? (1u << 29) : 0u) | (repeat ? (1u << 30) : 0u);
    PostMessage(target, down, (IntPtr)vk, (IntPtr)(long)l);
    Thread.Sleep(30);
    uint lu = 1u | (scan << 16) | (alt ? (1u << 29) : 0u) | (1u << 30) | (1u << 31);
    PostMessage(target, up, (IntPtr)vk, (IntPtr)(long)lu);
  }

  // the dev build's pointer hook (HarnessPointer): kind 1 click, 2 double click, 3 drag to toX; x, y on the screen
  public static bool Pointer(IntPtr main, int kind, int x, int y, int toX) {
    uint msg = RegisterWindowMessage("NixieHarnessPointer");
    uint w = (uint)(kind & 0xFFFF) | ((uint)(toX & 0xFFFF) << 16);
    uint l = (uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16);
    return PostMessage(main, msg, (IntPtr)(long)w, (IntPtr)(long)l);
  }

  // mpv's state over the in-process core's IPC pipe: get_property, answered by request id
  static NamedPipeClientStream _pipe; static StreamReader _reader; static StreamWriter _writer; static int _id = 100;
  public static string Connect(string name) {
    try {
      _pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
      _pipe.Connect(3000);
      _reader = new StreamReader(_pipe, new UTF8Encoding(false));
      _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
      return null;
    } catch (Exception e) { return e.Message; }
  }
  public static string Get(string property) {
    if (_writer == null) return null;
    lock (typeof(KeysMatrix)) {
      int id = ++_id;
      _writer.WriteLine("{\"command\":[\"get_property\",\"" + property + "\"],\"request_id\":" + id + "}");
      var until = DateTime.UtcNow.AddSeconds(2);
      while (DateTime.UtcNow < until) {
        var line = _reader.ReadLine();
        if (line == null) return null;
        if (line.Contains("\"request_id\":" + id)) {
          if (!line.Contains("\"error\":\"success\"")) return null;
          int at = line.IndexOf("\"data\":", StringComparison.Ordinal);
          if (at < 0) return null;
          var rest = line.Substring(at + 7);
          int end = rest.IndexOf(",\"request_id\"", StringComparison.Ordinal);
          if (end < 0) end = rest.IndexOf(",\"error\"", StringComparison.Ordinal);
          return (end < 0 ? rest.TrimEnd('}') : rest.Substring(0, end)).Trim('"');
        }
      }
      return null;
    }
  }
  public static void Close() { try { if (_pipe != null) _pipe.Dispose(); } catch (Exception) { } }
}
"@
}

# ---- reading the player ------------------------------------------------------------------------------------------------
# the in-process core's pipe, named in FluentFin's log (the latest one: a new core after a theme switch and a new play)
function KM-ConnectPipe {
    [KeysMatrix]::Close()
    $pipe = $null
    foreach ($f in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)) {
        $m = Select-String -LiteralPath $f.FullName -Pattern 'IPC pipe (fluentfin-mpv-[0-9a-f-]+)' | Select-Object -Last 1
        if ($m) { $pipe = $m.Matches[0].Groups[1].Value; break }
    }
    $err = if ($pipe) { [KeysMatrix]::Connect($pipe) } else { 'no pipe in the log' }
    Check "keys-matrix ${kmTheme}: mpv's state is readable (its IPC pipe)" (-not $err) $(if ($err) { $err } else { $pipe })
}

function KM-Num([string]$Property) { $v = [KeysMatrix]::Get($Property); $d = 0.0; if ($v -and [double]::TryParse($v, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $d } else { $null } }
function KM-Bool([string]$Property) { [KeysMatrix]::Get($Property) -eq 'true' }
function KM-Bars { [bool](Find-Ui -Id 'PlayPauseButton') }
function KM-State([switch]$NoUi) {
    $s = [ordered]@{
        Paused = KM-Bool 'pause'; Pos = KM-Num 'time-pos'; Vol = KM-Num 'volume'; Mute = KM-Bool 'mute'; Speed = KM-Num 'speed'
        Sid = [KeysMatrix]::Get('sid'); Aid = [KeysMatrix]::Get('aid'); Delay = KM-Num 'sub-delay'; Title = [KeysMatrix]::Get('media-title')
        Full = (Get-WindowState $script:hwnd).FullScreen
    }
    if (-not $NoUi) { $s.Bars = KM-Bars }
    [pscustomobject]$s
}
function KM-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { ($f | ConvertFrom-Json) } else { $null } }
function KM-FocusName { $f = KM-Focus; if ($f) { if ($f.automationId) { $f.automationId } else { "$($f.type) '$($f.name)'" } } else { 'nothing' } }

# ---- input ---------------------------------------------------------------------------------------------------------------
$kmKeys = @{
    'Space' = 0x20; 'Enter' = 0x0D; 'Esc' = 0x1B; 'Tab' = 0x09; 'Left' = 0x25; 'Up' = 0x26; 'Right' = 0x27; 'Down' = 0x28; 'Home' = 0x24
    'End' = 0x23; 'PageUp' = 0x21; 'PageDown' = 0x22; 'Backspace' = 0x08; 'F11' = 0x7A; '[' = 0xDB; ']' = 0xDD; 'BackKey' = 0xA6
    '0' = 0x30; '5' = 0x35; '9' = 0x39; 'Numpad3' = 0x63; 'Apps' = 0x5D; '/' = 0xBF; 'F1' = 0x70
}
function KM-Vk([string]$Name) { if ($kmKeys.ContainsKey($Name)) { $kmKeys[$Name] } else { [int][char]$Name.ToUpperInvariant() } }

# a chord such as 'Shift+Right', 'Ctrl+Left', 'Alt+Enter', 'Space', 'M'; posted to FluentFin's focus window
function KM-Press([string]$Chord, [switch]$Repeat) {
    $parts = $Chord -split '\+'
    $name = $parts[-1]
    $target = [KeysMatrix]::FocusWindow($script:hwnd)
    if ($target -eq [IntPtr]::Zero -or [KeysMatrix]::ProcessOf($target) -ne [uint32]$script:ui.Id) {
        $script:kmLastTarget = "no FluentFin window has the keyboard ($([KeysMatrix]::ClassOf($target)))"
        return $false
    }
    $script:kmLastTarget = [KeysMatrix]::ClassOf($target)
    [KeysMatrix]::Key($target, (KM-Vk $name), ($parts -contains 'Ctrl'), ($parts -contains 'Shift'), ($parts -contains 'Alt'), [bool]$Repeat)
}

# the centre of a UI Automation element (by automation id or name), on the screen
function KM-Centre($Element) { $r = [OffscreenUi]::RectOf($Element); @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function KM-Find([string]$Id, [string]$Name) { if ($Id) { Find-Ui -Id $Id } else { Find-Ui -Name $Name } }
# a click (or double click) through the dev build's pointer hook at an element's centre or a point
function KM-Click([string]$Id, [int[]]$At, [switch]$Double, [string]$Name) {
    if (-not $At) {
        $e = KM-Find $Id $Name
        if (-not $e) { return $false }
        $At = KM-Centre $e
    }
    [void][KeysMatrix]::Pointer($script:hwnd, $(if ($Double) { 2 } else { 1 }), $At[0], $At[1], 0)
    Start-Sleep -Milliseconds $(if ($Double) { 700 } else { 450 })
    $true
}
function KM-Drag([string]$Id, [double]$From, [double]$To) {
    $e = Find-Ui -Id $Id
    if (-not $e) { return $false }
    $r = [OffscreenUi]::RectOf($e)
    $y = [int]($r.Y + $r.Height / 2)
    [void][KeysMatrix]::Pointer($script:hwnd, 3, [int]($r.X + $r.Width * $From), $y, [int]($r.X + $r.Width * $To))
    Start-Sleep -Milliseconds 900
    $true
}
# the middle of the video (where no control is): the window's centre, a little above
function KM-VideoPoint { $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r); @([int](($r.L + $r.R) / 2), [int]($r.T + ($r.B - $r.T) * 0.42)) }
# a point in the bottom bar's scrim fade (just above its controls), or $null when the bar is not shown
function KM-ScrimPoint {
    $seek = Find-Ui -Id 'SeekBar'
    if (-not $seek) { return $null }
    $r = [OffscreenUi]::RectOf($seek)
    $w = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$w)
    @([int](($w.L + $w.R) / 2), [int]($r.Y - 40))
}
function KM-Wheel([int]$Delta) {
    $p = KM-VideoPoint
    [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, $p[0], $p[1], $Delta, $false, $false)
    Start-Sleep -Milliseconds 500
}

# ---- results -----------------------------------------------------------------------------------------------------------
function KM-Row([string]$Section, [string]$Case, [string]$Key, [bool]$Ok, [string]$Detail) {
    [void]$kmRows.Add([ordered]@{ theme = $kmTheme; section = $Section; case = $Case; key = $Key; ok = $Ok; detail = $Detail })
    Check "keys-matrix $kmTheme | $Section | $Case | $Key" $Ok $Detail
}

# what a key must have done, from the state before to the state after; $null while it has not (yet)
function KM-Did([string]$Chord, $B, $A) {
    $dp = if ($null -ne $A.Pos -and $null -ne $B.Pos) { $A.Pos - $B.Pos } else { $null }
    switch ($Chord) {
        { $_ -in 'Space', 'K' } { return $A.Paused -ne $B.Paused }
        'Left' { return $null -ne $dp -and $dp -le -2.5 }
        'Right' { return $null -ne $dp -and $dp -ge 3.5 }
        { $_ -in 'J', 'Ctrl+Left' } { return $null -ne $dp -and $dp -le -7 }
        { $_ -in 'L', 'Ctrl+Right' } { return $null -ne $dp -and $dp -ge 8 }
        'Shift+Left' { return $null -ne $dp -and $dp -le -25 }
        'Shift+Right' { return $null -ne $dp -and $dp -ge 25 }
        { $_ -in 'Home', '0' } { return $null -ne $A.Pos -and $A.Pos -lt 4 }
        '5' { return $null -ne $A.Pos -and [math]::Abs($A.Pos - 300) -lt 6 }
        'Numpad3' { return $null -ne $A.Pos -and [math]::Abs($A.Pos - 180) -lt 6 }
        'Up' { return ($A.Vol -gt $B.Vol) -or ($B.Mute -and -not $A.Mute) }
        'Down' { return ($A.Vol -lt $B.Vol) -or ($B.Mute -and -not $A.Mute) }
        'M' { return $A.Mute -ne $B.Mute }
        { $_ -in 'F', 'F11', 'Alt+Enter' } { return $A.Full -ne $B.Full }
        '[' { return $A.Speed -lt $B.Speed }
        ']' { return $A.Speed -gt $B.Speed }
        'Backspace' { return [math]::Abs($A.Speed - 1) -lt 0.01 }
        { $_ -in 'C', 'V' } { return $A.Sid -ne $B.Sid }
        'B' { return $A.Aid -ne $B.Aid }
        'Z' { return $A.Delay -lt $B.Delay - 0.05 }
        'X' { return $A.Delay -gt $B.Delay + 0.05 }
        'S' { return $null -ne $A.Pos -and $A.Pos -ge 39 }
        { $_ -in 'Shift+N', 'Shift+P' } { return $A.Title -and $A.Title -ne $B.Title }
        default { return $false }
    }
}

# presses a key and checks it did its job (polled up to $Seconds); the row says where the key went and what it changed. A
# chord's modifiers stay held until the key has done its job or the time is up.
function KM-Key([string]$Section, [string]$Case, [string]$Chord, [double]$Seconds = 2.5) {
    $b = KM-State -NoUi
    $parts = $Chord -split '\+'
    $mods = ($parts -contains 'Ctrl') -or ($parts -contains 'Shift') -or ($parts -contains 'Alt')
    $sent = $false
    if ($mods) {
        $target = [KeysMatrix]::FocusWindow($script:hwnd)
        if ($target -ne [IntPtr]::Zero -and [KeysMatrix]::ProcessOf($target) -eq [uint32]$script:ui.Id) {
            $script:kmLastTarget = [KeysMatrix]::ClassOf($target)
            $sent = [KeysMatrix]::Hold($target, ($parts -contains 'Ctrl'), ($parts -contains 'Shift'), ($parts -contains 'Alt'))
            if ($sent) { [KeysMatrix]::Post($target, (KM-Vk $parts[-1]), ($parts -contains 'Alt'), $false) }
        }
        else { $script:kmLastTarget = "no FluentFin window has the keyboard ($([KeysMatrix]::ClassOf($target)))" }
    }
    else { $sent = KM-Press $Chord }
    $a = $null; $ok = $false
    try {
        if ($sent) {
            $deadline = (Get-Date).AddSeconds($Seconds)
            do { Start-Sleep -Milliseconds 120; $a = KM-State -NoUi; $ok = [bool](KM-Did $Chord $b $a) } while (-not $ok -and (Get-Date) -lt $deadline)
        }
    }
    finally { if ($mods) { [KeysMatrix]::Release() } }
    $detail = if (-not $sent) { "not sent: $script:kmLastTarget" } else {
        "to $script:kmLastTarget; paused $($b.Paused)->$($a.Paused), pos $([math]::Round([double]$b.Pos,1))->$([math]::Round([double]$a.Pos,1)), vol $($b.Vol)/$($b.Mute)->$($a.Vol)/$($a.Mute), full $($b.Full)->$($a.Full), speed $($b.Speed)->$($a.Speed), sid $($b.Sid)->$($a.Sid), aid $($b.Aid)->$($a.Aid), delay $($b.Delay)->$($a.Delay)" }
    KM-Row $Section $Case $Chord $ok $detail
    $ok
}

# a key that must do NOTHING to the player (it is a panel's or a list's own): the state stays as it was for a moment
function KM-KeyIsNotThePlayers([string]$Section, [string]$Case, [string]$Chord, [scriptblock]$Also) {
    $b = KM-State -NoUi
    $sent = KM-Press $Chord
    Start-Sleep -Milliseconds 700
    $a = KM-State -NoUi
    $same = $sent -and $a.Paused -eq $b.Paused -and $a.Vol -eq $b.Vol -and $a.Mute -eq $b.Mute -and $a.Full -eq $b.Full -and [math]::Abs([double]$a.Pos - [double]$b.Pos - $(if ($b.Paused) { 0 } else { 0.7 })) -lt 2
    $extra = if ($Also) { & $Also } else { $true }
    KM-Row $Section $Case "$Chord (the panel's own)" ($same -and $extra) "to $script:kmLastTarget; paused $($b.Paused)->$($a.Paused), pos $([math]::Round([double]$b.Pos,1))->$([math]::Round([double]$a.Pos,1)), focus $(KM-FocusName)"
}

# ---- getting into a context ------------------------------------------------------------------------------------------------
# paused or playing, with K (UI Automation's Invoke on Play/Pause would move the keyboard onto the button, and a keyboard
# in the bars holds them up)
function KM-SetPaused([bool]$Paused) {
    for ($i = 0; $i -lt 3 -and (KM-Bool 'pause') -ne $Paused; $i++) {
        [void](KM-Press 'K')
        [void](Wait-Until { if ((KM-Bool 'pause') -eq $Paused) { $true } } -Seconds 3 -PollMs 100)
    }
}
function KM-SetWindowed {
    if ((Get-WindowState $script:hwnd).FullScreen) { [void](KM-Press 'Esc'); [void](Wait-WindowState $script:hwnd $false 6); Start-Sleep -Milliseconds 600 }
}
# the bars up (a key that changes nothing: Shift alone shows them? no: a volume step down and back up)
function KM-ShowBars { if (-not (KM-Bars)) { [void](KM-Press 'Down'); Start-Sleep -Milliseconds 250; [void](KM-Press 'Up'); Start-Sleep -Milliseconds 400 } }
function KM-WaitBarsHidden { [bool](Wait-Until { if (-not (KM-Bars)) { $true } } -Seconds 7 -PollMs 250) }
# the keyboard on the player itself ("nothing in particular"): a click on an empty spot of the bottom bar (between its
# button groups: no control, no play or pause), through the hook; the player's after-click step takes the keyboard
function KM-HomeKeyboard {
    KM-ShowBars
    $pp = Find-Ui -Id 'PlayPauseButton'
    if (-not $pp) { return }
    $r = [OffscreenUi]::RectOf($pp)
    $w = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$w)
    [void](KM-Click -At @([int]($w.L + ($w.R - $w.L) * 0.3), [int]($r.Y + $r.Height / 2)))
}
# somewhere with room to seek both ways (2 minutes in: digit 2), playing or paused as asked, windowed, bars up, the keyboard
# on the player
function KM-Ready([bool]$Paused) {
    # a menu or flyout a case left open (its light-dismiss layer would take the next click): Esc closes it
    for ($i = 0; $i -lt 3 -and ((Find-Ui -Id 'MuteButton') -or (Find-Ui -Id 'SubtitleTracks') -or ((KM-Focus).type -eq 'MenuItem')); $i++) { [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 400 }
    KM-SetWindowed
    [void](KM-Press '2'); Start-Sleep -Milliseconds 500
    KM-SetPaused $Paused
    KM-HomeKeyboard
}

# ==== B. browsing: the keys of each page, before the player (run with -Script home) =========================================
function KB-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
# the focused element's UI Automation runtime id (tells two cards of the same title apart: Editorial's Home shows some twice)
function KB-FocusId {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $e = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { try { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } catch { $false } } | Select-Object -First 1
    if ($e) { try { ($e.GetRuntimeId() -join '.') } catch { $null } } else { $null }
}
function KB-Desc($F) { if ($F) { "$($F.type) '$($F.name)'" } else { 'nothing' } }
# the same element: its name and type, and its place (two shelves can show the same title: Editorial's Home)
function KB-Same($A, $B) {
    if (-not (KB-SameItem $A $B)) { return $false }
    $ra = @($A.rect); $rb = @($B.rect)
    if ($ra.Count -lt 2 -or $rb.Count -lt 2) { return $true }
    [math]::Abs($ra[0] - $rb[0]) -lt 24 -and [math]::Abs($ra[1] - $rb[1]) -lt 24
}
# the same item (the place may have scrolled)
function KB-SameItem($A, $B) { $A -and $B -and $A.name -eq $B.name -and $A.type -eq $B.type }
# the first item of a list in the page (a card), under the top bar, on screen
function KB-FirstCard([double]$MinY = 120) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { try { $c = $_.Cached; -not $c.IsOffscreen -and $c.IsKeyboardFocusable -and $c.BoundingRectangle.Y -ge $MinY } catch { $false } } | Select-Object -First 1
}
# a key that must move the focus (to another element); $Back: the element it must land on instead
function KB-Moves([string]$Page, [string]$Chord, $Back) {
    $b = KB-Focus
    $bId = KB-FocusId
    $sent = KM-Press $Chord
    $a = $null
    $ok = $sent -and [bool](Wait-Until {
            $script:kbNow = KB-Focus
            if ($Back) { if (KB-SameItem $script:kbNow $Back) { $true } }
            elseif ($script:kbNow -and (-not (KB-Same $script:kbNow $b) -or ($bId -and (KB-FocusId) -ne $bId))) { $true }
        } -Seconds 3 -PollMs 150)
    $a = $script:kbNow
    KM-Row 'browsing' $Page "$Chord ($(if ($Back) { 'back' } else { 'moves the focus' }))" $ok "to $script:kmLastTarget; $(KB-Desc $b) -> $(KB-Desc $a)"
    $a
}
function KB-Start([string]$Page, $Element) {
    if (-not $Element) { KM-Row 'browsing' $Page 'a card to start from' $false 'none found'; return $null }
    [void][OffscreenUi]::SetFocus($Element); Start-Sleep -Milliseconds 500
    KB-Focus
}
# each pair: the first key moves the focus, the second moves it back: to the very card it left along a row (Right/Left,
# End/Home), to a card of the row or page it left between rows (Down/Up, Page Down/Up: the column the focus lands in is
# the layout's, and a row keeps its own remembered card)
function KB-ArrowsAndEnds([string]$Page, $Start, [string[]]$Pairs) {
    foreach ($pair in $Pairs) {
        $there, $back = $pair -split '/'
        $from = KB-Focus
        $moved = KB-Moves $Page $there $null
        if (-not $moved) { continue }
        if ($there -eq 'Right') { [void](KB-Moves $Page $back $from) }
        elseif ($there -eq 'End') { [void](KB-Moves $Page $back $Start) }
        else { [void](KB-Moves $Page $back $null) }
    }
}
# the Back key (Back, Alt+Left, Mouse 4) returns: $Test says the page is there again
# Nixie 1.4.0: FluentFin's "Refresh:" log lines since an epoch (R in a grid: refreshed or a letter)
function KM-RefreshLog([double]$Since) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '\[INF\] Refresh: ' } | ForEach-Object {
            $kmWhen = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(($_ -split ' \[INF\] ')[0], [ref]$kmWhen) -and $kmWhen.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { ($_ -split '\[INF\] ', 2)[1] }
        }
    })
}
function KM-Loads([double]$Since) { @(KM-RefreshLog $Since | Where-Object { $_ -match 'loaded again' }).Count }
$kmTitleOf = { param($Prefix) "^\s*((The|An|A)\s+)?$([regex]::Escape($Prefix))" }

function KB-Back([string]$Page, [scriptblock]$Test) {
    $sent = KM-Press 'BackKey'
    $ok = $sent -and [bool](Wait-Until $Test -Seconds 12 -PollMs 200)
    KM-Row 'browsing' $Page 'Back key (returns)' $ok "to $script:kmLastTarget"
    Start-Sleep -Milliseconds 800
}

if ($script:page -ne 'player') {
    # -- Home: a row's cards
    Open-Nav 'Home'; Start-Sleep -Seconds 3
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 700
    $kbStart = KB-Start 'Home' (Wait-Until { KB-FirstCard } -Seconds 15 -PollMs 300)
    if ($kbStart) {
        KB-ArrowsAndEnds 'Home' $kbStart @('Right/Left', 'End/Home', 'Down/Up')
        [void](KB-Start 'Home' (Find-Ui -Name $kbStart.name -Type 'ListItem'))
        $kbOpened = $kbStart.name
        $sent = KM-Press 'Enter'
        # its page, or (Editorial) its details in place under the shelf
        $kbHow = Wait-Until { if (Find-Ui -Name 'Full details' -Type 'Button' -Contains) { 'details' } elseif (-not (Find-Ui -Name $kbOpened -Type 'ListItem')) { 'page' } } -Seconds 10 -PollMs 200
        KM-Row 'browsing' 'Home' 'Enter (opens the card)' ($sent -and [bool]$kbHow) "to $script:kmLastTarget; '$kbOpened': $kbHow"
        Start-Sleep -Seconds 2
        if ($kbHow -eq 'page') { KB-Back 'Home' { Find-Ui -Name $kbOpened -Type 'ListItem' -OffscreenToo } }
        elseif ($kbHow -eq 'details') {
            [void](KM-Press 'Esc')
            $kbShut = Wait-Until { if (-not (Find-Ui -Name 'Full details' -Type 'Button' -Contains)) { $true } } -Seconds 5 -PollMs 200
            KM-Row 'browsing' 'Home' 'Esc (closes the details)' ([bool]$kbShut) "to $script:kmLastTarget"
        }
    }
    # -- Library: the grid, the ends, the pages, the letter jump, a card and back
    Open-Library $fixture.movie.library
    [void](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 150)
    Start-Sleep -Milliseconds 1500
    $kbStart = KB-Start 'Library' (Find-MediaCard $fixture.movie.name)
    if ($kbStart) {
        KB-ArrowsAndEnds 'Library' $kbStart @('Right/Left', 'Down/Up', 'PageDown/PageUp', 'End/Home')
        $kbJump = Find-Ui -Name 'Jump to C' -Type 'Button'
        if ($kbJump) {
            [void][OffscreenUi]::SetFocus($kbJump); Start-Sleep -Milliseconds 300
            [void](KM-Press 'Enter')
            $kbLanded = Wait-Until { $f = KB-Focus; if ($f -and $f.name -match '^\s*((The|An)\s+)?C') { $f } } -Seconds 8 -PollMs 150
            KM-Row 'browsing' 'Library' 'letter jump (Enter on "Jump to C")' ([bool]$kbLanded) "to $script:kmLastTarget; $(KB-Desc (KB-Focus))"
        }
        else { KM-Row 'browsing' 'Library' 'letter jump (Enter on "Jump to C")' $false 'no "Jump to C"' }
        # Nixie 1.4.0 (R in a grid): R refreshes; while you're typing a title, R is a letter; R titles: the A to Z's R
        # (a) on the A to Z, R presses R: the keyboard on a title that starts with R, nothing refreshed
        $kbJump = Find-Ui -Name 'Jump to C' -Type 'Button'
        if ($kbJump) {
            [void][OffscreenUi]::SetFocus($kbJump); Start-Sleep -Milliseconds 1300
            $kmAt = Get-Epoch
            [void](KM-Press 'R')
            $kmR = Wait-Until { $f = KB-Focus; if ($f -and $f.name -match (& $kmTitleOf 'R')) { $f } } -Seconds 6 -PollMs 150
            Start-Sleep -Milliseconds 1200
            KM-Row 'browsing' 'Library' 'R on the A to Z (the letter R: to an R title)' ([bool]$kmR -and (KM-Loads $kmAt) -eq 0) "to $script:kmLastTarget; $(KB-Desc (KB-Focus)); $(KM-Loads $kmAt) refresh(es)"
        }
        else { KM-Row 'browsing' 'Library' 'R on the A to Z (the letter R: to an R title)' $false 'no "Jump to C"' }
        # (b) a title's letters within a second, R the last: the keyboard on that title, nothing refreshed
        $kmRWord = $fixture.movie.r_word
        if ($kmRWord) {
            [void](KB-Start 'Library' (Find-MediaCard $fixture.movie.name)); Start-Sleep -Milliseconds 1300
            $kmAt = Get-Epoch
            foreach ($kmCh in $kmRWord.ToCharArray()) { [void](KM-Press "$kmCh") }
            $kmWord = Wait-Until { $f = KB-Focus; if ($f -and $f.name -match (& $kmTitleOf $kmRWord)) { $f } } -Seconds 4 -PollMs 150
            Start-Sleep -Milliseconds 1500
            KM-Row 'browsing' 'Library' "$(($kmRWord.ToLowerInvariant().ToCharArray() | ForEach-Object { "'$_'" }) -join ', ') within a second (R a letter: to $kmRWord...)" ([bool]$kmWord -and (KM-Loads $kmAt) -eq 0) "to $script:kmLastTarget; $(KB-Desc (KB-Focus)); $(KM-Loads $kmAt) refresh(es)"
        }
        else { KM-Row 'browsing' 'Library' 'a word ending in R within a second' $false 'the fixture has no r_word' }
        # (c) R alone on a poster (no letter in the last second) refreshes the library
        [void](KB-Start 'Library' (Find-MediaCard $fixture.movie.name)); Start-Sleep -Milliseconds 1300
        $kmAt = Get-Epoch
        [void](KM-Press 'R')
        $kmDone = Wait-Until { if ((KM-Loads $kmAt) -gt 0) { $true } } -Seconds 8 -PollMs 150
        KM-Row 'browsing' 'Library' 'R alone on a poster (refreshes)' ([bool]$kmDone) "to $script:kmLastTarget; $((KM-RefreshLog $kmAt) -join ' | ')"
        Start-Sleep -Seconds 3
        [void](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 15 -PollMs 200)
        [void](KB-Start 'Library' (Find-MediaCard $fixture.movie.name))
        [void](KM-Press 'Enter')
        # (Editorial: its details in place first; Enter on their Full details opens the page)
        $kbFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
        if ($kbFull) { [void][OffscreenUi]::SetFocus($kbFull); Start-Sleep -Milliseconds 300; [void](KM-Press 'Enter') }
        $kbMovie = Wait-ItemPage $fixture.movie.name 12
        KM-Row 'browsing' 'Library' 'Enter (opens the movie)' ([bool]$kbMovie) "to $script:kmLastTarget"
        if ($kbMovie) {
            Start-Sleep -Seconds 2
            # -- Movie: Tab into the page, Back
            $kbB = KB-Focus; [void](KM-Press 'Tab'); Start-Sleep -Milliseconds 500
            KM-Row 'browsing' 'Movie' 'Tab (moves the focus)' (-not (KB-Same (KB-Focus) $kbB)) "$(KB-Desc $kbB) -> $(KB-Desc (KB-Focus))"
            KB-Back 'Movie' { Find-MediaCard $fixture.movie.name }
        }
    }
    # -- Series and Season: Tab, the episode rows, the menu key, the episode's page, Back
    Step-Series $false
    $kbB = KB-Focus; [void](KM-Press 'Tab'); Start-Sleep -Milliseconds 500
    KM-Row 'browsing' 'Series' 'Tab (moves the focus)' (-not (KB-Same (KB-Focus) $kbB)) "$(KB-Desc $kbB) -> $(KB-Desc (KB-Focus))"
    Step-Season $false
    $kbRow = Find-EpisodeRow
    $kbStart = KB-Start 'Season' $kbRow
    if ($kbStart) {
        # the episodes are a list down the page (Classic, Drive-in's SHOWTIMES board, Editorial) or a shelf across it
        # (Cinematic's stills): the arrow along the list moves to the next episode and its opposite back
        [void](KM-Press 'Down'); Start-Sleep -Milliseconds 600
        $kbAlong = KB-Focus
        $kbKeys = if ($kbAlong -and $kbAlong.type -eq 'ListItem' -and -not (KB-SameItem $kbAlong $kbStart)) { @('Down', 'Up') } else { @('Right', 'Left') }
        $kbStart = KB-Start 'Season' (Find-EpisodeRow)
        $kbNext = KB-Moves 'Season' "$($kbKeys[0])" $null
        if ($kbNext) { [void](KB-Moves 'Season' "$($kbKeys[1])" $kbStart) }
        # the menu key opens the episode's menu, "Episode details" first (a menu can be a window of its own: its item has the
        # keyboard)
        [void](KB-Start 'Season' (Find-EpisodeRow))
        [void](KM-Press 'Apps')
        $kbMenu = Wait-Until { $f = KB-Focus; if ($f -and $f.type -eq 'MenuItem') { $f } } -Seconds 4 -PollMs 150
        KM-Row 'browsing' 'Season' 'the menu key (opens the episode''s menu)' ([bool]$kbMenu) "to $script:kmLastTarget; $(KB-Desc $kbMenu)"
        if ($kbMenu -and $kbMenu.name -ne 'Episode details') {
            foreach ($i in 1..6) { [void](KM-Press 'Down'); Start-Sleep -Milliseconds 250; if ((KB-Focus).name -eq 'Episode details') { break } }
        }
        if ($kbMenu) {
            [void](KM-Press 'Enter')
            $kbEp = Wait-ItemPage $fixture.episode.name 12
            KM-Row 'browsing' 'Episode' 'Enter on Episode details (opens it)' ([bool]$kbEp) "to $script:kmLastTarget"
            if ($kbEp) {
                Start-Sleep -Seconds 2
                $kbB = KB-Focus; [void](KM-Press 'Tab'); Start-Sleep -Milliseconds 500
                KM-Row 'browsing' 'Episode' 'Tab (moves the focus)' (-not (KB-Same (KB-Focus) $kbB)) "$(KB-Desc $kbB) -> $(KB-Desc (KB-Focus))"
                KB-Back 'Episode' { Find-EpisodeRow }
            }
        }
    }
    # -- Search: / and Ctrl+F put the keyboard in the box; typing and Enter show the results; Down goes into them
    foreach ($kbChord in @('/', 'Ctrl+F')) {
        [void](KM-Press $kbChord)
        $kbBox = Wait-Until { $f = KB-Focus; if ($f -and $f.type -eq 'Edit') { $f } } -Seconds 4 -PollMs 150
        KM-Row 'browsing' 'Search' "$kbChord (the search box)" ([bool]$kbBox) "$(KB-Desc (KB-Focus))"
        if ($kbBox -and $kbChord -eq '/') {
            # Nixie 1.4.0: R in the search box is a letter (typed; nothing refreshed), and Backspace takes it out again
            $kmAt = Get-Epoch
            [void](KM-Press 'R'); Start-Sleep -Milliseconds 1200
            $kmStill = KB-Focus
            KM-Row 'browsing' 'Search' 'R in the search box (a letter)' ($kmStill -and $kmStill.type -eq 'Edit' -and (KM-Loads $kmAt) -eq 0) "$(KB-Desc $kmStill); $(KM-Loads $kmAt) refresh(es)"
            [void](KM-Press 'Backspace'); Start-Sleep -Milliseconds 400
        }
        if ($kbBox -and $kbChord -eq 'Ctrl+F') {
            $kbTarget = [KeysMatrix]::FocusWindow($script:hwnd)
            foreach ($ch in 'Signal'.ToCharArray()) { [void][OffscreenUi]::PostMessage($kbTarget, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 80 }
            Start-Sleep -Milliseconds 900
            [void](KM-Press 'Enter')
            $kbResults = Wait-Until { if ((KB-Focus).type -ne 'Edit' -or (Find-Ui -Name 'Signal' -Type 'Text' -Contains)) { $true } } -Seconds 8 -PollMs 200
            Start-Sleep -Seconds 2
            $kbCard = KB-FirstCard
            KM-Row 'browsing' 'Search results' 'Enter (the results page)' ([bool]$kbCard) "$(KB-Desc (KB-Focus))"
            if ($kbCard) {
                $kbS = KB-Start 'Search results' $kbCard
                if ($kbS) { KB-ArrowsAndEnds 'Search results' $kbS @('Right/Left') }
            }
        }
        elseif ($kbBox) { [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 400; [void](KM-Press 'Tab'); Start-Sleep -Milliseconds 300 }
    }
    # -- Diary: Tab into it, the arrows, Back
    try {
        Open-Nav 'Diary'; Start-Sleep -Seconds 3
        $kbB = KB-Focus; [void](KM-Press 'Tab'); Start-Sleep -Milliseconds 500
        KM-Row 'browsing' 'Diary' 'Tab (moves the focus)' (-not (KB-Same (KB-Focus) $kbB)) "$(KB-Desc $kbB) -> $(KB-Desc (KB-Focus))"
    }
    catch { KM-Row 'browsing' 'Diary' 'open' $false $_.Exception.Message }

    # then the player: the season's first episode
    Step-Season $false
    Step-Play
    Start-Sleep -Seconds 2
}

KM-ConnectPipe
$kmFirst = KM-State
Say ("keys-matrix ${kmTheme}: start {0}, focus {1}, keyboard window {2}" -f ($kmFirst | ConvertTo-Json -Compress), (KM-FocusName), [KeysMatrix]::ClassOf([KeysMatrix]::FocusWindow($script:hwnd)))

# ==== 9. the overlay after each transition (Nixie 1.4.0) ===========================================================================
# The user's report on 1.3.1: "we've lost the player controls overlay" (it came back once the player was opened again). After
# every transition the bars must show on a pointer move and on a key, drawn (not only present to UI Automation: a PrintWindow
# of the window, Play/Pause's own fill where UI Automation puts it), and hide again by themselves while playing. Run alone with
# NIXIE_MATRIX_ONLY=overlay (run-offscreen.ps1 -Script season,play -Then offscreen\then-keys-matrix.ps1).
if (-not ('KmOverlay' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class KmOverlay {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);

  // the mean colour (r, g, b) of a square of side 2*half+1 at a point on the screen, from a PrintWindow of the window (what is
  // drawn, composition included); null when the point is outside it
  public static int[] Sample(IntPtr main, int x, int y, int half, string save) {
    RECT r; if (!GetWindowRect(main, out r)) return null;
    int w = r.R - r.L, h = r.B - r.T;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
      if (!string.IsNullOrEmpty(save)) bmp.Save(save, ImageFormat.Png);
      int cx = x - r.L, cy = y - r.T; long sr = 0, sg = 0, sb = 0, n = 0;
      for (int dy = -half; dy <= half; dy++) for (int dx = -half; dx <= half; dx++) {
        int px = cx + dx, py = cy + dy; if (px < 0 || py < 0 || px >= w || py >= h) continue;
        var c = bmp.GetPixel(px, py); sr += c.R; sg += c.G; sb += c.B; n++;
      }
      return n == 0 ? null : new[] { (int)(sr / n), (int)(sg / n), (int)(sb / n) };
    }
  }
}
"@
}

$script:kmOvRef = $null      # Play/Pause's fill as drawn the first time (the theme's accent)
$script:kmOvRect = $null     # where Play/Pause was last seen
$script:kmOvShot = 0
$kmOvDir = Join-Path $kmDir 'overlay'
New-Item -ItemType Directory -Force $kmOvDir | Out-Null

# the point sampled: inside Play/Pause's circle, below its glyph (its fill alone)
function KM-OvPoint($Rect) { @([int]($Rect.X + $Rect.Width / 2), [int]($Rect.Y + $Rect.Height * 0.84)) }
function KM-OvNear([int[]]$A, [int[]]$B) { $A -and $B -and ([math]::Abs($A[0] - $B[0]) + [math]::Abs($A[1] - $B[1]) + [math]::Abs($A[2] - $B[2])) -le 60 }
# the overlay now: Ui (Play/Pause in UI Automation), Drawn (its fill drawn where it was last seen), the colour there
function KM-Overlay([string]$Save) {
    $pp = Find-Ui -Id 'PlayPauseButton'
    if ($pp) { $script:kmOvRect = [OffscreenUi]::RectOf($pp) }
    if (-not $script:kmOvRect) { return [pscustomobject]@{ Ui = [bool]$pp; Drawn = $false; Colour = $null } }
    $p = KM-OvPoint $script:kmOvRect
    $file = if ($Save) { Join-Path $kmOvDir ("{0:D3}-{1}.png" -f (++$script:kmOvShot), ($Save -replace '[^A-Za-z0-9]+', '-')) } else { $null }
    $c = [KmOverlay]::Sample($script:hwnd, $p[0], $p[1], 3, $file)
    if ($pp -and -not $script:kmOvRef -and $c -and $c[0] -gt 150) { $script:kmOvRef = $c; Say "keys-matrix ${kmTheme}: Play/Pause's fill as drawn: $($c -join ',')" }
    [pscustomobject]@{ Ui = [bool]$pp; Drawn = [bool](KM-OvNear $c $script:kmOvRef); Colour = $(if ($c) { $c -join ',' } else { 'none' }) }
}
# the harness's pointer move over the video (HarnessPointer's Move: the player's own PointerMoved), which also logs the
# overlay as XAML has it ("[harness] overlay ...")
function KM-OvMove { $p = KM-VideoPoint; [void][KeysMatrix]::Pointer($script:hwnd, 4, $p[0], $p[1], 0) }
function KM-OvWaitHidden([double]$Seconds = 8) { [bool](Wait-Until { if (-not (KM-Bars)) { $true } } -Seconds $Seconds -PollMs 200) }
function KM-OvWaitDrawn([double]$Seconds = 2.5) {
    $s = $null
    $deadline = (Get-Date).AddSeconds($Seconds)
    do { Start-Sleep -Milliseconds 250; $s = KM-Overlay } while (-not ($s.Ui -and $s.Drawn) -and (Get-Date) -lt $deadline)
    $s
}

# after a transition: a pointer move shows the bars, drawn; playing, they hide by themselves (and are no longer drawn); a key
# shows them again, drawn; and they hide again
function KM-OvCase([string]$After) {
    # the window minimized by anything but the activation case is a finding of its own (then put back, so the case still runs)
    if ([KmOverlay]::IsIconic($script:hwnd)) {
        KM-Row 'overlay' $After 'the window is not minimized' $false 'it was: restored'
        [void][KmOverlay]::ShowWindow($script:hwnd, 9); Start-Sleep -Milliseconds 1200
    }
    KM-OvMove
    $s = KM-OvWaitDrawn
    if (-not ($s.Ui -and $s.Drawn)) { $s = KM-Overlay "$After-move" }
    KM-Row 'overlay' $After 'a pointer move shows the bars, drawn' ($s.Ui -and $s.Drawn) "in UI Automation $($s.Ui), drawn $($s.Drawn) ($($s.Colour); the accent $($script:kmOvRef -join ',')), full $((Get-WindowState $script:hwnd).FullScreen), paused $(KM-Bool 'pause')"
    if (KM-Bool 'pause') { return }
    $hidden = KM-OvWaitHidden
    # the fade out takes 0.3 s after UI Automation has let them go
    Start-Sleep -Milliseconds 700
    $h = KM-Overlay
    KM-Row 'overlay' $After 'playing, they hide by themselves' ($hidden -and -not $h.Ui -and -not $h.Drawn) "in UI Automation $($h.Ui), drawn $($h.Drawn) ($($h.Colour))"
    # a key that changes nothing for good: a volume step down and back up
    [void](KM-Press 'Down'); Start-Sleep -Milliseconds 200; [void](KM-Press 'Up')
    $k = KM-OvWaitDrawn
    if (-not ($k.Ui -and $k.Drawn)) { $k = KM-Overlay "$After-key" }
    KM-Row 'overlay' $After 'a key shows the bars, drawn' ($k.Ui -and $k.Drawn) "in UI Automation $($k.Ui), drawn $($k.Drawn) ($($k.Colour))"
    $hidden = KM-OvWaitHidden
    KM-Row 'overlay' $After 'and they hide again' $hidden ''
}

# the app loses the activation and gets it back: its window minimized, then restored and brought to the front (a window of
# the harness's own could not take the foreground on the private desktop)
function KM-OvDeactivate([double]$Seconds = 2) {
    [void][KmOverlay]::ShowWindow($script:hwnd, 6)
    Start-Sleep -Milliseconds 300
    $away = [KmOverlay]::GetForegroundWindow() -ne $script:hwnd
    Start-Sleep -Milliseconds ([int]($Seconds * 1000))
    [void][KmOverlay]::ShowWindow($script:hwnd, 9)
    [void][KmOverlay]::SetForegroundWindow($script:hwnd)
    # the private desktop does not always hand the foreground back to it: restored (not minimized) is what counts here
    $front = [bool](Wait-Until { if ([KmOverlay]::GetForegroundWindow() -eq $script:hwnd) { $true } } -Seconds 4 -PollMs 200)
    $back = -not [KmOverlay]::IsIconic($script:hwnd)
    Start-Sleep -Milliseconds 800
    Say "keys-matrix ${kmTheme}: activation away $away, restored $back, in front again $front"
    @($away, $back, $front)
}

# out of the player (when in it) and in again from the season page's Play: by the keyboard (the focus put on Play, Enter) or by
# a click on it (the hook); then nothing until mpv plays. Leaves $script:kmOvFocusAtOpen (what has the keyboard then).
function KM-OvOpen([string]$How) {
    if ((Find-Ui -Id 'PlayPauseButton') -or (Find-Ui -Id 'SeekBar') -or $script:page -eq 'player') {
        KM-SetWindowed
        [void](KM-Press 'BackKey')
        [void](Wait-Until { if (-not (Find-Ui -Id 'PlayPauseButton') -and -not (Find-Ui -Id 'SeekBar')) { $true } } -Seconds 10 -PollMs 200)
        Start-Sleep -Seconds 2
    }
    [KeysMatrix]::Close()
    $script:page = 'season'
    $play = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Cached.Name -match '^(Play|Resume) S\d' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1 } -Seconds 12 -PollMs 300
    if (-not $play) { KM-Row 'overlay' "opened by $How" 'the season page''s Play' $false 'not found'; return $false }
    if ($How -eq 'key') { [void][OffscreenUi]::SetFocus($play); Start-Sleep -Milliseconds 400; [void](KM-Press 'Enter') }
    else { [void](KM-Click -At (KM-Centre $play)) }
    $shown = Wait-Until { Find-Ui -Id 'PlayPauseButton' } -Seconds 40 -PollMs 150
    $script:page = 'player'
    KM-ConnectPipe
    $playing = Wait-Until { $t = KM-Num 'time-pos'; if ($null -ne $t -and $t -gt 0.5 -and -not (KM-Bool 'pause')) { $true } } -Seconds 30 -PollMs 150
    $script:kmOvFocusAtOpen = "$(KM-FocusName) (window $([KeysMatrix]::ClassOf([KeysMatrix]::FocusWindow($script:hwnd))))"
    KM-Row 'overlay' "opened by $How" 'the player plays' ([bool]$shown -and [bool]$playing) "focus $script:kmOvFocusAtOpen"
    [bool]$playing
}

# Nixie 1.4.0, the user's repro of the lost overlay: "i opened the player and then hit the spacebar and it like locked me in
# keyboard only mode", "if u spacebar before clicking into it": open the player (by the keyboard, and by a click on Play), press
# Space before any click on the player (at once, and once the bars have hidden), then move the mouse: the bars must show,
# drawn; then Space, mouse, key, mouse, and the reverse
function KM-OvSpaceFirst {
    foreach ($how in 'key', 'click') {
        foreach ($when in 'at once', 'once the bars hid') {
            $case = "opened by $how, Space $when"
            if (-not (KM-OvOpen $how)) { continue }
            if ($when -ne 'at once') { [void](KM-OvWaitHidden 10) }
            $p0 = KM-Bool 'pause'; $f0 = "$(KM-FocusName) (window $([KeysMatrix]::ClassOf([KeysMatrix]::FocusWindow($script:hwnd))))"
            $sent = KM-Press 'Space'
            [void](Wait-Until { if ((KM-Bool 'pause') -ne $p0) { $true } } -Seconds 3 -PollMs 100)
            $p1 = KM-Bool 'pause'
            KM-Row 'overlay' $case 'Space (the first input) paused' ($sent -and $p1 -ne $p0) "sent $sent to $script:kmLastTarget; focus before $f0, after $(KM-FocusName); paused $p0 -> $p1"
            KM-OvMove
            $s = KM-OvWaitDrawn
            if (-not ($s.Ui -and $s.Drawn)) { $s = KM-Overlay "$case-then-mouse" }
            KM-Row 'overlay' $case 'then the mouse shows the bars, drawn' ($s.Ui -and $s.Drawn) "in UI Automation $($s.Ui), drawn $($s.Drawn) ($($s.Colour)), focus $(KM-FocusName)"
            # Space again (playing): the mouse, the bars hiding, a key, hiding again
            $p0 = KM-Bool 'pause'; [void](KM-Press 'Space'); [void](Wait-Until { if ((KM-Bool 'pause') -ne $p0) { $true } } -Seconds 3 -PollMs 100)
            KM-OvCase "$case, then Space again"
            # the reverse: the mouse, then Space, then the mouse
            KM-OvMove; Start-Sleep -Milliseconds 500
            $p0 = KM-Bool 'pause'; [void](KM-Press 'Space'); [void](Wait-Until { if ((KM-Bool 'pause') -ne $p0) { $true } } -Seconds 3 -PollMs 100)
            KM-OvMove
            $s = KM-OvWaitDrawn
            KM-Row 'overlay' $case 'the mouse, Space, the mouse: the bars, drawn' ($s.Ui -and $s.Drawn) "in UI Automation $($s.Ui), drawn $($s.Drawn) ($($s.Colour)), paused $(KM-Bool 'pause')"
            if (KM-Bool 'pause') { [void](KM-Press 'Space'); Start-Sleep -Milliseconds 600 }
            KM-OvCase "$case, the mouse and Space in turn"
        }
    }
}

# Nixie 1.4.0 (the bug hunt's m1): F1's shortcut sheet over the player is modal: every player key under it changes nothing,
# and Esc closes the sheet only (then the player's Esc as before)
function KM-SheetSection {
    foreach ($kmFull in @($false, $true)) {
        $case = if ($kmFull) { 'the shortcut sheet open, full screen' } else { 'the shortcut sheet open, windowed' }
        KM-Ready $true
        if ($kmFull) { [void](KM-Press 'F'); [void](Wait-WindowState $script:hwnd $true 6); Start-Sleep -Milliseconds 800 }
        [void](KM-Press 'F1')
        $open = [bool](Wait-Until { Find-Ui -Name 'Keyboard shortcuts' } -Seconds 4 -PollMs 150)
        KM-Row 'dialog over the player' $case 'F1 opens it' $open "focus $(KM-FocusName)"
        if (-not $open) { KM-SetWindowed; continue }
        foreach ($kmChord in @('F', 'M', '0', '5', 'K', 'J', 'L', 'Left', 'Up', 'S', 'Shift+N')) {
            $t = [KeysMatrix]::Get('media-title')
            KM-KeyIsNotThePlayers 'dialog over the player' $case $kmChord { ([KeysMatrix]::Get('media-title') -eq $t) -and [bool](Find-Ui -Name 'Keyboard shortcuts') }
        }
        $kmB = KM-State -NoUi
        [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 700
        $kmA = KM-State -NoUi
        KM-Row 'dialog over the player' $case 'Esc closes the sheet only' ((-not (Find-Ui -Name 'Keyboard shortcuts')) -and $kmA.Full -eq $kmB.Full -and $kmA.Paused -eq $kmB.Paused) "full $($kmB.Full)->$($kmA.Full), paused $($kmB.Paused)->$($kmA.Paused)"
        [void](KM-Key 'dialog over the player' "$case, then closed" 'M')
        [void](KM-Key 'dialog over the player' "$case, then closed" 'M')
        KM-SetWindowed
    }
}

function KM-OverlaySection {
    # first: the user's repro (the player opened again for each); NIXIE_OVERLAY_PART=space runs it alone
    KM-OvSpaceFirst
    if ($env:NIXIE_OVERLAY_PART -eq 'space') { return }
    KM-Ready $false
    KM-OvCase 'the player playing'
    # pause and play by click, then by key (from the bars hidden too)
    KM-ShowBars; [void](KM-Click 'PlayPauseButton'); KM-OvCase 'a click paused'
    KM-ShowBars; [void](KM-Click 'PlayPauseButton'); KM-OvCase 'a click played'
    [void](KM-OvWaitHidden); [void](KM-Press 'K'); Start-Sleep -Milliseconds 600; KM-OvCase 'K paused (bars hidden)'
    [void](KM-Press 'Space'); Start-Sleep -Milliseconds 600; KM-OvCase 'Space played'
    [void](KM-OvWaitHidden); [void](KM-Click -At (KM-VideoPoint)); Start-Sleep -Milliseconds 600; KM-OvCase 'a click on the video paused (bars hidden)'
    [void](KM-Click -At (KM-VideoPoint)); Start-Sleep -Milliseconds 600; KM-OvCase 'a click on the video played'
    # double click into full screen and out (from the bars hidden, and shown)
    [void](KM-OvWaitHidden); [void](KM-Click -At (KM-VideoPoint) -Double); [void](Wait-WindowState $script:hwnd $true 5); Start-Sleep -Milliseconds 800
    KM-OvCase 'a double click into full screen (bars hidden)'
    KM-ShowBars; [void](KM-Click -At (KM-VideoPoint) -Double); [void](Wait-WindowState $script:hwnd $false 5); Start-Sleep -Milliseconds 800
    KM-OvCase 'a double click out of full screen'
    [void](KM-Press 'F'); [void](Wait-WindowState $script:hwnd $true 5); Start-Sleep -Milliseconds 800; KM-OvCase 'F into full screen'
    [void](KM-Press 'Esc'); [void](Wait-WindowState $script:hwnd $false 5); Start-Sleep -Milliseconds 800; KM-OvCase 'Esc out of full screen'
    # the menus: opened by a click, closed by Esc
    foreach ($id in 'VolumeButton', 'SpeedButton', 'TracksButton', 'ChaptersButton') {
        KM-SetPaused $false; KM-ShowBars
        if (KM-Click $id) { Start-Sleep -Milliseconds 500; [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 600; KM-OvCase "$id's menu opened and closed" }
        else { KM-Row 'overlay' "$id's menu" 'opened' $false 'the button is not shown' }
    }
    if ($kmTheme -eq 'Cinematic') {
        foreach ($tab in 'Info', 'Chapters') {
            KM-ShowBars
            if (KM-Click -Name $tab) { Start-Sleep -Milliseconds 700; [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 600; KM-OvCase "Cinematic's $tab panel opened and closed" }
            else { KM-Row 'overlay' "Cinematic's $tab panel" 'opened' $false 'the tab is not shown' }
        }
    }
    [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 600; KM-OvCase 'Esc in a window'
    # the app loses the activation and gets it back: the bars shown, then hidden
    KM-ShowBars; $a = KM-OvDeactivate; KM-Row 'overlay' 'activation' 'minimized, then restored' ($a[0] -and $a[1]) "away $($a[0]), restored $($a[1]), in front again $($a[2])"
    KM-OvCase 'the activation lost and back (bars shown)'
    [void](KM-OvWaitHidden); $a = KM-OvDeactivate 5; KM-OvCase 'the activation lost and back (bars hidden)'
    # the seek bar dragged; the wheel
    KM-ShowBars; [void](KM-Drag 'SeekBar' 0.2 0.4); KM-OvCase 'a drag of the seek bar'
    [void](KM-OvWaitHidden); KM-Wheel -120; KM-Wheel 120; KM-OvCase 'the wheel (bars hidden)'
    # the next and previous episode: keys and the bar's buttons
    [void](KM-Key 'overlay' 'next episode' 'Shift+N' 12); Start-Sleep -Seconds 3; KM-OvCase 'Shift+N (the next episode)'
    [void](KM-Key 'overlay' 'previous episode' 'Shift+P' 12); Start-Sleep -Seconds 3; KM-OvCase 'Shift+P (the previous episode)'
    KM-ShowBars; $t = [KeysMatrix]::Get('media-title'); [void](KM-Click 'NextButton')
    [void](Wait-Until { if ([KeysMatrix]::Get('media-title') -ne $t) { $true } } -Seconds 12 -PollMs 300); Start-Sleep -Seconds 3; KM-OvCase 'the Next button'
    KM-ShowBars; $t = [KeysMatrix]::Get('media-title'); [void](KM-Click 'PreviousButton')
    [void](Wait-Until { if ([KeysMatrix]::Get('media-title') -ne $t) { $true } } -Seconds 12 -PollMs 300); Start-Sleep -Seconds 3; KM-OvCase 'the Previous button'
    # the countdown's auto-play into the next episode (the last seconds, the bars hidden)
    KM-SetPaused $false; [void](KM-Press '9'); Start-Sleep -Milliseconds 600
    $t = [KeysMatrix]::Get('media-title')
    for ($i = 0; $i -lt 8 -and (KM-Num 'time-pos') -lt ((KM-Num 'duration') - 25); $i++) { [void](KM-Press 'Shift+Right'); Start-Sleep -Milliseconds 400 }
    $next = Wait-Until { if ([KeysMatrix]::Get('media-title') -ne $t) { $true } } -Seconds 60 -PollMs 500
    KM-Row 'overlay' 'the countdown' 'played the next episode by itself' ([bool]$next) "from '$t' to '$([KeysMatrix]::Get('media-title'))'"
    Start-Sleep -Seconds 3; KM-OvCase 'the countdown played the next episode'
    # leaving the player and coming back
    [void](KM-Press 'BackKey')
    [void](Wait-Until { if (-not (Find-Ui -Id 'PlayPauseButton') -and -not (Find-Ui -Id 'SeekBar')) { $true } } -Seconds 10 -PollMs 200)
    $script:page = 'season'
    $row = Wait-Until { Find-EpisodeRow } -Seconds 12 -PollMs 300
    if ($row) {
        [void](Invoke-Element $row)
        $shown = Wait-Until { Find-Ui -Id 'PlayPauseButton' } -Seconds 40 -PollMs 200
        Start-Sleep -Seconds 3
        KM-ConnectPipe
        KM-Row 'overlay' 'the player again' 'opened' ([bool]$shown) ''
        KM-SetPaused $false
        KM-OvCase 'the player left and opened again'
    }
    else { KM-Row 'overlay' 'the player again' 'opened' $false 'no episode row' }
}

if ($env:NIXIE_MATRIX_ONLY -eq 'overlay') {
    if ($env:NIXIE_OVERLAY_PART -ne 'space') { KM-SheetSection }
    KM-OverlaySection
    [KeysMatrix]::Close()
    [IO.File]::WriteAllText((Join-Path $kmDir 'keys-matrix.json'), (ConvertTo-Json -InputObject @($kmRows) -Depth 4), [Text.UTF8Encoding]::new($false))
    return
}

# ==== 0. the next episode and back (Shift+N, Shift+P): on the second episode Previous is enabled too ==========================
KM-Ready $false
[void](KM-Key 'all keys' 'playing, bars shown' 'Shift+N' 12)
Start-Sleep -Seconds 3
[void](KM-Key 'all keys' 'playing, bars shown' 'Shift+P' 12)
Start-Sleep -Seconds 3
[void](KM-Key 'all keys' 'playing, bars shown' 'Shift+N' 12)
Start-Sleep -Seconds 4

# ==== 1. every documented key, playing with the bars shown, and paused ===========================================================
$kmAll = @('Space', 'Space', 'K', 'K', 'Left', 'Right', 'Ctrl+Left', 'Ctrl+Right', 'J', 'L', 'Shift+Right', 'Shift+Left', 'Up', 'Down', 'M', 'M',
    '[', 'Backspace', ']', 'Backspace', 'C', 'C', 'V', 'B', 'Z', 'X', 'F', 'F', 'F11', 'F11', 'Alt+Enter', 'Alt+Enter', '5', 'Numpad3', 'Home', '0')
foreach ($kmPaused in @($false, $true)) {
    $kmCase = if ($kmPaused) { 'paused, bars shown' } else { 'playing, bars shown' }
    KM-Ready $kmPaused
    foreach ($kmChord in $kmAll) {
        if ($kmChord -in 'Home', '0', '5', 'Numpad3') { }
        elseif ((KM-Num 'time-pos') -lt 40) { [void](KM-Press '2'); Start-Sleep -Milliseconds 400 }
        if ($kmPaused -and (KM-Bool 'pause') -ne $true -and $kmChord -notin 'Space', 'K') { KM-SetPaused $true }
        [void](KM-Key 'all keys' $kmCase $kmChord)
        if ($kmChord -in 'F', 'F11', 'Alt+Enter') { Start-Sleep -Milliseconds 700 }
    }
    KM-SetWindowed
    # S: the intro (10-40 s) is on offer from 15 s
    [void](KM-Press 'Home'); Start-Sleep -Milliseconds 400
    foreach ($kmI in 1..3) { [void](KM-Press 'Right'); Start-Sleep -Milliseconds 200 }
    Start-Sleep -Milliseconds 900
    [void](KM-Key 'all keys' $kmCase 'S' 4)
}

# ==== 2. playing, the bars hidden (each key shows them; wait for them to go again) ===============================================
KM-Ready $false
foreach ($kmChord in @('Space', 'Space', 'Left', 'Right', 'Up', 'Down', 'M', 'M', 'F', 'F')) {
    if ((KM-Bool 'pause')) { KM-SetPaused $false }
    $kmHidden = KM-WaitBarsHidden
    if (-not $kmHidden) { KM-Row 'all keys' 'playing, bars hidden' $kmChord $false 'the bars did not hide'; continue }
    [void](KM-Key 'all keys' 'playing, bars hidden' $kmChord)
    if ($kmChord -eq 'F') { Start-Sleep -Milliseconds 800 }
}
KM-SetWindowed

# ==== 3. full screen ============================================================================================================
KM-Ready $false
[void](KM-Press 'F'); [void](Wait-WindowState $script:hwnd $true 6); Start-Sleep -Milliseconds 800
foreach ($kmChord in @('Space', 'Space', 'Left', 'Right', 'Up', 'Down', 'M', 'M', 'J', 'L')) { [void](KM-Key 'full screen' 'playing' $kmChord) }
$kmB = KM-State -NoUi
[void](KM-Press 'Esc')
$kmA = Wait-WindowState $script:hwnd $false 6
KM-Row 'full screen' 'playing' 'Esc (leaves full screen)' (-not $kmA.FullScreen -and $kmB.Full) "full $($kmB.Full) -> $($kmA.FullScreen)"
Start-Sleep -Milliseconds 800
$kmB = KM-State
[void](KM-Press 'Esc'); Start-Sleep -Milliseconds 800
$kmA = KM-State
KM-Row 'windowed' 'playing' 'Esc (shows the controls, nothing else)' ($kmA.Bars -and -not $kmA.Full -and $kmA.Paused -eq $kmB.Paused) "bars $($kmA.Bars), full $($kmA.Full), paused $($kmB.Paused)->$($kmA.Paused)"

# ==== 6. the user's report: the mouse, then the keys at once (the bars still showing) ===================================================
# 6.1 click pause, then Space
KM-Ready $false
[void](KM-Click 'PlayPauseButton')
$kmP = KM-Bool 'pause'
KM-Row 'after the mouse' '1. click pause' 'the click paused' $kmP "focus $(KM-FocusName)"
[void](KM-Key 'after the mouse' '1. click pause, then' 'Space')
# 6.2 click play, then Left, Right, Up, Down, F, M at once, the bars still showing
KM-Ready $true
[void](KM-Click 'PlayPauseButton')
KM-Row 'after the mouse' '2. click play' 'the click played' (-not (KM-Bool 'pause')) "focus $(KM-FocusName)"
foreach ($kmChord in @('Left', 'Right', 'Up', 'Down', 'F', 'F', 'M', 'M')) {
    $kmBars = KM-Bars
    $kmOk = KM-Key 'after the mouse' '2. click play, then at once' $kmChord
    if ($kmChord -eq 'F') { Start-Sleep -Milliseconds 700 }
    if (-not $kmBars) { Say "keys-matrix: the bars had hidden before $kmChord (the check is still the key's)" }
}
KM-Row 'after the mouse' '2. click play, then at once' 'the bars were still showing' (KM-Bars) ''
KM-SetWindowed
# 6.3 drag the seek bar, then Space
KM-Ready $false
$kmB = KM-State -NoUi
[void](KM-Drag 'SeekBar' 0.2 0.45)
$kmA = KM-State -NoUi
KM-Row 'after the mouse' '3. drag the seek bar' 'the drag seeked' ([math]::Abs([double]$kmA.Pos - [double]$kmB.Pos) -gt 30) "pos $([math]::Round([double]$kmB.Pos)) -> $([math]::Round([double]$kmA.Pos)), focus $(KM-FocusName)"
[void](KM-Key 'after the mouse' '3. drag the seek bar, then' 'Space')
[void](KM-Key 'after the mouse' '3. drag the seek bar, then' 'Right')
[void](KM-Key 'after the mouse' '3. drag the seek bar, then' 'Space')
# 6.4 click the video area, then keys
KM-Ready $false
$kmB = KM-State -NoUi
[void](KM-Click -At (KM-VideoPoint))
KM-Row 'after the mouse' '4. click the video' 'the click paused' ((KM-Bool 'pause') -ne $kmB.Paused) "focus $(KM-FocusName)"
foreach ($kmChord in @('Space', 'Left', 'Right', 'Up', 'Down', 'M', 'M', 'F', 'F')) { [void](KM-Key 'after the mouse' '4. click the video, then' $kmChord); if ($kmChord -eq 'F') { Start-Sleep -Milliseconds 700 } }
KM-SetWindowed
# 6.5 the wheel, then keys
KM-Ready $false
$kmB = KM-State -NoUi
KM-Wheel -120
$kmA = KM-State -NoUi
KM-Row 'after the mouse' '5. the wheel' 'a notch down lowered the volume' ($kmA.Vol -lt $kmB.Vol -or ($kmB.Mute -and -not $kmA.Mute)) "vol $($kmB.Vol)/$($kmB.Mute) -> $($kmA.Vol)/$($kmA.Mute)"
foreach ($kmChord in @('Space', 'Space', 'Left', 'Right', 'Up', 'F', 'F')) { [void](KM-Key 'after the mouse' '5. the wheel, then' $kmChord); if ($kmChord -eq 'F') { Start-Sleep -Milliseconds 700 } }
KM-SetWindowed
# 6.6 click a control that opens a menu, close it by a click elsewhere... (a light dismiss cannot be clicked off-screen: Esc), then Space
KM-Ready $true
[void](KM-Click 'VolumeButton'); Start-Sleep -Milliseconds 300
[void](KM-Press 'Esc'); Start-Sleep -Milliseconds 500
[void](KM-Key 'after the mouse' '6. click Volume, Esc, then' 'Space')
[void](KM-Key 'after the mouse' '6. click Volume, Esc, then' 'Space')

# ==== 5. panels: the tracks panel (a flyout) and the volume flyout, then Cinematic's tabs panel ===========================================
# Nixie 1.4.0: in Cinematic the bar's Audio and subtitles button opens its player panel's part (one UI for the job), not the flyout
$kmTracksInside = if ($kmTheme -eq 'Cinematic') { 'TracksSubtitlesHeading' } else { 'SubtitleTracks' }
foreach ($kmPanel in @(@{ Button = 'TracksButton'; Inside = $kmTracksInside; Name = 'tracks panel' }, @{ Button = 'VolumeButton'; Inside = 'MuteButton'; Name = 'volume flyout' }, @{ Button = 'SpeedButton'; Inside = ''; Name = 'speed menu' }, @{ Button = 'ChaptersButton'; Inside = ''; Name = 'chapters menu (1.4.0)' })) {
    KM-Ready $true
    if (-not (KM-Click $kmPanel.Button)) { KM-Row 'panels' $kmPanel.Name 'open' $false 'its button is not shown'; continue }
    # (the speed menu is a window of its own, outside the main window's UI Automation tree: its item has the keyboard)
    $kmOpen = if ($kmPanel.Inside) { [bool](Wait-Ui -Id $kmPanel.Inside -Seconds 3) } else { [bool](Wait-Until { if ((KM-Focus).type -eq 'MenuItem') { $true } } -Seconds 3 -PollMs 150) }
    KM-Row 'panels' $kmPanel.Name 'a click opens it' $kmOpen "focus $(KM-FocusName)"
    if (-not $kmOpen) { continue }
    KM-KeyIsNotThePlayers 'panels' "$($kmPanel.Name) open" 'Down'
    $kmB = KM-State -NoUi
    [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 600
    $kmClosed = if ($kmPanel.Inside) { -not (Find-Ui -Id $kmPanel.Inside) } else { (KM-Focus).type -ne 'MenuItem' }
    KM-Row 'panels' $kmPanel.Name 'Esc closes it' $kmClosed "focus $(KM-FocusName)"
    # the user's report: after a menu closed, Space opened it again (1.3.0)
    foreach ($kmChord in @('Space', 'Space', 'Left', 'Up', 'Down', 'F', 'F')) { [void](KM-Key 'panels' "$($kmPanel.Name) closed" $kmChord); if ($kmChord -eq 'F') { Start-Sleep -Milliseconds 700 } }
    $kmReopened = if ($kmPanel.Inside) { [bool](Find-Ui -Id $kmPanel.Inside) } else { (KM-Focus).type -eq 'MenuItem' }
    KM-Row 'panels' $kmPanel.Name 'Space did not open it again' (-not $kmReopened) ''
    KM-SetWindowed
}

if ($kmTheme -eq 'Cinematic') {
    foreach ($kmTab in @('Info', 'Episodes', 'Chapters', 'Audio and subtitles')) {
        KM-Ready $true
        if (-not (KM-Click -Name $kmTab)) { KM-Row 'panels' "Cinematic tabs: $kmTab" 'open' $false 'the tab is not shown'; continue }
        Start-Sleep -Milliseconds 500
        # a click leaves the keyboard on the video: the player's keys work with the panel open, Esc closes it
        foreach ($kmChord in @('Space', 'Space', 'Left', 'Right', 'Up', 'Down')) { [void](KM-Key 'panels' "Cinematic tabs panel ($kmTab) open by a click" $kmChord) }
        [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 600
        $kmStill = KM-Click -Name $kmTab   # opens it again to see it was closed: a second click on an open tab closes it
        Start-Sleep -Milliseconds 300
        [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 400
        KM-Row 'panels' "Cinematic tabs: $kmTab" 'Esc closes the panel' $kmStill ''
    }
    # the keyboard on the tabs (Tab put it there): Left/Right move between them, Enter opens one, Esc closes, Space is the tab's
    KM-Ready $true
    $kmT = Find-Ui -Name 'Info' -Type 'Button'
    if (-not $kmT) { $kmT = Find-Ui -Name 'Info' }
    if ($kmT) {
        [void][OffscreenUi]::SetFocus($kmT); Start-Sleep -Milliseconds 300
        $kmF0 = KM-FocusName
        KM-KeyIsNotThePlayers 'panels' 'Cinematic tabs focused' 'Right' { (KM-FocusName) -ne $kmF0 }
        KM-KeyIsNotThePlayers 'panels' 'Cinematic tabs focused' 'Left' { (KM-FocusName) -eq $kmF0 }
        [void](KM-Key 'panels' 'Cinematic tabs focused' 'F'); Start-Sleep -Milliseconds 700; KM-SetWindowed
        [void](KM-Key 'panels' 'Cinematic tabs focused' 'M'); [void](KM-Key 'panels' 'Cinematic tabs focused' 'M')
    }
    else { KM-Row 'panels' 'Cinematic tabs focused' 'focus' $false 'no Info tab' }
}

# ==== 7. double click ========================================================================================================
function KM-DoubleCase([string]$Case, [int[]]$At, [string]$Id, [bool]$ShouldToggle) {
    $b = KM-State -NoUi
    [void](KM-Click -At $At -Id $Id -Double)
    $want = if ($ShouldToggle) { -not $b.Full } else { $b.Full }
    $a = Wait-WindowState $script:hwnd $want 4
    Start-Sleep -Milliseconds 600
    $s = KM-State -NoUi
    $fullOk = $s.Full -eq $want
    KM-Row 'double click' $Case $(if ($ShouldToggle) { 'toggles full screen' } else { 'leaves full screen alone' }) $fullOk "full $($b.Full) -> $($s.Full), focus $(KM-FocusName)"
    if ($ShouldToggle) {
        KM-Row 'double click' $Case 'the play state is as before it' ($s.Paused -eq $b.Paused) "paused $($b.Paused) -> $($s.Paused)"
    }
}
foreach ($kmPaused in @($false, $true)) {
    $kmWhat = if ($kmPaused) { 'paused' } else { 'playing' }
    KM-Ready $kmPaused
    KM-DoubleCase "the video, $kmWhat, windowed" (KM-VideoPoint) $null $true
    foreach ($kmChord in @('Space', 'Space', 'Left', 'Right')) { [void](KM-Key 'double click' "the video, $kmWhat, then (full screen)" $kmChord) }
    KM-ShowBars
    KM-DoubleCase "the video, $kmWhat, full screen" (KM-VideoPoint) $null $true
    foreach ($kmChord in @('Space', 'Space', 'Up', 'Down')) { [void](KM-Key 'double click' "the video, $kmWhat, then (windowed)" $kmChord) }
    KM-SetWindowed
}
# the scrim's fade over the video (the bottom bar's top edge): the video's
KM-Ready $false
$kmS = KM-ScrimPoint
if ($kmS) { KM-DoubleCase 'the bottom scrim, playing' $kmS $null $true; Start-Sleep -Milliseconds 500; KM-SetWindowed } else { KM-Row 'double click' 'the bottom scrim' 'found' $false 'the bars are not shown' }
# the controls: never full screen
foreach ($kmId in @('PlayPauseButton', 'SeekBar', 'VolumeButton', 'SpeedButton', 'TracksButton', 'SkipBackButton', 'SkipForwardButton')) {
    KM-Ready $true
    KM-DoubleCase "on $kmId" $null $kmId $false
    [void](KM-Press 'Esc'); Start-Sleep -Milliseconds 300   # a menu the double click opened
    KM-SetWindowed
    KM-SetPaused $true
    [void](KM-Key 'double click' "on $kmId, then" 'Space')
}

# ==== 4. paused, the keyboard on each control (Tab or a menu closing puts it there); last: on a 1.3.0 build Space on Back left
# the player, so everything after it would have run on the season page =============================================================
$kmControls = @('PlayPauseButton', 'SeekBar', 'VolumeButton', 'TracksButton', 'NextButton', 'PreviousButton', 'SpeedButton', 'FullScreenButton', 'SkipBackButton', 'SkipForwardButton', 'BackButton')
foreach ($kmId in $kmControls) {
    KM-Ready $true
    $kmE = Wait-Ui -Id $kmId -Seconds 4
    if (-not $kmE -or -not $kmE.Current.IsEnabled) { KM-Row 'paused, focus on a control' $kmId 'focus' $false "not found or disabled (bars shown: $(KM-Bars))"; continue }
    [void][OffscreenUi]::SetFocus($kmE); Start-Sleep -Milliseconds 300
    $kmOn = KM-FocusName
    foreach ($kmChord in @('Space', 'Space', 'Left', 'Right', 'Up', 'Down', 'M', 'M')) {
        if ((KM-Num 'time-pos') -lt 40) { [void](KM-Press '2'); Start-Sleep -Milliseconds 300 }
        [void](KM-Key 'paused, focus on a control' "$kmId (focus: $kmOn)" $kmChord)
    }
}

# Enter presses the focused button (the keyboard's way to use one): Full screen, then Play/Pause
KM-Ready $true
$kmE = Find-Ui -Id 'FullScreenButton'; [void][OffscreenUi]::SetFocus($kmE); Start-Sleep -Milliseconds 300
$kmB = KM-State -NoUi; [void](KM-Press 'Enter'); $kmA = Wait-WindowState $script:hwnd (-not $kmB.Full) 5
KM-Row 'paused, focus on a control' 'FullScreenButton' 'Enter (presses it)' ($kmA.FullScreen -ne $kmB.Full) "full $($kmB.Full) -> $($kmA.FullScreen)"
Start-Sleep -Milliseconds 800; KM-SetWindowed
KM-Ready $true
$kmE = Find-Ui -Id 'PlayPauseButton'; [void][OffscreenUi]::SetFocus($kmE); Start-Sleep -Milliseconds 300
[void](KM-Key 'paused, focus on a control' 'PlayPauseButton' 'Space')
KM-SetPaused $true

# ==== 9 (defined above): F1's sheet over the player is modal; the overlay after each transition, before the theme switch ======
KM-SheetSection
KM-OverlaySection

# ==== 8. the Back key leaves; a theme switch mid-session, then the player again =========================================================
KM-SetWindowed
$kmBackAt = Get-Epoch
[void](KM-Press 'BackKey')
$kmLeft = Wait-Until { if (-not (Find-Ui -Id 'PlayPauseButton') -and -not (Find-Ui -Id 'SeekBar')) { $true } } -Seconds 10 -PollMs 200
KM-Row 'leaving' 'windowed' 'the Back key leaves the player' ([bool]$kmLeft) ''
[KeysMatrix]::Close()
Start-Sleep -Seconds 2

$kmNext = @{ 'Classic' = 'Drive-in'; 'DriveIn' = 'Cinematic'; 'Cinematic' = 'Editorial'; 'Editorial' = 'Classic' }[$kmTheme]
if ($kmNext) {
    try {
        Open-Nav 'Settings'
        $kmTile = Wait-Until { Find-Ui -Name $kmNext -Type 'ListItem' -Contains } -Seconds 15 -PollMs 200
        if ($kmTile) { [void](Invoke-Element $kmTile); Start-Sleep -Seconds 3 }
        KM-Row 'theme switch' "$kmTheme -> $kmNext" 'switched in Settings' ([bool]$kmTile) ''
        # back (the Back key) to the season page the player was left for, now in the new theme, and play again
        [void](KM-Press 'BackKey')
        $kmRow = Wait-Until { Find-EpisodeRow } -Seconds 12 -PollMs 300
        if (-not $kmRow) { Step-Season $false; $kmRow = Find-EpisodeRow }
        $script:page = 'season'
        if ($kmRow) {
            [void](Invoke-Element $kmRow)
            $kmShown = Wait-Until { Find-Ui -Id 'PlayPauseButton' } -Seconds 40 -PollMs 200
            Start-Sleep -Seconds 3
            KM-ConnectPipe
            KM-Row 'theme switch' "$kmTheme -> $kmNext" 'the player again' ([bool]$kmShown) ''
            KM-Ready $false
            [void](KM-Click 'PlayPauseButton')
            foreach ($kmChord in @('Space', 'Space', 'Left', 'Right', 'Up', 'Down', 'M', 'M', 'F', 'F')) { [void](KM-Key 'theme switch' "in $kmNext after the switch, after a click" $kmChord); if ($kmChord -eq 'F') { Start-Sleep -Milliseconds 700 } }
            KM-SetWindowed
            [void](KM-Press 'BackKey'); Start-Sleep -Seconds 2
        }
        else { KM-Row 'theme switch' "$kmTheme -> $kmNext" 'the player again' $false 'no episode row' }
    }
    catch { KM-Row 'theme switch' "$kmTheme -> $kmNext" 'switched' $false $_.Exception.Message }
}
[KeysMatrix]::Close()

[IO.File]::WriteAllText((Join-Path $kmDir 'keys-matrix.json'), (ConvertTo-Json -InputObject @($kmRows) -Depth 4), [Text.UTF8Encoding]::new($false))
$kmFailed = @($kmRows | Where-Object { -not $_.ok })
Say ("keys-matrix ${kmTheme}: {0}/{1} rows passed{2}" -f ($kmRows.Count - $kmFailed.Count), $kmRows.Count, $(if ($kmFailed) { '; failed: ' + (($kmFailed | Select-Object -First 12 | ForEach-Object { "$($_.case) $($_.key)" }) -join '; ') } else { '' }))
