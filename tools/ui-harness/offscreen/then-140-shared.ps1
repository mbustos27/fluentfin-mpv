# Nixie 1.4.0 (the shared stream): the shared Collection page, a playlist, "Part of" on a film page, the Libraries page, and
# where each page's title sits (the page grid: the title's top and the content's left and right edges, measured from UI
# Automation, logged as "grid <page>: ..."). Run with FAKESERVER_COLLECTIONS=1 and -Script wait:1 -Then
# offscreen\then-140-shared.ps1, once per NIXIE_LAYOUT; SG_SHOT_TAG names the shots. Dot-sourced by drive.ps1: no variable
# here may reuse one of its names.
$s14Tag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
$s14Only = if ($env:S14_ONLY) { $env:S14_ONLY -split ',' } else { @('collection', 'playlist', 'partof', 'libraries', 'grid') }
function S14-Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
# the element with the keyboard focus in FluentFin's window, by its name (UI Automation's FocusedElement does not cross desktops)
function S14-FocusName {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $f = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if ($f) { $f.Current.Name } else { '' }
}
function S14-Window { $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r); $r }
# an element's rectangle in the window ("x,y wxh"), its top relative to the window's top
function S14-Rect($Element) {
    if (-not $Element) { return 'none' }
    $w = S14-Window
    $b = $Element.Current.BoundingRectangle
    "x {0} y {1} w {2} h {3} (right {4})" -f [int]($b.X - $w.L), [int]($b.Y - $w.T), [int]$b.Width, [int]$b.Height, [int]($w.R - $b.Right)
}
# the page's title: the first text on screen with that name (or containing it), the topmost
function S14-Grid([string]$Page, [string]$Title, [switch]$Contains) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $h = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { -not $_.Current.IsOffscreen -and ($_.Current.BoundingRectangle.Y - (S14-Window).T) -gt 52 -and $(if ($Contains) { $_.Current.Name.IndexOf($Title, [StringComparison]::OrdinalIgnoreCase) -ge 0 } else { $_.Current.Name -eq $Title }) } |
        Sort-Object { $_.Current.BoundingRectangle.Y } | Select-Object -First 1
    Say "grid ${Page}: title '$(if ($h) { $h.Current.Name })' $(S14-Rect $h)"
}
function S14-Sizes([scriptblock]$Each) {
    foreach ($size in @(@(1920, 1080), @(1100, 900))) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); S14-Settle 1600
        & $Each $size[0]
    }
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); S14-Settle 1500

