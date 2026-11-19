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

# ---- Nixie 1.4.0 (bug hunt M1): the theatre is modal. Every global and page key over it is held back: the keyboard stays
# inside (FluentFin's log names where it is for each key held back, and UI Automation's focus is never the search box), the
# server is asked nothing, and Esc still closes it, the keyboard back on Trailer. Opened from the keyboard and from the mouse.
. (Join-Path $PSScriptRoot 'chord-keys.ps1')
function TR-Modal([string]$How) {
    $trKeys = @('Ctrl+F', '/', 'Ctrl+E', 'N', 'C', 'Ctrl+Z', 'F1', 'Shift+/', 'Alt+Home', 'Ctrl+,', 'R', 'F5', 'Ctrl+R', 'B')
    $trLeaks = New-Object System.Collections.ArrayList
    foreach ($trKey in $trKeys) {
        $trAt = Get-Epoch
        CK-Key $trKey
        Start-Sleep -Milliseconds 450
        $trHeld = @(CK-Log $trAt '\[trailer\] a key over the theatre held back') | Select-Object -Last 1
        $trFocus = CK-Focus
        $trAsked = @(Get-Requests $trAt | Where-Object { $_.path -notlike '/__harness/*' })
        if (-not $trHeld -or $trHeld -notmatch '\(inside\)$' -or $trFocus -match "'Search'" -or $trAsked.Count -or @(CK-Log $trAt '\[trailer\] theatre closed').Count) {
            [void]$trLeaks.Add("$trKey -> log: $(if ($trHeld) { $trHeld } else { 'no held-back line' }); focus: $trFocus; requests: $(($trAsked | ForEach-Object path) -join ',')")
        }
    }
    Check "trailer ($How): every global and page key over the theatre is held back, the keyboard inside, nothing asked" ($trLeaks.Count -eq 0) ($trLeaks -join ' | ')
    # Tab and Shift+Tab cycle inside: a key held back afterwards still finds the keyboard inside
    foreach ($trKey in @('Tab', 'Tab', 'Tab', 'Shift+Tab')) { CK-Key $trKey; Start-Sleep -Milliseconds 250 }
    $trAt = Get-Epoch
    CK-Key 'N'
    Start-Sleep -Milliseconds 400
    $trHeld = @(CK-Log $trAt '\[trailer\] a key over the theatre held back') | Select-Object -Last 1
    Check "trailer ($How): Tab and Shift+Tab keep the keyboard inside" ([bool]($trHeld -match '\(inside\)$')) "$trHeld; focus: $(CK-Focus)"
    $trAt = Get-Epoch
    CK-Key 'Esc'
    TR-Closed "Esc after the held-back keys ($How)" $trAt
}
# wide: the top bar shows its search box (narrow, it folds into a button and the box's own Ctrl+F is not there to leak)
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
Start-Sleep -Milliseconds 1500
if (TR-Open 'keyboard first, modal') { TR-Modal 'keyboard first' }
# mouse first: a click on Trailer opens it
$trButton = TR-Button
if ($trButton) {
    $trAt = Get-Epoch
    CK-Click $trButton
    $trOpened = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -like 'theatre opened*' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
    Check 'trailer (mouse first): a click on Trailer opens the theatre window' ([bool]$trOpened)
    [void](Wait-Until { @(TR-Log $trAt | Where-Object { $_ -match 'keyboard to Close after the page loaded' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200)
    Start-Sleep -Milliseconds 500
    if ($trOpened) { TR-Modal 'mouse first' }
}

# ---- Nixie 1.4.0 (bug hunt m8): while the theatre is open the top bar is not the window's caption: a press on the dim's top
# band is the dim's (it closes the theatre) and never drags or maximizes the window; Close is a click target; the window's own
# minimize and close stay. Asked of Windows itself (WM_NCHITTEST), at both sizes; the caption is back after it closes.
function TR-Hit([int]$X, [int]$Y) { [ChordKeys]::HitTest($script:hwnd, $script:ui.Id, $X, $Y) }
foreach ($trSize in @(@(1920, 1080), @(1100, 700))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $trSize[0], $trSize[1])
    Start-Sleep -Milliseconds 1500
    $trWin = New-Object OffscreenUi+RECT
    [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$trWin)
    # a point of the bar that drags the window (between the tabs and the search box, or whatever the width shows)
    $trBand = $trWin.T + 24
    $trMid = @(for ($trX = $trWin.L + 200; $trX -lt $trWin.R - 200; $trX += 16) { if ((TR-Hit $trX $trBand) -eq 2) { $trX } }) | Select-Object -First 1
    if (-not $trMid) { $trMid = [int](($trWin.L + $trWin.R) / 2) }
    $trBefore = TR-Hit $trMid $trBand
    if (TR-Open "caption $($trSize[0])") {
        $trOpen = TR-Hit $trMid $trBand
        $trCloseBtn = (CK-Focus)
        $trCloseHit = $null
        $trF = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
        if ($trF) { $trJ = $trF | ConvertFrom-Json; if ($trJ.rect) { $trCloseHit = TR-Hit ([int]($trJ.rect[0] + $trJ.rect[2] / 2)) ([int]($trJ.rect[1] + $trJ.rect[3] / 2)) } }
        $trWinClose = TR-Hit ($trWin.R - 24) ($trWin.T + 16)
        Check "trailer ($($trSize[0])x$($trSize[1])): the top band over the theatre is the dim's, not the window's caption (no drag, no maximize)" ($trBefore -eq 2 -and $trOpen -eq 1) "before: $trBefore, open: $trOpen (2 caption, 1 client)"
        Check "trailer ($($trSize[0])x$($trSize[1])): Close trailer takes clicks, the window's close stays" ($trCloseHit -eq 1 -and $trWinClose -eq 20) "Close trailer: $trCloseHit ($trCloseBtn); the window's close: $trWinClose"
        Shot "$trTag-caption-$($trSize[0])"
        $trAt = Get-Epoch
        CK-Key 'Esc'
        TR-Closed "Esc (caption $($trSize[0]))" $trAt
        Start-Sleep -Milliseconds 800
        $trAfter = TR-Hit $trMid $trBand
        Check "trailer ($($trSize[0])x$($trSize[1])): after it closes, the top bar drags the window again" ($trAfter -eq 2) "after: $trAfter"
    }
}
