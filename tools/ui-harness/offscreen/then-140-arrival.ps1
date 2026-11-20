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
$arOnly = if ($env:AR_ONLY) { $env:AR_ONLY -split ',' } else { @('untouched', 'collection', 'film', 'library') }

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
AR-Latency
