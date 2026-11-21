# Seaglass scrolling, the Library grid (the report's acceptance tests 1, 4 and 5 for a grid): run with
# -Script library -Then offscreen\then-library.ps1 (on the Movies library: 320 movies, one grid since 1.1.4).
# One Tab stop into the grid, arrows in both directions, Home/End, PageUp/PageDown, Up out of the first line, the grid's
# last-focused card coming back on Tab, the focused card never under the title bar or past the grid's edge, Narrator's
# "n of m" and the list's name, and the wheel over the grid and over the rest of the page. Keys are posted to FluentFin's
# input window (Key); Shift cannot be posted (WinUI reads the real keyboard state), so Shift+Tab is a hands-on check.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names (PowerShell names ignore case: $out is its -Out).
$A = [System.Windows.Automation.AutomationElement]
$Ids = [System.Windows.Automation.AutomationElementIdentifiers]
function Focused-Element {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::HasKeyboardFocusProperty, $true)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
}
function Info {
    $el = Focused-Element
    if (-not $el) { return $null }
    $c = $el.Cached; $r = $c.BoundingRectangle
    $pos = $el.GetCurrentPropertyValue($Ids::PositionInSetProperty); $size = $el.GetCurrentPropertyValue($Ids::SizeOfSetProperty)
    $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($el)
    [pscustomobject]@{ Name = $c.Name; Type = ($c.ControlType.ProgrammaticName -replace '^ControlType\.', ''); Class = $c.ClassName
        X = $r.X; Y = $r.Y; W = $r.Width; H = $r.Height; Pos = [int]$pos; Size = [int]$size
        ListName = $(if ($parent) { $parent.Current.Name }); ListType = $(if ($parent) { $parent.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '' }) }
}
function Press([int]$Vk, [int]$Ms = 700) { Key $Vk; Start-Sleep -Milliseconds $Ms }
function Scrollers { @([OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id) | ForEach-Object { $p = $_ -split '\|'; [pscustomobject]@{ Rect = ($p[2] -split ',' | ForEach-Object { [double]$_ }); V = [double]$p[4]; VScroll = ($p[6] -eq 'True') } }) }
function Grid-State { $best = $null; foreach ($s in @(Scrollers)) { if ($s.VScroll -and (-not $best -or $s.Rect[2] * $s.Rect[3] -gt $best.Rect[2] * $best.Rect[3])) { $best = $s } }; $best }
function Card-Inside($f, $g) { $f -and $g -and $f.Y -ge $g.Rect[1] - 1 -and ($f.Y + $f.H) -le ($g.Rect[1] + $g.Rect[3] + 1) -and $f.Y -ge 90 }
function Describe($f) { if ($f) { "{0} [{1} {2}] {3} of {4} at {5:0},{6:0}" -f $f.Name, $f.Type, $f.Class, $f.Pos, $f.Size, $f.X, $f.Y } else { 'no focus' } }

$total = 320
# Seaglass 1.1.4: the Filter button says how many filters are on ("Filter (none on)"), and the A-Z jump sits between the bar
# and the grid (one Tab stop): one Tab into the grid is from it, not from Filter. $beforeGrid is that stop.
$filter = Wait-Ui -Name 'Filter' -Type 'Button' -Contains -Seconds 10
Check 'library: the Filter button is there' ([bool]$filter)
$azList = Find-Ui -Name 'Jump to a letter' -Type 'List'
$beforeGrid = $filter
if ($azList) {
    $azCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    $azFirst = $azList.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $azCond)
    if ($azFirst) { $beforeGrid = $azFirst }
}
[void][OffscreenUi]::SetFocus($beforeGrid); Start-Sleep -Milliseconds 400

# --- Tab into the grid: one stop, the first card in view, its place for Narrator
Press 0x09
$f = Info
Check 'library: Tab from the stop before the grid (the A-Z jump, else Filter) goes into the grid (a card)' ($f -and $f.Class -eq 'BaseItemDtoCard') (Describe $f)
Check 'library: ... onto the first card' ($f -and $f.Pos -eq 1) (Describe $f)
Check 'library: Narrator: a card says its place in the whole library ("1 of 320")' ($f -and $f.Pos -eq 1 -and $f.Size -eq $total) (Describe $f)
Check 'library: Narrator: the grid is a list named after the library' ($f -and $f.ListType -eq 'List' -and $f.ListName -eq $fixture.movie.library) ("$($f.ListType) '$($f.ListName)'")
Shot 'library-1-tab-in'
Press 0x09 500
$leaving = Info
Check 'library: the next Tab leaves the grid (one stop for 100 cards)' ($leaving -and $leaving.Class -ne 'BaseItemDtoCard') (Describe $leaving)

