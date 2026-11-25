# Nixie 1.7.4 (the owner, 2026-11-23: "going back to the collection from a movie in the collection makes u wait for the
# collection to load again"; a part of the basics gate): Back to a page just left shows it as it was, at once: its items, its
# scroll, the focus, with no loading state; a change made on the page Back leaves (a film marked watched) shows; R loads again.
# Run per theme (back-gate.ps1 runs the four and writes the grid):
#   $env:FAKESERVER_COLLECTIONS='1'; $env:FAKESERVER_LONG_TITLE='1'; $env:FAKESERVER_BIG_SERIES='24x13'
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   [$env:BK_PAGES='collection,playlist,nixie,library,season,series,item,search,person,collections,home,change,refresh,signout']
#   [$env:BK_DELAY_MS='2500'] [$env:BK_LIMIT_MS='1200'] [$env:SG_SHOT_TAG='...']
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-174-back.ps1
# Each page: opened and loaded, a card (or row) down it given the keyboard (the page scrolled to it where it is long), the card
# opened; then every answer of the fake server is held BK_DELAY_MS (so a reload is visible) and Back pressed (BrowserBack). The
# row: how long until the card left from is on screen again, whether a loading state showed meanwhile (a Shimmer placeholder, a
# progress ring, "Loading..."), the page's scroll and the keyboard against what they were, and the requests the app sent for it
# after Back. A row passes when the card is back within BK_LIMIT_MS, no loading state showed, and the scroll and focus are kept.
#   forward   (with collection) Forward from the collection Back showed, to the film it was left for: its page at once, nothing asked
#   change    a film of a collection marked watched on its own page: the collection's card for it says "watched" after Back
#   refresh   R on a page Back showed asks the server again (the page's items asked for)
#   samelibrary  (1.7.5) Movies, the Shows tab, Back: Movies' cards; Forward: Shows' (two pages of one kind, kept whole)
#   samesearch   (1.7.5) a search, a second one from its results, Back: the first title and results; Forward: the second's
#   signout   sign out from a page with pages behind it: the app's log says the kept pages were let go, and none is shown
#             after signing in again (Back from the first page opened is off)
# Rows go to <Out>\back174\back174.json (theme, page, ms, loading, scroll, focus, requests, ok, detail), each a Check; shots
# per page. Dot-sourced by drive.ps1: every name here starts with bk / Bk- ($out is its -Out).
# Nixie 1.7.5, in the basics gate (tools\gate\manifest\back.psd1): with GATE_SIZES (1280x720,max) each page's cell runs at each
# size, the window sized first (tools\gate\gate-common.ps1's Set-GateSize); the cells the window's size has no part in
# (change, refresh, signout) once, at the first. The Check names are then the gate's: "back [Classic, 1280x720, collection]:
# ...". back-gate.ps1 sets no GATE_SIZES: the window as it opens, as before.

$bkTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$bkTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { $bkTheme.ToLowerInvariant() }
$bkBaseTag = $bkTag
$bkSizes = @($null)
if ($env:GATE_SIZES -and $env:GATE_SIZES -ne 'none') {
    . (Join-Path $PSScriptRoot '..\..\gate\gate-common.ps1')
    $bkSizes = @(Get-GateSizes)
}
$bkOnce = @('change', 'refresh', 'signout')
$bkSize = $null; $bkSizeSaid = ''
$bkPages = @($(if ($env:BK_PAGES) { $env:BK_PAGES } else { 'collection,playlist,nixie,library,season,series,item,search,person,collections,home,samelibrary,samesearch,change,refresh,signout' }) -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$bkDelay = if ($env:BK_DELAY_MS) { [int]$env:BK_DELAY_MS } else { 2500 }
$bkLimit = if ($env:BK_LIMIT_MS) { [int]$env:BK_LIMIT_MS } else { 1200 }
$bkDir = Join-Path $Out 'back174'
New-Item -ItemType Directory -Force $bkDir | Out-Null
$bkRows = New-Object System.Collections.Generic.List[object]
$bkBs = $fixture.big_series
$bkA = [System.Windows.Automation.AutomationElement]
$bkIds = [System.Windows.Automation.AutomationElementIdentifiers]
. (Join-Path $PSScriptRoot 'search-common.ps1')

# ---- reading the window -----------------------------------------------------------------------------------------------------
# the element with the keyboard: its name, control type, class and place in its set
function Bk-Focus {
    $cond = New-Object System.Windows.Automation.PropertyCondition($bkA::HasKeyboardFocusProperty, $true)
    $el = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if (-not $el) { return $null }
    [pscustomobject]@{ Name = $el.Cached.Name; Class = $el.Cached.ClassName; Type = ($el.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', '')
        Pos = [int]$el.GetCurrentPropertyValue($bkIds::PositionInSetProperty); Size = [int]$el.GetCurrentPropertyValue($bkIds::SizeOfSetProperty); Element = $el }
}
# the first answer of $Read that isn't empty, looked at up to $Times times $Ms apart ($null when none)
function Bk-Retry([scriptblock]$Read, [int]$Times = 5, [int]$Ms = 250) {
    for ($i = 0; $i -lt $Times; $i++) { $v = & $Read; if ($v) { return $v }; if ($i -lt $Times - 1) { Start-Sleep -Milliseconds $Ms } }
    $null
}
function Bk-Describe($f) { if ($f) { "'{0}' [{1} {2}] {3} of {4}" -f $f.Name, $f.Type, $f.Class, $f.Pos, $f.Size } else { 'no focus' } }
# how far down the page is, in pixels: the largest vertically scrollable element's offset, from its percent and its view's share
# of what it holds (-1: the page does not scroll). A page's height can change a little as its pictures come, which moves the
# percent of the same place: pixels are what the eye compares
function Bk-Px {
    $best = $null; $area = 0
    foreach ($e in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($bkA::IsScrollPatternAvailableProperty, $true))))) {
        try {
            $r = $e.Cached.BoundingRectangle
            $sp = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if (-not $sp.Current.VerticallyScrollable -or $r.IsEmpty) { continue }
            if ($r.Width * $r.Height -gt $area) { $area = $r.Width * $r.Height; $best = @($r.Height, $sp.Current.VerticalScrollPercent, $sp.Current.VerticalViewSize) }
        } catch { }
    }
    if (-not $best -or $best[2] -le 0) { return -1 }
    $extent = $best[0] * 100 / $best[2]
    [Math]::Round($best[1] / 100 * ($extent - $best[0]))
}
# how far down the page is: the largest vertically scrollable element's percent (-1: the page does not scroll)
function Bk-V {
    $best = $null
    foreach ($s in @([OffscreenUi]::ScrollState($script:hwnd, $script:ui.Id))) {
        $p = $s -split '\|'   # name|class|x,y,w,h|h%|v%|h scrollable|v scrollable
        if ($p[6] -ne 'True') { continue }
        $r = $p[2] -split ','; $area = [double]$r[2] * [double]$r[3]
        if (-not $best -or $area -gt $best.Area) { $best = [pscustomobject]@{ Area = $area; V = [double]$p[4] } }
    }
    if ($best) { $best.V } else { -1 }
}
# the loading states a page shows: Shimmer placeholders, a progress ring, "Loading..." words (each only while loading)
function Bk-Loading {
    $n = [OffscreenUi]::CountByClass($script:hwnd, $script:ui.Id, @('Shimmer', 'ProgressRing'))
    $count = 0; foreach ($k in $n.Keys) { $count += $n[$k] }
    if (-not $count -and (Find-Ui -Name 'Loading' -Type 'Text' -Contains)) { $count = 1 }
    $count
}
# an element given the keyboard as a user's is: in view (UI Automation's SetFocus leaves a far card out of view, which no user
# leaves a page from), then focused
function Bk-Take($Element) {
    $p = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { try { $p.ScrollIntoView() } catch { } ; Start-Sleep -Milliseconds 500 }
    [void][OffscreenUi]::SetFocus($Element); Start-Sleep -Milliseconds 900
}
# the element left from, on screen again: the same name and control type, not scrolled out of view (the first one found: a
# look through every element of the window took seconds on Home)
function Bk-Find($Left) {
    try {
        $ct = [System.Windows.Automation.ControlType]::($Left.Type)
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($bkA::ProcessIdProperty, [int]$script:ui.Id)),
            (New-Object System.Windows.Automation.PropertyCondition($bkA::NameProperty, $Left.Name)),
            (New-Object System.Windows.Automation.PropertyCondition($bkA::ControlTypeProperty, $ct)),
            (New-Object System.Windows.Automation.PropertyCondition($bkA::IsOffscreenProperty, $false)))
        return $bkA::FromHandle($script:hwnd).FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    } catch { }
    [OffscreenUi]::Find($script:hwnd, $script:ui.Id, $Left.Name, $null, $Left.Type, $false, $false)
}
# the same, from the element with the keyboard (one cheap call; a look through the whole window takes seconds on Drive-in's Home,
# and would be most of what is measured): the card left from, back with the keyboard, on screen
function Bk-FocusedIs($Left) {
    try {
        $f = $bkA::FocusedElement
        if (-not $f -or $f.Current.ProcessId -ne [int]$script:ui.Id) { return $null }
        # the element with the keyboard, or one it is in (an Editorial poster's part has it)
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        for ($i = 0; $f -and $i -lt 4; $i++) {
            if ($f.Current.Name -eq $Left.Name -and -not $f.Current.IsOffscreen) { return $f }
            $f = $walker.GetParent($f)
        }
    } catch { }
    $null
}
function Bk-Latency([int]$Ms) { [void](Invoke-Server '/__harness/latency' 'POST' @{ default = $Ms; rules = @() }) }
# the cards of the page (any theme's), in or out of view
function Bk-Cards([string[]]$Classes = @('BaseItemDtoCard', 'EditorialPoster', 'EditorialStill')) {
    $conds = @($Classes | ForEach-Object { New-Object System.Windows.Automation.PropertyCondition($bkA::ClassNameProperty, $_) })
    $cond = if ($conds.Count -eq 1) { $conds[0] } else { New-Object System.Windows.Automation.OrCondition(, [System.Windows.Automation.Condition[]]$conds) }
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond))
}
# a card given the keyboard: with -Scroll, the page scrolled that far down first (a percent) and the card on screen lowest down
# taken (the scroll Back must keep is the page's, not one the focus made); else the card at place Pos of the page's cards (by
# PositionInSet; by order when they give none)
function Bk-FocusCard([int]$Pos, [string[]]$Classes = @('BaseItemDtoCard', 'EditorialPoster', 'EditorialStill'), [double]$Scroll = -1) {
    if ($Scroll -ge 0) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, $Scroll); Start-Sleep -Milliseconds 1200
        $shown = @(Bk-Cards $Classes | Where-Object { -not $_.Cached.IsOffscreen -and $_.Cached.BoundingRectangle.Height -gt 0 } | Sort-Object { $_.Cached.BoundingRectangle.Y } -Descending)
        if (-not $shown.Count) {
            # (1.7.5, the gate at 1280 x 720: Cinematic's film page has its More like this along its stage's foot, above the
            # page's end, so none is on screen there: the page back at its top, and the card at place Pos, brought into view)
            [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Start-Sleep -Milliseconds 1200
            return Bk-FocusCard $Pos $Classes
        }
        # (1.7.5, the gate at max: the lowest card drawn had its name under the window's foot, and Back's focus brought it into
        # view, 78 px: a card the keyboard is on is always wholly in view; the lowest one wholly in the window, if any)
        try {
            $win = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd).Current.BoundingRectangle
            $whole = @($shown | Where-Object { $r = $_.Cached.BoundingRectangle; $r.Bottom -le $win.Bottom - 4 -and $r.Top -ge $win.Top + 48 })
            if ($whole.Count) { $shown = $whole }
        } catch { }
        Bk-Take $shown[0]
        return Bk-Focus
    }
    $cards = @(Bk-Cards $Classes)
    if (-not $cards.Count) { return $null }
    $card = $cards | Where-Object { [int]$_.GetCurrentPropertyValue($bkIds::PositionInSetProperty) -eq $Pos } | Select-Object -First 1
    if (-not $card) { $card = $cards[[Math]::Min($Pos, $cards.Count) - 1] }
    Bk-Take $card
    Bk-Focus
}
# opens what has the keyboard (Enter), then an Editorial poster's "Full details" (its details open in place first, under its
# row: the page as the user leaves it is the page with them open, $script:bkLeave)
function Bk-OpenFocused([int]$Vk = 0x0D) {
    $script:bkLeave = $null
    Key $Vk
    $full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2 -PollMs 150
    if ($full) {
        Start-Sleep -Milliseconds 600
        $script:bkLeave = @((Bk-V), (Bk-Px))
        [void](Invoke-Element $full)
    }
}
# waits until the page left from has gone (the element left from is no longer there)
function Bk-WaitGone($Left, [double]$Seconds = 15) {
    [bool](Wait-Until { if (-not ([OffscreenUi]::Find($script:hwnd, $script:ui.Id, $Left.Name, $null, $Left.Type, $false, $true))) { $true } } -Seconds $Seconds -PollMs 200)
}
function Bk-Record([string]$Page, [hashtable]$Row) {
    $o = [ordered]@{ theme = $bkTheme; page = $Page }
    if ($script:bkSize) { $o.size = $script:bkSize }
    foreach ($k in $Row.Keys) { $o[$k] = $Row[$k] }
    $bkRows.Add([pscustomobject]$o)
    if ($env:GATE_ID) {
        Check ("{0} [{1}, {2}, {3}]: Back (and Forward) show the page as it was, at once" -f $env:GATE_ID, $bkTheme, $(if ($script:bkSize) { $script:bkSize } else { 'as opened' }), $Page) ([bool]$Row.ok) ("{0}{1}" -f $Row.detail, $(if ($script:bkSizeSaid) { "; window $($script:bkSizeSaid)" }))
    }
    else { Check ("back174 ({0}): {1}" -f $bkTag, $Page) ([bool]$Row.ok) $Row.detail }
    [IO.File]::WriteAllText((Join-Path $bkDir 'back174.json'), (ConvertTo-Json -InputObject @($bkRows.ToArray()) -Depth 4), [Text.UTF8Encoding]::new($false))
}
# the app's own requests (not images) since a time: their count and the first few paths
function Bk-Asked([double]$Since) {
    $r = @(Get-Requests $Since | Where-Object { (Get-Origin $_) -eq 'ui' -and $_.path -notmatch '/Images/' -and $_.path -notmatch '^/(Sessions|socket)' })
    [pscustomobject]@{ Count = $r.Count; Paths = @($r | ForEach-Object { ($_.path -replace '[0-9a-fA-F]{32}|[0-9a-fA-F-]{36}', '<id>') } | Select-Object -Unique -First 4) }
}

