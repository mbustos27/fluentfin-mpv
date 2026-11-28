# Nixie 1.8.1 (BH-175-q4, BH-175-m3), the basics gate, class 8 (lifecycle): a library change on the server, read at the next
# start. The fake server honours MinDateLastSaved now (it answered every title, so no harness run took the index's read of a
# change): a film added to Movies is read as the titles saved since the last read (a few requests, not every title), and the
# index has it; a film removed from Movies while one is added to a library the index skips (a mixed library: the server's
# count moves by 0) is read as every title, and the removed film is out of the index (1.8.0 kept it in Made by Nixie, which
# is made of the index, until the day's read).
# Nixie 1.9 (the index's load on a server that isn't the user's, about 190,000 titles): a read of every title at most once a
# day. Within the day the removal is read as what changed too (no page of every title): the removed film stays, the index
# says its removals are due ("RemovalsDue":true); part day: the index and its notes dated a day back, the next start reads
# every title, and the removed film goes. Part marks: a film played on another device (POST /__harness/played) is played in
# the index at the next start, with no page of every title.
#   run-gate.ps1 ... -Checks index-change      (manifest\m181.psd1)
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then tools\gate\checks\8-index-change.ps1
# Each part: Nixie closed, the saved index and its note of the last read dated two hours back (a change is read at most once
# an hour after a read), the library changed on the fake server (POST /__harness/titles), Nixie started again (signed in:
# Remember me), then what its log and the saved index say. G4_PARTS picks parts: added, marks, removed-mixed, day. Dot-sourced by
# drive.ps1: names start with g4x / G4X- (g4-common.ps1: g4 / G4-).

$script:g4DefaultId = 'index-change'
. (Join-Path $PSScriptRoot 'g4-common.ps1')

function G4X-Folder { $d = Get-ChildItem -LiteralPath $DataDir -Recurse -Directory -Filter 'LibraryIndex' -ErrorAction SilentlyContinue | Select-Object -First 1; if ($d) { $d.FullName } }
# the saved index (the one file of the signed-in user)
function G4X-Index { $f = G4X-Folder; if ($f) { Get-ChildItem -LiteralPath $f -Filter '*.json' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 } }
function G4X-IndexText { $f = G4X-Index; if ($f) { try { [IO.File]::ReadAllText($f.FullName) } catch { '' } } else { '' } }
# the saved index and its note of the last read dated $Hours back: how many files were changed (1.9: and when the last read of
# every title began, and the titles were last brought up to date)
function G4X-DateBack([double]$Hours) {
    $folder = G4X-Folder
    if (-not $folder) { return 0 }
    $when = [DateTimeOffset]::UtcNow.AddHours(-$Hours).ToString('o')
    $n = 0
    foreach ($f in @(Get-ChildItem -LiteralPath $folder -Filter '*.json' -File) + @(Get-ChildItem -LiteralPath (Join-Path $folder 'Reads') -Filter '*.json' -File -ErrorAction SilentlyContinue)) {
        $t = [IO.File]::ReadAllText($f.FullName)
        $r = [regex]::Replace($t, '"(ReadAt|LastRead|LastWhole|TitlesAt)":"[^"]+"', { param($m) '"' + $m.Groups[1].Value + '":"' + $when + '"' })
        if ($r -ne $t) { [IO.File]::WriteAllText($f.FullName, $r, [Text.UTF8Encoding]::new($false)); $n++ }
    }
    $n
}
# closed, dated back, changed on the server ($Change: POST /__harness/titles's body), started again on Home
function G4X-Restart([string]$Label, $Change, [double]$Hours = 2) {
    Step-Close "$(G4-Id) $Label"
    $dated = G4X-DateBack $Hours
    $answer = Invoke-Server '/__harness/titles' 'POST' $Change
    Say "$(G4-Id) [$Label]: $dated index files dated two hours back; the server added $(@($answer.added).Count) and removed $(@($answer.removed).Count)"
    $script:navPane = $null
    $at = Get-Epoch
    Start-App "g4x_$($Label -replace '[^A-Za-z0-9]+', '_')_window_ms"
    $page = G4-WaitPage
    if ($page -eq 'home') { $script:navPane = G4-Shell; $script:page = 'home' }
    [pscustomobject]@{ Since = $at; Page = $page; Dated = $dated; Answer = $answer }
}
# the app's line on how it read the change, since $Since
function G4X-ChangeRead([double]$Since) { Wait-Until { @(G4-Log $Since 'Library index: a library change read as') | Select-Object -Last 1 } -Seconds 60 -PollMs 500 }
function G4X-SavedSinceAsked([double]$Since) { @(Get-Requests $Since | Where-Object { $_.path -match '(?i)^/Items$' -and "$($_.query)" -match '(?i)minDateLastSaved' }) }
# 1.9: the index's pages of every title since $Since (its fields, no MinDateLastSaved, no ids)
function G4X-WholePages([double]$Since) { @(Get-Requests $Since | Where-Object { $_.path -match '(?i)^/Items$' -and "$($_.query.fields)" -match 'ProductionLocations' -and -not $_.query.mindatelastsaved -and -not $_.query.ids }) }

