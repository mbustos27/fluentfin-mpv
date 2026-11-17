# Seaglass 1.1.3, the wheel's volume indicator measured (the "jumpy" report). Run with FluentFin's controls (libmpv in the build):
#   run-offscreen.ps1 ... -Script season,play -Then offscreen\then-volume.ps1
# On the player page (paused by the driver, the bars up), WM_MOUSEWHEEL bursts are posted to FluentFin's input window over the
# middle of the video from a thread of their own, on a schedule, while this thread grabs the window back to back (PrintWindow,
# about 30 ms a grab) and measures the indicator's fill: the run of accent pixels along the bar's middle row. Two more threads
# record the indicator's number (UI Automation, VolumeIndicatorText) and mpv's own `volume` (observe_property over the
# in-process core's IPC pipe, named in FluentFin's log): what is heard. Per scenario <Out>\volume\<name>.csv (ms since the
# first notch, fill left x, fill px, fill %, the number shown, mpv's volume) and <name>-strip.png (the indicator in every grab);
# summary.txt: notches posted, mpv's volume changes, the fill's steps against the burst's direction (more than 1.5 px) and the
# largest, the fill's end against the volume. VOLUME_SCENARIOS=a,b picks scenarios.

if (-not ('VolumeProbe' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing, UIAutomationClient, UIAutomationTypes, WindowsBase, System.Core @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

public static class VolumeProbe {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
  [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);

  static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
  public static double Now() { return Clock.Elapsed.TotalMilliseconds; }
  static IntPtr Lp(int x, int y) { return (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF)); }

  // the accent (#5CCFC1, the player is dark) against the track (white at 20 % over the dark glass) and the test picture's
  // cyan (no red in it): within 40 of the accent in each channel
  public static bool Accent(int p) {
    int r = (p >> 16) & 255, g = (p >> 8) & 255, b = p & 255;
    return Math.Abs(r - 92) <= 40 && Math.Abs(g - 207) <= 40 && Math.Abs(b - 193) <= 40;
  }

  public static Bitmap Grab(IntPtr main) {
    RECT r; GetWindowRect(main, out r);
    var bmp = new Bitmap(Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T), PixelFormat.Format32bppRgb);
    using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(main, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
    return bmp;
  }

  static int[] Row(Bitmap bmp, int y) {
    var line = new int[bmp.Width];
    var d = bmp.LockBits(new Rectangle(0, y, bmp.Width, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
    try { Marshal.Copy(d.Scan0, line, 0, bmp.Width); } finally { bmp.UnlockBits(d); }
    return line;
  }

  // the bar: the row of the top third with the longest run of accent pixels; returns { y, left, length } (length 0: none)
  public static int[] FindBar(IntPtr main) {
    using (var bmp = Grab(main)) {
      int bestY = -1, bestLeft = 0, bestLen = 0;
      for (int y = 0; y < bmp.Height / 3; y++) {
        var line = Row(bmp, y);
        int run = 0;
        for (int x = 0; x < line.Length; x++) {
          if (Accent(line[x])) { run++; if (run > bestLen) { bestLen = run; bestY = y; bestLeft = x - run + 1; } } else run = 0;
        }
      }
      // the middle of the rows that have the longest run (the bar is 10 DIPs high with round ends)
      int top = bestY, bottom = bestY;
      if (bestY >= 0) {
        for (int y = bestY - 1; y >= 0; y--) { var l = Row(bmp, y); int run = 0; for (int x = bestLeft; x < bestLeft + bestLen && x < l.Length; x++) if (Accent(l[x])) run++; if (run >= bestLen - 1) top = y; else break; }
        for (int y = bestY + 1; y < bmp.Height; y++) { var l = Row(bmp, y); int run = 0; for (int x = bestLeft; x < bestLeft + bestLen && x < l.Length; x++) if (Accent(l[x])) run++; if (run >= bestLen - 1) bottom = y; else break; }
      }
      return new[] { (top + bottom) / 2, bestLeft, bestLen };
    }
  }

  // the fill in one grab: the accent run on row y that starts within 24 px of left; { start, length } (-1, 0: none)
  public static int[] Fill(Bitmap bmp, int y, int left) {
    var line = Row(bmp, Math.Min(bmp.Height - 1, Math.Max(0, y)));
    for (int x = Math.Max(0, left - 24); x < Math.Min(line.Length, left + 24); x++) {
      if (!Accent(line[x])) continue;
      int end = x; while (end + 1 < line.Length && Accent(line[end + 1])) end++;
      return new[] { x, end - x + 1 };
    }
    return new[] { -1, 0 };
  }

  // ---- the wheel, on a thread of its own: delta[i] posted at start + at[i] ms; the posting times come back in posted
  public static Thread Post(IntPtr input, int sx, int sy, int[] at, int[] delta, double start, List<double> posted) {
    var t = new Thread(() => {
      timeBeginPeriod(1);
      for (int i = 0; i < at.Length; i++) {
        while (Now() < start + at[i]) { double left = start + at[i] - Now(); if (left > 2) Thread.Sleep(1); else Thread.SpinWait(200); }
        long w = ((long)(short)delta[i] << 16);
        PostMessage(input, 0x020A, (IntPtr)(uint)(w & 0xFFFFFFFF), Lp(sx, sy));
        lock (posted) posted.Add(Now() - start);
      }
    }) { IsBackground = true };
    t.Start();
    return t;
  }

  // two moves over the point first (WinUI hit-tests the wheel where the pointer last was)
  public static void MoveTo(IntPtr input, int sx, int sy) {
    var c = new POINT { X = sx, Y = sy }; ScreenToClient(input, ref c);
    PostMessage(input, 0x0200, IntPtr.Zero, Lp(c.X - 4, c.Y - 4)); Thread.Sleep(40);
    PostMessage(input, 0x0200, IntPtr.Zero, Lp(c.X, c.Y)); Thread.Sleep(40);
  }

  // ---- the indicator's number (UI Automation), on a thread of its own: (ms, text) on each change
  public static volatile bool TextRun;
  public static readonly List<KeyValuePair<double, string>> Texts = new List<KeyValuePair<double, string>>();
  public static Thread WatchText(IntPtr main) {
    TextRun = true;
    var t = new Thread(() => {
      AutomationElement root = null, text = null; string last = null;
      var cond = new PropertyCondition(AutomationElement.AutomationIdProperty, "VolumeIndicatorText");
      while (TextRun) {
        string now = null;
        try {
          if (root == null) root = AutomationElement.FromHandle(main);
          if (text == null) text = root.FindFirst(TreeScope.Descendants, cond);
          if (text != null) now = text.Current.Name;
        } catch (Exception) { text = null; }
        if (now != last) { lock (Texts) Texts.Add(new KeyValuePair<double, string>(Now(), now ?? "")); last = now; }
        Thread.Sleep(text == null ? 60 : 12);
      }
    }) { IsBackground = true };
    t.Start();
    return t;
  }

  // ---- mpv's volume (observe_property over the in-process core's pipe): (ms, volume) on each change
  public static readonly List<KeyValuePair<double, double>> Volumes = new List<KeyValuePair<double, double>>();
  static NamedPipeClientStream _pipe;
  public static string WatchMpv(string pipeName) {
    try {
      _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
      _pipe.Connect(3000);
      var w = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
      w.WriteLine("{\"command\":[\"observe_property\",77,\"volume\"]}");
      var reader = new StreamReader(_pipe, new UTF8Encoding(false));
      var re = new Regex("\"event\":\"property-change\".*\"id\":77.*\"data\":([0-9.eE+-]+)|\"data\":([0-9.eE+-]+).*\"id\":77.*\"event\":\"property-change\"");
      var t = new Thread(() => {
        try {
          string line;
          while ((line = reader.ReadLine()) != null) {
            if (line.IndexOf("\"volume\"", StringComparison.Ordinal) < 0) continue;
            var m = re.Match(line);
            if (!m.Success) continue;
            var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            lock (Volumes) Volumes.Add(new KeyValuePair<double, double>(Now(), double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)));
          }
        } catch (Exception) { }
      }) { IsBackground = true };
      t.Start();
      return null;
    } catch (Exception e) { return e.Message; }
  }
  public static KeyValuePair<double, string>[] TextsNow() { lock (Texts) return Texts.ToArray(); }
  public static KeyValuePair<double, double>[] VolumesNow() { lock (Volumes) return Volumes.ToArray(); }
  public static void StopMpv() { try { if (_pipe != null) _pipe.Dispose(); } catch (Exception) { } }

  // one pixel of a grab: "r,g,b"
  public static string Pixel(Bitmap bmp, int x, int y) { int p = Row(bmp, y)[x]; return string.Format("{0},{1},{2}", (p >> 16) & 255, (p >> 8) & 255, p & 255); }
  public static string PixelNow(IntPtr main, int x, int y) { using (var bmp = Grab(main)) return Pixel(bmp, x, y); }

  // ---- one burst: grabs back to back for durationMs from the first notch; rows "ms,grab end ms,left,width,r,g,b" (the
  // pixel at probeX on the bar's row: the indicator's opacity, against the same pixel with it hidden); the indicator strip
  public static List<string> Burst(IntPtr main, IntPtr input, int sx, int sy, int[] at, int[] delta, int durationMs,
      int barY, int barLeft, int barSpan, int probeX, string strip, List<double> posted) {
    var rows = new List<string>();
    var crops = new List<Bitmap>(); var times = new List<double>();
    double start = Now() + 30;
    var poster = Post(input, sx, sy, at, delta, start, posted);
    int cx0 = Math.Max(0, barLeft - 90), cy0 = Math.Max(0, barY - 70), cw = barSpan + 120, ch = 90;
    while (Now() < start + durationMs) {
      double t0 = Now() - start;
      using (var bmp = Grab(main)) {
        double t1 = Now() - start;
        var f = Fill(bmp, barY, barLeft);
        rows.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0},{1:0.0},{2},{3},{4}", t0, t1, f[0], f[1], Pixel(bmp, probeX, barY)));
        if (strip != null && crops.Count < 90) {
          var c = new Bitmap(cw, ch, PixelFormat.Format32bppRgb);
          using (var g = Graphics.FromImage(c)) g.DrawImage(bmp, new Rectangle(0, 0, cw, ch), new Rectangle(cx0, cy0, cw, ch), GraphicsUnit.Pixel);
          crops.Add(c); times.Add(t0);
        }
      }
    }
    poster.Join(2000);
    if (strip != null && crops.Count > 0) {
      int cols = 3, rowsN = (crops.Count + cols - 1) / cols, lab = 14;
      using (var sheet = new Bitmap(cols * (cw + 4), rowsN * (ch + lab)))
      using (var g = Graphics.FromImage(sheet))
      using (var font = new Font("Segoe UI", 8f)) {
        g.Clear(Color.FromArgb(30, 30, 30));
        for (int i = 0; i < crops.Count; i++) {
          int x = (i % cols) * (cw + 4), y = (i / cols) * (ch + lab);
          g.DrawImage(crops[i], x, y + lab);
          g.DrawString(string.Format("{0:0} ms", times[i]), font, Brushes.White, x + 2, y);
          crops[i].Dispose();
        }
        sheet.Save(strip, ImageFormat.Png);
      }
    }
    rows.Insert(0, string.Format(System.Globalization.CultureInfo.InvariantCulture, "#start {0:0.0}", start));
    return rows;
  }
}
"@
}

