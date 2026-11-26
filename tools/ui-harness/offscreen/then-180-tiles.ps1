# Nixie 1.8, stream t (P1-4): what the user sees of a Made by Nixie tile's words, in the current NIXIE_LAYOUT theme, against
# the fake server (Wikidata's lists from its outside fake: drive.ps1 sets NIXIE_OUTSIDE_BASE). Run with:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script home -NoA11y -Setting <Wikidata's switch, on: its key is under the guard below> -Then offscreen\then-180-tiles.ps1
# Covers: Home's Made by Nixie row says its reason line (facts and a count, never "your") beside its kicker, on screen; the
# Collections page's Nixie tiles: each tile's Narrator name says its reason and count, its words (title, facts, count) are
# drawn inside the tile and on screen, every tile of a row is one height, and a tile wears award badges (Wikidata on); a
# server's box set keeps "N films". Shots <tag>-180-<name>-<width>.png at 1920 and 1100 (T8_WIDTHS). Dot-sourced by
# drive.ps1: every name here starts with t8. ASCII only (Windows PowerShell reads a .ps1 without a BOM in the ANSI code page).

$t8Tag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
if ($env:NIXIE_THEME) { $t8Tag += '-' + $env:NIXIE_THEME.ToLowerInvariant() }
# the window sizes of the shots and the per-size checks: T8_SIZES "1280x720,2560x1392" (default 1920x1080 and 1100x900)
$t8Sizes = @($(if ($env:T8_SIZES) { $env:T8_SIZES } else { '1920x1080,1100x900' }) -split ',' | ForEach-Object { $p = $_ -split 'x'; , @([int]$p[0], [int]$p[1]) })
$t8Count = '\d[\d,]* (more )?(film|films|title|titles|show|shows)\b'

function T8-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function T8-Size([int]$W, [int]$H = 0) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $(if ($H -gt 0) { $H } elseif ($W -ge 1920) { 1080 } else { 900 })); T8-Settle 1500 }
function T8-IntoView($Element) {
    if (-not $Element) { return }
    $p = $null
    try { if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { $p.ScrollIntoView() } } catch {}
    T8-Settle 600
}
# found again until it is on screen: its own ScrollIntoView, else the page scrolled down a step at a time (a tile is a Button,
# which may have no ScrollItem pattern)
function T8-Reveal([scriptblock]$Find) {
    $e = & $Find
    T8-IntoView $e
    $e = & $Find
    for ($pct = 0; $e -and $e.Current.IsOffscreen -and $pct -le 100; $pct += 10) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $pct); T8-Settle 500
        $e = & $Find
    }
    $e
}
function T8-Shots([string]$Name, [scriptblock]$Before) {
    foreach ($size in $t8Sizes) {
        $script:t8At = "$($size[0])x$($size[1])"
        T8-Size $size[0] $size[1]
        if ($Before) { & $Before }
        Shot "$t8Tag-180-$Name-$($script:t8At)"
    }
    T8-Size 1920 1080
}
# the window's client area on screen (the elements' rectangles are screen coordinates)
function T8-Window { $root = [OffscreenUi]::Root($script:hwnd, $script:ui.Id); if ($root) { $root.Current.BoundingRectangle } else { $null } }
# drawn: not off screen, a real size, and inside $Within (the window, or the tile it belongs to)
function T8-Drawn($Element, $Within) {
    if (-not $Element) { return $false }
    try {
        $c = $Element.Current
        $r = $c.BoundingRectangle
        if ($c.IsOffscreen -or $r.IsEmpty -or $r.Width -lt 4 -or $r.Height -lt 4) { return $false }
        if ($Within -and -not $Within.IsEmpty) {
            return ($r.Left -ge $Within.Left - 1) -and ($r.Right -le $Within.Right + 1) -and ($r.Top -ge $Within.Top - 1) -and ($r.Bottom -le $Within.Bottom + 1)
        }
        return $true
    }
    catch { return $false }
}
function T8-Texts($Element) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    @([OffscreenUi]::Under($Element, $cond))
}

