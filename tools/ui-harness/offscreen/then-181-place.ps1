# Nixie 1.8.1 (the mem review): a Collection page's cards say their place to Narrator ("2 of 24"), on the first visit and after
# a film opened from it and Back. Since 1.6 the page set the place on the card when its ItemsRepeater prepared the entry, before
# the theme's PosterTemplate had made the card inside it: cards 2 to 24 said none (after Back, all but 1, 23 and 24). Run per
# theme with FAKESERVER_COLLECTIONS=1 and FAKESERVER_LONG_TITLE=1 ("Anthology of Twenty-Four", numbered in its order):
#   $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-181-place.ps1
# Each card's place is read through UI Automation (PositionInSet, SizeOfSet), as Narrator reads it, and matched with the number
# its name leads with ("2, Sleeping Kestrel Bay, 1983": 2 of 24). Check names: "<GATE_ID, else place181> (<theme>): ...".
# Dot-sourced by drive.ps1: every name here starts with pl / Pl-.

$env:BK_PAGES = 'none'
. (Join-Path $PSScriptRoot 'then-174-back.ps1')   # its helpers only (Bk-OpenCollection, Bk-Cards, Bk-FocusCard, Bk-Focus)
$plId = if ($env:GATE_ID) { $env:GATE_ID } else { 'place181' }
$plCount = if ($env:PL_COUNT) { [int]$env:PL_COUNT } else { 24 }

# every card's place against the number its name leads with: how many were read, and the ones that say another place
function Pl-Places {
    $rows = New-Object System.Collections.Generic.List[string]
    $wrong = New-Object System.Collections.Generic.List[string]
    foreach ($c in @(Bk-Cards)) {
        try {
            $name = $c.Current.Name
            $n = if ($name -match '^(\d+), ') { [int]$Matches[1] } else { 0 }
            $pos = [int]$c.GetCurrentPropertyValue($bkIds::PositionInSetProperty)
            $size = [int]$c.GetCurrentPropertyValue($bkIds::SizeOfSetProperty)
            $rows.Add("$n=$pos/$size")
            if ($n -le 0 -or $pos -ne $n -or $size -ne $plCount) { $wrong.Add("$n=$pos/$size") }
        }
        catch { }
    }
    [pscustomobject]@{ Read = $rows.Count; Wrong = @($wrong); Rows = ($rows -join ' ') }
}
function Pl-Said($Places) { "{0} cards read; wrong: {1}; all: {2}" -f $Places.Read, $(if ($Places.Wrong.Count) { $Places.Wrong -join ' ' } else { 'none' }), $Places.Rows }

$plFirst = $null; $plStart = $null; $plAfter = $null; $plAgain = $null; $plWhy = ''
try {
    Bk-OpenCollection 'Collections' $bkLongSet
    $plFirst = Pl-Places
    Say "${plId} ($bkTag): first visit: $(Pl-Said $plFirst)"
    $plStart = Bk-FocusCard 2
    Say "${plId} ($bkTag): the keyboard on $(Bk-Describe $plStart)"
    Bk-OpenFocused
    if (-not (Bk-WaitGone $plStart)) { throw "the card '$($plStart.Name)' did not open" }
    Start-Sleep -Milliseconds 2500
    Key 0xA6   # Back
    if (-not (Wait-Until { if ((Bk-Cards).Count) { $true } } -Seconds 20 -PollMs 200)) { throw 'Back did not show the collection' }
    Start-Sleep -Milliseconds 1500
    $plAfter = Pl-Places
    Say "${plId} ($bkTag): after Back: $(Pl-Said $plAfter)"
    $plAgain = Bk-FocusCard 2
}
catch {
    $plWhy = $_.Exception.Message
    Say "${plId} ($bkTag): stopped: $plWhy"
    Shot "place181-$bkTag-stopped"
}
Check "${plId} ($bkTag): every card of the collection says its place, first visit" ([bool]($plFirst -and $plFirst.Read -ge 2 -and $plFirst.Wrong.Count -eq 0)) $(if ($plFirst) { Pl-Said $plFirst } else { $plWhy })
Check "${plId} ($bkTag): the card given the keyboard says 2 of $plCount" ([bool]($plStart -and $plStart.Pos -eq 2 -and $plStart.Size -eq $plCount)) (Bk-Describe $plStart)
Check "${plId} ($bkTag): after a film and Back, every card says its place" ([bool]($plAfter -and $plAfter.Read -ge 2 -and $plAfter.Wrong.Count -eq 0)) $(if ($plAfter) { Pl-Said $plAfter } else { $plWhy })
Check "${plId} ($bkTag): after Back, the card given the keyboard says 2 of $plCount" ([bool]($plAgain -and $plAgain.Pos -eq 2 -and $plAgain.Size -eq $plCount)) (Bk-Describe $plAgain)
