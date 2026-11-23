# Nixie 1.7.3 fix round (the coordinator's Major: verify-package's NoMpv run of the first 1.7.3 build still crashed at the
# close: APPCRASH 0xc000027b in CoreMessagingXP.dll, a stowed E_UNEXPECTED from combase, 5.7 s to exit). Plays the season's
# episode with mpv's controls and no mpv on the PATH, waits for the player page's "mpv isn't installed on this PC", stays there
# as verify-package does (NIXIE_NOMPV_STAY_S, 0 by default: the crash needs no wait), then closes from the player page and
# checks the close: gone within NIXIE_CLOSE_LIMIT_MS (2000 here) with exit code 0. Run after the season page, with drive.ps1's
# own play and close left out (this script plays and closes):
#   run-offscreen.ps1 ... -WithoutMpv -Setting MpvPlayerControls=Mpv -Script season -NoA11y -Then offscreen\then-173-close-nompv.ps1
# Dot-sourced by drive.ps1: every name here starts with cn.

if ($PlayerControls -ne 'Mpv') { throw "then-173-close-nompv: run it with -Setting MpvPlayerControls=Mpv (this run has $PlayerControls)" }
if ($script:page -ne 'season') { Step-Season $false }
$cnRow = Find-EpisodeRow
if (-not $cnRow) { throw "then-173-close-nompv: no Play for '$($fixture.episode.name)'" }
[void](Invoke-Element $cnRow)
$script:page = 'player'
Say "play invoked on '$($fixture.episode.name)' with no mpv on the PATH"
# the page says so (Core\Mpv\MpvNotFoundMessage), as the user reads it
$cnSaid = Wait-Ui -Name "mpv isn't installed on this PC" -Contains -OffscreenToo -Seconds 30 -PollMs 100
Check "no mpv: the player page says mpv isn't installed" ([bool]$cnSaid) $(if ($cnSaid) { $cnSaid.Current.Name.Substring(0, [Math]::Min(120, $cnSaid.Current.Name.Length)) } else { 'not within 30 s' })
$cnStay = if ($env:NIXIE_NOMPV_STAY_S) { [double]$env:NIXIE_NOMPV_STAY_S } else { 0 }
if ($cnStay -gt 0) { Say "staying on the player page for $cnStay s"; Start-Sleep -Milliseconds ([int]($cnStay * 1000)) }

# the close checks of then-173-close.ps1, with the coordinator's 2 s limit unless the run says otherwise
if (-not $env:NIXIE_CLOSE_LIMIT_MS) { $env:NIXIE_CLOSE_LIMIT_MS = '2000' }
. (Join-Path $PSScriptRoot 'then-173-close.ps1')
