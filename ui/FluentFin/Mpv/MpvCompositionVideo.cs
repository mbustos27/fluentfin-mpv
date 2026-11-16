using System.Globalization;
using System.Runtime.InteropServices;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv player controls ("Player controls: FluentFin", docs/research/player-controls.md, option B1): the video
/// inside FluentFin's own XAML. One libmpv core in this process (<see cref="MpvInProcessCore"/>) renders in composition
/// mode; its swap chain is shown in the player page's <see cref="SwapChainPanel"/>, so FluentFin's controls draw over the
/// video in the same tree (no airspace). The backend attaches to the core over its pipe (<see cref="GetWindowAsync"/>)
/// and stays the only one that plays and reports. A core lives as long as the backend attached to it (docs/BRIDGE.md §4):
/// when that backend ends, whatever the reason, the core is ended too (<see cref="OnBackendExited"/>), so nothing plays on
/// that nobody reports; the next connect makes a new one under a new pipe name. A core that ended by itself is replaced
/// the same way, so the backend restarts and attaches to the new one.
/// </summary>
public sealed partial class MpvCompositionVideo(ILogger<MpvCompositionVideo> logger, string? configDirectory) : IDisposable
{
	private readonly object _lock = new();

	/// <summary>One core made at a time, outside <see cref="_lock"/>: the UI thread takes that one and never waits for a core being made.</summary>
	private readonly object _createLock = new();
	private readonly MpvSwapChainSlot _swapChains = new(Marshal.AddRef, Marshal.Release);

	/// <summary>Cores being ended on the thread pool (their backend ended): the app's close waits for them, bounded.</summary>
	private readonly List<Task> _ending = [];
	private MpvInProcessCore? _core;
	private SwapChainPanel? _panel;
	private DispatcherQueue? _dispatcher;
	private string _sizeSent = "";
	private bool _disposed;

	/// <summary>The panel's last size in physical pixels: what a new core starts at (mpv's composition output cannot start without one).</summary>
	private string _panelSize = "1280x720";

	/// <summary>The display options for the window the panel was last attached in (<see cref="DisplayOptions"/>).</summary>
	private List<(string Name, string Value)> _display = DisplayOptions(false, 0, 0);

	/// <summary>Extra mpv options after the defaults (the test harness: <c>ao=null</c>).</summary>
	public IReadOnlyList<(string Name, string Value)> ExtraOptions { get; init; } = [];

	/// <summary>The core ended by itself (UI thread when a page is attached, else the core's event thread).</summary>
	public event Action? CoreLost;

	/// <summary>libmpv can be loaded here and does composition output. <paramref name="why"/> says why not.</summary>
	public static bool IsAvailable(out string why) => Libmpv.IsAvailable(out why);

	/// <summary>The panel shows mpv's current swap chain (for diagnostics and the harness).</summary>
	public bool IsShowingVideo => _swapChains.Shown is var shown && shown != 0 && shown == _swapChains.Latest;

	/// <summary>The size mpv was last told to render at, physical pixels ("1280x720"), empty before.</summary>
	public string RenderSize => _sizeSent;

	/// <summary>The pipe of the running core, null when there is none (ended with its backend, or not made yet): diagnostics and the harness.</summary>
	public string? CorePipe
	{
		get
		{
			lock (_lock)
			{
				return _core is { IsShutDown: false } core ? core.Pipe : null;
			}
		}
	}