# --- the guard (BH-170-q1): Wikidata's lists come from the fake server only ---------------------------------------------
# The app's log names where the outside sources go; anything but loopback: the badge checks are skipped and said so (the
# switch is the run's -Setting, on from the start: a release package, which ignores NIXIE_OUTSIDE_BASE, must not be run here)
$t8Log = Get-ChildItem -LiteralPath (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$t8Fake = $t8Log -and (Select-String -LiteralPath $t8Log.FullName -Pattern '\[outside\] harness run: every outside source goes to "?http://127\.0\.0\.1:' -Quiet)
Check "tiles ($t8Tag): the app sends every outside source to the fake server" ([bool]$t8Fake) $(if ($t8Log) { $t8Log.Name } else { 'no app log' })
# the run line: run-offscreen.ps1 ... -Script home -NoA11y -Setting LookUpWikidata=true -Then offscreen\then-180-tiles.ps1

# --- Home: the Made by Nixie rows say why, beside their kicker ----------------------------------------------------------
# 1.8 gate fix: stream l's weekly lineup chooses Home's Made by Nixie rows: CollectionLineup.HomeDefault (2) by default, the
# best personal row first, then a seasonal, trending or Spotlight one, then the lineup's order; "Unfinished series" is Home's
# own row beside them, not counted (HomeViewModel.Nixie.cs). Which kinds they are depends on the library, the switches and the
# day (Acclaimed only when no canon list is on, and Wikidata's lists are on in this run), so the rows are found by their reason
# line, in every theme, not by 1.7's one title ("Acclaimed, not seen yet"). The line: Classic's and Cinematic's
# NixieRowReason under "MADE BY NIXIE"; Drive-in's and Editorial's caption "Made by Nixie <middle dot> <reason>".
$t8TextCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$t8Caption = '^Made by Nixie ' + [char]0x00B7 + ' '
function T8-HomeReasons {
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $t8TextCond) | Where-Object {
            try { $_.Current.AutomationId -eq 'NixieRowReason' -or $_.Current.Name -match $t8Caption } catch { $false } })
}
function T8-ReasonOf([string]$Name) { $Name -replace $t8Caption, '' }
# the rows fill in (the index's first pages, then the whole read): the count once two looks 2 s apart agree
$t8Seen = -1; $t8Reasons = @()
$t8Until = [DateTime]::UtcNow.AddSeconds(60)
while ([DateTime]::UtcNow -lt $t8Until) {
    $t8Reasons = T8-HomeReasons
    if ($t8Reasons.Count -gt 0 -and $t8Reasons.Count -eq $t8Seen) { break }
    $t8Seen = $t8Reasons.Count
    Start-Sleep -Milliseconds 2000
}
$t8ReasonNames = @($t8Reasons | ForEach-Object { T8-ReasonOf $_.Current.Name })
$t8Unfinished = [bool](Find-Ui -Name 'Unfinished series' -Type 'List' -OffscreenToo)
$t8Lineup = $t8Reasons.Count - $(if ($t8Unfinished) { 1 } else { 0 })
Check "tiles ($t8Tag): Home shows its lineup's Made by Nixie rows (1 to 2 by default, Unfinished series apart)" (($t8Lineup -ge 1) -and ($t8Lineup -le 2)) "$t8Lineup lineup rows$(if ($t8Unfinished) { ' and Unfinished series' }): $($t8ReasonNames -join ' | ')"
$t8First = if ($t8Reasons.Count) { $t8Reasons[0].Current.Name } else { $null }
$t8Reason = if ($t8First) { T8-Reveal { Find-Ui -Name $t8First -Type 'Text' -OffscreenToo } } else { $null }
$t8ReasonName = if ($t8Reason) { T8-ReasonOf $t8Reason.Current.Name } else { '' }
Check "tiles ($t8Tag): Home's Nixie row says its reason line" ([bool]$t8ReasonName) $t8ReasonName
$t8NoCount = @($t8ReasonNames | Where-Object { $_ -notmatch $t8Count })
Check "tiles ($t8Tag): every row's reason line has a count" (($t8ReasonNames.Count -gt 0) -and ($t8NoCount.Count -eq 0)) "without: $($t8NoCount -join ' | ')"
$t8YourRows = @($t8ReasonNames | Where-Object { $_ -match '\byour\b' })
Check "tiles ($t8Tag): no row's reason line says your" ($t8YourRows.Count -eq 0) ($t8YourRows -join ' | ')
Check "tiles ($t8Tag): the row's reason line is drawn on screen" (T8-Drawn $t8Reason (T8-Window)) "$(if ($t8Reason) { $t8Reason.Current.BoundingRectangle })"
# the shot: Home at its end, where the Made by Nixie rows are, so the row's cards show under its line
T8-Shots 'home-row' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); T8-Settle 900 }

