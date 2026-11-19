# Nixie 1.4.0, the library stream: what the user sees in a library, in the current NIXIE_LAYOUT theme, keyboard first and
# mouse first, with a way out of every mode. Run with:
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'; run-offscreen.ps1 ... -Script home -Then offscreen\then-140-library.ps1
# Covers: the skeleton while the titles load; the first poster focused on arrival (and the keys working at once); type-to-jump
# and a repeated letter; the one-click Unwatched (UI Automation, a click, a double click) with the applied-filter row and
# its way out (the chip's cross, Clear all, the switch again); Sort by Date added (newest first, said on the button), the
# direction in one click and the row of directions; the A to Z (the letter's row at the top, the letter marked, "M · n of
# 320"); a filter that leaves nothing (the no-matches state and Clear filters); View (list, small posters) and back; R mid-
# filter; an item and Back (sort, filters and the card kept); leaving for Home and coming back (a fresh library). Shots
# <tag>-<name>-<width>.png; NIXIE_THEME / SG_SHOT_TAG name them. Dot-sourced by drive.ps1: every name here starts with lb.

$lbTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
$lbItems = '^/(Users/[^/]+/)?Items$'

if (-not ('LibHarness' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class LibHarness {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
  [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO g);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  static uint ThreadOf(IntPtr h) { uint p; return GetWindowThreadProcessId(h, out p); }
  public static uint ProcessOf(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static IntPtr FocusWindow(IntPtr main) { var g = new GUITHREADINFO(); g.cbSize = Marshal.SizeOf(g); return GetGUIThreadInfo(ThreadOf(main), ref g) ? g.hwndFocus : IntPtr.Zero; }
  // a key as a keyboard sends it (WM_KEYDOWN, translated to WM_CHAR by the app's loop), to the window with the keyboard focus
  public static bool Key(IntPtr main, int vk, bool repeat) {
    var target = FocusWindow(main);
    if (target == IntPtr.Zero) return false;
    uint scan = MapVirtualKey((uint)vk, 0);
    PostMessage(target, 0x0100, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16) | (repeat ? (1u << 30) : 0u)));
    Thread.Sleep(30);
    PostMessage(target, 0x0101, (IntPtr)vk, (IntPtr)(long)(1u | (scan << 16) | (1u << 30) | (1u << 31)));
    return true;
  }
  // the dev build's pointer hook (HarnessPointer): kind 1 click, 2 double click; x, y on the screen
  public static bool Pointer(IntPtr main, int kind, int x, int y) {
    uint msg = RegisterWindowMessage("NixieHarnessPointer");
    return PostMessage(main, msg, (IntPtr)(long)(uint)(kind & 0xFFFF), (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
}
"@
}

function Lb-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function Lb-Size([int]$W, [int]$H) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H); Lb-Settle 1500 }
function Lb-Shot([string]$Name) { Shot "$lbTag-$Name" }
function Lb-Both([string]$Name) { foreach ($s in @(@(1920, 1080), @(1100, 900))) { Lb-Size $s[0] $s[1]; Lb-Shot "$Name-$($s[0])" }; Lb-Size 1920 1080 }
function Lb-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function Lb-Desc($F) { if ($F) { "$($F.type) '$($F.name)'" } else { 'nothing' } }
function Lb-Key([int]$Vk, [switch]$Repeat) { [void][LibHarness]::Key($script:hwnd, $Vk, [bool]$Repeat); Lb-Settle 450 }
function Lb-Char([char]$C) { Lb-Key ([int][char]::ToUpperInvariant($C)) }
function Lb-Click($Element, [switch]$Double) {
    if (-not $Element) { return $false }
    $r = [OffscreenUi]::RectOf($Element)
    [void][LibHarness]::Pointer($script:hwnd, $(if ($Double) { 2 } else { 1 }), [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
    Lb-Settle $(if ($Double) { 900 } else { 700 })
    $true
}
function Lb-OnCard { $f = Lb-Focus; [bool]($f -and $f.type -eq 'ListItem') }
function Lb-Showing { Find-Ui -Name 'Showing ' -Type 'Text' -Contains }
function Lb-Sort { Find-Ui -Name ', Sort' -Type 'Button' -Contains }
function Lb-Toggle([string]$Name) { Find-Ui -Name $Name -Type 'Button' }
function Lb-IsOn($Element) {
    $p = $null
    if ($Element -and $Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Current.ToggleState -eq 'On' } else { $false }
}
function Lb-Agree([string]$When) {
    # what the screen says agrees with itself: the Unwatched switch on exactly when its chip shows
    $on = Lb-IsOn (Lb-Toggle 'Unwatched')
    $chip = [bool](Find-Ui -Name 'Remove filter Unwatched' -Type 'Button')
    Check "library ($lbTag): $When, the Unwatched switch and its chip agree" ($on -eq $chip) "switch on=$on, chip=$chip"
}
function Lb-Menu([string]$Opener, [string]$Item) {
    $b = if ($Opener -eq 'Sort') { Lb-Sort } else { Find-Ui -Name $Opener -Type 'Button' -Contains }
    if (-not $b) { return $false }
    [void](Invoke-Element $b); Lb-Settle 700
    $m = Wait-Until { Find-Ui -Name $Item -Type 'MenuItem' } -Seconds 4 -PollMs 150
    if (-not $m) { Lb-Key 0x1B; return $false }
    [void](Invoke-Element $m); Lb-Settle 900
    $true
}

# --- the skeleton while the titles load (the items request held back 5 s)
Lb-Size 1920 1080
[void](Invoke-Server '/__harness/latency' 'POST' @{ rules = @(, @($lbItems, 5000)) })
Open-Library $fixture.movie.library
Lb-Settle 1800
Lb-Shot 'movies-loading-1920'
$lbTitle = Find-Ui -Name $fixture.movie.library -Type 'Text'
Check "library ($lbTag): while loading, the library's name shows" ([bool]$lbTitle)
[void](Invoke-Server '/__harness/latency' 'POST' @{ rules = @() })
$lbCard = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200
Lb-Settle 1500

# --- arrival: the first poster has the focus, and a key works at once (keyboard first)
$lbArrived = Wait-Until { if (Lb-OnCard) { $true } } -Seconds 4 -PollMs 200
Check "library ($lbTag): arriving, the focus is on a poster" ([bool]$lbArrived) (Lb-Desc (Lb-Focus))
$lbFirst = Lb-Focus
Lb-Key 0x27
$lbNext = Lb-Focus
Check "library ($lbTag): Right at once moves along the grid" ($lbNext -and $lbFirst -and $lbNext.type -eq 'ListItem' -and $lbNext.name -ne $lbFirst.name) "$(Lb-Desc $lbFirst) -> $(Lb-Desc $lbNext)"
Lb-Both 'movies-at-rest'

# --- type-to-jump, and the same letter again
Lb-Char 'S'
$lbS = Lb-Focus
Check "library ($lbTag): typing S goes to a title starting with S" ($lbS -and $lbS.type -eq 'ListItem' -and $lbS.name -match '^(The |A |An )?S') (Lb-Desc $lbS)
Lb-Settle 1200
Lb-Char 'S'
$lbS2 = Lb-Focus
Check "library ($lbTag): S again goes to the next S title" ($lbS2 -and $lbS -and $lbS2.name -ne $lbS.name -and $lbS2.name -match '^(The |A |An )?S') "$(Lb-Desc $lbS) -> $(Lb-Desc $lbS2)"

# --- Unwatched in one click (UI Automation), the applied-filter row, its ways out
$lbUnw = Lb-Toggle 'Unwatched'
Check "library ($lbTag): a one-click Unwatched switch" ([bool]$lbUnw)
if ($lbUnw) {
    [void](Invoke-Element $lbUnw); Lb-Settle 900
    $lbRow = Lb-Showing
    Check "library ($lbTag): Unwatched on: the row says what is shown" ($lbRow -and $lbRow.Current.Name -match 'Showing \d+ of 320') $(if ($lbRow) { $lbRow.Current.Name })
    Lb-Agree 'Unwatched on'
    Lb-Both 'movies-unwatched'
    # way out 1: the chip's cross, by a click
    [void](Lb-Click (Find-Ui -Name 'Remove filter Unwatched' -Type 'Button'))
    Check "library ($lbTag): the chip's cross (a click) takes Unwatched off" (-not (Lb-Showing) -and -not (Lb-IsOn (Lb-Toggle 'Unwatched'))) ''
    # a click on the switch, then Clear all
    [void](Lb-Click (Lb-Toggle 'Unwatched'))
    Lb-Agree 'Unwatched clicked on'
    [void](Lb-Click (Find-Ui -Name 'Clear all filters' -Type 'Hyperlink'))
    if (Lb-Showing) { [void](Invoke-Element (Find-Ui -Name 'Clear all filters' -Contains)) ; Lb-Settle 700 }
    Check "library ($lbTag): Clear all takes every filter off" (-not (Lb-Showing)) ''
    Lb-Agree 'after Clear all'
    # a double click on the switch: on and off again, the row agreeing
    [void](Lb-Click (Lb-Toggle 'Unwatched') -Double)
    Lb-Agree 'after a double click on Unwatched'
    if (Lb-IsOn (Lb-Toggle 'Unwatched')) { [void](Invoke-Element (Lb-Toggle 'Unwatched')); Lb-Settle 700 }
}

# --- Sort by Date added: newest first, said; the direction in one click; the row of directions
if (Lb-Menu 'Sort' 'Date added') {
    $lbSortBtn = Lb-Sort
    Check "library ($lbTag): Sort by Date added opens newest first, said on the button" ($lbSortBtn -and $lbSortBtn.Current.Name -eq 'Date added, newest first, Sort') $(if ($lbSortBtn) { $lbSortBtn.Current.Name })
    $lbDirs = Find-Ui -Name 'Oldest first' -Type 'Button'
    Check "library ($lbTag): under a date, the letter row holds the directions" ([bool]$lbDirs)
    Lb-Both 'movies-sorted-date'
    $lbDirBtn = Find-Ui -Name ', switch to ' -Type 'Button' -Contains
    if ($lbDirBtn) { [void](Lb-Click $lbDirBtn) }
    $lbSortBtn = Lb-Sort
    Check "library ($lbTag): the direction in one click (a click): oldest first" ($lbSortBtn -and $lbSortBtn.Current.Name -eq 'Date added, oldest first, Sort') $(if ($lbSortBtn) { $lbSortBtn.Current.Name })
    [void](Lb-Menu 'Sort' 'Title')
    $lbSortBtn = Lb-Sort
    Check "library ($lbTag): back to Title, A to Z" ($lbSortBtn -and $lbSortBtn.Current.Name -eq 'Title, A to Z, Sort') $(if ($lbSortBtn) { $lbSortBtn.Current.Name })
}
else { Check "library ($lbTag): the Sort menu offers Date added" $false }

# --- the A to Z: a letter by the keyboard, its row at the top, the letter marked, where the view is
$lbM = Find-Ui -Name 'Jump to M' -Type 'Button'
Check "library ($lbTag): the A to Z offers M" ([bool]$lbM)
if ($lbM) {
    [void][OffscreenUi]::SetFocus($lbM); Lb-Settle 300
    Lb-Key 0x0D; Lb-Settle 1200
    $lbOnM = Lb-Focus
    Check "library ($lbTag): Enter on M focuses an M title" ($lbOnM -and $lbOnM.type -eq 'ListItem' -and $lbOnM.name -match '^(The |A |An )?M') (Lb-Desc $lbOnM)
    $lbPos = Find-Ui -Name 'M · ' -Type 'Text' -Contains
    Check "library ($lbTag): the readout says where (""M · n of 320"")" ($lbPos -and $lbPos.Current.Name -match '^M · \d+ of 320$') $(if ($lbPos) { $lbPos.Current.Name })
    Lb-Shot 'movies-jump-m-1920'
    $lbZ = Find-Ui -Name 'No titles start with ' -Type 'Button' -Contains
    Check "library ($lbTag): an absent letter is silent to Narrator (not in the tree)" (-not $lbZ) $(if ($lbZ) { $lbZ.Current.Name })
}

# --- a filter that leaves nothing: Favorites and a genre only one favorite has, then Unwatched (genre by genre until nothing is
# left: the panel greys a value that would leave nothing, so only a switch can get there)
$lbNone = $false
$lbFilter = Find-Ui -Name 'Filter, ' -Type 'Button' -Contains
if ($lbFilter) {
    [void](Invoke-Element $lbFilter); Lb-Settle 800
    $lbFav = Wait-Until { Find-Ui -Name 'Favorites only' -Type 'CheckBox' } -Seconds 4
    if ($lbFav) { [void](Invoke-Element $lbFav); Lb-Settle 700 }
    $lbGenres = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox))) |
        ForEach-Object { try { $_.Cached.Name } catch { try { $_.Current.Name } catch { '' } } } | Where-Object { $_ -match '^[A-Za-z ]+, 1 title$' })
    Lb-Key 0x1B
    Say "no-matches: genres with one favorite: $($lbGenres -join '; ')"
    foreach ($lbGenre in ($lbGenres | Select-Object -First 8)) {
        [void](Invoke-Element (Find-Ui -Name 'Filter, ' -Type 'Button' -Contains)); Lb-Settle 700
        $lbBox = Wait-Until { Find-Ui -Name $lbGenre -Type 'CheckBox' } -Seconds 3
        if (-not $lbBox) { Lb-Key 0x1B; continue }
        [void](Invoke-Element $lbBox); Lb-Settle 500
        Lb-Key 0x1B
        [void](Invoke-Element (Lb-Toggle 'Unwatched')); Lb-Settle 900
        if (Find-Ui -Name 'Clear filters' -Type 'Button') { $lbNone = $true; break }
        [void](Invoke-Element (Lb-Toggle 'Unwatched')); Lb-Settle 600
        [void](Lb-Click (Find-Ui -Name "Remove filter $($lbGenre -replace ', 1 title$', '')" -Type 'Button'))
    }
}
Check "library ($lbTag): a filter that leaves nothing says so (no blank grid)" $lbNone
if ($lbNone) {
    $lbNoText = Find-Ui -Name 'No unwatched favorite movies' -Type 'Text' -Contains
    Check "library ($lbTag): ... in words (""No unwatched favorite movies in <genre>"")" ([bool]$lbNoText) $(if ($lbNoText) { $lbNoText.Current.Name })
    $lbTubes = Find-Ui -Name '0 of 320' -Contains
    Lb-Both 'movies-no-matches'
    [void](Lb-Click (Find-Ui -Name 'Clear filters' -Type 'Button'))
    $lbBack = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 6
    Check "library ($lbTag): Clear filters (a click) brings every title back" ($lbBack -and -not (Lb-Showing)) ''
    Lb-Agree 'after Clear filters'
}
else {
    # leave no filter behind for what follows
    $lbClear = Find-Ui -Name 'Clear all filters' -Contains
    if ($lbClear) { [void](Invoke-Element $lbClear); Lb-Settle 800 }
}