# Back, measured: on the page opened from $Left's page (it has settled), every answer held $bkDelay ms, Back; then how long until
# $Left is on screen, whether a loading state showed, the scroll ($V0 before) and the focus
function Bk-Measure([string]$Page, $Left, [double]$V0, [scriptblock]$Extra, [double]$Px0 = -1) {
    if ($script:bkLeave) { $V0 = $script:bkLeave[0]; $Px0 = $script:bkLeave[1]; $script:bkLeave = $null }
    Start-Sleep -Milliseconds 1800
    Bk-Latency $bkDelay
    $t0 = Get-Epoch
    Key 0xA6
    $loading = 0; $hit = $null; $ms = $null; $midShot = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $tries = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        # (the card through the keyboard's element, one cheap call; else the first element of its name on screen, every other
        # try; a loading state, which takes a look through the whole window, once the limit is past)
        # (1.7.5: tried reading only the keyboard's element within the limit: on Editorial's posters it never matched, and every
        # page read the limit; kept as it was)
        $hit = Bk-FocusedIs $Left
        if (-not $hit -and ((++$tries) % 2) -eq 0) { $hit = Bk-Find $Left }
        if (-not $hit -and ((Get-Epoch) - $t0) * 1000 -gt $bkLimit -and ($tries % 8) -eq 0) {
            $l = Bk-Loading; if ($l -gt $loading) { $loading = $l }
        }
        if ($hit) { $ms = [int](((Get-Epoch) - $t0) * 1000); break }
        # what the page shows while the server's answers are held (a loading state, or nothing yet)
        # (1.7.5: once the limit is past, not at 0.6 s: a shot of the whole window takes 0.1 to 0.7 s, which was counted as the page's)
        if (-not $midShot -and ((Get-Epoch) - $t0) * 1000 -gt $bkLimit) { $midShot = $true; Shot (Safe-Name "back174-$bkTag-$Page-waiting") }
        Start-Sleep -Milliseconds 40
    }
    # the place restore and the focus settle (each yields to the user: nothing is pressed meanwhile)
    Start-Sleep -Milliseconds 1500
    if (-not $loading) { $loading = Bk-Loading }
    # (1.7.5 gate fix: one look for the element with the keyboard found none on a busy PC, Classic's season at 1280 x 720, while
    # the shot taken just after showed the episode's focus ring: a few looks over a second; a keyboard really lost stays lost)
    $focus = Bk-Retry { Bk-Focus } 5 250
    $v = Bk-V
    $px = Bk-Px
    $asked = Bk-Asked $t0
    Bk-Latency 0
    Shot (Safe-Name "back174-$bkTag-$Page")
    # kept: within 3 % or 48 px (a nudge that brings the card's ring into view; a page's pictures can move the percent)
    $scrollKept = if ($V0 -lt 0 -and $v -lt 0) { $true } elseif ($V0 -lt 0 -or $v -lt 0) { $false } else { [Math]::Abs($v - $V0) -lt 3 -or ($Px0 -ge 0 -and $px -ge 0 -and [Math]::Abs($px - $Px0) -le 48) }
    $focusKept = [bool]($focus -and $focus.Name -eq $Left.Name)
    $fast = [bool]($ms -ne $null -and $ms -le $bkLimit)
    $row = @{ ms = $ms; left = $Left.Name; loading = [bool]$loading; scroll_before = [Math]::Round($V0, 1); scroll_after = [Math]::Round($v, 1); scroll_kept = $scrollKept
        focus_kept = $focusKept; requests = $asked.Count; asked = ($asked.Paths -join ' ')
        ok = ($fast -and -not $loading -and $scrollKept -and $focusKept)
        detail = ("{0}; loading state {1}; scroll {2:0.0} -> {3:0.0} % ({8} -> {9} px); focus: left {4}, back on {5}; {6} requests after Back{7}" -f
            $(if ($ms -ne $null) { "back in $ms ms (limit $bkLimit)" } else { 'not back within 15 s' }), $(if ($loading) { 'SHOWN' } else { 'none' }), $V0, $v,
            (Bk-Describe $Left), (Bk-Describe $focus), $asked.Count, $(if ($asked.Count) { " ($($asked.Paths -join ' '))" } else { '' }), $Px0, $px) }
    if ($Extra) { & $Extra $row $hit }
    Bk-Record $Page $row
}

