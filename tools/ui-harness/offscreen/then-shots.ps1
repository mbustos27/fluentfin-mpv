# Seaglass scrolling, screenshots (and the See all checks): run with -Script home -Then offscreen\then-shots.ps1
# Home wide (1920) and narrow (1100), keyboard focus on a card, then "See all" of Continue Watching (the grid page) and of
# Recently Added (the library).
function Settle([int]$Ms = 900) { Start-Sleep -Milliseconds $Ms }
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Settle 1500
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Settle
Shot 'home-wide-arrows'
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900); Settle 1500
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Settle
Shot 'home-narrow-arrows'
# the arrow: Scroll right on Continue Watching pages the row by a viewport minus a card. Nixie 1.7.4: at the row's end, shown
# with the pointer over the row (the dev build's pointer hook moves it there: posted moves never reach WinUI off-screen)
if (-not ('ShHook.Msg' -as [type])) { Add-Type -Namespace ShHook -Name Msg -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string n);' }
$shRow = Find-Ui -Name 'Continue Watching' -Type 'List'
if ($shRow) {
    $shR = [OffscreenUi]::RectOf($shRow)
    $shAt = ([int]($shR.Y + $shR.Height / 2) -shl 16) -bor ([int]($shR.X + $shR.Width / 2) -band 0xFFFF)
    [void][OffscreenUi]::PostMessage($script:hwnd, [ShHook.Msg]::RegisterWindowMessage('NixieHarnessPointer'), [IntPtr]4, [IntPtr]$shAt)   # 4: a move
    Settle 600
}
$arrow = Find-Ui -Name 'Scroll Continue Watching right' -Type 'Button'
Check 'see all: the Continue Watching arrow shows with the pointer over the row' ([bool]$arrow)
if ($arrow) { Invoke-Element $arrow | Out-Null; Settle 1500; Shot 'home-narrow-after-arrow' }
[OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1920, 1080); Settle 1500
$sa = Find-Ui -Name 'See all Continue Watching' -Type 'Hyperlink'
Check 'see all: Continue Watching has a See all button' ([bool]$sa)
if ($sa) {
    Invoke-Element $sa | Out-Null
    $title = Wait-Ui -Name 'Continue Watching' -Type 'Text' -Seconds 10
    Settle 2000
    $cards = [OffscreenUi]::CountByClass($script:hwnd, $script:ui.Id, @('BaseItemDtoCard'))
    Check 'see all: the grid page opens with the row''s cards' ($cards['BaseItemDtoCard'] -ge 6) "$($cards['BaseItemDtoCard']) cards"
    Shot 'see-all-continue-grid'
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, 1100, 900); Settle 1500
    Shot 'see-all-continue-grid-narrow'
    Key 0xA6; Settle 1500   # Back
}
