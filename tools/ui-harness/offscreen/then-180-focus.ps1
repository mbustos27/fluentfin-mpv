# Nixie 1.8 (BH-180-M1, the 1.8 bug hunt's probe F): the keyboard on a This week tile when the people come in. A first
# start on a big library (FAKESERVER_BIG_LIBRARY=25000, FAKESERVER_REAL_SHAPE=1): once the titles are read the Collections
# page opens; the keyboard goes to This week's first tile, then Right (the second, by the keyboard); the people step then
# publishes and This week changes (More from and the Spotlight come in). The keyboard must still be on a This week tile (it
# went to Back: the row was made again, and its tile with it), and a Right arrow must stay on a tile.
# Run with `-Script home -NoA11y -Then offscreen\then-180-focus.ps1` (NIXIE_LAYOUT the theme). Dot-sourced by drive.ps1:
# every name here starts with f180. ASCII only.
$f180Tag = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
function F180-Lines([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content $_.FullName -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}
$f180ButtonCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
function F180-NixieTiles { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $f180ButtonCond) | Where-Object { try { $_.Current.Name -like '*, made by Nixie, *' } catch { $false } }) }
function F180-IntoView($Element) {
    if (-not $Element) { return }
    $p = $null
    try { if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { $p.ScrollIntoView() } } catch {}
    Start-Sleep -Milliseconds 700
}
function F180-Focus {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $f = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if ($f) { "$($f.Current.Name) [$($f.Current.ControlType.ProgrammaticName)]" } else { '(none)' }
}

# 1. the titles in (the people step about to begin): the Collections page, the keyboard on This week's second tile
$f180Deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $f180Deadline -and (F180-Lines 'Library index: \d+ titles in \d+ libraries').Count -eq 0) { Start-Sleep -Milliseconds 300 }
Open-Nav 'Collections'
$f180Week = Wait-Until { Find-Ui -Name 'This week' -OffscreenToo } -Seconds 60 -PollMs 300
Check "focus180 ($f180Tag): This week shows before the people are in" ([bool]$f180Week) ''
Start-Sleep -Milliseconds 1500
F180-IntoView (@(F180-NixieTiles) | Select-Object -First 1)
F180-IntoView (Find-Ui -Name 'This week' -OffscreenToo)
$f180Tile = @(F180-NixieTiles) | Select-Object -First 1
F180-IntoView $f180Tile
if ($f180Tile) { [void][OffscreenUi]::SetFocus($f180Tile); Start-Sleep -Milliseconds 500; Key 0x27; Start-Sleep -Milliseconds 500 }
$f180Before = F180-Focus
$f180PeopleEarly = (F180-Lines 'the people are in').Count -gt 0
Say "focus180 before the people: focus $f180Before; people in already: $f180PeopleEarly"
Check "focus180 ($f180Tag): the keyboard is on a This week tile before the people come in" ($f180Before -like '*, made by Nixie, *') $f180Before
Shot "$f180Tag-focus-before"

# 2. the people come in: the index publishes, the page makes This week again
$f180In = Wait-Until { @(F180-Lines 'Library index: the people are in') | Select-Object -First 1 } -Seconds 300 -PollMs 300
Start-Sleep -Seconds 4
$f180After = F180-Focus
Say "focus180 after the people ($([bool]$f180In)): focus $f180After"
Shot "$f180Tag-focus-after"
Check "focus180 ($f180Tag): the people came in while the page showed" ([bool]$f180In -and -not $f180PeopleEarly) "early: $f180PeopleEarly"
Check "focus180 ($f180Tag): the keyboard stays on a This week tile when the people come in" ($f180After -like '*, made by Nixie, *') "before: $f180Before; after: $f180After"

# 3. a Right arrow now stays on a tile (not Back's, nor out of the row)
Key 0x27; Start-Sleep -Milliseconds 600
$f180Arrow = F180-Focus
Check "focus180 ($f180Tag): a Right arrow after it is still on a tile" ($f180Arrow -like '*, made by Nixie, *') $f180Arrow