function Bk-Case([string]$Name, [scriptblock]$Body) {
    if ($bkPages -notcontains $Name) { return }
    # (1.7.5, the gate: at each of its sizes, the window sized first; $bkOnce's cells once)
    foreach ($size in @(if ($bkOnce -contains $Name) { $bkSizes[0] } else { $bkSizes })) {
        if ($size -and $script:bkSize -ne $size) {
            try { $script:bkSizeSaid = Format-GateSize (Set-GateSize $size); $script:bkSize = $size; $script:bkTag = "$bkBaseTag-$size" }
            catch { Bk-Record $Name @{ ok = $false; detail = "the window could not be sized to ${size}: $($_.Exception.Message)" }; continue }
        }
        $script:bkLeave = $null
        Say "back174 ($bkTag): --- $Name"
        try { & $Body }
        catch {
            Say "back174 ($bkTag): $Name failed: $($_.Exception.Message) $($_.ScriptStackTrace)"
            Bk-Record $Name @{ ok = $false; detail = "the case could not run: $($_.Exception.Message)" }
        }
        try { Bk-Latency 0 } catch { }
    }
}

# ---- the pages ----------------------------------------------------------------------------------------------------------------
# a library by its top-bar entry, else its tile on the Libraries page, below the fold too (1.7.5, the gate at 1280 x 720: the
# "Collections & playlists" group is under the fold there, and Open-Library looks only at the tiles drawn)
function Bk-OpenLibrary([string]$Name) {
    try { Open-Library $Name; return }
    catch { if ("$($_.Exception.Message)" -notmatch '^no library') { throw } }
    foreach ($e in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($bkA::IsScrollPatternAvailableProperty, $true))))) {
        try { $sp = $e.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern); if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 100) } } catch { }
    }
    $tile = Wait-Until {
        $t = [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, $Name, 'Microsoft.UI.Xaml.Controls.ItemContainer')
        if (-not $t) { $t = Find-Ui -Name "$Name, " -Type 'ListItem' -Contains; if ($t -and -not $t.Current.Name.StartsWith("$Name, ")) { $t = $null } }
        $t
    } -Seconds 10 -PollMs 200
    if (-not $tile) { throw "no library '$Name' (the Libraries page scrolled to its end too)" }
    Say "library '$Name', below the Libraries page's fold ($(Invoke-Element $tile))"
    $script:page = "library:$Name"
}
function Bk-OpenCollection([string]$Library, [string]$Title) {
    Bk-OpenLibrary $Library
    Start-Sleep -Milliseconds 1500
    Open-Card $Title
    if (-not (Wait-Ui -Id 'OrderButton' -Seconds 20)) { throw "the Collection page of '$Title' did not open" }
    [void](Wait-Until { if ((Bk-Cards).Count) { $true } } -Seconds 15 -PollMs 200)
    Start-Sleep -Milliseconds 1500
}
# a Collection page: the card at $Pos opened (Enter) and Back
function Bk-CollectionBack([string]$Page, [int]$Pos, [double]$Scroll = -1) {
    $left = Bk-FocusCard $Pos -Scroll $Scroll
    if (-not $left) { throw 'no card on the Collection page' }
    $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Bk-Measure $Page $left $v0 -Px0 $px0
}
$bkLongSet = if ($env:FAKESERVER_LONG_TITLE) { 'Anthology of Twenty-Four' } else { 'Copper and Glass' }