G4-SignInRemembered
# the index's first read, saved (Home's Made by Nixie rows ask for it), and its people step over
$g4xIn = Wait-Until { if ((G4X-IndexText) -match '"Entries":\[\{') { $true } } -Seconds 90 -PollMs 500
if (-not $g4xIn) { throw 'the library index was never saved' }
[void](Wait-Until { @(G4-Log 0 'Library index: the people pass read') | Select-Object -First 1 } -Seconds 60 -PollMs 500)
Start-Sleep -Seconds 3
$g4xMovies = if ((G4X-IndexText) -match '\{"Id":"(?<id>[0-9a-f-]{36})","Name":"Movies","Kind":0\}') { $Matches['id'] } else { $null }
if (-not $g4xMovies) { throw 'no Movies library in the saved index' }

G4-Part 'added' {
    $label = 'a film added to Movies'
    $r = G4X-Restart $label @{ add = @(@{ name = 'Harness Film Added'; library = 'Movies' }) }
    if ($r.Page -ne 'home') { throw "started again: no Home (page $($r.Page))" }
    $line = G4X-ChangeRead $r.Since
    $asked = @(G4X-SavedSinceAsked $r.Since)
    G4-Check $label 'read as the titles saved since the last read, not every title' ($r.Dated -ge 2 -and $line -match 'read as the titles saved since the last read' -and $asked.Count -ge 1) "$(if ($line) { ($line -split '\] ', 2)[-1] } else { 'no change read in 60 s' }); $($asked.Count) request(s) of the titles saved since"
    # the server answered only what it saved since (the fake answered every title before 1.8.1: "1 added and 468 changed")
    $count = @(G4-Log $r.Since 'Library index: \d+ titles added and \d+ changed since the last read') | Select-Object -Last 1
    $one = "$count" -match 'Library index: 1 titles added and 0 changed since the last read'
    G4-Check $label 'the server answered only the titles it saved since: the one added' $one "$(if ($count) { ($count -split '\] ', 2)[-1] } else { 'no such line' })"
    $has = Wait-Until { if ((G4X-IndexText) -match '"Name":"Harness Film Added"') { $true } } -Seconds 30 -PollMs 500
    G4-Check $label 'the index has the film' ([bool]$has) "$(@($r.Answer.added).Count) added on the server"
}

G4-Part 'marks' {
    # 1.9 (the coordinator's review): a film played on another device shows at the next start (the user's marks: played
    # lately, latest first, and the favourites), with no page of every title
    $label = 'a film played on another device'
    $film = [regex]::Matches((G4X-IndexText), '\{"Id":"(?<id>[0-9a-f-]{36})","Name":"(?<name>[^"]+)","SortName":"[^"]*","Kind":0,"LibraryId":"' + $g4xMovies + '"[^{}]*?"Played":false') | Select-Object -First 1
    if (-not $film) { throw 'no unwatched film of Movies in the saved index' }
    $id = $film.Groups['id'].Value
    Step-Close "$(G4-Id) $label"
    $answer = Invoke-Server '/__harness/played' 'POST' @{ id = $id; played = $true }
    $script:navPane = $null
    $at = Get-Epoch
    Start-App 'g4x_a_film_played_on_another_device_window_ms'
    $page = G4-WaitPage
    if ($page -eq 'home') { $script:navPane = G4-Shell; $script:page = 'home' }
    if ($page -ne 'home') { throw "started again: no Home (page $page)" }
    $line = Wait-Until { @(G4-Log $at 'Library index: marks since the last read: ') | Select-Object -Last 1 } -Seconds 60 -PollMs 500
    $played = Wait-Until { if ((G4X-IndexText) -match ('\{"Id":"' + $id + '"[^{}]*?"Played":true')) { $true } } -Seconds 30 -PollMs 500
    $whole = @(G4X-WholePages $at)
    G4-Check $label 'the index has it played at the next start, with no page of every title' ($null -ne $answer -and "$line" -match '1 films and \d+ shows played' -and [bool]$played -and $whole.Count -eq 0) "$(if ($line) { ($line -split '\] ', 2)[-1] } else { 'no marks read in 60 s' }); $($whole.Count) page(s) of every title"
    [void](Invoke-Server '/__harness/played' 'POST' @{ id = $id; played = $false }) # put back for the parts after
}

