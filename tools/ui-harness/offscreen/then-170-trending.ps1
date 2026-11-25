# Nixie 1.7, stream T: Trakt and TMDb trending as the user meets them, against the fake server's strict Trakt and TMDb
# (tools\ui-harness\fakeserver\outside_trakt.py, outside_tmdb_trending.py; the app's outside requests go there in a harness run,
# NIXIE_OUTSIDE_BASE, never to a real host). Run with:
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-170-trending.ps1
# Covers: Settings > Look up lists online: Trakt's and TMDb trending's switches turned on (keyboard-free, through UI
# Automation), the TMDb token pasted and saved (the field empties, "Saved"), the status lines; the requests the fake took
# (Trakt's and TMDb's hosts only, list addresses only, no image); Home's "Trending on this server"; the Collections
# page's Trending and Box office rows and a trending collection's page (its eyebrow and why line); Trakt turned off taking
# its rows away. Shots <tag>-170-<name>-<width>.png at 1920 and 1100 (TT_WIDTHS). Dot-sourced by drive.ps1: every name
# here starts with tt. ASCII only (Windows PowerShell reads a .ps1 without a BOM in the ANSI code page).

$ttTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
if ($env:NIXIE_THEME) { $ttTag += '-' + $env:NIXIE_THEME.ToLowerInvariant() }
$ttWidths = if ($env:TT_WIDTHS) { @($env:TT_WIDTHS -split ',' | ForEach-Object { [int]$_ }) } else { @(1920, 1100) }
# the harness's TMDb token (a JWT's shape; only the fake takes it: outside_tmdb_trending.HARNESS_TOKEN)
$ttToken = 'eyJhbGciOiJIUzI1NiJ9.eyJuaXhpZSI6Imhhcm5lc3MifQ.bml4aWUtaGFybmVzcy1zaWduYXR1cmU'

function TT-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function TT-Size([int]$W) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $(if ($W -ge 1920) { 1080 } else { 900 })); TT-Settle 1500 }
function TT-Shots([string]$Name, [scriptblock]$Before) {
    foreach ($w in $ttWidths) {
        TT-Size $w
        if ($Before) { & $Before }
        Shot "$ttTag-170-$Name-$w"
    }
    TT-Size 1920
}
function TT-Toggled($Element) {
    $p = $null
    if ($Element -and $Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { return $p.Current.ToggleState -eq 'On' }
    $false
}
function TT-Switch([string]$Name) {
    Wait-Until {
        $all = ([OffscreenUi]::Root($script:hwnd, $script:ui.Id)).FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
        foreach ($e in $all) { $p = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { return $e } }
    } -Seconds 15 -PollMs 200
}
function TT-Expand([string]$Name) {
    $all = ([OffscreenUi]::Root($script:hwnd, $script:ui.Id)).FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    foreach ($e in $all) {
        $p = $null
        if ($e.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) {
            if ($p.Current.ExpandCollapseState -ne 'Expanded') { $p.Expand() }
            return $true
        }
    }
    $false
}
function TT-Requests { (Invoke-Server '/__harness/outside/requests' 'GET').requests }
# brings the element named $Name into view (its ScrollItem pattern, else the keyboard focus on it)
function TT-Into([string]$Name) {
    $e = Find-Ui -Name $Name -OffscreenToo
    if (-not $e) { return }
    $p = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { try { $p.ScrollIntoView(); return } catch { } }
    try { [void][OffscreenUi]::SetFocus($e) } catch { }
}
function TT-Bottom { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); TT-Settle 900 }