# the film a card opens: its title ("<place>, <title>, <year>" in a set whose order means something)
function Bk-TitleOf([string]$Name) { $parts = @($Name -split ', '); if ($parts.Count -gt 1 -and $parts[0] -match '^\d+$') { $parts[1] } else { $parts[0] } }
# the film's page shown: its Watched toggle, named for it in every theme
function Bk-OnFilm([string]$Title) { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $Title as ", '') }

Bk-Case 'collection' {
    Bk-OpenCollection 'Collections' $bkLongSet
    Bk-CollectionBack 'collection' 2 -Scroll $(if ($env:FAKESERVER_LONG_TITLE) { 60 } else { -1 })
    # and Forward, to the film Back left: shown as it was too (its view model kept), nothing asked
    $film = Bk-TitleOf $bkRows[$bkRows.Count - 1].left
    Start-Sleep -Milliseconds 800
    Bk-Latency $bkDelay
    $t0 = Get-Epoch
    Key 0xA7   # BrowserForward
    $on = Wait-Until { Bk-OnFilm $film } -Seconds 15 -PollMs 40
    $ms = if ($on) { [int](((Get-Epoch) - $t0) * 1000) } else { $null }
    Start-Sleep -Milliseconds 1000
    $asked = Bk-Asked $t0
    Bk-Latency 0
    Shot (Safe-Name "back174-$bkTag-forward")
    Bk-Record 'forward' @{ ms = $ms; requests = $asked.Count; ok = [bool]($on -and $ms -le $bkLimit -and $asked.Count -eq 0)
        detail = ("Forward to '{0}': {1}; {2} requests after Forward{3}" -f $film, $(if ($ms -ne $null) { "its page in $ms ms (limit $bkLimit)" } else { 'its page not shown within 15 s' }), $asked.Count, $(if ($asked.Count) { " ($($asked.Paths -join ' '))" } else { '' })) }
}
Bk-Case 'playlist' {
    Bk-OpenCollection 'Playlists' 'Rainy Sunday'
    Bk-CollectionBack 'playlist' 3
}
Bk-Case 'nixie' {
    Open-Nav 'Collections'
    $tile = Wait-Until { Find-Ui -Name ', made by Nixie, franchise, ' -Contains -OffscreenToo } -Seconds 30 -PollMs 300
    if (-not $tile) { throw 'no Made by Nixie franchise on the Collections page' }
    Say "franchise '$($tile.Current.Name)' ($(Invoke-Element $tile))"
    if (-not (Wait-Ui -Id 'OrderButton' -Seconds 20)) { throw 'the franchise did not open' }
    Start-Sleep -Milliseconds 1500
    Bk-CollectionBack 'nixie' 2
}
Bk-Case 'library' {
    Open-Library $fixture.movie.library
    [void](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200)
    Start-Sleep -Milliseconds 1500
    $first = @(Bk-Cards @('BaseItemDtoCard', 'EditorialPoster', 'LibraryListRow') | Where-Object { -not $_.Cached.IsOffscreen }) | Select-Object -First 1
    if (-not $first) { throw 'no card in the library' }
    [void][OffscreenUi]::SetFocus($first); Start-Sleep -Milliseconds 600
    Key 0x22; Start-Sleep -Milliseconds 1100; Key 0x22; Start-Sleep -Milliseconds 1100; Key 0x27; Start-Sleep -Milliseconds 900   # PageDown x2, Right
    $left = Bk-Focus; $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Bk-Measure 'library' $left $v0 -Px0 $px0
}
# the big series' page (FAKESERVER_BIG_SERIES), else the showcase series'
function Bk-OpenSeries {
    $s = if ($bkBs) { $bkBs } else { $fixture.series }
    Open-Library $s.library
    Start-Sleep -Milliseconds 1200
    $card = Wait-Until { Find-MediaCard $s.name } -Seconds 30 -PollMs 200
    if (-not $card) { throw "no card for the series '$($s.name)'" }
    [void](Invoke-Element $card)
    $full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 4 -PollMs 150
    if ($full) { [void](Invoke-Element $full) }
    if (-not (Wait-Until { Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo } -Seconds 20 -PollMs 200)) { throw 'the series page did not open' }
    Start-Sleep -Milliseconds 2500
}
# an episode row ("S1 E<n> ..."): the keyboard on it, then its menu's Episode details (Enter would play it)
function Bk-EpisodeDetails([string]$Prefix) {
    $row = Wait-Until { Find-Ui -Name $Prefix -Contains -OffscreenToo -Type 'ListItem' } -Seconds 10 -PollMs 200
    if (-not $row) { $row = Wait-Until { Find-Ui -Name $Prefix -Contains -OffscreenToo -Type 'Button' } -Seconds 3 -PollMs 200 }
    if (-not $row) { throw "no episode row '$Prefix'" }
    Bk-Take $row
    $left = Bk-Focus; $v0 = Bk-V; $px0 = Bk-Px
    Key 0x5D; Start-Sleep -Milliseconds 1000   # the menu key
    $details = Wait-Ui -Name 'Episode details' -Type 'MenuItem' -Seconds 5
    if (-not $details) { Key 0x1B; throw "no Episode details in the menu of '$($left.Name)'" }
    [void](Invoke-Element $details)
    if (-not (Bk-WaitGone $left)) { throw "the episode '$($left.Name)' did not open" }
    [pscustomobject]@{ Left = $left; V0 = $v0; Px0 = $px0 }
}
Bk-Case 'season' {
    Bk-OpenSeries
    $link = Wait-Until { Find-Ui -Name 'Season 1 details' -Type 'Hyperlink' -OffscreenToo } -Seconds 10 -PollMs 200
    [void](Invoke-Element $link)
    if (-not (Wait-Until { [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Mark Season 1 of ', '') } -Seconds 20 -PollMs 200)) { throw 'the season page did not open' }
    Start-Sleep -Milliseconds 2500
    $o = Bk-EpisodeDetails 'S1 E9 '
    Bk-Measure 'season' $o.Left $o.V0 -Px0 $o.Px0
}
Bk-Case 'series' {
    Bk-OpenSeries
    $o = Bk-EpisodeDetails 'S1 E5 '
    Bk-Measure 'series' $o.Left $o.V0 -Px0 $o.Px0
}
# an item's page (the showcase film's): a More like this card down it opened, Back
Bk-Case 'item' {
    Open-Library $fixture.movie.library
    Start-Sleep -Milliseconds 1200
    Open-Card $fixture.movie.name
    if (-not (Wait-ItemPage $fixture.movie.name)) { throw 'the film''s page did not open' }
    [void](Wait-Until { if ((Bk-Cards).Count -ge 2) { $true } } -Seconds 15 -PollMs 200)
    Start-Sleep -Milliseconds 2000
    $left = Bk-FocusCard 2 -Scroll 100
    if (-not $left) { throw 'no More like this card on the film''s page' }
    $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Bk-Measure 'item' $left $v0 -Px0 $px0
}
# a person's page ("With <name>", from search's People: FAKESERVER_SEARCH=sunny's Sunny Holm): a card opened, Back
Bk-Case 'person' {
    SrSearch 'sunny'
    $people = SrChip 'People'
    if ($people) { [void](Invoke-Element $people); Start-Sleep -Milliseconds 1500 }
    $who = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($bkA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like 'Sunny Holm*' }) | Select-Object -First 1 } -Seconds 8 -PollMs 200
    if (-not $who) { throw 'no Sunny Holm in the People results (FAKESERVER_SEARCH=sunny)' }
    [void][OffscreenUi]::SetFocus($who); Start-Sleep -Milliseconds 400
    Key 0x0D
    if (-not (Wait-Ui -Name 'With Sunny Holm' -Type 'Text' -Seconds 15)) { throw 'the person''s page did not open' }
    [void](Wait-Until { if ((Bk-Cards).Count) { $true } } -Seconds 15 -PollMs 200)
    Start-Sleep -Milliseconds 1500
    $left = Bk-FocusCard 2
    if (-not $left) { throw 'no card on the person''s page' }
    $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Bk-Measure 'person' $left $v0 -Px0 $px0
}
Bk-Case 'search' {
    SrSearch 'the'
    $left = Bk-FocusCard 3 -Scroll 50
    if (-not $left) { throw 'no card in the search results' }
    $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the result '$($left.Name)' did not open" }
    Bk-Measure 'search' $left $v0 -Px0 $px0
}
Bk-Case 'collections' {
    Open-Nav 'Collections'
    $tiles = Wait-Until { $t = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($bkA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Cached.Name -like '*, collection, *' }); if ($t.Count -ge 3) { $t } } -Seconds 30 -PollMs 300
    if (-not $tiles) { throw 'no collection tiles on the Collections page' }
    Start-Sleep -Milliseconds 1200
    Bk-Take $tiles[2]
    $left = Bk-Focus; $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Wait-Ui -Id 'OrderButton' -Seconds 20)) { throw "the tile '$($left.Name)' did not open" }
    Bk-Measure 'collections' $left $v0 -Px0 $px0
}
Bk-Case 'home' {
    Open-Nav 'Home'
    Start-Sleep -Milliseconds 3000
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 50)
    Start-Sleep -Milliseconds 1200
    $cards = @(Bk-Cards | Where-Object { -not $_.Cached.IsOffscreen } | Sort-Object { $_.Cached.BoundingRectangle.Y } -Descending)
    if (-not $cards.Count) { throw 'no card on Home' }
    [void][OffscreenUi]::SetFocus($cards[0]); Start-Sleep -Milliseconds 900
    $left = Bk-Focus; $v0 = Bk-V; $px0 = Bk-Px
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Bk-Measure 'home' $left $v0 -Px0 $px0
}

