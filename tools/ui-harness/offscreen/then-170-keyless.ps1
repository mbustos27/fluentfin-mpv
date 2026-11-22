# Nixie 1.7, stream K (keyless lists): Settings > Look up lists online and the Collections page's Awards row, in the current
# NIXIE_LAYOUT theme, against the fake server's outside fakes (drive.ps1 sets NIXIE_OUTSIDE_BASE: no real host is asked).
# Run with:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-170-keyless.ps1
# Covers: the section's heading and its two switches (off, then turned on), each switch's "What is sent" and Status lines,
# Wikidata's endpoint; the requests the fake server recorded (only files.tmdb.org and query.wikidata.org, each a fixed
# public list's shape, Nixie's User-Agent with its contact URL, nothing from the library); the Collections page's Awards
# row, its See all grid, and an award's Collection page (its eyebrow and the why line naming Wikidata). Shots
# <tag>-<name>-<width>.png at 1920 and 1100 (NIXIE_THEME / SG_SHOT_TAG name them). Dot-sourced by drive.ps1: every name
# here starts with ks. ASCII only (Windows PowerShell reads a .ps1 without a BOM in the ANSI code page).

$ksTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
if ($env:NIXIE_THEME) { $ksTag += '-' + $env:NIXIE_THEME.ToLowerInvariant() }
$ksWidths = if ($env:KS_WIDTHS) { @($env:KS_WIDTHS -split ',' | ForEach-Object { [int]$_ }) } else { @(1920, 1100) }

function Ks-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function Ks-Size([int]$W) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $(if ($W -ge 1920) { 1080 } else { 900 })); Ks-Settle 1500 }
function Ks-IntoView($Element) {
    if (-not $Element) { return }
    $p = $null
    try { if ($Element.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$p)) { $p.ScrollIntoView() } } catch {}
    Ks-Settle 500
}
function Ks-Shots([string]$Name, [scriptblock]$Before) {
    foreach ($w in $ksWidths) {
        Ks-Size $w
        if ($Before) { & $Before }
        Shot "$ksTag-$Name-$w"
    }
    Ks-Size 1920
}
function Ks-ToggleState($Element) {
    $p = $null
    if ($Element -and $Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { return [string]$p.Current.ToggleState }
    return 'none'
}
# a SettingsExpander by its x:Name: its own Expander (inside it) has the expand pattern (as then-160-skip.ps1 does)
function Ks-Expand([string]$Id) {
    $exp = Wait-Until { Find-Ui -Id $Id -OffscreenToo } -Seconds 10 -PollMs 250
    if (-not $exp) { return $false }
    Ks-IntoView $exp
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
    $toggle = if ($exp.GetSupportedPatterns() -contains [System.Windows.Automation.ExpandCollapsePattern]::Pattern) { $exp } else { $exp.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond) }
    try { $toggle.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Ks-Settle 700; return $true } catch { Say "lists: could not expand ${Id}: $($_.Exception.Message)" }
    return $false
}

$ksTmdbName = 'Franchise and keyword names from TMDb'
$ksWikiName = 'Awards and genres from Wikidata'

