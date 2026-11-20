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
    # Nixie 1.4.0 (YouTube's Error 153): the embed is a frame in Nixie's own page (Sec-Fetch-Dest iframe, cross-site; 1.3.1
    # loaded it as the page itself: document), told that page's origin. Its Referer is the page's origin for https YouTube;
    # to the harness's http stand-in the policy (strict-origin-when-cross-origin, https to http) sends none.
    if ($trLoaded) {
        Check "trailer ($How): the embed is a frame of Nixie's page https://io.github.mbustos27.nixie (an iframe, told the origin, no Referer to http)" `
            ("$($trLoaded.report.dest)" -eq 'iframe' -and "$($trLoaded.report.site)" -eq 'cross-site' -and "$($trLoaded.query.origin)" -eq 'https://io.github.mbustos27.nixie' `
                -and "$($trLoaded.query.enablejsapi)" -eq '1' -and "$($trLoaded.report.referer)" -eq '') `
            "Sec-Fetch-Dest '$($trLoaded.report.dest)', Sec-Fetch-Site '$($trLoaded.report.site)', origin '$($trLoaded.query.origin)', enablejsapi '$($trLoaded.query.enablejsapi)', Referer '$($trLoaded.report.referer)'"
    }
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

# NIXIE_TRAILER_ONLY=message runs only the 1.4.0 message checks below (the Light shots: the rest is the same in both)
if ($env:NIXIE_TRAILER_ONLY -ne 'message') {   # (the block is not indented: a small diff for the streams merging this file)
# the shots first, at both sizes (the page under it dimmed)
foreach ($trSize in @(@(1920, 1080), @(1100, 900))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $trSize[0], $trSize[1])
    Start-Sleep -Milliseconds 1500
    $trAt0 = Get-Epoch
    if (TR-Open "$($trSize[0])") {
        # Nixie 1.4.0: the app hears the player (the stand-in speaks the IFrame Player API's messages): playing, no message
        $trPlaying = Wait-Until { @(TR-Log $trAt0 | Where-Object { $_ -match 'the player: ready True, state 1, error 0' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
        Check "trailer ($($trSize[0])): the app hears the player playing through Nixie's page (state 1, no error, no message)" ([bool]$trPlaying -and -not @(TR-Log $trAt0 | Where-Object { $_ -match "Nixie's message" }).Count) ((TR-Log $trAt0) -join ' | ')
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
        Check "trailer ($($trSize[0])x$($trSize[1])): the window's own close stays over the theatre" ($trWinClose -eq 20) "the window's close: $trWinClose (20 close)"
        # Close trailer's place, when UI Automation can read the focused element (Drive-in's frame hides it from UIA)
        if ($null -ne $trCloseHit) { Check "trailer ($($trSize[0])x$($trSize[1])): Close trailer takes clicks" ($trCloseHit -eq 1) "Close trailer: $trCloseHit ($trCloseBtn)" }
        else { Say "trailer ($($trSize[0])x$($trSize[1])): Close trailer's place is not readable by UI Automation here ($trCloseBtn): its click not checked" }
        Shot "$trTag-caption-$($trSize[0])"
        $trAt = Get-Epoch
        CK-Key 'Esc'
        TR-Closed "Esc (caption $($trSize[0]))" $trAt
        Start-Sleep -Milliseconds 800
        $trAfter = TR-Hit $trMid $trBand
        Check "trailer ($($trSize[0])x$($trSize[1])): after it closes, the top bar drags the window again" ($trAfter -eq 2) "after: $trAfter"
    }
}
}   # NIXIE_TRAILER_ONLY

# ---- Nixie 1.4.0: Nixie's own message when a trailer can't play, and the browser only when the user picks it. The stand-in
# answers error 150, error 153 or nothing at all (POST /__harness/trailer {"mode"}), or the trailer is on another site
# ({"url"}). Each time: Nixie's message (the reason, its Narrator name, the way to watch it elsewhere, Close with the
# keyboard), no browser, and the way out on Enter, Esc and a click; the user's own pick of Watch on YouTube is the one way the
# browser opens (logged; under the harness never launched). Keyboard first and mouse first.
if (-not (Get-Command CK-Key -ErrorAction SilentlyContinue)) { . (Join-Path $PSScriptRoot 'chord-keys.ps1') }
function TR-SetTrailer([string]$Mode, [string]$Url) {
    $trWas = (Invoke-Server '/__harness/trailer').url
    [void](Invoke-Server '/__harness/trailer' 'POST' @{ mode = $Mode; url = $Url })
    if ("$trWas" -ne "$Url") {
        # the link changed: R loads the movie page again
        $trAt = Get-Epoch
        Key 0x52
        [void](Wait-Until { @(Get-Requests $trAt | Where-Object { $_.auth -and $_.path -match [regex]::Escape($fixture.movie.id) }) | Select-Object -First 1 } -Seconds 10 -PollMs 200)
        Start-Sleep -Seconds 2
    }
}
function TR-NoBrowser([string]$How, [double]$Since) {
    $trOpened = @(TR-Log $Since | Where-Object { $_ -match 'the browser (opens|would open)' })
    Check "trailer ($How): no browser was opened" ($trOpened.Count -eq 0) ($trOpened -join ' | ')
}
function TR-Message([string]$How, [string]$Reason, [string]$Link, [double]$Since, [string]$ShotName) {
    $trSaid = Wait-Until { @(TR-Log $Since | Where-Object { $_ -like "Nixie's message*" }) | Select-Object -First 1 } -Seconds 32 -PollMs 200
    Check "trailer ($How): Nixie's message instead of the trailer" ([bool]($trSaid -and $trSaid -like "*This trailer can't play in Nixie. $Reason*")) "$trSaid"
    $trName = "This trailer can't play in Nixie. $Reason"
    $trPanel = Wait-Ui -Name $trName -Seconds 6
    Check "trailer ($How): Narrator's name of the message is what it says ('$trName')" ([bool]$trPanel)
    $trLink = Wait-Ui -Name $Link -Type 'Button' -Seconds 4
    Check "trailer ($How): the message offers '$Link' (a button)" ([bool]$trLink)
    $trOnClose = Wait-Until { $f = CK-Focus; if ($f -match "^Button 'Close'") { $f } } -Seconds 4 -PollMs 150
    Check "trailer ($How): the keyboard is on the message's Close (never on the button that opens the browser)" ([bool]$trOnClose) "$(CK-Focus)"
    if ($ShotName) { Start-Sleep -Milliseconds 600; Shot $ShotName }
    TR-NoBrowser $How $Since
    return $trLink
}

# keyboard first: error 150 (the owner allows it only on YouTube); Enter on the message's Close closes it
TR-SetTrailer 'error150' ''
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
Start-Sleep -Milliseconds 1200
$trAt = Get-Epoch
if (TR-Open 'error 150, keyboard first') {
    [void](TR-Message 'error 150' "Its owner doesn't allow it to play outside YouTube, or it was removed." 'Watch on YouTube' $trAt "$trTag-message-150-1920")
    $trAt = Get-Epoch
    Key 0x0D
    TR-Closed "Enter on the message's Close (error 150)" $trAt
}
# error 153 (YouTube refused the player): Tab to Watch on YouTube, Enter: the user's pick, the one way the browser opens
TR-SetTrailer 'error153' ''
$trAt = Get-Epoch
if (TR-Open 'error 153') {
    [void](TR-Message 'error 153' 'YouTube refused to play it here.' 'Watch on YouTube' $trAt '')
    CK-Key 'Shift+Tab'
    Start-Sleep -Milliseconds 300
    $trOnLink = CK-Focus
    $trAt2 = Get-Epoch
    Key 0x0D
    $trPicked = Wait-Until { @(TR-Log $trAt2 | Where-Object { $_ -match 'the browser would open www\.youtube\.com \(the user picked Watch on YouTube; the harness: not launched\)' }) | Select-Object -First 1 } -Seconds 5 -PollMs 150
    Check 'trailer (error 153): Enter on Watch on YouTube is the user''s pick: the browser opens YouTube (the harness: logged, not launched)' ([bool]$trPicked) "on $trOnLink; $((TR-Log $trAt2) -join ' | ')"
    Check 'trailer (error 153): the theatre stays open after the pick' (-not @(TR-Log $trAt2 | Where-Object { $_ -eq 'theatre closed' }).Count)
    $trAt = Get-Epoch
    Key 0x1B
    TR-Closed 'Esc (error 153)' $trAt
}
# the stand-in never answers: after the quiet limit (20 s from Nixie's page, no sign of the player), Nixie's message
TR-SetTrailer 'hang' ''
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900)
Start-Sleep -Milliseconds 1200
$trAt = Get-Epoch
if (TR-Open 'never starts') {
    [void](TR-Message 'never starts' "YouTube didn't start it." 'Watch on YouTube' $trAt "$trTag-message-hang-1100")
    $trAt = Get-Epoch
    Key 0x1B
    TR-Closed 'Esc (never starts)' $trAt
}
# another site (vimeo.com), mouse first: a click on Trailer opens the theatre with the message at once (no web view); a click
# on Open vimeo.com in browser is the user's pick; a click on Close closes it
TR-SetTrailer 'play' 'https://vimeo.com/76979871'
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
Start-Sleep -Milliseconds 1200
$trButton = TR-Button
Check 'trailer (another site): the Trailer button stays (there is a trailer)' ([bool]$trButton)
if ($trButton) {
    $trAt = Get-Epoch
    CK-Click $trButton
    $trOpened = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -like 'theatre opened (Nixie''s message*' }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
    Check 'trailer (another site, mouse first): a click on Trailer opens the theatre with Nixie''s message' ([bool]$trOpened) ((TR-Log $trAt) -join ' | ')
    $trLink = TR-Message 'another site' "It's on vimeo.com, which Nixie can't play." 'Open vimeo.com in browser' $trAt "$trTag-message-site-1920"
    if ($trLink) {
        $trAt2 = Get-Epoch
        CK-Click $trLink
        $trPicked = Wait-Until { @(TR-Log $trAt2 | Where-Object { $_ -match 'the browser would open vimeo\.com \(the user picked Open vimeo\.com in browser; the harness: not launched\)' }) | Select-Object -First 1 } -Seconds 5 -PollMs 150
        Check 'trailer (another site): a click on Open vimeo.com in browser is the user''s pick (logged, not launched)' ([bool]$trPicked) ((TR-Log $trAt2) -join ' | ')
    }
    $trClose = Find-Ui -Id 'CloseButton'
    if ($trClose) {
        $trAt = Get-Epoch
        CK-Click $trClose
        $trGone = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -eq 'theatre closed' }) | Select-Object -First 1 } -Seconds 5 -PollMs 150
        Check 'trailer (another site): a click on the message''s Close closes it' ([bool]$trGone)
    }
    else { Check 'trailer (another site): the message has a Close button' $false }
    # a held Enter on Trailer (auto-repeat) opens it once and leaves it open: the repeats never press the message's Close
    $trButton = TR-Button
    [void][OffscreenUi]::SetFocus($trButton)
    Start-Sleep -Milliseconds 400
    $trAt = Get-Epoch
    CK-Key 'Enter' -NoUp
    for ($trI = 0; $trI -lt 8; $trI++) { Start-Sleep -Milliseconds 60; CK-Key 'Enter' -Repeat -NoUp }
    CK-Up 'Enter'
    Start-Sleep -Milliseconds 1200
    $trOpens = @(TR-Log $trAt | Where-Object { $_ -like 'theatre opened*' }).Count
    $trCloses = @(TR-Log $trAt | Where-Object { $_ -eq 'theatre closed' }).Count
    Check 'trailer (another site): a held Enter on Trailer opens the theatre once and it stays open' ($trOpens -eq 1 -and $trCloses -eq 0) "opened $trOpens, closed $trCloses; $((TR-Log $trAt) -join ' | ')"
    TR-NoBrowser 'a held Enter' $trAt
    # Nixie 1.4.0 (review): a held Enter on the message's Close closes it once; its repeats land on Trailer and must not
    # reopen it (until the key comes up)
    $trOnClose = Wait-Until { $f = CK-Focus; if ($f -match "^Button 'Close'") { $f } } -Seconds 4 -PollMs 150
    $trAt = Get-Epoch
    CK-Key 'Enter' -NoUp
    for ($trI = 0; $trI -lt 8; $trI++) { Start-Sleep -Milliseconds 60; CK-Key 'Enter' -Repeat -NoUp }
    CK-Up 'Enter'
    Start-Sleep -Milliseconds 1200
    $trOpens = @(TR-Log $trAt | Where-Object { $_ -like 'theatre opened*' }).Count
    $trCloses = @(TR-Log $trAt | Where-Object { $_ -eq 'theatre closed' }).Count
    Check 'trailer (another site): a held Enter on the message''s Close closes the theatre once, and its repeats do not reopen it' `
        ([bool]$trOnClose -and $trCloses -eq 1 -and $trOpens -eq 0) "on $trOnClose; opened $trOpens, closed $trCloses; $((TR-Log $trAt) -join ' | ')"
    if ($trOpens) { $trAt = Get-Epoch; Key 0x1B; TR-Closed 'Esc (after the held Enter reopened it)' $trAt }
    else {
        $trBack = Wait-Until { $f = CK-Focus; if ($f -match "'Watch trailer'") { $f } } -Seconds 4 -PollMs 150
        Check 'trailer (another site): after the held Enter, the keyboard is back on Trailer' ([bool]$trBack) "$(CK-Focus)"
    }
}