# --- the guard: nothing is turned on unless the app says every outside source goes to the fake server -----------------
# (the coordinator, 2026-11-21: the layer once took harness mode from the glass setting; a run with -GlassFollowsActivity
# would have sent the sources to the real hosts. The app's log names where they go; anything but loopback stops here.)
$ttLog = Get-ChildItem -LiteralPath (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$ttFake = $ttLog -and (Select-String -LiteralPath $ttLog.FullName -Pattern '\[outside\] harness run: every outside source goes to "?http://127\.0\.0\.1:' -Quiet)
Check "trending ($ttTag): the app sends every outside source to the fake server" ([bool]$ttFake) $(if ($ttLog) { $ttLog.Name } else { 'no app log' })
if ($ttFake) {

# --- Settings > Look up lists online: Trakt and TMDb trending on, the token saved ---------------------------------------
Open-Nav 'Settings'
$ttTrakt = TT-Switch 'Trending from Trakt'
Check "trending ($ttTag): Settings has Trakt's switch, off by default" ([bool]($ttTrakt -and -not (TT-Toggled $ttTrakt))) $(if ($ttTrakt) { "on=$(TT-Toggled $ttTrakt)" } else { 'none' })
$ttTmdb = TT-Switch 'Trending from TMDb'
Check "trending ($ttTag): Settings has TMDb trending's switch, off by default" ([bool]($ttTmdb -and -not (TT-Toggled $ttTmdb))) $(if ($ttTmdb) { "on=$(TT-Toggled $ttTmdb)" } else { 'none' })
TT-Shots 'settings-off' { TT-Into 'Trending from TMDb'; TT-Settle 700 }
if ($ttTrakt) { [void](Invoke-Element $ttTrakt); TT-Settle 500 }
Check "trending ($ttTag): Trakt's switch turns on" (TT-Toggled $ttTrakt) ''
if ($ttTmdb) { [void](Invoke-Element $ttTmdb); TT-Settle 500 }
Check "trending ($ttTag): TMDb trending's switch turns on" (TT-Toggled $ttTmdb) ''
[void](TT-Expand 'Trending from TMDb')
TT-Settle 800
# 1.7 (the owner, 2026-11-22): the build carries Nixie's own TMDb key (a harness run: the fake's), so TMDb works at once
$ttAppKey = Wait-Until { Find-Ui -Name "Uses Nixie's own TMDb key" -Contains -OffscreenToo } -Seconds 8 -PollMs 200
Check "trending ($ttTag): TMDb says it uses Nixie's own key, and that yours can replace it" ([bool]$ttAppKey) ''
$ttBox = Wait-Until { Find-Ui -Name 'TMDb API Read Access Token' -OffscreenToo } -Seconds 10 -PollMs 200
Check "trending ($ttTag): the token field is there, named for Narrator" ([bool]$ttBox) ''
if ($ttBox) {
    try { Set-ElementValue $ttBox $ttToken; TT-Settle 500 } catch { Say "token field: $($_.Exception.Message)" }
    $ttSave = Wait-Ui -Name 'Save TMDb token' -Type 'Button' -Seconds 5
    if ($ttSave) { [void](Invoke-Element $ttSave) }
    $ttSaved = Wait-Until { Find-Ui -Name 'Saved. Nixie uses it for TMDb trending.' -OffscreenToo } -Seconds 8 -PollMs 200
    Check "trending ($ttTag): the token is saved and the field says so" ([bool]$ttSaved) ''
    $ttRemove = Wait-Ui -Name 'Remove TMDb token' -Type 'Button' -Seconds 5
    Check "trending ($ttTag): Remove is offered for the kept token" ([bool]$ttRemove) ''
}
$ttUpToDate = Wait-Until { @(Find-Ui -Name 'Lists up to date.' -OffscreenToo).Count -ge 1 } -Seconds 30 -PollMs 300
Check "trending ($ttTag): the status says the lists are in" ([bool]$ttUpToDate) ''
TT-Shots 'settings-on' { [void](TT-Expand 'Trending from TMDb'); TT-Into 'Your TMDb token'; TT-Settle 700 }

# --- what the fake took: Trakt's and TMDb's list addresses only, no image, nothing of the library -----------------------
$ttAsked = @(TT-Requests)
$ttHosts = @($ttAsked | ForEach-Object { $_.host } | Sort-Object -Unique)
Check "trending ($ttTag): requests went to Trakt's and TMDb's fakes" (($ttHosts -contains 'api.trakt.tv') -and ($ttHosts -contains 'api.themoviedb.org')) ($ttHosts -join ', ')
$ttOdd = @($ttAsked | Where-Object { $_.host -in @('api.trakt.tv', 'api.themoviedb.org') } | Where-Object {
        -not (($_.path -match '^/(movies|shows)/(trending|popular|anticipated)$' -and $_.query -match '^page=\d&limit=250$') -or
              ($_.path -eq '/movies/boxoffice' -and -not $_.query) -or
              ($_.path -match '^/3/trending/(movie|tv)/week$' -and $_.query -match '^page=[1-5]$')) })
Check "trending ($ttTag): only the fixed list addresses were asked" ($ttOdd.Count -eq 0) $(($ttOdd | Select-Object -First 3 | ForEach-Object { "$($_.host)$($_.path)?$($_.query)" }) -join '; ')
$ttImages = @($ttAsked | Where-Object { $_.host -eq 'image.tmdb.org' -or $_.path -like '/t/p/*' })
Check "trending ($ttTag): no image was asked for" ($ttImages.Count -eq 0) "$($ttImages.Count)"
$ttKeyInUrl = @($ttAsked | Where-Object { $_.query -match 'api_key' -or $_.query -like "*$ttToken*" })
Check "trending ($ttTag): the token never went in an address" ($ttKeyInUrl.Count -eq 0) ''

# --- Home: Trending on this server (1.8; not "this week": Trakt's trending is right now) ---------------------------------
Open-Nav 'Home'
TT-Settle 1500
$ttRow = Wait-Until {
    # the row is Home's last: scrolled to, so a page that realizes its rows as they come into view has it
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100)
    $r = Find-Ui -Name 'Trending on this server' -Contains -OffscreenToo
    if (-not $r) { $r = Find-Ui -Name 'TRENDING ON THIS SERVER' -Contains -OffscreenToo }
    $r
} -Seconds 45 -PollMs 300
Check "trending ($ttTag): Home shows Trending on this server" ([bool]$ttRow) ''
TT-Shots 'home' {
    $r = Find-Ui -Name 'Trending on this server' -Contains -OffscreenToo
    if (-not $r) { $r = Find-Ui -Name 'TRENDING ON THIS SERVER' -Contains -OffscreenToo }
    if ($r) { try { ([System.Windows.Automation.ScrollItemPattern]$r.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)).ScrollIntoView() } catch { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100) } }
    TT-Settle 900
}