# --- the Collections page: each Nixie tile's words ------------------------------------------------------------------------
Open-Nav 'Collections'
$t8Any = Wait-Until { Find-Ui -Name ', made by Nixie, ' -Contains -Type 'Button' -OffscreenToo } -Seconds 60 -PollMs 300
Check "tiles ($t8Tag): the Collections page has Nixie tiles" ([bool]$t8Any) ''
T8-Settle 1500
$t8ButtonCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$t8Tiles = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $t8ButtonCond) | Where-Object { try { $_.Current.Name -like '*, made by Nixie, *' } catch { $false } })
$t8Names = @($t8Tiles | ForEach-Object { $_.Current.Name })
Say "tiles ($t8Tag): $($t8Names.Count) Nixie tiles"
$t8NoCount = @($t8Names | Where-Object { $_ -notmatch $t8Count })
Check "tiles ($t8Tag): every Nixie tile's Narrator name says a count" (($t8Names.Count -gt 0) -and ($t8NoCount.Count -eq 0)) "$($t8Names.Count) tiles; without: $($t8NoCount -join ' | ')"
# the name is "<title>, made by Nixie, <kind>, <facts>, <count>...": a reason line adds at least one part past the kind
$t8NoReason = @($t8Names | Where-Object { ($_ -split ', ').Count -lt 5 })
Check "tiles ($t8Tag): every Nixie tile's Narrator name says its reason" ($t8NoReason.Count -eq 0) "without: $($t8NoReason -join ' | ')"
$t8Your = @($t8Names | Where-Object { $_ -match '^your\b' -or $_ -match '\byour\b' })
Check "tiles ($t8Tag): no Nixie tile says your" ($t8Your.Count -eq 0) ($t8Your -join ' | ')
$t8Badged = @($t8Names | Where-Object { $_ -match ' (winner|winners|nominee|nominees)$' -or $_ -match ' (winner|winners|nominee|nominees), ' })
if ($t8Fake) { Check "tiles ($t8Tag): a tile wears award badges (Wikidata's lists on)" ($t8Badged.Count -gt 0) "$($t8Badged.Count): $($t8Badged | Select-Object -First 2)" }
else { Say "tiles ($t8Tag): the badge check skipped: the app's outside sources don't go to the fake server" }

