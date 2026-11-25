# Nixie 1.7.5, the basics gate, class 7 (settings): every switch, choice and number on the Settings page keeps the value it was
# given across a restart (read back from the page as the user sees it: the switch's state, the choice shown, the number);
# a theme switch in the middle of a page keeps that page (its title, the keyboard in it, drawn, Back still goes back); the
# layout theme chosen on Settings is the one in use after a restart; Check for updates against the fake server's release feed
# (never GitHub) says what it found: a newer version, then up to date.
#   run-gate.ps1 ... -Checks settings-restart      (manifest\g4.psd1)
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then tools\gate\checks\7-settings.ps1
# The user's server-side choices (Preferred audio and subtitle language, Subtitles, Play the next episode automatically) are
# saved on the server; the fake server keeps no user configuration, so with it they are listed and not judged (a QA server
# judges them). G4_PARTS picks parts: updates, switches, midpage, theme. Dot-sourced by drive.ps1: names start with g4s / G4S- (g4-common.ps1: g4 / G4-).

$script:g4DefaultId = 'settings-restart'
. (Join-Path $PSScriptRoot 'g4-common.ps1')
$g4sDot = " $([char]0xB7) "

function G4S-Open {
    Open-Nav 'Settings'
    $ok = Wait-Until { Find-Ui -Name 'Check now' -Type 'Button' -OffscreenToo } -Seconds 20 -PollMs 200
    if (-not $ok) { throw 'Settings did not open (no Check now)' }
    Start-Sleep -Milliseconds 1500
}
function G4S-Pattern($Element, $Pattern) { $p = $null; if ($Element.TryGetCurrentPattern($Pattern, [ref]$p)) { $p } else { $null } }
# every expander of the page opened (their switches exist only then)
function G4S-Expand {
    foreach ($e in @([OffscreenUi]::All($script:hwnd, $script:ui.Id))) {
        try {
            if ($e.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Button) { continue }
            $x = G4S-Pattern $e ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($x -and $x.Current.ExpandCollapseState -eq 'Collapsed' -and -not (G4S-Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern))) { $x.Expand(); Start-Sleep -Milliseconds 150 }
        } catch {}
    }
    Start-Sleep -Milliseconds 800
}
# the page's switches, choices and numbers: kind, key (name and id), the element; enabled ones only, the window's own left out
function G4S-Controls {
    $list = New-Object System.Collections.ArrayList
    foreach ($e in @([OffscreenUi]::All($script:hwnd, $script:ui.Id))) {
        try {
            $c = $e.Cached
            if (-not $e.Current.IsEnabled -or -not $c.Name) { continue }
            $kind = $null
            if ($c.ControlType -eq [System.Windows.Automation.ControlType]::Button -and (G4S-Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern))) { $kind = 'switch' }
            elseif ($c.ControlType -eq [System.Windows.Automation.ControlType]::ComboBox) { $kind = 'choice' }
            elseif ($c.ControlType -eq [System.Windows.Automation.ControlType]::Spinner -and (G4S-Pattern $e ([System.Windows.Automation.RangeValuePattern]::Pattern))) { $kind = 'number' }
            if (-not $kind) { continue }
            # a switch whose name carries its state ("Compact Home Off") is keyed without it
            $name = if ($kind -eq 'switch') { $c.Name -replace ' (On|Off)$', '' } else { $c.Name }
            [void]$list.Add([pscustomobject]@{ Kind = $kind; Key = "$name$(if ($c.AutomationId) { " ($($c.AutomationId))" })"; Name = $name; Element = $e })
        } catch {}
    }
    # one item per control down the pipeline (callers wrap the call in @())
    $list
}
function G4S-Value($Control) {
    $e = $Control.Element
    switch ($Control.Kind) {
        'switch' { "$((G4S-Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState)" }
        'choice' { $s = G4S-Pattern $e ([System.Windows.Automation.SelectionPattern]::Pattern); if ($s) { (@($s.Current.GetSelection()) | ForEach-Object { $_.Current.Name }) -join ';' } else { '' } }
        'number' { "$((G4S-Pattern $e ([System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value)" }
    }
}
# a different value: the switch pressed; the choice after the one shown (the first when it was the last); the number up by 2
function G4S-Change($Control) {
    $e = $Control.Element
    switch ($Control.Kind) {
        'switch' { (G4S-Pattern $e ([System.Windows.Automation.TogglePattern]::Pattern)).Toggle() }
        'choice' {
            $x = G4S-Pattern $e ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $x.Expand(); Start-Sleep -Milliseconds 600
            $items = @($e.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
                            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
            $now = G4S-Value $Control
            $at = [array]::IndexOf(@($items | ForEach-Object { $_.Current.Name }), $now)
            if ($items.Count -ge 2) { (G4S-Pattern $items[($at + 1) % $items.Count] ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select() }
            Start-Sleep -Milliseconds 300
            try { $x.Collapse() } catch {}
        }
        'number' { $r = G4S-Pattern $e ([System.Windows.Automation.RangeValuePattern]::Pattern); $v = $r.Current.Value; $r.SetValue($(if ($v + 2 -le $r.Current.Maximum) { $v + 2 } else { $v - 2 })) }
    }
    Start-Sleep -Milliseconds 500
}
# the server's own settings (saved on the server, not on this PC)
$g4sServerSide = @('Preferred audio language', 'Preferred subtitle language', 'Subtitles', 'Play the next episode automatically')

G4-SignInRemembered

G4-Part 'updates' {
    # this build's version, and one newer: the fake server's release answer (drive.ps1 points NIXIE_UPDATE_API at it)
    $base = [version](Get-Item -LiteralPath $Exe).VersionInfo.FileVersion
    $newer = '{0}.{1}.{2}' -f $base.Major, ($base.Minor + 1), 0
    [void](Invoke-Server '/__harness/release' 'POST' @{ version = $newer; mode = 'ok' })
    G4S-Open
    $check = Find-Ui -Name 'Check now' -Type 'Button' -OffscreenToo
    $si = G4S-Pattern $check ([System.Windows.Automation.ScrollItemPattern]::Pattern); if ($si) { $si.ScrollIntoView(); Start-Sleep -Milliseconds 500 }
    $at = Get-Epoch
    [void](Invoke-Element $check)
    $said = Wait-Until { Find-Ui -Name "Nixie $newer is available" -Contains -Type 'Text' -OffscreenToo } -Seconds 20 -PollMs 300
    $asked = @(Get-Requests $at | Where-Object { $_.path -eq '/__harness/release/latest' })
    G4-Check 'Check now, a newer version out' "Settings says Nixie $newer is available" ([bool]$said) "$(if ($said) { $said.Current.Name } else { 'not said' }); $($asked.Count) request(s) to the fake feed"
    # back to no newer version: up to date (Check now answers from its last answer for 15 s)
    [void](Invoke-Server '/__harness/release' 'POST' @{ version = "$($base.Major).$($base.Minor).$($base.Build)"; mode = 'ok' })
    Start-Sleep -Seconds 16
    [void](Invoke-Element (Find-Ui -Name 'Check now' -Type 'Button' -OffscreenToo))
    $fresh = Wait-Until { Find-Ui -Name "You're up to date$($g4sDot)checked " -Contains -Type 'Text' -OffscreenToo } -Seconds 20 -PollMs 300
    G4-Check 'Check now, nothing newer' "Settings says you're up to date, and when it checked" ([bool]$fresh) "$(if ($fresh) { $fresh.Current.Name } else { 'not said' })"
    $gh = @(Get-Requests 0 | Where-Object { $_.path -match 'github' })
    G4-Check 'Check now' 'only the fake feed was asked (never GitHub)' ($gh.Count -eq 0) "$($gh.Count) request(s) naming GitHub"
}

# every control of the page, by key: the page scrolled through (a repeater's cards, as Made by Nixie's switches, exist only near
# the view), each expander opened on the way
$g4sStops = @(0, 12, 25, 37, 50, 62, 75, 87, 100)
function G4S-All {
    $seen = [ordered]@{}
    foreach ($pct in $g4sStops) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); Start-Sleep -Milliseconds 400
        G4S-Expand
        foreach ($c in @(G4S-Controls)) { if (-not $seen.Contains($c.Key)) { $c | Add-Member -NotePropertyName Stop -NotePropertyValue $pct; $seen[$c.Key] = $c } }
    }
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 300
    @($seen.Values)
}
# a control as it is now (found again where it is, else the page scrolled through), scrolled into view
function G4S-Get([string]$Key, $Stop = $null) {
    $order = @(-1) + $(if ($null -ne $Stop) { @($Stop) + @($g4sStops | Where-Object { $_ -ne $Stop }) } else { $g4sStops })
    foreach ($pct in $order) {
        if ($pct -ge 0) { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); Start-Sleep -Milliseconds 400; G4S-Expand }
        $c = @(G4S-Controls | Where-Object { $_.Key -eq $Key }) | Select-Object -First 1
        if ($c) {
            $si = G4S-Pattern $c.Element ([System.Windows.Automation.ScrollItemPattern]::Pattern)
            if ($si) { try { $si.ScrollIntoView(); Start-Sleep -Milliseconds 250 } catch {} }
            return $c
        }
    }
    $null
}

