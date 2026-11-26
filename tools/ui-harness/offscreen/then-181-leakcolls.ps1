# Nixie 1.8.1 (the back stream's review): the Collections page is kept whole for Back (87b11835). Back to it must not keep the
# collections opened from it alive, nor make a second Collections page. Run per theme with the app counting its live pages
# after each navigation (NIXIE_HARNESS_LEAKS=1: a full collection, then "[leak] live pages: ..." in its log; a release build
# never reads it):
#   $env:NIXIE_HARNESS_LEAKS='1'; $env:FAKESERVER_COLLECTIONS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|...>'
#   [$env:LK_ROUNDS='30'] [$env:LK_MOST='2'] [$env:LK_MB_ROUND='']
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-181-leakcolls.ps1
# The Collections page is opened, then LK_ROUNDS times: the server's third collection opened from it (its tile, Enter), Back.
# Then the app's last "[leak]" line: Collections pages alive (exactly 1: the one kept whole) and collection pages (at most
# LK_MOST). The app's private bytes after round 5 and at the end are said too (MB a round); LK_MB_ROUND bounds them when set
# (the mem stream's own check bounds private bytes: BH-175-M2). Uses then-174-back.ps1's helpers (BK_PAGES=none: none of its
# cases run). Rows in <Out>\leak181\leak181.json.

$env:BK_PAGES = 'none'
. (Join-Path $PSScriptRoot 'then-174-back.ps1')
$lcRounds = if ($env:LK_ROUNDS) { [int]$env:LK_ROUNDS } else { 30 }
$lcMost = if ($env:LK_MOST) { [int]$env:LK_MOST } else { 2 }
$lcMbRound = if ($env:LK_MB_ROUND) { [double]$env:LK_MB_ROUND } else { $null }
$lcDir = Join-Path $Out 'leak181'
New-Item -ItemType Directory -Force $lcDir | Out-Null

function Lc-LastLine {
    $file = Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return $null }
    $hit = Select-String -LiteralPath $file.FullName -Pattern '\[leak\] live pages: (.*)$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($hit) { $hit.Matches[0].Groups[1].Value.Trim() } else { $null }
}
# a page type's count in the line ("CollectionsPage 1, CollectionPage 2"): the whole name, so CollectionPage is not CollectionsPage
function Lc-Count([string]$Line, [string]$Page) {
    $n = 0
    foreach ($part in @("$Line" -split ',\s*')) { if ($part -match "^$Page\s+(\d+)$") { $n += [int]$Matches[1] } }
    $n
}
function Lc-PrivateMb { try { [Math]::Round((Get-Process -Id ([int]$script:ui.Id) -ErrorAction Stop).PrivateMemorySize64 / 1MB, 1) } catch { $null } }
function Lc-Tiles { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($bkA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Cached.Name -like '*, collection, *' }) }

$lcDone = 0
$lcMb5 = $null
try {
    Open-Nav 'Collections'
    if (-not (Wait-Until { if ((Lc-Tiles).Count -ge 3) { $true } } -Seconds 30 -PollMs 300)) { throw 'no collection tiles on the Collections page' }
    Start-Sleep -Milliseconds 1200
    for ($i = 1; $i -le $lcRounds; $i++) {
        $tiles = @(Lc-Tiles)
        if ($tiles.Count -lt 3) { throw "round ${i}: the Collections page shows $($tiles.Count) collection tiles" }
        Bk-Take $tiles[2]
        Key 0x0D
        if (-not (Wait-Ui -Id 'OrderButton' -Seconds 20)) { throw "round ${i}: the collection did not open" }
        Start-Sleep -Milliseconds 700
        Key 0xA6
        if (-not (Wait-Until { if (-not (Find-Ui -Id 'OrderButton') -and (Lc-Tiles).Count -ge 3) { $true } } -Seconds 20 -PollMs 200)) { throw "round ${i}: Back did not show the Collections page" }
        Start-Sleep -Milliseconds 500
        $lcDone = $i
        if ($i -eq 5) { $lcMb5 = Lc-PrivateMb }
        if ($i % 10 -eq 0) { Say ("leak181 ({0}): {1} rounds, {2}; private {3} MB" -f $bkTag, $i, (Lc-LastLine), (Lc-PrivateMb)) }
    }
} catch {
    Say "leak181 ($bkTag): stopped: $($_.Exception.Message)"
}
$lcLine = Lc-LastLine
$lcPages = Lc-Count $lcLine 'CollectionsPage'
$lcSets = Lc-Count $lcLine 'CollectionPage'
$lcMbEnd = Lc-PrivateMb
$lcPerRound = if ($lcMb5 -ne $null -and $lcMbEnd -ne $null -and $lcDone -gt 5) { [Math]::Round(($lcMbEnd - $lcMb5) / ($lcDone - 5), 2) } else { $null }
$lcMbOk = ($lcMbRound -eq $null) -or ($lcPerRound -ne $null -and $lcPerRound -le $lcMbRound)
$lcOk = [bool]($lcDone -eq $lcRounds -and $lcLine -and $lcPages -eq 1 -and $lcSets -le $lcMost -and $lcMbOk)
$lcDetail = "{0} of {1} rounds; Collections pages alive {2} (the one kept whole: 1), collection pages {3} (at most {4}); private bytes {5} MB after round 5, {6} MB at the end ({7} MB a round{8}); the app's last line: {9}" -f
    $lcDone, $lcRounds, $lcPages, $lcSets, $lcMost, $lcMb5, $lcMbEnd, $lcPerRound, $(if ($lcMbRound -ne $null) { ", at most $lcMbRound" } else { ', not bounded here' }), $(if ($lcLine) { $lcLine } else { 'none (NIXIE_HARNESS_LEAKS=1?)' })
Check "leak181 ($bkTag): Collections <-> a collection" $lcOk $lcDetail
$lcRow = [pscustomobject][ordered]@{ theme = $bkTheme; rounds = $lcDone; collections_pages = $lcPages; collection_pages = $lcSets; private_mb_round5 = $lcMb5; private_mb_end = $lcMbEnd; mb_per_round = $lcPerRound; line = $lcLine; ok = $lcOk }
[IO.File]::WriteAllText((Join-Path $lcDir 'leak181.json'), (ConvertTo-Json -InputObject @($lcRow) -Depth 3), [Text.UTF8Encoding]::new($false))