# --- arrows in two directions
[void][OffscreenUi]::SetFocus($beforeGrid); Start-Sleep -Milliseconds 300
Press 0x09
$first = Info
Press 0x27   # Right
$r1 = Info
Check 'library: Right moves to the next card' ($r1 -and $r1.Pos -eq 2 -and [math]::Abs($r1.Y - $first.Y) -lt 2 -and $r1.X -gt $first.X) (Describe $r1)
Press 0x28 900  # Down
$d1 = Info
$cols = if ($d1) { $d1.Pos - $r1.Pos } else { 0 }
Check 'library: Down moves to the card below (same column, next line)' ($d1 -and $cols -ge 2 -and [math]::Abs($d1.X - $r1.X) -lt 2 -and $d1.Y -gt $r1.Y + 100) ("$(Describe $d1); $cols to a line")
Shot 'library-2-down'
Press 0x26 900  # Up
Check 'library: Up moves back up the column' ((Info).Pos -eq 2)
Press 0x25   # Left
Check 'library: Left moves back' ((Info).Pos -eq 1)
Press 0x25
Check 'library: Left at the first card stays (no wrap)' ((Info).Pos -eq 1)
# Right at a line's end goes on to the next line's first card (reading order)
for ($i = 1; $i -lt $cols; $i++) { Press 0x27 400 }
$eol = Info
Press 0x27 900
$next = Info
Check 'library: Right at the end of a line goes to the next line''s first card' ($eol -and $next -and $eol.Pos -eq $cols -and $next.Pos -eq $cols + 1 -and $next.X -lt $eol.X) ("$(Describe $eol) -> $(Describe $next)")

# --- Home/End, PageDown/PageUp, the focused card always in view
Press 0x23 1500  # End
$end = Info; $g = Grid-State
# Seaglass 1.1.4: one continuously scrolling grid (no pages of 100): End is the library's last card
Check 'library: End goes to the last card of the library' ($end -and $end.Pos -eq $total) (Describe $end)
Check 'library: ... scrolled fully into view (not under the title bar, not past the grid''s edge)' (Card-Inside $end $g) ("card {0:0}..{1:0}, grid {2:0}..{3:0}" -f $end.Y, ($end.Y + $end.H), $g.Rect[1], ($g.Rect[1] + $g.Rect[3]))
Shot 'library-3-end'
Press 0x24 1500  # Home
$home_ = Info; $g = Grid-State
Check 'library: Home goes to the first card, at the top' ($home_ -and $home_.Pos -eq 1 -and $g.V -lt 1) ("$(Describe $home_); grid at $($g.V) %")
Press 0x22 1200  # PageDown
$pd = Info; $g = Grid-State
Check 'library: PageDown moves a page of lines down the same column' ($pd -and $pd.Pos -gt $cols -and (($pd.Pos - 1) % $cols) -eq 0) (Describe $pd)
Check 'library: ... and the card is in view' (Card-Inside $pd $g)
Shot 'library-4-pagedown'
Press 0x21 1200  # PageUp
Check 'library: PageUp moves back up' ((Info).Pos -eq 1)
# Down, Down, Down...: the focus walks down the grid and the grid follows, the card never hidden
$hidden = @()
for ($i = 0; $i -lt 6; $i++) {
    Press 0x28 900
    $x = Info; $g = Grid-State
    if (-not (Card-Inside $x $g)) { $hidden += (Describe $x) }
}
Check 'library: walking Down six lines, every focused card is fully in view' ($hidden.Count -eq 0) ($hidden -join '; ')
Check 'library: ... and the grid scrolled' ((Grid-State).V -gt 1) ("grid at $((Grid-State).V) %")
Shot 'library-5-walked-down'

# --- Up out of the first line: to the sort and filter buttons above
Press 0x24 1500
Press 0x26 700
$up = Info
Check 'library: Up from the first line leaves the grid upward (the A-Z jump, Sort/Filter)' ($up -and $up.Type -eq 'Button' -and $up.Class -ne 'BaseItemDtoCard') (Describe $up)

# --- the grid remembers its last card: into the grid, to a card, out to Filter, Tab back in
[void][OffscreenUi]::SetFocus($beforeGrid); Start-Sleep -Milliseconds 300
Press 0x09
Press 0x28 900; Press 0x28 900; Press 0x27
$mem = Info
[void][OffscreenUi]::SetFocus($beforeGrid); Start-Sleep -Milliseconds 500
Press 0x09 900
$back = Info
Check 'library: Tab back into the grid lands on its last-focused card (not the first)' ($mem -and $back -and $back.Pos -eq $mem.Pos -and $mem.Pos -gt 1) ("$(Describe $mem) / $(Describe $back)")
Shot 'library-6-remembered'

