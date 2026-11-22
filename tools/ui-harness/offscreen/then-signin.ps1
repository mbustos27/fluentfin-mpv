<#
  1.5.1 fixes (BH-150-M2): -Then script (run-offscreen.ps1 -Script wait:1 -Then offscreen\then-signin.ps1), against the fake
  server or a QA server (-QaServer 10.11|12: the tap's /__harness/ endpoints). A sign-in the server stops accepting asks to
  sign in again on every page, and the device id Jellyfin sees is this copy's, not the PC's:
  1. "other": another copy on this PC with its own device id signs in as the same user; this copy's session goes on (R on
     Home loads). Up to 1.5.0 every copy sent the PC's id, and that sign-in ended this one.
  2. "ended": a copy with this copy's device id signs in (as two copies did up to 1.5.0); this copy's session ends. R on Home
     shows "Your sign-in to <server> ended" below the top bar, with Sign in again clear of the search box; a library and a
     search say the same in place of "Check that the server is running". On a QA server the administrator then also
     removes the device (Dashboard > Devices), the other real trigger.
  3. "again": Sign in again reaches the server's sign-in page; Quick Connect, approved as on another device, opens Home
     again, loaded.
  4. "season" (1.6.1, BH-151-m1; the fake server with FAKESERVER_BIG_SERIES=13x20 only, and by default when it is on): a
     series page is open, the server ends the sign-in, and Season 13 (not asked ahead) is picked: its list says the sign-in
     ended and offers Sign in again, not "Couldn't load the episodes" with Try again; Sign in again then signs in again in
     place (a saved password: the season's episodes come) or opens the server's sign-in page (Quick Connect).
  NIXIE_SIGNIN_PARTS (other,ended,again, and season with a big series; all by default) leaves parts out. Dot-sourced by
  drive.ps1 after the sign-in.
#>
. (Join-Path $PSScriptRoot 'search-common.ps1')
$siParts = @(([string]$(if ($env:NIXIE_SIGNIN_PARTS) { $env:NIXIE_SIGNIN_PARTS } elseif ($fixture.big_series -and -not $QaUpstream) { 'other,ended,again,season' } else { 'other,ended,again' })) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$siTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
$siRect = { param($e) if ($e) { $r = $e.Current.BoundingRectangle; '{0},{1} {2}x{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height } else { 'none' } }
function SiText([string]$Part) { Find-Ui -Name $Part -Contains -Type 'Text' }
function SiButton([string]$Name) { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, $Name) }
# Home asked its rows again since $Since and the server gave them, and nothing on the page speaks of a sign-in or a failure
function SiHomeLoaded([double]$Since) {
    $rows = Wait-Until { if (@(Get-Requests $Since | Where-Object { $_.route -in @('resume', 'nextup', 'latest') -and $_.status -eq 200 }).Count) { $true } } -Seconds 20 -PollMs 200
    Start-Sleep -Milliseconds 1500
    [bool]$rows -and -not (SiText 'Your sign-in to') -and -not (SiText "couldn't be loaded") -and -not (SiText "Couldn't load")
}
# R on Home (the hunt's repro: Home's rows asked again)
function SiRefreshHome {
    if ($script:page -ne 'home') { Open-Nav 'Home'; $script:page = 'home'; Start-Sleep -Milliseconds 800 }
    $at = Get-Epoch
    Key 0x52
    $at
}

# the device this copy signed in from: its last sign-in the server logged (report.device)
$siAuth = @(Get-Requests 0 | Where-Object { $_.route -eq 'authenticate' -and $_.status -eq 200 }) | Select-Object -Last 1
$siDevice = if ($siAuth -and $siAuth.report) { [string]$siAuth.report.device } else { '' }
Check 'signin (BH-150-M2): this copy signed in with a device id of its own (32 hex digits)' ($siDevice -match '^[0-9a-f]{32}$') "device $siDevice"
$siDevices = @(Invoke-Server '/__harness/devices')
Check 'signin: the server lists this copy''s device' ([bool]@($siDevices | Where-Object { $_.device -eq $siDevice }).Count) "$($siDevices.Count) device(s) of the user"

if ($siParts -contains 'other') {
    # 1. another copy on this PC, with its own id, signs in as the same user
    $siOther = "nixie-151-another-copy-$PID"
    $answer = Invoke-Server '/__harness/signin' 'POST' @{ device = $siOther }
    Say "another copy signed in as $siOther (it ended $($answer.ended) session(s))"
    $at = SiRefreshHome
    Check 'signin (BH-150-M2): another copy''s sign-in leaves this one signed in (R on Home loads)' (SiHomeLoaded $at) "the other copy ended $($answer.ended)"
}

if ($siParts -contains 'ended') {
    # 2. a copy with this copy's id signs in: this session ends, as two 1.5.0 copies ended each other's
    $answer = Invoke-Server '/__harness/signin' 'POST' @{ device = $siDevice }
    Check 'signin: a sign-in from this copy''s device id ends this copy''s session at the server' ($answer.ended -ge 1) "ended $($answer.ended)"
    $at = SiRefreshHome
    $notice = Wait-Until { SiText 'Your sign-in to' } -Seconds 20 -PollMs 200
    Start-Sleep -Milliseconds 800
    $notice = SiText 'Your sign-in to'
    $bar = Find-Ui -Id 'TopTabs'
    if (-not $bar) { $bar = Find-Ui -Id 'TopTabsMenu' }
    $box = Find-Ui -Name 'Search' -Type 'Edit' -Contains
    $again = SiButton 'Sign in again'
    Capture-Page "signin-home-$siTag"
    Check 'signin (BH-150-M2): R on Home says the sign-in ended (not "check that the server is running")' ([bool]$notice -and -not (SiText 'Check that the server is running')) "$(if ($notice) { $notice.Current.Name } else { 'none' })"
    Check 'signin (BH-150-M2): the notice is below the top bar''s tabs' ($notice -and $bar -and $notice.Current.BoundingRectangle.Top -ge $bar.Current.BoundingRectangle.Bottom) "notice $(& $siRect $notice); tabs $(& $siRect $bar)"
    Check 'signin (BH-150-M2): its button is Sign in again, shown and clear of the search box' ($again -and -not $again.Current.IsOffscreen -and (-not $box -or -not $again.Current.BoundingRectangle.IntersectsWith($box.Current.BoundingRectangle))) "Sign in again $(& $siRect $again); search $(& $siRect $box)"
    Check 'signin: no Try again beside it (it can never work)' (-not (SiButton 'Try again')) ''

    # a library: the message in place of the grid
    Open-Library $fixture.movie.library
    $lib = Wait-Until { SiText 'Your sign-in to' } -Seconds 20 -PollMs 200
    Start-Sleep -Milliseconds 800
    Capture-Page "signin-library-$siTag"
    Check 'signin (BH-150-M2): a library says the sign-in ended, with Sign in again' ([bool]$lib -and [bool](SiButton 'Sign in again') -and -not (SiText 'Check that the server is running')) ''

    # a search
    SrType 'matrix' -Slash
    Key 0x0D
    $sr = Wait-Until { SiText 'Your sign-in to' } -Seconds 20 -PollMs 200
    Start-Sleep -Milliseconds 800
    Capture-Page "signin-search-$siTag"
    Check 'signin (BH-150-M2): a search says the sign-in ended, with Sign in again' ([bool]$sr -and [bool](SiButton 'Sign in again') -and -not (SiText 'Check that the server is running')) ''
    Key 0x1B

    if ($QaUpstream) {
        # the other real trigger: the administrator removes the device (nothing changes for the user: it has ended already)
        $removed = Invoke-Server '/__harness/revoke' 'POST' @{ device = $siDevice }
        Say "the QA administrator removed this copy's device ($($removed.ended))"
    }
    else {
        # the fake: everything of the user's ends (sign out of all devices), still one prompt
        $revoked = Invoke-Server '/__harness/revoke' 'POST' @{}
        Say "the fake server signed the user out everywhere ($($revoked.ended) session(s))"
    }

    if ($siParts -contains 'again') {
        # 3. Sign in again: the server's sign-in page; Quick Connect, approved, opens Home again
        $button = Wait-Until { SiButton 'Sign in again' } -Seconds 10 -PollMs 200
        if (-not $button) { Open-Nav 'Home'; $script:page = 'home'; $button = Wait-Until { SiButton 'Sign in again' } -Seconds 10 -PollMs 200 }
        Say "pressed 'Sign in again' ($(if ($button) { Invoke-Element $button } else { 'none' }))"
        $quick = Wait-Until { $q = SiButton 'Use Quick Connect'; if (-not $q) { $q = SiButton 'Quick Connect' }; $q } -Seconds 20 -PollMs 150
        Capture-Page "signin-page-$siTag"
        Check 'signin (BH-150-M2): Sign in again reaches the server''s sign-in page (Quick Connect)' ([bool]$quick) ''
        if ($quick) {
            $at = Get-Epoch
            [void](Invoke-Element $quick)
            $code = Wait-Until {
                $t = Find-Ui -Id 'QuickConnectCode' -Type 'Text'
                if ($t -and ($t.Current.Name -replace '\s', '') -match '^(\d{6})$') { return $Matches[1] } } -Seconds 20 -PollMs 100
            if ($code) { [void](Invoke-Server '/__harness/quickconnect/approve' 'POST' @{ code = $code }); Say 'Quick Connect: approved' }
            $signedIn = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'authenticate' -and $_.status -eq 200 }) | Select-Object -First 1 } -Seconds 30 -PollMs 150
            $script:page = 'home'
            $siHome = $signedIn -and (SiHomeLoaded $at)
            Capture-Page "signin-again-home-$siTag"
            Check 'signin (BH-150-M2): signed in again, Home loads (nothing of the ended sign-in left)' ([bool]$siHome) "device $(if ($signedIn -and $signedIn.report) { $signedIn.report.device } else { '?' })"
            Check 'signin: the new sign-in is from the same copy''s device id' ($signedIn -and $signedIn.report -and $signedIn.report.device -eq $siDevice) ''
        }
    }
}