# ---- a change made on the page Back leaves shows after Back ------------------------------------------------------------------
Bk-Case 'change' {
    Bk-OpenCollection 'Collections' 'Copper and Glass'
    # a film of the set not watched yet: its card given the keyboard
    $card = @(Bk-Cards | Where-Object { $_.Cached.Name -notmatch ', watched' }) | Select-Object -First 1
    if (-not $card) { throw 'every film of the set is watched already' }
    Bk-Take $card
    $left = Bk-Focus; $v0 = Bk-V; $px0 = Bk-Px
    # (a card in a set whose order means something is named "<place>, <title>, <year>")
    $parts = @($left.Name -split ', ')
    $title = if ($parts.Count -gt 1 -and $parts[0] -match '^\d+$') { $parts[1] } else { $parts[0] }
    $bkOf = { param($c) $n = $c.Current.Name; $n -like "$title, *" -or $n -like "*, $title, *" }
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    $mark = Wait-Until { Find-Ui -Name "Mark $title as watched" -Type 'Button' } -Seconds 15 -PollMs 200
    if (-not $mark) { throw "no 'Mark $title as watched' on the film's page" }
    Say "marked watched ($(Invoke-Element $mark))"
    [void](Wait-Until { Find-Ui -Name "Mark $title as unwatched" -Type 'Button' } -Seconds 10 -PollMs 200)
    # the card's name changes ("..., watched"): the one left from is found by its title, and must say watched
    Start-Sleep -Milliseconds 1800
    Bk-Latency $bkDelay
    $t0 = Get-Epoch
    Key 0xA6
    # the card on screen again (at once: the page as it was), then saying watched (at once, or after one round trip behind it)
    $shownMs = $null; $ms = $null; $watched = $null
    $until = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $until -and -not $watched) {
        $c = @(Bk-Cards | Where-Object { -not $_.Cached.IsOffscreen -and (& $bkOf $_) }) | Select-Object -First 1
        if ($c -and $shownMs -eq $null) { $shownMs = [int](((Get-Epoch) - $t0) * 1000) }
        if ($c -and $c.Current.Name -match ', watched') { $watched = $c; $ms = [int](((Get-Epoch) - $t0) * 1000) }
        Start-Sleep -Milliseconds 60
    }
    Start-Sleep -Milliseconds 1200
    $focus = Bk-Focus
    Bk-Latency 0
    Shot (Safe-Name "back174-$bkTag-change")
    $now = @(Bk-Cards | Where-Object { & $bkOf $_ }) | Select-Object -First 1
    Bk-Record 'change' @{ ms = $shownMs; watched_ms = $ms; ok = [bool]($watched -and $shownMs -ne $null -and $shownMs -le $bkLimit -and $ms -le ($bkLimit + $bkDelay)); focus_kept = [bool]($focus -and ($focus.Name -like "$title, *" -or $focus.Name -like "*, $title, *"))
        detail = ("the card of '{0}' after Back: {1}; on screen {2}, {3}; focus on {4}" -f $title, $(if ($now) { "'$($now.Current.Name)'" } else { 'not found' }),
            $(if ($shownMs -ne $null) { "in $shownMs ms (limit $bkLimit)" } else { 'never' }), $(if ($ms -ne $null) { "said watched in $ms ms (limit $($bkLimit + $bkDelay))" } else { 'never said watched within 15 s' }), (Bk-Describe $focus)) }
}

