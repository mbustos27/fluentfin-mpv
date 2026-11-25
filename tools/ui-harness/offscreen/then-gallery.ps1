# repo polish: the README's screenshots, off-screen and silent, against the showcase library (FAKESERVER_SHOWCASE,
# tools\ui-harness\fakeserver\showcase.py). Run by tools\ui-harness\gallery.ps1; by hand:
#   $env:FAKESERVER_SHOWCASE='<manifest folder>'; $env:NIXIE_LAYOUT='<DriveIn|Cinematic|Editorial|Classic>'
#   $env:GALLERY_PART='home' (or 'detail', or 'collections': the Collections page alone)
#   run-offscreen.ps1 -DataDir <scratch> -Out <scratch> -Script wait:2 -NoA11y -Then offscreen\then-gallery.ps1
# Dot-sourced by drive.ps1 on Home after the sign-in. The window is 1920 x 1080. Shots: <Out>\shots\NN-gallery-<what>.png
#   home:   Home at the top of the page, once its pictures have landed.
#   detail: the film page, the series page, the player with Playback stats, the skip question ("Skip this every
#           episode?"), Collections, and Settings at "Look up lists online", in that order.
# Nothing here clicks a setting or changes the library. Every name here starts with GL.

$glTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Default' }
$glPart = if ($env:GALLERY_PART) { $env:GALLERY_PART } else { 'home' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function GL-Settle([int]$Ms = 1500) { Start-Sleep -Milliseconds $Ms }
function GL-Size { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); GL-Settle 1500 }
function GL-Top { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); GL-Settle 900 }
function GL-Shot([string]$What) { Shot "gallery-$What" }
# the pictures of a page: wait until the server has answered no image request for a second and a half (at most $Seconds)
function GL-Pictures([double]$Seconds = 12) {
    $since = Get-Epoch
    $quiet = Wait-Until {
        $last = @(Get-Requests $since | Where-Object { $_.path -match '/Images/' } | ForEach-Object { Get-Done $_ } | Sort-Object)[-1]
        if (-not $last) { $last = $since }
        if ((Get-Epoch) - $last -ge 1.5) { $true }
    } -Seconds $Seconds -PollMs 250
    if (-not $quiet) { Say "gallery: pictures still arriving after $Seconds s" }
    GL-Settle 800
}
# scrolls the page until the element's top is just under the top bar (a percentage of the page, 0 to 100)
function GL-ScrollTo($Element, [int]$Below = 150) {
    $win = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd).Current.BoundingRectangle
    for ($p = 0; $p -le 100; $p += 2) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $p)
        Start-Sleep -Milliseconds 120
        $top = $Element.Current.BoundingRectangle.Top
        if (-not [double]::IsInfinity($top) -and ($top - $win.Top) -le $Below) { GL-Settle 900; return $p }
    }
    GL-Settle 900
    return 100
}
function GL-Open([string]$Library, [string]$Name) {
    Open-Library $Library
    GL-Pictures 8
    Open-Card $Name
    $page = Wait-ItemPage $Name
    Check "gallery $glTheme | the $Name page opens" ([bool]$page)
    GL-Top
    GL-Pictures
}

Say "gallery ($glTheme): $glPart"
GL-Size
$glShowcase = Invoke-Server '/__harness/showcase'
Check "gallery $glTheme | the fake server runs the showcase library" ([bool]$glShowcase.on) 'set FAKESERVER_SHOWCASE'
if ($glShowcase.on) {
    Check "gallery $glTheme | the showcase manifest has no problems" (@($glShowcase.problems).Count -eq 0) (@($glShowcase.problems) -join '; ')
}

# the Collections page from its tab, at its top, its pictures in (1.7.5: also a part of its own, GALLERY_PART=collections)
function GL-Collections {
    Open-Nav 'Collections'
    Check "gallery $glTheme | the Collections tab opens the Collections page" ([bool](Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 20))
    GL-Settle 2000
    GL-Top
    GL-Pictures
    GL-Shot 'collections'
}

if ($glPart -eq 'home') {
    GL-Top
    GL-Pictures 15
    GL-Shot "home-$glTheme"
}
elseif ($glPart -eq 'detail') {
    # the film page
    GL-Open $fixture.movie.library $fixture.movie.name
    GL-Shot 'movie'

    # the series page (Step-Season goes on from here)
    GL-Open $fixture.series.library $fixture.series.name
    GL-Shot 'series'
    $script:page = 'series'

    # the player, paused at the start with its bars up, then Playback stats (the I key)
    Step-Season $false
    Step-Play
    GL-Settle 2500
    [void][OffscreenUi]::SetFocus((Find-Ui -Id 'PlayPauseButton'))
    GL-Settle 300
    Key 0x49
    $glStats = Wait-Ui -Id 'StatsPanel' -Seconds 6
    Check "gallery $glTheme | I opens Playback stats" ([bool]$glStats)
    if ($glStats) {
        [void](Wait-Ui -Name 'Playing: ' -Contains -Seconds 6)
        GL-Settle 2500
        GL-Shot 'player-stats'
        Key 0x1B
        [void](Wait-Until { if (-not (Find-Ui -Id 'StatsPanel')) { $true } } -Seconds 6 -PollMs 150)
    }

    # the skip question: the same two skips in two episodes (as then-160-skip.ps1 asks it)
    $skTheme = $glTheme; $skLook = 'Dark'
    . (Join-Path $PSScriptRoot 'skip-common.ps1')
    SK-TwoSkips
    if (SK-Go 'MediaNext') {
        SK-TwoSkips
        $glToast = Wait-Until { $n = SK-ToastName; if ($n -match ' in every episode of ') { $n } } -Seconds 14 -PollMs 250
        Check "gallery $glTheme | the skip question shows" ([bool]$glToast) "$glToast"
        if ($glToast) { GL-Settle 900; GL-Shot 'skip-question'; [void](SK-Press 'Esc'); GL-Settle 900 }
    }
    Step-Leave

    # Collections
    GL-Collections

    # Settings, at "Look up lists online"
    Open-Nav 'Settings'
    $glLists = Wait-Ui -Name 'Look up lists online' -Type 'Text' -OffscreenToo -Seconds 10
    Check "gallery $glTheme | Settings has Look up lists online" ([bool]$glLists)
    if ($glLists) {
        $glAt = GL-ScrollTo $glLists
        Say "gallery: Look up lists online at $glAt % of Settings"
        GL-Shot 'settings-lists'
    }
}
elseif ($glPart -eq 'collections') { GL-Collections }
else { Check "gallery: GALLERY_PART is home, detail or collections" $false $glPart }
