# Nixie 1.4.0, item 3 of the library and collections review's plan: the contrast floor, as drawn. Every label of the top
# bar (the tabs, the wordmark, the search placeholder, Back, the account) and of a library's toolbar (Unwatched, Sort, the
# direction, Filter, View), and the labels between the bar and a library's grid (its name, the sections, the letters, the
# place), are measured from the window's own pixels (PrintWindow: every layer composited, the glass and the scrims over the
# artwork included) at 4.5:1 or more, over the worst case any artwork can be: the fake server's flat art, every image white
# (FAKESERVER_ART=white) or black (FAKESERVER_ART=black). Run once per layout theme (NIXIE_LAYOUT), app theme (NIXIE_THEME
# Light or Dark) and art; tools\ui-harness\contrast-matrix.ps1 runs all 16 and writes the table:
#   $env:NIXIE_LAYOUT='DriveIn'; $env:NIXIE_THEME='Light'; $env:FAKESERVER_ART='white'; $env:FAKESERVER_COLLECTIONS='1'
#   run-offscreen.ps1 ... -Script home -Then offscreen\then-140-contrast.ps1
# Pages: Home, the Movies library (1920 and 1100 wide), the Libraries page, the showcase movie's page, the Collections
# library and a collection (with FAKESERVER_COLLECTIONS). A label's ground is the median of its box's edge (no glyph there),
# its ink the pixel farthest from it (the review's measure). A disabled control (Back on Home) is not held to it (WCAG
# 1.4.3's inactive components), and the caption buttons' glyphs, drawn by Windows, are graphics: 3:1 (1.4.11).
# Output: <Out>\contrast\contrast.json (every measure), a crop of each label (<page>-<label>.png), shots <tag>-<page>.png.
# Dot-sourced by drive.ps1: every name here starts with ct.

$ctArt = if ($env:FAKESERVER_ART) { $env:FAKESERVER_ART.ToLowerInvariant() } else { 'own' }
$ctLayout = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$ctMode = if ($env:NIXIE_THEME) { $env:NIXIE_THEME } else { 'System' }
$ctTag = "$($ctLayout.ToLowerInvariant())-$($ctMode.ToLowerInvariant())-$ctArt"
$ctDir = Join-Path $Out 'contrast'
New-Item -ItemType Directory -Force $ctDir | Out-Null
$ctRows = New-Object System.Collections.ArrayList
$ctUia = [System.Windows.Automation.AutomationElement]
$ctTypes = [System.Windows.Automation.ControlType]

