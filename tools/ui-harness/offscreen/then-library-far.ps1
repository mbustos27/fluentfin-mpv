# Nixie 1.4.0, the release's layout cycle: a library scrolled far in one go (the scroll bar's jump, Narrator's scrolling, UI
# Automation's SetScrollPercent), then a title far above opened by Narrator's Invoke, by the keyboard (Enter on the title the
# keyboard was left on) and closed again (Esc, Invoke). In Editorial this hung the window for about 30 s, logged a
# LayoutCycleException and left the window unusable (the rows' posters got their width only once in the window, so every row
# the grid made changed height after it was laid out). Run in each theme:
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'; run-offscreen.ps1 ... -Script home -Then offscreen\then-library-far.ps1
# Checks: the window answers within 2 s all along (a WM_NULL every 200 ms) and settles after the far scroll (every answer
# within 400 ms 10 to 15 s after it); no LayoutCycleException in FluentFin's log; what
# the user then sees: in Editorial the keyboard follows a far scroll to the first poster in view (Narrator's Invoke then
# reaches a title drawn out of view; Enter opens what is in view), details open in view, the keyboard on Play when Enter
# opened them, the poster "expanded", and Esc or a second Invoke closes them with the poster in view (the keyboard on it
# after Esc); the other themes' item page opens and Back returns. The window is 1920 x 1080 for the checks (the desktop's own size put back after). Shots
# <tag>-far-<name>.png. Dot-sourced by drive.ps1: every name here starts with lf.

$lfTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
$lfEditorial = $env:NIXIE_LAYOUT -eq 'Editorial'
$lfTitle = $fixture.movie.name

