# Seaglass scrolling, keyboard and focus (acceptance tests 3 and 4): run with -Script home -Then offscreen\then-rows.ps1
# Keys are posted to FluentFin's input window (Key); Shift is posted around the key by -shift (WinUI reads the real keyboard state
# for modifiers, so Shift+Tab is checked in the unit tests and by hand).
function Focus-Info { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } }
function Press([int]$Vk, [int]$Ms = 700) { Key $Vk; Start-Sleep -Milliseconds $Ms }
# a card by the start of its name, also when its row is below the fold (focusing it brings it into view; 1.2.0: a
# 1080-pixel window shows Next Up only in part, and the card was not found)
function Card-Focus([string]$Prefix) { $c = Find-Ui -Name $Prefix -Type 'ListItem' -Contains -OffscreenToo; if ($c) { [void][OffscreenUi]::SetFocus($c); Start-Sleep -Milliseconds 500 }; Focus-Info }
# the names of a row's cards (its list is named after the row): which row the focus is in, at any window size (1.2.0:
# the checks compared heights on screen, which a 1080-pixel window's page scroll upsets)
function Row-Cards([string]$RowName) {
    $rowList = Find-Ui -Name $RowName -Type 'List' -OffscreenToo
    if (-not $rowList) { return @() }
    $itemCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    @($rowList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCond) | ForEach-Object { $_.Current.Name })
}
$winBottom = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd).Current.BoundingRectangle.Bottom

[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 700
$f = Card-Focus 'Electric Ravenmoor, 2021'
Check 'rows: the first Continue Watching card has the focus' ($f -and $f.name -like 'Electric Ravenmoor, 2021*') ($f.name)
Shot 'rows-1-first-card'

Press 0x27   # Right
$f2 = Focus-Info
Check 'rows: Right moves to the next card' ($f2 -and $f2.name -like 'Iron Lighthouses*') ($f2.name)
Press 0x25   # Left
Check 'rows: Left moves back' ((Focus-Info).name -like 'Electric Ravenmoor*')
Press 0x25
Check 'rows: Left at the first card stays (no wrap, focus does not leave the row)' ((Focus-Info).name -like 'Electric Ravenmoor*')

Press 0x23 1200  # End
$end = Focus-Info
Check 'rows: End goes to the last card' ($end -and $end.name -notlike 'Electric Ravenmoor*' -and $end.type -eq 'ListItem') ($end.name)
Shot 'rows-2-end'
Press 0x27
Check 'rows: Right at the last card stays' ((Focus-Info).name -eq $end.name)
Press 0x24 1200  # Home
Check 'rows: Home goes to the first card' ((Focus-Info).name -like 'Electric Ravenmoor*')

# Right x2 then Down: the card in the same column of Next Up
Press 0x27; Press 0x27
$col = Focus-Info
Press 0x28 1000  # Down
$down = Focus-Info
Check 'rows: Down moves to the Next Up row' ($down -and $down.type -eq 'ListItem' -and (Row-Cards 'Up next') -contains $down.name -and $down.name -ne $col.name) ($down.name)
Check 'rows: ... in the same column (centres within one card)' ($down -and [math]::Abs(($down.rect[0] + $down.rect[2] / 2) - ($col.rect[0] + $col.rect[2] / 2)) -lt 400) ("{0} vs {1}" -f $down.rect[0], $col.rect[0])
Shot 'rows-3-down'
Press 0x28 1500
$d2 = Focus-Info
Check 'rows: Down again reaches Recently Added (the row below the fold)' ($d2 -and $d2.type -eq 'ListItem' -and $d2.rect[1] -gt $down.rect[1] - 400 -and $d2.name -ne $down.name) ($d2.name)
Check 'rows: the focused card is fully inside the window (not under the title bar or the edge)' ($d2 -and $d2.rect[1] -ge 90 -and ($d2.rect[1] + $d2.rect[3]) -le $winBottom) (($d2.rect -join ',') + " (window bottom $winBottom)")
Shot 'rows-4-lower-row'
Press 0x26 1200
$up = Focus-Info
Check 'rows: Up returns to the Next Up row' ($up -and $up.name -eq $down.name) ($up.name)

# Down to the very last row: every Recently Added row is reachable with Down alone
$seen = @{}
for ($i = 0; $i -lt 6; $i++) { Press 0x28 1300; $x = Focus-Info; if ($x) { $seen[$x.name] = $x.rect[1] } }
Say "rows: Down x6 visited: $($seen.Keys -join ' | ')"
Check 'rows: Down reaches the last row (the four Recently Added rows are four distinct cards)' ($seen.Count -eq 4) "$($seen.Count) distinct cards"
Shot 'rows-5-last-row'

# a row remembers its card: Tab out of the row and Tab back in
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 900
Card-Focus 'Iron Lighthouses' | Out-Null
Press 0x27; Press 0x27
$mem = Focus-Info
Press 0x09 400   # Tab leaves the row (to Next Up's See all)
$left = Focus-Info
Check 'rows: Tab leaves the row after one stop' ($left -and $left.type -eq 'Hyperlink' -and $left.name -like 'See all*') ($left.name)
# the row remembers its last card: into the Next Up row, to its third card, out to its See all button, Tab back in: the third card
$c = Card-Focus 'A Signal in Winter'
Press 0x27; Press 0x27
$third = Focus-Info
$sa = Find-Ui -Name 'See all Up next' -Type 'Hyperlink'
[void][OffscreenUi]::SetFocus($sa); Start-Sleep -Milliseconds 500
Press 0x09 700
$back = Focus-Info
Check 'rows: Tab back into a row lands on the card it had (not the first)' ($back -and $third -and $back.name -eq $third.name -and $back.name -notlike 'A Signal in Winter*') ("$($third.name) / $($back.name)")
Shot 'rows-6-remembered'

# Up from the first row leaves the rows for the hero above (WinUI 3's TryMoveFocus found nothing without a search root:
# the key fell to the page, which scrolled, and the focus stayed on the card)
# (from the row's first and last cards: the title bar and the row's own See all must not win)
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 900
foreach ($endKey in 0x24, 0x23) {   # Home: the first card; End: the last
    $anyCard = Find-Ui -Name 'Electric Ravenmoor' -Type 'ListItem' -Contains -OffscreenToo
    if ($anyCard) { [void][OffscreenUi]::SetFocus($anyCard); Start-Sleep -Milliseconds 500 }
    Press $endKey 1200
    $topCard = Focus-Info
    Press 0x26 900   # Up
    $aboveRows = Focus-Info
    Check "rows: Up from the first row's $(if ($endKey -eq 0x24) { 'first' } else { 'last' }) card goes up to the hero (not the title bar, nor the row's own header)" ($topCard -and $topCard.type -eq 'ListItem' -and $aboveRows -and $aboveRows.type -notin 'ListItem', 'TitleBar' -and $aboveRows.name -notlike 'See all*' -and $aboveRows.name -notlike 'Scroll *' -and $aboveRows.rect[1] -ge 60 -and $aboveRows.rect[1] -lt $topCard.rect[1] - 100) ("$($topCard.name) -> $($aboveRows.type) '$($aboveRows.name)'")
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 700
}
