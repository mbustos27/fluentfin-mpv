# Seaglass shortcuts, run with: run-offscreen.ps1 ... -Script home,movie,season,play,leave -Then offscreen\then-keys.ps1
# (dot-sourced by drive.ps1 after the steps, on the season page). Posted messages cannot set the keyboard's modifier state, so
# the unmodified keys are used: the Back and Forward keys (VK 0xA6 / 0xA7, the same handler as Alt+Left / Mouse 4) and F1 (the sheet).
# XButton and Alt+Left are covered by the unit tests (AppShortcuts, AppMouse); a real Mouse 4 / Mouse 5 click is a manual check.

# the season page: its own Watched ("Mark Season 1 of <series> as ..."); 1.2.0's series page lists the same rows
function Season-Shown { [bool][OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.season.name) of ", '') }

Check 'keys: on the season page to begin with' (Season-Shown)

# Forward after leaving the player: nothing to go to, and above all never the player (it would start the episode again)
Key 0xA7
Start-Sleep -Seconds 2
Check 'keys: Forward after leaving the player stays on the season page (the player is not a forward target)' ((Season-Shown) -and -not (Find-Ui -Id 'BackButton'))

# Back goes up to the series page, Forward returns to the season page
Key 0xA6
# the series page: its own Watched ("Mark <series> as ..."); 1.2.0 lists episodes there, so the rows tell nothing
$series = Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.series.name) as ", '') } -Seconds 10 -PollMs 200
Check 'keys: the Back key goes back to the series page' ([bool]$series -and -not (Season-Shown))
Key 0xA7
$again = Wait-Until { if (Season-Shown) { $true } } -Seconds 10 -PollMs 200
Check 'keys: the Forward key returns to the season page' ([bool]$again)

# the sheet: F1 opens it, Esc closes it
Key 0x70
$sheet = Wait-Ui -Name 'Keyboard shortcuts' -Type 'Text' -Seconds 8
Check 'keys: F1 opens the shortcut sheet' ([bool]$sheet)
if ($sheet) {
    Start-Sleep -Milliseconds 600
    Shot 'shortcuts'
    Key 0x1B
    $gone = Wait-Until { if (-not (Find-Ui -Name 'Keyboard shortcuts' -Type 'Text')) { $true } } -Seconds 6 -PollMs 200
    Check 'keys: Esc closes the sheet' ([bool]$gone)
    Start-Sleep -Seconds 2
    # while it is open, Back does nothing under it: after closing it again the season page is still the one shown
    Key 0x70
    $second = Wait-Ui -Name 'Keyboard shortcuts' -Type 'Text' -Seconds 8
    Check 'keys: F1 opens the sheet a second time' ([bool]$second)
    Key 0xA6
    Start-Sleep -Seconds 1
    Check 'keys: the sheet is still open after Back' ([bool](Find-Ui -Name 'Keyboard shortcuts' -Type 'Text'))
    Key 0x1B
    [void](Wait-Until { if (-not (Find-Ui -Name 'Keyboard shortcuts' -Type 'Text')) { $true } } -Seconds 6 -PollMs 200)
    Check 'keys: Back under an open sheet does not navigate' (Season-Shown)
}
