<#
  The basics gate, class 1 (sign-in and sessions; tools\gate\README.md): a 'then' check of run-offscreen.ps1, after the
  driver's Quick Connect sign-in, against the fake server and its two generated test users (fakeserver\credentials.json;
  never a real server). What the user sees, in each part (GATE_SI_PARTS, all by default):

  layout   the server page and the sign-in page at each gate size (GATE_SIZES) and after a live resize: the address box,
           Connect, the username and password boxes, Remember me, Sign in and Use Quick Connect are on screen, inside the
           window, and none overlaps another.
  each sign-in method, with Remember me, then three ways back in: closed and reopened; Nixie ended abruptly and started
  fresh on the same data (a PC restart or a power cut: nothing is saved at the close); and an update (the start the
  updater's helper makes after installing: its pending.json, "Updated to Nixie <version>"). Each time: Home, the Account
  button naming the user who signed in, and for Quick Connect no new sign-in at the server (no code, no password):
    password  the username and password typed;
    qc        Use Quick Connect, the code approved by the server's test user;
    picked    Switch user, the user (remembered by password) taken from the username box's list of this server's saved
              users (clicked: UI Automation Invoke; else Down and Enter): signed in at once (1.7.5).
  switch-qc  back to a Quick Connect user after Switch user (1.7.5, decided as a basics bug): the list offers them, taking
           them signs in at once with the kept token (no code, no password sent), and a reopen keeps them.
  wrong    a wrong password: the sign-in page stays, says "Wrong username or password", one refused sign-in at the server;
           the right one then signs in.
  revoked  the session ended on the server while Nixie is open (POST /__harness/revoke: "sign out of all devices", a
           removed device, a new password), then a library opened: the page says the sign-in ended (not "check this PC's
           connection"), and Nixie doesn't keep asking (no request loop over the next 20 s); Sign out and a new sign-in work.
  The rest of class 1 is in then-174-sessions.ps1 (Sign out sticks, two users and switching, two servers, a refused,
  unreachable or silent server at start) and then-174-qc-remember.ps1, registered beside this one (manifest\g1.psd1).
  Dot-sourced by drive.ps1: every name here starts with si (drive.ps1's own must not be reused).
#>
. (Join-Path $PSScriptRoot '..\gate-common.ps1')
# then-174-sessions.ps1's helpers (Ss*: the pages, the boxes, Remember me, the Account menu, the saved settings), none of
# its parts: SS_PARTS=none
$siSsParts = $env:SS_PARTS
$env:SS_PARTS = 'none'
. (Join-Path $PSScriptRoot '..\..\ui-harness\offscreen\then-174-sessions.ps1')
$env:SS_PARTS = $siSsParts
$siId = if ($env:GATE_ID) { $env:GATE_ID } else { 'signin-methods' }
$siParts = @(([string]$(if ($env:GATE_SI_PARTS) { $env:GATE_SI_PARTS } else { 'layout,password,qc,picked,switch-qc,wrong,revoked' })) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$siVersion = $(if ((Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion -match '^(\d+\.\d+\.\d+)') { $Matches[1] } else { '' })
Say "signin-methods: parts $($siParts -join ','); theme $(Get-GateTheme); Nixie $siVersion"

function SiCheck([string]$Name, [bool]$Ok, [string]$Detail = '') { Check "${siId}: $Name" $Ok $Detail }
# the starts that took an update's handoff, in Nixie's log ('[update] the install of <v> from 1.7.3: "Updated"')
function SiUpdatedLines {
    # Serilog quotes a string's value ("1.7.4") and, RELEASE.md says, the word too: taken with or without
    $rx = 'the install of "?' + [regex]::Escape($siVersion) + '"? from "?1\.7\.3"?: "?Updated'
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Select-String -Pattern $rx).Count
}
function SiAccountName { $a = SsAccount; if ($a) { "$($a.Current.Name)" } else { '' } }

# --- getting to a page -------------------------------------------------------------------------------------------------
# the server's sign-in page from wherever Nixie is: Home (Sign out, which forgets the saved sign-in of the user), the
# server page (its saved row) or already there
function SiToSignIn {
    $p = SsPage
    if ($p -eq 'home') { [void](SsAccountMenu 'Sign out'); $p = SsWaitPage @('server') 15 }
    if ($p -eq 'server') { if (-not (SsOpenServerRow $serverName)) { Submit-Address $script:serverUrl; [void](SsWaitPage @('signin') 20) } }
    SsSettle 600
    (SsPage) -eq 'signin'
}
# Quick Connect on the sign-in page, Remember me as asked, the code approved by the server's test user; true on Home
function SiQuick([bool]$Remember = $true) {
    $quick = Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect') } -Seconds 20 -PollMs 150
    if (-not $quick) { Say 'signin-methods: no Use Quick Connect'; return $false }
    SsRemember $Remember
    SsSettle 300
    [void](Invoke-Element $quick)
    $code = Wait-Until { $t = Find-Ui -Id 'QuickConnectCode' -Type 'Text'; if ($t -and ($t.Current.Name -replace '\s', '') -match '^(\d{6})$') { $Matches[1] } } -Seconds 20 -PollMs 100
    if (-not $code) { Say 'signin-methods: Quick Connect showed no code'; return $false }
    [void](Invoke-Server '/__harness/quickconnect/approve' 'POST' @{ code = $code })
    $pg = SsWaitPage @('home') 30
    if ($pg -eq 'home') { $script:navPane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); $script:page = 'home'; SsSettle 1200 }
    $pg -eq 'home'
}
# Switch user, then the user picked from the username box's list (the keyboard: the name's first letter typed, Down onto
# the first saved user, Enter), then Sign in; what was picked and whether Home showed
function SiPick([string]$Name) {
    if (-not (SsAccountMenu 'Switch user')) { return [pscustomobject]@{ Picked = ''; Home = $false; Why = 'no Switch user' } }
    if ((SsWaitPage @('signin') 15) -ne 'signin') { return [pscustomobject]@{ Picked = ''; Home = $false; Why = 'no sign-in page' } }
    SsSettle 800
    $box = SsUserBox
    Set-ElementValue $box ''
    [void][OffscreenUi]::SetFocus($box); SsSettle 300
    $at = Get-Epoch
    Key ([int][char]::ToUpperInvariant($Name[0])); SsSettle 700
    $item = Wait-Until { Find-Ui -Name $Name -Type 'ListItem' -OffscreenToo } -Seconds 5 -PollMs 200
    $listed = [bool]$item
    # the name in the list clicked (its UI Automation Invoke: the list's ItemClick, as a click; SuggestionChosen fills the
    # password); else Down through the suggestions until the box has the name, then Enter (the keyboard's way). 1.7.5: a
    # saved user taken from the list signs in at once (AtOnce), with no Sign in pressed
    $how = 'none'
    $atOnce = $false
    if ($item) { $how = Invoke-Element $item; $atOnce = (SsWaitPage @('home') 8) -eq 'home' }
    $picked = if ($atOnce) { $Name } else { SsValue (SsUserBox) }
    if (-not $atOnce -and $picked -ne $Name) {
        $how = 'Down'
        for ($i = 0; $i -lt 4 -and $picked -ne $Name; $i++) { Key 0x28; SsSettle 500; $picked = SsValue (SsUserBox) }
        Key 0x0D
        $atOnce = $picked -eq $Name -and (SsWaitPage @('home') 8) -eq 'home'
        if (-not $atOnce) { $picked = SsValue (SsUserBox) }
    }
    Say "signin-methods: picked '$picked' from the list ($how)$(if ($atOnce) { ', signed in at once' })"
    if ($picked -ne $Name) { return [pscustomobject]@{ Picked = $picked; Listed = $listed; Home = $false; AtOnce = $false; Auth = @(); Codes = 0; Texts = SsTexts } }
    if (-not $atOnce) {
        $sign = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Sign in')
        if ($sign) { [void](Invoke-Element $sign) }
    }
    $pg = SsWaitPage @('home') 20
    if ($pg -eq 'home') { $script:navPane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); $script:page = 'home'; SsSettle 1200 }
    $since = @(Get-Requests $at)
    $auth = @($since | Where-Object { $_.path -match '^/Users/AuthenticateByName$' } | ForEach-Object { $_.status })
    [pscustomobject]@{ Picked = $picked; Listed = $listed; Home = ($pg -eq 'home'); AtOnce = $atOnce; Auth = $auth
        Codes = @($since | Where-Object { $_.path -match '^/QuickConnect/' }).Count; Texts = $(if ($pg -ne 'home') { SsTexts } else { '' }) }
}

# --- the three ways back in ----------------------------------------------------------------------------------------------
# reopen: closed as a user closes it; kill: ended abruptly (a PC restart, a power cut); update: the start after an update
# (the helper's pending.json and its installer's exit, as then-update.ps1's 7b leaves them). Then the page it opens, the
# sign-ins the server saw, the Account button
function SiRestart([string]$Tag, [string]$How) {
    if ($How -eq 'kill') {
        Say "signin-methods ($Tag): Nixie#$($script:ui.Id) ended abruptly, as a PC restart or a power cut ends it"
        try { $script:ui.Kill() } catch {}
        [void]$script:ui.WaitForExit(15000)
        SsSettle 1500
    }
    else { Step-Close $Tag }
    if ($How -eq 'update') {
        $updates = Join-Path $DataDir 'Updates'
        New-Item -ItemType Directory -Force $updates | Out-Null
        $pending = [ordered]@{ Version = $siVersion; FromVersion = '1.7.3'; StartedAt = ([DateTimeOffset]::UtcNow).ToString('o'); PageUrl = "https://example.invalid/v$siVersion"
            Notes = 'the gate''s update'; Reopen = $true; Resume = [ordered]@{ PageKey = 'FluentFin.Core.ViewModels.HomeViewModel'; ItemId = $null } } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText((Join-Path $updates 'pending.json'), $pending, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $updates 'install-result.txt'), "exit=0`r`n", [Text.UTF8Encoding]::new($false))
    }
    $updatedBefore = SiUpdatedLines
    $script:navPane = $null
    $at = Get-Epoch
    Start-App "$($Tag)_window_ms"
    [void](SsWaitPage @('server', 'home', 'signin') 40)
    SsSettle 1500
    $pg = SsPage
    if ($pg -eq 'home') { $script:navPane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); $script:page = 'home' }
    $since = @(Get-Requests $at)
    [pscustomobject]@{
        Page = $pg; Account = SiAccountName
        Auth = @($since | Where-Object { $_.path -match '^/Users/AuthenticateByName$' } | ForEach-Object { $_.status })
        Codes = @($since | Where-Object { $_.path -match '^/QuickConnect/' }).Count
        Updated = $(if ($How -eq 'update') { (SiUpdatedLines) -gt $updatedBefore } else { $null })
        Notice = $(if ($How -eq 'update') { [bool](Wait-Until { Find-Ui -Name "Updated to Nixie $siVersion" -Contains -Type 'Text' } -Seconds 8 -PollMs 200) } else { $null })
        Texts = $(if ($pg -ne 'home') { SsTexts } else { '' })
    }
}
# each way back in for a method; $NoSignIn: the start must sign in nowhere (a remembered Quick Connect keeps its token)
function SiWaysBackIn([string]$Method, [string]$User, [bool]$NoSignIn) {
    foreach ($how in @('reopen', 'kill', 'update')) {
        $r = SiRestart "$Method-$how" $how
        $ok = $r.Page -eq 'home' -and $r.Account -eq "Account, $User"
        SiCheck "[$Method, $how]: Home, signed in as $User" $ok "page '$($r.Page)'; '$($r.Account)'$(if ($r.Texts) { "; texts: $($r.Texts)" })"
        if ($NoSignIn) {
            SiCheck "[$Method, $how]: no new sign-in at the server (no password, no code)" ($r.Auth.Count -eq 0 -and $r.Codes -eq 0) "sign-ins: $($r.Auth.Count) ($($r.Auth -join ',')); Quick Connect requests: $($r.Codes)"
        }
        else {
            SiCheck "[$Method, $how]: at most one sign-in at the server, answered" ($r.Auth.Count -le 1 -and @($r.Auth | Where-Object { $_ -ne 200 }).Count -eq 0) "sign-ins: $($r.Auth -join ',')"
        }
        if ($how -eq 'update') { SiCheck "[$Method, update]: the start took the update's handoff (Nixie's log: the install of $siVersion, Updated)" ([bool]$r.Updated) "the notice 'Updated to Nixie $siVersion' shown: $($r.Notice)" }
        if ($r.Page -ne 'home') { Capture-State "si-$Method-$how"; return }
    }
}

