using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FluentFin.Core.Mpv;
using FluentFin.Mpv;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace CompositionSpike;

/// <summary>
/// Phase 2a spike (docs/research/player-controls.md): libmpv in this process, rendering in composition mode into a XAML
/// SwapChainPanel with XAML drawn over it, or (for comparison) into a child window with <c>wid</c>, the same libmpv build.
/// Never Nixie.exe. One scenario per run; CHECK lines and STAT lines to host.txt; pictures of its own window only.
///
///   CompositionSpike.exe --out DIR --mode composition|wid --scenario local|hdr|bridge|quit --clip FILE
///                        [--python PY --fakejf2 DIR --backend DIR]  (bridge: the backend folder to run the bridge from)
/// </summary>
internal static partial class Host
{
	private static readonly object s_logLock = new();
	private static string s_out = Path.GetTempPath();
	private static readonly Stopwatch s_clock = Stopwatch.StartNew();
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
		main.DispatcherQueue.TryEnqueue(async () =>
		{
			var code = 1;
			try
			{
				code = await Run(main);
			}
			catch (Exception ex)
			{
				Log($"FAILED {ex}");
			}

			Log($"RESULT: {(code == 0 && s_failures == 0 ? "ok" : $"FAIL ({s_failures} check(s) failed)")}");
			Environment.Exit(code == 0 && s_failures == 0 ? 0 : 1);
		});
	}

	private sealed class Page
	{
		public required Grid Root { get; init; }
		public required SwapChainPanel Panel { get; init; }
		public required Border Bar { get; init; }
		public required TextBlock Status { get; init; }
	}

	private static Page BuildPage(Window main)
	{
		var root = new Grid { Background = new SolidColorBrush(Colors.Black), RequestedTheme = ElementTheme.Dark };
		var panel = new SwapChainPanel();
		root.Children.Add(panel);

		// XAML over the video, in the same tree: the whole point of composition mode
		var bar = new Border
		{
			VerticalAlignment = VerticalAlignment.Bottom,
			Height = 96,
			Padding = new Thickness(24, 40, 24, 12),
		};
		var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 1), EndPoint = new Windows.Foundation.Point(0, 0) };
		gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xC0, 0, 0, 0), Offset = 0 });
		gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0, 0, 0, 0), Offset = 1 });
		bar.Background = gradient;
		var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
		row.Children.Add(new FontIcon { Glyph = "\uE769", FontSize = 24, Foreground = new SolidColorBrush(Colors.White) });
		var status = new TextBlock { Text = "XAML over the video", Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Center };
		row.Children.Add(status);
		bar.Child = row;
		root.Children.Add(bar);
		main.Content = root;
		return new Page { Root = root, Panel = panel, Bar = bar, Status = status };
	}

	private static async Task<int> Run(Window main)
	{
		var mode = Arg("--mode", "composition");
		var scenario = Arg("--scenario", "local");
		var clip = Path.GetFullPath(Arg("--clip"));
		Log($"mode {mode}, scenario {scenario}, clip {Path.GetFileName(clip)}, libmpv client API {Libmpv.mpv_client_api_version() >> 16}.{Libmpv.mpv_client_api_version() & 0xffff}");
		foreach (var line in Dxgi.Displays())
		{
			Log($"display: {line}");
		}

		var page = BuildPage(main);
		main.AppWindow.MoveAndResize(new RectInt32(60, 60, 1280, 760));
		main.Activate();
		await WhenLoaded(page.Panel);

		var composition = mode == "composition";
		nint childWindow = 0;
		if (!composition)
		{
			childWindow = ChildWindow.Create(WinRT.Interop.WindowNative.GetWindowHandle(main));
		}

		var pipe = $"fluentfin-mpv-spike-{Environment.ProcessId}";
		var options = new List<(string, string)>
		{
			("ao", "null"),                              // tests make no sound
			("vo", "gpu-next"),
			("gpu-context", "d3d11"),
			("hwdec", "d3d11va"),
			("idle", "yes"),
			("keep-open", "no"),
			("osc", "no"),
			("input-default-bindings", "no"),
			("input-vo-keyboard", "no"),
			("terminal", "no"),
			("config", "no"),
			("audio-display", "no"),
			("input-ipc-server", @"\\.\pipe\" + pipe),
		};
		if (composition)
		{
			options.Add(("d3d11-output-mode", "composition"));
			options.Add(("force-window", "yes"));
		}
		else
		{
			options.Add(("wid", ((uint)childWindow).ToString(CultureInfo.InvariantCulture)));
			options.Add(("force-window", "yes"));
		}

		if (scenario == "hdr")
		{
			// composition: mpv cannot see the display (PR #16795), so HDR output is asked for, not detected
			options.Add(("target-colorspace-hint", "yes"));
			options.Add(("target-trc", "pq"));
			options.Add(("target-prim", "bt.2020"));
			options.Add(("d3d11-output-format", "rgb10_a2"));
		}

		var started = Stopwatch.StartNew();
		using var core = new Core(options, Path.Combine(s_out, $"mpv-{mode}-{scenario}.log"));
		Log($"core initialized in {started.ElapsedMilliseconds} ms");

		var binder = new SwapChainBinder(main, page.Panel, core);
		if (composition)
		{
			binder.Start();
		}
		else
		{
			ChildWindow.Place(childWindow, page.Panel);
			page.Panel.SizeChanged += (_, _) => ChildWindow.Place(childWindow, page.Panel);
		}

		try
		{
			return scenario switch
			{
				"local" => await Local(main, page, core, binder, clip, composition, childWindow),
				"hdr" => await Hdr(main, page, core, binder, clip, composition),
				"bridge" => await BridgeAttached(main, page, core, binder, clip, pipe, composition),
				"quit" => await QuitFromIpc(main, page, core, binder, clip),
				"repeat" => await Repeat(main, page, core, binder, clip),
				_ => throw new ArgumentException($"unknown scenario {scenario}"),
			};
		}
		finally
		{
			binder.Stop();
		}
	}

	// -- scenarios --------------------------------------------------------------------------------------------------------

	/// <summary>
	/// Does display-swapchain arrive every time? Ten fresh cores in this process, one after the other, each into the same
	/// panel: each must bind its swap chain and play (Mio treats more than 2 s as an error).
	/// </summary>
	private static async Task<int> Repeat(Window main, Page page, Core first, SwapChainBinder firstBinder, string clip)
	{
		firstBinder.Stop();
		var times = new List<long>();
		for (var i = 0; i < 10; i++)
		{
			var started = Stopwatch.StartNew();
			using var core = new Core([("ao", "null"), ("vo", "gpu-next"), ("gpu-context", "d3d11"), ("d3d11-output-mode", "composition"), ("hwdec", "d3d11va"), ("idle", "yes"), ("force-window", "yes"), ("config", "no"), ("terminal", "no"), ("input-default-bindings", "no")], null);
			var binder = new SwapChainBinder(main, page.Panel, core);
			binder.Start();
			try
			{
				core.Command("loadfile", clip);
				var bound = await Until(() => binder.Bound != 0, 5);
				var ms = started.ElapsedMilliseconds;
				var plays = await Until(() => core.Double("playback-time") > 0.3, 10);
				Check(bound && plays && ms < 2000, $"core {i + 1}: swap chain bound {ms} ms after the core was made, and it plays");
				times.Add(ms);
			}
			finally
			{
				binder.Stop();
			}
		}

		times.Sort();
		Log($"STAT swap chain arrival over {times.Count} cores: min {times[0]} ms, median {times[times.Count / 2]} ms, max {times[^1]} ms");
		await Picture(main, page, firstBinder, "repeat-last");
		return 0;
	}

	private static async Task<int> Local(Window main, Page page, Core core, SwapChainBinder binder, string clip, bool composition, nint child)
	{
		var loaded = Stopwatch.StartNew();
		Check(core.Command("loadfile", clip) >= 0, "loadfile accepted");
		if (composition)
		{
			Check(await Until(() => binder.Bound != 0, 5), $"display-swapchain appeared and was bound ({loaded.ElapsedMilliseconds} ms after loadfile; Mio treats > 2 s as an error)");
			Log($"swap chain first seen {binder.FirstSeenMs} ms after the core started; times changed: {binder.Changes}");
		}

		Check(await Until(() => core.Double("playback-time") > 1, 15), "playback runs");
		await Task.Delay(1000);
		LogVideo(core, binder);
		await Picture(main, page, binder, "playing");
		await Stats(core, "windowed", 20);

		if (composition)
		{
			main.AppWindow.Resize(new SizeInt32(1600, 900));
			await Task.Delay(1200);
			CheckSize(page, binder, "resized");
			await Picture(main, page, binder, "resized");

			main.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
			await Task.Delay(1500);
			CheckSize(page, binder, "full screen");
			await Picture(main, page, binder, "fullscreen");
			await Stats(core, "fullscreen", 15);
			main.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
			await Task.Delay(1200);
			CheckSize(page, binder, "back to windowed");

			// a few quick resizes in a row: the size mpv renders at must end equal to the panel's
			for (var i = 0; i < 6; i++)
			{
				main.AppWindow.Resize(new SizeInt32(900 + 97 * i, 520 + 61 * i));
				await Task.Delay(120);
			}

			await Task.Delay(1500);
			CheckSize(page, binder, "after 6 quick resizes");
			Check(binder.Bound != 0 && core.Int64("display-swapchain") == binder.Bound, "the panel still shows mpv's current swap chain");
		}

		Check(!core.ShutDown.IsSet, "the core is still up");
		return 0;
	}

	private static async Task<int> Hdr(Window main, Page page, Core core, SwapChainBinder binder, string clip, bool composition)
	{
		Check(core.Command("loadfile", clip) >= 0, "loadfile accepted");
		Check(await Until(() => core.Double("playback-time") > 1, 20), "playback runs");
		await Task.Delay(1500);
		LogVideo(core, binder);
		foreach (var line in core.LogLinesWith("colorspace").Concat(core.LogLinesWith("color space")).Concat(core.LogLinesWith("swapchain")).Concat(core.LogLinesWith("swap chain")).Distinct().Take(40))
		{
			Log($"mpv: {line}");
		}

		if (binder.Bound != 0 && Dxgi.Describe((nint)binder.Bound) is { } desc)
		{
			Check(desc.Format == 24, $"HDR: mpv's swap chain is 10-bit ({Dxgi.FormatName(desc.Format)})");
		}

		await Picture(main, page, binder, "hdr");
		await Stats(core, "hdr", 15);
		return 0;
	}

	private static async Task<int> QuitFromIpc(Window main, Page page, Core core, SwapChainBinder binder, string clip)
	{
		// a client on the IPC pipe (the shim, if any path of it ever did) sends quit: the core ends inside this process
		core.Command("loadfile", clip);
		await Until(() => core.Double("playback-time") > 1, 15);
		var answer = core.Command("quit");
		Log($"quit sent: {answer}");
		Check(await Until(() => core.ShutDown.IsSet, 5), "a quit ends the in-process core (SHUTDOWN event)");
		await Task.Delay(500);
		Check(true, "the host process survives it (this line is written after the core ended)");

		// and a new core can be made in the same process, bound to the same panel (what a restart after a lost core does)
		binder.Stop();
		using var second = new Core([("ao", "null"), ("vo", "gpu-next"), ("gpu-context", "d3d11"), ("d3d11-output-mode", "composition"), ("hwdec", "d3d11va"), ("idle", "yes"), ("force-window", "yes"), ("config", "no"), ("terminal", "no"), ("input-default-bindings", "no")],
			Path.Combine(s_out, "mpv-second-core.log"));
		var secondBinder = new SwapChainBinder(main, page.Panel, second);
		secondBinder.Start();
		try
		{
			Check(second.Command("loadfile", clip) >= 0, "the second core takes a loadfile");
			Check(await Until(() => secondBinder.Bound != 0, 5), "the second core's swap chain is bound to the same panel");
			Check(await Until(() => second.Double("playback-time") > 0.5, 15), $"a second core plays in the same process afterwards ({second.Double("playback-time"):0.0} s)");
			await Picture(main, page, secondBinder, "second-core");
		}
		finally
		{
			secondBinder.Stop();
		}

		return 0;
	}

	/// <summary>
	/// The shim attached to this process's core: the real bridge (FluentFin.Core's MpvBackendHost and the job launcher)
	/// connected with the protocol's attached window (docs/BRIDGE.md §4: the shim's external backend, mpv_ext_start=false,
	/// on this core's pipe; conf.json says nothing about it), against a fake server serving the clip.
	/// </summary>
	private static async Task<int> BridgeAttached(Window main, Page page, Core core, SwapChainBinder binder, string clip, string pipe, bool composition)
	{
		var python = Arg("--python");
		var fakejf2 = Arg("--fakejf2");
		using var server = FakeServer.Start(python, Path.Combine(AppContext.BaseDirectory, "fakeserver.py"), fakejf2, clip);
		Log($"fake server at {server.Url} (fake token)");

		var configDir = Path.Combine(s_out, "backend-config");
		Directory.CreateDirectory(configDir);
		File.WriteAllText(Path.Combine(configDir, "mpv.conf"), "ao=null\n");

		var events = new List<BridgeEvent>();
		var backend = new MpvBackendHost(new Launcher(python, Path.GetFullPath(Arg("--backend")), configDir, Path.Combine(s_out, "backend.log")),
			new AttachedSurface(pipe), new FileLogger<MpvBackendHost>(Path.Combine(s_out, "backend-host.log")));
		backend.Events.Subscribe(e =>
		{
			lock (events)
			{
				events.Add(e);
			}

			if (e.Name != Bridge.Events.Position)
			{
				Log($"bridge event {e.Name}{(e.Name == Bridge.Events.State ? " " + e.DataAs<BridgeState>()?.State : "")}");
			}
		});

		BridgeState? Last() { lock (events) { return events.LastOrDefault(e => e.Name == Bridge.Events.State)?.DataAs<BridgeState>(); } }
		int Positions() { lock (events) { return events.Count(e => e.Name == Bridge.Events.Position); } }

		try
		{
			var connectTimer = Stopwatch.StartNew();
			await backend.StartAsync();
			var mpvVersion = await backend.ConnectAsync(new BridgeIdentity(server.Url, "srv", "Fake", server.UserId, "u", server.Token, "spike-device", "Spike", "FluentFin", "0.0.1"));
			Log($"connected in {connectTimer.ElapsedMilliseconds} ms; the shim reports mpv {mpvVersion} ({backend.Status.MpvKind})");
			Check(backend.Status.MpvKind == "external", "the shim talks to mpv over IPC (its external backend)");
			Check(mpvVersion is not null && mpvVersion == core.String("mpv-version"), "and the mpv it found is this process's core");

			await backend.SendAsync(Bridge.Commands.SetControls, BridgeCommands.Controls(ui: true, 0.15));
			await backend.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest([FakeServer.ItemId])));
			Check(await Until(() => Last()?.State == Bridge.States.Playing, 40), "the shim plays the item in the attached core");
			Check(Last()?.Window == "attached", $"the backend's state says the window is attached ({Last()?.Window})");
			if (composition)
			{
				Check(await Until(() => binder.Bound != 0, 5), "the composition swap chain is bound");
			}

			Check(await Until(() => Positions() >= 4, 15), "position events flow");
			Log($"the core plays: {core.String("path")?.Split('?')[0]}");
			LogVideo(core, binder);
			// mpv answers sub-pos as a float string ("85.000000"): compared as a number
			Check(await Until(() => core.Double("sub-pos") is double subPos && Math.Abs(subPos - 85) < 0.01, 5), $"set_controls ui reached the core: sub-pos {core.Double("sub-pos")}");
			await Picture(main, page, binder, "bridge-playing");

			await backend.SendAsync(Bridge.Commands.Pause);
			Check(await Until(() => Last()?.State == Bridge.States.Paused, 5), "pause through the bridge");
			Check(core.String("pause") == "yes", "the core is paused");
			await backend.SendAsync(Bridge.Commands.Seek, BridgeCommands.Seek(TimeSpan.FromSeconds(30).Ticks));
			await backend.SendAsync(Bridge.Commands.Unpause);
			Check(await Until(() => core.Double("playback-time") is > 29 and < 40, 8), $"seek through the bridge lands ({core.Double("playback-time"):0.0} s)");
			await Stats(core, "bridge", 12);

			await backend.SendAsync(Bridge.Commands.Stop);
			Check(await Until(() => Last()?.State == Bridge.States.Idle, 10), "stop: idle");
			var stopped = server.Stopped();
			Check(stopped >= 1, $"the fake server got the Stopped report ({stopped})");
		}
		finally
		{
			await backend.ShutdownAsync();
		}

		await Task.Delay(1000);
		Check(!core.ShutDown.IsSet, "the shim's shutdown leaves the in-process core running (it only closes its pipe)");
		Check(core.Command("loadfile", clip) >= 0 && await Until(() => core.Double("playback-time") > 0.5, 15), "and the core still plays");
		return 0;
	}

	// -- measurements -----------------------------------------------------------------------------------------------------

	private static void LogVideo(Core core, SwapChainBinder binder)
	{
		Log($"STAT video: hwdec-current={core.String("hwdec-current")}, current-vo={core.String("current-vo")}, gpu-context={core.String("current-gpu-context")}, " +
			$"video-params={core.String("video-params/pixelformat")} {core.String("video-params/w")}x{core.String("video-params/h")} {core.String("video-params/primaries")}/{core.String("video-params/gamma")}, " +
			$"video-target-params={core.String("video-target-params/primaries")}/{core.String("video-target-params/gamma")}, container-fps={core.Double("container-fps"):0.###}");
		if (binder.Bound != 0 && Dxgi.Describe((nint)binder.Bound) is { } d)
		{
			Log($"STAT swap chain: {d.Width}x{d.Height} {Dxgi.FormatName(d.Format)}, {d.BufferCount} buffers, swap effect {d.SwapEffect}, alpha mode {d.AlphaMode}, scaling {d.Scaling}, flags 0x{d.Flags:x}");
		}
	}

	private static async Task Stats(Core core, string label, int seconds)
	{
		long Get(string name) => core.Int64(name) ?? -1;
		var drop0 = Get("frame-drop-count");
		var dec0 = Get("decoder-frame-drop-count");
		var late0 = Get("vo-delayed-frame-count");
		var mis0 = Get("mistimed-frame-count");
		var t0 = core.Double("playback-time") ?? 0;
		await Task.Delay(seconds * 1000);
		var played = (core.Double("playback-time") ?? 0) - t0;
		Log(string.Create(CultureInfo.InvariantCulture,
			$"STAT pacing {label}: {played:0.0} s played in {seconds} s; frame drops {Get("frame-drop-count") - drop0}, decoder drops {Get("decoder-frame-drop-count") - dec0}, " +
			$"VO delayed {Get("vo-delayed-frame-count") - late0}, mistimed {Get("mistimed-frame-count") - mis0}; estimated-vf-fps {core.Double("estimated-vf-fps"):0.##}, " +
			$"display-fps {core.Double("display-fps"):0.##}, estimated-display-fps {core.Double("estimated-display-fps"):0.##}, vsync-jitter {core.Double("vsync-jitter"):0.###}, video-sync {core.String("video-sync")}"));
	}

	private static void CheckSize(Page page, SwapChainBinder binder, string when)
	{
		var want = binder.PhysicalSize();
		var desc = binder.Bound != 0 ? Dxgi.Describe((nint)binder.Bound) : null;
		Log($"{when}: panel {want.Width}x{want.Height} physical px, swap chain {desc?.Width}x{desc?.Height}, composition size sent {binder.LastSizeSent}");
		Check(desc is { } d && d.Width == want.Width && d.Height == want.Height, $"{when}: mpv renders at the panel's physical size");
	}

	private static async Task Picture(Window main, Page page, SwapChainBinder binder, string name)
	{
		var image = ControlsHost.Shots.Print(WinRT.Interop.WindowNative.GetWindowHandle(main));
		if (image is null)
		{
			Log($"{name}: PrintWindow failed");
			return;
		}

		await ControlsHost.Shots.Save(image, Path.Combine(s_out, "shots", $"{name}.png"));
		var lit = ControlsHost.Shots.PercentLit(image, 8, 40, image.Width - 8, image.Height / 2);
		Log($"{name}: picture saved, {lit}% of the upper half lit");
	}

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
}

