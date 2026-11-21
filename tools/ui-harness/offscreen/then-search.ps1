# Seaglass 1.1.4 (interaction audit, item 2): the title bar's search, live. Run with -Script wait:1 -Then offscreen\then-search.ps1
# (signed in, on Home). The arrows move through the rows without opening anything and the box shows the row's name; Narrator's
# names are "name, year, type"; Enter on a row opens it and clears the box; Enter on the text opens the results page, grouped
# by type, the query kept there; the box clears when a result is opened from it. Nixie 1.5 (with FAKESERVER_SEARCH=sunny):
# the top result, the groups by best match, "Show N more", the chips and people, each with its way out (keyboard, mouse,
# R, Back, a new search), shots at 1920 and 1100. SEAGLASS_THEME / -Setting Glass / SG_SHOT_TAG
# as then-scroll-shots.ps1. Dot-sourced by drive.ps1: no variable here may reuse one of its names.
. (Join-Path $PSScriptRoot 'search-common.ps1')   # the search scripts' helpers (SrType, SrShot, SrChips, ...)
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1500
[void](Wait-Until { if (SrHome) { $true } } -Seconds 20 -PollMs 200)

# ---- typing, then the arrows
SrType 'Quiet' -Slash
Check 'search: the typed text is in the box' ((SrText) -eq 'Quiet') "text: '$(SrText)'"
$rows = Wait-Until { $l = SrRows; if ($l.Count) { , $l } } -Seconds 8 -PollMs 150
$names = @($rows | ForEach-Object { $_.Current.Name })
Say "search rows: $($names.Count): $($names -join ' | ')"
Check 'search: Narrator reads each row as name, year, type (never a class name)' ($names.Count -gt 0 -and -not ($names | Where-Object { $_ -match 'Jellyfin\.Sdk|BaseItemDto' }) -and ($names | Where-Object { $_ -match ', (movie|show|episode)$' })) (($names | Select-Object -First 3) -join ' | ')
SrShot "$srTag-search-rows"
Key 0x28; SrSettle 1500
$first = SrText
Check 'search: Down moves to the first row and opens nothing' ((SrHome) -and $names.Count -and $names[0].StartsWith($first) -and $first -ne 'Quiet') "page home: $(SrHome), box: '$first', row: '$($names[0])'"
Key 0x28; SrSettle 1500
$second = SrText
Check 'search: a second Down moves on, still on Home, the box showing the row''s name' ((SrHome) -and $second -and $second -notmatch 'Jellyfin' -and ($names.Count -lt 2 -or $names[1].StartsWith($second))) "box: '$second'"
SrShot "$srTag-search-after-downs"
# Enter on the row: it opens, the box clears
Key 0x0D
$page = Wait-ItemPage $second -Seconds 15
Check 'search: Enter on a row opens it' ([bool]$page) $second
SrSettle 800
Check 'search: the box is empty after opening a result' ([string]::IsNullOrEmpty((SrText))) "text: '$(SrText)'"

# ---- Enter on the text: the results page
SrType 'the'
[void](Wait-Until { $l = SrRows; if ($l.Count) { , $l } } -Seconds 8 -PollMs 150)
Key 0x0D
$title = Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15
Check 'search: Enter on the text opens the results page' ([bool]$title)
SrSettle 2500
$headings = @('Movies', 'Shows', 'Episodes' | ForEach-Object { $h = Find-Ui -Name "$_ (" -Type 'Text' -Contains -OffscreenToo; if ($h) { $h.Current.Name } })
Check 'search: the results are grouped by type' ($headings.Count -ge 2) ($headings -join ', ')
Check 'search: the query stays in the box on its results page' ((SrText) -eq 'the') "text: '$(SrText)'"
Dump-Tree "$srTag-search-results"
Invoke-A11yScan "$srTag-search-results"
foreach ($size in @(@(1920, 1080), @(1100, 900))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); SrSettle 1800
    Shot "$srTag-search-results-$($size[0])"
}
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1500
# a result opened from the page: the box clears
$card = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'FluentFin.Controls.BaseItemDtoCard'))) | Where-Object { -not $_.Current.IsOffscreen }) | Select-Object -First 1
if (-not $card) { $card = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'BaseItemDtoCard'))) | Where-Object { -not $_.Current.IsOffscreen }) | Select-Object -First 1 }
Check 'search: the results page has cards' ([bool]$card)
if ($card) {
    $cardName = $card.Current.Name
    [void](Invoke-Element $card)
    $opened = Wait-Until { if (-not (Find-Ui -Name 'Results for' -Type 'Text' -Contains)) { $true } } -Seconds 15 -PollMs 200
    SrSettle 1200
    Check 'search: a card opens its item, and the box clears' ([bool]$opened -and [string]::IsNullOrEmpty((SrText))) "card '$cardName', text: '$(SrText)'"
    Key 0xA6
    $back = Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15
    Check 'search: Back returns to the results page' ([bool]$back)
}