# --- layout: the server page and the sign-in page at each size ------------------------------------------------------------
# each named control: on screen, inside the window's client area, no two overlapping
function SiLayout([string]$Page, [string[]]$Want, $Size) {
    $client = [GateWindow]::ClientOnScreen($script:hwnd)
    $rects = [ordered]@{}
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($n in $Want) {
        $e = switch ($n) {
            'address' { Find-Ui -Id 'TxtUrl' -Type 'Edit' -OffscreenToo }
            'username' { SsUserBox }
            'password' { SsPassBox }
            'Remember me' { Find-Ui -Name 'Remember me' -Type 'CheckBox' -OffscreenToo }
            default { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, $n) }
        }
        if (-not $e) { $bad.Add("$n missing"); continue }
        $r = [OffscreenUi]::RectOf($e)
        if ($e.Current.IsOffscreen -or $r.Width -lt 4 -or $r.Height -lt 4) { $bad.Add("$n not on screen"); continue }
        if ($r.X -lt $client[0] - 1 -or $r.Y -lt $client[1] - 1 -or ($r.X + $r.Width) -gt $client[2] + 1 -or ($r.Y + $r.Height) -gt $client[3] + 1) {
            $bad.Add(("{0} outside the window ({1:0},{2:0} {3:0}x{4:0}; client {5})" -f $n, $r.X, $r.Y, $r.Width, $r.Height, ($client -join ',')))
        }
        $rects[$n] = $r
    }
    $names = @($rects.Keys)
    for ($i = 0; $i -lt $names.Count; $i++) {
        for ($j = $i + 1; $j -lt $names.Count; $j++) {
            $a = $rects[$names[$i]]; $b = $rects[$names[$j]]
            $w = [Math]::Min($a.X + $a.Width, $b.X + $b.Width) - [Math]::Max($a.X, $b.X)
            $h = [Math]::Min($a.Y + $a.Height, $b.Y + $b.Height) - [Math]::Max($a.Y, $b.Y)
            if ($w -gt 2 -and $h -gt 2) { $bad.Add("$($names[$i]) overlaps $($names[$j])") }
        }
    }
    SiCheck "[layout, $Page, $($Size.Label)]: every control on screen, inside the window, none overlapping" ($bad.Count -eq 0) "$(Format-GateSize $Size)$(if ($bad.Count) { ': ' + ($bad -join '; ') })"
}

