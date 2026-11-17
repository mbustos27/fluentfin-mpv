# Seaglass scrolling, the season page's episode rows: run with -Script season -Then offscreen\then-season.ps1 (the showcase
# series' Season 1: 8 episodes, the first three watched). One Tab stop per row (it was five buttons), Up/Down/Home/End between
# episodes, out of the list at either end, every episode reached by Tab, the focused row clear of the title bar, Narrator's
# "S1E4 Title, watched" and "4 of 8" in a list named Episodes, Play and Watched still there for the pointer, the row's menu
# (the menu key) with the episode's page and the More menu's items, the wheel over the rows scrolling the page, and Enter
# playing the episode (last: it opens the player). Shift cannot be posted (WinUI reads the real keyboard state): Shift+Tab
# and Shift+F10 are hands-on checks; the menu key (VK_APPS) stands in for Shift+F10.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names (PowerShell names ignore case: $out is its -Out).
$A = [System.Windows.Automation.AutomationElement]
$Ids = [System.Windows.Automation.AutomationElementIdentifiers]
function Focused-Element {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::HasKeyboardFocusProperty, $true)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
}
function Info {
    $el = Focused-Element
    if (-not $el) { return $null }
    $c = $el.Cached; $r = $c.BoundingRectangle
    $pos = $el.GetCurrentPropertyValue($Ids::PositionInSetProperty); $size = $el.GetCurrentPropertyValue($Ids::SizeOfSetProperty)
    $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($el)
    [pscustomobject]@{ Name = $c.Name; Type = ($c.ControlType.ProgrammaticName -replace '^ControlType\.', ''); Class = $c.ClassName
        X = $r.X; Y = $r.Y; W = $r.Width; H = $r.Height; Pos = [int]$pos; Size = [int]$size
        ListName = $(if ($parent) { $parent.Current.Name }); ListType = $(if ($parent) { $parent.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '' }) }
}
function Press([int]$Vk, [int]$Ms = 700) { Key $Vk; Start-Sleep -Milliseconds $Ms }
function Describe($f) { if ($f) { "{0} [{1} {2}] {3} of {4} at {5:0},{6:0}" -f $f.Name, $f.Type, $f.Class, $f.Pos, $f.Size, $f.X, $f.Y } else { 'no focus' } }
function Window-Rect { $wr = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$wr); $wr }
# fully inside the window, below the title bar (as then-rows.ps1 checks a card)
function Row-Inside($f) { $wr = Window-Rect; $f -and $f.Y -ge $wr.T + 90 -and ($f.Y + $f.H) -le $wr.B - 8 }
function Page-State { $best = $null; foreach ($s in @([OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id))) { $p = $s -split '\|'; $r = $p[2] -split ',' | ForEach-Object { [double]$_ }; if ($p[6] -eq 'True' -and (-not $best -or $r[2] * $r[3] -gt $best.Area)) { $best = [pscustomobject]@{ Area = $r[2] * $r[3]; V = [double]$p[4]; Rect = $r } } }; $best }

$epCount = 8
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 700
$pill = Wait-Ui -Name 'Season 1, current season' -Type 'Button' -Seconds 10
Check 'season: the season pill is there' ([bool]$pill)
[void][OffscreenUi]::SetFocus($pill); Start-Sleep -Milliseconds 400

