# Nixie 1.7.5, the basics gate, class 8 (lifecycle): minimise and restore keep the page as it was (its title, its cards, the
# keyboard where it was, drawn); offline then online: a page opened with the server gone says what Nixie couldn't do and what
# the user can do on this PC (rule 11: never "your server", no step on the server), Nixie doesn't hammer the address while it
# is gone (rule 5: retries back off), and Try again shows the page once the server is back; two quick restarts each open Home
# signed in, exit cleanly, and stay one device on the server (every request carries the same device id), also when the new
# copy starts while the old one is still closing.
#   run-gate.ps1 ... -Checks lifecycle      (manifest\g4.psd1)
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Before tools\gate\checks\8-lifecycle-before.ps1 -Then tools\gate\checks\8-lifecycle.ps1
# The -Before script puts g4_forward.py between Nixie and the fake server (the address Nixie saves): offline = its connections
# reset and refused, online = forwarded again. G4_PARTS picks parts: minimise, offline, restarts. Dot-sourced by drive.ps1:
# names start with g4l / G4L- (g4-common.ps1: g4 / G4-).

$script:g4DefaultId = 'lifecycle'
. (Join-Path $PSScriptRoot 'g4-common.ps1')
if (-not $script:g4Forward) { throw 'lifecycle: run with -Before tools\gate\checks\8-lifecycle-before.ps1 (the forwarder)' }

function G4L-Stats { $f = Join-Path $script:g4Forward 'stats.json'; if (Test-Path $f) { try { Get-Content -LiteralPath $f -Raw | ConvertFrom-Json } catch { $null } } }
function G4L-Offline([bool]$Off) {
    $flag = Join-Path $script:g4Forward 'offline'
    if ($Off) { [IO.File]::WriteAllText($flag, 'offline') } else { Remove-Item -LiteralPath $flag -ErrorAction SilentlyContinue }
    [void](Wait-Until { $s = G4L-Stats; if ($s -and [bool]$s.offline -eq $Off) { $true } } -Seconds 5 -PollMs 100)
    Say "lifecycle: the server $(if ($Off) { 'gone (offline)' } else { 'back (online)' })"
}
# the device ids seen in requests since an epoch
function G4L-DevicesSince([double]$Since) {
    $s = G4L-Stats
    if (-not $s) { return @() }
    @($s.devices.PSObject.Properties | Where-Object { [double]$_.Value -ge $Since } | ForEach-Object { $_.Name })
}
function G4L-AllDevices { $s = G4L-Stats; if ($s) { @($s.devices.PSObject.Properties | ForEach-Object { $_.Name }) } else { @() } }
# the visible texts of the window
function G4L-Texts {
    @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::Text -and -not $_.Cached.IsOffscreen -and $_.Cached.Name } catch { $false } } | ForEach-Object { $_.Cached.Name })
}
function G4L-Focus { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } else { $null } }
function G4L-FocusDesc($F) { if ($F) { "$($F.type) '$($F.name)'" } else { 'nothing' } }

G4-SignInRemembered

G4-Part 'minimise' {
    # Home: its cards, the keyboard on the first card in view
    Open-Nav 'Home'; Start-Sleep -Seconds 3
    $card = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Cached.IsOffscreen -and $_.Cached.IsKeyboardFocusable -and $_.Cached.BoundingRectangle.Y -gt 120 } catch { $false } }) | Select-Object -First 1 } -Seconds 15 -PollMs 300
    if ($card) { [void][OffscreenUi]::SetFocus($card); Start-Sleep -Milliseconds 500 }
    foreach ($where in 'Home', 'the movie page') {
        if ($where -eq 'the movie page') {
            Open-Library $fixture.movie.library; Open-Card $fixture.movie.name
            if (-not (Wait-ItemPage $fixture.movie.name)) { throw 'no movie page' }
            Start-Sleep -Seconds 2
            $play = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Cached.Name -match '^(Play|Resume)' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1
            if ($play) { [void][OffscreenUi]::SetFocus($play); Start-Sleep -Milliseconds 500 }
        }
        $f0 = G4L-Focus
        if (-not $f0) {
            $first = [OffscreenUi]::FirstContentFocusable($script:hwnd, $script:ui.Id, 80, 60)
            if ($first) { [void][OffscreenUi]::SetFocus($first); Start-Sleep -Milliseconds 400 }
            $f0 = G4L-Focus
        }
        $n0 = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Cached.IsOffscreen } catch { $false } }).Count
        [void][G4Win]::ShowWindow($script:hwnd, 6)
        $min = [bool](Wait-Until { if ([G4Win]::IsIconic($script:hwnd)) { $true } } -Seconds 4 -PollMs 100)
        Start-Sleep -Seconds 3
        G4-Restore
        Start-Sleep -Milliseconds 1500
        $f1 = G4L-Focus
        $n1 = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Cached.IsOffscreen } catch { $false } }).Count
        $lit = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.1, 0.15, 0.9, 0.9)
        $same = if ($where -eq 'Home') { [bool](G4-Shell) -and $n1 -ge [math]::Max(1, $n0 - 1) } else { [bool](Wait-ItemPage $fixture.movie.name 3) }
        G4-Check "minimised and restored on $where" 'the page is as it was: shown, drawn, the same items' ($min -and $same -and $lit -gt 5) "minimised $min; items in view $n0 -> $n1; $lit% not black"
        G4-Check "minimised and restored on $where" 'the keyboard is where it was' ($f0 -and $f1 -and $f1.name -eq $f0.name -and $f1.type -eq $f0.type) "$(G4L-FocusDesc $f0) -> $(G4L-FocusDesc $f1)"
    }
}

