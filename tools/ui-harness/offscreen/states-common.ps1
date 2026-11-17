<#
  Helpers of the states pass (dot-sourced by before-states.ps1 and then-states.ps1, which drive.ps1 dot-sources in turn):
  the fake server's fault rules, and a Narrator-oriented UI Automation probe of the page as it is now.

  Probe-A11y <name> writes a11y\<name>-narrator.json: the headings (UIA HeadingLevel), the live regions (UIA LiveSetting:
  polite or assertive, with their text), the focused element, the keyboard-focusable elements without a name, and the
  visible texts (what Narrator can read on the page), read through UI Automation's COM client (StatesUia below).
#>

# UI Automation's own COM client (the .NET Framework client cannot read LiveSetting: "Unsupported Property"): the
# window's elements with the properties the probe needs, one walk
if (-not ('StatesUia' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
[ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] class CUIAutomation { }
[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomation {
  void _0(); void _1(); void _2();
  IUIAutomationElement ElementFromHandle(IntPtr hwnd);
  void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12(); void _13(); void _14(); void _15(); void _16(); void _17();
  IntPtr CreateTrueCondition();
}
[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElement {
  void _0(); void _1(); void _2();
  IUIAutomationElementArray FindAll(int scope, IntPtr condition);
  void _4(); void _5(); void _6();
  [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
}
[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IUIAutomationElementArray {
  int Length { get; }
  IUIAutomationElement GetElement(int index);
}
public static class StatesUia {
  // name, control type id, automation id, class, rect, offscreen, focused, focusable, enabled, process, heading level (0-9), live (0-2)
  public static List<object[]> Walk(IntPtr hwnd) {
    var uia = (IUIAutomation)new CUIAutomation();
    var root = uia.ElementFromHandle(hwnd);
    var all = root.FindAll(4, uia.CreateTrueCondition());
    var list = new List<object[]>();
    for (int i = 0; i < all.Length; i++) {
      try {
        var e = all.GetElement(i);
        var rect = e.GetCurrentPropertyValue(30001) as double[];
        int heading = Convert.ToInt32(e.GetCurrentPropertyValue(30173) ?? 0);
        if (heading > 80050) heading -= 80050; else heading = 0;
        list.Add(new object[] { e.GetCurrentPropertyValue(30005) as string ?? "", Convert.ToInt32(e.GetCurrentPropertyValue(30003) ?? 0),
          e.GetCurrentPropertyValue(30011) as string ?? "", e.GetCurrentPropertyValue(30012) as string ?? "",
          rect == null ? "" : string.Format("{0},{1} {2}x{3}", (int)rect[0], (int)rect[1], (int)rect[2], (int)rect[3]),
          Convert.ToBoolean(e.GetCurrentPropertyValue(30022) ?? false), Convert.ToBoolean(e.GetCurrentPropertyValue(30008) ?? false),
          Convert.ToBoolean(e.GetCurrentPropertyValue(30009) ?? false), Convert.ToBoolean(e.GetCurrentPropertyValue(30010) ?? false),
          Convert.ToInt32(e.GetCurrentPropertyValue(30002) ?? 0), heading, Convert.ToInt32(e.GetCurrentPropertyValue(30135) ?? 0),
          rect != null && rect[2] > 0 && rect[3] > 0 });
      } catch (Exception) { }
    }
    return list;
  }
}
"@
}
$script:probes = [ordered]@{}
$script:uiaTypes = @{ 50000 = 'Button'; 50002 = 'CheckBox'; 50003 = 'ComboBox'; 50004 = 'Edit'; 50005 = 'Hyperlink'; 50006 = 'Image'
    50007 = 'ListItem'; 50008 = 'List'; 50011 = 'MenuItem'; 50012 = 'ProgressBar'; 50013 = 'RadioButton'; 50015 = 'Slider'
    50020 = 'Text'; 50025 = 'Custom'; 50026 = 'Group'; 50031 = 'SplitButton'; 50032 = 'Window'; 50033 = 'Pane'; 50037 = 'TitleBar' }

# the fake server's fault rules, replaced as a whole: @(@('regex', 'status:503'), @('regex', 'empty'), ...); none = all off
function Set-Rules([object[]]$Rules = @()) {
    $list = @($Rules | ForEach-Object { , @([string]$_[0], [string]$_[1]) })
    [void](Invoke-Server '/__harness/latency' 'POST' @{ rules = $list })
    Say ("rules: {0}" -f $(if ($list.Count) { ($list | ForEach-Object { "$($_[0])=$($_[1])" }) -join '; ' } else { 'none' }))
}

function Describe-Element($e) {
    try {
        $c = $e.Current
        [ordered]@{ type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''; name = $c.Name; id = $c.AutomationId; class = $c.ClassName
            rect = ('{0},{1} {2}x{3}' -f [int]$c.BoundingRectangle.X, [int]$c.BoundingRectangle.Y, [int]$c.BoundingRectangle.Width, [int]$c.BoundingRectangle.Height) }
    } catch { $null }
}

# the page as Narrator meets it: headings, live regions, the focus, unnamed focusables, the visible texts
# (control types: 50020 Text, 50032 Window)
function Probe-A11y([string]$Name) {
    $headings = @(); $live = @(); $unnamed = @(); $texts = @(); $focused = $null
    $rows = $null
    for ($try = 0; $try -lt 3 -and -not $rows; $try++) { try { $rows = [StatesUia]::Walk($script:hwnd) } catch { Start-Sleep -Milliseconds 300 } }
    foreach ($r in @($rows)) {
        if ($r[9] -ne $script:ui.Id -or $r[5] -or -not $r[12]) { continue }
        $d = [ordered]@{ type = $(if ($script:uiaTypes.ContainsKey([int]$r[1])) { $script:uiaTypes[[int]$r[1]] } else { [string]$r[1] }); name = $r[0]; id = $r[2]; class = $r[3]; rect = $r[4] }
        if ($r[10] -gt 0) { $h = [ordered]@{} + $d; $h.level = $r[10]; $headings += [pscustomobject]$h }
        if ($r[11] -gt 0) { $l = [ordered]@{} + $d; $l.live = @('off', 'polite', 'assertive')[$r[11]]; $live += [pscustomobject]$l }
        if ($r[6] -and $r[1] -ne 50032) { $focused = [pscustomobject]$d }
        if ($r[7] -and $r[8] -and [string]::IsNullOrWhiteSpace($r[0]) -and $r[1] -ne 50032) { $unnamed += [pscustomobject]$d }
        if ($r[1] -eq 50020 -and $r[0]) { $texts += $r[0] }
    }
    $probe = [ordered]@{ page = $Name; focused = $focused; headings = $headings; live = $live; unnamedFocusable = $unnamed; texts = @($texts | Select-Object -Unique) }
    $file = Join-Path $Out ("a11y\{0}-narrator.json" -f (Safe-Name $Name))
    [IO.File]::WriteAllText($file, ($probe | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    $script:probes[$Name] = $probe
    Say ("narrator {0}: focus on {1}; {2} heading(s) [{3}]; {4} live region(s) [{5}]; {6} unnamed focusable" -f $Name,
        $(if ($focused) { "$($focused.type) '$($focused.name)'" } else { 'nothing' }), $headings.Count, (($headings | ForEach-Object { "h$($_.level) $($_.name)" }) -join ' | '),
        $live.Count, (($live | ForEach-Object { "$($_.live) $($_.type) '$($_.name)'" }) -join ' | '), $unnamed.Count)
}

# a state: the screenshot, the UIA tree and scan (Capture-Page), and the Narrator probe
function Capture-State([string]$Name) {
    Capture-Page $Name
    Probe-A11y $Name
}

# FluentFin's windows other than its main window, visible: its other top-level windows (the folder picker) and the popup
# windows UI Automation lists under the main one (menus, suggestion lists: PopupWindowSiteBridge)
function Get-OtherWindows {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ui.Id)
    $tops = @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))
    $popups = @()
    try {
        $main = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
        $popups = @($main.FindAll([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge'))))
    } catch { }
    @($tops + $popups | Where-Object {
            try { $h = [IntPtr]$_.Current.NativeWindowHandle; $h -ne [IntPtr]::Zero -and $h -ne $script:hwnd -and [OffscreenUi]::IsWindowVisible($h) } catch { $false } })
}

# one of them by (part of) its title, or the first one; null when there is none
function Find-OtherWindow([string]$Title) {
    @(Get-OtherWindows | Where-Object { -not $Title -or $_.Current.Name -like "*$Title*" }) | Select-Object -First 1
}

# each popup window open now, as shots\NN-<name>-popup<i>.png (PrintWindow of the main window leaves them out)
function Shot-Popups([string]$Name) {
    $i = 0
    foreach ($w in @(Get-OtherWindows)) {
        $i++; $script:shotN++
        $file = Join-Path $Out ("shots\{0:00}-{1}-popup{2}.png" -f $script:shotN, (Safe-Name $Name), $i)
        try { Say "popup $Name $i ($($w.Current.ClassName)) -> $file ($([OffscreenUi]::Shot([IntPtr]$w.Current.NativeWindowHandle, $script:ui.Id, $file)))" } catch { Say "popup $Name ${i}: $($_.Exception.Message)" }
    }
}

# the window's size for the run's states (SG_STATES_SIZE=WxH, e.g. 1100x900 or 1920x1080), set once
if ($env:SG_STATES_SIZE -match '^(\d+)x(\d+)$' -and -not $script:statesSized) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, [int]$Matches[1], [int]$Matches[2])
    $script:statesSized = $true
    Start-Sleep -Milliseconds 800
    Say "window resized to $($env:SG_STATES_SIZE)"
}

# a button by its name anywhere in the main window (UI Automation), invoked
function Press([string]$Name, [double]$Seconds = 10) {
    $b = Wait-Until { [OffscreenUi]::FindButton($script:hwnd, $script:ui.Id, $Name) } -Seconds $Seconds -PollMs 150
    if (-not $b) { throw "no button '$Name'" }
    [void](Invoke-Element $b)
    Say "pressed '$Name'"
}

# the server page's address box and Next: type an address and go on
function Submit-Address([string]$Address) {
    $url = Wait-Ui -Id 'TxtUrl' -Type 'Edit' -Seconds 20
    if (-not $url) { throw "no server address box" }
    Set-ElementValue $url $Address
    Press 'Next'
}
