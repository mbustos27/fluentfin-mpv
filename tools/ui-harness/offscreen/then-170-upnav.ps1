# Nixie 1.7 (upnav; the owner, 2026-11-21: an episode opened from Continue watching had no way back to its show): the way up
# from an episode, in one theme per run (NIXIE_LAYOUT; NIXIE_THEME=Light for the light pass). Run with
# `-Script wait:1 -NoA11y -Then offscreen\then-170-upnav.ps1`. From Home, an episode in progress (Continue watching's), the
# episode page shows "<library> › <series> › Season <n> ›" over its title: buttons named "Go to series <series>" and
# "Go to Season <n>" showing those words, on one line above the actions; Shift+Tab from Play (where the keyboard is on
# arrival) reaches them, and Tab goes library, series, season, then the actions. Enter on the series crumb opens the series
# page (no crumbs of its own); Back returns to the episode with the keyboard on that crumb. The season crumb opens that
# season (its own trail: the series, which opens the series page; Back, Back); Back returns with the keyboard on the season
# crumb. A click opens the series page, and Back returns. R keeps the trail. Shots at
# UP_SIZES (default 1920,1100). Dot-sourced by drive.ps1: its helpers; variables here start with up.
. (Join-Path $PSScriptRoot 'chord-keys.ps1')

$upTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
if ($env:NIXIE_THEME) { $upTag = "$upTag-$($env:NIXIE_THEME.ToLowerInvariant())" }
$upSizes = if ($env:UP_SIZES) { @($env:UP_SIZES -split ',' | ForEach-Object { [int]$_ }) } else { @(1920, 1100) }
# an episode card's name (ItemLabels.CardName): "<series>, season 3 episode 2, <title>, 40% watched" (or "S3 E2")
$upEpisodeRx = '^(?<series>.+?), (?:season (?<season>\d+) episode |S(?<season>\d+) E)(?<episode>\d+)(?:, (?<rest>.*))?$'
# Editorial's Up next lines (EditorialUpNextEntry.Name): "<series> S3 E2 <title>, 6 min left" ("in progress": resumed)
$upLineRx = '^(?<series>.+?) S(?<season>\d+) E(?<episode>\d+)\b.*, (?:.+ left|in progress)$'

function Up-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function Up-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function Up-Describe($f) { if ($f) { "$($f.type) '$($f.name)' id=$($f.automationId)" } else { 'nothing' } }
function Up-Crumb([string]$Id) { Find-Ui -Id $Id }
function Up-Trail { [pscustomobject]@{ Library = (Up-Crumb 'TrailEyebrow'); Series = (Up-Crumb 'TrailSeries'); Season = (Up-Crumb 'TrailSeason') } }
function Up-Name($e) { if ($e) { try { $e.Current.Name } catch { '' } } else { '' } }
# the words the button shows (its label, a Text inside it), as the user reads them
function Up-Words($e) {
    if (-not $e) { return '' }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $t = $e.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($t) { $t.Current.Name } else { '' }
}
# the app's own line for the item page now shown (TopNavigation.Trail: "the trail of SeriesViewModel: none")
function Up-Shown([double]$Since, [string]$Page) { @(CK-Log $Since "^Top bar: the trail of $Page").Count }
function Up-Back { Key 0xA6 }   # the Back key
function Up-OnEpisode([double]$Seconds = 15) { Wait-Until { $s = Up-Crumb 'TrailSeason'; if ($s -and (Up-Name $s) -eq "Go to $upSeasonWord") { $s } } -Seconds $Seconds -PollMs 200 }
function Up-FocusIs([string]$Id, [double]$Seconds = 10) { Wait-Until { $f = Up-Focus; if ($f -and $f.automationId -eq $Id) { $f } } -Seconds $Seconds -PollMs 200 }
# the crumbs on one line, in order, above the actions (Play, by its name), and inside the window
function Up-Layout([string]$Where, [string]$PrimaryName) {
    $Primary = if ($PrimaryName) { Find-Ui -Name $PrimaryName -Type 'Button' } else { $null }
    $t = Up-Trail
    $parts = @($t.Library, $t.Series, $t.Season | Where-Object { $_ })
    $rects = @($parts | ForEach-Object { $_.Current.BoundingRectangle })
    $win = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$win)
    $mid = $rects[0].Y + $rects[0].Height / 2
    $oneLine = @($rects | Where-Object { [math]::Abs(($_.Y + $_.Height / 2) - $mid) -gt 2 -or $_.Height -gt 44 }).Count -eq 0
    $inOrder = $true
    for ($i = 1; $i -lt $rects.Count; $i++) { if ($rects[$i].X -lt $rects[$i - 1].X + $rects[$i - 1].Width - 1) { $inOrder = $false } }
    $last = $rects[-1]
    $inside = ($last.X + $last.Width) -le $win.R -and $rects[0].X -ge $win.L
    $playTop = if ($Primary) { try { $Primary.Current.BoundingRectangle.Y } catch { $null } } else { $null }
    $above = $playTop -and ($last.Y + $last.Height) -le $playTop
    $detail = ($rects | ForEach-Object { "{0:0},{1:0} {2:0}x{3:0}" -f ($_.X - $win.L), ($_.Y - $win.T), $_.Width, $_.Height }) -join ' | '
    Check "upnav ${Where}: the crumbs are on one line, in order, inside the window" ($parts.Count -ge 2 -and $oneLine -and $inOrder -and $inside) $detail
    Check "upnav ${Where}: ... above the title's actions" ([bool]$above) ("crumbs' foot {0:0}, Play's top {1}" -f ($last.Y + $last.Height - $win.T), $(if ($playTop) { [int]($playTop - $win.T) } else { 'not found' }))
}

