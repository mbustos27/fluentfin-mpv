# 1.8 (the reverse toggle, stream s): the Collection page's direction button next to Order, in one theme per run
# (NIXIE_LAYOUT: Classic, Cinematic, DriveIn, Editorial). Run with FAKESERVER_COLLECTIONS=1 and
# `-Script wait:1 -NoA11y -Then offscreen\then-180-reverse.ps1`. SG_SHOT_TAG names the shots. Checks what the user sees:
# the button's Narrator name says the direction ("Oldest first, switch to newest first"), the cards run the other way after
# a press (keyboard first, right after arriving; then the mouse; then a gamepad's A), its words and width hold still when it
# flips (rule 7), Order: Name says "A to Z" / "Z to A", the direction survives Back, Forward and R, and a playlist's own order
# is "First to last". Dot-sourced by drive.ps1: no variable here may reuse one of its names (r180 prefix).
$r180Tag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
$r180Layout = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
function R180-Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
function R180-Window { $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r); $r }
function R180-FocusName {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $f = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if ($f) { $f.Current.Name } else { '' }
}
# the direction button: a button whose name says a direction, then what a press does (SortDirections.ButtonName)
function R180-Direction { Find-Ui -Name ', switch to ' -Type 'Button' -Contains }
function R180-Name { $b = R180-Direction; if ($b) { $b.Current.Name } else { '' } }
function R180-Rect($Element) { if ($Element) { $Element.Current.BoundingRectangle } else { $null } }
# the cards in the order they are drawn: list items named after a film ("1, Wild Canyons, 1979, ..." or "Wild Canyons, ..."),
# top to bottom, left to right; their names without the place
function R180-Cards {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $w = R180-Window
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 60 -and $_.Current.Name -match ', (19|20)\d\d' } |
        Sort-Object { [int](($_.Current.BoundingRectangle.Y - $w.T) / 40) }, { $_.Current.BoundingRectangle.X } |
        ForEach-Object { ($_.Current.Name -replace '^\d+, ', '') -replace ', .*$', '' }
}
function R180-Press([string]$How) {
    switch ($How) {
        'invoke' { [void](Invoke-Element (R180-Direction)) }
        'space' { Key 0x20 }
        'gamepad' { Key 0xC3 } # VK_GAMEPAD_A
    }
    R180-Settle 900
}
# Tab from where the keyboard is until it is on the direction button (at most 24 presses); true when it got there
function R180-TabTo {
    for ($i = 0; $i -lt 24; $i++) {
        if ((R180-FocusName) -like '*, switch to *') { return $true }
        Key 0x09; Start-Sleep -Milliseconds 220
    }
    (R180-FocusName) -like '*, switch to *'
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); R180-Settle 1500
Say "then-180-reverse: theme $r180Layout"

# ---- a box set in its own (release) order ----------------------------------------------------------------------------
Open-Library 'Collections'
R180-Settle 2000
Open-Card 'Copper and Glass'
$r180Play = Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 20
R180-Settle 2500
Check 'reverse: the Collection page opens' ([bool]$r180Play)
$r180Name = R180-Name
Check 'reverse: the direction button is there, its name says the direction first' ($r180Name -eq 'Oldest first, switch to newest first') $r180Name
$r180Order = Find-Ui -Name 'Order: ' -Type 'Button' -Contains
$r180Button = R180-Direction
$r180OrderRect = R180-Rect $r180Order; $r180Rect = R180-Rect $r180Button
Check 'reverse: it sits next to Order, on its right' ($r180OrderRect -and $r180Rect -and $r180Rect.X -ge $r180OrderRect.X + $r180OrderRect.Width - 1 -and [Math]::Abs($r180Rect.Y - $r180OrderRect.Y) -lt 24) "Order x $($r180OrderRect.X)+$($r180OrderRect.Width), button x $($r180Rect.X)"
$r180Before = @(R180-Cards)
Say "cards: $($r180Before -join ' | ')"
Capture-Page "$r180Tag-$r180Layout-reverse-rest"

