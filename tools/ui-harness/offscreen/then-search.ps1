# Seaglass 1.1.4 (interaction audit, item 2): the title bar's search, live. Run with -Script wait:1 -Then offscreen\then-search.ps1
# (signed in, on Home). The arrows move through the rows without opening anything and the box shows the row's name; Narrator's
# names are "name, year, type"; Enter on a row opens it and clears the box; Enter on the text opens the results page, grouped
# by type, the query kept there; the box clears when a result is opened from it. SEAGLASS_THEME / -Setting Glass / SG_SHOT_TAG
# as then-scroll-shots.ps1. Dot-sourced by drive.ps1: no variable here may reuse one of its names.
Add-Type -Namespace SgSearch -Name W -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
$srTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
function SrSettle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function SrBox { Find-Ui -Name 'Search' -Type 'Edit' -Contains }
function SrText { $b = SrBox; if ($b) { try { ($b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { $null } } }
function SrRows { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'ListViewItem')))) }
function SrHome { [bool](Find-Ui -Name 'Continue Watching' -Type 'Text' -OffscreenToo) }
# '/' focuses the box (WindowShortcuts) when it does not have the keyboard already (then '/' would be typed): UI Automation
# otherwise; the characters go as WM_CHAR to the window WinUI reads keys from
function SrType([string]$Text, [switch]$Slash) {
    if ($Slash) { Key 0xBF } else { $b = SrBox; if ($b) { [void][OffscreenUi]::SetFocus($b) } }
    SrSettle 500
    $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
    foreach ($ch in $Text.ToCharArray()) { [void][SgSearch.W]::PostMessage($kw, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 80 }
    SrSettle 300
}

[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1500
[void](Wait-Until { if (SrHome) { $true } } -Seconds 20 -PollMs 200)

# ---- typing, then the arrows
SrType 'Quiet' -Slash
Check 'search: the typed text is in the box' ((SrText) -eq 'Quiet') "text: '$(SrText)'"
$rows = Wait-Until { $l = SrRows; if ($l.Count) { , $l } } -Seconds 8 -PollMs 150
$names = @($rows | ForEach-Object { $_.Current.Name })
Say "search rows: $($names.Count): $($names -join ' | ')"
Check 'search: Narrator reads each row as name, year, type (never a class name)' ($names.Count -gt 0 -and -not ($names | Where-Object { $_ -match 'Jellyfin\.Sdk|BaseItemDto' }) -and ($names | Where-Object { $_ -match ', (movie|series|episode)$' })) (($names | Select-Object -First 3) -join ' | ')
Shot "$srTag-search-rows"
Key 0x28; SrSettle 1500
$first = SrText
Check 'search: Down moves to the first row and opens nothing' ((SrHome) -and $names.Count -and $names[0].StartsWith($first) -and $first -ne 'Quiet') "page home: $(SrHome), box: '$first', row: '$($names[0])'"
Key 0x28; SrSettle 1500
$second = SrText
Check 'search: a second Down moves on, still on Home, the box showing the row''s name' ((SrHome) -and $second -and $second -notmatch 'Jellyfin' -and ($names.Count -lt 2 -or $names[1].StartsWith($second))) "box: '$second'"
Shot "$srTag-search-after-downs"
# Enter on the row: it opens, the box clears
Key 0x0D
$page = Wait-ItemPage $second -Seconds 15
Check 'search: Enter on a row opens it' ([bool]$page) $second
SrSettle 800
Check 'search: the box is empty after opening a result' ([string]::IsNullOrEmpty((SrText))) "text: '$(SrText)'"

# ---- Enter on the text: the results page
SrType 'the'
[void](Wait-Until { $l = SrRows; if ($l.Count) { , $l } } -Seconds 8 -PollMs 150)
Key 0x0D
$title = Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15
Check 'search: Enter on the text opens the results page' ([bool]$title)
SrSettle 2500
$headings = @('Movies', 'Shows', 'Episodes' | ForEach-Object { $h = Find-Ui -Name "$_ (" -Type 'Text' -Contains -OffscreenToo; if ($h) { $h.Current.Name } })
Check 'search: the results are grouped by type' ($headings.Count -ge 2) ($headings -join ', ')
Check 'search: the query stays in the box on its results page' ((SrText) -eq 'the') "text: '$(SrText)'"
Dump-Tree "$srTag-search-results"
Invoke-A11yScan "$srTag-search-results"
foreach ($size in @(@(1920, 1080), @(1100, 900))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); SrSettle 1800
    Shot "$srTag-search-results-$($size[0])"
}
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1500
# a result opened from the page: the box clears
$card = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'FluentFin.Controls.BaseItemDtoCard'))) | Where-Object { -not $_.Current.IsOffscreen }) | Select-Object -First 1
if (-not $card) { $card = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'BaseItemDtoCard'))) | Where-Object { -not $_.Current.IsOffscreen }) | Select-Object -First 1 }
Check 'search: the results page has cards' ([bool]$card)
if ($card) {
    $cardName = $card.Current.Name
    [void](Invoke-Element $card)
    $opened = Wait-Until { if (-not (Find-Ui -Name 'Results for' -Type 'Text' -Contains)) { $true } } -Seconds 15 -PollMs 200
    SrSettle 1200
    Check 'search: a card opens its item, and the box clears' ([bool]$opened -and [string]::IsNullOrEmpty((SrText))) "card '$cardName', text: '$(SrText)'"
    Key 0xA6
    $back = Wait-Ui -Name 'Results for' -Type 'Text' -Contains -Seconds 15
    Check 'search: Back returns to the results page' ([bool]$back)
}
