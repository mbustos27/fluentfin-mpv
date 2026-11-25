# 1.8 (P1-3, stream s): Made by Nixie's census line at real shape. Run with FAKESERVER_BIG_LIBRARY=25000 (and
# FAKESERVER_COLLECTIONS=1) and `-Script home -NoA11y -Then offscreen\then-180-census.ps1`: it opens the Collections page and
# waits (up to CN180_WAIT seconds, 600) for the census line in Nixie's log (the pages' build of a whole index says it once),
# then says it: each kind's candidates/floor/cap. The review's Major 2: the Spotlight isn't in that build (Home's first Nixie
# row waits on it); it is made after the first rows (SmartCollectionCatalog.BuildLaterAsync), and says its own line, with
# its gates and its time, once a page asks for it (stream l's This week and Home). Counts and kinds only, never a title.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names (cn180 prefix).
$cn180Wait = if ($env:CN180_WAIT) { [int]$env:CN180_WAIT } else { 600 }
$cn180Logs = Join-Path $DataDir 'Logs'
function CN180-Line([string]$Pattern) {
    if (-not (Test-Path $cn180Logs)) { return $null }
    Get-ChildItem $cn180Logs -File | Sort-Object LastWriteTime -Descending | ForEach-Object {
        Select-String -Path $_.FullName -Pattern $Pattern -SimpleMatch | Select-Object -Last 1
    } | Select-Object -First 1
}
$cn180Started = Get-Date
Open-Nav 'Collections'
$cn180Line = Wait-Until { CN180-Line 'Made by Nixie census:' } -Seconds $cn180Wait -PollMs 2000
$cn180Took = [int]((Get-Date) - $cn180Started).TotalSeconds
if ($cn180Line) {
    $cn180Text = $cn180Line.Line
    Say "census ($cn180Took s after Collections opened): $($cn180Text.Substring($cn180Text.IndexOf('Made by Nixie census:')))"
    Check 'census: the line came, and the Spotlight is not in the build Home''s first row waits on' ($cn180Text -notmatch 'Spotlight') $cn180Text
}
else {
    Check 'census: the line came' $false "none in $cn180Wait s in $cn180Logs"
}

$cn180Later = CN180-Line 'Made by Nixie, after the first rows:'
if ($cn180Later) {
    Say "after the first rows: $($cn180Later.Line.Substring($cn180Later.Line.IndexOf('Made by Nixie, after')))"
    Check 'census: the Spotlight''s own line says its gates and its time' ($cn180Later.Line -match 'Spotlight \d+/\d+/\d+ \[\d{4}-W\d\d: director \d+>\d+>\d+>\d+, actor \d+>\d+>\d+>\d+, studio \d+>\d+>\d+>\d+(, people not all in)?; (a|no) pick in \d+ ms\] in \d+ ms') $cn180Later.Line
}
else {
    Say 'after the first rows: no page asked for the Spotlight in this build (stream l places it)'
}
Shot 'census-collections'
