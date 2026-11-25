# Nixie 1.6, the collections stream: what the user sees of Nixie's collections in the current NIXIE_LAYOUT theme, with
# FAKESERVER_COLLECTIONS=1 (the admin's box sets, one with its franchise's TMDb id leaving an owned film out). Run with:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:FAKESERVER_COLLIDE='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
# (1.7.3: FAKESERVER_COLLIDE adds a 13th box set, so the server's own row ends in See all: since 1.7.3 no Made by Nixie row on
# the fake server has more than 12 tiles, Directors and Actors being retired, and R1 below needs a See all)
#   run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-160-collections.ps1
# Covers: Home's Made by Nixie rows (after New in ..., filled once the library index is in); the Libraries page's "Collections &
# playlists" group with "Made by Nixie - N"; its tile opening the Collections page at Made by Nixie (the keyboard on its first
# tile); a Nixie franchise's Collection page (the eyebrow, the why line, the mosaic, Save as playlist and its answer, and the
# server's no); the admin's box set with "Also in your library"; Back then the same place again (1.6.1, R1: the library
# tabs, a See all, the Collections tab); Settings > Collections. Shots <tag>-<name>-<width>.png at
# 1920 and 1100 (NIXIE_THEME / SG_SHOT_TAG name them). Dot-sourced by drive.ps1: every name here starts with co.

$coTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
if ($env:NIXIE_THEME) { $coTag += '-' + $env:NIXIE_THEME.ToLowerInvariant() }
$coWidths = if ($env:CO_WIDTHS) { @($env:CO_WIDTHS -split ',' | ForEach-Object { [int]$_ }) } else { @(1920, 1100) }

function Co-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function Co-Size([int]$W) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $(if ($W -ge 1920) { 1080 } else { 900 })); Co-Settle 1500 }
function Co-Shots([string]$Name, [scriptblock]$Before) {
    foreach ($w in $coWidths) {
        Co-Size $w
        if ($Before) { & $Before }
        Shot "$coTag-$Name-$w"
    }
    Co-Size 1920
}
function Co-Focus { $coF = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($coF) { $coF | ConvertFrom-Json } else { $null } }

# --- Home: the Made by Nixie rows, after New in ... (the index is read in the background on a first visit) -----------
# 1.7.3: the acclaimed pick's row, found by its role: "Highly rated, not watched" until 1.7.2, "Acclaimed, not seen yet" from
# 1.7.3 (stream M2 renamed it), so the check holds on either side of that change
# 1.8 gate fix: stream l's weekly lineup chooses Home's Made by Nixie rows (2 by default: the best personal row, then a
# seasonal, trending or Spotlight one; Acclaimed only when no canon list is on), so any of them counts: the row's reason line
# (Classic's and Cinematic's NixieRowReason; Drive-in's and Editorial's caption "Made by Nixie <middle dot> ..."), or 1.7's
# acclaimed row by name on a 1.7 build. then-180-tiles.ps1 checks how many and what they say.
$coPickNames = @('Acclaimed, not seen yet', 'Highly rated, not watched')
$coRow = Wait-Until {
    $r = Find-Ui -Id 'NixieRowReason' -Type 'Text' -OffscreenToo
    if (-not $r) { $r = Find-Ui -Name ('Made by Nixie ' + [char]0x00B7 + ' ') -Contains -Type 'Text' -OffscreenToo }
    if (-not $r) { $r = $coPickNames | ForEach-Object { Find-Ui -Name $_ -Contains -OffscreenToo } | Select-Object -First 1 }
    $r
} -Seconds 60 -PollMs 300
Check "collections ($coTag): Home shows a Made by Nixie row" ([bool]$coRow) $(if ($coRow) { $coRow.Current.Name } else { "no row's reason line, none of: $($coPickNames -join ' | ')" })
$coKicker = Find-Ui -Name 'Made by Nixie' -Contains -OffscreenToo
if (-not $coKicker) { $coKicker = Find-Ui -Name 'MADE BY NIXIE' -OffscreenToo }
Check "collections ($coTag): the row says it is Nixie's" ([bool]$coKicker) ''
Co-Shots 'home-nixie' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Co-Settle 900 }

