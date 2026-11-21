# 1.1.4 (layout audit, item 22; the states stream's Narrator pass): an empty or failed library shows its message and
# nothing that cannot act on anything: no Sort, direction, Filter, count, A-Z jump, and no pager (there is none since the
# grid scrolls on). Run with -Script wait:1 -Then offscreen\then-library-states.ps1. The fake server's rules make the
# library's items request answer with no items, then fail (500), then answer again.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
$lsItems = '^/(Users/[^/]+/)?Items$'
function Ls-Rules([object[]]$Rules = @()) {
    $list = @($Rules | ForEach-Object { , @([string]$_[0], [string]$_[1]) })
    [void](Invoke-Server '/__harness/latency' 'POST' @{ rules = $list })
}
function Ls-Bar {
    [ordered]@{
        sort = [bool](Find-Ui -Name 'Sort: ' -Type 'Button' -Contains)
        direction = [bool](Find-Ui -Name ', switch to ' -Type 'Button' -Contains)
        filter = [bool](Find-Ui -Name 'Filter (' -Type 'Button' -Contains)
        letters = [bool](Find-Ui -Name 'Jump to a letter' -Type 'List')
        pager = [bool]((Find-Ui -Name 'Next page' -Type 'Button') -or (Find-Ui -Name 'Page' -Type 'ComboBox' -Contains))
    }
}
function Ls-Describe($bar) { ($bar.Keys | ForEach-Object { "$_=$($bar[$_])" }) -join ' ' }

# --- empty
Ls-Rules @(, @($lsItems, 'empty'))
Open-Library $fixture.movie.library
$emptyMsg = Wait-Ui -Name 'library is empty' -Type 'Text' -Contains -Seconds 20
Start-Sleep -Milliseconds 800
$bar = Ls-Bar
Check 'library states: an empty library says so' ([bool]$emptyMsg)
Check 'library states: ... with no Sort, direction, Filter, A-Z or pager' (-not ($bar.sort -or $bar.direction -or $bar.filter -or $bar.letters -or $bar.pager)) (Ls-Describe $bar)
Shot 'library-state-empty'

# --- failed (Refresh on the empty one asks again)
Ls-Rules @(, @($lsItems, 'status:500'))
$refresh = Find-Ui -Name 'Refresh' -Type 'Button'
if ($refresh) { [void](Invoke-Element $refresh) }
$failMsg = Wait-Ui -Name "Couldn't load" -Type 'Text' -Contains -Seconds 20
Start-Sleep -Milliseconds 800
$bar = Ls-Bar
Check 'library states: a failed library says so' ([bool]$failMsg)
Check 'library states: ... with no Sort, direction, Filter, A-Z or pager' (-not ($bar.sort -or $bar.direction -or $bar.filter -or $bar.letters -or $bar.pager)) (Ls-Describe $bar)
Shot 'library-state-failed'

# --- Try again: the titles, and the bar back
Ls-Rules @()
$retry = Find-Ui -Name 'Try again' -Type 'Button'
if ($retry) { [void](Invoke-Element $retry) }
[void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 20 -PollMs 200)
Start-Sleep -Milliseconds 1200
$bar = Ls-Bar
# (the direction is in the Sort menu since 1.2.0, the button review's L24: no button of its own)
Check 'library states: Try again: the titles, and Sort, Filter and A-Z are back (no pager)' ($bar.sort -and $bar.filter -and $bar.letters -and -not $bar.pager) (Ls-Describe $bar)
Shot 'library-state-loaded'
