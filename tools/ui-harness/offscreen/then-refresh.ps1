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

# ---- Nixie 1.4.0 (bug hunt M4, m3, m2, M3; m9: what the user sees, keyboard first and mouse first) -----------------------
. (Join-Path $PSScriptRoot 'chord-keys.ps1')
function RF-Loads([double]$Since) { @(CK-Log $Since '^Refresh: .* loaded again').Count }
function RF-OnTrailer { $f = CK-Focus; if ($f -match "'Watch trailer'") { $f } }
# the keyboard on the movie page's Trailer: from the keyboard (UI Automation's focus, as Tab would put it) or a click
function RF-ToTrailer([switch]$Click) {
    $b = Wait-Ui -Name 'Watch trailer' -Type 'Button' -Seconds 15
    if (-not $b) { return $false }
    if ($Click) {
        # a click on Trailer opens the theatre: close it again; the keyboard is then back on Trailer, put there by the mouse
        CK-Click $b
        [void](Wait-Until { @(CK-Log ((Get-Epoch) - 5) '\[trailer\] theatre opened') | Select-Object -First 1 } -Seconds 8 -PollMs 200)
        Start-Sleep -Milliseconds 800
        CK-Key 'Esc'
        [void](Wait-Until { RF-OnTrailer } -Seconds 5 -PollMs 150)
    }
    else { [void][OffscreenUi]::SetFocus($b) }
    Start-Sleep -Milliseconds 500
    [bool](RF-OnTrailer)
}
# after a refresh: the keyboard back on Trailer once the place is kept, and still there a second later (not left on the
# TitleBar, where a second refresh used to leave it; on the way, while the page loads, it may pass through it)
function RF-BackOnTrailer([string]$What, [double]$Since) {
    $seen = New-Object System.Collections.ArrayList
    $back = Wait-Until { $f = CK-Focus; [void]$seen.Add($f); if ($f -match "'Watch trailer'" -and @(CK-Log $Since '^Refresh: the place').Count) { $f } } -Seconds 8 -PollMs 200
    Start-Sleep -Seconds 1
    $still = CK-Focus
    Check "refresh: $What, the keyboard is back on Trailer and stays" ([bool]$back -and $still -match "'Watch trailer'") "a second later: $still; on the way: $((($seen | Select-Object -Unique) -join ' > ')); $((CK-Log $Since '^Refresh: ') -join ' | ')"
}

# m3 + M4 on the movie page (still open from 4.)
[void](Wait-ItemPage $fixture.movie.name 10)
Start-Sleep -Seconds 2
foreach ($rfHow in @('keyboard', 'mouse')) {
    if (-not (RF-ToTrailer -Click:($rfHow -eq 'mouse'))) { Check "refresh ($rfHow first): the keyboard on Trailer" $false (CK-Focus); continue }
    # one R
    $rfAt = Get-Epoch
    CK-Key 'R'
    RF-BackOnTrailer "R once ($rfHow first)" $rfAt
    # a held R: one press and 15 repeats
    [void](RF-ToTrailer -Click:($rfHow -eq 'mouse'))
    $rfAt = Get-Epoch
    CK-Key 'R' -NoUp
    Start-Sleep -Milliseconds 400
    for ($rfI = 0; $rfI -lt 15; $rfI++) { CK-Key 'R' -Repeat -NoUp; Start-Sleep -Milliseconds 20 }
    CK-Up 'R'
    RF-BackOnTrailer "a held R ($rfHow first)" $rfAt
    Check "refresh: a held R (1 press, 15 repeats; $rfHow first) loads the page once" ((RF-Loads $rfAt) -eq 1) "$(RF-Loads $rfAt) loads; $((CK-Log $rfAt '^Refresh: ') -join ' | ')"
    # R, R 150 ms apart: the second joins the first
    [void](RF-ToTrailer -Click:($rfHow -eq 'mouse'))
    $rfAt = Get-Epoch
    CK-Key 'R'
    Start-Sleep -Milliseconds 150
    CK-Key 'R'
    RF-BackOnTrailer "R, R 150 ms apart ($rfHow first)" $rfAt
    Check "refresh: R, R 150 ms apart ($rfHow first) loads the page once" ((RF-Loads $rfAt) -eq 1) "$(RF-Loads $rfAt) loads; $((CK-Log $rfAt '^Refresh: ') -join ' | ')"
}
# a held F5
[void](RF-ToTrailer)
$rfAt = Get-Epoch
CK-Key 'F5' -NoUp
Start-Sleep -Milliseconds 400
for ($rfI = 0; $rfI -lt 10; $rfI++) { CK-Key 'F5' -Repeat -NoUp; Start-Sleep -Milliseconds 20 }
CK-Up 'F5'
Start-Sleep -Seconds 3
Check 'refresh: a held F5 (1 press, 10 repeats) loads the page once' ((RF-Loads $rfAt) -eq 1) "$(RF-Loads $rfAt) loads"