# ---- review: Back between two results pages puts each page's query back in the box
Open-Nav 'Home'; SrSettle 1200
SrType 'quiet' -Slash
Key 0x0D                                                # at once: within the 500 ms pause of the typed search
$srQuiet = Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15
SrSettle 1800
# review: the typed search, due after Enter, never opens its list over the results page
$srLate = @(SrRows | Where-Object { -not $_.Current.IsOffscreen })
Check 'search: Enter within the typing pause opens the results, and no list opens over them afterwards' ([bool]$srQuiet -and $srLate.Count -eq 0) "rows shown: $($srLate.Count)"
SrType 'the'
Key 0x0D
[void](Wait-Until { if ((SrText) -eq 'the' -and (Find-Ui -Name 'the' -Type 'Text' -Contains)) { $true } } -Seconds 15 -PollMs 200)
SrSettle 1500
# the title bar's Back (the keys are the box's while it has the focus)
$srWinTop = [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id)).Top
$srBack = @(Find-Ui -Name 'Back' -Type 'Button') | Where-Object { ([OffscreenUi]::RectOf($_).Top - $srWinTop) -lt 48 } | Select-Object -First 1
if ($srBack) { [void](Invoke-Element $srBack) } else { Say 'search: no Back button in the title bar' }
$srBackText = Wait-Until { if ((SrText) -eq 'quiet') { 'quiet' } } -Seconds 10 -PollMs 200
Check 'search: Back to the first results page puts its query back in the box' ($srBackText -eq 'quiet') "text: '$(SrText)'"

