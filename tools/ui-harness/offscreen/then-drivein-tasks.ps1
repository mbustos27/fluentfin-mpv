# Nixie 1.3.0, the Drive-in theme's gate: the four common tasks, each one action (the concepts' table), run as a user would
# with the keyboard or one click (UI Automation Invoke):
#   1. resume the last thing: Home opens with Resume on the outdoor screen focused; Enter plays it;
#   2. the next episode of a show: one entry of the Coming up sign plays it;
#   3. a letter jump in the Library: one letter tile, and a title starting with it has the focus;
#   4. picking a season: one season tube, and the SHOWTIMES board lists that season.
# Run with NIXIE_LAYOUT=DriveIn: run-offscreen.ps1 -Script home -Then offscreen\then-drivein-tasks.ps1
# Dot-sourced by drive.ps1: every name here starts with DT (PowerShell names ignore case; drive.ps1's must not be reused).

function DT-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }

# leaves the player the way a user does (the Back key, FluentFin's controls) and waits for the page to be back
function DT-LeavePlayer([scriptblock]$Back) {
    DT-Settle 2500
    Key 0xA6
    $dtBack = Wait-Until $Back -Seconds 10 -PollMs 150
    if (-not $dtBack) {
        $dtBtn = Find-Ui -Id 'BackButton'
        if ($dtBtn) { [void](Invoke-Element $dtBtn); $dtBack = Wait-Until $Back -Seconds 10 -PollMs 150 }
    }
    [bool]$dtBack
}

function DT-Playing([double]$Since) {
    [bool](Wait-Until { @(Get-Requests $Since | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 30 -PollMs 200)
}

# ---- 1. resume: Resume has the keyboard on arrival, Enter plays ---------------------------------------------------------
$dtFocused = Wait-Until { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f -and $f -match 'Resume') { $f } } -Seconds 15 -PollMs 200
Check 'drive-in task 1: Home opens with Resume focused on the screen' ([bool]$dtFocused) "$dtFocused"
if ($dtFocused) {
    $dtAt = Get-Epoch
    Key 0x0D
    $dtPlays = DT-Playing $dtAt
    Check 'drive-in task 1: Enter resumes it (one key)' $dtPlays
    if ($dtPlays) {
        $dtLeft = DT-LeavePlayer { Find-Ui -Name 'Coming up' -Type 'Text' }
        Say "drive-in tasks: left the player ($(if ($dtLeft) { 'back on Home' } else { 'not back on Home: Home from its tab' }))"
        if (-not $dtLeft) { Open-Nav 'Home'; [void](Wait-Ui -Name 'Coming up' -Type 'Text' -Seconds 15) }
        DT-Settle 2000
        Shot 'drivein-tasks-home-again'
    }
}

# ---- 2. the next episode: one entry of the Coming up sign ---------------------------------------------------------------
$dtEntry = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Cached.Name -match '^Play .+, season \d+, episode \d+$' } catch { $false } }) | Select-Object -First 1 } -Seconds 15 -PollMs 200
Check 'drive-in task 2: the Coming up sign lists the next episodes' ([bool]$dtEntry) $(if ($dtEntry) { $dtEntry.Current.Name })
if ($dtEntry) {
    $dtAt = Get-Epoch
    [void](Invoke-Element $dtEntry)
    $dtPlays = DT-Playing $dtAt
    Check 'drive-in task 2: one click plays the next episode' $dtPlays
    if ($dtPlays) { [void](DT-LeavePlayer { Find-Ui -Name 'Coming up' -Type 'Text' }); DT-Settle 1500 }
}

# ---- 3. a letter jump: one tile --------------------------------------------------------------------------------------
Open-Library $fixture.movie.library
[void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150)
DT-Settle 1500
$dtTile = Wait-Ui -Name 'Jump to C' -Type 'Button' -Seconds 10
Check 'drive-in task 3: the letter board has its tiles' ([bool]$dtTile)
if ($dtTile) {
    [void](Invoke-Element $dtTile)
    $dtCard = Wait-Until { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f -and $f -match '"C') { $f } } -Seconds 10 -PollMs 150
    Check 'drive-in task 3: one tile takes the focus to a title starting with C' ([bool]$dtCard) "$dtCard"
    $dtRest = Find-Ui -Name 'No titles start with' -Type 'Button' -Contains
    Check 'drive-in task 3: a letter with no titles rests (not a stop)' ((-not $dtRest) -or (-not $dtRest.Current.IsEnabled)) $(if ($dtRest) { $dtRest.Current.Name } else { 'every letter has titles' })
}

# ---- 4. picking a season: one tube ---------------------------------------------------------------------------------
Step-Series $false
DT-Settle 2000
$dtTube = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $_.Cached.Name -match '^Season 2$' } catch { $false } }) | Select-Object -First 1 } -Seconds 10 -PollMs 200
Check 'drive-in task 4: the season tubes list season 2' ([bool]$dtTube)
if ($dtTube) {
    [void](Invoke-Element $dtTube)
    $dtRow = Wait-Until { Find-Ui -Name 'S2 E1' -Type 'ListItem' -Contains -OffscreenToo } -Seconds 15 -PollMs 200
    Check 'drive-in task 4: one tube shows that season on the SHOWTIMES board' ([bool]$dtRow) $(if ($dtRow) { $dtRow.Current.Name })
    $dtCurrent = Find-Ui -Name 'Season 2, current season' -Type 'Button'
    Check 'drive-in task 4: the tube says it is the season shown' ([bool]$dtCurrent)
}
