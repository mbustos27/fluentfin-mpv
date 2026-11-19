# Nixie 1.4.0 (the top bar: fold by width, one lit destination, its geometry), live: -Script wait:1 -Then
# offscreen\then-topbar140.ps1, with FAKESERVER_COLLECTIONS=1, FAKESERVER_BIG_LIBRARY=<n> and FAKESERVER_EXTRA_VIEWS=<n> for
# 5, 6 or 9 views (NIXIE_LAYOUT the theme, SG_SHOT_TAG names the shots). Checks, at 2560, 1920, 1440 and 1100 px: the bar's
# items, Back and the caption buttons on y 24; Back 40 x 32, 16 px in; the search box centred in the window or 8 px from the
# account; a drag gap between the tabs and search; the libraries folded from the end only when they do not fit, under a tab
# named after the lit library. Then, keyboard first and mouse: the Libraries menu (All libraries first, the lit library
# checked, Esc out with the keyboard on the tab, a choice puts the keyboard in the page); the lit library across resizes;
# the trail rule (a film keeps its library lit, Back restores it, search results and an item from them light the search
# box, Settings the account, Home and the wordmark Home); the lit tab chosen again back at its top; R keeps the light; no
# focus on the TitleBar after opening a film. Every variable starts with $tt (dot-sourced by drive.ps1).
$ttTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'tb140' }
function TtSettle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
# a resize, then the bar laid out at it: the client this wide (less the frame) and the tabs' row steady
function TtSize([int]$W, [int]$H) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H)
    [void](Wait-Until { if ([Math]::Abs((TtWin).Width - $W) -le 24) { $true } } -Seconds 10 -PollMs 200)
    $last = ''; $same = 0
    for ($i = 0; $i -lt 30 -and $same -lt 3; $i++) { TtSettle 400; $now = "$(TtNames)|$([int]((TtBox).Current.BoundingRectangle.Left))"; if ($now -eq $last) { $same++ } else { $same = 0; $last = $now } }
    TtSettle 600
}
if (-not ('TtW' -as [type])) {
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class TtW {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  // the window's client area on the screen: left, top, width, height (UI Automation's window box has the frame in it)
  public static int[] Client(IntPtr h) { RECT r; GetClientRect(h, out r); var p = new POINT(); ClientToScreen(h, ref p); return new[] { p.X, p.Y, r.R - r.L, r.B - r.T }; }
}
"@
}
function TtWin { $c = [TtW]::Client($script:hwnd); [pscustomobject]@{ Left = $c[0]; Top = $c[1]; Width = $c[2]; Height = $c[3] } }
$ttUia = [System.Windows.Automation.AutomationElement]
function TtPane { [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id) }
function TtTabItems {
    $p = TtPane
    if (-not $p) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($ttUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    @($p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })
}
function TtSelected($e) { $sp = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Current.IsSelected } else { $false } }
function TtMarked { @(TtTabItems | Where-Object { TtSelected $_ } | ForEach-Object { $_.Current.Name }) -join ',' }
function TtFolded { TtTabItems | Where-Object { $_.Current.HelpText -like 'Libraries menu*' } | Select-Object -First 1 }
function TtNames { @(TtTabItems | ForEach-Object { $_.Current.Name }) -join ',' }
function TtFocused {
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($ttUia::HasKeyboardFocusProperty, $true))) |
        Where-Object { $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window }) | Select-Object -Last 1
}
function TtInBar($e) { if (-not $e) { return $true }; $r = $e.Current.BoundingRectangle; ($r.Top - (TtWin).Top) -lt 50 }
function TtBox { Find-Ui -Name 'Search' -Type 'Edit' -Contains }
function TtAccount { Find-Ui -Name 'Account, ' -Type 'Button' -Contains }
function TtMenuItems { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($ttUia::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem))) | Where-Object { $_.Current.ClassName -like '*MenuFlyoutItem' }) }
function TtChecked($e) {
    $p = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { return $p.Current.ToggleState -eq 'On' }
    if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { return $p.Current.IsSelected }
    $false
}
function TtScrollPercent {
    $best = $null; $area = 0
    foreach ($e in [OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($ttUia::IsScrollPatternAvailableProperty, $true)))) {
        try {
            $r = $e.Current.BoundingRectangle; $sp = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($sp.Current.VerticallyScrollable -and $r.Width * $r.Height -gt $area) { $area = $r.Width * $r.Height; $best = $sp }
        } catch { }
    }
    if ($best) { $best.Current.VerticalScrollPercent } else { -1 }
}
function TtCy($r, $win) { [Math]::Round($r.Top + $r.Height / 2 - $win.Top, 1) }

