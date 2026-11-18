# 1.2.0 (the top bar, replacing the left rail), live: run with -Script wait:1 -Then offscreen\then-topbar.ps1 (signed in, on
# Home). Checks: no rail; the tab list (Narrator: a tab list "Sections", tab items "n of m", the marked one selected); one Tab
# stop and Left/Right between the tabs (the arrows open nothing); a tab opens its page and is marked; Settings from the Account
# menu and Ctrl+, (no tab, nothing marked); the window's drag region and caption buttons (WM_NCHITTEST); 800 px: the tabs in
# one menu button, search behind an icon; the player page hides the tabs. Shots: Home, a library, an item, Settings and the player at
# 1920 and 1100 (SEAGLASS_THEME, -Setting Glass, SG_SHOT_TAG as the other -Then scripts). The review's checks at the end want
# the fake server's Anime library pinned: -Setting '_Items=[{"Name":"Anime","Key":"FluentFin.Core.ViewModels.LibraryViewModel",
# "Parameter":"6a3880d4-0b68-37e6-8647-c1edfe4762c4","Persistent":true,"Commands":[]}]'. Dot-sourced by drive.ps1: every
# variable here starts with $tb so none clashes with the driver's.
$tbTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
function TbSettle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function TbSize([int]$W, [int]$H) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H); TbSettle 1500 }

