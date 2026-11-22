# Nixie 1.6, the collections stream: what the user sees of Nixie's collections in the current NIXIE_LAYOUT theme, with
# FAKESERVER_COLLECTIONS=1 (the admin's box sets, one with its franchise's TMDb id leaving an owned film out). Run with:
#   $env:FAKESERVER_COLLECTIONS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script home -NoA11y -Then offscreen\then-160-collections.ps1
# Covers: Home's Made by Nixie rows (after New in ..., filled once the library index is in); the Libraries page's "Collections &
# playlists" group with "Made by Nixie - N"; its tile opening the Collections page at Made by Nixie (the keyboard on its first
# tile); a Nixie franchise's Collection page (the eyebrow, the why line, the mosaic, Save as playlist and its answer, and the
# server's no); the admin's box set with "Also in your library"; Settings > Collections. Shots <tag>-<name>-<width>.png at
# 1920 and 1100 (NIXIE_THEME / SG_SHOT_TAG name them). Dot-sourced by drive.ps1: every name here starts with co.

$coTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
if ($env:NIXIE_THEME) { $coTag += '-' + $env:NIXIE_THEME.ToLowerInvariant() }
$coWidths = if ($env:CO_WIDTHS) { @($env:CO_WIDTHS -split ',' | ForEach-Object { [int]$_ }) } else { @(1920, 1100) }

function Co-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function Co-Size([int]$W) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $(if ($W -ge 1920) { 1080 } else { 900 })); Co-Settle 1500 }
function Co-Shots([string]$Name, [scriptblock]$Before) {
    foreach ($w in $coWidths) {
        Co-Size $w
        if ($Before) { & $Before }
        Shot "$coTag-$Name-$w"
    }
    Co-Size 1920
}
function Co-Focus { $coF = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); if ($coF) { $coF | ConvertFrom-Json } else { $null } }

# --- Home: the Made by Nixie rows, after New in ... (the index is read in the background on a first visit) -----------
$coRow = Wait-Until { Find-Ui -Name 'Highly rated, not watched' -Contains -OffscreenToo } -Seconds 60 -PollMs 300
Check "collections ($coTag): Home shows a Made by Nixie row" ([bool]$coRow) 'Highly rated, not watched'
$coKicker = Find-Ui -Name 'Made by Nixie' -Contains -OffscreenToo
if (-not $coKicker) { $coKicker = Find-Ui -Name 'MADE BY NIXIE' -OffscreenToo }
Check "collections ($coTag): the row says it is Nixie's" ([bool]$coKicker) ''
Co-Shots 'home-nixie' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Co-Settle 900 }

# --- the Libraries page: the group, with Made by Nixie - N ----------------------------------------------------------
Open-Nav 'Libraries'
$coMade = Wait-Until { Find-Ui -Name 'Made by Nixie, ' -Contains -Type 'ListItem' } -Seconds 30 -PollMs 300
Check "collections ($coTag): the Libraries page has Made by Nixie, N" ([bool]$coMade) $(if ($coMade) { $coMade.Current.Name } else { 'none' })
Co-Shots 'libraries' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Co-Settle 600 }

# --- its tile: the Collections page at Made by Nixie, the keyboard on its first tile ---------------------------------
if ($coMade) {
    Say "Made by Nixie tile ($(Invoke-Element $coMade))"
    # (1.6 integration: the page's title element, not any Text named Collections: the top bar's tab is one too)
    $coTitle = Wait-Ui -Id 'TitleText' -Name 'Collections' -Seconds 20
    Check "collections ($coTag): the tile opens the Collections page" ([bool]$coTitle) ''
    $coPicks = Wait-Until { Find-Ui -Name 'Picks' -Contains } -Seconds 20 -PollMs 200
    Check "collections ($coTag): its Made by Nixie rows are there" ([bool]$coPicks) ''
    Co-Settle 1200
    $coF = Co-Focus
    Check "collections ($coTag): arriving at Made by Nixie, the keyboard is on a Nixie tile" ([bool]($coF -and $coF.name -like '*made by Nixie*')) "$($coF.type) '$($coF.name)'"
    Co-Shots 'collections-nixie'
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Co-Settle 600
    Co-Shots 'collections-top' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Co-Settle 600 }
}

