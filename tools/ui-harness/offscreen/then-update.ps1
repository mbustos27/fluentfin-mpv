# Nixie 1.3.1, the update check; Nixie 1.5 (the updater stream), every step of an update, against the fake server's release
# (fakeserver\release.py: never GitHub; drive.ps1 sets NIXIE_UPDATE_API, honoured by dev builds under the harness only).
#
# Quick (UP_FLOW unset): the start check finds FAKESERVER_RELEASE, the notice names it with its action, Settings > Updates
# says the same.
#   $env:FAKESERVER_RELEASE='1.5.1'; run-offscreen.ps1 -Script home -Setting CheckForUpdates=true -Then offscreen\then-update.ps1
#
# Full (UP_FLOW=full, with NIXIE_UPDATE_INSTALLED=1 so the dev build acts as an installed copy; FAKESERVER_RELEASE unset):
#   1. the start check: up to date (Settings: "You're up to date · checked just now"); the API request carried Nixie's
#      User-Agent;
#   2. Check now with GitHub not answering: "Checking for updates…", then "Couldn't check: GitHub did not answer in time";
#   3. 1.5.1 published: Check now finds it and the installed copy downloads it: progress in Settings and in the notice;
#   4. Cancel (the notice): "available · 24 MB" with Download; Download resumes from where it stopped (an HTTP Range);
#   5. ready: "Nixie 1.5.1 is ready to install" (Restart to update), the release notes in the app;
#   6. 1.5.2 whose .sha256 names other bytes: "Couldn't download Nixie 1.5.2" with Retry;
#   7. UP_RESTART=1: 1.5.3 ready, then Restart to update: the handoff is written down (a harness run never installs) and
#      Nixie closes; otherwise: a pending install of this version (as the helper leaves it), then a relaunch: "Updated to
#      Nixie <version>" back on Settings, and What's new.
#   $env:UP_FLOW='full'; $env:NIXIE_UPDATE_INSTALLED='1'; run-offscreen.ps1 -Script home -Setting CheckForUpdates=true -Then offscreen\then-update.ps1
#   (NIXIE_LAYOUT names the theme, NIXIE_THEME Light or Dark; SG_SHOT_TAG the shots; UP_NARROW=0 leaves out the 1100 shots)
# Dot-sourced by drive.ps1: every name here starts with UP (drive.ps1's names must not be reused). Titles with … and · are
# built from their code points: Windows PowerShell 5.1 reads this file as ANSI.

$upTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'update' }
$upEll = [string][char]0x2026
$upDot = " $([char]0xB7) "
$upNarrow = $env:UP_NARROW -ne '0'

$script:upOnSettings = $false

# Settings > Updates in view: the group sits above About, near the end of the page; its last switch scrolled into view
# shows the whole group (the heading, Check for updates, the status and Install when I close Nixie)
function UP-ScrollUpdates {
    # the heading after the group (About) brought to the bottom edge: the whole group above it in view
    $last = Wait-Ui -Name 'About' -Type 'Text' -OffscreenToo -Seconds 10
    if (-not $last) { $last = Find-Ui -Name 'Install when I close Nixie' -OffscreenToo }
    if ($last) {
        $p = $null
        try { if ($last.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { $p.ScrollIntoView() } } catch {}
    }
    if (-not (Find-Ui -Name 'Check now' -Type 'Button')) { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100) }
    Start-Sleep -Milliseconds 500
}

function UP-Shot([string]$Name) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
    Start-Sleep -Milliseconds 700
    if ($script:upOnSettings) { UP-ScrollUpdates }
    Shot "$upTag-$Name-1920"
    if ($upNarrow) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900)
        Start-Sleep -Milliseconds 900
        if ($script:upOnSettings) { UP-ScrollUpdates }
        Shot "$upTag-$Name-1100"
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
        Start-Sleep -Milliseconds 400
        if ($script:upOnSettings) { UP-ScrollUpdates }
    }
}

function UP-Settings {
    Open-Nav 'Settings'
    $script:upOnSettings = $true
    $card = Wait-Ui -Name 'Check now' -Type 'Button' -OffscreenToo -Seconds 15
    if ($card) { UP-ScrollUpdates }
    $card
}

function UP-Home { Open-Nav 'Home'; $script:upOnSettings = $false; Start-Sleep -Milliseconds 800 }

