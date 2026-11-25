<#
  The basics gate, class 3 (every menu; Nixie 1.7.5, g2). A -Then script of run-offscreen.ps1 (manifest\g2.psd1, id
  "menus"), one theme per run (NIXIE_LAYOUT), after the driver signed in as the fake server's administrator ("harness").
  Each menu found, through each way in it has (the menu key on a card or row; its More options button by a click, Enter,
  Space and gamepad A; a toolbar button by a click and Enter), is checked with Test-G2Menu (g2-common.ps1): it opens with
  only its own items, each once; nothing else happens (the page stays, nothing is sent or opened); Escape closes it with
  the keyboard back where it was. Then what each item does, once per theme:
    - Home's cards, each card kind the theme shows (the shared card, Cinematic's stills, Drive-in's showtimes, Editorial's
      posters and stills): Mark as watched and back, Add to favorites and back (one request each, the words flipped the
      next time), Media info by the keyboard (Down, Enter: its dialog, Escape: the page, the menu not open again), a
      favourite by Space (its key-up reaching nothing else), Play (the player, then Back), the page entry (Episode
      details, Full details: the page, then Back); a live resize with a menu open;
    - the movie, series and season pages' More options, the season's episode rows;
    - the library's Sort and View: a choice said on its button, then back;
    - the Account menu: open, Escape, Settings;
    - Classic only (they are the same dialogs in every theme): the administrator's items open their dialogs and Escape sends
      nothing; Delete asks, and Escape and Cancel send nothing;
    - then Switch user to the second test user ("harness-two", not an administrator): a card's menu and the movie page's
      have no administrator's item, and a series page shows no More options with nothing in it.
  1.7.3: the same menus; it fails where a 1.7.4 fix lived in a menu (the card's More options in the Continue watching row
  and the row's tap; see the manifest's Fails173). Every name here starts with g2/G2 (dot-sourced by drive.ps1).
#>
. (Join-Path $PSScriptRoot 'g2-common.ps1')
$g2Id = 'menus'
$g2Theme = G2-Theme
$g2Sizes = @(G2-Sizes)
$g2Ell = [string][char]0x2026
$g2AdminTexts = @("Edit metadata$g2Ell", "Edit images$g2Ell", 'Edit segments', "Identify$g2Ell", "Refresh metadata$g2Ell", "Delete movie$g2Ell", "Delete series$g2Ell", "Delete season$g2Ell")
$g2ItemTexts = @('Play', 'Mark as watched', 'Mark as unwatched', 'Add to favorites', 'Remove from favorites', 'Reset watch progress',
    'Remove from Continue Watching', 'Media info', "Edit subtitles$g2Ell", 'Episode details', 'Full details') + $g2AdminTexts
$g2CardClasses = @('BaseItemDtoCard', 'CinematicStill', 'Showtime', 'EditorialPoster', 'EditorialStill', 'EpisodeRow')
$g2Admin = $true
Say "menus: theme $g2Theme, sizes $($g2Sizes -join ', ')"

# --- what is on the page -----------------------------------------------------------------------------------------------
# the first card of each kind in view (by class), in the page's order
function G2-CardKinds {
    $found = [ordered]@{}
    foreach ($cls in $g2CardClasses) {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, $cls)
        $hit = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object {
                try { $r = $_.Cached.BoundingRectangle; -not $_.Cached.IsOffscreen -and -not $r.IsEmpty -and $r.Width -gt 20 -and $r.Height -gt 20 } catch { $false } })[0]
        if ($hit) { $found[$cls] = $hit }
    }
    # the entry buttons with a menu of their own: Editorial's Up next lines, Drive-in's Coming up sign
    $btn = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($host_ in @(@('EditorialUpNextLine', [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'UpNextList'), @('ComingUpEntry', [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'ComingUpSign'))) {
        try {
            $box = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($host_[1], $host_[2]))))[0]
            if (-not $box) { continue }
            $hit = @([OffscreenUi]::Under($box, $btn) | Where-Object {
                    try { $n = $_.Cached.Name; $r = $_.Cached.BoundingRectangle; $n -and $n -notmatch '^(All|See all)' -and -not $_.Cached.IsOffscreen -and $r.Width -gt 20 } catch { $false } })[0]
            if ($hit) { $found[$host_[0]] = $hit }
        } catch {}
    }
    $found
}
# a card's own More options button (shown while the card has the keyboard or the pointer)
function G2-CardMore($Card) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    @([OffscreenUi]::Under($Card, $cond) | Where-Object { try { $_.Cached.Name -like 'More options*' -and -not $_.Cached.IsOffscreen } catch { $false } })[0]
}
function G2-Name($Element) { try { $Element.Current.Name } catch { '' } }
# a card of a kind by its name, on screen
# a card of a kind by its title (its name up to the first comma: the rest says watched, a year, progress, and changes)
function G2-CardNamed([string]$Kind, [string]$Name, [switch]$OffscreenToo) {
    if (-not $Kind -or -not $Name) { return $null }
    $title = ($Name -split ', ')[0]
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, $Kind)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object {
            try { $n = $_.Cached.Name; ($n -eq $title -or $n.StartsWith("$title, ")) -and ($OffscreenToo -or -not $_.Cached.IsOffscreen) } catch { $false } })[0]
}
function G2-Short([string]$Name) { if ($Name.Length -gt 40) { $Name.Substring(0, 40) + '...' } else { $Name } }

# an item menu's words agree with each other and with who is signed in
function Test-G2ItemMenuWords([string]$Case, [string[]]$Names, [bool]$NoPlay) {
    $bad = @()
    if ($Names -contains 'Mark as watched' -and $Names -contains 'Mark as unwatched') { $bad += 'both watched words' }
    if ($Names -contains 'Add to favorites' -and $Names -contains 'Remove from favorites') { $bad += 'both favourite words' }
    if ($Names -contains 'Reset watch progress' -and $Names -contains 'Remove from Continue Watching') { $bad += 'Reset and Remove from Continue Watching' }
    if ($NoPlay -and $Names -contains 'Play') { $bad += 'Play (this one has its own)' }
    $adminSeen = @($Names | Where-Object { $_ -in $g2AdminTexts })
    if (-not $g2Admin -and $adminSeen) { $bad += "an administrator's items for a user who isn't one: $($adminSeen -join ', ')" }
    Check "${g2Id} [$Case]: its words fit the item and the user$(if (-not $g2Admin) { ' (not an administrator)' })" ($bad.Count -eq 0) $(if ($bad) { $bad -join '; ' } else { $Names -join ' | ' })
}

