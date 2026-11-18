# Seaglass 1.1.4 (interaction audit, item 5): Back puts you where you were. Run with -Script home -Then offscreen\then-back.ps1.
# Home scrolled down, a card of a lower row opened with Enter, Back: Home is as far down as it was and that card has the
# focus (1.1.3: Home at the top, the focus on the title bar). The Movies library on its second page, down the grid, a card
# opened, Back: the second page, the grid as far down, that card focused (1.1.3: page 1, the title bar).
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
$A = [System.Windows.Automation.AutomationElement]
$Ids = [System.Windows.Automation.AutomationElementIdentifiers]
function Bk-Info {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::HasKeyboardFocusProperty, $true)
    $el = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if (-not $el) { return $null }
    [pscustomobject]@{ Name = $el.Cached.Name; Class = $el.Cached.ClassName; Type = ($el.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', '')
        Pos = [int]$el.GetCurrentPropertyValue($Ids::PositionInSetProperty); Size = [int]$el.GetCurrentPropertyValue($Ids::SizeOfSetProperty) }
}
function Bk-Describe($f) { if ($f) { "{0} [{1} {2}] {3} of {4}" -f $f.Name, $f.Type, $f.Class, $f.Pos, $f.Size } else { 'no focus' } }
function Bk-Scrollers { @([OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id) | ForEach-Object { $p = $_ -split '\|'; [pscustomobject]@{ Rect = ($p[2] -split ',' | ForEach-Object { [double]$_ }); V = [double]$p[4]; VScroll = ($p[6] -eq 'True') } }) }
function Bk-Page { $best = $null; foreach ($x in @(Bk-Scrollers)) { if ($x.VScroll -and (-not $best -or $x.Rect[2] * $x.Rect[3] -gt $best.Rect[2] * $best.Rect[3])) { $best = $x } }; $best }
function Bk-Press([int]$Vk, [int]$Ms = 800) { Key $Vk; Start-Sleep -Milliseconds $Ms }
function Bk-OpenAndBack($left) {
    Bk-Press 0x0D 600
    $page = Wait-Until { $x = Bk-Info; if ($x -and $x.Name -ne $left.Name) { $x } } -Seconds 10
    Start-Sleep -Milliseconds 2000
    Key 0xA6   # Back
    Start-Sleep -Milliseconds 3000
}

# --- Home
Open-Nav 'Home'
Start-Sleep -Milliseconds 2500
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 60)
Start-Sleep -Milliseconds 1200
# a card of a row lower down, then one along it with the keys (the row remembers it)
$cards = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'BaseItemDtoCard'))) |
    Where-Object { -not $_.Cached.IsOffscreen } | Sort-Object { $_.Cached.BoundingRectangle.Y } -Descending)
if ($cards.Count) {
    [void][OffscreenUi]::SetFocus($cards[0]); Start-Sleep -Milliseconds 600
    Bk-Press 0x27 900
}
$homeLeft = Bk-Info; $homeAt = (Bk-Page).V
Shot 'back-1-home-before'
Check 'back: a card of a lower Home row has the focus' ($homeLeft -and $homeLeft.Class -eq 'BaseItemDtoCard' -and $homeAt -gt 10) ("{0}; page at {1:0.0} %" -f (Bk-Describe $homeLeft), $homeAt)
Bk-OpenAndBack $homeLeft
$homeBack = Bk-Info; $homeAgain = (Bk-Page).V
Shot 'back-2-home-after'
Check 'back: Home is as far down as it was' ([math]::Abs($homeAgain - $homeAt) -lt 3) ("{0:0.0} % -> {1:0.0} %" -f $homeAt, $homeAgain)
Check 'back: ... and the card that was opened has the focus (not the title bar)' ($homeBack -and $homeLeft -and $homeBack.Name -eq $homeLeft.Name) ("left {0}; back on {1}" -f (Bk-Describe $homeLeft), (Bk-Describe $homeBack))

# --- the Library, far down its grid (one grid since 1.1.4: no pages)
Open-Library $fixture.movie.library
[void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150)
Start-Sleep -Milliseconds 1500
$first = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'BaseItemDtoCard'))) | Where-Object { -not $_.Cached.IsOffscreen }) | Select-Object -First 1
if ($first) { [void][OffscreenUi]::SetFocus($first); Start-Sleep -Milliseconds 600 }
Bk-Press 0x22 1200; Bk-Press 0x22 1200; Bk-Press 0x22 1200; Bk-Press 0x27 900   # PageDown x3, Right
$libLeft = Bk-Info; $libAt = (Bk-Page).V
Shot 'back-3-library-before'
Check 'back: a card far down the Library has the focus' ($libLeft -and $libLeft.Class -eq 'BaseItemDtoCard' -and $libLeft.Pos -gt 40 -and $libAt -gt 5) ("{0}; grid at {1:0.0} %" -f (Bk-Describe $libLeft), $libAt)
Bk-OpenAndBack $libLeft
$libBack = Bk-Info; $libAgain = (Bk-Page).V
Shot 'back-4-library-after'
Check 'back: the same card of the Library has the focus' ($libBack -and $libLeft -and $libBack.Name -eq $libLeft.Name -and $libBack.Pos -eq $libLeft.Pos) ("left {0}; back on {1}" -f (Bk-Describe $libLeft), (Bk-Describe $libBack))
Check 'back: ... and the grid is as far down as it was' ([math]::Abs($libAgain - $libAt) -lt 3) ("{0:0.0} % -> {1:0.0} %" -f $libAt, $libAgain)

# --- a row's "See all" grid (Continue Watching's): a card opened, Back: that card has the focus
Open-Nav 'Home'
Start-Sleep -Milliseconds 2500
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 800
$seeAll = Find-Ui -Name 'See all Continue Watching' -Type 'Button'
if ($seeAll) {
    [void](Invoke-Element $seeAll)
    [void](Wait-Ui -Name 'Continue Watching' -Type 'List' -Seconds 10)
    Start-Sleep -Milliseconds 1500
    $gridFirst = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'BaseItemDtoCard'))) | Where-Object { -not $_.Cached.IsOffscreen }) | Select-Object -First 1
    if ($gridFirst) { [void][OffscreenUi]::SetFocus($gridFirst); Start-Sleep -Milliseconds 600 }
    Bk-Press 0x28 900; Bk-Press 0x27 900
    $gridLeft = Bk-Info
    Shot 'back-5-grid-before'
    Check 'back: a card down the See all grid has the focus' ($gridLeft -and $gridLeft.Class -eq 'BaseItemDtoCard' -and $gridLeft.Pos -gt 1) (Bk-Describe $gridLeft)
    Bk-OpenAndBack $gridLeft
    $gridBack = Bk-Info
    Shot 'back-6-grid-after'
    Check 'back: the See all grid comes back with the same card focused' ($gridBack -and $gridLeft -and $gridBack.Name -eq $gridLeft.Name -and $gridBack.Pos -eq $gridLeft.Pos) ("left {0}; back on {1}" -f (Bk-Describe $gridLeft), (Bk-Describe $gridBack))
} else {
    Check 'back: Continue Watching has its See all' $false
}
