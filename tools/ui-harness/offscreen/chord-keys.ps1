# Nixie 1.4.0 (bug hunt m9): keys as a real keyboard sends them, for the -Then scripts that check what a key does where the
# user is (then-trailer.ps1, then-refresh.ps1). Dot-sourced by them (so by drive.ps1): every name here starts with CK, and
# nothing here assigns a variable at the top level.
#   - CK-Key posts a key down and up to the window that has the keyboard focus of FluentFin's UI thread (GetGUIThreadInfo's
#     hwndFocus), with Ctrl, Shift and Alt set in the UI thread's keyboard state (AttachThreadInput + SetKeyboardState), as
#     WinUI reads them there; -Repeat marks the down as a held key's auto-repeat (lParam bit 30), -NoUp leaves the key down.
#   - CK-Click clicks through the dev build's pointer hook (HarnessPointer: posted clicks never reach WinUI off-screen).
#   - CK-Log reads FluentFin's own log lines since a time; CK-Focus names the focused element (UI Automation).

if (-not ('ChordKeys' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class ChordKeys {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO g);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
  [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  public static uint ProcessOf(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  static uint ThreadOf(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }
  public static IntPtr FocusWindow(IntPtr main) {
    var g = new GUITHREADINFO(); g.cbSize = Marshal.SizeOf(g);
    return GetGUIThreadInfo(ThreadOf(main), ref g) ? g.hwndFocus : IntPtr.Zero;
  }
  public static bool Key(IntPtr target, int vk, bool ctrl, bool shift, bool alt, bool repeat, bool noUp) {
    bool mods = ctrl || shift || alt;
    uint ui = ThreadOf(target), me = GetCurrentThreadId();
    byte[] before = new byte[256];
    if (mods) {
      if (!AttachThreadInput(me, ui, true)) return false;
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
      Thread.Sleep(mods ? 500 : 30);
      if (!noUp) {
        uint lu = 1u | (scan << 16) | (alt ? (1u << 29) : 0u) | (1u << 30) | (1u << 31);
        PostMessage(target, up, (IntPtr)vk, (IntPtr)(long)lu);
      }
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
  public static void Up(IntPtr target, int vk) {
    uint scan = MapVirtualKey((uint)vk, 0);
    PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16) | (1u << 30) | (1u << 31)));
  }
  // the dev build's pointer hook (HarnessPointer): kind 1 click, 2 double click; x, y on the screen
  public static bool Pointer(IntPtr main, int kind, int x, int y) {
    uint msg = RegisterWindowMessage("NixieHarnessPointer");
    return PostMessage(main, msg, (IntPtr)(long)(uint)(kind & 0xFFFF), (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
}
"@
}

function CK-Target {
    $t = [ChordKeys]::FocusWindow($script:hwnd)
    if ($t -eq [IntPtr]::Zero -or [ChordKeys]::ProcessOf($t) -ne [uint32]$script:ui.Id) { return [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id) }
    $t
}

# a chord such as 'Ctrl+F', 'Shift+/', 'Alt+Home', 'R', 'F5', 'Esc'
function CK-Vk([string]$Name) {
    $named = @{ Esc = 0x1B; Tab = 0x09; Enter = 0x0D; Space = 0x20; Left = 0x25; Up = 0x26; Right = 0x27; Down = 0x28; Home = 0x24
        F1 = 0x70; F5 = 0x74; '/' = 0xBF; ',' = 0xBC }
    if ($named.ContainsKey($Name)) { $named[$Name] } else { [int][char]$Name.ToUpperInvariant() }
}
function CK-Key([string]$Chord, [switch]$Repeat, [switch]$NoUp) {
    $parts = $Chord -split '\+(?=.)'
    $name = $parts[-1]
    [void][ChordKeys]::Key((CK-Target), (CK-Vk $name), ($parts -contains 'Ctrl'), ($parts -contains 'Shift'), ($parts -contains 'Alt'), [bool]$Repeat, [bool]$NoUp)
}
function CK-Up([string]$Name) { [ChordKeys]::Up((CK-Target), (CK-Vk $Name)) }
function CK-Click($Element, [switch]$Double) {
    $r = [OffscreenUi]::RectOf($Element)
    [void][ChordKeys]::Pointer($script:hwnd, $(if ($Double) { 2 } else { 1 }), [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

# FluentFin's own log lines since $Since (epoch seconds) whose message matches $Pattern
function CK-Log([double]$Since, [string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '^(\S+ \S+ [+-]\d\d:\d\d) \[\w+\] (.*)$' } | ForEach-Object {
            $ckWhen = [DateTimeOffset]::MinValue
            $ckHead = ($_ -split ' \[\w+\] ', 2)
            if ([DateTimeOffset]::TryParse($ckHead[0], [ref]$ckWhen) -and $ckWhen.ToUnixTimeMilliseconds() / 1000.0 -ge $Since -and $ckHead[1] -match $Pattern) { $ckHead[1] }
        }
    })
}

# the focused element as "Type 'Name' id=AutomationId" (UI Automation), or 'nothing'
function CK-Focus {
    $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
    if ($f) { $j = $f | ConvertFrom-Json; "$($j.type) '$($j.name)' id=$($j.automationId)" } else { 'nothing' }
}