# ---- R still loads again --------------------------------------------------------------------------------------------------------
Bk-Case 'refresh' {
    Bk-OpenCollection 'Collections' 'Copper and Glass'
    $left = Bk-FocusCard 2
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Start-Sleep -Milliseconds 1800
    Key 0xA6
    [void](Wait-Until { Bk-Find $left } -Seconds 15 -PollMs 100)
    Start-Sleep -Milliseconds 1500
    $t0 = Get-Epoch
    Key 0x52   # R
    $asked = Wait-Until { $a = Bk-Asked $t0; if ($a.Count) { $a } } -Seconds 10 -PollMs 200
    $back = Wait-Until { Bk-Find $left } -Seconds 15 -PollMs 150
    Bk-Record 'refresh' @{ ok = [bool]($asked -and $back); requests = $(if ($asked) { $asked.Count } else { 0 })
        detail = ("R on the page Back showed: {0}; the card back: {1}" -f $(if ($asked) { "$($asked.Count) requests ($($asked.Paths -join ' '))" } else { 'nothing asked of the server' }), [bool]$back) }
}

# ---- Nixie 1.7.5 (the re-review's Blocker): Back and Forward between two pages of the same kind ------------------------------------
# WinUI keeps one page per type, the shown one included: with the libraries and the search results kept whole, Movies, the Shows
# tab, Back gave Movies' entry the Shows page (titled Movies, Shows' cards, its load stopped). Each move is checked by what the
# page shows: its own cards, never the other page's, its title (search: "Results for <query>").
function Bk-Holds([string]$Has, [string]$HasNot, [double]$Seconds = 10) {
    [bool](Wait-Until { if ((Find-MediaCard $Has) -and -not (Find-MediaCard $HasNot)) { $true } } -Seconds $Seconds -PollMs 250)
}
Bk-Case 'samelibrary' {
    $movies = $fixture.movie.library; $shows = $fixture.series.library
    Open-Library $movies
    if (-not (Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200)) { throw "no '$($fixture.movie.name)' in $movies" }
    Start-Sleep -Milliseconds 1200
    Open-Library $shows
    if (-not (Wait-Until { Find-MediaCard $fixture.series.name } -Seconds 30 -PollMs 200)) { throw "no '$($fixture.series.name)' in $shows" }
    Start-Sleep -Milliseconds 1200
    Key 0xA6
    $back = Bk-Holds $fixture.movie.name $fixture.series.name
    Start-Sleep -Milliseconds 800
    Key 0xA7   # BrowserForward
    $forward = Bk-Holds $fixture.series.name $fixture.movie.name
    Bk-Record 'samelibrary' @{ ok = ($back -and $forward)
        detail = ("{0}, the {1} tab, Back: {2}; Forward: {3}" -f $movies, $shows, $(if ($back) { "$movies's cards" } else { "NOT $movies's cards (or $shows's still there)" }), $(if ($forward) { "$shows's cards" } else { "NOT $shows's cards" })) }
}
Bk-Case 'samesearch' {
    # the results' sign: the kind chips with their counts, which differ for the two searches (else the groups' headings, else
    # the first cards' names: a search of one kind shows no chips); Back and Forward must show the title and the results of
    # the same search (a kept page shown with the other search's results would pass a title check)
    $first = 'sunny'; $second = 'the'
    function Bk-Results {
        $sign = (@(SrChips | ForEach-Object { $_.Current.Name }) | Sort-Object) -join ' | '
        if (-not $sign) { $sign = @(SrHeadings) -join ' | ' }
        if (-not $sign) { $sign = (@(Bk-Cards | Select-Object -First 3 | ForEach-Object { $_.Cached.Name }) | Sort-Object) -join ' | ' }
        $sign
    }
    function Bk-Shows([string]$query, [string]$sign) {
        [bool](Wait-Until { $t = Find-Ui -Name 'Results for' -Type 'Text' -Contains; if ($t -and $t.Current.Name -like "*$query*" -and (Bk-Results) -eq $sign) { $true } } -Seconds 10 -PollMs 200)
    }
    # (1.7.5 gate fix: each page's title and the keyboard before Back are said, so a failure tells a search that never opened
    # from a Back that did nothing; the verify run's Drive-in and Editorial cells at 1280 x 720 said neither)
    function Bk-Title { $t = Find-Ui -Name 'Results for' -Type 'Text' -Contains; if ($t) { "'$($t.Current.Name)'" } else { 'no results title' } }
    # the app's popups open (windowed: the search box's list, a menu): an open one holds the window's keys (WindowShortcuts)
    if (-not ('BkWin' -as [type])) {
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class BkWin {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static IntPtr[] Find(int pid, string cls) {
    var found = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == (uint)pid && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); if (sb.ToString() == cls) found.Add(h); } return true; }, IntPtr.Zero);
    return found.ToArray();
  }
}
'@
    }
    # how many, and what each holds (its first named elements' type and name: a tooltip, the search box's list...)
    function Bk-Popups {
        try {
            $hs = @([BkWin]::Find([int]$script:ui.Id, 'Microsoft.UI.Content.PopupWindowSiteBridge'))
            $what = @(foreach ($h in $hs) {
                    try {
                        $e = [System.Windows.Automation.AutomationElement]::FromHandle($h)
                        $kids = @($e.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Select-Object -First 4 |
                            ForEach-Object { "$($_.Current.ControlType.ProgrammaticName -replace '^ControlType\.', '') $($_.Current.ClassName) '$($_.Current.Name)'" })
                        "[$($kids -join '; ')]"
                    } catch { '[unreadable]' }
                })
            "$($hs.Count)$(if ($what) { ' ' + ($what -join ' ') })"
        } catch { '-1' }
    }
    SrSearch $first
    $t1 = Wait-Until { $t = Find-Ui -Name 'Results for' -Type 'Text' -Contains; if ($t -and $t.Current.Name -like "*$first*") { $t } } -Seconds 15 -PollMs 200
    $t1Said = Bk-Title
    $s1 = Bk-Results
    SrSearch $second   # from the results page: a second search
    $t2 = Wait-Until { $t = Find-Ui -Name 'Results for' -Type 'Text' -Contains; if ($t -and $t.Current.Name -like "*$second*") { $t } } -Seconds 15 -PollMs 200
    $s2 = Bk-Results
    $keyAt = "$(Bk-Describe (Bk-Focus)), $(Bk-Popups) popup(s) open"
    Key 0xA6
    $back = Bk-Shows $first $s1
    $backSeen = Bk-Results
    $backTitle = Bk-Title
    Start-Sleep -Milliseconds 1500
    Key 0xA7
    $forward = Bk-Shows $second $s2
    $forwardSeen = Bk-Results
    Bk-Record 'samesearch' @{ ok = ($t1 -and $t2 -and $s1 -and $s2 -and $s1 -ne $s2 -and $back -and $forward)
        detail = ("'{0}' ({1}; title {6}), then '{2}' from its results ({3}), Back (the keyboard on {7}): {4}; Forward: {5}" -f $first, $s1, $second, $s2,
            $(if ($back) { "Results for '$first' with its results" } else { "NOT the first search (title $backTitle; $backSeen)" }),
            $(if ($forward) { "Results for '$second' with its results" } else { "NOT the second search ($forwardSeen)" }), $t1Said, $keyAt) }
}