# --- Tab: one stop per episode, every episode, each named and placed for Narrator
Press 0x09
$ep1 = Info
Check 'season: Tab from the season pill goes to the first episode row' ($ep1 -and $ep1.Class -eq 'EpisodeRow' -and $ep1.Type -eq 'ListItem') (Describe $ep1)
Check 'season: Narrator: the row is named for its episode ("S1E1 <title>, watched")' ($ep1 -and $ep1.Name -eq "S1E1 $($fixture.episode.name), watched") (Describe $ep1)
Check 'season: Narrator: ... "1 of 8" in a list named Episodes' ($ep1 -and $ep1.Pos -eq 1 -and $ep1.Size -eq $epCount -and $ep1.ListType -eq 'List' -and $ep1.ListName -eq 'Episodes') ("$(Describe $ep1); $($ep1.ListType) '$($ep1.ListName)'")
Shot 'season-1-first-row'
$tabbed = @($ep1)
for ($i = 1; $i -lt $epCount; $i++) { Press 0x09 500; $tabbed += Info }
$rows = @($tabbed | Where-Object { $_ -and $_.Class -eq 'EpisodeRow' })
Check 'season: Tab reaches every episode, one stop each (8 presses, 8 rows in order)' ($rows.Count -eq $epCount -and (($rows | ForEach-Object { $_.Pos }) -join ',') -eq '1,2,3,4,5,6,7,8') (($tabbed | ForEach-Object { if ($_) { $_.Pos } else { '-' } }) -join ',')
Check 'season: ... the episode not watched is not said to be' ($rows.Count -ge 4 -and $rows[3].Name -notmatch 'watched') ($rows[3].Name)
$hiddenTab = @($rows | Where-Object { -not (Row-Inside $_) } | ForEach-Object { Describe $_ })
Check 'season: every row Tab reaches is fully in view (not under the title bar)' ($hiddenTab.Count -eq 0) ($hiddenTab -join '; ')
Shot 'season-2-tabbed-to-last'
Press 0x09 500
$after = Info
Check 'season: Tab after the last episode leaves the list' ($after -and $after.Class -ne 'EpisodeRow') (Describe $after)

# --- Up/Down, Home/End
[void][OffscreenUi]::SetFocus($pill); Start-Sleep -Milliseconds 300
Press 0x09
Press 0x28 900   # Down
$d1 = Info
Check 'season: Down moves to the next episode' ($d1 -and $d1.Pos -eq 2 -and $d1.Y -gt $ep1.Y - 400) (Describe $d1)
Shot 'season-3-down'
$walk = @()
for ($i = 0; $i -lt 3; $i++) { Press 0x28 900; $walk += Info }
Check 'season: Down, Down, Down walks the episodes in order' ((($walk | ForEach-Object { $_.Pos }) -join ',') -eq '3,4,5') (($walk | ForEach-Object { Describe $_ }) -join '; ')
$hiddenWalk = @($walk | Where-Object { -not (Row-Inside $_) } | ForEach-Object { Describe $_ })
Check 'season: ... each focused row fully in view' ($hiddenWalk.Count -eq 0) ($hiddenWalk -join '; ')
Shot 'season-4-walked-down'
Press 0x26 900   # Up
Check 'season: Up moves back' ((Info).Pos -eq 4)
Press 0x23 1300  # End
$last = Info
Check 'season: End goes to the last episode' ($last -and $last.Pos -eq $epCount -and $last.Class -eq 'EpisodeRow') (Describe $last)
Check 'season: ... fully in view' (Row-Inside $last) (Describe $last)
Press 0x24 1300  # Home
$firstAgain = Info
Check 'season: Home goes to the first episode' ($firstAgain -and $firstAgain.Pos -eq 1) (Describe $firstAgain)
Check 'season: ... fully in view, below the title bar' (Row-Inside $firstAgain) (Describe $firstAgain)
Press 0x26 900   # Up from the first: out of the list
$above = Info
Check 'season: Up from the first episode leaves the list (to the season pills above)' ($above -and $above.Class -ne 'EpisodeRow') (Describe $above)

# --- Play and Watched are still there for the pointer, named for the episode, not Tab stops; Favourite is not a button
$playBtn = Find-Ui -Name "Play, episode 1, $($fixture.episode.name)" -Type 'Button'
# Seaglass 1.1.4: Watched says what a click does now ("Mark as unwatched, ..." on a watched episode), its tooltip the same
$watchedBtn = Find-Ui -Name "Mark as unwatched, episode 1, $($fixture.episode.name)" -Type 'Button'
if (-not $watchedBtn) { $watchedBtn = Find-Ui -Name "Mark as watched, episode 1, $($fixture.episode.name)" -Type 'Button' }
Check 'season: Play and Watched stay on the row, named for the episode' ($playBtn -and $watchedBtn)
Check 'season: ... and have no Tab stop of their own' ($playBtn -and -not $playBtn.Cached.IsKeyboardFocusable -and $watchedBtn -and -not $watchedBtn.Cached.IsKeyboardFocusable)
Check 'season: Favourite is no longer a button on the row' (-not (Find-Ui -Name "Favorite, episode 1, $($fixture.episode.name)" -Type 'Button'))

