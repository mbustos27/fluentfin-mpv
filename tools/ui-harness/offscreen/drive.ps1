<#
  Runs ON the private desktop that run-offscreen.ps1 creates (through tools\backend-bench\hidden_run.py, inside its
  kill-on-close job): starts the fake server and FluentFin (its own --data-dir), signs in (Quick Connect approved by
  the harness, so no password is typed; or -SignIn password with the fixture's generated test credentials), runs the
  steps, takes window-only screenshots and UI Automation dumps, measures, scans each page for accessibility gaps,
  closes FluentFin and the server. Refuses to run on the input desktop.

  Input: UI Automation patterns where an element has one; otherwise messages posted to FluentFin's own input window
  (WM_LBUTTONDOWN/UP, WM_KEYDOWN/UP) or to the video window of the mpv it started. SendInput is never used: it would
  go to the user's desktop.
  Processes: only the ones this script starts (tools\ui-harness\common.ps1's handle and start-time rules), and the job
  ends whatever is left when this script exits.

  Steps (-Steps, comma-separated, run in the order given; sign-in always comes first):
    login       capture the server and sign-in pages (sign-in itself always happens)
    home        wait for the home rows to fill, timings, capture (with a Tab walk)
    library     the Movies library from the Libraries page
    movie       the showcase movie's page (from its library)
    series      the showcase series' page
    season      its first season's page
    play        play the season's first episode, wait for the first frame, capture the player
    fullscreen  / windowed   full screen and back (mpv's f key, or F with FluentFin's controls)
    leave       stop (mpv's q, or Esc with FluentFin's controls): back on the season page, one Stopped, by the backend
    close       close FluentFin (WM_CLOSE) and check nothing it started survives
    relaunch    close it as close does and start it again on the same data folder: it signs itself in with the server
                and user the first sign-in saved (the automatic sign-in at start-up, "Signing in to ..."); on Home
    steady      after play: playback running (FluentFin's controls: Play again, the play step paused), then the middle
                of the video sampled over 2 s: nothing may move there (the test video is still there), so no ring or
                indicator is drawn over it (the 1.0 test build's ring: the startup panel's, stuck after an automatic
                sign-in, over the video of FluentFin's own mpv)
    nav:<Name>  invoke the navigation item <Name> (e.g. nav:Settings, nav:Diary) and capture the page as nav-<name>
    shot:<label>  capture the current page as <label>
    wait:<seconds>
  'all' = login,home,library,movie,series,season,play,fullscreen,windowed,leave,close (relaunch and steady are extra).
  -Then <script.ps1> is dot-sourced after the steps (before close): it can use this script's helpers (Capture-Page,
  Open-Nav (a top-bar tab, a Libraries menu item, Settings from the Account menu), Open-Card, Open-Library, Find-Ui, Wait-Ui, Invoke-Element, Key, Check, Say, Invoke-Server; $fixture).
  -Before <script.ps1> is dot-sourced the same way before the sign-in, on the server page; it must leave FluentFin on
  the server page (TxtUrl) with no rule in force that stops the sign-in that follows.
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$DataDir,
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$Media,
    [double]$Latency = 0,
    [string]$LatencyRulesFile,
    [string]$Steps = 'all',
    [string]$StepsFile,
    [ValidateSet('quickconnect', 'password')][string]$SignIn = 'quickconnect',
    [ValidateSet('Mpv', 'FluentFin')][string]$PlayerControls = 'Mpv',
    [string]$Then,
    [string]$Before,
    [switch]$NoA11y,
    [int]$TabStops = 60,
    [int]$CloseSeconds = 20
)

$ErrorActionPreference = 'Stop'
$DataDir = $DataDir.TrimEnd('\', '/')   # quoted, a trailing backslash would escape the closing quote ("X\" arrives as X")
if ($StepsFile) { $Steps = (Get-Content -LiteralPath $StepsFile -Raw -Encoding utf8).Trim() }
$LatencyRule = if ($LatencyRulesFile) { @(Get-Content $LatencyRulesFile -Raw | ConvertFrom-Json) } else { @() }
. (Join-Path $PSScriptRoot '..\common.ps1')
. (Join-Path $PSScriptRoot 'uia.ps1')

$desktop = [OffscreenUi]::ThisDesktop()
$inputDesktop = [OffscreenUi]::InputDesktop()
if (-not $desktop -or $desktop -eq 'Default' -or $desktop -eq $inputDesktop) {
    throw "drive.ps1 runs only on a private desktop (this thread's desktop: '$desktop', the input desktop: '$inputDesktop'); use run-offscreen.ps1"
}

$Fixed = @('login', 'home', 'library', 'movie', 'series', 'season', 'play', 'fullscreen', 'windowed', 'leave', 'close')
$Extra = @('relaunch', 'steady')   # not in 'all'
$wanted = if ($Steps.Trim() -eq 'all') { $Fixed } else { @($Steps -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
foreach ($s in $wanted) {
    if ($Fixed -notcontains $s -and $Extra -notcontains $s -and $s -notmatch '^(nav|shot):.+$' -and $s -notmatch '^wait:\d+(\.\d+)?$') {
        throw "-Steps: unknown step '$s' (steps: $(($Fixed + $Extra) -join ', '), nav:<Name>, shot:<label>, wait:<seconds>)"
    }
}
if ($Then) { $Then = [IO.Path]::GetFullPath($Then); if (-not (Test-Path $Then)) { throw "-Then: no script at $Then" } }
if ($Before) { $Before = [IO.Path]::GetFullPath($Before); if (-not (Test-Path $Before)) { throw "-Before: no script at $Before" } }

New-Item -ItemType Directory -Force $Out, (Join-Path $Out 'shots'), (Join-Path $Out 'uia'), (Join-Path $Out 'a11y'), (Join-Path $Out 'server') | Out-Null
$log = Join-Path $Out 'drive.log'
function Say([string]$Text) {
    $line = "[{0:HH:mm:ss.fff}] {1}" -f (Get-Date), $Text
    Add-Content -LiteralPath $log -Value $line -Encoding utf8
    Write-Host $line
}

$timings = [ordered]@{}
$shots = [ordered]@{}
$dumps = [ordered]@{}
$checks = [ordered]@{}
$script:ui = $null; $script:hwnd = [IntPtr]::Zero; $script:serverUrl = $null; $script:shotN = 0
$script:page = $null          # where the driver believes it is: home, library:<name>, movie, series, season, player, ...
$script:signedInAt = $null
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    $checks[$Name] = [ordered]@{ ok = $Ok; detail = $Detail }
    Say ("{0} {1}{2}" -f $(if ($Ok) { 'PASS' } else { 'FAIL' }), $Name, $(if ($Detail) { ": $Detail" } else { '' }))
}
function Ms([double]$From, [double]$To) { if ($From -and $To) { [int][Math]::Round(($To - $From) * 1000) } else { $null } }
function Safe-Name([string]$Name) { ($Name -replace '[^A-Za-z0-9._-]+', '-').Trim('-').ToLowerInvariant() }

# --- captures -------------------------------------------------------------------------------------------------------
function Shot([string]$Name) {
    $script:shotN++
    $file = Join-Path $Out ("shots\{0:00}-{1}.png" -f $script:shotN, $Name)
    $info = [OffscreenUi]::Shot($script:hwnd, $script:ui.Id, $file)
    $shots[$Name] = $file
    Say "shot $Name -> $file ($info)"
}
function Dump-Tree([string]$Name) {
    $file = Join-Path $Out "uia\$Name.txt"
    [IO.File]::WriteAllText($file, [OffscreenUi]::DumpTree($script:hwnd, $script:ui.Id), [Text.UTF8Encoding]::new($false))
    $dumps[$Name] = $file
}
# the page as it is now: a screenshot (shots\NN-<name>.png), its UI Automation tree (uia\<name>.txt) and, unless
# -NoA11y, the accessibility scan (a11y\<name>.json) and with -TabWalk the Tab order (a11y\<name>-tab.json)
function Capture-Page([string]$Name, [switch]$TabWalk) {
    $Name = Safe-Name $Name
    Shot $Name
    Dump-Tree $Name
    Invoke-A11yScan $Name -TabWalk:$TabWalk
}

# --- requests the fake server logged (its clock is this machine's) ------------------------------------------------
$requestLog = Join-Path $Out 'server\requests.jsonl'
function Get-Requests([double]$Since = 0) {
    if (-not (Test-Path $requestLog)) { return @() }
    @(Get-Content -LiteralPath $requestLog -Encoding utf8 | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.t -ge $Since })
}
# when a request finished (its start + its duration), epoch seconds
function Get-Done($Request) { $Request.t + $Request.ms / 1000.0 }
# Who sent a request, by its User-Agent (FluentFin and its backend both say Client="Nixie", and FluentFin's
# signed-in SDK requests carry no Client at all): 'ui' = FluentFin (the Jellyfin SDK's kiota-dotnet, WinUI's image
# loader), 'backend' = the mpv backend (python, "<client name>/<version>"), 'mpv' = mpv's own HTTP (the stream),
# 'harness' = this script.
function Get-Origin($Request) {
    $ua = [string]$Request.ua
    if ($ua -like 'kiota-dotnet/*' -or $ua -match 'Trident/|MSIE ') { return 'ui' }
    if ($ua -match '^(Nixie|Seaglass|FluentFin|Jellyfin-MPV-Shim)/') { return 'backend' }
    if ($ua -match '^(Lavf|libmpv|mpv)') { return 'mpv' }
    if ($ua -match 'WindowsPowerShell') { return 'harness' }
    'other'
}

function Invoke-Server([string]$Path, [string]$Method = 'GET', $Body = $null) {
    $args_ = @{ Uri = "$($script:serverUrl)$Path"; Method = $Method; UseBasicParsing = $true; TimeoutSec = 10 }
    if ($Body) { $args_.Body = ($Body | ConvertTo-Json -Compress); $args_.ContentType = 'application/json' }
    (Invoke-WebRequest @args_).Content | ConvertFrom-Json
}

# --- accessibility ---------------------------------------------------------------------------------------------------
function Invoke-A11yScan([string]$Page, [switch]$TabWalk) {
    if ($NoA11y) { return }
    $file = Join-Path $Out "a11y\$Page.json"
    $json = [OffscreenUi]::Scan($script:hwnd, $script:ui.Id, $Page)
    [IO.File]::WriteAllText($file, $json, [Text.UTF8Encoding]::new($false))
    $scan = $json | ConvertFrom-Json
    Say ("a11y {0}: {1} visible, {2} interactive, {3} unnamed, {4} not keyboard-focusable, {5} without a control type, {6} images not reachable by keyboard -> {7}" -f
        $Page, $scan.visible, $scan.interactive, @($scan.unnamedInteractive).Count, @($scan.interactiveNotKeyboardFocusable).Count,
        @($scan.withoutControlType).Count, @($scan.imagesNotReachableByKeyboard).Count, $file)
    if ($TabWalk) { Invoke-TabWalk $Page }
}

# Tab through the page from its first focusable element in the content: each focused element (as FluentFin's UI reports
# it), until the focus comes back to the first one or after -TabStops presses. Tab is posted to FluentFin's own input
# window. If a posted Tab does not move the focus, the file says so and lists the focusable elements in tree order
# instead (what Tab visits when no TabIndex reorders it), marked "approximate".
function Invoke-TabWalk([string]$Page) {
    $file = Join-Path $Out "a11y\$Page-tab.json"
    $order = New-Object System.Collections.ArrayList
    $method = 'tab'
    $startEl = [OffscreenUi]::FirstContentFocusable($script:hwnd, $script:ui.Id, 80, 60)
    if ($startEl) { [void][OffscreenUi]::SetFocus($startEl); Start-Sleep -Milliseconds 200 }
    $first = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
    if ($first) { [void]$order.Add($first) }
    for ($i = 0; $i -lt $TabStops; $i++) {
        Key 0x09
        Start-Sleep -Milliseconds 150
        $now = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id)
        if (-not $now) { [void]$order.Add('{"note":"no element of FluentFin reports the focus"}'); break }
        if ($order.Count -and $now -eq $order[$order.Count - 1] -and $i -eq 0) { $method = 'tree-order (approximate): a posted Tab did not move the focus'; break }
        if ($now -eq $first) { break }
        [void]$order.Add($now)
    }
    if ($method -ne 'tab') { $order = [System.Collections.ArrayList]@([OffscreenUi]::FocusableInTreeOrder($script:hwnd, $script:ui.Id)) }
    $stops = @($order | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.PSObject.Properties['type'] })
    $unnamed = @($stops | Where-Object { -not $_.name })
    $json = '{"page":"' + $Page + '","method":' + (ConvertTo-Json $method) + ',"stops":[' + ($order -join ',') + ']}'
    [IO.File]::WriteAllText($file, $json, [Text.UTF8Encoding]::new($false))
    Say ("tab order {0} ({1}): {2} stops, {3} unnamed -> {4}" -f $Page, $method, $stops.Count, $unnamed.Count, $file)
}

# --- UI helpers --------------------------------------------------------------------------------------------------------
function Find-Ui([string]$Name, [string]$Id, [string]$Type, [switch]$Contains, [switch]$OffscreenToo) {
    [OffscreenUi]::Find($script:hwnd, $script:ui.Id, $Name, $Id, $Type, [bool]$Contains, [bool]$OffscreenToo)
}
function Wait-Ui([string]$Name, [string]$Id, [string]$Type, [switch]$Contains, [switch]$OffscreenToo, [double]$Seconds = 20, [int]$PollMs = 150) {
    Wait-Until { Find-Ui -Name $Name -Id $Id -Type $Type -Contains:$Contains -OffscreenToo:$OffscreenToo } -Seconds $Seconds -PollMs $PollMs
}
# a key posted to the window FluentFin reads keys from (its XAML island's input site; virtual-key code)
# ($script:lastKeyWindow: the window it went to)
function Key([int]$Vk, [switch]$Shift) { $script:lastKeyWindow = [OffscreenUi]::Key($script:hwnd, $script:ui.Id, $Vk, [bool]$Shift) }

# opens a media card: the card is one UIA button named after its item (a11y-items) that opens the item on Invoke; an
# older build's card (an image that opens on a pointer tap) gets a posted click on its image, above its title
function Open-Card([string]$Title, [double]$Seconds = 30) {
    $card = Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $Title) } -Seconds $Seconds -PollMs 200
    if ($card) { Say "card '$Title' ($(Invoke-Element $card))"; return }
    $text = Find-Ui -Name $Title -Type 'Text'
    if (-not $text) { throw "no card titled '$Title' on screen" }
    $r = [OffscreenUi]::RectOf($text)
    $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y - 60)
    [OffscreenUi]::Hover($script:hwnd, $script:ui.Id, $x, $y)
    Start-Sleep -Milliseconds 250
    [OffscreenUi]::Click($script:hwnd, $script:ui.Id, $x, $y)
    Say "card '$Title' (clicked)"
}
# a navigation pane item (ListItem), searched for inside the pane only (found once after sign-in): a search of the whole
# window takes seconds on a page with many cards
# 1.2.0 (the top bar, no rail): a section by name. A tab of the bar's tab list (TopTabStrip, AutomationId TopTabs); in a narrow
# window the tabs are one menu button (TopTabsMenu) whose menu has them; a library inside the "Libraries" tab's menu (many
# libraries); Settings from the Account menu (it has no tab). Invoked through UI Automation.
function Open-NavMenuItem([string]$Name) {
    $mi = Wait-Until { Find-Ui -Name $Name -Type 'MenuItem' } -Seconds 8 -PollMs 150
    if (-not $mi) { throw "no menu item '$Name'" }
    $mi
}
function Open-Nav([string]$Name) {
    if ($Name -eq 'Settings') {
        $acct = Wait-Until { Find-Ui -Name 'Account, ' -Type 'Button' -Contains } -Seconds 15 -PollMs 150
        if (-not $acct) { throw "no Account button (Settings is in the Account menu)" }
        [void](Invoke-Element $acct)
        $set = Wait-Ui -Name 'Settings' -Type 'Button' -Seconds 8
        if (-not $set) { throw "no Settings in the Account menu" }
        Say "nav 'Settings' (Account menu: $(Invoke-Element $set))"
        return
    }
    $tab = Wait-Until {
        if (Find-Ui -Id 'TopTabsMenu') { return 'menu' }
        try {
            if (-not $script:navPane) { $script:navPane = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id) }
            if ($script:navPane) {
                $t = [OffscreenUi]::NavItem($script:navPane, $Name)
                if ($t) { return $t }
                if ([OffscreenUi]::NavItem($script:navPane, 'Home')) { return 'not-a-tab' }
            }
        } catch { $script:navPane = $null }
    } -Seconds 15 -PollMs 150
    if (-not $tab) { throw "no navigation tab '$Name'" }
    if ($tab -eq 'menu') {
        # a narrow window: the sections' menu (a library of many: its Libraries submenu)
        [void](Invoke-Element (Find-Ui -Id 'TopTabsMenu'))
        $mi = Find-Ui -Name $Name -Type 'MenuItem'
        if (-not $mi) { $sub = Find-Ui -Name 'Libraries' -Type 'MenuItem'; if ($sub) { [void](Invoke-Element $sub) }; $mi = Open-NavMenuItem $Name }
        Say "nav '$Name' (sections menu: $(Invoke-Element $mi))"
        return
    }
    if ($tab -eq 'not-a-tab') {
        # a library inside the "Libraries" tab's menu, or the Libraries page itself ("All libraries")
        $libs = [OffscreenUi]::NavItem($script:navPane, 'Libraries')
        if (-not $libs) { throw "no navigation tab '$Name' (nor a Libraries tab with it)" }
        [void](Invoke-Element $libs)
        $mi = Open-NavMenuItem $(if ($Name -eq 'Libraries') { 'All libraries' } else { $Name })
        Say "nav '$Name' (Libraries menu: $(Invoke-Element $mi))"
        return
    }
    $how = Invoke-Element $tab
    if ($Name -eq 'Libraries') {
        # the Libraries tab opens its menu when there are libraries to list: its page is "All libraries"
        $all = Wait-Until { Find-Ui -Name 'All libraries' -Type 'MenuItem' } -Seconds 3 -PollMs 150
        if ($all) { $how = "menu: $(Invoke-Element $all)" }
    }
    Say "nav '$Name' ($how)"
}
# a library: its tab (a few libraries are tabs of their own), else through the Libraries tab's menu (Open-Nav); a build with a
# Libraries page and no tab for it: the page's tile (an ItemContainer named after the library)
function Open-Library([string]$Name) {
    try { Open-Nav $Name; $script:page = "library:$Name"; return }
    catch { Say "library '$Name': $($_.Exception.Message); trying the Libraries page" }
    Open-Nav 'Home'
    Start-Sleep -Milliseconds 300
    Open-Nav 'Libraries'
    $tile = Wait-Until { [OffscreenUi]::FindByNameAndClass($script:hwnd, $script:ui.Id, $Name, 'Microsoft.UI.Xaml.Controls.ItemContainer') } -Seconds 15 -PollMs 200
    if ($tile) { Say "library '$Name' ($(Invoke-Element $tile))"; $script:page = "library:$Name"; return }
    throw "no library '$Name'"
}
# an item page is up once its title (a Text with the item's exact name) shows
# the item page's title: its text, or (Seaglass) the logo image that stands in for it, named after the item; matched by its id
# so a card of the same name on the page being left does not count
function Wait-ItemPage([string]$Name, [double]$Seconds = 20) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $hit = Find-Ui -Name $Name -Type 'Text'
        if (-not $hit) { $hit = Find-Ui -Name $Name -Id 'LogoImage' }
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)
    $null
}