	/// <summary>
	/// The window the backend attaches to (docs/BRIDGE.md §4, <c>attached</c>): the running core's pipe, a new core first
	/// when there is none or it ended. Making one takes a while (libmpv starts its GPU device and swap chain): not for the UI
	/// thread (<see cref="MpvVideoSurfaceChoice.GetWindowAsync"/> runs it on the thread pool). Any thread.
	/// </summary>
	public Task<BridgeWindow> GetWindowAsync()
	{
		lock (_createLock)
		{
			MpvInProcessCore? ended;
			List<(string Name, string Value)> display;
			string size;
			lock (_lock)
			{
				ObjectDisposedException.ThrowIf(_disposed, this);
				if (_core is { IsShutDown: false } running)
				{
					return Task.FromResult(BridgeWindow.Attached(running.Pipe));
				}

				ended = _core;
				_core = null;
				Unhook(ended);
				// started at the panel's size and for its display: a composition output with no size fails to start
				// ("Failed to get height and width"), and a page may attach before the core exists
				size = _panelSize;
				display = _display;
			}

			ended?.Dispose(TimeSpan.FromSeconds(1));
			var core = MpvInProcessCore.Create(logger, configDirectory, [.. display, ("d3d11-composition-size", size), .. ExtraOptions]);
			lock (_lock)
			{
				if (!_disposed)
				{
					_core = core;
					core.SwapChainChanged += OnSwapChainChanged;
					core.ShutDown += OnShutDown;
					core.VideoAspectChanged += OnVideoAspectChanged;
					_sizeSent = size;

					// what the page changed while the core was being made
					if (_panelSize != size)
					{
						_sizeSent = _panelSize;
						core.SetAsync("d3d11-composition-size", _panelSize);
					}

					if (!ReferenceEquals(_display, display))
					{
						foreach (var (name, value) in _display)
						{
							core.SetAsync(name, value);
						}
					}

					return Task.FromResult(BridgeWindow.Attached(core.Pipe));
				}
			}

			core.Dispose(TimeSpan.FromSeconds(1));
			throw new ObjectDisposedException(nameof(MpvCompositionVideo));
		}
	}

	/// <summary>
	/// The backend that was offered <paramref name="window"/> has ended (its shutdown, a crash, a kill; docs/BRIDGE.md §4):
	/// if that is the running core's pipe, the core ends too, on the thread pool (it can take a few seconds), and the panel
	/// lets go of its video. Without this a crashed backend left the core playing on behind the error, after FluentFin had
	/// already reported the playback stopped. Any thread; never blocks, never throws.
	/// </summary>
	public void OnBackendExited(BridgeWindow window)
	{
		if (window.Mode != "attached" || window.Ipc is not { } pipe)
		{
			return;
		}

		MpvInProcessCore core;
		lock (_lock)
		{
			// another core by now (this one ended by itself and was replaced), or none: nothing of this backend's is left
			if (_core is not { } current || current.Pipe != pipe)
			{
				return;
			}

			core = current;
			_core = null;
			_sizeSent = "";
			Unhook(core);
			_swapChains.Publish(0);
		}

		logger.LogInformation("The mpv backend attached to the in-process mpv (pipe {Pipe}) has ended: ending that mpv too", pipe);
		_dispatcher?.TryEnqueue(BindCurrent);
		var ending = Task.Run(() =>
		{
			try
			{
				core.Dispose(TimeSpan.FromSeconds(2));
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Ending the in-process mpv failed");
			}
		});
		lock (_ending)
		{
			_ending.RemoveAll(x => x.IsCompleted);
			_ending.Add(ending);
		}
	}

	private void Unhook(MpvInProcessCore? core)
	{
		if (core is not null)
		{
			core.SwapChainChanged -= OnSwapChainChanged;
			core.ShutDown -= OnShutDown;
			core.VideoAspectChanged -= OnVideoAspectChanged;
		}
	}

	private void OnVideoAspectChanged(MpvInProcessCore sender, double aspect)
	{
		if (ReferenceEquals(sender, Volatile.Read(ref _core)))
		{
			Interlocked.Exchange(ref _videoAspect, double.IsFinite(aspect) && aspect > 0 ? aspect : 0);
		}
	}

	/// <summary>Shows the video in <paramref name="panel"/> from now on (UI thread). One panel at a time: a new one takes over.</summary>
	public void Attach(SwapChainPanel panel)
	{
		if (ReferenceEquals(_panel, panel))
		{
			return;
		}

		Detach();
		_panel = panel;
		_dispatcher = panel.DispatcherQueue;
		panel.SizeChanged += OnPanelSizeChanged;
		panel.CompositionScaleChanged += OnPanelScaleChanged;
		if (panel.XamlRoot?.ContentIslandEnvironment is { } island && Microsoft.UI.Windowing.AppWindow.GetFromWindowId(island.AppWindowId) is { } appWindow)
		{
			_appWindow = appWindow;
			appWindow.Changed += OnAppWindowChanged;
		}

		ApplyDisplay();
		SendSize();
		BindCurrent();
	}