# --- the Libraries page: the group, with Made by Nixie - N ----------------------------------------------------------
Open-Nav 'Libraries'
$coMade = Wait-Until { Find-Ui -Name 'Made by Nixie, ' -Contains -Type 'ListItem' } -Seconds 30 -PollMs 300
Check "collections ($coTag): the Libraries page has Made by Nixie, N" ([bool]$coMade) $(if ($coMade) { $coMade.Current.Name } else { 'none' })
Co-Shots 'libraries' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Co-Settle 600 }

# --- its tile: the Collections page at Made by Nixie, the keyboard on its first tile ---------------------------------
# 1.6.1 (the integration's "Editorial lands on the Playlists row"): found again after the shots. The shots resize the window
# (1920, 1100, 1920) and the group's ItemsView hands its containers to other tiles: the tile found before them could be
# Playlists' by then, and Invoke opened the Collections page at Playlists, whose first tile ("Nothing Yet") took the keyboard
if ($coMade) { $coMade = Wait-Until { Find-Ui -Name 'Made by Nixie, ' -Contains -Type 'ListItem' } -Seconds 10 -PollMs 300 }
if ($coMade) {
    Say "Made by Nixie tile '$($coMade.Current.Name)' ($(Invoke-Element $coMade))"
    # (1.6 integration: the page's title element, not any Text named Collections: the top bar's tab is one too; 1.6.1:
    # -OffscreenToo, the page scrolls down to Made by Nixie's rows and its title leaves the view)
    $coTitle = Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 20
    Check "collections ($coTag): the tile opens the Collections page" ([bool]$coTitle) ''
    $coPicks = Wait-Until { Find-Ui -Name 'Picks' -Contains } -Seconds 20 -PollMs 200
    Check "collections ($coTag): its Made by Nixie rows are there" ([bool]$coPicks) ''
    Co-Settle 1200
    $coF = Co-Focus
    Check "collections ($coTag): arriving at Made by Nixie, the keyboard is on a Nixie tile" ([bool]($coF -and $coF.name -like '*made by Nixie*')) "$($coF.type) '$($coF.name)'"
    Co-Shots 'collections-nixie'
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Co-Settle 600
    Co-Shots 'collections-top' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Co-Settle 600 }
}

