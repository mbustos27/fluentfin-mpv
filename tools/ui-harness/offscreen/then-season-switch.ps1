# Season speed (the owner, 2026-11-19: "make seasons display load faster clicking between the different ones"): how long a
# season switch takes, from the click on a season pill to the new season's episodes on screen and their pictures in, in the
# current NIXIE_LAYOUT theme. Run with the fake server's big series on:
#   $env:FAKESERVER_BIG_SERIES='10x20'; $env:NIXIE_LAYOUT='<Classic|DriveIn|Cinematic|Editorial>'
#   run-offscreen.ps1 ... -Script wait:1 -NoA11y -Then offscreen\then-season-switch.ps1
#     [-Latency 50 -LatencyRule '^/Items\?.*ParentId==300','/Items/[^/]+/Images/=80']
# Two parts, each a list of switches:
#   series  the series page (A Tide of Seasons: 10 seasons of 20 episodes, opened on Season 1): its season pills switch the
#           list in place: 1>2 and 2>3 (asked ahead), 3>2 and 2>1 (seen), a fast click-through 2>3>4 (the pills found once,
#           then the three clicks posted ~75 ms apart), 4>1, 1>9 (a season nothing asked yet) and 9>1;
#   season  the series page's "Season 1 details" (series>season page), then a season page's pills, each opening another
#           season's page: 1>6 (nothing asked it yet), 6>7 (asked ahead), 7>6 and 6>1 (seen), a fast click-through 7>8>9
#           (each click as soon as the page the last one opened has its pills, as a user must wait for them), and 9>6.
# Each click goes through the dev build's pointer hook (HarnessPointer: what a click does, focus included; posted clicks never
# reach WinUI off-screen; a pill off screen is invoked through UI Automation instead and says so). Per switch:
#   rows_ms       click (the hook's message posted) -> the new season's first episode (its own name, unique in the series) on
#                 screen in UI Automation (a thread searching the window's episode items back to back: `poll_ms` is how long
#                 one search took, plus the pause between searches, SS_POLL_GAP_MS (40 ms by default; 10, 40 and 150 gave
#                 the same app times), is the timing's resolution; the hook waits 40 ms between press and click, as a press does);
#   clean_ms      -> no other season's episode in the tree any more (a page left behind, an old list);
#   images_ms     -> the last picture of the new season's episodes the app asked for answered (the fake server's log;
#                 decoding is not in it); `cached` when it asked for none (the pictures were kept); images_n, their sizes;
#   list_asked_ms / list_answered_ms   the season's episode list (/Items?ParentId=<season>) asked and answered;
#   ui_after_answer_ms   rows_ms - list_answered_ms: the app's own time from the list's answer to the rows (and the poll);
#   requests      the app's requests from the click to the end of the switch, by kind, `in_flight` (most at once) and the
#                 `timeline` of the ones that are not episode pictures (kind start..end, ms after the last click);
#   hook_ms       when FluentFin's UI thread ran each click (its log line), ms after the first was posted;
#   art_pct       share of coloured pixels where the first episode's picture is drawn, once settled (a picture, not a blank);
#   seasons_seen  every season whose episodes showed after the first click, in order, with when (ms after the last click).
# A gate (Nixie 1.4.1, the season speed fix). Per switch it FAILs when: the switch does not end on the season clicked (its
# episodes only), or a season comes back after a newer one showed (a late answer winning); the picker marks another season,
# or more than one, as current (its mark, Narrator's "current season"); a click-through finds the pills disabled while a
# season loads (its clicks were dropped); a season seen or asked ahead (1>2, 2>3 and 6>7 are: a page asks the seasons
# either side of the one shown) is asked of the server again; a row on screen never asked for its own picture (a reused row
# showing another episode's); Enter or Space on a pill of the series page leaves the keyboard anywhere but the new
# current pill (Classic and Drive-in); the rows or the pictures miss their loose time limit ($ssLimits,
# per theme and kind; SS_LIMIT_SCALE widens them). The build before the fix fails the picker (Classic's series page), the
# dropped clicks (Classic, Drive-in), every "kept" request check and, under the latency rules, the kept time limits. The
# table goes to drive.log, result.json (timings.season_switch) and season-switch.json; one shot per switch,
# <tag>-<part>-<n>-s<season>.png. Dot-sourced by drive.ps1: every name here starts with ss / Ss- ($out is its -Out).
# Nixie 1.5 (the season stream): a show of at most 12 seasons asks every season ahead, in turn, so 1>9 and the season page's
# first switch to 6 are "far, asked ahead" (kept: no list request; a longer big series, e.g. 13x20, keeps them "new"); the
# table's img1< is when the first picture was asked (ms after the last click) and x the most pictures at once, and a switch
# of 6 pictures or more (not a click-through) FAILs when they come 2 at a time (WinINet's default for a program; the build
# before 1.5 does). The seasons either side of the one shown have their stills asked ahead (StillsAhead, 2 at a time, a
# second after their list): their switches show the pictures kept.

$ssTheme = if ($env:NIXIE_LAYOUT) { $env:NIXIE_LAYOUT } else { 'Classic' }
$ssTag = if ($env:SG_SHOT_TAG) { $env:SG_SHOT_TAG } else { $ssTheme.ToLowerInvariant() }
$ssBs = $fixture.big_series
if (-not $ssBs) {
    Check "season switch ($ssTag): the fake server's big series (FAKESERVER_BIG_SERIES=10x20)" $false 'the fixture has no big_series'
    return
}