# keyboard first, right after arriving (the keyboard is on Play): Tab to the button, Space
Check 'reverse: arriving, the keyboard is on Play' ((R180-FocusName).StartsWith('Play ')) (R180-FocusName)
Check 'reverse: Tab reaches the direction button' (R180-TabTo) (R180-FocusName)
R180-Press 'space'
$r180After = @(R180-Cards)
Say "cards reversed: $($r180After -join ' | ')"
$r180Name = R180-Name
Check 'reverse: Space flips it: "Newest first", and Narrator hears it on the focused button' ($r180Name -eq 'Newest first, switch to oldest first' -and (R180-FocusName) -eq $r180Name) "$r180Name / focus $(R180-FocusName)"
Check 'reverse: the cards run the other way' ($r180Before.Count -ge 2 -and ($r180After -join '|') -eq (@($r180Before[($r180Before.Count - 1)..0]) -join '|')) "$($r180After -join ' | ')"
$r180Rect2 = R180-Rect (R180-Direction); $r180OrderRect2 = R180-Rect (Find-Ui -Name 'Order: ' -Type 'Button' -Contains)
Check 'reverse: rule 7: the button and Order hold still when it flips' ($r180Rect2 -and [Math]::Abs($r180Rect2.X - $r180Rect.X) -lt 1 -and [Math]::Abs($r180Rect2.Width - $r180Rect.Width) -lt 1 -and [Math]::Abs($r180OrderRect2.X - $r180OrderRect.X) -lt 1) "button x $($r180Rect.X)->$($r180Rect2.X), w $($r180Rect.Width)->$($r180Rect2.Width)"
Shot "$r180Tag-$r180Layout-reverse-newest-first"

# the mouse (UI Automation's Invoke, as a click): back to oldest first
R180-Press 'invoke'
Check 'reverse: a click flips it back' ((R180-Name) -eq 'Oldest first, switch to newest first' -and ((@(R180-Cards)) -join '|') -eq ($r180Before -join '|')) (R180-Name)
# a held key: three quick presses end reversed (odd), nothing lost
if (R180-TabTo) { Key 0x20; Key 0x20; Key 0x20; R180-Settle 1200 }
Check 'reverse: three quick presses end on newest first' ((R180-Name) -eq 'Newest first, switch to oldest first') (R180-Name)
# a gamepad's A on the focused button
R180-Press 'gamepad'
Check 'reverse: a gamepad''s A flips it' ((R180-Name) -eq 'Oldest first, switch to newest first') (R180-Name)

# ---- rule 7 (review, Minor 5): every order, both ways, and nothing in the toolbar moves ----------------------------------
function R180-Toolbar {
    $hide = R180-Rect (Find-Ui -Name 'Hide watched' -Type 'CheckBox'); $ord = R180-Rect (Find-Ui -Name 'Order: ' -Type 'Button' -Contains); $dir = R180-Rect (R180-Direction)
    if ($hide -and $ord -and $dir) { '{0:0},{1:0}+{2:0},{3:0}+{4:0}' -f $hide.X, $ord.X, $ord.Width, $dir.X, $dir.Width } else { 'missing' }
}
function R180-SetOrder([string]$Item) {
    [void](Invoke-Element (Find-Ui -Name 'Order: ' -Type 'Button' -Contains)); R180-Settle 700
    $mi = Find-Ui -Name $Item -Type 'MenuItem'; if ($mi) { [void](Invoke-Element $mi) }; R180-Settle 900
}
$r180Rest = R180-Toolbar
$r180Moved = @()
foreach ($r180Item in 'Release date', 'Name', 'Date added', 'Collection order') {
    R180-SetOrder $r180Item
    foreach ($r180Way in 1, 2) {
        $r180Now = R180-Toolbar
        if ($r180Now -ne $r180Rest) { $r180Moved += "$r180Item ($(R180-Name)): $r180Now" }
        R180-Press 'invoke'
    }
}
Check 'reverse: rule 7: every order, both ways: Hide watched, Order and the direction hold still' ($r180Moved.Count -eq 0 -and $r180Rest -ne 'missing') "at rest $r180Rest; moved: $($r180Moved -join '; ')"