# --- a Nixie franchise on the Collection page -----------------------------------------------------------------------
$coFranchise = Wait-Until { Find-Ui -Name ', made by Nixie, franchise, ' -Contains -OffscreenToo } -Seconds 10
if ($coFranchise) {
    $coName = $coFranchise.Current.Name
    Say "franchise '$coName' ($(Invoke-Element $coFranchise))"
    # (ASCII only in this file: Windows PowerShell reads a .ps1 without a BOM in the ANSI code page)
    $coEyebrow = Wait-Until { $e = Find-Ui -Name 'Made by Nixie ' -Contains -Type 'Text'; if ($e -and $e.Current.Name -like '*Franchise*') { $e } } -Seconds 5 -PollMs 200
    # the eyebrow and the why line are the colpage stream's header, filled through its hook (CollectionViewModel's
    # "1.6 collections" block); 1.6 integration: checked now the two branches are merged (NIXIE_COLPAGE_SEAMS on)
    Check "collections ($coTag): the header's eyebrow says Made by Nixie, Franchise" ([bool]$coEyebrow) "$(if ($coEyebrow) { $coEyebrow.Current.Name })"
    $coWhy = Wait-Until { Find-Ui -Name 'Films that TMDb lists in one collection' -Contains -Type 'Text' } -Seconds 3 -PollMs 200
    Check "collections ($coTag): the header's why line says where it came from" ([bool]$coWhy) ''
    $coSave = Wait-Ui -Name 'Save as playlist' -Type 'Button' -Seconds 10
    Check "collections ($coTag): Save as playlist is there" ([bool]$coSave) ''
    Co-Shots 'nixie-collection'
    # the server's no, in Nixie's words; then made, with Open
    [void](Invoke-Server '/__harness/playlists' 'POST' @{ refuse = 403 })
    if ($coSave) { [void](Invoke-Element $coSave) }
    $coNo = Wait-Until { Find-Ui -Name "doesn't let your account make playlists" -Contains -Type 'Text' } -Seconds 10
    Check "collections ($coTag): a refused playlist is said in Nixie's words" ([bool]$coNo) ''
    if ($coWidths -contains 1920) { Co-Size 1920; Shot "$coTag-playlist-refused-1920" }
    [void](Invoke-Server '/__harness/playlists' 'POST' @{ refuse = 0 })
    $coSave = Find-Ui -Name 'Save as playlist' -Type 'Button'
    if ($coSave) { [void](Invoke-Element $coSave) }
    $coYes = Wait-Until { Find-Ui -Name 'Saved to your playlists as' -Contains -Type 'Text' } -Seconds 10
    Check "collections ($coTag): Save as playlist makes it and says so" ([bool]$coYes) $(if ($coYes) { $coYes.Current.Name } else { '' })
    $coMade = @(Get-Requests | Where-Object { $_.path -match '^/Playlists$' -and $_.method -eq 'POST' -and $_.status -eq 200 })
    Check "collections ($coTag): one playlist made (a second click makes no copy)" ($coMade.Count -eq 1) "$($coMade.Count) made"
    if ($coWidths -contains 1920) { Shot "$coTag-playlist-saved-1920" }
    # 1.6.1 (the merge review's R5): the harness's real Back is BrowserBack (0xA6), as drive.ps1's leave; 0x08 is Backspace
    Key 0xA6
    # (-OffscreenToo: Back puts the page where it was, scrolled down to the franchise's row, its title above the view)
    $coBack = Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 15
    Check "collections ($coTag): Back from the franchise shows the Collections page" ([bool]$coBack) ''
    Co-Settle 1000
    # 1.6.1 (bug hunt BH-160-m2): a Nixie collection of shows only ("Unfinished series") doesn't offer Save as playlist
    $coShows = Wait-Until { Find-Ui -Name 'Unfinished series, made by Nixie' -Contains -OffscreenToo } -Seconds 5 -PollMs 300
    # 1.8 gate fix: Back puts the page where it was, at the franchise's row, and 1.8's page has This week and Picks (where the
    # Unfinished series tile is) above it: a row's tiles are made only near the view (its ItemsRepeater), so the tile is looked
    # for from the page's top down, as a user scrolls up to it
    for ($coPct = 0; -not $coShows -and $coPct -le 100; $coPct += 10) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $coPct); Co-Settle 500
        $coShows = Find-Ui -Name 'Unfinished series, made by Nixie' -Contains -OffscreenToo
    }
    if ($coShows) {
        try { $coSp = $null; if ($coShows.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$coSp)) { $coSp.ScrollIntoView() } } catch { }
        Say "Unfinished series ($(Invoke-Element $coShows))"
        $coShowsPage = Wait-Ui -Id 'TitleText' -Name 'Unfinished series' -Seconds 15
        Co-Settle 1500
        $coShowsSave = Find-Ui -Name 'Save as playlist' -Type 'Button' -OffscreenToo
        Check "collections ($coTag): a collection of shows only doesn't offer Save as playlist" ([bool]$coShowsPage -and -not $coShowsSave) "page: $([bool]$coShowsPage); Save shown: $([bool]$coShowsSave)"
        Key 0xA6
        [void](Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 15)
        Co-Settle 800
    }
    else { Check "collections ($coTag): an Unfinished series tile" $false 'none on the Collections page' }
}
else { Check "collections ($coTag): a franchise tile" $false 'none on the Collections page' }