G4-Part 'switches' {
    G4S-Open
    $before = @(G4S-All)
    Say "$(G4-Id): $($before.Count) switches, choices and numbers on Settings: $(($before | ForEach-Object { "$($_.Kind) $($_.Key)=$(G4S-Value $_)" }) -join '; ')"
    $expect = [ordered]@{}
    # the player on its own, after: another player hides the mpv player's own settings (its controls, the skip settings)
    $player = @($before | Where-Object { $_.Key -eq 'Player' }) | Select-Object -First 1
    $before = @($before | Where-Object { $_.Key -ne 'Player' })
    foreach ($b in $before) {
        $c = G4S-Get $b.Key $b.Stop
        if (-not $c) { G4-Check $b.Key 'is on the page to change' $false ''; continue }
        $old = G4S-Value $c
        try { G4S-Change $c } catch { G4-Check $c.Key 'can be changed' $false $_.Exception.Message; continue }
        # the page may have redrawn around it (a switch that shows more): found again by its key
        $again = G4S-Get $c.Key $b.Stop
        $new = if ($again) { G4S-Value $again } else { $null }
        G4-Check $c.Key "the $($c.Kind) shows the new value at once" ($null -ne $new -and $new -ne $old) "was '$old', now '$new'"
        if ($null -ne $new -and $new -ne $old) { $expect[$c.Key] = @($c.Kind, $old, $new, $c.Name) }
    }
    Start-Sleep -Seconds 2
    $r = G4-Restart 'settings'
    G4-Check 'restart' 'Nixie exits cleanly and starts again on Home, signed in' ($r.Code -eq 0 -and $r.Page -eq 'home') "exit $(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { "none: still running $CloseSeconds s after the close" }), page $($r.Page)"
    if ($r.Page -ne 'home') { throw 'not on Home after the restart' }
    G4S-Open
    $serverKept = -not (@(Get-Requests 0 | Where-Object { $_.path -match '^/Users/Configuration$' -and $_.status -eq 404 }).Count)
    $stops = @{}; foreach ($b in $before) { $stops[$b.Key] = $b.Stop }
    foreach ($key in $expect.Keys) {
        $kind, $old, $new, $name = $expect[$key]
        $c = G4S-Get $key $stops[$key]
        $now = if ($c) { G4S-Value $c } else { '<not on the page>' }
        if ($name -in $g4sServerSide -and -not $serverKept) {
            Say "$(G4-Id): '$key' is the server's setting and the fake server keeps no user configuration: '$old' -> '$new', after the restart '$now' (not judged)"
            continue
        }
        G4-Check $key "the $kind keeps its value across a restart" ($now -eq $new) "set '$old' -> '$new'; after the restart '$now'"
    }
    G4-Check 'restart' 'every switch, choice and number on Settings was changed and read back' ($expect.Count -ge 26) "$($expect.Count) of $($before.Count) changed"
    # the player: another one chosen, kept across a restart, then mpv again (the run's other checks play with it)
    if ($player) {
        $c = G4S-Get 'Player' $player.Stop
        $old = G4S-Value $c
        G4S-Change $c
        $new = G4S-Value (G4S-Get 'Player' $player.Stop)
        G4-Check 'Player' 'the choice shows the new value at once' ($new -and $new -ne $old) "was '$old', now '$new'"
        Start-Sleep -Seconds 2
        $r = G4-Restart 'settings, the player'
        G4-Check 'restart' 'Nixie exits cleanly and starts again on Home, signed in' ($r.Code -eq 0 -and $r.Page -eq 'home') "exit $(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { "none: still running $CloseSeconds s after the close" }), page $($r.Page)"
        if ($r.Page -ne 'home') { throw 'not on Home after the restart' }
        G4S-Open
        $c = G4S-Get 'Player' $player.Stop
        $now = if ($c) { G4S-Value $c } else { '<not on the page>' }
        G4-Check 'Player' 'the choice keeps its value across a restart' ($now -eq $new) "set '$old' -> '$new'; after the restart '$now'"
        # back to mpv
        for ($i = 0; $i -lt 4 -and $c -and (G4S-Value $c) -ne $old; $i++) { G4S-Change $c; $c = G4S-Get 'Player' $player.Stop }
    }
}