# ---- the bar at a width: geometry, the fold, a shot
$ttWidths = [ordered]@{}
function TtBar([int]$W, [int]$H) {
    TtSize $W $H
    $win = TtWin
    $tabs = TtTabItems
    $compact = [bool](Find-Ui -Id 'TopTabsMenu')
    $box = TtBox; $icon = Find-Ui -Name 'Search' -Type 'Button'
    $acct = TtAccount
    $back = @(Find-Ui -Name 'Back' -Type 'Button') | Where-Object { ([OffscreenUi]::RectOf($_).Top - $win.Top) -lt 48 } | Select-Object -First 1
    $close = Find-Ui -Name 'Close' -Type 'Button'
    $folded = TtFolded
    $info = [ordered]@{ width = $W; compact = $compact; tabs = (TtNames); folded = $(if ($folded) { $folded.Current.Name }); marked = (TtMarked) }
    $cys = [ordered]@{}
    foreach ($t in $tabs) { $cys["tab:$($t.Current.Name)"] = TtCy $t.Current.BoundingRectangle $win }
    if ($box) { $cys.search = TtCy $box.Current.BoundingRectangle $win }
    if ($icon) { $cys.searchIcon = TtCy $icon.Current.BoundingRectangle $win }
    if ($acct) { $cys.account = TtCy $acct.Current.BoundingRectangle $win }
    if ($back) { $cys.back = TtCy $back.Current.BoundingRectangle $win }
    if ($close) { $cys.close = TtCy $close.Current.BoundingRectangle $win }
    $off = @($cys.GetEnumerator() | Where-Object { [Math]::Abs($_.Value - 24) -gt 1.5 })
    Check "bar $($W): every item, Back and the caption buttons centred on y 24" ($off.Count -eq 0 -and $close) (($cys.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')
    if ($back) {
        $br = $back.Current.BoundingRectangle
        Check "bar $($W): Back is 40 x 32, 16 px from the window's edge" ([Math]::Abs($br.Left - $win.Left - 16) -le 1 -and [Math]::Abs($br.Width - 40) -le 1 -and [Math]::Abs($br.Height - 32) -le 1) "Back at $($br.Left - $win.Left), $($br.Width) x $($br.Height)"
    }
    if ($box -and $acct -and $tabs.Count) {
        $sr = $box.Current.BoundingRectangle; $ar = $acct.Current.BoundingRectangle
        $lastTab = ($tabs | ForEach-Object { $_.Current.BoundingRectangle.Right } | Measure-Object -Maximum).Maximum
        $centred = [Math]::Abs(($sr.Left + $sr.Width / 2) - ($win.Left + $win.Width / 2)) -le 2
        $pinned = [Math]::Abs($ar.Left - $sr.Right - 8) -le 1.5
        $info.search = "x $([int]($sr.Left - $win.Left)) w $([int]$sr.Width) $(if ($centred) { 'centred' } elseif ($pinned) { 'pinned' } else { 'neither' })"
        Check "bar $($W): the search box centred in the window, or 8 px from the account" ($centred -or $pinned) $info.search
        Check "bar $($W): a drag gap of 48 px or more between the tabs and the search box" (($sr.Left - $lastTab) -ge 47) "gap $([int]($sr.Left - $lastTab))"
    }
    $script:ttWidths["$W"] = $info
    Say ("bar $($W): " + (($info.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '; '))
    Shot "$ttTag-bar-$W"
}

TtSize 1920 1080
[void](Wait-Until { if ((TtTabItems).Count -ge 2) { $true } } -Seconds 20 -PollMs 200)
TtSettle 1500
$ttViews = @($fixture.views)
Say "views: $($ttViews -join ', ')"
foreach ($ttW in @(2560, 1920, 1440, 1100)) { TtBar $ttW $(if ($ttW -ge 1920) { 1080 } else { 900 }) }

# the fold: every view a tab while it fits; once folded, the shown libraries are the server's first ones
$ttAll = $ttWidths['2560']
Check 'fold: at 2560 every view is a tab, then Libraries' (-not $ttAll.compact -and -not $ttAll.folded -and ($ttViews | Where-Object { ($ttAll.tabs -split ',') -notcontains $_ }).Count -eq 0) $ttAll.tabs
$ttPrev = 99
foreach ($ttW in @('2560', '1920', '1440', '1100')) {
    $i = $ttWidths[$ttW]; $n = if ($i.compact) { -1 } else { @(($i.tabs -split ',') | Where-Object { $ttViews -contains $_ }).Count }
    Check "fold $($ttW): no more library tabs than in a wider window" ($n -le $ttPrev) "$n after $ttPrev"
    $ttPrev = $n
}

# ---- the Libraries menu, keyboard first (1100 px: folded when there are enough views)
TtSize 1100 900
$ttFoldedTab = TtFolded
if ($ttFoldedTab) {
    # the bar's order: the server's, Collections and Playlists last
    $ttOrder = @(@($ttViews | Where-Object { $_ -notin @('Collections', 'Playlists') }) + @($ttViews | Where-Object { $_ -in @('Collections', 'Playlists') }))
    $ttFolded = @($ttOrder | Where-Object { ((TtNames) -split ',') -notcontains $_ })
    [void][OffscreenUi]::SetFocus($ttFoldedTab); TtSettle 400
    Key 0x0D; TtSettle 900
    $ttItems = TtMenuItems
    $ttItemNames = @($ttItems | ForEach-Object { $_.Current.Name })
    Check 'menu (keyboard): Enter opens it: All libraries, then the folded libraries in order' ($ttItemNames.Count -ge 2 -and $ttItemNames[0] -eq 'All libraries' -and (($ttItemNames | Select-Object -Skip 1) -join ',') -eq ($ttFolded -join ',')) "$($ttItemNames -join ',') (expected All libraries,$($ttFolded -join ','))"
    Shot "$ttTag-menu-1100"
    Key 0x1B; TtSettle 600
    Check 'menu: Esc closes it, the keyboard back on the folded tab' ((TtMenuItems).Count -eq 0 -and (TtFocused).Current.HelpText -like 'Libraries menu*') "focused: $((TtFocused).Current.Name)"
    # again with the keyboard: Down twice and a repeat back and forth; the item the keyboard is on opens
    Key 0x0D; TtSettle 900
    Key 0x28; TtSettle 250; Key 0x28; TtSettle 250; Key 0x28; TtSettle 120; Key 0x26; TtSettle 400
    $ttOn = (@(TtMenuItems | Where-Object { $_.Current.HasKeyboardFocus }) | Select-Object -First 1)
    $ttLast = if ($ttOn) { $ttOn.Current.Name } else { '' }
    Say "menu: the keyboard is on '$ttLast'"
    Key 0x0D
    $ttOpened = if ($ttLast -and $ttLast -ne 'All libraries') { Wait-Ui -Name $ttLast -Type 'Text' -Seconds 15 } else { $null }
    TtSettle 1800
    $ttF = TtFocused
    Check 'menu (keyboard): the library the keyboard was on opens, the folded tab named after it and lit' ([bool]$ttOpened -and (TtMarked) -eq $ttLast -and (TtFolded).Current.Name -eq $ttLast) "on '$ttLast'; marked: $(TtMarked), folded tab: $((TtFolded).Current.Name)"
    Check 'menu (keyboard): the keyboard goes to the page, not back to the tab' ($ttF -and -not (TtInBar $ttF)) "focused: $($ttF.Current.Name) ($($ttF.Current.ClassName))"
    Shot "$ttTag-folded-lit-1100"
    # the menu checks the lit library; the mouse: choose another, the keyboard leaves the bar too
    [void](Invoke-Element (TtFolded)); TtSettle 900
    $ttLit = TtMenuItems | Where-Object { $_.Current.Name -eq $ttLast } | Select-Object -First 1
    Check 'menu: the lit library is checked, and only it' ([bool]$ttLit -and (TtChecked $ttLit) -and @(TtMenuItems | Where-Object { TtChecked $_ }).Count -eq 1) "checked: $(@(TtMenuItems | Where-Object { TtChecked $_ } | ForEach-Object { $_.Current.Name }) -join ',')"
    $ttOther = @($ttFolded | Where-Object { $_ -ne $ttLast })[-1]
    $ttOtherItem = TtMenuItems | Where-Object { $_.Current.Name -eq $ttOther } | Select-Object -First 1
    if (-not $ttOtherItem) { Say "menu items now: $(@(TtMenuItems | ForEach-Object { $_.Current.Name }) -join ',')" }
    if ($ttOtherItem) { [void](Invoke-Element $ttOtherItem) }
    $ttOpened2 = Wait-Ui -Name $ttOther -Type 'Text' -Seconds 15
    TtSettle 1500
    Check 'menu (mouse): another library opens and is lit' ([bool]$ttOpened2 -and (TtMarked) -eq $ttOther) "marked: $(TtMarked)"
    # the lit library chosen again in the menu, on its own page: no new page, still lit (back at its top)
    [void](Invoke-Element (TtFolded)); TtSettle 900
    $ttAgain = TtMenuItems | Where-Object { $_.Current.Name -eq $ttOther } | Select-Object -First 1
    if ($ttAgain) { [void](Invoke-Element $ttAgain) }
    TtSettle 2500
    Check 'menu: the lit library chosen again stays lit' ([bool]$ttAgain -and (TtMarked) -eq $ttOther -and [bool](Find-Ui -Name $ttOther -Type 'Text')) "marked: $(TtMarked)"
    # the lit library across the fold: its own tab when the window grows, the folded tab's name again when it shrinks
    TtSize 2560 1080
    Check 'resize: at 2560 the lit library is its own tab' ((TtMarked) -eq $ttOther -and -not (TtFolded)) "marked: $(TtMarked), tabs: $(TtNames)"
    TtSize 1100 900
    Check 'resize: back at 1100 the folded tab is named after it again' ((TtMarked) -eq $ttOther -and (TtFolded).Current.Name -eq $ttOther) "marked: $(TtMarked), tabs: $(TtNames)"
}
else {
    Say "1100 px: nothing folded ($(TtNames))"
}

# ---- the trail rule (1920)
TtSize 1920 1080
Open-Nav 'Movies'
[void](Wait-Ui -Name 'Movies' -Type 'Text' -Seconds 15); TtSettle 1500
Check 'trail: Movies lit on Movies' ((TtMarked) -eq 'Movies') (TtMarked)
# chosen again on its own page: its top, still lit
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 60); TtSettle 1500
$ttBefore = TtScrollPercent
if ($ttBefore -le 0) { Key 0x22; TtSettle 400; Key 0x22; TtSettle 400; Key 0x22; TtSettle 1200; $ttBefore = TtScrollPercent }
[void](Invoke-Element (TtTabItems | Where-Object { $_.Current.Name -eq 'Movies' } | Select-Object -First 1))
TtSettle 3000
$ttAfter = TtScrollPercent
Check 'trail: Movies chosen again on Movies goes back to its top, still lit' ($ttBefore -gt 0 -and $ttAfter -le 1 -and (TtMarked) -eq 'Movies') "scrolled to $ttBefore, then $ttAfter; marked $(TtMarked)"
# R: the light stays
Key 0x52; TtSettle 2500
Check 'trail: R refresh keeps Movies lit' ((TtMarked) -eq 'Movies') (TtMarked)
Open-Card $fixture.movie.name
$ttItem = Wait-ItemPage $fixture.movie.name -Seconds 20
TtSettle 1500
$ttF = TtFocused
Check 'trail: a film opened from Movies keeps Movies lit' ([bool]$ttItem -and (TtMarked) -eq 'Movies') (TtMarked)
Check 'focus: after opening a film the keyboard is not on the TitleBar' (-not $ttF -or $ttF.Current.ClassName -ne 'TitleBar') "focused: $($ttF.Current.Name) ($($ttF.Current.ClassName))"
Key 0xA6; TtSettle 2500
Check 'trail: Back to Movies, Movies lit' ((TtMarked) -eq 'Movies') (TtMarked)
Key 0xA7; TtSettle 2500
Check 'trail: Forward to the film, Movies still lit' ((TtMarked) -eq 'Movies') (TtMarked)
# search: its results and an item from them light the search box, no tab
Key 0xBF; TtSettle 500
$ttKw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
foreach ($ch in 'the'.ToCharArray()) { [void][TtW]::PostMessage($ttKw, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 80 }
TtSettle 1500
Key 0x0D
$ttResults = Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15
TtSettle 1500
Check 'trail: the search results light the search box, no tab' ([bool]$ttResults -and -not (TtMarked) -and (TtBox).Current.ItemStatus -eq 'Current') "marked: '$(TtMarked)', box: '$((TtBox).Current.ItemStatus)'"
Shot "$ttTag-search-lit-1920"
$ttCard = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($ttUia::ClassNameProperty, 'FluentFin.Controls.BaseItemDtoCard'))) | Where-Object { -not $_.Current.IsOffscreen }) | Select-Object -First 1
if ($ttCard) {
    [void](Invoke-Element $ttCard)
    [void](Wait-Until { if (-not (Find-Ui -Name 'Results for' -Type 'Text' -Contains)) { $true } } -Seconds 15 -PollMs 200)
    TtSettle 1500
    Check 'trail: an item from the results keeps the search box lit' (-not (TtMarked) -and (TtBox).Current.ItemStatus -eq 'Current') "marked: '$(TtMarked)', box: '$((TtBox).Current.ItemStatus)'"
}
# Settings: the account button
Open-Nav 'Settings'
[void](Wait-Ui -Name 'Appearance' -Type 'Text' -OffscreenToo -Seconds 10); TtSettle 1200
Check 'trail: Settings lights the account, no tab, not the search box' (-not (TtMarked) -and (TtAccount).Current.ItemStatus -eq 'Settings is open' -and -not (TtBox).Current.ItemStatus) "marked: '$(TtMarked)', account: '$((TtAccount).Current.ItemStatus)'"
Shot "$ttTag-settings-lit-1920"
# the wordmark: Home
$ttMark = Find-Ui -Name 'Home' -Type 'Button'
if ($ttMark) { [void](Invoke-Element $ttMark) }
$ttHome = Wait-Until { if ((TtMarked) -eq 'Home') { $true } } -Seconds 15 -PollMs 200
TtSettle 1200
Check 'wordmark: a click goes Home, Home lit, the account and box unlit' ([bool]$ttMark -and [bool]$ttHome -and -not (TtAccount).Current.ItemStatus) "marked: $(TtMarked)"
Shot "$ttTag-home-1920"
