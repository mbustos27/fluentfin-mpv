# Nixie 1.6 skip: skipping repeated extras, end to end against the fake server and the real backend, off-screen and silent.
#   NIXIE_LAYOUT=<Classic|DriveIn|Cinematic|Editorial> [NIXIE_THEME=Light] [SKIP_SIZES=1920,1100]
#   run-offscreen.ps1 ... -Script season,play -Then offscreen\then-160-skip.ps1
# Dot-sourced by drive.ps1 after the play step (FluentFin's controls; paused at the start, the bars up). The test video is the
# fake server's generated one (a test pattern, digital silence), the same file for every episode; the server marks an intro at
# 0:10-0:40. Every name here starts with SK.
#   1. Episode 1: the viewer seeks 0:00 -> 1:00 (the 1 key), then 1:00 -> 1:30 (Shift+Right), five seconds apart (two skips).
#   2. Episode 2 (the Next media key): the same two seeks. The backend asks once per skip (skip_suggestion); the second question
#      takes the toast's place. Checked: the toast shows with the span and the show, Tab reaches its Yes first, Esc is Not now.
#   3. Episode 1 again, then 2, the same seeks: the toast again; its Yes makes the rule ("Skipping it in every episode").
#   4. Episode 3: the 1 key lands in the rule's part: the pill says "Skip extra". S skips to its end (about 1:30).
#   5. Shift+S: the mark sheet, pre-filled from the last forward seek; Esc closes it.
#   6. Settings > Playback > Skip rules: the rule is listed.
# Shots (<Out>\shots): 160-skip-<theme>-<Dark|Light>-<what>-<width>.png. SKIP_SIZES (default 1920,1100 in Dark, 1920 in Light).

$skTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Default' }
$skLook = if ($env:NIXIE_THEME) { $env:NIXIE_THEME } else { 'Dark' }
$skSizes = if ($env:SKIP_SIZES) { @($env:SKIP_SIZES -split ',' | ForEach-Object { [int]$_ }) } elseif ($skLook -eq 'Light') { @(1920) } else { @(1920, 1100) }

# the keys (Shift held as the keys matrix holds it) and the helpers: skip-common.ps1
. (Join-Path $PSScriptRoot 'skip-common.ps1')