if (-not ('CtW' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public static class CtW {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  // the window as drawn (PW_RENDERFULLCONTENT: the composition's layers too), and where its top-left corner is on the screen
  public static Bitmap Grab(IntPtr main, out int left, out int top) {
    RECT r; GetWindowRect(main, out r); left = r.L; top = r.T;
    var bmp = new Bitmap(r.R - r.L, r.B - r.T, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
    return bmp;
  }
  static double Lin(int v) { double c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
  public static double Lum(int argb) { return 0.2126 * Lin((argb >> 16) & 255) + 0.7152 * Lin((argb >> 8) & 255) + 0.0722 * Lin(argb & 255); }
  // a label's contrast in the box (x, y, w, h; the bitmap's pixels): the ground is the median of the box's edge, the ink the
  // pixel farthest from it. Returns { ratio, ink luminance, ground luminance, ink RGB, ground RGB }, null for an empty box.
  public static double[] Measure(Bitmap bmp, int x, int y, int w, int h, string save) {
    int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(bmp.Width, x + w), y1 = Math.Min(bmp.Height, y + h);
    if (x1 - x0 < 4 || y1 - y0 < 4) return null;
    var rect = new Rectangle(x0, y0, x1 - x0, y1 - y0);
    var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var px = new int[rect.Width * rect.Height];
    try { for (int j = 0; j < rect.Height; j++) Marshal.Copy(data.Scan0 + j * data.Stride, px, j * rect.Width, rect.Width); }
    finally { bmp.UnlockBits(data); }
    var edge = new List<KeyValuePair<double, int>>();
    for (int j = 0; j < rect.Height; j++) for (int i = 0; i < rect.Width; i++)
      if (i == 0 || j == 0 || i == rect.Width - 1 || j == rect.Height - 1) { int p = px[j * rect.Width + i]; edge.Add(new KeyValuePair<double, int>(Lum(p), p)); }
    edge.Sort((a, b) => a.Key.CompareTo(b.Key));
    var ground = edge[edge.Count / 2];
    double far = -1; int ink = ground.Value; double inkL = ground.Key;
    foreach (var p in px) { double l = Lum(p); if (Math.Abs(l - ground.Key) > far) { far = Math.Abs(l - ground.Key); ink = p; inkL = l; } }
    if (!string.IsNullOrEmpty(save)) using (var crop = bmp.Clone(rect, PixelFormat.Format32bppArgb)) crop.Save(save, ImageFormat.Png);
    double hi = Math.Max(inkL, ground.Key), lo = Math.Min(inkL, ground.Key);
    return new[] { (hi + 0.05) / (lo + 0.05), inkL, ground.Key, ink & 0xFFFFFF, ground.Value & 0xFFFFFF };
  }
}
"@
}

function CtSettle([int]$Ms = 900) { Start-Sleep -Milliseconds $Ms }
function CtSize([int]$W, [int]$H) { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $W, $H); CtSettle 2000 }
function CtWin { $c = [IcWClient]::Client($script:hwnd); [pscustomobject]@{ Left = $c[0]; Top = $c[1]; Width = $c[2]; Height = $c[3] } }
if (-not ('IcWClient' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class IcWClient {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  public static int[] Client(IntPtr h) { RECT r; GetClientRect(h, out r); var p = new POINT(); ClientToScreen(h, ref p); return new[] { p.X, p.Y, r.R - r.L, r.B - r.T }; }
}
"@
}

# every element shown in the window (one walk), with what the measures need
function CtElements {
    $win = CtWin
    @([OffscreenUi]::All($script:hwnd, $script:ui.Id) | ForEach-Object {
        try {
            $c = $_.Current
            if ($c.IsOffscreen) { return }
            $r = $c.BoundingRectangle
            if ($r.IsEmpty -or $r.Width -lt 4 -or $r.Height -lt 4) { return }
            [pscustomobject]@{ E = $_; Name = $c.Name; Id = $c.AutomationId; Type = $c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
                Class = $c.ClassName; Enabled = $c.IsEnabled; X = $r.Left - $win.Left; Y = $r.Top - $win.Top; W = $r.Width; H = $r.Height }
        } catch { }
    })
}

# one page as drawn: the measures of its labels ($Boxes: label, box in client pixels, whether it is held to 4.5:1)
function CtPage([string]$Page, $Boxes) {
    Shot "$ctTag-$Page"
    $left = 0; $top = 0
    $bmp = [CtW]::Grab($script:hwnd, [ref]$left, [ref]$top)
    try {
        $win = CtWin
        $dx = $win.Left - $left; $dy = $win.Top - $top
        foreach ($b in $Boxes) {
            $file = Join-Path $ctDir ("{0}-{1}-{2}.png" -f $ctTag, $Page, (Safe-Name $b.Label))
            $m = [CtW]::Measure($bmp, [int]($b.X + $dx), [int]($b.Y + $dy), [int]$b.W, [int]$b.H, $file)
            $min = if ($b.Min) { [double]$b.Min } else { 4.5 }
            $row = [ordered]@{ tag = $ctTag; layout = $ctLayout; mode = $ctMode; art = $ctArt; page = $Page; label = $b.Label; held = [bool]$b.Held; min = $min
                ratio = $(if ($m) { [Math]::Round($m[0], 2) } else { $null }); ink = $(if ($m) { '#{0:X6}' -f [int]$m[3] }); ground = $(if ($m) { '#{0:X6}' -f [int]$m[4] })
                box = "$([int]$b.X),$([int]$b.Y) $([int]$b.W)x$([int]$b.H)"; crop = $file }
            [void]$ctRows.Add([pscustomobject]$row)
            $detail = if ($m) { "{0:N2}:1 (ink {1}, ground {2})" -f $m[0], $row.ink, $row.ground } else { 'not measured (no box)' }
            if ($b.Held) { Check "contrast ($ctTag, $Page): $($b.Label) at $($min):1 or more" ($m -and $m[0] -ge $min) $detail }
            else { Say "contrast ($ctTag, $Page): $($b.Label) (reported only): $detail" }
        }
    }
    finally { $bmp.Dispose() }
}

# a label's box: the element's rectangle, inset (its border and focus room left out), and at most $Band high around its middle
function CtBox($El, [string]$Label, [int]$Inset = 3, [int]$Band = 0, [bool]$Held = $true, [double]$Min = 4.5) {
    $x = $El.X + $Inset; $y = $El.Y + $Inset; $w = $El.W - 2 * $Inset; $h = $El.H - 2 * $Inset
    if ($Band -gt 0 -and $h -gt $Band) { $y = $El.Y + ($El.H - $Band) / 2; $h = $Band }
    [pscustomobject]@{ Label = $Label; X = $x; Y = $y; W = $w; H = $h; Held = $Held; Min = $Min }
}

# the top bar's labels (everything in the window's top 48 px)
function CtBarBoxes($Els) {
    $bar = @($Els | Where-Object { ($_.Y + $_.H / 2) -lt 48 -and $_.Y -ge -2 })
    $boxes = New-Object System.Collections.ArrayList
    foreach ($t in @($bar | Where-Object { $_.Type -eq 'TabItem' })) { [void]$boxes.Add((CtBox $t "tab '$($t.Name -replace ('\s*' + [char]0x25BE), '')'" 3 22)) }
    foreach ($e in $bar) {
        switch -Regex ($e.Id) {
            '^Wordmark$' { [void]$boxes.Add((CtBox $e 'wordmark' 2 26)); continue }
            '^SubtitleText$' { [void]$boxes.Add((CtBox $e 'wordmark subtitle' 0 0)); continue }
            '^TopTabsMenu$' { [void]$boxes.Add((CtBox $e 'sections menu' 3 22)); continue }
        }
        if ($e.Type -eq 'Edit' -and $e.Name -like '*Search*') {
            # the placeholder: the box's left part (its query button, the magnifier, is a label of its own)
            [void]$boxes.Add([pscustomobject]@{ Label = 'search placeholder'; X = $e.X + 3; Y = $e.Y + ($e.H - 22) / 2; W = [Math]::Min(110, $e.W - 44); H = 22; Held = $true })
            # (the magnifier is a graphic: WCAG 1.4.11's 3:1)
            [void]$boxes.Add([pscustomobject]@{ Label = 'search glyph'; X = $e.X + $e.W - 34; Y = $e.Y + ($e.H - 22) / 2; W = 30; H = 22; Held = $true; Min = 3.0 })
        }
        elseif ($e.Type -eq 'Button' -and $e.Name -eq 'Search') { [void]$boxes.Add((CtBox $e 'search button' 3 24)) }
        elseif ($e.Type -eq 'Button' -and $e.Name -eq 'Back') {
            # a disabled Back (Home, the first page) is not held to it: WCAG 1.4.3's inactive components
            [void]$boxes.Add((CtBox $e "back arrow$(if (-not $e.Enabled) { ' (disabled)' })" 4 24 ([bool]$e.Enabled)))
        }
        elseif ($e.Type -eq 'Button' -and $e.Name -like 'Account, *') { [void]$boxes.Add((CtBox $e 'account' 6 0)) }
        # the caption buttons' glyphs (drawn by Windows in the colours the bar gives it): graphics, WCAG 1.4.11's 3:1
        elseif ($e.Type -eq 'Button' -and $e.Name -match '^(Minimize|Maximize|Restore|Close)$') { [void]$boxes.Add((CtBox $e "caption $($e.Name)" 8 0 $true 3.0)) }
    }
    $boxes
}

# the library's toolbar (LibraryToolbar's parts, by name)
function CtToolbarBoxes($Els) {
    $boxes = New-Object System.Collections.ArrayList
    foreach ($id in @('UnwatchedToggle', 'FavoritesToggle', 'SortButton', 'DirectionButton', 'FilterButton', 'ViewButton')) {
        $e = @($Els | Where-Object { $_.Id -eq $id }) | Select-Object -First 1
        if ($e) { [void]$boxes.Add((CtBox $e "toolbar $($id -replace '(Button|Toggle)$', '')" 3 0)) }
    }
    $boxes
}

# the labels between the bar and the grid (or a page's first row): texts, the sections, the letters, the buttons (the
# stage's or the hero's words and actions too); a slideshow's picture buttons are art, not labels. -Held:$false: reported only
function CtHeaderBoxes($Els, [double]$GridTop, [bool]$Held = $true) {
    $boxes = New-Object System.Collections.ArrayList
    $toolbar = @('UnwatchedToggle', 'FavoritesToggle', 'SortButton', 'DirectionButton', 'FilterButton', 'ViewButton')
    $head = @($Els | Where-Object { $_.Y -ge 48 -and ($_.Y + $_.H) -le $GridTop + 2 -and $toolbar -notcontains $_.Id })
    # a row that scrolls on fades its last chips into the ground by design (Editorial's genres: the overflow cue, its More
    # genres chevron shown): those are reported, not held
    $fade = @($Els | Where-Object { $_.Id -eq 'ChipScroller' }) | Select-Object -First 1
    $faded = if ($fade -and @($Els | Where-Object { $_.Id -eq 'ChipMore' })) { $fade.X + $fade.W - 56 } else { [double]::MaxValue }
    foreach ($t in @($head | Where-Object { $_.Type -eq 'Text' -and "$($_.Name)".Trim() })) {
        $label = "text '$((($t.Name -replace '\s+', ' ').Trim()) -replace '^(.{28}).+$', '$1...')'"
        if (($t.X + $t.W) -gt $faded) { [void]$boxes.Add((CtBox $t "$label (under the overflow fade)" -2 0 $false)) } else { [void]$boxes.Add((CtBox $t $label -2 0 $Held)) }
    }
    # the A to Z's letters ("Jump to M"; the absent ones are not shown to UI Automation: dimmed, inactive, not held)
    $letters = @($head | Where-Object { $_.Type -eq 'Button' -and $_.Enabled -and $_.Name -match '^(Jump to )?([#A-Z]|A to Z|Z to A)(,|$)' -and $_.W -le 60 })
    foreach ($l in $letters) { [void]$boxes.Add((CtBox $l "letter '$($l.Name -replace '^Jump to ', '' -replace ',.*$', '')'" 3 0 $Held)) }
    foreach ($t in @($head | Where-Object { $_.Type -eq 'TabItem' })) {
        if (($t.X + $t.W) -gt $faded) { [void]$boxes.Add((CtBox $t "section '$($t.Name)' (under the overflow fade)" 3 0 $false)) } else { [void]$boxes.Add((CtBox $t "section '$($t.Name)'" 3 0 $Held)) }
    }
    foreach ($b in @($head | Where-Object { ($_.Type -eq 'Button' -or $_.Type -eq 'CheckBox' -or $_.Type -eq 'Hyperlink') -and $_.Enabled -and $letters -notcontains $_ -and $_.W -ge 24 -and "$($_.Name)".Trim() -and $_.Name -notmatch '^Slide \d' })) {
        [void]$boxes.Add((CtBox $b "button '$($b.Name -replace ',.*$', '')'" 3 0 $Held))
    }
    $boxes
}

function CtGridTop($Els) {
    $cards = @($Els | Where-Object { $_.Type -eq 'ListItem' -and $_.Y -gt 120 -and $_.Name } | Sort-Object Y)
    if ($cards) { $cards[0].Y } else { (CtWin).Height }
}

# waits for a page's art to be drawn: its cards, then the images and the stage's crossfade
function CtWaitCards([double]$Seconds = 30) {
    [void](Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($ctUia::ControlTypeProperty, $ctTypes::ListItem))) | Where-Object { try { -not $_.Current.IsOffscreen } catch { $false } }).Count -ge 2 } -Seconds $Seconds -PollMs 300)
    CtSettle 3000
}

