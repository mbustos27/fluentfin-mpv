# Seaglass 1.1.4: Continue Watching kept clean, run with -Script home -Then offscreen\then-continue.ps1.
# A Continue Watching card's menu (the menu key) has "Remove from Continue Watching"; it takes the card out at once, the
# focus stays in the row, a notice says so with Undo, and the fake server got the resume point cleared (POST UserData);
# Undo puts the card back where it was and the resume point back; a card of another row has no such entry; the notice goes
# by itself. Screenshots with the menu (a window of its own) composed over the page: shots\NN-cw-*.png.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
if (-not ('CwShot' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class CwShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  // the window, and FluentFin's other visible top-level windows (menus, tooltips: windows of their own) drawn where they are
  public static string Shot(IntPtr main, int pid, string file) {
    RECT r; GetWindowRect(main, out r);
    int w = r.R - r.L, h = r.B - r.T, extra = 0;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); }
      var popups = new List<IntPtr>();
      EnumWindows((x, l) => { uint p; GetWindowThreadProcessId(x, out p); if (p == (uint)pid && x != main && IsWindowVisible(x)) popups.Add(x); return true; }, IntPtr.Zero);
      popups.Reverse(); // bottom of the z-order first
      foreach (var x in popups) {
        RECT pr; GetWindowRect(x, out pr);
        int pw = pr.R - pr.L, ph = pr.B - pr.T;
        if (pw < 8 || ph < 8 || pw > w * 2) continue;
        using (var pb = new Bitmap(pw, ph, PixelFormat.Format32bppArgb)) {
          using (var pg = Graphics.FromImage(pb)) { var ph2 = pg.GetHdc(); try { PrintWindow(x, ph2, 2); } finally { pg.ReleaseHdc(ph2); } }
          g.DrawImage(pb, pr.L - r.L, pr.T - r.T); extra++;
        }
      }
      bmp.Save(file, ImageFormat.Png);
    }
    return string.Format("{0}x{1}, {2} popup window(s)", w, h, extra);
  }
}
'@
}
$A = [System.Windows.Automation.AutomationElement]
$Ids = [System.Windows.Automation.AutomationElementIdentifiers]
function CwShot([string]$Name) {
    $script:shotN++
    $file = Join-Path $Out ("shots\{0:00}-{1}.png" -f $script:shotN, $Name)
    $info = [CwShot]::Shot($script:hwnd, $script:ui.Id, $file)
    $shots[$Name] = $file
    Say "shot $Name -> $file ($info)"
}
function CwFocused {
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::HasKeyboardFocusProperty, $true)
    $el = @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $cond)) | Where-Object { $_.Cached.ControlType -ne [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    if ($el) { [pscustomobject]@{ Name = $el.Cached.Name; Class = $el.Cached.ClassName; Pos = [int]$el.GetCurrentPropertyValue($Ids::PositionInSetProperty) } }
}
# the cards of the Continue Watching row, in order (the row is a list named after it; its cards are list items)
function CwCards {
    $list = Find-Ui -Name 'Continue Watching' -Type 'List'
    if (-not $list) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | ForEach-Object { $_.Current.Name })
}
function CwUserDataPosts([double]$Since) { @(Get-Requests $Since | Where-Object { $_.route -eq 'user-data' -and $_.method -eq 'POST' }).Count }
function CwOpenMenu([string]$CardName) {
    $card = Find-Ui -Name $CardName -Type 'ListItem'
    if (-not $card) { return $null }
    [void][OffscreenUi]::SetFocus($card); Start-Sleep -Milliseconds 500
    Key 0x5D   # the menu key (Shift+F10 cannot be posted: WinUI reads the real Shift)
    Wait-Ui -Name 'Remove from Continue Watching' -Type 'MenuItem' -Seconds 4
}

