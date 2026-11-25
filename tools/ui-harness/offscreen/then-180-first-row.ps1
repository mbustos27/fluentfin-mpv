# Nixie 1.8 (P1-1, stream l; the coordinator's Major: Home must not get slower): Home's first Made by Nixie row, timed from
# Home's own log lines, on a first start with nothing saved (FAKESERVER_BIG_LIBRARY=25000) and on the next start (Home's saved
# page, the index saved on this PC). Run with `-Script home -NoA11y -Then offscreen\then-180-first-row.ps1`. Checks the first
# start's whole-read fill, the people step's end, the relaunch and the next start's fill; says the TIMING lines (durations
# and counts only). Dot-sourced by drive.ps1: every name here starts with fr. ASCII only.
function Fr-Lines([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content $_.FullName -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}
function Fr-At([string]$Line) {
    try { [DateTimeOffset]::ParseExact($Line.Substring(0, 30), 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture) } catch { [DateTimeOffset]::MinValue }
}
function Fr-Wait([string]$Pattern, [DateTimeOffset]$Since, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $l = @(Fr-Lines $Pattern | Where-Object { (Fr-At $_) -ge $Since })
        if ($l.Count -ge 1) { return $l }
        Start-Sleep -Milliseconds 500
    }
    @(Fr-Lines $Pattern | Where-Object { (Fr-At $_) -ge $Since })
}

$frStart = [DateTimeOffset]::MinValue
# 1. the first start: the whole read's fill (both builds say it)
$frWhole = Fr-Wait 'Home: \d+ Made by Nixie rows in \d+ ms from the index read' $frStart 420
Check 'first row: the first start fills Home from the whole read' ($frWhole.Count -ge 1) ($frWhole | Select-Object -First 1)
# its people step, then Home saved again (the next start's saved page and index)
$frPeople = Fr-Wait 'Library index: the people (are in|didn.t all come)' $frStart 300
Check 'first row: the people step ended' ($frPeople.Count -ge 1) ($frPeople | Select-Object -First 1)
Start-Sleep -Seconds 4
foreach ($l in @(Fr-Lines 'Home: (its first Made by Nixie row|\d+ Made by Nixie rows|the week.s Spotlight)|Made by Nixie, after the first rows|Library index: \d+ titles (in|made)|Made by Nixie census:|Made by Nixie lineup:')) { Say "TIMING first-start $l" }

# 2. the next start, on the same data folder
$frAgain = [DateTimeOffset]::Now
Step-Relaunch
$frNext = Fr-Wait 'Home: \d+ Made by Nixie rows in \d+ ms from (the index read|the saved index)' $frAgain 180
Check 'first row: the next start fills Home behind (the saved index)' ($frNext.Count -ge 1) ($frNext | Select-Object -First 1)
Start-Sleep -Seconds 2
Shot 'next-start-home'
foreach ($l in @(Fr-Lines 'Home: (its first Made by Nixie row|\d+ Made by Nixie rows|the week.s Spotlight)|Made by Nixie, after the first rows|Library index: (the saved one|\d+ titles (in|made))|Made by Nixie lineup:' | Where-Object { (Fr-At $_) -ge $frAgain })) { Say "TIMING next-start $l" }