# the video window: a visible child window of FluentFin's that belongs to the mpv this run started (mpv's own controls)
function Get-VideoWindow {
    $video = @([OffscreenUi]::Children($script:hwnd) | Where-Object { [OffscreenUi]::ProcessOf($_) -ne $script:ui.Id -and [OffscreenUi]::IsWindowVisible($_) }) | Select-Object -First 1
    if (-not $video) { return $null }
    $mpvPid = [int][OffscreenUi]::ProcessOf($video)
    [void](Get-Descendants $script:ui.Id)
    if (-not (Get-TrackedProcess $mpvPid)) { throw "the video window's process $mpvPid is not one this script started" }
    $video
}
# a key to the player: mpv's own window with mpv's controls (WM_KEYDOWN/WM_CHAR/WM_KEYUP posted to it), FluentFin's
# input window with FluentFin's controls
function Send-PlayerKey([int]$Vk, [char]$Char) {
    if ($PlayerControls -eq 'Mpv') {
        $video = Get-VideoWindow
        if (-not $video) { throw "no mpv video window to send '$Char' to" }
        $scan = [int64]([OffscreenUi]::ScanCode($Vk)) -shl 16
        [void][OffscreenUi]::PostMessage($video, 0x0100, [IntPtr]$Vk, [IntPtr](1 -bor $scan))
        [void][OffscreenUi]::PostMessage($video, 0x0102, [IntPtr][int]$Char, [IntPtr](1 -bor $scan))
        [void][OffscreenUi]::PostMessage($video, 0x0101, [IntPtr]$Vk, [IntPtr](1 -bor $scan -bor 0xC0000000))
    }
    else { Key $Vk }
}