[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 800
$before = CwCards
Check 'continue: the row has cards' ($before.Count -ge 3) ("{0} cards: {1}" -f $before.Count, ($before -join ' | '))
CwShot 'cw-1-row-before'

# --- the menu of the second card
$target = $before[1]
$entry = CwOpenMenu $target
Check 'continue: the menu key opens the card''s menu, with "Remove from Continue Watching"' ([bool]$entry) $target
Check 'continue: ... and no "Reset watch progress" beside it (the same thing on the server)' (-not (Find-Ui -Name 'Reset watch progress' -Type 'MenuItem'))
CwShot 'cw-2-menu'

# --- remove: the card goes at once, the focus stays in the row, the notice with Undo, the server's resume point cleared
$t0 = Get-Epoch
if ($entry) { [void](Invoke-Element $entry) }
$gone = Wait-Until { $c = CwCards; if ($c.Count -eq $before.Count - 1 -and $c -notcontains $target) { $c } } -Seconds 5 -PollMs 100
Check 'continue: the card leaves the row at once' ([bool]$gone) (($gone | Select-Object -First 3) -join ' | ')
$notice = Wait-Ui -Name 'from Continue Watching' -Type 'Text' -Contains -Seconds 3
$undo = Find-Ui -Name 'Undo' -Type 'Button'
Check 'continue: a notice says so, with Undo' ($notice -and $undo) $(if ($notice) { $notice.Current.Name })
Start-Sleep -Milliseconds 700
$f = CwFocused
Check 'continue: the keyboard focus stays in the row (the card that took its place)' ($f -and $f.Class -eq 'BaseItemDtoCard' -and $f.Name -eq $before[2]) ("{0} [{1}]" -f $f.Name, $f.Class)
$posted = Wait-Until { if ((CwUserDataPosts $t0) -ge 1) { $true } } -Seconds 5
Check 'continue: the server''s resume point was cleared (POST /UserItems/{id}/UserData)' ([bool]$posted)
CwShot 'cw-3-removed-notice'

# --- Undo: back where it was, and the resume point back on the server
if ($undo) { [void](Invoke-Element $undo) }
$back = Wait-Until { $c = CwCards; if ($c.Count -eq $before.Count) { $c } } -Seconds 5 -PollMs 100
Check 'continue: Undo puts the card back where it was' ($back -and $back[1] -eq $target) (($back | Select-Object -First 3) -join ' | ')
Check 'continue: ... and its resume point (a second POST)' ([bool](Wait-Until { if ((CwUserDataPosts $t0) -ge 2) { $true } } -Seconds 5))
Check 'continue: ... and the notice goes, its Undo too' ([bool](Wait-Until { -not (Find-Ui -Name 'from Continue Watching' -Type 'Text' -Contains) -and -not (Find-Ui -Name 'Undo' -Type 'Button') } -Seconds 3))
Start-Sleep -Milliseconds 500
$f = CwFocused
Check 'continue: ... and the keyboard is on the card that came back (Undo had it)' ($f -and $f.Name -eq $target) ("{0} [{1}]" -f $f.Name, $f.Class)
CwShot 'cw-4-undone'

# --- a card of another row: no such entry
$movieCard = Find-Ui -Name $fixture.movie.name -Type 'ListItem' -Contains -OffscreenToo
if ($movieCard) {
    [void][OffscreenUi]::SetFocus($movieCard); Start-Sleep -Milliseconds 700
    Key 0x5D
    $any = Wait-Ui -Name 'Add to favorites' -Type 'MenuItem' -Seconds 4
    if (-not $any) { $any = Find-Ui -Name 'Remove from favorites' -Type 'MenuItem' }
    Check 'continue: a card of another row has a menu without "Remove from Continue Watching"' ($any -and -not (Find-Ui -Name 'Remove from Continue Watching' -Type 'MenuItem')) $fixture.movie.name
    Key 0x1B; Start-Sleep -Milliseconds 500
}

# --- removed for good: the notice goes by itself, the card stays out
[void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0)
Start-Sleep -Milliseconds 800
$entry2 = CwOpenMenu $target
if ($entry2) { [void](Invoke-Element $entry2) }
Start-Sleep -Milliseconds 800
CwShot 'cw-5-removed-again'
$expired = Wait-Until { -not (Find-Ui -Name 'from Continue Watching' -Type 'Text' -Contains) } -Seconds 16 -PollMs 500
Check 'continue: the notice goes by itself after about ten seconds' ([bool]$expired)
$after = CwCards
Check 'continue: ... and the card stays out of the row' ($after -notcontains $target -and $after.Count -eq $before.Count - 1) (($after | Select-Object -First 3) -join ' | ')
CwShot 'cw-6-row-after'