function UP-Click([string]$Name, [double]$Seconds = 10) {
    $b = Wait-Ui -Name $Name -Seconds $Seconds
    if ($b) { [void](Invoke-Element $b) }
    [bool]$b
}

function UP-Release($Body) { Invoke-Server '/__harness/release' 'POST' $Body }

# a dialog of notes: the keyboard starts on its Close button (not on a link in the notes), and Esc closes it. Never a
# click on a button named Close: the window's own caption button has that name too
function UP-CloseDialog([string]$What) {
    # the app's own focus report (the system's focus tracking sees nothing on the harness's desktop)
    $focused = [string](Wait-Until { [OffscreenUi]::Focused($script:hwnd, $script:ui.Id) } -Seconds 3 -PollMs 150)
    $upF = if ($focused) { $focused | ConvertFrom-Json } else { $null }
    if (-not $upF -or $upF.name -ne 'Close') { Dump-Tree (Safe-Name "$upTag-dialog-$What") }
    # and the keyboard is not held there: Tab moves on (to the notes' links and the release page)
    Key 0x09
    Start-Sleep -Milliseconds 400
    $upNext = [string](Wait-Until { [OffscreenUi]::Focused($script:hwnd, $script:ui.Id) } -Seconds 2 -PollMs 150)
    $upN = if ($upNext) { $upNext | ConvertFrom-Json } else { $null }
    Check "update: in $What, Tab moves on from Close" ($upN -and $upN.name -ne 'Close') "focus on $upNext"
    Check "update: $What starts with the keyboard on Close" ($upF -and $upF.type -eq 'Button' -and $upF.name -eq 'Close') "focus on $focused"
    Key 0x1B
    Start-Sleep -Milliseconds 800
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)

if ($env:UP_FLOW -ne 'full') {
    # ---- quick: the start check and the notice (1.3.1's run, with 1.5's words)
    $upVersion = if ($env:FAKESERVER_RELEASE) { $env:FAKESERVER_RELEASE } else { '0.0.1' }
    $upInstalled = $env:NIXIE_UPDATE_INSTALLED -eq '1'
    $upTitles = @("Nixie $upVersion is available", "Downloading Nixie $upVersion", "Nixie $upVersion is ready to install")
    $upBar = Wait-Until { foreach ($t in $upTitles) { if (Find-Ui -Name $t) { return $t } }; if (Find-Ui -Name "Nixie $upVersion is available$upDot" -Contains) { 'sized' } } -Seconds 60 -PollMs 500
    $upAsked = @(Get-Requests 0 | Where-Object { $_.path -eq '/__harness/release/latest' })
    Check 'update: the start check asked the fake server (never GitHub)' ($upAsked.Count -ge 1) "$($upAsked.Count) request(s) to /__harness/release/latest"
    Check 'update: the check sends a User-Agent (GitHub refuses requests without one)' (@($upAsked | Where-Object { $_.ua -like 'Nixie/*' }).Count -eq $upAsked.Count) (($upAsked | ForEach-Object ua | Select-Object -Unique) -join ', ')
    Check "update: the notice names Nixie $upVersion after the start check" ([bool]$upBar) "$upBar"
    $upAction = if ($upInstalled) { @('Download', 'Cancel', 'Restart to update') } else { @('Open the release page') }
    Check "update: the notice offers its step's action ($($upAction -join ' or '))" ([bool](Wait-Until { foreach ($a in $upAction) { if (Find-Ui -Name $a -Type 'Button') { return $a } } } -Seconds 10))
    Check 'update: the notice offers Dismiss' ([bool](Find-Ui -Name 'Dismiss' -Type 'Button'))
    UP-Shot 'home'
    $upCheck = UP-Settings
    Check 'update: Settings > Updates has Check now' ([bool]$upCheck)
    if ($upCheck) {
        $upAt = Get-Epoch
        [void](Invoke-Element $upCheck)
        $upSaid = Wait-Until { foreach ($t in $upTitles) { if (Find-Ui -Name $t -Type 'Text') { return $t } }; if (Find-Ui -Name "Nixie $upVersion is available$upDot" -Contains -Type 'Text') { 'sized' } } -Seconds 15 -PollMs 300
        $upAsked = @(Get-Requests $upAt | Where-Object { $_.path -eq '/__harness/release/latest' })
        # 1.5: a Check now within 15 s of the last answer says it again without asking
        Check 'update: Check now says what it found' ([bool]$upSaid) "$upSaid; $($upAsked.Count) new request(s)"
        UP-Shot 'settings-check-now'
    }
    return
}

