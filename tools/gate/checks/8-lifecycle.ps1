# Nixie 1.7.5, the basics gate, class 8 (lifecycle): minimise and restore keep the page as it was (its title, its cards, the
# keyboard where it was, drawn); offline then online: a page opened with the server gone says what Nixie couldn't do and what
# the user can do on this PC (rule 11: never "your server", no step on the server), Nixie doesn't hammer the address while it
# is gone (rule 5: retries back off), and Try again shows the page once the server is back; two quick restarts each open Home
# signed in, exit cleanly, and stay one device on the server (every request carries the same device id), also when the new
# copy starts while the old one is still closing; 1.8.1 (BH-175-m4): closed during a film and opened again at once with a
# remembered password, the film's place is kept (the closing copy's Stopped, arriving after the new copy's sign-in, is taken);
# the review of it: opened again while that Stopped is still on its way, the new copy waits for the closing copy's device slot
# and is the same device (restart-playing-wait).
#   run-gate.ps1 ... -Checks lifecycle      (manifest\g4.psd1)
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Before tools\gate\checks\8-lifecycle-before.ps1 -Then tools\gate\checks\8-lifecycle.ps1
# The -Before script puts g4_forward.py between Nixie and the fake server (the address Nixie saves): offline = its connections
# reset and refused, online = forwarded again, hold-stopped = a Stopped report held. G4_PARTS picks parts: minimise, offline,
# restarts, restart-playing-wait, restart-playing. Dot-sourced by drive.ps1: names start with g4l / G4L- (g4-common.ps1: g4 / G4-).

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

