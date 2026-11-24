<#
  174-signin (a friend's report: "log in does not remember if you use quick connect", "normal log in saved"): a Quick
  Connect sign-in with "Remember me" is still signed in after a restart, and Sign out forgets it. -Then script of
  run-offscreen.ps1 after the driver's sign-in (-Script login), against the fake server or a QA server:

    run-offscreen.ps1 -QaServer 12 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script login -NoA11y `
        -Then tools\ui-harness\offscreen\then-174-qc-remember.ps1

  1. Account > Switch user, "Remember me" ticked, Use Quick Connect (the code approved by the server's test user through
     the harness endpoint); the saved file keeps a protected token and an empty password, protected (what Nixie 1.7.3
     and earlier read at a start).
  2. Closed and started again on the same data folder: Home, with no sign-in at the server (no code, no password), only
     GET /Users/Me answered 200.
  3. Settings, then Sign out: the saved sign-in goes, and the next start shows the server list without asking anything.
  4. The fake server only (its fault rules): signed in again with "Remember me", then a start where the server refuses
     the token (GET /Users/Me 401): the server's sign-in page (174-sessions; the server list before) says "The saved
     sign-in to ... no longer works. Sign in again." with the name filled in, and the saved sign-in is gone.
  No password anywhere.
#>
. (Join-Path $PSScriptRoot 'states-common.ps1')

function Get-Shell { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); if ($p -and ([OffscreenUi]::NavItem($p, 'Home') -or (Find-Ui -Id 'TopTabsMenu'))) { $p } }
function Get-Settings { Get-Content -LiteralPath (Join-Path $DataDir 'LocalSettings.json') -Raw -Encoding utf8 | ConvertFrom-Json }
function Get-Saved { @((Get-Settings).Servers | Where-Object { $null -ne $_ }) }
# a saved password as Nixie 1.7.3 reads it (DPAPI with the settings' Entropy): its text, or a note when it can't be read
function Read-OldStyle([string]$Base64) {
    Add-Type -AssemblyName System.Security
    try {
        $bytes = [Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($Base64), [Convert]::FromBase64String((Get-Settings).Entropy), 'CurrentUser')
        [Text.Encoding]::UTF8.GetString($bytes)
    }
    catch { "<unreadable: $($_.Exception.InnerException.Message)>" }
}
function Open-Account {
    $btn = Find-Ui -Name 'Account, ' -Type 'Button' -Contains
    if (-not $btn) { return $false }
    [void](Invoke-Element $btn)
    [bool](Wait-Ui -Name 'Switch user' -Type 'Button' -Seconds 8)
}
function Wait-Page([double]$Seconds = 40) {
    Wait-Until { if (Get-Shell) { 'home' } elseif (Find-Ui -Id 'TxtUrl' -Type 'Edit') { 'server' } elseif ([OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect')) { 'signin' } } -Seconds $Seconds -PollMs 150
}
# on a sign-in page: "Remember me" ticked, Use Quick Connect, the code approved; true once Home shows
function SignIn-Remembered([string]$What) {
    $quick = Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect') } -Seconds 20 -PollMs 150
    if (-not $quick) { throw "${What}: no sign-in page" }
    $remember = Wait-Ui -Name 'Remember me' -Type 'CheckBox' -Seconds 5
    if (-not $remember) { throw 'no Remember me' }
    $toggle = $remember.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    Start-Sleep -Milliseconds 300
    Check "quick connect remembered ($What): Remember me ticked" ($toggle.Current.ToggleState -eq 'On')
    $at = Get-Epoch
    [void](Invoke-Element $quick)
    $code = Wait-Until { $t = Find-Ui -Id 'QuickConnectCode' -Type 'Text'; if ($t -and ($t.Current.Name -replace '\s', '') -match '^(\d{6})$') { $Matches[1] } } -Seconds 20 -PollMs 100
    if (-not $code) { throw 'Quick Connect showed no code' }
    [void](Invoke-Server '/__harness/quickconnect/approve' 'POST' @{ code = $code })
    Say "quick connect remembered ($What): the code was approved by the server's test user"
    $auth = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'authenticate' -and $_.status -eq 200 }) | Select-Object -First 1 } -Seconds 30 -PollMs 100
    $home_ = Wait-Until { Get-Shell } -Seconds 30 -PollMs 150
    Check "quick connect remembered ($What): signed in with Quick Connect" ($auth -and $home_)
    Start-Sleep -Milliseconds 800
    [bool]$home_
}

# 1. Switch user: the sign-in page of this server
if (-not (Open-Account)) { throw 'no Account menu' }
[void](Invoke-Element (Find-Ui -Name 'Switch user' -Type 'Button'))
[void](SignIn-Remembered 'Switch user')
$users = @((Get-Saved | Select-Object -First 1).Users)
$kept = @($users | Where-Object { $_.Token -and $_.Password })
$old = if ($kept) { Read-OldStyle $kept[0].Password } else { '<none>' }
Check 'quick connect remembered: saved as a protected token, its password an empty one (1.7.3 reads it)' ($users.Count -eq 1 -and $kept.Count -eq 1 -and $old -eq '') "saved users: $($users.Count); with a token: $($kept.Count); the password as 1.7.3 reads it: '$old'"

# 2. closed and started again: Home, with no new sign-in at the server
Step-Close 'qc-restart'
$at = Get-Epoch
Start-App 'qc_restart_window_ms'
$page = Wait-Page
$since = @(Get-Requests $at)
$signIns = @($since | Where-Object { $_.route -eq 'authenticate' -or $_.path -match '^/QuickConnect/' })
$me = @($since | Where-Object { $_.path -match '^/Users/Me$' -and $_.status -eq 200 })
Check 'quick connect remembered: the restart opens Home, signed in' ($page -eq 'home') "page: $page"
Check 'quick connect remembered: the restart signed in nowhere (no code, no password); the saved token answered' ($signIns.Count -eq 0 -and $me.Count -ge 1) "sign-ins: $($signIns.Count); /Users/Me 200: $($me.Count)"
$timings.qc_restart_home_ms = Ms $at (Get-Epoch)
Shot 'qc-restarted'
$script:navPane = Get-Shell
$script:page = 'home'

# 3. Sign out forgets it: the next start shows the server list and asks the server nothing. Settings first: its server list
# shows each server's saved sign-ins, and a list of a page left behind must not stand in the way of the change
Open-Nav 'Settings'
[void](Wait-Ui -Name 'Servers and accounts' -Contains -Seconds 15)
Start-Sleep -Milliseconds 800
Open-Nav 'Home'
Start-Sleep -Milliseconds 1500
if (-not (Open-Account)) { throw 'no Account menu after the restart' }
[void](Invoke-Element (Find-Ui -Name 'Sign out' -Type 'Button'))
[void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 20)
Start-Sleep -Milliseconds 800
$users = @((Get-Saved | Select-Object -First 1).Users)
Check 'quick connect remembered: Sign out forgets the saved sign-in' ($users.Count -eq 0) "saved users: $($users.Count)"
Step-Close 'qc-signed-out'
$at = Get-Epoch
Start-App 'qc_signed_out_window_ms'
$page = Wait-Page
Start-Sleep -Milliseconds 800
$asked = @(Get-Requests $at | Where-Object { $_.path -match '^/Users/' })
Check 'quick connect remembered: after Sign out the start shows the server list, the token never sent' ($page -eq 'server' -and $asked.Count -eq 0) "page: $page; /Users/* asked: $($asked.Count)"
Shot 'qc-signed-out-start'

# 4. a saved token the server refuses at a start (the fake server's fault rules; a QA server has none)
$rules = $true
try { Set-Rules @() } catch { $rules = $false; Say "quick connect remembered: no fault rules here (a QA server): the refused-token step is the fake server's" }
if ($rules) {
    $serverName = (Get-Saved | Select-Object -First 1).DisplayName
    Submit-Address $script:serverUrl
    [void](SignIn-Remembered 'again')
    Step-Close 'qc-refused'
    Set-Rules @(, @('^/Users/Me$', 'status:401'))
    $at = Get-Epoch
    Start-App 'qc_refused_window_ms'
    $page = Wait-Page
    $line = "The saved sign-in to $serverName no longer works. Sign in again."
    $said = Wait-Until { $t = Find-Ui -Name 'no longer works' -Contains -Type 'Text'; if ($t -and $t.Current.Name -eq $line) { $t } } -Seconds 10 -PollMs 150
    Start-Sleep -Milliseconds 600
    $users = @((Get-Saved | Select-Object -First 1).Users)
    $userBox = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Cached.ClassName -ne 'PasswordBox' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1
    $value = $null; $typed = if ($userBox -and $userBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value)) { $value.Current.Value } else { '' }
    Check 'quick connect remembered: a refused saved sign-in opens the sign-in page, which says so (174-sessions)' ($page -eq 'signin' -and $said) "page: $page; said: $([bool]$said) ('$line')"
    Check 'quick connect remembered: ... with the name filled in' ($typed.Length -gt 0) "username '$typed'"
    Check 'quick connect remembered: the refused saved sign-in is forgotten' ($users.Count -eq 0) "saved users: $($users.Count)"
    Capture-State 'qc-refused-start'
    Set-Rules @()
}
