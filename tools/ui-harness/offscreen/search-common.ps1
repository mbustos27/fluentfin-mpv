# The search scripts' helpers (then-search.ps1, then-search-live.ps1), dot-sourced after drive.ps1's own: the box, typing,
# the results page's headings, chips, "Show N more" buttons and lists, and a shot that includes the search list's popup.
# Dot-sourced: no variable here may reuse one of drive.ps1's names.
$srTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { 'run' }
Add-Type -Namespace SgSearch -Name W -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessage(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
# The search list is a popup window of its own, which a PrintWindow of the main window leaves out: SrShot prints each visible
# window of the app's own process (checked by process id) and draws it over the main window where it sits (as the layout
# audit's walk did).
if (-not ('SgPopShot' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices; using System.Text;
public static class SgPopShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  static Bitmap Print(IntPtr h, int w, int hh) {
    var b = new Bitmap(w, hh, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(b)) { var dc = g.GetHdc(); try { PrintWindow(h, dc, 2); } finally { g.ReleaseHdc(dc); } }
    return b;
  }
  public static string Shot(IntPtr main, int pid, string file) {
    if (Pid(main) != (uint)pid) throw new InvalidOperationException("not FluentFin's window");
    RECT r; GetWindowRect(main, out r);
    int w = r.R - r.L, h = r.B - r.T;
    var pops = new List<IntPtr>();
    EnumWindows((x, l) => { if (x != main && Pid(x) == (uint)pid && IsWindowVisible(x)) pops.Add(x); return true; }, IntPtr.Zero);
    EnumChildWindows(main, (x, l) => { if (Pid(x) == (uint)pid && IsWindowVisible(x) && Cls(x).Contains("Popup") && !pops.Contains(x)) pops.Add(x); return true; }, IntPtr.Zero);
    var drawn = new List<string>();
    using (var bmp = Print(main, w, h)) using (var g = Graphics.FromImage(bmp)) {
      foreach (var p in pops) {
        RECT pr; if (!GetWindowRect(p, out pr)) continue;
        int pw = pr.R - pr.L, ph = pr.B - pr.T;
        if (pw < 8 || ph < 8 || pw > 4 * w || ph > 4 * h) continue;
        if (pr.R <= r.L || pr.L >= r.R || pr.B <= r.T || pr.T >= r.B) continue;
        using (var pb = Print(p, pw, ph)) g.DrawImage(pb, pr.L - r.L, pr.T - r.T, pw, ph);
        drawn.Add(string.Format("{0} {1}x{2}", Cls(p), pw, ph));
      }
      bmp.Save(file, ImageFormat.Png);
    }
    return string.Format("{0}x{1}, popups: {2}", w, h, drawn.Count == 0 ? "none" : string.Join("; ", drawn));
  }
}
'@
}
function SrShot([string]$Name) {
    $script:shotN++
    $srFile = Join-Path $Out ("shots\{0:00}-{1}.png" -f $script:shotN, $Name)
    $srInfo = [SgPopShot]::Shot($script:hwnd, $script:ui.Id, $srFile)
    $shots[$Name] = $srFile
    Say "shot $Name -> $srFile ($srInfo)"
}
function SrSettle([int]$Ms = 1000) { Start-Sleep -Milliseconds $Ms }
function SrBox { Find-Ui -Name 'Search' -Type 'Edit' -Contains }
function SrText { $b = SrBox; if ($b) { try { ($b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value } catch { $null } } }
function SrRows { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, 'ListViewItem')))) }
# still on Home: its Continue Watching row (Classic, Drive-in) or its Up next shelf or list (Cinematic, Editorial: 1.5)
function SrHome { [bool](Find-Ui -Name 'Continue Watching' -Type 'Text' -OffscreenToo) -or [bool](Find-Ui -Name 'Up next' -Type 'Text' -OffscreenToo -Contains) }
# '/' focuses the box (WindowShortcuts) when it does not have the keyboard already (then '/' would be typed): UI Automation
# otherwise; the characters go as WM_CHAR to the window WinUI reads keys from
function SrType([string]$Text, [switch]$Slash) {
    if ($Slash) { Key 0xBF } else { $b = SrBox; if ($b) { [void][OffscreenUi]::SetFocus($b) } }
    SrSettle 500
    # an empty box first (a results page keeps its query in it); set through UI Automation: no search starts for it
    $b = SrBox
    if ($b -and (SrText)) { try { ([System.Windows.Automation.ValuePattern]$b.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue(''); SrSettle 300 } catch { } }
    $kw = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
    foreach ($ch in $Text.ToCharArray()) { [void][SgSearch.W]::PostMessage($kw, 0x0102, [IntPtr][int]$ch, [IntPtr]1); Start-Sleep -Milliseconds 80 }
    SrSettle 300
}


# ---- Nixie 1.5: the results page
$srSunny = "It's Always Sunny in Philadelphia"
function SrTexts { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | Where-Object { -not $_.Current.IsOffscreen }) }
function SrHeadings { @(SrTexts | Where-Object { $_.Current.Name -match '^(Shows|Movies|Episodes|Collections|People|Videos) \(\d+\)$' } | Sort-Object { [OffscreenUi]::RectOf($_).Top } | ForEach-Object { $_.Current.Name }) }
function SrChips { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton))) | Where-Object { $_.Current.Name -match '^(All|Shows|Movies|Episodes|Collections|People|Videos), \d+ results?$' }) }
function SrChip([string]$Label) { SrChips | Where-Object { $_.Current.Name -like "$Label,*" } | Select-Object -First 1 }
function SrLit { @(SrChips | Where-Object { $p = $null; $_.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p) -and $p.Current.IsSelected } | ForEach-Object { ($_.Current.Name -split ',')[0] }) }
function SrMore([string]$Nouns) { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button))) | Where-Object { $_.Current.Name -match "^Show (\d+ more|fewer) $Nouns" }) | Select-Object -First 1 }
function SrList([string]$Name) { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List))) | Where-Object { $_.Current.Name -eq $Name }) | Select-Object -First 1 }
# the pointer's way: UI Automation's Invoke (or Select), which raises the control's Click as a click does (a posted click
# does not reach WinUI on the harness's private desktop: then-trailer.ps1 found the same)
function SrClick($Element) { [void](Invoke-Element $Element) }
function SrFocused { [OffscreenUi]::Focused($script:hwnd, $script:ui.Id) }
function SrSearch([string]$Query) {
    SrType $Query -Slash
    Key 0x0D
    [void](Wait-Until { if ((Find-Ui -Name "Results for" -Type 'Text' -Contains) -and -not (Find-Ui -Name 'Searching' -Type 'Text' -Contains)) { $true } } -Seconds 20 -PollMs 200)
    SrSettle 1500
}