	/// <summary>
	/// Stops showing the video in <paramref name="owner"/> (UI thread): unbinds and forgets it. With an owner, only if the
	/// video is still in that panel: a page that replaced it may have taken it already.
	/// </summary>
	public void Detach(SwapChainPanel? owner = null)
	{
		if (_panel is not { } panel || (owner is not null && !ReferenceEquals(owner, panel)))
		{
			return;
		}

		panel.SizeChanged -= OnPanelSizeChanged;
		panel.CompositionScaleChanged -= OnPanelScaleChanged;
		if (_appWindow is { } appWindow)
		{
			appWindow.Changed -= OnAppWindowChanged;
			_appWindow = null;
		}

		StopTrace();
		EndSettle(panel);
		var hr = MpvSwapChainInterop.Bind(panel, 0);
		if (hr < 0)
		{
			logger.LogDebug("Unbinding the swap chain from the player page failed: 0x{Hr:x8}", hr);
		}

		_panel = null;
		_swapChains.SetShown(0);
	}

	/// <summary>
	/// HDR and the refresh rate of the display the panel's window is on: mpv cannot see it in composition mode (UI thread).
	/// Applied when a page attaches (a playback starts); a window moved to another display mid-playback keeps the first.
	/// </summary>
	public void ApplyDisplay()
	{
		// the window the panel is in: its display is the one that matters (none known yet: SDR at an unknown rate)
		var hwnd = _panel?.XamlRoot?.ContentIslandEnvironment is { } environment ? Microsoft.UI.Win32Interop.GetWindowFromWindowId(environment.AppWindowId) : 0;
		var display = MpvSwapChainInterop.DisplayOf(hwnd);
		lock (_lock)
		{
			_display = DisplayOptions(display.Hdr, display.PeakNits, display.RefreshHz);
			if (_core is { } core)
			{
				foreach (var (name, value) in _display)
				{
					core.SetAsync(name, value);
				}
			}
		}

		logger.LogInformation("Composition video on a display with HDR {Hdr}, peak {Peak} nits, {Refresh} Hz", display.Hdr ? "on" : "off", Math.Round(display.PeakNits), display.RefreshHz);
	}

	/// <summary>
	/// The options that tell mpv about the display (pure): with Windows HDR on, mpv signals the content's colour space and
	/// the display's peak (HDR passes through); off, it tone-maps to SDR itself. The refresh rate lets display-synced modes
	/// work (composition mode reports none).
	/// </summary>
	public static List<(string Name, string Value)> DisplayOptions(bool hdr, double peakNits, double refreshHz)
	{
		var options = new List<(string, string)>
		{
			("target-colorspace-hint", hdr ? "yes" : "no"),
			("target-peak", hdr && peakNits >= 100 ? Math.Round(peakNits).ToString(CultureInfo.InvariantCulture) : "auto"),
		};

		if (refreshHz > 1)
		{
			options.Add(("display-fps-override", refreshHz.ToString("0.###", CultureInfo.InvariantCulture)));
		}

		return options;
	}

	private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
	{
		Trace($"panel size {e.PreviousSize.Width:0}x{e.PreviousSize.Height:0} -> {e.NewSize.Width:0}x{e.NewSize.Height:0} DIPs");
		SendSize();
		Settle();
	}

	// -- the picture holds still while mpv catches up with a new size -------------------------------------------------------

	/// <summary>The picture's display aspect from mpv (0: none known yet).</summary>
	private double _videoAspect;

	/// <summary>The transform that holds the old frame where the new one will be (reused).</summary>
	private Microsoft.UI.Xaml.Media.CompositeTransform? _hold;

	private bool _settling;
	private nint _settleSwapChain;
	private long _settleStarted;
	private (int Width, int Height) _heldSize;

	/// <summary>The settle's watch of mpv's swap chain (its own thread): when the buffers took the new size, and the frames since.</summary>
	private ResizeWatch? _watch;

