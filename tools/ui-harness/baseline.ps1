<#
.SYNOPSIS
  Nixie 1.5, the release baseline: start-up time, Home's first paint and memory after a fixed browse, off-screen against
  the fake server, for one build or several side by side (docs\RELEASE.md, "Baseline").

.DESCRIPTION
  Runs run-offscreen.ps1 -Script home -NoA11y -Then offscreen\then-baseline.ps1 -Runs times per build, the builds taking
  turns (run 1 of each, then run 2 of each, ...), so a busy minute weighs on all of them alike. Per run: the window
  (process start to the main window, drive.ps1's window_ms; the app's own "[perf] startup: window shown" beside it),
  Home's first cards (from the server's sign-in answer), and Nixie.exe's private bytes after the browse (Home, Movies, a
  film, Back, a show, a season, Back, 5 s). Writes <Out>\baseline-rows.json and <Out>\baseline.md (the medians).
  Measure with nothing else running: the harness's other runs, a build or a test suite skew every number.

.EXAMPLE
  .\baseline.ps1 -Out C:\scratch\bl -Exe C:\b141\Nixie.exe, C:\b150\Nixie.exe -Backend C:\src141\backend, .\backend -Label 1.4.1, 1.5.0
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string[]]$Exe,
    # each build's backend checkout (default this repo's backend\); an older build runs with its own
    [string[]]$Backend = @(),
    [string[]]$Label = @(),
    [int]$Runs = 5,
    [string]$Python,
    [string]$Cache
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null
if (-not $Python) { $Python = Join-Path $repo 'backend\.venv\Scripts\python.exe' }
$builds = for ($i = 0; $i -lt $Exe.Count; $i++) {
    [pscustomobject]@{
        Label = $(if ($i -lt $Label.Count) { $Label[$i] } else { "build$($i + 1)" })
        Exe = [IO.Path]::GetFullPath($Exe[$i])
        Backend = $(if ($i -lt $Backend.Count -and $Backend[$i]) { [IO.Path]::GetFullPath($Backend[$i]) } else { Join-Path $repo 'backend' })
    }
}
# the default layout theme (the one a new user gets)
Remove-Item Env:NIXIE_LAYOUT -ErrorAction SilentlyContinue
$rows = @()
for ($n = 1; $n -le $Runs; $n++) {
    foreach ($b in $builds) {
        $name = '{0}-{1}' -f (($b.Label -replace '[^A-Za-z0-9.]+', '-')), $n
        $run = Join-Path $Out "run-$name"; $data = Join-Path $Out "data-$name"
        foreach ($d in @($run, $data)) { if (Test-Path $d) { Remove-Item -Recurse -Force $d } }
        $a = @{ DataDir = $data; Out = $run; Exe = $b.Exe; Backend = $b.Backend; Python = $Python; Script = @('home'); NoA11y = $true
            Then = (Join-Path $PSScriptRoot 'offscreen\then-baseline.ps1') }
        if ($Cache) { $a.Cache = $Cache }
        & (Join-Path $PSScriptRoot 'run-offscreen.ps1') @a *> (Join-Path $Out "run-$name.console.txt")
        $code = $LASTEXITCODE
        $j = $null; try { $j = Get-Content (Join-Path $run 'baseline.json') -Raw -Encoding utf8 | ConvertFrom-Json } catch {}
        $row = [pscustomobject]@{ build = $b.Label; run = $n; exit = $code; window_ms = $j.window_ms; app_window_shown_ms = $j.perf_window_shown
            home_first_cards_ms = $j.home_first_cards_ms; private_mb = $j.private_mb; working_set_mb = $j.working_set_mb }
        $rows += $row
        Write-Host ("{0} run {1}: exit {2}, window {3} ms (app {4}), Home's first cards {5} ms, private {6} MB" -f $b.Label, $n, $code, $row.window_ms, $row.app_window_shown_ms, $row.home_first_cards_ms, $row.private_mb)
    }
}
[IO.File]::WriteAllText((Join-Path $Out 'baseline-rows.json'), ($rows | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
function Get-Median($Values) {
    $s = @($Values | Where-Object { $null -ne $_ } | ForEach-Object { [double]$_ } | Sort-Object)
    if (-not $s.Count) { return $null }
    if ($s.Count % 2) { $s[($s.Count - 1) / 2] } else { ($s[$s.Count / 2 - 1] + $s[$s.Count / 2]) / 2 }
}
$md = @('| Build | Runs | Window (ms) | App: window shown (ms) | Home first cards (ms) | Private bytes (MB) | Working set (MB) |', '|---|---|---|---|---|---|---|')
foreach ($b in $builds) {
    $r = @($rows | Where-Object { $_.build -eq $b.Label -and $_.exit -eq 0 })
    $md += '| {0} | {1} | {2} | {3} | {4} | {5} | {6} |' -f $b.Label, $r.Count, (Get-Median $r.window_ms), (Get-Median $r.app_window_shown_ms), (Get-Median $r.home_first_cards_ms), (Get-Median $r.private_mb), (Get-Median $r.working_set_mb)
}
[IO.File]::WriteAllText((Join-Path $Out 'baseline.md'), ($md -join "`r`n") + "`r`n", [Text.UTF8Encoding]::new($false))
$md | Write-Host
if (@($rows | Where-Object { $_.exit -ne 0 }).Count) { exit 1 }
