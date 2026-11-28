# Nixie 1.9 "Collections 2" (the ui stream): what the user sees of the Collections page's Categories and Franchises rows and of
# Settings' Categories switch, in the theme of NIXIE_LAYOUT, at the sizes of GATE_SIZES (the c190 manifest: 1280x720,
# 1920x1080 and max, 2560 x 1392). Run with FAKESERVER_COLLECTIONS=1, FAKESERVER_FRANCHISES190=1 (twins "Harbor Watch" and
# the started "Zenith Lane") and FAKESERVER_BIG_LIBRARY=25000 (a real-sized library, whose genres and decades make categories):
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-190-collections.ps1
# At each size:
#   row       the Categories row: shown, the first of the kinds' rows (above Franchises), at most a dozen tiles and See all;
#             each tile's Narrator name "<name>, made by Nixie, category, ..."; the tiles one size, apart, their words inside
#   keys      the keyboard on its first tile, End: its last tile has the keyboard, on screen; Home: the first again
#   dpad      a gamepad's D-pad right from the first to the last tile: the last has the keyboard, on screen
#   back      a category opened from the row (Enter), Back: the page as it was, at once, the keyboard on that tile (then-174-back's
#             measure: no loading state, the scroll and the focus kept)
# Once, at the first size:
#   seeall    See all Categories: For you (the lineup's ranking, not A to Z), its order and direction buttons saying so; the order
#             button (Space) sorts them A to Z, and back; Back: the row again
#   franchise the Franchises row opens on the started "Zenith Lane"; See all Franchises runs A to Z and names the twins with
#             their years ("Harbor Watch (1958-1966)", "Harbor Watch (2015-2022)")
#   switch    Settings > Collections > Made by Nixie collections: a switch named "Categories" with its words; off (Space):
#             the Collections page has no Categories row and no category tile; on: the row is back
# Check names: "<GATE_ID, else c190> [<theme>, <size>]: ...". Dot-sourced by drive.ps1: every name here starts with c190 /
# C190-. ASCII only.

