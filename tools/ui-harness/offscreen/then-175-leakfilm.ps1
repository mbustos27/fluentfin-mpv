# Nixie 1.7.5 (BH-175-M1): Back to a film page no longer keeps the old film page alive. A film, one of its More like this
# films, Back, FL_ROUNDS times (20); the app's own "[leak] live pages" count (NIXIE_HARNESS_LEAKS=1, taken after a full
# collection at each navigation) of the theme's film page at rounds 5 and 20 must be the same, and Nixie still running.
# On 1.7.5's RC the count grew one a round (Classic MoviePage 7, 12, 22 at rounds 5, 10, 20; Drive-in the same). Run with:
#   $env:NIXIE_HARNESS_LEAKS='1'; $env:NIXIE_LAYOUT='<Classic|DriveIn|...>'
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-175-leakfilm.ps1
# Check names follow the basics gate's pattern: 'leak-film: ...'. Dot-sourced by drive.ps1: every name here starts with fl.

$flRounds = if ($env:FL_ROUNDS) { [int]$env:FL_ROUNDS } else { 20 }
$flTag = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT.ToLowerInvariant() } else { 'classic' }
$env:BK_PAGES = 'none'
. (Join-Path $PSScriptRoot 'then-174-back.ps1')   # its helpers only (Bk-OnFilm, Bk-Cards)

$flMe = Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^Nixie\.exe$' -and $_.CommandLine -like "*$DataDir*" } | Select-Object -First 1
function Fl-Priv { try { $p = Get-Process -Id ([int]$flMe.ProcessId); $p.Refresh(); [math]::Round($p.PrivateMemorySize64 / 1MB) } catch { -1 } }
function Fl-Live {
    $file = Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return '' }
    $hit = Select-String -LiteralPath $file.FullName -Pattern '\[leak\] live pages: (.*)$' -ErrorAction SilentlyContinue | Select-Object -Last 1
    if ($hit) { $hit.Matches[0].Groups[1].Value.Trim() } else { '' }
}
# the film pages alive (MoviePage, DriveInMoviePage, CinematicMoviePage, EditorialMoviePage)
function Fl-Films([string]$Live) { $n = 0; foreach ($m in [regex]::Matches($Live, '\b\w*MoviePage (\d+)')) { $n += [int]$m.Groups[1].Value }; $n }

$flAt = @{}
try {
    Open-Library $fixture.movie.library
    Open-Card $fixture.movie.name
    if (-not (Wait-Until { Bk-OnFilm $fixture.movie.name } -Seconds 20 -PollMs 200)) { throw 'no film page' }
    Start-Sleep -Milliseconds 2500
    for ($flI = 1; $flI -le $flRounds; $flI++) {
        $flCard = @(Bk-Cards @('BaseItemDtoCard', 'EditorialPoster', 'EditorialStill')) | Select-Object -First 1
        if (-not $flCard) { throw "round ${flI}: no More like this card" }
        [void](Invoke-Element $flCard)
        if (-not (Wait-Until { -not (Bk-OnFilm $fixture.movie.name) } -Seconds 15 -PollMs 150)) { throw "round ${flI}: the similar film did not open" }
        Start-Sleep -Milliseconds 900
        Key 0xA6   # Back
        if (-not (Wait-Until { Bk-OnFilm $fixture.movie.name } -Seconds 20 -PollMs 150)) { throw "round ${flI}: Back did not show the film" }
        Start-Sleep -Milliseconds 900
        if ($flI % 5 -eq 0) {
            $flLive = Fl-Live
            $flAt[$flI] = Fl-Films $flLive
            Say "leak-film ($flTag): round $flI; private $(Fl-Priv) MB; film pages $($flAt[$flI]); live: $flLive"
        }
    }
    Shot "leakfilm-$flTag"
}
catch {
    Say "leak-film ($flTag): stopped: $($_.Exception.Message)"
    Shot "leakfilm-$flTag-stopped"
}
$script:ui.Refresh()
Check "leak-film: ($flTag) Nixie still running after $flRounds rounds" (-not $script:ui.HasExited)
$flFirst = $flAt[5]; $flLast = $flAt[$flRounds]
Check "leak-film: ($flTag) the film pages alive after round $flRounds are round 5's (none kept by Back)" ($null -ne $flFirst -and $null -ne $flLast -and $flLast -le $flFirst) "round 5: $flFirst; round ${flRounds}: $flLast"
