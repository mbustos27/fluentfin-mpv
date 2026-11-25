# Nixie 1.8 (P1-1, stream l): what the user sees of the week's lineup, in the theme of NIXIE_LAYOUT, at the gate's sizes
# (GATE_SIZES: 1280x720 and maximised; one size outside the gate): Home's Made by Nixie rows said and drawn at Home's end; the
# Collections page's This week, first under Made by Nixie, its first tile on screen; See all's direction button by the
# keyboard (Space), its Narrator name before and after, its bounds unchanged when it flips (rule 7), the direction kept after
# Back. Run with FAKESERVER_COLLECTIONS=1 and FAKESERVER_COLLIDE=1 (the server's collections row ends in See all) and
# `-Script home -NoA11y -Then offscreen\then-180-week.ps1`. Dot-sourced by drive.ps1: every name here starts with wk180.
# ASCII only.
. (Join-Path $PSScriptRoot '..\..\gate\gate-common.ps1')
$wk180Tag = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
$wk180Sizes = @(Get-GateSizes)
if ($wk180Sizes.Count -eq 0) { $wk180Sizes = @('1920x1080') }
function Wk180-Lines([string]$Pattern) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
            Get-Content $_.FullName -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match $Pattern }
        })
}
function Wk180-IntoView($Element) {
    if (-not $Element) { return }
    $p = $null
    try { if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { $p.ScrollIntoView() } } catch {}
    Start-Sleep -Milliseconds 700
}
function Wk180-Button { Find-Ui -Id 'GridDirectionButton' -OffscreenToo }
function Wk180-OnScreen($Element) { $Element -and -not $Element.Current.IsOffscreen -and $Element.Current.BoundingRectangle.Width -gt 0 }

# 1. Home's Made by Nixie rows (the lineup's): said, and drawn at Home's end
$wk180Deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $wk180Deadline -and (Wk180-Lines 'Home: \d+ Made by Nixie rows').Count -eq 0) { Start-Sleep -Milliseconds 500 }
Check "week180 ($wk180Tag): Home said its Made by Nixie rows" ((Wk180-Lines 'Home: \d+ Made by Nixie rows').Count -gt 0) "$(Wk180-Lines 'Home: \d+ Made by Nixie rows' | Select-Object -Last 1)"
foreach ($wk180Size in $wk180Sizes) {
    [void](Set-GateSize $wk180Size)
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Start-Sleep -Milliseconds 1500
    $wk180Kicker = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $_.Cached.Name -match '(?i)^made by nixie' -and -not $_.Cached.IsOffscreen } catch { $false } })
    Check "week180 ($wk180Tag, $wk180Size): a Made by Nixie row is drawn at Home's end" ($wk180Kicker.Count -gt 0) "$($wk180Kicker.Count) kickers on screen"
    Shot "$wk180Tag-180-home-nixie-$wk180Size"
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 500
}

# 2. the Collections page: This week, first under Made by Nixie, its first tile on screen
Open-Nav 'Collections'
$wk180Week = Wait-Until { Find-Ui -Name 'This week' -OffscreenToo } -Seconds 90 -PollMs 400
Check "week180 ($wk180Tag): the Collections page shows This week" ([bool]$wk180Week) ''
if ($wk180Week) {
    foreach ($wk180Size in $wk180Sizes) {
        [void](Set-GateSize $wk180Size)
        Wk180-IntoView (Find-Ui -Name ', made by Nixie, ' -Contains -OffscreenToo)
        Wk180-IntoView (Find-Ui -Name 'This week' -OffscreenToo)
        $wk180First = Find-Ui -Name ', made by Nixie, ' -Contains -OffscreenToo
        Wk180-IntoView $wk180First
        Check "week180 ($wk180Tag, $wk180Size): This week's first tile is on screen" (Wk180-OnScreen (Find-Ui -Name ', made by Nixie, ' -Contains -OffscreenToo)) ''
        Shot "$wk180Tag-180-thisweek-$wk180Size"
    }
}

# 3. See all (the server's collections row: FAKESERVER_COLLIDE) and its direction button, by the keyboard
[void](Set-GateSize $wk180Sizes[0])
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 600
$wk180See = Wait-Until { Find-Ui -Name 'See all' -Contains -Type 'Hyperlink' -OffscreenToo } -Seconds 30 -PollMs 400
Check "week180 ($wk180Tag): a row ends in See all" ([bool]$wk180See) ''
if ($wk180See) {
    [void](Invoke-Element $wk180See)
    $wk180Btn = Wait-Until { Wk180-Button } -Seconds 20 -PollMs 300
    Check "week180 ($wk180Tag): See all has the direction button" ([bool]$wk180Btn) ''
    if ($wk180Btn) {
        [void](Wait-Ui -Id 'TitleText' -Name "The server's collections" -OffscreenToo -Seconds 15)
        Start-Sleep -Milliseconds 2000   # its transition over (mid-transition the button sat 140 px lower)
        $wk180Btn = Wk180-Button
        $wk180Name0 = $wk180Btn.Current.Name
        $wk180Rect0 = $wk180Btn.Current.BoundingRectangle
        Check "week180 ($wk180Tag): its Narrator name says the direction and what a press does" ($wk180Name0 -eq 'A to Z, switch to Z to A') $wk180Name0
        [void][OffscreenUi]::SetFocus($wk180Btn); Start-Sleep -Milliseconds 500
        Key 0x20   # Space on the focused button
        $wk180Flipped = Wait-Until { $b = Wk180-Button; if ($b -and $b.Current.Name -like 'Z to A*') { $b } } -Seconds 10 -PollMs 200
        Check "week180 ($wk180Tag): Space on the button runs the grid Z to A" ([bool]$wk180Flipped) "$(if ($wk180Flipped) { $wk180Flipped.Current.Name } else { (Wk180-Button).Current.Name })"
        if ($wk180Flipped) {
            $wk180Rect1 = $wk180Flipped.Current.BoundingRectangle
            Check "week180 ($wk180Tag): the button doesn't move or change size when it flips (rule 7)" ($wk180Rect0 -eq $wk180Rect1) "$wk180Rect0 -> $wk180Rect1"
            Shot "$wk180Tag-180-grid-reversed"
            $wk180Back = Find-Ui -Name 'Back' -Type 'Button'
            if ($wk180Back) { [void](Invoke-Element $wk180Back); Start-Sleep -Milliseconds 1500 }
            $wk180See2 = Wait-Until { Find-Ui -Name 'See all' -Contains -Type 'Hyperlink' -OffscreenToo } -Seconds 20 -PollMs 300
            if ($wk180See2) { [void](Invoke-Element $wk180See2) }
            $wk180Kept = Wait-Until { Wk180-Button } -Seconds 15 -PollMs 300
            Check "week180 ($wk180Tag): See all again after Back is still Z to A" ([bool]($wk180Kept -and $wk180Kept.Current.Name -like 'Z to A*')) "$(if ($wk180Kept) { $wk180Kept.Current.Name })"
        }
    }
}
