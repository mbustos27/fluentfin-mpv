using System.Runtime.InteropServices;
using System.Text;
using FluentFin.Core.Mpv;
using FluentFin.Mpv;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ControlsHost;

/// <summary>
/// Runs one scenario and exits: its own main window (a title row over a black video area, like the player page), the real
/// backend started in a job with --dev or against a fake server, mpv embedded through the bridge, and FluentFin's controls
/// window kept over the video. Each step logs CHECK lines (ok/FAIL) to host.txt and pictures of its own windows under -out.
///
///   ControlsHost.exe --python &lt;venv python&gt; --out &lt;dir&gt; --scenario spike
/// </summary>
internal static partial class Host
{
	private static readonly object s_logLock = new();
	private static string s_out = Path.GetTempPath();
	private static readonly System.Diagnostics.Stopwatch s_clock = System.Diagnostics.Stopwatch.StartNew();
	private static int s_failures;

	public static void Log(string line)
	{
		lock (s_logLock)
		{
			File.AppendAllText(Path.Combine(s_out, "host.txt"), $"[{s_clock.Elapsed.TotalSeconds,7:0.000}] {line}{Environment.NewLine}");
		}
	}

	public static void Check(bool ok, string what)
	{
		if (!ok)
		{
			Interlocked.Increment(ref s_failures);
		}

		Log($"CHECK {(ok ? "ok" : "FAIL")}: {what}");
	}

	private static string Arg(string name, string? fallback = null)
	{
		var args = Environment.GetCommandLineArgs();
		var i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback ?? throw new ArgumentException($"{name} is required");
	}

	public static void Start(Window main)
	{
		s_out = Path.GetFullPath(Arg("--out"));
		Directory.CreateDirectory(s_out);
		File.WriteAllText(Path.Combine(s_out, "host.txt"), "");
		var scenario = Arg("--scenario", "spike");
		main.DispatcherQueue.TryEnqueue(async () =>
		{
			var code = 1;
			try
			{
				code = await Run(main, scenario);
			}
			catch (Exception ex)
			{
				Log($"FAILED {ex}");
			}

			Log($"RESULT {scenario}: {(code == 0 && s_failures == 0 ? "ok" : $"FAIL ({s_failures} check(s) failed)")}");
			Environment.Exit(code == 0 && s_failures == 0 ? 0 : 1);
		});
	}

	private sealed class Page
	{
		public required Grid Root { get; init; }
		public required Grid VideoArea { get; init; }
	}

