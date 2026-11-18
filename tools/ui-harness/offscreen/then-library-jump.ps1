# Seaglass 1.1.4: the Library's bar, its A-Z jump and the way back to the card you left: run with -Script library -Then
# offscreen\then-library-jump.ps1 (the Movies library, sorted by name).
# The Sort and Filter buttons say the sort in use and the filters on (to Narrator too), the count shows; the A-Z jump is
# one Tab stop, a list named "Jump to a letter" of the letters the titles start with, each named "Jump to X"; a letter
# (invoked, or Enter on it) takes the grid to the first title with that letter, on its page, and that card has the focus;
# a card opened with Enter, then Back: the library comes back on that card (its page, its place).
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
$A = [System.Windows.Automation.AutomationElement]
$Ids = [System.Windows.Automation.AutomationElementIdentifiers]
function Jf-Info {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::HasKeyboardFocusProperty, $true)
    $el = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if (-not $el) { return $null }
    $c = $el.Cached
    [pscustomobject]@{ Name = $c.Name; Type = ($c.ControlType.ProgrammaticName -replace '^ControlType\.', ''); Class = $c.ClassName
        Pos = [int]$el.GetCurrentPropertyValue($Ids::PositionInSetProperty); Size = [int]$el.GetCurrentPropertyValue($Ids::SizeOfSetProperty) }
}
function Jf-Describe($f) { if ($f) { "{0} [{1} {2}] {3} of {4}" -f $f.Name, $f.Type, $f.Class, $f.Pos, $f.Size } else { 'no focus' } }
function Jf-Press([int]$Vk, [int]$Ms = 700) { Key $Vk; Start-Sleep -Milliseconds $Ms }
# the A-Z key of a title as the app makes it: its first letter without accents, A-Z, else '#'
function Jf-Letter([string]$Name) {
    $n = ($Name -split ',')[0].Trim().Normalize([Text.NormalizationForm]::FormD)
    foreach ($ch in $n.ToCharArray()) {
        if ([Globalization.CharUnicodeInfo]::GetUnicodeCategory($ch) -eq 'NonSpacingMark') { continue }
        $u = [char]::ToUpperInvariant($ch)
        if ($u -ge 'A' -and $u -le 'Z') { return [string]$u } else { return '#' }
    }
    '#'
}

# --- the bar
$sortBtn = Wait-Ui -Name 'Sort: ' -Type 'Button' -Contains -Seconds 10
Check 'library bar: the Sort button says the sort in use, to Narrator ("Sort: Name, ascending")' ($sortBtn -and $sortBtn.Current.Name -eq 'Sort: Name, ascending') $(if ($sortBtn) { $sortBtn.Current.Name })
$filterBtn = Find-Ui -Name 'Filter, ' -Type 'Button' -Contains
Check 'library bar: the Filter button says how many filters are on ("Filter, none on")' ($filterBtn -and $filterBtn.Current.Name -eq 'Filter, none on') $(if ($filterBtn) { $filterBtn.Current.Name })
function Jf-Count {
    $textCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $textCond)) | Where-Object { $_.Cached.Name -match '^\d[\d,]* (of \d[\d,]* )?movies$' } | Select-Object -First 1
}
$countText = Wait-Until { Jf-Count } -Seconds 5
Check 'library bar: the count shows ("320 movies")' ($countText -and $countText.Cached.Name -match '^\d[\d,]* movies$') $(if ($countText) { $countText.Cached.Name })

# --- the A-Z jump
$letterList = Wait-Ui -Name 'Jump to a letter' -Type 'List' -Seconds 5
Check 'a-z: a list named "Jump to a letter"' ([bool]$letterList)
$btnCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$letters = if ($letterList) { @($letterList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) } else { @() }
$letterNames = @($letters | ForEach-Object { $_.Current.Name })
Check 'a-z: only letters (each "Jump to X"), each once, at most 27' ($letters.Count -ge 2 -and $letters.Count -le 27 -and @($letterNames | Where-Object { $_ -notmatch '^Jump to (\w|titles that start with a number or a sign)$' }).Count -eq 0 -and ($letterNames | Sort-Object -Unique).Count -eq $letterNames.Count) ($letterNames -join ', ')
Shot 'library-az-1-bar'

