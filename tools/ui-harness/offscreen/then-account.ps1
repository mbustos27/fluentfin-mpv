# Seaglass 1.1.4 (account stream): the title bar while playing, the Account menu, Settings > Other devices and the Volume
# step. Run with -Script series,season -Then offscreen\then-account.ps1 (FluentFin's controls, the default), once per theme
# and glass mode for the shots: SEAGLASS_THEME=Light|Dark, -Setting Glass=Full|Solid, SG_SHOT_TAG naming them (e.g.
# dark-full); SG_ACCOUNT_SHOTS_ONLY=1 skips the checks that change state (Quick Connect, the keys, administration).
# Dot-sourced by drive.ps1 on the season page: no variable here may reuse one of its names (PowerShell names ignore case).
$acTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
$acShotsOnly = $env:SG_ACCOUNT_SHOTS_ONLY -eq '1'
function AcSettle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function AcSize([int]$W, [int]$H) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H); AcSettle 1500 }
function AcWidths([string]$Name, [scriptblock]$Before) {
    foreach ($size in @(@(1920, 1080), @(1100, 900))) {
        AcSize $size[0] $size[1]
        if ($Before) { & $Before }
        Shot "$acTag-$Name-$($size[0])"
    }
}
# the window's top edge (screen coordinates): the title bar is its first 56 px
function AcWindowTop { [OffscreenUi]::RectOf([OffscreenUi]::Root($script:hwnd, $script:ui.Id)).Top }
function AcAll([string]$Name, [string]$Type) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::$Type)))
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { -not $_.Current.IsOffscreen })
}
# the title bar's Back (a Back button in the title bar's row; the player's own Back sits lower, in its top bar)
function AcTitleBack {
    $top = AcWindowTop
    @(AcAll 'Back' 'Button' | Where-Object { ([OffscreenUi]::RectOf($_).Top - $top) -lt 56 }) | Select-Object -First 1
}
function AcAccount { Find-Ui -Name 'Account, ' -Type 'Button' -Contains }
function AcTitleBarState([string]$Where, [bool]$BackWanted, [bool]$AccountWanted) {
    $back = [bool](AcTitleBack); $account = [bool](AcAccount)
    Check "title bar ($Where): Back $(if ($BackWanted) { 'shown' } else { 'hidden' }), Account $(if ($AccountWanted) { 'shown' } else { 'hidden' })" (($back -eq $BackWanted) -and ($account -eq $AccountWanted)) "Back: $back, Account: $account"
}
function AcValue($Element) {
    try { ([System.Windows.Automation.RangeValuePattern]$Element.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value }
    catch { try { [double]([System.Windows.Automation.ValuePattern]$Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { $null } }
}
# scrolls the page until the text $Name is on screen (Settings' groups), from the top down
function AcReveal([string]$Name) {
    for ($p = 0; $p -le 100; $p += 5) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $p); AcSettle 250
        $t = Find-Ui -Name $Name -Type 'Text'
        # in the upper part of the window, so the group's cards show under it
        if ($t -and ([OffscreenUi]::RectOf($t).Top - (AcWindowTop)) -gt 60 -and ([OffscreenUi]::RectOf($t).Top - (AcWindowTop)) -lt 420) { AcSettle 500; return $true }
    }
    $false
}
function AcSetText($Element, [string]$Text) {
    ([System.Windows.Automation.ValuePattern]$Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Text)
}

# ---- the title bar on the season page, on the player page (in and out of full screen) and after it
AcSize 1920 1080
AcTitleBarState 'season page' $true $true
Step-Play
$withOwnBack = $PlayerControls -eq 'FluentFin'
AcTitleBarState "player page ($PlayerControls controls)" (-not $withOwnBack) $false
AcWidths 'player' $null
Dump-Tree "$acTag-player"
AcSize 1920 1080
Step-FullScreen $true
Check 'title bar (full screen): no Account' (-not (AcAccount))
Step-FullScreen $false
AcTitleBarState 'player page, out of full screen' (-not $withOwnBack) $false
Shot "$acTag-player-after-fullscreen-1920"
Step-Leave
AcTitleBarState 'after the player' $true $true

# ---- the Account menu
function AcOpenAccount {
    $btn = AcAccount
    if (-not $btn) { return $false }
    [void](Invoke-Element $btn)
    [bool](Wait-Ui -Name 'Switch user' -Type 'Button' -Seconds 8)
}
function AcCloseMenu { Key 0x1B; AcSettle 600 }
AcSize 1920 1080
$opened = AcOpenAccount
Check 'account: the Account button opens the menu' $opened
if ($opened) {
    AcSettle 800
    Dump-Tree "$acTag-account-menu"
    Invoke-A11yScan "$acTag-account-menu"
    $items = @('Switch user', 'Sign out', 'Settings' | Where-Object { Find-Ui -Name $_ -Type 'Button' })
    Check 'account: Switch user, Sign out and Settings' ($items.Count -eq 3) ($items -join ', ')
    $gone = @('Dashboard', 'Play On', 'Sync Play', 'Quick Connect', 'Logout' | Where-Object { Find-Ui -Name $_ -Type 'Button' })
    Check 'account: no Dashboard, Play On, Sync Play, Quick Connect or Logout' ($gone.Count -eq 0) ($gone -join ', ')
    $userText = Find-Ui -Id 'AccountUserName'
    $serverText = Find-Ui -Id 'AccountServerName'
    Check 'account: names the user and the server' ($userText -and $userText.Current.Name -eq $fixture.username -and $serverText -and $serverText.Current.Name -match 'Harness') "user: $(if ($userText) { $userText.Current.Name }), server: $(if ($serverText) { $serverText.Current.Name } else { 'none' })"
    $pane = Find-Ui -Name 'Account'
    Say "account: an element named 'Account' (the menu's pane, for Narrator): $(if ($pane) { $pane.Current.ControlType.ProgrammaticName } else { 'none' })"
    Shot "$acTag-account-menu-1920"
    AcCloseMenu
    AcSize 1100 900
    if (AcOpenAccount) { AcSettle 800; Shot "$acTag-account-menu-1100"; AcCloseMenu }
    AcSize 1920 1080
    # its Settings opens Settings
    if (AcOpenAccount) {
        [void](Invoke-Element (Find-Ui -Name 'Settings' -Type 'Button'))
        $settingsTitle = Wait-Ui -Name 'Other devices' -Type 'Text' -OffscreenToo -Seconds 10
        Check 'account: Settings opens Settings (its Other devices group)' ([bool]$settingsTitle)
    }
}

# ---- Settings > Other devices
if (-not (Find-Ui -Name 'Other devices' -Type 'Text' -OffscreenToo)) { Open-Nav 'Settings'; [void](Wait-Ui -Name 'Other devices' -Type 'Text' -OffscreenToo -Seconds 10) }
AcSettle 1500
$qcButton = Find-Ui -Id 'QuickConnectButton' -OffscreenToo
Check 'settings: Quick Connect is in Settings' ([bool]$qcButton)
if ($qcButton) { [void][OffscreenUi]::SetFocus($qcButton); AcSettle 900 }
Dump-Tree "$acTag-settings-other-devices"
Invoke-A11yScan "$acTag-settings-other-devices"
AcWidths 'settings-other-devices' { [void](AcReveal 'Other devices') }
AcSize 1920 1080
$sp = Find-Ui -Id 'SyncPlayButton' -OffscreenToo
Check 'settings: Sync Play is in Settings' ([bool]$sp)
if ($sp) { Check 'settings: Sync Play is disabled with the mpv player, and says why' ((-not $sp.Current.IsEnabled) -and [bool](Find-Ui -Name 'Not available with the mpv player' -Type 'Text' -Contains -OffscreenToo)) "enabled: $($sp.Current.IsEnabled)" }
Check 'settings: Play On left Settings (its dialog showed nothing)' (-not (Find-Ui -Id 'PlayOnButton' -OffscreenToo))

if (-not $acShotsOnly -and $qcButton) {
    # Quick Connect end to end: another device asks for a code (the fake server), Nixie approves it from Settings
    $other = Invoke-Server '/QuickConnect/Initiate' 'POST'
    [void](Invoke-Element (Find-Ui -Id 'QuickConnectButton' -OffscreenToo))
    $codeBox = Wait-Ui -Id 'CodeBox' -Seconds 8
    Check 'quick connect: the dialog opens from Settings' ([bool]$codeBox)
    if ($codeBox) {
        AcSetText $codeBox '000000'; AcSettle 300
        [void](Invoke-Element (Find-Ui -Name 'Approve' -Type 'Button'))
        $bad = Wait-Ui -Name "didn't work" -Type 'Text' -Contains -Seconds 8
        Check 'quick connect: a wrong code is said' ([bool]$bad)
        AcSetText $codeBox $other.Code; AcSettle 300
        [void](Invoke-Element (Find-Ui -Name 'Approve' -Type 'Button'))
        $ok = Wait-Ui -Name 'Approved' -Type 'Text' -Contains -Seconds 8
        AcSettle 400
        $approve = Find-Ui -Name 'Approve' -Type 'Button'
        Check 'quick connect: Approve is off once approved, and Cancel became Close' ($approve -and -not $approve.Current.IsEnabled -and (Find-Ui -Name 'Close' -Type 'Button')) "Approve enabled: $(if ($approve) { $approve.Current.IsEnabled })"
        AcSettle 600
        Shot "$acTag-quick-connect-approved"
        $state = Wait-Until { $w = @(Invoke-Server '/__harness/quickconnect') | Where-Object { $_.code -eq $other.Code -and $_.approved }; if ($w) { $w } } -Seconds 5 -PollMs 200
        Check 'quick connect: the code is approved at the server, and the dialog says so' ([bool]$ok -and [bool]$state)
        # the dialog's Close, not the window's caption button of the same name (in the title bar's row)
        $top = AcWindowTop
        $close = @(AcAll 'Close' 'Button' | Where-Object { ([OffscreenUi]::RectOf($_).Top - $top) -ge 56 }) | Select-Object -First 1
        if ($close) { [void](Invoke-Element $close) } else { Say 'quick connect: no Close button in the dialog' }
        AcSettle 800
    }
}

# ---- Settings > Playback > Volume step: its targets and keys
$vs = Find-Ui -Name 'Volume step, in percent' -OffscreenToo
Check 'volume step: found' ([bool]$vs)
if ($vs) {
    [void](AcReveal 'Volume step')
    [void][OffscreenUi]::SetFocus($vs); AcSettle 900
    $vs = Find-Ui -Name 'Volume step, in percent'
    if (-not $vs) { $vs = Find-Ui -Name 'Volume step, in percent' -OffscreenToo }
    $spins = @([OffscreenUi]::Under($vs, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button })
    $sizes = @($spins | ForEach-Object { $r = [OffscreenUi]::RectOf($_); '{0}x{1}' -f [int]$r.Width, [int]$r.Height })
    $small = @($spins | Where-Object { $r = [OffscreenUi]::RectOf($_); $r.Width -lt 24 -or $r.Height -lt 24 })
    Check 'volume step: spin buttons at least 24 x 24' ($spins.Count -ge 2 -and $small.Count -eq 0) ("{0} buttons: {1}" -f $spins.Count, ($sizes -join ', '))
    Shot "$acTag-volume-step-1920"
    $edit = @([OffscreenUi]::Under($vs, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit }) | Select-Object -First 1
    $target = if ($edit) { $edit } else { $vs }
    if (-not $acShotsOnly) {
        [void][OffscreenUi]::SetFocus($target); AcSettle 400
        $v0 = AcValue $vs
        Key 0x26; AcSettle 400; $up = AcValue $vs           # Up
        Key 0x28; AcSettle 400; $down = AcValue $vs         # Down
        Key 0x21; AcSettle 400; $pgUp = AcValue $vs         # PageUp
        Key 0x22; AcSettle 400; $pgDown = AcValue $vs       # PageDown
        Say "volume step keys: start $v0, Up $up, Down $down, PageUp $pgUp, PageDown $pgDown"
        Check 'volume step: Up and Down step by 1' ($up -eq [Math]::Min(20, $v0 + 1) -and $down -eq $v0) "$v0 -> $up -> $down"
        Check 'volume step: PageUp and PageDown step by 5' ($pgUp -eq [Math]::Min(20, $v0 + 5) -and $pgDown -eq [Math]::Max(1, $pgUp - 5)) "$v0 -> $pgUp -> $pgDown"
        # typing: End, Backspace twice, 7, Enter
        Key 0x23; Key 0x08; Key 0x08; Key 0x37; Key 0x0D; AcSettle 600
        $typed = AcValue $vs
        Check 'volume step: typing a value and Enter sets it' ($typed -eq 7) "typed 7: $typed"
        # put it back
        if ($null -ne $v0 -and $typed -ne $v0) { Key 0x23; Key 0x08; Key 0x08; foreach ($ch in ([string][int]$v0).ToCharArray()) { Key ([int][char]$ch) }; Key 0x0D; AcSettle 400 }
    }
}

# ---- Settings > Servers and accounts > Server administration (the old Dashboard), for the fake server's administrator
$admin = Find-Ui -Id 'ServerAdministrationButton' -OffscreenToo
Check 'settings: Server administration is there for an administrator' ([bool]$admin)
if ($admin -and -not $acShotsOnly) {
    $acBefore = Get-Epoch
    [void](Invoke-Element $admin)
    # the admin stream's sections are in sentence case since 1.2.0 ("Scheduled tasks"; the button review's A83-A96)
    $page = Wait-Until { $x = Find-Ui -Name 'Scheduled tasks'; if (-not $x) { $x = Find-Ui -Name 'Scheduled Tasks' }; $x } -Seconds 10
    AcSettle 2500
    Check 'server administration: its pages open' ([bool]$page)
    Shot "$acTag-server-administration"
    $errs = @(Get-Requests $acBefore | Where-Object { $_.status -ge 500 -or $_.route -eq 'unhandled' } | ForEach-Object { "$($_.method) $($_.path) $($_.status)" })
    Say ("server administration: {0} requests failed or unhandled{1}" -f $errs.Count, $(if ($errs) { ': ' + (($errs | Select-Object -First 6) -join '; ') }))
    Key 0xA6; AcSettle 1500
}

# ---- Sign out (last): the server page shows no search box (layout audit, item 12: it stayed after a sign-out)
AcSize 1920 1080
if (AcOpenAccount) {
    [void](Invoke-Element (Find-Ui -Name 'Sign out' -Type 'Button'))
    $serverPage = Wait-Ui -Id 'TxtUrl' -Seconds 20
    AcSettle 1500
    Check 'sign out: the server page opens' ([bool]$serverPage)
    Check 'sign out: no search box once signed out' (-not (Find-Ui -Name 'Search' -Type 'Edit' -Contains))
    Check 'sign out: no Account button once signed out' (-not (AcAccount))
    Shot "$acTag-signed-out-1920"
}
