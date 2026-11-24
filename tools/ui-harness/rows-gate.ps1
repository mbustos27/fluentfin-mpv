<#
.SYNOPSIS
  Nixie 1.7.4 (the basics gate's rows): every horizontal row reaches its last item with every input, in all four layout
  themes, at 2560 x 1392 (maximised), 1920 x 1080 and 1280 x 720, off-screen; and the grid of row x input x pass or fail.

.DESCRIPTION
  For each theme (NIXIE_LAYOUT) runs run-offscreen.ps1 -Script wait:1 -Then offscreen\then-174-rows.ps1 against the fake
  server with FAKESERVER_BIG_SERIES=24x13 (13-episode seasons, 24 of them: a long show's season strip, 16 people with long
  names) and
  FAKESERVER_COLLECTIONS=1 (Made by Nixie's rows): the season page, its cast's names, the series page, Home, Collections,
  the showcase film, the Movies library, search results and the top bar's lit tab, at each size (RG_SIZES). Each run's rows
  are in <Out>\<theme>\rows174\rows174.json; the grid goes to -Table (default <Out>\rows174.md): one line per page, row and
  input, one column per theme and size. A failed cell fails the gate (exit 1). Silent (ao=null) and off-screen like every
  run-offscreen.ps1 run; nothing is typed into a real server.
  -Parallel starts the themes at once, each its own process (run-offscreen.ps1's slots, NIXIE_HARNESS_PARALLEL, decide how
  many go together); it needs -Exe.

.EXAMPLE
  .\rows-gate.ps1 -Out C:\scratch\rows -Exe C:\scratch\build\bin\FluentFin\debug\Nixie.exe -Python C:\...\python.exe -Parallel
  .\rows-gate.ps1 -Out C:\scratch\rows -Exe ... -Themes Cinematic -Sizes max -Pages season,cast
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe,
    [switch]$Build,
    [string]$Python,
    [string]$Cache,
    [string[]]$Themes = @('Classic', 'DriveIn', 'Cinematic', 'Editorial'),
    [string]$Sizes = 'max,1920x1080,1280x720',
    [string]$Pages = 'season,cast,series,home,collections,movie,library,search,topbar',
    # auto: every input at the first size, the quick set (no Tab, shoulder or Shift+wheel) at the others
    [ValidateSet('auto', 'full', 'quick')][string]$Depth = 'auto',
    # the first N rows of each page (0: all); every row is the one ShelfRow (or EdgeScroller strip), so two per page cover
    # each page's kinds of row and keep a theme's run near half an hour
    [int]$RowsPerPage = 2,
    [string]$Table,
    [int]$TimeoutSeconds = 5400,
    [switch]$Parallel
)

$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
if (-not $Table) { $Table = Join-Path $Out 'rows174.md' }
New-Item -ItemType Directory -Force $Out, (Split-Path $Table) | Out-Null
$Themes = @($Themes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$runner = Join-Path $PSScriptRoot 'run-offscreen.ps1'
$then = Join-Path $PSScriptRoot 'offscreen\then-174-rows.ps1'
if ($Parallel -and -not $Exe) { throw "-Parallel runs the themes at once: build first (run-offscreen.ps1 -Build) and pass -Exe" }

function Read-Rows([string]$File) { if (Test-Path $File) { @(Get-Content $File -Raw -Encoding utf8 | ConvertFrom-Json | ForEach-Object { $_ }) } else { @() } }

$env:FAKESERVER_BIG_SERIES = '24x13'
$env:FAKESERVER_COLLECTIONS = '1'
$env:RG_SIZES = $Sizes; $env:RG_PAGES = $Pages; $env:RG_DEPTH = $Depth; $env:RG_ROWS_PER_PAGE = [string]$RowsPerPage
$results = [ordered]@{}
$going = [ordered]@{}
$built = $false
foreach ($theme in $Themes) {
    $env:NIXIE_LAYOUT = $theme
    $runOut = Join-Path $Out $theme
    $data = Join-Path $Out "data-$theme"
    foreach ($dir in @($runOut, $data)) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
    $runArgs = [ordered]@{ DataDir = $data; Out = $runOut; Script = 'wait:1'; Then = $then; TimeoutSeconds = $TimeoutSeconds
        QueueMinutes = [Math]::Max(30, [int]($TimeoutSeconds / 60) * $Themes.Count) }
    if ($Exe) { $runArgs.Exe = $Exe } elseif ($Build -and -not $built) { $runArgs.Build = $true }
    if ($Python) { $runArgs.Python = $Python }
    if ($Cache) { $runArgs.Cache = $Cache }
    Write-Host "== $theme -> $runOut"
    if ($Parallel) {
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $runner + '"'), '-NoA11y')
        foreach ($k in $runArgs.Keys) { $argList += "-$k"; $argList += ('"' + [string]$runArgs[$k] + '"') }
        $going[$theme] = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $argList -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $Out "$theme.console.txt") -RedirectStandardError (Join-Path $Out "$theme.console-err.txt")
        $null = $going[$theme].Handle
        continue
    }
    & $runner @runArgs -NoA11y | Out-Host
    $built = $true
    $results[$theme] = Read-Rows (Join-Path $runOut 'rows174\rows174.json')
}
foreach ($name in 'NIXIE_LAYOUT', 'FAKESERVER_BIG_SERIES', 'FAKESERVER_COLLECTIONS', 'RG_SIZES', 'RG_PAGES', 'RG_DEPTH', 'RG_ROWS_PER_PAGE') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
foreach ($theme in @($going.Keys)) {
    $going[$theme].WaitForExit()
    Write-Host "== $theme ended (exit $($going[$theme].ExitCode)); its console: $(Join-Path $Out "$theme.console.txt")"
    $results[$theme] = Read-Rows (Join-Path (Join-Path $Out $theme) 'rows174\rows174.json')
}
$ordered = [ordered]@{}; foreach ($theme in $Themes) { if ($results.Contains($theme)) { $ordered[$theme] = $results[$theme] } }; $results = $ordered

