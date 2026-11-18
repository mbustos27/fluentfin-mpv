# 1.2.0 items stream: the names of a card's hover buttons and of the item pages' buttons, Play on a series and a
# season naming what it starts, "Your rating" in the action row, the hero's 24 px dots; with screenshots at 1920 and 1100
# wide. Run with -Script home -Then offscreen\then-items.ps1, once per theme and glass mode: SEAGLASS_THEME=Light|Dark in
# the environment, -Setting Glass=Full|Solid, SG_SHOT_TAG naming the shots (e.g. light-solid); SG_ITEMS_RESUME=1 also plays
# the series' next episode for a moment (Play on the series page), leaves, and shoots "Resume S1 E4" with its progress.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names (PowerShell names ignore case: $out is its -Out).
$itTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
function It-Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
function It-Widths([string]$Name, [scriptblock]$Before) {
    foreach ($size in @(@(1920, 1080), @(1100, 900))) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); It-Settle 1500
        if ($Before) { & $Before }
        Shot "$itTag-$Name-$($size[0])"
    }
}
function It-Rect($element) {
    $r = [OffscreenUi]::RectOf($element)
    if ($r.IsEmpty -or [double]::IsInfinity($r.Width) -or [double]::IsInfinity($r.Height)) { return 'no rect' }
    "{0}x{1}" -f [int]$r.Width, [int]$r.Height
}
$itAny = [System.Windows.Automation.Condition]::TrueCondition
# the series page: its own Watched ("Mark <series> as ..."); the season page's is "Mark Season 1 of <series> as ..."
function It-OnSeries { [bool][OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.series.name) as ", '') }
function It-Where([string]$Prefix, [string]$Type) {
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $itAny) | Where-Object {
        $n = $_.Cached.Name
        $n -and $n.StartsWith($Prefix, [StringComparison]::Ordinal) -and (-not $Type -or $_.Cached.ControlType.ProgrammaticName -eq "ControlType.$Type")
    })
}
# the first media card in the page's order (on Home: Continue Watching's first)
function It-FirstCard {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'BaseItemDtoCard')
    [OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Select-Object -First 1
}
# every element of the page whose name starts with one of these words, with its type and size: the names Narrator reads
function It-Names([string]$Label, [string[]]$Prefixes) {
    $file = Join-Path $Out "uia\items-$itTag-$Label-names.txt"
    $lines = foreach ($itP in $Prefixes) {
        foreach ($e in (It-Where $itP)) {
            "{0} | {1} | {2}" -f $e.Cached.ControlType.ProgrammaticName, $e.Cached.Name, (It-Rect $e)
        }
    }
    $lines = @($lines | Sort-Object -Unique)
    [IO.File]::WriteAllText($file, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    Say "names on $Label ($($lines.Count)) -> $file"
    $lines
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); It-Settle 1500

# --- Home: the hero's dots are 24 px targets, the hero shot at both widths
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 800
$itDots = It-Where 'Slide ' 'Button'
$itSmall = @($itDots | Where-Object { $r = [OffscreenUi]::RectOf($_); $r.Width -lt 24 -or $r.Height -lt 24 })
Check 'items: the hero dots are at least 24x24' ($itDots.Count -gt 1 -and $itSmall.Count -eq 0) (($itDots | ForEach-Object { It-Rect $_ }) -join ', ')
It-Widths 'hero' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 800 }

# --- a card with keyboard focus shows its buttons, each named for the card's item (Continue Watching's first card)
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); It-Settle 1500
$itFirstCard = Wait-Until { It-FirstCard } -Seconds 15 -PollMs 200
$itCardShown = {
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 600
    $c = It-FirstCard
    if ($c) { [void][OffscreenUi]::SetFocus($c); It-Settle 1200 }
}
& $itCardShown
$itCardNames = It-Names 'card' @('Play ', 'Mark ', 'Add ', 'Remove ', 'More options')
Dump-Tree "items-$itTag-card"
$itCardTitle = if ($itFirstCard) { ($itFirstCard.Cached.Name -split ',')[0] } else { '' }
Check 'items: the focused card''s buttons are named for its item' ($itCardTitle -and @($itCardNames | Where-Object { $_ -like "*| Play $itCardTitle*" }).Count -ge 1 -and @($itCardNames | Where-Object { $_ -match "\| (Mark .* as (un)?watched)" -and $_ -like "*$itCardTitle*" }).Count -ge 1 -and @($itCardNames | Where-Object { $_ -like "*More options for $itCardTitle*" }).Count -ge 1) "card '$itCardTitle'"
It-Widths 'card-buttons' $itCardShown

# --- the movie page: its icon buttons named for the movie
Step-Movie
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); It-Settle 1500
$itMovieNames = It-Names 'movie' @('Mark ', 'Add ', 'Remove ', 'More options', 'Play ', 'Your rating')
Check 'items: the movie page''s buttons are named for the movie' (@($itMovieNames | Where-Object { $_ -like "*Mark $($fixture.movie.name) as *watched*" }).Count -eq 1 -and @($itMovieNames | Where-Object { $_ -like "*to favorites*" -or $_ -like "*from favorites*" }).Count -eq 1 -and @($itMovieNames | Where-Object { $_ -like "*More options for $($fixture.movie.name)*" }).Count -eq 1)
It-Widths 'movie' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 600 }

