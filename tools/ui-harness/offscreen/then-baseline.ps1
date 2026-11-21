# Nixie 1.5, the release baseline's browse script (tools\ui-harness\baseline.ps1 runs it; docs\RELEASE.md "Baseline").
# Dot-sourced by drive.ps1 after -Script home (signed in, Home's first cards timed by drive.ps1): Movies, the showcase film,
# Back, Shows, the showcase series, its Season 1 page, Back; then 5 s to settle and Nixie.exe's private bytes and working
# set. Writes <Out>\baseline.json: drive.ps1's window_ms (process start to the main window) and home_first_cards_ms (the
# server's sign-in answer to Home's first cards), the app's own [perf] start-up lines, each browse step's time and the
# memory. No screenshots, no scans. Every name here starts with bl (drive.ps1's names must not be reused).
function BL-Back { Key 0xA6 }
$bl = [ordered]@{ window_ms = $timings.window_ms; home_first_cards_ms = $timings.home_first_cards_ms; home_rows_loaded_ms = $timings.home_visible_rows_loaded_ms }
# the app's own start-up timestamps ([perf] lines of its log)
foreach ($blF in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)) {
    foreach ($blM in (Select-String -LiteralPath $blF.FullName -Pattern '\[perf\] startup: (.+?) at (\d+) ms' -AllMatches)) {
        $blK = 'perf_' + ($blM.Matches[0].Groups[1].Value -replace '[^a-z]+', '_')
        if (-not $bl.Contains($blK)) { $bl[$blK] = [int]$blM.Matches[0].Groups[2].Value }
    }
}
$blSteps = [ordered]@{}
$blT = Get-Epoch
Open-Library $fixture.movie.library
$blOk = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 100
$blSteps.movies = @([bool]$blOk, (Ms $blT (Get-Epoch)))
$blT = Get-Epoch
Open-Card $fixture.movie.name
$blOk = Wait-ItemPage $fixture.movie.name
$blSteps.film = @([bool]$blOk, (Ms $blT (Get-Epoch)))
Start-Sleep -Milliseconds 1500
$blT = Get-Epoch
BL-Back
$blOk = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 20 -PollMs 100
$blSteps.back_to_movies = @([bool]$blOk, (Ms $blT (Get-Epoch)))
$blT = Get-Epoch
Open-Library $fixture.series.library
$blOk = Wait-Until { Find-MediaCard $fixture.series.name } -Seconds 30 -PollMs 100
$blSteps.shows = @([bool]$blOk, (Ms $blT (Get-Epoch)))
$blT = Get-Epoch
Open-Card $fixture.series.name
$blOk = Wait-ItemPage $fixture.series.name
$blSteps.show = @([bool]$blOk, (Ms $blT (Get-Epoch)))
Start-Sleep -Milliseconds 1500
$blT = Get-Epoch
$blLink = Wait-Until { Find-Ui -Name "$($fixture.season.name) details" -Type 'Hyperlink' -OffscreenToo } -Seconds 10 -PollMs 100
if ($blLink) { [void](Invoke-Element $blLink) } else { Open-Card $fixture.season.name }
$blOk = Wait-Until { if ([OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.season.name) of ", '')) { Find-EpisodeRow } } -Seconds 20 -PollMs 100
$blSteps.season = @([bool]$blOk, (Ms $blT (Get-Epoch)))
Start-Sleep -Milliseconds 1500
$blT = Get-Epoch
BL-Back
$blOk = Wait-Until { if (-not [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.season.name) of ", '')) { Find-Ui -Name "$($fixture.season.name) details" -Type 'Hyperlink' -OffscreenToo } } -Seconds 20 -PollMs 100
$blSteps.back_to_show = @([bool]$blOk, (Ms $blT (Get-Epoch)))
Start-Sleep -Seconds 5
$script:ui.Refresh()
$bl.private_mb = [math]::Round($script:ui.PrivateMemorySize64 / 1MB, 1)
$bl.working_set_mb = [math]::Round($script:ui.WorkingSet64 / 1MB, 1)
$bl.steps = $blSteps
$blBad = @($blSteps.Values | Where-Object { -not $_[0] })
Check 'baseline: every browse step reached its page' ($blBad.Count -eq 0) (($blSteps.Keys | ForEach-Object { "$_=$($blSteps[$_][0])/$($blSteps[$_][1])ms" }) -join ' ')
[IO.File]::WriteAllText((Join-Path $Out 'baseline.json'), ($bl | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
Say ("baseline: window {0} ms (app: {1}), home first cards {2} ms, private {3} MB" -f $bl.window_ms, $bl.perf_window_shown, $bl.home_first_cards_ms, $bl.private_mb)
