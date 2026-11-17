# Seaglass scrolling, keyboard and focus (acceptance tests 3 and 4): run with -Script home -Then offscreen\then-rows.ps1
function Focused-Name { $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($f) { ($f | ConvertFrom-Json).name } }
function Tab-Walk([int]$Max = 40, [string]$Label = 'walk') {
    $seen = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $Max; $i++) {
        Key 0x09; Start-Sleep -Milliseconds 250
        $n = Focused-Name
        [void]$seen.Add($n)
    }
    Say "$Label : $($seen -join ' > ')"
    , $seen
}
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 600
$first = [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.continue_first)
$sa = Find-Ui -Name 'See all Continue Watching' -Type 'Button'
if ($sa) { [void][OffscreenUi]::SetFocus($sa); Start-Sleep -Milliseconds 300 }
Say "start: $(Focused-Name)"
[void](Tab-Walk 6 'tab from the first See all')
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100)
Start-Sleep -Milliseconds 800
$sa = Find-Ui -Name 'See all Recently Added In Movies' -Type 'Button'
[void][OffscreenUi]::SetFocus($sa); Start-Sleep -Milliseconds 300
Say "start2: $(Focused-Name)"
[void](Tab-Walk 8 'tab from Movies See all, page scrolled to the bottom')