$siServerWant = @('address', 'Connect')
$siSignInWant = @('username', 'password', 'Remember me', 'Sign in', 'Use Quick Connect')

# ==== layout ==============================================================================================================
if ($siParts -contains 'layout') {
    [void](SsAccountMenu 'Sign out')
    if ((SsWaitPage @('server') 15) -eq 'server') {
        SsSettle 800
        foreach ($size in @(Get-GateSizes)) { $s = Set-GateSize $size; SiLayout 'server page' $siServerWant $s }
        $s = Invoke-GateLiveResize; SiLayout 'server page' $siServerWant $s
        Shot 'si-layout-server'
    }
    else { SiCheck '[layout]: Sign out shows the server page' $false "page: $(SsPage)" }
    if (SiToSignIn) {
        foreach ($size in @(Get-GateSizes)) { $s = Set-GateSize $size; SiLayout 'sign-in page' $siSignInWant $s }
        $s = Invoke-GateLiveResize; SiLayout 'sign-in page' $siSignInWant $s
        Shot 'si-layout-signin'
        [void](SiQuick $true)
    }
    else { SiCheck '[layout]: the saved server''s row opens its sign-in page' $false "page: $(SsPage)" }
    [void](Set-GateSize 'max')
}

# ==== password ============================================================================================================
if ($siParts -contains 'password') {
    $ok = (SiToSignIn) -and (SsSignIn $ssCreds $true)
    SiCheck '[password]: signed in with the username and password, Remember me on' $ok
    if ($ok) { SiWaysBackIn 'password' $ssCreds.username $false }
}