# --- the menu key: the row's menu, the episode's page first, favourite in it
[void][OffscreenUi]::SetFocus($pill); Start-Sleep -Milliseconds 300
Press 0x09
Press 0x5D 1200   # VK_APPS
$details = Wait-Ui -Name 'Episode details' -Type 'MenuItem' -Seconds 5
$fav = Find-Ui -Name 'Add to favorites' -Type 'MenuItem'
if (-not $fav) { $fav = Find-Ui -Name 'Remove from favorites' -Type 'MenuItem' }
Check 'season: the menu key opens the row''s menu, with the episode''s page' ([bool]$details)
Check 'season: ... and favourite (the More menu''s items)' ([bool]$fav)
Shot 'season-5-menu'
Press 0x1B 800    # Esc
Check 'season: Esc closes the menu, the focus back on the row' ((Info).Class -eq 'EpisodeRow') (Describe (Info))

# --- the menu's Episode details opens the episode's page; its "Up next" row stands alone: a button, not an item of a list
Press 0x5D 1200
$detailsItem = Wait-Ui -Name 'Episode details' -Type 'MenuItem' -Seconds 5
if ($detailsItem) { Invoke-Element $detailsItem | Out-Null }
$epPage = Wait-ItemPage $fixture.episode.name
Check 'season: the menu''s Episode details opens the episode''s page' ([bool]$epPage)
$rowCond = New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'EpisodeRow')
$upNext = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $rowCond)) | Where-Object { $_.Cached.Name -like 'S1E2 *' } | Select-Object -First 1 } -Seconds 10 -PollMs 200
if ($upNext) {
    $unPos = $upNext.GetCurrentPropertyValue($Ids::PositionInSetProperty)
    $unType = $upNext.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', ''
    Check 'season: the episode page''s Up next row is a button, not a list item without a list' ($unType -eq 'Button' -and [int]$unPos -le 0) ("$unType '$($upNext.Cached.Name)', position $unPos")
}
else { Check 'season: the episode page''s Up next row is a button, not a list item without a list' $false 'no Up next row (S1E2) found' }
Key 0xA6   # Back to the season page
$pill = Wait-Ui -Name 'Season 1, current season' -Type 'Button' -Seconds 15
Start-Sleep -Milliseconds 1200

# --- the wheel over the rows scrolls the page
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 700
$rowEl = Find-Ui -Name "S1E2 " -Type 'ListItem' -Contains
if ($rowEl) {
    $rr = [OffscreenUi]::RectOf($rowEl)
    $p0 = (Page-State).V
    [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($rr.X + $rr.Width / 2), [int]($rr.Y + $rr.Height / 2), -120, $false, $false); Start-Sleep -Milliseconds 900
    $p1 = (Page-State).V
    Check 'season: the wheel over an episode row scrolls the page' ($p1 -gt $p0 + 0.1) "$p0 -> $p1 %"
}

# --- Enter plays (last: the player opens)
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 500
[void][OffscreenUi]::SetFocus($pill); Start-Sleep -Milliseconds 300
Press 0x09
$playing = Info
$enterAt = Get-Epoch
Press 0x0D 300   # Enter
$player = Wait-Until { if (-not (Find-Ui -Name 'Season 1, current season' -Type 'Button') -and -not (Find-Ui -Name "Play, episode 1, $($fixture.episode.name)" -Type 'Button')) { $true } } -Seconds 30 -PollMs 250
Check 'season: Enter on a row leaves for the player page' ([bool]$player) (Describe $playing)
# ... and plays that episode: the server is asked for its PlaybackInfo, and the backend reports it Playing
$episodeId = ([string]$fixture.episode.id -replace '-', '').ToLowerInvariant()
function Is-Episode($Request) { $Request.report -and (([string]$Request.report.item -replace '-', '').ToLowerInvariant() -eq $episodeId) }
$played = Wait-Until { @(Get-Requests $enterAt | Where-Object { $_.route -eq 'report-playing' -and (Is-Episode $_) }) | Select-Object -First 1 } -Seconds 30 -PollMs 300
$asked = @(Get-Requests $enterAt | Where-Object { $_.route -eq 'playbackinfo' -and (Is-Episode $_) }) | Select-Object -First 1
Check 'season: Enter on a row plays its episode (PlaybackInfo, then Playing, for that episode)' ($asked -and $played) ("PlaybackInfo: $([bool]$asked), Playing: $([bool]$played), episode $episodeId")
if ($player) { Start-Sleep -Seconds 2; Shot 'season-6-enter-played' }
