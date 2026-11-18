# Seaglass scrolling, screenshots of the rows, the Library and a season page, 1920 and 1100 wide, with keyboard focus on a
# card and on an episode row: run with -Script home -Then offscreen\then-scroll-shots.ps1, once per theme and glass mode:
# SEAGLASS_THEME=Light|Dark in the environment, -Setting Glass=Full|Solid, and SG_SHOT_TAG naming the shots (e.g. light-solid).
# Dot-sourced by drive.ps1: no variable here may reuse one of its names (PowerShell names ignore case: $out is its -Out).
$tag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
function Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
function Both-Widths([string]$Name, [scriptblock]$Before) {
    foreach ($size in @(@(1920, 1080), @(1100, 900))) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); Settle 1500
        if ($Before) { & $Before }
        Shot "$tag-$Name-$($size[0])"
    }
}

# Home: the rows' titles, arrows and See all (the light theme was never shot in 1.1.2)
Both-Widths 'home-rows' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Settle 800 }
Both-Widths 'home-rows-lower' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 45); Settle 800 }

# the Library: the grid, then a card with keyboard focus (Tab from Filter, Down, Right)
Open-Library $fixture.movie.library
[void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150)
Settle 2000
Both-Widths 'library' $null
Both-Widths 'library-focus' {
    # 1.1.4: "Filter, none on"; the A-Z jump is the next stop, then the grid
    $filterBtn = Find-Ui -Name 'Filter' -Type 'Button' -Contains
    $tabs = if (Find-Ui -Name 'Jump to a letter' -Type 'List') { 2 } else { 1 }
    if ($filterBtn) { [void][OffscreenUi]::SetFocus($filterBtn); Settle 300; for ($k = 0; $k -lt $tabs; $k++) { Key 0x09; Settle 600 }; Key 0x28; Settle 900; Key 0x27; Settle 900 }
}

# a season page: the episode rows, then a row with keyboard focus (Tab from the season pill, Down)
Step-Series $false
Step-Season $false
Settle 1500
Both-Widths 'season' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Settle 800 }
Both-Widths 'season-focus' {
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Settle 600
    $seasonPill = Find-Ui -Name 'Season 1, current season' -Type 'Button'
    if ($seasonPill) { [void][OffscreenUi]::SetFocus($seasonPill); Settle 300; Key 0x09; Settle 600; Key 0x28; Settle 900; Key 0x28; Settle 900 }
}