/// <summary>
/// Hands mpv's composition swap chain to the panel, on the UI thread, every time display-swapchain changes; tells mpv
/// the panel's size in physical pixels (d3d11-composition-size) on every size or scale change.
/// </summary>
internal sealed class SwapChainBinder(Window main, SwapChainPanel panel, Core core)
{
	private readonly Stopwatch _since = Stopwatch.StartNew();
	private volatile int _changes;

	public long Bound { get; private set; }

	public long FirstSeenMs { get; private set; } = -1;

	public int Changes => _changes;

	public string LastSizeSent { get; private set; } = "";

	public void Start()
	{
		core.SwapChainChanged += OnSwapChain;
		panel.SizeChanged += OnSize;
		panel.CompositionScaleChanged += OnScale;
		SendSize();
		if (core.Int64("display-swapchain") is { } existing and not 0)
		{
			OnSwapChain(existing);
		}
	}

	public void Stop()
	{
		core.SwapChainChanged -= OnSwapChain;
		panel.SizeChanged -= OnSize;
		panel.CompositionScaleChanged -= OnScale;
	}

	public SizeInt32 PhysicalSize() => new(
		(int)Math.Round(panel.ActualWidth * panel.CompositionScaleX),
		(int)Math.Round(panel.ActualHeight * panel.CompositionScaleY));