# ---- Order: Name: A to Z, then Z to A --------------------------------------------------------------------------------
[void](Invoke-Element (Find-Ui -Name 'Order: ' -Type 'Button' -Contains)); R180-Settle 700
$r180Mi = Find-Ui -Name 'Name' -Type 'MenuItem'; if ($r180Mi) { [void](Invoke-Element $r180Mi) }; R180-Settle 1200
Check 'reverse: Order: Name says A to Z' ((R180-Name) -eq 'A to Z, switch to Z to A') (R180-Name)
$r180Az = @(R180-Cards)
R180-Press 'invoke'
$r180Za = @(R180-Cards)
Check 'reverse: ... flipped, Z to A, the cards the other way' ((R180-Name) -eq 'Z to A, switch to A to Z' -and $r180Az.Count -ge 2 -and ($r180Za -join '|') -eq (@($r180Az[($r180Az.Count - 1)..0]) -join '|')) "$(R180-Name): $($r180Za -join ' | ')"
Shot "$r180Tag-$r180Layout-reverse-z-to-a"

# ---- Back and Forward: a film, then Back: still Z to A, the same cards ------------------------------------------------
Open-Card $r180Za[0]
R180-Settle 3000
Key 0xA6 # Back (VK_BROWSER_BACK)
$r180Back = Wait-Until { R180-Direction } -Seconds 15 -PollMs 200
R180-Settle 1500
Check 'reverse: Back: still Z to A, the same cards' ((R180-Name) -eq 'Z to A, switch to A to Z' -and ((@(R180-Cards)) -join '|') -eq ($r180Za -join '|')) "$(R180-Name): $((@(R180-Cards)) -join ' | ')"
# Back once more (the Collections page), then Forward: the collection still Z to A
Key 0xA6; R180-Settle 2500
Key 0xA7 # Forward (VK_BROWSER_FORWARD)
[void](Wait-Until { R180-Direction } -Seconds 15 -PollMs 200)
R180-Settle 1500
Check 'reverse: Forward: still Z to A' ((R180-Name) -eq 'Z to A, switch to A to Z' -and ((@(R180-Cards)) -join '|') -eq ($r180Za -join '|')) "$(R180-Name): $((@(R180-Cards)) -join ' | ')"
# R: loaded again in place: still Z to A
Key 0x52; R180-Settle 3000
[void](Wait-Until { R180-Direction } -Seconds 15 -PollMs 200)
Check 'reverse: R keeps Z to A' ((R180-Name) -eq 'Z to A, switch to A to Z' -and ((@(R180-Cards)) -join '|') -eq ($r180Za -join '|')) "$(R180-Name): $((@(R180-Cards)) -join ' | ')"
# put the set back as it was opened, for the next run's eyes: own order, unreversed
R180-Press 'invoke'
[void](Invoke-Element (Find-Ui -Name 'Order: ' -Type 'Button' -Contains)); R180-Settle 700
$r180Mi = Find-Ui -Name 'Collection order' -Type 'MenuItem'; if ($r180Mi) { [void](Invoke-Element $r180Mi) }; R180-Settle 1000

# ---- a playlist: its own order is first to last -----------------------------------------------------------------------
Open-Library 'Playlists'
R180-Settle 2000
Open-Card 'Rainy Sunday'
[void](Wait-Ui -Name 'Play ' -Type 'Button' -Contains -Seconds 20)
R180-Settle 2000
Check 'reverse: a playlist''s own order says First to last' ((R180-Name) -eq 'First to last, switch to last to first') (R180-Name)
$r180Pl = @(R180-Cards)
R180-Press 'invoke'
Check 'reverse: ... flipped, Last to first, the playlist the other way' ((R180-Name) -eq 'Last to first, switch to first to last' -and ((@(R180-Cards)) -join '|') -eq (@($r180Pl[($r180Pl.Count - 1)..0]) -join '|')) (R180-Name)
Shot "$r180Tag-$r180Layout-reverse-playlist"
R180-Press 'invoke'