$env:BK_PAGES = 'none'
. (Join-Path $PSScriptRoot 'then-174-back.ps1')   # its helpers only (Bk-Take, Bk-Focus, Bk-OpenFocused, Bk-Measure)
. (Join-Path $PSScriptRoot '..\..\gate\gate-common.ps1')
$c190Id = if ($env:GATE_ID) { $env:GATE_ID } else { 'c190' }
$c190Theme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$c190Sizes = @(Get-GateSizes)
if ($c190Sizes.Count -eq 0) { $c190Sizes = @('1920x1080') }
$c190Dash = [string][char]0x2013
$c190A = [System.Windows.Automation.AutomationElement]
$c190Button = New-Object System.Windows.Automation.PropertyCondition($c190A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$c190Size = ''

function C190-Check([string]$What, [bool]$Ok, [string]$Detail = '') {
    $where = if ($script:c190Size) { "$c190Theme, $($script:c190Size)" } else { $c190Theme }
    Check "$c190Id [$where]: $What" $Ok $Detail
}
# the list of a row (ShelfRow names its list after the row's title), in or out of view
function C190-List([string]$Row) { Find-Ui -Name $Row -Type 'List' -OffscreenToo }
# a row's tiles (its list's buttons named "..., made by Nixie, ..."), left to right
function C190-Tiles([string]$Row) {
    $list = C190-List $Row
    if (-not $list) { return @() }
    @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c190Button) | Where-Object { try { $_.Current.Name -like '*, made by Nixie, *' } catch { $false } } |
        Sort-Object { $_.Current.BoundingRectangle.X })
}
function C190-Rect($Element) { try { $Element.Current.BoundingRectangle } catch { [System.Windows.Rect]::Empty } }
function C190-OnScreen($Element) {
    if (-not $Element) { return $false }
    try {
        $r = $Element.Current.BoundingRectangle
        $w = $c190A::FromHandle($script:hwnd).Current.BoundingRectangle
        -not $Element.Current.IsOffscreen -and $r.Width -gt 0 -and $r.Left -ge $w.Left - 1 -and $r.Right -le $w.Right + 1 -and $r.Bottom -le $w.Bottom + 1
    } catch { $false }
}
# the element with the keyboard's name (the tile, or one it is in)
function C190-FocusName { $f = Bk-Retry { Bk-Focus } 5 200; if ($f) { $f.Name } else { '' } }
# the page scrolled until a row's list is wholly in view (below the top bar)
function C190-ShowRow([string]$Row) {
    $w = $c190A::FromHandle($script:hwnd).Current.BoundingRectangle
    foreach ($pct in @(-1) + @(0..20 | ForEach-Object { $_ * 5 })) {
        if ($pct -ge 0) { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); Start-Sleep -Milliseconds 350 }
        $l = C190-List $Row
        if ($l) { $r = C190-Rect $l; if ($r.Height -gt 0 -and $r.Top -ge $w.Top + 56 -and $r.Bottom -le $w.Bottom - 2) { return $l } }
    }
    C190-List $Row
}
function C190-OpenCollections {
    Open-Nav 'Collections'
    [void](Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 20)
}
function C190-Log([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content $_.FullName -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}

# ---- the Categories row, at each size -------------------------------------------------------------------------------------------
C190-OpenCollections
# a 25,000-film first read: Made by Nixie's rows come with its pages; the row once the whole index is in (its line said)
$c190Deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $c190Deadline -and -not ((C190-Log 'Library index: \d+ titles in \d+ libraries').Count -and (C190-List 'Categories'))) { Start-Sleep -Milliseconds 700 }
Start-Sleep -Seconds 3
$c190Found = [bool](C190-List 'Categories')
C190-Check 'the Collections page shows a Categories row' $c190Found "$(C190-Log 'Made by Nixie census' | Select-Object -Last 1)"