# --- Home: an episode in progress, the first one down the page --------------------------------------------------------
Open-Nav 'Home'
Up-Settle 3000
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Up-Settle 1000
$upCond = New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
$upCards = @(Wait-Until {
        $c = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $upCond) | Where-Object { ($_.Cached.Name -match $upEpisodeRx -and $_.Cached.Name -match '\d+% watched') -or $_.Cached.Name -match $upLineRx })
        if ($c.Count) { , $c }
    } -Seconds 20 -PollMs 300)
$upCard = @($upCards | Sort-Object { [int]$_.Cached.IsOffscreen }, { $_.Cached.BoundingRectangle.Y }, { $_.Cached.BoundingRectangle.X }) | Select-Object -First 1
Check 'upnav: Home has an episode in progress (Continue watching)' ([bool]$upCard) $(if ($upCard) { $upCard.Cached.Name } else { "$($upCards.Count) found" })
if (-not $upCard) { return }
if (-not ($upCard.Cached.Name -match $upEpisodeRx)) { [void]($upCard.Cached.Name -match $upLineRx) }
$upSeries = $Matches.series
$upSeasonNo = [int]$Matches.season
$upSeasonWord = if ($upSeasonNo -eq 0) { 'Specials' } else { "Season $upSeasonNo" }
Say "upnav: opening '$($upCard.Cached.Name)' (series '$upSeries', $upSeasonWord)"

$upT0 = Get-Epoch
Say "upnav: card ($(Invoke-Element $upCard))"
# Editorial: a poster opens its details in place, whose Full details opens the page
$upFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2.5 -PollMs 150
if ($upFull) { Say "upnav: Full details ($(Invoke-Element $upFull))" }
$upOpened = Up-OnEpisode 25
Check 'upnav: the episode page opens' ([bool]$upOpened -and (Up-Shown $upT0 'EpisodeViewModel') -gt 0)
if (-not $upOpened) { Shot "upnav-$upTag-episode-failed"; return }
Up-Settle 2500

