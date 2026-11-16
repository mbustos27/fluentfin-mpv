using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using FluentFin.Controls;
using FluentFin.Core.Mpv;
using FluentFin.Mpv;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace FluentFinControlsHost;

/// <summary>
/// Phase 2b harness (docs/research/player-controls.md): FluentFin's own player controls (MpvPlayerControls over
/// MpvControlsViewModel) over FluentFin's in-process mpv (MpvCompositionVideo), with the real backend from this checkout
/// attached to it (docs/BRIDGE.md §4, <c>window.mode = "attached"</c>) against a fake server serving one clip as two
/// episodes. Every action goes the way the player page sends it (the view model, through the bridge); mpv's own state is
/// read back over its IPC pipe. CHECK lines and a RESULT line to host.txt, pictures of its own window to shots\.
///
///   FluentFinControls.exe --out DIR --clip FILE --python PY --fakejf2 DIR --backend DIR
/// </summary>
internal static partial class Host
{
	private static readonly object s_logLock = new();
	private static string s_out = Path.GetTempPath();
	private static readonly Stopwatch s_clock = Stopwatch.StartNew();
	private static int s_failures;

	private const string Episode1 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1";
	private const string Episode2 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2";

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

	private static string Arg(string name)
	{
		var args = Environment.GetCommandLineArgs();
		var i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException($"{name} is required");
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

	private static async Task<int> Run(Window main)
	{
		var python = Arg("--python");
		var clip = Path.GetFullPath(Arg("--clip"));
		using var server = FakeServer.Start(python, Path.Combine(AppContext.BaseDirectory, "fakeserver.py"), Arg("--fakejf2"), clip);
		Log($"fake server at {server.Url} (fake token), clip {Path.GetFileName(clip)}");

		var configDir = Path.Combine(s_out, "backend-config");
		Directory.CreateDirectory(configDir);
		// no sound from any test run: the in-process mpv reads this config dir's mpv.conf (and gets ao=null itself)
		File.WriteAllText(Path.Combine(configDir, "mpv.conf"), "# written by the player controls host: tests make no sound\nao=null\n");

		using var composition = new MpvCompositionVideo(new FileLogger<MpvCompositionVideo>(Path.Combine(s_out, "composition.log")), configDir)
		{
			ExtraOptions = [("ao", "null")],
		};
		var backend = new MpvBackendHost(new Launcher(python, Path.GetFullPath(Arg("--backend")), configDir, Path.Combine(s_out, "backend.log")),
			new AttachedSurface(composition), new FileLogger<MpvBackendHost>(Path.Combine(s_out, "backend-host.log")));

		var controls = new MpvControlsViewModel(async (command, args) =>
		{
			try
			{
				await backend.SendAsync(command, args);
			}
			catch (BridgeException ex)
			{
				Log($"'{command}' was refused: {ex.Message}");
			}
		});
		var presenterChanges = 0;
		controls.SetFullScreen = fullScreen =>
		{
			presenterChanges++;
			main.AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
			controls.IsFullScreen = fullScreen;
			_ = backend.SendAsync(Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(fullScreen));
		};

		var view = new MpvPlayerControls { Controls = controls };
		main.Content = new Grid { Background = new SolidColorBrush(Colors.Black), Children = { view } };
		main.AppWindow.MoveAndResize(new RectInt32(60, 60, 1280, 760));
		main.Activate();
		await WhenLoaded(view);
		composition.Attach(view.VideoSurface);

		var states = new List<BridgeState>();
		var items = new List<BridgeItem>();
		backend.Events.Subscribe(e => main.DispatcherQueue.TryEnqueue(() =>
		{
			if (e.Name == Bridge.Events.State && e.DataAs<BridgeState>() is { } state)
			{
				states.Add(state);
				Log($"bridge state {state.State} {state.ItemId} window={state.Window}");
			}
			else if (e.Name == Bridge.Events.Item && e.DataAs<BridgeItem>() is { } item)
			{
				items.Add(item);
				Log($"bridge item {item.ItemId} {item.Type}");
			}
			else if (e.Name != Bridge.Events.Position)
			{
				Log($"bridge event {e.Name}");
			}

			controls.OnEvent(e);
		}));
		BridgeState? Last() => states.LastOrDefault();

		try
		{
			await backend.StartAsync();
			var version = await backend.ConnectAsync(new BridgeIdentity(server.Url, "srv", "Fake", server.UserId, "u", server.Token, "controls-host-device", "Controls host", "FluentFin", "0.0.1"));
			var window = await composition.GetWindowAsync();
			Log($"connected: mpv {version} ({backend.Status.MpvKind}), window {window.Mode} {window.Ipc}");
			Check(window.Mode == "attached" && backend.Status.MpvKind == "external", "the backend attached to FluentFin's in-process mpv over its pipe (the shim's external backend)");
			using var mpv = MpvIpc.Connect(window.Ipc!);
			Check(Str(mpv.Get("mpv-version")) == version, "and the mpv it talks to is the in-process one");

			await controls.SendControls();
			await backend.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest([Episode1, Episode2])));
			Check(await Until(() => Last() is { State: Bridge.States.Playing, Window: "attached" }, 40), "the item plays in the in-process mpv (state playing, window attached)");
			Check(await Until(() => composition.IsShowingVideo, 5), "mpv's swap chain is shown in the controls' panel");
			Check(await Until(() => (mpv.Number("time-pos") ?? 0) > 1, 15), "the video runs");

			var panel = view.VideoSurface;
			var physical = $"{(int)Math.Round(panel.ActualWidth * panel.CompositionScaleX)}x{(int)Math.Round(panel.ActualHeight * panel.CompositionScaleY)}";
			Check(await Until(() => composition.RenderSize == physical, 3), $"mpv renders at the panel's physical size ({composition.RenderSize} vs {physical})");

			Check(controls.Title == "Spike Show" && controls.Subtitle == "S1E1 · Pilot", $"title from the item event: '{controls.Title}' / '{controls.Subtitle}'");
			Check(Text(view, "TitleText") == "Spike Show" && Text(view, "SubtitleText") == "S1E1 · Pilot", "and the top bar shows it");
			Check(Str(mpv.Get("user-data/osc-visibility")) == "never", $"mpv's OSC is off (set_controls ui): {Str(mpv.Get("user-data/osc-visibility"))}");

			controls.NotifyActivity();
			Check(await Until(() => (mpv.Number("sub-pos") ?? 100) < 100, 3), $"subtitles moved above the bar while it shows (sub-pos {mpv.Number("sub-pos")})");
			await Picture(main, "playing-controls");
			Check(Visible(view, "BottomBar") && Visible(view, "TopBar"), "both bars are shown");

			// pause and play through the controls
			await controls.TogglePause();
			Check(await Until(() => Last()?.State == Bridge.States.Paused && mpv.Flag("pause") == true, 5), "pause: the backend and mpv are paused");
			await Picture(main, "paused");
			await controls.TogglePause();
			Check(await Until(() => Last()?.State == Bridge.States.Playing && mpv.Flag("pause") == false, 5), "play again");

			// the seek bar's thumb let go at 0:30
			controls.BeginScrub();
			controls.Scrub(TimeSpan.FromSeconds(30).Ticks);
			Check(controls.ElapsedText == "0:30", $"while dragging, the time follows the thumb ({controls.ElapsedText})");
			await controls.SeekTo(TimeSpan.FromSeconds(30).Ticks);
			Check(await Until(() => mpv.Number("time-pos") is > 29 and < 34, 5), $"the seek lands in mpv ({mpv.Number("time-pos"):0.0} s)");

			// a key on the page: Right is 5 s on
			var before = mpv.Number("time-pos") ?? 0;
			Check(view.HandleKey(Windows.System.VirtualKey.Right), "Right is one of the player's keys");
			Check(await Until(() => mpv.Number("time-pos") is { } t && t > before + 3.5 && t < before + 8, 5), $"Right seeks 5 s on ({before:0.0} -> {mpv.Number("time-pos"):0.0})");

			// the wheel: volume, never a seek
			await controls.SetVolume(100);
			await Until(() => mpv.Number("volume") == 100, 3);
			var timeBeforeWheel = mpv.Number("time-pos") ?? 0;
			await controls.Wheel(-120);
			await controls.Wheel(-120);
			Check(await Until(() => mpv.Number("volume") == 90, 3), $"two wheel notches down: volume 90 in mpv ({mpv.Number("volume")})");
			// and stays at 90: the backend's state echoes of the first notch (95) must not put it back meanwhile
			var shown = new HashSet<string?>();
			for (var i = 0; i < 20; i++)
			{
				shown.Add(Text(view, "VolumeIndicatorText"));
				await Task.Delay(25);
			}

			Check(controls.VolumeIndicatorVisible && Visible(view, "VolumeIndicator") && shown.SetEquals(["90 %"]), $"the volume indicator shows 90 % throughout ({string.Join(", ", shown)})");
			await Picture(main, "wheel-volume");
			await controls.Wheel(40);
			await controls.Wheel(40);
			await controls.Wheel(40);
			Check(await Until(() => mpv.Number("volume") == 95, 3), $"a fine wheel adds up to one notch: 95 ({mpv.Number("volume")})");
			Check(Math.Abs((mpv.Number("time-pos") ?? 0) - timeBeforeWheel) < 3, "the wheel did not seek");


			// full screen and back: mpv renders at the new size
			controls.ToggleFullScreen();
			await Task.Delay(1200);
			var full = $"{(int)Math.Round(panel.ActualWidth * panel.CompositionScaleX)}x{(int)Math.Round(panel.ActualHeight * panel.CompositionScaleY)}";
			Check(main.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen && composition.RenderSize == full, $"full screen: the window and mpv's size ({composition.RenderSize}, panel {full})");
			Check(Last()?.Fullscreen == true || await Until(() => Last()?.Fullscreen == true, 3), "the backend knows the window is full screen");
			await Picture(main, "fullscreen");
			controls.ToggleFullScreen();
			await Task.Delay(1200);
			Check(main.AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped, "back to windowed");

			// auto-hide: no input for 3 s while playing
			controls.NotifyActivity();
			var hideBy = DateTime.UtcNow.AddSeconds(6);
			while (controls.ControlsVisible && DateTime.UtcNow < hideBy)
			{
				controls.Tick();
				await Task.Delay(100);
			}

			Check(!controls.ControlsVisible && !Visible(view, "BottomBar"), "the controls hid after 3 s without input");
			Check(await Until(() => mpv.Number("sub-pos") == 100, 3), $"and the subtitles went back down (sub-pos {mpv.Number("sub-pos")})");
			await Picture(main, "hidden");

			// next episode
			Check(controls.CanNext, "Next is offered (a second episode is queued)");
			await controls.Next();
			Check(await Until(() => controls.Subtitle == "S1E2 · Second" && Last() is { State: Bridge.States.Playing, ItemId: Episode2 }, 30), $"Next plays episode 2 ({controls.Subtitle})");
			Check(!controls.CanNext, "no Next after the last episode");

			await backend.SendAsync(Bridge.Commands.Stop);
			Check(await Until(() => Last()?.State == Bridge.States.Idle, 10), "stop: idle");
			Check(await Until(() => server.Stopped() >= 2, 10), $"the server got one Stopped per episode ({server.Stopped()}), from the backend only");
		}
		finally
		{
			await backend.ShutdownAsync();
			backend.Dispose();
		}