foreach ($c190S in $c190Sizes) {
    if (-not $c190Found) { break }
    try { $c190State = Set-GateSize $c190S; $script:c190Size = $c190S; $script:bkSize = $c190S; $script:bkTag = "$($c190Theme.ToLowerInvariant())-$c190S" }
    catch { $script:c190Size = $c190S; C190-Check 'the window took the size' $false $_.Exception.Message; continue }
    $c190Said = Format-GateSize $c190State
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 600

    # row: shown, first of the kinds, a dozen, See all, Narrator names, drawn (the first tile given the keyboard, as Tab
    # leaves it: the row in view; UI Automation clips a row's rectangle to what shows, so "in view" is read from its tiles)
    $c190List = C190-ShowRow 'Categories'
    $c190Tiles = @(C190-Tiles 'Categories')
    if ($c190Tiles.Count) { Bk-Take $c190Tiles[0]; $c190Tiles = @(C190-Tiles 'Categories') }
    $c190Names = @($c190Tiles | ForEach-Object { $_.Current.Name })
    $c190Franchises = C190-List 'Franchises'
    $c190Above = $c190List -and (-not $c190Franchises -or (C190-Rect $c190List).Top -lt (C190-Rect $c190Franchises).Top)
    C190-Check 'the Categories row is above Franchises, with 1 to 12 tiles' ([bool]($c190Above -and $c190Tiles.Count -ge 1 -and $c190Tiles.Count -le 12)) "$($c190Tiles.Count) tiles; window $c190Said"
    $c190See = Find-Ui -Name 'See all Categories' -Contains -Type 'Hyperlink' -OffscreenToo
    C190-Check 'the row ends in See all Categories' ([bool]$c190See) "$(if ($c190See) { $c190See.Current.Name })"
    $c190Unnamed = @($c190Names | Where-Object { $_ -notmatch '^.+, made by Nixie, category, .+' })
    C190-Check 'Narrator hears each tile as "<name>, made by Nixie, category, <reason>"' ($c190Names.Count -gt 0 -and $c190Unnamed.Count -eq 0) "$($c190Names | Select-Object -First 2); off: $($c190Unnamed -join ' | ')"
    # drawn: one size, apart, the first on screen, each tile's words inside it
    $c190Rects = @($c190Tiles | ForEach-Object { C190-Rect $_ } | Where-Object { $_.Width -gt 0 })
    # (UI Automation clips a tile to the row's view: one cut by the row's edge, narrower than the widest, isn't a size)
    $c190View = C190-Rect (C190-List 'Categories')
    $c190Widest = ($c190Rects | Measure-Object -Property Width -Maximum).Maximum
    $c190Whole = @($c190Rects | Where-Object { -not ($_.Width -lt $c190Widest - 1 -and ($_.Right -ge $c190View.Right - 1 -or $_.Left -le $c190View.Left + 1)) })
    $c190Sizes1 = @($c190Whole | ForEach-Object { "{0:0}x{1:0}" -f $_.Width, $_.Height } | Select-Object -Unique)
    $c190Overlap = @(for ($i = 1; $i -lt $c190Rects.Count; $i++) { if ($c190Rects[$i].Left -lt $c190Rects[$i - 1].Right - 1) { $i } })
    $c190Spill = @()
    foreach ($t in @($c190Tiles | Where-Object { C190-OnScreen $_ })) {
        $tr = C190-Rect $t
        foreach ($x in @($t.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($c190A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))))) {
            $xr = C190-Rect $x
            if ($xr.Width -gt 0 -and ($xr.Left -lt $tr.Left - 1 -or $xr.Right -gt $tr.Right + 1 -or $xr.Bottom -gt $tr.Bottom + 1)) { $c190Spill += "'$($x.Current.Name)'" }
        }
    }
    C190-Check 'the tiles are one size, apart, the first on screen, their words inside them' ([bool]($c190Sizes1.Count -eq 1 -and $c190Overlap.Count -eq 0 -and $c190Spill.Count -eq 0 -and (C190-OnScreen $c190Tiles[0]))) "sizes $($c190Sizes1 -join ','); overlaps at $($c190Overlap -join ','); words outside: $($c190Spill -join ' ')"
    Shot (Safe-Name "c190-$c190Theme-$c190S-categories")

    if ($c190Tiles.Count -ge 2) {
        # the row's last tile is the one whose place is its set's size ("12 of 12": the row realises the tiles near view only)
        $c190First = Bk-Retry { Bk-Focus } 5 200
        $c190Count = if ($c190First -and $c190First.Size -gt 0) { $c190First.Size } else { $c190Tiles.Count }
        function C190-AtLast { $f = Bk-Focus; [bool]($f -and $f.Pos -eq $c190Count -and $f.Size -eq $c190Count) }
        function C190-SaidLast { $f = Bk-Retry { Bk-Focus } 5 200; if ($f) { "'$($f.Name)' $($f.Pos) of $($f.Size), on screen: $(C190-OnScreen $f.Element)" } else { 'no focus' } }
        # keys: End, then Home
        Key 0x23; [void](Wait-Until { if (C190-AtLast) { $true } } -Seconds 4 -PollMs 250); Start-Sleep -Milliseconds 700
        $c190EndOk = (C190-AtLast) -and (C190-OnScreen (Bk-Focus).Element)
        $c190EndSaid = C190-SaidLast
        Key 0x24; Start-Sleep -Milliseconds 900
        $c190Home = Bk-Retry { Bk-Focus } 5 200
        C190-Check 'End takes the keyboard to the last tile, on screen; Home back to the first' ([bool]($c190EndOk -and $c190Home -and $c190Home.Pos -eq 1)) "End: $c190EndSaid; Home: $(if ($c190Home) { "'$($c190Home.Name)' $($c190Home.Pos) of $($c190Home.Size)" })"
        # dpad: right to the last
        foreach ($i in 1..($c190Count - 1)) { Key 0xCE; Start-Sleep -Milliseconds 110 }
        [void](Wait-Until { if (C190-AtLast) { $true } } -Seconds 4 -PollMs 250); Start-Sleep -Milliseconds 700
        $c190PadOk = (C190-AtLast) -and (C190-OnScreen (Bk-Focus).Element)
        C190-Check 'a gamepad''s D-pad right takes the keyboard to the last tile, on screen' ([bool]$c190PadOk) "$($c190Count - 1) presses: $(C190-SaidLast)"
        Shot (Safe-Name "c190-$c190Theme-$c190S-categories-last")
    }

    # back: a category opened (Enter on its third tile), Back
    $c190Tiles = @(C190-Tiles 'Categories')
    if ($c190Tiles.Count -ge 3) {
        try {
            Bk-Take $c190Tiles[2]
            $c190Left = Bk-Focus; $c190V0 = Bk-V; $c190Px0 = Bk-Px
            Bk-OpenFocused
            if (-not (Wait-Ui -Id 'OrderButton' -Seconds 20)) { throw "the tile '$($c190Left.Name)' did not open" }
            Bk-Measure 'categories' $c190Left $c190V0 -Px0 $c190Px0
        }
        catch { C190-Check 'a category opened from the row, then Back' $false $_.Exception.Message }
    }
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 500
}