	private void OnSize(object sender, SizeChangedEventArgs e) => SendSize();

	private void OnScale(SwapChainPanel sender, object args)
	{
		SendSize();
		if (Bound != 0 && (sender.CompositionScaleX != 1 || sender.CompositionScaleY != 1))
		{
			Host.Log($"scale {sender.CompositionScaleX}: inverse transform 0x{Dxgi.SetInverseScale((nint)Bound, sender.CompositionScaleX, sender.CompositionScaleY):x8}");
		}
	}

	private void SendSize()
	{
		var size = PhysicalSize();
		if (size.Width < 1 || size.Height < 1)
		{
			return;
		}

		var value = $"{size.Width}x{size.Height}";
		if (value == LastSizeSent)
		{
			return;
		}

		LastSizeSent = value;
		var rc = core.Set("d3d11-composition-size", value);
		if (rc < 0)
		{
			Host.Log($"d3d11-composition-size={value}: {Libmpv.Error(rc)}");
		}
	}

	private void OnSwapChain(long pointer)
	{
		_changes++;
		if (pointer != 0 && FirstSeenMs < 0)
		{
			FirstSeenMs = _since.ElapsedMilliseconds;
		}

		main.DispatcherQueue.TryEnqueue(() =>
		{
			var hr = Dxgi.Bind(panel, (nint)pointer);
			Host.Log($"display-swapchain 0x{pointer:x} bound: hr 0x{hr:x8}");
			Bound = hr >= 0 ? pointer : 0;
		});
	}
}