# --- what the user sees: the crumbs, their words and names ----------------------------------------------------------------
$upTrail = Up-Trail
$upArrival = Up-Focus
Check 'upnav: the series crumb is a button named "Go to series <series>"' ((Up-Name $upTrail.Series) -eq "Go to series $upSeries") (Up-Name $upTrail.Series)
Check 'upnav: ... showing the series'' name' ((Up-Words $upTrail.Series) -eq $upSeries) (Up-Words $upTrail.Series)
Check 'upnav: the season crumb is a button named "Go to Season <n>"' ((Up-Name $upTrail.Season) -eq "Go to $upSeasonWord") (Up-Name $upTrail.Season)
Check 'upnav: ... showing "Season <n>"' ((Up-Words $upTrail.Season) -eq $upSeasonWord) (Up-Words $upTrail.Season)
Check 'upnav: the library crumb leads them (reached from Home)' ([bool]$upTrail.Library) "'$(Up-Name $upTrail.Library)'"
Check 'upnav: on arrival the keyboard is on the page''s Play, as before' ($upArrival -and $upArrival.name -match '^(Play|Resume)') (Up-Describe $upArrival)
$upPlayName = if ($upArrival -and $upArrival.name -match '^(Play|Resume)') { $upArrival.name } else { $null }
Up-Layout 'episode' $upPlayName

# --- the keyboard: Shift+Tab from Play reaches them; Tab goes library, series, season, then the actions --------------------
$upBackwards = @()
for ($upI = 0; $upI -lt 4; $upI++) {
    CK-Key 'Shift+Tab'; Up-Settle 350
    $upF = Up-Focus; $upBackwards += (Up-Describe $upF)
    if ($upF -and $upF.automationId -eq 'TrailSeason') { break }
}
Check 'upnav: keyboard first, Shift+Tab from Play reaches the season crumb' ((Up-Focus).automationId -eq 'TrailSeason') ($upBackwards -join ' -> ')
[void][OffscreenUi]::SetFocus((Up-Crumb 'TrailEyebrow')); Up-Settle 400
$upStops = @()
for ($upI = 0; $upI -lt 4; $upI++) { Key 0x09; Up-Settle 350; $upStops += (Up-Focus) }
Check 'upnav: Tab from the library crumb goes to the series crumb, then the season crumb' ($upStops[0].automationId -eq 'TrailSeries' -and $upStops[1].automationId -eq 'TrailSeason') (($upStops | ForEach-Object { Up-Describe $_ }) -join ' -> ')
$upNext = @($upStops[2..3] | Where-Object { $_ -and $_.name -match '^(Play|Resume)' })
Check 'upnav: ... then the title''s actions (Play within two Tabs)' ($upNext.Count -ge 1) (($upStops[2..3] | ForEach-Object { Up-Describe $_ }) -join ' -> ')
Shot "upnav-$upTag-episode-keyboard"

# --- Enter on the series crumb: the series page; Back: the episode, the keyboard on the crumb -----------------------------
[void][OffscreenUi]::SetFocus((Up-Crumb 'TrailEyebrow')); Up-Settle 300
Key 0x09; Up-Settle 400
Check 'upnav: the series crumb has the keyboard' ((Up-Focus).automationId -eq 'TrailSeries') (Up-Describe (Up-Focus))
$upT = Get-Epoch
Key 0x0D
$upOnSeries = Wait-Until { if ((Up-Shown $upT 'SeriesViewModel') -gt 0 -and -not (Up-Crumb 'TrailSeries')) { $true } } -Seconds 15 -PollMs 200
Up-Settle 2500
Check 'upnav: Enter on the series crumb opens the series page (it has no crumbs of its own)' ([bool]$upOnSeries -and -not (Up-Crumb 'TrailSeason'))
Check 'upnav: ... showing the series' ([bool](Find-Ui -Name $upSeries -Type 'Text') -or [bool](Find-Ui -Name $upSeries -Id 'LogoImage') -or [bool](Find-Ui -Name $upSeries -Type 'Image'))
Shot "upnav-$upTag-series"
Up-Back
$upAgain = Up-OnEpisode 15
$upOnCrumb = Up-FocusIs 'TrailSeries' 10
Check 'upnav: Back returns to the episode' ([bool]$upAgain)
Check 'upnav: ... with the keyboard on the series crumb' ([bool]$upOnCrumb) (Up-Describe (Up-Focus))

