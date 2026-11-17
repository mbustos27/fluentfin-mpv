<#
  -Before script of the states pass (run-offscreen.ps1 -Before offscreen\before-states.ps1): the server page's and the
  sign-in page's states against the fake server, failing on purpose through its rules. Each state: a screenshot, the UIA
  tree and scan, and the Narrator probe (states-common.ps1). No password is typed anywhere: the wrong-password state is
  the fixture's user name with an empty password, which the fake server refuses.
  Leaves FluentFin on the server page, with Home's rows failing (then-states.ps1 starts there; SG_STATES_HOME_FAIL=0:
  no rule), for the driver's sign-in.
#>
. (Join-Path $PSScriptRoot 'states-common.ps1')

# an open ContentDialog's Close (Cancel) or Primary (OK) button, found by the dialog template's own automation ids, so
# the window's Close caption button is never pressed
function Dismiss-Dialog {
    foreach ($id in @('CloseButton', 'PrimaryButton')) {
        $b = Find-Ui -Id $id -Type 'Button'
        if ($b) { [void](Invoke-Element $b); Say "dialog closed ($id '$($b.Current.Name)')"; Start-Sleep -Milliseconds 700; return }
    }
    Say 'no dialog open'
}
function Press-SignIn { $b = $null; foreach ($n in @('Sign in', 'Login')) { $b = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, $n); if ($b) { break } }; if (-not $b) { throw 'no Sign in button' }; [void](Invoke-Element $b); Say 'pressed Sign in' }
function Wait-Text([string]$Part, [double]$Seconds = 15) { Wait-Until { Find-Ui -Name $Part -Contains -Type 'Text' } -Seconds $Seconds -PollMs 150 }

# 1. the first start: no saved server, the page looks for one on this network by itself and finds none
[void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 30)
Start-Sleep -Milliseconds 400
Capture-State 'server-detecting'
$none = Wait-Until { -not (Find-Ui -Name 'Looking for servers' -Contains -Type 'Text') } -Seconds 20 -PollMs 200
Start-Sleep -Milliseconds 500
Capture-State 'server-none-found'

# 2. not an address
Submit-Address 'jellyfin.local'
Start-Sleep -Milliseconds 600
Capture-State 'server-not-an-address'

# 3. nothing listens there (a closed port on this machine)
Submit-Address 'http://127.0.0.1:9'
[void](Wait-Until { -not (Find-Ui -Name 'Connecting to' -Contains -Type 'Text') } -Seconds 15 -PollMs 150)
Start-Sleep -Milliseconds 600
Capture-State 'server-down'

# 4. the server answers with an error (500: a 503 is retried by the client until the page's 10 s time-out)
Set-Rules @(, @('^/System/Info/Public$', 'status:500'))
Submit-Address $script:serverUrl
[void](Wait-Until { -not (Find-Ui -Name 'Connecting to' -Contains -Type 'Text') } -Seconds 15 -PollMs 150)
Start-Sleep -Milliseconds 600
Capture-State 'server-error'

# 5. the server does not answer: connecting, then the time-out
Set-Rules @(, @('^/System/Info/Public$', 'hang'))
Submit-Address $script:serverUrl
Start-Sleep -Milliseconds 1200
Capture-State 'server-connecting'
[void](Wait-Until { -not (Find-Ui -Name 'Connecting to' -Contains -Type 'Text') } -Seconds 20 -PollMs 250)
Start-Sleep -Milliseconds 600
Capture-State 'server-timeout'
Set-Rules @()

# 6. the sign-in page
Submit-Address $script:serverUrl
$quick = Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Quick Connect') } -Seconds 20 -PollMs 150
if (-not $quick) { throw 'no sign-in page' }
Start-Sleep -Milliseconds 800
Capture-State 'login'

# 7. a wrong password (the fixture's user, no password typed: the server refuses it)
$userBox = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Cached.ClassName -ne 'PasswordBox' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1
if (-not $userBox) { throw 'no user name box' }
Set-ElementValue $userBox $fixture.username
Start-Sleep -Milliseconds 300
Press-SignIn
Start-Sleep -Milliseconds 1500
Capture-State 'login-wrong-password'
Dismiss-Dialog

# 8. the server fails the sign-in
Set-Rules @(, @('^/Users/AuthenticateByName$', 'status:500'))
Press-SignIn
Start-Sleep -Milliseconds 1500
Capture-State 'login-server-error'
Dismiss-Dialog

# 9. a slow server: signing in (the answer, 4 s later, is the refusal of step 7)
Set-Rules @(, @('^/Users/AuthenticateByName$', '4000'))
Press-SignIn
Start-Sleep -Milliseconds 1200
Capture-State 'login-signing-in'
Start-Sleep -Seconds 4
Dismiss-Dialog
Set-Rules @()

# 10. Quick Connect switched off on the server
Set-Rules @(, @('^/QuickConnect/Initiate$', 'status:401'))
Press 'Quick Connect'
Start-Sleep -Milliseconds 1500
Capture-State 'login-quickconnect-off'
Dismiss-Dialog
Set-Rules @()

# 11. Quick Connect's code
Press 'Quick Connect'
[void](Wait-Text 'Enter code' 15)
Start-Sleep -Milliseconds 600
Capture-State 'login-quickconnect-code'
Dismiss-Dialog

# back to the server page for the driver's sign-in
Press 'Switch server'
[void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 20)
Start-Sleep -Milliseconds 600
Capture-State 'server-saved'

# Home's rows fail from now on (then-states.ps1 starts with the failed Home; the sign-in itself is not affected)
if ($env:SG_STATES_HOME_FAIL -ne '0') {
    Set-Rules @(, @('^/(UserItems/Resume|Users/[^/]+/Items/Resume|Shows/NextUp|Items/Latest|Users/[^/]+/Items/Latest)$', 'status:500'))
}