# every way into one card's menus: the menu key and gamepad Menu on the card (its context menu), and its More options by a
# click. A card is one keyboard stop (Seaglass 1.1.3; an episode row too, 1.2.0): its More options is for the pointer, so
# the keys go to it only where it is a keyboard stop of its own.
function Test-G2CardMenus([string]$Page, [string]$Kind, $Card) {
    $title = G2-Short (G2-Name $Card)
    $noPlay = $Kind -in @('EpisodeRow', 'Showtime')
    $names = $null
    foreach ($way in @(@('the menu key', $g2Vk.Apps), @('gamepad Menu', $g2Vk.GamepadMenu))) {
        $Card = (G2-CardKinds)[$Kind]
        if (-not $Card) { G2-RecoverKind $Kind; $Card = (G2-CardKinds)[$Kind]; if (-not $Card) { return $names } }
        [void][OffscreenUi]::SetFocus($Card); Start-Sleep -Milliseconds 500
        $vk = $way[1]
        $n = Test-G2Menu -Id $g2Id -Name "$Page, $Kind '$title', $($way[0])" -Opener $Card -Open { Key $vk } -Allowed $g2ItemTexts
        if ($n -and -not $names) { $names = $n; Test-G2ItemMenuWords "$Page, $Kind" $n $noPlay }
        G2-RecoverKind $Kind
    }
    $Card = (G2-CardKinds)[$Kind]
    if (-not $Card) { return $names }
    [void][OffscreenUi]::SetFocus($Card); Start-Sleep -Milliseconds 500
    $more = G2-CardMore $Card
    if (-not $more) { Say "menus: $Page, $Kind '$title' shows no More options button (focused)"; return $names }
    [void](Test-G2Menu -Id $g2Id -Name "$Page, $Kind '$title', a click on More options" -Opener $more -Open { G2-Click $more })
    G2-RecoverKind $Kind
    $stop = $false; try { $stop = [bool]$more.Current.IsKeyboardFocusable } catch {}
    if (-not $stop) { Say "menus: $Page, $Kind's More options is not a keyboard stop (the card is; its menu key opens the same menu)"; return $names }
    foreach ($way in @(@('Enter on More options', $g2Vk.Enter), @('Space on More options', $g2Vk.Space), @('gamepad A on More options', $g2Vk.GamepadA))) {
        $Card = (G2-CardKinds)[$Kind]; if (-not $Card) { break }
        $more = G2-CardMore $Card; if (-not $more) { break }
        [void][OffscreenUi]::SetFocus($more); Start-Sleep -Milliseconds 300
        $vk = $way[1]
        [void](Test-G2Menu -Id $g2Id -Name "$Page, $Kind '$title', $($way[0])" -Opener $more -Open { Key $vk })
        G2-RecoverKind $Kind
    }
    $names
}
# a press that went through to the card opened its page: Back to where the card is again
function G2-RecoverKind([string]$Kind) {
    G2-CloseFlyouts
    if ((G2-CardKinds)[$Kind]) { return }
    Say "menus: the $Kind card is gone (a press went through?): Back"
    [void](G2-Back { if ((G2-CardKinds)[$Kind]) { $true } })
}

