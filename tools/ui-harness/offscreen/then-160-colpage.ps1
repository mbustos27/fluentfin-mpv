# 1.6 colpage: the Collection page (a box set, a playlist), the Libraries page and a library, in one theme per run
# (NIXIE_LAYOUT; NIXIE_THEME=Light for the light shots). Run with FAKESERVER_COLLECTIONS=1 (FAKESERVER_LONG_TITLE=1 for
# 'long') and `-Script wait:1 -NoA11y -Then offscreen\then-160-colpage.ps1`. C160_ONLY picks parts (collection, long,
# playlist, libraries, library, list); C160_SIZES the widths (default 1920,1100); SG_SHOT_TAG names the shots. Checks what the user sees: the
# cards' places in their captions (not on the art), no "6 films" heading at rest, the title clear of the bar (its top at the
# theme's title y), the presets strip and the Filter count. Dot-sourced by drive.ps1: no variable here may reuse one of its
# names (c160 prefix).
$c160Tag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
$c160Only = if ($env:C160_ONLY) { $env:C160_ONLY -split ',' } else { @('collection', 'playlist', 'libraries', 'library') }
$c160Sizes = if ($env:C160_SIZES) { @($env:C160_SIZES -split ',' | ForEach-Object { [int]$_ }) } else { @(1920, 1100) }
function C160-Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
function C160-Window { $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r); $r }
function C160-Top($Element) { if (-not $Element) { return -1 }; [int]($Element.Current.BoundingRectangle.Y - (C160-Window).T) }
function C160-Focus {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $f = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if ($f) { $f.Current.Name } else { '' }
}
function C160-Sizes([scriptblock]$Each) {
    foreach ($w in $c160Sizes) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $w, $(if ($w -ge 1900) { 1080 } else { 900 })); C160-Settle 1800
        & $Each $w
    }
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); C160-Settle 1000
}
# the focused element itself (its rectangle too)
function C160-FocusElement {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
}
# the review's Major 1: the header stage is pictures only; nothing of it reaches Narrator: no text, image, list or item in the
# stage's region (right of 62 % of the window, under the bar, above the toolbar's Order button)
function C160-StageExposed {
    $w = C160-Window
    $order = Find-Ui -Name 'Order: ' -Type 'Button' -Contains
    $foot = if ($order) { $order.Current.BoundingRectangle.Y - $w.T - 4 } else { 420 }
    $found = @()
    foreach ($type in @('Text', 'Image', 'List', 'ListItem', 'Group')) {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::$type)
        foreach ($e in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond))) {
            $b = $e.Current.BoundingRectangle
            if ($e.Current.IsOffscreen -or $b.Width -le 0) { continue }
            $x = $b.X - $w.L; $y = $b.Y - $w.T
            if ($x -gt ($w.R - $w.L) * 0.62 -and $y -gt 52 -and ($y + $b.Height) -lt $foot) { $found += "$type '$($e.Current.Name)'" }
        }
    }
    $found
}
$c160Layout = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'DriveIn' }
# the theme's title y (PageTitleY): the title's box starts there, 40 or more under the 48 px bar in every theme (L2, C4, LB4)
$c160TitleY = @{ Classic = 72; Cinematic = 72; DriveIn = 88; Editorial = 88 }[$c160Layout]

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); C160-Settle 1500