# --- steps ------------------------------------------------------------------------------------------------------------
function Step-SignIn([bool]$Capture) {
    $url = Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 30
    $timings.server_page_ms = Ms $script:launchedAt (Get-Epoch)
    if (-not $url) { throw "no server page (TxtUrl) within 30 s" }
    # let the page's entrance and the backdrop's 600 ms fade finish first (a shot taken at once caught the title at 4.2:1)
    if ($Capture) { Start-Sleep -Milliseconds 1200; Capture-Page 'server' }
    Set-ElementValue $url $script:serverUrl
    # the server page's "Connect" (1.2.0, the button review's S13; "Next" before)
    $next = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Connect'); if (-not $next) { $next = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Next') }
    $nextAt = Get-Epoch
    [void](Invoke-Element $next)
    # the sign-in page: "Use Quick Connect" (1.1.4; "Quick Connect" before)
    $quick = Wait-Until { $q = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Use Quick Connect'); if (-not $q) { $q = [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Quick Connect') }; $q } -Seconds 20 -PollMs 150
    if (-not $quick) { throw "no sign-in page (no Quick Connect button) within 20 s" }
    $timings.server_to_login_ms = Ms $nextAt (Get-Epoch)
    Start-Sleep -Milliseconds 300
    if ($Capture) { Start-Sleep -Milliseconds 900; Capture-Page 'login' }
    $at = Get-Epoch
    if ($SignIn -eq 'quickconnect') {
        # as a user would: Quick Connect shows a code, approved on "another device" (the fake server's harness endpoint);
        # FluentFin's next poll signs in with the fresh token the server issues. No password anywhere.
        [void](Invoke-Element $quick)
        # the code: the dialog's QuickConnectCode text (1.1.4: named digit by digit, "1 2 3 4 5 6"), or an older build's
        # "Enter code 123456" line
        $code = Wait-Until {
            $t = Find-Ui -Id 'QuickConnectCode' -Type 'Text'
            if ($t -and ($t.Current.Name -replace '\s', '') -match '^(\d{6})$') { return $Matches[1] }
            $t = Find-Ui -Name 'Enter code' -Contains -Type 'Text'; if ($t -and $t.Current.Name -match '(\d{6})') { $Matches[1] } } -Seconds 20 -PollMs 100
        if (-not $code) { throw "Quick Connect showed no code within 20 s" }
        if ($Capture) { Shot 'quickconnect' }
        [void](Invoke-Server '/__harness/quickconnect/approve' 'POST' @{ code = $code })
        Say "Quick Connect: the code FluentFin shows was approved"
    }
    else {
        $creds = Get-Content (Join-Path $PSScriptRoot '..\fakeserver\credentials.json') -Raw | ConvertFrom-Json
        $password = [OffscreenUi]::FindByClass($script:hwnd, $script:ui.Id, 'Edit', 'PasswordBox')
        $userBox = @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | Where-Object { try { $_.Cached.ControlType.ProgrammaticName -eq 'ControlType.Edit' -and $_.Cached.ClassName -ne 'PasswordBox' -and -not $_.Cached.IsOffscreen } catch { $false } }) | Select-Object -First 1
        if (-not $password -or -not $userBox) { throw "no username or password box on the sign-in page" }
        Set-ElementValue $userBox $creds.username
        Set-ElementValue $password $creds.password   # the fake server's generated test password (never a real one)
        [void](Invoke-Element ([OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, 'Login')))
        Say "signed in with the fixture's test credentials"
    }
    $auth = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'authenticate' -and $_.status -eq 200 }) | Select-Object -First 1 } -Seconds 30 -PollMs 100
    if (-not $auth) { throw "no successful sign-in at the server within 30 s" }
    $script:signedInAt = Get-Done $auth
    $timings.sign_in_ms = Ms $at $script:signedInAt
    $shell = Wait-Until { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); if ($p -and ([OffscreenUi]::NavItem($p, 'Home') -or (Find-Ui -Id 'TopTabsMenu'))) { $p } } -Seconds 30 -PollMs 150
    $script:navPane = $shell
    Check "signed in ($SignIn)" ([bool]$shell) "$($timings.sign_in_ms) ms to the server's answer"
    if (-not $shell) { throw "no navigation pane after signing in" }
    $script:page = 'home'
}

