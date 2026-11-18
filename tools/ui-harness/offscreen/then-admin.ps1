# Nixie 1.2.0 (admin stream): the server administration and the item dialogs against the fake server, off-screen: the tabs
# along the top (no side pane), and the dangerous buttons. Each destructive one is tried with Escape, Enter and Cancel, which
# must send nothing, then confirmed, which must send exactly one request (the fake server's <out>\server\admin.jsonl).
# Run with -Script login,movie -Then offscreen\then-admin.ps1; SEAGLASS_THEME=Light|Dark in the environment and
# ADM_SHOT_TAG naming the shots; FAKESERVER_PLAYBACK_REPORTING=1 for the Playback reports page.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names (PowerShell names ignore case: $out is its -Out).
$admTag = if ($env:ADM_SHOT_TAG) { $env:ADM_SHOT_TAG } else { 'admin' }
$admLog = Join-Path $Out 'server\admin.jsonl'
function Adm-Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
function Adm-Requests {
    if (-not (Test-Path $admLog)) { return @() }
    @(Get-Content -LiteralPath $admLog -Encoding utf8 | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
function Adm-Count([string]$Method, [string]$PathRegex) { @(Adm-Requests | Where-Object { $_.method -eq $Method -and $_.path -match $PathRegex }).Count }
function Adm-Shot([string]$Name) { Shot "$admTag-$Name" }
function Adm-Invoke([string]$Name, [string]$Type = 'Button', [double]$Seconds = 15) {
    $el = Wait-Ui -Name $Name -Type $Type -Seconds $Seconds
    if (-not $el) { throw "no $Type named '$Name'" }
    Say "invoke '$Name' ($(Invoke-Element $el))"
}
function Adm-Tab([string]$Name) {
    $el = Wait-Until { [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, $Name, 'Microsoft.UI.Xaml.Controls.SelectorBarItem') } -Seconds 15 -PollMs 150
    if (-not $el) { throw "no tab '$Name'" }
    Say "tab '$Name' ($(Invoke-Element $el))"
    Adm-Settle 1500
}
# a destructive button: Escape, Enter and Cancel send nothing; the confirm sends exactly one request
function Adm-Destructive([string]$Label, [scriptblock]$Open, [string]$Confirm, [string]$Method, [string]$PathRegex, [string]$ShotName) {
    $before = Adm-Count $Method $PathRegex
    foreach ($way in @('escape', 'enter', 'cancel')) {
        & $Open
        $dialogButton = Wait-Ui -Name $Confirm -Type 'Button' -Seconds 10
        Check "$Label asks first ($way)" ([bool]$dialogButton) $Confirm
        if (-not $dialogButton) { return }
        if ($way -eq 'escape') { Adm-Shot "$ShotName-confirm" }
        switch ($way) {
            'escape' { Key 0x1B }
            'enter' { Key 0x0D }
            'cancel' { Adm-Invoke 'Cancel' }
        }
        Adm-Settle 1200
        $gone = -not (Find-Ui -Name $Confirm -Type 'Button')
        Check "$Label $way closes it and sends nothing" ($gone -and (Adm-Count $Method $PathRegex) -eq $before) "requests $(Adm-Count $Method $PathRegex) (before $before)"
    }
    & $Open
    Adm-Invoke $Confirm
    Adm-Settle 2000
    $after = Adm-Count $Method $PathRegex
    Check "$Label confirmed sends exactly one $Method" ($after -eq $before + 1) "requests $after (before $before)"
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1600, 1000); Adm-Settle 1000

# --- Edit metadata (the worst offender): Revert puts the loaded values back, Save sends only what changed -------------------
if ($script:page -eq 'movie') {
    Adm-Invoke 'More options'
    Adm-Invoke 'Edit Metadata' 'MenuItem'
    $title = Wait-Ui -Name 'Title' -Type 'Edit' -Seconds 15
    Adm-Settle 1500
    Adm-Shot 'edit-metadata'
    if ($title) {
        $loaded = $title.Current.Name
        $p = $null; [void]$title.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)
        $loadedTitle = $p.Current.Value
        Set-ElementValue $title 'Changed by the harness'; Adm-Settle 600
        Adm-Shot 'edit-metadata-changed'
        Adm-Invoke 'Revert'; Adm-Settle 800
        Check 'Edit metadata: Revert puts back the loaded title' ($p.Current.Value -eq $loadedTitle) "'$($p.Current.Value)' (loaded '$loadedTitle')"
        $overview = Find-Ui -Name 'Overview' -Type 'Edit'
        if ($overview) { Set-ElementValue $overview 'A shorter night (harness).'; Adm-Settle 600 }
        $beforeSave = Adm-Count 'POST' '^/Items/[0-9a-fA-F-]+$'
        Adm-Invoke 'Save'; Adm-Settle 2000
        $saves = @(Adm-Requests | Where-Object { $_.method -eq 'POST' -and $_.path -match '^/Items/[0-9a-fA-F-]+$' })
        Check 'Edit metadata: Save after Revert sends one request' ($saves.Count -eq $beforeSave + 1) "$($saves.Count)"
        if ($saves.Count) {
            $sent = $saves[-1].body
            Check 'Edit metadata: the title went back as loaded, the overview as edited' ($sent.Name -eq $loadedTitle -and $sent.Overview -eq 'A shorter night (harness).') "Name '$($sent.Name)', Overview '$($sent.Overview)'"
        }
    }
    if (Find-Ui -Name 'Cancel' -Type 'Button') { Adm-Invoke 'Cancel'; Adm-Settle 800 }

    # Edit images: Delete asks in a flyout at the button (ConfirmFlyout: Cancel has the focus); two deletes in a row each
    # remove the image picked, on a server that renumbers (the fake server does, as Jellyfin)
    Adm-Invoke 'More options'
    Adm-Invoke 'Edit Images' 'MenuItem'
    Adm-Settle 2000
    Adm-Shot 'edit-images'
    $imgDelete = '^/Items/[0-9a-fA-F-]+/Images/Backdrop/\d+$'
    Adm-Destructive 'Delete image (flyout)' { Adm-Invoke 'Delete backdrop image' } 'Delete image' 'DELETE' $imgDelete 'delete-image'
    Adm-Invoke 'Delete backdrop image'; Adm-Invoke 'Delete image'; Adm-Settle 2000
    $imgDeletes = @(Adm-Requests | Where-Object { $_.method -eq 'DELETE' -and $_.path -match $imgDelete })
    Check 'Edit images: two deletes in a row each remove the first backdrop shown (index 0 after the renumbering)' ($imgDeletes.Count -eq 2 -and @($imgDeletes | Where-Object { $_.path -notmatch '/Backdrop/0$' }).Count -eq 0) (($imgDeletes | ForEach-Object { $_.path }) -join ', ')
    Adm-Shot 'edit-images-after-two-deletes'
    # the dialog's own Close (its template part), never the window's caption button of the same name
    $dialogClose = Wait-Ui -Id 'CloseButton' -Type 'Button' -Seconds 5
    if (-not $dialogClose) { throw 'no Close button of the dialog (automation id CloseButton)' }
    Say "dialog close ($(Invoke-Element $dialogClose))"; Adm-Settle 800
}

# --- the server administration: tabs along the top ------------------------------------------------------------------------
$account = Wait-Until { Find-Ui -Name 'Account' -Type 'Button' -Contains } -Seconds 15
if (-not $account) { throw 'no account button' }
Say "account ($(Invoke-Element $account))"
Adm-Invoke 'Dashboard'
Adm-Settle 3000
Check 'admin: the sections are tabs along the top' ([bool](Wait-Ui -Name 'Server administration sections' -Seconds 10))
Capture-Page "$admTag-admin-dashboard"
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900); Adm-Settle 1500
Adm-Shot 'admin-dashboard-1100'
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Adm-Settle 1500
Adm-Shot 'admin-dashboard-1920'
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1600, 1000); Adm-Settle 1000