G4-Part 'offline' {
    # the Movies library while the server is there; then it goes, and the showcase movie's page is opened
    Open-Library $fixture.movie.library
    $card = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200
    if (-not $card) { throw 'no showcase movie in its library' }
    Start-Sleep -Seconds 2
    $s0 = G4L-Stats
    G4L-Offline $true
    try {
        $at = Get-Epoch
        Open-Card $fixture.movie.name
        $said = Wait-Until { $t = @(G4L-Texts); if ($t | Where-Object { $_ -match "couldn't|could not|can't reach|isn't answering" }) { , $t } } -Seconds 60 -PollMs 500
        # the texts as they were when the message showed (a second read can land in a redraw and see none)
        $texts = if ($said) { @($said) } else { @(G4L-Texts) }
        $why = "$(@($texts | Where-Object { $_ -match "couldn't|could not|can't reach|isn't answering" }) | Select-Object -First 1)"
        $retry = Find-Ui -Name 'Try again' -Type 'Button'
        G4-Check 'offline' 'a page opened with the server gone says what Nixie couldn''t do, with Try again' ([bool]$said -and [bool]$retry) "said '$why' $([int]((Get-Epoch) - $at)) s after the click; Try again $([bool]$retry)"
        $bad = @($texts | Where-Object { $_ -match '(?i)your server|dashboard|restart the server|scan the library|server.s logs|plugin' })
        G4-Check 'offline' 'its words are rule 11''s: about this PC, never "your server" or a step on the server' ($bad.Count -eq 0 -and $why -and -not ($why -match '(?i)check that the server')) "$(if ($bad) { $bad -join ' | ' } else { $why })"
        # 1.7.5 (g4): only the message: no Play, Watched or Favourite of a film that isn't there, no empty track pickers (Editorial
        # showed them behind it)
        $behind = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $c = $_.Cached; -not $c.IsOffscreen -and (($c.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $c.Name -match '^(Play|Resume|Mark .* as (watched|unwatched)|Add .* to favorites)') -or ($c.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox)) } catch { $false } } | ForEach-Object { "$($_.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', '') '$($_.Cached.Name)'" })
        G4-Check 'offline' 'while it shows, the film page shows nothing of its own behind it (no Play, no pickers)' ($behind.Count -eq 0) "$(if ($behind) { $behind -join ', ' } else { 'nothing behind the message' })"
        Capture-Page "$(G4-Id)-offline"
        # 30 s gone: the attempts it makes (rule 5: retries back off; a hammering client makes many)
        $r0 = (G4L-Stats).resets
        Start-Sleep -Seconds 30
        $r1 = (G4L-Stats).resets
        G4-Check 'offline' 'while the server is gone Nixie does not hammer its address (30 s, at most 12 attempts)' (($r1 - $r0) -le 12) "$($r1 - $r0) connection attempts in 30 s ($r1 since it went)"
    }
    finally { G4L-Offline $false }
    $retry = Find-Ui -Name 'Try again' -Type 'Button'
    if ($retry) { [void](Invoke-Element $retry) }
    $page = Wait-ItemPage $fixture.movie.name 30
    G4-Check 'online again' 'Try again shows the page once the server is back' ([bool]$page -and -not (Find-Ui -Name 'Try again' -Type 'Button')) "Try again pressed $([bool]$retry); the movie's title shown $([bool]$page)"
    # and Home loads again (R)
    Open-Nav 'Home'; Start-Sleep -Seconds 2
    Key 0x52
    $cards = Wait-Until { $n = [OffscreenUi]::CountByClass($script:hwnd, $script:ui.Id, @('BaseItemDtoCard', 'EditorialPoster', 'EditorialStill', 'Shimmer')); $c = $n['BaseItemDtoCard'] + $n['EditorialPoster'] + $n['EditorialStill']; if ($c -gt 0 -and $n['Shimmer'] -eq 0) { $c } } -Seconds 30 -PollMs 300
    if (-not $cards) { $cards = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and -not $_.Cached.IsOffscreen } catch { $false } }).Count }
    G4-Check 'online again' 'Home refreshed (R) shows its cards' ($cards -gt 0) "$cards cards"
}

G4-Part 'restarts' {
    $first = @(G4L-AllDevices)
    G4-Check 'before the restarts' 'Nixie sends one device id' ($first.Count -eq 1) "$($first.Count) device ids seen"
    foreach ($k in 1, 2, 3) {
        $quick = $k -eq 3
        $label = if ($quick) { 'a restart started while the last copy is still closing' } else { "restart $k" }
        # a page that asks the server: the new copy's requests seen with their device id (in the quick case while the old
        # copy is still closing)
        $ask = { Open-Library $fixture.movie.library; [void](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 20 -PollMs 300) }
        $r = G4-Restart $label -Quick:$quick -WhileClosing $ask
        if (-not $quick -and $r.Page -eq 'home') { & $ask }
        G4-Check $label 'Nixie opens Home, still signed in' ($r.Page -eq 'home' -and $r.StartedWhileClosing -eq $quick) "page $($r.Page); the new copy started while the old one was closing: $($r.StartedWhileClosing)$(if ($quick) { " (held in its close: $($r.Held))" })"
        G4-Check $label 'the copy before exited cleanly (exit code 0), leaving nothing running' ($r.Code -eq 0 -and $r.Alive.Count -eq 0) "exit $(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { 'none' }) $($r.ExitMs) ms after the close; left running: $(($r.Alive | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ', ')"
        $all = @(G4L-AllDevices)
        G4-Check $label 'still one device on the server: every request carried the same device id' ($all.Count -eq 1) "$($all.Count) device ids seen in all ($(@(G4L-DevicesSince $r.Since).Count) new since this start)"
    }
}
