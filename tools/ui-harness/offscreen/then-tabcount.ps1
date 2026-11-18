# Seaglass scrolling, Tab counts (the before and after of one stop per episode row and one stop for the Library grid):
# run with -Script home,season -TabStops 250 -Then offscreen\then-tabcount.ps1. The season page's walk is the 'season' step's
# (a11y\season-tab.json); this adds the Movies library's (a11y\library-grid-tab.json) and says both counts.
function Tab-Count([string]$Page) {
    $file = Join-Path $Out "a11y\$Page-tab.json"
    if (-not (Test-Path $file)) { return $null }
    $walk = Get-Content $file -Raw -Encoding utf8 | ConvertFrom-Json
    $stops = @($walk.stops | Where-Object { $_.PSObject.Properties['type'] })
    # one cycle: up to the first stop's return (the walk compares whole descriptions, rectangles included, so on a page that
    # scrolls it never sees the first stop again and runs to -TabStops; here the type, name and class decide)
    for ($i = 1; $i -lt $stops.Count; $i++) {
        if ($stops[$i].type -eq $stops[0].type -and $stops[$i].name -eq $stops[0].name -and $stops[$i].class -eq $stops[0].class) { $stops = $stops[0..($i - 1)]; break }
    }
    # content: not the top bar (its tabs, search and account are in its top 48 px; 1.2.0: there is no rail to leave out)
    $content = @($stops | Where-Object { $_.rect -and $_.type -ne 'TitleBar' -and -not ($_.rect[1] -lt 48 -and $_.rect[3] -le 48) })
    [pscustomobject]@{ Page = $Page; Method = $walk.method; Stops = $stops.Count; Content = $content.Count }
}

Open-Library $fixture.movie.library
$card = Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150
Check 'tabcount: the Movies library is shown' ([bool]$card)
Start-Sleep -Seconds 2
Invoke-TabWalk 'library-grid'

foreach ($p in 'season', 'library-grid') {
    $c = Tab-Count $p
    if ($c) { Say ("tabcount {0}: {1} stops in a Tab cycle, {2} of them in the page's content ({3})" -f $c.Page, $c.Stops, $c.Content, $c.Method) }
    else { Say "tabcount ${p}: no walk recorded" }
}