if (-not ('SsWatch' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing, UIAutomationClient, UIAutomationTypes, WindowsBase, System.Core @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

public static class SsHarness {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

  public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

  // the dev build's pointer hook (HarnessPointer): kind 1 click; x, y on the screen. Returns at once (a posted message).
  public static bool Click(IntPtr main, int x, int y) {
    uint msg = RegisterWindowMessage("NixieHarnessPointer");
    return PostMessage(main, msg, (IntPtr)1, (IntPtr)(long)((uint)(x & 0xFFFF) | ((uint)(y & 0xFFFF) << 16)));
  }

  static CacheRequest Request() {
    var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
    c.Add(AutomationElement.NameProperty); c.Add(AutomationElement.ClassNameProperty);
    c.Add(AutomationElement.BoundingRectangleProperty); c.Add(AutomationElement.IsOffscreenProperty);
    c.Add(AutomationElement.ControlTypeProperty); c.Add(SelectionItemPattern.IsSelectedProperty);
    c.Add(AutomationElement.IsEnabledProperty);
    return c;
  }

  static string Line(AutomationElement e) {
    var r = e.Cached.BoundingRectangle;
    string rect = r.IsEmpty ? "-" : string.Format("{0:0},{1:0},{2:0},{3:0}", r.X, r.Y, r.Width, r.Height);
    object sel = e.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true);
    return string.Join("\u001f", new[] { e.Cached.ClassName ?? "", e.Cached.Name ?? "", rect, e.Cached.IsOffscreen ? "1" : "0",
      (sel is bool && (bool)sel) ? "1" : "0", e.Cached.IsEnabled ? "1" : "0" });
  }

  // one look at the episode items: "class<US>name<US>x,y,w,h<US>offscreen<US>selected" for every element of these classes
  public static List<string> Items(AutomationElement root, string[] classes) {
    Condition cond = classes.Length == 1 ? (Condition)new PropertyCondition(AutomationElement.ClassNameProperty, classes[0])
      : new OrCondition(classes.Select(c => (Condition)new PropertyCondition(AutomationElement.ClassNameProperty, c)).ToArray());
    using (Request().Activate()) return root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().Select(Line).ToList();
  }

  // the season pills (any button, tab or list item named "Season N" or "Season N, ..."): the same lines
  static readonly Regex Pill = new Regex(@"^Season \d+(,|$)");
  // a search that fails while the page changes under it (ElementNotAvailable, a bare COMException) is tried again, 4 times
  static T Retry<T>(Func<T> search) {
    for (int attempt = 0; ; attempt++) {
      try { return search(); }
      catch (Exception) { if (attempt >= 3) throw; Thread.Sleep(60 + 80 * attempt); }
    }
  }

  // the name of the first element with this automation id (null when there is none): a page's own button
  public static string NameOfId(AutomationElement root, string id) {
    return Retry(() => {
      var c = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
      c.Add(AutomationElement.NameProperty);
      using (c.Activate()) { var e = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); return e == null ? null : e.Cached.Name; }
    });
  }

  public static List<string> Pills(AutomationElement root) {
    var cond = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
      new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
      new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
      new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton));
    return Retry(() => {
      using (Request().Activate())
        return root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>().Where(e => Pill.IsMatch(e.Cached.Name ?? "")).Select(Line).ToList();
    });
  }

  // the share (%) of coloured pixels in a rectangle of the window (screen coordinates): a picture there, not a blank
  public static int Coloured(IntPtr main, int x, int y, int w, int h) {
    RECT r; GetWindowRect(main, out r);
    int ww = r.R - r.L, wh = r.B - r.T;
    int x0 = Math.Max(0, x - r.L), y0 = Math.Max(0, y - r.T), x1 = Math.Min(ww, x - r.L + w), y1 = Math.Min(wh, y - r.T + h);
    if (x1 <= x0 || y1 <= y0) return -1;
    using (var bmp = new Bitmap(ww, wh, PixelFormat.Format32bppRgb)) {
      using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
      var data = bmp.LockBits(new Rectangle(x0, y0, x1 - x0, y1 - y0), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
      try {
        int n = 0, all = (x1 - x0) * (y1 - y0);
        var row = new int[x1 - x0];
        for (int yy = 0; yy < y1 - y0; yy++) {
          Marshal.Copy(data.Scan0 + yy * data.Stride, row, 0, row.Length);
          foreach (var p in row) {
            int R = (p >> 16) & 255, G = (p >> 8) & 255, B = p & 255;
            int mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
            if (mx - mn > 48 && mx > 60) n++;
          }
        }
        return (int)Math.Round(100.0 * n / all);
      } finally { bmp.UnlockBits(data); }
    }
  }
}

// a thread looking at the window's episode items back to back while a switch runs: each frame "start ms<RS>search ms<RS>items"
// (items joined by <GS>; "!<exception>" when a search failed while the page changed under it)
public sealed class SsWatch {
  readonly IntPtr _main; readonly string[] _classes; Thread _thread; volatile bool _stop;
  readonly List<string> _frames = new List<string>();
  public int GapMs = 10;
  public SsWatch(IntPtr main, string[] classes) { _main = main; _classes = classes; }
  public void Start() {
    _thread = new Thread(Run) { IsBackground = true };
    _thread.SetApartmentState(ApartmentState.MTA);
    _thread.Start();
  }
  void Run() {
    var root = AutomationElement.FromHandle(_main);
    while (!_stop) {
      long t0 = SsHarness.Now();
      string line;
      try { var items = SsHarness.Items(root, _classes); line = t0 + "\u001e" + (SsHarness.Now() - t0) + "\u001e" + string.Join("\u001d", items); }
      catch (Exception ex) { line = t0 + "\u001e" + (SsHarness.Now() - t0) + "\u001e!" + ex.GetType().Name; Thread.Sleep(30); }
      lock (_frames) _frames.Add(line);
      Thread.Sleep(GapMs);
    }
  }
  public string[] Frames() { lock (_frames) return _frames.ToArray(); }
  public int Count { get { lock (_frames) return _frames.Count; } }
  public void Stop() { _stop = true; if (_thread != null) _thread.Join(10000); }
}
"@
}

# --- the big series: every episode's name (unique in the series) -> its season and number ------------------------------------
$ssKey = @{}; $ssSeasonById = @{}; $ssEpisodeIds = @{}
foreach ($s in $ssBs.seasons) {
    $ssSeasonById[(($s.id -replace '-', '').ToLowerInvariant())] = [int]$s.index
    $ssEpisodeIds[[int]$s.index] = @{}
    foreach ($e in $s.episodes) {
        $ssKey[$e.name] = @([int]$s.index, [int]$e.index)
        $ssEpisodeIds[[int]$s.index][(($e.id -replace '-', '').ToLowerInvariant())] = [int]$e.index
    }
}
$ssSeriesId = ($ssBs.id -replace '-', '').ToLowerInvariant()
function Ss-SeasonId([int]$N) { (($ssBs.seasons | Where-Object { [int]$_.index -eq $N } | Select-Object -First 1).id -replace '-', '').ToLowerInvariant() }
# the pause between two looks of the watching thread (each look is UI Automation work on FluentFin's UI thread: the shorter,
# the finer the timing and the more the looking itself slows the app); SS_POLL_GAP_MS overrides it
$ssGap = 40
if ($env:SS_POLL_GAP_MS -match '^\d+$') { $ssGap = [int]$env:SS_POLL_GAP_MS }
function Ss-NewWatch { $w = New-Object SsWatch($script:hwnd, $ssClasses); $w.GapMs = $ssGap; $w }
# The gate's time limits (ms after the click, the last click of a click-through), from the after-fix runs of 2026-11-20 on a
# loaded machine (2 to 4 other harness runs, CPU 30-75 %) with a generous margin. "kept": a season seen before or asked ahead
# (the seasons either side of the one shown): its rows without a server round trip, so with the latency rules (300 ms per
# season list) a build that asks the server again cannot meet them; "new": a season nothing asked for yet; "fast": the
# click-through's last season. Drive-in builds its rows slowly (its showtimes: 200-500 ms after the data), hence its own.
# SS_LIMIT_SCALE (e.g. 1.5) widens them all on a slower machine.
$ssScale = 1.0
if ($env:SS_LIMIT_SCALE -match '^\d+(\.\d+)?$') { $ssScale = [double]$env:SS_LIMIT_SCALE }
$ssLimits = @{
    'series-kept' = @{ Classic = 700; DriveIn = 900; Cinematic = 350; Editorial = 400 }
    'series-fast' = @{ Classic = 900; DriveIn = 1200; Cinematic = 500; Editorial = 600 }
    'series-new' = @{ Classic = 1200; DriveIn = 1500; Cinematic = 1200; Editorial = 1200 }
    'season-kept' = @{ Classic = 700; DriveIn = 1300; Cinematic = 600; Editorial = 750 }
    'season-fast' = @{ Classic = 700; DriveIn = 1200; Cinematic = 800; Editorial = 700 }
    'season-new' = @{ Classic = 1200; DriveIn = 1500; Cinematic = 1200; Editorial = 1200 }
}
# the pictures of the rows in view (and a screen below), answered: loose, the pictures still come 2 at a time
$ssImageLimit = @{ Classic = 2000; DriveIn = 2500; Cinematic = 2000; Editorial = 2500 }
function Ss-Limit([string]$Part, [string]$Expect) {
    $t = $ssLimits["$Part-$Expect"]
    $v = if ($t -and $t.ContainsKey($ssTheme)) { $t[$ssTheme] } elseif ($t) { ($t.Values | Measure-Object -Maximum).Maximum } else { 2000 }
    [int]($v * $ssScale)
}
# the episode items of the four themes (their UI Automation class names: Classic's rows, Drive-in's showtimes, Cinematic's stills
# (a card's peer), Editorial's stills); a card of another item is no episode of the big series and is left out
$ssClasses = [string[]]@('EpisodeRow', 'Showtime', 'BaseItemDtoCard', 'EditorialStill')