/// <summary>For the wid comparison: a plain child window over the panel, as FluentFin's video window is today.</summary>
internal static partial class ChildWindow
{
	public static nint Create(nint parent) => CreateWindowExW(0, "STATIC", "spike video", 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000, 0, 0, 16, 16, parent, 0, 0, 0);

	public static void Place(nint child, FrameworkElement target)
	{
		var scale = target.XamlRoot?.RasterizationScale ?? 1;
		var at = target.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
		SetWindowPos(child, 0, (int)Math.Round(at.X * scale), (int)Math.Round(at.Y * scale), (int)Math.Round(target.ActualWidth * scale), (int)Math.Round(target.ActualHeight * scale), 0x0010 | 0x0040);
	}

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}

/// <summary>
/// The bridge from <paramref name="backendDir"/> (this checkout's backend): <c>-I -c</c> with that folder first on sys.path,
/// since a <c>-m</c> run would import the venv's editable install, which is the main repository's backend.
/// </summary>
internal sealed class Launcher(string python, string backendDir, string configDir, string logFile) : IMpvBackendLauncher
{
	public MpvBackendCommand? Resolve(bool dev, out string searched)
	{
		searched = python;
		var argv = new List<string> { "--config-dir", configDir, "--log-file", logFile };
		if (dev)
		{
			argv.Add("--dev");
		}

		static string Py(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
		var boot = $"import sys; sys.path.insert(0, {Py(backendDir)}); from jellyfin_mpv_shim.bridge import main; sys.exit(main([{string.Join(", ", argv.Select(Py))}]))";
		return new MpvBackendCommand(python, ["-I", "-c", boot], "ui-harness-composition-spike");
	}

	public IMpvBackendProcess Start(MpvBackendCommand command) => WindowsMpvBackendLauncher.StartInJob(command.FileName, command.Arguments, configDir);
}

/// <summary>The protocol's attached window: this process's core, on its pipe.</summary>
internal sealed class AttachedSurface(string pipe) : IMpvVideoSurface
{
	public Task<nint> GetHandleAsync() => throw new NotSupportedException("attached: there is no window");