	/// <summary>A title row over a black video area, as the player page lays it out.</summary>
	private static Page BuildPage(Window main)
	{
		var root = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)), RequestedTheme = ElementTheme.Dark };
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.Children.Add(new TextBlock { Text = "Controls host (not FluentFin)", Margin = new Thickness(12, 10, 0, 0), Foreground = new SolidColorBrush(Colors.Gray) });
		var video = new Grid { Background = new SolidColorBrush(Colors.Black) };
		Grid.SetRow(video, 1);
		root.Children.Add(video);
		main.Content = root;
		return new Page { Root = root, VideoArea = video };
	}

	private static async Task<int> Run(Window main, string scenario)
	{
		var page = BuildPage(main);
		main.AppWindow.MoveAndResize(new RectInt32(40, 40, 1100, 680));
		main.Activate();
		await WhenLoaded(page.VideoArea);

		var videoHost = new MpvVideoHost(new FileLogger<MpvVideoHost>(Path.Combine(s_out, "video-host.log")));
		var overlay = new MpvControlsOverlay(main);
		var spike = SpikeContent();
		overlay.Content = spike;
		videoHost.ScreenRectChanged += overlay.PlaceAt;
		videoHost.Attach(page.VideoArea);

		var python = Arg("--python");
		var configDir = Path.Combine(s_out, "backend-config");
		Directory.CreateDirectory(configDir);
		var backend = new MpvBackendHost(new Launcher(python, configDir, Path.Combine(s_out, "backend.log")), videoHost, new FileLogger<MpvBackendHost>(Path.Combine(s_out, "backend-host.log")));
		var states = new List<string>();
		backend.Events.Subscribe(e =>
		{
			if (e.Name == Bridge.Events.State && e.DataAs<BridgeState>() is { } s)
			{
				lock (states)
				{
					states.Add(s.State);
				}
			}

			if (e.Name != Bridge.Events.Position)
			{
				Log($"event {e.Name}");
			}
		});

		try
		{
			await backend.StartAsync(dev: true);
			await backend.ConnectAsync(null);
			videoHost.SetVideoVisible(true);
			await backend.SendAsync(Bridge.Commands.PlayFile, BridgeCommands.PlayFile(MpvSelfTestRequest.TestSource, "controls host"));
			Check(await Until(() => { lock (states) { return states.Contains(Bridge.States.Playing); } }, 30), "the test pattern plays");
			await Task.Delay(1500);

			return scenario switch
			{
				"spike" => await Spike(main, page, videoHost, overlay, spike),
				"frame" => await FrameExperiment(overlay),
				"popup" => await PopupExperiment(main, page, videoHost, overlay),
				_ => throw new ArgumentException($"unknown scenario {scenario}"),
			};
		}
		finally
		{
			main.Close();
			await backend.ShutdownAsync();
			Check(overlay.Placement is null, "the controls window is gone once the main window closed");
		}
	}

	private static async Task<int> Spike(Window main, Page page, MpvVideoHost videoHost, MpvControlsOverlay overlay, FrameworkElement spike)
	{
		async Task Settle(int ms = 700) => await Task.Delay(ms);

		async Task Step(string name)
		{
			await Settle();
			var host = videoHost.ScreenRect;
			var placed = overlay.Placement;
			// the controls' client area on the screen: the window is larger by its (clipped) frame
			GetClientRect(overlay.Handle, out var client);
			var origin = new POINT();
			ClientToScreen(overlay.Handle, ref origin);
			var real = new Shots.RECT { Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };
			Shots.GetWindowRect(overlay.Handle, out var outer);
			Log($"{name}: controls window outer {outer.Left},{outer.Top}-{outer.Right},{outer.Bottom}");
			var mpv = MpvChild(videoHost.Handle);
			Shots.RECT mpvRect = default;
			if (mpv != 0)
			{
				Shots.GetWindowRect(mpv, out mpvRect);
			}

			var scale = main.Content.XamlRoot?.RasterizationScale ?? 0;
			var overlayScale = spike.XamlRoot?.RasterizationScale ?? 0;
			Log($"{name}: video window {Describe(host)}, mpv {mpvRect.Left},{mpvRect.Top} {mpvRect.Right - mpvRect.Left}x{mpvRect.Bottom - mpvRect.Top}, controls window {real.Left},{real.Top} {real.Right - real.Left}x{real.Bottom - real.Top} (placed {Describe(placed)}), scale {scale} / {overlayScale}");
			Check(host is not null && placed is not null && host.Value.Equals(placed.Value), $"{name}: the controls window is placed exactly over the video window");
			Check(placed is { } p && real.Left == p.X && real.Top == p.Y && real.Right - real.Left == p.Width && real.Bottom - real.Top == p.Height, $"{name}: and Windows has its client area there");
			Check(mpv != 0 && IsWindowVisible(overlay.Handle) && mpvRect.Left == real.Left && mpvRect.Top == real.Top && mpvRect.Right == real.Right && mpvRect.Bottom == real.Bottom, $"{name}: mpv's window has the same rectangle");
			Check(GetWindow(overlay.Handle, GW_OWNER) == WinRT.Interop.WindowNative.GetWindowHandle(main), $"{name}: owned by the main window (always above it and its mpv child)");
			Check(Math.Abs(scale - overlayScale) < 0.001, $"{name}: same DPI scale as the main window");

			// what is on top at the middle of the controls' bottom bar: the controls window, not mpv's
			var probe = new POINT { X = real.Left + (real.Right - real.Left) / 2, Y = real.Bottom - 30 };
			var top = WindowFromPoint(probe);
			Check(top == overlay.Handle || IsChild(overlay.Handle, top), $"{name}: the window under the bottom bar is the controls window (0x{top:x})");

			await Picture(main, overlay, spike, name);
		}

		Log($"controls window style 0x{GetWindowLongPtrW(overlay.Handle, -16):x8}, ex style 0x{GetWindowLongPtrW(overlay.Handle, -20):x8}");
		Check((GetWindowLongPtrW(overlay.Handle, -20) & 0x80) != 0 && (GetWindowLongPtrW(overlay.Handle, -20) & 0x40000) == 0, "the controls window is a tool window, not an app window (no taskbar button, no Alt+Tab entry)");
		await Step("windowed");

		main.AppWindow.Resize(new SizeInt32(1400, 860));
		await Step("resized");

		main.AppWindow.Move(new PointInt32(180, 120));
		await Step("moved");

		main.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
		await Step("fullscreen");
		var display = DisplayArea.GetFromWindowId(main.AppWindow.Id, DisplayAreaFallback.Primary).OuterBounds;
		var titleRow = (int)Math.Round(40 * main.Content.XamlRoot!.RasterizationScale);
		Check(videoHost.ScreenRect is { } fs && fs.X == display.X && fs.Width == display.Width && Math.Abs(fs.Height - (display.Height - titleRow)) <= 1,
			$"fullscreen: the video area fills the display below the title row ({Describe(videoHost.ScreenRect)} on {display.X},{display.Y} {display.Width}x{display.Height})");

		main.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
		await Step("restored");

		(main.AppWindow.Presenter as OverlappedPresenter)?.Minimize();
		await Settle();
		Check(!IsWindowVisible(overlay.Handle), "minimized: the controls window is hidden with its owner");
		(main.AppWindow.Presenter as OverlappedPresenter)?.Restore();
		await Settle(1200);
		Check(IsWindowVisible(overlay.Handle), "restored from minimized: the controls window is back");
		await Step("unminimized");

		// a flyout of the page would open under the video and the controls: both step aside
		var anchor = new Button { Content = "menu", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(250, 4, 0, 0) };
		page.Root.Children.Add(anchor);
		var flyout = new Flyout { Content = new TextBlock { Text = "a flyout of the page" } };
		await Settle(300);
		flyout.ShowAt(anchor);
		await Settle();
		Check(overlay.Placement is null && !IsWindowVisible(overlay.Handle), "a page flyout is open: the controls window steps aside with the video");
		await Picture(main, overlay, spike, "page-flyout");
		flyout.Hide();
		await Settle();
		Check(overlay.Placement is not null && IsWindowVisible(overlay.Handle), "the flyout closed: the controls window is back");
		return 0;
	}

	/// <summary>
	/// Option A1 of the UX audit: the controls in a windowed XAML Popup of the page (ShouldConstrainToRootBounds=false)
	/// instead of a window of their own. Measured like the spike: on top of mpv? see-through? where is it after the main
	/// window moves? does it take activation? (The video window hides for any open popup, so it is told to ignore this one
	/// by name in this experiment only.)
	/// </summary>
	private static async Task<int> PopupExperiment(Window main, Page page, MpvVideoHost videoHost, MpvControlsOverlay overlay)
	{
		overlay.PlaceAt(null);
		videoHost.ScreenRectChanged -= overlay.PlaceAt;
		var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(main);
		var root = main.Content.XamlRoot!;
		var content = SpikeContent();
		var before = TopLevelWindowsOfThisProcess();
		var popup = new Popup { ShouldConstrainToRootBounds = false, IsLightDismissEnabled = false, Child = content, XamlRoot = root };
		void Fit()
		{
			var at = page.VideoArea.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
			popup.HorizontalOffset = at.X;
			popup.VerticalOffset = at.Y;
			content.Width = page.VideoArea.ActualWidth;
			content.Height = page.VideoArea.ActualHeight;
		}

		Fit();
		popup.IsOpen = true;
		await Task.Delay(800);
		var popupHwnd = TopLevelWindowsOfThisProcess().Except(before).FirstOrDefault(h => IsWindowVisible(h));
		Log($"popup window 0x{popupHwnd:x}, class {ClassName(popupHwnd)}, owner 0x{GetWindow(popupHwnd, GW_OWNER):x}, style 0x{GetWindowLongPtrW(popupHwnd, -16):x8} ex 0x{GetWindowLongPtrW(popupHwnd, -20):x8}, video window {Describe(videoHost.ScreenRect)}");
		Check(popupHwnd != 0, "A1: the open popup has a window of its own");
		if (popupHwnd == 0)
		{
			return 0;
		}

		async Task Measure(string name)
		{
			Shots.GetWindowRect(popupHwnd, out var r);
			var area = videoHost.ScreenRect ?? new RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
			Log($"A1 {name}: popup window {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}; video {Describe(videoHost.ScreenRect)}; foreground is main: {GetForegroundWindow() == mainHwnd}");
			var b = WindowCapture.OverColor(popupHwnd, area, 0x000000);
			var w = WindowCapture.OverColor(popupHwnd, area, 0xFFFFFF);
			if (b is not null && w is not null)
			{
				var a = Shots.AlphaFrom(b, w);
				Log($"A1 {name}: over the video area: middle alpha {Shots.MeanAlpha(a, a.Width / 4, a.Height / 3, a.Width * 3 / 4, a.Height * 2 / 3):0.0}, bottom {Shots.MeanAlpha(a, 4, a.Height - 12, a.Width - 4, a.Height - 4):0.0}, left edge {Shots.MeanAlpha(a, 0, a.Height / 3, 1, a.Height * 2 / 3):0.0}");
				await Shots.Save(w, Path.Combine(s_out, "shots", $"popup-{name}-over-white.png"));
			}

			if (Shots.Print(popupHwnd) is { } printed)
			{
				await Shots.Save(printed, Path.Combine(s_out, "shots", $"popup-{name}-printwindow.png"));
				Log($"A1 {name}: PrintWindow of the popup: {Shots.PercentLit(printed, 0, printed.Height - 80, printed.Width, printed.Height)}% of its bottom 80 px not black");
			}

			var probe = new POINT { X = area.X + area.Width / 2, Y = area.Y + area.Height - 30 };
			var top = WindowFromPoint(probe);
			Log($"A1 {name}: window under the bottom bar 0x{top:x} ({ClassName(top)}){(top == popupHwnd || IsChild(popupHwnd, top) ? " = the popup" : "")}");
		}

		await Measure("opened");
		main.AppWindow.Move(new PointInt32(220, 160));
		await Task.Delay(800);
		await Measure("main moved, offsets untouched");
		Fit();
		await Task.Delay(500);
		await Measure("main moved, offsets set again");
		main.AppWindow.Resize(new SizeInt32(1300, 800));
		await Task.Delay(500);
		Fit();
		await Task.Delay(500);
		await Measure("resized");
		popup.IsOpen = false;
		return 0;
	}

	private static List<nint> TopLevelWindowsOfThisProcess()
	{
		var list = new List<nint>();
		var me = (uint)Environment.ProcessId;
		EnumWindows((h, _) =>
		{
			GetWindowThreadProcessId(h, out var pid);
			if (pid == me)
			{
				list.Add(h);
			}

			return true;
		}, 0);
		return list;
	}

	private static string ClassName(nint hwnd)
	{
		var buffer = new char[256];
		var n = GetClassNameW(hwnd, buffer, buffer.Length);
		return new string(buffer, 0, Math.Max(0, n));
	}

	private delegate bool EnumProc(nint hwnd, nint lParam);

	[DllImport("user32.dll")]
	private static extern bool EnumWindows(EnumProc proc, nint lParam);

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern int GetClassNameW(nint hwnd, char[] buffer, int size);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	/// <summary>Style variants of the controls window, each measured: see-through in the middle? a frame at the edges?</summary>
	private static async Task<int> FrameExperiment(MpvControlsOverlay overlay)
	{
		var h = overlay.Handle;
		void Measure(string variant)
		{
			Shots.GetWindowRect(h, out var r);
			int w = r.Right - r.Left, ht = r.Bottom - r.Top;
			var b = WindowCapture.OverColor(h, new RectInt32(r.Left, r.Top, w, ht), 0x000000);
			var wh = WindowCapture.OverColor(h, new RectInt32(r.Left, r.Top, w, ht), 0xFFFFFF);
			if (b is null || wh is null)
			{
				Log($"{variant}: no capture");
				return;
			}

			var a = Shots.AlphaFrom(b, wh);
			Log($"{variant}: style 0x{GetWindowLongPtrW(h, -16):x8} ex 0x{GetWindowLongPtrW(h, -20):x8} rect {w}x{ht}; middle alpha {Shots.MeanAlpha(a, w / 4, ht / 3, w * 3 / 4, ht * 2 / 3):0.0}, bottom {Shots.MeanAlpha(a, 4, ht - 12, w - 4, ht - 4):0.0}, x0 {Shots.MeanAlpha(a, 0, ht / 3, 1, ht * 2 / 3):0.0}, x1 {Shots.MeanAlpha(a, 1, ht / 3, 2, ht * 2 / 3):0.0}, x2 {Shots.MeanAlpha(a, 2, ht / 3, 3, ht * 2 / 3):0.0}, x3 {Shots.MeanAlpha(a, 3, ht / 3, 4, ht * 2 / 3):0.0}, xw-1 {Shots.MeanAlpha(a, w - 1, ht / 3, w, ht * 2 / 3):0.0}, xw-2 {Shots.MeanAlpha(a, w - 2, ht / 3, w - 1, ht * 2 / 3):0.0}, y0 {Shots.MeanAlpha(a, w / 3, 0, w * 2 / 3, 1):0.0}, yh-1 {Shots.MeanAlpha(a, w / 3, ht - 1, w * 2 / 3, ht):0.0}");
		}

		Measure("as built");
		{
			Shots.GetWindowRect(h, out var wr);
			GetClientRect(h, out var cr);
			var o = new POINT();
			ClientToScreen(h, ref o);
			Log($"window {wr.Left},{wr.Top}-{wr.Right},{wr.Bottom}, client origin {o.X},{o.Y} size {cr.Right}x{cr.Bottom}; DWMWA_EXTENDED_FRAME_BOUNDS {ExtendedFrame(h)}");
		}
		var style = GetWindowLongPtrW(h, -16);
		SetWindowLongPtrW(h, -16, (nint)((long)style & ~0x00400000L & ~0x00080000L));
		SetWindowPos(h, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
		await Task.Delay(500);
		Measure("no WS_DLGFRAME/WS_SYSMENU");
		SetWindowLongPtrW(h, -16, style);
		SetWindowPos(h, 0, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
		await Task.Delay(500);
		Measure("style back");
		var margins = new MARGINS { Left = 0, Right = 0, Top = 0, Bottom = 0 };
		Log($"DwmExtendFrameIntoClientArea(0) = 0x{DwmExtendFrameIntoClientArea(h, ref margins):x8}");
		await Task.Delay(500);
		Measure("frame not extended");
		margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
		DwmExtendFrameIntoClientArea(h, ref margins);
		await Task.Delay(300);
		Shots.GetWindowRect(h, out var rr);
		var region = CreateRectRgn(3, 3, rr.Right - rr.Left - 3, rr.Bottom - rr.Top - 3);
		Log($"SetWindowRgn = {SetWindowRgn(h, region, true)}");
		await Task.Delay(500);
		Measure("region inset 3 px");
		return 0;
	}

	private static string ExtendedFrame(nint hwnd) { DwmGetWindowAttribute(hwnd, 9, out var r, 16); return $"{r.Left},{r.Top}-{r.Right},{r.Bottom}"; }

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmGetWindowAttribute(nint hwnd, int attr, out Shots.RECT value, int size);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetClientRect(nint hwnd, out Shots.RECT rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ClientToScreen(nint hwnd, ref POINT point);

	[StructLayout(LayoutKind.Sequential)]
	private struct MARGINS
	{
		public int Left, Right, Top, Bottom;
	}

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

	[LibraryImport("gdi32.dll")]
	private static partial nint CreateRectRgn(int l, int t, int r, int b);

	[LibraryImport("user32.dll")]
	private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

	[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

	[LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

	private static async Task Picture(Window main, MpvControlsOverlay overlay, FrameworkElement content, string name)
	{
		var dir = Path.Combine(s_out, "shots");
		var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(main);
		var under = Shots.Print(mainHwnd);
		if (under is null)
		{
			Check(false, $"{name}: PrintWindow of the main window");
			return;
		}

		await Shots.Save(under, Path.Combine(dir, $"{name}-main.png"));
		Shots.GetWindowRect(mainHwnd, out var mainRect);
		if (overlay.Placement is not { } placed || !IsWindowVisible(overlay.Handle))
		{
			await Shots.Save(under, Path.Combine(dir, $"{name}.png"));
			return;
		}

		var lit = Shots.PercentLit(under, placed.X - mainRect.Left, placed.Y - mainRect.Top, placed.X - mainRect.Left + placed.Width, placed.Y - mainRect.Top + placed.Height / 2);
		Check(lit > 50, $"{name}: the video draws in mpv's window under the controls ({lit}% of the upper half not black)");

		// the controls window alone, as DWM composes it, over black and over white: the difference is its alpha
		var overBlack = WindowCapture.OverColor(overlay.Handle, placed, 0x000000);
		var overWhite = WindowCapture.OverColor(overlay.Handle, placed, 0xFFFFFF);
		if (overBlack is null || overWhite is null || overBlack.Width != overWhite.Width || overBlack.Height != overWhite.Height)
		{
			Check(false, $"{name}: the controls window could be composed on its own");
			return;
		}

		var own = Shots.AlphaFrom(overBlack, overWhite);
		await Shots.Save(overWhite, Path.Combine(dir, $"{name}-controls-window-over-white.png"));
		await Shots.Save(overBlack, Path.Combine(dir, $"{name}-controls-window-over-black.png"));
		string Px(Shots.Image i, int x, int y) { var (b, g, r, _) = i.At(x, y); return $"{r},{g},{b}"; }
		var midY = overBlack.Height / 2;
		Log($"{name}: edge pixels over black / white: x0 {Px(overBlack, 0, midY)} / {Px(overWhite, 0, midY)}, x1 {Px(overBlack, 1, midY)} / {Px(overWhite, 1, midY)}, x2 {Px(overBlack, 2, midY)} / {Px(overWhite, 2, midY)}, xw-1 {Px(overBlack, overBlack.Width - 1, midY)} / {Px(overWhite, overWhite.Width - 1, midY)}, y0 {Px(overBlack, overBlack.Width / 2, 0)} / {Px(overWhite, overWhite.Width / 2, 0)}");
		var clear = Shots.MeanAlpha(own, own.Width / 4, own.Height / 3, own.Width * 3 / 4, own.Height * 2 / 3);
		var bar = Shots.MeanAlpha(own, 4, own.Height - 12, own.Width - 4, own.Height - 4);
		Check(clear < 3, $"{name}: the controls window is see-through where the controls draw nothing (mean alpha {clear:0.0} of 255 in the middle)");
		Check(bar > 40, $"{name}: and draws its bottom gradient (mean alpha {bar:0.0} of 255 along the bottom)");
		var left = Shots.MeanAlpha(own, 0, own.Height / 3, 1, own.Height * 2 / 3);
		var right = Shots.MeanAlpha(own, own.Width - 1, own.Height / 3, own.Width, own.Height * 2 / 3);
		var inner = Shots.MeanAlpha(own, 1, own.Height / 3, 2, own.Height * 2 / 3);
		Log($"{name}: alpha at x=0 {left:0.0}, x=1 {inner:0.0}, x=w-1 {right:0.0}");
		Check(Math.Max(left, right) < 3, $"{name}: no frame at its left and right edges (mean alpha {left:0.0} / {right:0.0})");

		// what the user sees: the main window with the controls window composed over it, both as DWM has them
		var composed = Shots.Compose(under, own, placed.X - mainRect.Left, placed.Y - mainRect.Top);
		await Shots.Save(composed, Path.Combine(dir, $"{name}.png"));
	}

	/// <summary>Spike content: a top title and a bottom bar over gradients, nothing in between (the video shows through).</summary>
	private static FrameworkElement SpikeContent()
	{
		var root = new Grid { Background = new SolidColorBrush(Colors.Transparent), RequestedTheme = ElementTheme.Dark };
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		var top = new Grid { Padding = new Thickness(24, 16, 24, 40), Background = Gradient(top: true) };
		top.Children.Add(new TextBlock { Text = "Series \u2014 S6E4 \u00B7 Episode title", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) });
		root.Children.Add(top);

		var bottom = new StackPanel { Padding = new Thickness(24, 48, 24, 16), Background = Gradient(top: false), Spacing = 4 };
		bottom.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 35 });
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
		row.Children.Add(new Button { Content = new FontIcon { Glyph = "\uE769", FontSize = 22 }, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) });
		row.Children.Add(new TextBlock { Text = "00:21 / 01:00", VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.White) });
		bottom.Children.Add(row);
		Grid.SetRow(bottom, 2);
		root.Children.Add(bottom);
		return root;
	}

	private static LinearGradientBrush Gradient(bool top)
	{
		var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, top ? 0 : 1), EndPoint = new Windows.Foundation.Point(0, top ? 1 : 0) };
		brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xB0, 0, 0, 0), Offset = 0 });
		brush.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 1 });
		return brush;
	}

	private static string Describe(RectInt32? r) => r is { } v ? $"{v.X},{v.Y} {v.Width}x{v.Height}" : "hidden";

	private static Task WhenLoaded(FrameworkElement element)
	{
		if (element.IsLoaded)
		{
			return Task.CompletedTask;
		}

		var done = new TaskCompletionSource();
		element.Loaded += (_, _) => done.TrySetResult();
		return done.Task;
	}

	private static async Task<bool> Until(Func<bool> condition, double seconds)
	{
		var end = DateTime.UtcNow.AddSeconds(seconds);
		while (DateTime.UtcNow < end)
		{
			if (condition())
			{
				return true;
			}

			await Task.Delay(50);
		}

		return condition();
	}

	private static nint MpvChild(nint host) => host == 0 ? 0 : FindWindowExW(host, 0, "mpv", null);

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X, Y;
	}

	private const uint GW_OWNER = 4;

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsWindowVisible(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsChild(nint parent, nint hwnd);

	[LibraryImport("user32.dll")]
	private static partial nint GetWindow(nint hwnd, uint command);

	[LibraryImport("user32.dll")]
	private static partial nint WindowFromPoint(POINT point);

	[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static partial nint GetWindowLongPtrW(nint hwnd, int index);

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);
}

internal sealed class Launcher(string python, string configDir, string logFile) : IMpvBackendLauncher
{
	public MpvBackendCommand? Resolve(bool dev, out string searched)
	{
		searched = python;
		var args = new List<string> { "-I", "-m", MpvBackendCommand.BridgeModule, "--config-dir", configDir, "--log-file", logFile };
		if (dev)
		{
			args.Add("--dev");
		}

		return new MpvBackendCommand(python, args, "ui-harness-controls");
	}

	public IMpvBackendProcess Start(MpvBackendCommand command) => WindowsMpvBackendLauncher.StartInJob(command.FileName, command.Arguments, configDir);
}

internal sealed class FileLogger<T>(string path) : ILogger<T>
{
	private readonly object _lock = new();

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		lock (_lock)
		{
			File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {logLevel}: {BridgeRedaction.Redact(formatter(state, exception))}{(exception is null ? "" : " | " + exception.GetType().Name + ": " + BridgeRedaction.Redact(exception.Message))}{Environment.NewLine}", new UTF8Encoding(false));
		}
	}
}