		var stillThere = composition.GetWindowAsync().Result;
		Check(stillThere.Ipc is not null, "the in-process mpv outlives the backend's shutdown (the shim only closes its pipe)");
		composition.Detach(view.VideoSurface);
		return 0;
	}

	// -- helpers ------------------------------------------------------------------------------------------------------------

	private static string? Str(JsonNode? node) => node?.ToString();

	private static string? Text(FrameworkElement root, string name) => (root.FindName(name) as TextBlock)?.Text;

	private static bool Visible(FrameworkElement root, string name) => root.FindName(name) is UIElement { Visibility: Visibility.Visible };

	private static async Task Picture(Window main, string name)
	{
		await Task.Delay(300);
		var image = ControlsHost.Shots.Print(WinRT.Interop.WindowNative.GetWindowHandle(main));
		if (image is null)
		{
			Log($"{name}: PrintWindow failed");
			return;
		}

		await ControlsHost.Shots.Save(image, Path.Combine(s_out, "shots", $"{name}.png"));
		Log($"{name}: picture saved, {ControlsHost.Shots.PercentLit(image, 8, 40, image.Width - 8, image.Height / 2)}% of the upper half lit");
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

/// <summary>The backend attaches to the composition video's in-process mpv (what MpvVideoSurfaceChoice does for FluentFin's controls).</summary>
internal sealed class AttachedSurface(MpvCompositionVideo composition) : IMpvVideoSurface
{
	public Task<nint> GetHandleAsync() => throw new NotSupportedException("attached: there is no window");

	public Task<BridgeWindow> GetWindowAsync() => composition.GetWindowAsync();
}

/// <summary>A JSON IPC client of the in-process mpv (the harness's own reads: what mpv really does).</summary>
internal sealed class MpvIpc : IDisposable
{
	private readonly NamedPipeClientStream _pipe;
	private readonly StreamReader _reader;
	private readonly StreamWriter _writer;
	private readonly object _lock = new();
	private int _id;

	private MpvIpc(NamedPipeClientStream pipe)
	{
		_pipe = pipe;
		_reader = new StreamReader(pipe, new UTF8Encoding(false));
		_writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
	}

	public static MpvIpc Connect(string name)
	{
		var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
		pipe.Connect(5000);
		return new MpvIpc(pipe);
	}

	public JsonNode? Get(string property)
	{
		lock (_lock)
		{
			var id = ++_id;
			_writer.WriteLine(new JsonObject { ["command"] = new JsonArray("get_property", property), ["request_id"] = id }.ToJsonString());
			while (_reader.ReadLine() is { } line)
			{
				if (JsonNode.Parse(line) is JsonObject reply && reply["request_id"]?.GetValue<int>() == id)
				{
					return reply["error"]?.ToString() == "success" ? reply["data"] : null;
				}
			}

			return null;
		}
	}

	public double? Number(string property) => Get(property) is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

	public bool? Flag(string property) => Get(property) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

	public void Dispose() => _pipe.Dispose();
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
		return new MpvBackendCommand(python, ["-I", "-c", boot], "ui-harness-fluentfin-controls");
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

/// <summary>fakeserver.py (fakejf2 serving the clip as two episodes), started by this host (its own Process, ended at the end). Token over stdin.</summary>
internal sealed class FakeServer : IDisposable
{
	private readonly Process _process;
	private readonly object _ask = new();

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

	public int Stopped()
	{
		lock (_ask)
		{
			_process.StandardInput.WriteLine("stopped");
			_process.StandardInput.Flush();
			return int.TryParse(_process.StandardOutput.ReadLine(), CultureInfo.InvariantCulture, out var n) ? n : -1;
		}
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