# --- the season crumb: that season's page, its own trail (the series); Back: the keyboard on the season crumb -------------
Key 0x09; Up-Settle 400
Check 'upnav: Tab: the season crumb has the keyboard' ((Up-Focus).automationId -eq 'TrailSeason') (Up-Describe (Up-Focus))
$upT = Get-Epoch
Key 0x0D
$upOnSeason = Wait-Until { if ((Up-Shown $upT 'SeasonViewModel') -gt 0 -and -not (Up-Crumb 'TrailSeason')) { $true } } -Seconds 15 -PollMs 200
Up-Settle 2500
$upSeasonTrail = Up-Trail
Check 'upnav: Enter on the season crumb opens that season''s page' ([bool]$upOnSeason -and [bool](Find-Ui -Name $upSeasonWord -Type 'Text' -Contains -OffscreenToo)) $upSeasonWord
Check 'upnav: ... whose own trail is its series ("Go to series <series>", no season crumb)' ((Up-Name $upSeasonTrail.Series) -eq "Go to series $upSeries" -and -not $upSeasonTrail.Season) (Up-Name $upSeasonTrail.Series)
Shot "upnav-$upTag-season"
# the way up goes on: the season's series crumb opens the series page; Back to the season, Back to the episode
$upT = Get-Epoch
if ($upSeasonTrail.Series) { [void](Invoke-Element $upSeasonTrail.Series) }
$upSeasonUp = Wait-Until { if ((Up-Shown $upT 'SeriesViewModel') -gt 0 -and -not (Up-Crumb 'TrailSeries')) { $true } } -Seconds 15 -PollMs 200
Check 'upnav: the season page''s series crumb opens the series page' ([bool]$upSeasonUp)
Up-Settle 1500
Up-Back
[void](Wait-Until { $s = Up-Crumb 'TrailSeries'; if ($s -and -not (Up-Crumb 'TrailSeason')) { $s } } -Seconds 15 -PollMs 200)
Up-Settle 1500
Up-Back
$upAgain = Up-OnEpisode 15
$upOnCrumb = Up-FocusIs 'TrailSeason' 10
Check 'upnav: Back, Back: the episode again' ([bool]$upAgain)
Check 'upnav: ... with the keyboard on the season crumb' ([bool]$upOnCrumb) (Up-Describe (Up-Focus))

# --- the pointer: a click opens the series page; Back returns (a double click opening it once is a unit test, ItemTrailTests:
# here its second click lands on the series page, so it passed without the guard) --------------------------------------------
$upT = Get-Epoch
CK-Click (Up-Crumb 'TrailSeries')
$upClicked = Wait-Until { if ((Up-Shown $upT 'SeriesViewModel') -gt 0 -and -not (Up-Crumb 'TrailSeries')) { $true } } -Seconds 15 -PollMs 200
Check 'upnav: a click on the series crumb opens the series page' ([bool]$upClicked)
Up-Settle 1500
Up-Back
$upAgain = Up-OnEpisode 15
Check 'upnav: ... and Back returns to the episode' ([bool]$upAgain)
Up-Settle 1500

# --- R: the page loads again, the trail with it ---------------------------------------------------------------------------
$upT = Get-Epoch
[void][OffscreenUi]::SetFocus((Up-Crumb 'TrailSeason')); Up-Settle 300
CK-Key 'R'
Up-Settle 3500
$upAfterR = Up-Trail
Check 'upnav: R keeps the trail (series and season)' ((Up-Name $upAfterR.Series) -eq "Go to series $upSeries" -and (Up-Name $upAfterR.Season) -eq "Go to $upSeasonWord") ("{0} | {1}" -f (Up-Name $upAfterR.Series), (Up-Name $upAfterR.Season))

# --- the shots: each size, the keyboard on the series crumb (its focus box) and at rest ------------------------------------
foreach ($upW in $upSizes) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $upW, $(if ($upW -ge 1900) { 1080 } else { 900 })); Up-Settle 2200
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Up-Settle 600
    Up-Layout "episode at $upW" $upPlayName
    Shot "upnav-$upTag-episode-$upW"
    [void][OffscreenUi]::SetFocus((Up-Crumb 'TrailEyebrow')); Up-Settle 250
    Key 0x09; Up-Settle 500
    Shot "upnav-$upTag-episode-$upW-focus"
}
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Up-Settle 800