# --- the movie page's Watched, twice: a mark then an unmark, its name and the More menu following (1.1.3 audit, item 3)
function It-Toggle($element) { ([System.Windows.Automation.TogglePattern]$element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() }
$itWOff = Find-Ui -Name "Mark $($fixture.movie.name) as watched" -Type 'Button'
if ($itWOff) {
    $itTAt = Get-Epoch
    It-Toggle $itWOff
    $itWOn = Wait-Ui -Name "Mark $($fixture.movie.name) as unwatched" -Type 'Button' -Seconds 10
    It-Settle 800
    $itMore = Find-Ui -Name "More options for $($fixture.movie.name)" -Type 'Button'
    $itMenuText = $null
    if ($itMore) {
        [void](Invoke-Element $itMore)
        $itMenuText = Wait-Until { if (Find-Ui -Name 'Mark as unwatched' -Type 'MenuItem') { 'Mark as unwatched' } elseif (Find-Ui -Name 'Mark as watched' -Type 'MenuItem') { 'Mark as watched' } } -Seconds 5 -PollMs 200
        Key 0x1B; It-Settle 600
    }
    Check 'items: after Watched, the movie''s More menu says "Mark as unwatched"' ($itMenuText -eq 'Mark as unwatched') "menu: $itMenuText"
    if ($itWOn) { It-Toggle $itWOn }
    $itWBack = Wait-Ui -Name "Mark $($fixture.movie.name) as watched" -Type 'Button' -Seconds 10
    It-Settle 1000
    $itSent = @(Get-Requests $itTAt | Where-Object { $_.route -eq 'played' } | ForEach-Object { $_.method })
    Check 'items: Watched twice on the movie page sends a mark, then an unmark, and the name follows' (($itSent -join ',') -eq 'POST,DELETE' -and $itWOn -and $itWBack) ("requests: " + ($itSent -join ', '))
}
else { Check 'items: the movie page''s Watched toggle' $false 'not found' }

# --- the movie's Audio and Subtitle pickers: plain labels, no codecs (layout audit item 16); what Narrator reads as chosen
$itPickers = foreach ($itPn in @('Audio', 'Subtitle')) {
    $itCb = Find-Ui -Name $itPn -Type 'ComboBox' -OffscreenToo
    if ($itCb) {
        $itSel = ([System.Windows.Automation.SelectionPattern]$itCb.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
        "{0}={1}" -f $itPn, (($itSel | ForEach-Object { $_.Current.Name }) -join '/')
    }
    else { "{0}=(no picker)" -f $itPn }
}
Check 'items: the movie''s pickers read plain labels, no codec or "Default"' (@($itPickers | Where-Object { $_ -match 'AAC|SUBRIP|SRT|Default|MediaStream' }).Count -eq 0 -and @($itPickers | Where-Object { $_ -notmatch '\(no picker\)' }).Count -ge 1) ($itPickers -join '; ')
It-Widths 'movie-details' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 45); It-Settle 600 }

# --- the series and the season: Play names the episode it starts (the showcase: S1E1-E3 watched, so S1 E4)
Step-Series $false
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); It-Settle 1500
$itSeriesPlay = Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Play S1 E4', '') } -Seconds 15 -PollMs 200
$itSeriesNames = It-Names 'series' @('Play', 'Resume', 'Mark ', 'Add ', 'Remove ', 'More options', 'Your rating')
Check 'items: the series page''s Play names the next episode (S1 E4)' ([bool]$itSeriesPlay) $(if ($itSeriesPlay) { $itSeriesPlay.Current.Name })
Check 'items: the series page''s buttons are named for the series' (@($itSeriesNames | Where-Object { $_ -like "*Mark $($fixture.series.name) as *watched*" }).Count -eq 1)
# the series page lists the season of the next episode (layout audit item 9): Season 1's rows, S1 E4 among them
$itSeriesRow = Wait-Until { Find-Ui -Name 'S1 E4 ' -Type 'ListItem' -Contains -OffscreenToo } -Seconds 10 -PollMs 200
Check 'items: the series page lists the next episode''s season (S1 E4 is a row)' ([bool]$itSeriesRow) $(if ($itSeriesRow) { $itSeriesRow.Current.Name })
$itLink = Find-Ui -Name "$($fixture.season.name) details" -Type 'Hyperlink'
Check 'items: the series page links the shown season''s own page ("Season 1 details")' ([bool]$itLink)
$itRating = Find-Ui -Name 'Your rating' -Type 'Slider'
Check 'items: Your rating is a 24 px target in the series page''s action row' ($itRating -and [OffscreenUi]::RectOf($itRating).Height -ge 24) $(if ($itRating) { It-Rect $itRating } else { 'no rating (no journal?)' })
It-Widths 'series' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 600 }