	/// <summary>
	/// The panel changed size or place (the window went full screen or back, the title bar came or went): mpv renders at
	/// the new size a few frames later, and until then its old frame would sit at the panel's top-left corner, the picture
	/// jumping by the title bar's height and then snapping to its size (the user's "jump"). Until a frame of the new size is
	/// presented, the panel is transformed so the old frame's picture is where and as big as the new one will be (the same
	/// UI commit as the layout change); then the transform goes. UI thread.
	/// </summary>
	private void Settle()
	{
		if (_panel is not { } panel)
		{
			return;
		}

		var shown = _swapChains.Shown;
		var (sw, sh) = MpvSwapChainInterop.SizeOf(shown);
		var scale = DpiScale(panel);
		var pw = panel.ActualWidth * scale;
		var ph = panel.ActualHeight * scale;
		var matches = Math.Abs(sw - pw) <= 1.5 && Math.Abs(sh - ph) <= 1.5;
		if (shown == 0 || sw <= 0 || sh <= 0 || pw < 1 || ph < 1)
		{
			EndSettle(panel);
			return;
		}

		if (!_settling)
		{
			_settling = true;
			_settleSwapChain = shown;
			_settleStarted = Environment.TickCount64;
			_heldSize = (sw, sh);
			_watch = ResizeWatch.Start(shown, sw, sh);
			Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSettleFrame;
		}

		if (shown != _settleSwapChain)
		{
			// a new swap chain (mpv reconfigured): it is drawn at the panel's size from its first frame
			EndSettle(panel);
			return;
		}

		if (matches)
		{
			// the buffers have the new size, but the screen shows the last frame presented: mpv resizes as soon as it is told
			// and presents a frame of the new size only at its next video frame. The transform goes at that present (taken
			// off before it, the old frame would sit at the corner; left on after it, the new one would show zoomed)
			if (_watch?.NewFramePresented != false || Environment.TickCount64 - _settleStarted > 500)
			{
				EndSettle(panel);
			}

			return;
		}
		var aspect = Volatile.Read(ref _videoAspect) is > 0 and var a ? a : sw / (double)sh;
		var k = Fit(aspect, pw, ph).Width / Fit(aspect, sw, sh).Width;
		_hold ??= new Microsoft.UI.Xaml.Media.CompositeTransform();
		_hold.ScaleX = _hold.ScaleY = k;
		_hold.TranslateX = (pw - k * sw) / 2 / scale;
		_hold.TranslateY = (ph - k * sh) / 2 / scale;
		if (!ReferenceEquals(panel.RenderTransform, _hold))
		{
			panel.RenderTransform = _hold;
		}

		_transformNote = string.Create(CultureInfo.InvariantCulture, $", held x{k:0.000} at {_hold.TranslateX * scale:0},{_hold.TranslateY * scale:0}");
	}

	/// <summary>
	/// The window's DPI scale, physical pixels per DIP. Not the panel's CompositionScale: that one also carries the panel's
	/// own render transform (<see cref="Settle"/>'s), which would then feed back into the size mpv renders at.
	/// </summary>
	private static double DpiScale(SwapChainPanel panel) =>
		panel.XamlRoot?.RasterizationScale is > 0 and var dpi ? dpi : panel.CompositionScaleX > 0 ? panel.CompositionScaleX : 1;

	/// <summary>The picture of a frame <paramref name="width"/> x <paramref name="height"/> with the display aspect <paramref name="aspect"/>, letterboxed into it (mpv's placement).</summary>
	internal static (double Width, double Height) Fit(double aspect, double width, double height) =>
		width / height > aspect ? (height * aspect, height) : (width, width / aspect);

	private void EndSettle(SwapChainPanel panel)
	{
		if (panel.RenderTransform is not null && ReferenceEquals(panel.RenderTransform, _hold))
		{
			panel.RenderTransform = null;
		}

		_transformNote = "";
		_watch?.Stop();
		_watch = null;
		if (_settling)
		{
			_settling = false;
			Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSettleFrame;
		}
	}

	/// <summary>
	/// Watches mpv's swap chain from a thread of its own while the picture is held (at most half a second): the present
	/// count when its buffers left the size they had, so the first frame presented after that, the first of the new size,
	/// is known within a fraction of a millisecond (the UI's frames are too far apart to tell a present before the resize
	/// from one after it). Holds a reference of its own on the swap chain; reads only (its size, its present count).
	/// </summary>
	private sealed class ResizeWatch
	{
		private readonly nint _swapChain;
		private readonly int _width;
		private readonly int _height;
		private volatile bool _stop;
		private long _presentsAtResize = -1;
		private volatile bool _newFrame;

