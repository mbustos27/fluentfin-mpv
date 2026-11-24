<#
  174-sessions (the desktop basics sweep of 1.7.3: BS-M1 to BS-M4, BS-m1 to BS-m3): what a sign-in leaves on this PC and
  what the next start does with it, on the real app against the fake server and its two generated test users (their
  passwords are the fixture's, fakeserver\credentials.json; never a real server's). -Then script of run-offscreen.ps1 after
  the driver's Quick Connect sign-in:

    run-offscreen.ps1 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script wait:1 -NoA11y `
        -Then tools\ui-harness\offscreen\then-174-sessions.ps1

  1. bs6 (BS-M1, BS-m3): a password sign-in with "Remember me", reopened (Home), then Sign out and reopened: the server
     page, no sign-in at the server; its row opens the sign-in page with the name filled in and "Remember me" ticked.
  1b. bsN (the review's m2): Switch user, the second user signs in with "Remember me" off, reopened: the server page,
     nothing sent (it signed in as the first user, remembered before).
  2. bs8 (BS-M2): Switch user to the second user, both remembered, reopened: Home as the second user (the server page,
     with one saved user only, before). Then (the review's Major) Switch user again: the first user's name ticks
     "Remember me", the sign-in leaves the box as it is, reopened: Home as the first user, both still saved.
  3. bsD (BS-M2): a second saved server beside it, reopened: Home; that server made the last one signed in to, reopened:
     signed in to it.
  4. bsB (BS-m1): Sign out, then a sign-in with "Remember me" unticked for the remembered name, reopened: the server page,
     nothing sent, no saved sign-in of that user left.
  5. bsC (BS-M3, BS-M4): a saved password the server refuses (stale), three starts: the first opens the sign-in page with
     "The saved sign-in to ... no longer works. Sign in again." and the name; one refused sign-in in all three.
  6. bs9 (BS-M3): the saved address refuses the connection: the server page says "Couldn't sign in to ... Can't reach".
  7. bsF (BS-M3): the server doesn't answer a sign-in (the fake's hang rule): the server page within 10 s and a little,
     saying it "didn't answer in time".
  8. bsE (BS-m2): Settings > Remove server on the server in use: signed out, the server page without it; signed in again
     through its address with "Remember me", reopened: Home (the server was saved again).
  SS_PARTS (bs6,bsN,bs8,bsD,bsB,bsC,bs9,bsF,bsE; all by default) leaves parts out. Dot-sourced by drive.ps1.
#>
. (Join-Path $PSScriptRoot 'states-common.ps1')
Add-Type -AssemblyName System.Security
$ssCreds = Get-Content (Join-Path $PSScriptRoot '..\fakeserver\credentials.json') -Raw | ConvertFrom-Json
$ssParts = @(([string]$(if ($env:SS_PARTS) { $env:SS_PARTS } else { 'bs6,bsN,bs8,bsD,bsB,bsC,bs9,bsF,bsE' })) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$ssFile = Join-Path $DataDir 'LocalSettings.json'

function SsSettle([int]$Ms = 800) { Start-Sleep -Milliseconds $Ms }
function SsAccount { Find-Ui -Name 'Account, ' -Type 'Button' -Contains }
function SsPage {
    if (Find-Ui -Id 'TxtUrl' -Type 'Edit') { return 'server' }
    if ([OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect')) { return 'signin' }
    $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id)
    if ($p -and ([OffscreenUi]::NavItem($p, 'Home') -or (Find-Ui -Id 'TopTabsMenu'))) { return 'home' }
    $null
}
function SsWaitPage([string[]]$Want, [double]$Seconds = 30) { Wait-Until { $p = SsPage; if ($p -and ($Want -contains $p)) { $p } } -Seconds $Seconds -PollMs 150 }
# every visible text on the page (the fake server's names only)
function SsTexts {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { -not $_.Current.IsOffscreen } | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
}
function SsAccountMenu([string]$Item) {
    $b = SsAccount
    if (-not $b) { Say "sessions: no Account button"; return $false }
    [void](Invoke-Element $b)
    $i = Wait-Ui -Name $Item -Type 'Button' -Seconds 8
    if (-not $i) { Say "sessions: no '$Item' in the Account menu"; return $false }
    SsSettle 400
    [void](Invoke-Element $i)
    $true
}
function SsUserBox { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Cached.ClassName -ne 'PasswordBox' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1 }
function SsPassBox { [OffscreenUi]::FindByClass($script:hwnd, $script:ui.Id, 'Edit', 'PasswordBox') }
function SsValue($e) { $p = $null; if ($e -and $e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.Current.Value } else { '<none>' } }
function SsRememberState { $cb = Find-Ui -Name 'Remember me' -Type 'CheckBox'; if ($cb) { "$($cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)" } else { '<none>' } }
function SsRemember([bool]$On) {
    $cb = Find-Ui -Name 'Remember me' -Type 'CheckBox'
    if (-not $cb) { Say 'sessions: no Remember me'; return }
    $t = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $want = if ($On) { 'On' } else { 'Off' }
    for ($i = 0; $i -lt 3 -and "$($t.Current.ToggleState)" -ne $want; $i++) { $t.Toggle(); SsSettle 150 }
}
# on the sign-in page: the fixture's test user (or the second) with its generated password, "Remember me" set as asked
# ($null: as the page has it); true once Home shows
function SsSignIn($User, $Remember) {
    $u = SsUserBox; $p = SsPassBox
    if (-not $u -or -not $p) { Say 'sessions: no username or password box'; return $false }
    Set-ElementValue $u $User.username; Set-ElementValue $p $User.password   # the fake server's generated test password
    if ($null -ne $Remember) { SsSettle 300; SsRemember ([bool]$Remember) }
    SsSettle 300
    [void](Invoke-Element ([OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Sign in')))
    $pg = SsWaitPage @('home') 30
    if ($pg -eq 'home') { $script:navPane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); $script:page = 'home'; SsSettle 1200 }
    $pg -eq 'home'
}
function SsOpenServerRow([string]$Name = 'Harness Jellyfin (fake)') {
    $row = Wait-Until { Find-Ui -Name $Name -Type 'ListItem' } -Seconds 10 -PollMs 150
    if (-not $row) { Say "sessions: no saved-server row '$Name'. texts: $(SsTexts)"; return $false }
    [void](Invoke-Element $row)
    [bool](SsWaitPage @('signin') 15)
}
# close, start again on the same data folder: the page it opens, and the sign-ins the server saw meanwhile (their statuses)
function SsRestart([string]$Tag, [scriptblock]$Before) {
    Step-Close $Tag
    if ($Before) { & $Before }
    $script:navPane = $null
    $at = Get-Epoch
    Start-App "$($Tag)_window_ms"
    [void](SsWaitPage @('server', 'home', 'signin') 40)
    $shown = Get-Epoch
    SsSettle 1500
    $pg = SsPage
    if ($pg -eq 'home') { $script:navPane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); $script:page = 'home' }
    $auth = @(Get-Requests $at | Where-Object { $_.path -match '^/Users/AuthenticateByName$' } | ForEach-Object { $_.status })
    $acct = SsAccount
    $r = [pscustomobject]@{ Page = $pg; Auth = $auth; Seconds = [Math]::Round($shown - $script:launchedAt, 1); Account = $(if ($acct) { $acct.Current.Name } else { '' }) }
    Say "sessions ($Tag): page '$($r.Page)' after $($r.Seconds) s; sign-ins at start: $($auth.Count) ($($auth -join ',')); account '$($r.Account)'"
    $r
}
function SsSettings { Get-Content -LiteralPath $ssFile -Raw -Encoding utf8 | ConvertFrom-Json }
function SsSaveSettings($Set) { [IO.File]::WriteAllText($ssFile, ($Set | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false)) }
function SsServers { @((SsSettings).Servers | Where-Object { $null -ne $_ }) }
function SsProtect([string]$Text, $Set) { [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Text), [Convert]::FromBase64String($Set.Entropy), 'CurrentUser')) }
# from Home or the server page: signed in as $User with "Remember me" on, through the saved server's row
function SsSignedInRemembered($User) {
    if ((SsPage) -eq 'home') { [void](SsAccountMenu 'Sign out'); [void](SsWaitPage @('server') 15) }
    if (-not (SsOpenServerRow)) { return $false }
    SsSettle 600
    SsSignIn $User $true
}

$serverName = 'Harness Jellyfin (fake)'

# ---- 1. bs6: Sign out sticks (BS-M1); the sign-in page keeps the name (BS-m3)
if ($ssParts -contains 'bs6') {
    Check 'sessions bs6: signed in with a password and Remember me' (SsSignedInRemembered $ssCreds)
    $r = SsRestart 'bs6-remembered'
    Check 'sessions bs6: reopened, still signed in (Home)' ($r.Page -eq 'home') "page: $($r.Page)"
    Check 'sessions bs6: Sign out from the Account menu' (SsAccountMenu 'Sign out')
    Check 'sessions bs6: Sign out lands on the server page' ((SsWaitPage @('server') 15) -eq 'server')
    $users = @((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ })
    Check 'sessions bs6: Sign out forgets the saved password (the file keeps no sign-in)' ($users.Count -eq 0) "saved users: $($users.Count)"
    $r = SsRestart 'bs6-signed-out'
    Check 'sessions bs6 (BS-M1): after Sign out a restart stays signed out, nothing sent' ($r.Page -eq 'server' -and $r.Auth.Count -eq 0) "page: $($r.Page); sign-ins at start: $($r.Auth.Count)"
    Shot 'bs6-signed-out-start'
    if (SsOpenServerRow) {
        SsSettle 800
        $name = SsValue (SsUserBox); $pw = SsValue (SsPassBox); $keep = SsRememberState
        Check 'sessions bs6 (BS-m3): the sign-in page opens with the name, Remember me ticked, no password' ($name -eq $ssCreds.username -and $keep -eq 'On' -and ($pw -eq '' -or $pw -eq '<none>')) "username '$name'; Remember me $keep"
        $focus = Focused-Element
        Check 'sessions bs6: the keyboard is in the password box' ($focus -and $focus.name -eq 'Password') "focused: $(if ($focus) { "$($focus.type) '$($focus.name)'" })"
        Capture-State 'bs6-signin-name'
        [void](SsSignIn $ssCreds $true)
    }
}

# ---- 1b. bsN: another user signs in without Remember me: the next start signs in as nobody (the review's m2)
if ($ssParts -contains 'bsN') {
    if ((SsPage) -ne 'home') { [void](SsSignedInRemembered $ssCreds) }
    Check 'sessions bsN: Switch user' (SsAccountMenu 'Switch user')
    [void](SsWaitPage @('signin') 15)
    Check 'sessions bsN: the second user signs in, Remember me off' (SsSignIn $ssCreds.second_user $false)
    $r = SsRestart 'bsN'
    Check "sessions bsN (the review's m2): the last sign-in wasn't remembered, reopened: the server page, nothing sent" ($r.Page -eq 'server' -and $r.Auth.Count -eq 0) "page: $($r.Page); sign-ins at start: $($r.Auth.Count) ($($r.Auth -join ','))"
    $users = @((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ } | ForEach-Object { $_.Username })
    Check 'sessions bsN: the first user''s saved sign-in is still there' ($users -contains $ssCreds.username -and $users.Count -eq 1) "saved: $($users -join ',')"
}

# ---- 2. bs8: two users, the last one signs in (BS-M2)
if ($ssParts -contains 'bs8') {
    if ((SsPage) -ne 'home') { [void](SsSignedInRemembered $ssCreds) }
    Check 'sessions bs8: Switch user' (SsAccountMenu 'Switch user')
    Check 'sessions bs8: Switch user opens the sign-in page' ((SsWaitPage @('signin') 15) -eq 'signin')
    Check 'sessions bs8: the second user signs in, Remember me on' (SsSignIn $ssCreds.second_user $true)
    $users = @((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ })
    Check 'sessions bs8: both users are saved' ($users.Count -eq 2) "saved users: $($users.Count)"
    $r = SsRestart 'bs8'
    Check 'sessions bs8 (BS-M2): two users saved, reopened: signed in as the last one' ($r.Page -eq 'home' -and $r.Account -match [regex]::Escape($ssCreds.second_user.username)) "page: $($r.Page); account '$($r.Account)'"
    Check 'sessions bs8: one sign-in at start, answered' ($r.Auth.Count -eq 1 -and $r.Auth[0] -eq 200) "$($r.Auth -join ',')"
    Shot 'bs8-second-user'

    # the review's Major: a saved user picked on Switch user's page keeps their sign-in: their name ticks "Remember me"
    Check 'sessions bs8: Switch user again' (SsAccountMenu 'Switch user')
    [void](SsWaitPage @('signin') 15)
    Set-ElementValue (SsUserBox) $ssCreds.username
    SsSettle 600
    $keep = SsRememberState
    Check "sessions bs8 (the review's Major): a saved user's name ticks Remember me" ($keep -eq 'On') "Remember me $keep"
    Check 'sessions bs8: the first user signs in, the box left as it is' (SsSignIn $ssCreds $null)
    $r = SsRestart 'bs8-picked'
    $users = @((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ })
    Check "sessions bs8 (the review's Major): reopened: signed in as the user picked, both still saved" ($r.Page -eq 'home' -and $r.Account -eq "Account, $($ssCreds.username)" -and $users.Count -eq 2) "page: $($r.Page); account '$($r.Account)'; saved users: $($users.Count)"
}

# ---- 3. bsD: two saved servers (BS-M2)
if ($ssParts -contains 'bsD') {
    if ((SsPage) -ne 'home') { [void](SsSignedInRemembered $ssCreds) }
    $r = SsRestart 'bsD' {
        $set = SsSettings
        $first = @($set.Servers | Where-Object { $null -ne $_ })[0]
        # a second saved server: an address nothing answers (loopback, port 9), its own id, no sign-in
        $second = [pscustomobject]@{ DisplayName = 'Second server'; PublicUrl = 'http://127.0.0.1:9'; LocalUrl = 'http://127.0.0.1:9'; LocalNetworkNames = @(); Id = 'harness-second-server'; Users = @() }
        $set.Servers = @($first, $second)
        SsSaveSettings $set
    }
    Check 'sessions bsD (BS-M2): with a second saved server, reopened: signed in to the one used last' ($r.Page -eq 'home' -and $r.Auth.Count -eq 1) "page: $($r.Page); sign-ins at start: $($r.Auth.Count)"
    # the second one signed in to last: the same fake under another id and name, the user's saved sign-in copied
    $r = SsRestart 'bsD-second' {
        $set = SsSettings
        $all = @($set.Servers | Where-Object { $null -ne $_ })
        $first = $all[0]
        $copy = @($first.Users | Where-Object { $null -ne $_ } | Where-Object { $_.Username -eq $first.LastUser })
        $second = [pscustomobject]@{ DisplayName = 'Second server'; PublicUrl = $script:serverUrl; LocalUrl = $script:serverUrl; LocalNetworkNames = @(); Id = 'harness-second-server'
            Users = $copy; LastUser = $first.LastUser; LastSignIn = ([DateTimeOffset]::UtcNow.AddMinutes(1)).ToString('o') }
        $set.Servers = @($first, $second)
        SsSaveSettings $set
    }
    $acct = SsAccount
    $menuServer = $null
    if ($acct) { [void](Invoke-Element $acct); SsSettle 600; $menuServer = Find-Ui -Name 'Second server' -Type 'Text'; Key 0x1B; SsSettle 400 }
    Check 'sessions bsD (BS-M2): the server signed in to last decides: signed in to Second server' ($r.Page -eq 'home' -and [bool]$menuServer) "page: $($r.Page); Account menu names Second server: $([bool]$menuServer)"
    # back to one server
    $r = SsRestart 'bsD-back' { $set = SsSettings; $set.Servers = @(@($set.Servers | Where-Object { $null -ne $_ })[0]); SsSaveSettings $set }
}

# ---- 4. bsB: Remember me off forgets an earlier remembered sign-in (BS-m1)
if ($ssParts -contains 'bsB') {
    if ((SsPage) -eq 'home') { [void](SsAccountMenu 'Sign out'); [void](SsWaitPage @('server') 15) }
    if (SsOpenServerRow) {
        SsSettle 600
        Check 'sessions bsB: signed in with Remember me off' (SsSignIn $ssCreds $false)
        $r = SsRestart 'bsB'
        Check 'sessions bsB (BS-m1): Remember me off, reopened: the server page, nothing sent' ($r.Page -eq 'server' -and $r.Auth.Count -eq 0) "page: $($r.Page); sign-ins at start: $($r.Auth.Count)"
        $users = @((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ -and $_.Username -eq $ssCreds.username })
        Check 'sessions bsB: no saved sign-in of that user is left' ($users.Count -eq 0) "saved: $($users.Count)"
    }
}

# ---- 5. bsC: a saved password the server refuses is sent once (BS-M3, BS-M4)
if ($ssParts -contains 'bsC') {
    Check 'sessions bsC: signed in with Remember me' (SsSignedInRemembered $ssCreds)
    $fails = 0; $sent = 0
    foreach ($i in 1..3) {
        $ssEdit = if ($i -eq 1) {
            {
                $set = SsSettings
                $all = @($set.Servers | Where-Object { $null -ne $_ })
                $all[0].Users = @(@($all[0].Users | Where-Object { $null -ne $_ }) | Where-Object { $_.Username -eq $ssCreds.username } | Select-Object -First 1)
                $all[0].Users[0].Password = SsProtect 'an-old-password' $set   # changed elsewhere: the saved one is stale
                $set.Servers = $all
                SsSaveSettings $set
            }
        } else { $null }
        $r = SsRestart "bsC$i" $ssEdit
        $sent += $r.Auth.Count; $fails += @($r.Auth | Where-Object { $_ -eq 401 }).Count
        if ($i -eq 1) {
            $line = "The saved sign-in to $serverName no longer works. Sign in again."
            $said = Wait-Until { $t = Find-Ui -Name 'no longer works' -Contains -Type 'Text'; if ($t -and $t.Current.Name -eq $line) { $t } } -Seconds 10 -PollMs 150
            $name = SsValue (SsUserBox)
            Check 'sessions bsC (BS-M3): the refused saved password opens the sign-in page, which says so' ($r.Page -eq 'signin' -and [bool]$said) "page: $($r.Page); said: $([bool]$said); texts: $(SsTexts)"
            $focus = Focused-Element
            Check 'sessions bsC: with the name filled in, the keyboard in the password box' ($name -eq $ssCreds.username -and $focus -and $focus.name -eq 'Password') "username '$name'; focused: $(if ($focus) { "$($focus.type) '$($focus.name)'" })"
            Capture-State 'bsC-refused-start'
        }
        else {
            Check "sessions bsC: start $i shows the server page" ($r.Page -eq 'server') "page: $($r.Page)"
        }
    }
    Check 'sessions bsC (BS-M4): one refused sign-in in three starts (Jellyfin disables an account after 3)' ($fails -le 1 -and $sent -le 1) "$fails refused of $sent sent"
}

# ---- 6. bs9: a refused port at start (BS-M3)
if ($ssParts -contains 'bs9') {
    Check 'sessions bs9: signed in with Remember me' (SsSignedInRemembered $ssCreds)
    $probe = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0); $probe.Start(); $closedPort = $probe.LocalEndpoint.Port; $probe.Stop()
    $closed = "http://127.0.0.1:$closedPort"
    $r = SsRestart 'bs9' { $set = SsSettings; $all = @($set.Servers | Where-Object { $null -ne $_ }); $all[0].LocalUrl = $closed; $all[0].PublicUrl = $closed; $set.Servers = $all; SsSaveSettings $set }
    $line = Wait-Until { $t = Find-Ui -Name "Couldn't sign in to $serverName. Can't reach $closed" -Contains -Type 'Text'; $t } -Seconds 5 -PollMs 150
    Check 'sessions bs9 (BS-M3): a refused port at start: the server page says why' ($r.Page -eq 'server' -and [bool]$line) "page: $($r.Page) after $($r.Seconds) s; texts: $(SsTexts)"
    Capture-State 'bs9-refused-port'
    $users = @((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ })
    Check 'sessions bs9: the saved sign-in is kept (not the server''s refusal)' ($users.Count -ge 1) "saved users: $($users.Count)"
    # the address back, while Nixie is closed (it keeps its own copy of the settings while it runs)
    $r = SsRestart 'bs9-back' { $set = SsSettings; $all = @($set.Servers | Where-Object { $null -ne $_ }); $all[0].LocalUrl = $script:serverUrl; $all[0].PublicUrl = $script:serverUrl; $set.Servers = $all; SsSaveSettings $set }
    Check 'sessions bs9: the address answering again, the kept sign-in signs in at the next start' ($r.Page -eq 'home') "page: $($r.Page)"
}

# ---- 7. bsF: no answer at start (BS-M3)
if ($ssParts -contains 'bsF') {
    if (-not ((SsServers | Select-Object -First 1).Users | Where-Object { $null -ne $_ })) { [void](SsSignedInRemembered $ssCreds) }
    Set-Rules @(, @('^/Users/AuthenticateByName$', 'hang'))
    $r = SsRestart 'bsF'
    $line = Wait-Until { Find-Ui -Name "didn't answer in time" -Contains -Type 'Text' } -Seconds 5 -PollMs 150
    Check 'sessions bsF (BS-M3): a server that doesn''t answer at start: the server page within 13 s' ($r.Page -eq 'server' -and $r.Seconds -le 13) "page: $($r.Page) after $($r.Seconds) s (it took 22)"
    Check 'sessions bsF: ... saying it didn''t answer in time' ([bool]$line) "texts: $(SsTexts)"
    Capture-State 'bsF-no-answer'
    Set-Rules @()
}

# ---- 8. bsE: Remove server on the server in use (BS-m2)
if ($ssParts -contains 'bsE') {
    Check 'sessions bsE: signed in with Remember me' (SsSignedInRemembered $ssCreds)
    Open-Nav 'Settings'
    [void](Wait-Ui -Name 'Other devices' -Type 'Text' -OffscreenToo -Seconds 15)
    SsSettle 2000
    $rm = $null
    for ($pp = 0; $pp -le 100 -and -not $rm; $pp += 5) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pp); SsSettle 300
        $exp = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.Name -eq $serverName } catch { $false } } | Where-Object { $_.Current.ClassName -match 'Expander$' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' }) | Select-Object -First 1
        if ($exp) { $ec = $null; if ($exp.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) { try { $ec.Expand() } catch { } }; SsSettle 800 }
        $rm = Find-Ui -Name "Remove server $serverName" -Type 'Button' -OffscreenToo
    }
    Check 'sessions bsE: Settings offers Remove server for the server in use' ([bool]$rm)
    if ($rm) {
        [void][OffscreenUi]::SetFocus($rm); SsSettle 500
        [void](Invoke-Element $rm)
        $yes = Wait-Ui -Name 'Remove' -Type 'Button' -Seconds 8
        $prompt = SsTexts
        Check 'sessions bsE (BS-m2): the prompt says it signs out first' ($prompt -match 'signs you out of it, then forgets this server') $prompt
        if ($yes) { [void](Invoke-Element $yes) }
        $pg = SsWaitPage @('server') 20
        SsSettle 1500
        Check 'sessions bsE (BS-m2): Remove server on the server in use signs out: the server page, no Account button' ($pg -eq 'server' -and -not (SsAccount)) "page: $pg"
        Check 'sessions bsE: the server is gone from the list and the file' (-not (Find-Ui -Name $serverName -Type 'ListItem') -and @(SsServers).Count -eq 0) "saved servers: $(@(SsServers).Count)"
        Shot 'bsE-removed'
        # sign in to add it again
        Submit-Address $script:serverUrl
        [void](SsWaitPage @('signin') 20)
        SsSettle 600
        Check 'sessions bsE: signed in again through its address, Remember me on' (SsSignIn $ssCreds $true)
        $r = SsRestart 'bsE'
        Check 'sessions bsE (BS-m2): reopened: signed in (the server was saved again)' ($r.Page -eq 'home' -and @(SsServers).Count -eq 1) "page: $($r.Page); saved servers: $(@(SsServers).Count)"
    }
}
