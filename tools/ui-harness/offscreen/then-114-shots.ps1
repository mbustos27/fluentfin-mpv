# Seaglass 1.1.4 (library stream): the screenshots, once per theme and glass mode (SEAGLASS_THEME=Light|Dark in the
# environment, -Setting Glass=Full|Solid, SG_SHOT_TAG naming them): run with -Script home -Then offscreen\then-114-shots.ps1.
# then-scroll-shots.ps1's (Home's rows with their headers, the Library with its bar and A-Z, a season), then at 1920 and
# 1100 wide: a Continue Watching card's menu, the row after "Remove from Continue Watching" with its notice, and after Undo;
# the Library's Filter menu. Menus are windows of their own: drawn over the page where they are.
# Dot-sourced by drive.ps1: no variable here may reuse one of its names ($out is its -Out).
. (Join-Path $PSScriptRoot 'then-scroll-shots.ps1')

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
  public static string Shot(IntPtr main, int pid, string file) {
    RECT r; GetWindowRect(main, out r);
    int w = r.R - r.L, h = r.B - r.T, extra = 0;
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); }
      var popups = new List<IntPtr>();
      EnumWindows((x, l) => { uint p; GetWindowThreadProcessId(x, out p); if (p == (uint)pid && x != main && IsWindowVisible(x)) popups.Add(x); return true; }, IntPtr.Zero);
      popups.Reverse();
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
function Shot114([string]$Name) {
    $script:shotN++
    $file = Join-Path $Out ("shots\{0:00}-{1}.png" -f $script:shotN, $Name)
    $info = [CwShot]::Shot($script:hwnd, $script:ui.Id, $file)
    $shots[$Name] = $file
    Say "shot $Name -> $file ($info)"
}
function Cw-Cards {
    $list = Find-Ui -Name 'Continue Watching' -Type 'List'
    if (-not $list) { return @() }
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    @($list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | ForEach-Object { $_.Current.Name })
}

Open-Nav 'Home'
Settle 2500
foreach ($size in @(@(1920, 1080), @(1100, 900))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); Settle 1500
    [void][OffscreenUi]::ScrollPage($script:hwnd, $script:ui.Id, 0); Settle 800
    $cards = Cw-Cards
    if ($cards.Count -lt 2) { Say "shots: no Continue Watching row to show"; continue }
    $card = Find-Ui -Name $cards[1] -Type 'ListItem'
    [void][OffscreenUi]::SetFocus($card); Settle 500
    Key 0x5D
    $entry = Wait-Ui -Name 'Remove from Continue Watching' -Type 'MenuItem' -Seconds 4
    Settle 400
    Shot114 "$tag-cw-menu-$($size[0])"
    if (-not $entry) { Key 0x1B; continue }
    [void](Invoke-Element $entry); Settle 1200
    Shot114 "$tag-cw-removed-notice-$($size[0])"
    $undo = Find-Ui -Name 'Undo' -Type 'Button'
    if ($undo) { [void](Invoke-Element $undo); Settle 1200 }
    Shot114 "$tag-cw-undone-$($size[0])"
}

# the Library's Filter menu, both widths
Open-Library $fixture.movie.library
[void](Wait-Until { [OffscreenUi]::FindCard($script:hwnd, $script:ui.Id, $fixture.movie.name) } -Seconds 30 -PollMs 150)
Settle 1500
foreach ($size in @(@(1920, 1080), @(1100, 900))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); Settle 1500
    $filterBtn = Find-Ui -Name 'Filter' -Type 'Button' -Contains
    if ($filterBtn) { [void](Invoke-Element $filterBtn); Settle 1200; Shot114 "$tag-library-filter-$($size[0])"; Key 0x1B; Settle 600 }
}
