# Nixie 1.6 skip: helpers shared by offscreen	hen-160-skip.ps1 and tools\qa-servers	hen-qa-skip.ps1 (dot-sourced after
# drive.ps1's helpers). A key with Shift is posted to FluentFin's focus window with Shift held in its UI thread's keyboard
# state, as then-keys-matrix.ps1 does (WinUI reads the modifiers there). Every name here starts with SK.

if (-not ('SkipKeys' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class SkipKeys {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO g);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
  [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  static uint ThreadOf(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }
  public static uint ProcessOf(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  // the window FluentFin's UI thread has the keyboard focus on (where a real key goes)
  public static IntPtr FocusWindow(IntPtr main) {
    var g = new GUITHREADINFO(); g.cbSize = Marshal.SizeOf(g);
    return GetGUIThreadInfo(ThreadOf(main), ref g) ? g.hwndFocus : IntPtr.Zero;
  }
  // a key posted to target, Shift held in the UI thread's keyboard state while it is read (as the keys matrix does)
  public static bool Key(IntPtr target, int vk, bool shift) {
    uint ui = ThreadOf(target), me = GetCurrentThreadId();
    byte[] before = new byte[256];
    bool attached = false;
    if (shift) {
      attached = AttachThreadInput(me, ui, true);
      if (!attached) return false;
      GetKeyboardState(before);
      var st = (byte[])before.Clone(); st[0x10] = 0x80; st[0xA0] = 0x80;
      SetKeyboardState(st);
      Thread.Sleep(60);
    }
    try {
      uint scan = MapVirtualKey((uint)vk, 0);
      PostMessage(target, 0x0100, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16)));
      Thread.Sleep(shift ? 500 : 40);
      PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16) | (1u << 30) | (1u << 31)));
      if (shift) Thread.Sleep(300);
    } finally {
      if (shift) {
        var clear = (byte[])before.Clone(); clear[0x10] = 0; clear[0xA0] = 0; clear[0xA1] = 0;
        SetKeyboardState(clear);
        AttachThreadInput(me, ui, false);
      }
    }
    return true;
  }
}
"@
}

# a key to FluentFin's focus window ('S', 'Shift+S', 'Esc', 'Tab', '1', 'Shift+Right', 'MediaNext', 'MediaPrev')
$skKeys = @{ 'Esc' = 0x1B; 'Tab' = 0x09; 'Right' = 0x27; 'MediaNext' = 0xB0; 'MediaPrev' = 0xB1; 'Home' = 0x24; 'K' = 0x4B }
function SK-Press([string]$Chord) {
    $parts = $Chord -split '\+'
    $name = $parts[-1]
    $vk = if ($skKeys.ContainsKey($name)) { $skKeys[$name] } else { [int][char]$name.ToUpperInvariant() }
    $target = [SkipKeys]::FocusWindow($script:hwnd)
    if ($target -eq [IntPtr]::Zero -or [SkipKeys]::ProcessOf($target) -ne [uint32]$script:ui.Id) { Say "skip: no FluentFin window has the keyboard for $Chord"; return $false }
    [SkipKeys]::Key($target, $vk, ($parts -contains 'Shift'))
}
function SK-Shot([string]$What, [int]$Width) { Shot "160-skip-$skTheme-$skLook-$What-$Width" }
function SK-Size([int]$Width) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $Width, $(if ($Width -ge 1600) { 1080 } else { 900 })); Start-Sleep -Milliseconds 1500 }
function SK-Episode { $e = Find-Ui -Id 'SubtitleText'; if ($e) { $e.Current.Name } else { '' } }
# (the card is a Border, which UI Automation does not list: its two lines and its buttons are)
function SK-Toast { Find-Ui -Id 'SkipSuggestionDetail' }
function SK-ToastName { $q = Find-Ui -Id 'SkipSuggestionQuestion'; $t = SK-Toast; if ($q -and $t) { "$($q.Current.Name) $($t.Current.Name)" } else { '' } }
# the two skips of an episode: 0:00 -> 1:00 (the 1 key), then 1:00 -> 1:30 (Shift+Right), judged 4 s after each (BURST_GAP)
function SK-TwoSkips {
    [void](SK-Press '1'); Start-Sleep -Seconds 5
    [void](SK-Press 'Shift+Right'); Start-Sleep -Seconds 1
}
function SK-Go([string]$Key) {
    $before = SK-Episode
    [void](SK-Press $Key)
    $changed = Wait-Until { $now = SK-Episode; if ($now -and $now -ne $before) { $now } } -Seconds 25 -PollMs 250
    Start-Sleep -Seconds 4   # the item opens and plays
    Say "skip: $Key -> '$changed' (was '$before')"
    [bool]$changed
}
function SK-FocusName { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $j = $f | ConvertFrom-Json; if ($j.automationId) { $j.automationId } else { $j.name } } else { '' } }