# UI Automation's own COM client: the managed client cannot read PositionInSet/SizeOfSet (Narrator's "n of m")
if (-not ('SgTabsUia' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] class SgCUIAutomation { }
[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface SgIUIAutomation {
  void _0(); void _1(); void _2();
  SgIUIAutomationElement ElementFromHandle(IntPtr hwnd);
  void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
  IntPtr CreateTrueCondition();
}
[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface SgIUIAutomationElement {
  void _0(); void _1(); void _2();
  SgIUIAutomationElementArray FindAll(int scope, IntPtr condition);
  void _4(); void _5(); void _6();
  [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
}
[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface SgIUIAutomationElementArray {
  int Length { get; }
  SgIUIAutomationElement GetElement(int index);
}
public static class SgTabsUia {
  [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  // the window's tab items and tab lists: name, control type (50018 tab list, 50019 tab item), position, size, selected,
  // focused, offscreen, automation id
  public static List<object[]> Tabs(IntPtr hwnd) {
    var uia = (SgIUIAutomation)new SgCUIAutomation();
    var all = uia.ElementFromHandle(hwnd).FindAll(4, uia.CreateTrueCondition());
    var list = new List<object[]>();
    for (int i = 0; i < all.Length; i++) {
      try {
        var e = all.GetElement(i);
        int type = Convert.ToInt32(e.GetCurrentPropertyValue(30003) ?? 0);
        if (type != 50018 && type != 50019) continue;
        list.Add(new object[] { e.GetCurrentPropertyValue(30005) as string ?? "", type,
          Convert.ToInt32(e.GetCurrentPropertyValue(30152) ?? 0), Convert.ToInt32(e.GetCurrentPropertyValue(30153) ?? 0),
          Convert.ToBoolean(e.GetCurrentPropertyValue(30079) ?? false), Convert.ToBoolean(e.GetCurrentPropertyValue(30008) ?? false),
          Convert.ToBoolean(e.GetCurrentPropertyValue(30022) ?? false), e.GetCurrentPropertyValue(30011) as string ?? "" });
      } catch (Exception) { }
    }
    return list;
  }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, uint flags);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  // what a click at a screen point of FluentFin's own window hits, as Windows decides it: WM_NCHITTEST to the deepest of its
  // own child windows there (the content's input site), then to each window under it while they answer HTTRANSPARENT (-1).
  // 2 caption (drag), 1 client, 8 minimize, 9 maximize, 20 close. Only FluentFin's windows (by process id) are asked.
  public static int HitTest(IntPtr hwnd, int pid, int x, int y) {
    uint p; GetWindowThreadProcessId(hwnd, out p);
    if (p != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    var chain = new List<IntPtr> { hwnd };
    for (var cur = hwnd; ;) {
      var pt = new POINT { X = x, Y = y };
      ScreenToClient(cur, ref pt);
      var child = ChildWindowFromPointEx(cur, pt, 1);   // CWP_SKIPINVISIBLE
      if (child == IntPtr.Zero || child == cur) break;
      uint cp; GetWindowThreadProcessId(child, out cp);
      if (cp != (uint)pid) break;
      chain.Add(child); cur = child;
    }
    var lparam = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
    for (int i = chain.Count - 1; i >= 0; i--) {
      int r = (int)(long)SendMessage(chain[i], 0x0084, IntPtr.Zero, lparam);
      if (r != -1) return r;
    }
    return 0;
  }
}
"@
}
function TbTabs { @([SgTabsUia]::Tabs($script:hwnd) | ForEach-Object { [pscustomobject]@{ Name = $_[0]; Type = $_[1]; Pos = $_[2]; Size = $_[3]; Selected = $_[4]; Focused = $_[5]; Offscreen = $_[6]; Id = $_[7] } }) }
function TbItems { @(TbTabs | Where-Object { $_.Type -eq 50019 -and -not $_.Offscreen }) }
function TbMarked { @(TbItems | Where-Object Selected | ForEach-Object Name) -join ',' }
function TbFocusedTab { @(TbItems | Where-Object Focused | ForEach-Object Name) -join ',' }
function TbTab([string]$Name) { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); if ($p) { [OffscreenUi]::NavItem($p, $Name) } }
function TbWindow { [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id)) }
function TbShots([string]$Name) {
    foreach ($size in @(@(1920, 1080), @(1100, 900))) { TbSize $size[0] $size[1]; Shot "$tbTag-$Name-$($size[0])" }
    TbSize 1920 1080
}

TbSize 1920 1080
[void](Wait-Until { if ((TbItems).Count -ge 2) { $true } } -Seconds 20 -PollMs 200)
TbSettle 1500

# ---- no rail; the tab list
Check 'top bar: no rail (no navigation pane)' (-not (Find-Ui -Id 'PaneRoot' -OffscreenToo))
$tbList = @(TbTabs | Where-Object { $_.Type -eq 50018 })
$tbItems = TbItems
Say ("top bar tabs: " + (($tbItems | ForEach-Object { "{0} ({1} of {2}{3})" -f $_.Name, $_.Pos, $_.Size, $(if ($_.Selected) { ', selected' }) }) -join '; '))
Check 'top bar: a tab list named Sections' ($tbList.Count -eq 1 -and $tbList[0].Name -eq 'Sections') (($tbList | ForEach-Object Name) -join ',')
$tbNames = @($tbItems | ForEach-Object Name)
Check 'top bar: Home, the four libraries, Diary' (($tbNames -join ',') -eq 'Home,Movies,Shows,Documentaries,Anime,Diary') ($tbNames -join ',')
$tbOk = $tbItems.Count -gt 0
for ($i = 0; $i -lt $tbItems.Count; $i++) { if ($tbItems[$i].Pos -ne $i + 1 -or $tbItems[$i].Size -ne $tbItems.Count) { $tbOk = $false } }
Check 'top bar: Narrator reads each tab "n of m"' $tbOk
Check 'top bar: Home is marked on Home' ((TbMarked) -eq 'Home') (TbMarked)
Check 'top bar: no Settings tab nor gear' (-not ($tbNames -contains 'Settings') -and -not (Find-Ui -Name 'Settings' -Type 'Button'))
Dump-Tree "$tbTag-topbar-home"
Invoke-A11yScan "$tbTag-topbar-home"
TbShots 'home'

# ---- the drag region and the caption buttons (WM_NCHITTEST on the window itself)
$tbWin = TbWindow
$tbHome = TbTab 'Home'; $tbSearch = Find-Ui -Name 'Search' -Type 'Edit' -Contains
$tbHr = [OffscreenUi]::RectOf($tbHome); $tbSr = [OffscreenUi]::RectOf($tbSearch)
$tbBarY = [int]($tbWin.Top + 24)
$tbLastTab = [OffscreenUi]::RectOf((TbTab 'Diary'))
$tbGapX = [int](($tbLastTab.Right + $tbSr.Left) / 2)   # between the last tab and the search box, clear of both
$tbHits = [ordered]@{
    gap = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, $tbGapX, $tbBarY)
    tab = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbHr.X + $tbHr.Width / 2), [int]($tbHr.Y + $tbHr.Height / 2))
    search = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbSr.X + $tbSr.Width / 2), [int]($tbSr.Y + $tbSr.Height / 2))
    close = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbWin.Right - 24), [int]($tbWin.Top + 16))
    minimize = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbWin.Right - 24 - 2 * 46), [int]($tbWin.Top + 16))
}
$tbAr = [OffscreenUi]::RectOf((Find-Ui -Name 'Account, ' -Type 'Button' -Contains))
$tbHits.account = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbAr.X + $tbAr.Width / 2), [int]($tbAr.Y + $tbAr.Height / 2))
# the last tab came with the libraries, after the bar was laid out (the TitleBar looks again when its headers change size)
$tbHits.lastTab = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbLastTab.X + $tbLastTab.Width / 2), [int]($tbLastTab.Y + $tbLastTab.Height / 2))
Say ("hit tests: " + (($tbHits.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
Check 'top bar: the bar between the tabs and search drags the window' ($tbHits.gap -eq 2) "gap: $($tbHits.gap)"
Check 'top bar: the tabs, the search box and the account take clicks (not drag)' ($tbHits.tab -eq 1 -and $tbHits.lastTab -eq 1 -and $tbHits.search -eq 1 -and $tbHits.account -eq 1) "first tab: $($tbHits.tab), last tab: $($tbHits.lastTab), search: $($tbHits.search), account: $($tbHits.account)"
Check 'top bar: the caption buttons are the window''s (close, minimize)' ($tbHits.close -eq 20 -and $tbHits.minimize -eq 8) "close: $($tbHits.close), minimize: $($tbHits.minimize)"

# ---- the keyboard: Tab lands on the marked tab, Left/Right move between the tabs and open nothing, Enter opens
[void][OffscreenUi]::SetFocus($tbHome); TbSettle 400
Key 0x27; TbSettle 400                                 # Right
$tbAfterRight = TbFocusedTab
Key 0x25; TbSettle 300; Key 0x25; TbSettle 400         # Left, Left: round to the last
$tbAfterLeft = TbFocusedTab
Check 'top bar: Right moves to the next tab, Left round to the last, and Home stays marked' ($tbAfterRight -eq 'Movies' -and $tbAfterLeft -eq 'Diary' -and (TbMarked) -eq 'Home') "Right: $tbAfterRight, Left Left: $tbAfterLeft, marked: $(TbMarked)"
Key 0x25; TbSettle 300; Key 0x25; TbSettle 300; Key 0x25; TbSettle 300; Key 0x25; TbSettle 300   # Left x4: Movies
$tbFocus = TbFocusedTab
Key 0x0D                                                # Enter
$tbMovies = Wait-Until { if ((TbMarked) -eq 'Movies') { $true } } -Seconds 15 -PollMs 200
TbSettle 1500
Check 'top bar: Enter opens the focused tab, which is then marked' ([bool]$tbMovies -and $tbFocus -eq 'Movies') "focused: $tbFocus, marked: $(TbMarked)"
Key 0x09; TbSettle 500                                  # Tab: out of the tab list
$tbOut = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
$tbOutTab = TbFocusedTab
Check 'top bar: one Tab stop: Tab leaves the tab list (not the next tab)' (-not $tbOutTab) "after Tab: $tbOut"
# Tab into the list from before it (Back) lands on the marked tab, not the first. (Shift+Tab is the same code, the other way;
# posted Shift and Ctrl are not seen by WinUI's key state, so Shift+Tab and Ctrl+, are not driven here: TopBarTests, AppShortcuts.)
$tbBackButton = @(Find-Ui -Name 'Back' -Type 'Button') | Where-Object { ([OffscreenUi]::RectOf($_).Top - (TbWindow).Top) -lt 48 } | Select-Object -First 1
if ($tbBackButton) { [void][OffscreenUi]::SetFocus($tbBackButton); TbSettle 400; Key 0x09; TbSettle 500 }
$tbIn = TbFocusedTab
Check 'top bar: Tab into the tab list lands on the marked tab' ([bool]$tbBackButton -and $tbIn -eq 'Movies') "back button: $([bool]$tbBackButton), focused: $tbIn ($([OffscreenUi]::Focused($script:hwnd, $script:ui.Id)))"
Dump-Tree "$tbTag-topbar-library"
TbShots 'library'

# ---- Settings: from the Account menu (no tab), nothing marked; and Ctrl+,
Open-Nav 'Home'; [void](Wait-Until { if ((TbMarked) -eq 'Home') { $true } } -Seconds 10)
Open-Nav 'Settings'
$tbSettings = Wait-Ui -Name 'Appearance' -Type 'Text' -OffscreenToo -Seconds 10
TbSettle 1200
Check 'top bar: Settings opens from the Account menu, and marks no tab' ([bool]$tbSettings -and -not (TbMarked)) "marked: '$(TbMarked)'"
TbShots 'settings'
Open-Nav 'Home'; [void](Wait-Until { if ((TbMarked) -eq 'Home') { $true } } -Seconds 10); TbSettle 800

# ---- a narrow window (800 px; the bar folds when its parts do not fit, no longer at a fixed 1100): the tabs in one menu
# button, search behind an icon
TbSize 800 700
$tbMenu = Find-Ui -Id 'TopTabsMenu'
Check 'top bar (800 px): the tabs fold into one menu button' ([bool]$tbMenu -and (TbItems).Count -eq 0) "menu: $([bool]$tbMenu), tabs shown: $((TbItems).Count)"
$tbIcon = Find-Ui -Name 'Search' -Type 'Button'
Check 'top bar (800 px): search is an icon' ([bool]$tbIcon -and -not (Find-Ui -Name 'Search' -Type 'Edit' -Contains))
if ($tbMenu) {
    Open-Nav 'Shows'
    $tbShows = Wait-Ui -Name 'Shows' -Type 'Text' -Seconds 10
    TbSettle 1200
    Check 'top bar (800 px): the menu opens a section' ([bool]$tbShows)
    [void](Invoke-Element (Find-Ui -Id 'TopTabsMenu')); TbSettle 800
    Shot "$tbTag-topbar-menu-800"
    Key 0x1B; TbSettle 500
}
if ($tbIcon) {
    [void](Invoke-Element $tbIcon); TbSettle 800
    $tbBox = Find-Ui -Name 'Search' -Type 'Edit' -Contains
    Check 'top bar (800 px): the icon opens the search box' ([bool]$tbBox)
    if ($tbBox) {
        TbSettle 600
        $tbBr = [OffscreenUi]::RectOf($tbBox)
        $tbBoxHit = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($tbBr.X + 20), [int]($tbBr.Y + $tbBr.Height / 2))
        Check 'top bar (800 px): the opened box takes clicks (not drag)' ($tbBoxHit -eq 1) "hit: $tbBoxHit"
    }
    Shot "$tbTag-topbar-search-800"
    Key 0x1B; TbSettle 300; Key 0x09; TbSettle 600
}
TbSize 1920 1080
Open-Nav 'Home'; TbSettle 1000

# ---- the player: the tabs go (its own bar is the way out); shots
Step-Series $false
TbSettle 1500
# an item page (the series): only the top bar, no rail, no left gutter
Check 'top bar: an item page has the bar and no navigation pane' ((TbItems).Count -ge 2 -and -not (Find-Ui -Id 'PaneRoot' -OffscreenToo)) "tabs: $((TbItems).Count)"
TbShots 'item'
Step-Season $false
Step-Play
TbSettle 1500
Check 'top bar: the player page hides the tabs' ((TbItems).Count -eq 0 -and -not (Find-Ui -Id 'TopTabsMenu')) "tabs shown: $((TbItems).Count)"
TbShots 'player'
Step-Leave
[void](Wait-Until { if ((TbItems).Count -ge 2) { $true } } -Seconds 10)
Check 'top bar: the tabs come back after the player, the section still marked' ((TbItems).Count -ge 2 -and (TbMarked)) "marked: $(TbMarked)"

# ---- review of 590eaf6
function TbBack {
    $b = @(Find-Ui -Name 'Back' -Type 'Button') | Where-Object { ([OffscreenUi]::RectOf($_).Top - (TbWindow).Top) -lt 48 } | Select-Object -First 1
    if ($b) { [void](Invoke-Element $b) }
}
# 4: a page returned to gets the tab it had: Shows, its series, the Movies tab, Back: Shows (Movies stayed marked)
TbSize 1920 1080
Open-Library 'Shows'
Open-Card $fixture.series.name
[void](Wait-ItemPage $fixture.series.name)
TbSettle 800
Open-Nav 'Movies'
[void](Wait-Until { if ((TbMarked) -eq 'Movies') { $true } } -Seconds 10)
TbBack
$tbAfterBack = Wait-ItemPage $fixture.series.name
TbSettle 800
Check 'top bar: Back to the series marks Shows again (not the Movies tab left)' ([bool]$tbAfterBack -and (TbMarked) -eq 'Shows') "marked: $(TbMarked)"

# 3 and 1: a library renamed on the server (LibraryChanged): its tab follows; the long name at several widths
Open-Nav 'Home'; TbSettle 600
[void](Invoke-Server '/__harness/libraries' 'POST' @{ rename = @{ Shows = 'Television Shows' } })
$tbRenamed = Wait-Until { if ((TbItems | ForEach-Object Name) -contains 'Television Shows') { $true } } -Seconds 15 -PollMs 250
Check 'top bar: a library renamed on the server renames its tab' ([bool]$tbRenamed) ((TbItems | ForEach-Object Name) -join ',')
function TbRight([string]$What) {
    # the right header's clickable part and the left header's end, and what a click hits there
    $win = TbWindow
    $acct = Find-Ui -Name 'Account, ' -Type 'Button' -Contains
    $box = Find-Ui -Name 'Search' -Type 'Edit' -Contains
    $icon = Find-Ui -Name 'Search' -Type 'Button'
    $rights = @()
    foreach ($n in (TbItems | ForEach-Object Name)) { $t = TbTab $n; if ($t) { $rights += [OffscreenUi]::RectOf($t).Right } }
    $menu = Find-Ui -Id 'TopTabsMenu'; if ($menu) { $rights += [OffscreenUi]::RectOf($menu).Right }
    # nothing of the left header shown (search open in the narrowest window): the title bar's Back, else the window's edge
    $back = @(Find-Ui -Name 'Back' -Type 'Button') | Where-Object { ([OffscreenUi]::RectOf($_).Top - $win.Top) -lt 48 } | Select-Object -First 1
    if ($back) { $rights += [OffscreenUi]::RectOf($back).Right }
    $leftEnd = if ($rights.Count) { ($rights | Measure-Object -Maximum).Maximum } else { $win.Left + 8 }
    $rightStart = @(@($box, $icon, $acct) | Where-Object { $_ } | ForEach-Object { [OffscreenUi]::RectOf($_).Left }) | Measure-Object -Minimum
    $ar = [OffscreenUi]::RectOf($acct)
    $acctHit = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($ar.X + $ar.Width / 2), [int]($ar.Y + $ar.Height / 2))
    $gap = $rightStart.Minimum - $leftEnd
    $gapHit = [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int](($leftEnd + $rightStart.Minimum) / 2), [int]($win.Top + 24))
    $boxHit = if ($box) { $r = [OffscreenUi]::RectOf($box); [SgTabsUia]::HitTest($script:hwnd, $script:ui.Id, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) } else { 1 }
    Check "top bar ($What): the account and search take clicks, and a drag gap of 48 px or more is left" ($acctHit -eq 1 -and $boxHit -eq 1 -and $gap -ge 47 -and $gapHit -eq 2) ("account: {0}, search: {1}, gap: {2:0} px (hit {3}), compact: {4}, sections: {5}" -f $acctHit, $boxHit, $gap, $gapHit, [bool](Find-Ui -Id 'TopTabsMenu'), (@(TbItems).Count + [int][bool](Find-Ui -Id 'TopTabsMenu')))
}
foreach ($w in 1920, 1300, 1120) { TbSize $w 900; TbRight "$w px, a long library name" }
Shot "$tbTag-topbar-long-name-1120"
foreach ($w in 600, 520) {
    TbSize $w 800
    $i = Find-Ui -Name 'Search' -Type 'Button'
    if ($i) { [void](Invoke-Element $i); TbSettle 900 }
    TbRight "$w px, search open"
    Shot "$tbTag-topbar-search-open-$w"
    Key 0x1B; TbSettle 300
}
TbSize 1920 1080
[void](Invoke-Server '/__harness/libraries' 'POST' @{ rename = @{ 'Television Shows' = 'Shows' } })
[void](Wait-Until { if ((TbItems | ForEach-Object Name) -contains 'Shows') { $true } } -Seconds 15 -PollMs 250)

