<#
  The basics gate, g2's helpers (classes 2 and 3: servers and every menu). Dot-sourced by 2-servers.ps1 and 3-menus.ps1,
  which offscreen\drive.ps1 dot-sources in turn (run-offscreen.ps1 -Then): drive.ps1's helpers are there (Check, Say,
  Find-Ui, Wait-Ui, Invoke-Element, Key, Open-Nav, Get-Requests, Step-Close, Start-App, Shot). Every name here starts with
  G2 or g2 (PowerShell names ignore case, and drive.ps1's own must not be shadowed).

  Input as a user gives it, off-screen: the dev build's pointer hook for a click (ui\FluentFin\Services\HarnessPointer.cs:
  posted clicks do nothing on a desktop nobody sees), keys posted to the window that reads them (a menu is a popup window of
  its own, so a key meant for an open menu goes to its window), gamepad A as VK_GAMEPAD_A. What can't be pressed here: a
  right click (no right button in the hook; the menu key raises the same ContextRequested, and so does Shift+F10, whose
  Shift can't be posted), and the tap WinUI raises after a real click (RowButtonTapTests checks the buttons that keep it).
#>
. (Join-Path $PSScriptRoot '..\..\ui-harness\offscreen\states-common.ps1')
$g2Common = Join-Path $PSScriptRoot '..\gate-common.ps1'
if (Test-Path -LiteralPath $g2Common) { . $g2Common }

$g2Vk = @{ Enter = 0x0D; Space = 0x20; Escape = 0x1B; Down = 0x28; Up = 0x26; Tab = 0x09; Apps = 0x5D; GamepadA = 0xC3; GamepadB = 0xC4; GamepadMenu = 0xCF }

# --- the pointer hook: a click (1) or a double click (2) at a point on the screen ---------------------------------------
if (-not ('G2Pointer' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class G2Pointer {
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  public static bool Send(IntPtr main, int kind, int x, int y) {
    return PostMessage(main, RegisterWindowMessage("NixieHarnessPointer"), (IntPtr)kind, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
}
"@
}
function G2-Center($Element) {
    $r = $Element.Current.BoundingRectangle
    [pscustomobject]@{ X = [int]($r.X + $r.Width / 2); Y = [int]($r.Y + $r.Height / 2) }
}
function G2-Click($Element, [int]$Kind = 1) {
    $c = G2-Center $Element
    [void][G2Pointer]::Send($script:hwnd, $Kind, $c.X, $c.Y)
}
function G2-Rect($Element) {
    try { $r = $Element.Current.BoundingRectangle; '{0},{1} {2}x{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height } catch { 'gone' }
}
function G2-HasFocus($Element) { try { [bool]$Element.Current.HasKeyboardFocus } catch { $false } }
function G2-Focused { $f = Focused-Element; if ($f) { "$($f.type) '$($f.name)'" } else { 'nothing' } }

# a key to where it goes: the open menu's window when one is open, else the main window's input site
function G2-Key([int]$Vk) {
    $popups = @(G2-Popups)
    if ($popups) { [void][OffscreenUi]::Key([IntPtr]$popups[-1].Current.NativeWindowHandle, $script:ui.Id, $Vk, $false) }
    else { Key $Vk }
}

# --- sizes (g1's gate-common.ps1 when it is there) ---------------------------------------------------------------------
function G2-Sizes {
    if (Get-Command Get-GateSizes -ErrorAction SilentlyContinue) { return @(Get-GateSizes) }
    $s = if ($env:GATE_SIZES) { $env:GATE_SIZES } else { '1920x1080' }
    @($s -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
function G2-Size([string]$Size) {
    if (Get-Command Set-GateSize -ErrorAction SilentlyContinue) { $r = Set-GateSize $Size; return "$($r.Label)" }
    if ($Size -eq 'max') { $Size = '2560x1392' }
    if ($Size -notmatch '^(\d+)x(\d+)$') { throw "G2-Size: '$Size' is not WxH" }
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, [int]$Matches[1], [int]$Matches[2])
    Start-Sleep -Milliseconds 1500
    $Size
}
# the window dragged through sizes without waiting (a live resize), then back
function G2-LiveResize {
    if (Get-Command Invoke-GateLiveResize -ErrorAction SilentlyContinue) { Invoke-GateLiveResize; return }
    $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r)
    $w = $r.R - $r.L; $h = $r.B - $r.T
    foreach ($step in 1..6) {
        [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, [int]($w - 40 * $step), [int]($h - 24 * $step))
        Start-Sleep -Milliseconds 60
    }
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $w, $h)
    Start-Sleep -Milliseconds 1200
}
function G2-Theme { if (Get-Command Get-GateTheme -ErrorAction SilentlyContinue) { Get-GateTheme } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' } }

# --- servers ------------------------------------------------------------------------------------------------------------
function G2-Post([string]$Url, [string]$Path, $Body = $null) {
    $a = @{ Uri = "$Url$Path"; Method = 'POST'; UseBasicParsing = $true; TimeoutSec = 10 }
    if ($null -ne $Body) { $a.Body = ($Body | ConvertTo-Json -Compress); $a.ContentType = 'application/json' }
    (Invoke-WebRequest @a).Content | ConvertFrom-Json
}
# the requests a fake server logged since a time (its folder's requests.jsonl)
function G2-RequestsIn([string]$Folder, [double]$Since) {
    $f = Join-Path $Folder 'requests.jsonl'
    if (-not (Test-Path -LiteralPath $f)) { return @() }
    @(Get-Content -LiteralPath $f -Encoding utf8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.t -ge $Since })
}
# the second server (checks\second-server.py: the fake with another id and name), in this run's job and tracked, so
# drive.ps1's cleanup ends it; its log in <Out>\server-b
function Start-G2SecondServer {
    $dir = Join-Path $Out 'server-b'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $ready = Join-Path $dir 'ready.json'
    $a = @('-B', '-I', (Join-Path $PSScriptRoot 'second-server.py'), '--out', $dir, '--media', $Media, '--ready-file', $ready, '--auth', 'strict')
    $q = @($a | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } })
    $p = Start-Process -FilePath $Python -ArgumentList $q -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $dir 'stdout.txt') -RedirectStandardError (Join-Path $dir 'stderr.txt')
    Add-Tracked -Process $p
    if (-not (Wait-Until { Test-Path -LiteralPath $ready } -Seconds 60)) { throw "the second fake server did not start (see $dir\stderr.txt)" }
    $url = (Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json).url
    Say "second fake server at $url (Second Harness (fake))"
    [pscustomobject]@{ Url = $url; Folder = $dir; Process = $p }
}
function Stop-G2SecondServer($Server) { if ($Server) { try { [void](G2-Post $Server.Url '/__harness/shutdown') } catch {} } }

# the saved servers, as the settings file has them: DisplayName, Id, the saved users' names
function G2-SavedServers {
    $f = Join-Path $DataDir 'LocalSettings.json'
    $s = Get-Content -LiteralPath $f -Raw -Encoding utf8 | ConvertFrom-Json
    @($s.Servers | Where-Object { $null -ne $_ } | ForEach-Object { [pscustomobject]@{ Name = $_.DisplayName; Id = $_.Id; Users = @($_.Users | ForEach-Object { $_.Username }) } })
}
function G2-SavedNames { @(G2-SavedServers | ForEach-Object { $_.Name }) }

function G2-OnServerPage { [bool](Find-Ui -Id 'TxtUrl' -Type 'Edit') }
function G2-SignInPageServer {
    $line = Find-Ui -Id 'ServerLine' -Type 'Text'
    if ($line -and [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect')) { ($line.Current.Name -replace '^Sign in to ', '').Trim() } else { $null }
}
function G2-OnHome {
    $p = $null
    try { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id) } catch {}
    [bool]($p -and ([OffscreenUi]::NavItem($p, 'Home') -or (Find-Ui -Id 'TopTabsMenu')))
}
function G2-ServerRow([string]$Name) { [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, $Name, 'Microsoft.UI.Xaml.Controls.ItemContainer') }
function G2-ServerMore([string]$Name) { Find-Ui -Name "More options for $Name" -Type 'Button' }
function G2-ServerRemove([string]$Name) { Find-Ui -Name "Remove $Name" -Type 'MenuItem' }

# the Account menu: its button, open; $null when there is none (not signed in)
function G2-OpenAccount {
    $acct = Wait-Until { Find-Ui -Name 'Account, ' -Type 'Button' -Contains } -Seconds 15 -PollMs 150
    if (-not $acct) { return $null }
    [void](Invoke-Element $acct)
    [void](Wait-Ui -Id 'AccountServerName' -Seconds 5)
    $acct
}
function G2-CloseFlyouts {
    foreach ($w in @(G2-Popups)) { [void][OffscreenUi]::Key([IntPtr]$w.Current.NativeWindowHandle, $script:ui.Id, 0x1B, $false) }
    [void](Wait-Until { -not @(G2-Popups).Count } -Seconds 3 -PollMs 100)
    # the Account menu and other flyouts drawn in the main window (no popup window of their own)
    if ((Find-Ui -Id 'AccountServerName') -or @(G2-Flyouts).Count) { Key 0x1B; [void](Wait-Until { -not (Find-Ui -Id 'AccountServerName') -and -not @(G2-Flyouts).Count } -Seconds 3 -PollMs 100) }
}
# the server and user the Account menu names (what the user sees signed in), then the menu closed
function G2-SignedInAs {
    if (-not (G2-OpenAccount)) { return $null }
    $server = Find-Ui -Id 'AccountServerName'
    $user = Find-Ui -Id 'AccountUserName'
    $r = [pscustomobject]@{ Server = $(if ($server) { $server.Current.Name }); User = $(if ($user) { $user.Current.Name }) }
    G2-CloseFlyouts
    $r
}
# Account > Sign out: the server page
function G2-SignOut {
    if (-not (G2-OpenAccount)) { throw 'no Account button to sign out with' }
    $out_ = Wait-Ui -Name 'Sign out' -Type 'Button' -Seconds 5
    if (-not $out_) { throw 'no Sign out in the Account menu' }
    [void](Invoke-Element $out_)
    if (-not (Wait-Until { G2-OnServerPage } -Seconds 20)) { throw 'Sign out: no server page within 20 s' }
    $script:navPane = $null
    Start-Sleep -Milliseconds 800
}
# on a sign-in page: Quick Connect, the code approved on that server (as the given user, else its first), Home
# -Remember: "Remember me" ticked first, as a user who wants to stay signed in does
function G2-SignInQuick([string]$Url, [string]$User, [switch]$Remember) {
    $quick = Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect') } -Seconds 20 -PollMs 150
    if (-not $quick) { throw 'no Use Quick Connect on the sign-in page' }
    if ($Remember) {
        $box = Wait-Ui -Name 'Remember me' -Type 'CheckBox' -Seconds 5
        if (-not $box) { throw 'no Remember me on the sign-in page' }
        $t = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($t.Current.ToggleState -ne 'On') { $t.Toggle(); Start-Sleep -Milliseconds 300 }
    }
    [void](Invoke-Element $quick)
    $code = Wait-Until { $t = Find-Ui -Id 'QuickConnectCode' -Type 'Text'; if ($t -and ($t.Current.Name -replace '\s', '') -match '^(\d{6})$') { $Matches[1] } } -Seconds 20 -PollMs 100
    if (-not $code) { throw 'Quick Connect showed no code' }
    $body = @{ code = $code }; if ($User) { $body.user = $User }
    [void](G2-Post $Url '/__harness/quickconnect/approve' $body)
    if (-not (Wait-Until { G2-OnHome } -Seconds 30 -PollMs 150)) { throw 'no Home within 30 s of the Quick Connect sign-in' }
    $script:navPane = $null
    Start-Sleep -Milliseconds 800
}
function G2-ChangeServer {
    $c = Wait-Ui -Name 'Change server' -Seconds 10
    if (-not $c) { throw 'no Change server link on the sign-in page' }
    [void](Invoke-Element $c)
    if (-not (Wait-Until { G2-OnServerPage } -Seconds 15)) { throw 'Change server: no server page' }
    Start-Sleep -Milliseconds 800
}

# --- menus --------------------------------------------------------------------------------------------------------------
# the app's popup windows shown now (menus, flyouts: each a window of its own), found through Win32 (the visible
# PopupWindowSiteBridge children of the main window), each read through UI Automation from its own handle. 1.7.4's
# servermenu flake: after a first menu closed with Escape, the main window's UI Automation tree showed no later menu.
if (-not ('G2Win' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class G2Win {
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
  // every visible window of the process with this class: top-level ones and the children of each
  public static List<IntPtr> Visible(int pid, string cls) {
    var tops = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == (uint)pid) tops.Add(h); return true; }, IntPtr.Zero);
    var hits = new List<IntPtr>();
    foreach (var t in tops) {
      if (IsWindowVisible(t) && Cls(t) == cls) hits.Add(t);
      EnumChildWindows(t, (h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == (uint)pid && IsWindowVisible(h) && Cls(h) == cls && !hits.Contains(h)) hits.Add(h); return true; }, IntPtr.Zero);
    }
    return hits;
  }
}
"@
}
# a popup that is a menu or a flyout (a tooltip, which shows on keyboard focus too, is not)
function G2-IsMenuPopup($Popup) {
    $cond = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Menu)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'FlyoutPresenter')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'MenuFlyoutPresenter')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AccountServerName')))
    try { [bool]@([OffscreenUi]::Under($Popup, $cond)).Count } catch { $false }
}
function G2-Popups {
    @([G2Win]::Visible($script:ui.Id, 'Microsoft.UI.Content.PopupWindowSiteBridge') | ForEach-Object { try { [System.Windows.Automation.AutomationElement]::FromHandle($_) } catch {} } |
        Where-Object { $_ -and (G2-IsMenuPopup $_) })
}
# the menu items open now, in order, inside the popups only (the window's own title bar has a "System" menu item that is
# always there)
# A flyout drawn inside the window's own bounds (the top bar's Libraries menu) has no window of its own: its items are read
# from the main window too, leaving out the title bar's System item. Each item once (a windowed menu shows in both).
function G2-MenuItems {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    $seen = @{}
    @(foreach ($src in @(@(G2-Popups) + @($null))) {
            $found = try { if ($src) { [OffscreenUi]::Under($src, $cond) } else { [OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) } } catch { @() }
            foreach ($e in @($found)) {
                try {
                    $c = $e.Cached
                    if ($c.IsOffscreen -or $c.Name -eq 'System') { continue }
                    $key = '{0}|{1}' -f $c.Name, $c.BoundingRectangle
                    if ($seen.ContainsKey($key)) { continue }
                    $seen[$key] = $true
                    $e
                } catch {}
            }
        })
}
function G2-MenuNames { @(G2-MenuItems | ForEach-Object { $_.Cached.Name }) }
# a flyout that isn't a menu (the player's Volume, Quality, Skip parts): its presenter, in the window or a popup of its own
function G2-Flyouts {
    $cond = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'FlyoutPresenter')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'MenuFlyoutPresenter')))
    $all = foreach ($src in @(@(G2-Popups) + @($null))) {
        try { if ($src) { [OffscreenUi]::Under($src, $cond) } else { [OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) } } catch {}
    }
    @($all | Where-Object { try { -not $_.Cached.IsOffscreen } catch { $false } })
}
# what an open flyout holds, named: its buttons, list items, boxes and sliders
function G2-FlyoutNames {
    $kinds = @('Button', 'ListItem', 'Edit', 'Slider', 'CheckBox', 'RadioButton', 'MenuItem')
    $all = foreach ($f in @(G2-Flyouts)) {
        try { [OffscreenUi]::Under($f, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { try { $_.Cached.Name -and ($_.Cached.ControlType.ProgrammaticName -replace '^ControlType\.', '') -in $kinds } catch { $false } } | ForEach-Object { $_.Cached.Name } } catch {}
    }
    @($all | Select-Object -Unique)
}
# the popup the keyboard is in: from the focused element up (the raw tree: a flyout's presenter isn't in the control view),
# the first ancestor that is a flyout's presenter, a popup or a popup's window; $null when the keyboard is on the page.
# A flyout opened by a click or a key takes the keyboard (the player's Volume: its Unmute; Quality: its list).
function G2-FocusedPopup {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $f = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { try { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } catch { $false } })[0]
    if (-not $f) { return $null }
    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $p = $f
    for ($i = 0; $i -lt 25 -and $p; $i++) {
        try {
            $p = $walker.GetParent($p)
            if (-not $p) { break }
            $cls = [string]$p.Current.ClassName
            if ($cls -match 'FlyoutPresenter|Popup') { return $p }
            if ($p.Current.NativeWindowHandle -eq [int]$script:hwnd) { return $null }
        } catch { return $null }
    }
    $null
}
function G2-MenuOpen { [bool]@(G2-Popups).Count -or [bool]@(G2-MenuItems).Count -or [bool]@(G2-Flyouts).Count -or [bool](G2-FocusedPopup) }
# a popup's item by name (a menu's, a flyout's button)
function G2-PopupItem([string]$Name, [string]$Type = 'MenuItem') {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::$Type)))
    foreach ($w in @(G2-Popups)) { try { $hit = @([OffscreenUi]::Under($w, $cond))[0]; if ($hit) { return $hit } } catch {} }
    $null
}

