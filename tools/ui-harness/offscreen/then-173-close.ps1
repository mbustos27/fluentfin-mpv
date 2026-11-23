# Nixie 1.7.3, stream C (the slow exit): closes Nixie from the player page and checks the close. Run after the player page is
# up, with drive.ps1's own close left out of the steps (this script closes):
#   run-offscreen.ps1 ... -Script season,play -NoA11y -Then offscreen\then-173-close.ps1
# 1.7.2's verify-package: a close from the player page (its NoMpv run) took 3.2 s on an idle PC, 8.1 s and over 20 s at 76% CPU,
# where a close from any other page took 0.2 to 1 s; and each close logged a first-chance NullReferenceException in
# TitleBarControl.ApplyCaptionHeight (the shell's Unloaded, after the window had gone).
# Checks: the process is gone within NIXIE_CLOSE_LIMIT_MS (5000 by default: verify-package allows 20 s, so a few seconds leave room
# for a loaded PC), and no first-chance NullReferenceException is logged from the close on. The app's [close] lines (each part's
# time from the close's start) go to drive.log. Dot-sourced by drive.ps1: every name here starts with cx.

$cxLimit = if ($env:NIXIE_CLOSE_LIMIT_MS) { [int]$env:NIXIE_CLOSE_LIMIT_MS } else { 5000 }
# 1.7.3 fix round's re-review: then-173-close-home.ps1 closes from Drive-in's Home through these checks ($cxFrom names the page)
if (-not $cxFrom) { $cxFrom = 'the player page' }
$cxPage = Find-Ui -Id 'MpvLeaveButton' -OffscreenToo
Say "close from $cxFrom (the player's leave button there: $([bool]$cxPage))"
$cxBegan = [DateTimeOffset]::Now
Step-Close 'close'
$cxMs = $timings['close_exit_ms']
Check "close from ${cxFrom}: Nixie exited within $cxLimit ms" ($null -ne $cxMs -and $cxMs -le $cxLimit) "$(if ($null -ne $cxMs) { "$cxMs ms" } else { 'not within the driver''s wait' })"
# 1.7.3: the slow close was a crash: 0xc000027b (a stowed exception, CoreMessagingXP.dll) once the NullReferenceException was
# thrown in the shell's Unloaded, then Windows Error Reporting's seconds before the process ended (every verify-package NoMpv
# close since 1.6.1 at least). A clean close exits with 0
$cxCode = if ($script:ui.HasExited) { $script:ui.ExitCode } else { $null }
Check "close from ${cxFrom}: a clean exit (code 0, not a crash)" ($cxCode -eq 0) "$(if ($null -ne $cxCode) { '0x{0:X8}' -f $cxCode } else { 'still running' })"

# the app's log from the close on: its [close] lines, and any NullReferenceException
$cxLines = [System.Collections.Generic.List[string]]::new()
foreach ($cxFile in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)) {
    $cxAfter = $false
    foreach ($cxLine in @(Get-Content -LiteralPath $cxFile.FullName -Encoding utf8)) {
        if ($cxLine -match '^(\S+ \S+ [+-]\d\d:\d\d) \[') {
            $cxWhen = [DateTimeOffset]::MinValue
            $cxAfter = [DateTimeOffset]::TryParse($Matches[1], [ref]$cxWhen) -and $cxWhen -ge $cxBegan.AddMilliseconds(-50)
        }
        if ($cxAfter) { $cxLines.Add($cxLine) }
    }
}
foreach ($cxLine in @($cxLines | Where-Object { $_ -match '\[close\]' })) { Say "app: $cxLine" }
$cxNre = @($cxLines | Where-Object { $_ -match 'NullReferenceException' })
Check "close from ${cxFrom}: no NullReferenceException at the close" ($cxNre.Count -eq 0) $(if ($cxNre) { ($cxNre | Select-Object -First 2) -join ' | ' } else { 'none' })
$cxExit = @($cxLines | Where-Object { $_ -match '\[close\] the process: exiting after (\d+) ms' })
Check "close from ${cxFrom}: the log has the close's last line (the process exiting)" ($cxExit.Count -eq 1) "$($cxExit.Count) line(s)"