# an item line -> season, episode, rect, offscreen (season 0: not one of the big series' episodes)
function Ss-Item([string]$Line) {
    $p = $Line -split [char]0x1f
    $s = 0; $e = 0
    if ($p[1] -match 'The [A-Z][a-z]+ [A-Z][a-z]+' -and $ssKey.ContainsKey($Matches[0])) { $s = $ssKey[$Matches[0]][0]; $e = $ssKey[$Matches[0]][1] }
    $r = if ($p[2] -ne '-') { @($p[2] -split ',' | ForEach-Object { [double]$_ }) } else { $null }
    [pscustomobject]@{ Class = $p[0]; Name = $p[1]; Rect = $r; Off = ($p[3] -eq '1'); Selected = ($p[4] -eq '1'); Enabled = ($p[5] -ne '0'); Season = $s; Episode = $e }
}
# a frame -> start (epoch s), search ms, its items, the seasons among them
function Ss-Frame([string]$Line) {
    $p = $Line -split [char]0x1e
    $failed = $p[2].StartsWith('!')
    $items = if ($failed -or -not $p[2]) { @() } else { @($p[2] -split [char]0x1d | ForEach-Object { Ss-Item $_ } | Where-Object { $_.Season -gt 0 }) }
    [pscustomobject]@{ T = [double]$p[0] / 1000.0; Ms = [int]$p[1]; End = ([double]$p[0] + [double]$p[1]) / 1000.0; Failed = $failed
        Items = $items; Seasons = @($items | ForEach-Object { $_.Season } | Sort-Object -Unique) }
}
# the target's first episode, on screen (not off screen, a rectangle inside the window)
function Ss-FirstShown($Frame, [int]$Season) {
    @($Frame.Items | Where-Object { $_.Season -eq $Season -and $_.Episode -eq 1 -and -not $_.Off -and $_.Rect }) | Select-Object -First 1
}

# --- the fake server's log, read as it grows (only the new lines each time) ---------------------------------------------------
$script:ssReqPos = 0; $script:ssReqTail = ''; $script:ssReqs = New-Object System.Collections.ArrayList
function Ss-ReadRequests {
    if (-not (Test-Path $requestLog)) { return }
    $fs = [IO.File]::Open($requestLog, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$fs.Seek($script:ssReqPos, 'Begin')
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        $text = $script:ssReqTail + $sr.ReadToEnd()
        $script:ssReqPos = $fs.Position
    } finally { $fs.Dispose() }
    $lines = $text -split "`n"
    $script:ssReqTail = $lines[-1]
    foreach ($l in $lines[0..($lines.Count - 2)]) {
        if (-not $l.Trim()) { continue }
        $r = $l | ConvertFrom-Json
        [void]$script:ssReqs.Add($r)
        if ($r.route -eq 'image' -and $r.path -match '^/Items/([0-9a-fA-F-]{32,36})/Images/') { [void]$script:ssPictured.Add((Ss-Norm $Matches[1])) }
    }
}
# every episode whose picture the app asked for in this run (a row on screen must have asked for its own: one that did not
# shows another episode's picture, or none)
$script:ssPictured = New-Object 'System.Collections.Generic.HashSet[string]'
$ssEpisodeIdOf = @{}
foreach ($k in $ssEpisodeIds.Keys) { foreach ($id in $ssEpisodeIds[$k].Keys) { $ssEpisodeIdOf["$k`:$($ssEpisodeIds[$k][$id])"] = $id } }
function Ss-Norm([string]$Id) { ($Id -replace '-', '').ToLowerInvariant() }
# a request of the app's -> what it is for this switch (the target season $Season)
function Ss-Kind($R, [int]$Season) {
    switch ($R.route) {
        'items' {
            $parent = if ($R.query.PSObject.Properties['parentid']) { Ss-Norm $R.query.parentid[0] } else { '' }
            if ($parent -eq $ssSeriesId) { return 'list-seasons' }
            if ($ssSeasonById.ContainsKey($parent)) { return $(if ($ssSeasonById[$parent] -eq $Season) { 'list-season' } else { 'list-other-season' }) }
            return 'list-other'
        }
        'image' {
            $id = if ($R.path -match '^/Items/([0-9a-fA-F-]{32,36})/Images/') { Ss-Norm $Matches[1] } else { '' }
            if ($ssEpisodeIds[$Season] -and $ssEpisodeIds[$Season].ContainsKey($id)) { return 'image-episode' }
            foreach ($k in $ssEpisodeIds.Keys) { if ($ssEpisodeIds[$k].ContainsKey($id)) { return 'image-other-episode' } }
            return 'image-other'
        }
        default { return [string]$R.route }
    }
}
function Ss-Start($R) { [double]$R.t - [double]$R.ms / 1000.0 }   # the server's "t" is when it answered

