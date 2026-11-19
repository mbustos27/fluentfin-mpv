# Nixie 1.4.0 (style stream): the highlight vocabulary and the button system, keyboard first, at 1920 and 1100 wide: run with
# -Script home -Then offscreen\then-140-style.ps1 once per layout theme (NIXIE_LAYOUT) and colour theme (NIXIE_THEME=Light|Dark);
# SG_SHOT_TAG names the shots. Shots: Home's rows (one See all link each), the Movies grid with the keyboard on a card (the
# two-tone ring, the action strip, the neutral watched marks), the Shows grid (the "N to watch" counts), the series page (the
# season picker, the rows' See all), and the movie page's delete confirmation (the trash glyph; Delete with the keyboard: its
# danger-ink glow). Hover cannot be made off-screen (the pointer hook has none): the mouse-first orders are unit-tested
# (HighlightVocabularyTests). Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
$styleTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
function Style-Settle([int]$Ms = 1200) { Start-Sleep -Milliseconds $Ms }
function Style-Both([string]$Name, [scriptblock]$Before) {
    foreach ($size in @(@(1920, 1080), @(1100, 900))) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); Style-Settle 1500
        if ($Before) { & $Before }
        Shot "$styleTag-$Name-$($size[0])"
    }
}
function Style-FocusCard([string]$Title) {
    $card = Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $Title) } -Seconds 20 -PollMs 150
    if (-not $card) { $card = Find-MediaCard $Title }
    if ($card) { [void][OffscreenUi]::SetFocus($card); Style-Settle 900; Say "style: keyboard on '$Title'" } else { Say "style: no card '$Title'" }
}

# Home: the rows' headers (the title a heading, one See all link at the right)
Open-Nav 'Home'
Style-Settle 2500
Style-Both 'home-rows' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 30); Style-Settle 800 }

# Movies: the grid at rest, then the keyboard on the showcase movie's card
Open-Library $fixture.movie.library
[void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150)
Style-Settle 2000
Style-Both 'movies-focus' { Style-FocusCard $fixture.movie.name; Key 0x27; Style-Settle 900 }

# Shows: the "N to watch" counts, top left; the watched checks, top right
Open-Library $fixture.series.library
Style-Settle 2500
Style-Both 'shows' $null

# the series page: the season picker (Drive-in's tubes on the Tab role), the rows' See all
Step-Series $false
Style-Settle 2000
Style-Both 'series' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Style-Settle 800 }

# the movie page's delete confirmation: the menu's Delete, the dialog, then Delete with the keyboard (its focus glow)
Step-Movie
Style-Settle 2000
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Style-Settle 1500
$more = Find-Ui -Name 'More options' -Type 'Button' -Contains
if ($more) {
    Say "style: more ($(Invoke-Element $more))"; Style-Settle 900
    $delete = Wait-Ui -Name 'Delete' -Type 'MenuItem' -Contains -Seconds 5
    if ($delete) {
        Say "style: delete ($(Invoke-Element $delete))"; Style-Settle 1500
        Shot "$styleTag-delete-dialog-1920"
        Key 0x09 -Shift; Style-Settle 700
        Shot "$styleTag-delete-dialog-focus-1920"
        Key 0x1B; Style-Settle 900
    } else { Say 'style: no Delete in the menu'; Key 0x1B }
} else { Say 'style: no More button on the movie page' }