# m2: R on a closed combo box is its type-ahead; F5 there still refreshes
$rfCombo = Find-Ui -Type 'ComboBox'
if ($rfCombo) {
    [void][OffscreenUi]::SetFocus($rfCombo)
    Start-Sleep -Milliseconds 500
    $rfAt = Get-Epoch
    CK-Key 'R'
    Start-Sleep -Seconds 2
    Check 'refresh: R on a closed combo box does not refresh (its letter)' ((RF-Loads $rfAt) -eq 0) "focus: $(CK-Focus)"
    $rfAt = Get-Epoch
    CK-Key 'F5'
    $rfF5 = Wait-Until { if ((RF-Loads $rfAt) -gt 0) { $true } } -Seconds 6 -PollMs 150
    Check 'refresh: F5 on a closed combo box still refreshes' ([bool]$rfF5)
}
else { Check 'refresh: the movie page has a combo box (Audio, Subtitles)' $false }

# M3: a slow server (the library's items take 3 s): R, then the user moves before the grid is back; the place never takes the
# keyboard back. Ctrl+F and a letter (the box keeps the keyboard and the text), and an arrow key (the keyboard stays put)
Open-Library $fixture.movie.library
[void](Invoke-Server '/__harness/latency' 'POST' @{ default = 0; rules = @(, @('/Items$', 3000)) })
foreach ($rfMove in @('Ctrl+F', 'Down')) {
    $rfCard = Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 200
    if (-not $rfCard) { Check "refresh (slow server, $rfMove): the library shows the showcase movie" $false; continue }
    Start-Sleep -Milliseconds 800
    [void][OffscreenUi]::SetFocus($rfCard)
    Start-Sleep -Milliseconds 400
    $rfAt = Get-Epoch
    CK-Key 'R'
    Start-Sleep -Milliseconds 600
    CK-Key $rfMove
    Start-Sleep -Milliseconds 300
    if ($rfMove -eq 'Ctrl+F') { CK-Key 'M'; Start-Sleep -Milliseconds 300 }
    $rfMoved = CK-Focus
    Start-Sleep -Seconds 6
    $rfLater = CK-Focus
    if ($rfMove -eq 'Ctrl+F') {
        $rfBox = Find-Ui -Name 'Search' -Type 'Edit'
        $rfText = if ($rfBox) { try { $rfBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { '?' } } else { '(no box)' }
        Check 'refresh (slow server): R, then Ctrl+F and M: 6 s later the keyboard is still in the search box, the text intact' ($rfLater -match "'Search'" -and $rfText -eq 'm') "after the move: $rfMoved; 6 s later: $rfLater; text '$rfText'; $((CK-Log $rfAt '^Refresh: ') -join ' | ')"
        CK-Key 'Esc'; Start-Sleep -Milliseconds 300; CK-Key 'Esc'
        Open-Library $fixture.movie.library
    }
    else {
        Check 'refresh (slow server): R, then Down: the place does not take the keyboard back' ($rfLater -eq $rfMoved -and $rfLater -notmatch [regex]::Escape($fixture.movie.name)) "after the move: $rfMoved; 6 s later: $rfLater; $((CK-Log $rfAt '^Refresh: ') -join ' | ')"
    }
}
[void](Invoke-Server '/__harness/latency' 'POST' @{ default = 0; rules = @() })