if ($s14Only -contains 'collection') {
    Open-Library 'Collections'
    S14-Settle 2000
    Open-Card 'Copper and Glass'
    $s14Play = Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 20
    S14-Settle 1500
    Check 'collection: the page opens with its Play (the first unwatched film)' ([bool]$s14Play) $(if ($s14Play) { $s14Play.Current.Name })
    $s14Order = Find-Ui -Name 'Order: Collection' -Type 'Button' -Contains
    Check 'collection: Order: Collection by default' ([bool]$s14Order)
    $s14Summary = Find-Ui -Name ' films · ' -Type 'Text' -Contains
    Say "collection summary: $(if ($s14Summary) { $s14Summary.Current.Name })"
    $s14List = Find-Ui -Name 'Copper and Glass, collection' -Type 'List'
    Say "collection list: $(S14-Rect $s14List); order button: $(S14-Rect $s14Order)"
    Check 'collection: no A to Z for six films' (-not (Find-Ui -Name 'Jump to a letter' -Type 'List'))
    # keyboard first: arriving, the focus is on the page's Primary; Tab goes on through the page in reading order
    $s14Focus = S14-FocusName
    Check 'collection: arriving, the focus is on Play' ($s14Focus.StartsWith('Play ')) $s14Focus
    $s14Tabs = @()
    foreach ($i in 1..9) {
        Key 0x09; Start-Sleep -Milliseconds 300
        $s14Tabs += S14-FocusName
        if ($s14Tabs[-1] -match '^(Wild Canyons|Painted Forests)') { break }
    }
    Say "collection Tab order: $($s14Tabs -join ' | ')"
    Check 'collection: Tab reaches Shuffle, the toggles, Hide watched, Order, then a film' ($s14Tabs[0] -eq 'Shuffle' -and ($s14Tabs -match '^Order: ') -and ($s14Tabs -match '^(Wild Canyons|Painted Forests)')) ($s14Tabs -join ' | ')
    # Enter on the focused film opens it; Back comes back to the collection
    Key 0x0D
    $s14Movie = Wait-ItemPage 'Wild Canyons' -Seconds 15
    Check 'collection: Enter on the first film opens it' ([bool]$s14Movie)
    $s14BackBtn = Find-Ui -Name 'Back' -Type 'Button'
    if ($s14BackBtn) { [void](Invoke-Element $s14BackBtn) }
    $s14Again = Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 15
    S14-Settle 1200
    Check 'collection: Back from the film shows the collection again, with its Play' ([bool]$s14Again -and [bool](Find-Ui -Name 'Copper and Glass, collection' -Type 'List'))
    # R refreshes it in place
    Key 0x52
    S14-Settle 2500
    Check 'collection: R refreshes it in place (title, Play and films still there)' ([bool](Find-Ui -Name 'Play ' -Type 'Button' -Contains) -and [bool](Find-MediaCard 'The Silent Letter'))
    S14-Sizes { param($w) S14-Grid "collection-$w" 'Copper and Glass'; Capture-Page "$s14Tag-collection-$w" }
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); S14-Settle 1200
    $s14Order = Find-Ui -Name 'Order: Collection' -Type 'Button' -Contains
    if ($s14Order) { [void](Invoke-Element $s14Order); S14-Settle 800; Shot "$s14Tag-collection-order-menu-1920"; Key 0x1B; S14-Settle 500 }
    $s14Hide = Find-Ui -Name 'Hide watched' -Type 'CheckBox'
    if ($s14Hide) {
        try { ($s14Hide.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() } catch { }
        S14-Settle 1000; Shot "$s14Tag-collection-hide-watched-1920"
        Check 'collection: Hide watched hides the watched film (and says how many are shown)' (-not (Find-MediaCard 'The Silent Letter') -and [bool](Find-Ui -Name '5 of 6 films' -Type 'Text'))
        try { ($s14Hide.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() } catch { }
        S14-Settle 800
        Check 'collection: ... and unticked, it is back' ([bool](Find-MediaCard 'The Silent Letter') -and [bool](Find-Ui -Name '6 films' -Type 'Text'))
    }
    # an order picked, then the collection's own order again (the way back)
    $s14Order = Find-Ui -Name 'Order: Collection' -Type 'Button' -Contains
    if ($s14Order) {
        [void](Invoke-Element $s14Order); S14-Settle 600
        $mi = Find-Ui -Name 'Name' -Type 'MenuItem'; if ($mi) { [void](Invoke-Element $mi) }; S14-Settle 900
        Check 'collection: Order: Name' ([bool](Find-Ui -Name 'Order: Name' -Type 'Button' -Contains))
        Shot "$s14Tag-collection-order-name-1920"
        [void](Invoke-Element (Find-Ui -Name 'Order: Name' -Type 'Button' -Contains)); S14-Settle 600
        $mi = Find-Ui -Name 'Collection order' -Type 'MenuItem'; if ($mi) { [void](Invoke-Element $mi) }; S14-Settle 900
        Check 'collection: ... and back to Order: Collection' ([bool](Find-Ui -Name 'Order: Collection' -Type 'Button' -Contains))
    }
}

if ($s14Only -contains 'partof') {
    if (-not (Find-Ui -Name 'Copper and Glass, collection' -Type 'List')) { Open-Library 'Collections'; S14-Settle 2000; Open-Card 'Copper and Glass'; S14-Settle 2500 }
    Open-Card 'Wild Canyons'
    $s14Link = Wait-Ui -Name 'Part of the collection Copper and Glass' -Seconds 15
    # (the shared movie page, Classic's, has the link; each layout theme's own movie page takes one line at integration)
    if (-not $env:NIXIE_LAYOUT -or $env:NIXIE_LAYOUT -eq 'Classic') { Check 'part of: the film page links to its collection' ([bool]$s14Link) }
    else { Say "part of: $(if ($s14Link) { 'shown' } else { 'not on this theme''s movie page yet (PartOfLink to wire at integration)' })" }
    S14-Settle 1200
    Shot "$s14Tag-movie-part-of-1920"
    if ($s14Link) {
        [void](Invoke-Element $s14Link)
        $s14Back = Wait-Ui -Name 'Copper and Glass, collection' -Type 'List' -Seconds 15
        Check 'part of: the link opens the collection' ([bool]$s14Back)
    }
}

if ($s14Only -contains 'playlist') {
    Open-Library 'Playlists'
    S14-Settle 2000
    Open-Card 'Rainy Sunday'
    $s14Pl = Wait-Ui -Name 'Order: Playlist' -Type 'Button' -Contains -Seconds 20
    Check 'playlist: opens as an ordered list in its own order' ([bool]$s14Pl)
    S14-Settle 1500
    Shot "$s14Tag-playlist-1920"
}

if ($s14Only -contains 'libraries') {
    Open-Nav 'Home'; S14-Settle 800
    Open-Nav 'Libraries'
    S14-Settle 2500
    S14-Sizes { param($w) S14-Grid "libraries-$w" 'Libraries'; Capture-Page "$s14Tag-libraries-$w" }
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); S14-Settle 1200
}

if ($s14Only -contains 'grid') {
    Open-Library 'Movies'
    S14-Settle 3000
    S14-Sizes { param($w) S14-Grid "library-$w" 'Movies' -Contains; Shot "$s14Tag-library-$w" }
    Open-Nav 'Settings'
    S14-Settle 2500
    S14-Sizes { param($w) S14-Grid "settings-$w" 'Settings'; Shot "$s14Tag-settings-$w" }
    $s14Box = Find-Ui -Name 'Search' -Type 'Edit' -Contains
    if ($s14Box) {
        [void][OffscreenUi]::SetFocus($s14Box); S14-Settle 400
        try { ($s14Box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('the') } catch { }
        S14-Settle 600
        Key 0x0D
        [void](Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15)
        S14-Settle 2500
        S14-Sizes { param($w) S14-Grid "search-$w" 'Results for' -Contains; Shot "$s14Tag-search-$w" }
    }
}