# --- View: the list, small posters, and back to the grid (UI Automation and the keyboard)
if (Lb-Menu 'View' 'List') {
    Lb-Both 'movies-list'
    $lbRow = Find-Ui -Name ', ' -Type 'ListItem' -Contains
    Check "library ($lbTag): the list's rows are named as cards" ($lbRow -and $lbRow.Current.ClassName -eq 'LibraryListRow') $(if ($lbRow) { "$($lbRow.Current.ClassName) '$($lbRow.Current.Name)'" })
    [void](Lb-Menu 'View' 'Small posters')
    Lb-Shot 'movies-small-1920'
    [void](Lb-Menu 'View' 'Medium posters')
    $lbView = Find-Ui -Name 'View: ' -Type 'Button' -Contains
    Check "library ($lbTag): View's way back: medium posters in a grid" ($lbView -and $lbView.Current.Name -match 'medium .*grid') $(if ($lbView) { $lbView.Current.Name })
}
else { Check "library ($lbTag): View offers the list" $false }

# --- R mid-filter: the page loads again in place, the filter and the card kept, the screen agreeing with itself
[void](Invoke-Element (Lb-Toggle 'Unwatched')); Lb-Settle 800
$lbAny = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { try { -not $_.Current.IsOffscreen -and $_.Current.IsKeyboardFocusable } catch { $false } } | Select-Object -First 3)
if ($lbAny.Count -gt 2) { [void][OffscreenUi]::SetFocus($lbAny[2]); Lb-Settle 400 }
$lbBeforeR = Lb-Focus
Lb-Key 0x52; Lb-Settle 3000
[void](Wait-Until { Find-Ui -Name ', Sort' -Type 'Button' -Contains } -Seconds 10); Lb-Settle 1500
Check "library ($lbTag): R mid-filter keeps the filter" ([bool](Lb-Showing) -and (Lb-IsOn (Lb-Toggle 'Unwatched'))) ''
Lb-Agree 'after R mid-filter'
$lbAfterR = Lb-Focus
Check "library ($lbTag): R mid-filter keeps the card" ($lbAfterR -and $lbBeforeR -and $lbAfterR.name -eq $lbBeforeR.name) "$(Lb-Desc $lbBeforeR) -> $(Lb-Desc $lbAfterR)"