function CtLibrary([string]$Name, [string]$Page) {
    $els = CtElements
    $boxes = New-Object System.Collections.ArrayList
    foreach ($b in @(CtBarBoxes $els) + @(CtToolbarBoxes $els) + @(CtHeaderBoxes $els (CtGridTop $els))) { if ($b -and $b.Label) { [void]$boxes.Add($b) } }
    if (-not @($boxes | Where-Object { $_.Label -like 'toolbar Sort*' })) { Check "contrast ($ctTag, $Page): the toolbar is there to measure" $false 'no SortButton' }
    if ($env:CT_DUMP) { Dump-Tree "$ctTag-$Page" }
    CtPage $Page $boxes
}

Say "contrast: $ctTag (FAKESERVER_ART=$($env:FAKESERVER_ART))"
CtSize 1920 1080

# ---- Home ----------------------------------------------------------------------------------------------------------------
Open-Nav 'Home'; CtSettle 4000
$ctEls = CtElements
if ($env:CT_DUMP) { Dump-Tree "$ctTag-home" }
# the hero's words and actions over its art: Cinematic's are held (its controls on art: the UX review's C-M2), the other
# themes' are reported
$ctHeroHeld = $ctLayout -eq 'Cinematic'
CtPage 'home' (@(CtBarBoxes $ctEls) + @(CtHeaderBoxes $ctEls (CtGridTop $ctEls) $ctHeroHeld))