# the grid: one line per (page, row, input), one column per theme and size; a cell is pass, page (the wheel scrolled the page,
# as it should while the page can move), fits (the row's last item shows at rest), n/t (not testable off-screen), FAIL, or -
$sizeList = @($Sizes -split ',' | Where-Object { $_ })
$columns = @(foreach ($theme in $results.Keys) { foreach ($size in $sizeList) { "$theme $size" } })
$order = New-Object System.Collections.Generic.List[string]
$cells = @{}
foreach ($theme in $results.Keys) {
    foreach ($row in $results[$theme]) {
        $id = "{0} | {1} | {2}" -f $row.page, $row.row, $row.input
        if (-not $order.Contains($id)) { $order.Add($id) }
        $cells["$theme $($row.size)`t$id"] = $row
    }
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Nixie 1.7.4: horizontal rows x inputs')
$lines.Add('')
$lines.Add("Run $(Get-Date -Format 'yyyy-MM-dd HH:mm') off-screen against the fake server (tools\ui-harness\rows-gate.ps1; offscreen\then-174-rows.ps1 says what")
$lines.Add('each input is and how it is checked). A cell: pass, page (the plain wheel scrolled the page, which could still move), fits (the')
$lines.Add("row's last item shows at rest), n/t (not testable off-screen), FAIL, or - (the row is not on that page there).")
$lines.Add('')
$lines.Add('| Page | Row | Input | ' + ($columns -join ' | ') + ' |')
$lines.Add('|---|---|---|' + (($columns | ForEach-Object { '---' }) -join '|') + '|')
foreach ($id in $order) {
    $parts = $id -split ' \| ', 3
    $row = foreach ($col in $columns) { $c = $cells["$col`t$id"]; if (-not $c) { '-' } elseif ($c.ok) { $c.verdict } else { '**FAIL**' } }
    $lines.Add(('| {0} | {1} | {2} | {3} |' -f $parts[0], $parts[1], $parts[2], ($row -join ' | ')))
}
$failures = foreach ($theme in $results.Keys) { foreach ($row in $results[$theme] | Where-Object { -not $_.ok }) { "- $theme $($row.size) | $($row.page) | $($row.row) | $($row.input): $($row.detail)" } }
if ($failures) { $lines.Add(''); $lines.Add('## Failures'); $lines.Add(''); foreach ($f in $failures) { $lines.Add($f) } }
[IO.File]::WriteAllLines($Table, [string[]]$lines, [Text.UTF8Encoding]::new($false))
Write-Host "grid: $Table"
foreach ($theme in $results.Keys) { $r = $results[$theme]; Write-Host ("  {0,-10} {1}/{2} row checks passed" -f $theme, @($r | Where-Object ok).Count, $r.Count) }
if (@($failures).Count -or @($results.Values | Where-Object { $_.Count -eq 0 }).Count -or $results.Count -lt $Themes.Count) { exit 1 }
