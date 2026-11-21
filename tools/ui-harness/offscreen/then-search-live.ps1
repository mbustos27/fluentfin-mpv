# Nixie 1.5 (search), opt-in: the results page against a real Jellyfin, a local QA server's "QA Search" libraries
# (docs\QA-SERVERS.md; tools\qa-servers\seed.ps1 -SearchOnly adds them). Never the owner's server, never in a default suite:
#   run-offscreen.ps1 -QaServer 10.11|12 -Script wait:1 -Then offscreen\then-search-live.ps1 -NoA11y   (any NIXIE_LAYOUT)
# Each query's top result is the title it names, as typed and as people type it (no apostrophe; 10.11 finds that one only
# through Nixie's longest-word retry), words out of order still find their title, people come back with a People chip, and
# a person opens "With <name>" with their films. Dot-sourced by drive.ps1: no variable here may reuse one of its names.
. (Join-Path $PSScriptRoot 'search-common.ps1')
$slOn = if ([string]$QaUpstream -match ':8196') { '10.11' } elseif ([string]$QaUpstream -match ':8296') { '12' } else { [string]$QaUpstream }
if (-not $slOn) {
    Check 'search live: run against a QA server (-QaServer 10.11|12)' $false 'this check is for the QA servers only'
    return
}
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); SrSettle 1500
[void](Wait-Until { if (SrHome) { $true } } -Seconds 20 -PollMs 200)

$slCases = @(
    @{ Q = "it's always sunny"; Top = "It's Always Sunny in Philadelphia" },
    @{ Q = 'its always sunny'; Top = "It's Always Sunny in Philadelphia" },
    @{ Q = "it's always fair"; Top = "It's Always Fair Weather" },
    @{ Q = 'spirited away'; Top = 'Spirited Away' },
    @{ Q = 'se7en'; Top = 'Se7en' },
    @{ Q = 'wonderful life'; Group = 'Movies' },
    @{ Q = 'river night'; Group = 'Movies' }
)
foreach ($slCase in $slCases) {
    SrSearch $slCase.Q
    $slHeads = SrHeadings
    if ($slCase.Top) {
        $slTop = Find-Ui -Name $slCase.Top -Type 'Text'
        Check "search live ($slOn): '$($slCase.Q)' leads with $($slCase.Top)" ([bool]$slTop -and [bool](Find-Ui -Name 'Top result' -Type 'Text')) "groups: $($slHeads -join ', ')"
    }
    else {
        Check "search live ($slOn): '$($slCase.Q)' finds $($slCase.Group)" ([bool]($slHeads | Where-Object { $_ -like "$($slCase.Group) (*" })) "groups: $($slHeads -join ', ')"
    }
}
SrShot "$srTag-search-live-$slOn"

SrSearch 'sunny'
Check "search live ($slOn): 'sunny' has a People chip" ([bool](SrChip 'People')) "chips: $((SrChips | ForEach-Object { $_.Current.Name }) -join ' | ')"
SrSearch 'holm'
$slPerson = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like 'Ben Holm*' }) | Select-Object -First 1 } -Seconds 8 -PollMs 200
Check "search live ($slOn): 'holm' finds the people" ([bool]$slPerson) "groups: $((SrHeadings) -join ', ')"
if ($slPerson) {
    $slSince = Get-Epoch
    [void](Invoke-Element $slPerson)
    $slWith = Wait-Ui -Name 'With Ben Holm' -Type 'Text' -Seconds 15
    SrSettle 2500
    $slCards = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))) | Where-Object { -not $_.Current.IsOffscreen -and $_.Current.ClassName -match 'BaseItemDtoCard' })
    Check "search live ($slOn): a person opens With <name>, their films" ([bool]$slWith -and $slCards.Count -gt 0) "cards: $($slCards.Count)"
    # review (m5): the grid is the person's (asked with personIds), not a library: the seeded media's films, whose cast is
    # TMDb's, never show, and no card is a film the generated cast left Ben Holm out of
    $slAsked = @(Get-Requests $slSince | Where-Object { $_.path -match '/Items$' -and $_.query -and $_.query.PSObject.Properties['personids'] })
    $slOthers = @($slCards | Where-Object { $_.Current.Name -match '^(Big Buck Bunny|Sintel|Tears of Steel|The Matrix|Night of the Living Dead)' })
    Check "search live ($slOn): With <name> asks for the person's titles only (personIds) and shows none of the others" ($slAsked.Count -ge 1 -and $slOthers.Count -eq 0) "personIds requests: $($slAsked.Count); other films shown: $(($slOthers | ForEach-Object { $_.Current.Name }) -join ', ')"
    SrShot "$srTag-search-live-$slOn-person"
}