# ---- once, at the first size ------------------------------------------------------------------------------------------------------
if ($c190Found) {
    try { [void](Set-GateSize $c190Sizes[0]); $script:c190Size = $c190Sizes[0] } catch { }
    # seeall: every category, For you (the lineup's ranking, not A to Z), said so; the order button (keyboard) to A to Z and back;
    # Back: the row again
    # the grid's realised tiles in their order: by their place in the set ("3 of 104") where they say it, else on screen (top to
    # bottom, left to right), those not drawn left out (a recycled tile out of view keeps a stale or empty rectangle)
    function C190-GridNames {
        $grid = Find-Ui -Id 'GridList' -OffscreenToo
        if (-not $grid) { return @() }
        $tiles = @($grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c190Button) | Where-Object { $_.Current.Name -like '*, made by Nixie, category, *' } | ForEach-Object {
                [pscustomobject]@{ Name = ($_.Current.Name -split ', made by Nixie, ')[0]; Pos = [int]$_.GetCurrentPropertyValue([System.Windows.Automation.AutomationElementIdentifiers]::PositionInSetProperty)
                    Rect = $_.Current.BoundingRectangle; Off = $_.Current.IsOffscreen }
            })
        if ($tiles.Count -and -not @($tiles | Where-Object { $_.Pos -le 0 }).Count) { return @($tiles | Sort-Object Pos | ForEach-Object { $_.Name }) }
        @($tiles | Where-Object { -not $_.Off -and $_.Rect.Width -gt 0 } | Sort-Object { [Math]::Round($_.Rect.Y) }, { $_.Rect.X } | ForEach-Object { $_.Name })
    }
    $c190See = $null
    [void](C190-ShowRow 'Categories')
    $c190See = Find-Ui -Name 'See all Categories' -Contains -Type 'Hyperlink' -OffscreenToo
    if ($c190See) {
        [void](Invoke-Element $c190See)
        $c190Btn = Wait-Until { Find-Ui -Id 'GridDirectionButton' -OffscreenToo } -Seconds 20 -PollMs 300
        [void](Wait-Ui -Id 'TitleText' -Name 'Categories' -OffscreenToo -Seconds 15)
        Start-Sleep -Milliseconds 2000
        $c190GridNames = @(C190-GridNames)
        $c190Sorted = @($c190GridNames | Sort-Object)
        $c190Btn = Find-Ui -Id 'GridDirectionButton' -OffscreenToo
        $c190Order = Find-Ui -Id 'GridOrderButton' -OffscreenToo
        # (the grid realises the tiles in and near view: those are compared; the census says how many were made)
        C190-Check 'See all Categories opens For you (not A to Z), its order button says "For you, switch to A to Z"' ([bool]($c190GridNames.Count -ge 2 -and (($c190GridNames -join '|') -cne ($c190Sorted -join '|')) -and $c190Order -and $c190Order.Current.Name -eq 'For you, switch to A to Z' -and $c190Btn -and $c190Btn.Current.Name -eq 'Best fit first, switch to best fit last')) "$($c190GridNames.Count) realised; first $($c190GridNames | Select-Object -First 3); order '$(if ($c190Order) { $c190Order.Current.Name })'; direction '$(if ($c190Btn) { $c190Btn.Current.Name })'"
        if ($c190Order) {
            Bk-Take $c190Order
            Key 0x20   # Space on the order button
            $c190AToZ = Wait-Until { $o = Find-Ui -Id 'GridOrderButton' -OffscreenToo; if ($o -and $o.Current.Name -like 'A to Z*') { $o } } -Seconds 10 -PollMs 200
            Start-Sleep -Milliseconds 800
            $c190Names2 = @(C190-GridNames)
            C190-Check 'Space on the order button sorts the categories A to Z, and it says "A to Z, switch to For you"' ([bool]($c190AToZ -and $c190Names2.Count -ge 2 -and (($c190Names2 -join '|') -ceq ((@($c190Names2 | Sort-Object)) -join '|')) -and (Find-Ui -Id 'GridDirectionButton' -OffscreenToo).Current.Name -eq 'A to Z, switch to Z to A')) "$(if ($c190AToZ) { $c190AToZ.Current.Name }); first $($c190Names2 | Select-Object -First 3)"
            Shot (Safe-Name "c190-$c190Theme-seeall-categories-atoz")
            $c190Order = Find-Ui -Id 'GridOrderButton' -OffscreenToo
            if ($c190Order) { Bk-Take $c190Order; Key 0x20; Start-Sleep -Milliseconds 800 }   # For you again (kept for the session)
        }
        Shot (Safe-Name "c190-$c190Theme-seeall-categories")
        Key 0xA6
        $c190Back = Wait-Until { C190-List 'Categories' } -Seconds 15 -PollMs 300
        C190-Check 'Back from See all shows the Collections page with its Categories row' ([bool]$c190Back) ''
    }
    else { C190-Check 'See all Categories opens' $false 'no See all Categories link' }

    # franchise: the row opens on the started one; See all A to Z with the twins' years
    $c190FRow = C190-ShowRow 'Franchises'
    $c190FTiles = @(C190-Tiles 'Franchises')
    $c190First = if ($c190FTiles.Count) { $c190FTiles[0].Current.Name } else { '' }
    C190-Check 'the Franchises row opens on the franchise started ("Zenith Lane"), not A to Z' ($c190First -like 'Zenith Lane, made by Nixie, franchise*') "first: '$c190First'"
    $c190FSee = Find-Ui -Name 'See all Franchises' -Contains -Type 'Hyperlink' -OffscreenToo
    if ($c190FSee) {
        [void](Invoke-Element $c190FSee)
        [void](Wait-Ui -Id 'TitleText' -Name 'Franchises' -OffscreenToo -Seconds 15)
        [void](Wait-Until { Find-Ui -Id 'GridDirectionButton' -OffscreenToo } -Seconds 20 -PollMs 300)
        Start-Sleep -Milliseconds 2000
        $c190Grid = Find-Ui -Id 'GridList' -OffscreenToo
        $c190FNames = @(if ($c190Grid) { @($c190Grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c190Button) | Where-Object { $_.Current.Name -like '*, made by Nixie, franchise*' } |
                    ForEach-Object { ($_.Current.Name -split ', made by Nixie, ')[0] }) })
        $c190FSorted = @($c190FNames | Sort-Object)
        $c190Twins = @("Harbor Watch (1958${c190Dash}1966)", "Harbor Watch (2015${c190Dash}2022)")
        $c190HasTwins = -not @($c190Twins | Where-Object { $c190FNames -notcontains $_ }).Count
        C190-Check 'See all Franchises runs A to Z' ([bool]($c190FNames.Count -ge 3 -and (($c190FNames -join '|') -ceq ($c190FSorted -join '|')))) "$($c190FNames -join ' | ')"
        C190-Check 'the two franchises named alike are said with their years' $c190HasTwins "$(@($c190FNames | Where-Object { $_ -like 'Harbor Watch*' }) -join ' | ')"
        Shot (Safe-Name "c190-$c190Theme-seeall-franchises")
        Key 0xA6
        [void](Wait-Until { C190-List 'Franchises' } -Seconds 15 -PollMs 300)
    }
    else { C190-Check 'See all Franchises opens' $false 'no See all Franchises link' }

    # switch: Settings > Collections > Made by Nixie collections > Categories, off then on
    function C190-Switch {
        $exp = Wait-Until { Find-Ui -Id 'NixieKindsExpander' -OffscreenToo } -Seconds 15 -PollMs 300
        if (-not $exp) { return $null }
        $p = $null
        $btn = @($exp.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c190Button) | Where-Object { $x = $null; $_.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$x) }) | Select-Object -First 1
        if (-not $btn) { $btn = $exp }
        if ($btn.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p) -and $p.Current.ExpandCollapseState -eq 'Collapsed') { $p.Expand(); Start-Sleep -Milliseconds 900 }
        Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $c190Button) | Where-Object { $t = $null; $_.Cached.Name -eq 'Categories' -and $_.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$t) }) | Select-Object -First 1 } -Seconds 10 -PollMs 300
    }
    function C190-Toggle($Switch) { $t = $Switch.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); "$($t.Current.ToggleState)" }
    try {
        Open-Nav 'Settings'
        $c190Switch = C190-Switch
        $c190Words = Find-Ui -Name 'Films or shows that share a genre' -Contains -Type 'Text' -OffscreenToo
        C190-Check 'Settings > Collections has a switch Narrator names "Categories", on, with its words' ([bool]($c190Switch -and (C190-Toggle $c190Switch) -eq 'On' -and $c190Words)) "$(if ($c190Switch) { C190-Toggle $c190Switch }); words: $(if ($c190Words) { $c190Words.Current.Name })"
        if ($c190Switch) {
            Bk-Take $c190Switch
            Key 0x20; Start-Sleep -Milliseconds 900
            C190-Check 'Space turns the Categories switch off' ((C190-Toggle $c190Switch) -eq 'Off') (C190-Toggle $c190Switch)
            Shot (Safe-Name "c190-$c190Theme-switch-off")
            C190-OpenCollections
            Start-Sleep -Seconds 6
            $c190Gone = -not (C190-List 'Categories')
            $c190Left = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $c190Button) | Where-Object { try { $_.Cached.Name -like '*, made by Nixie, category, *' } catch { $false } })
            C190-Check 'with Categories off, the Collections page has no Categories row and no category tile' ([bool]($c190Gone -and $c190Left.Count -eq 0)) "row: $(-not $c190Gone); category tiles: $($c190Left.Count)"
            Open-Nav 'Settings'
            $c190Switch = C190-Switch
            if ($c190Switch) { Bk-Take $c190Switch; Key 0x20; Start-Sleep -Milliseconds 900 }
            C190-OpenCollections
            $c190Again = Wait-Until { C190-List 'Categories' } -Seconds 30 -PollMs 400
            C190-Check 'turned on again, the Categories row is back' ([bool]$c190Again) "switch $(if ($c190Switch) { C190-Toggle $c190Switch })"
        }
    }
    catch { C190-Check 'the Categories switch, off and on' $false $_.Exception.Message }
}
Say "c190 ($c190Theme): done"
