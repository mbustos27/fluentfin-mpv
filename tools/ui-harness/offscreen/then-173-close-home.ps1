# Nixie 1.7.3, the fix round's re-review: closes Nixie from Drive-in's Home while its hero (OutdoorScreen) cycles, timed so
# the hero's next advance (every 7 s) falls in the window's teardown, where XAML answers E_UNEXPECTED: its timer had no
# closing check (the close crash's class: 0xc000027b, then Windows Error Reporting's seconds). Checks: the hero cycles, and
# the close ends within NIXIE_CLOSE_LIMIT_MS (2000 here) with exit code 0. Run on Home with drive.ps1's close left out:
#   $env:NIXIE_LAYOUT='DriveIn'; run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-173-close-home.ps1
# NIXIE_HERO_CLOSE_LEAD_MS (120): how long before the hero's next advance the close is sent. Dot-sourced by drive.ps1:
# every name here starts with ch.

$chEvery = 7000   # OutdoorScreen.AdvanceEvery
$chLead = if ($env:NIXIE_HERO_CLOSE_LEAD_MS) { [int]$env:NIXIE_HERO_CLOSE_LEAD_MS } else { 120 }
# the feature on the screen, by its Play button's name ("Play A Field Guide to Rivers": the title is drawn in bulbs)
function Ch-Title {
    $t = Find-Ui -Id 'PlayButton' -OffscreenToo
    if ($t) { "$($t.Current.Name)" } else { $null }
}

# the hero pauses while the keyboard is in it (Home's arrival puts it on Resume or Play): the keyboard goes to the search box
$chSearch = Wait-Ui -Name 'Search' -Type 'Edit' -Contains -Seconds 10
if ($chSearch) { [void][OffscreenUi]::SetFocus($chSearch) }
Say "hero: the keyboard moved out of it ($(if ($chSearch) { 'to the search box' } else { 'no search box found' }))"
$chFirst = Wait-Until { Ch-Title } -Seconds 15 -PollMs 100
$chAt = $null
$chWatch = [Diagnostics.Stopwatch]::StartNew()
while ($chWatch.Elapsed.TotalSeconds -lt 20 -and -not $chAt) {
    $chNow = Ch-Title
    if ($chNow -and $chFirst -and $chNow -ne $chFirst) { $chAt = $chWatch.ElapsedMilliseconds }
    else { Start-Sleep -Milliseconds 40 }
}
Check "close from Drive-in's Home: the hero cycles (its title changed)" ([bool]$chAt) "'$chFirst' -> '$(Ch-Title)'$(if ($chAt) { " after $chAt ms" })"
if ($chAt) {
    # the next advance is due $chEvery ms after this one: the close goes $chLead ms before it, so the tick lands in the teardown
    $chWait = $chEvery - ($chWatch.ElapsedMilliseconds - $chAt) - $chLead
    if ($chWait -gt 0) { Start-Sleep -Milliseconds $chWait }
}

if (-not $env:NIXIE_CLOSE_LIMIT_MS) { $env:NIXIE_CLOSE_LIMIT_MS = '2000' }
$cxFrom = "Drive-in's Home"
. (Join-Path $PSScriptRoot 'then-173-close.ps1')