if ($c160Only -contains 'collection') {
    Open-Library 'Collections'
    C160-Settle 2000
    Open-Card 'Copper and Glass'
    $c160Play = Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 20
    C160-Settle 2500
    Check 'collection: opens with its Play' ([bool]$c160Play) $(if ($c160Play) { $c160Play.Current.Name })
    $c160Focus = C160-Focus
    Check 'collection: arriving, the keyboard is on Play' ($c160Focus.StartsWith('Play ')) $c160Focus
    # LB1: the place leads the card's name (its caption), "1, Wild Canyons, 1979"
    $c160First = Find-Ui -Name '1, Wild Canyons' -Type 'ListItem' -Contains
    Check 'collection: the first card says its place first ("1, Wild Canyons")' ([bool]$c160First) $(if ($c160First) { $c160First.Current.Name })
    # C2: no "6 films" heading over the grid at rest (the summary says it)
    Check 'collection: no "6 films" heading at rest' (-not (Find-Ui -Name '6 films' -Type 'Text'))
    $c160Title = Find-Ui -Name 'Copper and Glass' -Type 'Text'
    $c160T = C160-Top $c160Title
    Say "collection title top: $c160T (theme title y $c160TitleY)"
    Check 'collection: the title box sits at the theme''s title y or below (clear of the bar)' ($c160T -ge 48 + 24) "top $c160T"
    $c160Exposed = @(C160-StageExposed)
    Check 'collection: the header stage exposes nothing to Narrator (pictures only)' ($c160Exposed.Count -eq 0) "$($c160Exposed.Count): $(($c160Exposed | Select-Object -First 6) -join ', ')"
    C160-Sizes { param($w) Capture-Page "$c160Tag-collection-$w" }
    # Hide watched: the heading says what is shown; then back
    $c160Hide = Find-Ui -Name 'Hide watched' -Type 'CheckBox'
    if ($c160Hide) {
        try { ($c160Hide.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() } catch { }
        C160-Settle 1200
        Check 'collection: Hide watched says "5 of 6 films"' ([bool](Find-Ui -Name '5 of 6 films' -Type 'Text'))
        Shot "$c160Tag-collection-hide-watched-1920"
        try { ($c160Hide.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() } catch { }
        C160-Settle 1000
        Check 'collection: ... unticked, the heading goes' (-not (Find-Ui -Name '6 films' -Type 'Text'))
    }
    # Order: Name: no places in the captions; back to the collection's order: places again
    $c160Order = Find-Ui -Name 'Order: Collection' -Type 'Button' -Contains
    if ($c160Order) {
        [void](Invoke-Element $c160Order); C160-Settle 700
        $mi = Find-Ui -Name 'Name' -Type 'MenuItem'; if ($mi) { [void](Invoke-Element $mi) }; C160-Settle 1200
        Check 'collection: Order: Name leaves the captions without places' (-not (Find-Ui -Name '1, ' -Type 'ListItem' -Contains) -and [bool](Find-MediaCard 'Wild Canyons'))
        [void](Invoke-Element (Find-Ui -Name 'Order: Name' -Type 'Button' -Contains)); C160-Settle 700
        $mi = Find-Ui -Name 'Collection order' -Type 'MenuItem'; if ($mi) { [void](Invoke-Element $mi) }; C160-Settle 1200
        Check 'collection: ... back to its own order, the places come back' ([bool](Find-Ui -Name '1, Wild Canyons' -Type 'ListItem' -Contains))
    }
    # R refreshes in place; the header stays
    Key 0x52; C160-Settle 2500
    Check 'collection: R keeps the page (title, Play, the first card)' ([bool](Find-Ui -Name 'Play ' -Type 'Button' -Contains) -and [bool](Find-Ui -Name '1, Wild Canyons' -Type 'ListItem' -Contains))
}

if ($c160Only -contains 'long') {
    # FAKESERVER_LONG_TITLE=1: a box set whose name runs two lines (the header's wrap, the marquee, the stage beside it)
    Open-Library 'Collections'
    C160-Settle 2000
    Open-Card 'The Extraordinarily Long Franchise of Copper, Glass and Every Sequel After'
    $c160Play = Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 20
    C160-Settle 2500
    Check 'long title: opens with its Play' ([bool]$c160Play)
    Check 'long title: Narrator hears the whole name' ([bool](Find-Ui -Name 'The Extraordinarily Long Franchise of Copper, Glass and Every Sequel After' -Type 'Text'))
    C160-Sizes { param($w) Capture-Page "$c160Tag-collection-long-$w" }
}

if ($c160Only -contains 'deep') {
    # FAKESERVER_LONG_TITLE=1's 24-film set, at 1100 x 760: the keyboard down to a deep row, Up through the grid, and Back to a
    # deep card: the focused card is never under the 48 px top bar (Cinematic's page runs under it: the review's Major 2)
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 760); C160-Settle 1500
    Open-Library 'Collections'
    C160-Settle 2000
    Open-Card 'Anthology of Twenty-Four'
    [void](Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 20)
    C160-Settle 2500
    $c160W = C160-Window
    $c160First = Find-Ui -Name '1, ' -Type 'ListItem' -Contains
    if ($c160First) { [void][OffscreenUi]::SetFocus($c160First); C160-Settle 500 }
    foreach ($i in 1..4) { Key 0x28; C160-Settle 450 }   # Down to the fifth row
    $c160Low = 99999
    foreach ($i in 1..4) {
        Key 0x26; C160-Settle 450                          # Up, a row at a time
        $f = C160-FocusElement
        if ($f) { $c160Low = [Math]::Min($c160Low, [int]($f.Current.BoundingRectangle.Y - $c160W.T)) }
    }
    Check 'deep: Up through the grid keeps the focused card clear of the bar (its top 56 or more down)' ($c160Low -ge 56) "lowest top $c160Low"
    foreach ($i in 1..4) { Key 0x28; C160-Settle 450 }
    $c160Deep = C160-FocusElement
    $c160DeepName = if ($c160Deep) { $c160Deep.Current.Name } else { '' }
    Key 0x0D
    C160-Settle 2500
    $c160Full = Find-Ui -Name 'Full details' -Type 'Button' -Contains
    if ($c160Full) { [void](Invoke-Element $c160Full); C160-Settle 2500 }
    $c160BackBtn = Find-Ui -Name 'Back' -Type 'Button'
    if ($c160BackBtn) { [void](Invoke-Element $c160BackBtn) }
    [void](Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 15)
    C160-Settle 3000
    $c160Back = C160-FocusElement
    $c160BackTop = if ($c160Back) { [int]($c160Back.Current.BoundingRectangle.Y - $c160W.T) } else { -1 }
    Check 'deep: Back to a deep card puts the keyboard on it, clear of the bar (its top 56 or more down)' ($c160Back -and $c160Back.Current.Name -eq $c160DeepName -and $c160BackTop -ge 56) "'$(if ($c160Back) { $c160Back.Current.Name })' (left '$c160DeepName') top $c160BackTop"
    Capture-Page "$c160Tag-collection-deep-back-1100"
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); C160-Settle 1000
}