function Step-Home {
    if ($script:page -ne 'home') { Open-Nav 'Home'; $script:page = 'home' }
    $since = $script:signedInAt
    $views = @($fixture.views)
    # Nixie 1.3.0: a row counts under any of its names in the layout themes (Drive-in's sign says "Coming up", Cinematic's
    # and Editorial's list "Up next", which Editorial also gives Continue Watching's items)
    $groups = @(, @('Continue Watching', 'Continue watching', 'Up next')) + @(, @('Next Up', 'Coming up', 'Coming Up', 'COMING UP', 'Up next')) +
        @($views | ForEach-Object { , @("New in $_") })   # Seaglass 1.1.4: the rows are "New in <library>"
    $headers = @($groups | ForEach-Object { $_[0] })
    $names = [string[]]@($groups | ForEach-Object { $_ } | Select-Object -Unique)
    # progressive home: the headers show at once with placeholder cards (Shimmer) that turn into cards
    # (BaseItemDtoCard) as each row's request answers; loaded = cards and no placeholder left
    $firstCards = $null; $loaded = $null; $last = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while ([DateTime]::UtcNow -lt $deadline) {
        $n = [OffscreenUi]::CountByClass($script:hwnd, $script:ui.Id, @('BaseItemDtoCard', 'Shimmer'))
        $now = Get-Epoch
        if (-not $firstCards -and $n['BaseItemDtoCard'] -gt 0) { $firstCards = $now }
        $last = $n
        if ($n['BaseItemDtoCard'] -gt 0 -and $n['Shimmer'] -eq 0) { $loaded = $now; break }
        Start-Sleep -Milliseconds 100
    }
    $timings.home_first_cards_ms = Ms $since $firstCards
    $timings.home_visible_rows_loaded_ms = Ms $since $loaded
    Check 'home: the rows in view loaded (no placeholder left)' ([bool]$loaded) ("{0} cards, {1} placeholders" -f $last['BaseItemDtoCard'], $last['Shimmer'])
    # the rows below the fold exist only once scrolled to (the rows are an ItemsRepeater): to the bottom, until every
    # header is there and no placeholder is left, then back to the top
    $present = @(); $allAt = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 100)
        Start-Sleep -Milliseconds 150
        $shown = @([OffscreenUi]::PresentTexts($script:hwnd, $script:ui.Id, $names))
        $present = @($groups | Where-Object { $g = $_; @($g | Where-Object { $shown -contains $_ }).Count -gt 0 } | ForEach-Object { $_[0] })
        $n = [OffscreenUi]::CountByClass($script:hwnd, $script:ui.Id, @('Shimmer'))
        if ($present.Count -eq $headers.Count -and $n['Shimmer'] -eq 0) { $allAt = Get-Epoch; break }
    }
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
    Check 'home: every row has its header and loaded (scrolled to the bottom)' ([bool]$allAt) ("{0}/{1} headers{2}" -f $present.Count, $headers.Count, $(if ($present.Count -lt $headers.Count) { '; missing: ' + (($headers | Where-Object { $present -notcontains $_ }) -join ', ') }))
    Start-Sleep -Milliseconds 500
    Start-Sleep -Seconds 2   # images
    # the same from the server's log: when each row's data was sent, and the card images
    $reqs = @(Get-Requests $since)
    $rows = @($reqs | Where-Object { (Get-Origin $_) -eq 'ui' -and $_.route -in @('resume', 'nextup', 'latest') -and $_.status -eq 200 })
    if ($rows) {
        $done = @($rows | ForEach-Object { Get-Done $_ })
        $timings.home_first_row_data_ms = Ms $since ($done | Measure-Object -Minimum).Minimum
        $timings.home_all_rows_data_ms = Ms $since ($done | Measure-Object -Maximum).Maximum
    }
    $images = @($reqs | Where-Object { $_.route -eq 'image' })
    if ($images) {
        $timings.home_images = $images.Count
        $timings.home_first_card_image_ms = Ms $since ($images | Measure-Object -Property t -Minimum).Minimum
        $timings.home_images_done_ms = Ms $since (($images | ForEach-Object { Get-Done $_ }) | Measure-Object -Maximum).Maximum
    }
    Say ("home: first cards {0} ms, rows in view loaded {1} ms (rows' data {2}-{3} ms), {4} images, first requested {5} ms, all by {6} ms" -f $timings.home_first_cards_ms,
        $timings.home_visible_rows_loaded_ms,$timings.home_first_row_data_ms, $timings.home_all_rows_data_ms, $timings.home_images, $timings.home_first_card_image_ms, $timings.home_images_done_ms)
    Capture-Page 'home' -TabWalk
}

