# Nixie 1.4.0 (bug hunt M1, m4): a page's arrival focus never takes the keyboard from the user. On a slow server the user
# goes to the search box (Ctrl+F and a letter, or the focus put there with no key) while the page loads: once it is in, the keyboard is still in the box
# and the next letter (R) is typed there, not taken as a refresh. Untouched, the arrival still puts the keyboard in the page
# (the Collection's Play, the Libraries page's first tile, a library's first poster, a film's Play).
# Once per theme (NIXIE_LAYOUT), with FAKESERVER_COLLECTIONS=1: run-offscreen.ps1 -Script wait:1 -NoA11y -Then <this>.
. (Join-Path $PSScriptRoot 'chord-keys.ps1')

$arItem = '^/(Users/[^/]+/)?Items/[0-9a-fA-F-]{32,36}$'   # one item: a collection, a film
$arItems = '^/(Users/[^/]+/)?Items$'                       # a library's items
function AR-Latency([object[]]$Rules = @()) { [void](Invoke-Server '/__harness/latency' 'POST' @{ default = 0; rules = $Rules }) }
function AR-Box { Find-Ui -Name 'Search' -Type 'Edit' }
function AR-Text { $b = AR-Box; if ($b) { try { $b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '?' } } else { '(no search box)' } }
function AR-InBox { (CK-Focus) -match "^Edit 'Search'" }
function AR-Refreshes([double]$Since) { @(CK-Log $Since '^Refresh: .* loaded again').Count }
# the search box emptied and left, so the next row starts clean
function AR-Reset {
    AR-Latency
    $b = AR-Box
    if ($b) { try { $b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('') } catch { } }
    CK-Key 'Esc'
    Start-Sleep -Milliseconds 400
}
# how the user gets to the box while the page loads: the keyboard (Ctrl+F, then a letter), or no key at all (the focus put in
# the box as a click or Narrator puts it: UI Automation; the harness's pointer hook does not focus a text box), so only the
# text-box rule keeps it there
function AR-ToBox([string]$How) {
    if ($How -eq 'focus') { $b = AR-Box; if ($b) { [void][OffscreenUi]::SetFocus($b) } }
    else { CK-Key 'Ctrl+F'; Start-Sleep -Milliseconds 300; CK-Key 'W' }
    Start-Sleep -Milliseconds 300
}