# ==== Quick Connect =======================================================================================================
if ($siParts -contains 'qc') {
    $ok = (SiToSignIn) -and (SiQuick $true)
    SiCheck '[qc]: signed in with Quick Connect, Remember me on' $ok
    if ($ok) { SiWaysBackIn 'qc' $ssCreds.username $true }
}

# ==== a user picked from the list (remembered by password; another user signed in since) ===================================
if ($siParts -contains 'picked') {
    $ok = (SiToSignIn) -and (SsSignIn $ssCreds $true)
    if ($ok -and (SsAccountMenu 'Switch user') -and (SsWaitPage @('signin') 15) -eq 'signin') { SsSettle 600; $ok = SsSignIn $ssCreds.second_user $true } else { $ok = $false }
    SiCheck "[picked]: $($ssCreds.username) remembered by password, then $($ssCreds.second_user.username) signed in" $ok "account '$(SiAccountName)'"
    if ($ok) {
        $p = SiPick $ssCreds.username
        SiCheck '[picked]: Switch user lists the saved users in the username box' ([bool]$p.Listed)
        SiCheck "[picked]: picking $($ssCreds.username) from the list fills the name" ($p.Picked -eq $ssCreds.username) "username box '$($p.Picked)'"
        if ($p.Picked -ne $ssCreds.username) { Capture-State 'si-picked-not-picked' }
        else {
            $refused = @($p.Auth | Where-Object { $_ -ne 200 }).Count
            $asUser = $p.Home -and (SiAccountName) -eq "Account, $($ssCreds.username)"
            SiCheck '[picked]: taking the saved user from the list signs them in at once, no refused sign-in at the server' ($asUser -and $p.AtOnce -and $refused -eq 0) "home: $($p.Home); at once: $($p.AtOnce); '$(SiAccountName)'; sign-ins: $($p.Auth -join ','); texts: $($p.Texts)"
            if ($asUser) { SiWaysBackIn 'picked' $ssCreds.username $false } else { Capture-State 'si-picked-refused' }
        }
    }
}

