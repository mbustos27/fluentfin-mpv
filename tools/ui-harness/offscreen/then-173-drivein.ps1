# Nixie 1.7.3, stream C: Drive-in Home's Up next board, and the Libraries page's art. Run in Drive-in after -Script home:
#   $env:NIXIE_LAYOUT='DriveIn'; run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-173-drivein.ps1
# With Next Up empty and Continue Watching full (1.7.2's board read "All of Up next (3)" over fewer entries, or none):
#   $env:UPNEXT_EMPTY='1'; run-offscreen.ps1 ... -LatencyRule '^/Shows/NextUp=empty' ...
# With Next Up refused (its list is empty too, but not known to be: no "Nothing up next"):
#   $env:UPNEXT_FAILS='1'; run-offscreen.ps1 ... -LatencyRule '^/Shows/NextUp=status:500' ...
# For the art (a logo on a dark card, cropped round its middle, not to its top strip), a square picture for every Primary:
#   $env:FAKESERVER_PHOTO_DIR='<folder with one square .jpg>'; $env:FAKESERVER_PHOTO_KINDS='Primary'
# Checks: the board's entries and its link read from one list (the link's N is the entries' count, or 4 of N on a wide
# window), the link opens N items, an empty Next Up says "Nothing up next" with no link; shots of Home and the Libraries page
# (the art). Dot-sourced by drive.ps1: every name here starts with dv.

$dvTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'drivein173' }
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Start-Sleep -Milliseconds 1500
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 800

# the board: its heading (UP NEXT, read "Up next"), its link, and the entries between them
$dvHeading = Wait-Until { Find-Ui -Name 'Up next' -Type 'Text' } -Seconds 20 -PollMs 300
Check "drivein173 ($dvTag): Home's sign shows its Up next board" ([bool]$dvHeading) ''
Start-Sleep -Milliseconds 1500
$dvLink = Find-Ui -Name 'All of Up next (' -Contains
function Dv-Entries {
    if (-not $dvHeading) { return @() }
    $h = [OffscreenUi]::RectOf($dvHeading)
    $bottom = if ($dvLink) { ([OffscreenUi]::RectOf($dvLink)).Top } else { $h.Bottom + 400 }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object {
            $r = $_.Current.BoundingRectangle
            -not $r.IsEmpty -and $r.Top -gt $h.Bottom -and $r.Bottom -le $bottom + 2 -and [Math]::Abs(($r.Left + $r.Width / 2) - ($h.Left + $h.Width / 2)) -lt 260 -and $_.Current.Name -ne $dvLink.Current.Name
        })
}
$dvEntries = @(Dv-Entries)
$dvNames = ($dvEntries | ForEach-Object { $_.Current.Name }) -join ' | '
Say "drivein173: link '$(if ($dvLink) { $dvLink.Current.Name })', $($dvEntries.Count) entries: $dvNames"
Shot "$dvTag-home"

if ($env:UPNEXT_FAILS) {
    # Next Up refused (-LatencyRule '^/Shows/NextUp=status:500'): its list is empty too, but the board doesn't claim so
    $dvProblem = Wait-Until { Find-Ui -Name "couldn't be loaded" -Contains -OffscreenToo } -Seconds 20 -PollMs 300
    Check "drivein173 ($dvTag): Next Up refused: Home says part of it couldn't be loaded" ([bool]$dvProblem) $(if ($dvProblem) { $dvProblem.Current.Name })
    Check "drivein173 ($dvTag): Next Up refused: the board doesn't say Nothing up next" (-not (Find-Ui -Name 'Nothing up next' -Type 'Text')) ''
    Check "drivein173 ($dvTag): Next Up refused: no link" (-not $dvLink) "$(if ($dvLink) { $dvLink.Current.Name })"
}
elseif ($env:UPNEXT_EMPTY) {
    $dvEmpty = Find-Ui -Name 'Nothing up next' -Type 'Text'
    Check "drivein173 ($dvTag): Next Up empty: the board says Nothing up next" ([bool]$dvEmpty) ''
    Check "drivein173 ($dvTag): Next Up empty: no link counting what the board doesn't list" (-not $dvLink) "$(if ($dvLink) { $dvLink.Current.Name })"
    Check "drivein173 ($dvTag): Next Up empty: no entries on the board" ($dvEntries.Count -eq 0) $dvNames
    $dvContinue = Find-Ui -Name 'Continue watching' -Contains -OffscreenToo
    Check "drivein173 ($dvTag): Next Up empty: Continue Watching still shows" ([bool]$dvContinue) ''
}
else {
    $dvN = if ($dvLink -and $dvLink.Current.Name -match '\((\d+)\)') { [int]$Matches[1] } else { -1 }
    Check "drivein173 ($dvTag): the board's link counts its entries (4 of N on a wide window)" ($dvN -gt 0 -and $dvEntries.Count -eq [Math]::Min($dvN, 4)) "link N $dvN, $($dvEntries.Count) entries"
    Check "drivein173 ($dvTag): no Nothing up next beside entries" (-not (Find-Ui -Name 'Nothing up next' -Type 'Text')) ''
    if ($dvLink) {
        Say "drivein173: the link ($(Invoke-Element $dvLink))"
        # the grid page: its title "Up next", Home's board gone
        $dvGrid = Wait-Until { if (-not (Find-Ui -Name 'All of Up next (' -Contains) -and (Find-Ui -Name 'Up next' -Type 'Text')) { $true } } -Seconds 15 -PollMs 300
        Start-Sleep -Milliseconds 1500
        $dvCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $dvItems = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $dvCond) | Where-Object { $_.Current.BoundingRectangle.Top -gt 120 })
        Check "drivein173 ($dvTag): the link opens Up next with the N it says" ([bool]$dvGrid -and $dvItems.Count -eq $dvN) "title: $([bool]$dvGrid); $($dvItems.Count) items for N $dvN"
        Shot "$dvTag-upnext-grid"
        Key 0xA6
        [void](Wait-Until { Find-Ui -Name 'Up next' -Type 'Text' } -Seconds 15 -PollMs 300)
        Start-Sleep -Milliseconds 800
    }
}

# the Libraries page's art (with FAKESERVER_PHOTO_DIR, a square picture: centred in the tile, not its top strip)
Open-Nav 'Libraries'
$dvTile = Wait-Until { Find-Ui -Name "$($fixture.movie.library), " -Contains -Type 'ListItem' } -Seconds 20 -PollMs 300
Check "drivein173 ($dvTag): the Libraries page shows its tiles" ([bool]$dvTile) ''
Start-Sleep -Milliseconds 2500
Shot "$dvTag-libraries"