# the layout theme switched in place on a series', a season's and a film's page (the dev hook's kind 6: ThemeService.Choose,
# what any caller can do): from the run's theme to each other theme and back, so the four runs meet every ordered pair. After
# each, the same page shows, drawn, the keyboard in it, Nixie still running (1.7.4: Drive-in's film page ended Nixie,
# DriveInBoardTextBrush not found, when the dictionary changed under it)
$g4sThemes = @('DriveIn', 'Cinematic', 'Editorial', 'Classic')
function G4S-SwitchTo([string]$Theme) {
    $at = Get-Epoch
    [void][G4Win]::Pointer($script:hwnd, 6, 0, 0, [array]::IndexOf($g4sThemes, $Theme) + 1)
    $line = Wait-Until { @(G4-Log $at '\[harness\] the layout theme chosen: ') | Select-Object -Last 1 } -Seconds 8 -PollMs 300
    Start-Sleep -Seconds 2
    if ($line -match 'chosen: "?(\w+)') { $Matches[1] } else { '?' }
}
function G4S-OnPage([string]$Kind) {
    switch ($Kind) {
        'series' { [bool](Wait-ItemPage $fixture.series.name 10) -and [bool](Wait-Ui -Name "$($fixture.season.name) details" -Type 'Hyperlink' -OffscreenToo -Seconds 5) }
        'season' { [bool](Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.season.name) of ", '') } -Seconds 10 -PollMs 300) }
        'film' { [bool](Wait-ItemPage $fixture.movie.name 10) }
    }
}
G4-Part 'midpage' {
    $home_ = Get-GateTheme
    if ($g4sThemes -notcontains $home_) { throw "the run's theme '$home_' is not one of $($g4sThemes -join ', ')" }
    foreach ($kind in 'series', 'season', 'film') {
        switch ($kind) {
            'series' { Step-Series $false }
            'season' { Step-Season $false }
            'film' { Open-Library $fixture.movie.library; Open-Card $fixture.movie.name }
        }
        if (-not (G4S-OnPage $kind)) { throw "no $kind page" }
        Start-Sleep -Seconds 2
        foreach ($other in @($g4sThemes | Where-Object { $_ -ne $home_ })) {
            foreach ($pair in @(@($home_, $other), @($other, $home_))) {
                $chosen = G4S-SwitchTo $pair[1]
                $script:ui.Refresh()
                if ($script:ui.HasExited) { throw "Nixie ended switching $($pair[0]) to $($pair[1]) on the $kind page: $(G4-LastCrash (Get-Date).AddMinutes(-1))" }
                $page = G4S-OnPage $kind
                $focus = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
                $f = if ($focus) { $focus | ConvertFrom-Json } else { $null }
                $lit = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.1, 0.15, 0.9, 0.9)
                G4-Check "$($pair[0]) to $($pair[1]) on the $kind page" 'the same page stays, drawn, with the keyboard in it' ($chosen -eq $pair[1] -and $page -and $f -and $f.type -ne 'Window' -and $lit -gt 5) "theme now $chosen; page shown $page; focus $(if ($f) { "$($f.type) '$($f.name)'" } else { 'nothing' }); $lit% not black"
            }
        }
    }
    Key 0xA6
    $lib = Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 12 -PollMs 300
    G4-Check 'Back after the theme switches' 'goes back to the library the film came from' ([bool]$lib -and -not (Find-Ui -Name $fixture.movie.name -Id 'LogoImage')) ''
}

