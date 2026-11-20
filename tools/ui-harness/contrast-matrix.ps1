<#
.SYNOPSIS
  Nixie 1.4.0 (the library review's plan item 3): the contrast floor in all four layout themes, Light and Dark, over the
  worst-case artwork, off-screen, and its table.

.DESCRIPTION
  For each layout theme (NIXIE_LAYOUT Classic, DriveIn, Cinematic, Editorial), app theme (NIXIE_THEME Dark, Light) and art
  (FAKESERVER_ART white, black: every image of the fake server one flat colour, the worst case any artwork can be under a
  label) runs run-offscreen.ps1 -Script home -Then offscreen\then-140-contrast.ps1 (with FAKESERVER_COLLECTIONS=1): the top
  bar's labels on Home, the Movies library (1920 and 1100 wide), the Libraries page, the showcase movie's page, the
  Collections library and a collection; the library toolbar's labels; and the labels between the bar and a library's grid,
  each measured as drawn (PrintWindow) at 4.5:1 or more. Each run's measures are in <Out>\<run>\contrast\contrast.json;
  the table, one column per run, goes to -Table (default <repo>\shots\contrast-matrix.md, not committed). Exit code 0 only
  when every label held to it reaches 4.5:1 in every run. Silent and off-screen like every run-offscreen.ps1 run; the
  release proof runs it with the release candidate's exe. -Parallel starts the 16 runs at once as separate processes;
  run-offscreen.ps1's slots let 3 go together.

.EXAMPLE
  .\contrast-matrix.ps1 -Out C:\scratch\contrast -Exe C:\scratch\build\bin\FluentFin\debug\Nixie.exe -Python C:\...\python.exe
  .\contrast-matrix.ps1 -Out C:\scratch\contrast -Exe ... -Themes DriveIn -Modes Light -Arts white
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Exe,
    [switch]$Build,
    [string]$Python,
    [string]$Cache,
    [string[]]$Themes = @('Classic', 'DriveIn', 'Cinematic', 'Editorial'),
    [string[]]$Modes = @('Dark', 'Light'),
    [string[]]$Arts = @('white', 'black'),
    [string]$Table,
    # runs an earlier call left in -Out are read, not run again (by name: drivein-light-white)
    [string[]]$Reuse = @(),
    [int]$TimeoutSeconds = 900,
    # how long each run waits for a slot (run-offscreen.ps1's, shared with the other streams' runs)
    [int]$QueueMinutes = 60,
    # the runs at once, each its own process: run-offscreen.ps1's slots (NIXIE_HARNESS_PARALLEL, default 3) and its memory
    # wait decide how many go together (each run's console in <Out>\<run>.console.txt). Needs -Exe, not -Build.
    [switch]$Parallel
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$Out = [IO.Path]::GetFullPath($Out)
if (-not $Table) { $Table = Join-Path $repo 'shots\contrast-matrix.md' }
New-Item -ItemType Directory -Force $Out, (Split-Path $Table) | Out-Null
$split = { param($List) @($List | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
$Themes = & $split $Themes; $Modes = & $split $Modes; $Arts = & $split $Arts; $Reuse = & $split $Reuse
$runner = Join-Path $PSScriptRoot 'run-offscreen.ps1'
$then = Join-Path $PSScriptRoot 'offscreen\then-140-contrast.ps1'

# a run's measures (Windows PowerShell's ConvertFrom-Json writes a JSON array as one object: enumerated here)
function Read-Rows([string]$File) { if (Test-Path $File) { @(Get-Content $File -Raw -Encoding utf8 | ConvertFrom-Json | ForEach-Object { $_ }) } else { @() } }
# a run's own failures (a page that did not open) count too: its result.json
function Read-RunFailures([string]$Name, [string]$RunOut) {
    $result = Join-Path $RunOut 'result.json'
    if (-not (Test-Path $result)) { return "$Name | no result.json" }
    $checks = (Get-Content $result -Raw -Encoding utf8 | ConvertFrom-Json).checks
    foreach ($p in @($checks.PSObject.Properties | Where-Object { -not $_.Value.ok -and $_.Name -notlike 'contrast (*): * at *:1 or more' })) {
        "$Name | $($p.Name): $($p.Value.detail)"
    }
}
if ($Parallel -and -not $Exe) { throw "-Parallel runs at once: build first and pass -Exe" }
$going = [ordered]@{}   # -Parallel: run name -> its process

$saved = @{ NIXIE_LAYOUT = $env:NIXIE_LAYOUT; NIXIE_THEME = $env:NIXIE_THEME; FAKESERVER_ART = $env:FAKESERVER_ART; FAKESERVER_COLLECTIONS = $env:FAKESERVER_COLLECTIONS }
$results = [ordered]@{}
$failedRuns = New-Object System.Collections.ArrayList
$built = $false
try {
    foreach ($theme in $Themes) {
        foreach ($mode in $Modes) {
            foreach ($art in $Arts) {
                $name = "$($theme.ToLowerInvariant())-$($mode.ToLowerInvariant())-$($art.ToLowerInvariant())"
                $runOut = Join-Path $Out $name
                $rowsFile = Join-Path $runOut 'contrast\contrast.json'
                if ($Reuse -contains $name -and (Test-Path $rowsFile)) {
                    Write-Host "== ${name}: the measures of the earlier run ($rowsFile)"
                    $results[$name] = Read-Rows $rowsFile
                    continue
                }
                $env:NIXIE_LAYOUT = $theme; $env:NIXIE_THEME = $mode; $env:FAKESERVER_ART = $art; $env:FAKESERVER_COLLECTIONS = '1'
                $data = Join-Path $Out "data-$name"
                foreach ($dir in @($runOut, $data)) { if (Test-Path $dir) { Remove-Item -Recurse -Force $dir } }
                $runArgs = @{ DataDir = $data; Out = $runOut; Script = 'home'; Then = $then; NoA11y = $true; TimeoutSeconds = $TimeoutSeconds; QueueMinutes = $QueueMinutes }
                if ($Exe) { $runArgs.Exe = $Exe } elseif ($Build -and -not $built) { $runArgs.Build = $true }
                if ($Python) { $runArgs.Python = $Python }
                if ($Cache) { $runArgs.Cache = $Cache }
                Write-Host "== $name -> $runOut"
                if ($Parallel) {
                    # its own powershell.exe, this run's NIXIE_* and FAKESERVER_* inherited from here as they are now
                    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $runner + '"'), '-NoA11y')
                    foreach ($k in @($runArgs.Keys | Where-Object { $_ -ne 'NoA11y' })) { $argList += "-$k"; $argList += ('"' + [string]$runArgs[$k] + '"') }
                    $going[$name] = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $argList -PassThru -WindowStyle Hidden `
                        -RedirectStandardOutput (Join-Path $Out "$name.console.txt") -RedirectStandardError (Join-Path $Out "$name.console-err.txt")
                    $null = $going[$name].Handle   # (Windows PowerShell keeps the exit code only for a process whose handle was opened)
                    continue
                }
                & $runner @runArgs | Out-Host
                $built = $true
                $results[$name] = Read-Rows $rowsFile
                foreach ($f in @(Read-RunFailures $name $runOut)) { if ($f) { [void]$failedRuns.Add($f) } }
            }
        }
    }
}
finally {
    foreach ($k in $saved.Keys) { if ($null -eq $saved[$k]) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue } else { Set-Item "Env:$k" $saved[$k] } }
}
foreach ($name in @($going.Keys)) {
    $going[$name].WaitForExit()
    Write-Host "== $name ended (exit $($going[$name].ExitCode)); its console: $(Join-Path $Out "$name.console.txt")"
    $runOut = Join-Path $Out $name
    $results[$name] = Read-Rows (Join-Path $runOut 'contrast\contrast.json')
    foreach ($f in @(Read-RunFailures $name $runOut)) { if ($f) { [void]$failedRuns.Add($f) } }
}
if ($going.Count) {
    # the columns in the serial order (theme, mode, art)
    $ordered = [ordered]@{}
    foreach ($theme in $Themes) { foreach ($mode in $Modes) { foreach ($art in $Arts) {
        $name = "$($theme.ToLowerInvariant())-$($mode.ToLowerInvariant())-$($art.ToLowerInvariant())"
        if ($results.Contains($name)) { $ordered[$name] = $results[$name] }
    } } }
    $results = $ordered
}

# the table: one line per (page, label) in the order first seen, one column per run; a cell is the ratio, bold under 4.5:1
$order = New-Object System.Collections.Generic.List[string]
$cells = @{}
foreach ($run in $results.Keys) {
    foreach ($row in $results[$run]) {
        $id = "$($row.page) | $($row.label)"
        if (-not $order.Contains($id)) { $order.Add($id) }
        $cells["$run`t$id"] = $row
    }
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Nixie 1.4.0 contrast matrix')
$lines.Add('')
$lines.Add("Run $(Get-Date -Format 'yyyy-MM-dd HH:mm') off-screen against the fake server (tools\ui-harness\contrast-matrix.ps1): each label as drawn")
$lines.Add('(PrintWindow), its ground the median of its box''s edge, its ink the pixel farthest from it, over flat white and flat black art.')
$lines.Add('A cell is the ratio; **bold** is under its floor (4.5:1; the caption buttons'' glyphs 3:1, WCAG 1.4.11); *italic* is reported only (a disabled control).')
$lines.Add('')
$lines.Add('| Totals | ' + (($results.Keys | ForEach-Object { $_ }) -join ' | ') + ' |')
$lines.Add('|---|' + (($results.Keys | ForEach-Object { '---' }) -join '|') + '|')
$lines.Add('| held, under the floor | ' + (($results.Keys | ForEach-Object { $r = @($results[$_] | Where-Object held); "$($r.Count), $(@($r | Where-Object { $null -eq $_.ratio -or $_.ratio -lt $_.min }).Count)" }) -join ' | ') + ' |')
$lines.Add('| lowest held | ' + (($results.Keys | ForEach-Object { $low = @($results[$_] | Where-Object held | Sort-Object ratio | Select-Object -First 1); if ($low) { "$($low[0].ratio) ($($low[0].page) $($low[0].label))" } else { '-' } }) -join ' | ') + ' |')
$lines.Add('')
$lines.Add('| Page | Label | ' + (($results.Keys | ForEach-Object { $_ }) -join ' | ') + ' |')
$lines.Add('|---|---|' + (($results.Keys | ForEach-Object { '---' }) -join '|') + '|')
foreach ($id in $order) {
    $parts = $id -split ' \| ', 2
    $row = foreach ($run in $results.Keys) {
        $c = $cells["$run`t$id"]
        if (-not $c) { '-' } elseif (-not $c.held) { "*$($c.ratio)*" } elseif ($null -eq $c.ratio -or $c.ratio -lt $c.min) { "**$($c.ratio)**" } else { "$($c.ratio)" }
    }
    $lines.Add(('| {0} | {1} | {2} |' -f $parts[0], ($parts[1] -replace '\|', '\|'), ($row -join ' | ')))
}
$failures = foreach ($run in $results.Keys) { foreach ($row in @($results[$run] | Where-Object { $_.held -and ($null -eq $_.ratio -or $_.ratio -lt $_.min) })) { "- $run | $($row.page) | $($row.label): $($row.ratio):1, under $($row.min):1 (ink $($row.ink), ground $($row.ground))" } }
$empty = @($results.Keys | Where-Object { @($results[$_]).Count -eq 0 })
if ($failures -or $failedRuns.Count -or $empty.Count) {
    $lines.Add(''); $lines.Add('## Failures'); $lines.Add('')
    foreach ($f in @($failures) + @($failedRuns | ForEach-Object { "- $_" }) + @($empty | ForEach-Object { "- ${_}: no measures" })) { if ($f) { $lines.Add($f) } }
}
[IO.File]::WriteAllLines($Table, [string[]]$lines, [Text.UTF8Encoding]::new($false))
Write-Host "table: $Table"
foreach ($run in $results.Keys) {
    $r = @($results[$run] | Where-Object held)
    Write-Host ("  {0,-24} {1} labels, {2} under their floor" -f $run, $r.Count, @($r | Where-Object { $null -eq $_.ratio -or $_.ratio -lt $_.min }).Count)
}
if (@($failures).Count -or $failedRuns.Count -or $empty.Count) { exit 1 }
exit 0
