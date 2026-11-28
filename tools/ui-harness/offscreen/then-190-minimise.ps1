# Nixie 1.9 (stream text; the review of wip/190-text, item 8): background work pauses while Nixie's window is minimised (design
# rule 5). Minimises the window as the user's minimise does (ShowWindow SW_MINIMIZE), checks Nixie's log says the background
# gate paused for it, restores the window and checks the log says it goes again. Run with
# `-Script home -NoA11y -Then offscreen\then-190-minimise.ps1`. Dot-sourced by drive.ps1: every name here starts with tm.
# ASCII only.
if (-not ('TmWindow' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TmWindow {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@
}

function Tm-Lines([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content $_.FullName -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}
function Tm-At([string]$Line) {
    try { [DateTimeOffset]::ParseExact($Line.Substring(0, 30), 'yyyy-MM-dd HH:mm:ss.fff zzz', [Globalization.CultureInfo]::InvariantCulture) } catch { [DateTimeOffset]::MinValue }
}
function Tm-Wait([string]$Pattern, [DateTimeOffset]$Since, [int]$Seconds) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $l = @(Tm-Lines $Pattern | Where-Object { (Tm-At $_) -ge $Since })
        if ($l.Count -ge 1) { return $l }
        Start-Sleep -Milliseconds 250
    }
    @(Tm-Lines $Pattern | Where-Object { (Tm-At $_) -ge $Since })
}

Start-Sleep -Seconds 2
$tmBefore = [DateTimeOffset]::Now.AddMilliseconds(-50)
[void][TmWindow]::ShowWindow($script:hwnd, 6)
$tmIconic = [bool](Wait-Until { if ([TmWindow]::IsIconic($script:hwnd)) { $true } } -Seconds 4 -PollMs 100)
Check 'minimise: the window is minimised' $tmIconic
$tmPaused = Tm-Wait '\[background\] paused \(.*minimised' $tmBefore 10
Check 'minimise: the background work pauses (minimised)' ($tmPaused.Count -ge 1) ($tmPaused | Select-Object -First 1)
Start-Sleep -Milliseconds 1500 # minimised a while: the pause must hold until the restore
$tmEarly = @(Tm-Lines '\[background\] going again' | Where-Object { (Tm-At $_) -ge $tmBefore })
Check 'minimise: the pause holds while minimised' ([TmWindow]::IsIconic($script:hwnd) -and $tmEarly.Count -eq 0) ($tmEarly | Select-Object -First 1)

$tmRestored = [DateTimeOffset]::Now.AddMilliseconds(-50)
[void][TmWindow]::ShowWindow($script:hwnd, 9)
$tmShown = [bool](Wait-Until { if (-not [TmWindow]::IsIconic($script:hwnd)) { $true } } -Seconds 4 -PollMs 100)
Check 'minimise: the window is restored' $tmShown
$tmGoing = Tm-Wait '\[background\] going again' $tmRestored 10
Check 'minimise: the background work goes on once restored' ($tmGoing.Count -ge 1) ($tmGoing | Select-Object -First 1)
foreach ($tmLine in @(Tm-Lines '\[background\]')) { Say "BACKGROUND $tmLine" }
