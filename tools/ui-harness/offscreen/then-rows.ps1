# Seaglass scrolling, keyboard and focus (acceptance tests 3 and 4): run with -Script home -Then offscreen\then-rows.ps1
# Keys are posted to FluentFin's input window (Key); Shift is posted around the key by -shift (WinUI reads the real keyboard state
# for modifiers, so Shift+Tab is checked in the unit tests and by hand).
function Focus-Info { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { $f | ConvertFrom-Json } }
function Press([int]$Vk, [int]$Ms = 700) { Key $Vk; Start-Sleep -Milliseconds $Ms }
function Card-Focus([string]$Prefix) { $c = Find-Ui -Name $Prefix -Type 'ListItem' -Contains; if ($c) { [void][OffscreenUi]::SetFocus($c); Start-Sleep -Milliseconds 500 }; Focus-Info }

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
Check 'rows: Down moves to the Next Up row' ($down -and $down.type -eq 'ListItem' -and $down.rect[1] -gt $col.rect[1] + 100 -and $down.name -ne $col.name) ($down.name)
Check 'rows: ... in the same column (centres within one card)' ($down -and [math]::Abs(($down.rect[0] + $down.rect[2] / 2) - ($col.rect[0] + $col.rect[2] / 2)) -lt 400) ("{0} vs {1}" -f $down.rect[0], $col.rect[0])
Shot 'rows-3-down'
Press 0x28 1500
$d2 = Focus-Info
Check 'rows: Down again reaches Recently Added (the row below the fold)' ($d2 -and $d2.type -eq 'ListItem' -and $d2.rect[1] -gt $down.rect[1] - 400 -and $d2.name -ne $down.name) ($d2.name)
Check 'rows: the focused card is fully inside the window (not under the title bar or the edge)' ($d2 -and $d2.rect[1] -ge 90 -and ($d2.rect[1] + $d2.rect[3]) -le 1400) ($d2.rect -join ',')
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
Check 'rows: Tab leaves the row after one stop' ($left -and $left.type -eq 'Button' -and $left.name -like 'See all*') ($left.name)
# the row remembers its last card: into the Next Up row, to its third card, out to its See all button, Tab back in: the third card
$c = Card-Focus 'A Signal in Winter'
Press 0x27; Press 0x27
$third = Focus-Info
$sa = Find-Ui -Name 'See all Next Up' -Type 'Button'
[void][OffscreenUi]::SetFocus($sa); Start-Sleep -Milliseconds 500
Press 0x09 700
$back = Focus-Info
Check 'rows: Tab back into a row lands on the card it had (not the first)' ($back -and $third -and $back.name -eq $third.name -and $back.name -notlike 'A Signal in Winter*') ("$($third.name) / $($back.name)")
Shot 'rows-6-remembered'
