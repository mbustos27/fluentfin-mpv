<#
  1.6.1 (BH-151-m6): a packaged Nixie.exe refuses --install-update without the upgrade test's marker, for real. Runs ON a
  private desktop, started by verify-package.ps1 (its InstallSwitch run) through tools\backend-bench\hidden_run.py inside its
  kill-on-close job; refuses the input desktop. Nothing is installed: the installer it offers is a decoy (a text file named
  as a Nixie installer, with a matching .sha256), and a copy run from the unzipped folder is a portable copy, which never
  installs anything; no key or click is sent.

  Two starts, each Nixie.exe --data-dir <DataDir>\<leg> --install-update <decoy>:
    no-marker   NIXIE_UPGRADE_TEST unset: the log says the switch is refused for want of the marker (the release package's
                gate, UpdateHandoff.InstallSwitchAllowed), nothing says it is ready to install, and Nixie keeps running past
                the moment it would restart to install (8 s), until it is closed.
    marker      NIXIE_UPGRADE_TEST names a marker as the Sandbox test makes it: the gate lets the switch through and the
                portable copy refuses it later ("this copy can't install updates"), so the first leg's refusal is the
                gate's and not something else's.
  Writes <Out>\installswitch.json (the checks).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Out,
    [int]$Seconds = 60
)

$ErrorActionPreference = 'Stop'
$DataDir = $DataDir.TrimEnd('\', '/')
. (Join-Path $PSScriptRoot '..\ui-harness\common.ps1')
. (Join-Path $PSScriptRoot '..\ui-harness\offscreen\uia.ps1')

$desktop = [OffscreenUi]::ThisDesktop()
if (-not $desktop -or $desktop -eq 'Default' -or $desktop -eq [OffscreenUi]::InputDesktop()) { throw "runs only on a private desktop (this one: '$desktop')" }

New-Item -ItemType Directory -Force $Out | Out-Null
$checks = [ordered]@{}
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $checks[$Name] = [ordered]@{ ok = $Ok; detail = $Detail }
    "{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" })
}

# the decoy: named as a newer Nixie's installer, its .sha256 matching, so the gate is the only thing that can refuse it
$offered = Join-Path $Out 'offered'
New-Item -ItemType Directory -Force $offered | Out-Null
$msi = Join-Path $offered 'Nixie-99.0.0-win-x64.msi'
[IO.File]::WriteAllText($msi, "Not an installer: tools\release\installswitch-offscreen.ps1's decoy.`r`n", [Text.UTF8Encoding]::new($false))
$hash = (Get-FileHash -Algorithm SHA256 $msi).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$msi.sha256", "$hash *Nixie-99.0.0-win-x64.msi`n", [Text.UTF8Encoding]::new($false))
$decoyBefore = (Get-FileHash -Algorithm SHA256 $msi).Hash

$marker = Join-Path $Out '.nixie-upgrade-test'
[IO.File]::WriteAllText($marker, "made by tools\release\upgrade-test-sandbox.ps1 (stood in for by installswitch-offscreen.ps1)`r`n", [Text.UTF8Encoding]::new($false))

function Get-UpdateLines([string]$LogDir) {
    @(Get-ChildItem $LogDir -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -ErrorAction SilentlyContinue } |
        Where-Object { $_ -match '\[update\]' })
}

function Invoke-Leg([string]$Leg, [string]$MarkerPath) {
    $data = Join-Path $DataDir $Leg
    New-Item -ItemType Directory -Force $data | Out-Null
    $logDir = Join-Path $data 'Logs'
    if ($MarkerPath) { $env:NIXIE_UPGRADE_TEST = $MarkerPath } else { Remove-Item Env:NIXIE_UPGRADE_TEST -ErrorAction SilentlyContinue }
    $ui = $null
    try {
        $ui = Start-FluentFin $Exe @('--data-dir', ('"' + $data + '"'), '--install-update', ('"' + $msi + '"'))
        $hwnd = Wait-Until { $h = [OffscreenUi]::MainWindow($ui.Id); if ($h -ne [IntPtr]::Zero) { $h } } -Seconds $Seconds -PollMs 100
        Check "$($Leg): the window shows" ([bool]$hwnd) "FluentFin#$($ui.Id)"
        $said = Wait-Until { $l = @(Get-UpdateLines $logDir | Where-Object { $_ -match 'install-update' }); if ($l.Count) { $l } } -Seconds $Seconds -PollMs 250
        # past the moment a taken switch restarts to install (8 s after the start), with a margin
        Start-Sleep -Seconds 12
        $lines = @(Get-UpdateLines $logDir | Where-Object { $_ -match 'install-update' })
        $ui.Refresh()
        $running = -not $ui.HasExited
        $tree = @(Get-Descendants $ui.Id | Where-Object { $_.Name -in 'msiexec.exe', 'Nixie.exe' -and $_.ProcessId -ne $ui.Id })
        $gate = @($lines | Where-Object { $_ -match 'refused: a release package takes --install-update only in the upgrade test' })
        $ready = @($lines | Where-Object { $_ -match 'is ready; restarting to update' })
        $portable = @($lines | Where-Object { $_ -match "refused: this copy can't install updates" })
        $shown = ($lines | ForEach-Object { ($_ -replace '^.*?\[update\]', '[update]').Trim() }) -join ' | '
        if ($Leg -eq 'no-marker') {
            Check 'no-marker: the release package refuses --install-update without the marker (the log)' ($gate.Count -eq 1) $(if ($shown) { $shown } else { "no [update] line about it in $logDir" })
            Check 'no-marker: nothing is made ready to install' ($ready.Count -eq 0 -and $portable.Count -eq 0) $shown
        }
        else {
            Check 'marker: with the marker the gate lets it through (the portable copy refuses it later)' ($gate.Count -eq 0 -and $portable.Count -eq 1) $shown
            Check 'marker: nothing is made ready to install (a portable copy)' ($ready.Count -eq 0) $shown
        }
        Check "$($Leg): Nixie keeps running past the restart's moment, and starts no installer" ($running -and $tree.Count -eq 0) $(
            if (-not $running) { "exited with $($ui.ExitCode)" } elseif ($tree) { ($tree | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ', ' } else { 'running, no msiexec or helper' })
        if ($hwnd -and $running) {
            [OffscreenUi]::Close([IntPtr]$hwnd, $ui.Id)
            $close = [Diagnostics.Stopwatch]::StartNew()
            while ($close.Elapsed.TotalSeconds -lt 20 -and -not $ui.HasExited) { Start-Sleep -Milliseconds 100; $ui.Refresh() }
            Check "$($Leg): closes" $ui.HasExited "after $($close.ElapsedMilliseconds) ms"
        }
    }
    finally {
        Remove-Item Env:NIXIE_UPGRADE_TEST -ErrorAction SilentlyContinue
        if ($ui -and -not $ui.HasExited) { [void](Stop-Tracked $ui.Id) }
    }
}

try {
    Invoke-Leg 'no-marker' $null
    Invoke-Leg 'marker' $marker
    Check 'the decoy is untouched' ((Get-FileHash -Algorithm SHA256 $msi).Hash -eq $decoyBefore) $msi
}
catch { Check 'run' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage) }
finally {
    [IO.File]::WriteAllText((Join-Path $Out 'installswitch.json'), (ConvertTo-Json -Depth 5 -InputObject $checks), [Text.UTF8Encoding]::new($false))
    # anything left is ended by the job that runs this script
}
if (@($checks.Values | Where-Object { -not $_.ok }).Count) { exit 1 }
