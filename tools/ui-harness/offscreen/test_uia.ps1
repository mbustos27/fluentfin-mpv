<#
  Tests of offscreen\uia.ps1's choice of the window posted keys go to (OffscreenUi.PickKeyWindow). Starts nothing and
  touches no window: the choice is tested on made-up child window lists.

    powershell -NoProfile -ExecutionPolicy Bypass -File tools\ui-harness\offscreen\test_uia.ps1

  Prints PASS/FAIL per check; exit code 1 when any failed. -Uia <copy of uia.ps1> tests another copy.
#>
param([string]$Uia)
$ErrorActionPreference = 'Stop'
if (-not $Uia) { $Uia = Join-Path $PSScriptRoot 'uia.ps1' }
. $Uia
$script:failed = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if (-not $Ok) { $script:failed++ }
    Write-Host ("{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" }))
}
$bridge = 'Microsoft.UI.Content.DesktopChildSiteBridge'; $site = 'InputSiteWindowClass'
function Pick([string[]]$Classes, [uint32[]]$Pids, [uint32]$AppPid = 10) { [OffscreenUi]::PickKeyWindow($Classes, $Pids, $AppPid) }

# FluentFin's window as it is: the bridge, then its hidden input site (keys posted to the bridge never reach the app)
$i = Pick @($bridge, $site) @(10, 10)
Check 'the input site is chosen over the bridge before it' ($i -eq 1) "$i"
$i = Pick @('mpv', $bridge, 'Other', $site) @(20, 10, 10, 10)
Check 'the input site is chosen with the video window and other children around it' ($i -eq 3) "$i"
$i = Pick @($site, $bridge, $site) @(20, 10, 10)
Check "another process's input site (mpv's or anything embedded) is never chosen" ($i -eq 2) "$i"
$i = Pick @($bridge, 'Other') @(10, 10)
Check 'no input site: the bridge' ($i -eq 0) "$i"
$i = Pick @($site, $bridge) @(20, 20)
Check 'none of the app''s own: -1 (the caller posts to the main window)' ($i -eq -1) "$i"
$i = Pick @() @()
Check 'no children: -1' ($i -eq -1) "$i"

Write-Host ("{0} failed" -f $script:failed)
if ($script:failed) { exit 1 }
