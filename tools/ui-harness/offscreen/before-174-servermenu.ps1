<#
  174-signin (a friend's report: "when i hit the 3 dots to try and remove it opens up log in"): the server list's More
  options with two saved servers, pressed as people press it. -Before script of run-offscreen.ps1, on a -DataDir whose
  LocalSettings.json already saves a second server on a closed loopback port (run-offscreen keeps loopback servers):
    { "Servers": [ { "DisplayName": "Spare (closed port)", "LocalUrl": "http://127.0.0.1:9", "PublicUrl": "http://127.0.0.1:9",
                     "LocalNetworkNames": [], "Id": "spare-closed-port", "Users": [] } ] }

    run-offscreen.ps1 -DataDir <scratch>\data -Out <scratch>\run -Exe <Nixie.exe> -Script login,close -NoA11y `
        -Before tools\ui-harness\offscreen\before-174-servermenu.ps1

  A click (the dev build's pointer hook, HarnessPointer: posted clicks do nothing on a desktop nobody sees), Enter, Space,
  gamepad A and the menu key on the button: each opens a menu with Remove and nothing else (the page stays, the server is
  not asked, no error), and Escape closes it with the page still there. Then Remove (the item invoked) removes the
  closed-port server: the row and the saved file (174-signin: a server page left for the sign-in page kept the old list
  listening, and the removed row stayed). Leaves the fake server's row for the driver's sign-in.
  What this cannot press: the tap and double tap WinUI raises after a real click, which is what went through to the row
  (the hook clicks through the automation peer, and two of its clicks reopen an open menu where a real second press
  closes it; RowButtonTapTests checks the button keeps both), and Shift+F10 (a posted Shift is not seen off-screen,
  then-topbar160.ps1 found the same; the menu key opens the same context menu).
#>
. (Join-Path $PSScriptRoot 'states-common.ps1')

$liveName = 'Harness Jellyfin (fake)' # the fake server's name (fakeserver\server.py)
$spareName = 'Spare (closed port)'    # the second saved server, written into the data folder before the start

# the dev build's pointer hook (ui\FluentFin\Services\HarnessPointer.cs; run-offscreen sets NIXIE_HARNESS_POINTER=1): a click
# (1) or a double click (2) at a point on the screen, posted as then-season-switch.ps1 posts it
if (-not ('S174Pointer' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class S174Pointer {
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  public static bool Send(IntPtr main, int kind, int x, int y) {
    return PostMessage(main, RegisterWindowMessage("NixieHarnessPointer"), (IntPtr)kind, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
}
"@
}
function Click-At($Point, [int]$Kind = 1) { [void][S174Pointer]::Send($script:hwnd, $Kind, $Point.X, $Point.Y) }
function Get-Center($Element) {
    $r = $Element.Current.BoundingRectangle
    [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
}
function Find-More([string]$Server) { Find-Ui -Name "More options for $Server" -Type 'Button' }
function Find-Remove([string]$Server) { Find-Ui -Name "Remove $Server" -Type 'MenuItem' }
# Escape where the keyboard is: a menu is a popup window of its own (states-common.ps1's Get-OtherWindows), and one opened
# from the keyboard has the keys there (Escape posted to the main window never reached it); none open: the main window
function Close-Menu([string]$Server) {
    $popups = @(Get-OtherWindows)
    if ($popups) { foreach ($w in $popups) { [void][OffscreenUi]::Key([IntPtr]$w.Current.NativeWindowHandle, $script:ui.Id, 0x1B, $false) } }
    else { Key 0x1B }
    [bool](Wait-Until { -not (Find-Remove $Server) } -Seconds 3 -PollMs 100)
}
# the page after a press: still the server list (the address box), never the sign-in page, the server not asked, no error
function Test-StayedOnList([string]$What, [double]$Since) {
    Start-Sleep -Milliseconds 1500
    $signIn = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect')
    $asked = @(Get-Requests $Since | Where-Object { $_.path -match '^/System/Info/Public$' })
    $error_ = Find-Ui -Name "Can't reach" -Contains -Type 'Text'
    $connecting = Find-Ui -Name 'Connecting to' -Contains -Type 'Text'
    $list = Find-Ui -Id 'TxtUrl' -Type 'Edit'
    Check "server menu, $($What): the list stays (no sign-in page, the server not asked, no error)" ($list -and -not $signIn -and $asked.Count -eq 0 -and -not $error_ -and -not $connecting) `
        "list: $([bool]$list); sign-in page: $([bool]$signIn); /System/Info/Public asked: $($asked.Count); error: $([bool]$error_); connecting: $([bool]$connecting)"
    if ($signIn) {
        # the bug: back to the list for the next press
        $change = Find-Ui -Name 'Change server'
        if ($change) { [void](Invoke-Element $change); [void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 10); Start-Sleep -Milliseconds 600 }
    }
}
function Test-MenuOpens([string]$What, [string]$Server, [double]$Since) {
    $item = Wait-Until { Find-Remove $Server } -Seconds 3 -PollMs 100
    Check "server menu, $($What): a menu with Remove $Server opens" ([bool]$item)
    Test-StayedOnList $What $Since
    if ($item -and (Find-Remove $Server)) {
        $closed = Close-Menu $Server
        $f = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
        Check "server menu, $($What): Escape closes it, the list stays" ($closed -and (Find-Ui -Id 'TxtUrl' -Type 'Edit') -and -not [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect')) "focus after: $f"
    }
}
function Focus-More([string]$Server) {
    $more = Find-More $Server
    if (-not $more) { return $false }
    [void][OffscreenUi]::SetFocus($more); Start-Sleep -Milliseconds 400
    ([string][OffscreenUi]::Focused($script:hwnd, $script:ui.Id)) -like "*More options for $Server*"
}

# the second saved row: the fake server's, as a sign-in saves it (its address, then Change back to the list)
[void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 30)
Check 'server menu: the closed-port server is saved before the start' ([bool](Wait-Until { Find-More $spareName } -Seconds 10))
Submit-Address $script:serverUrl
[void](Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect') } -Seconds 20)
$change = Wait-Ui -Name 'Change server' -Seconds 10
if (-not $change) { throw 'no Change server link' }
[void](Invoke-Element $change)
[void](Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 20)
Start-Sleep -Milliseconds 800
$two = (Find-More $liveName) -and (Find-More $spareName)
Check 'server menu: two saved servers on the list' ([bool]$two)
if (-not $two) { throw 'server menu: the list does not show both servers' }
Capture-State 'server-menu-two'

foreach ($server in @($liveName, $spareName)) {
    # a mouse click on More options
    $at = Get-Epoch
    $c = Get-Center (Find-More $server)
    Click-At $c
    if ($server -eq $liveName) { Start-Sleep -Milliseconds 300; Capture-State 'server-menu-open' }
    Test-MenuOpens "a click on $server's" $server $at
}

# the keyboard and a gamepad on the live server's button: Enter, Space, gamepad A (its click); the menu key (the row's
# context menu: the same Remove)
foreach ($press in @(@('Enter', 0x0D, $false), @('Space', 0x20, $false), @('gamepad A', 0xC3, $false), @('the menu key', 0x5D, $false))) {
    $focused = Focus-More $liveName
    Check "server menu, $($press[0]): More options has the keyboard first" $focused
    $at = Get-Epoch
    Key $press[1] -Shift:$press[2]
    Test-MenuOpens $press[0] $liveName $at
}

# Remove on the closed-port server: More options clicked, its Remove item invoked (the hook does not reach a menu's popup;
# Invoke runs the item's Click as a click does), then the question's Remove
$c = Get-Center (Find-More $spareName)
$at = Get-Epoch
Click-At $c
$item = Wait-Until { Find-Remove $spareName } -Seconds 3 -PollMs 100
if (-not $item) { Check 'server menu: Remove' $false 'no menu' }
else {
    [void](Invoke-Element $item)
    $ask = Wait-Until { Find-Ui -Id 'PrimaryButton' -Type 'Button' } -Seconds 5 -PollMs 100
    Check 'server menu: Remove asks first' ([bool]$ask) $(if ($ask) { "the question's button: '$($ask.Current.Name)'" } else { 'no question' })
    if ($ask) {
        Capture-State 'server-menu-asks'
        [void](Invoke-Element $ask)
        $gone = Wait-Until { -not (Find-More $spareName) } -Seconds 5 -PollMs 100
        Start-Sleep -Milliseconds 800
        $saved = Get-Content -LiteralPath (Join-Path $DataDir 'LocalSettings.json') -Raw -Encoding utf8 | ConvertFrom-Json
        $names = @($saved.Servers | ForEach-Object { $_.DisplayName })
        Check 'server menu: Remove removes the server (the row and the saved file), the other stays' ($gone -and (Find-More $liveName) -and $names -notcontains $spareName -and $names -contains $liveName) "saved: $($names -join ', ')"
        Test-StayedOnList 'after Remove' $at
    }
}
Capture-State 'server-menu-after'
