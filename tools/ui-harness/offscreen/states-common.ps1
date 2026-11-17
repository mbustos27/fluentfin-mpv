<#
  Helpers of the states pass (dot-sourced by before-states.ps1 and then-states.ps1, which drive.ps1 dot-sources in turn):
  the fake server's fault rules, and a Narrator-oriented UI Automation probe of the page as it is now.

  Probe-A11y <name> writes a11y\<name>-narrator.json: the headings (UIA HeadingLevel), the live regions (UIA LiveSetting:
  polite or assertive, with their text), the focused element, the keyboard-focusable elements without a name, and the
  visible texts (what Narrator can read on the page). HeadingLevel (30173) and LiveSetting (30135) are looked up by id and,
  where the managed UIA client of .NET Framework does not list them, registered with it by reflection.
#>

function Register-UiaProperty([int]$Id, [string]$Name) {
    $known = [System.Windows.Automation.AutomationProperty]::LookupById($Id)
    if ($known) { return $known }
    $register = [System.Windows.Automation.AutomationIdentifier].GetMethod('Register', [Reflection.BindingFlags]'NonPublic,Static')
    $kind = [System.Windows.Automation.AutomationIdentifier].Assembly.GetType('MS.Internal.Automation.AutomationIdType')
    $property = if ($kind) { [Enum]::Parse($kind, 'Property') } else { 1 }
    [void]$register.Invoke($null, @($property, $Id, $Name))
    [System.Windows.Automation.AutomationProperty]::LookupById($Id)
}
$script:uiaHeading = Register-UiaProperty 30173 'HeadingLevelProperty'
$script:uiaLive = Register-UiaProperty 30135 'LiveSettingProperty'
$script:probes = [ordered]@{}

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
function Probe-A11y([string]$Name) {
    $headings = @(); $live = @(); $unnamed = @(); $texts = @(); $focused = $null
    foreach ($e in @([OffscreenUi]::All($script:hwnd, $script:ui.Id))) {
        try {
            $c = $e.Cached
            if ($c.ProcessId -ne $script:ui.Id -or $c.IsOffscreen -or $c.BoundingRectangle.IsEmpty) { continue }
            $d = Describe-Element $e
            if (-not $d) { continue }
            # .NET Framework's client returns its own enums (AutomationHeadingLevel None..Level9, AutomationLiveSetting
            # Off/Polite/Assertive); a raw UIA value would be 80050 + level, 0..2
            $level = try { [int]$e.GetCurrentPropertyValue($script:uiaHeading) } catch { 0 }
            if ($level -gt 80050) { $level -= 80050 }
            if ($level -gt 0) { $d.level = $level; $headings += [pscustomobject]$d }
            $setting = try { [int]$e.GetCurrentPropertyValue($script:uiaLive) } catch { 0 }
            if ($setting -gt 0 -and $setting -le 2) { $d2 = Describe-Element $e; $d2.live = @('off', 'polite', 'assertive')[$setting]; $live += [pscustomobject]$d2 }
            if ($c.HasKeyboardFocus -and $c.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $focused = [pscustomobject]$d }
            if ($c.IsKeyboardFocusable -and $c.IsEnabled -and [string]::IsNullOrWhiteSpace($c.Name) -and $c.ControlType -ne [System.Windows.Automation.ControlType]::Window) { $unnamed += [pscustomobject]$d }
            if ($c.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $c.Name) { $texts += $c.Name }
        } catch { }
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

# a top-level window of FluentFin's other than its main window (a picker window), or null
function Find-OtherWindow([string]$Title) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:ui.Id)
    foreach ($w in @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond))) {
        try {
            $h = [IntPtr]$w.Current.NativeWindowHandle
            if ($h -ne $script:hwnd -and (-not $Title -or $w.Current.Name -like "*$Title*")) { return $w }
        } catch { }
    }
    $null
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