# ---- the Movies library, 1920 and 1100 wide ------------------------------------------------------------------------------
Open-Library $fixture.movie.library
CtWaitCards
CtLibrary $fixture.movie.library 'movies-1920'
CtSize 1100 900
CtSettle 1500
CtLibrary $fixture.movie.library 'movies-1100'
CtSize 1920 1080

# ---- the Libraries page ---------------------------------------------------------------------------------------------------
Open-Nav 'Libraries'
[void](Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, (New-Object System.Windows.Automation.PropertyCondition($ctUia::ControlTypeProperty, $ctTypes::ListItem)))).Count -ge 4 } -Seconds 15 -PollMs 300)
CtSettle 2500
CtPage 'libraries' @(CtBarBoxes (CtElements))

# ---- the showcase movie's page (its backdrop under the bar) ----------------------------------------------------------------
try {
    Open-Library $fixture.movie.library
    CtWaitCards
    Open-Card $fixture.movie.name
    if (Wait-ItemPage $fixture.movie.name 20) {
        CtSettle 3500
        $ctEls = CtElements
        if ($env:CT_DUMP) { Dump-Tree "$ctTag-movie" }
        CtPage 'movie' (@(CtBarBoxes $ctEls) + @(CtHeaderBoxes $ctEls (CtGridTop $ctEls) $ctHeroHeld))
        # the search box while you type in it (its focused glass), over the same art: / puts the keyboard in it
        Key 0xBF; CtSettle 900
        $ctFocus = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id) | ConvertFrom-Json
        if ($ctFocus -and $ctFocus.type -eq 'Edit') {
            $ctBoxes = @(CtBarBoxes (CtElements) | Where-Object { $_.Label -like 'search*' } | ForEach-Object { $_.Label += ' (typing)'; $_ })
            CtPage 'movie-search' $ctBoxes
        }
        else { Check "contrast ($ctTag): / puts the keyboard in the search box" $false "$($ctFocus.type) '$($ctFocus.name)'" }
    }
    else { Check "contrast ($ctTag): the movie page opens" $false $fixture.movie.name }
}
catch { Check "contrast ($ctTag): the movie page opens" $false $_.Exception.Message }