# --- pills -------------------------------------------------------------------------------------------------------------------
function Ss-Root { [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd) }
function Ss-Pills {
    @([SsHarness]::Pills((Ss-Root)) | ForEach-Object {
        $i = Ss-Item $_
        if ($i.Name -match '^Season (\d+)') { $i | Add-Member -NotePropertyName Number -NotePropertyValue ([int]$Matches[1]) -PassThru }
    })
}
function Ss-Pill([int]$N) { @(Ss-Pills | Where-Object { $_.Number -eq $N -and $_.Rect -and -not $_.Off }) | Select-Object -First 1 }
function Ss-IsCurrent($Pill) { $Pill -and ($Pill.Name -match ', current season' -or $Pill.Selected) }
# a click on the pill through the pointer hook (its middle); a pill off screen through UI Automation's Invoke. $Known: the pills
# found before (a fast click-through posts its clicks without a search between them)
function Ss-ClickPill([int]$N, [object[]]$Known) {
    $pill = if ($Known) { @($Known | Where-Object { $_.Number -eq $N -and $_.Rect -and -not $_.Off }) | Select-Object -First 1 } else { Ss-Pill $N }
    if ($pill) {
        $x = [int]($pill.Rect[0] + $pill.Rect[2] / 2); $y = [int]($pill.Rect[1] + $pill.Rect[3] / 2)
        $at = [SsHarness]::Now() / 1000.0
        [void][SsHarness]::Click($script:hwnd, $x, $y)
        return [pscustomobject]@{ At = $at; How = "click $x,$y"; Season = $N }
    }
    $any = Find-Ui -Name "Season $N" -Type 'Button' -OffscreenToo
    if (-not $any) { $any = Find-Ui -Name "Season $N, " -Type 'Button' -Contains -OffscreenToo }
    if (-not $any) { return $null }
    $at = [SsHarness]::Now() / 1000.0
    [void](Invoke-Element $any)
    [pscustomobject]@{ At = $at; How = 'invoke (the pill is off screen)'; Season = $N }
}

