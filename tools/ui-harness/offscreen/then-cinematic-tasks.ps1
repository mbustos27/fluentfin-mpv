# Nixie 1.3.0, the Cinematic theme's four task gates (the concepts' "common tasks, in clicks"): resume the last thing, the
# next episode, a letter jump in the Library and picking a season, each in ONE action of the user's, through UI Automation
# and posted keys only. Run with NIXIE_LAYOUT=Cinematic in the environment and -Script home -Then
# offscreen\then-cinematic-tasks.ps1 (the fake server's fixture).
#   resume: the Up next shelf's first still (the keyboard's C, which only moves the focus), its Play: one action;
#   next episode: N (the focus to the first episode that came from Next Up), its Play: one action;
#   letter: the A-Z jump's letter: one action, and the focus lands on the first title with that letter;
#   season: a season tab: one action, and the shelf shows that season's episodes.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
$ctA = [System.Windows.Automation.AutomationElement]
function Ct-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function Ct-Focused {
    $cond = New-Object System.Windows.Automation.PropertyCondition($ctA::HasKeyboardFocusProperty, $true)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
}
function Ct-PlayOfFocused {
    # the focused still shows its Play (a child button named "Play ..." / "Resume ...")
    $still = Ct-Focused
    if (-not $still) { return $null }
    $buttons = $still.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ctA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $buttons) { if ($b.Current.Name -match '^(Play|Resume)') { return @{ Still = $still.Cached.Name; Play = $b } } }
    $null
}
function Ct-InPlayer { Wait-Ui -Id 'PlayPauseButton' -Seconds 30 -PollMs 150 }
function Ct-Leave {
    Key 0xA6   # the Back key
    if (-not (Wait-Until { Find-Ui -Name 'Up next' -Type 'List' } -Seconds 10 -PollMs 200)) {
        $btn = Find-Ui -Id 'BackButton'; if ($btn) { [void](Invoke-Element $btn) }
        [void](Wait-Until { Find-Ui -Name 'Up next' -Type 'List' } -Seconds 15 -PollMs 200)
    }
    Ct-Settle 2500
}

# --- resume -------------------------------------------------------------------------------------------------------
Open-Nav 'Home'; Ct-Settle 3000
Key 0x43; Ct-Settle 900   # C: the keyboard to Up next (moves the focus only)
$ctResume = Ct-PlayOfFocused
if ($ctResume) {
    Say "task resume: $($ctResume.Still) -> $($ctResume.Play.Current.Name)"
    [void](Invoke-Element $ctResume.Play)   # the one action
}
$ctPlaying = if ($ctResume) { Ct-InPlayer } else { $null }
Check 'task (Cinematic): resume the last thing in one action (Up next, its Play)' ([bool]$ctPlaying) $(if ($ctResume) { $ctResume.Play.Current.Name } else { 'no Play on the focused still' })
if ($ctPlaying) { Ct-Leave }

# --- the next episode ---------------------------------------------------------------------------------------------
Key 0x4E; Ct-Settle 900   # N: the keyboard to the first episode from Next Up
$ctNext = Ct-PlayOfFocused
if ($ctNext) {
    Say "task next: $($ctNext.Still) -> $($ctNext.Play.Current.Name)"
    [void](Invoke-Element $ctNext.Play)
}
$ctPlaying = if ($ctNext) { Ct-InPlayer } else { $null }
$ctSub = if ($ctPlaying) { Find-Ui -Id 'SubtitleText' }; $ctTitle = if ($ctSub) { $ctSub.Current.Name } else { '' }
Check 'task (Cinematic): the next episode in one action (N, its Play)' ([bool]$ctPlaying) "$(if ($ctNext) { $ctNext.Still } else { 'no Play on the focused still' }); playing: $ctTitle"
if ($ctPlaying) { Ct-Leave }

# --- a letter jump in the Library ------------------------------------------------------------------------------------
Open-Library $fixture.movie.library
$ctJump = Wait-Until { Find-Ui -Name 'Jump to a letter' -Type 'List' } -Seconds 20 -PollMs 200
Ct-Settle 1500
$ctLetter = $null; $ctLanded = $null
if ($ctJump) {
    $ctLetters = $ctJump.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ctA::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    # a letter past the first screen of titles
    $ctPick = @($ctLetters) | Where-Object { $_.Current.Name -match '^Jump to [A-Z]$' } | Select-Object -Last 1
    if ($ctPick) {
        $ctLetter = $ctPick.Current.Name.Substring(8)
        [void](Invoke-Element $ctPick)   # the one action
        $ctLanded = Wait-Until { $f = Ct-Focused; if ($f -and $f.Cached.ClassName -eq 'BaseItemDtoCard') { $f } } -Seconds 10 -PollMs 150
    }
}
$ctLandedName = if ($ctLanded) { $ctLanded.Cached.Name } else { '' }
Check 'task (Cinematic): a letter jump in one action, the focus on its first title' ($ctLanded -and $ctLandedName.TrimStart().ToUpperInvariant().StartsWith($ctLetter)) "Jump to $ctLetter -> '$ctLandedName'"

# --- picking a season --------------------------------------------------------------------------------------------
Step-Series $false
Ct-Settle 2500
$ctTabs = Wait-Until { Find-Ui -Name 'Seasons' -Type 'Tab' } -Seconds 10 -PollMs 200
$ctOther = $null
if ($ctTabs) {
    $ctItems = $ctTabs.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ctA::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)))
    $ctOther = @($ctItems) | Where-Object { $_.Current.Name -notmatch 'current season' } | Select-Object -First 1
}
$ctSeasonOk = $false; $ctSeen = ''
if ($ctOther) {
    $ctWanted = $ctOther.Current.Name
    $ctNumber = if ($ctWanted -match '(\d+)') { $Matches[1] } else { '' }
    [void](Invoke-Element $ctOther)   # the one action
    $ctSeasonOk = [bool](Wait-Until {
        $list = Find-Ui -Name 'Episodes' -Type 'List'
        $first = if ($list) { $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($ctA::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) }
        if ($first) { $script:ctSeenName = $first.Current.Name; if ($first.Current.Name -match "^S$ctNumber\s?E") { $true } }
    } -Seconds 10 -PollMs 200)
    $ctSeen = "$ctWanted -> first episode '$script:ctSeenName'"
}
Check 'task (Cinematic): picking a season in one action (its tab), its episodes on the shelf' $ctSeasonOk $ctSeen
Shot 'cin-tasks-season'
