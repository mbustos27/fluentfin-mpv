# 1.5 fixes: no Windows "ding" on Alt chords (WM_MENUCHAR). Off-screen, any NIXIE_LAYOUT:
#   run-offscreen.ps1 -Script home -NoA11y -Then offscreen\then-alt-keys.ps1     (Home: Alt+Enter, Alt+X, Alt+Home)
#   run-offscreen.ps1 -Script play -NoA11y -Then offscreen\then-alt-keys.ps1     (the player: Alt+Enter in and out of full screen)
# Never plays a sound: the Alt chords are posted only once every window of the app has been proven to answer WM_MENUCHAR
# itself (step 1), and an answered WM_MENUCHAR is what keeps Windows from beeping (its menu code beeps on MNC_IGNORE, the
# answer of a window that does not handle the message).
#   1. SendMessage(WM_MENUCHAR) to each visible top-level window of the app that has a system menu (the main window; the
#      player controls' window when mpv draws in a window of its own) answers MNC_CLOSE << 16 (0x10000).
#   2. The chords: each one's WM_MENUCHAR is counted in the app's log ("[keys] Alt+... WM_MENUCHAR answered"), the
#      confirmation that it reaches Windows' menu code (the ding's source); the window still answers afterwards.
. (Join-Path $PSScriptRoot 'chord-keys.ps1')

if (-not ('AltKeysProbe' -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class AltKeysProbe {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumDesktopWindows(IntPtr desk, EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(uint tid);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
  [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
  public static long Ask(IntPtr h, uint m, long w, long l) {
    IntPtr r;
    if (SendMessageTimeout(h, m, (IntPtr)w, (IntPtr)l, 0x0002 /* SMTO_ABORTIFHUNG */, 3000, out r) == IntPtr.Zero) return -1;
    return (long)r;
  }
  // the visible top-level windows of a process on this thread's desktop: "hwnd|sysmenu|class|title" (sysmenu: WS_SYSMENU, the
  // only windows whose Alt chords reach Windows' menu code: without it the menu code returns before asking or beeping)
  public static List<string> Windows(int pid) {
    var found = new List<string>();
    EnumDesktopWindows(GetThreadDesktop(GetCurrentThreadId()), (h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == (uint)pid && IsWindowVisible(h)) {
        var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
        var cn = new StringBuilder(256); GetClassName(h, cn, 256);
        bool sys = ((long)GetWindowLongPtr(h, -16) & 0x00080000L) != 0;
        found.Add(((long)h).ToString() + "|" + (sys ? "1" : "0") + "|" + cn.ToString() + "|" + sb.ToString());
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@
}

function AK-AskAll([string]$Where) {
    $akOk = $true
    foreach ($akW in [AltKeysProbe]::Windows($script:ui.Id)) {
        $akParts = $akW -split '\|', 4
        # wParam: the character in the low word, MF_SYSMENU (0x2000) in the high word, as Windows sends it for a menu-less window
        $akAnswer = [AltKeysProbe]::Ask([IntPtr][long]$akParts[0], 0x0120, (0x2000 -shl 16) -bor [int][char]'x', 0)
        $akSys = $akParts[1] -eq '1'
        Say ("alt-keys ({0}): '{1}' ({2}{3}) answers WM_MENUCHAR 0x{4:X}" -f $Where, $akParts[3], $akParts[2], $(if ($akSys) { ', system menu' } else { ', no system menu: never asked' }), $akAnswer)
        if ($akSys -and $akAnswer -ne 0x10000) { $akOk = $false }
    }
    Check "alt-keys ($Where): every window of the app with a system menu answers WM_MENUCHAR with MNC_CLOSE (0x10000)" $akOk
    $akOk
}

function AK-Chord([string]$Chord, [string]$Where, [double]$Since) {
    $akBefore = @(CK-Log $Since 'WM_MENUCHAR answered').Count
    CK-Key $Chord
    Start-Sleep -Milliseconds 900
    $akNew = @(CK-Log $Since 'WM_MENUCHAR answered').Count - $akBefore
    $akAlive = [AltKeysProbe]::Ask($script:hwnd, 0, 0, 0) -ge 0
    Say ("alt-keys ({0}): {1}: WM_MENUCHAR answered {2} time(s); window answers: {3}; focus {4}" -f $Where, $Chord, $akNew, $akAlive, (CK-Focus))
    Check "alt-keys ($Where): $Chord leaves the window answering" $akAlive
    [pscustomobject]@{ where = $Where; chord = $Chord; menuchar = $akNew; alive = $akAlive }
}

$akStart = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0
# the player: its Play/Pause button is on the page (FluentFin's controls draw in the main window since the composition video)
$akInPlayer = [bool](Find-Ui -Id 'PlayPauseButton' -Type 'Button')
$akWhere = if ($akInPlayer) { 'player' } else { 'home' }
if (AK-AskAll $akWhere) {
    $akRows = @()
    if ($akInPlayer) {
        $akRows += AK-Chord 'Alt+Enter' $akWhere $akStart
        Shot 'alt-keys-player-fullscreen'
        $akRows += AK-Chord 'Alt+Enter' $akWhere $akStart
        $null = AK-AskAll "$akWhere, after"
    }
    else {
        foreach ($akChord in @('Alt+Enter', 'Alt+X', 'Alt+Home')) { $akRows += AK-Chord $akChord $akWhere $akStart }
    }
    $timings.alt_keys = $akRows
    # the confirmation: Alt+Enter reaches Windows' menu code (the ding's source), and is answered
    Check "alt-keys ($akWhere): Alt+Enter reached WM_MENUCHAR and was answered (no sound)" (@($akRows | Where-Object { $_.chord -eq 'Alt+Enter' -and $_.menuchar -ge 1 }).Count -ge 1)
    Say ("alt-keys ({0}): the app's log: {1}" -f $akWhere, (@(CK-Log $akStart 'WM_MENUCHAR answered') -join ' | '))
}
Shot "alt-keys-$akWhere"