# ---- the Collections library and a collection (FAKESERVER_COLLECTIONS) -------------------------------------------------------
if ($fixture.views -contains 'Collections' -and @($fixture.collections).Count) {
    try {
        Open-Nav 'Home'; CtSettle 800
        Open-Library 'Collections'
        CtWaitCards
        CtLibrary 'Collections' 'collections'
        $ctFirst = @($fixture.collections)[0].name
        Open-Card $ctFirst
        [void](Wait-Until { Find-Ui -Name $ctFirst -Type 'Text' } -Seconds 15 -PollMs 250)
        CtWaitCards
        $ctEls = CtElements
        $ctBoxes = New-Object System.Collections.ArrayList
        foreach ($b in @(CtBarBoxes $ctEls) + @(CtHeaderBoxes $ctEls (CtGridTop $ctEls))) { if ($b -and $b.Label) { [void]$ctBoxes.Add($b) } }
        if ($env:CT_DUMP) { Dump-Tree "$ctTag-collection" }
        CtPage 'collection' $ctBoxes
    }
    catch { Check "contrast ($ctTag): the Collections library and a collection" $false $_.Exception.Message }
}

$ctRows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ctDir 'contrast.json') -Encoding utf8
$ctHeld = @($ctRows | Where-Object { $_.held })
$ctLow = @($ctHeld | Where-Object { $null -eq $_.ratio -or $_.ratio -lt $_.min })
Say ("contrast ($ctTag): {0} labels held to their floor, {1} below; the lowest: {2}" -f $ctHeld.Count, $ctLow.Count,
    (($ctHeld | Sort-Object ratio | Select-Object -First 3 | ForEach-Object { "$($_.page) $($_.label) $($_.ratio)" }) -join '; '))