# ==== switching back to a Quick Connect user ==================================================================================
# 1.7.5 (decided as a basics bug; 1.7.4 ended the Quick Connect session at Switch user and forgot its token, so going back
# needed a new code): Switch user keeps the leaving user's saved sign-in. Back on Switch user's page the Quick Connect user
# is offered in the list, taking them signs in at once with the saved token (no code, no password sent), and a reopen
# keeps them (the last user signed in)
if ($siParts -contains 'switch-qc') {
    $ok = (SiToSignIn) -and (SiQuick $true)
    if ($ok -and (SsAccountMenu 'Switch user') -and (SsWaitPage @('signin') 15) -eq 'signin') { SsSettle 600; $ok = SsSignIn $ssCreds.second_user $true } else { $ok = $false }
    SiCheck "[switch-qc]: $($ssCreds.username) by Quick Connect, then Switch user to $($ssCreds.second_user.username)" $ok "account '$(SiAccountName)'"
    if ($ok) {
        $p = SiPick $ssCreds.username
        SiCheck "[switch-qc]: Switch user's list offers $($ssCreds.username), kept from the Quick Connect sign-in" ([bool]$p.Listed) "username box '$($p.Picked)'"
        $asUser = $p.Home -and (SiAccountName) -eq "Account, $($ssCreds.username)"
        SiCheck "[switch-qc]: taking $($ssCreds.username) from the list signs in at once, with no code and no password sent" ($asUser -and $p.AtOnce -and $p.Auth.Count -eq 0 -and $p.Codes -eq 0) "home: $($p.Home); at once: $($p.AtOnce); '$(SiAccountName)'; sign-ins: $($p.Auth -join ','); codes: $($p.Codes); texts: $($p.Texts)"
        if ($asUser) {
            $r = SiRestart 'switch-qc-reopen' 'reopen'
            SiCheck "[switch-qc, reopen]: Home, signed in as $($ssCreds.username) (the last one), no new sign-in" ($r.Page -eq 'home' -and $r.Account -eq "Account, $($ssCreds.username)" -and $r.Auth.Count -eq 0 -and $r.Codes -eq 0) "page '$($r.Page)'; '$($r.Account)'; sign-ins: $($r.Auth -join ','); codes: $($r.Codes)"
        }
        else { Capture-State 'si-switch-qc-back' }
    }
}