# Restart server: asks by name, Cancel the default; confirmed, one POST /System/Restart, then "Restarting…" until it is back
Adm-Destructive 'Restart server' { Adm-Invoke 'Restart server…' } 'Restart server' 'POST' '^/System/Restart$' 'restart'
Adm-Settle 500
Adm-Shot 'admin-restarting'
$back = Wait-Ui -Name 'Harness Jellyfin (fake) restarted' -Contains -Seconds 30
Check 'Restart: the dashboard says the server is back' ([bool]$back)
Adm-Shot 'admin-restarted'

# Scan all libraries: one POST /Library/Refresh, "Scan started"
$scans = Adm-Count 'POST' '^/Library/Refresh$'
Adm-Invoke 'Scan all libraries'; Adm-Settle 1500
Check 'Scan all libraries sends one refresh' ((Adm-Count 'POST' '^/Library/Refresh$') -eq $scans + 1)
Check 'Scan all libraries says it started' ([bool](Find-Ui -Name 'Scan started' -Contains))

# Shut down server: asks by name, Cancel the default; confirmed, one POST /System/Shutdown, then the buttons wait
Adm-Destructive 'Shut down server' { Adm-Invoke 'Shut down server…' } 'Shut down server' 'POST' '^/System/Shutdown$' 'shutdown'
$restartButton = Find-Ui -Name 'Restart server…' -Type 'Button'
Check 'Shut down: the dashboard says so and Restart waits' ([bool](Find-Ui -Name 'Harness Jellyfin (fake) is shutting down' -Contains) -and $restartButton -and -not $restartButton.Current.IsEnabled)
Adm-Shot 'admin-shut-down'

