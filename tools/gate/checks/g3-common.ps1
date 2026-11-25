# The basics gate (Nixie 1.7.5), g3's helpers for its class 4 and 5 checks (4-topbar.ps1, 5-item-buttons.ps1): what the
# user sees of the top bar (the lit tab), which page shows, the sizes. Dot-sourced by a check that drive.ps1 dot-sourced
# (its helpers: Find-Ui, Wait-Ui, Open-Nav, Open-Card, Key, Check, Say, Shot, Invoke-Server, $fixture); every name here
# starts with G3 / $g3 (drive.ps1's names must not be reused).
# tools\gate\gate-common.ps1 (g1's) when it is there: Set-GateSize and Invoke-GateLiveResize; else the same done here.
$g3GateCommon = Join-Path $PSScriptRoot '..\gate-common.ps1'
if (Test-Path $g3GateCommon) { . $g3GateCommon }
# keys with Ctrl, Shift or Alt held as WinUI reads them (CK-Key), the pointer hook's clicks (CK-Click)
. (Join-Path $PSScriptRoot '..\..\ui-harness\offscreen\chord-keys.ps1')
$g3Uia = [System.Windows.Automation.AutomationElement]
$g3Theme = if ($env:GATE_THEME) { $env:GATE_THEME } elseif ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }

function G3-Settle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }

if (-not ('G3Win' -as [type])) {
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class G3Win {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
  static void Own(IntPtr h, int pid) { uint p; GetWindowThreadProcessId(h, out p); if (p != (uint)pid) throw new InvalidOperationException("not FluentFin's window"); }
  public static int[] Size(IntPtr h) { RECT r; GetWindowRect(h, out r); return new[] { r.R - r.L, r.B - r.T, (int)GetDpiForWindow(h) }; }
  public static void Maximize(IntPtr h, int pid) { Own(h, pid); ShowWindow(h, 3); }
  public static void Place(IntPtr h, int pid, int w, int ht) { Own(h, pid); ShowWindow(h, 9); SetWindowPos(h, IntPtr.Zero, 0, 0, w, ht, 0x0004 | 0x0010); }
}
"@
}

# the sizes the check runs at: GATE_SIZES ('gate' = 1280x720,1920x1080,max), as g1's Get-GateSizes reads it
function G3-Sizes {
    if (Get-Command Get-GateSizes -ErrorAction SilentlyContinue) { return @(Get-GateSizes) }
    $v = if ($env:GATE_SIZES) { $env:GATE_SIZES } else { 'gate' }
    if ($v -eq 'gate') { $v = '1280x720,1920x1080,max' }
    if ($v -eq 'none') { return @('') }
    @($v -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}
# the window at a size ('WxH' outer, or 'max'): "<w> x <h> at <dpi> dpi"
function G3-Size([string]$Size) {
    if (-not $Size) { $s = [G3Win]::Size($script:hwnd); return "$($s[0]) x $($s[1]) at $($s[2]) dpi" }
    if (Get-Command Set-GateSize -ErrorAction SilentlyContinue) { $r = Set-GateSize $Size; G3-Settle 600; return "$($r.W) x $($r.H) at $($r.Dpi) dpi" }
    if ($Size -eq 'max') { [G3Win]::Maximize($script:hwnd, $script:ui.Id) }
    else { $wh = $Size -split 'x'; [G3Win]::Place($script:hwnd, $script:ui.Id, [int]$wh[0], [int]$wh[1]) }
    G3-Settle 1800
    $s = [G3Win]::Size($script:hwnd)
    "$($s[0]) x $($s[1]) at $($s[2]) dpi"
}
# a live resize: several sizes in a row without waiting, then back
function G3-LiveResize {
    if (Get-Command Invoke-GateLiveResize -ErrorAction SilentlyContinue) { Invoke-GateLiveResize; return }
    $s = [G3Win]::Size($script:hwnd)
    foreach ($w in 1700, 1400, 1180, 1500, 2000) { [G3Win]::Place($script:hwnd, $script:ui.Id, $w, [int]($w * 0.56)); Start-Sleep -Milliseconds 60 }
    [G3Win]::Place($script:hwnd, $script:ui.Id, $s[0], $s[1]); G3-Settle 1500
}

# ---- the top bar: what is lit, as the user sees it ----------------------------------------------------------------------------
# the bar's tabs as shown (the selected one is drawn lit); in a narrow window the one menu button, named after the lit page
function G3-Tabs {
    $p = [OffscreenUi]::NavPane($script:hwnd, $script:ui.Id)
    if (-not $p) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($g3Uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)
    @($p.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | Where-Object { -not $_.Current.IsOffscreen })
}
function G3-Selected($e) { $sp = $null; if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$sp)) { $sp.Current.IsSelected } else { $false } }
# the lit destination's name ('' for none): a selected tab, or the narrow menu's label when it names one
function G3-Lit {
    $menu = Find-Ui -Id 'TopTabsMenu'
    if ($menu -and -not $menu.Current.IsOffscreen) { $n = $menu.Current.Name; if ($n -and $n -ne 'Menu') { return ($n -replace (' ?' + [char]0x25BE + '$'), '') }; return '' }
    @(G3-Tabs | Where-Object { G3-Selected $_ } | ForEach-Object { $_.Current.Name -replace (' ?' + [char]0x25BE + '$'), '' }) -join ','
}
function G3-TabNames { @(G3-Tabs | ForEach-Object { $_.Current.Name }) -join ',' }
# what $Read gives once it settles: $Want read twice in a row ($Ms apart) within $Seconds; else the last read (1.7.5 gate fix:
# one read after the first match took a moment's unlit bar between two lit states)
function G3-Settled([scriptblock]$Read, [string]$Want, [double]$Seconds = 6, [int]$Ms = 300) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds); $same = 0; $last = $null
    do {
        $last = & $Read
        if ($last -eq $Want) { if (++$same -ge 2) { return $last } } else { $same = 0 }
        Start-Sleep -Milliseconds $Ms
    } while ([DateTime]::UtcNow -lt $until)
    $last
}