		private ResizeWatch(nint swapChain, int width, int height)
		{
			_swapChain = swapChain;
			_width = width;
			_height = height;
		}

		/// <summary>A frame was presented after the buffers took a new size.</summary>
		public bool NewFramePresented => _newFrame;

		public static ResizeWatch Start(nint swapChain, int width, int height)
		{
			var watch = new ResizeWatch(swapChain, width, height);
			Marshal.AddRef(swapChain);
			new Thread(watch.Run) { IsBackground = true, Name = "mpv resize watch" }.Start();
			return watch;
		}

		public void Stop() => _stop = true;

		private void Run()
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			try
			{
				// the present count as of the last look that still found the old size: the size is read first, so a present
				// counted with the new size can only have come after the resize (mpv resizes, draws, presents)
				long lastOld = MpvSwapChainInterop.PresentCount(_swapChain);
				while (!_stop && !_newFrame && clock.ElapsedMilliseconds < 600)
				{
					if (Interlocked.Read(ref _presentsAtResize) < 0)
					{
						var (w, h) = MpvSwapChainInterop.SizeOf(_swapChain);
						var presents = MpvSwapChainInterop.PresentCount(_swapChain);
						if (w != _width || h != _height)
						{
							Interlocked.Exchange(ref _presentsAtResize, lastOld);
							_newFrame = presents != lastOld;
						}
						else
						{
							lastOld = presents;
						}
					}
					else if (MpvSwapChainInterop.PresentCount(_swapChain) != Interlocked.Read(ref _presentsAtResize))
					{
						_newFrame = true;
					}

					Thread.SpinWait(2000);
					Thread.Yield();
				}
			}
			finally
			{
				Marshal.Release(_swapChain);
			}
		}
	}

	private void OnSettleFrame(object? sender, object e) => Settle();

	// -- what the video does while the window or the page changes (Debug log: per rendered frame, for 2 s after) ----------

	private Microsoft.UI.Windowing.AppWindow? _appWindow;
	private readonly System.Diagnostics.Stopwatch _traceClock = new();
	private DateTime _traceUntil;
	private bool _tracing;
	private string _lastTrace = "";

	private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
	{
		if (args.DidPresenterChange || args.DidSizeChange || args.DidPositionChange)
		{
			Trace($"window {sender.Presenter.Kind} {sender.Size.Width}x{sender.Size.Height}{(args.DidPresenterChange ? " (presenter changed)" : "")}");
		}
	}

	/// <summary>
	/// The trace is on: <c>FLUENTFIN_MPV_VIDEO_TRACE=1</c> (FluentFin's log has no Debug level), or a logger that takes
	/// Debug (the harness's).
	/// </summary>
	private bool TraceOn => _traceOn ??= Environment.GetEnvironmentVariable("FLUENTFIN_MPV_VIDEO_TRACE") == "1" || logger.IsEnabled(LogLevel.Debug);

	private bool? _traceOn;

	/// <summary>Logs <paramref name="why"/> and then, each rendered frame for 2 s, where the video is (when <see cref="TraceOn"/>).</summary>
	private void Trace(string why)
	{
		if (!TraceOn)
		{
			return;
		}

		if (!_tracing)
		{
			_tracing = true;
			_traceClock.Restart();
			_lastTrace = "";
			Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnTraceFrame;
		}

		_traceUntil = DateTime.UtcNow.AddSeconds(2);
		logger.LogInformation("video trace +{Ms} ms: {Why}", _traceClock.ElapsedMilliseconds, why);
	}

	private void StopTrace()
	{
		if (_tracing)
		{
			_tracing = false;
			Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnTraceFrame;
		}
	}

	private void OnTraceFrame(object? sender, object e)
	{
		if (_panel is not { } panel || DateTime.UtcNow > _traceUntil)
		{
			StopTrace();
			return;
		}

		var line = DescribeVideo(panel);
		if (line != _lastTrace)
		{
			_lastTrace = line;
			logger.LogInformation("video trace +{Ms} ms: {Video}", _traceClock.ElapsedMilliseconds, line);
		}
	}

	/// <summary>
	/// Where FluentFin shows the picture now, in its window's physical pixels: the panel's place with its hold transform,
	/// the frame it holds (the old size while held) or else mpv's current one, and mpv's letterbox for the video's aspect.
	/// Null without a panel or a swap chain. UI thread (the harness samples it each frame). What FluentFin commits each
	/// frame: mpv presents on its own thread, so its first new frame can still meet the hold for up to one frame.
	/// </summary>
	public Windows.Foundation.Rect? PictureRect()
	{
		var shown = _swapChains.Shown;
		if (_panel is not { } panel || shown == 0)
		{
			return null;
		}

		var dpi = DpiScale(panel);
		// held, the old frame is what this frame commits (the hold goes in the same tick as the first new frame is seen)
		var (sw, sh) = _settling ? _heldSize : MpvSwapChainInterop.SizeOf(shown);
		if (sw <= 0 || sh <= 0)
		{
			return null;
		}

		var aspect = Volatile.Read(ref _videoAspect) is > 0 and var a ? a : sw / (double)sh;
		var (w, h) = Fit(aspect, sw, sh);
		try
		{
			var r = panel.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect((sw - w) / 2 / dpi, (sh - h) / 2 / dpi, w / dpi, h / dpi));
			return new Windows.Foundation.Rect(r.X * dpi, r.Y * dpi, r.Width * dpi, r.Height * dpi);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>The panel's rectangle in its window and the swap chain's size, physical pixels (diagnostics, the harness).</summary>
	public string DescribeVideo() => _panel is { } panel ? DescribeVideo(panel) : "no panel";

	private string DescribeVideo(SwapChainPanel panel)
	{
		var scale = DpiScale(panel);
		Windows.Foundation.Point origin;
		try
		{
			origin = panel.TransformToVisual(null).TransformPoint(default);
		}
		catch (ArgumentException)
		{
			return "panel not in the tree";
		}

		var (w, h) = MpvSwapChainInterop.SizeOf(_swapChains.Shown);
		return string.Create(CultureInfo.InvariantCulture,
			$"panel {origin.X * scale:0},{origin.Y * scale:0} {panel.ActualWidth * scale:0}x{panel.ActualHeight * scale:0}, swap chain {w}x{h}{_transformNote}");
	}

	private string _transformNote = "";

	private void OnPanelScaleChanged(SwapChainPanel sender, object args)
	{
		SendSize();
		ApplyScale();
	}

	/// <summary>mpv renders at the panel's size in physical pixels (never 0: no zero-size swap chain).</summary>
	private void SendSize()
	{
		if (_panel is not { } panel)
		{
			return;
		}

		var scale = DpiScale(panel);
		var width = (int)Math.Round(panel.ActualWidth * scale);
		var height = (int)Math.Round(panel.ActualHeight * scale);
		if (width < 1 || height < 1)
		{
			return;
		}

		var size = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
		lock (_lock)
		{
			// a core being made now starts at this size, or is told it once it is there (GetWindowAsync)
			_panelSize = size;
			if (_core is not { } core || size == _sizeSent)
			{
				return;
			}

			_sizeSent = size;
			core.SetAsync("d3d11-composition-size", size);
		}

		Trace($"mpv told to render at {size}");
	}

	private void ApplyScale()
	{
		if (_panel is { } panel && _swapChains.Shown is var shown && shown != 0)
		{
			var scale = (float)DpiScale(panel);
			MpvSwapChainInterop.SetInverseScale(shown, scale, scale);
		}
	}

	/// <summary>
	/// mpv's swap chain changed (the core's event thread): a reference is taken here, while mpv's own surely holds, and the
	/// UI thread binds it later (a core that is no longer the running one is not shown).
	/// </summary>
	private void OnSwapChainChanged(MpvInProcessCore sender, long swapChain)
	{
		lock (_lock)
		{
			if (!ReferenceEquals(sender, _core))
			{
				return;
			}

			_swapChains.Publish((nint)swapChain);
		}

		_dispatcher?.TryEnqueue(BindCurrent);
	}

	/// <summary>The panel shows the newest swap chain (UI thread); the one it showed before is let go only after the bind.</summary>
	private void BindCurrent()
	{
		if (_panel is not { } panel)
		{
			return;
		}

		var next = _swapChains.TakeLatest();
		if (next == _swapChains.Shown)
		{
			_swapChains.Drop(next);
			return;
		}

		var hr = MpvSwapChainInterop.Bind(panel, next);
		if (hr < 0)
		{
			_swapChains.Drop(next);
			logger.LogWarning("Showing mpv's swap chain in the player page failed: 0x{Hr:x8}", hr);
			return;
		}

		_swapChains.SetShown(next);
		if (next == 0)
		{
			logger.LogInformation("mpv's swap chain is no longer shown in the player page (its core ended)");
			return;
		}

		ApplyScale();
		logger.LogInformation("mpv's swap chain is shown in the player page");
	}

	private void OnShutDown(MpvInProcessCore sender)
	{
		lock (_lock)
		{
			if (!ReferenceEquals(sender, _core))
			{
				return;
			}

			_swapChains.Publish(0);
		}

		if (_dispatcher is { } dispatcher)
		{
			dispatcher.TryEnqueue(() =>
			{
				BindCurrent();
				CoreLost?.Invoke();
			});
		}
		else
		{
			CoreLost?.Invoke();
		}
	}

	/// <summary>
	/// Ends the core (the app's close, after the backend's shutdown), and waits for cores still being ended. Bounded: a hung
	/// core is left to the process exit.
	/// </summary>
	public void Dispose()
	{
		MpvInProcessCore? core;
		lock (_lock)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			core = _core;
			_core = null;
			Unhook(core);
			_swapChains.Publish(0);
		}

		core?.Dispose(TimeSpan.FromSeconds(2));
		Task[] ending;
		lock (_ending)
		{
			ending = [.. _ending];
		}

		try
		{
			Task.WaitAll(ending, TimeSpan.FromSeconds(2));
		}
		catch (Exception ex)
		{
			logger.LogDebug(ex, "Waiting for the in-process mpv to end failed");
		}
	}
}

