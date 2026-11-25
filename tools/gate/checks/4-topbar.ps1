# The basics gate (Nixie 1.7.5, class 4, g3): the top bar lights the section the page belongs to, every top-bar entry opens
# its page, the way up from an episode, a season and a series, Back from every page, and search (type, clear, open, Back),
# at each of GATE_SIZES and after a live resize. What the user sees: the page shown (its own heading, crumb or button) and
# the bar's lit tab (the selected tab, drawn lit; in a narrow window the one menu button, named after the lit page).
# The friend's 1.7.x report: "Home" lit over a season page reached from Home.
#   Kind then; FAKESERVER_REAL_SHAPE=1, FAKESERVER_COLLECTIONS=1 (the real-shaped series in progress, the 40-film box set) and
#   FAKESERVER_BIG_LIBRARY=5000 (a big library in the Libraries menu: End reaches its last title, in view);
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then tools\gate\checks\4-topbar.ps1 (NIXIE_LAYOUT the theme).
# Expected lit tab: Home on Home only; a library's tab on the library and on what is opened from it or from Home (a show's
# pages: Shows); Collections on the Collections page and what is opened from it; Libraries on the Libraries page; the
# Libraries tab named after a library without a tab of its own; nothing for the search results, an item opened from them,
# and Settings.
# Dot-sourced by drive.ps1: every name here starts with tb4 / Tb4-.
. (Join-Path $PSScriptRoot 'g3-common.ps1')
$tb4Id = if ($env:GATE_ID) { $env:GATE_ID } else { 'topbar' }
$tb4Real = if ($fixture.PSObject.Properties['real_shape']) { $fixture.real_shape } else { $null }
if (-not $tb4Real) { Check "${tb4Id}: the fake server has the real-shaped titles (FAKESERVER_REAL_SHAPE=1)" $false 'the fixture has no real_shape'; return }
$tb4Series = $tb4Real.series.name
$tb4Ep = $tb4Real.in_progress
$tb4Film = $fixture.movie.name
$tb4Box = if ($tb4Real.collection) { $tb4Real.collection.name } else { $null }
$tb4Case = ''

# (PowerShell's names ignore case: a local "$lit" was the parameter "$Lit" itself, and every lit check passed whatever was
# lit: the 1.7.3 revert run caught it. $Want is the tab wanted, $litNow the one lit.)
function Tb4-Expect([string]$Where, [scriptblock]$Shown, [string]$Want, [double]$Seconds = 20) {
    $pageOk = G3-Wait $Shown $Seconds
    # the bar follows the page within a moment (an item's library is known after its page)
    # (1.7.5 gate fix: the tab lit for the page before can still show as the new page arrives, then the bar is unlit for the
    # moment its library takes, then lit again: the verify run's Cinematic at max read 'Shows', then '' in that moment, while
    # the shot 0.1 s later showed Shows lit. What is lit is taken once it holds for two looks in a row)
    $litNow = G3-Settled { G3-Lit } $Want 6 300
    $pass = $pageOk -and $litNow -eq $Want
    G3-Check $tb4Id "$tb4Case, $Where" "the page shows, the bar lights $(if ($Want) { "'$Want'" } else { 'no tab' })" $pass "page shown: $pageOk; lit: '$litNow'; tabs: $(G3-TabNames)"
    if (-not $pass) { Shot (Safe-Name "$tb4Id-$g3Theme-$tb4Case-$Where") }
    $pageOk
}
# the episode in progress on Home (Continue watching's first card), opened
function Tb4-OpenEpisodeFromHome {
    $opened = G3-OpenInProgress $tb4Series
    if ($opened) { Say "${tb4Id}: from Home '$opened'"; return $true }
    # what Home has of the show, for the reason (any element named after it)
    $seen = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | ForEach-Object { try { "$($_.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '') '$($_.Current.Name)'" } catch { } } |
            Where-Object { $_ -like "*$tb4Series*" } | Select-Object -Unique -First 4)
    G3-Check $tb4Id $tb4Case "Home shows the episode in progress ($tb4Series S$($tb4Ep.season) E$($tb4Ep.index))" $false "no card nor feature of it on Home; named after the show: $(if ($seen) { $seen -join ' | ' } else { 'nothing' })"
    $false
}
function Tb4-OnEpisode { $s = Find-Ui -Id 'TrailSeason'; $s -and $s.Current.Name -eq "Go to Season $($tb4Ep.season)" }