# --- the admin's box set: Also in your library (the Libraries page's Collections tile: the Collections page at the server's) --
Open-Nav 'Libraries'
$coServer = Wait-Until { Find-Ui -Name 'Collections, ' -Contains -Type 'ListItem' } -Seconds 15 -PollMs 200
if ($coServer) {
    Say "Collections tile ($(Invoke-Element $coServer))"
    Co-Settle 1500
    $coF = Co-Focus
    Check "collections ($coTag): the Collections tile opens the page at the server's collections" ([bool]($coF -and $coF.name -like '*, collection*')) "$($coF.type) '$($coF.name)'"
}
$coBox = Wait-Until { Find-Ui -Name 'Kestrel Bay Trilogy, collection' -Contains -OffscreenToo } -Seconds 15
if ($coBox) {
    Say "box set ($(Invoke-Element $coBox))"
    $coAlso = Wait-Until { Find-Ui -Name 'Also in your library' -Contains -Type 'Text' -OffscreenToo } -Seconds 20 -PollMs 200
    Check "collections ($coTag): the admin's box set shows the owned film it leaves out" ([bool]$coAlso) $(if ($coAlso) { $coAlso.Current.Name } else { 'none' })
    Co-Shots 'boxset-also' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Co-Settle 700 }
}
else { Check "collections ($coTag): the admin's box set tile" $false 'none' }

# --- Nixie 1.6.1 (the merge review's R1): Back, then the same place again, opens it (the frame's refusal asked about the
# last page opened, not the one Back showed). The library tabs, a See all, and the Collections tab from a See all. The
# Back key is the harness's real one, 0xA6 (BrowserBack). (The merge review's then-rp.ps1, kept here.)
function Co-Title { $t = Find-Ui -Id 'TitleText' -OffscreenToo; if ($t) { $t.Current.Name } else { '<none>' } }
function Co-Grid { Wait-Until { $n = Co-Title; if ($n -ne 'Collections' -and $n -ne '<none>') { $n } } -Seconds 15 -PollMs 200 }
$coM = $fixture.movie.library; $coS = $fixture.series.library
Open-Nav $coM
Check "collections ($coTag): R1 the $coM tab opens $coM" ([bool](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200)) ''
Open-Nav $coS
Check "collections ($coTag): R1 the $coS tab opens $coS" ([bool](Wait-Until { Find-MediaCard $fixture.series.name } -Seconds 30 -PollMs 200)) ''
Key 0xA6
Check "collections ($coTag): R1 Back shows $coM again" ([bool](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 20 -PollMs 200)) ''
Co-Settle 1200
Open-Nav $coS
Check "collections ($coTag): R1 the $coS tab after Back opens $coS" ([bool](Wait-Until { Find-MediaCard $fixture.series.name } -Seconds 15 -PollMs 200)) ''
Open-Nav 'Collections'
Check "collections ($coTag): R1 the Collections tab opens the Collections page" ([bool](Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 20)) (Co-Title)
# 1.7.3: any row's See all, found by its role (a row of more than 12 tiles, or Awards): with FAKESERVER_COLLIDE the server's own
# row (13 box sets) has one in every version; until 1.7.2 Directors and Actors had one too
$coSee = Wait-Until { Find-Ui -Name 'See all' -Contains -Type 'Hyperlink' -OffscreenToo } -Seconds 60 -PollMs 400
if (-not $coSee) { $coSee = Find-Ui -Name 'See all' -Contains -Type 'Button' -OffscreenToo }
Check "collections ($coTag): R1 the Collections page has a See all" ([bool]$coSee) $(if ($coSee) { $coSee.Current.Name } elseif (-not $env:FAKESERVER_COLLIDE) { 'none: run with FAKESERVER_COLLIDE=1 (13 box sets: the server''s row ends in See all)' } else { 'none' })
if ($coSee) {
    $coSeeName = $coSee.Current.Name
    Say "R1: '$coSeeName' ($(Invoke-Element $coSee))"
    $coGrid = Co-Grid
    Check "collections ($coTag): R1 See all opens its grid" ([bool]$coGrid) "title '$(Co-Title)'"
    Key 0xA6
    Check "collections ($coTag): R1 Back shows the Collections page" ([bool](Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 15)) "title '$(Co-Title)'"
    Co-Settle 1200
    $coSee = Wait-Until { Find-Ui -Name $coSeeName -OffscreenToo } -Seconds 15 -PollMs 300
    if ($coSee) { Say "R1: '$coSeeName' again ($(Invoke-Element $coSee))" }
    $coGrid = Co-Grid
    Check "collections ($coTag): R1 the same See all after Back opens its grid" ([bool]$coGrid) "title '$(Co-Title)'"
    if ($coGrid) {
        Open-Nav 'Collections'
        Check "collections ($coTag): R1 the Collections tab from a See all opens the Collections page" ([bool](Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 15)) "title '$(Co-Title)'"
        Key 0xA6
        Check "collections ($coTag): R1 Back shows the See all" ([bool](Co-Grid)) "title '$(Co-Title)'"
        Co-Settle 1200
        Open-Nav 'Collections'
        Check "collections ($coTag): R1 the Collections tab again opens the Collections page" ([bool](Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 15)) "title '$(Co-Title)'"
    }
}