# one Tab stop: Tab from Filter lands on a letter, the next Tab in the grid
[void][OffscreenUi]::SetFocus($filterBtn); Start-Sleep -Milliseconds 400
Jf-Press 0x09
$onLetter = Jf-Info
Check 'a-z: Tab from Filter goes to the A-Z jump (a letter, "1 of n")' ($onLetter -and $onLetter.Name -like 'Jump to*' -and $onLetter.Pos -eq 1 -and $onLetter.Size -eq $letters.Count) (Jf-Describe $onLetter)
Jf-Press 0x27   # Right: the next letter
$nextLetter = Jf-Info
Check 'a-z: Right moves to the next letter' ($nextLetter -and $nextLetter.Name -like 'Jump to*' -and $nextLetter.Pos -eq 2) (Jf-Describe $nextLetter)
Jf-Press 0x09
$inGrid = Jf-Info
Check 'a-z: the next Tab goes into the grid (the strip is one stop)' ($inGrid -and $inGrid.Class -eq 'BaseItemDtoCard') (Jf-Describe $inGrid)

# a letter far down the library (the last one), by Enter on it: its first title on its page, with the keyboard focus
$last = $letters[-1]; $lastLetter = ($last.Current.Name -replace '^Jump to ', '')
[void][OffscreenUi]::SetFocus($last); Start-Sleep -Milliseconds 400
Jf-Press 0x0D 1800
$jumped = Jf-Info
Check "a-z: Enter on ""$lastLetter"" takes the focus to a card starting with it" ($jumped -and $jumped.Class -eq 'BaseItemDtoCard' -and (Jf-Letter $jumped.Name) -eq $lastLetter) (Jf-Describe $jumped)
Shot 'library-az-2-jumped'
if ($jumped -and $jumped.Pos -gt 1) {
    Jf-Press 0x25 900   # Left: the title before it starts with another letter
    $before_ = Jf-Info
    Check "a-z: ... the first one (the title before it does not start with ""$lastLetter"")" ($before_ -and (Jf-Letter $before_.Name) -ne $lastLetter) (Jf-Describe $before_)
    Jf-Press 0x27 900
}

# a letter in the middle by invoking it (as a click does)
$mid = $letters[[int][Math]::Floor($letters.Count / 2)]; $midLetter = ($mid.Current.Name -replace '^Jump to ', '')
[void](Invoke-Element $mid); Start-Sleep -Milliseconds 1800
$jumped2 = Jf-Info
Check "a-z: ""$midLetter"" invoked: the card with the focus starts with it" ($jumped2 -and $jumped2.Class -eq 'BaseItemDtoCard' -and (Jf-Letter $jumped2.Name) -eq $midLetter) (Jf-Describe $jumped2)

# --- the way back: two cards on, open it with Enter, Back: the same card has the focus
Jf-Press 0x27; Jf-Press 0x27
$left = Jf-Info
Jf-Press 0x0D 600
$moviePage = Wait-Until { Find-Ui -Name ($left.Name -split ',')[0] -Id 'LogoImage' } -Seconds 15
if (-not $moviePage) { $moviePage = Wait-ItemPage ($left.Name -split ',')[0] 10 }
Check 'return: Enter on a card opens its page' ([bool]$moviePage) (Jf-Describe $left)
Start-Sleep -Milliseconds 1200
Key 0xA6   # Back (the browser-back key; Alt+Left cannot be posted)
$returned = Wait-Until { $x = Jf-Info; if ($x -and $x.Class -eq 'BaseItemDtoCard') { $x } } -Seconds 15 -PollMs 200
Start-Sleep -Milliseconds 800
$returned = Jf-Info
Check 'return: Back to the library puts the focus on the card that was opened' ($returned -and $left -and $returned.Name -eq $left.Name -and $returned.Pos -eq $left.Pos) ("left {0}; back on {1}" -f (Jf-Describe $left), (Jf-Describe $returned))
Shot 'library-return-1-focused'

