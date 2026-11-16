<#
  The mpv self-test of a packaged FluentFin.exe, off-screen: FluentFin.exe --mpv-selftest --data-dir <DataDir> (no server;
  the backend plays mpv's test pattern). Runs ON a private desktop, started by verify-package.ps1 through
  tools\backend-bench\hidden_run.py inside its kill-on-close job; refuses the input desktop. No key or click is sent.
  Checks: the window shows, the backend took play_file, the video area shows the test pattern (a window-only capture
  with many colours), and a normal close (WM_CLOSE) ends FluentFin and everything it started.
  Writes <Out>\selftest.png and <Out>\selftest.json (the checks).
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Out,
    [int]$Seconds = 60,
    [int]$CloseSeconds = 20,
    [switch]$Kill   # end FluentFin abruptly instead of closing it: its job must take the backend with it
)

$ErrorActionPreference = 'Stop'
$DataDir = $DataDir.TrimEnd('\', '/')
. (Join-Path $PSScriptRoot '..\ui-harness\common.ps1')
. (Join-Path $PSScriptRoot '..\ui-harness\offscreen\uia.ps1')
$script:LogDir = Join-Path $DataDir 'Logs'

$desktop = [OffscreenUi]::ThisDesktop()
if (-not $desktop -or $desktop -eq 'Default' -or $desktop -eq [OffscreenUi]::InputDesktop()) { throw "runs only on a private desktop (this one: '$desktop')" }

New-Item -ItemType Directory -Force $Out | Out-Null
$checks = [ordered]@{}
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $checks[$Name] = [ordered]@{ ok = $Ok; detail = $Detail }
    "{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" })
}
function Get-Colours([string]$Png) {
    Add-Type -AssemblyName System.Drawing
    $bmp = [Drawing.Bitmap]::FromFile($Png)
    try {
        $seen = @{}
        for ($y = [int]($bmp.Height * 0.15); $y -lt [int]($bmp.Height * 0.85); $y += 6) {
            for ($x = [int]($bmp.Width * 0.1); $x -lt [int]($bmp.Width * 0.9); $x += 6) {
                $c = $bmp.GetPixel($x, $y); $seen[('{0}-{1}-{2}' -f ($c.R -shr 4), ($c.G -shr 4), ($c.B -shr 4))] = 1
            }
        }
        $seen.Count
    }
    finally { $bmp.Dispose() }
}

$ui = $null
try {
    $ui = Start-FluentFin $Exe @('--mpv-selftest', '--data-dir', ('"' + $DataDir + '"'))
    $hwnd = Wait-Until { $h = [OffscreenUi]::MainWindow($ui.Id); if ($h -ne [IntPtr]::Zero) { $h } } -Seconds 60 -PollMs 50
    Check 'window shown' ([bool]$hwnd) "FluentFin#$($ui.Id)"
    if (-not $hwnd) { return }
    $hwnd = [IntPtr]$hwnd
    $backendLog = Join-Path $script:LogDir 'mpv-backend.log'
    $took = Wait-Until { if ((Test-Path $backendLog) -and (Select-String -Path $backendLog -Pattern '<- play_file' -Quiet)) { $true } } -Seconds $Seconds -PollMs 250
    Check 'backend took play_file' ([bool]$took) $backendLog
    # the pattern moves; give it a few seconds on screen, then capture until it shows (the first frame can take a moment)
    $png = Join-Path $Out 'selftest.png'
    $colours = 0
    $at = [Diagnostics.Stopwatch]::StartNew()
    while ($at.Elapsed.TotalSeconds -lt 20 -and $colours -lt 40) {
        Start-Sleep -Seconds 2
        [void][OffscreenUi]::Shot($hwnd, $ui.Id, $png)
        $colours = Get-Colours $png
    }
    Check 'the test pattern is on screen (window capture)' ($colours -ge 40) "$colours colours in the middle of the window; $png"
    $tree = @(Get-Descendants $ui.Id)
    $how = if ($Kill) { 'kill' } else { 'close' }
    $close = [Diagnostics.Stopwatch]::StartNew()
    if ($Kill) {
        # a crash, as far as the backend can tell: FluentFin ended at once through the handle this script started it with
        # (common.ps1's Stop-Tracked); only the kill-on-close job FluentFin put its backend in can end the backend now
        Check 'kill: the bundled backend was running' ([bool]($tree | Where-Object Name -eq 'fluentfin-mpv-backend.exe')) (($tree | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',')
        [void](Stop-Tracked $ui.Id)
    }
    else { [OffscreenUi]::Close($hwnd, $ui.Id) }
    while ($close.Elapsed.TotalSeconds -lt $CloseSeconds -and -not $ui.HasExited) { Start-Sleep -Milliseconds 50; $ui.Refresh() }
    Check "$($how): FluentFin exited" $ui.HasExited "after $($close.ElapsedMilliseconds) ms"
    Start-Sleep -Milliseconds 800
    $alive = @($tree | Where-Object { Get-TrackedProcess ([int]$_.ProcessId) })
    Check "$($how): nothing FluentFin started survives" ($alive.Count -eq 0) $(if ($alive) { ($alive | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',' } else { "none of: $(($tree | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',')" })
}
catch { Check 'run' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage) }
finally {
    [IO.File]::WriteAllText((Join-Path $Out 'selftest.json'), (ConvertTo-Json -Depth 5 -InputObject $checks), [Text.UTF8Encoding]::new($false))
    # anything left is ended by the job that runs this script
}
if (@($checks.Values | Where-Object { -not $_.ok }).Count) { exit 1 }
