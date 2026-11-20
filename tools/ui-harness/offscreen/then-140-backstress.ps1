# Nixie 1.4.0 (bug hunt M3: the app ended once, 0xc000027b in CoreMessagingXP, about a second after Back from a film to a
# Collection page): Back and Forward between a Collection page and its films many times (BS_TIMES, default 50), on a server
# whose answers come at changing speeds, with the page's Order, Hide watched and R in between, Back by the button, the
# key and Forward again, and waits from a blink to six seconds. Each cycle checks the app is still there; when it is not,
# the run names the crash (the Application log's event 1000 for its process: module and code).
# Once per theme (NIXIE_LAYOUT), with FAKESERVER_COLLECTIONS=1: run-offscreen.ps1 -Script wait:1 -NoA11y -Then <this>.
. (Join-Path $PSScriptRoot 'chord-keys.ps1')

$bsStart = Get-Date
$bsTimes = if ($env:BS_TIMES) { [int]$env:BS_TIMES } else { 50 }
$bsItem = '^/(Users/[^/]+/)?Items/[0-9a-fA-F-]{32,36}$'
function BS-Latency([double]$Ms) { [void](Invoke-Server '/__harness/latency' 'POST' @{ default = 0; rules = @(, @($bsItem, $Ms)) }) }
function BS-Alive { try { [void](Find-Ui -Name 'zzz-none' -Type 'Text'); $true } catch { $false } }
# the crash, named: the Application log's "Application Error" (1000) for FluentFin's process since the run began
function BS-Crash {
    $hex = '0x{0:X}' -f [int]$script:ui.Id
    $e = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $bsStart } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match "Faulting process id: $hex\b" } | Select-Object -First 1
    if ($e) { (($e.Message -split "`r?`n") | Where-Object { $_ -match '^(Faulting module name|Exception code|Fault offset):' }) -join '; ' } else { "no Application Error event for process $hex (yet)" }
}
function BS-Order([string]$Name) {
    $o = Find-Ui -Id 'OrderButton'
    if ($o) { [void](Invoke-Element $o); $m = Wait-Ui -Name $Name -Type 'MenuItem' -Seconds 4; if ($m) { [void](Invoke-Element $m) } else { CK-Key 'Esc' } }
    Start-Sleep -Milliseconds 500
}
function BS-Card([int]$I) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$script:ui.Id)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $cards = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { try { -not $_.Cached.IsOffscreen -and $_.Cached.Name -match ', \d{4}' } catch { $false } })
    if ($cards.Count -eq 0) { $one = Find-Ui -Type 'ListItem'; if ($one) { $cards = @($one) } }
    if ($cards.Count -eq 0) { return $null }
    $cards[$I % $cards.Count]
}
# the Collection page shown (its Order button), opened again from the Collections library when a cycle ended elsewhere
function BS-OnCollection {
    if (Find-Ui -Id 'OrderButton') { return 'on it' }
    $tile = Find-MediaCard 'Copper and Glass'
    if (-not $tile) { Open-Library 'Collections'; Start-Sleep -Seconds 2 }
    Open-Card 'Copper and Glass'
    [void](Wait-Ui -Id 'OrderButton' -Seconds 20); Start-Sleep -Milliseconds 800
    $script:bsReopened++
    'opened again'
}
function BS-Back([int]$I) {
    if ($I % 2 -eq 0) { $b = Find-Ui -Name 'Back' -Type 'Button'; if ($b) { [void](Invoke-Element $b); return 'the Back button' } }
    CK-Key 'Alt+Left'
    'Alt+Left'
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1600, 1000); Start-Sleep -Milliseconds 1500
Open-Library 'Collections'; Start-Sleep -Seconds 3
Open-Card 'Copper and Glass'
[void](Wait-Ui -Id 'PlayButton' -Seconds 20); Start-Sleep -Seconds 1

