# Seaglass scrolling, the Library grid (the report's acceptance tests 1, 4 and 5 for a grid): run with
# -Script library -Then offscreen\then-library.ps1 (on the Movies library: 320 movies, 100 to a page).
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
$filter = Wait-Ui -Name 'Filter' -Type 'Button' -Seconds 10
Check 'library: the Filter button is there' ([bool]$filter)
[void][OffscreenUi]::SetFocus($filter); Start-Sleep -Milliseconds 400

# --- Tab into the grid: one stop, the first card in view, its place for Narrator
Press 0x09
$f = Info
Check 'library: Tab from Filter goes into the grid (a card)' ($f -and $f.Class -eq 'BaseItemDtoCard') (Describe $f)
Check 'library: ... onto the first card' ($f -and $f.Pos -eq 1) (Describe $f)
Check 'library: Narrator: a card says its place in the whole library ("1 of 320")' ($f -and $f.Pos -eq 1 -and $f.Size -eq $total) (Describe $f)
Check 'library: Narrator: the grid is a list named after the library' ($f -and $f.ListType -eq 'List' -and $f.ListName -eq $fixture.movie.library) ("$($f.ListType) '$($f.ListName)'")
Shot 'library-1-tab-in'
Press 0x09 500
$leaving = Info
Check 'library: the next Tab leaves the grid (one stop for 100 cards)' ($leaving -and $leaving.Class -ne 'BaseItemDtoCard') (Describe $leaving)

# --- arrows in two directions
[void][OffscreenUi]::SetFocus($filter); Start-Sleep -Milliseconds 300
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
Check 'library: End goes to the last card of the page' ($end -and $end.Pos -eq 100) (Describe $end)
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
Check 'library: Up from the first line leaves the grid upward (Sort/Filter)' ($up -and $up.Type -eq 'Button' -and $up.Class -ne 'BaseItemDtoCard') (Describe $up)

# --- the grid remembers its last card: into the grid, to a card, out to Filter, Tab back in
[void][OffscreenUi]::SetFocus($filter); Start-Sleep -Milliseconds 300
Press 0x09
Press 0x28 900; Press 0x28 900; Press 0x27
$mem = Info
[void][OffscreenUi]::SetFocus($filter); Start-Sleep -Milliseconds 500
Press 0x09 900
$back = Info
Check 'library: Tab back into the grid lands on its last-focused card (not the first)' ($mem -and $back -and $back.Pos -eq $mem.Pos -and $mem.Pos -gt 1) ("$(Describe $mem) / $(Describe $back)")
Shot 'library-6-remembered'

# --- the wheel: over the grid, and over the page outside it (the sort bar, the pager)
[void][OffscreenUi]::SetFocus($filter); Start-Sleep -Milliseconds 300
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
