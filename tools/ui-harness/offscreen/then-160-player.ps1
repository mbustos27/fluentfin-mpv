# Nixie 1.6 (the player stream): the Quality menu and Playback stats, by keyboard, in the layout theme NIXIE_LAYOUT names.
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script season,play -Setting ShowPlaybackStatsButton=true -Then offscreen\then-160-player.ps1 `
#       -Before offscreen\before-backend-debug.ps1
#   (1.6.1: the backend logs a polled get_stats at DEBUG, so the stats checks count its questions in a DEBUG log)
# Dot-sourced by drive.ps1 after the play step (FluentFin's controls; paused, the bars up, the keyboard on Play/Pause). Every
# name here starts with QP. P160_NARROW=0 skips the 1100 px shots; P160_SETTINGS=0 skips Settings.
#
# The fake server direct-plays whatever it is asked (it never converts), so a cap under its file's bitrate is the refusal a
# server makes for an account that may not have video converted: the notice in plain words, the menu then on Original, and
# one viewing (no second Playing). A real conversion is the QA servers' (tools\qa-servers\live-quality.ps1).

$qpTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Default' }
# the middle dot and the times sign as characters (this file is read as ANSI by Windows PowerShell: no literal of them)
$qpDot = [string][char]0x00B7; $qpTimes = [string][char]0x00D7
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function QP-Names($Element) {
    if (-not $Element) { return @() }
    @($Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) |
        ForEach-Object { $_.Current.Name })
}
function QP-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function QP-FocusText { $f = QP-Focus; if ($f) { "$($f.type) '$($f.name)' $($f.automationId)" } else { 'nothing' } }
# get_stats questions the backend has had so far (its log: "<- get_stats (id N)", at DEBUG since 1.6.1: -Before
# offscreen\before-backend-debug.ps1, or every count here is 0)
function QP-StatsAsked {
    $log = Join-Path $DataDir 'Logs\mpv-backend.log'
    if (-not (Test-Path $log)) { return 0 }
    @(Get-Content -LiteralPath $log -Encoding utf8 | Where-Object { $_ -match '<- get_stats \(id' }).Count
}
function QP-Menu { Find-Ui -Name 'Quality' -Type 'List' }
function QP-OpenMenu {
    $button = Wait-Ui -Id 'QualityButton' -Seconds 6
    if (-not $button) { return $null }
    [void][OffscreenUi]::SetFocus($button)
    Start-Sleep -Milliseconds 300
    Key 0x0D
    Wait-Until { QP-Menu } -Seconds 6 -PollMs 150
}
function QP-CloseMenu { Key 0x1B; Wait-Until { if (-not (QP-Menu)) { $true } } -Seconds 6 -PollMs 150 }

Say "then-160-player: theme $qpTheme"

# ---- the Quality button and its menu, by keyboard ---------------------------------------------------------------------------
$qpButton = Wait-Ui -Id 'QualityButton' -Seconds 10
Check "160 $qpTheme | the Quality button is in the bar" ([bool]$qpButton) $(if ($qpButton) { $qpButton.Current.Name })
if ($qpButton) {
    Check "160 $qpTheme | its name says the quality (Quality, Auto)" ($qpButton.Current.Name -eq 'Quality, Auto') $qpButton.Current.Name
}

$qpMenu = QP-OpenMenu
Check "160 $qpTheme | Enter on the Quality button opens its menu" ([bool]$qpMenu)
if ($qpMenu) {
    Start-Sleep -Milliseconds 500
    $now = Find-Ui -Id 'QualityNowText'
    $nowText = if ($now) { $now.Current.Name } else { '' }
    Check "160 $qpTheme | the header says what plays now" ($nowText -match "^Direct play $qpDot 720p $qpDot \d") $nowText
    $names = QP-Names $qpMenu
    $expected = @('Auto, current', 'Original, the file as it is', '6 Mbps, 720p', '4 Mbps, 720p', '3 Mbps, 480p', '1.5 Mbps, 480p', '720 kbps, 480p')
    Check "160 $qpTheme | the choices suit the 720p file, Auto current" (($names -join '|') -eq ($expected -join '|')) ($names -join ' | ')
    $focus = QP-FocusText
    Check "160 $qpTheme | the keyboard starts on the choice in effect" ($focus -match "'Auto, current'") $focus
    Shot "160-quality-menu-$qpTheme"
    $closed = QP-CloseMenu
    Check "160 $qpTheme | Esc closes the menu" ([bool]$closed)
    Start-Sleep -Milliseconds 400
    $focus = QP-FocusText
    Check "160 $qpTheme | the keyboard is back on the Quality button" ($focus -match 'QualityButton') $focus
}

# ---- a cap the server does not convert: plain words, Original, one viewing --------------------------------------------------
$qpPlays = @(Get-Requests | Where-Object { $_.route -eq 'report-playing' }).Count
$qpInfos = @(Get-Requests | Where-Object { $_.route -eq 'playbackinfo' }).Count
$qpMenu = QP-OpenMenu
if ($qpMenu) {
    Start-Sleep -Milliseconds 400
    foreach ($i in 1..5) { Key 0x28; Start-Sleep -Milliseconds 150 }   # Down to 1.5 Mbps: the arrows only move
    Start-Sleep -Milliseconds 300
    $infosMoving = @(Get-Requests | Where-Object { $_.route -eq 'playbackinfo' }).Count
    Check "160 $qpTheme | moving through the list asks the server nothing" ($infosMoving -eq $qpInfos) "$qpInfos -> $infosMoving PlaybackInfo"
    $focus = QP-FocusText
    Check "160 $qpTheme | Down five times is 1.5 Mbps" ($focus -match "'1.5 Mbps, 480p'") $focus
    Key 0x0D
    $gone = Wait-Until { if (-not (QP-Menu)) { $true } } -Seconds 6 -PollMs 150
    Check "160 $qpTheme | Enter picks and closes the menu" ([bool]$gone)
    $notice = Wait-Ui -Name "This server doesn't allow a lower quality" -Contains -Seconds 20
    Check "160 $qpTheme | the refusal is said in plain words" ([bool]$notice) $(if ($notice) { $notice.Current.Name })
    if ($notice) { Shot "160-quality-refused-$qpTheme" }
    $infos = @(Get-Requests | Where-Object { $_.route -eq 'playbackinfo' }).Count
    $plays = @(Get-Requests | Where-Object { $_.route -eq 'report-playing' }).Count
    Check "160 $qpTheme | the pick renegotiated the stream once" ($infos -eq $qpInfos + 1) "$qpInfos -> $infos PlaybackInfo"
    Check "160 $qpTheme | one viewing: no second Playing" ($plays -eq $qpPlays) "$qpPlays -> $plays Playing"
    $button = Wait-Until { $b = Find-Ui -Id 'QualityButton'; if ($b -and $b.Current.Name -eq 'Quality, Original') { $b } } -Seconds 10 -PollMs 200
    Check "160 $qpTheme | the button then reads Original" ([bool]$button) $(if ($b = Find-Ui -Id 'QualityButton') { $b.Current.Name })
    $qpMenu = QP-OpenMenu
    $names = QP-Names $qpMenu
    Check "160 $qpTheme | the menu marks Original as current" ($names -contains 'Original, the file as it is, current') ($names -join ' | ')
    [void](QP-CloseMenu)
}

# ---- Playback stats: I opens, reads about once a second, Esc closes, nothing read while closed ------------------------------
# the counts below mean something only with the backend's DEBUG lines in its log (before-backend-debug.ps1)
$qpBackendLog = Join-Path $DataDir 'Logs\mpv-backend.log'
$qpDebug = (Test-Path -LiteralPath $qpBackendLog) -and [bool](Select-String -LiteralPath $qpBackendLog -Pattern '\[\s*DEBUG\]' -Quiet)
Check "160 $qpTheme | the backend logs at DEBUG, so its get_stats questions can be counted" $qpDebug 'run with -Before offscreen\before-backend-debug.ps1'
$qpAsked0 = QP-StatsAsked
Start-Sleep -Seconds 3
Check "160 $qpTheme | closed, the stats are never asked for" ((QP-StatsAsked) -eq $qpAsked0) "$qpAsked0 -> $(QP-StatsAsked)"
[void][OffscreenUi]::SetFocus((Find-Ui -Id 'PlayPauseButton'))
Start-Sleep -Milliseconds 300
Key 0x49
$panel = Wait-Ui -Id 'StatsPanel' -Seconds 6
Check "160 $qpTheme | I opens Playback stats" ([bool]$panel) $(if ($panel) { "$($panel.Current.ControlType.ProgrammaticName) '$($panel.Current.Name)'" })
if ($panel) {
    $row = Wait-Ui -Name 'Playing: Direct play' -Seconds 6
    Check "160 $qpTheme | the panel reads the play method" ([bool]$row)
    foreach ($label in 'Container: MKV', "Resolution: 1280${qpTimes}720", 'Server: At home', 'Hardware decoding: ', 'mpv: mpv ') {
        Check "160 $qpTheme | the panel has '$label'" ([bool](Find-Ui -Name $label -Contains)) ''
    }
    $asked = QP-StatsAsked
    Start-Sleep -Seconds 3
    $more = (QP-StatsAsked) - $asked
    Check "160 $qpTheme | open, it asks about once a second" ($more -ge 2 -and $more -le 5) "$more questions in 3 s"
    Check "160 $qpTheme | Copy and Close are named" ([bool](Find-Ui -Name 'Copy the playback stats' -Type 'Button') -and [bool](Find-Ui -Name 'Close playback stats' -Type 'Button'))
    Shot "160-stats-$qpTheme"
    Key 0x1B
    $gone = Wait-Until { if (-not (Find-Ui -Id 'StatsPanel')) { $true } } -Seconds 6 -PollMs 150
    Check "160 $qpTheme | Esc closes Playback stats" ([bool]$gone)
    Check "160 $qpTheme | the window stays windowed after that Esc" (-not (Get-WindowState $script:hwnd).FullScreen)
    $asked = QP-StatsAsked
    Start-Sleep -Seconds 3
    Check "160 $qpTheme | closed again, nothing more is read" ((QP-StatsAsked) -eq $asked) "$asked -> $(QP-StatsAsked)"
    # I opens and I closes; a held I does not flicker it
    Key 0x49
    $open = Wait-Ui -Id 'StatsPanel' -Seconds 6
    Key 0x49
    $closed = Wait-Until { if (-not (Find-Ui -Id 'StatsPanel')) { $true } } -Seconds 6 -PollMs 150
    Check "160 $qpTheme | I opens and I closes" ([bool]$open -and [bool]$closed)
    $focus = QP-FocusText
    Check "160 $qpTheme | the keyboard stays in the player" ($focus -notmatch 'TitleBar|Minimize|Close Nixie') $focus
}

# the button (Settings' "Show the Playback stats button", set for this run)
$statsButton = Find-Ui -Id 'StatsButton'
if ($statsButton) {
    Check "160 $qpTheme | the stats button is named for what it does" ($statsButton.Current.Name -eq 'Show playback stats') $statsButton.Current.Name
    [void](Invoke-Element $statsButton)
    $open = Wait-Ui -Id 'StatsPanel' -Seconds 6
    $named = Wait-Until { $b = Find-Ui -Id 'StatsButton'; if ($b -and $b.Current.Name -eq 'Hide playback stats') { $b } } -Seconds 4 -PollMs 150
    Check "160 $qpTheme | the stats button opens the panel and says Hide" ([bool]$open -and [bool]$named)
    if ($env:P160_NARROW -ne '0') {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900); Start-Sleep -Milliseconds 1800
        Shot "160-stats-1100-$qpTheme"
        $qpMenu = QP-OpenMenu
        if ($qpMenu) { Start-Sleep -Milliseconds 500; Shot "160-quality-menu-1100-$qpTheme"; [void](QP-CloseMenu) }
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Start-Sleep -Milliseconds 1500
    }
    [void](Invoke-Element (Find-Ui -Id 'StatsButton'))
    $closed = Wait-Until { if (-not (Find-Ui -Id 'StatsPanel')) { $true } } -Seconds 6 -PollMs 150
    Check "160 $qpTheme | the stats button closes it again" ([bool]$closed)
}
else { Say 'the stats button is off for this run (Settings: ShowPlaybackStatsButton)' }

# ---- Settings > Playback: the quality cards ---------------------------------------------------------------------------------
if ($env:P160_SETTINGS -ne '0') {
    Key 0xA6     # the Back key: leaves the player
    Start-Sleep -Seconds 2
    Open-Nav 'Settings'
    $card = Wait-Ui -Name 'Quality at home' -Type 'ComboBox' -OffscreenToo -Seconds 15
    Check "160 $qpTheme | Settings has the quality at home" ([bool]$card)
    if ($card) {
        # the group's last card into view (the stats switch), so the three show together
        $last = Find-Ui -Name 'Show the Playback stats button' -Type 'Button' -OffscreenToo
        if (-not $last) { $last = $card }
        try { ($last.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView() } catch { }
        Start-Sleep -Milliseconds 800
        Check "160 $qpTheme | it reads Auto" ([bool](Find-Ui -Name 'Auto' -Contains -OffscreenToo)) ''
        Check "160 $qpTheme | Settings has the quality away and the stats switch" ([bool](Find-Ui -Name 'Quality away from home' -Type 'ComboBox' -OffscreenToo) -and [bool](Find-Ui -Name 'Show the Playback stats button' -OffscreenToo))
        Shot "160-settings-$qpTheme"
    }
}