# --- the Collections page: Trending and Box office this week -----------------------------------------------------------
Open-Nav 'Collections'
$ttTrendRow = Wait-Until { Find-Ui -Name 'Trending' -Type 'Text' -OffscreenToo } -Seconds 30 -PollMs 300
Check "trending ($ttTag): the Collections page has a Trending row" ([bool]$ttTrendRow) ''
$ttBoxRow = Find-Ui -Name 'Box office this week' -Contains -OffscreenToo
Check "trending ($ttTag): and a Box office this week row" ([bool]$ttBoxRow) ''
$ttTile = Wait-Until { Find-Ui -Name 'Trending films, made by Nixie, trending, ' -Contains -OffscreenToo } -Seconds 10 -PollMs 200
Check "trending ($ttTag): its tile is named for Narrator" ([bool]$ttTile) $(if ($ttTile) { $ttTile.Current.Name } else { 'none' })
TT-Shots 'collections' { TT-Bottom }
if ($ttTile) {
    Say "trending tile ($(Invoke-Element $ttTile))"
    $ttEyebrow = Wait-Until { $e = Find-Ui -Name 'Made by Nixie ' -Contains -Type 'Text'; if ($e -and $e.Current.Name -like '*Trending*') { $e } } -Seconds 10 -PollMs 200
    Check "trending ($ttTag): the collection's eyebrow says Made by Nixie, Trending" ([bool]$ttEyebrow) $(if ($ttEyebrow) { $ttEyebrow.Current.Name })
    # each source's window (Trakt's trending is right now, TMDb's the week; Trakt's API Use Policy: lists labelled accurately)
    $ttWhy = Wait-Until { $e = Find-Ui -Name 'Trakt: watched right now' -Contains -Type 'Text'; if ($e -and $e.Current.Name -like '*TMDb: this week*') { $e } } -Seconds 5 -PollMs 200
    Check "trending ($ttTag): its why line says each source's window (Trakt: watched right now, TMDb: this week)" ([bool]$ttWhy) $(if ($ttWhy) { $ttWhy.Current.Name } else { 'none' })
    TT-Shots 'collection'
    Key 0x08   # Back
    TT-Settle 1500
}

# --- the way out: Trakt off takes its rows away ----------------------------------------------------------------------
Open-Nav 'Settings'
$ttTrakt = TT-Switch 'Trending from Trakt'
if ($ttTrakt -and (TT-Toggled $ttTrakt)) { [void](Invoke-Element $ttTrakt); TT-Settle 500 }
Check "trending ($ttTag): Trakt's switch turns off again" ([bool]($ttTrakt -and -not (TT-Toggled $ttTrakt))) ''
Open-Nav 'Collections'
TT-Settle 2500
$ttBoxGone = -not (Find-Ui -Name 'Box office this week' -Contains -OffscreenToo)
Check "trending ($ttTag): with Trakt off, its Box office row is gone" $ttBoxGone ''
$ttStill = Find-Ui -Name 'Trending' -Type 'Text' -OffscreenToo
Check "trending ($ttTag): TMDb's trending row stays" ([bool]$ttStill) ''
if ($ttWidths -contains 1920) { TT-Size 1920; TT-Bottom; Shot "$ttTag-170-collections-trakt-off-1920" }

# --- About: TMDb's logo beside its notice (1.7, the owner's yes of 2026-11-22), every theme at 1920 --------------------
Open-Nav 'Settings'
TT-Size 1920
$ttLogo = Wait-Until { Find-Ui -Name 'TMDB logo' -OffscreenToo } -Seconds 15 -PollMs 200
if ($ttLogo) { TT-Bottom; TT-Settle 600; $ttLogo = Find-Ui -Name 'TMDB logo' }   # About's credits end the page
Check "trending ($ttTag): About shows TMDb's logo, named for Narrator, on screen" ([bool]$ttLogo) $(if ($ttLogo) { "$([int]$ttLogo.Current.BoundingRectangle.Width) x $([int]$ttLogo.Current.BoundingRectangle.Height)" } else { 'none' })
$ttNotice = Find-Ui -Name 'Nixie uses TMDB and the TMDB APIs but is not endorsed' -Contains
Check "trending ($ttTag): its notice is beside it" ([bool]$ttNotice) ''
Shot "$ttTag-170-about-1920"
}
else { Say "trending ($ttTag): skipped: the app is not in harness mode, so no source is turned on" }