G4-Part 'removed-mixed' {
    $label = 'a film removed from Movies while one is added to a library the index skips'
    $text = G4X-IndexText
    $gone = [regex]::Matches($text, '\{"Id":"(?<id>[0-9a-f-]{36})","Name":"(?<name>[A-Za-z ]+)","SortName":"[^"]*","Kind":0,"LibraryId":"' + $g4xMovies + '"') |
        Where-Object { $_.Groups['name'].Value -ne $fixture.movie.name } | Select-Object -First 1
    if (-not $gone) { throw 'no film of Movies in the saved index to remove' }
    $goneId = $gone.Groups['id'].Value
    $r = G4X-Restart $label @{ remove = @($gone.Groups['name'].Value); add = @(@{ name = 'Harness Mixed Film'; library = 'Harness Mixed'; collection = $null }) }
    if ($r.Page -ne 'home') { throw "started again: no Home (page $($r.Page))" }
    $line = G4X-ChangeRead $r.Since
    # 1.8.1 (the review): each indexed library's own count says it: Movies moved by -1 with nothing added there. 1.9: within
    # the day that is read as what changed, not every title (about 190 requests on a server of 190,000), and said
    $said = Wait-Until { @(G4-Log $r.Since 'Library index: titles were removed \(') | Select-Object -Last 1 } -Seconds 30 -PollMs 500
    $whole = @(G4X-WholePages $r.Since)
    G4-Check $label 'within the day the change the count hides is read as what changed, not every title' ($r.Dated -ge 2 -and @($r.Answer.removed).Count -eq 1 -and $line -match 'read as the titles saved since the last read' -and "$said" -match '0 titles new to it since the last read, but its count moved by -1' -and $whole.Count -eq 0) "$(if ($line) { ($line -split '\] ', 2)[-1] } else { 'no change read in 60 s' }); $(if ($said) { ($said -split '\] ', 2)[-1] } else { 'no removal said' }); $($whole.Count) page(s) of every title"
    $due = Wait-Until { $t = G4X-IndexText; if ($t -match '"RemovalsDue":true') { $true } } -Seconds 30 -PollMs 500
    G4-Check $label 'the index says its removals are due (never taken as complete), the removed film still in it until the day''s read' ([bool]$due -and (G4X-IndexText) -match ('"Id":"' + $goneId + '"')) "'$($gone.Groups['name'].Value)'"
    $mixed = (G4X-IndexText) -match '"Name":"Harness Mixed Film"'
    G4-Check $label 'and the mixed library''s film is not in it (the index skips that library)' (-not $mixed) ''
    $script:g4xGoneId = $goneId
    $script:g4xGoneName = $gone.Groups['name'].Value
}

G4-Part 'day' {
    # 1.9: a day after the last read of every title, the next start reads every title once: the removed film goes
    $label = 'a start a day after the last read of every title'
    if (-not $script:g4xGoneId) { throw 'part day needs part removed-mixed first' }
    $r = G4X-Restart $label @{} 25
    if ($r.Page -ne 'home') { throw "started again: no Home (page $($r.Page))" }
    $out = Wait-Until { $t = G4X-IndexText; if ($t -match '"Entries":\[' -and $t -notmatch ('"Id":"' + $script:g4xGoneId + '"')) { $true } } -Seconds 120 -PollMs 500
    $whole = @(G4X-WholePages $r.Since)
    G4-Check $label 'the day''s read of every title takes the removed film out (Made by Nixie is made of the index)' ([bool]$out -and (G4X-IndexText) -notmatch '"RemovalsDue":true' -and $whole.Count -gt 0) "'$script:g4xGoneName'; $($whole.Count) page(s) of every title"
}