# 1.8.1 (BH-175-m4): Account > Switch user, signed in with the fake server's generated test password, "Remember me" ticked:
# the next start signs in by name with it (a remembered password; the fake server's test user, never a real one)
function G4L-SignInPassword {
    $acct = Wait-Until { Find-Ui -Name 'Account, ' -Type 'Button' -Contains } -Seconds 15 -PollMs 150
    if (-not $acct) { throw 'no Account button' }
    [void](Invoke-Element $acct)
    $switch = Wait-Ui -Name 'Switch user' -Type 'Button' -Seconds 8
    if (-not $switch) { throw 'no Switch user in the Account menu' }
    [void](Invoke-Element $switch)
    if (-not (Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect') } -Seconds 20 -PollMs 150)) { throw 'Switch user: no sign-in page' }
    $creds = Get-Content -LiteralPath (Join-Path $g4Repo 'tools\ui-harness\fakeserver\credentials.json') -Raw | ConvertFrom-Json
    $password = [OffscreenUi]::FindByClass($script:hwnd, $script:ui.Id, 'Edit', 'PasswordBox')
    $userBox = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Cached.ClassName -ne 'PasswordBox' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1
    if (-not $password -or -not $userBox) { throw 'no username or password box on the sign-in page' }
    Set-ElementValue $userBox $creds.username
    Set-ElementValue $password $creds.password   # the fake server's generated test password (never a real one)
    $remember = Wait-Ui -Name 'Remember me' -Type 'CheckBox' -Seconds 5
    if (-not $remember) { throw 'no Remember me on the sign-in page' }
    $toggle = $remember.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() }
    $button = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Sign in')
    if (-not $button) { throw 'no Sign in button on the sign-in page' }
    $at = Get-Epoch
    [void](Invoke-Element $button)
    $auth = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'authenticate' -and $_.path -match '(?i)AuthenticateByName' -and $_.status -eq 200 }) | Select-Object -First 1 } -Seconds 30 -PollMs 100
    $shell = Wait-Until { G4-Shell } -Seconds 30 -PollMs 150
    if (-not ($auth -and $shell)) { throw "the password sign-in did not reach Home (server answer $([bool]$auth), Home $([bool]$shell))" }
    $script:navPane = $shell; $script:page = 'home'
    Start-Sleep -Milliseconds 800
}
# the movie page's Play or Resume button (its name: "Play", "Resume at 0:12")
function G4L-PlayButton { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Cached.Name -match '^(Play|Resume)' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1 }
# "Resume at 1:02" / "Resume at 1:02:03" in seconds; $null when it says no place
function G4L-ResumeSecs([string]$Name) {
    if ($Name -notmatch '^Resume at ([\d:]+)') { return $null }
    $s = 0; foreach ($p in ($Matches[1] -split ':')) { $s = $s * 60 + [int]$p }; $s
}
# the fixture's film played from its page, for 8 s (progress reports go to the server)
function G4L-PlayFilm {
    Open-Library $fixture.movie.library; Open-Card $fixture.movie.name
    if (-not (Wait-ItemPage $fixture.movie.name)) { throw 'no movie page' }
    $play = Wait-Until { G4L-PlayButton } -Seconds 10 -PollMs 200
    if (-not $play) { throw 'no Play on the movie page' }
    $playAt = Get-Epoch
    [void](Invoke-Element $play)
    if (-not (Wait-Until { @(Get-Requests $playAt | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 60 -PollMs 250)) { throw 'the film never played (no Playing report)' }
    Start-Sleep -Seconds 8
}
# the film's page in this copy: its Resume (or Play) button's name, and whether it offers Resume within 3 s of $Left seconds
function G4L-ResumeOffered([double]$Left) {
    Open-Library $fixture.movie.library; Open-Card $fixture.movie.name
    $page = Wait-ItemPage $fixture.movie.name
    $resume = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Cached.Name -match '^Resume at ' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1 } -Seconds 10 -PollMs 250
    $name = if ($resume) { $resume.Cached.Name } else { $b = G4L-PlayButton; if ($b) { $b.Cached.Name } else { 'no Play button' } }
    $at = G4L-ResumeSecs $name
    [pscustomobject]@{ Name = $name; Ok = ([bool]$page -and $null -ne $at -and [math]::Abs($at - $Left) -le 3) }
}

# 1.8.1 (the review of BH-175-m4): closed during a film and opened again at once, the closing copy's Stopped on its way (the
# forwarder holds it until the new copy's window is there, as a server answering in a second or two). The closing copy keeps
# its device slot until that report is sent; the new copy waits for that slot (a few seconds at most) and is the same device
# on the server. Before: the copy's second device for its whole run, which a per-device allow list refuses. The server takes
# the Stopped, and the film's page offers Resume where it was left. Before restart-playing: this window is the copy's first.
G4-Part 'restart-playing-wait' {
    G4L-PlayFilm
    $hold = Join-Path $script:g4Forward 'hold-stopped'
    $held0 = [int](G4L-Stats).held
    $before = @(G4L-AllDevices)
    [IO.File]::WriteAllText($hold, 'hold')
    $label = 'a restart during a film, its Stopped still on its way'
    try {
        $r = G4-Restart $label -Overlap -BeforeStart {
            # the closing copy's Stopped held in the network: it keeps its slot for it
            if (-not (Wait-Until { if ([int](G4L-Stats).held -gt $held0) { $true } } -Seconds 20 -PollMs 50)) { throw 'the closing copy sent no Stopped' }
        } -OnceStarted {
            # let go 2.2 s after the new copy's process started: past the moment it claims its slot (about 1 s in, at its
            # start), within the 3 s it may wait for it (a copy that claims later finds the slot free either way)
            while ((Get-Epoch) -lt $script:launchedAt + 2.2) { Start-Sleep -Milliseconds 20 }
            $script:g4lLetGo = Get-Epoch
            Remove-Item -LiteralPath $hold -ErrorAction SilentlyContinue
        }
    }
    finally { Remove-Item -LiteralPath $hold -ErrorAction SilentlyContinue }
    Say "lifecycle: the closing copy's Stopped let go $([int](($script:g4lLetGo - $script:launchedAt) * 1000)) ms after the new copy's process started"
    G4-Check $label 'Nixie opens Home, still signed in, started while the old copy sent its Stopped' ($r.Page -eq 'home' -and $r.StartedWhileClosing) "page $($r.Page); started while the old one closed: $($r.StartedWhileClosing)"
    [void](Wait-Until { @(Get-Requests $r.Since | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
    $stops = @(Get-Requests $r.Since | Where-Object { $_.route -eq 'report-stopped' })
    G4-Check $label 'the closing copy''s Stopped report is taken by the server' ($stops.Count -eq 1 -and (Get-Origin $stops[0]) -eq 'backend' -and $stops[0].status -ge 200 -and $stops[0].status -lt 300) "$($stops.Count) Stopped: $(($stops | ForEach-Object { "$(Get-Origin $_) $($_.status)" }) -join ', ')"
    G4-Check $label 'the copy before exited cleanly (exit code 0), leaving nothing running' ($r.Code -eq 0 -and $r.Alive.Count -eq 0) "exit $(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { 'none' }) $($r.ExitMs) ms after the close"
    $left = if ($stops.Count -ge 1 -and $null -ne $stops[0].report.position) { [double]$stops[0].report.position / 1e7 } else { -100 }
    $offer = G4L-ResumeOffered $left
    G4-Check $label 'the film''s page offers Resume where it was left' $offer.Ok "'$($offer.Name)'; the Stopped report said $(if ($left -ge 0) { [math]::Round($left, 1) } else { 'nothing' }) s"
    # the device the server sees the new copy as, in its own words (its log): the forwarder sees only what goes through it, and
    # the saved server's local address goes round it
    $said = Wait-Until { G4-Log $r.Since '\[device\] this window is the copy''s device slot' | Select-Object -Last 1 } -Seconds 10 -PollMs 200
    $slot = if ($said -match 'device slot (\w+), claimed in (\d+) ms') { $Matches[1]; $claimMs = $Matches[2] } else { 'not said'; $claimMs = '?' }
    $all = @(G4L-AllDevices)
    G4-Check $label 'still one device on the server: the new copy waited for the closing copy''s device slot (the copy''s own, slot 0)' ($slot -eq '0') "the new copy's log: slot $slot, claimed in $claimMs ms; the forwarder saw $($all.Count) device id(s), $($before.Count) before"
}

# 1.8.1 (BH-175-m4): closed during a film and opened again at once with a remembered password. The new copy signs in by name
# before the closing copy's Stopped report reaches the server (the forwarder holds that report in the network until the new
# copy is on Home, as a slow network or server would): Jellyfin ends a device's older sessions at a sign-in by name, so when
# the new copy was the same device (1.7.5 freed the device slot as the close began) the Stopped was refused and the film's
# place lost. The film's place is kept: the server takes that Stopped, and the film's page offers Resume where it was left.
# Last: its new copy is the copy's second device while the old one reports (one device is checked before, in restarts and
# restart-playing-wait). The old copy is held in its close here, its Stopped with it: the new copy waits for its slot (at most
# InstallDeviceId.ClosingWait, 3 s), then takes the next.
G4-Part 'restart-playing' {
    G4L-SignInPassword
    G4L-PlayFilm
    $hold = Join-Path $script:g4Forward 'hold-stopped'
    [IO.File]::WriteAllText($hold, 'hold')
    $label = 'a quick restart during a film, with a remembered password'
    $script:g4lRestartAt = Get-Epoch
    try {
        # the new copy is on Home, signed in by name: the closing copy's Stopped, held so far, goes on, and the closing copy is
        # let go at once (held longer, FluentFin's controls' mpv, which lives in that process, stood still with it)
        $r = G4-Restart $label -Quick -WhileClosing { Remove-Item -LiteralPath $hold -ErrorAction SilentlyContinue }
    }
    finally { Remove-Item -LiteralPath $hold -ErrorAction SilentlyContinue }
    [void](Wait-Until { @(Get-Requests $script:g4lRestartAt | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
    $byName = @(Get-Requests $r.Since | Where-Object { $_.route -eq 'authenticate' -and $_.path -match '(?i)AuthenticateByName' -and $_.status -eq 200 })
    G4-Check $label 'the new copy signs in by name with the remembered password, on Home, while the old one closes' ($r.Page -eq 'home' -and $byName.Count -ge 1 -and $r.StartedWhileClosing) "page $($r.Page); $($byName.Count) sign-in(s) by name; started while the old one closed: $($r.StartedWhileClosing) (held in its close: $($r.Held))"
    $stops = @(Get-Requests $r.Since | Where-Object { $_.route -eq 'report-stopped' })
    $held = [int](G4L-Stats).held
    $stopDesc = ($stops | ForEach-Object { "$(Get-Origin $_) $($_.status) at $(if ($null -ne $_.report.position) { [math]::Round([double]$_.report.position / 1e7, 1) } else { '?' }) s" }) -join ', '
    # after the sign-in by name: held in the network (the old copy's backend stopped as its close began), or sent once the
    # old copy was let go (held in its close before it told its backend to stop: its film played on meanwhile)
    $late = if ($stops.Count -ge 1 -and $byName.Count -ge 1) { [math]::Round($stops[0].t - ($byName | ForEach-Object { [double]$_.t } | Measure-Object -Minimum).Minimum, 1) } else { $null }
    G4-Check $label 'the closing copy''s Stopped report, arriving after that sign-in, is taken by the server (not refused)' ($null -ne $late -and $late -gt 0 -and $stops.Count -eq 1 -and (Get-Origin $stops[0]) -eq 'backend' -and $stops[0].status -ge 200 -and $stops[0].status -lt 300) "$($stops.Count) Stopped: $stopDesc, $late s after the sign-in by name; held in the network until the new copy was on Home: $held"
    Say "lifecycle: device ids new since this start: $(@(G4L-DevicesSince $r.Since).Count) (the new copy is the copy's second device while the old one reports)"
    G4-Check $label 'the copy before exited cleanly (exit code 0), leaving nothing running' ($r.Code -eq 0 -and $r.Alive.Count -eq 0) "exit $(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { 'none' }) $($r.ExitMs) ms after the close"
    # what the user sees: the film's page in the new copy offers Resume where it was left
    $left = if ($stops.Count -ge 1 -and $null -ne $stops[0].report.position) { [double]$stops[0].report.position / 1e7 } else { -100 }
    $offer = G4L-ResumeOffered $left
    G4-Check $label 'the film''s page offers Resume where it was left' $offer.Ok "'$($offer.Name)'; the Stopped report said $(if ($left -ge 0) { [math]::Round($left, 1) } else { 'nothing' }) s"
}