function Step-Library {
    $at = Get-Epoch
    Open-Library $fixture.movie.library
    $card = Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150
    $timings.library_first_card_ms = Ms $at (Get-Epoch)
    Check 'library: the showcase movie is shown' ([bool]$card) $fixture.movie.name
    Start-Sleep -Seconds 2
    Capture-Page 'library'
}

# the fixture episode's row: the row itself plays it (a list item, or a button on its own; its Play disc is only a picture
# since 1.2.0), else the row's Play button of an older build ("Play, episode 1, <name>")
function Find-EpisodeRow {
    $row = Find-Ui -Name $fixture.episode.name -Type 'ListItem' -Contains -OffscreenToo
    if ($row) { return $row }
    [OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, 'Play, ', $fixture.episode.name)
}

function Step-Movie {
    if ($script:page -ne "library:$($fixture.movie.library)") { Open-Library $fixture.movie.library }
    $at = Get-Epoch
    Open-Card $fixture.movie.name
    $page = Wait-ItemPage $fixture.movie.name
    $timings.movie_page_ms = Ms $at (Get-Epoch)
    Check 'movie page opened' ([bool]$page) $fixture.movie.name
    $script:page = 'movie'
    Start-Sleep -Seconds 2
    Capture-Page 'movie' -TabWalk
}

function Step-Series([bool]$Capture = $true) {
    if ($script:page -ne "library:$($fixture.series.library)") { Open-Library $fixture.series.library }
    $at = Get-Epoch
    Open-Card $fixture.series.name
    $page = Wait-ItemPage $fixture.series.name
    $timings.series_page_ms = Ms $at (Get-Epoch)
    Check 'series page opened' ([bool]$page) $fixture.series.name
    $script:page = 'series'
    Start-Sleep -Seconds 2
    if ($Capture) { Capture-Page 'series' -TabWalk }
}

