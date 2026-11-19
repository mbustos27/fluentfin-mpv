# Nixie 1.3.1, trailers play in the app: on the showcase movie's page, the Trailer button opens the theatre window, which
# loads the fake server's trailer page (NIXIE_TRAILER_EMBED_BASE, set by drive.ps1: never YouTube), takes the keyboard, and
# closes on Esc, on Close and on a click on the dimmed page, the keyboard back on the Trailer button each time. Shots of the
# window at 1920x1080 and 1100x900 (SG_SHOT_TAG names them: the theme and the colour mode).
#   run-offscreen.ps1 -Script movie -Then offscreen\then-trailer.ps1   (NIXIE_LAYOUT=DriveIn|Cinematic|Editorial|Classic)
# Dot-sourced by drive.ps1: every name here starts with TR (drive.ps1's names must not be reused).

$trTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'trailer' }

function TR-Button { Wait-Ui -Name 'Watch trailer' -Type 'Button' -Seconds 15 }

# the app's own [trailer] log lines since $Since (UI Automation cannot walk the window while the web view is in it, so what
# the theatre did with the keyboard is read from FluentFin's log)
function TR-Log([double]$Since) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '^(\S+ \S+ [+-]\d\d:\d\d) \[INF\] \[trailer\] (.*)$' } | ForEach-Object {
            $trWhen = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(($_ -split ' \[INF\] ')[0], [ref]$trWhen) -and $trWhen.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { ($_ -split '\[trailer\] ', 2)[1] }
        }
    })
}

# opens the theatre with the keyboard on the Trailer button (so its way back is that button); true when it opened
function TR-Open([string]$How) {
    $trButton = TR-Button
    if (-not $trButton) { Check "trailer ($How): the movie page has a Trailer button" $false; return $false }
    [void][OffscreenUi]::SetFocus($trButton)
    Start-Sleep -Milliseconds 300
    $trAt = Get-Epoch
    Key 0x0D
    $trOpened = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -like 'theatre opened*' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
    Check "trailer ($How): Enter on Trailer opens the theatre window" ([bool]$trOpened) "$trOpened"
    $trLoaded = Wait-Until { @(Get-Requests $trAt | Where-Object { $_.path -like '/__harness/trailer/*' }) | Select-Object -First 1 } -Seconds 20 -PollMs 200
    Check "trailer ($How): the theatre loads the harness's embed page (not YouTube)" ([bool]$trLoaded) $(if ($trLoaded) { "$($trLoaded.path)?$(($trLoaded.query.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join '&')" })
    $trIn = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -match 'keyboard to Close after the page loaded: True, now on Button Close trailer' }) | Select-Object -First 1 } -Seconds 8 -PollMs 200
    Check "trailer ($How): the keyboard moves into the window (Close trailer), also after the page loads" ([bool]$trIn) ((TR-Log $trAt) -join ' | ')
    Start-Sleep -Milliseconds 500
    return [bool]$trOpened
}

function TR-Closed([string]$How, [double]$Since) {
    $trGone = Wait-Until { @(TR-Log $Since | Where-Object { $_ -eq 'theatre closed' }) | Select-Object -First 1 } -Seconds 5 -PollMs 150
    Check "trailer: $How closes the window" ([bool]$trGone)
    $trBack = Wait-Until { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f -and $f -match 'Watch trailer') { $f } } -Seconds 5 -PollMs 150
    Check "trailer: after $How, the keyboard is back on the Trailer button" ([bool]$trBack) "$([OffscreenUi]::Focused($script:hwnd, $script:ui.Id))"
}

# the shots first, at both sizes (the page under it dimmed)
foreach ($trSize in @(@(1920, 1080), @(1100, 900))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $trSize[0], $trSize[1])
    Start-Sleep -Milliseconds 1500
    if (TR-Open "$($trSize[0])") {
        Start-Sleep -Milliseconds 3500
        Shot "$trTag-$($trSize[0])"
        $trAt = Get-Epoch
        Key 0x1B
        TR-Closed 'Esc' $trAt
    }
}

# Close (the keyboard is on it: Enter), and a click on the dimmed page
if (TR-Open 'close button') {
    $trAt = Get-Epoch
    Key 0x0D
    TR-Closed 'Close' $trAt
}
# a click on the dimmed page: reported, not a check. A click posted off-screen did not reach the dim while the web view is
# in the window (its own input window takes the posted pointer messages); Esc closes it if the click did not
if (TR-Open 'dim') {
    $trWin = New-Object OffscreenUi+RECT
    [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$trWin)
    $trAt = Get-Epoch
    [OffscreenUi]::Hover($script:hwnd, $script:ui.Id, $trWin.L + 40, $trWin.B - 60)
    Start-Sleep -Milliseconds 250
    [OffscreenUi]::Click($script:hwnd, $script:ui.Id, $trWin.L + 40, $trWin.B - 60)
    $trGone = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -eq 'theatre closed' }) | Select-Object -First 1 } -Seconds 4 -PollMs 150
    Say "trailer: a posted click on the dimmed page $(if ($trGone) { 'closed the window' } else { 'did not reach it (posted input, see above): Esc instead' })"
    if (-not $trGone) { Key 0x1B; [void](Wait-Until { @(TR-Log $trAt | Where-Object { $_ -eq 'theatre closed' }) | Select-Object -First 1 } -Seconds 4 -PollMs 150) }
}
