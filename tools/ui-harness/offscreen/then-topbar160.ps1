# Nixie 1.6 (the top bar's favourites, the review's T1 to T4), live: -Script wait:1 -Then offscreen\then-topbar160.ps1
# (NIXIE_LAYOUT the theme, NIXIE_THEME Dark or Light, SG_SHOT_TAG names the shots). Two fixtures (TB_MODE):
#  - matrix (FAKESERVER_VIEWS=1, no Collections view): the bar with 1, 3, 6 and 9 libraries (the others added while it
#    runs, as a server does) at 2560, 1920, 1440 and 1100: Home · up to 3 favourites · Collections · Libraries ▾, its
#    geometry (y 24, Back, the search box centred or pinned, the drag gap), the favourites folding from the right; the
#    Libraries menu with its stars by mouse and keyboard first (a star keeps the menu open, a fourth is refused and said,
#    Esc gives the keyboard back to Libraries, a choice puts it in the page and names Libraries after it); Collections
#    with neither server view (1.6 integration: the Collections page, lit); the Libraries page's stars (a click stars, never
#    opens the tile; the tile's menu by Shift+F10; R keeps them); the Diary in the account menu, lighting the account.
#  - hub (FAKESERVER_HUB_VIEWS=1, FAKESERVER_COLLECTIONS=1): three "[HUB] …" hubs first in the server's order: the default
#    favourites are Movies and Shows by kind, never the hubs; Collections opens the Collections page, lit; the menu has
#    no Collections or Playlists; a library opened twice is the third favourite after a restart (the opens, kept per server
#    and user), as is a star.
# Every variable starts with $tb (dot-sourced by drive.ps1).
$tbTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'tb160' }
$tbMode = if ($env:TB_MODE) { $env:TB_MODE } else { 'matrix' }
$tbUia = [System.Windows.Automation.AutomationElement]
function TbSettle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
if (-not ('TbW' -as [type])) {
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class TbW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  public static int[] Client(IntPtr h) { RECT r; GetClientRect(h, out r); var p = new POINT(); ClientToScreen(h, ref p); return new[] { p.X, p.Y, r.R - r.L, r.B - r.T }; }
}
"@
}
function TbWin { $c = [TbW]::Client($script:hwnd); [pscustomobject]@{ Left = $c[0]; Top = $c[1]; Width = $c[2]; Height = $c[3] } }
function TbPane { [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id) }
function TbTabItems {
    $p = TbPane
    if (-not $p) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($tbUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    @($p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })
}
function TbSelected($e) { $sp = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Current.IsSelected } else { $false } }
function TbMarked { @(TbTabItems | Where-Object { TbSelected $_ } | ForEach-Object { $_.Current.Name }) -join ',' }
function TbNames { @(TbTabItems | ForEach-Object { $_.Current.Name }) -join ',' }
function TbLibs { TbTabItems | Where-Object { $_.Current.HelpText -like 'Libraries menu*' } | Select-Object -First 1 }
function TbTab([string]$Name) { TbTabItems | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1 }
# 1.6 integration: the Collections page shows: its title element (TitleText) says Collections (the Libraries page's says
# Libraries; a Text named Collections or Libraries alone is the top bar's own tab too)
function TbOnCollectionsPage { [bool](Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 8 -PollMs 250) }
function TbFocused {
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($tbUia::HasKeyboardFocusProperty, $true))) |
        Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window }) | Select-Object -Last 1
}
function TbInBar($e) { if (-not $e) { return $true }; $r = $e.Current.BoundingRectangle; ($r.Top - (TbWin).Top) -lt 50 }
function TbBox { Find-Ui -Name 'Search' -Type 'Edit' -Contains }
function TbAccount { Find-Ui -Name 'Account, ' -Type 'Button' -Contains }
# the Libraries menu's entries (TopMenuEntry: menu items) and the stars (buttons named for what they do)
function TbEntries { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($tbUia::ClassNameProperty, 'TopMenuEntry'))) | Where-Object { -not $_.Current.IsOffscreen }) }
function TbStars {
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($tbUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
        Where-Object { $n = $_.Current.Name; ($n -like 'Add * to the top bar' -or $n -like 'Remove * from the top bar') -and -not $_.Current.IsOffscreen })
}
function TbStar([string]$Library) { TbStars | Where-Object { $_.Current.Name -in @("Add $Library to the top bar", "Remove $Library from the top bar") } | Select-Object -First 1 }
function TbOn([string]$Library) { $s = TbStar $Library; $s -and $s.Current.Name -like 'Remove *' }
function TbMenuOpen { (TbEntries).Count -gt 0 }
function TbOpenMenu([switch]$Keyboard) {
    $libs = TbLibs
    if (-not $libs) { return $false }
    if ($Keyboard) { [void][OffscreenUi]::SetFocus($libs); TbSettle 300; Key 0x0D } else { [void](Invoke-Element $libs) }
    [bool](Wait-Until { if (TbMenuOpen) { $true } } -Seconds 6 -PollMs 100)
}
function TbCloseMenu { if (TbMenuOpen) { Key 0x1B; [void](Wait-Until { if (-not (TbMenuOpen)) { $true } } -Seconds 4 -PollMs 100) }; TbSettle 900 }
function TbNote { [OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($tbUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like 'Up to 3 favourites*' -and -not $_.Current.IsOffscreen } | Select-Object -First 1 }
function TbRow { @(TbTabItems | ForEach-Object { $_.Current.Name }) }
function TbCy($r, $win) { [Math]::Round($r.Top + $r.Height / 2 - $win.Top, 1) }
function TbSize([int]$W, [int]$H) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H)
    [void](Wait-Until { if ([Math]::Abs((TbWin).Width - $W) -le 24) { $true } } -Seconds 10 -PollMs 200)
    $last = ''; $same = 0
    for ($i = 0; $i -lt 30 -and $same -lt 3; $i++) { TbSettle 400; $now = "$(TbNames)|$([int]((TbBox).Current.BoundingRectangle.Left))"; if ($now -eq $last) { $same++ } else { $same = 0; $last = $now } }
    TbSettle 600
}
# the views the server lists now (its media libraries in order; the Collections and Playlists views apart)
function TbViews { @((Invoke-Server '/__harness/fixture').views) }
# the media libraries as the bar and the menu name them: 1.6 integration, the tidy library names (CollectionNames.DisplayLibrary:
# the fake server's hubs "[HUB] Trending Movies" read "Trending Movies"; no two of its libraries tidy to one name)
function TbMedia { @(TbViews | Where-Object { $_ -notin @('Collections', 'Playlists') } | ForEach-Object { $_ -replace '^\[HUB\]\s+', '' }) }