Say "skip ($skTheme, $skLook): sizes $($skSizes -join ', ')"
SK-Size $skSizes[0]
# SKIP_SETTINGS_ONLY=1 (run with -Script home): only Settings' skip section (the player is dark in both app themes, so a
# Light run needs only the page that changes with the theme)
$skSettingsOnly = $env:SKIP_SETTINGS_ONLY -eq '1'
if (-not $skSettingsOnly) {

# 1. episode 1
SK-TwoSkips
# 2. episode 2: the same skips; the toast
Check 'skip: the next episode starts (media next)' (SK-Go 'MediaNext')
SK-TwoSkips
# the first skip (0:0x-1:00) asks first; the second (1:0x-1:3x) takes its place: wait for that one
$skToast = Wait-Until { $n = SK-ToastName; if ($n -match '1:0\d.1:3\d in every episode of ') { $n } } -Seconds 14 -PollMs 250
Check 'skip: the same skip in two episodes asks "Skip this every episode?"' ([bool]$skToast) "$skToast"
Check 'skip: the toast names the span (1:0x-1:3x) and the show' ($skToast -match '1:0\d.1:3\d in every episode of ') "$skToast"
if ($skToast) {
    Start-Sleep -Milliseconds 600
    foreach ($w in $skSizes) { SK-Size $w; SK-Shot 'toast' $w }
    SK-Size $skSizes[0]
    # the keyboard: Tab reaches the toast's buttons (the prompts come first in the player's Tab order; from the bars the
    # cycle goes round the transport row first)
    $skSeen = @()
    foreach ($i in 1..18) {
        [void](SK-Press 'Tab'); Start-Sleep -Milliseconds 350
        $skSeen += SK-FocusName
        if ($skSeen[-1] -eq 'SkipSuggestionYes') { break }
    }
    Check 'skip: Tab reaches the toast (its Yes)' ($skSeen -contains 'SkipSuggestionYes') "Tab went: $($skSeen -join ' > ')"
    [void](SK-Press 'Esc')
    $skGone = Wait-Until { if (-not (SK-Toast)) { $true } } -Seconds 4 -PollMs 150
    Check 'skip: Esc is Not now (the toast goes, the player stays)' ([bool]$skGone -and [bool](Find-Ui -Id 'SeekBar' -OffscreenToo)) "focus after: $(SK-FocusName)"
}

# 3. episode 1 again, then 2: the toast again; Yes
Check 'skip: back to episode 1 (media previous)' (SK-Go 'MediaPrev')
SK-TwoSkips
Check 'skip: on to episode 2 again' (SK-Go 'MediaNext')
SK-TwoSkips
$skAgain = Wait-Until { if ((SK-ToastName) -match '1:0\d.1:3\d in every episode of ') { $true } } -Seconds 14 -PollMs 250
Check 'skip: after Not now, two more matching skips ask again' ([bool]$skAgain)
$skYes = Find-Ui -Id 'SkipSuggestionYes'
if ($skYes) {
    [void](Invoke-Element $skYes)
    $skSaid = Wait-Until { $t = Find-Ui -Id 'ToastLabel' -OffscreenToo; if ($t -and $t.Current.Name -eq 'Skipping it in every episode') { $true } } -Seconds 4 -PollMs 100
    Check 'skip: Yes saves the rule ("Skipping it in every episode")' ([bool]$skSaid -and -not (SK-Toast))
}

# 4. episode 3: the rule's part has the pill "Skip extra"
Check 'skip: on to episode 3' (SK-Go 'MediaNext')
[void](SK-Press '1')
$skPill = Wait-Until { $p = Find-Ui -Id 'SkipSegmentButton'; if ($p -and $p.Current.Name -eq 'Skip extra') { $p } } -Seconds 8 -PollMs 200
Check 'skip: the third episode offers "Skip extra" in the rule''s part' ([bool]$skPill) "$(if ($p = Find-Ui -Id 'SkipSegmentButton') { $p.Current.Name } else { 'no pill' })"
if ($skPill) {
    # paused, so the pill stays for the shots
    [void](SK-Press 'K'); Start-Sleep -Milliseconds 800
    foreach ($w in $skSizes) { SK-Size $w; SK-Shot 'pill-rule' $w }
    SK-Size $skSizes[0]
    [void](SK-Press 'S')
    $skPast = Wait-Until { if (-not (Find-Ui -Id 'SkipSegmentButton')) { $true } } -Seconds 5 -PollMs 150
    Check 'skip: S skips the extra (the pill goes)' ([bool]$skPast)
}
# the server's intro (source server: no source line) for the pill shots of the other source
[void](SK-Press 'Home'); Start-Sleep -Milliseconds 600
foreach ($i in 1..3) { [void](SK-Press 'Right'); Start-Sleep -Milliseconds 250 }
$skIntro = Wait-Until { $p = Find-Ui -Id 'SkipSegmentButton'; if ($p -and $p.Current.Name -eq 'Skip intro') { $p } } -Seconds 6 -PollMs 200
if ($skIntro) { SK-Shot 'pill-server' $skSizes[0] }

# 5. Shift+S: the mark sheet
[void](SK-Press 'Shift+S')
$skSheet = Wait-Until { Find-Ui -Name 'Skip a part every episode' -Type 'Text' } -Seconds 5 -PollMs 150
$skStart = Find-Ui -Id 'SkipSheetStart'
Check 'skip: Shift+S opens the mark sheet, pre-filled' ([bool]$skSheet -and [bool]$skStart) "focus: $(SK-FocusName)"
if ($skSheet) {
    Start-Sleep -Milliseconds 500
    foreach ($w in $skSizes) {
        SK-Size $w
        if (-not (Find-Ui -Name 'Skip a part every episode' -Type 'Text')) { [void](SK-Press 'Shift+S'); [void](Wait-Ui -Name 'Skip a part every episode' -Type 'Text' -Seconds 4) }
        Start-Sleep -Milliseconds 500
        SK-Shot 'sheet' $w
    }
    SK-Size $skSizes[0]
    [void](SK-Press 'Esc')
    $skClosed = Wait-Until { if (-not (Find-Ui -Name 'Skip a part every episode' -Type 'Text')) { $true } } -Seconds 4 -PollMs 150
    Check 'skip: Esc closes the mark sheet' ([bool]$skClosed)
}

}

# 6. Settings > Playback > Skip rules
if (-not $skSettingsOnly) { Step-Leave }
Open-Nav 'Settings'
Start-Sleep -Seconds 2
$skExp = Wait-Until { Find-Ui -Id 'SkipRulesExpander' -OffscreenToo } -Seconds 10 -PollMs 250
if ($skExp) {
    [void][OffscreenUi]::SetFocus($skExp); Start-Sleep -Milliseconds 600
    # the SettingsExpander's own Expander (inside it) is the one with the expand pattern
    $skCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsExpandCollapsePatternAvailableProperty, $true)
    $skToggle = if ($skExp.Current.IsEnabled -and $skExp.GetSupportedPatterns() -contains [System.Windows.Automation.ExpandCollapsePattern]::Pattern) { $skExp } else { $skExp.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $skCond) }
    try { $skToggle.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand() } catch { Say "skip: could not expand Skip rules: $($_.Exception.Message)" }
    # a run of its own (SKIP_SETTINGS_ONLY) may reuse a data folder with rules in it, or start from none
    $skRow = Wait-Until { $r = Find-Ui -Name 'Forget everything for ' -Contains -OffscreenToo; if ($r) { $r } elseif ($skSettingsOnly) { Find-Ui -Id 'SkipRulesStatus' -OffscreenToo } } -Seconds 25 -PollMs 300
    Check 'skip: Settings lists the rule under its show (or says there is none yet)' ([bool]$skRow) "$(if ($skRow) { $skRow.Current.Name })"
    Start-Sleep -Milliseconds 800
    foreach ($w in $skSizes) {
        SK-Size $w
        # the skip section into view: its expander's own scroll-into-view, else the page scrolled most of the way down
        $skInView = $false
        $skE = Find-Ui -Id 'SkipRulesExpander' -OffscreenToo
        try { $skE.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); $skInView = $true } catch { }
        if (-not $skInView) { [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 55) }
        Start-Sleep -Milliseconds 900
        SK-Shot 'settings' $w
    }
}
else { Check 'skip: Settings has the Skip rules list' $false 'not found' }