if ($c160Only -contains 'playlist') {
    Open-Library 'Playlists'
    C160-Settle 2000
    Open-Card 'Rainy Sunday'
    $c160Pl = Wait-Ui -Name 'Order: Playlist' -Type 'Button' -Contains -Seconds 20
    C160-Settle 2500
    Check 'playlist: opens in its own order' ([bool]$c160Pl)
    Check 'playlist: its cards carry their place' ([bool](Find-Ui -Name '1, ' -Type 'ListItem' -Contains))
    C160-Sizes { param($w) Capture-Page "$c160Tag-playlist-$w" }
}

if ($c160Only -contains 'libraries') {
    Open-Nav 'Home'; C160-Settle 800
    Open-Nav 'Libraries'
    C160-Settle 2500
    $c160T = C160-Top (Find-Ui -Name 'Libraries' -Type 'Text')
    Say "libraries title top: $c160T"
    Check 'libraries: the title box sits clear of the bar' ($c160T -ge 48 + 24) "top $c160T"
    C160-Sizes { param($w) Capture-Page "$c160Tag-libraries-$w" }
}

if ($c160Only -contains 'library') {
    Open-Library 'Movies'
    C160-Settle 3000
    C160-Sizes { param($w) Capture-Page "$c160Tag-library-$w" }
    # the presets strip: Unwatched on, then off (every theme has the strip; the toolbar has no Unwatched switch)
    $c160Unw = Find-Ui -Name 'Unwatched' -Type 'Button' -Contains
    if (-not $c160Unw) { $c160Unw = Find-Ui -Name 'Unwatched' -Type 'ListItem' -Contains }
    if (-not $c160Unw) { $c160Unw = Find-Ui -Name 'Unwatched' -Type 'TabItem' -Contains }
    Check 'library: the presets strip has Unwatched' ([bool]$c160Unw) $(if ($c160Unw) { "$($c160Unw.Current.ControlType.ProgrammaticName) $($c160Unw.Current.Name)" })
    if ($c160Unw) {
        [void](Invoke-Element $c160Unw); C160-Settle 1500
        Check 'library: Unwatched on shows the applied filter' ([bool](Find-Ui -Name 'Showing ' -Type 'Text' -Contains))
        $c160Filter = Find-Ui -Name 'Filter (' -Type 'Button' -Contains
        Check 'library: Filter''s count leaves out Unwatched (it has its own switch)' ($c160Filter -and $c160Filter.Current.Name -eq 'Filter (none on)') $(if ($c160Filter) { $c160Filter.Current.Name })
        C160-Sizes { param($w) Capture-Page "$c160Tag-library-unwatched-$w" }
        $c160All = Find-Ui -Name 'All' -Type 'Button' -Contains
        if (-not $c160All) { $c160All = Find-Ui -Name 'All' -Type 'TabItem' -Contains }
        if ($c160All) { [void](Invoke-Element $c160All); C160-Settle 1500 }
        Check 'library: All takes the filter off' (-not (Find-Ui -Name 'Showing ' -Type 'Text' -Contains))
    }
}