if ($siParts -contains 'season') {
    # 4. 1.6.1 (BH-151-m1): a series page's season list after the sign-in ended. The fake server only: on a QA server the revoke
    # would end the shared test user's other sessions
    $sisBs = $fixture.big_series
    if ($QaUpstream -or -not $sisBs) {
        Check 'signin season (BH-151-m1): the fake server has a big series (FAKESERVER_BIG_SERIES=13x20)' $false "$(if ($QaUpstream) { 'not on a QA server' } else { 'no big_series in the fixture' })"
    }
    else {
        Open-Library $sisBs.library
        Start-Sleep -Milliseconds 1200
        $sisCard = Wait-Until { Find-MediaCard $sisBs.name } -Seconds 30 -PollMs 200
        if ($sisCard) { [void](Invoke-Element $sisCard) }
        if ($env:NIXIE_LAYOUT -eq 'Editorial') {
            # Editorial's poster opens its details in place: their "Full details" opens the page
            $sisFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 5 -PollMs 100
            if ($sisFull) { [void](Invoke-Element $sisFull) }
        }
        $sisPage = Wait-ItemPage $sisBs.name -Seconds 20
        Start-Sleep -Seconds 4   # Season 1 shown, the seasons either side asked ahead (not Season 13)
        $sisRevoked = Invoke-Server '/__harness/revoke' 'POST' @{}
        Say "signin season: the server ended the sign-in ($($sisRevoked.ended) session(s)) on '$($sisBs.name)'"
        $sisPill = Wait-Until {
            @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object {
                $_.Current.Name -match '^Season 13(,|$)' -and $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Hyperlink -and
                $_.Current.ControlType -ne [System.Windows.Automation.ControlType]::Text }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
        $sisAt = Get-Epoch
        $sisPillName = if ($sisPill) { $sisPill.Current.Name } else { '' }
        if ($sisPill) { Say "signin season: picked '$sisPillName' ($(Invoke-Element $sisPill))" }
        $sisSaid = Wait-Until { $t = SiText 'Your sign-in to'; if (-not $t) { $t = SiText "Couldn't load the episodes" }; $t } -Seconds 15 -PollMs 200
        Start-Sleep -Milliseconds 800
        $sisSaid = SiText 'Your sign-in to'
        $sisAgain = SiButton 'Sign in again'
        $sisTry = SiButton 'Try again'
        # the season's list asked (refused: the fake logs a refused request as "unauthorized")
        $sisAsked = @(Get-Requests $sisAt | Where-Object { $_.route -in @('items', 'unauthorized') -and $_.path -eq '/Items' })
        Capture-Page "signin-season-$siTag"
        Check 'signin season (BH-151-m1): Season 13 was picked and asked of the server' ([bool]$sisPill -and $sisAsked.Count -gt 0) "$(if ($sisPill) { $sisPillName } else { 'no Season 13 pill' }); $($sisAsked.Count) /Items request(s), $(@($sisAsked | Where-Object { $_.status -eq 401 }).Count) refused"
        Check 'signin season (BH-151-m1): the season list says the sign-in ended (not "Couldn''t load the episodes")' ([bool]$sisSaid -and -not (SiText "Couldn't load the episodes")) "$(if ($sisSaid) { $sisSaid.Current.Name } else { 'none' })"
        Check 'signin season (BH-151-m1): its button is Sign in again, shown, and no Try again' ($sisAgain -and -not $sisAgain.Current.IsOffscreen -and -not $sisTry) "Sign in again $(& $siRect $sisAgain); Try again $([bool]$sisTry)"
        if ($sisAgain) {
            # the way out: a saved password signs in again in place and the season's episodes come; Quick Connect opens the sign-in page
            $sisAt = Get-Epoch
            Say "signin season: pressed 'Sign in again' ($(Invoke-Element $sisAgain))"
            $sisFirst = $sisBs.seasons | Where-Object { $_.index -eq 13 } | Select-Object -First 1
            $sisEpisode = if ($sisFirst -and $sisFirst.episodes) { [string]$sisFirst.episodes[0].name } else { '' }
            $sisOut = Wait-Until {
                if ($sisEpisode -and (Find-Ui -Name $sisEpisode -Contains)) { return 'episodes' }
                if ((SiButton 'Use Quick Connect') -or (SiButton 'Quick Connect')) { return 'sign-in page' } } -Seconds 25 -PollMs 200
            Capture-Page "signin-season-again-$siTag"
            Check 'signin season (BH-151-m1): Sign in again signs in in place (the episodes come) or opens the sign-in page' ([bool]$sisOut) "$(if ($sisOut) { $sisOut } else { 'neither' })"
        }
    }
}