# --- a Nixie franchise on the Collection page -----------------------------------------------------------------------
$coFranchise = Wait-Until { Find-Ui -Name ', made by Nixie, franchise, ' -Contains -OffscreenToo } -Seconds 10
if ($coFranchise) {
    $coName = $coFranchise.Current.Name
    Say "franchise '$coName' ($(Invoke-Element $coFranchise))"
    # (ASCII only in this file: Windows PowerShell reads a .ps1 without a BOM in the ANSI code page)
    $coEyebrow = Wait-Until { $e = Find-Ui -Name 'Made by Nixie ' -Contains -Type 'Text'; if ($e -and $e.Current.Name -like '*Franchise*') { $e } } -Seconds 5 -PollMs 200
    # the eyebrow and the why line are the colpage stream's header, filled through its hook (CollectionViewModel's
    # "1.6 collections" block); 1.6 integration: checked now the two branches are merged (NIXIE_COLPAGE_SEAMS on)
    Check "collections ($coTag): the header's eyebrow says Made by Nixie, Franchise" ([bool]$coEyebrow) "$(if ($coEyebrow) { $coEyebrow.Current.Name })"
    $coWhy = Wait-Until { Find-Ui -Name 'Your films that TMDb lists in one collection' -Contains -Type 'Text' } -Seconds 3 -PollMs 200
    Check "collections ($coTag): the header's why line says where it came from" ([bool]$coWhy) ''
    $coSave = Wait-Ui -Name 'Save as playlist' -Type 'Button' -Seconds 10
    Check "collections ($coTag): Save as playlist is there" ([bool]$coSave) ''
    Co-Shots 'nixie-collection'
    # the server's no, in Nixie's words; then made, with Open
    [void](Invoke-Server '/__harness/playlists' 'POST' @{ refuse = 403 })
    if ($coSave) { [void](Invoke-Element $coSave) }
    $coNo = Wait-Until { Find-Ui -Name "doesn't let your account make playlists" -Contains -Type 'Text' } -Seconds 10
    Check "collections ($coTag): a refused playlist is said in Nixie's words" ([bool]$coNo) ''
    if ($coWidths -contains 1920) { Co-Size 1920; Shot "$coTag-playlist-refused-1920" }
    [void](Invoke-Server '/__harness/playlists' 'POST' @{ refuse = 0 })
    $coSave = Find-Ui -Name 'Save as playlist' -Type 'Button'
    if ($coSave) { [void](Invoke-Element $coSave) }
    $coYes = Wait-Until { Find-Ui -Name 'Saved to your playlists as' -Contains -Type 'Text' } -Seconds 10
    Check "collections ($coTag): Save as playlist makes it and says so" ([bool]$coYes) $(if ($coYes) { $coYes.Current.Name } else { '' })
    $coMade = @(Get-Requests | Where-Object { $_.path -match '^/Playlists$' -and $_.method -eq 'POST' -and $_.status -eq 200 })
    Check "collections ($coTag): one playlist made (a second click makes no copy)" ($coMade.Count -eq 1) "$($coMade.Count) made"
    if ($coWidths -contains 1920) { Shot "$coTag-playlist-saved-1920" }
    Key 0x08   # Back
    Co-Settle 1500
}
else { Check "collections ($coTag): a franchise tile" $false 'none on the Collections page' }

# --- the admin's box set: Also in your library (the Libraries page's Collections tile: the Collections page at the server's) --
Open-Nav 'Libraries'
$coServer = Wait-Until { Find-Ui -Name 'Collections, ' -Contains -Type 'ListItem' } -Seconds 15 -PollMs 200
if ($coServer) {
    Say "Collections tile ($(Invoke-Element $coServer))"
    Co-Settle 1500
    $coF = Co-Focus
    Check "collections ($coTag): the Collections tile opens the page at the server's collections" ([bool]($coF -and $coF.name -like '*, collection*')) "$($coF.type) '$($coF.name)'"
}
$coBox = Wait-Until { Find-Ui -Name 'Kestrel Bay Trilogy, collection' -Contains -OffscreenToo } -Seconds 15
if ($coBox) {
    Say "box set ($(Invoke-Element $coBox))"
    $coAlso = Wait-Until { Find-Ui -Name 'Also in your library' -Contains -Type 'Text' -OffscreenToo } -Seconds 20 -PollMs 200
    Check "collections ($coTag): the admin's box set shows the owned film it leaves out" ([bool]$coAlso) $(if ($coAlso) { $coAlso.Current.Name } else { 'none' })
    Co-Shots 'boxset-also' { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100); Co-Settle 700 }
}
else { Check "collections ($coTag): the admin's box set tile" $false 'none' }

# --- Settings > Collections (a shared page: one theme is enough, CO_SETTINGS=1) ----------------------------------------
if ($env:CO_SETTINGS) {
    Open-Nav 'Settings'
    $coTidy = Wait-Until { Find-Ui -Name 'Tidy collection names' -Type 'Button' -Contains } -Seconds 15
    if (-not $coTidy) { $coTidy = Find-Ui -Name 'Tidy collection names' -Contains -OffscreenToo }
    Check "collections ($coTag): Settings has Tidy collection names" ([bool]$coTidy) ''
    if ($coTidy) { [void][OffscreenUi]::SetFocus($coTidy); Co-Settle 800 }
    Shot "$coTag-settings-collections-1920"
}