# ---- full: every step
# 1.5.1: the three releases the full flow publishes are this build's next three (they were 1.5.1 to 1.5.3, which a 1.5.1
# build would not take for updates)
$upBase = [version](Get-Item -LiteralPath $Exe).VersionInfo.FileVersion
$upV1, $upV2, $upV3 = @(1, 2, 3 | ForEach-Object { '{0}.{1}.{2}' -f $upBase.Major, $upBase.Minor, ($upBase.Build + $_) })
Say "update (full): this build is $upBase; the releases published are $upV1, $upV2 and $upV3"
$upInstalled = $env:NIXIE_UPDATE_INSTALLED -eq '1'
Check 'update (full): the run acts as an installed copy (NIXIE_UPDATE_INSTALLED=1)' $upInstalled
$upNotes = "## Nixie $upV1`n`nThe **updater** shows every step.`n`n- Downloads in the background, with its progress`n- Restart to update reopens Nixie where you were ([details](https://github.com/mbustos27/fluentfin-mpv/releases))`n  - checked against its ``.sha256`` and GitHub's digest`n- Release notes in the app`n"
[void](UP-Release @{ version = '0.0.1'; mode = 'ok'; notes = $upNotes; kbps = 600; mismatch = $false; digest = 'ok' })

# 1. the start check: up to date
$upFirst = Wait-Until { @(Get-Requests 0 | Where-Object { $_.path -eq '/__harness/release/latest' }) | Select-Object -First 1 } -Seconds 60 -PollMs 500
Check 'update: the start check asked the fake server (never GitHub)' ([bool]$upFirst)
Check 'update: with a User-Agent (GitHub answers 403 without one)' ($upFirst -and $upFirst.ua -like 'Nixie/*') "$($upFirst.ua)"
[void](UP-Settings)
$upFresh = Wait-Until { Find-Ui -Name "You're up to date$($upDot)checked " -Contains -Type 'Text' } -Seconds 15
Check "update: Settings says you're up to date, and when it checked" ([bool]$upFresh) $(if ($upFresh) { $upFresh.Current.Name })
UP-Shot 'settings-uptodate'

# 2. GitHub not answering: Checking for updates…, then why it couldn't check (Check now says an answer under 15 s old
#    again without asking: wait that out first)
[void](UP-Release @{ mode = 'hang' })
$upWait = 16 - ((Get-Epoch) - $upFirst.t)
if ($upWait -gt 0) { Start-Sleep -Milliseconds ([int]($upWait * 1000)) }
[void](UP-Click 'Check now')
$upChecking = Wait-Until { Find-Ui -Name "Checking for updates$upEll" -Type 'Text' } -Seconds 8 -PollMs 100
Check 'update: Check now says it is checking' ([bool]$upChecking)
UP-Shot 'settings-checking'
$upFailed = Wait-Until { Find-Ui -Name "Couldn't check: " -Contains -Type 'Text' } -Seconds 30 -PollMs 300
Check 'update: a check GitHub never answers says why, with Retry' ($upFailed -and (Find-Ui -Name 'Retry' -Type 'Button')) $(if ($upFailed) { $upFailed.Current.Name })
UP-Shot 'settings-check-failed'

# 3. the next version published: found, and the installed copy downloads it at once
[void](UP-Release @{ mode = 'ok'; version = $upV1; kbps = 600 })
Start-Sleep -Seconds 1
[void](UP-Click 'Retry')
$upDownloading = Wait-Until { Find-Ui -Name "Downloading Nixie $upV1" -Type 'Text' } -Seconds 20 -PollMs 200
Check 'update: a found update downloads by itself (installed copy)' ([bool]$upDownloading)
Start-Sleep -Seconds 3
$upLine = Find-Ui -Name ' MB of 24 MB' -Contains -Type 'Text'
Check 'update: Settings shows the progress: MB of total, speed, time left' ($upLine -and $upLine.Current.Name -match 'KB/s|MB/s' -and $upLine.Current.Name -match 'left') $(if ($upLine) { $upLine.Current.Name })
Check 'update: and a progress bar' ([bool](Find-Ui -Name 'Download progress'))
UP-Shot 'settings-downloading'
UP-Home
$upNotice = Wait-Until { Find-Ui -Name "Downloading Nixie $upV1" -Type 'Text' } -Seconds 10
Check 'update: the notice shows the same download' ($upNotice -and (Find-Ui -Name 'Cancel' -Type 'Button'))
UP-Shot 'notice-downloading'