# Nixie 1.4.0 (review): a pre-roll ad (the player answers, then sits unstarted for 12 s) is left to play: no "didn't start"
TR-SetTrailer 'ad' ''
$trAt = Get-Epoch
if (TR-Open 'a pre-roll ad') {
    $trPlayed = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -match 'the player: ready True, state 1, error 0' }) | Select-Object -First 1 } -Seconds 25 -PollMs 250
    $trSaid = @(TR-Log $trAt | Where-Object { $_ -like "Nixie's message*" })
    Check 'trailer (a pre-roll ad, unstarted 12 s): the trailer plays after it, no "didn''t start" message' ([bool]$trPlayed -and $trSaid.Count -eq 0) ((TR-Log $trAt) -join ' | ')
    $trAt = Get-Epoch
    Key 0x1B
    TR-Closed 'Esc (a pre-roll ad)' $trAt
}

# Nixie 1.4.0 (review): the page finishing its load late (YouTube's frame often takes long) never takes the keyboard back
# from where the user put it: Tab into the video before the load, then Space is the player's, the theatre stays open
TR-SetTrailer 'play' ''
[void](Invoke-Server '/__harness/trailer' 'POST' @{ slow = 6 })
$trButton = TR-Button
if ($trButton) {
    [void][OffscreenUi]::SetFocus($trButton)
    Start-Sleep -Milliseconds 300
    $trAt = Get-Epoch
    Key 0x0D
    [void](Wait-Until { @(TR-Log $trAt | Where-Object { $_ -match 'keyboard into the window: True' }) | Select-Object -First 1 } -Seconds 10 -PollMs 150)
    Start-Sleep -Milliseconds 600
    CK-Key 'Tab'
    $trMoved = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -like 'the user moved the keyboard inside the theatre*' }) | Select-Object -First 1 } -Seconds 4 -PollMs 150
    $trLoaded = Wait-Until { @(TR-Log $trAt | Where-Object { $_ -like 'page loaded*' }) | Select-Object -First 1 } -Seconds 20 -PollMs 200
    Start-Sleep -Milliseconds 800
    $trAt2 = Get-Epoch
    CK-Key 'Space'
    Start-Sleep -Milliseconds 1500
    $trClosedBySpace = @(TR-Log $trAt2 | Where-Object { $_ -eq 'theatre closed' }).Count
    Check 'trailer (a slow page): Tab into the video before the page loads; the load leaves the keyboard there, and Space does not close the theatre' `
        ([bool]$trMoved -and $trLoaded -like '*stays where the user put it*' -and $trClosedBySpace -eq 0 -and -not @(TR-Log $trAt | Where-Object { $_ -like 'keyboard to Close after the page loaded*' }).Count) `
        "moved: $trMoved; loaded: $trLoaded; closed by Space: $trClosedBySpace; $((TR-Log $trAt) -join ' | ')"
    if (-not $trClosedBySpace) { $trAt = Get-Epoch; Key 0x1B; TR-Closed 'Esc (a slow page, the keyboard in the video)' $trAt }
}
[void](Invoke-Server '/__harness/trailer' 'POST' @{ slow = 0 })

# Nixie 1.4.0 (review): the web view is InPrivate: YouTube's (here the stand-in's) storage never outlives a trailer. The
# stand-in marks its storage on each load and asks /__harness/trailer-seen when it finds an earlier mark
$trSeen = @(Get-Requests 0 | Where-Object { $_.path -eq '/__harness/trailer-seen' })
$trLoads = @(Get-Requests 0 | Where-Object { $_.path -like '/__harness/trailer/*' })
$trPrivate = @(TR-Log 0 | Where-Object { $_ -like 'the web view is *' })
Check 'trailer: the web view is InPrivate, and no trailer saw an earlier one''s storage' `
    ($trLoads.Count -ge 2 -and $trSeen.Count -eq 0 -and $trPrivate.Count -ge 2 -and -not ($trPrivate -match 'NOT InPrivate')) `
    "$($trLoads.Count) loads, $($trSeen.Count) saw an earlier mark; $(($trPrivate | Select-Object -Unique) -join ', ')"
[void](Invoke-Server '/__harness/trailer' 'POST' @{ mode = 'play'; url = '' })
