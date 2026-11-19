# Nixie 1.3.1, R / F5 / Ctrl+R refresh the page: in the Movies library with the keyboard on the showcase movie's card, R
# loads the page again from the server (new requests, FluentFin's "Refresh:" log line) and the keyboard is back on that card;
# an R typed in the search box is a letter (no refresh); Settings does not refresh; F5 on the movie page loads it again.
#   run-offscreen.ps1 -Script home -Then offscreen\then-refresh.ps1   (any NIXIE_LAYOUT)
# Dot-sourced by drive.ps1: every name here starts with RF (drive.ps1's names must not be reused).

function RF-Log([double]$Since) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 | Where-Object { $_ -match '\[INF\] Refresh: ' } | ForEach-Object {
            $rfWhen = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(($_ -split ' \[INF\] ')[0], [ref]$rfWhen) -and $rfWhen.ToUnixTimeMilliseconds() / 1000.0 -ge $Since) { ($_ -split '\[INF\] ', 2)[1] }
        }
    })
}

# 1. the library, the keyboard on a card, R
Open-Library $fixture.movie.library
$rfCard = Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 200
Check 'refresh: the library shows the showcase movie' ([bool]$rfCard)
if ($rfCard) {
    Start-Sleep -Milliseconds 800
    [void][OffscreenUi]::SetFocus($rfCard)
    Start-Sleep -Milliseconds 400
    $rfAt = Get-Epoch
    Key 0x52
    $rfDone = Wait-Until { @(RF-Log $rfAt) | Select-Object -First 1 } -Seconds 8 -PollMs 150
    Check 'refresh: R loads the library again' ([bool]$rfDone) "$rfDone"
    $rfAsked = Wait-Until { $r = @(Get-Requests $rfAt | Where-Object { $_.auth -and $_.path -match '/Items' }); if ($r.Count) { $r.Count } } -Seconds 8 -PollMs 150
    Check 'refresh: the page asked the server again' ([bool]$rfAsked) "$rfAsked request(s) to /Items after R"
    $rfBack = Wait-Until { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f -and $f -match [regex]::Escape($fixture.movie.name)) { $f } } -Seconds 8 -PollMs 200
    Check 'refresh: the keyboard is back on the same card' ([bool]$rfBack) "$([OffscreenUi]::Focused($script:hwnd, $script:ui.Id))"
    Capture-Page 'refresh-library'
}

# 2. an R typed in the search box is a letter
$rfSearch = Find-Ui -Name 'Search' -Type 'Edit'
if ($rfSearch) {
    [void][OffscreenUi]::SetFocus($rfSearch)
    Start-Sleep -Milliseconds 300
    $rfAt = Get-Epoch
    Key 0x52
    Start-Sleep -Seconds 2
    Check 'refresh: an R typed in the search box does not refresh' (@(RF-Log $rfAt).Count -eq 0)
    Key 0x1B
}
else { Say 'refresh: no search box found (narrow bar?): the typing check skipped' }

# 3. Settings has no server data: nothing
Open-Nav 'Settings'
Start-Sleep -Seconds 2
$rfAt = Get-Epoch
Key 0x74
Start-Sleep -Seconds 2
Check 'refresh: F5 on Settings does nothing' (@(RF-Log $rfAt).Count -eq 0)

# 4. F5 on the movie page
Open-Library $fixture.movie.library
Open-Card $fixture.movie.name
[void](Wait-ItemPage $fixture.movie.name)
Start-Sleep -Seconds 2
$rfAt = Get-Epoch
Key 0x74
$rfDone = Wait-Until { @(RF-Log $rfAt) | Select-Object -First 1 } -Seconds 8 -PollMs 150
Check 'refresh: F5 loads the movie page again' ([bool]$rfDone) "$rfDone"
$rfItem = Wait-Until { @(Get-Requests $rfAt | Where-Object { ($_.path -replace '-', '') -match [regex]::Escape(($fixture.movie.id -replace '-', '')) }) | Select-Object -First 1 } -Seconds 8 -PollMs 150
Check 'refresh: the movie was asked for again' ([bool]$rfItem) $(if ($rfItem) { $rfItem.path })
$rfStill = Wait-ItemPage $fixture.movie.name 10
Check 'refresh: the movie page is still shown' ([bool]$rfStill)
