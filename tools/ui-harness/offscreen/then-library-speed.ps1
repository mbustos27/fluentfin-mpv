# Seaglass 1.1.4: the Library's first card, timed (the UX review measured 7.2 s). Run with -Script wait:1 -Then
# offscreen\then-library-speed.ps1 [-Latency 150]. Five times, from the Libraries page, the Movies tile is opened with the
# keyboard (the focus put on it, Enter posted to the window: a posted click never reaches WinUI's pointer events off-screen)
# and once more through UI Automation's Invoke (what the review's run did); each time three numbers:
#   app_ms      the app's own: from the library page being made to its first card made (its log: "[perf] library: first
#               card N ms after the page was opened"; a build without the line gives none);
#   seen_ms     from the key to a card in the UI Automation tree (the harness's view: UI Automation's own search of a page
#               of 100 cards is in it);
#   invoke_ms   from UI Automation's Invoke to a card in the tree.
# With the server's timings of the requests the library made (items, filters). Each pass starts from Home.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
function Speed-PerfLines {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '\[perf\] library: first card (\d+) ms' } })
}
function Speed-Tile {
    Open-Nav 'Home'
    Start-Sleep -Milliseconds 600
    Open-Nav 'Libraries'
    $t = Wait-Until { [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, $fixture.movie.library, 'Microsoft.UI.Xaml.Controls.ItemContainer') } -Seconds 15 -PollMs 100
    Start-Sleep -Milliseconds 800
    $t
}
function Speed-FirstCardSeen {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $n = [OffscreenUi]::CountByClass($script:hwnd, $script:ui.Id, @('BaseItemDtoCard'))
        if ($n['BaseItemDtoCard'] -gt 0) { return Get-Epoch }
        Start-Sleep -Milliseconds 30
    }
    $null
}
$speedRuns = @()
for ($pass = 1; $pass -le 5; $pass++) {
    $tile = Speed-Tile
    if (-not $tile) { Check "library speed: pass $pass found the Movies tile" $false; continue }
    $perfBefore = (Speed-PerfLines).Count
    [void][OffscreenUi]::SetFocus($tile); Start-Sleep -Milliseconds 400
    $t0 = Get-Epoch
    Key 0x0D
    $seen = Speed-FirstCardSeen
    Start-Sleep -Milliseconds 1500
    $perf = @(Speed-PerfLines)
    $app = if ($perf.Count -gt $perfBefore -and $perf[-1] -match 'first card (\d+) ms') { [int]$Matches[1] } else { $null }
    # the server's log: "t" is when a request ended, "ms" how long it took (the latency included)
    $reqs = @(Get-Requests $t0 | Where-Object { (Get-Origin $_) -eq 'ui' -and $_.route -in @('items', 'filters') })
    $items = @($reqs | Where-Object { $_.route -eq 'items' }) | Select-Object -First 1
    $filters = @($reqs | Where-Object { $_.route -eq 'filters' }) | Select-Object -First 1
    # the same, the tile invoked through UI Automation
    $tile2 = Speed-Tile
    $invokeMs = $null; $invokeApp = $null
    if ($tile2) {
        $perfBefore = (Speed-PerfLines).Count
        $t1 = Get-Epoch
        [void](Invoke-Element $tile2)
        $invokeMs = Ms $t1 (Speed-FirstCardSeen)
        Start-Sleep -Milliseconds 1500
        $perf = @(Speed-PerfLines)
        $invokeApp = if ($perf.Count -gt $perfBefore -and $perf[-1] -match 'first card (\d+) ms') { [int]$Matches[1] } else { $null }
    }
    $run = [ordered]@{
        pass = $pass
        app_ms = $app
        seen_ms = Ms $t0 $seen
        invoke_ms = $invokeMs
        invoke_app_ms = $invokeApp
        items_asked_ms = $(if ($items) { Ms $t0 ($items.t - $items.ms / 1000.0) })
        items_answered_ms = $(if ($items) { Ms $t0 $items.t })
        filters_asked_ms = $(if ($filters) { Ms $t0 ($filters.t - $filters.ms / 1000.0) })
        filters_answered_ms = $(if ($filters) { Ms $t0 $filters.t })
    }
    $speedRuns += [pscustomobject]$run
    Say ("library speed pass {0}: Enter: app {1} ms, seen {2} ms; UI Automation invoke: app {3} ms, seen {4} ms; items asked {5} ms, answered {6} ms; filters asked {7} ms, answered {8} ms" -f
        $pass, $run.app_ms, $run.seen_ms, $run.invoke_app_ms, $run.invoke_ms, $run.items_asked_ms, $run.items_answered_ms, $run.filters_asked_ms, $run.filters_answered_ms)
}
function Speed-Median([object[]]$Values) { $v = @($Values | Where-Object { $null -ne $_ } | Sort-Object); if ($v.Count) { $v[[int][Math]::Floor($v.Count / 2)] } }
$timings.library_app_first_card_median_ms = Speed-Median @($speedRuns | ForEach-Object { $_.app_ms })
$timings.library_seen_after_enter_median_ms = Speed-Median @($speedRuns | ForEach-Object { $_.seen_ms })
$timings.library_seen_after_invoke_median_ms = Speed-Median @($speedRuns | ForEach-Object { $_.invoke_ms })
[IO.File]::WriteAllText((Join-Path $Out 'library-speed.json'), (ConvertTo-Json -InputObject @($speedRuns) -Depth 4), [Text.UTF8Encoding]::new($false))
Check 'library speed: five passes timed' (@($speedRuns | Where-Object { $_.seen_ms }).Count -eq 5) ("median: app {0} ms, seen after Enter {1} ms, after a UI Automation invoke {2} ms" -f
    $timings.library_app_first_card_median_ms, $timings.library_seen_after_enter_median_ms, $timings.library_seen_after_invoke_median_ms)