/// <summary>
/// fluentfin-mpv player controls: references to mpv's swap chains while they cross threads. mpv publishes its swap chain's
/// address on the core's event thread and may release the swap chain any time after (a reconfigure, the end of its core);
/// the panel binds it later, on the UI thread. So the event thread takes a reference of this slot's at once
/// (<see cref="Publish"/>), the UI thread one of its own for the bind (<see cref="TakeLatest"/>), and the reference to what
/// the panel showed before is released only once the panel shows the next (<see cref="SetShown"/>). The two functions are
/// IUnknown's AddRef and Release (tests count them).
/// </summary>
internal sealed class MpvSwapChainSlot(Func<nint, int> addRef, Func<nint, int> release)
{
	private readonly Lock _lock = new();
	private nint _latest;
	private nint _shown;

	/// <summary>mpv's newest swap chain (0: none), held by this slot.</summary>
	public nint Latest => Volatile.Read(ref _latest);

	/// <summary>What the panel shows (0: nothing), held by this slot.</summary>
	public nint Shown => Volatile.Read(ref _shown);

	/// <summary>
	/// mpv's newest swap chain (event thread; 0: none). Referenced before this returns, while mpv's own reference surely
	/// holds; the one published before is released.
	/// </summary>
	public void Publish(nint swapChain)
	{
		if (swapChain != 0)
		{
			addRef(swapChain);
		}

		nint old;
		lock (_lock)
		{
			old = _latest;
			_latest = swapChain;
		}

		if (old != 0)
		{
			release(old);
		}
	}

	/// <summary>The newest swap chain with a reference of the caller's (UI thread, for the bind; 0: none).</summary>
	public nint TakeLatest()
	{
		lock (_lock)
		{
			if (_latest != 0)
			{
				addRef(_latest);
			}

			return _latest;
		}
	}

	/// <summary>A reference from <see cref="TakeLatest"/> that was not bound after all.</summary>
	public void Drop(nint taken)
	{
		if (taken != 0)
		{
			release(taken);
		}
	}

	/// <summary>The panel now shows <paramref name="taken"/> (from <see cref="TakeLatest"/>; 0: nothing): the one it showed before is released, after the bind.</summary>
	public void SetShown(nint taken)
	{
		var old = Interlocked.Exchange(ref _shown, taken);
		if (old != 0)
		{
			release(old);
		}
	}
}