# --- the guard: nothing is turned on unless the app says every outside source goes to the fake server -----------------
# (BH-170-q1, the 1.7.0 bug hunt; as then-170-trending's: a release package ignores NIXIE_OUTSIDE_BASE by design, and
# run-offscreen.ps1 -Exe takes any Nixie.exe, so turning the switches on there would send 14 real requests. The app's log
# names where the sources go; anything but loopback stops here, before any switch is touched.)
$ksLog = Get-ChildItem -LiteralPath (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$ksFake = $ksLog -and (Select-String -LiteralPath $ksLog.FullName -Pattern '\[outside\] harness run: every outside source goes to "?http://127\.0\.0\.1:' -Quiet)
Check "lists ($ksTag): the app sends every outside source to the fake server" ([bool]$ksFake) $(if ($ksLog) { $ksLog.Name } else { 'no app log' })
if ($ksFake) {

# --- Settings > Look up lists online: both switches off by default, then turned on --------------------------------------
[void](Invoke-Server '/__harness/outside/requests' 'POST' @{ clear = $true })
Open-Nav 'Settings'
$ksHeading = Wait-Ui -Name 'Look up lists online' -Type 'Text' -OffscreenToo -Seconds 15
Check "lists ($ksTag): Settings has Look up lists online" ([bool]$ksHeading) ''
Ks-IntoView $ksHeading
# the switches by their x:Name (the expander's own button carries the same name, without the toggle)
$ksTmdb = Wait-Until { Find-Ui -Id 'TmdbExportsSwitch' -OffscreenToo } -Seconds 10 -PollMs 200
$ksWiki = Wait-Until { Find-Ui -Id 'WikidataSwitch' -OffscreenToo } -Seconds 10 -PollMs 200
Check "lists ($ksTag): the switches' Narrator names say what they look up" (($ksTmdb.Current.Name -eq $ksTmdbName) -and ($ksWiki.Current.Name -eq $ksWikiName)) "'$($ksTmdb.Current.Name)', '$($ksWiki.Current.Name)'"
Check "lists ($ksTag): the TMDb switch is named and off by default" ((Ks-ToggleState $ksTmdb) -eq 'Off') "$(Ks-ToggleState $ksTmdb)"
Check "lists ($ksTag): the Wikidata switch is named and off by default" ((Ks-ToggleState $ksWiki) -eq 'Off') "$(Ks-ToggleState $ksWiki)"
$ksBefore = @((Invoke-Server '/__harness/outside/requests').requests)
Check "lists ($ksTag): nothing is asked while both are off" ($ksBefore.Count -eq 0) "$($ksBefore.Count) requests"
Ks-Shots 'settings-lists-off' { Ks-IntoView (Find-Ui -Id 'WikidataSwitch' -OffscreenToo) }

if ($ksTmdb) { Say "TMDb switch ($(Invoke-Element $ksTmdb))" }
if ($ksWiki) { Say "Wikidata switch ($(Invoke-Element $ksWiki))" }
Ks-Settle 800
Check "lists ($ksTag): the TMDb switch reads on" ((Ks-ToggleState $ksTmdb) -eq 'On') "$(Ks-ToggleState $ksTmdb)"
Check "lists ($ksTag): the Wikidata switch reads on" ((Ks-ToggleState $ksWiki) -eq 'On') "$(Ks-ToggleState $ksWiki)"

# the fake answers every list: TMDb's two files, Wikidata's twelve queries
$ksSeen = Wait-Until {
    $r = @((Invoke-Server '/__harness/outside/requests').requests)
    if (@($r | Where-Object { $_.host -eq 'query.wikidata.org' }).Count -ge 12 -and @($r | Where-Object { $_.host -eq 'files.tmdb.org' }).Count -ge 2) { , $r }
} -Seconds 45 -PollMs 500
$ksRequests = @($ksSeen)
Check "lists ($ksTag): turning them on fetched TMDb's two files and Wikidata's lists" ($ksRequests.Count -ge 14) "$($ksRequests.Count) requests"
$ksHosts = @($ksRequests | ForEach-Object { $_.host } | Sort-Object -Unique)
Check "lists ($ksTag): only files.tmdb.org and query.wikidata.org were asked (through the fake)" ((@($ksHosts | Where-Object { $_ -notin @('files.tmdb.org', 'query.wikidata.org') }).Count -eq 0)) ($ksHosts -join ', ')
$ksBadShape = @($ksRequests | Where-Object {
        -not (($_.host -eq 'files.tmdb.org' -and $_.path -match '^/p/exports/(collection|keyword)_ids_\d\d_\d\d_\d{4}\.json\.gz$' -and -not $_.query) -or
            ($_.host -eq 'query.wikidata.org' -and $_.path -eq '/sparql' -and $_.query -like 'query=PREFIX*'))
    })
Check "lists ($ksTag): every request is a fixed public list's shape" ($ksBadShape.Count -eq 0) "$(@($ksBadShape | ForEach-Object { $_.path }) -join ' ')"
$ksAgents = @($ksRequests | ForEach-Object { $_.headers.'User-Agent' } | Sort-Object -Unique)
Check "lists ($ksTag): each carries Nixie's User-Agent with its contact URL" (($ksAgents.Count -eq 1) -and ($ksAgents[0] -match '^Nixie/\S+ \(https://github\.com/\S+\)$')) ($ksAgents -join ' | ')
# nothing from the library: the showcase titles and ids the fake server holds
$ksFixture = Invoke-Server '/__harness/fixture'
$ksSecrets = @($ksFixture.PSObject.Properties | ForEach-Object { $_.Value } | Where-Object { $_ -is [string] -and $_.Length -ge 4 -and $_ -notmatch '^https?://' })
$ksLeaks = @(foreach ($r in $ksRequests) {
        $text = [Uri]::UnescapeDataString("$($r.path)?$($r.query) $(($r.headers.PSObject.Properties | ForEach-Object { "$($_.Name): $($_.Value)" }) -join ' ')")
        foreach ($s in $ksSecrets) { if ($text.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $s } }
    })
Check "lists ($ksTag): no request carries a title or id from the library" ($ksLeaks.Count -eq 0) "$(@($ksLeaks | Sort-Object -Unique) -join ', ') (checked $($ksSecrets.Count) values)"

# each source's cards: What is sent, Status (the fake answered, so "Lists up to date."), Wikidata's endpoint
$ksExpanded = Ks-Expand 'WikidataExpander'
[void](Ks-Expand 'TmdbExportsExpander')
$ksSent = Wait-Ui -Name 'What is sent to Wikidata' -OffscreenToo -Seconds 5
Check "lists ($ksTag): Wikidata's What is sent card is there" ([bool]$ksSent) "expanded: $ksExpanded"
$ksEndpoint = Wait-Ui -Name 'Wikidata query endpoint' -OffscreenToo -Seconds 5
Check "lists ($ksTag): Wikidata's query endpoint is named" ([bool]$ksEndpoint) ''
$ksStatus = Wait-Until { Find-Ui -Name 'Lists up to date.' -Contains -OffscreenToo } -Seconds 20 -PollMs 300
Check "lists ($ksTag): the status line says the lists are up to date" ([bool]$ksStatus) ''
Ks-Shots 'settings-lists-on' { Ks-IntoView (Find-Ui -Name 'What is sent to TMDb' -OffscreenToo) }
Ks-Shots 'settings-lists-wikidata' { Ks-IntoView (Find-Ui -Name 'Wikidata query endpoint' -OffscreenToo) }

# a refusal shows in Nixie's words: query-next before it opens
if ($ksEndpoint) {
    $box = Find-Ui -Name 'Wikidata query endpoint' -Type 'ComboBox' -OffscreenToo
    if ($box) {
        $p = $null
        if ($box.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand(); Ks-Settle 500 }
        $next = Wait-Ui -Name 'query-next.wikidata.org' -Type 'ListItem' -Seconds 5
        if ($next) { [void](Invoke-Element $next) }
        Ks-Settle 600
        $desc = Find-Ui -Name 'early access' -Contains -OffscreenToo
        Check "lists ($ksTag): choosing query-next says it is in early access" ([bool]$desc) ''
        if ($box.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand(); Ks-Settle 500 }
        $main = Wait-Ui -Name 'query.wikidata.org' -Type 'ListItem' -Seconds 5
        if ($main) { [void](Invoke-Element $main) }
        Ks-Settle 600
    }
}

# --- the Collections page: the Awards row, its See all, an award's Collection page ------------------------------------
Open-Nav 'Libraries'
$ksMade = Wait-Until { Find-Ui -Name 'Made by Nixie, ' -Contains -Type 'ListItem' } -Seconds 30 -PollMs 300
if ($ksMade) { Say "Made by Nixie tile ($(Invoke-Element $ksMade))" }
$ksTitle = Wait-Ui -Id 'TitleText' -Name 'Collections' -OffscreenToo -Seconds 20  # opened at Made by Nixie, the title may be scrolled away
Check "lists ($ksTag): the Collections page opens" ([bool]$ksTitle) ''
$ksAward = Wait-Until { Find-Ui -Name ', made by Nixie, award, ' -Contains -OffscreenToo } -Seconds 40 -PollMs 400
Check "lists ($ksTag): the Collections page has award collections" ([bool]$ksAward) $(if ($ksAward) { $ksAward.Current.Name } else { 'none' })
$ksSeeAll = Find-Ui -Name 'See all Awards' -OffscreenToo
Check "lists ($ksTag): the Awards row ends in See all" ([bool]$ksSeeAll) ''
Ks-Shots 'collections-awards' { Ks-IntoView (Find-Ui -Name ', made by Nixie, award, ' -Contains -OffscreenToo) }

if ($ksSeeAll) {
    Say "See all Awards ($(Invoke-Element $ksSeeAll))"
    $ksGridTitle = Wait-Ui -Id 'TitleText' -Name 'Awards' -Seconds 15
    Check "lists ($ksTag): See all opens the Awards grid" ([bool]$ksGridTitle) ''
    $ksAward = Wait-Until { Find-Ui -Name ', made by Nixie, award, ' -Contains -OffscreenToo } -Seconds 15 -PollMs 300
    Ks-Shots 'awards-see-all'
}

if ($ksAward) {
    $ksName = $ksAward.Current.Name
    Say "award '$ksName' ($(Invoke-Element $ksAward))"
    $ksEyebrow = Wait-Until { $e = Find-Ui -Name 'Made by Nixie ' -Contains -Type 'Text'; if ($e -and $e.Current.Name -like '*Award*') { $e } } -Seconds 15 -PollMs 200
    Check "lists ($ksTag): the award's eyebrow says Made by Nixie, Award" ([bool]$ksEyebrow) "$(if ($ksEyebrow) { $ksEyebrow.Current.Name })"
    $ksWhy = Wait-Until { Find-Ui -Name 'from Wikidata' -Contains -Type 'Text' } -Seconds 5 -PollMs 200
    Check "lists ($ksTag): the award's why line names Wikidata" ([bool]$ksWhy) "$(if ($ksWhy) { $ksWhy.Current.Name })"
    Ks-Shots 'award-collection'
    Key 0x08   # Back
    Ks-Settle 1500
}
}
else { Say "lists ($ksTag): skipped: the app is not in harness mode, so no source is turned on" }