# ---- the bar at a width: its order, its geometry, the fold from the right, a shot
function TbBar([int]$W, [string]$Label, [string[]]$Favourites) {
    TbSize $W $(if ($W -ge 1920) { 1080 } else { 900 })
    $win = TbWin
    if ([Math]::Abs($win.Width - $W) -gt 24) { Say "bar $W ($Label): skipped, the window is $($win.Width) px wide at most on this desktop"; return }
    $row = TbRow
    $compact = [bool](Find-Ui -Id 'TopTabsMenu')
    if ($compact) { Say "bar $W ($Label): the sections' menu (compact)"; Shot "$tbTag-$Label-bar-$W"; return }
    # Home · the favourites shown (from the left) · Collections · Libraries ▾
    $shown = @($row | Select-Object -Skip 1 | Select-Object -First ([Math]::Max(0, $row.Count - 3)))
    $prefix = @($Favourites | Select-Object -First $shown.Count)
    $libs = TbLibs
    Check "bar $W ($Label): Home, the favourites from the left, Collections, then Libraries (a menu)" ($row.Count -ge 3 -and $row[0] -eq 'Home' -and $row[-2] -eq 'Collections' -and [bool]$libs -and $row[-1] -eq $libs.Current.Name -and ($shown -join '|') -eq ($prefix -join '|')) "$($row -join ', ') (favourites: $($Favourites -join ', '))"
    $script:tbShown["$Label-$W"] = $shown.Count
    $cys = [ordered]@{}
    foreach ($t in TbTabItems) { $cys["tab:$($t.Current.Name)"] = TbCy $t.Current.BoundingRectangle $win }
    $box = TbBox; $acct = TbAccount; $close = Find-Ui -Name 'Close' -Type 'Button'
    if ($box) { $cys.search = TbCy $box.Current.BoundingRectangle $win }
    if ($acct) { $cys.account = TbCy $acct.Current.BoundingRectangle $win }
    if ($close) { $cys.close = TbCy $close.Current.BoundingRectangle $win }
    $off = @($cys.GetEnumerator() | Where-Object { [Math]::Abs($_.Value - 24) -gt 1.5 })
    Check "bar $W ($Label): every item and the caption buttons centred on y 24" ($off.Count -eq 0 -and $close) (($cys.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')
    if ($box -and $acct) {
        $sr = $box.Current.BoundingRectangle; $ar = $acct.Current.BoundingRectangle
        $lastTab = (TbTabItems | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum
        $centred = [Math]::Abs(($sr.Left + $sr.Width / 2) - ($win.Left + $win.Width / 2)) -le 2
        $pinned = [Math]::Abs($ar.Left - $sr.Right - 8) -le 1.5
        Check "bar $W ($Label): the search box centred in the window, or 8 px from the account" ($centred -or $pinned) "x $([int]($sr.Left - $win.Left)) w $([int]$sr.Width)"
        Check "bar $W ($Label): a drag gap of 48 px or more between the tabs and the search box" (($sr.Left - $lastTab) -ge 47) "gap $([int]($sr.Left - $lastTab))"
    }
    Say "bar $W ($Label): $($row -join ', ')"
    Shot "$tbTag-$Label-bar-$W"
}
$tbShown = @{}
function TbBars([string]$Label, [string[]]$Favourites) {
    foreach ($w in @(2560, 1920, 1440, 1100)) { TbBar $w $Label $Favourites }
    $prev = 99
    foreach ($w in @(2560, 1920, 1440, 1100)) {
        if (-not $tbShown.ContainsKey("$Label-$w")) { continue }
        $n = $tbShown["$Label-$w"]
        Check "fold ($Label, $w): no more favourites than in a wider window" ($n -le $prev) "$n after $prev"
        $prev = $n
    }
    TbSize 1920 1080
}
# the menu at 1920: All libraries, then every media library in the server's order, each with its star; the lit one checked
function TbMenuCheck([string]$Label, [string[]]$Favourites, [switch]$Keyboard) {
    $open = TbOpenMenu -Keyboard:$Keyboard
    $entries = @(TbEntries | ForEach-Object { $_.Current.Name })
    $media = TbMedia
    Check "menu ($Label): opens: All libraries, then every media library in the server's order" ($open -and ($entries -join '|') -eq ((@('All libraries') + $media) -join '|')) "$($entries -join ', ') (server: $($media -join ', '))"
    $stars = @($media | ForEach-Object { $s = TbStar $_; if ($s) { "$_=$(if ($s.Current.Name -like 'Remove *') { 'on' } else { 'off' })" } else { "$_=none" } })
    $want = @($media | ForEach-Object { "$_=$(if ($Favourites -contains $_) { 'on' } else { 'off' })" })
    Check "menu ($Label): each library has its star, lit for the favourites only" (($stars -join '|') -eq ($want -join '|')) ($stars -join ', ')
    TbSettle 700 # the menu's opening fade
    Shot "$tbTag-$Label-menu"
}

TbSize 1920 1080
[void](Wait-Until { if ((TbTabItems).Count -ge 3 -and (TbLibs)) { $true } } -Seconds 20 -PollMs 200)
TbSettle 2000
Say "mode $tbMode; views: $((TbViews) -join ', '); bar: $(TbNames)"
Check 'launch: the Diary is not a tab (the account menu has it)' (-not ((TbRow) -contains 'Diary')) (TbNames)

if ($tbMode -eq 'hub') {
    # ---- the hubs: the default favourites by kind
    Check 'hub: the default favourites are Movies and Shows by their kind, no hub' (((TbRow) -join '|') -eq 'Home|Movies|Shows|Collections|Libraries') (TbNames)
    TbBar 1920 'hub' @('Movies', 'Shows')
    TbMenuCheck 'hub' @('Movies', 'Shows') -Keyboard
    Check 'hub (menu): the server''s Collections and Playlists views are not in it' (-not (@(TbEntries | ForEach-Object { $_.Current.Name }) | Where-Object { $_ -in @('Collections', 'Playlists') })) ''
    TbCloseMenu
    $tbF = TbFocused
    Check 'hub (menu, keyboard): Esc gives the keyboard back to Libraries' ($tbF -and $tbF.Current.HelpText -like 'Libraries menu*') "focused: $(if ($tbF) { $tbF.Current.Name })"
    # Collections: the Collections page, lit (1.6 integration: topbar's interim route opened the server's box-set view)
    [void](Invoke-Element (TbTab 'Collections'))
    $tbOpened = Wait-Until { if ((TbMarked) -eq 'Collections') { $true } } -Seconds 15 -PollMs 200
    TbSettle 1800
    Check 'hub: Collections opens the Collections page and is lit' ([bool]$tbOpened -and (TbOnCollectionsPage)) "marked: $(TbMarked)"
    Shot "$tbTag-hub-collections-1920"
    # the Libraries page: its stars
    Open-Nav 'Libraries'
    [void](Wait-Until { if ((TbStars).Count -ge 2) { $true } } -Seconds 15 -PollMs 200)
    TbSettle 1500
    $tbPage = @(TbStars | Where-Object { -not (TbInBar $_) } | ForEach-Object { $_.Current.Name })
    Check 'hub (Libraries page): a star on each media tile, none on the collections' ($tbPage.Count -eq (TbMedia).Count) ($tbPage -join ', ')
    Shot "$tbTag-hub-libraries-1920"
    # the opens: Anime twice (and a page left between), then a restart: Anime is the third favourite
    foreach ($tbN in 1..2) {
        Open-Nav 'Anime'
        $tbLit = Wait-Until { if ((TbMarked) -eq 'Anime') { $true } } -Seconds 15 -PollMs 200; TbSettle 1200
        if ($tbN -eq 1) {
            Check 'hub: a library without a tab, opened from the menu, names Libraries after it and lights it' ([bool]$tbLit -and (TbLibs).Current.Name -eq 'Anime') "marked: $(TbMarked); libraries tab: $((TbLibs).Current.Name)"
        }
        Open-Nav 'Home'; TbSettle 1200
    }
    Step-Relaunch
    TbSize 1920 1080
    [void](Wait-Until { if ((TbTabItems).Count -ge 4 -and (TbLibs)) { $true } } -Seconds 20 -PollMs 200); TbSettle 2000
    Check 'hub (restart): the library opened most is the third favourite, kept for this server and user' (((TbRow) -join '|') -eq 'Home|Movies|Shows|Anime|Collections|Libraries') (TbNames)
    Shot "$tbTag-hub-restart-1920"
    return
}

# ================= matrix: 1, 3, 6 and 9 libraries
$tbFav = @('Movies')
Check 'matrix (1 library): Home, Movies, Collections, Libraries' (((TbRow) -join '|') -eq 'Home|Movies|Collections|Libraries') (TbNames)
TbBars '1lib' $tbFav
TbMenuCheck '1lib' $tbFav
TbCloseMenu

# ---- 3 libraries: two added on the server (films: no Shows, nothing opened: not favourites), then starred with the mouse
[void](Invoke-Server '/__harness/libraries' 'POST' @{ add = @('Kids and Family', 'Stand-up Specials') })
[void](Wait-Until { if (TbOpenMenu) { if ((TbEntries).Count -ge 4) { $true } else { TbCloseMenu; $null } } } -Seconds 20 -PollMs 500)
TbCloseMenu
Check 'matrix (3 libraries): new libraries are in the menu, not the bar' (((TbRow) -join '|') -eq 'Home|Movies|Collections|Libraries') (TbNames)
[void](TbOpenMenu)
foreach ($tbLib in 'Kids and Family', 'Stand-up Specials') {
    $tbS = TbStar $tbLib
    if ($tbS) { [void](Invoke-Element $tbS) }
    $tbOn = Wait-Until { if (TbOn $tbLib) { $true } } -Seconds 5 -PollMs 100
    Check "menu (mouse): the star adds $tbLib, its name now says Remove, the menu stays open" ([bool]$tbOn -and (TbMenuOpen)) "star: $((TbStar $tbLib).Current.Name)"
}
Check 'menu: the bar waits for the menu to close' (((TbRow) -join '|') -eq 'Home|Movies|Collections|Libraries') (TbNames)
TbCloseMenu
$tbFav = @('Movies', 'Kids and Family', 'Stand-up Specials')
[void](Wait-Until { if (((TbRow) -join '|') -eq "Home|$($tbFav -join '|')|Collections|Libraries") { $true } } -Seconds 6 -PollMs 200)
Check 'menu: closed, the bar shows the stars' (((TbRow) -join '|') -eq "Home|$($tbFav -join '|')|Collections|Libraries") (TbNames)
TbBars '3lib' $tbFav

# ---- 6 libraries; the menu keyboard first: a fourth star refused and said, a star off and on by keyboard
[void](Invoke-Server '/__harness/libraries' 'POST' @{ add = @('Concerts', 'Classics', 'Home Videos') })
[void](Wait-Until { if (TbOpenMenu) { if ((TbEntries).Count -ge 7) { $true } else { TbCloseMenu; $null } } } -Seconds 20 -PollMs 500)
TbCloseMenu
TbMenuCheck '6lib' $tbFav -Keyboard
$tbF = TbFocused
Check 'menu (keyboard): Enter on Libraries opens it with the keyboard on All libraries' ($tbF -and $tbF.Current.Name -eq 'All libraries') "focused: $(if ($tbF) { $tbF.Current.Name })"
# Down to Concerts (All libraries, Movies, Kids and Family, Stand-up Specials, Concerts), Right to its star, Enter: refused
foreach ($tbK in 1..4) { Key 0x28; TbSettle 200 }
Key 0x27; TbSettle 300
$tbF = TbFocused
Check 'menu (keyboard): Down to Concerts, Right to its star' ($tbF -and $tbF.Current.Name -eq 'Add Concerts to the top bar') "focused: $(if ($tbF) { $tbF.Current.Name })"
Key 0x0D; TbSettle 700
$tbNote = TbNote
Check 'menu (keyboard): a fourth star is refused, said under the list, nothing changed' ([bool]$tbNote -and $tbNote.Current.Name -eq 'Up to 3 favourites in the top bar. Remove a star to add Concerts.' -and -not (TbOn 'Concerts') -and (TbMenuOpen)) "note: $(if ($tbNote) { $tbNote.Current.Name }); star: $((TbStar 'Concerts').Current.Name)"
Shot "$tbTag-6lib-menu-full"
# Up to Stand-up Specials' star, Space: off; Down to Concerts' star, Enter: on (a held key repeats: once each)
Key 0x26; TbSettle 300
$tbF = TbFocused
Check 'menu (keyboard): Up from a star goes to the star above' ($tbF -and $tbF.Current.Name -eq 'Remove Stand-up Specials from the top bar') "focused: $(if ($tbF) { $tbF.Current.Name })"
Key 0x20; TbSettle 500
Key 0x28; TbSettle 300; Key 0x0D; TbSettle 500
Check 'menu (keyboard): Space removes, Enter adds, the note goes' (-not (TbOn 'Stand-up Specials') -and (TbOn 'Concerts') -and -not (TbNote)) "stand-up: $((TbStar 'Stand-up Specials').Current.Name); concerts: $((TbStar 'Concerts').Current.Name)"
TbCloseMenu
$tbFav = @('Movies', 'Kids and Family', 'Concerts')
[void](Wait-Until { if (((TbRow) -join '|') -eq "Home|$($tbFav -join '|')|Collections|Libraries") { $true } } -Seconds 6 -PollMs 200)
TbSettle 500
$tbF = TbFocused
Check 'menu (keyboard): Esc, the keyboard back on Libraries, the bar follows the stars' ($tbF -and $tbF.Current.HelpText -like 'Libraries menu*' -and ((TbRow) -join '|') -eq "Home|$($tbFav -join '|')|Collections|Libraries") "focused: $(if ($tbF) { $tbF.Current.Name }); bar: $(TbNames)"
TbBars '6lib' $tbFav

# ---- 9 libraries: a library chosen in the menu opens, Libraries named after it and lit, the keyboard in the page
[void](Invoke-Server '/__harness/libraries' 'POST' @{ add = @('4K Remasters', 'Documentaries Plus', 'Anime Club') })
[void](Wait-Until { if (TbOpenMenu) { if ((TbEntries).Count -ge 10) { $true } else { TbCloseMenu; $null } } } -Seconds 20 -PollMs 500)
TbCloseMenu
TbMenuCheck '9lib' $tbFav
$tbE = TbEntries | Where-Object { $_.Current.Name -eq 'Home Videos' } | Select-Object -First 1
if ($tbE) { [void](Invoke-Element $tbE) }
$tbOpened = Wait-Until { if ((TbMarked) -eq 'Home Videos') { $true } } -Seconds 15 -PollMs 200
TbSettle 1800
$tbF = TbFocused
Check 'menu (mouse): a library chosen opens, Libraries named after it and lit' ([bool]$tbOpened -and (TbLibs).Current.Name -eq 'Home Videos') "marked: $(TbMarked); libraries tab: $((TbLibs).Current.Name)"
Check 'menu: the keyboard goes to the page, not back to the bar' ($tbF -and -not (TbInBar $tbF)) "focused: $(if ($tbF) { "$($tbF.Current.Name) ($($tbF.Current.ClassName))" })"
# (UI Automation's invoke is no key or click: the page's arrival focus, which waits for its data, could still close a menu
# opened at once; a person's click or key counts as moving first)
TbSettle 2500
[void](TbOpenMenu -Keyboard)
$tbE = TbEntries | Where-Object { $_.Current.Name -eq 'Home Videos' } | Select-Object -First 1
$tbSel = $null; $tbSp = $null
if ($tbE -and $tbE.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$tbSp)) { $tbSel = $tbSp.Current.IsSelected }
Check 'menu: the current library is checked (selected for Narrator)' ([bool]$tbSel) ''
TbSettle 700
Shot "$tbTag-9lib-menu-current"
TbCloseMenu
TbBars '9lib' $tbFav

# ---- Collections with neither server view: the Collections page, lit; All libraries is Libraries; Collections again opens
# its page (1.6 integration: the Collections page replaced topbar's interim Libraries page lit as Collections)
[void](Invoke-Element (TbTab 'Collections'))
$tbOpened = Wait-Until { if ((TbMarked) -eq 'Collections') { $true } } -Seconds 15 -PollMs 200
TbSettle 1500
Check 'Collections (no server views): the Collections page, lit' ([bool]$tbOpened -and (TbOnCollectionsPage)) "marked: $(TbMarked)"
Open-Nav 'Libraries'
$tbLit = Wait-Until { if ((TbMarked) -eq 'Libraries') { $true } } -Seconds 15 -PollMs 200
TbSettle 1500
Check 'All libraries from there: Libraries lit' ([bool]$tbLit) "marked: $(TbMarked)"
# and Collections again (the review of 1.6: on topbar's interim route the frame would not show the page it shows, so this did nothing)
[void](Invoke-Element (TbTab 'Collections'))
$tbLit = Wait-Until { if ((TbMarked) -eq 'Collections') { $true } } -Seconds 10 -PollMs 200
TbSettle 1000
Check 'Collections again from there: Collections lit, the Collections page' ([bool]$tbLit -and (TbOnCollectionsPage)) "marked: $(TbMarked)"
Open-Nav 'Libraries'
[void](Wait-Until { if ((TbMarked) -eq 'Libraries') { $true } } -Seconds 10 -PollMs 200)
TbSettle 1000

# ---- the Libraries page's stars
[void](Wait-Until { if (@(TbStars | Where-Object { -not (TbInBar $_) }).Count -ge 9) { $true } } -Seconds 15 -PollMs 200)
$tbPage = @(TbStars | Where-Object { -not (TbInBar $_) })
Check 'Libraries page: a star on each of the 9 media tiles, lit for the favourites' ($tbPage.Count -eq 9 -and @($tbPage | Where-Object { $_.Current.Name -like 'Remove *' }).Count -eq 3) (@($tbPage | ForEach-Object { $_.Current.Name }) -join '; ')
Shot "$tbTag-libraries-1920"
[void](Invoke-Element (TbStar 'Kids and Family')); TbSettle 1200
Check 'Libraries page: a star click removes, the tile is not opened' (-not (TbOn 'Kids and Family') -and (TbMarked) -eq 'Libraries' -and [bool](Find-Ui -Name 'Libraries' -Type 'Text')) "marked: $(TbMarked); star: $((TbStar 'Kids and Family').Current.Name)"
[void](Invoke-Element (TbStar 'Classics')); TbSettle 800
[void](Invoke-Element (TbStar 'Anime Club')); TbSettle 1000
$tbNote = TbNote
Check 'Libraries page: a fourth star is refused, said on the page' ((TbOn 'Classics') -and -not (TbOn 'Anime Club') -and [bool]$tbNote) "note: $(if ($tbNote) { $tbNote.Current.Name })"
Shot "$tbTag-libraries-full-1920"
$tbFav = @('Movies', 'Concerts', 'Classics')
Check 'Libraries page: the bar follows' (((TbRow) -join '|') -eq "Home|$($tbFav -join '|')|Collections|Libraries") (TbNames)
# the keyboard: a tile's menu (Shift+F10) says what its star does
$tbTile = [OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($tbUia::ClassNameProperty, 'Microsoft.UI.Xaml.Controls.ItemContainer'))) | Where-Object { $_.Current.Name -like 'Concerts*' } | Select-Object -First 1
Check 'Libraries page: Narrator''s tile name says it is in the top bar' ([bool]$tbTile -and $tbTile.Current.Name -like '*, in the top bar') "tile: $(if ($tbTile) { $tbTile.Current.Name })"
if ($tbTile) {
    [void][OffscreenUi]::SetFocus($tbTile); TbSettle 400
    Key 0x79 -Shift; TbSettle 900
    $tbMi = Find-Ui -Name 'Remove Concerts from the top bar' -Type 'MenuItem'
    if (-not $tbMi) { Say 'Shift+F10 opened no menu; the Menu key'; Key 0x5D; TbSettle 900; $tbMi = Find-Ui -Name 'Remove Concerts from the top bar' -Type 'MenuItem' }
    Check 'Libraries page (keyboard): Shift+F10 on a tile offers "Remove Concerts from the top bar"' ([bool]$tbMi) ''
    if ($tbMi) { Key 0x0D; TbSettle 1000 }
    Check 'Libraries page (keyboard): Enter removes it' (-not (TbOn 'Concerts')) "star: $((TbStar 'Concerts').Current.Name)"
    # Tab from the tile: where the keyboard goes (the star, or out of the list)
    [void][OffscreenUi]::SetFocus($tbTile); TbSettle 300
    Key 0x09; TbSettle 400
    $tbF = TbFocused
    Say "Libraries page: Tab from a tile goes to '$(if ($tbF) { $tbF.Current.Name })'"
}
# R keeps the stars
Key 0x52; TbSettle 2500
Check 'Libraries page: R keeps the stars' ((TbOn 'Movies') -and (TbOn 'Classics') -and -not (TbOn 'Concerts')) (@(TbStars | Where-Object { $_.Current.Name -like 'Remove *' } | ForEach-Object { $_.Current.Name }) -join '; ')

# ---- the Diary: in the account menu; it lights the account, no tab
$tbAcct = TbAccount
if ($tbAcct) { [void](Invoke-Element $tbAcct) }
$tbDiary = Wait-Ui -Name 'Diary' -Type 'Button' -Seconds 8
Check 'account menu: the Diary is in it' ([bool]$tbDiary) ''
Shot "$tbTag-account-menu-1920"
Key 0x1B; TbSettle 800
Open-Nav 'Diary'
$tbLit = Wait-Until { if ((TbAccount).Current.ItemStatus -eq 'Diary is open') { $true } } -Seconds 15 -PollMs 200
TbSettle 1200
Check 'Diary: opened from the account menu, the account lit ("Diary is open"), no tab' ([bool]$tbLit -and -not (TbMarked)) "account: '$((TbAccount).Current.ItemStatus)'; marked: '$(TbMarked)'"
Shot "$tbTag-diary-1920"
Open-Nav 'Home'
TbSettle 1000
Check 'Home: the account no longer lit' (-not (TbAccount).Current.ItemStatus -and (TbMarked) -eq 'Home') "account: '$((TbAccount).Current.ItemStatus)'"