# one franchise tile, scrolled into view: its title, facts and count drawn inside it
# (1.8.1: found, then lost by the reveal once in Cinematic, the page redrawing its rows as the week's Spotlight came: the
# reveal is tried again while the 25 s last, as the finding is)
$t8Franchise = Wait-Until { T8-Reveal { Find-Ui -Name ', made by Nixie, franchise, ' -Contains -Type 'Button' -OffscreenToo } } -Seconds 25 -PollMs 250
# (r4: not found within 10 s in two themes, and its four checks were skipped without a word: now a failure of its own)
Check "tiles ($t8Tag): a franchise tile is there to check" ([bool]$t8Franchise) 
if ($t8Franchise) {
    $t8Rect = $t8Franchise.Current.BoundingRectangle
    $t8Words = @(T8-Texts $t8Franchise | Where-Object { try { $_.Current.Name } catch { $false } })
    $t8Shown = @($t8Words | Where-Object { T8-Drawn $_ $t8Rect })
    $t8WordNames = @($t8Words | ForEach-Object { $_.Current.Name })
    Say "tiles ($t8Tag): franchise tile '$($t8Franchise.Current.Name)' words: $($t8WordNames -join ' | ')"
    Check "tiles ($t8Tag): the franchise tile draws its facts" (@($t8Shown | Where-Object { $_.Current.Name -match 'TMDb' }).Count -ge 1) ($t8WordNames -join ' | ')
    Check "tiles ($t8Tag): the franchise tile draws its count on a line of its own" (@($t8Shown | Where-Object { $_.Current.Name -match "^$t8Count" }).Count -ge 1) ($t8WordNames -join ' | ')
    Check "tiles ($t8Tag): every word of the tile is drawn inside it" (($t8Words.Count -ge 3) -and ($t8Shown.Count -eq $t8Words.Count)) "$($t8Shown.Count) of $($t8Words.Count) inside $t8Rect"
    # one size: the tiles beside it (the same top) are as tall and as wide
    # the tiles found again now the row is in view (the first search saw the tiles realized then)
    $t8Now = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $t8ButtonCond) | Where-Object { try { $_.Current.Name -like '*, made by Nixie, *' } catch { $false } })
    $t8Line = @($t8Now | Where-Object { try { [Math]::Abs($_.Current.BoundingRectangle.Top - $t8Rect.Top) -lt 2 -and -not $_.Current.IsOffscreen } catch { $false } })
    $t8TileSizes = @($t8Line | ForEach-Object { '{0}x{1}' -f [int]$_.Current.BoundingRectangle.Width, [int]$_.Current.BoundingRectangle.Height } | Sort-Object -Unique)
    Check "tiles ($t8Tag): every tile of its row is one size" (($t8Line.Count -ge 1) -and ($t8TileSizes.Count -eq 1)) "$($t8Line.Count) tiles: $($t8TileSizes -join ', ')"
}
# a server's box set keeps its own line
$t8Server = Find-Ui -Name ', collection, ' -Contains -Type 'Button' -OffscreenToo
if ($t8Server) {
    Check "tiles ($t8Tag): a server's box set keeps its count" ($t8Server.Current.Name -match '\d+ films?') $t8Server.Current.Name
}
# 1.8.1 (BH-180-q2): a server's box set's tile ends under its own words: it had a Nixie tile's words height (room for three
# count lines), about 85 px empty under its two lines, and its row was taller. Its words drawn inside it, at most three lines'
# room under the last one, and every tile of its row one size
$t8Server = T8-Reveal { Find-Ui -Name ', collection, ' -Contains -Type 'Button' -OffscreenToo }
Check "tiles ($t8Tag): a server's box set is there to check" ([bool]$t8Server) '(FAKESERVER_COLLECTIONS)'
if ($t8Server) {
    $t8SRect = $t8Server.Current.BoundingRectangle
    $t8SWords = @(T8-Texts $t8Server | Where-Object { try { $_.Current.Name -and (T8-Drawn $_ $t8SRect) } catch { $false } })
    $t8Last = ($t8SWords | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum).Maximum
    $t8Line = ($t8SWords | ForEach-Object { $_.Current.BoundingRectangle.Height } | Measure-Object -Minimum).Minimum
    $t8Empty = if ($t8SWords.Count) { $t8SRect.Bottom - $t8Last } else { -1 }
    Check "tiles ($t8Tag): a server's box set's tile ends under its own words (no room left for a Nixie tile's count lines)" (($t8SWords.Count -ge 2) -and ($t8Empty -ge 0) -and ($t8Empty -le 3 * $t8Line)) "$([int]$t8Empty) px under its last line (a line $([int]$t8Line) px): $(($t8SWords | ForEach-Object { $_.Current.Name }) -join ' | ')"
    # one height (a tile at the row's edge is cut by the row's view: its width says nothing)
    $t8SNow = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $t8ButtonCond) | Where-Object { try { $_.Current.Name -like '*, collection, *' -and -not $_.Current.IsOffscreen -and [Math]::Abs($_.Current.BoundingRectangle.Top - $t8SRect.Top) -lt 2 } catch { $false } })
    $t8SSizes = @($t8SNow | ForEach-Object { [int]$_.Current.BoundingRectangle.Height } | Sort-Object -Unique)
    Check "tiles ($t8Tag): every tile of the server's row is one height" (($t8SNow.Count -ge 1) -and ($t8SSizes.Count -eq 1)) "$($t8SNow.Count) tiles: $($t8SSizes -join ', ') px high"
}
# at each window size: the franchise tile's words still drawn inside it (a size change moves nothing out of it)
T8-Shots 'collections' {
    $t = T8-Reveal { Find-Ui -Name ', made by Nixie, franchise, ' -Contains -Type 'Button' -OffscreenToo }
    if ($t) {
        $r = $t.Current.BoundingRectangle
        $words = @(T8-Texts $t | Where-Object { try { $_.Current.Name } catch { $false } })
        $inside = @($words | Where-Object { T8-Drawn $_ $r })
        Check "tiles ($t8Tag): at $($script:t8At) the franchise tile's words are drawn inside it" (($words.Count -ge 3) -and ($inside.Count -eq $words.Count)) "$($inside.Count) of $($words.Count) inside $r"
    }
}
$t8BadgedName = if ($t8Badged.Count) { $t8Badged[0] } else { $null }
if ($t8BadgedName) { T8-Shots 'badges' { [void](T8-Reveal { Find-Ui -Name $t8BadgedName -Type 'Button' -OffscreenToo }) } }
