<#
  -Then script of the states pass, after before-states.ps1 (run-offscreen.ps1 -Before offscreen\before-states.ps1
  -Then offscreen\then-states.ps1 -Script wait:1): the signed-in app's loading, empty and error states against the
  fake server, failing on purpose through its rules. before-states.ps1 leaves Home's rows failing, so the first Home
  after the sign-in is the failed one (a later visit would show the saved home page instead; a Retry always asks the
  server, so the states after it come from Retry and Refresh). Each state: a screenshot, the UIA tree and scan, and the
  Narrator probe (states-common.ps1). SG_STATES_SKIP (comma-separated: home, library, item, search, settings, dashboard, picker)
  leaves parts out.
#>
. (Join-Path $PSScriptRoot 'states-common.ps1')
$skip = @(([string]$env:SG_STATES_SKIP) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$homeRows = '^/(UserItems/Resume|Users/[^/]+/Items/Resume|Shows/NextUp|Items/Latest|Users/[^/]+/Items/Latest)$'
$resume = '^/(UserItems/Resume|Users/[^/]+/Items/Resume)$'
$items = '^/(Users/[^/]+/)?Items$'
function Wait-Text([string]$Part, [double]$Seconds = 20) { Wait-Until { Find-Ui -Name $Part -Contains -Type 'Text' } -Seconds $Seconds -PollMs 200 }
function Press-First([string[]]$Names, [double]$Seconds = 10) {
    $b = Wait-Until { foreach ($n in $Names) { $x = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, $n); if ($x) { return $x } } } -Seconds $Seconds -PollMs 150
    if (-not $b) { throw "none of the buttons: $($Names -join ', ')" }
    $how = Invoke-Element $b
    Say "pressed '$($b.Current.Name)' ($how)"
}
function Search-For([string]$Term) {
    $box = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Cached.Name -like '*Search*' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1 } -Seconds 10
    if (-not $box) { throw 'no search box' }
    [void][OffscreenUi]::SetFocus($box)
    Set-ElementValue $box $Term
    Say "searched for '$Term'"
}