if (-not ('LfProbe' -as [type])) {
    Add-Type @"
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class LfProbe {
  [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
  // ms the window's thread took to answer a WM_NULL, -1 when it did not answer within timeoutMs (its UI thread was busy)
  public static int Answer(IntPtr h, int timeoutMs) {
    var sw = Stopwatch.StartNew(); IntPtr r;
    var ok = SendMessageTimeout(h, 0, IntPtr.Zero, IntPtr.Zero, 0, (uint)timeoutMs, out r);
    return ok == IntPtr.Zero ? -1 : (int)sw.ElapsedMilliseconds;
  }
}
"@
}

# the window answers a WM_NULL within 2 s, every 200 ms for $Seconds: the result as "n probes, m unanswered, slowest x ms"
function Lf-Responsive([double]$Seconds = 5) {
    $end = (Get-Date).AddSeconds($Seconds); $n = 0; $missed = 0; $slowest = 0
    while ((Get-Date) -lt $end) {
        $a = [LfProbe]::Answer($script:hwnd, 2000); $n++
        if ($a -lt 0) { $missed++ } elseif ($a -gt $slowest) { $slowest = $a }
        Start-Sleep -Milliseconds 200
    }
    [pscustomobject]@{ Ok = ($missed -eq 0 -and $n -gt 0); Slowest = $slowest; Text = "$n answers asked, $missed not within 2 s, slowest $slowest ms" }
}
function Lf-Cycles {
    $n = 0
    foreach ($lfF in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)) {
        $n += @(Select-String -LiteralPath $lfF.FullName -Pattern 'LayoutCycleException' -SimpleMatch).Count
    }
    $n
}
function Lf-Settle([int]$Ms = 800) { Start-Sleep -Milliseconds $Ms }
function Lf-Shot([string]$Name) { Shot "$lfTag-far-$Name" }
function Lf-Focus { $lfF = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($lfF) { $lfF | ConvertFrom-Json } else { $null } }
function Lf-Desc($F) { if ($F) { "$($F.type) '$($F.name)'" } else { 'nothing' } }
# the library afresh (Home, then the library): its first title's card drawn
function Lf-Fresh {
    Open-Nav 'Home'; Lf-Settle 800
    Open-Library $fixture.movie.library
    $lfCard = Wait-Until { Find-MediaCard $lfTitle } -Seconds 30 -PollMs 200
    Lf-Settle 2000
    [bool]$lfCard
}
# the grid scrolled far in one go (the scroll bar's jump): its percent after
function Lf-Jump([double]$Percent = 58) { $p = [OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $Percent); Lf-Settle 300; $p }
function Lf-Window { $lfR = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$lfR); $lfR }
# whether an element shows inside the window (not scrolled out of it)
function Lf-Shows($Element) {
    if (-not $Element) { return $false }
    try { if ($Element.Current.IsOffscreen) { return $false }; $lfR = $Element.Current.BoundingRectangle } catch { return $false }
    $w = Lf-Window
    -not $lfR.IsEmpty -and $lfR.Top -ge $w.T -and $lfR.Bottom -le $w.B
}
function Lf-Details([string]$Name = $lfTitle) { Find-Ui -Name "Details of $Name" -Type 'Group' -Contains -OffscreenToo }
# the focused element (Lf-Focus) shows inside the window
function Lf-FocusShows($F) {
    if (-not $F -or -not $F.rect) { return $false }
    $w = Lf-Window
    $F.rect[3] -gt 0 -and $F.rect[1] -ge $w.T -and ($F.rect[1] + $F.rect[3]) -le $w.B
}
# a title's name from its card's ("Lonely Mirrors, 2020, watched": Lonely Mirrors)
function Lf-TitleOf([string]$Name) { ($Name -split ', ')[0] }
# a title drawn but out of view (what Narrator's cursor can reach past the screen), or null
function Lf-DrawnOutOfView {
    foreach ($e in [OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))) {
        try { if ($e.Cached.IsOffscreen -and $e.Cached.Name -like '*, *') { return $e } } catch {}
    }
    $null
}
function Lf-State($Card) {
    $p = $null
    try { if ($Card.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { return "$($p.Current.ExpandCollapseState)" } } catch {}
    ''
}

$lfCyclesBefore = Lf-Cycles
Say "far ($lfTag): LayoutCycleException lines before: $lfCyclesBefore"
# a 1920 x 1080 window whatever the desktop's size (the grid's rows, and so the time a far scroll takes, follow the width)
$lfWindowWas = Lf-Window
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Lf-Settle 1500

# --- 1. Narrator's way: the scroll bar's jump, then Invoke on the first title (far above, still drawn: the keyboard's)
if (Lf-Fresh) {
    $lfF = Lf-Focus; Say "far ($lfTag): on arrival the keyboard is on $(Lf-Desc $lfF)"
    $lfPc = Lf-Jump
    $lfR = Lf-Responsive 4
    Check "far ($lfTag): a far scroll in one go: the window answers within 2 s" $lfR.Ok "$($lfR.Text) (the scroll bar read $([int]$lfPc) % at once)"
    # and then it settles (the release's second fault: each image that arrived re-made the rows from the top, for 30 s):
    # 10 to 15 s after the scroll, every answer within 400 ms (the first seconds may still lay out a pass or two)
    $lfR = Lf-Responsive 6
    Say "far ($lfTag): 4 to 10 s after the far scroll: $($lfR.Text)"
    $lfR = Lf-Responsive 5
    Check "far ($lfTag): the grid settles after the far scroll (10 to 15 s after it: every answer within 400 ms)" ($lfR.Ok -and $lfR.Slowest -le 400) $lfR.Text
    $lfShown = Find-Ui -Type 'ListItem' -Contains -Name ', '
    Check "far ($lfTag): after the far scroll, titles show in view" ([bool]$lfShown) $(if ($lfShown) { $lfShown.Current.Name } else { 'none' })
    if ($lfEditorial) {
        # Nixie 1.4.0: the keyboard follows a far scroll (to the first poster in view), as it was: here the program's
        $lfF = Lf-Focus
        Check "far ($lfTag): the keyboard follows the far scroll: on a poster in view" ($lfF -and $lfF.class -eq 'EditorialPoster' -and (Lf-FocusShows $lfF) -and -not $lfF.name.StartsWith($lfTitle)) "keyboard on $(Lf-Desc $lfF)"
    }
    # Narrator's Invoke on a title past the screen: the first title, if the grid still draws it, else any drawn out of view
    $lfCard = Find-MediaCard $lfTitle
    if (-not $lfCard -or -not $lfCard.Current.IsOffscreen) { $lfCard = Lf-DrawnOutOfView }
    if (-not $lfCard) {
        Say "far ($lfTag): no title is drawn out of view after the far scroll: the Invoke path does not apply"
    } else {
        $lfTitle = Lf-TitleOf $lfCard.Current.Name
        Say "far ($lfTag): '$lfTitle' drawn, out of view: $([bool]$lfCard.Current.IsOffscreen)"
        [OffscreenUi]::InvokeAsync([OffscreenUi]::InvokePatternOf($lfCard))
        $lfR = Lf-Responsive 5
        Check "far ($lfTag): Invoke on a title out of view: the window answers within 2 s" $lfR.Ok $lfR.Text
        if ($lfEditorial) {
            $lfD = Wait-Until { Lf-Details } -Seconds 6 -PollMs 200
            Check "far ($lfTag): Invoke on a title out of view: its details open, in view" (Lf-Shows $lfD) $(if ($lfD) { $lfD.Current.Name } else { 'no details' })
            $lfCard = Find-MediaCard $lfTitle
            Check "far ($lfTag): Invoke on a title out of view: the poster shows, and says expanded" ((Lf-Shows $lfCard) -and (Lf-State $lfCard) -eq 'Expanded') "state '$(Lf-State $lfCard)'"
            Lf-Shot 'invoked'
            # the way out: Invoke again (Narrator's collapse), after another far scroll
            [void](Lf-Jump 70)
            $lfCard = Find-MediaCard $lfTitle
            if (-not $lfCard) {
                Say "far ($lfTag): '$lfTitle' is not drawn from far below: the second Invoke does not apply"
            } else {
                [OffscreenUi]::InvokeAsync([OffscreenUi]::InvokePatternOf($lfCard))
                $lfR = Lf-Responsive 4
                Check "far ($lfTag): Invoke again from far below: the window answers within 2 s" $lfR.Ok $lfR.Text
                $lfGone = Wait-Until { -not (Lf-Details) } -Seconds 5 -PollMs 200
                $lfCard = Find-MediaCard $lfTitle
                Check "far ($lfTag): Invoke again from far below: the details close, the poster in view, collapsed" ([bool]$lfGone -and (Lf-Shows $lfCard) -and (Lf-State $lfCard) -eq 'Collapsed') "state '$(Lf-State $lfCard)'"
            }
        } else {
            $lfPage = Wait-ItemPage $lfTitle 15
            Check "far ($lfTag): Invoke on a title out of view: its page opens" ([bool]$lfPage) ''
            if ($lfPage) { Key 0xA6; Lf-Settle 2500 }
            $lfBack = Wait-Until { Find-MediaCard $lfTitle } -Seconds 8 -PollMs 200
            $lfR = Lf-Responsive 3
            Check "far ($lfTag): Back from that page: the library, the window answering" ([bool]$lfBack -and $lfR.Ok) $lfR.Text
        }
    }
} else {
    Check "far ($lfTag): the library opens" $false $lfTitle
}

# --- 2. the keyboard's way: Home (the first title, by the keyboard), the scroll bar's jump, then Enter; Esc from far below
$lfTitle = $fixture.movie.name
if (Lf-Fresh) {
    Key 0x24; Lf-Settle 800   # Home
    $lfF = Lf-Focus
    $lfOnFirst = $lfF -and $lfF.name.StartsWith($lfTitle)
    Say "far ($lfTag): after Home the keyboard is on $(Lf-Desc $lfF)"
    [void](Lf-Jump)
    $lfR = Lf-Responsive 3
    Check "far ($lfTag): keyboard first, a far scroll: the window answers within 2 s" $lfR.Ok $lfR.Text
    if (-not $lfOnFirst) {
        Say "far ($lfTag): Home did not put the keyboard on '$lfTitle': the Enter path does not apply"
    } elseif ($lfEditorial) {
        # Nixie 1.4.0: the keyboard follows the far scroll (as it was: the keyboard's), Enter opens what is in view
        Lf-Settle 700
        $lfF = Lf-Focus
        $lfFollowed = $lfF -and $lfF.class -eq 'EditorialPoster' -and (Lf-FocusShows $lfF) -and -not $lfF.name.StartsWith($lfTitle)
        Check "far ($lfTag): keyboard first, the keyboard follows the far scroll: on a poster in view" $lfFollowed "keyboard on $(Lf-Desc $lfF)"
        if ($lfFollowed) {
            $lfHere = Lf-TitleOf $lfF.name
            Key 0x0D; Lf-Settle 300
            $lfR = Lf-Responsive 4
            Check "far ($lfTag): Enter on the poster the keyboard followed to: the window answers within 2 s" $lfR.Ok $lfR.Text
            $lfD = Wait-Until { Lf-Details $lfHere } -Seconds 6 -PollMs 200
            $lfF = Lf-Focus
            Check "far ($lfTag): Enter there: its details open in view, the keyboard on Play" ((Lf-Shows $lfD) -and $lfF -and $lfF.automationId -eq 'PlayButton') "$(if ($lfD) { $lfD.Current.Name } else { 'no details' }); keyboard on $(Lf-Desc $lfF)"
            Lf-Shot 'entered'
            # far below with the keyboard in the details: the keyboard follows again, Enter there opens that poster's details
            # (the ones far above close), Esc closes them: the keyboard back on the poster, in view
            [void](Lf-Jump 85)
            $lfR = Lf-Responsive 4
            Check "far ($lfTag): from the details, a far scroll: the window answers within 2 s" $lfR.Ok $lfR.Text
            $lfF = Lf-Focus
            $lfFollowed = $lfF -and $lfF.class -eq 'EditorialPoster' -and (Lf-FocusShows $lfF)
            Check "far ($lfTag): from the details, the keyboard follows the far scroll: on a poster in view" $lfFollowed "keyboard on $(Lf-Desc $lfF)"
            if ($lfFollowed) {
                $lfThere = Lf-TitleOf $lfF.name
                Key 0x0D; Lf-Settle 300
                $lfD = Wait-Until { Lf-Details $lfThere } -Seconds 6 -PollMs 200
                Check "far ($lfTag): Enter there: its details open in view, the ones far above close" ((Lf-Shows $lfD) -and -not (Lf-Details $lfHere)) "$(if ($lfD) { $lfD.Current.Name } else { 'no details' })"
                Key 0x1B; Lf-Settle 300
                $lfR = Lf-Responsive 3
                $lfGone = Wait-Until { -not (Lf-Details $lfThere) } -Seconds 5 -PollMs 200
                $lfF = Lf-Focus
                Check "far ($lfTag): Esc: the details close, the keyboard back on the poster, in view; the window answering" ([bool]$lfGone -and $lfF -and $lfF.name.StartsWith($lfThere) -and (Lf-FocusShows $lfF) -and $lfR.Ok) "keyboard on $(Lf-Desc $lfF); $($lfR.Text)"
                Lf-Shot 'escaped'
            }
        }
    } else {
        Key 0x0D; Lf-Settle 300   # Enter on the title the keyboard was left on, far above
        $lfR = Lf-Responsive 5
        Check "far ($lfTag): Enter on the keyboard's title far above: the window answers within 2 s" $lfR.Ok $lfR.Text
        $lfPage = Wait-ItemPage $lfTitle 15
        Check "far ($lfTag): Enter on the keyboard's title far above: its page opens" ([bool]$lfPage) ''
        if ($lfPage) { Key 0xA6; Lf-Settle 2500 }
        $lfBack = Wait-Until { Find-MediaCard $lfTitle } -Seconds 8 -PollMs 200
        $lfR = Lf-Responsive 3
        Check "far ($lfTag): Back from that page: the library, the window answering" ([bool]$lfBack -and $lfR.Ok) $lfR.Text
    }
} else {
    Check "far ($lfTag): the library opens again" $false $lfTitle
}

# --- 3. the window's height changed far down (the keyboard's title far above): no cycle either
if (Lf-Fresh) {
    $lfWas = Lf-Window
    foreach ($lfPc in 20, 40, 60) { [void](Lf-Jump $lfPc) }
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $lfWas.R - $lfWas.L, $lfWas.B - $lfWas.T - 180); Lf-Settle 300
    $lfR = Lf-Responsive 4
    Check "far ($lfTag): far down, the window made shorter: it answers within 2 s" $lfR.Ok $lfR.Text
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $lfWas.R - $lfWas.L, $lfWas.B - $lfWas.T); Lf-Settle 1500
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $lfWindowWas.R - $lfWindowWas.L, $lfWindowWas.B - $lfWindowWas.T); Lf-Settle 1000
$lfCyclesAfter = Lf-Cycles
Check "far ($lfTag): no LayoutCycleException in FluentFin's log" ($lfCyclesAfter -eq $lfCyclesBefore) "$($lfCyclesAfter - $lfCyclesBefore) new"