# ---- Nixie 1.5 (search): best match first, chips, "Show N more", people, and each one's way out. Needs the fake server's
# search titles (FAKESERVER_SEARCH=sunny); the section answers as a loose search plugin does (POST /__harness/search
# {"mode":"loose"}), as the owner's 12.4 server gave "it's always sunny" 295 films.
$srLoose = if ($env:FAKESERVER_SEARCH -eq 'sunny') { try { (Invoke-Server '/__harness/search' 'POST' @{ mode = 'loose' }).mode } catch { $null } }
if ($srLoose -eq 'loose') {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1200
    Open-Nav 'Home'; SrSettle 1200
    SrSearch "it's always sunny"
    $srTop = Find-Ui -Name $srSunny -Type 'Text'
    $srTopHead = Find-Ui -Name 'Top result' -Type 'Text'
    $srHeads = SrHeadings
    Say "search 1.5: groups $($srHeads -join ', '); chips $((SrChips | ForEach-Object { $_.Current.Name }) -join ' | ')"
    Check 'search 1.5: the show leads as the top result, above every group (not Movies first)' ([bool]$srTop -and [bool]$srTopHead -and ($srHeads.Count -eq 0 -or [OffscreenUi]::RectOf($srTop).Top -lt [OffscreenUi]::RectOf((SrTexts | Where-Object { $_.Current.Name -eq $srHeads[0] } | Select-Object -First 1)).Top)) "top: $([bool]$srTop), groups: $($srHeads -join ', ')"
    $srMovies = SrMore 'movies'
    Check 'search 1.5: the weak films wait behind "Show N weaker matches" (no film cards until asked)' ([bool]$srMovies -and $srMovies.Current.Name -match '^Show \d+ more movies, weaker matches$' -and $srMovies.Current.Name -ne '' -and -not (SrList 'Movies')) "button: '$(if ($srMovies) { $srMovies.Current.Name })', list shown: $([bool](SrList 'Movies'))"
    Check 'search 1.5: one chip lit, All' (((SrLit) -join ',') -eq 'All') "lit: $((SrLit) -join ',')"
    SrShot "$srTag-search150-sunny-1920"

    # the pointer first: a click (Invoke) on "Show N weaker matches", then the keyboard's way out (Space on "Show fewer") and
    # back in (Enter)
    if ($srMovies) {
        SrClick $srMovies; SrSettle 1500
        $srFewer = SrMore 'movies'
        Check 'search 1.5: a click on Show N more opens the films, the button now Show fewer' ([bool](SrList 'Movies') -and $srFewer -and $srFewer.Current.Name -eq 'Show fewer movies') "list: $([bool](SrList 'Movies')), button: '$(if ($srFewer) { $srFewer.Current.Name })'"
        SrShot "$srTag-search150-more-1920"
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900); SrSettle 1800
        SrShot "$srTag-search150-more-1100"
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1500
        $srFewer = SrMore 'movies'
        if ($srFewer) { [void][OffscreenUi]::SetFocus($srFewer); SrSettle 400; Key 0x20; SrSettle 1200 }
        $srAgain = SrMore 'movies'
        Check 'search 1.5: Space on Show fewer folds the films away again, the keyboard still on the button' ($srAgain -and $srAgain.Current.Name -match '^Show \d+ more movies' -and -not (SrList 'Movies') -and (SrFocused) -match 'Show \d+ more movies') "button: '$(if ($srAgain) { $srAgain.Current.Name })', focus: $(SrFocused)"
        Key 0x0D; SrSettle 1200
        Check 'search 1.5: Enter on Show N more opens them again' ([bool](SrList 'Movies')) "focus: $(SrFocused)"
    }

    # the chips, keyboard first: the lit chip takes the keyboard, Right moves, Space lights one; one lit at a time
    $srAll = SrChip 'All'
    if ($srAll) {
        [void][OffscreenUi]::SetFocus($srAll); SrSettle 400
        Key 0x27; SrSettle 500
        $srNext = SrFocused
        Key 0x20; SrSettle 1500
        $srLit = @(SrLit)
        Check 'search 1.5: Right then Space on the chips lights the next kind, alone, and hides the top result' ($srLit.Count -eq 1 -and $srLit[0] -ne 'All' -and $srNext -match "^.*$($srLit[0])" -and -not (Find-Ui -Name 'Top result' -Type 'Text')) "focus after Right: $srNext; lit: $($srLit -join ',')"
        $srOne = @(SrHeadings)
        Check 'search 1.5: a kind shows its one group' ($srOne.Count -eq 1 -and $srOne[0] -like "$($srLit[0]) (*") "groups: $((SrHeadings) -join ', ')"
        # the pointer: a click (Select) on Movies
        $srMovieChip = SrChip 'Movies'
        if ($srMovieChip) { SrClick $srMovieChip; SrSettle 1500 }
        Check 'search 1.5: a click on the Movies chip lights it alone' (((SrLit) -join ',') -eq 'Movies' -and (SrHeadings) -join ',' -like 'Movies (*') "lit: $((SrLit) -join ','); groups: $((SrHeadings) -join ', ')"
        SrShot "$srTag-search150-movies-chip-1920"
        # R (the keyboard on a chip, not in the box): the page loads again, the chip stays lit
        $srMovieChip = SrChip 'Movies'
        if ($srMovieChip) { [void][OffscreenUi]::SetFocus($srMovieChip); SrSettle 300 }
        Key 0x52; SrSettle 3000
        Check 'search 1.5: R reloads the page with the Movies chip still lit' (((SrLit) -join ',') -eq 'Movies' -and (SrText) -eq "it's always sunny") "lit: $((SrLit) -join ','), box: '$(SrText)'"
        # the way back: All (Narrator's select), the top result again
        $srAll = SrChip 'All'
        if ($srAll) { [void](Invoke-Element $srAll); SrSettle 1500 }
        Check 'search 1.5: All brings the top result and every group back' (((SrLit) -join ',') -eq 'All' -and [bool](Find-Ui -Name 'Top result' -Type 'Text') -and (SrHeadings).Count -ge 2) "lit: $((SrLit) -join ','); groups: $((SrHeadings) -join ', ')"
        # a new search starts on All
        $srMovieChip = SrChip 'Movies'
        if ($srMovieChip) { [void](Invoke-Element $srMovieChip); SrSettle 1000 }
        SrSearch "it's always sunny"
        Check 'search 1.5: searching again starts on All' (((SrLit) -join ',') -eq 'All') "lit: $((SrLit) -join ',')"
    }

    # people: a person result opens "With <name>", and Back returns
    SrSearch 'sunny'
    $srPeople = SrChip 'People'
    Check 'search 1.5: people come back, with a People chip' ([bool]$srPeople) "chips: $((SrChips | ForEach-Object { $_.Current.Name }) -join ' | ')"
    if ($srPeople) {
        [void](Invoke-Element $srPeople); SrSettle 1500
        $srPerson = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like 'Sunny Holm*' }) | Select-Object -First 1 } -Seconds 8 -PollMs 200
        if (-not $srPerson) { Dump-Tree "$srTag-search150-people" }
        if ($srPerson) {
            [void](Invoke-Element $srPerson)
            $srWith = Wait-Ui -Name 'With Sunny Holm' -Type 'Text' -Seconds 15
            SrSettle 2000
            $srWithCards = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.ClassName -match 'BaseItemDtoCard' })
            Check 'search 1.5: a person opens "With <name>", their films and shows' ([bool]$srWith -and $srWithCards.Count -gt 0) "cards: $($srWithCards.Count)"
            SrShot "$srTag-search150-person-1920"
            Key 0xA6
            [void](Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15); SrSettle 1500
            Check 'search 1.5: Back returns to the results with People still lit' (((SrLit) -join ',') -eq 'People') "lit: $((SrLit) -join ',')"
        }
        else { Check 'search 1.5: the People group has Sunny Holm' $false }
    }
    [void](Invoke-Server '/__harness/search' 'POST' @{ mode = 'stock' })

    # review (m5): an original title leads too, which needs the search to ask for it (fields=OriginalTitle: the fake sends
    # it only when asked, as Jellyfin does)
    SrSearch 'le fabuleux destin'
    $srOriginal = Find-Ui -Name 'Original title: Le Fabuleux Destin' -Type 'Text' -Contains
    Check 'search 1.5: an original title finds its film as the top result, and says so' ([bool]$srOriginal -and [bool](Find-Ui -Name 'Top result' -Type 'Text')) "groups: $((SrHeadings) -join ', ')"
}
else { Say 'search 1.5: skipped (needs FAKESERVER_SEARCH=sunny and the fake server)' }