# --- Nixie 1.6.1 (bug hunt BH-160-M2), with FAKESERVER_COLLIDE=1 ("Winter Films (4K)" beside "Winter Films"): two names
# that would read the same keep their server names in search (its cards and its top result), not on the Collections page only
if ($env:FAKESERVER_COLLIDE) {
    . (Join-Path $PSScriptRoot 'search-common.ps1')
    SrSearch 'Winter Films'
    $coCards = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -like 'Winter Films*' })
    $coTop = @(SrTexts | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like 'Winter Films*' })
    $coSaid = @($coCards + $coTop | ForEach-Object { ($_ -split ', ')[0] } | Sort-Object -Unique)
    Check "collections ($coTag): search says both Winter Films and Winter Films (4K)" (($coSaid -contains 'Winter Films') -and ($coSaid -contains 'Winter Films (4K)')) "cards: $($coCards -join ' | '); texts: $($coTop -join ' | ')"
    Shot "$coTag-collide-search-1920"
}

# --- Settings > Collections (a shared page: one theme is enough, CO_SETTINGS=1) ----------------------------------------
if ($env:CO_SETTINGS) {
    Open-Nav 'Settings'
    $coTidy = Wait-Until { Find-Ui -Name 'Tidy collection names' -Type 'Button' -Contains } -Seconds 15
    if (-not $coTidy) { $coTidy = Find-Ui -Name 'Tidy collection names' -Contains -OffscreenToo }
    Check "collections ($coTag): Settings has Tidy collection names" ([bool]$coTidy) ''
    if ($coTidy) { [void][OffscreenUi]::SetFocus($coTidy); Co-Settle 800 }
    Shot "$coTag-settings-collections-1920"
    # 1.7.3 fix round (BH-173-m2): the library filters' switches are back, each named for Narrator
    $coFilters = Wait-Until { Find-Ui -Id 'NixieLibraryFiltersExpander' -OffscreenToo } -Seconds 10 -PollMs 250
    Check "collections ($coTag): Settings has the library filters' switches" ([bool]$coFilters) ''
    if ($coFilters) {
        [void][OffscreenUi]::SetFocus($coFilters); Co-Settle 600
        $coCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
        $coToggle = if ($coFilters.GetSupportedPatterns() -contains [System.Windows.Automation.ExpandCollapsePattern]::Pattern) { $coFilters } else { $coFilters.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $coCond) }
        try { $coToggle.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Co-Settle 900 } catch { Say "collections: could not expand the library filters: $($_.Exception.Message)" }
        $coSwitches = @('Decades', 'Genre mixes', 'Countries' | Where-Object { Find-Ui -Name $_ -Type 'Button' -OffscreenToo })
        Check "collections ($coTag): the library filters' three switches show (Decades, Genre mixes, Countries)" ($coSwitches.Count -eq 3) ($coSwitches -join ', ')
        Shot "$coTag-settings-library-filters-1920"
    }
}