# what a menu's press may send: nothing that changes anything (POST, DELETE: a mark, a removal, a playback), and no item
# page or playback asked for. The app's own background calls are not presses (capabilities, the live-updates socket).
# While something plays ($script:g2Playing), the backend's reports and mpv's reads go on by themselves: left out (the
# player's checks read the pause state instead).
$script:g2Playing = $false
function G2-StrayRequests([double]$Since, [string]$Folder) {
    $reqs = if ($Folder) { G2-RequestsIn $Folder $Since } else { @(Get-Requests $Since) }
    if ($script:g2Playing) { $reqs = @($reqs | Where-Object { $_.route -notlike 'report-*' -and $_.route -notin @('stream', 'subtitles', 'trickplay', 'segments') }) }
    @($reqs | Where-Object {
            ($_.method -in @('POST', 'DELETE', 'PUT') -and $_.route -notin @('capabilities', 'sessions', 'websocket', 'quickconnect')) -or
            $_.route -in @('item', 'playbackinfo', 'stream', 'public-info') -or $_.path -match '^/System/Info/Public$'
        } | ForEach-Object { "$($_.method) $($_.path) [$($_.route)]" })
}

# where the page is: the title bar's Back (enabled or not) and the opener's place; a press that navigated changes one
function G2-PageMark($Opener) {
    # the title bar's Back (its template part), else the player's own
    $back = @(Find-Ui -Id 'PART_BackButton' -Type 'Button')[0]
    if (-not $back) { $back = @(Find-Ui -Id 'BackButton' -Type 'Button')[0] }
    [pscustomobject]@{ Back = $(if ($back) { try { [bool]$back.Current.IsEnabled } catch { $null } }); Opener = G2-Rect $Opener }
}
# the page stays: Back as it was, and the opener still there and on screen. Not its exact place: a focused card zooms
# (Cinematic's FocusZoom moves a card's box by 30 px and more as the menu takes the keyboard; Editorial's posters by 4).
function G2-OnScreen($Element) { try { $r = $Element.Current.BoundingRectangle; -not $Element.Current.IsOffscreen -and -not $r.IsEmpty -and $r.Width -gt 0 } catch { $false } }
function G2-SamePage($Mark, $Opener) {
    $now = G2-PageMark $Opener
    [pscustomobject]@{ Ok = ($now.Back -eq $Mark.Back -and $now.Opener -ne 'gone' -and (G2-OnScreen $Opener)); Detail = "Back enabled $($Mark.Back) -> $($now.Back); opener $($Mark.Opener) -> $($now.Opener)" }
}