# 3: a fifth library added on the server: the libraries move into the Libraries menu, the new one in it
[void](Invoke-Server '/__harness/libraries' 'POST' @{ add = @('Concerts') })
$tbFifth = Wait-Until { if ((TbItems | ForEach-Object Name) -contains 'Libraries') { $true } } -Seconds 15 -PollMs 250
Check 'top bar: a fifth library moves the libraries into one Libraries tab' ([bool]$tbFifth) ((TbItems | ForEach-Object Name) -join ',')
Open-Nav 'Concerts'
$tbConcerts = Wait-Ui -Name 'Concerts' -Type 'Text' -Seconds 10
Check 'top bar: the new library opens from the Libraries menu' ([bool]$tbConcerts -and (TbMarked) -eq 'Libraries') "marked: $(TbMarked)"

# 2: a pinned library (the run is given one: -Setting _Items=[Anime], see the header) is a tab of its own once the libraries
# are in the menu; its menu (the menu key) has Unpin, which removes the tab
$tbPinned = Wait-Until { if ((TbItems | ForEach-Object Name) -contains 'Anime') { $true } } -Seconds 10 -PollMs 200
Check 'top bar: a pinned library is a tab of its own beside the Libraries menu' ([bool]$tbPinned) ((TbItems | ForEach-Object Name) -join ',')
if ($tbPinned) {
    [void][OffscreenUi]::SetFocus((TbTab 'Anime')); TbSettle 400; Key 0x5D; TbSettle 800
    $tbUnpin = Find-Ui -Name 'Unpin' -Type 'MenuItem'
    if ($tbUnpin) { [void](Invoke-Element $tbUnpin) }
    $tbGone = Wait-Until { if (-not ((TbItems | ForEach-Object Name) -contains 'Anime')) { $true } } -Seconds 10 -PollMs 200
    Check 'top bar: the menu key on a pinned tab offers Unpin, which removes the tab' ([bool]$tbUnpin -and [bool]$tbGone) ((TbItems | ForEach-Object Name) -join ',')
}