# --- the wheel: over the grid, and over the page outside it (the sort bar, the pager)
[void][OffscreenUi]::SetFocus($beforeGrid); Start-Sleep -Milliseconds 300
Press 0x09; Press 0x24 1500
$g0 = Grid-State
$gx = [int]($g0.Rect[0] + $g0.Rect[2] / 2); $gy = [int]($g0.Rect[1] + $g0.Rect[3] / 2)
[OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, $gx, $gy, -120, $false, $false); Start-Sleep -Milliseconds 900
$g1 = Grid-State
Check 'library: the wheel over the grid scrolls it' ($g1.V -gt $g0.V + 0.1) ("$($g0.V) -> $($g1.V) %")
$fr = [OffscreenUi]::RectOf($filter)
[OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($fr.X - 200), [int]($fr.Y + $fr.Height / 2), -120, $false, $false); Start-Sleep -Milliseconds 900
$g2 = Grid-State
Check 'library: the wheel over the sort bar (outside the grid) scrolls the grid too' ($g2.V -gt $g1.V + 0.1) ("$($g1.V) -> $($g2.V) %")
$pager = Find-Ui -Name 'Next page' -Type 'Button'
if ($pager) {
    $pr = [OffscreenUi]::RectOf($pager)
    [OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($pr.X - 300), [int]($pr.Y + $pr.Height / 2), -120, $false, $false); Start-Sleep -Milliseconds 900
    $g3 = Grid-State
    Check 'library: ... and over the pager' ($g3.V -gt $g2.V + 0.1) ("$($g2.V) -> $($g3.V) %")
}
# the page's margin (outside the grid and the page's own panel) takes the wheel too
$g4 = Grid-State
[OffscreenUi]::Wheel($script:hwnd, $script:ui.Id, [int]($g4.Rect[0] - 12), [int]($g4.Rect[1] + $g4.Rect[3] / 2), -120, $false, $false); Start-Sleep -Milliseconds 900
$g5 = Grid-State
Check 'library: ... and over the page''s margin' ($g5.V -gt $g4.V + 0.1) ("$($g4.V) -> $($g5.V) %")

# --- a key that arrives before the grid has scrolled to a far card cancels it (keys posted in one burst, as fast typing)
function Burst([int[]]$Vks) {
    $target = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
    foreach ($vk in $Vks) {
        $sc = [OffscreenUi]::ScanCode($vk)
        [void][OffscreenUi]::PostMessage($target, 0x0100, [IntPtr][long]$vk, [IntPtr][long](1 -bor ($sc -shl 16)))
        [void][OffscreenUi]::PostMessage($target, 0x0101, [IntPtr][long]$vk, [IntPtr]([long](1 -bor ($sc -shl 16) -bor (1 -shl 30)) -bor 0x80000000L))
    }
}
[void][OffscreenUi]::SetFocus($beforeGrid); Start-Sleep -Milliseconds 300
Press 0x09 900
Burst @(0x23, 0x24)   # End, Home
Start-Sleep -Milliseconds 1500
$eh = Info; $g6 = Grid-State
Check 'library: End then Home at once ends on the first card, at the top (End''s far card does not take the focus later)' ($eh -and $eh.Pos -eq 1 -and $g6.V -lt 1) ("$(Describe $eh); grid at $($g6.V) %")
Burst @(0x23, 0x09)   # End, Tab
Start-Sleep -Milliseconds 1500
$et = Info
Check 'library: End then Tab at once leaves the grid (the scroll''s end does not pull the focus back)' ($et -and $et.Class -ne 'BaseItemDtoCard') (Describe $et)

# --- the See all grid (Continue Watching): the same model; nothing above it in the page, so Up stays on the card
Open-Nav 'Home'
$seeAll = Wait-Ui -Name 'See all Continue Watching' -Type 'Hyperlink' -Seconds 15
Check 'see all: Continue Watching has its See all' ([bool]$seeAll)
if ($seeAll) {
    Invoke-Element $seeAll | Out-Null
    $saCard = Wait-Until { Find-Ui -Name 'Electric Ravenmoor' -Type 'ListItem' -Contains } -Seconds 15 -PollMs 200
    Start-Sleep -Milliseconds 1500
    $saCard = Find-Ui -Name 'Electric Ravenmoor' -Type 'ListItem' -Contains
    if ($saCard) { [void][OffscreenUi]::SetFocus($saCard); Start-Sleep -Milliseconds 600 }
    $sa1 = Info
    Check 'see all: the first card says "1 of n" in a list named Continue Watching' ($sa1 -and $sa1.Pos -eq 1 -and $sa1.Size -ge 2 -and $sa1.ListType -eq 'List' -and $sa1.ListName -eq 'Continue Watching') ("$(Describe $sa1); $($sa1.ListType) '$($sa1.ListName)'")
    Press 0x27   # Right
    Check 'see all: Right moves to the next card' ((Info).Pos -eq 2)
    Press 0x26 900   # Up from the first line: nothing above in the page
    $saUp = Info
    Check 'see all: Up from the first line stays on the card (never the title bar''s search box)' ($saUp -and $saUp.Class -eq 'BaseItemDtoCard' -and $saUp.Pos -eq 2) (Describe $saUp)
    Press 0x23 900   # End
    Press 0x28 900   # Down from the last line: nothing below in the page
    $saDown = Info
    Check 'see all: Down from the last line stays on the card' ($saDown -and $saDown.Class -eq 'BaseItemDtoCard' -and $saDown.Pos -eq $saDown.Size) (Describe $saDown)
    Shot 'see-all-keys'
    Key 0xA6; Start-Sleep -Milliseconds 1500   # Back
}
