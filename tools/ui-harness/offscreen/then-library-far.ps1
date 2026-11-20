# Nixie 1.4.0, the release's layout cycle: a library scrolled far in one go (the scroll bar's jump, Narrator's scrolling, UI
# Automation's SetScrollPercent), then a title far above opened by Narrator's Invoke, by the keyboard (Enter on the title the
# keyboard was left on) and closed again (Esc, Invoke). In Editorial this hung the window for about 30 s, logged a
# LayoutCycleException and left the window unusable (the rows' posters got their width only once in the window, so every row
# the grid made changed height after it was laid out). Run in each theme:
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'; run-offscreen.ps1 ... -Script home -Then offscreen\then-library-far.ps1
# Checks: the window answers within 2 s all along (a WM_NULL every 200 ms) and settles after the far scroll (every answer
# within 400 ms over the next 5 s); no LayoutCycleException in FluentFin's log; what
# the user then sees: Editorial's details open in view, the keyboard on Play when Enter opened them, the poster "expanded",
# and Esc or a second Invoke closes them with the poster in view (the keyboard on it after Esc); the other themes' item page
# opens and Back returns. Shots <tag>-far-<name>.png. Dot-sourced by drive.ps1: every name here starts with lf.

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
function Lf-Details { Find-Ui -Name "Details of $lfTitle" -Type 'Group' -Contains -OffscreenToo }
function Lf-State($Card) {
    $p = $null
    try { if ($Card.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { return "$($p.Current.ExpandCollapseState)" } } catch {}
    ''
}

$lfCyclesBefore = Lf-Cycles
Say "far ($lfTag): LayoutCycleException lines before: $lfCyclesBefore"

# --- 1. Narrator's way: the scroll bar's jump, then Invoke on the first title (far above, still drawn: the keyboard's)
if (Lf-Fresh) {
    $lfF = Lf-Focus; Say "far ($lfTag): on arrival the keyboard is on $(Lf-Desc $lfF)"
    $lfPc = Lf-Jump
    $lfR = Lf-Responsive 4
    Check "far ($lfTag): a far scroll in one go: the window answers within 2 s" $lfR.Ok "$($lfR.Text) (the scroll bar read $([int]$lfPc) % at once)"
    # and then it settles (the release's second fault: each image that arrived re-made the rows from the top, for 30 s)
    $lfR = Lf-Responsive 5
    Check "far ($lfTag): the grid settles after the far scroll (its next 5 s: every answer within 400 ms)" ($lfR.Ok -and $lfR.Slowest -le 400) $lfR.Text
    $lfShown = Find-Ui -Type 'ListItem' -Contains -Name ', '
    Check "far ($lfTag): after the far scroll, titles show in view" ([bool]$lfShown) $(if ($lfShown) { $lfShown.Current.Name } else { 'none' })
    $lfCard = Find-MediaCard $lfTitle
    if (-not $lfCard) {
        Say "far ($lfTag): '$lfTitle' is not drawn after the far scroll (the grid let it go): the Invoke path does not apply"
    } else {
        Say "far ($lfTag): '$lfTitle' drawn, out of view: $([bool]$lfCard.Current.IsOffscreen)"
        [OffscreenUi]::InvokeAsync([OffscreenUi]::InvokePatternOf($lfCard))
        $lfR = Lf-Responsive 5
        Check "far ($lfTag): Invoke on a title far above: the window answers within 2 s" $lfR.Ok $lfR.Text
        if ($lfEditorial) {
            $lfD = Wait-Until { Lf-Details } -Seconds 6 -PollMs 200
            Check "far ($lfTag): Invoke on a title far above: its details open, in view" (Lf-Shows $lfD) $(if ($lfD) { $lfD.Current.Name } else { 'no details' })
            $lfCard = Find-MediaCard $lfTitle
            Check "far ($lfTag): Invoke on a title far above: the poster shows, and says expanded" ((Lf-Shows $lfCard) -and (Lf-State $lfCard) -eq 'Expanded') "state '$(Lf-State $lfCard)'"
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
            Check "far ($lfTag): Invoke on a title far above: its page opens" ([bool]$lfPage) ''
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
    } else {
        Key 0x0D; Lf-Settle 300   # Enter on the title the keyboard was left on, far above
        $lfR = Lf-Responsive 5
        Check "far ($lfTag): Enter on the keyboard's title far above: the window answers within 2 s" $lfR.Ok $lfR.Text
        if ($lfEditorial) {
            $lfD = Wait-Until { Lf-Details } -Seconds 6 -PollMs 200
            $lfF = Lf-Focus
            Check "far ($lfTag): Enter far above: its details open in view, the keyboard on Play" ((Lf-Shows $lfD) -and $lfF -and $lfF.automationId -eq 'PlayButton') "$(if ($lfD) { $lfD.Current.Name } else { 'no details' }); keyboard on $(Lf-Desc $lfF)"
            Lf-Shot 'entered'
            # the way out by the keyboard, from far below: Esc (the grid may have moved the keyboard from Play to the poster
            # when the far scroll let the details go: Esc there closes them too)
            [void](Lf-Jump 80)
            Say "far ($lfTag): far below, the keyboard is on $(Lf-Desc (Lf-Focus))"
            Key 0x1B; Lf-Settle 300
            $lfR = Lf-Responsive 4
            Check "far ($lfTag): Esc in the details from far below: the window answers within 2 s" $lfR.Ok $lfR.Text
            $lfGone = Wait-Until { -not (Lf-Details) } -Seconds 5 -PollMs 200
            $lfF = Lf-Focus
            $lfCard = Find-MediaCard $lfTitle
            Check "far ($lfTag): Esc from far below: the details close, the keyboard back on the poster, in view" ([bool]$lfGone -and $lfF -and $lfF.name.StartsWith($lfTitle) -and (Lf-Shows $lfCard)) "keyboard on $(Lf-Desc $lfF)"
            Lf-Shot 'escaped'
        } else {
            $lfPage = Wait-ItemPage $lfTitle 15
            Check "far ($lfTag): Enter on the keyboard's title far above: its page opens" ([bool]$lfPage) ''
            if ($lfPage) { Key 0xA6; Lf-Settle 2500 }
            $lfBack = Wait-Until { Find-MediaCard $lfTitle } -Seconds 8 -PollMs 200
            $lfR = Lf-Responsive 3
            Check "far ($lfTag): Back from that page: the library, the window answering" ([bool]$lfBack -and $lfR.Ok) $lfR.Text
        }
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

$lfCyclesAfter = Lf-Cycles
Check "far ($lfTag): no LayoutCycleException in FluentFin's log" ($lfCyclesAfter -eq $lfCyclesBefore) "$($lfCyclesAfter - $lfCyclesBefore) new"