if ($c160Only -contains 'presets12') {
    # NIXIE_PRESETS=12 (the harness's stand-in source, answering 1.5 s late): the four presets and 12 Nixie collections at
    # 1100: they come in the same visit, the strip scrolls sideways, and End puts the last in view with the keyboard on it
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 800); C160-Settle 1500
    Open-Library 'Movies'
    C160-Settle 4000
    $c160W = C160-Window
    $c160Nixie = @()
    foreach ($type in @('Button', 'TabItem')) {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::$type)
        $c160Nixie += @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { "$($_.Current.Name)".Contains(', a Nixie collection') })
    }
    Check 'presets12: the 12 Nixie collections join the strip in the same visit (answered late)' ($c160Nixie.Count -eq 12) "$($c160Nixie.Count)"
    $c160All = Find-Ui -Name 'All titles' -Contains
    if ($c160All) { [void][OffscreenUi]::SetFocus($c160All); C160-Settle 400 }
    Capture-Page "$c160Tag-presets12-1100"
    # 1.7.3 fix round (BH-173-m4): HarnessPresetSource's last chip is 1.7.3's kind of name ("Directed by Kestrel" went with the
    # director collections)
    Key 0x23; C160-Settle 800                              # End: the last preset
    $f = C160-FocusElement
    $b = if ($f) { $f.Current.BoundingRectangle } else { $null }
    Check 'presets12: End puts the keyboard on the last preset, in view' ($f -and $f.Current.Name -like 'Based on a true story*' -and $b.X -ge $c160W.L -and $b.Right -le $c160W.R) "$(if ($f) { "'$($f.Current.Name)' x $([int]($b.X - $c160W.L))..$([int]($b.Right - $c160W.L)) of $([int]($c160W.R - $c160W.L))" })"
    Capture-Page "$c160Tag-presets12-end-1100"
    Key 0x24; C160-Settle 600                              # Home: back to All
    $f = C160-FocusElement
    Check 'presets12: Home goes back to All' ($f -and $f.Current.Name -like 'All titles*') "$(if ($f) { $f.Current.Name })"
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); C160-Settle 1000
}

if ($c160Only -contains 'list') {
    # Editorial's list view: View > List, kept per library
    Open-Library 'Movies'
    C160-Settle 3000
    $c160View = Find-Ui -Name 'View:' -Type 'Button' -Contains
    if ($c160View) {
        [void](Invoke-Element $c160View); C160-Settle 700
        $mi = Find-Ui -Name 'List' -Type 'MenuItem'
        Check 'list: View offers a list' ([bool]$mi)
        if ($mi) { [void](Invoke-Element $mi) }
        C160-Settle 2000
        Check 'list: the View button says so' ([bool](Find-Ui -Name 'View: list' -Type 'Button' -Contains))
        C160-Sizes { param($w) Capture-Page "$c160Tag-library-list-$w" }
    }
}