	public Task<BridgeWindow> GetWindowAsync() => Task.FromResult(BridgeWindow.Attached(pipe));
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

/// <summary>
/// tools\backend-harness\rr\fakejf2.py serving the clip, started by this host (its own Process object, killed at the
/// end). A fresh fake token goes to it over stdin, never argv or the environment.
/// </summary>
internal sealed class FakeServer : IDisposable
{
	public const string ItemId = "11111111111111111111111111111111";

	private readonly Process _process;

	private FakeServer(Process process, string url, string userId, string token)
	{
		_process = process;
		Url = url;
		UserId = userId;
		Token = token;
	}

	public string Url { get; }
	public string UserId { get; }
	public string Token { get; }

	public static FakeServer Start(string python, string script, string fakejf2Dir, string clip)
	{
		var token = "fake-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
		var start = new ProcessStartInfo(python) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
		foreach (var a in new[] { "-B", "-I", script, fakejf2Dir, clip })
		{
			start.ArgumentList.Add(a);
		}

		var process = Process.Start(start) ?? throw new InvalidOperationException("fake server did not start");
		process.StandardInput.WriteLine(token);
		process.StandardInput.Flush();
		var ready = process.StandardOutput.ReadLine() ?? "";
		var parts = ready.Split(' ');
		if (parts.Length < 3 || parts[0] != "READY")
		{
			process.Kill();
			throw new InvalidOperationException($"fake server said: {ready}");
		}

		return new FakeServer(process, parts[1], parts[2], token);
	}

	/// <summary>How many Stopped reports the server has accepted (asked over stdin).</summary>
	public int Stopped()
	{
		_process.StandardInput.WriteLine("stopped");
		_process.StandardInput.Flush();
		return int.TryParse(_process.StandardOutput.ReadLine(), out var n) ? n : -1;
	}

	public void Dispose()
	{
		try
		{
			_process.StandardInput.Close();
			if (!_process.WaitForExit(3000))
			{
				_process.Kill();
			}
		}
		catch (Exception)
		{
		}

		_process.Dispose();
	}
}