# 4. Cancel, then Download carries on where it stopped
[void](UP-Click 'Cancel')
$upAvail = Wait-Until { Find-Ui -Name "Nixie $upV1 is available$($upDot)24 MB" -Type 'Text' } -Seconds 10
Check 'update: Cancel stops it: available, with its size and Download' ($upAvail -and (Find-Ui -Name 'Download' -Type 'Button'))
UP-Shot 'notice-available'
[void](UP-Settings)
Check 'update: Settings says available too, with its size and Download' ([bool](Wait-Until { Find-Ui -Name "Nixie $upV1 is available$($upDot)24 MB" -Type 'Text' } -Seconds 5))
UP-Shot 'settings-available'
UP-Home
[void](UP-Release @{ kbps = 40000 })
$upAt = Get-Epoch
[void](UP-Click 'Download')
$upReady = Wait-Until { Find-Ui -Name "Nixie $upV1 is ready to install" -Type 'Text' } -Seconds 60 -PollMs 300
$upResumed = @(Get-Requests $upAt | Where-Object { $_.route -like 'release:installer*' })
Check 'update: Download resumed the cancelled download (an HTTP Range)' (@($upResumed | Where-Object { $_.report.range_from -gt 0 }).Count -ge 1) (($upResumed | ForEach-Object { "$($_.route) $($_.status)" }) -join '; ')
Check 'update: then checked and ready, with Restart to update' ($upReady -and (Find-Ui -Name 'Restart to update' -Type 'Button'))
UP-Shot 'notice-ready'
[void](UP-Settings)
Check 'update: Settings says it is ready too' ([bool](Wait-Until { Find-Ui -Name "Nixie $upV1 is ready to install" -Type 'Text' } -Seconds 5))
Check 'update: on Settings the notice steps aside (one Restart to update, one Primary)' (@([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Restart to update')), (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))).Count -eq 1)
UP-Shot 'settings-ready'

# 5. the release notes in the app
if (UP-Click 'Release notes') {
    $upDialog = Wait-Until { Find-Ui -Name "What's new in Nixie $upV1" } -Seconds 10
    Check 'update: Release notes opens the notes in the app' ([bool]$upDialog)
    Check 'update: the notes read as Markdown (their heading, no ## left)' (-not (Find-Ui -Name '## ' -Contains -Type 'Text'))
    $script:upOnSettings = $false
    UP-Shot 'notes-dialog'
    UP-CloseDialog 'the release notes'
    $script:upOnSettings = $true
}

# 6. a download that doesn't match its checksum
[void](UP-Release @{ version = $upV2; mismatch = $true; digest = 'none'; kbps = 40000 })   # no digest: the .sha256 alone, started over once
Start-Sleep -Seconds 16      # Check now says a 15-s-old answer again: ask after it
[void](UP-Click 'Check now')
$upBad = Wait-Until { Find-Ui -Name "Couldn't download Nixie $upV2" -Type 'Text' } -Seconds 60 -PollMs 300
Check "update: a checksum mismatch (twice) says it couldn't download, with Retry" ($upBad -and (Find-Ui -Name 'Retry' -Type 'Button'))
UP-Shot 'settings-download-failed'
UP-Home
Check 'update: the notice says so too' ([bool](Wait-Until { Find-Ui -Name "Couldn't download Nixie $upV2" -Type 'Text' } -Seconds 5))
UP-Shot 'notice-download-failed'

$upUpdates = Join-Path $DataDir 'Updates'
if ($env:UP_RESTART -eq '1') {
    # 7a. Restart to update: the handoff written down (never installed in a harness run), and Nixie closes
    [void](UP-Release @{ version = $upV3; mismatch = $false; digest = 'ok' })
    Start-Sleep -Seconds 16
    [void](UP-Settings)
    [void](UP-Click 'Check now')
    $upReady = Wait-Until { Find-Ui -Name "Nixie $upV3 is ready to install" -Type 'Text' } -Seconds 60 -PollMs 300
    Check "update: $upV3 ready" ([bool]$upReady)
    [void](UP-Click 'Restart to update')
    $upGone = Wait-Until { $script:ui.Refresh(); $script:ui.HasExited } -Seconds 40 -PollMs 250
    Check 'update: Restart to update closes Nixie' ([bool]$upGone)
    $upPlan = Join-Path $upUpdates 'harness-handoff.txt'
    $upArgs = if (Test-Path $upPlan) { @(Get-Content -LiteralPath $upPlan) } else { @() }
    Check "update: the handoff installs $upV3 with a log, waits for this Nixie, and reopens it" (
        $upArgs -contains '--nixie-install-update' -and ($upArgs -match ('Nixie-' + [regex]::Escape($upV3) + '-win-x64\.msi$')).Count -eq 1 -and $upArgs -contains '--reopen' -and $upArgs -contains '--wait-pid' -and $upArgs -notcontains '--quiet') ($upArgs -join ' ')
    $upPending = Join-Path $upUpdates 'pending.json'
    $upP = if (Test-Path $upPending) { Get-Content -Raw -LiteralPath $upPending | ConvertFrom-Json }
    Check 'update: what the next start reads: the version and the page to come back to' ($upP -and $upP.Version -eq $upV3 -and $upP.Resume.PageKey -like '*ViewModel') "$($upP.Version) $($upP.Resume.PageKey)"
    return
}

# 7b. after an update: a pending install of this version, as the helper leaves it; the next start says Updated to
$upCsproj = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\..\..\ui\FluentFin\FluentFin.csproj')
$upCurrent = ([regex]::Matches($upCsproj, '>(\d+\.\d+\.\d+)</Version>') | Select-Object -First 1).Groups[1].Value
$upPendingJson = [ordered]@{ Version = $upCurrent; FromVersion = '1.4.0'; StartedAt = ([DateTimeOffset]::UtcNow).ToString('o'); PageUrl = "https://github.com/mbustos27/fluentfin-mpv/releases/tag/v$upCurrent"
    Notes = $upNotes; Reopen = $true; Resume = [ordered]@{ PageKey = 'FluentFin.Core.ViewModels.SettingsViewModel'; ItemId = $null } } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $upUpdates 'pending.json'), $upPendingJson, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $upUpdates 'install-result.txt'), "exit=0`r`n", [Text.UTF8Encoding]::new($false))