# one item of a card's menu (opened by the menu key), invoked: returns the epoch it was invoked at, $null when not there
function G2-InvokeCardItem($Card, [string[]]$Texts) {
    G2-CloseFlyouts
    [void][OffscreenUi]::SetFocus($Card); Start-Sleep -Milliseconds 400
    Key $g2Vk.Apps
    $item = Wait-Until { foreach ($t in $Texts) { $i = G2-PopupItem $t; if ($i) { return $i } } } -Seconds 4 -PollMs 100
    if (-not $item) { G2-CloseFlyouts; return $null }
    $at = Get-Epoch
    $text = $item.Current.Name
    [void](Invoke-Element $item)
    [pscustomobject]@{ At = $at; Text = $text }
}
# a toggle's round trip: the item (whichever way it reads now), one request of its route, the words flipped next time;
# then back as it was
function Test-G2Toggle([string]$Page, [string]$Kind, [scriptblock]$Find, [string[]]$Pair, [string]$Route) {
    $Card = & $Find
    $mark = G2-PageMark $Card
    $first = G2-InvokeCardItem $Card $Pair
    if (-not $first) { Check "${g2Id} [$Page, $Kind, $($Pair -join ' / ')]: the menu has it" $false; return }
    $sent = Wait-Until { $r = @(Get-Requests $first.At | Where-Object { $_.route -eq $Route -or ($_.route -eq 'user-data' -and $_.method -eq 'POST') }); if ($r.Count) { , $r } } -Seconds 6 -PollMs 150
    Start-Sleep -Milliseconds 800
    $sent = @(Get-Requests $first.At | Where-Object { $_.route -eq $Route -or ($_.route -eq 'user-data' -and $_.method -eq 'POST') })
    # the page stays: Back as it was and the card still there (found again: its name says watched now)
    $now = G2-PageMark $null
    $again = & $Find
    $stray = @(Get-Requests $first.At | Where-Object { $_.route -in @('item', 'playbackinfo') })
    Check "${g2Id} [$Page, $Kind, '$($first.Text)']: one request, the menu closed, the page stays" ($sent.Count -eq 1 -and -not (G2-MenuOpen) -and $now.Back -eq $mark.Back -and $again -and $stray.Count -eq 0) `
        ("{0} request(s) {1}; menu open: {2}; Back enabled {3} -> {4}; the card: {5}{6}" -f $sent.Count, (($sent | ForEach-Object { "$($_.method) $($_.route)" }) -join ', '), (G2-MenuOpen), $mark.Back, $now.Back, $(if ($again) { "'$(G2-Name $again)'" } else { 'gone' }), $(if ($stray) { '; opened: ' + (($stray | ForEach-Object { $_.path }) -join ', ') }))
    $other = @($Pair | Where-Object { $_ -ne $first.Text })[0]
    $second = G2-InvokeCardItem (& $Find) @($other)
    Check "${g2Id} [$Page, $Kind, '$($first.Text)']: the next time the menu says '$other'" ([bool]$second) $(if ($second) { 'and it undid it' } else { "menu: $((G2-MenuNames) -join ' | ')" })
    Start-Sleep -Milliseconds 800
    G2-CloseFlyouts
}

# a dialog by its title: opened by $Open, then Escape: closed, nothing sent to the server's administration (admin.jsonl),
# the page as it was
function Test-G2Dialog([string]$Case, [string]$Title, [scriptblock]$Open, $Opener) {
    $adminLog = Join-Path $Out 'server\admin.jsonl'
    $before = if (Test-Path -LiteralPath $adminLog) { @(Get-Content -LiteralPath $adminLog).Count } else { 0 }
    $mark = G2-PageMark $Opener
    $at = Get-Epoch
    & $Open
    $shown = Wait-Until { if ((Find-Ui -Name $Title -Type 'Text') -and (Find-Ui -Id 'CloseButton' -Type 'Button')) { $true } } -Seconds 10 -PollMs 150
    Check "${g2Id} [$Case]: '$Title' opens" ([bool]$shown)
    Key $g2Vk.Escape
    $gone = Wait-Until { -not (Find-Ui -Id 'CloseButton' -Type 'Button') } -Seconds 5 -PollMs 150
    Start-Sleep -Milliseconds 600
    $after = if (Test-Path -LiteralPath $adminLog) { @(Get-Content -LiteralPath $adminLog).Count } else { 0 }
    $writes = @(Get-Requests $at | Where-Object { $_.method -in @('POST', 'DELETE') -and $_.route -notin @('capabilities', 'sessions') })
    $same = G2-SamePage $mark $Opener
    Check "${g2Id} [$Case]: Escape closes it, nothing sent, the page stays" ($gone -and $after -eq $before -and $writes.Count -eq 0 -and $same.Ok -and -not (G2-MenuOpen)) "closed: $([bool]$gone); administration requests: $($after - $before); writes: $($writes.Count); $($same.Detail)"
    if (-not $gone) { $c = Find-Ui -Id 'CloseButton' -Type 'Button'; if ($c) { [void](Invoke-Element $c) } }
}

# Back (the title bar's, or the Back key), then wait for an element to be there again
function G2-Back([scriptblock]$Until) {
    Key 0xA6   # VK_BROWSER_BACK, as Alt+Left and mouse button 4
    $ok = Wait-Until $Until -Seconds 15 -PollMs 200
    if (-not $ok) { $b = Find-Ui -Id 'BackButton' -Type 'Button'; if ($b) { [void](Invoke-Element $b); $ok = Wait-Until $Until -Seconds 10 -PollMs 200 } }
    $ok
}

function Test-G2PageMore([string]$Page, [string]$Item, [string[]]$Forbidden, [switch]$HasEntries) {
    # the page's own: named for its item ("More options for Season 1 of <series>"), the highest on the page
    $more = Wait-Until {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
        @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { try { ($_.Cached.Name -eq "More options for $Item" -or $_.Cached.Name -like "More options for $Item of *" -or $_.Cached.Name -eq 'More options') -and -not $_.Cached.IsOffscreen } catch { $false } } |
            Sort-Object { $_.Cached.BoundingRectangle.Y })[0] } -Seconds 8 -PollMs 200
    # 1.7.5 (the review's Minor): missing passes only where the page has nothing in it for this user; the film page has
    # Media info for everyone (-HasEntries), so there a missing More options fails for someone who isn't an administrator too
    if (-not $more) { $want = $g2Admin -or $HasEntries; Check "${g2Id} [$Page]: More options shows" (-not $want) $(if ($want) { 'none' } else { 'none: nothing in it for this user' }); return }
    foreach ($way in @(@('a click', { G2-Click (Find-Ui -Name (G2-Name $more) -Type 'Button') }, $true), @('Enter', { Key $g2Vk.Enter }, $false), @('Space', { Key $g2Vk.Space }, $false), @('gamepad A', { Key $g2Vk.GamepadA }, $false))) {
        $more = Find-Ui -Name (G2-Name $more) -Type 'Button'
        if (-not $way[2]) { [void][OffscreenUi]::SetFocus($more); Start-Sleep -Milliseconds 300 }
        $names = Test-G2Menu -Id $g2Id -Name "$Page, $g2Size, More options, $($way[0])" -Opener $more -Open $way[1] -Allowed $g2ItemTexts -Forbidden $Forbidden
        if ($names -and $way[0] -eq 'a click') { Test-G2ItemMenuWords "$Page, More options" $names $true }
    }
    $more
}

# a phase that throws fails its own check and lets the next one run
function G2-PhaseFailed([string]$Phase, $Err) {
    Check "${g2Id} [$Phase]: ran to its end" $false ("{0} @ line {1}" -f $Err.Exception.Message, $Err.InvocationInfo.ScriptLineNumber)
    try { G2-CloseFlyouts } catch {}
}

# ========================================================================================================================
try {
# 1. Home's cards
$g2Size = G2-Size $g2Sizes[0]
[void](Open-Nav 'Home'); $script:page = 'home'
[void](Wait-Until { (G2-CardKinds).Count } -Seconds 30 -PollMs 300)
Start-Sleep -Seconds 2
$g2Kinds = G2-CardKinds
Check "${g2Id} [Home, $g2Size]: cards with a menu are shown" ($g2Kinds.Count -gt 0) ($g2Kinds.Keys -join ', ')
foreach ($g2K in @($g2Kinds.Keys)) {
    $g2Card = (G2-CardKinds)[$g2K]
    if ($g2Card) { [void](Test-G2CardMenus "Home, $g2Size" $g2K $g2Card) }
}

# Editorial's All genres (a radio menu): a click and Enter, Escape; a choice closes it and stays on Home
if ($g2Theme -eq 'Editorial') {
    # a link-styled button (UI Automation: a Hyperlink), at the genre strip's end below the fold
    $g2Gen = Find-Ui -Name 'All genres' -Type 'Hyperlink' -OffscreenToo
    if ($g2Gen) {
        [void][OffscreenUi]::SetFocus($g2Gen); Start-Sleep -Milliseconds 600
        [void](Test-G2Menu -Id $g2Id -Name "Home, All genres, Enter" -Opener (Find-Ui -Name 'All genres' -Type 'Hyperlink') -Open { Key $g2Vk.Enter })
        [void](Test-G2Menu -Id $g2Id -Name "Home, All genres, a click" -Opener (Find-Ui -Name 'All genres' -Type 'Hyperlink') -Open { G2-Click (Find-Ui -Name 'All genres' -Type 'Hyperlink') })
    }
    else { Check "${g2Id} [Home, All genres]: the button is there (Editorial)" $false }
}

# what the items do, on one card of Home's first card kind: one not in Continue watching (marking that one watched takes
# it out of the row), found again by its name each time
$g2Kinds = G2-CardKinds
$g2K = @($g2Kinds.Keys | Where-Object { $_ -in $g2CardClasses })[0]
$g2EffName = $null
if ($g2K) {
    # in any row, in view or not (a small window shows only Continue watching): focusing it brings it into view
    $g2All = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, $g2K))) |
            Where-Object { try { $_.Cached.Name } catch { $false } })
    # a film of a New in row: not Continue watching's (marking it watched takes it out) nor Next up's (it moves to the next
    # episode)
    $g2EffName = @($g2All | Where-Object { $_.Cached.Name -notmatch 'watched|Resume|remaining|season \d+|episode \d+' } | ForEach-Object { $_.Cached.Name })[0]
    if (-not $g2EffName -and $g2All) { $g2EffName = $g2All[0].Cached.Name }
    Say "menus: what the items do, on $g2K '$g2EffName'"
}
function G2-EffCard {
    $c = G2-CardNamed $g2K $g2EffName -OffscreenToo
    if ($c) { [void][OffscreenUi]::SetFocus($c); Start-Sleep -Milliseconds 600; $c = G2-CardNamed $g2K $g2EffName }
    $c
}
$g2Card = if ($g2EffName) { G2-EffCard } else { $null }
if ($g2Card) {
    Test-G2Toggle 'Home' $g2K { G2-EffCard } @('Mark as watched', 'Mark as unwatched') 'played'
    Test-G2Toggle 'Home' $g2K { G2-EffCard } @('Add to favorites', 'Remove from favorites') 'favorite'
    $g2Card = G2-EffCard

    # the keyboard inside the menu: Down to Media info, Enter (its dialog; Escape: the page, the menu not open again)
    G2-CloseFlyouts
    [void][OffscreenUi]::SetFocus($g2Card); Start-Sleep -Milliseconds 400
    $g2Mark = G2-PageMark $g2Card
    Key $g2Vk.Apps
    $g2Names = @(Wait-Until { $n = @(G2-MenuNames); if ($n.Count) { , $n } } -Seconds 4)
    $g2Index = [array]::IndexOf($g2Names, 'Media info')
    if ($g2Index -ge 0) {
        Start-Sleep -Milliseconds 300
        $g2F = Focused-Element
        $g2Start = if ($g2F -and $g2F.name -in $g2Names) { [array]::IndexOf($g2Names, $g2F.name) } else { -1 }
        for ($g2I = $g2Start; $g2I -lt $g2Index; $g2I++) { G2-Key $g2Vk.Down; Start-Sleep -Milliseconds 150 }
        $g2F = Focused-Element
        Check "${g2Id} [Home, $g2K, the keyboard]: Down reaches Media info" ($g2F -and $g2F.name -eq 'Media info') "focus on $(G2-Focused)"
        G2-Key $g2Vk.Enter
        $g2Shown = Wait-Until { if ((Find-Ui -Name 'Media info' -Type 'Text') -and (Find-Ui -Id 'CloseButton' -Type 'Button')) { $true } } -Seconds 8
        Check "${g2Id} [Home, $g2K, the keyboard]: Enter on Media info opens it, the menu closed" ($g2Shown -and -not @(G2-MenuItems).Count)
        Key $g2Vk.Escape
        [void](Wait-Until { -not (Find-Ui -Id 'CloseButton' -Type 'Button') } -Seconds 5)
        Start-Sleep -Milliseconds 800
        $g2Same = G2-SamePage $g2Mark $g2Card
        Check "${g2Id} [Home, $g2K, the keyboard]: Escape closes Media info; the page stays and the menu doesn't open again" ($g2Same.Ok -and -not (G2-MenuOpen)) "$($g2Same.Detail); menu open: $(G2-MenuOpen); focus on $(G2-Focused)"
    }
    else { Say "menus: Home, ${g2K}: no Media info in its menu (not a video), the keyboard part skipped: $($g2Names -join ' | ')"; G2-CloseFlyouts }

    # Space on a menu item: its key-up reaches nothing else (no page, no menu again)
    $g2Card = G2-EffCard
    G2-CloseFlyouts
    [void][OffscreenUi]::SetFocus($g2Card); Start-Sleep -Milliseconds 400
    $g2Mark = G2-PageMark $g2Card
    Key $g2Vk.Apps
    $g2Names = @(Wait-Until { $n = @(G2-MenuNames); if ($n.Count) { , $n } } -Seconds 4)
    $g2Fav = @($g2Names | Where-Object { $_ -in @('Add to favorites', 'Remove from favorites') })[0]
    if ($g2Fav) {
        Start-Sleep -Milliseconds 300
        $g2F = Focused-Element
        $g2Start = if ($g2F -and $g2F.name -in $g2Names) { [array]::IndexOf($g2Names, $g2F.name) } else { -1 }
        for ($g2I = $g2Start; $g2I -lt [array]::IndexOf($g2Names, $g2Fav); $g2I++) { G2-Key $g2Vk.Down; Start-Sleep -Milliseconds 150 }
        $g2At = Get-Epoch
        G2-Key $g2Vk.Space
        Start-Sleep -Milliseconds 1500
        $g2Sent = @(Get-Requests $g2At | Where-Object { $_.route -eq 'favorite' -or ($_.route -eq 'user-data' -and $_.method -eq 'POST') })
        $g2Stray = @(Get-Requests $g2At | Where-Object { $_.route -in @('item', 'playbackinfo') })
        $g2Same = G2-SamePage $g2Mark $g2Card
        Check "${g2Id} [Home, $g2K, Space on '$g2Fav']: done once; the page stays, the menu doesn't open again" ($g2Sent.Count -eq 1 -and $g2Stray.Count -eq 0 -and $g2Same.Ok -and -not (G2-MenuOpen)) "$($g2Sent.Count) request(s); opened: $($g2Stray.Count); $($g2Same.Detail); menu open: $(G2-MenuOpen)"
        # back as it was
        $g2Back = G2-InvokeCardItem (G2-EffCard) @('Add to favorites', 'Remove from favorites')
        Start-Sleep -Milliseconds 800
    }
    G2-CloseFlyouts

    # a live resize with a card's menu open: the page stays, nothing opens
    $g2Card = G2-EffCard
    [void][OffscreenUi]::SetFocus($g2Card); Start-Sleep -Milliseconds 400
    Key $g2Vk.Apps
    [void](Wait-Until { @(G2-MenuItems).Count } -Seconds 4)
    $g2At = Get-Epoch
    G2-LiveResize
    $g2Stray = @(G2-StrayRequests $g2At $null)
    Check "${g2Id} [Home, $g2K, a live resize with its menu open]: nothing opens or is sent, Home stays" ($g2Stray.Count -eq 0 -and (G2-CardKinds)[$g2K]) "sent: $($g2Stray -join ', '); menu open after: $(G2-MenuOpen)"
    G2-CloseFlyouts

    # the page entry (an episode's Episode details, Editorial's Full details): the page, then Back to the same card
    foreach ($g2K2 in @((G2-CardKinds).Keys)) {
        $g2C = (G2-CardKinds)[$g2K2]
        $g2Title = G2-Name $g2C
        $g2Entry = G2-InvokeCardItem $g2C @('Episode details', 'Full details')
        if (-not $g2Entry) { continue }
        $g2Opened = Wait-Until { @(Get-Requests $g2Entry.At | Where-Object { $_.route -eq 'item' }).Count } -Seconds 10 -PollMs 200
        Start-Sleep -Milliseconds 1500
        Check "${g2Id} [Home, $g2K2, '$($g2Entry.Text)']: the item's page opens" ([bool]$g2Opened -and -not (G2-MenuOpen))
        $g2Home = G2-Back { if ((G2-CardKinds)[$g2K2]) { $true } }
        Check "${g2Id} [Home, $g2K2, '$($g2Entry.Text)']: Back returns to Home's card" ([bool]$g2Home)
        break
    }

    # Play: the player, then Back
    $g2Card = G2-EffCard
    $g2Play = G2-InvokeCardItem $g2Card @('Play')
    if ($g2Play) {
        $g2Playing = Wait-Until { @(Get-Requests $g2Play.At | Where-Object { $_.route -eq 'playbackinfo' }).Count } -Seconds 30 -PollMs 250
        $g2Player = Wait-Until { (Find-Ui -Id 'PlayPauseButton') -or (Get-VideoWindow) } -Seconds 30 -PollMs 250
        Check "${g2Id} [Home, $g2K, 'Play']: it plays (the server asked for playback, the player shows)" ([bool]$g2Playing -and [bool]$g2Player)
        # the player's menus (Nixie's controls), paused so the bars stay up: each by a click and Enter, Escape closes it,
        # and the press reached nothing else (still paused: a click through to the video would play)
        $g2Pp = Wait-Ui -Id 'PlayPauseButton' -Seconds 5 -PollMs 50
        # (1.7.5 gate fix: without Nixie's controls, a build with no libmpv beside it, every player menu was left out and the
        # check passed; a check never passes by being skipped)
        Check "${g2Id} [player]: Nixie's controls show (Play/Pause), so the player's menus are tried" ([bool]$g2Pp) $(if ($g2Pp) { '' } else { 'no PlayPauseButton within 5 s (mpv''s own controls: a build without libmpv beside it?)' })
        if ($g2Pp) {
            $script:g2Playing = $true
            try {
                [void](Invoke-Element $g2Pp)
                $g2Paused = Wait-Until { $b = Find-Ui -Id 'PlayPauseButton'; if ($b -and $b.Current.Name -eq 'Play') { $b } } -Seconds 5 -PollMs 100
                Check "${g2Id} [player]: paused through Play/Pause (the bars stay up)" ([bool]$g2Paused)
                foreach ($g2PB in @('VolumeButton', 'SkipPartsButton', 'ChaptersButton', 'SpeedButton', 'QualityButton', 'TracksButton')) {
                    $g2Btn = Find-Ui -Id $g2PB -Type 'Button'
                    if (-not $g2Btn -or -not $g2Btn.Current.IsEnabled) { Say "menus: the player's $g2PB is not shown or not enabled here"; continue }
                    $g2PBName = G2-Short (G2-Name $g2Btn)
                    [void](Test-G2Menu -Id $g2Id -Name "player, $g2PBName, a click" -Opener $g2Btn -Open { G2-Click (Find-Ui -Id $g2PB -Type 'Button') } -Flyout)
                    [void][OffscreenUi]::SetFocus((Find-Ui -Id $g2PB -Type 'Button')); Start-Sleep -Milliseconds 300
                    [void](Test-G2Menu -Id $g2Id -Name "player, $g2PBName, Enter" -Opener (Find-Ui -Id $g2PB -Type 'Button') -Open { Key $g2Vk.Enter } -Flyout)
                    $g2Still = Find-Ui -Id 'PlayPauseButton'
                    Check "${g2Id} [player, $g2PBName]: still paused after its menu (no press reached the video)" ($g2Still -and $g2Still.Current.Name -eq 'Play') "Play/Pause says '$(G2-Name $g2Still)'"
                }
            }
            finally { $script:g2Playing = $false }
        }
        Start-Sleep -Seconds 1
        $g2Home = G2-Back { if ((G2-CardKinds).Count) { $true } }
        Check "${g2Id} [Home, $g2K, 'Play']: Back leaves the player for Home" ([bool]$g2Home)
        Start-Sleep -Seconds 2
    }
}
}
catch { G2-PhaseFailed 'Home' $_ }

# ========================================================================================================================
# 2. The item pages' More options, and the season's episode rows
try {
$g2Size = G2-Size $g2Sizes[-1]
$g2PageForbidden = @('Play', 'Mark as watched', 'Mark as unwatched', 'Add to favorites', 'Remove from favorites')
Step-Movie
$g2Movie = Test-G2PageMore 'movie page' $fixture.movie.name $g2PageForbidden
if ($g2Admin -and $g2Theme -eq 'Classic' -and $g2Movie) {
    # the administrator's dialogs (the same in every theme: Classic only) and Delete's question
    foreach ($g2D in @(@("Edit metadata$g2Ell", 'Edit metadata'), @("Edit images$g2Ell", 'Edit images'), @("Identify$g2Ell", 'Identify'), @("Refresh metadata$g2Ell", 'Refresh metadata'), @("Edit subtitles$g2Ell", 'Edit subtitles'))) {
        $g2Text = $g2D[0]
        Test-G2Dialog "movie page, '$g2Text'" $g2D[1] {
            G2-Click (Find-Ui -Name (G2-Name $g2Movie) -Type 'Button')
            $i = Wait-Until { G2-PopupItem $g2Text } -Seconds 4
            if ($i) { [void](Invoke-Element $i) }
        } $g2Movie
    }
    foreach ($g2No in @('Escape', 'Cancel')) {
        G2-Click (Find-Ui -Name (G2-Name $g2Movie) -Type 'Button')
        $g2I = Wait-Until { G2-PopupItem "Delete movie$g2Ell" } -Seconds 4
        if (-not $g2I) { Check "${g2Id} [movie page, Delete]: the menu has Delete movie" $false; G2-CloseFlyouts; break }
        $g2At = Get-Epoch
        [void](Invoke-Element $g2I)
        $g2Ask = Wait-Until { Find-Ui -Id 'PrimaryButton' -Type 'Button' } -Seconds 5
        if ($g2No -eq 'Escape') { Key $g2Vk.Escape } else { $c = Find-Ui -Id 'CloseButton' -Type 'Button'; if ($c) { [void](Invoke-Element $c) } }
        [void](Wait-Until { -not (Find-Ui -Id 'PrimaryButton' -Type 'Button') } -Seconds 5)
        Start-Sleep -Milliseconds 800
        $g2Del = @(Get-Requests $g2At | Where-Object { $_.method -eq 'DELETE' })
        Check "${g2Id} [movie page, Delete, then $g2No]: it asks first, and sends nothing" ($g2Ask -and $g2Del.Count -eq 0 -and (Wait-ItemPage $fixture.movie.name 2)) "asked: $([bool]$g2Ask); DELETE sent: $($g2Del.Count)"
    }
}
Step-Series
[void](Test-G2PageMore 'series page' $fixture.series.name $g2PageForbidden)
Step-Season
$g2Season = "$($fixture.season.name) of $($fixture.series.name)"
[void](Test-G2PageMore 'season page' $fixture.season.name $g2PageForbidden)
# the season's episodes: each row kind the theme shows
$g2Kinds = G2-CardKinds
foreach ($g2K in @($g2Kinds.Keys)) {
    $g2Card = (G2-CardKinds)[$g2K]
    if ($g2Card) { [void](Test-G2CardMenus "season page, $g2Size" $g2K $g2Card) }
}
}
catch { G2-PhaseFailed 'item pages' $_ }

# ========================================================================================================================
try {
# 3. The library's Sort and View
$g2Size = G2-Size $g2Sizes[0]
Open-Library $fixture.movie.library
[void](Wait-Until { Find-MediaCard $fixture.movie.name } -Seconds 30 -PollMs 200)
Start-Sleep -Seconds 1
foreach ($g2B in @(@('SortButton', 'Sort'), @('ViewButton', 'View'))) {
    $g2Btn = Wait-Ui -Id $g2B[0] -Type 'Button' -Seconds 5
    if (-not $g2Btn) { Check "${g2Id} [library, $($g2B[1])]: the button is there" $false; continue }
    $g2Was = G2-Name $g2Btn
    [void](Test-G2Menu -Id $g2Id -Name "library, $($g2B[1]), a click" -Opener $g2Btn -Open { G2-Click (Find-Ui -Id $g2B[0] -Type 'Button') })
    [void][OffscreenUi]::SetFocus((Find-Ui -Id $g2B[0] -Type 'Button')); Start-Sleep -Milliseconds 300
    $g2Names = Test-G2Menu -Id $g2Id -Name "library, $($g2B[1]), Enter" -Opener (Find-Ui -Id $g2B[0] -Type 'Button') -Open { Key $g2Vk.Enter } -KeepOpen
    $g2Pick = @(G2-MenuItems | Where-Object { $p = $null; -not ($_.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p) -and $p.Current.ToggleState -eq 'On') })[0]
    if ($g2Pick) {
        $g2PickName = $g2Pick.Current.Name
        [void](Invoke-Element $g2Pick)
        $g2Now = Wait-Until { $n = G2-Name (Find-Ui -Id $g2B[0] -Type 'Button'); if ($n -and $n -ne $g2Was) { $n } } -Seconds 6
        Check "${g2Id} [library, $($g2B[1]), '$g2PickName']: the button says the new choice, the menu closed" ($g2Now -and -not (G2-MenuOpen)) "'$g2Was' -> '$g2Now'"
        # back as it was
        G2-Click (Find-Ui -Id $g2B[0] -Type 'Button')
        $g2Old = Wait-Until { @(G2-MenuItems | Where-Object { $g2Was -like "*$($_.Current.Name)*" })[0] } -Seconds 4
        if ($g2Old) { [void](Invoke-Element $g2Old) } else { G2-CloseFlyouts }
        Start-Sleep -Milliseconds 800
    }
    else { G2-CloseFlyouts }
}
}
catch { G2-PhaseFailed 'library' $_ }

# ========================================================================================================================
try {
# 3b. The Libraries page: a library tile's menu (the menu key): its star's words, then the star (and back)
Open-Nav 'Libraries'
$g2Lib = $fixture.movie.library
$g2Tile = Wait-Until { @(Find-Ui -Name "$g2Lib, " -Contains -OffscreenToo | Where-Object { $_.Current.ClassName -eq 'Microsoft.UI.Xaml.Controls.ItemContainer' })[0] } -Seconds 15 -PollMs 200
if (-not $g2Tile) { $g2Tile = [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, $g2Lib, 'Microsoft.UI.Xaml.Controls.ItemContainer') }
if ($g2Tile) {
    $g2Stars = @("Add $g2Lib to the top bar", "Remove $g2Lib from the top bar")
    [void][OffscreenUi]::SetFocus($g2Tile); Start-Sleep -Milliseconds 400
    $g2Names = Test-G2Menu -Id $g2Id -Name "Libraries page, $g2Lib's tile, the menu key" -Opener $g2Tile -Open { Key $g2Vk.Apps } -Allowed $g2Stars
    if ($g2Names) {
        [void][OffscreenUi]::SetFocus($g2Tile); Start-Sleep -Milliseconds 300
        Key $g2Vk.Apps
        $g2Item = Wait-Until { foreach ($t in $g2Stars) { $i = G2-PopupItem $t; if ($i) { return $i } } } -Seconds 4
        if ($g2Item) {
            $g2Was = $g2Item.Current.Name
            $g2Mark = G2-PageMark $g2Tile
            [void](Invoke-Element $g2Item)
            Start-Sleep -Milliseconds 1200
            [void][OffscreenUi]::SetFocus($g2Tile); Start-Sleep -Milliseconds 300
            Key $g2Vk.Apps
            $g2Now = @(Wait-Until { $n = @(G2-MenuNames); if ($n.Count) { , $n } } -Seconds 4)
            $g2Other = @($g2Stars | Where-Object { $_ -ne $g2Was })[0]
            $g2Same = G2-SamePage $g2Mark $g2Tile
            Check "${g2Id} [Libraries page, '$g2Was']: done (the menu says '$g2Other' now), the tile not opened" ($g2Now -contains $g2Other -and $g2Same.Ok) "menu now: $($g2Now -join ' | '); $($g2Same.Detail)"
            $g2Back = G2-PopupItem $g2Other
            if ($g2Back) { [void](Invoke-Element $g2Back); Start-Sleep -Milliseconds 1000 } else { G2-CloseFlyouts }
        }
        else { G2-CloseFlyouts }
    }
}
else { Check "${g2Id} [Libraries page]: $g2Lib's tile" $false 'not found' }
}
catch { G2-PhaseFailed 'Libraries page' $_ }

# ========================================================================================================================
try {
# 3c. A collection's Order (FAKESERVER_COLLECTIONS=1): a click and Enter, Escape; a choice said on the button, then back
$g2Col = @($fixture.collections | Where-Object { $_ })[0]
if ($g2Col) {
    Open-Library 'Collections'
    Start-Sleep -Milliseconds 1500
    Open-Card $g2Col.name
    $g2Order = Wait-Ui -Id 'OrderButton' -Type 'Button' -Seconds 20
    if ($g2Order) {
        Start-Sleep -Milliseconds 1500
        $g2Was = G2-Name $g2Order
        [void](Test-G2Menu -Id $g2Id -Name "collection, Order, a click" -Opener $g2Order -Open { G2-Click (Find-Ui -Id 'OrderButton' -Type 'Button') })
        [void][OffscreenUi]::SetFocus((Find-Ui -Id 'OrderButton' -Type 'Button')); Start-Sleep -Milliseconds 300
        [void](Test-G2Menu -Id $g2Id -Name "collection, Order, Enter" -Opener (Find-Ui -Id 'OrderButton' -Type 'Button') -Open { Key $g2Vk.Enter } -KeepOpen)
        $g2Pick = @(G2-MenuItems | Where-Object { $p = $null; -not ($_.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p) -and $p.Current.ToggleState -eq 'On') })[0]
        if ($g2Pick) {
            $g2PickName = $g2Pick.Current.Name
            [void](Invoke-Element $g2Pick)
            $g2Now = Wait-Until { $n = G2-Name (Find-Ui -Id 'OrderButton' -Type 'Button'); if ($n -and $n -ne $g2Was) { $n } } -Seconds 6
            Check "${g2Id} [collection, Order, '$g2PickName']: the button says the new order, the menu closed" ($g2Now -and -not (G2-MenuOpen)) "'$g2Was' -> '$g2Now'"
            G2-Click (Find-Ui -Id 'OrderButton' -Type 'Button')
            $g2Old = Wait-Until { @(G2-MenuItems | Where-Object { $g2Was -like "*$($_.Current.Name)*" })[0] } -Seconds 4
            if ($g2Old) { [void](Invoke-Element $g2Old) } else { G2-CloseFlyouts }
            Start-Sleep -Milliseconds 800
        }
        else { G2-CloseFlyouts }
    }
    else { Check "${g2Id} [collection '$($g2Col.name)']: its Order button" $false 'not found' }
}
else { Say 'menus: no collections in this fake server (FAKESERVER_COLLECTIONS unset): the Order menu not tried' }
}
catch { G2-PhaseFailed 'collection' $_ }

# ========================================================================================================================
try {
# 4. The Account menu: a click and Enter, Escape, Settings
$g2Acct = Find-Ui -Name 'Account, ' -Type 'Button' -Contains
if ($g2Acct) {
    foreach ($g2Way in @(@('a click', { G2-Click (Find-Ui -Name 'Account, ' -Type 'Button' -Contains) }), @('Enter', { [void][OffscreenUi]::SetFocus((Find-Ui -Name 'Account, ' -Type 'Button' -Contains)); Start-Sleep -Milliseconds 300; Key $g2Vk.Enter }))) {
        G2-CloseFlyouts
        $g2Mark = G2-PageMark $g2Acct
        $g2At = Get-Epoch
        & $g2Way[1]
        $g2Open = Wait-Until { Find-Ui -Id 'AccountServerName' } -Seconds 4
        $g2Items = @(Wait-Until { $i = @('Switch user', 'Settings', 'Sign out' | Where-Object { Find-Ui -Name $_ -Type 'Button' -OffscreenToo }); if ($i.Count -eq 3) { , $i } } -Seconds 3 -PollMs 200)
        if ($g2Items.Count -ne 3) { $g2Items = @('Switch user', 'Settings', 'Sign out' | Where-Object { Find-Ui -Name $_ -Type 'Button' -OffscreenToo }) }
        Check "${g2Id} [Account, $($g2Way[0])]: the menu opens: the user, the server, Switch user, Settings, Sign out" ($g2Open -and $g2Items.Count -eq 3) "items: $($g2Items -join ', ')"
        Start-Sleep -Milliseconds 800
        $g2Same = G2-SamePage $g2Mark $g2Acct
        $g2Stray = @(G2-StrayRequests $g2At $null)
        Check "${g2Id} [Account, $($g2Way[0])]: nothing else happens" ($g2Same.Ok -and $g2Stray.Count -eq 0) "$($g2Same.Detail); sent: $($g2Stray -join ', ')"
        G2-Key $g2Vk.Escape
        $g2Closed = Wait-Until { -not (Find-Ui -Id 'AccountServerName') } -Seconds 3
        Start-Sleep -Milliseconds 300
        Check "${g2Id} [Account, $($g2Way[0])]: Escape closes it, the keyboard back on Account" ($g2Closed -and (G2-HasFocus (Find-Ui -Name 'Account, ' -Type 'Button' -Contains))) "focus on $(G2-Focused)"
    }
    G2-CloseFlyouts
    [void](G2-OpenAccount)
    $g2Set = Wait-Ui -Name 'Settings' -Type 'Button' -Seconds 4
    if ($g2Set) { [void](Invoke-Element $g2Set) }
    $g2OnSettings = Wait-Until { Find-Ui -Name 'Servers and accounts' -Type 'Text' -OffscreenToo } -Seconds 10
    Check "${g2Id} [Account, Settings]: Settings opens, the menu closed" ($g2OnSettings -and -not (Find-Ui -Id 'AccountServerName'))
}
}
catch { G2-PhaseFailed 'Account' $_ }

# ========================================================================================================================
try {
# 4b. The top bar's Libraries menu (a click and Enter, Escape, the keyboard back on it); the sections menu where the
# window is narrow enough to fold the tabs into one
[void](Open-Nav 'Home'); Start-Sleep -Milliseconds 800
function G2-LibrariesTab {
    $pane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id)
    if (-not $pane) { return $null }
    $t = [OffscreenUi]::NavItem($pane, 'Libraries')
    if (-not $t) { $t = @($pane.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { try { $_.Current.HelpText -like 'Libraries menu*' } catch { $false } })[0] }
    $t
}
$g2Tab = G2-LibrariesTab
if ($g2Tab) {
    [void](Test-G2Menu -Id $g2Id -Name "top bar, Libraries, a click" -Opener $g2Tab -Open { G2-Click (G2-LibrariesTab) })
    [void][OffscreenUi]::SetFocus((G2-LibrariesTab)); Start-Sleep -Milliseconds 300
    [void](Test-G2Menu -Id $g2Id -Name "top bar, Libraries, Enter" -Opener (G2-LibrariesTab) -Open { Key $g2Vk.Enter })
}
else { Say 'menus: no Libraries tab in the top bar at this size' }
foreach ($g2W in @('1000x700')) {
    $g2Narrow = G2-Size $g2W
    $g2Fold = Wait-Until { Find-Ui -Id 'TopTabsMenu' } -Seconds 3
    if ($g2Fold) {
        [void](Test-G2Menu -Id $g2Id -Name "top bar at $g2Narrow, the sections menu, a click" -Opener $g2Fold -Open { G2-Click (Find-Ui -Id 'TopTabsMenu') })
    }
    else { Say "menus: the top bar's tabs don't fold at $g2Narrow" }
}
$g2Size = G2-Size $g2Sizes[0]
}
catch { G2-PhaseFailed 'top bar' $_ }

# ========================================================================================================================
try {
# 5. A user who isn't an administrator: Switch user to harness-two
$g2Creds = Get-Content (Join-Path $PSScriptRoot '..\..\ui-harness\fakeserver\credentials.json') -Raw | ConvertFrom-Json
$g2Two = $g2Creds.second_user.username
[void](G2-OpenAccount)
$g2Switch = Wait-Ui -Name 'Switch user' -Type 'Button' -Seconds 4
if ($g2Switch -and $g2Two) {
    [void](Invoke-Element $g2Switch)
    [void](Wait-Until { G2-SignInPageServer } -Seconds 20)
    G2-SignInQuick $script:serverUrl $g2Two
    $g2Admin = $false
    $g2As = G2-SignedInAs
    Check "${g2Id} [not an administrator]: signed in as $g2Two" ($g2As.User -eq $g2Two) "account: $($g2As.User)"
    $g2Size = G2-Size $g2Sizes[0]
    [void](Wait-Until { (G2-CardKinds).Count } -Seconds 30 -PollMs 300)
    Start-Sleep -Seconds 2
    $g2Kinds = G2-CardKinds
    foreach ($g2K in @($g2Kinds.Keys)) {
        $g2Card = (G2-CardKinds)[$g2K]
        [void][OffscreenUi]::SetFocus($g2Card); Start-Sleep -Milliseconds 400
        $g2Names = Test-G2Menu -Id $g2Id -Name "not an administrator, Home, $g2K, the menu key" -Opener $g2Card -Open { Key $g2Vk.Apps } -Allowed $g2ItemTexts -Forbidden $g2AdminTexts
        if ($g2Names) { Test-G2ItemMenuWords "not an administrator, Home, $g2K" $g2Names ($g2K -in @('EpisodeRow', 'Showtime')) }
    }
    Step-Movie
    [void](Test-G2PageMore 'not an administrator, movie page' $fixture.movie.name ($g2PageForbidden + $g2AdminTexts) -HasEntries)
    # a series and a season have nothing in their More options for someone who isn't an administrator (no video, no
    # administration): the button must not be there at all, or open something; a button that does nothing is a dead end
    foreach ($g2PG in @(@('series page', $fixture.series.name, { Step-Series }), @('season page', $fixture.season.name, { Step-Season }))) {
        & $g2PG[2]
        Start-Sleep -Milliseconds 1200
        $g2PM = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) |
                Where-Object { try { ($_.Cached.Name -eq "More options for $($g2PG[1])" -or $_.Cached.Name -like "More options for $($g2PG[1]) of *") -and -not $_.Cached.IsOffscreen } catch { $false } } |
                Sort-Object { $_.Cached.BoundingRectangle.Y })[0]
        if (-not $g2PM) { Check "${g2Id} [not an administrator, $($g2PG[0])]: More options is shown only with something in it" $true 'not shown (nothing in it for this user)'; continue }
        G2-CloseFlyouts
        $g2At = Get-Epoch
        G2-Click $g2PM
        $g2Opened = Wait-Until { @(G2-MenuItems).Count } -Seconds 4
        Check "${g2Id} [not an administrator, $($g2PG[0])]: More options is shown only with something in it" ([bool]$g2Opened) $(if ($g2Opened) { (G2-MenuNames) -join ' | ' } else { "'$(G2-Name $g2PM)' is on the page and a click opens nothing" })
        G2-CloseFlyouts
    }
}
else { Check "${g2Id} [not an administrator]: Switch user in the Account menu" $false "Switch user: $([bool]$g2Switch); second test user: '$g2Two'" }
}
catch { G2-PhaseFailed 'not an administrator' $_ }