# --- an item and Back: the filter and the card kept
$lbOpen = Lb-Focus
if ($lbOpen -and $lbOpen.type -eq 'ListItem') {
    Lb-Key 0x0D; Lb-Settle 2500
    Lb-Key 0xA6; Lb-Settle 2500   # Browser Back
    [void](Wait-Until { Find-Ui -Name ', Sort' -Type 'Button' -Contains } -Seconds 10); Lb-Settle 1200
    $lbAfter = Lb-Focus
    Check "library ($lbTag): Back from an item: on the card left" ($lbAfter -and $lbAfter.name -eq $lbOpen.name) "$(Lb-Desc $lbOpen) -> $(Lb-Desc $lbAfter)"
    Lb-Agree 'Back from an item'
    Check "library ($lbTag): Back from an item: the filter kept" ([bool](Lb-Showing)) ''
}

# --- leaving for Home and coming back: a fresh library, its first poster focused
Open-Nav 'Home'; Lb-Settle 1500
Open-Library $fixture.movie.library
[void](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 20); Lb-Settle 1500
Check "library ($lbTag): opened again: no filter on" (-not (Lb-Showing)) ''
Lb-Agree 'opened again'
Check "library ($lbTag): opened again: the focus on a poster" (Lb-OnCard) (Lb-Desc (Lb-Focus))
