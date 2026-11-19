<#
.SYNOPSIS
  Nixie 1.3.1: the keyboard matrix in all four layout themes, off-screen, and its pass/fail table.

.DESCRIPTION
  For each theme (NIXIE_LAYOUT Classic, DriveIn, Cinematic, Editorial) runs run-offscreen.ps1 -Script home -Then
  offscreen\then-keys-matrix.ps1 against the fake server: the browsing pages' keys (Home, Library, Movie, Series, Season,
  Episode, Search results, Diary), then the player's: every documented key playing and paused, the bars shown and hidden,
  in full screen, with the keyboard on each control, with each panel open, after each kind of click (the user's report of
  2026-11-18), double clicks, the Back key, and a theme switch mid-session. Each run's rows are in
  <Out>\<theme>\keys-matrix\keys-matrix.json; the table, one column per theme, goes to -Table (default
  <repo>\shots\keys-matrix.md, not committed). Silent (ao=null) and off-screen like every run-offscreen.ps1 run.

.EXAMPLE
  .\keys-matrix.ps1 -Out C:\scratch\keys -Exe C:\scratch\build\bin\FluentFin\debug\Nixie.exe -Python C:\...\python.exe
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe,
    [switch]$Build,
    [string]$Python,
    [string]$Cache,
    [string[]]$Themes = @('Classic', 'DriveIn', 'Cinematic', 'Editorial'),
    [string]$Table,
    # themes whose rows an earlier run left in -Out are read, not run again (the table still has all of -Themes)
    [string[]]$Reuse = @(),
    [int]$TimeoutSeconds = 2400
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
if (-not $Table) { $Table = Join-Path $repo 'shots\keys-matrix.md' }
New-Item -ItemType Directory -Force $Out, (Split-Path $Table) | Out-Null
$Themes = @($Themes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$Reuse = @($Reuse | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$runner = Join-Path $PSScriptRoot 'run-offscreen.ps1'
$then = Join-Path $PSScriptRoot 'offscreen\then-keys-matrix.ps1'

# a run's rows (Windows PowerShell's ConvertFrom-Json writes a JSON array as one object: enumerated here)
function Read-Rows([string]$File) { if (Test-Path $File) { @(Get-Content $File -Raw -Encoding utf8 | ConvertFrom-Json | ForEach-Object { $_ }) } else { @() } }

$results = [ordered]@{}
$built = $false
foreach ($theme in $Themes) {
    $env:NIXIE_LAYOUT = $theme
    $runOut = Join-Path $Out $theme
    $kept = Join-Path $runOut 'keys-matrix\keys-matrix.json'
    if ($Reuse -contains $theme -and (Test-Path $kept)) {
        Write-Host "== ${theme}: the rows of the earlier run ($kept)"
        $results[$theme] = Read-Rows $kept
        continue
    }
    $data = Join-Path $Out "data-$theme"
    foreach ($dir in @($runOut, $data)) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
    $runArgs = @{ DataDir = $data; Out = $runOut; Script = 'home'; Then = $then; NoA11y = $true; TimeoutSeconds = $TimeoutSeconds }
    if ($Exe) { $runArgs.Exe = $Exe } elseif ($Build -and -not $built) { $runArgs.Build = $true }
    if ($Python) { $runArgs.Python = $Python }
    if ($Cache) { $runArgs.Cache = $Cache }
    Write-Host "== $theme -> $runOut"
    & $runner @runArgs | Out-Host
    $built = $true
    $rowsFile = Join-Path $runOut 'keys-matrix\keys-matrix.json'
    $results[$theme] = Read-Rows $rowsFile
}
Remove-Item Env:NIXIE_LAYOUT -ErrorAction SilentlyContinue

# the table: one line per (section, case, key) in the order first seen, one column per theme; a cell is PASS, FAIL, or
# PASS n/m when the same check ran more than once (a key pressed twice in a case)
$order = New-Object System.Collections.Generic.List[string]
$cells = @{}
foreach ($theme in $results.Keys) {
    foreach ($row in $results[$theme]) {
        $id = "{0} | {1} | {2}" -f $row.section, ($row.case -replace '\s*\(focus: [^)]*\)', ''), $row.key
        if (-not $order.Contains($id)) { $order.Add($id) }
        $k = "$theme`t$id"
        if (-not $cells.ContainsKey($k)) { $cells[$k] = @(0, 0, '') }
        $c = $cells[$k]; $c[1]++; if ($row.ok) { $c[0]++ } elseif (-not $c[2]) { $c[2] = $row.detail }
        $cells[$k] = $c
    }
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Nixie 1.3.1 keyboard matrix')
$lines.Add('')
$lines.Add("Run $(Get-Date -Format 'yyyy-MM-dd HH:mm') off-screen against the fake server (tools\ui-harness\keys-matrix.ps1). Keys go to the window with")
$lines.Add("FluentFin's keyboard focus, as a real keyboard's do; clicks through the dev build's pointer hook; results read from mpv's own state,")
$lines.Add('the window and UI Automation. A cell is the checks that passed of those run.')
$lines.Add('')
$lines.Add('| Totals | ' + (($results.Keys | ForEach-Object { $_ }) -join ' | ') + ' |')
$lines.Add('|---|' + (($results.Keys | ForEach-Object { '---' }) -join '|') + '|')
$lines.Add('| rows passed | ' + (($results.Keys | ForEach-Object { $r = $results[$_]; "$(@($r | Where-Object ok).Count) / $($r.Count)" }) -join ' | ') + ' |')
$lines.Add('')
$lines.Add('| Section | Case | Key | ' + (($results.Keys | ForEach-Object { $_ }) -join ' | ') + ' |')
$lines.Add('|---|---|---|' + (($results.Keys | ForEach-Object { '---' }) -join '|') + '|')
foreach ($id in $order) {
    $parts = $id -split ' \| ', 3
    $row = foreach ($theme in $results.Keys) {
        $c = $cells["$theme`t$id"]
        if (-not $c) { '-' } elseif ($c[0] -eq $c[1]) { if ($c[1] -gt 1) { "PASS $($c[0])/$($c[1])" } else { 'PASS' } } else { "**FAIL** $($c[0])/$($c[1])" }
    }
    $lines.Add(('| {0} | {1} | {2} | {3} |' -f $parts[0], $parts[1], ($parts[2] -replace '\|', '\|'), ($row -join ' | ')))
}
$failures = foreach ($theme in $results.Keys) { foreach ($row in $results[$theme] | Where-Object { -not $_.ok }) { "- $theme | $($row.section) | $($row.case) | $($row.key): $($row.detail)" } }
if ($failures) { $lines.Add(''); $lines.Add('## Failures'); $lines.Add(''); foreach ($f in $failures) { $lines.Add($f) } }
[IO.File]::WriteAllLines($Table, [string[]]$lines, [Text.UTF8Encoding]::new($false))
Write-Host "table: $Table"
foreach ($theme in $results.Keys) { $r = $results[$theme]; Write-Host ("  {0,-10} {1}/{2} rows passed" -f $theme, @($r | Where-Object ok).Count, $r.Count) }
if (@($failures).Count -or @($results.Values | Where-Object { $_.Count -eq 0 }).Count) { exit 1 }