G4-Part 'theme' {
    # a theme chosen on Settings, come to from the movie page: Back shows that page in the new theme; and it is the one a
    # start uses (NIXIE_LAYOUT, the run's override, left out of the next start)
    Open-Library $fixture.movie.library
    Open-Card $fixture.movie.name
    if (-not (Wait-ItemPage $fixture.movie.name)) { throw 'no movie page' }
    Start-Sleep -Seconds 2
    G4S-Open
    $tiles = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -and $_.Cached.Name -match '^(Drive-in|Cinematic|Editorial|Classic)\. ' } catch { $false } })
    $inUse = @($tiles | Where-Object { $_.Current.Name -match 'In use\.?$' }) | Select-Object -First 1
    $pick = @($tiles | Where-Object { $_.Current.Name -notmatch 'In use\.?$' }) | Select-Object -First 1
    if (-not $pick) { throw "no theme tile to choose ($($tiles.Count) tiles)" }
    $want = ($pick.Current.Name -split '\.')[0]
    [void](Invoke-Element $pick)
    $now = Wait-Until { $t = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.Name -like "$want. *In use*" } catch { $false } }) | Select-Object -First 1; $t } -Seconds 8 -PollMs 300
    G4-Check "theme $want chosen" 'its tile says In use, on Settings still' ([bool]$now -and [bool](Find-Ui -Name 'Check now' -Type 'Button' -OffscreenToo)) "was $(if ($inUse) { ($inUse.Current.Name -split '\.')[0] })"
    Start-Sleep -Seconds 2
    Key 0xA6
    $back = Wait-ItemPage $fixture.movie.name 12
    $script:ui.Refresh()
    G4-Check "theme $want chosen" 'Back shows the movie page again, in the new theme, Nixie still running' ([bool]$back -and -not $script:ui.HasExited) "page title shown $([bool]$back); Nixie running $(-not $script:ui.HasExited)"
    if ($script:ui.HasExited) { throw 'Nixie ended after Back' }
    Start-Sleep -Seconds 1
    $g4sLayout = $env:NIXIE_LAYOUT
    $env:NIXIE_LAYOUT = $null
    try { $r = G4-Restart 'theme' } finally { $env:NIXIE_LAYOUT = $g4sLayout }
    G4-Check 'restart' 'Nixie exits cleanly and starts again on Home, signed in' ($r.Code -eq 0 -and $r.Page -eq 'home') "exit $(if ($null -ne $r.Code) { '0x{0:X8}' -f $r.Code } else { "none: still running $CloseSeconds s after the close" }), page $($r.Page)"
    G4S-Open
    $kept = Wait-Until { @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.Name -like "$want. *In use*" } catch { $false } }) | Select-Object -First 1 } -Seconds 8 -PollMs 300
    G4-Check "theme $want chosen" 'it is the theme in use after a restart' ([bool]$kept) ''
}
