<#
.SYNOPSIS
  Nixie 1.7.4 (the basics gate's Back): Back to a page just left shows it as it was, at once, in all four layout themes, against
  the fake server with every answer held (so a reload shows); and the grid of page x theme.

.DESCRIPTION
  For each theme (NIXIE_LAYOUT) runs run-offscreen.ps1 -Script wait:1 -Then offscreen\then-174-back.ps1 against the fake server
  with FAKESERVER_COLLECTIONS=1 and FAKESERVER_LONG_TITLE=1 (box sets, a 24-film set, playlists, Made by Nixie),
  FAKESERVER_BIG_SERIES=24x13 (13-episode seasons) and FAKESERVER_SEARCH=sunny (a person in search). Each page: a card down it
  opened, every answer held -DelayMs, Back: how long until the card is on screen again, whether a loading state showed, the
  scroll and the focus against what they were, the requests sent after Back (then-174-back.ps1 says how each is checked). Also
  "change" (a film marked watched on its page shows so on its collection's card after Back), "refresh" (R still asks the server)
  and "signout" (a sign-out lets go of every kept page). Each run's rows are in <Out>\<theme>\back174\back174.json; the grid goes
  to -Table (default <Out>\back174.md). A failed cell fails the gate (exit 1). Silent and off-screen like every run-offscreen.ps1
  run; nothing is typed into a real server.
  -Parallel starts the themes at once, each its own process (run-offscreen.ps1's slots, NIXIE_HARNESS_PARALLEL, decide how many
  go together); it needs -Exe.

.EXAMPLE
  .\back-gate.ps1 -Out C:\scratch\back -Exe C:\scratch\build\bin\FluentFin\debug\Nixie.exe -Python C:\...\python.exe -Parallel
  .\back-gate.ps1 -Out C:\scratch\back -Exe ... -Themes Cinematic -Pages collection,change
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe,
    [switch]$Build,
    [string]$Python,
    [string]$Cache,
    [string[]]$Themes = @('Classic', 'DriveIn', 'Cinematic', 'Editorial'),
    [string]$Pages = 'collection,forward,playlist,nixie,library,season,series,item,search,person,collections,home,samelibrary,samesearch,searchlist,change,refresh,signout',
    [int]$DelayMs = 2500,
    [int]$LimitMs = 1200,
    [string]$Table,
    [string]$Label = '',
    [int]$TimeoutSeconds = 1800,
    [switch]$Parallel
)

$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
if (-not $Table) { $Table = Join-Path $Out 'back174.md' }
New-Item -ItemType Directory -Force $Out, (Split-Path $Table) | Out-Null
$Themes = @($Themes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$runner = Join-Path $PSScriptRoot 'run-offscreen.ps1'
$then = Join-Path $PSScriptRoot 'offscreen\then-174-back.ps1'
if ($Parallel -and -not $Exe) { throw "-Parallel runs the themes at once: build first (run-offscreen.ps1 -Build) and pass -Exe" }

function Read-Rows([string]$File) { if (Test-Path $File) { @(Get-Content $File -Raw -Encoding utf8 | ConvertFrom-Json | ForEach-Object { $_ }) } else { @() } }

$env:FAKESERVER_COLLECTIONS = '1'; $env:FAKESERVER_LONG_TITLE = '1'; $env:FAKESERVER_BIG_SERIES = '24x13'; $env:FAKESERVER_SEARCH = 'sunny'
$env:BK_PAGES = $Pages; $env:BK_DELAY_MS = [string]$DelayMs; $env:BK_LIMIT_MS = [string]$LimitMs
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
    $results[$theme] = Read-Rows (Join-Path $runOut 'back174\back174.json')
}
foreach ($name in 'NIXIE_LAYOUT', 'FAKESERVER_COLLECTIONS', 'FAKESERVER_LONG_TITLE', 'FAKESERVER_BIG_SERIES', 'FAKESERVER_SEARCH', 'BK_PAGES', 'BK_DELAY_MS', 'BK_LIMIT_MS') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
foreach ($theme in @($going.Keys)) {
    $going[$theme].WaitForExit()
    Write-Host "== $theme ended (exit $($going[$theme].ExitCode)); its console: $(Join-Path $Out "$theme.console.txt")"
    $results[$theme] = Read-Rows (Join-Path (Join-Path $Out $theme) 'back174\back174.json')
}
$ordered = [ordered]@{}; foreach ($theme in $Themes) { if ($results.Contains($theme)) { $ordered[$theme] = $results[$theme] } }; $results = $ordered

# the grid: one line per page, one column per theme; a cell is the time to the card left from (ms), then what fell short:
# "loading" (a loading state showed), "scroll" (not where it was), "focus" (not on the card left from); FAIL when it failed
$order = @($Pages -split ',' | Where-Object { $_ })
function Cell($Row) {
    if (-not $Row) { return '-' }
    $words = New-Object System.Collections.Generic.List[string]
    if ($Row.PSObject.Properties['ms'] -and $Row.ms -ne $null) { $words.Add("$($Row.ms) ms") }
    # 1.8.1: the app's own time to its place (its [perf] back line), when it said one: a cell over the limit whose app time is
    # well under it is the check's reading under load
    if ($Row.PSObject.Properties['app_place_ms'] -and $Row.app_place_ms -ne $null) { $words.Add("app $($Row.app_place_ms) ms") }
    if ($Row.PSObject.Properties['watched_ms'] -and $Row.watched_ms -ne $null) { $words.Add("watched $($Row.watched_ms) ms") }
    if ($Row.PSObject.Properties['loading'] -and $Row.loading) { $words.Add('loading') }
    if ($Row.PSObject.Properties['scroll_kept'] -and -not $Row.scroll_kept) { $words.Add('scroll') }
    if ($Row.PSObject.Properties['focus_kept'] -and -not $Row.focus_kept) { $words.Add('focus') }
    if ($Row.PSObject.Properties['requests'] -and $Row.requests) { $words.Add("$($Row.requests) req") }
    $text = if ($words.Count) { $words -join ', ' } else { 'ran' }
    if ($Row.ok) { $text } else { "**FAIL** $text" }
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# Nixie 1.7.4: Back to a page just left$(if ($Label) { " ($Label)" })")
$lines.Add('')
$lines.Add("Run $(Get-Date -Format 'yyyy-MM-dd HH:mm') off-screen against the fake server, every answer held $DelayMs ms after Back (tools\ui-harness\back-gate.ps1;")
$lines.Add("offscreen\then-174-back.ps1 says how each is checked). A cell: the time from Back to the card left from on screen again (limit $LimitMs ms),")
$lines.Add('then the app''s own time to its place (app N ms, its [perf] back line), what fell short: loading (a loading state showed), scroll, focus (not on the card left from); the requests sent after Back.')
$lines.Add('')
$lines.Add('| Page | ' + (@($results.Keys) -join ' | ') + ' |')
$lines.Add('|---|' + ((@($results.Keys) | ForEach-Object { '---' }) -join '|') + '|')
foreach ($page in $order) {
    $cells = foreach ($theme in $results.Keys) { Cell (@($results[$theme] | Where-Object { $_.page -eq $page }) | Select-Object -First 1) }
    $lines.Add(('| {0} | {1} |' -f $page, ($cells -join ' | ')))
}
$failures = foreach ($theme in $results.Keys) { foreach ($row in $results[$theme] | Where-Object { -not $_.ok }) { "- $theme | $($row.page): $($row.detail)" } }
if ($failures) { $lines.Add(''); $lines.Add('## Failures'); $lines.Add(''); foreach ($f in $failures) { $lines.Add($f) } }
[IO.File]::WriteAllLines($Table, [string[]]$lines, [Text.UTF8Encoding]::new($false))
Write-Host "grid: $Table"
foreach ($theme in $results.Keys) { $r = $results[$theme]; Write-Host ("  {0,-10} {1}/{2} Back checks passed" -f $theme, @($r | Where-Object ok).Count, $r.Count) }
if (@($failures).Count -or @($results.Values | Where-Object { $_.Count -eq 0 }).Count -or $results.Count -lt $Themes.Count) { exit 1 }