# --- one switch ----------------------------------------------------------------------------------------------------------------
$ssRows = New-Object System.Collections.ArrayList
# FluentFin's own log since $Since: the pointer hook's lines ('[harness] "Click" at x,y: <what it hit>', when the UI thread ran
# the click) and errors, each with its time (epoch s)
function Ss-AppLog([double]$Since) {
    @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Encoding utf8 -Tail 400 } | ForEach-Object {
        if ($_ -match '^(\S+ \S+ [+-]\d\d:\d\d) \[(\w+)\] (.*)$') {
            # (kept before the next -match, which replaces $Matches)
            $stamp = $Matches[1]; $level = $Matches[2]; $text = $Matches[3]
            $when = [DateTimeOffset]::MinValue
            if (($text -match '^\[harness\] "?Click' -or $level -in @('ERR', 'FTL', 'WRN')) -and
                [DateTimeOffset]::TryParse($stamp, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$when)) {
                $at = $when.ToUnixTimeMilliseconds() / 1000.0
                if ($at -ge $Since - 0.05) { [pscustomobject]@{ T = $at; Text = "$level $text" } }
            }
        } })
}
# $Clicks: season numbers clicked in order ($GapMs apart; on a season page each next click waits for its pill on the new page)
# $Expect: kept (a season seen or asked ahead: no request for its list, rows within the "kept" limit), new, or fast (a
# click-through: no pill disabled meanwhile, so no click is dropped)
function Ss-Switch([string]$Part, [string]$Label, [int]$From, [int[]]$Clicks, [int]$GapMs = 0, [switch]$PageToPage,
    [ValidateSet('kept', 'new', 'fast')][string]$Expect = 'new') {
    $target = $Clicks[-1]
    Ss-ReadRequests
    $watch = Ss-NewWatch
    $watch.Start()
    Start-Sleep -Milliseconds 120
    $done = @()
    $known = if (-not $PageToPage -and $Clicks.Count -gt 1) { @(Ss-Pills) }
    foreach ($n in $Clicks) {
        if ($done.Count -gt 0) {
            if ($PageToPage) {
                # the next pill on the page the last click opened (as a user must wait for it), where it stays: the page grows
                # in from 94 % for 300 ms (PageTransition), and a click at the place it is drawn meanwhile went to the page
                # under it (the hook hits the layout; Cinematic, 2026-11-20): at most 3 s
                $deadline = [DateTime]::UtcNow.AddSeconds(3)
                $was = $null
                while ([DateTime]::UtcNow -lt $deadline) {
                    $p = Ss-Pill $n
                    $cur = Ss-Pill $done[-1].Season
                    $at = if ($p) { $p.Rect -join ',' }
                    if ($p -and (Ss-IsCurrent $cur) -and $at -eq $was) { break }
                    $was = $at
                    Start-Sleep -Milliseconds 40
                }
            }
            else {
                # the hook waits 40 ms between a click's press and its click, so two clicks it takes in the same moment (the
                # UI thread was busy) can swap, which a real mouse never does: the next is posted once the hook took the last
                # (its log line), then after what is left of the gap
                $deadline = [DateTime]::UtcNow.AddSeconds(3)
                while ([DateTime]::UtcNow -lt $deadline -and @(Ss-AppLog ($done[0].At - 0.05) | Where-Object { $_.Text -match '\[harness\] "?Click' }).Count -lt $done.Count) { Start-Sleep -Milliseconds 10 }
                $left = $GapMs - [int](([SsHarness]::Now() / 1000.0 - $done[-1].At) * 1000)
                if ($left -gt 0) { [System.Threading.Thread]::Sleep($left) }
            }
        }
        $c = Ss-ClickPill $n $known
        if (-not $c) { Check "season switch ($ssTag) ${Part} ${Label}: the Season $n pill is there" $false; $watch.Stop(); return }
        $done += $c
    }
    $last = $done[-1].At; $first = $done[0].At
    # the pills just after the clicks (a fast click-through: are they still clickable while the first season loads?)
    # (looked for again for up to 1.5 s: Drive-in remakes its pills right then, and one look found none)
    $pillsAfter = $null
    if ($Clicks.Count -gt 1 -and -not $PageToPage) {
        $pillsAfter = @(); $until = [DateTime]::UtcNow.AddSeconds(1.5)
        while ($pillsAfter.Count -eq 0 -and [DateTime]::UtcNow -lt $until) { $pillsAfter = @(Ss-Pills); if ($pillsAfter.Count -eq 0) { Start-Sleep -Milliseconds 30 } }
    }
    # until the target's first episode is on screen (20 s at most)
    $seenAt = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and -not $seenAt) {
        $frames = $watch.Frames()
        if ($frames.Count) {
            $fr = Ss-Frame $frames[-1]
            if ($fr.T -ge $last -and (Ss-FirstShown $fr $target)) { $seenAt = $fr.End }
        }
        if (-not $seenAt) { Start-Sleep -Milliseconds 25 }
    }
    # then until its pictures are in: no new picture of its episodes asked for in 700 ms, at least 1.2 s after the rows
    # (2.5 s when none was asked for: kept), 10 s at most
    $settleEnd = [DateTime]::UtcNow.AddSeconds(10)
    $quietSince = Get-Epoch; $lastCount = -1
    while ([DateTime]::UtcNow -lt $settleEnd) {
        Start-Sleep -Milliseconds 150
        Ss-ReadRequests
        $mine = @($script:ssReqs | Where-Object { (Ss-Start $_) -ge $first - 0.02 -and (Get-Origin $_) -eq 'ui' -and (Ss-Kind $_ $target) -eq 'image-episode' })
        if ($mine.Count -ne $lastCount) { $lastCount = $mine.Count; $quietSince = Get-Epoch }
        $now = Get-Epoch
        $since = if ($seenAt) { $now - $seenAt } else { 99 }
        if ($since -ge 1.2 -and $now - $quietSince -ge 0.7 -and ($mine.Count -gt 0 -or $since -ge 2.5)) { break }
    }
    Start-Sleep -Milliseconds 250
    $watch.Stop()
    $endAt = Get-Epoch
    Ss-ReadRequests
    $frames = @($watch.Frames() | ForEach-Object { Ss-Frame $_ })
    $good = @($frames | Where-Object { -not $_.Failed })

    # what showed after the first click: the seasons in order (a change of the set of seasons in the tree), with when
    $seen = New-Object System.Collections.ArrayList
    $prev = ''
    foreach ($fr in @($good | Where-Object { $_.T -ge $first })) {
        $key = ($fr.Seasons -join '+')
        if ($key -ne $prev) { [void]$seen.Add([pscustomobject]@{ ms = Ms $last $fr.End; seasons = $key }); $prev = $key }
    }
    $rowsFrame = @($good | Where-Object { $_.T -ge $last -and (Ss-FirstShown $_ $target) }) | Select-Object -First 1
    $cleanFrame = @($good | Where-Object { $_.T -ge $last -and (Ss-FirstShown $_ $target) -and ($_.Seasons -join ',') -eq "$target" }) | Select-Object -First 1
    $final = @($good) | Select-Object -Last 1
    # the season the page showed before the first click (a switch before this one may not have ended where it should)
    $prior = @($good | Where-Object { $_.T -lt $first -and $_.Seasons.Count -eq 1 }) | Select-Object -Last 1
    $fromSeen = if ($prior) { [int]$prior.Seasons[0] } else { $From }
    # a season coming back after a newer one showed: the clicks' order, from the season the page was on
    $order = @($fromSeen) + @($Clicks)
    $back = @(); $high = -1
    foreach ($fr in @($good | Where-Object { $_.T -ge $first })) {
        foreach ($s in $fr.Seasons) {
            $rank = [Array]::LastIndexOf($order, [int]$s)
            if ($rank -lt 0) { $back += "Season $s (never clicked) at $(Ms $last $fr.End) ms" }
            elseif ($rank -lt $high -and @($fr.Seasons).Count -eq 1) { $back += "Season $s at $(Ms $last $fr.End) ms after a newer season showed" }
        }
        if (@($fr.Seasons).Count -eq 1) { $high = [Math]::Max($high, [Array]::LastIndexOf($order, [int]$fr.Seasons[0])) }
    }
    # after the target showed, nothing else
    $afterTarget = @()
    if ($cleanFrame) { $afterTarget = @($good | Where-Object { $_.T -gt $cleanFrame.T -and ($_.Seasons -join ',') -ne "$target" -and $_.Seasons.Count -gt 0 } | ForEach-Object { "$($_.Seasons -join '+') at $(Ms $last $_.End) ms" }) }
    # older seasons shown after the last click was posted (a fast click-through: the seasons clicked through, reported)
    $between = @($good | Where-Object { $_.T -ge $last + 0.15 -and @($_.Seasons | Where-Object { $_ -ne $target -and $_ -ne $fromSeen }).Count -gt 0 } |
        ForEach-Object { "$($_.Seasons -join '+')@$(Ms $last $_.End)" } | Select-Object -Unique)

    # the requests from the first click to the end
    $reqs = @($script:ssReqs | Where-Object { (Ss-Start $_) -ge $first - 0.02 -and (Ss-Start $_) -le $endAt -and (Get-Origin $_) -eq 'ui' })
    $kinds = [ordered]@{}
    foreach ($r in $reqs) { $k = Ss-Kind $r $target; $kinds[$k] = 1 + $(if ($kinds.Contains($k)) { $kinds[$k] } else { 0 }) }
    $inFlight = 0
    foreach ($r in $reqs) { $s0 = Ss-Start $r; $n = @($reqs | Where-Object { (Ss-Start $_) -le $s0 -and $_.t -gt $s0 }).Count; if ($n -gt $inFlight) { $inFlight = $n } }
    $list = @($reqs | Where-Object { (Ss-Kind $_ $target) -eq 'list-season' }) | Select-Object -Last 1
    $images = @($reqs | Where-Object { (Ss-Kind $_ $target) -eq 'image-episode' })
    $imgFlight = 0
    foreach ($r in $images) { $s0 = Ss-Start $r; $n = @($images | Where-Object { (Ss-Start $_) -le $s0 -and $_.t -gt $s0 }).Count; if ($n -gt $imgFlight) { $imgFlight = $n } }
    $asked = @($reqs | Where-Object { $_.route -eq 'items' -and $_.query.PSObject.Properties['parentid'] } | Sort-Object { Ss-Start $_ } | ForEach-Object {
        $pid_ = Ss-Norm $_.query.parentid[0]; if ($ssSeasonById.ContainsKey($pid_)) { $ssSeasonById[$pid_] } })
    $widths = @($images | ForEach-Object { foreach ($q in 'maxwidth', 'fillwidth', 'fillheight', 'width') { if ($_.query.PSObject.Properties[$q]) { "$q=$($_.query.$q[0])" } } } | Sort-Object -Unique)
    $rowsMs = if ($rowsFrame) { Ms $last $rowsFrame.End }
    $imagesDone = if ($images) { ($images | ForEach-Object { [double]$_.t } | Measure-Object -Maximum).Maximum } else { $null }
    # the timeline: each request's start and end (ms after the last click) in the order asked
    $timeline = @($reqs | Sort-Object { Ss-Start $_ } | Where-Object { (Ss-Kind $_ $target) -notin @('image-episode', 'image-other-episode') } |
        ForEach-Object { "{0} {1}..{2}" -f (Ss-Kind $_ $target), (Ms $last (Ss-Start $_)), (Ms $last $_.t) })

    # what the user sees once settled: the target's pill current, its first episode's picture
    $pill = Ss-Pill $target
    $firstEp = if ($final) { Ss-FirstShown $final $target }
    # (its left part, at most three times as wide as tall: Classic's and Drive-in's thumbnail; the whole still in the others)
    $art = if ($firstEp) { [SsHarness]::Coloured($script:hwnd, [int]$firstEp.Rect[0], [int]$firstEp.Rect[1], [int][Math]::Min($firstEp.Rect[2], 3 * $firstEp.Rect[3]), [int]$firstEp.Rect[3]) } else { $null }
    $appLog = @(Ss-AppLog ($first - 0.2))
    $hookAt = @($appLog | Where-Object { $_.Text -match '\[harness\] "?Click' } | ForEach-Object { Ms $first $_.T })
    $script:ssShotN++
    Shot ("{0}-{1}-{2:00}-s{3}" -f $ssTag, $Part, $script:ssShotN, $target)

    $row = [ordered]@{
        theme = $ssTheme; part = $Part; label = $Label; from = $fromSeen; clicks = ($Clicks -join '>'); how = $done[0].How
        lists_asked = ($asked -join ',')   # the seasons whose episode list the app asked for, in order: a click that asked none was dropped
        pills_disabled_after_clicks = $(if ($pillsAfter) { @($pillsAfter | Where-Object { -not $_.Enabled }).Count })
        click_gaps_ms = $(if ($done.Count -gt 1) { (@(1..($done.Count - 1) | ForEach-Object { Ms $done[$_ - 1].At $done[$_].At }) -join ',') } else { '' })
        rows_ms = $rowsMs; clean_ms = $(if ($cleanFrame) { Ms $last $cleanFrame.End })
        images_ms = $(if ($imagesDone) { [Math]::Max([int](Ms $last $imagesDone), [int]$rowsMs) } elseif ($rowsMs) { $rowsMs })
        cached = ((-not $images) -and [bool]$rowsFrame); images_n = $images.Count; image_sizes = ($widths -join ' '); images_in_flight = $imgFlight
        # Nixie 1.5: when the first of them was asked (ms after the last click): with the rows, or after them
        images_first_asked_ms = $(if ($images) { Ms $last (($images | ForEach-Object { Ss-Start $_ } | Measure-Object -Minimum).Minimum) })
        image_server_ms = $(if ($images) { [Math]::Round((($images | ForEach-Object { [double]$_.ms - [double]$_.delay_ms } | Measure-Object -Maximum).Maximum), 1) })
        list_asked_ms = $(if ($list) { Ms $last (Ss-Start $list) }); list_answered_ms = $(if ($list) { Ms $last $list.t })
        ui_after_answer_ms = $(if ($list -and $rowsMs) { $rowsMs - (Ms $last $list.t) })
        requests = $reqs.Count; by_kind = (($kinds.Keys | ForEach-Object { "$_=$($kinds[$_])" }) -join ' '); in_flight = $inFlight
        timeline = ($timeline -join '; ')
        poll_ms = $(if ($good) { [int](($good | Measure-Object -Property Ms -Average).Average) }); frames = $frames.Count; failed_frames = @($frames | Where-Object { $_.Failed }).Count
        seasons_seen = (($seen | ForEach-Object { "$($_.seasons)@$($_.ms)" }) -join ' ')
        shown_after_last_click = ($between -join ' ')
        pill_current = [bool](Ss-IsCurrent $pill); art_pct = $art
        final = $(if ($final) { $final.Seasons -join '+' })
        hook_ms = ($hookAt -join ',')   # each click's time on FluentFin's UI thread (its log line), ms after the first was posted
        app_log = ((@($appLog | Where-Object { $_.Text -notmatch '\[harness\] "?Click' } | ForEach-Object { $_.Text }) | Select-Object -First 3) -join ' | ')
    }
    $listAfter = @($reqs | Where-Object { (Ss-Kind $_ $target) -eq 'list-season' })
    $row.expect = $Expect
    $row.list_requests = $listAfter.Count
    $row.limit_rows_ms = Ss-Limit $Part $Expect
    $row.limit_images_ms = [int]$(if ($ssImageLimit.ContainsKey($ssTheme)) { $ssImageLimit[$ssTheme] * $ssScale } else { 3500 * $ssScale })
    foreach ($k in @($row.Keys)) { if ($null -eq $row[$k]) { $row[$k] = $null } }   # (an empty result would be written as {})
    $row.poll_gap_ms = $ssGap
    [void]$ssRows.Add([pscustomobject]$row)
    $ok = $rowsFrame -and $final -and ($final.Seasons -join ',') -eq "$target" -and -not $back -and -not $afterTarget
    $why = @()
    if (-not $rowsFrame) { $why += "Season $target's first episode never showed (lists asked for seasons: $(if ($asked) { $asked -join ', ' } else { 'none' }))" }
    if ($final -and ($final.Seasons -join ',') -ne "$target") { $why += "ended on season(s) $($final.Seasons -join '+')" }
    $why += $back; $why += @($afterTarget | ForEach-Object { "after Season $target showed: $_" })
    Check "season switch ($ssTag) ${Part} ${Label}: ends on Season $target's episodes, nothing older after them" ([bool]$ok) $(if ($why) { $why -join '; ' } else {
        "rows $($row.rows_ms) ms, images $($row.images_ms) ms$(if ($row.cached) { ' (kept)' }), $($row.requests) requests ($($row.by_kind))" })
    # what the picker says (its mark, and Narrator's "current season"): the season the list shows
    $current = @(Ss-Pills | Where-Object { Ss-IsCurrent $_ } | ForEach-Object { $_.Name })
    Check "season switch ($ssTag) ${Part} ${Label}: the picker marks Season $target as the current season, and no other" ([bool]$row.pill_current -and $current.Count -eq 1) $(
        "Season $target's pill: $(if ($pill) { "'$($pill.Name)'" } else { 'not found' }); marked current: $(if ($current) { $current -join ', ' } else { 'none' })")
    # every row of the season on screen asked for its own picture in this run (now or before: a kept one); a row that never
    # did shows another episode's picture (a reused row keeping the last one) or none
    $onScreen = @(if ($final) { $final.Items | Where-Object { $_.Season -eq $target -and -not $_.Off -and $_.Rect } })
    $noPicture = @($onScreen | Where-Object { -not $script:ssPictured.Contains([string]$ssEpisodeIdOf["$($_.Season):$($_.Episode)"]) } | ForEach-Object { "S$($_.Season) E$($_.Episode)" })
    $row.rows_on_screen = $onScreen.Count; $row.rows_without_picture = ($noPicture -join ' ')
    Check "season switch ($ssTag) ${Part} ${Label}: every Season $target row on screen asked for its own picture" ($onScreen.Count -gt 0 -and $noPicture.Count -eq 0) $(
        if ($noPicture) { "$($noPicture.Count) of $($onScreen.Count) never asked (they show another episode's picture or none): $($noPicture -join ', ')" } else { "$($onScreen.Count) rows" })
    # a click-through: every pill stays clickable while a season loads (they were disabled until the first answer, and the
    # clicks meanwhile were dropped)
    if ($null -ne $pillsAfter) {
        $off = @($pillsAfter | Where-Object { -not $_.Enabled } | ForEach-Object { $_.Name })
        Check "season switch ($ssTag) ${Part} ${Label}: the pills stay enabled while a season loads (no click dropped)" ($pillsAfter.Count -gt 0 -and $off.Count -eq 0) $(
            if ($pillsAfter.Count -eq 0) { 'no pill found within 1.5 s of the clicks' } elseif ($off) { "disabled just after the clicks: $($off -join ', ')" } else { "$($pillsAfter.Count) pills enabled" })
    }
    # a season seen or asked ahead: nothing asked of the server for its list
    if ($Expect -eq 'kept') {
        Check "season switch ($ssTag) ${Part} ${Label}: Season $target's list is kept (seen or asked ahead): no request for it" ($listAfter.Count -eq 0) $(if ($listAfter) {
            "asked $($listAfter.Count) time(s), answered $((@($listAfter | ForEach-Object { Ms $last $_.t })) -join ', ') ms after the click" } else { 'none' })
    }
    # Nixie 1.5: the pictures come more than 2 at a time (WinINet gives a program 2 connections to a server unless told; a
    # season's stills came 2 by 2, 0.4 to 0.9 s after its rows). Not after a click-through: the last season's stills may be
    # on their way from the page before, which asks the seasons either side's ahead 2 at a time (StillsAhead) by design
    if ($images.Count -ge 6 -and $Expect -ne 'fast') {
        Check "season switch ($ssTag) ${Part} ${Label}: Season $target's pictures come more than 2 at a time" ($imgFlight -gt 2) "$imgFlight at once, $($images.Count) pictures"
    }
    $timeOk = $rowsMs -and $rowsMs -le $row.limit_rows_ms -and ($null -eq $row.images_ms -or $row.images_ms -le $row.limit_images_ms)
    Check "season switch ($ssTag) ${Part} ${Label}: in time (${Expect}: rows within $($row.limit_rows_ms) ms, pictures within $($row.limit_images_ms) ms)" ([bool]$timeOk) (
        "rows $(if ($rowsMs) { $rowsMs } else { 'never' }) ms, pictures $($row.images_ms) ms$(if ($row.cached) { ' (kept)' })")
    Say ("season switch ({0}) {1} {2}: rows {3} ms (clean {4}), images {5} ms ({6} asked{7}), list asked {8} answered {9}, UI after the answer {10} ms; {11} requests, {12} at once; seen {13}; poll {14} ms" -f
        $ssTag, $Part, $Label, $row.rows_ms, $row.clean_ms, $row.images_ms, $row.images_n, $(if ($row.image_sizes) { ", $($row.image_sizes)" }), $row.list_asked_ms, $row.list_answered_ms,
        $row.ui_after_answer_ms, $row.requests, $row.in_flight, $row.seasons_seen, $row.poll_ms)
    Start-Sleep -Milliseconds 400
}