function Step-Season([bool]$Capture = $true) {
    if ($script:page -ne 'series') { Step-Series $false }
    $at = Get-Epoch
    # 1.2.0: the series page lists the episodes with a season picker, and its "<season> details" link opens the season's
    # page (a build with season cards on the series page: the card)
    # (scrolled out of view too: the series step's Tab walk leaves the page at its bottom, and Invoke needs no view)
    $link = Wait-Until { Find-Ui -Name "$($fixture.season.name) details" -Type 'Hyperlink' -OffscreenToo } -Seconds 10 -PollMs 200
    if ($link) { Say "season link '$($fixture.season.name) details' ($(Invoke-Element $link))" } else { Open-Card $fixture.season.name }
    # the season page's own Watched ("Mark Season 1 of <series> as ..."), so the series page's rows do not count
    $play = Wait-Until {
        if ([OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $($fixture.season.name) of ", '')) {
            Find-EpisodeRow
        }
    } -Seconds 20 -PollMs 150
    $timings.season_page_ms = Ms $at (Get-Epoch)
    Check 'season page opened (its episodes listed)' ([bool]$play) "$($fixture.season.name): $($fixture.episode.name)"
    $script:page = 'season'
    Start-Sleep -Seconds 2
    if ($Capture) { Capture-Page 'season' -TabWalk }
}

function Step-Play {
    if ($script:page -ne 'season') { Step-Season $false }
    # the episode's row plays it (Find-EpisodeRow)
    $play = Find-EpisodeRow
    if (-not $play) { throw "no Play button for '$($fixture.episode.name)'" }
    $playAt = Get-Epoch
    [void](Invoke-Element $play)
    Say "play invoked on '$($fixture.episode.name)' (player controls: $PlayerControls)"
    $script:page = 'player'
    if ($PlayerControls -eq 'Mpv') {
        $lit = Wait-Until { $v = [OffscreenUi]::VideoLit($script:hwnd, $script:ui.Id); if ($v -gt 20) { $v } } -Seconds 60 -PollMs 50
        $what = 'the embedded mpv window is not black'
    }
    else {
        # mpv draws inside FluentFin (composition): the middle of the window, approximate (the loading screen is dark)
        $lit = Wait-Until { $v = [OffscreenUi]::RegionLit($script:hwnd, $script:ui.Id, 0.3, 0.3, 0.7, 0.7); if ($v -gt 20) { $v } } -Seconds 60 -PollMs 50
        $what = 'the middle of the window is not black (approximate)'
    }
    $timings.first_frame_visible_ms = Ms $playAt (Get-Epoch)
    if ($PlayerControls -eq 'FluentFin') {
        # FluentFin's controls hide after 3 idle seconds and only real pointer input shows them again (WinUI ignores posted
        # mouse moves), so the driver pauses while they show (paused, they stay up) and leaves the keyboard focus on
        # Play/Pause: the later steps try the keyboard first and the controls' buttons second
        $pp = Wait-Ui -Id 'PlayPauseButton' -Seconds 5 -PollMs 50
        if ($pp) {
            [void](Invoke-Element $pp)
            [void][OffscreenUi]::SetFocus($pp)
            $held = Wait-Until { $b = Find-Ui -Id 'PlayPauseButton'; if ($b -and $b.Current.Name -eq 'Play') { $b } } -Seconds 5 -PollMs 100
            Say "paused through the controls' Play/Pause so they stay up ($(if ($held) { 'paused' } else { 'not paused?' }))"
        }
        else { Say "FluentFin's controls did not show" }
    }
    Check "video shows ($what)" ([bool]$lit) "$lit% not black"
    # the backend reports Playing once mpv plays (with FluentFin's controls it attaches to FluentFin's mpv first)
    [void](Wait-Until { @(Get-Requests $playAt | Where-Object { $_.route -eq 'report-playing' }) | Select-Object -First 1 } -Seconds 15 -PollMs 200)
    $reqs = @(Get-Requests $playAt)
    $pbi = $reqs | Where-Object { $_.route -eq 'playbackinfo' } | Select-Object -First 1
    $stream = $reqs | Where-Object { $_.route -eq 'stream' } | Select-Object -First 1
    $playing = $reqs | Where-Object { $_.route -eq 'report-playing' } | Select-Object -First 1
    if ($pbi) { $timings.playbackinfo_ms = Ms $playAt (Get-Done $pbi) }
    if ($stream) { $timings.first_stream_request_ms = Ms $playAt $stream.t }
    if ($playing) { $timings.playing_report_ms = Ms $playAt $playing.t }
    $reporters = @($reqs | Where-Object { $_.route -like 'report-*' } | ForEach-Object { Get-Origin $_ } | Sort-Object -Unique)
    Check 'playback reported by the backend only' ($playing -and ($reporters -join ',') -eq 'backend') "reports from: $(if ($reporters) { $reporters -join ', ' } else { 'nobody' })"
    # silent: mpv says which audio output it opened ("AO: [null] ..."), and the backend's log relays it. (Not a log file
    # of mpv's own: that logs verbosely, the access token in the request headers among it.)
    $aoLines = Wait-Until {
        $f = Join-Path $DataDir 'Logs\mpv-backend.log'
        $l = @(if (Test-Path $f) { Get-Content -LiteralPath $f -Encoding utf8 | Where-Object { $_ -match 'AO: \[' -and $_ -notmatch 'Setting option' } })
        if ($l) { , $l }
    } -Seconds 10 -PollMs 250
    $loud = @($aoLines | Where-Object { $_ -notmatch 'AO: \[null\]' })
    Check 'silent: mpv opened no audio device (AO: [null])' ($aoLines -and -not $loud) $(if ($loud) { $loud[0] } elseif ($aoLines) { 'AO: [null]' } else { 'no AO line in mpv-backend.log' })
    Start-Sleep -Seconds 3
    Capture-Page 'player'
}

function Step-FullScreen([bool]$On) {
    $before = Get-WindowState $script:hwnd
    # mpv's controls: mpv's f key in mpv's window. FluentFin's controls: F posted to FluentFin (the keyboard focus was put
    # on the controls when playback began); if that does nothing (the focus went away when the controls hid), the check
    # FAILs, and the Full screen button (through UI Automation, when it shows) still does it so the steps after it can run
    $how = 'f'; $keyWorked = $true
    Send-PlayerKey 0x46 'f'
    $state = Wait-WindowState $script:hwnd $On $(if ($PlayerControls -eq 'Mpv') { 8 } else { 3 })
    if ($PlayerControls -eq 'FluentFin' -and [bool]$state.FullScreen -ne $On) {
        $keyWorked = $false
        $btn = Find-Ui -Id 'FullScreenButton'
        if ($btn) { [void](Invoke-Element $btn); $how = 'F did nothing; the Full screen button did it'; $state = Wait-WindowState $script:hwnd $On 8 }
        else { $how = 'F did nothing and the controls are hidden' }
    }
    if ($On) { Check "full screen ($how)" ($keyWorked -and $state.FullScreen) "$($before.Rect) -> $($state.Rect) on $($state.Monitor)" }
    else { Check "windowed again ($how)" ($keyWorked -and -not $state.FullScreen) $state.Rect }
    Start-Sleep -Seconds 1
    Shot $(if ($On) { 'fullscreen' } else { 'windowed' })
}

function Step-Leave {
    $at = Get-Epoch
    # mpv's stop key q in mpv's window / the Back key (VK_BROWSER_BACK, as Alt+Left and Mouse 4) posted to FluentFin with its
    # controls: since 1.2.0 Esc only leaves full screen and never the player (docs\SHORTCUTS.md). The key doing nothing
    # FAILs, and their Back button (when shown) still leaves so the steps after it can run
    $how = if ($PlayerControls -eq 'Mpv') { 'q' } else { 'the Back key' }
    $keyWorked = $true
    if ($PlayerControls -eq 'Mpv') { Send-PlayerKey 0x51 'q' } else { Key 0xA6 }
    $back = Wait-Until { Find-EpisodeRow } -Seconds $(if ($PlayerControls -eq 'Mpv') { 20 } else { 5 }) -PollMs 150
    if (-not $back -and $PlayerControls -eq 'FluentFin') {
        $keyWorked = $false
        $btn = Find-Ui -Id 'BackButton'
        if ($btn) { [void](Invoke-Element $btn); $how = 'the Back key did nothing; the Back button left' } else { $how = 'the Back key did nothing and the controls are hidden' }
        $back = Wait-Until { Find-EpisodeRow } -Seconds 15 -PollMs 150
    }
    $timings.leave_player_ms = Ms $at (Get-Epoch)
    $stopped = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'report-stopped' }) | Select-Object -First 1 } -Seconds 15
    Start-Sleep -Seconds 1
    $stops = @(Get-Requests $at | Where-Object { $_.route -eq 'report-stopped' })
    Check "left the player ($how; back on the season page)" ($keyWorked -and [bool]$back)
    Check 'stop reported once, by the backend' ($stops.Count -eq 1 -and (Get-Origin $stops[0]) -eq 'backend') ("{0} Stopped{1}" -f $stops.Count, $(if ($stops) { ' from ' + (($stops | ForEach-Object { Get-Origin $_ }) -join ', ') }))
    $state = Get-WindowState $script:hwnd
    Check 'window not full screen after leaving' (-not $state.FullScreen) $state.Rect
    $script:page = 'season'
    Capture-Page 'after-player'
}

