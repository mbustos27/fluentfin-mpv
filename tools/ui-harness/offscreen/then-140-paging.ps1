# Nixie 1.4.0 integration (the big-library stream with the others): a library read a page at a time, in the current
# NIXIE_LAYOUT theme, while its pages are still arriving. Run with a big fake library and slow pages:
#   $env:FAKESERVER_BIG_LIBRARY='25000'; run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-140-paging.ps1
# (FAKESERVER_BIG_LIBRARY makes the "Archive" library; this script holds each /Items page back 1.2 s.) Checks:
#   - the first posters show at once and the head counts the titles in ("Loading... n of 25,000");
#   - the A to Z works on the titles in so far: a letter goes to a title starting with it;
#   - the arrival rule while pages arrive: R, then / and a letter: 6 s later the keyboard is still in the search box;
#   - R while pages arrive keeps the poster the keyboard was on;
#   - End and Home on a growing grid (Editorial's far-scroll follow): no layout cycle, the window answers;
#   - every page comes in the end ("25,000 movies"), and FluentFin logged no error.
# Shots <tag>-paging-*.png. Dot-sourced by drive.ps1: every name here starts with pg.

$pgTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
$pgBig = if ($env:FAKESERVER_BIG_LIBRARY) { [int]$env:FAKESERVER_BIG_LIBRARY } else { 0 }
$pgItems = '^/(Users/[^/]+/)?Items$'

if (-not ('PgW' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class PgW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO g);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
  static uint ThreadOf(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }
  public static IntPtr FocusWindow(IntPtr main) { var g = new GUITHREADINFO(); g.cbSize = Marshal.SizeOf(g); return GetGUIThreadInfo(ThreadOf(main), ref g) ? g.hwndFocus : IntPtr.Zero; }
  public static bool Key(IntPtr main, int vk) {
    var target = FocusWindow(main); if (target == IntPtr.Zero) return false;
    uint scan = MapVirtualKey((uint)vk, 0);
    PostMessage(target, 0x0100, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16))); Thread.Sleep(30);
    PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16) | (1u << 30) | (1u << 31)));
    return true;
  }
  // whether the window's thread answers within ms (a layout cycle holds it for seconds)
  public static bool Answers(IntPtr main, uint ms) { IntPtr r; return SendMessageTimeout(main, 0, IntPtr.Zero, IntPtr.Zero, 0x0002, ms, out r) != IntPtr.Zero; }
}
"@
}
function PgSettle([int]$Ms = 800) { Start-Sleep -Milliseconds $Ms }
function PgKey([int]$Vk) { [void][PgW]::Key($script:hwnd, $Vk); PgSettle 450 }
function PgFocus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function PgDesc($F) { if ($F) { "$($F.type) '$($F.name)'" } else { 'nothing' } }
# the head's count ("Loading... 1,200 of 25,000 films", then "25,000 films"), not the A to Z's readout ("A · 129 of 25,000")
function PgCount {
    $t = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) |
        ForEach-Object { try { $_.Current.Name } catch { '' } } | Where-Object { $_ -match '\b(films|movies)\b' -and $_ -notmatch [char]0x00B7 }) | Select-Object -First 1
    "$t"
}
function PgCards { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { try { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Top -gt 140 -and $_.Current.Name } catch { $false } }) }
function PgErrors([double]$Since) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '\[ERR\]|LayoutCycle' } | ForEach-Object {
            $w = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(($_ -split ' \[[A-Z]{3}\] ')[0], [ref]$w) -and $w.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { $_ }
        }
    })
}

if ($pgBig -lt 1000) { Check "paging ($pgTag): FAKESERVER_BIG_LIBRARY is set (at least 1,000)" $false "it is '$($env:FAKESERVER_BIG_LIBRARY)'"; return }
$pgStart = Get-Epoch
$pgTotal = '{0:N0}' -f $pgBig

# 1. open the Archive with slow pages: the first posters at once, the head counting
[void](Invoke-Server '/__harness/latency' 'POST' @{ rules = @(, @($pgItems, 1200)) })
$pgOpened = Get-Epoch
Open-Library 'Archive'
$pgFirst = Wait-Until { if (@(PgCards).Count -ge 3) { $true } } -Seconds 20 -PollMs 200
$pgFirstMs = [int]((Get-Epoch) - $pgOpened) * 1000
$pgLoadingText = Wait-Until { $c = PgCount; if ($c -match 'Loading') { $c } } -Seconds 6 -PollMs 200
Check "paging ($pgTag): the first posters show while the rest load" ([bool]$pgFirst) "after about $pgFirstMs ms"
Check "paging ($pgTag): the head counts the titles in (Loading... n of $pgTotal)" ([bool]$pgLoadingText -and $pgLoadingText -match [regex]::Escape($pgTotal)) "$pgLoadingText"
Shot "$pgTag-paging-loading"