# --- the series page -----------------------------------------------------------------------------------------------------------
$script:ssShotN = 0
Ss-ReadRequests
Open-Library $ssBs.library
Start-Sleep -Milliseconds 1200
$ssCard = Wait-Until { Find-MediaCard $ssBs.name } -Seconds 30 -PollMs 200
if (-not $ssCard) { Check "season switch ($ssTag): the big series' card in $($ssBs.library)" $false; return }
$ssWatch0 = Ss-NewWatch
$ssWatch0.Start()
$ssOpenAt = Get-Epoch
[void](Invoke-Element $ssCard)
if ($ssTheme -eq 'Editorial') {
    # Editorial's poster opens its details in place: their "Full details" opens the page (timed from there)
    $ssFull = Wait-Until { Find-Ui -Name 'Full details' -Type 'Button' -Contains } -Seconds 5 -PollMs 100
    if ($ssFull) { $ssOpenAt = Get-Epoch; [void](Invoke-Element $ssFull) }
}
$ssOpened = Wait-Until { $f = $ssWatch0.Frames(); if ($f.Count) { $fr = Ss-Frame $f[-1]; if ($fr.T -ge $ssOpenAt -and (Ss-FirstShown $fr 1)) { $fr.End } } } -Seconds 20 -PollMs 25
$ssWatch0.Stop()
$timings.big_series_page_first_episode_ms = Ms $ssOpenAt $ssOpened
Check "season switch ($ssTag): the big series' page opens on Season 1" ([bool]$ssOpened) "$($ssBs.name): first episode on screen $($timings.big_series_page_first_episode_ms) ms after the card"
if (-not $ssOpened) { Dump-Tree "$ssTag-season-switch-no-series"; return }
Start-Sleep -Seconds 3   # its pictures, More like this
$ssP1 = Ss-Pill 2
Check "season switch ($ssTag): the series page has its season pills" ([bool]$ssP1) $(if ($ssP1) { "'$($ssP1.Name)' at $($ssP1.Rect -join ',')" } else { (@(Ss-Pills | ForEach-Object { $_.Name }) -join ', ') })
if (-not $ssP1) { Dump-Tree "$ssTag-season-switch-no-pills"; return }
# Nixie 1.5 (review M1): the series' seasons are asked with their logos: a season page opened from a pill first shows the
# pill's season, and without Logo among the image types Jellyfin (and the fake server, 1.5) leaves out the show's logo, so
# the header showed the name as text, then the logo a moment later, moving what is under it
Ss-ReadRequests
$ssSeasonsAsked = @($script:ssReqs | Where-Object { $_.route -eq 'items' -and $_.query.PSObject.Properties['parentid'] -and (Ss-Norm $_.query.parentid[0]) -eq $ssSeriesId })
$ssSeasonsTypes = @($ssSeasonsAsked | ForEach-Object { if ($_.query.PSObject.Properties['enableimagetypes']) { @($_.query.enableimagetypes) -join ',' } else { '(all)' } } | Sort-Object -Unique)
Check "season switch ($ssTag): the series' seasons are asked with their logos (a season page's header from a pill)" (
    $ssSeasonsAsked.Count -gt 0 -and -not @($ssSeasonsTypes | Where-Object { $_ -ne '(all)' -and $_ -notmatch '(^|,)Logo(,|$)' }).Count) $(
    if ($ssSeasonsAsked) { "image types: $($ssSeasonsTypes -join ' | ')" } else { 'the seasons were never asked' })