# --- the direction: Ascending / Descending, a second group in the Sort menu (1.2.0, the button review's L24; it was a round
# button beside Sort)
function Jf-SortMenu([string]$Order) {
    $sb = Find-Ui -Name 'Sort: ' -Type 'Button' -Contains
    if (-not $sb) { return $false }
    [void](Invoke-Element $sb); Start-Sleep -Milliseconds 700
    $mi = Wait-Ui -Name $Order -Type 'MenuItem' -Seconds 4
    if ($mi) { [void](Invoke-Element $mi) } else { Key 0x1B }
    Start-Sleep -Milliseconds 1200
    [bool]$mi
}
$orderBtn = Jf-SortMenu 'Descending'
Check 'library bar: the direction is in the Sort menu ("Descending")' $orderBtn
if ($orderBtn) {
    $sortBtn = Find-Ui -Name 'Sort: ' -Type 'Button' -Contains
    Check 'library bar: the Sort button says descending ("Sort: Name, descending")' ($sortBtn -and $sortBtn.Current.Name -eq 'Sort: Name, descending') $(if ($sortBtn) { $sortBtn.Current.Name })
    $firstLetter = Find-Ui -Name 'Jump to a letter' -Type 'List'
    $firstBtn = if ($firstLetter) { @($firstLetter.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond))[0] }
    Check 'a-z: ... and the letters run the other way (the first is not the ascending first)' ($firstBtn -and $firstBtn.Current.Name -ne $letterNames[0]) $(if ($firstBtn) { $firstBtn.Current.Name })
    Shot 'library-az-3-descending'
    [void](Jf-SortMenu 'Ascending')
}

# --- the Filter menu: lists of check boxes with counts, Clear filters; the button counts them
$filterBtn = Find-Ui -Name 'Filter, ' -Type 'Button' -Contains
if ($filterBtn) {
    [void](Invoke-Element $filterBtn); Start-Sleep -Milliseconds 900
    $boxCond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
    $boxes = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $boxCond))
    $popupBoxes = @()
    if (-not $boxes.Count) {
        # the flyout may be a window of its own: look in every window of FluentFin's
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:ui.Id)
        $popupBoxes = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond) | ForEach-Object { $_.FindAll([System.Windows.Automation.TreeScope]::Descendants, $boxCond) })
        $boxes = $popupBoxes
    }
    $boxNames = @($boxes | ForEach-Object { $_.Current.Name })
    Check 'filter: the menu starts with "Unwatched only" and "Favorites only"' ($boxNames.Count -ge 2 -and $boxNames[0] -eq 'Unwatched only' -and $boxNames[1] -eq 'Favorites only') (($boxNames | Select-Object -First 2) -join ' | ')
    $facetNames = @($boxNames | Select-Object -Skip 2)
    Check 'filter: ... then check boxes, each "<value>, n titles"' ($facetNames.Count -ge 3 -and @($facetNames | Where-Object { $_ -notmatch ', \d+ titles?$' }).Count -eq 0) ("{0} boxes: {1}" -f $facetNames.Count, (($facetNames | Select-Object -First 4) -join ' | '))
    $boxes = @($boxes | Select-Object -Skip 2)
    Shot 'library-filter-1-menu'
    if ($boxes.Count) {
        $tp = $null
        if ($boxes[0].TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$tp)) { $tp.Toggle() }
        Start-Sleep -Milliseconds 900
        $filterBtn = Find-Ui -Name 'Filter, ' -Type 'Button' -Contains
        Check 'filter: a box ticked: the button says "Filter, 1 on"' ($filterBtn -and $filterBtn.Current.Name -eq 'Filter, 1 on') $(if ($filterBtn) { $filterBtn.Current.Name })
        $count2 = Jf-Count
        Check 'filter: ... and the count says how many are left ("n of 320 movies")' ($count2 -and $count2.Cached.Name -match '^\d[\d,]* of \d[\d,]* movies$') $(if ($count2) { $count2.Cached.Name })
        Shot 'library-filter-2-one-on'
        $clear = Find-Ui -Name 'Clear filters' -Type 'Button'
        if (-not $clear) {
            # in the flyout's own window
            $root = [System.Windows.Automation.AutomationElement]::RootElement
            $pidCond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $script:ui.Id)
            $nameCond = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, 'Clear filters')
            $clear = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond) | ForEach-Object { $_.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond) } | Where-Object { $_ }) | Select-Object -First 1
        }
        if ($clear) { [void](Invoke-Element $clear); Start-Sleep -Milliseconds 800 }
        $filterBtn = Find-Ui -Name 'Filter, ' -Type 'Button' -Contains
        Check 'filter: Clear filters: "Filter, none on" again' ($filterBtn -and $filterBtn.Current.Name -eq 'Filter, none on') $(if ($filterBtn) { $filterBtn.Current.Name })
    }
    Key 0x1B; Start-Sleep -Milliseconds 500
}