# $Open opens the page (its request slowed by $Rule, 5 s); $Loaded says the page is in
function AR-Slow([string]$What, [string]$How, [string]$Rule, [scriptblock]$Open, [scriptblock]$Loaded) {
    AR-Latency @(, @($Rule, 5000))
    $at = Get-Epoch
    & $Open
    Start-Sleep -Milliseconds 700
    AR-ToBox $How
    $while = "$(CK-Focus) (box '$(AR-Text)')"
    $in = Wait-Until { & $Loaded } -Seconds 15 -PollMs 200
    Start-Sleep -Milliseconds 1500   # the arrival's own moment (a low-priority dispatch, the item pages' 100 ms look)
    $after = CK-Focus
    if ($How -eq 'focus') { CK-Key 'W'; Start-Sleep -Milliseconds 300 }
    $r = Get-Epoch
    CK-Key 'R'
    Start-Sleep -Milliseconds 1200
    $text = AR-Text
    $refreshes = AR-Refreshes $r
    Check "arrival ($What, $How first while it loads): the keyboard stays in the search box, and R is typed there" ([bool]$in -and $after -match "^Edit 'Search'" -and $text -eq 'wr' -and $refreshes -eq 0) `
        "loaded $([bool]$in); while loading: $while; after: $after; box '$text'; refreshes $refreshes; $((CK-Log $at '\[focus\]|^Refresh:') -join ' | ')"
    Shot "arrival-$($What -replace '\W+', '-')-$How"
    AR-Reset
}
# untouched: the arrival puts the keyboard in the page
function AR-Untouched([string]$What, [string]$Expect, [scriptblock]$Open, [scriptblock]$Loaded) {
    $at = Get-Epoch
    & $Open
    $in = Wait-Until { & $Loaded } -Seconds 15 -PollMs 200
    $seen = Wait-Until { $f = CK-Focus; if ($f -match $Expect) { $f } } -Seconds 6 -PollMs 200
    Check "arrival ($What, untouched): the keyboard goes to $Expect" ([bool]$in -and [bool]$seen) "loaded $([bool]$in); focus $(CK-Focus); $((CK-Log $at '\[focus\]') -join ' | ')"
}

$arCollection = { Open-Library 'Collections'; Start-Sleep -Seconds 3; Open-Card 'Copper and Glass' }
$arCollectionIn = { Find-Ui -Id 'PlayButton' }
$arFilm = { Open-Library $fixture.movie.library; Start-Sleep -Seconds 3; Open-Card $fixture.movie.name }
$arFilmIn = { Wait-ItemPage $fixture.movie.name 0.2 }
$arLibrary = { Open-Library $fixture.movie.library }
$arLibraryIn = { Find-MediaCard $fixture.movie.name }
$arOnly = if ($env:AR_ONLY) { $env:AR_ONLY -split ',' } else { @('untouched', 'collection', 'film', 'library', 'filmback', 'suggest', 'colstate', 'rlock') }

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1600, 1000); Start-Sleep -Milliseconds 1500

if ($arOnly -contains 'untouched') {
    AR-Untouched 'Collection' "id=PlayButton" $arCollection $arCollectionIn
    AR-Untouched 'Libraries page' '^ListItem ' { Open-Nav 'Home'; Start-Sleep -Seconds 2; Open-Nav 'Libraries' } { Find-Ui -Name 'Movies' -Type 'ListItem' -Contains }
    AR-Untouched 'library' '^ListItem ' { Open-Nav 'Home'; Start-Sleep -Seconds 2; & $arLibrary } $arLibraryIn
    AR-Untouched 'film' "^Button '(Play|Resume)" { Open-Nav 'Home'; Start-Sleep -Seconds 2; & $arFilm } $arFilmIn
    Open-Nav 'Home'; Start-Sleep -Seconds 2
}
if ($arOnly -contains 'collection') {
    # the bug hunt's M1: Play took the keyboard out of the box, and R refreshed the collection
    $arOpenSlow = { Open-Card 'Copper and Glass' }
    foreach ($how in 'keyboard', 'focus') {
        Open-Library 'Collections'; Start-Sleep -Seconds 3
        AR-Slow 'Collection' $how $arItem $arOpenSlow $arCollectionIn
    }
}
if ($arOnly -contains 'film') {
    foreach ($how in 'keyboard', 'focus') {
        Open-Library $fixture.movie.library; Start-Sleep -Seconds 3
        AR-Slow 'film' $how $arItem { Open-Card $fixture.movie.name } $arFilmIn
    }
}
if ($arOnly -contains 'library') {
    # the bug hunt's m4: the first poster took the keyboard on a slow library
    Open-Nav 'Home'; Start-Sleep -Seconds 2
    AR-Slow 'library' 'keyboard' $arItems $arLibrary $arLibraryIn
}
if ($arOnly -contains 'filmback') {
    # (review of wip/140-fix2) Back to a slow film: Ctrl+F and a letter while it loads; its place (Play, a card) used to take
    # the keyboard out of the box when the page came in, and the next R refreshed it (the hunt's M1 again)
    Open-Nav 'Home'; Start-Sleep -Seconds 2
    & $arFilm
    [void](Wait-Until { & $arFilmIn } -Seconds 15 -PollMs 200); Start-Sleep -Seconds 2
    Open-Nav 'Home'; Start-Sleep -Seconds 3
    AR-Slow 'film, Back to it' 'keyboard' $arItem { CK-Key 'Alt+Left' } $arFilmIn
}
if ($arOnly -contains 'suggest') {
    # (review of wip/140-fix2) a search suggestion chosen with Down and Enter: the film opens with the keyboard on Play (the
    # emptied box kept it, Enter then did nothing and letters typed into the box)
    Open-Nav 'Home'; Start-Sleep -Seconds 2
    CK-Key 'Ctrl+F'; Start-Sleep -Milliseconds 400
    $query = $fixture.movie.name.Substring(0, [Math]::Min(9, $fixture.movie.name.Length)).TrimEnd()
    foreach ($ch in $query.ToCharArray()) { if ($ch -eq ' ') { CK-Key 'Space' } else { CK-Key "$ch" }; Start-Sleep -Milliseconds 60 }
    $listed = Wait-Until { Find-Ui -Name $fixture.movie.name -Type 'ListItem' -Contains } -Seconds 10 -PollMs 200
    $at = Get-Epoch
    CK-Key 'Down'; Start-Sleep -Milliseconds 400
    CK-Key 'Enter'
    $opened = Wait-ItemPage $fixture.movie.name 15
    $onPlay = Wait-Until { $f = CK-Focus; if ($f -match "^Button '(Play|Resume)") { $f } } -Seconds 6 -PollMs 200
    $before = AR-Text
    CK-Key 'Q'; Start-Sleep -Milliseconds 700
    $after = AR-Text
    Check "search: a suggestion chosen with Down and Enter opens the film with the keyboard on Play; a letter then does not type into the box" ([bool]$listed -and [bool]$opened -and [bool]$onPlay -and $before -eq '' -and $after -eq '') "typed '$query'; listed $([bool]$listed); opened $([bool]$opened); focus $(CK-Focus); box '$before' then '$after'; $((CK-Log $at '\[focus\]') -join ' | ')"
    AR-Reset
}
if ($arOnly -contains 'colstate') {
    # the bug hunt's M2: R and Back on a Collection page reset its Order and Hide watched
    function AR-Order([string]$Name) {
        $o = Find-Ui -Id 'OrderButton'
        if ($o) { [void](Invoke-Element $o); $m = Wait-Ui -Name $Name -Type 'MenuItem' -Seconds 5; if ($m) { [void](Invoke-Element $m) } else { CK-Key 'Esc' } }
        Start-Sleep -Milliseconds 800
    }
    function AR-Hide { $h = Find-Ui -Id 'HideWatchedBox'; if ($h) { try { $h.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState } catch { '?' } } else { '(none)' } }
    function AR-View { "order '$((Find-Ui -Id 'OrderButton').Current.Name)', hide watched $(AR-Hide)" }
    Open-Nav 'Home'; Start-Sleep -Seconds 2
    & $arCollection
    [void](Wait-Until { & $arCollectionIn } -Seconds 15 -PollMs 200); Start-Sleep -Seconds 1
    AR-Order 'Name'
    $h = Find-Ui -Id 'HideWatchedBox'; if ($h) { try { $h.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { } }
    Start-Sleep -Milliseconds 800
    $before = AR-View
    $card = Find-Ui -Name 'Painted Forests' -Type 'ListItem' -Contains
    if ($card) { [void][OffscreenUi]::SetFocus($card); Start-Sleep -Milliseconds 400 }
    $at = Get-Epoch
    CK-Key 'R'
    [void](Wait-Until { if (@(CK-Log $at '^Refresh: the place').Count) { $true } } -Seconds 10 -PollMs 200); Start-Sleep -Seconds 1
    $after = AR-View
    Check 'collection: R keeps Order and Hide watched' ($before -match "Order: Name" -and $after -eq $before) "before: $before || after R: $after; focus $(CK-Focus)"
    # a film, then Back
    $card = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
        Where-Object { try { $_.Cached.Name -match ', \d{4}' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -Last 1
    $film = if ($card) { $card.Current.Name } else { '(none)' }
    if ($card) { [void][OffscreenUi]::SetFocus($card); Start-Sleep -Milliseconds 300; CK-Key 'Enter' }
    [void](Wait-Until { if (-not (Find-Ui -Id 'OrderButton')) { $true } } -Seconds 10 -PollMs 200); Start-Sleep -Seconds 2
    CK-Key 'Alt+Left'
    [void](Wait-Until { Find-Ui -Id 'OrderButton' } -Seconds 15 -PollMs 200); Start-Sleep -Seconds 2
    $back = AR-View
    $focus = CK-Focus
    Check "collection: Back from a film keeps Order and Hide watched, and the keyboard on its card" ($back -eq $before -and $focus -like "*'$film'*") "before: $before || after Back from '$film': $back; focus $focus"
    Shot 'collection-after-back'
}
if ($arOnly -contains 'rlock') {
    # the bug hunt's m1: R with no place to put back (the keyboard on the top bar's tab, where invoking it leaves it) kept the
    # restore running its whole 6 s, and each R meanwhile joined it and did nothing; R, R 150 ms apart still loads once (M4)
    Open-Nav 'Home'; Start-Sleep -Seconds 2
    Open-Library $fixture.movie.library
    [void](Wait-Until { & $arLibraryIn } -Seconds 15 -PollMs 200); Start-Sleep -Seconds 3
    # the keyboard on the library's own tab in the top bar (out of the page: no card for the refresh to put it back on)
    $tab = Find-Ui -Name $fixture.movie.library -Type 'TabItem'
    if (-not $tab) { $tab = Find-Ui -Name $fixture.movie.library -Type 'ListItem' }
    if ($tab) { [void][OffscreenUi]::SetFocus($tab); Start-Sleep -Milliseconds 600 }
    $where = CK-Focus
    $at = Get-Epoch
    foreach ($n in 1..3) { CK-Key 'R'; Start-Sleep -Milliseconds 2500 }
    Start-Sleep -Seconds 2
    $loads = AR-Refreshes $at
    $joins = @(CK-Log $at 'this key joins it').Count
    Check "R three times 2.5 s apart (the keyboard on $where): three loads, none joined" ($loads -eq 3 -and $joins -eq 0) "loads $loads, joins $joins; $((CK-Log $at '^Refresh: ') -join ' | ')"
    Start-Sleep -Seconds 2
    $at = Get-Epoch
    CK-Key 'R'; Start-Sleep -Milliseconds 150; CK-Key 'R'
    Start-Sleep -Seconds 4
    $loads = AR-Refreshes $at
    Check 'R, R 150 ms apart: one load (the second joins it)' ($loads -eq 1) "loads $loads; $((CK-Log $at '^Refresh: ') -join ' | ')"
}
AR-Latency
