# Nixie 1.3.1, the update check: with the setting on, the check at start (about 20 s after the window opens) asks the fake
# server's release answer (NIXIE_UPDATE_API, set by drive.ps1: never GitHub) and the notice "Nixie <version> is available"
# shows above the page, in every theme; then Settings > Check for updates > Check now says the same inline.
#   $env:FAKESERVER_RELEASE='1.3.2'; run-offscreen.ps1 -Script home -Setting CheckForUpdates=true -Then offscreen\then-update.ps1
#   (NIXIE_LAYOUT names the theme; SG_SHOT_TAG the shots)
# Dot-sourced by drive.ps1: every name here starts with UP (drive.ps1's names must not be reused).

$upTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'update' }
$upVersion = if ($env:FAKESERVER_RELEASE) { $env:FAKESERVER_RELEASE } else { '0.0.1' }
$upTitle = "Nixie $upVersion is available"

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080)
$upBar = Wait-Until { Find-Ui -Name $upTitle } -Seconds 60 -PollMs 500
$upAsked = @(Get-Requests 0 | Where-Object { $_.path -eq '/__harness/release/latest' })
Check 'update: the start check asked the fake server (never GitHub)' ($upAsked.Count -ge 1) "$($upAsked.Count) request(s) to /__harness/release/latest"
Check "update: the notice '$upTitle' shows after the start check" ([bool]$upBar)
foreach ($upName in @('Download and install', 'Release notes', 'Dismiss')) {
    $upButton = Find-Ui -Name $upName
    if (-not $upButton -and $upName -eq 'Download and install') { $upButton = Find-Ui -Name 'Open the download page' }
    Check "update: the notice offers $upName" ([bool]$upButton)
}
Start-Sleep -Milliseconds 800
Shot "$upTag-home-1920"

# Check now, in Settings: the same answer, inline
Open-Nav 'Settings'
$upCheck = Wait-Ui -Name 'Check now' -Type 'Button' -OffscreenToo -Seconds 15
Check 'update: Settings has Check now' ([bool]$upCheck)
if ($upCheck) {
    [void][OffscreenUi]::SetFocus($upCheck)
    $upAt = Get-Epoch
    [void](Invoke-Element $upCheck)
    $upSaid = Wait-Until { Find-Ui -Name $upTitle -Type 'Text' } -Seconds 15 -PollMs 300
    $upAsked = @(Get-Requests $upAt | Where-Object { $_.path -eq '/__harness/release/latest' })
    Check 'update: Check now asks at once and says what it found' ([bool]$upSaid -and $upAsked.Count -ge 1) "$($upAsked.Count) request(s)"
    [void][OffscreenUi]::SetFocus($upCheck)
    Start-Sleep -Milliseconds 600
    Shot "$upTag-settings-check-now"
}