# ==== a wrong password ======================================================================================================
if ($siParts -contains 'wrong') {
    if (SiToSignIn) {
        $at = Get-Epoch
        $wrong = [pscustomobject]@{ username = $ssCreds.username; password = 'not-the-password-' + [Guid]::NewGuid().ToString('N').Substring(0, 6) }
        $home_ = SsSignIn $wrong $true
        $said = Wait-Until { Find-Ui -Name 'Wrong username or password' -Contains -Type 'Text' } -Seconds 10 -PollMs 150
        $auth = @(Get-Requests $at | Where-Object { $_.path -match '^/Users/AuthenticateByName$' } | ForEach-Object { $_.status })
        SiCheck '[wrong]: a wrong password stays on the sign-in page, which says "Wrong username or password"' (-not $home_ -and (SsPage) -eq 'signin' -and [bool]$said) "page: $(SsPage); texts: $(SsTexts)"
        SiCheck '[wrong]: one refused sign-in at the server' ($auth.Count -eq 1 -and $auth[0] -eq 401) "sign-ins: $($auth -join ',')"
        Capture-State 'si-wrong-password'
        SiCheck '[wrong]: the right password then signs in' (SsSignIn $ssCreds $true)
    }
    else { SiCheck '[wrong]: the sign-in page' $false "page: $(SsPage)" }
}

# ==== the session revoked while Nixie is open ==================================================================================
if ($siParts -contains 'revoked') {
    if ((SsPage) -ne 'home') { [void]((SiToSignIn) -and (SsSignIn $ssCreds $true)) }
    if ((SsPage) -eq 'home') {
        $ended = Invoke-Server '/__harness/revoke' 'POST' @{}
        Say "signin-methods (revoked): the server ended $($ended.ended) session(s) of the test user"
        $at = Get-Epoch
        try { Open-Library 'Movies' } catch { Say "signin-methods (revoked): Movies didn't open: $($_.Exception.Message)" }
        $said = Wait-Until { $t = SsTexts; if ($t -match '(?i)sign(ed)?[ -]?(in|out)\b|sign-in') { $t } } -Seconds 15 -PollMs 300
        $texts = SsTexts
        Capture-State 'si-revoked'
        SiCheck '[revoked]: the page says the sign-in ended (not "check this PC''s connection")' ([bool]$said -and $texts -notmatch "(?i)check this PC's connection") "page: $(SsPage); texts: $texts"
        $quietFrom = $at + 10
        Start-Sleep -Seconds ([Math]::Max(0, [int]($at + 20 - (Get-Epoch))))
        $after = @(Get-Requests $at | Where-Object { $_.t -lt $at + 20 -and (Get-Origin $_) -ne 'harness' })
        $late = @($after | Where-Object { $_.t -ge $quietFrom -and $_.status -eq 401 })
        $top = $after | Group-Object path | Sort-Object Count -Descending | Select-Object -First 3 | ForEach-Object { "$($_.Name) x$($_.Count)" }
        SiCheck '[revoked]: no request loop (in its second 10 s, at most 2 refused requests)' ($late.Count -le 2) ("{0} requests in 20 s, {1} refused in the last 10 s; most: {2}" -f $after.Count, $late.Count, ($top -join ', '))
        # the way out the page gives: its Sign in again, to this server's sign-in page with the name; else the Account
        # menu's Sign out and the server's row
        $again = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Sign in again')
        $way = if ($again) { 'Sign in again' } else { 'Sign out' }
        if ($again) { [void](Invoke-Element $again); [void](SsWaitPage @('signin', 'server') 15) }
        $pg = SsPage
        if ($pg -eq 'home') { [void](SsAccountMenu 'Sign out'); $pg = SsWaitPage @('server', 'signin') 15 }
        if ($pg -eq 'server') { [void](SsOpenServerRow $serverName) }
        SsSettle 600
        $name = if ((SsPage) -eq 'signin') { SsValue (SsUserBox) } else { '' }
        if ($again) { SiCheck '[revoked]: Sign in again opens this server''s sign-in page with the name' ((SsPage) -eq 'signin' -and $name -eq $ssCreds.username) "page: $(SsPage); username '$name'" }
        $back = ((SsPage) -eq 'signin') -and (SsSignIn $ssCreds $true)
        SiCheck "[revoked]: $way and a new sign-in bring Home back" $back "page: $(SsPage)"
    }
    else { SiCheck '[revoked]: signed in first' $false "page: $(SsPage)" }
}