# Users: Delete user asks; Remove password asks
Adm-Tab 'Users'
Capture-Page "$admTag-admin-users"
$bobId = '[0-9a-fA-F-]+'
Adm-Destructive 'Delete user' { Adm-Invoke 'More options for bob'; Adm-Invoke 'Delete user…' 'MenuItem' } 'Delete user' 'DELETE' "^/Users/$bobId$" 'delete-user'
Check 'Delete user: the card goes' (-not (Find-Ui -Name 'More options for bob' -Type 'Button'))
Adm-Shot 'admin-users-after-delete'
Adm-Invoke 'More options for carol'; Adm-Invoke 'Edit user' 'MenuItem'; Adm-Settle 2000
Check 'a user page links back to Users' ([bool](Find-Ui -Name 'Back to Users' -Contains))
Adm-Shot 'admin-user-editor'
$pwTab = Wait-Until { [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, 'Password', 'Microsoft.UI.Xaml.Controls.SelectorBarItem') } -Seconds 10
if ($pwTab) { [void](Invoke-Element $pwTab); Adm-Settle 1500 }
Adm-Destructive 'Remove password' { Adm-Invoke 'Remove password…' } 'Remove password' 'POST' '^/Users/Password$' 'remove-password'
$resets = @(Adm-Requests | Where-Object { $_.path -eq '/Users/Password' })
Check 'Remove password: the request resets the password' ($resets.Count -ge 1 -and $resets[-1].body.ResetPassword -eq $true)
Adm-Shot 'admin-password'

# Libraries: the second row of tabs; Add library opens its dialog
Adm-Tab 'Libraries'
Capture-Page "$admTag-admin-libraries"
Adm-Invoke 'Add library'; Adm-Settle 1500
Adm-Shot 'admin-add-library'
Adm-Invoke 'Cancel'; Adm-Settle 800
Adm-Destructive 'Delete library' { Adm-Invoke 'More options for Anime'; Adm-Invoke 'Delete library…' 'MenuItem' } 'Delete library' 'DELETE' '^/Library/VirtualFolders$' 'delete-library'
Check 'Delete library: the card goes' (-not (Find-Ui -Name 'More options for Anime' -Type 'Button'))
Adm-Tab 'Display'
Adm-Shot 'admin-libraries-display'

foreach ($section in @('General', 'Playback', 'Activity', 'Scheduled tasks')) {
    Adm-Tab $section
    Adm-Shot ("admin-" + ($section -replace ' ', '-').ToLowerInvariant())
}

# Playback reports (the plugin, when the fake server lists it)
if ($env:FAKESERVER_PLAYBACK_REPORTING) {
    try {
        Open-Nav 'Reporting'
        Adm-Settle 3000
        Check 'Playback reports: tabs along the top' ([bool](Wait-Until { [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, 'Usage', 'Microsoft.UI.Xaml.Controls.SelectorBarItem') } -Seconds 10))
        Capture-Page "$admTag-reports"
        Adm-Tab 'Usage'
        Adm-Shot 'reports-usage'
    } catch { Check 'Playback reports opened' $false "$_" }
}