# (the page opened on Season 1 asks Season 2 ahead; Season 2 shown asks 3 ahead, and so on: "ahead" switches are kept too)
Ss-Switch 'series' '1>2 (ahead)' 1 @(2) -Expect kept
Ss-Switch 'series' '2>3 (ahead)' 2 @(3) -Expect kept
Ss-Switch 'series' '3>2 (seen)' 3 @(2) -Expect kept
Ss-Switch 'series' '2>1 (seen)' 2 @(1) -Expect kept
Ss-Switch 'series' '1>2>3>4 fast' 1 @(2, 3, 4) -GapMs 75 -Expect fast
Ss-Switch 'series' '4>1 (seen)' 4 @(1) -Expect kept
# a season far from the one shown: Nixie 1.5 asks every season of a show of at most 12 (SeriesEpisodes.MostSeasonsAhead) ahead,
# in turn, so it is kept by now; a longer show (FAKESERVER_BIG_SERIES=13x20) asks only the seasons either side: the server's
# list, then the pictures
$ssAllAhead = @($ssBs.seasons).Count -le 12
if ($ssAllAhead) { Ss-Switch 'series' '1>9 (far, asked ahead)' 1 @(9) -Expect kept } else { Ss-Switch 'series' '1>9 (new)' 1 @(9) -Expect new }
Ss-Switch 'series' '9>1 (seen)' 9 @(1) -Expect kept