if ($skip -notcontains 'home') {
    # 1. Home: nothing could be loaded
    [void](Wait-Text "Couldn't load" 30)
    Start-Sleep -Milliseconds 800
    Capture-State 'home-failed'
    # 2. the server has nothing (Try again)
    Set-Rules @(, @($homeRows, 'empty'))
    Press-First @('Try again')
    [void](Wait-Text 'Nothing to watch' 20)
    Start-Sleep -Milliseconds 800
    Capture-State 'home-empty'
    # 3. a part could not be loaded (Continue Watching fails, the rest is there)
    Set-Rules @(, @($resume, 'status:500'))
    Press-First @('Refresh', 'Try again')
    [void](Wait-Text 'Some of Home' 20)
    Start-Sleep -Seconds 2
    Capture-State 'home-partly'
    # 1.5 huntfix (BH-150-M1): what the user sees, not only the words: the notice below the top bar's tabs (Classic drew it
    # under them, its text through the tab labels), its Try again clear of the search box (a click on it landed in Search),
    # and Try again, invoked, loads Home
    $hpRect = { param($e) if ($e) { $r = $e.Current.BoundingRectangle; '{0},{1} {2}x{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height } else { 'none' } }
    $hpNotice = Find-Ui -Name 'Some of Home' -Contains -Type 'Text'
    $hpBar = Find-Ui -Id 'TopTabs'
    if (-not $hpBar) { $hpBar = Find-Ui -Id 'TopTabsMenu' }
    $hpBox = Find-Ui -Name 'Search' -Type 'Edit' -Contains
    $hpRetry = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Try again')
    Check 'states (BH-150-M1): Home''s notice is below the top bar''s tabs' ($hpNotice -and $hpBar -and $hpNotice.Current.BoundingRectangle.Top -ge $hpBar.Current.BoundingRectangle.Bottom) "notice $(& $hpRect $hpNotice); tabs $(& $hpRect $hpBar)"
    Check 'states (BH-150-M1): its Try again is clear of the search box' ($hpRetry -and (-not $hpBox -or -not $hpRetry.Current.BoundingRectangle.IntersectsWith($hpBox.Current.BoundingRectangle))) "Try again $(& $hpRect $hpRetry); search $(& $hpRect $hpBox)"
    if ($hpRetry) {
        Set-Rules @()
        Say "pressed 'Try again' ($(Invoke-Element $hpRetry))"
        $hpLoaded = Wait-Until { if (-not (Find-Ui -Name 'Some of Home' -Contains -Type 'Text') -and (Find-Ui -Name 'Continue watching' -Type 'Text')) { $true } } -Seconds 20 -PollMs 200
        Check 'states (BH-150-M1): Try again, invoked, loads Home (the notice goes, Continue Watching is back)' ([bool]$hpLoaded)
        # back to the partly loaded Home for the next state (it starts from its Try again)
        Set-Rules @(, @($resume, 'status:500'))
        Key 0x52
        [void](Wait-Text 'Some of Home' 20)
        Start-Sleep -Seconds 1
    }
    # 4. nothing to continue: the Continue Watching row is empty
    Set-Rules @(, @($resume, 'empty'))
    Press-First @('Try again', 'Refresh')
    Start-Sleep -Seconds 3
    Capture-State 'home-no-continue-watching'
    Set-Rules @()
}

if ($skip -notcontains 'library') {
    # 5. an empty library, then one that fails, then loaded
    Set-Rules @(, @($items, 'empty'))
    Open-Library $fixture.movie.library
    [void](Wait-Text 'library is empty' 20)
    Start-Sleep -Milliseconds 800
    Capture-State 'library-empty'
    Set-Rules @(, @($items, 'status:500'))
    Press-First @('Refresh', 'Try again')
    [void](Wait-Text "Couldn't load" 20)
    Start-Sleep -Milliseconds 800
    Capture-State 'library-failed'
    Set-Rules @()
    Press-First @('Try again', 'Refresh')
    [void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 20 -PollMs 200)
    Start-Sleep -Seconds 2
    Capture-State 'library'
}

if ($skip -notcontains 'item') {
    # 6. an item page that cannot be loaded, then Try again
    if (-not [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name)) { Open-Library $fixture.movie.library }
    Set-Rules @(, @('^/(Users/[^/]+/)?Items/[0-9a-fA-F-]{32,36}$', 'status:500'))
    Open-Card $fixture.movie.name
    [void](Wait-Text "Couldn't load" 20)
    Start-Sleep -Milliseconds 800
    Capture-State 'movie-failed'
    Set-Rules @()
    Press-First @('Try again')
    [void](Wait-ItemPage $fixture.movie.name)
    Start-Sleep -Seconds 2
    Capture-State 'movie-after-retry'
}

if ($skip -notcontains 'search') {
    # 7. a search that finds nothing, and one that fails
    Set-Rules @(, @($items, 'empty'))
    Search-For 'zqxj'
    Start-Sleep -Seconds 2
    Capture-State 'search-no-results'
    Shot-Popups 'search-no-results'
    Set-Rules @(, @($items, 'status:500'))
    Search-For 'zqxjk'
    Start-Sleep -Seconds 2
    Capture-State 'search-failed'
    Shot-Popups 'search-failed'
    Set-Rules @()
    Search-For ''
    Key 0x1B
    Start-Sleep -Milliseconds 500
}

if ($skip -notcontains 'settings') {
    # the app's Settings (its playback preferences come from the server: a LoadStateView inside a glass group)
    Open-Nav 'Settings'
    Start-Sleep -Seconds 3
    Capture-State 'settings'
}

if ($skip -notcontains 'dashboard') {
    # 8. the dashboard's server configuration pages (General, Trickplay: ServerConfigurationPage)
    # 1.2.0 merge: the account stream moved the account menu's Dashboard into Settings ("Server administration", Open)
    Open-Nav 'Settings'
    Start-Sleep -Milliseconds 800
    $dash = Wait-Ui -Name 'Open server administration' -Type 'Button' -OffscreenToo -Seconds 10
    if (-not $dash) { throw 'no Server administration in Settings' }
    [void](Invoke-Element $dash)
    Start-Sleep -Seconds 2
    Capture-State 'dashboard'
    $general = Wait-Ui -Name 'General' -Seconds 10
    if ($general) { [void](Invoke-Element $general); Start-Sleep -Seconds 2; Capture-State 'server-config-general' }
    $playback = Wait-Ui -Name 'Playback' -Seconds 5
    if ($playback) {
        [void](Invoke-Element $playback); Start-Sleep -Milliseconds 800
        $t = Wait-Ui -Name 'Trickplay' -Seconds 5
        if ($t) { [void](Invoke-Element $t); Start-Sleep -Seconds 2; Capture-State 'server-config-trickplay' }
    }
}

if ($skip -notcontains 'picker' -and $skip -notcontains 'dashboard') {
    # 9. the server folder picker (Libraries > a library's More options > Manage > Folder: Add), a window of its own
    $sub = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Libraries'))) |
        Where-Object { try { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Current.IsOffscreen } catch { $false } })
    Say "Libraries items in the dashboard: $($sub.Count)"
    if ($sub.Count) {
        [void](Invoke-Element $sub[-1]); Start-Sleep -Milliseconds 800
        $sub = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Libraries'))) |
            Where-Object { try { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Current.IsOffscreen } catch { $false } })
        if ($sub.Count -gt 1) { [void](Invoke-Element $sub[-1]) }
        Start-Sleep -Seconds 2
        Capture-State 'dashboard-libraries'
        # the folder picker's way in: Add New Library > Content type (a ComboBox: expanded and its first type selected
        # through UI Automation) > Folder: Add. (A library's More options menu, a MenuBarItemFlyout, opened neither for
        # UI Automation's Invoke nor for a posted click.)
        Press-First @('Add library', 'Add New Library')   # the admin stream's words since 1.2.0 ("Add New Library" before)
        Start-Sleep -Seconds 2
        $combo = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))) | Select-Object -First 1 } -Seconds 8
        $manage = $null
        if ($combo) {
            $ec = $null
            if ($combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$ec)) { $ec.Expand(); Start-Sleep -Milliseconds 800 }
            $item = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
                    Where-Object { try { $_.Current.Name -match '^(movies|Movies)$' } catch { $false } }) | Select-Object -First 1 } -Seconds 5
            if (-not $item) { $item = @(Get-OtherWindows | ForEach-Object { Under-Name $_ 'movies' } | Where-Object { $_ }) | Select-Object -First 1 }
            if ($item) { [void](Invoke-Element $item); Say 'content type: movies'; Start-Sleep -Milliseconds 800 }
            if ($ec) { try { $ec.Collapse() } catch { } }
            $manage = $item
        }
        if ($manage) {
            Start-Sleep -Seconds 1
            Capture-State 'add-library'
            # the picker twice: once listing the server's top folder, once with the server failing (the client answers a
            # failure with no folders: the "No folders here" message), each closed with WM_CLOSE (posted: a UI
            # Automation Invoke inside the picker's window hung a run for its whole time-out, 2026-11-17)
            foreach ($round in @(@{ Name = 'picker'; Rule = $null }, @{ Name = 'picker-server-failing'; Rule = 'status:500' })) {
                if ($round.Rule) { Set-Rules @(, @('^/Environment/DirectoryContents$', $round.Rule)) } else { Set-Rules @() }
                Press-First @('Add folder', 'Add')   # the admin stream's name since 1.2.0 ("Add" before)
                $picker = Wait-Until { Find-OtherWindow 'Choose a folder' } -Seconds 10
                if (-not $picker) { Say 'no folder picker window'; break }
                $ph = [IntPtr]$picker.Current.NativeWindowHandle
                Start-Sleep -Seconds 2
                $main = $script:hwnd; $script:hwnd = $ph
                try { Capture-State $round.Name }
                finally { $script:hwnd = $main }
                [void][OffscreenUi]::PostMessage($ph, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
                $gone = Wait-Until { -not (Find-OtherWindow 'Choose a folder') } -Seconds 5
                Check "$($round.Name): the window's close closes the picker" ([bool]$gone)
            }
            Set-Rules @()
            # the window's close picks nothing: the new library's folder list is still empty
            Capture-State 'add-library-after-picker'
            $closeDialog = Find-Ui -Id 'CloseButton' -Type 'Button'
            if ($closeDialog) { [void](Invoke-Element $closeDialog); Start-Sleep -Milliseconds 600 }
        }
        else { Say 'no content type to choose in Add New Library' }
    }
}
Set-Rules @()