function Step-Close([string]$What = 'close') {
    $tree = @(Get-Descendants $script:ui.Id)
    $at = [Diagnostics.Stopwatch]::StartNew()
    [OffscreenUi]::Close($script:hwnd, $script:ui.Id)
    $hidden = $null
    while ($at.Elapsed.TotalSeconds -lt $CloseSeconds -and -not $script:ui.HasExited) {
        if (-not $hidden -and -not [OffscreenUi]::IsWindowVisible($script:hwnd)) { $hidden = $at.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 25; $script:ui.Refresh()
    }
    $timings["$($What)_hidden_ms"] = $hidden
    $timings["$($What)_exit_ms"] = if ($script:ui.HasExited) { $at.ElapsedMilliseconds } else { $null }
    Check "$($What): FluentFin exited" $script:ui.HasExited "after $($timings["$($What)_exit_ms"]) ms (window hidden after $hidden ms)"
    Start-Sleep -Milliseconds 800
    $alive = @($tree | Where-Object { Get-TrackedProcess ([int]$_.ProcessId) })
    Check "$($What): nothing FluentFin started survives" ($alive.Count -eq 0) $(if ($alive) { ($alive | ForEach-Object { "$($_.Name)#$($_.ProcessId)" }) -join ',' } else { 'none' })
}

# FluentFin on this run's data folder: its process and main window (the window's time under $Timing)
function Start-App([string]$Timing) {
    $script:ui = Start-FluentFin $Exe @('--data-dir', ('"' + $DataDir + '"'))
    $script:launchedAt = ([DateTimeOffset][DateTime]::FromFileTimeUtc($script:Tracked[$script:ui.Id].Created)).ToUnixTimeMilliseconds() / 1000.0
    Say "FluentFin#$($script:ui.Id) started ($Exe --data-dir $DataDir)"
    $found = Wait-Until { $h = [OffscreenUi]::MainWindow($script:ui.Id); if ($h -ne [IntPtr]::Zero) { $h } } -Seconds 60 -PollMs 20
    if (-not $found) { throw "FluentFin showed no window within 60 s" }
    $script:hwnd = [IntPtr]$found
    $timings[$Timing] = Ms $script:launchedAt (Get-Epoch)
    Say "window after $($timings[$Timing]) ms"
}

# a user saved with the run's one saved server, as "Remember me" saves one, so that the next start signs itself in
# (DefaultActivationHandler). No password: an empty one, protected as FluentFin protects one (DPAPI, the settings'
# Entropy). Quick Connect saves the server only. The fake server refuses the empty password: the start-up sign-in runs
# ("Signing in to ..." in the window) and ends, as a real one does, then FluentFin shows the server page.
function Add-AutoSignInUser {
    Add-Type -AssemblyName System.Security
    $file = Join-Path $DataDir 'LocalSettings.json'
    $settings = Get-Content -LiteralPath $file -Raw -Encoding utf8 | ConvertFrom-Json
    $servers = @($settings.Servers | Where-Object { $null -ne $_ })
    if ($servers.Count -ne 1) { throw "relaunch: $($servers.Count) saved servers (the sign-in saves one)" }
    if (-not $settings.PSObject.Properties['Entropy']) {
        $bytes = New-Object byte[] 20
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $settings | Add-Member -NotePropertyName Entropy -NotePropertyValue ([Convert]::ToBase64String($bytes))
    }
    $none = [Security.Cryptography.ProtectedData]::Protect([byte[]]@(), [Convert]::FromBase64String($settings.Entropy), 'CurrentUser')
    $servers[0].Users = @([pscustomobject]@{ Username = 'harness-start-up'; Password = [Convert]::ToBase64String($none) })
    $settings.Servers = $servers
    [IO.File]::WriteAllText($file, ($settings | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

# close FluentFin and start it again on the same data folder with a remembered login (Add-AutoSignInUser): it signs
# itself in at start-up, the window saying "Signing in to ..." meanwhile (the startup panel and its ring), then the
# server page, where the run signs in again; on Home
function Step-Relaunch {
    Step-Close 'relaunch'
    Add-AutoSignInUser
    $script:navPane = $null
    $at = Get-Epoch
    Start-App 'relaunch_window_ms'
    $auth = Wait-Until { @(Get-Requests $at | Where-Object { $_.route -eq 'authenticate' }) | Select-Object -First 1 } -Seconds 30 -PollMs 100
    Check 'relaunch: FluentFin signed itself in at start-up (the remembered login)' ([bool]$auth) $(if ($auth) { "the server answered $($auth.status)" } else { 'no sign-in at the server' })
    $page = Wait-Until {
        if (Find-Ui -Id 'TxtUrl' -Type 'Edit') { 'server' }
        else { $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id); if ($p -and ([OffscreenUi]::NavItem($p, 'Home') -or (Find-Ui -Id 'TopTabsMenu'))) { 'home' } }
    } -Seconds 30 -PollMs 150
    if ($page -eq 'server') { Step-SignIn $false }
    elseif ($page -ne 'home') { throw "relaunch: neither the server page nor Home within 30 s" }
    $script:page = 'home'
    Start-Sleep -Seconds 1
    Shot 'relaunched'
}

# steady playback, then the middle of the video sampled: the fake server's test video (lavfi testsrc) is still there
# (colour bars; its scrolling band and counter are lower and to the right), so a pixel that changes from one capture to
# the next is something drawn over the video: a spinning ring, mpv's bar's buffering or pause indicator
function Step-Steady {
    if ($script:page -ne 'player') { throw "steady: nothing is playing (run play first)" }
    if ($PlayerControls -eq 'FluentFin') {
        # the play step paused through Play/Pause so the controls stayed up: Play again (the button keeps the keyboard)
        $pp = Find-Ui -Id 'PlayPauseButton'
        if ($pp -and $pp.Current.Name -eq 'Play') { [void](Invoke-Element $pp); Say 'steady: Play pressed (the play step had paused)' }
        $playing = Wait-Until { $b = Find-Ui -Id 'PlayPauseButton'; if ($b -and $b.Current.Name -eq 'Pause') { $b } } -Seconds 3 -PollMs 100
        Check 'steady: playing (the controls offer Pause)' ([bool]$playing)
    }
    # past mpv's own start and FluentFin's controls hiding (3 s)
    Start-Sleep -Seconds 5
    $box = [OffscreenUi]::MiddleChanges($script:hwnd, $script:ui.Id, 9, 250, 0.40, 0.36, 0.60, 0.64)
    Shot 'steady'
    $changed = $box[0]; $area = $box[3] * $box[4]
    Check 'steady playback: nothing moves over the middle of the video (no ring, no indicator)' ($area -gt 0 -and $changed -lt 40) `
        ("{0} of {1} pixels changed in 2 s (box {2},{3} {4}x{5}; the video is still there)" -f $changed, $area, $box[1], $box[2], $box[3], $box[4])
}

# --- run ----------------------------------------------------------------------------------------------------------------
$runStarted = Get-Epoch
try {
    # 1. fake server, in this job
    $ready = Join-Path $Out 'server\ready.json'
    $serverArgs = @('-B', '-I', (Join-Path $PSScriptRoot '..\fakeserver\server.py'), '--out', (Join-Path $Out 'server'), '--media', $Media,
        '--ready-file', $ready, '--latency', $Latency) + @($LatencyRule | ForEach-Object { '--latency-rule'; $_ })
    $quoted = @($serverArgs | ForEach-Object { if ("$_" -match '[\s"]') { '"' + ("$_" -replace '"', '\"') + '"' } else { "$_" } })
    $server = Start-Process -FilePath $Python -ArgumentList $quoted -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $Out 'server\stdout.txt') -RedirectStandardError (Join-Path $Out 'server\stderr.txt')
    Add-Tracked -Process $server
    if (-not (Wait-Until { Test-Path $ready } -Seconds 60)) { throw "the fake server did not start (see $Out\server\stderr.txt)" }
    $script:serverUrl = (Get-Content $ready -Raw | ConvertFrom-Json).url
    $fixture = Invoke-Server '/__harness/fixture'
    Say "fake server $($script:serverUrl): $($fixture.counts.movie) movies, $($fixture.counts.series) series, $($fixture.counts.episode) episodes; latency $Latency ms $(if ($LatencyRule) { '+ ' + ($LatencyRule -join ' ') })"

    # 2. FluentFin with its own data folder
    Start-App 'window_ms'
    # the window a key goes to: the XAML island's (hidden) input site; posted to the bridge, F and Esc never reached the
    # player page. Shift (does nothing on the server page) through Key itself, until the input site exists.
    $keyClass = Wait-Until { Key 0x10; $c = [OffscreenUi]::ClassOf($script:lastKeyWindow); if ($c -eq 'InputSiteWindowClass') { $c } } -Seconds 10 -PollMs 100
    Check 'keys: posted to the window WinUI reads them from (InputSiteWindowClass)' ([bool]$keyClass) $(if ($keyClass) { $keyClass } else { [OffscreenUi]::ClassOf($script:lastKeyWindow) })

    # 3. -Before (on the server page, before the sign-in: its states, the sign-in page's), sign in, then the steps in order
    if ($Before) { Say "-Before $Before"; . $Before }
    Step-SignIn ($wanted -contains 'login')
    foreach ($step in $wanted) {
        switch -Regex ($step) {
            '^(login|close)$' { break }
            '^home$' { Step-Home; break }
            '^library$' { Step-Library; break }
            '^movie$' { Step-Movie; break }
            '^series$' { Step-Series; break }
            '^season$' { Step-Season; break }
            '^play$' { Step-Play; break }
            '^fullscreen$' { Step-FullScreen $true; break }
            '^windowed$' { Step-FullScreen $false; break }
            '^leave$' { Step-Leave; break }
            '^relaunch$' { Step-Relaunch; break }
            '^steady$' { Step-Steady; break }
            '^nav:(.+)$' {
                $name = $Matches[1]
                Open-Nav $name; $script:page = "nav:$name"
                Start-Sleep -Seconds 2
                Capture-Page "nav-$name"; break
            }
            '^shot:(.+)$' { Capture-Page $Matches[1]; break }
            '^wait:(.+)$' { Start-Sleep -Milliseconds ([int]([double]$Matches[1] * 1000)); break }
        }
    }
    if ($Then) { Say "-Then $Then"; . $Then }
    if ($wanted -contains 'close') { Step-Close }
}
catch {
    Check 'run' $false ($_.Exception.Message + ' @ ' + $_.InvocationInfo.PositionMessage)
    if ($script:ui -and -not $script:ui.HasExited -and $script:hwnd -ne [IntPtr]::Zero) {
        try { Shot 'zz-failure' } catch {}
        try { Dump-Tree 'zz-failure' } catch {}
    }
}
finally {
    if ($script:ui -and $script:ui.Id) { [void](Get-Descendants $script:ui.Id) }
    if ($script:ui -and -not $script:ui.HasExited -and $script:hwnd -ne [IntPtr]::Zero) {
        try { [OffscreenUi]::Close($script:hwnd, $script:ui.Id); [void]$script:ui.WaitForExit($CloseSeconds * 1000) } catch {}
    }
    if ($script:serverUrl) { try { [void](Invoke-Server '/__harness/shutdown' 'POST') } catch {} }
    Start-Sleep -Milliseconds 500
    $ended = @(Close-Tracked $null 1)
    if ($ended.Count) { Say "cleanup ended: $($ended -join ', ')" }
    # FluentFin's own errors during this run ([ERR]/[FTL] lines of its log stamped after the run began): reported, not a check
    $appErrors = @()
    foreach ($f in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue)) {
        $lines = @(Get-Content -LiteralPath $f.FullName -Encoding utf8)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^(\S+ \S+ [+-]\d\d:\d\d) \[(ERR|FTL)\] (.*)$') {
                $when = [DateTimeOffset]::MinValue
                if ([DateTimeOffset]::TryParse($Matches[1], [ref]$when) -and $when.ToUnixTimeMilliseconds() / 1000.0 -ge $runStarted) {
                    # the message, and the exception's first line under it (Serilog writes it on the next line)
                    $what = "$($Matches[2]) $($Matches[3])"
                    if ($i + 1 -lt $lines.Count -and $lines[$i + 1] -notmatch '^\d{4}-\d\d-\d\d ') { $what += ': ' + $lines[$i + 1].Trim() }
                    $appErrors += $what.Substring(0, [Math]::Min(300, $what.Length))
                }
            }
        }
    }
    if ($appErrors) { Say ("FluentFin logged {0} error(s) during the run, first: {1}" -f $appErrors.Count, $appErrors[0]) }
    $failed = @($checks.Keys | Where-Object { -not $checks[$_].ok })
    $result = [ordered]@{ started = $runStarted; desktop = $desktop; server = $script:serverUrl; sign_in = $SignIn; player_controls = $PlayerControls;
        latency_ms = $Latency; latency_rules = $LatencyRule; steps = $wanted; timings = $timings; checks = $checks; shots = $shots; uia = $dumps; failed = $failed
        app_errors = [ordered]@{ count = $appErrors.Count; distinct = @($appErrors | ForEach-Object { $_ -replace '\d+', '#' } | Sort-Object -Unique | Select-Object -First 10) }
        uia_searches = [ordered]@{ count = [OffscreenUi]::Searches; total_ms = [OffscreenUi]::SearchMs; slowest_ms = [OffscreenUi]::SlowestSearchMs; retried = [OffscreenUi]::Retries } }
    [IO.File]::WriteAllText((Join-Path $Out 'result.json'), ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Say ("done: {0}/{1} checks passed{2}" -f ($checks.Count - $failed.Count), $checks.Count, $(if ($failed) { '; FAILED: ' + ($failed -join '; ') } else { '' }))
    if ($failed) { exit 1 }
}