$volDir = Join-Path $Out 'volume'
New-Item -ItemType Directory -Force $volDir | Out-Null
$summaryFile = Join-Path $volDir 'summary.txt'
if ($PlayerControls -ne 'FluentFin') { Check 'volume: FluentFin controls' $false "player controls are $PlayerControls (libmpv missing from the build?)"; return }

$wheelWindow = [OffscreenUi]::KeyWindow($script:hwnd, $script:ui.Id)
$r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r)
$sx = [int](($r.L + $r.R) / 2); $sy = [int]($r.T + ($r.B - $r.T) * 0.45)
Say "volume: window $($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T), wheel at $sx,$sy to $([OffscreenUi]::ClassOf($wheelWindow))"

# mpv's pipe, from FluentFin's log
$pipeName = $null
foreach ($f in @(Get-ChildItem (Join-Path $DataDir 'Logs') -Filter 'log*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)) {
    $m = Select-String -LiteralPath $f.FullName -Pattern 'IPC pipe (fluentfin-mpv-[0-9a-f-]+)' | Select-Object -Last 1
    if ($m) { $pipeName = $m.Matches[0].Groups[1].Value; break }
}
if ($pipeName) { $err = [VolumeProbe]::WatchMpv($pipeName); Say "volume: mpv's pipe $pipeName $(if ($err) { "NOT watched: $err" } else { 'watched' })" }
else { Say 'volume: no IPC pipe in the log: mpv''s volume is not watched' }
[void][VolumeProbe]::WatchText($script:hwnd)

function Post-Notches([int]$Count, [int]$Delta, [int]$GapMs) {
    $posted = New-Object 'System.Collections.Generic.List[double]'
    $at = @(for ($i = 0; $i -lt $Count; $i++) { $i * $GapMs }); $d = @(for ($i = 0; $i -lt $Count; $i++) { $Delta })
    [VolumeProbe]::MoveTo($wheelWindow, $sx, $sy)
    $t = [VolumeProbe]::Post($wheelWindow, $sx, $sy, [int[]]$at, [int[]]$d, [VolumeProbe]::Now(), $posted)
    $t.Join(10000) | Out-Null
}
function Last-Mpv { $v = [VolumeProbe]::VolumesNow(); if ($v.Count) { $v[$v.Count - 1].Value } else { $null } }
function Last-Text { $t = [VolumeProbe]::TextsNow(); for ($i = $t.Count - 1; $i -ge 0; $i--) { if ($t[$i].Value) { return $t[$i].Value } }; $null }

# a known start: 100 (ups at 100 change nothing), then the indicator gone
Post-Notches 22 120 35
Start-Sleep -Milliseconds 2600
# the bar's place: one notch down (95 %), the glide over
Post-Notches 1 -120 0
Start-Sleep -Milliseconds 700
$bar = [VolumeProbe]::FindBar($script:hwnd)
Shot 'volume-indicator'
Dump-Tree 'volume-indicator'
$span = if ($bar[2] -gt 0) { [int][math]::Round($bar[2] / 0.95) } else { 0 }
Check 'volume: the indicator''s bar found' ($bar[2] -gt 50) "row $($bar[0]), left $($bar[1]), $($bar[2]) px at 95 % (full: $span px), text '$(Last-Text)', mpv $(Last-Mpv)"
if ($bar[2] -le 50) { return }
Start-Sleep -Milliseconds 2200
# the opacity probe: a quarter along the bar (filled from 30 %), the video under it with the indicator gone; the accent
$probeX = $bar[1] + [int]($span / 4)
$under = @([VolumeProbe]::PixelNow($script:hwnd, $probeX, $bar[0]) -split ',' | ForEach-Object { [int]$_ })
$accent = @(92, 207, 193)
$ch = 0; for ($i = 1; $i -lt 3; $i++) { if ([math]::Abs($accent[$i] - $under[$i]) -gt [math]::Abs($accent[$ch] - $under[$ch])) { $ch = $i } }
function Alpha([int[]]$Rgb) { [math]::Round(($Rgb[$ch] - $under[$ch]) / [double]($accent[$ch] - $under[$ch]), 2) }
Say "volume: opacity probe at $probeX,$($bar[0]): under it $($under -join ','), channel $ch"

# scenario: name, notch deltas, their times (ms from the first), wait first for the indicator to go (ms), grab for (ms)
function Notches([int]$Count, [int]$Delta, [int]$Gap) { @{ D = @(for ($i = 0; $i -lt $Count; $i++) { $Delta }); A = @(for ($i = 0; $i -lt $Count; $i++) { $i * $Gap }) } }
$touch = @(-7, -13, -22, -9, -31, -18, -4, -26, -15, -11, -24, -20, -8, -30, -12, -17, -25, -6, -14, -28, -19, -10, -23, -16, -5, -27, -21, -3, -29, -14, -18, -22, -9, -13, -11, -16, -20, -7, -25)
$touchSum = ($touch | Measure-Object -Sum).Sum; $touch += @(-600 - $touchSum)
$scenarios = [ordered]@{
    'single'     = @{ S = @{ D = @(-120, -120, -120, -120); A = @(0, 450, 900, 1350) }; Wait = 0; For = 2000 }
    'stale-base' = @{ S = @{ D = @(-120); A = @(0) }; Wait = 2600; For = 700 }
    'fast-down'  = @{ S = (Notches 12 -120 30); Wait = 2600; For = 1000 }
    'fast-up'    = @{ S = (Notches 12 120 30); Wait = 0; For = 1000 }
    'medium'     = @{ S = (Notches 8 -120 90); Wait = 2600; For = 1400 }
    'hires-up'   = @{ S = (Notches 40 30 8); Wait = 2600; For = 1000 }
    'touchpad'   = @{ S = @{ D = $touch; A = @(for ($i = 0; $i -lt $touch.Count; $i++) { $i * 10 }) }; Wait = 2600; For = 1100 }
    'reverse'    = @{ S = @{ D = @(-120, -120, -120, 120, 120, 120); A = @(0, 40, 80, 120, 160, 200) }; Wait = 0; For = 900 }
    # a notch, then another as the indicator fades out (it hides at the first 250 ms tick after 1.5 s, then fades 0.4 s)
    'reshow'     = @{ S = @{ D = @(-120, -120); A = @(0, 1800) }; Wait = 2600; For = 2600 }
}
$pick = if ($env:VOLUME_SCENARIOS) { $env:VOLUME_SCENARIOS -split ',' } else { @($scenarios.Keys) }
$volumeNow = Last-Mpv
if ($null -eq $volumeNow) { $volumeNow = 95 }
foreach ($name in $pick) {
    $sc = $scenarios[$name]; if (-not $sc) { Say "volume: no scenario $name"; continue }
    if ($sc.Wait) { Start-Sleep -Milliseconds $sc.Wait }
    $before = Last-Mpv; if ($null -eq $before) { $before = $volumeNow }
    $posted = New-Object 'System.Collections.Generic.List[double]'
    [VolumeProbe]::MoveTo($wheelWindow, $sx, $sy)
    $rows = [VolumeProbe]::Burst($script:hwnd, $wheelWindow, $sx, $sy, [int[]]$sc.S.A, [int[]]$sc.S.D, [int]$sc.For, $bar[0], $bar[1], $span, $probeX, (Join-Path $volDir "$name-strip.png"), $posted)
    Start-Sleep -Milliseconds 300
    $start = [double](($rows[0] -split ' ')[1])
    # the expected volume after each notch (the view model's arithmetic: 120 a notch, 5 a notch, 0 to 100)
    $acc = 0; $expect = [double]$before; $targets = @()
    foreach ($d in $sc.S.D) { $acc += $d; $n = [math]::Truncate($acc / 120); $acc -= $n * 120; if ($n -ne 0) { $expect = [math]::Max(0, [math]::Min(100, $expect + 5 * $n)); $targets += $expect } }
    $direction = [math]::Sign(($sc.S.D | Measure-Object -Sum).Sum)
    $texts = @([VolumeProbe]::TextsNow() | Where-Object { $_.Key -ge $start - 50 -and $_.Key -le $start + $sc.For } | ForEach-Object { [pscustomobject]@{ T = $_.Key - $start; V = $_.Value } })
    $mpv = @([VolumeProbe]::VolumesNow() | Where-Object { $_.Key -ge $start - 50 -and $_.Key -le $start + $sc.For + 300 } | ForEach-Object { [pscustomobject]@{ T = $_.Key - $start; V = $_.Value } })
    $samples = foreach ($line in $rows | Select-Object -Skip 1) {
        $c = $line -split ','
        $t = [double]$c[0]
        $txt = ($texts | Where-Object { $_.T -le $t } | Select-Object -Last 1).V
        $mv = ($mpv | Where-Object { $_.T -le $t } | Select-Object -Last 1).V
        [pscustomobject]@{ T = $t; T1 = [double]$c[1]; Left = [int]$c[2]; W = [int]$c[3]; Pct = [math]::Round(100.0 * [int]$c[3] / $span, 1); Text = $txt; Mpv = $mv
            Alpha = (Alpha @([int]$c[4], [int]$c[5], [int]$c[6])) }
    }
    $samples = @($samples)
    $csv = @('ms,grab_end_ms,fill_left,fill_px,fill_pct,opacity,text,mpv_volume') + @($samples | ForEach-Object { '{0:0.0},{1:0.0},{2},{3},{4},{5},{6},{7}' -f $_.T, $_.T1, $_.Left, $_.W, $_.Pct, $_.Alpha, $_.Text, $_.Mpv })
    [IO.File]::WriteAllLines((Join-Path $volDir "$name.csv"), [string[]]$csv)
    # backward steps: between two grabs in a row the fill moved against the burst's direction (the grow from 94 % when the
    # indicator appears is not one: its left end is not yet in place, those grabs are left out)
    $steady = @($samples | Where-Object { $_.W -gt 0 -and [math]::Abs($_.Left - $bar[1]) -le 1 })
    $back = @(); $maxBack = 0.0
    if ($direction -ne 0 -and $name -notin 'reverse', 'reshow') {
        for ($i = 1; $i -lt $steady.Count; $i++) {
            $dw = ($steady[$i].W - $steady[$i - 1].W) * $direction
            if ($dw -lt -1.5) { $back += $steady[$i]; $maxBack = [math]::Max($maxBack, -$dw) }
        }
    }
    $final = $samples | Where-Object { $_.W -gt 0 } | Select-Object -Last 1
    $finalPct = if ($final) { $final.Pct } else { 0 }
    $after = Last-Mpv
    $mpvSeq = ($mpv | ForEach-Object { '{0:0}@{1:0}' -f $_.V, $_.T }) -join ' '
    $textSeq = ($texts | ForEach-Object { '{0}@{1:0}' -f $_.V, $_.T }) -join ' '
    $line = "{0}: {1} events ({2} notches) over {3:0} ms, posted at {4}; volume {5} -> {6} (expected {7}); mpv changes {8} [{9}]; text [{10}]; grabs {11} ({12:0} ms avg); fill backward steps {13}, largest {14:0} px ({15:0.0} %); fill ends at {16} % ({17} px)" -f `
        $name, $sc.S.D.Count, $targets.Count, ($sc.S.A | Measure-Object -Maximum).Maximum, (($posted | ForEach-Object { '{0:0}' -f $_ }) -join ' '),
        $before, $after, $expect, $mpv.Count, $mpvSeq, $textSeq, $samples.Count, (($samples | ForEach-Object { $_.T1 - $_.T } | Measure-Object -Average).Average),
        $back.Count, $maxBack, (100.0 * $maxBack / $span), $finalPct, $(if ($final) { $final.W } else { 0 })
    Say "volume $line"
    Add-Content -LiteralPath $summaryFile -Value $line -Encoding utf8
    Check "volume ${name}: ends at the expected volume" ($null -ne $after -and [math]::Abs($after - $expect) -lt 0.5) "mpv $after, expected $expect"
    Check "volume ${name}: the fill never steps back" ($back.Count -eq 0) "$($back.Count) backward steps, largest $([math]::Round($maxBack)) px"
    Check "volume ${name}: the fill ends at the volume" ([math]::Abs($finalPct - $expect) -le 1.5) "$finalPct % for $expect"
    if ($name -eq 'reshow' -and $posted.Count -ge 2) {
        # the second notch came as the indicator faded out: from there on its opacity may only rise (a drop is a blink: the
        # show animation starting again from nothing) and its left end may only come back to its place (a jump away from
        # it is the grow starting again from 94 %)
        $at = $posted[1]
        $prev = $samples | Where-Object { $_.T -lt $at } | Select-Object -Last 1
        $later = @($samples | Where-Object { $_.T -ge $at -and $_.T -le $at + 500 })
        $minAlpha = ($later | Measure-Object Alpha -Minimum).Minimum
        # (a grab where the fill was not found, faded too far, has no left end: left out)
        $moved = ($later | Where-Object { $_.Left -ge 0 } | ForEach-Object { [math]::Abs($_.Left - $bar[1]) } | Measure-Object -Maximum).Maximum
        $prevMoved = if ($prev -and $prev.Left -ge 0) { [math]::Abs($prev.Left - $bar[1]) } else { 0 }
        $alphas = (@($prev) + $later | ForEach-Object { '{0:0.00}@{1:0}' -f $_.Alpha, $_.T }) -join ' '
        $rs = "reshow: second notch at $([math]::Round($at)) ms with the opacity at $($prev.Alpha); after it the lowest $minAlpha; the left end off its place by up to $moved px (before: $prevMoved); opacity [$alphas]"
        Say "volume $rs"; Add-Content -LiteralPath $summaryFile -Value $rs -Encoding utf8
        Check 'volume reshow: no blink when a notch comes as the indicator fades out' ($prev -and $minAlpha -ge $prev.Alpha - 0.1) "opacity $($prev.Alpha) -> lowest $minAlpha"
        Check 'volume reshow: no jump in size either' ($moved -le [math]::Max(2, $prevMoved + 1)) "left end off by $moved px (before $prevMoved)"
    }
    $volumeNow = $after
}
# without motion (VOLUME_NO_MOTION=1: the run was made with -Setting Glass=Solid or Reduced) the fill moves at once: every
# grab shows it at a level (a multiple of the 5 % step), never between two
if ($env:VOLUME_NO_MOTION -eq '1') {
    $between = 0
    foreach ($f in Get-ChildItem $volDir -Filter '*.csv') {
        foreach ($row in @(Import-Csv $f.FullName)) {
            $pct = [double]$row.fill_pct
            if ([int]$row.fill_px -gt 0 -and [int]$row.fill_left -eq $bar[1] -and [math]::Abs($pct - 5 * [math]::Round($pct / 5)) -gt 1.5) { $between++ }
        }
    }
    Check 'volume: without motion the fill moves at once' ($between -eq 0) "$between grabs with the fill between two levels"
}
# the indicator in a narrow and a wide window (one notch each, shot once the fill is there)
$was = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$was)
$wasMax = [VolumeProbe]::IsZoomed($script:hwnd)
if ($wasMax) { [void][VolumeProbe]::ShowWindow($script:hwnd, 9) }
foreach ($size in @(@(1100, 760), @(1920, 1080))) {
    [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $size[0], $size[1]); Start-Sleep -Milliseconds 900
    $r = New-Object OffscreenUi+RECT; [void][OffscreenUi]::GetWindowRect($script:hwnd, [ref]$r)
    $sx = [int](($r.L + $r.R) / 2); $sy = [int]($r.T + ($r.B - $r.T) * 0.45)
    Post-Notches 1 120 0
    Start-Sleep -Milliseconds 450
    Shot "volume-$($size[0])"
    Start-Sleep -Milliseconds 2200
}
if ($wasMax) { [void][VolumeProbe]::ShowWindow($script:hwnd, 3) } else { [OffscreenUi]::Resize($script:hwnd, $script:ui.Id, $was.R - $was.L, $was.B - $was.T) }
[VolumeProbe]::TextRun = $false
[VolumeProbe]::StopMpv()
$sets = @(Get-Content -LiteralPath (Join-Path $DataDir 'Logs\mpv-backend.log') -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match '<- set_volume' }).Count
Say "volume: set_volume commands the backend received in the whole run: $sets"
Add-Content -LiteralPath $summaryFile -Value "set_volume commands the backend received in the whole run: $sets" -Encoding utf8
