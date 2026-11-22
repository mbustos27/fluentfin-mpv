# Nixie 1.6 skip: the live check on a local QA Jellyfin server (never the owner's), muted. A -Then script of
# tools\ui-harness\run-offscreen.ps1:
#   run-offscreen.ps1 -QaServer 12 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script series `
#       -Then tools\qa-servers\then-qa-skip.ps1 -NoA11y
# The seeded show's episodes are 60 s test patterns with silent audio (tools\qa-servers\media.py). On the first one (paused at
# the start by the play step) the 3 key skips to 0:18; on the second the same: the real backend, against the real server's
# items, asks "Skip this every episode?"; Yes makes the rule; on the third the 1 key lands in the rule's part and its pill
# shows. Checked too: nothing is asked of the server beyond what playback asks (no request the fake server would not see).
# Dot-sourced by drive.ps1; names start with SQ (and skip-common.ps1's SK).

$skTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Default' }
$skLook = 'Dark'
. (Join-Path $PSScriptRoot '..\ui-harness\offscreen\skip-common.ps1')

$sqAt = Get-Epoch
if ($script:page -ne 'player') {
    # the series page opens on the season up next (the show's Specials here): its "Season 1" tube first
    $sqTube = Wait-Until { Find-Ui -Name $fixture.season.name -Type 'Button' } -Seconds 10 -PollMs 300
    if ($sqTube) { [void](Invoke-Element $sqTube); Start-Sleep -Seconds 2 }
    $sqRow = Wait-Until { Find-EpisodeRow } -Seconds 20 -PollMs 300
    Check "qa skip: the first episode's row is on the series page" ([bool]$sqRow) "$($fixture.episode.name)"
    if ($sqRow) { [void](Invoke-Element $sqRow); $script:page = 'player' }
    $sqPlaying = Wait-Until { Find-Ui -Id 'SeekBar' -OffscreenToo } -Seconds 40 -PollMs 250
    Check 'qa skip: the episode plays' ([bool]$sqPlaying)
    Start-Sleep -Seconds 3
}
[void](SK-Press '3'); Start-Sleep -Seconds 5
Check 'qa skip: the next episode starts (media next)' (SK-Go 'MediaNext')
[void](SK-Press '3')
$sqToast = Wait-Until { $n = SK-ToastName; if ($n -like 'Skip this every episode?*') { $n } } -Seconds 15 -PollMs 250
Check 'qa skip: the same skip in two episodes asks "Skip this every episode?"' ([bool]$sqToast) "$sqToast"
if ($sqToast) {
    SK-Shot 'qa-toast' 1920
    $sqYes = Find-Ui -Id 'SkipSuggestionYes'
    if ($sqYes) { [void](Invoke-Element $sqYes) }
    $sqSaid = Wait-Until { $t = Find-Ui -Id 'ToastLabel' -OffscreenToo; if ($t -and $t.Current.Name -eq 'Skipping it in every episode') { $true } } -Seconds 4 -PollMs 100
    Check 'qa skip: Yes saves the rule' ([bool]$sqSaid)
    Check 'qa skip: on to the third episode' (SK-Go 'MediaNext')
    [void](SK-Press '1')
    $sqPill = Wait-Until { $p = Find-Ui -Id 'SkipSegmentButton'; if ($p -and $p.Current.Name -like 'Skip*') { $p.Current.Name } } -Seconds 8 -PollMs 200
    Check 'qa skip: the third episode offers the rule''s skip' ([bool]$sqPill) "$sqPill"
    if ($sqPill) { SK-Shot 'qa-pill' 1920 }
}
# the server saw only what playback asks for: PlaybackInfo, streams, reports, images, segments; no new kind of request
$sqRoutes = @(Get-Requests $sqAt | ForEach-Object { $_.route } | Sort-Object -Unique)
Say "qa skip: the server's routes during the check: $($sqRoutes -join ', ')"
Step-Leave