# ---- which page shows ---------------------------------------------------------------------------------------------------------
function G3-Texts([string]$Pattern) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($g3Uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond) | Where-Object { $_.Cached.Name -match $Pattern })
}
# Home: its first row's heading (any theme; matched without case: 1.7.5's fx3 writes Home's row names in sentence case)
function G3-OnHome { @(G3-Texts '(?i)^(continue watching|up next|next up)\b').Count -gt 0 -and -not (Find-Ui -Id 'SortButton') }
function G3-OnLibrary { [bool](Find-Ui -Id 'SortButton') }
function G3-OnItem([string]$Name) { [bool](Find-Ui -Name $Name -Type 'Text') -or [bool](Find-Ui -Name $Name -Id 'LogoImage') }
function G3-OnSeries([string]$Series) { [bool][OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark $Series as ", '') }
function G3-OnSeason([string]$Series, [int]$Season) { [bool][OffscreenUi]::FindButtonLike($script:hwnd, $script:ui.Id, "Mark Season $Season of $Series as ", '') }
function G3-OnResults { [bool](Find-Ui -Name 'Results for' -Type 'Text' -Contains) }
function G3-OnTitle([string]$Title) { [bool](Find-Ui -Id 'TitleText' -Name $Title) }
function G3-OnSettings { [bool](Find-Ui -Name 'Settings' -Type 'Text') -and -not (Find-Ui -Id 'SortButton') }
# waits until the page shows (a scriptblock of the above); $true or $false
function G3-Wait([scriptblock]$Shown, [double]$Seconds = 20) { [bool](Wait-Until { if (& $Shown) { $true } } -Seconds $Seconds -PollMs 250) }
function G3-Back { Key 0xA6 }   # the Back key (the mouse's back button and Alt+Left do the same)

# ---- the search box ------------------------------------------------------------------------------------------------------------
function G3-Box { Find-Ui -Name 'Search' -Type 'Edit' -Contains }
function G3-BoxText { $b = G3-Box; if ($b) { try { ($b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { $null } } }
# the box emptied as a user does: Ctrl+A, Delete (Backspace when a Delete did not reach it)
function G3-Clear {
    $b = G3-Box
    if ($b) { [void][OffscreenUi]::SetFocus($b) } else { Key 0xBF }
    G3-Settle 400
    if (-not (G3-BoxText)) { return }
    CK-Key 'Ctrl+A'; G3-Settle 200; Key 0x2E; G3-Settle 500
    if (G3-BoxText) { $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id); [void][OffscreenUi]::PostMessage($kw, 0x0102, [IntPtr]8, [IntPtr]1); G3-Settle 500 }
}
function G3-Type([string]$Text) {
    G3-Clear
    $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
    foreach ($ch in $Text.ToCharArray()) { [void][OffscreenUi]::PostMessage($kw, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 80 }
    G3-Settle 400
}

# a check named "<id> [<case>]: <what the user sees>"
function G3-Check([string]$Id, [string]$Case, [string]$What, [bool]$Ok, [string]$Detail = '') {
    $name = if ($Case) { "${Id} [$Case]: $What" } else { "${Id}: $What" }
    Check $name $Ok $Detail
}

# ---- the pointer: the dev build's hook (HarnessPointer, "NixieHarnessPointer": kind 1 a click at a screen point), the
# mouse's way (a posted click does not reach WinUI on the private desktop); a release build ignores it (Dev = $true)
if (-not ('G3Hook' -as [type])) {
    Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class G3Hook {
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static bool Click(IntPtr main, int pid, int x, int y) {
    uint p; GetWindowThreadProcessId(main, out p); if (p != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    return PostMessage(main, RegisterWindowMessage("NixieHarnessPointer"), (IntPtr)1, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }
}
"@
}
# a click in the middle of an element (the dev build's hook); $false when it has no rect
function G3-Click($Element) {
    $r = $Element.Current.BoundingRectangle
    if ($r.IsEmpty -or [double]::IsInfinity($r.Width)) { return $false }
    [G3Hook]::Click($script:hwnd, $script:ui.Id, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}
# a toggle button's drawn state: 'On', 'Off' or '' (no toggle)
function G3-State($Element) { $p = $null; if ($Element -and $Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { "$($p.Current.ToggleState)" } else { '' } }

# a season's tab in a series page's picker (Classic's pills "Season 1", Editorial's lines "Season 1, Watched", Drive-in's
# tubes, Cinematic's tabs): the first TabItem, ListItem or Button named "Season <n>" or "Season <n>, ..."
function G3-SeasonTab([int]$Season) {
    foreach ($type in 'TabItem', 'ListItem', 'Button') {
        $ct = [System.Windows.Automation.ControlType]::$type
        $hit = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($g3Uia::ControlTypeProperty, $ct))) |
                Where-Object { $_.Cached.Name -match "^Season $Season(,|$)" }) | Select-Object -First 1
        if ($hit) { return $hit }
    }
    $null
}

# opens the episode in progress of $Series from Home, as a user does: its card in Continue watching (the shared card,
# "<series>, season 2 episode 6, ..."; a theme's "<series> S2 E6 ..."), else the hero that features it (Drive-in's screen
# takes the first in progress out of the row: its dot "Feature N of M: <series>", then "More info about <series>").
# Returns what was invoked, or $null when Home shows it nowhere.
# a hero's way into the show's episode in progress: Classic's, Cinematic's and Drive-in's "More info about <show>"; Editorial's
# hero tile's "Details of <show> S<n> E<n> <title>"
function G3-IsInProgressHero([string]$Name, [string]$Series) {
    $Name -eq "More info about $Series" -or $Name -match ('^Details of ' + [regex]::Escape($Series) + ' S\d+ E\d+')
}
function G3-OpenInProgress([string]$Series) {
    Open-Nav 'Home'
    [void](G3-Wait { G3-OnHome } 20)
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); G3-Settle 1200
    $esc = [regex]::Escape($Series)
    $cardRx = "^$esc(, (?:season \d+ episode |S\d+ E)\d+| S\d+ E\d+)"
    $cond = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition($g3Uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($g3Uia::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
    $card = Wait-Until {
        $all = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond))
        $c = @($all | Where-Object { $_.Cached.Name -match $cardRx }) | Sort-Object { [int]$_.Cached.IsOffscreen }, { $_.Cached.BoundingRectangle.Y } | Select-Object -First 1
        if ($c) { return $c }
        # the hero: its "More info" once the slide shows the show; Editorial's hero tile, which features Continue watching's
        # first and leaves it out of Up next, by its "Details of <show> S2 E6 <title>" (it opens the details in place; Full
        # details below opens the episode) (1.7.5 gate fix: the verify run's Editorial found only its Resume)
        $more = @($all | Where-Object { G3-IsInProgressHero $_.Cached.Name $Series }) | Select-Object -First 1
        if ($more) { return $more }
        $dot = @($all | Where-Object { $_.Cached.Name -match "^Feature \d+ of \d+: $esc$" }) | Select-Object -First 1
        if ($dot) { try { [void](Invoke-Element $dot) } catch { } }
    } -Seconds 20 -PollMs 400
    if (-not $card) { return $null }
    $name = $card.Cached.Name
    [void](Invoke-Element $card)
    $full = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 2.5 -PollMs 150
    if ($full) { [void](Invoke-Element $full) }
    $name
}