Step-Season $false
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); It-Settle 1500
$itSeasonPlay = Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Play S1 E4', '') } -Seconds 15 -PollMs 200
$itSeasonNames = It-Names 'season' @('Play', 'Resume', 'Mark ', 'Add ', 'Remove ', 'More options', 'Your rating')
Check 'items: the season page''s Play names the next episode (S1 E4)' ([bool]$itSeasonPlay) $(if ($itSeasonPlay) { $itSeasonPlay.Current.Name })
Check 'items: the season page''s buttons are named for the season' (@($itSeasonNames | Where-Object { $_ -like "*Mark $($fixture.season.name) of $($fixture.series.name) as *watched*" }).Count -eq 1)
It-Widths 'season' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 600 }

# --- season pills switch in place: Season 2, Season 3, then Back goes to the series (1.1.3 audit, item 10)
$itPill2 = Find-Ui -Name 'Season 2' -Type 'Button'
if ($itPill2) {
    [void](Invoke-Element $itPill2)
    [void](Wait-Ui -Name 'Season 2, current season' -Type 'Button' -Seconds 15)
    $itPill3 = Wait-Ui -Name 'Season 3' -Type 'Button' -Seconds 10
    if ($itPill3) { [void](Invoke-Element $itPill3); [void](Wait-Ui -Name 'Season 3, current season' -Type 'Button' -Seconds 15) }
    It-Settle 1000
    Key 0xA6   # the Back key
    $itAtSeries = Wait-Until { if (It-OnSeries) { $true } } -Seconds 15 -PollMs 200
    Check 'items: after switching seasons with the pills, Back goes to the series page' ([bool]$itAtSeries)
    $script:page = 'series'
}
else { Check 'items: the season pills' $false 'no "Season 2" pill' }

# --- Back to an item page keeps its place: the series page scrolled to More Like This, a card opened with the keyboard, Back (item 5)
function It-PagePercent {
    $best = $null; $area = 0
    foreach ($l in [OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id)) {
        $f = $l -split '\|'
        if ($f.Count -lt 6 -or $f[-1] -ne 'True') { continue }
        $r = $f[2] -split ','; $a = [double]$r[2] * [double]$r[3]
        if ($a -gt $area) { $area = $a; $best = [double]$f[4] }
    }
    $best
}
if ($script:page -ne 'series') { Step-Series $false }
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); It-Settle 1500
$itSet = [OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); It-Settle 1500
$itSimilar = It-FirstCard
if ($itSimilar) {
    $itSimilarName = ($itSimilar.Cached.Name -split ',')[0]
    [void][OffscreenUi]::SetFocus($itSimilar); It-Settle 600
    $itBefore = It-PagePercent
    Key 0x0D   # Enter opens the card
    [void](Wait-ItemPage $itSimilarName 15)
    It-Settle 1500
    Key 0xA6
    $itBackCard = Wait-Until { if (It-OnSeries) { $true } } -Seconds 15 -PollMs 200
    It-Settle 3000
    $itAfter = It-PagePercent
    $itFocus = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
    Check 'items: Back to the series page scrolls back where it was' ($itBackCard -and $itBefore -gt 5 -and [math]::Abs($itAfter - $itBefore) -lt 3) ("before {0:0.0}%, after {1:0.0}%" -f $itBefore, $itAfter)
    Check 'items: ... and the card that was opened has the focus again' ($itFocus -and $itFocus -like "*""name"":""$itSimilarName*") $itFocus
    Shot "$itTag-series-back-place"
}
else { Check 'items: a More Like This card on the series page' $false 'none' }

# --- optional: a resume point on S1 E4 (Play on the series page, a minute or two in, back), then "Resume S1 E4"
if ($env:SG_ITEMS_RESUME) {
    Step-Series $false
    $itP = Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Play S1 E4', '') } -Seconds 15 -PollMs 200
    if ($itP) {
        $itAt = Get-Epoch
        [void](Invoke-Element $itP)
        [void](Wait-Until { @(Get-Requests $itAt | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 60 -PollMs 250)
        It-Settle 3000
        foreach ($i in 1..2) { Key 0x26; It-Settle 700 }   # Up: mpv seeks a minute ahead
        It-Settle 2500
        Key 0x1B   # Esc: back to the series page
        [void](Wait-Until { @(Get-Requests $itAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 20 -PollMs 250)
        $itBtn = Find-Ui -Id 'BackButton'; if ($itBtn -and -not (Wait-ItemPage $fixture.series.name 3)) { [void](Invoke-Element $itBtn) }
        $script:page = 'series'
        $itResume = Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Resume S1 E4', '') } -Seconds 20 -PollMs 250
        Check 'items: after playing a moment, the series page''s Play is "Resume S1 E4" with its progress' ([bool]$itResume) $(if ($itResume) { $itResume.Current.Name })
        [void](It-Names 'series-resume' @('Resume', 'Play'))
        It-Widths 'series-resume' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 600 }
        Step-Season $false
        [void](Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Resume S1 E4', '') } -Seconds 15 -PollMs 250)
        It-Widths 'season-resume' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); It-Settle 600 }
    }
    else { Check 'items: the series page''s Play to start a resume point' $false 'no Play S1 E4' }
}