<#
  One way into one menu: $Open presses (a click, a key, the menu key), then
    - a menu opens, its items all in $Allowed (none twice), every one of $Required there, none of $Forbidden;
    - nothing else happened: the page the same (Back, the opener's place), no request that changes or opens anything;
    - Escape closes it: the popup gone, the page the same, the keyboard back on $FocusBack (the opener by default).
  Returns the items' names ($null when no menu opened). $Name is the check's case, e.g. "Home, a card, the menu key".
#>
function Test-G2Menu {
    param([string]$Id, [string]$Name, $Opener, [scriptblock]$Open, [string[]]$Allowed = @(), [string[]]$Required = @(),
        [string[]]$Forbidden = @(), $FocusBack, [string]$ServerFolder, [switch]$KeepOpen, [switch]$Flyout)
    if (-not $FocusBack) { $FocusBack = $Opener }
    $script:g2Panel = $null
    G2-CloseFlyouts
    $mark = G2-PageMark $Opener
    # where the keyboard was before the press: a click on a button that doesn't take the focus leaves it there
    $before = Focused-Element
    # -Flyout: what the keyboard could reach before the press, so a panel that opens in the page (Cinematic's audio and
    # subtitles) is told by the keyboard moving into something that wasn't there
    $shownBefore = @{}
    $panel = $null
    if ($Flyout) {
        $fc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsKeyboardFocusableProperty, $true)
        foreach ($e in @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $fc))) { try { if (-not $e.Cached.IsOffscreen) { $shownBefore[[string]$e.Cached.Name] = $true } } catch {} }
    }
    $at = Get-Epoch
    & $Open
    # -Flyout: a flyout that isn't a menu: open when its presenter is there, its items whatever it holds
    $names = Wait-Until {
        $n = @(G2-MenuNames)
        if (-not $n.Count -and $Flyout) {
            if (@(G2-Flyouts).Count) { $n = @(G2-FlyoutNames); if (-not $n.Count) { $n = @('(a flyout)') } }
            elseif ($fp = G2-FocusedPopup) {
                $n = @(try { [OffscreenUi]::Under($fp, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { try { $_.Cached.Name -and $_.Cached.IsKeyboardFocusable } catch { $false } } | ForEach-Object { $_.Cached.Name } } catch {})
                if (-not $n.Count) { $n = @("(a flyout: $($fp.Current.ClassName))") }
            }
            else {
                $fc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
                $fe = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $fc) | Where-Object { try { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } catch { $false } })[0]
                if ($fe -and -not $shownBefore.ContainsKey([string]$fe.Cached.Name) -and -not (G2-HasFocus $Opener)) { $script:g2Panel = $fe; $n = @("(a panel: $($fe.Cached.Name))") }
            }
        }
        if ($n.Count) { , $n } } -Seconds 4 -PollMs 100
    $names = @($names | Where-Object { $null -ne $_ })
    Check "${Id} [$Name]: a $(if ($Flyout) { 'flyout' } else { 'menu' }) opens" ($names.Count -gt 0) $(if ($names.Count) { $names -join ' | ' } else { "nothing opened; focus on $(G2-Focused)" })
    if (-not $names.Count) { return $null }
    $odd = @($names | Where-Object { $Allowed.Count -and $_ -notin $Allowed })
    $twice = @($names | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name })
    $missing = @($Required | Where-Object { $_ -notin $names })
    $banned = @($names | Where-Object { $_ -in $Forbidden })
    Check "${Id} [$Name]: only this menu's items, each once" ($odd.Count -eq 0 -and $twice.Count -eq 0 -and $missing.Count -eq 0 -and $banned.Count -eq 0) `
        ("{0}{1}{2}{3}" -f $(if ($odd) { "not this menu's: $($odd -join ', '); " }), $(if ($twice) { "twice: $($twice -join ', '); " }), $(if ($missing) { "missing: $($missing -join ', '); " }), $(if ($banned) { "must not be there: $($banned -join ', ')" }))
    Start-Sleep -Milliseconds 1200
    $same = G2-SamePage $mark $Opener
    $stray = @(G2-StrayRequests $at $ServerFolder)
    Check "${Id} [$Name]: nothing else happens (the page stays, nothing sent or opened)" ($same.Ok -and $stray.Count -eq 0) "$($same.Detail)$(if ($stray) { '; sent: ' + ($stray -join ', ') })"
    if ($KeepOpen) { return , $names }
    G2-Key $g2Vk.Escape
    $closed = [bool](Wait-Until { -not (G2-MenuOpen) -and -not ($script:g2Panel -and (G2-OnScreen $script:g2Panel)) } -Seconds 3 -PollMs 100)
    if (-not $closed -and $Flyout) {
        # the keyboard inside the flyout (a box), not where the key went: once more where the focus is
        Key $g2Vk.Escape; $closed = [bool](Wait-Until { -not (G2-MenuOpen) -and -not ($script:g2Panel -and (G2-OnScreen $script:g2Panel)) } -Seconds 2 -PollMs 100)
        if ($closed) { Say "${Id} [$Name]: the flyout closed at the second Escape" }
    }
    Start-Sleep -Milliseconds 400
    $same = G2-SamePage $mark $Opener
    $now = Focused-Element
    $back = (G2-HasFocus $FocusBack) -or ($before -and $now -and $now.name -eq $before.name -and $now.type -eq $before.type)
    Check "${Id} [$Name]: Escape closes it, the page stays, the keyboard back where it was" ($closed -and $same.Ok -and $back) "closed: $closed; $($same.Detail); focus on $(G2-Focused) (before the press: $(if ($before) { "$($before.type) '$($before.name)'" } else { 'nothing' }))"
    G2-CloseFlyouts
    , $names
}