# 2. the A to Z while pages arrive: the row is there and works on the titles in so far (sorted by name, the first pages
#    are all A); a letter none of them starts with rests until its titles come
$pgA = Find-Ui -Name 'Jump to A' -Type 'Button'
if ($pgA) {
    [void][OffscreenUi]::SetFocus($pgA); PgSettle 300
    PgKey 0x0D
    $pgOnA = Wait-Until { $f = PgFocus; if ($f -and $f.type -eq 'ListItem' -and $f.name -match '^\s*((The|An|A)\s+)?A') { $f } } -Seconds 8 -PollMs 200
    Check "paging ($pgTag): the A to Z while pages arrive (Enter on A: an A title)" ([bool]$pgOnA) "$(PgDesc (PgFocus)); $(PgCount)"
}
else { Check "paging ($pgTag): the A to Z while pages arrive (Enter on A: an A title)" $false "no 'Jump to A' ($(PgCount))" }
$pgRestingW = -not (Find-Ui -Name 'Jump to W' -Type 'Button')
Check "paging ($pgTag): while the A titles load, W (no title in yet) rests" $pgRestingW "$(PgCount)"

# 3. R while pages arrive keeps the poster the keyboard is on
$pgCard = PgFocus
if ($pgCard -and $pgCard.type -eq 'ListItem') {
    PgKey 0x52   # R
    $pgBack = Wait-Until { $f = PgFocus; if ($f -and $f.name -eq $pgCard.name) { $f } } -Seconds 12 -PollMs 250
    Check "paging ($pgTag): R while pages arrive keeps the poster" ([bool]$pgBack) "$(PgDesc $pgCard) -> $(PgDesc (PgFocus)); $(PgCount)"
}
else { Check "paging ($pgTag): R while pages arrive keeps the poster" $false "the keyboard was not on a poster: $(PgDesc $pgCard)" }

# 4. the arrival rule while pages arrive: R, then / and a letter; 6 s later the keyboard is still in the search box
PgSettle 1500
PgKey 0x52   # R
PgSettle 300
PgKey 0xBF   # /
$pgBox = Wait-Until { $f = PgFocus; if ($f -and $f.type -eq 'Edit') { $f } } -Seconds 4 -PollMs 150
if ($pgBox) {
    $target = [PgW]::FocusWindow($script:hwnd)
    [void][OffscreenUi]::PostMessage($target, 0x0102, [IntPtr][int][char]'k', [IntPtr]1)
    Start-Sleep -Seconds 6
    $pgStill = PgFocus
    Check "paging ($pgTag): R, then / and a letter: 6 s later the keyboard is still in the search box" ($pgStill -and $pgStill.type -eq 'Edit') "$(PgDesc $pgStill)"
    PgKey 0x1B; PgKey 0x1B   # Esc (the suggestions, then the box)
}
else { Check "paging ($pgTag): R, then / and a letter: 6 s later the keyboard is still in the search box" $false "/ did not reach the search box: $(PgDesc (PgFocus))" }

# 5. End and Home on the growing grid (Editorial's far-scroll follow): the window answers, no layout cycle
$pgCards = PgCards
if ($pgCards.Count) { [void][OffscreenUi]::SetFocus($pgCards[0]); PgSettle 400 }
$pgEndAt = Get-Epoch
PgKey 0x23   # End
$pgSlow = 0
foreach ($i in 1..20) { if (-not [PgW]::Answers($script:hwnd, 2000)) { $pgSlow++ }; Start-Sleep -Milliseconds 500 }
PgKey 0x24   # Home
foreach ($i in 1..10) { if (-not [PgW]::Answers($script:hwnd, 2000)) { $pgSlow++ }; Start-Sleep -Milliseconds 500 }
$pgCycles = @(PgErrors $pgEndAt | Where-Object { $_ -match 'LayoutCycle' })
Check "paging ($pgTag): End and Home on a growing grid: the window answers, no layout cycle" ($pgSlow -eq 0 -and $pgCycles.Count -eq 0) "$pgSlow slow answers (2 s); $($pgCycles.Count) layout cycle(s); $(PgDesc (PgFocus))"
Shot "$pgTag-paging-home"

# 6. every page in the end, at full speed again
[void](Invoke-Server '/__harness/latency' 'POST' @{ rules = @() })
$pgDone = Wait-Until { $c = PgCount; if ($c -and $c -notmatch 'Loading' -and $c -match [regex]::Escape($pgTotal)) { $c } } -Seconds 120 -PollMs 500
Check "paging ($pgTag): every page comes in ($pgTotal)" ([bool]$pgDone) "$(PgCount)"
$pgW = Wait-Until { Find-Ui -Name 'Jump to W' -Type 'Button' } -Seconds 10 -PollMs 300
Check "paging ($pgTag): with every page in, the A to Z has W" ([bool]$pgW) ''
$pgErr = @(PgErrors $pgStart)
Check "paging ($pgTag): FluentFin logged no error" ($pgErr.Count -eq 0) "$(($pgErr | Select-Object -First 2) -join ' | ')"
Shot "$pgTag-paging-done"