foreach ($tb4Size in (G3-Sizes)) {
    $tb4Case = if ($tb4Size) { $tb4Size } else { 'as opened' }
    $tb4Got = G3-Size $tb4Size
    Say "${tb4Id} ($g3Theme): window $tb4Case -> $tb4Got"

    # ---- 1. every entry of the bar opens its page and lights itself ------------------------------------------------------------
    Open-Nav 'Home'
    [void](Tb4-Expect 'Home tab' { G3-OnHome } 'Home')
    $tb4Libs = @(G3-Tabs | ForEach-Object { $_.Current.Name } | Where-Object { $_ -notin @('Home', 'Collections') -and $_ -notlike 'Libraries*' -and $_ -ne 'Menu' })
    if (-not $tb4Libs.Count -and (Find-Ui -Id 'TopTabsMenu')) { $tb4Libs = @('Movies', 'Shows') }   # (a narrow bar: its menu has them)
    foreach ($tb4Lib in $tb4Libs) { Open-Nav $tb4Lib; [void](Tb4-Expect "$tb4Lib tab" { G3-OnLibrary } $tb4Lib) }
    Open-Nav 'Collections'
    [void](Tb4-Expect 'Collections tab' { G3-OnTitle 'Collections' } 'Collections')
    Open-Nav 'Libraries'
    [void](Tb4-Expect 'Libraries > All libraries' { G3-OnTitle 'Libraries' } 'Libraries')
    # a library without a tab, from the Libraries menu: the Libraries tab is named after it, lit
    $tb4Other = @($fixture.views | Where-Object { $_ -notin $tb4Libs -and $_ -notin @('Collections', 'Playlists') }) | Select-Object -First 1
    if ($tb4Other) { Open-Nav $tb4Other; [void](Tb4-Expect "Libraries > $tb4Other" { G3-OnLibrary } $tb4Other) }
    Open-Nav 'Settings'
    [void](Tb4-Expect 'Account > Settings' { G3-OnSettings } '')
    G3-Back
    [void](Tb4-Expect 'Back from Settings' { -not (G3-OnSettings) } $(if ($tb4Other) { $tb4Other } else { 'Libraries' }))

    # ---- 2. a show's pages reached from Home: the show's library, never Home; the way up; Back to Home ---------------------------
    # (each step from the one before: a step that fails ends the walk there, back on Home, rather than pressing Back through
    # the pages before it)
    $tb4Walk = Tb4-OpenEpisodeFromHome
    if ($tb4Walk) {
        $tb4Walk = Tb4-Expect 'episode from Home' { Tb4-OnEpisode } 'Shows' 25
        if (-not $tb4Walk) {
            # the way up must be in view: the crumbs there but scrolled under the bar is no way up
            $tb4Hidden = Find-Ui -Id 'TrailSeason' -OffscreenToo
            $tb4R = if ($tb4Hidden) { $tb4Hidden.Current.BoundingRectangle } else { $null }
            G3-Check $tb4Id $tb4Case 'the episode page shows its way up (the season crumb in view)' $false $(if ($tb4Hidden) { "the crumb '$($tb4Hidden.Current.Name)' is there but not in view (offscreen: $($tb4Hidden.Current.IsOffscreen); rect $(if ($tb4R -and -not $tb4R.IsEmpty) { "$([int]$tb4R.X),$([int]$tb4R.Y) $([int]$tb4R.Width)x$([int]$tb4R.Height)" } else { 'none' }))" } else { 'no season crumb on the page' })
        }
    }
    $tb4Steps = @(
        @{ Where = "way up: episode > Season $($tb4Ep.season)"; Do = { $c = Find-Ui -Id 'TrailSeason'; if ($c) { [void](Invoke-Element $c) } }; Shown = { G3-OnSeason $tb4Series $tb4Ep.season } }
        @{ Where = 'way up: season > series'; Do = { $c = Find-Ui -Id 'TrailSeries'; if ($c) { [void](Invoke-Element $c) } }; Shown = { G3-OnSeries $tb4Series } }
        # the way up from the series: its lit tab (or the eyebrow, when its library has no tab: section 4)
        @{ Where = 'way up: series > Shows (its lit tab)'; Do = { $t = @(G3-Tabs | Where-Object { $_.Current.Name -eq 'Shows' }) | Select-Object -First 1; if ($t) { [void](Invoke-Element $t) } else { Open-Nav 'Shows' } }; Shown = { G3-OnLibrary } }
        @{ Where = 'Back: the series'; Do = { G3-Back }; Shown = { G3-OnSeries $tb4Series } }
        @{ Where = 'Back: the season'; Do = { G3-Back }; Shown = { G3-OnSeason $tb4Series $tb4Ep.season } }
        # a live resize on the season page: the bar still shows the lit tab
        @{ Where = 'the season after a live resize'; Do = { G3-LiveResize }; Shown = { G3-OnSeason $tb4Series $tb4Ep.season } }
        @{ Where = 'Back: the episode'; Do = { G3-Back }; Shown = { Tb4-OnEpisode } }
        @{ Where = 'Back: Home'; Do = { G3-Back }; Shown = { G3-OnHome }; Want = 'Home' }
    )
    foreach ($tb4Step in $tb4Steps) {
        if (-not $tb4Walk) { G3-Check $tb4Id "$tb4Case, $($tb4Step.Where)" 'not tried: the step before it failed' $false ''; continue }
        & $tb4Step.Do
        $tb4Walk = Tb4-Expect $tb4Step.Where $tb4Step.Shown $(if ($tb4Step.Want) { $tb4Step.Want } else { 'Shows' })
    }
    if (-not $tb4Walk) { Open-Nav 'Home'; G3-Settle 1500 }

    # ---- 3. a film from its library: the library stays lit; Back -------------------------------------------------------------
    Open-Library $fixture.movie.library
    [void](G3-Wait { G3-OnLibrary } 20)
    Open-Card $tb4Film
    [void](Tb4-Expect 'film from Movies' { G3-OnItem $tb4Film } $fixture.movie.library)
    G3-Back
    [void](Tb4-Expect 'Back: Movies' { G3-OnLibrary } $fixture.movie.library)

    # ---- 4. search: type, results, clear, open a result, Back; the way up from a series whose library has no tab ---------------
    # (the film's own words: "lighthouse" alone has more films than the results' Movies group shows)
    $tb4Q = 'altogether unlikely'
    G3-Type $tb4Q
    $tb4Typed = G3-BoxText
    Key 0x0D
    $tb4On = Tb4-Expect "search results for '$tb4Q'" { G3-OnResults } ''
    G3-Check $tb4Id $tb4Case 'search: the box keeps the words typed' ($tb4Typed -eq $tb4Q -and (G3-BoxText) -eq $tb4Q) "typed: '$tb4Typed'; now: '$(G3-BoxText)'"
    # clear (Ctrl+A, Delete): the box empties, the results stay, nothing else happens
    G3-Clear
    G3-Settle 800
    G3-Check $tb4Id $tb4Case 'search: cleared, the box is empty and the results stay' (-not (G3-BoxText) -and (G3-OnResults) -and (G3-Lit) -eq '') "box: '$(G3-BoxText)'; results shown: $(G3-OnResults); lit: '$(G3-Lit)'"
    $tb4Hit = if ($tb4On) { Wait-Until { Find-MediaCard $tb4Real.film.name } -Seconds 10 -PollMs 250 } else { $null }
    G3-Check $tb4Id $tb4Case 'search: the long-titled film is among the results' ([bool]$tb4Hit) $tb4Real.film.name
    if ($tb4Hit) {
        Say "${tb4Id}: result ($(Invoke-Element $tb4Hit))"
        $tb4Full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
        if ($tb4Full) { [void](Invoke-Element $tb4Full) }
        [void](Tb4-Expect 'a film opened from the search results' { G3-OnItem $tb4Real.film.name } '')
        G3-Back
        [void](Tb4-Expect 'Back: the search results' { (G3-OnResults) -and [bool](Find-Ui -Name $tb4Q -Type 'Text' -Contains) } '')
        G3-Check $tb4Id $tb4Case 'search: Back shows the same results' ([bool](Wait-Until { Find-MediaCard $tb4Real.film.name } -Seconds 8 -PollMs 250)) ''
    }
    # an anime series (no tab of its own) from the results: nothing lit, its eyebrow names the library and opens it
    if ($fixture.anime) {
        G3-Type $fixture.anime.name
        Key 0x0D
        [void](G3-Wait { G3-OnResults } 15)
        $tb4A = Wait-Until { Find-MediaCard $fixture.anime.name } -Seconds 10 -PollMs 250
        if ($tb4A) {
            [void](Invoke-Element $tb4A)
            $tb4Full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
            if ($tb4Full) { [void](Invoke-Element $tb4Full) }
            [void](Tb4-Expect 'a series from the search results' { G3-OnSeries $fixture.anime.name } '')
            $tb4Eye = Wait-Until { Find-Ui -Id 'TrailEyebrow' } -Seconds 6 -PollMs 200
            G3-Check $tb4Id $tb4Case "way up: the series' eyebrow names its library ($($fixture.anime.library))" ($tb4Eye -and $tb4Eye.Current.Name -match [regex]::Escape($fixture.anime.library)) "eyebrow: '$(if ($tb4Eye) { $tb4Eye.Current.Name })'"
            if ($tb4Eye) {
                [void](Invoke-Element $tb4Eye)
                [void](Tb4-Expect "way up: series > $($fixture.anime.library) (the eyebrow)" { G3-OnLibrary } $fixture.anime.library)
            }
        }
        else { G3-Check $tb4Id $tb4Case "search: '$($fixture.anime.name)' is found" $false 'no result card' }
    }

    # ---- 5. Collections: the page, a collection, a film of it: Collections stays lit; Back, Back ---------------------------------
    if ($tb4Box) {
        Open-Nav 'Collections'
        [void](G3-Wait { G3-OnTitle 'Collections' } 20)
        $tb4C = $null
        foreach ($tb4Pct in 0, 30, 60, 100) {
            [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $tb4Pct); G3-Settle 700
            $tb4C = Find-MediaCard $tb4Box
            if ($tb4C) { break }
        }
        if ($tb4C) {
            [void](Invoke-Element $tb4C)
            [void](Tb4-Expect 'a collection from Collections' { (G3-OnTitle $tb4Box) -or (G3-OnItem $tb4Box) } 'Collections')
            G3-Back
            [void](Tb4-Expect 'Back: Collections' { G3-OnTitle 'Collections' } 'Collections')
        }
        else { G3-Check $tb4Id $tb4Case "Collections shows '$tb4Box'" $false 'no tile' }
    }

    # ---- 6. a big library (FAKESERVER_BIG_LIBRARY, "Archive", in the Libraries menu): lit; End reaches its last title, in view --
    $tb4Big = @($fixture.big) | Where-Object { $_ -and $_.count } | Select-Object -First 1
    if ($tb4Big) {
        Open-Nav $tb4Big.name
        [void](Tb4-Expect "Libraries > $($tb4Big.name) ($($tb4Big.count) titles)" { G3-OnLibrary } $tb4Big.name 30)
        $tb4First = Wait-Until { Find-MediaCard $tb4Big.first } -Seconds 30 -PollMs 250
        if ($tb4First) {
            [void][OffscreenUi]::SetFocus($tb4First); G3-Settle 600
            $tb4At = Get-Date
            Key 0x23
            $tb4Last = Wait-Until { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f -and ($f | ConvertFrom-Json).name -like "$($tb4Big.last)*") { $f } } -Seconds 30 -PollMs 300
            $tb4Ms = [int]((Get-Date) - $tb4At).TotalMilliseconds
            G3-Settle 800
            $tb4Card = Find-MediaCard $tb4Big.last
            $tb4R = if ($tb4Card) { $tb4Card.Current.BoundingRectangle } else { $null }
            $tb4W = [G3Win]::Size($script:hwnd)
            $tb4In = $tb4R -and -not $tb4Card.Current.IsOffscreen -and -not $tb4R.IsEmpty -and $tb4R.Bottom -le $tb4W[1] + 8 -and $tb4R.Top -ge 0
            G3-Check $tb4Id $tb4Case "$($tb4Big.name): End takes the keyboard to its last title, in view" ([bool]$tb4Last -and $tb4In) "$($tb4Big.count) titles; the keyboard on the last after $tb4Ms ms: $([bool]$tb4Last); its card $(if ($tb4R) { "at $([int]$tb4R.X),$([int]$tb4R.Y) $([int]$tb4R.Width)x$([int]$tb4R.Height)" } else { 'not drawn' }); last '$($tb4Big.last)'"
            if (-not ($tb4Last -and $tb4In)) { Shot (Safe-Name "$tb4Id-$g3Theme-$tb4Case-big-end") }
            Key 0x24; G3-Settle 1500
        }
        else { G3-Check $tb4Id $tb4Case "$($tb4Big.name) shows its first title" $false $tb4Big.first }
    }
}