# ---- sign-out lets the kept pages go ---------------------------------------------------------------------------------------------
# the log's lines that say a sign-out let the kept pages go: "a sign-out" (HomeCache.Forgetting), or, since 1.7.5's Switch user fix
# (bea16c2f), "the shell went" (NavigationService.Release, which the sign-out reaches first, so Forgetting finds none kept);
# never Back's own reach ("Back's reach") or a theme switch
function Bk-SignOutLetGo([string[]]$Lines) { @($Lines | Where-Object { $_ -match '\[back\] \d+ kept page\(s\) let go: (a sign-out|a sign-in|the shell went)\s*$' }) }
Bk-Case 'signout' {
    Bk-OpenCollection 'Collections' 'Copper and Glass'
    $left = Bk-FocusCard 2
    Bk-OpenFocused
    if (-not (Bk-WaitGone $left)) { throw "the card '$($left.Name)' did not open" }
    Start-Sleep -Milliseconds 1500
    $logDir = Join-Path $DataDir 'Logs'
    $acct = Wait-Until { Find-Ui -Name 'Account, ' -Type 'Button' -Contains } -Seconds 10 -PollMs 150
    if (-not $acct) { throw 'no Account button' }
    [void](Invoke-Element $acct)
    $bkSignOut = Wait-Ui -Name 'Sign out' -Type 'Button' -Seconds 8
    if (-not $bkSignOut) { Key 0x1B; throw 'no Sign out in the Account menu' }
    [void](Invoke-Element $bkSignOut)
    [void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 30)
    Start-Sleep -Milliseconds 1500
    $said = @(Get-ChildItem $logDir -Filter '*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2 |
        ForEach-Object { Select-String -LiteralPath $_.FullName -Pattern '\[back\] .*let go' -ErrorAction SilentlyContinue } | ForEach-Object { $_.Line })
    $dropped = @(Bk-SignOutLetGo $said)
    Step-SignIn $false
    Start-Sleep -Milliseconds 2500
    $canBack = [bool](Find-Ui -Name 'Back' -Type 'Button' | Where-Object { $_.Current.IsEnabled })
    Bk-Record 'signout' @{ ok = [bool]($dropped.Count -gt 0); detail = ("the log on sign-out: {0}; signed in again, Back {1}" -f $(if ($dropped.Count) { $dropped[-1].Trim() } else { 'no "[back] ... let go" line' }), $(if ($canBack) { 'is on' } else { 'is off' })) }
}

Say ("back174 ({0}): {1}/{2} rows passed -> {3}" -f $bkTag, @($bkRows | Where-Object { $_.ok }).Count, $bkRows.Count, (Join-Path $bkDir 'back174.json'))