$bsWaits = @(0.3, 1.0, 6.0, 0.6, 2.5, 4.0, 1.5)
$bsSpeeds = @(0, 1500, 300, 3000, 800)
$bsDone = 0
$script:bsReopened = 0
$bsFilms = 0
for ($i = 0; $i -lt $bsTimes; $i++) {
    $speed = $bsSpeeds[$i % $bsSpeeds.Count]
    BS-Latency 0
    # the app gone between two cycles (its window no longer FluentFin's): named, not a bare "not FluentFin's window"
    try { [void](BS-OnCollection) }
    catch {
        if (BS-Alive) { throw }
        Start-Sleep -Seconds 3
        Check "backstress: the app is still there before cycle $($i + 1)" $false "$(BS-Crash)"
        throw "the app ended before cycle $($i + 1): $(BS-Crash)"
    }
    BS-Latency $speed
    switch ($i % 6) {
        1 { BS-Order 'Name' }
        2 { CK-Key 'R'; Start-Sleep -Milliseconds (300 + 400 * ($i % 3)) }
        3 { $h = Find-Ui -Id 'HideWatchedBox'; if ($h) { try { $h.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() } catch { } }; Start-Sleep -Milliseconds 400 }
        5 { BS-Order 'Collection order' }
    }
    $card = BS-Card $i
    $name = if ($card) { $card.Current.Name } else { '(no card)' }
    if ($card) { [void][OffscreenUi]::SetFocus($card); Start-Sleep -Milliseconds 200; CK-Key 'Enter' }
    Start-Sleep -Milliseconds (800 + 700 * ($i % 4))
    # the film's page is up (the Collection page's Order button gone): Back from it; else this cycle opened nothing
    if (Find-Ui -Id 'OrderButton') { Say "backstress: cycle $($i + 1): Enter on '$name' opened nothing"; $bsDone++; continue }
    $bsFilms++
    $how = BS-Back $i
    Start-Sleep -Seconds $bsWaits[$i % $bsWaits.Count]
    if ($i % 6 -eq 4) {
        # Forward to the film, and Back again at once
        CK-Key 'Alt+Right'; Start-Sleep -Milliseconds (200 + 300 * ($i % 3)); CK-Key 'Alt+Left'; $how += ', Forward, Alt+Left'
        Start-Sleep -Seconds 1.5
    }
    if (-not (BS-Alive)) {
        Start-Sleep -Seconds 3
        Check "backstress: the app is still there after cycle $($i + 1)" $false "opened '$name', Back by $how, server $speed ms: $(BS-Crash)"
        throw "the app ended in cycle $($i + 1): $(BS-Crash)"
    }
    $bsDone++
    if ($i % 10 -eq 9) { $o = Find-Ui -Id 'OrderButton'; Say "backstress: $bsDone cycles, $bsFilms films opened and left; focus $(CK-Focus); order '$(if ($o) { $o.Current.Name })'" }
}
BS-Latency 0
$bsSince = ($bsStart.ToUniversalTime() - [datetime]'1970-01-01').TotalSeconds
$guarded = @(CK-Log $bsSince '^\[ui\] ')
$unhandled = @(CK-Log $bsSince '^Unhandled exception')
$trail = @(CK-Log $bsSince '^\[first-chance\]')
Check "backstress: $bsDone of $bsTimes cycles ($bsFilms films opened and left by Back), the app still there" ($bsDone -eq $bsTimes -and $bsFilms -ge $bsTimes * 0.8) "page opened again $($script:bsReopened) times; $($trail.Count) first-chance line(s): $(($trail | Select-Object -First 3) -join ' | ')"
# (review of wip/140-fix2) the guard must not hide the crash: a queued callback that failed ("[ui] ... failed") or an exception
# XAML raised (a layout cycle) fails the run, the app alive or not
Check 'backstress: no queued work failed ([ui]) and no unhandled exception (a layout cycle)' ($guarded.Count -eq 0 -and $unhandled.Count -eq 0) "$($guarded.Count) [ui] line(s), $($unhandled.Count) unhandled: $((@($guarded) + @($unhandled) | Select-Object -First 5) -join ' | ')"
