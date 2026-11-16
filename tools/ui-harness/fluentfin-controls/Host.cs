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
using Microsoft.UI.Xaml.Input;
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

	public static bool Check(bool ok, string what)
	{
		if (!ok)
		{
			Interlocked.Increment(ref s_failures);
		}

		Log($"CHECK {(ok ? "ok" : "FAIL")}: {what}");
		return ok;
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
		Button? chrome = null;
		controls.SetFullScreen = fullScreen =>
		{
			presenterChanges++;
			main.AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
			// as the player page does with FluentFin's title bar: hidden in full screen, right after the presenter
			chrome?.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
			controls.IsFullScreen = fullScreen;
			_ = backend.SendAsync(Bridge.Commands.SetFullscreen, BridgeCommands.Fullscreen(fullScreen));
		};

		var backs = 0;
		controls.GoBack = () => backs++;

		// the player page's key path (VideoPlayerPage: its root previews every key through MpvPlayerKeys, Escape is its
		// accelerator), with a button above the video like the page's notice (its close button)
		var view = new MpvPlayerControls { Controls = controls, IsTabStop = true };
		var noticeClicks = 0;
		var notice = new Button { Content = "Close notice" };
		notice.Click += (_, _) => noticeClicks++;
		var page = new Grid { Background = new SolidColorBrush(Colors.Black), RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
		Grid.SetRow(view, 1);
		page.Children.Add(notice);
		page.Children.Add(view);
		var keysSeen = 0;
		page.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => keysSeen++), handledEventsToo: true);
		page.PreviewKeyDown += (_, e) => MpvPlayerKeys.OnPreviewKeyDown(view, videoShown: true, e);
		var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
		escape.Invoked += (_, e) =>
		{
			e.Handled = true;
			if (controls.IsFullScreen)
			{
				controls.SetFullScreen(false);
			}
			else
			{
				backs++;
			}
		};
		page.KeyboardAccelerators.Add(escape);
		// the window's title bar: outside the page, before it in the tab order (where WinUI hands the focus of a collapsed bar)
		var titleBar = new Button { Content = "Title bar" };
		chrome = titleBar;
		var window = new Grid { Background = new SolidColorBrush(Colors.Black), RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
		Grid.SetRow(page, 1);
		window.Children.Add(titleBar);
		window.Children.Add(page);
		view.KeyboardHome = page;
		main.Content = window;
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

		var identity = new BridgeIdentity(server.Url, "srv", "Fake", server.UserId, "u", server.Token, "controls-host-device", "Controls host", "FluentFin", "0.0.1");
		string? lastPipe = null;
		try
		{
			await backend.StartAsync();
			var version = await backend.ConnectAsync(identity);
			var pipe = lastPipe = composition.CorePipe;
			Log($"connected: mpv {version} ({backend.Status.MpvKind}), window attached {pipe}");
			Check(pipe is not null && backend.Status.MpvKind == "external", "the backend attached to FluentFin's in-process mpv over its pipe (the shim's external backend)");
			var mpv = MpvIpc.Connect(pipe!);
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
			// the display FluentFin read for mpv, which cannot see it in composition mode (HDR off on every display here)
			// target-colorspace-hint is a flag on some builds and a choice (auto/yes/no) on others
			Check(mpv.Number("display-fps-override") is > 1 && Str(mpv.Get("target-colorspace-hint")) is "no" or "false",
				$"mpv was told the display's rate and SDR ({mpv.Number("display-fps-override")} Hz, hint {Str(mpv.Get("target-colorspace-hint"))})");
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

			// -- keys through the player page's key path: real key messages posted to this window (never SendInput) -----
			await backend.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest([Episode1])));
			Check(await Until(() => Last() is { State: Bridge.States.Playing, ItemId: Episode1 } && mpv.Flag("pause") == false, 30), "playing again, for the keys");
			await KeysScenario(main, view, notice, titleBar, controls, mpv, () => noticeClicks, () => backs, () => keysSeen);

			// -- full screen and back: the picture changes once, it does not jump ----------------------------------------
			await JumpScenario(main, composition, controls);

			// -- the backend dies mid-playback (the reviewer's kill repro): FluentFin's mpv ends with it ----------------
			if (mpv.Flag("pause") == true)
			{
				await controls.TogglePause();
			}

			Check(await Until(() => Last()?.State == Bridge.States.Playing && mpv.Flag("pause") == false, 5), "playing before the kill");
			Log($"before the kill: time-pos {mpv.Number("time-pos"):0.00}, pipe {pipe}, backend pid {Launcher.Last?.Id}");
			Launcher.Last!.Kill();
			Check(await Until(() => backend.Status.State == MpvBackendState.Crashed, 15), $"the backend was killed mid-playback: {backend.Status.State}");
			Check(await Until(() => composition.CorePipe is null && !PipeAnswers(pipe!), 8),
				$"FluentFin's in-process mpv ended with the backend (its pipe {pipe} is gone): nothing plays on behind the error or after Back");
			Check(await Until(() => !composition.IsShowingVideo, 3), "and the panel let go of its video");
			mpv.Dispose();

			// the next playback: a new backend, a new mpv on a new pipe
			await backend.StartAsync();
			await backend.ConnectAsync(identity);
			var pipe2 = lastPipe = composition.CorePipe;
			Check(pipe2 is not null && pipe2 != pipe, $"the next connect made a new mpv on a new pipe ({pipe2})");
			mpv = MpvIpc.Connect(pipe2!);
			await controls.SendControls();
			await backend.SendAsync(Bridge.Commands.Play, BridgeCommands.Play(new BridgePlayRequest([Episode1])));
			Check(await Until(() => Last() is { State: Bridge.States.Playing, ItemId: Episode1, Window: "attached" }, 30), "it plays in the new mpv");
			Check(await Until(() => composition.IsShowingVideo, 5) && await Until(() => (mpv.Number("time-pos") ?? 0) > 1, 15), "shown in the panel, and the video runs");
			await Picture(main, "after-the-kill");
			mpv.Dispose();
		}
		finally
		{
			await backend.ShutdownAsync();
			backend.Dispose();
		}

		// docs/BRIDGE.md §4: FluentFin's mpv lives as long as the backend process, its shutdown included
		Check(await Until(() => composition.CorePipe is null && (lastPipe is null || !PipeAnswers(lastPipe)), 8),
			$"the backend's shutdown ended FluentFin's in-process mpv too (pipe {lastPipe})");
		composition.Detach(view.VideoSurface);
		return 0;
	}

	/// <summary>
	/// The player page's key path with real keys (posted WM_KEYDOWN/WM_KEYUP to this window's input site, as a keyboard
	/// sends them; Ctrl and Shift are not held): Space on the video pauses and plays, Space on a button the keyboard
	/// focused presses that button and leaves playback alone (the notice's close, the bar's Back), F goes full screen and
	/// Escape comes back, the bars stay while the keyboard is on them, and hide with the keyboard handed back to the video.
	/// </summary>
	private static async Task KeysScenario(Window main, MpvPlayerControls view, Button notice, Button titleBar, MpvControlsViewModel controls, MpvIpc mpv,
		Func<int> noticeClicks, Func<int> backs, Func<int> keysSeen)
	{
		bool Paused() => mpv.Flag("pause") == true;
		bool Playing() => mpv.Flag("pause") == false;
		var root = main.Content.XamlRoot;

		// which window takes a posted key here: the thread's focus window, the island's input site, its bridge, the window
		view.Focus(FocusState.Programmatic);
		await Task.Delay(200);
		(nint Hwnd, string Class) input = default;
		foreach (var candidate in InputWindows(WinRT.Interop.WindowNative.GetWindowHandle(main)))
		{
			var seen = keysSeen();
			PostKey(candidate.Hwnd, 0x41);
			var reached = await Until(() => keysSeen() > seen, 1.5);
			Log($"a key posted to {candidate.Class} 0x{candidate.Hwnd:x}: {(reached ? "reaches the page" : "does not reach the page")}");
			if (reached)
			{
				input = candidate;
				break;
			}
		}

		if (!Check(input.Hwnd != 0, "a key posted to one of this window's windows reaches the page (keys can be checked off-screen)"))
		{
			return;
		}

		// Space with the video focused: the player's
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Paused, 3), "Space on the video pauses (the player's key)");
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Playing, 3), "Space again plays");

		// Space on a button the keyboard is on: the button's
		notice.Focus(FocusState.Keyboard);
		var clicks = noticeClicks();
		PostKey(input.Hwnd, VkSpace);
		var pressed = await Until(() => noticeClicks() > clicks, 3);
		await Task.Delay(800);
		Check(pressed && Playing(), $"Space on a focused button (a notice's close) presses it and leaves playback alone (pressed {pressed}, pause {mpv.Flag("pause")})");

		controls.NotifyActivity();
		var back = (Button)view.FindName("BackButton");
		back.Focus(FocusState.Keyboard);
		var backsBefore = backs();
		PostKey(input.Hwnd, VkSpace);
		pressed = await Until(() => backs() > backsBefore, 3);
		await Task.Delay(800);
		Check(pressed && Playing(), $"Space on the bar's Back (keyboard focus) is Back, not pause (pressed {pressed}, pause {mpv.Flag("pause")})");

		// F from that button too, and Escape back (the page's accelerator)
		PostKey(input.Hwnd, VkF);
		Check(await Until(() => main.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, 3), "F goes full screen through the page's key path (from a focused button too)");
		await Task.Delay(500);
		PostKey(input.Hwnd, VkEscape);
		Check(await Until(() => main.AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped, 3), "Escape leaves full screen (the page's accelerator)");

		// the bars stay while the keyboard is on them
		back.Focus(FocusState.Keyboard);
		controls.NotifyActivity();
		await Task.Delay(4500);
		Check(controls.ControlsVisible && Visible(view, "BottomBar") && controls.HeldBy == "the keyboard is on them",
			$"the bars stay up while the keyboard is on Back ({controls.HeldBy ?? "nothing holds them"})");

		// the mouse's focus in the bar (a click on the seek bar): they hide, and the keyboard goes back to the video
		((Control)view.FindName("SeekBar")).Focus(FocusState.Pointer);
		controls.NotifyActivity();
		var hid = await Until(() => !controls.ControlsVisible, 6);
		await Task.Delay(300);
		var focused = FocusManager.GetFocusedElement(root);
		Check(hid && ReferenceEquals(focused, view), $"the bars hid with the mouse's focus in them, and the keyboard went back to the video ({focused?.GetType().Name ?? "nothing"})");
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Paused, 3), "so Space still pauses");
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Playing, 3), "and plays");

		// the focus on a bar button that the keyboard does not hold (the mouse's; a programmatic focus counts as the keyboard's
		// after keyboard input, as here): the bars hide and the player has the keyboard, not the title bar before the page
		// that WinUI would hand it to (visual check: Space then did nothing)
		async Task<(bool Hid, object? Focused)> HideWith(Control focus, FocusState how)
		{
			controls.NotifyActivity();
			focus.Focus(how);
			var gone = await Until(() => !controls.ControlsVisible, 6);
			await Task.Delay(300);
			return (gone, FocusManager.GetFocusedElement(root));
		}

		var (hidBack, onBack) = await HideWith(back, FocusState.Pointer);
		Check(hidBack && ReferenceEquals(onBack, view), $"the bars hid with the mouse's focus on Back: the keyboard went to the video, not the title bar ({Describe(onBack)})");
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Paused, 3), "so Space pauses");
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Playing, 3), "and plays");

		var (hidTitle, onTitle) = await HideWith(titleBar, FocusState.Keyboard);
		Check(hidTitle && ReferenceEquals(onTitle, view), $"the bars hid with the keyboard on the title bar (outside the page): the player took it back ({Describe(onTitle)})");

		var (hidNotice, onNotice) = await HideWith(notice, FocusState.Keyboard);
		Check(hidNotice && ReferenceEquals(onNotice, notice), $"the bars hid with the keyboard on the notice's close (on the page): it stays there ({Describe(onNotice)})");

		// a button the mouse clicked keeps no claim on Space: it plays and pauses, as in every player
		controls.NotifyActivity();
		notice.Focus(FocusState.Pointer);
		clicks = noticeClicks();
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Paused, 3) && noticeClicks() == clicks, "Space after a click on a button (pointer focus) pauses, the button is not pressed again");
		PostKey(input.Hwnd, VkSpace);
		Check(await Until(Playing, 3), "and plays");
		view.Focus(FocusState.Programmatic);
	}

	private const int VkSpace = 0x20, VkEscape = 0x1B, VkF = 0x46;

	/// <summary>
	/// Full screen and back with real keys (F, Escape, through the page's key path), the title bar row collapsing with it
	/// as the page's does. The user saw the picture "jump": the panel took its new place and size at once, mpv rendered at
	/// the new size a few frames later, and meanwhile its old frame sat at the panel's corner (moved by the title bar's
	/// height, then snapped to its size). Sampled on every frame FluentFin renders: where it shows the picture
	/// (MpvCompositionVideo.PictureRect: the panel with its hold, the frame mpv last presented, mpv's letterbox). Checked:
	/// that changes once each way, from where it was to where it ends. Pictures of the window before and after.
	/// </summary>
	private static async Task JumpScenario(Window main, MpvCompositionVideo composition, MpvControlsViewModel controls)
	{
		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(main);
		var input = InputWindows(hwnd).First(x => x.Class.Contains("InputSiteWindowClass", StringComparison.Ordinal)).Hwnd;

		async Task<List<(double Ms, Windows.Foundation.Rect Rect)>> Across(int vk)
		{
			var frames = new List<(double, Windows.Foundation.Rect)>();
			var clock = Stopwatch.StartNew();
			void OnFrame(object? sender, object e)
			{
				if (composition.PictureRect() is { } rect)
				{
					frames.Add((clock.Elapsed.TotalMilliseconds, rect));
				}
			}

			Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame;
			try
			{
				await Task.Delay(200);
				PostKey(input, vk);
				await Task.Delay(1500);
			}
			finally
			{
				Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame;
			}

			return frames;
		}

		static int Changes(List<(double Ms, Windows.Foundation.Rect Rect)> frames, out string seen)
		{
			var changes = 0;
			var runs = new List<string>();
			Windows.Foundation.Rect? last = null;
			foreach (var (ms, r) in frames)
			{
				if (last is { } l && Math.Abs(r.X - l.X) <= 3 && Math.Abs(r.Y - l.Y) <= 3 && Math.Abs(r.Width - l.Width) <= 3 && Math.Abs(r.Height - l.Height) <= 3)
				{
					continue;
				}

				if (last is not null)
				{
					changes++;
				}

				last = r;
				runs.Add(string.Create(CultureInfo.InvariantCulture, $"{ms:0} ms: {r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}"));
			}

			seen = string.Join(" | ", runs);
			return changes;
		}

		var on = await Across(VkF);
		Check(main.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, "F went full screen");
		var onChanges = Changes(on, out var onSeen);
		Log($"full screen, the picture each frame: {on.Count} frames; {onSeen}");
		Check(onChanges == 1, $"going full screen the picture changes once, it does not jump ({onChanges} changes in {on.Count} frames)");
		await Picture(main, "jump-fullscreen");
		var off = await Across(VkEscape);
		Check(main.AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped, "Escape came back to the window");
		var offChanges = Changes(off, out var offSeen);
		Log($"windowed, the picture each frame: {off.Count} frames; {offSeen}");
		Check(offChanges == 1, $"back to the window the picture changes once, it does not jump ({offChanges} changes in {off.Count} frames)");
		await Picture(main, "jump-windowed");
		controls.NotifyActivity();
	}

	private static string Describe(object? element) => element switch
	{
		null => "nothing",
		Button { Content: string text } => $"Button '{text}'",
		_ => element.GetType().Name,
	};

	/// <summary>Where a keyboard's messages could go: the thread's focus window, then every window under the main one, then it.</summary>
	private static List<(nint Hwnd, string Class)> InputWindows(nint main)
	{
		static string ClassOf(nint hwnd)
		{
			var name = new StringBuilder(256);
			GetClassNameW(hwnd, name, name.Capacity);
			return name.ToString();
		}

		var found = new List<(nint, string)>();
		if (GetFocus() is var focus && focus != 0)
		{
			found.Add((focus, "the focus window " + ClassOf(focus)));
		}

		EnumChildWindows(main, (child, _) =>
		{
			found.Add((child, ClassOf(child)));
			return true;
		}, 0);
		found.Add((main, "the main window " + ClassOf(main)));
		return found;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern nint GetFocus();

	private static void PostKey(nint hwnd, int vk)
	{
		var scan = (nint)(MapVirtualKeyW((uint)vk, 0) << 16);
		PostMessageW(hwnd, 0x0100, vk, 1 | scan);                                  // WM_KEYDOWN
		PostMessageW(hwnd, 0x0101, vk, 1 | scan | (1 << 30) | unchecked((int)0x80000000)); // WM_KEYUP
	}

	private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint lParam);

	[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
	private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern uint MapVirtualKeyW(uint code, uint mapType);

	/// <summary>Something answers on the pipe (an mpv's JSON IPC server).</summary>
	private static bool PipeAnswers(string name)
	{
		try
		{
			using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
			pipe.Connect(300);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
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

/// <summary>
/// The backend attaches to the composition video's in-process mpv, as MpvVideoSurfaceChoice does for FluentFin's controls:
/// the core is made on the thread pool, and it ends when the backend that was given it ends.
/// </summary>
internal sealed class AttachedSurface(MpvCompositionVideo composition) : IMpvVideoSurface
{
	public Task<nint> GetHandleAsync() => throw new NotSupportedException("attached: there is no window");

	public Task<BridgeWindow> GetWindowAsync() => Task.Run(composition.GetWindowAsync);

	public void OnBackendExited(BridgeWindow window) => composition.OnBackendExited(window);
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

	/// <summary>The backend this host started last (its own job: the kill scenario ends it through its handle).</summary>
	public static IMpvBackendProcess? Last { get; private set; }

	public IMpvBackendProcess Start(MpvBackendCommand command) => Last = WindowsMpvBackendLauncher.StartInJob(command.FileName, command.Arguments, configDir);
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