# --- the keyboard on the series page's pills: Enter, then Space, on a pill that has the keyboard. The season shows, and the
# keyboard ends on the new current pill (Classic makes its pills anew for each season: the pill that had the keyboard is
# gone, and the keyboard fell to the title bar; Drive-in's tubes, made anew too, dropped it to "Season N details"). A check in
# both (SeasonPillFocus gives the keyboard back to the current pill). Cinematic's
# and Editorial's pickers are tab lists of their own (not buttons): not looked at here.
function Ss-KeySwitch([int]$N, [int]$Vk, [string]$KeyName) {
    $el = Find-Ui -Name "Season $N" -Type 'Button'
    if (-not $el) { Say "season switch ($ssTag) series keyboard ${KeyName}: no 'Season $N' button (the picker is not buttons here)"; return }
    [void][OffscreenUi]::SetFocus($el)
    Start-Sleep -Milliseconds 400
    $had = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); $had = if ($had) { $had | ConvertFrom-Json }
    $at = Get-Epoch
    Key $Vk
    $shown = Wait-Until { $p = Ss-Pill $N; if ($p -and (Ss-IsCurrent $p)) { Get-Epoch } } -Seconds 10 -PollMs 50
    # the pills are remade and the keyboard given back at a low priority: a moment for both
    Start-Sleep -Milliseconds 800
    $fr = Ss-Frame ("{0}{1}0{1}{2}" -f [SsHarness]::Now(), [char]0x1e, ([SsHarness]::Items((Ss-Root), $ssClasses) -join [char]0x1d))
    $rows = [bool](Ss-FirstShown $fr $N) -and ($fr.Seasons -join ',') -eq "$N"
    $now = [OffscreenUi]::Focused($script:hwnd, $script:ui.Id); $now = if ($now) { $now | ConvertFrom-Json }
    $onPill = $now -and $now.name -eq "Season $N, current season"
    $detail = "had: $(if ($had) { "$($had.type) '$($had.name)'" } else { 'nothing' }); after $(Ms $at $shown) ms: Season $N's rows $(if ($rows) { 'shown' } else { 'NOT shown' }), keyboard on $(if ($now) { "$($now.type) '$($now.name)'$(if ($now.class) { " [$($now.class)]" })" } else { 'nothing' })"
    if ($ssTheme -in @('Classic', 'DriveIn')) {
        Check "season switch ($ssTag) series keyboard ${KeyName} on the Season $N pill: Season $N shows and the keyboard ends on its pill" ([bool]($shown -and $rows -and $onPill)) $detail
    }
    else { Say "season switch ($ssTag) series keyboard ${KeyName} on the Season $N pill (reported, not a check here): $detail" }
}
if ($ssTheme -in @('Classic', 'DriveIn')) {
    Ss-KeySwitch 2 0x0D 'Enter'
    Ss-KeySwitch 1 0x20 'Space'
}

# --- series page -> season page, then a season page's pills (page to page) ---------------------------------------------------
# the link to the season the series page shows ("Season 1 details", unless a switch above did not end where it should)
$ssLinks = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Hyperlink)
$ssLink = Wait-Until { @([OffscreenUi]::Where($script:hwnd, $script:ui.Id, $ssLinks)) | Where-Object { $_.Cached.Name -match '^Season \d+ details$' } | Select-Object -First 1 } -Seconds 10 -PollMs 150
$ssLinkSeason = if ($ssLink -and $ssLink.Current.Name -match '^Season (\d+) details$') { [int]$Matches[1] } else { 0 }
Check "season switch ($ssTag): the series page's link to its season's own page" ([bool]$ssLink) $(if ($ssLink) { "'$($ssLink.Current.Name)'" })
if ($ssLink) {
    $ssWatch1 = Ss-NewWatch
    $ssWatch1.Start(); Start-Sleep -Milliseconds 100
    $ssLinkAt = Get-Epoch
    [void](Invoke-Element $ssLink)
    # the season page: its own Watched button ("Mark Season N of <series> as ...") and the season's first episode
    $ssSeasonPage = Wait-Until { if ([string][SsHarness]::NameOfId((Ss-Root), 'WatchedButton') -like "Mark Season $ssLinkSeason of *") { $f = $ssWatch1.Frames(); if ($f.Count) { $fr = Ss-Frame $f[-1]; if ($fr.T -ge $ssLinkAt -and (Ss-FirstShown $fr $ssLinkSeason)) { $fr.End } } } } -Seconds 20 -PollMs 25
    $ssWatch1.Stop()
    $timings.big_season_page_ms = Ms $ssLinkAt $ssSeasonPage
    Check "season switch ($ssTag): '$($ssLink.Current.Name)' opens Season $ssLinkSeason's page" ([bool]$ssSeasonPage) "$($timings.big_season_page_ms) ms to its first episode"
    if (-not $ssSeasonPage) { Dump-Tree "$ssTag-season-switch-no-season-page" }
    else {
        Start-Sleep -Seconds 2
        $ssSp = Wait-Until { Ss-Pill 5 } -Seconds 8 -PollMs 150
        Check "season switch ($ssTag): the season page has its season pills" ([bool]$ssSp) $(if (-not $ssSp) { (@(Ss-Pills | ForEach-Object { $_.Name }) -join ', ') })
        if ($ssSp) {
            # seasons the series page never showed (their pictures not kept yet), then back to ones seen on this page
            # (the series page showed 1-4 and 9: 6 is far, asked ahead for a show of at most 12 seasons, else new; its page
            # asks 7 ahead)
            if ($ssAllAhead) { Ss-Switch 'season' "$ssLinkSeason>6 (far, asked ahead)" $ssLinkSeason @(6) -PageToPage -Expect kept }
            else { Ss-Switch 'season' "$ssLinkSeason>6 (new)" $ssLinkSeason @(6) -PageToPage -Expect new }
            Ss-Switch 'season' '6>7 (ahead)' 6 @(7) -PageToPage -Expect kept
            Ss-Switch 'season' '7>6 (seen)' 7 @(6) -PageToPage -Expect kept
            Ss-Switch 'season' "6>$ssLinkSeason (seen)" 6 @($ssLinkSeason) -PageToPage -Expect kept
            Ss-Switch 'season' "$ssLinkSeason>7>8>9 fast" $ssLinkSeason @(7, 8, 9) -PageToPage -Expect fast
            Ss-Switch 'season' '9>6 (seen)' 9 @(6) -PageToPage -Expect kept
        }
    }
}

# --- the table ---------------------------------------------------------------------------------------------------------------
$timings.season_switch = @($ssRows)
[IO.File]::WriteAllText((Join-Path $Out 'season-switch.json'), (ConvertTo-Json -InputObject @($ssRows) -Depth 4), [Text.UTF8Encoding]::new($false))
Say ("season switch ({0}), latency {1} ms{2}:" -f $ssTag, $Latency, $(if ($LatencyRule) { ' + ' + ($LatencyRule -join ' ') }))
Say ("  {0,-7} {1,-24} {2,7} {3,7} {4,7} {5,-6} {6,5} {7,5} {8,4} {9,6} {10,6} {11,5} {12}" -f 'part', 'switch', 'rows', 'clean', 'images', 'kept', 'imgs', 'img1<', 'x', 'list<', 'list>', 'reqs', 'seasons seen')
foreach ($r in $ssRows) {
    Say ("  {0,-7} {1,-24} {2,7} {3,7} {4,7} {5,-6} {6,5} {7,5} {8,4} {9,6} {10,6} {11,5} {12}" -f $r.part, $r.label, $r.rows_ms, $r.clean_ms, $r.images_ms, $r.cached, $r.images_n, $r.images_first_asked_ms, $r.images_in_flight, $r.list_asked_ms, $r.list_answered_ms, $r.requests, $r.seasons_seen)
}