[void](UP-Release @{ version = '0.0.1'; mismatch = $false; digest = 'ok' })
Step-Relaunch
$script:upOnSettings = $true
Check 'update: the next start is back on the page it was on (Settings)' ([bool](Wait-Until { Find-Ui -Name 'Check now' -Type 'Button' -OffscreenToo } -Seconds 15))
UP-ScrollUpdates
$upUpdated = Wait-Until { Find-Ui -Name "Updated to Nixie $upCurrent" -Type 'Text' } -Seconds 20
Check "update: Settings says Updated to Nixie $upCurrent, with What's new" ($upUpdated -and (Find-Ui -Name 'What''s new' -Type 'Button'))
Check 'update: on Settings the notice steps aside (one What''s new, one Primary)' (@([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.AndCondition((New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'What''s new')), (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))).Count -eq 1)
Check 'update: the handoff files are gone' (-not (Test-Path (Join-Path $upUpdates 'pending.json')) -and -not (Test-Path (Join-Path $upUpdates 'install-result.txt')))
UP-Shot 'settings-updated'
UP-Home
$upUpdated = Wait-Until { Find-Ui -Name "Updated to Nixie $upCurrent" -Type 'Text' } -Seconds 10
Check "update: on Home the notice says Updated to Nixie $upCurrent, with What's new" ($upUpdated -and (Find-Ui -Name 'What''s new' -Type 'Button'))
UP-Shot 'notice-updated'
if (UP-Click 'What''s new') {
    Check "update: What's new shows the notes" ([bool](Wait-Until { Find-Ui -Name "What's new in Nixie $upCurrent" } -Seconds 10))
    UP-Shot 'whatsnew-dialog'
    UP-CloseDialog "What's new"
    Check "update: once seen, the notice goes" (-not (Find-Ui -Name "Updated to Nixie $upCurrent" -Type 'Text'))
}
