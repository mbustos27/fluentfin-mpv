using System.Globalization;
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
/// and stays the only one that plays and reports. The core is made on first use and kept for the app's lifetime; a core
/// that ended by itself is replaced on the next use, under a new pipe name, so the backend restarts and attaches to it.
/// </summary>
public sealed partial class MpvCompositionVideo(ILogger<MpvCompositionVideo> logger, string? configDirectory) : IDisposable
{
	private readonly object _lock = new();
	private MpvInProcessCore? _core;
	private SwapChainPanel? _panel;
	private DispatcherQueue? _dispatcher;
	private long _swapChain;
	private long _boundSwapChain;
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

	/// <summary>libmpv can be loaded here. <paramref name="why"/> says why not.</summary>
	public static bool IsAvailable(out string why) => Libmpv.IsAvailable(out why);

	/// <summary>The panel shows mpv's current swap chain (for diagnostics and the harness).</summary>
	public bool IsShowingVideo => _boundSwapChain != 0 && _boundSwapChain == Volatile.Read(ref _swapChain);

	/// <summary>The size mpv was last told to render at, physical pixels ("1280x720"), empty before.</summary>
	public string RenderSize => _sizeSent;

	/// <summary>
	/// The window the backend attaches to (docs/BRIDGE.md §4, <c>attached</c>): the running core's pipe, a new core first
	/// when there is none or it ended. Any thread.
	/// </summary>
	public Task<BridgeWindow> GetWindowAsync()
	{
		lock (_lock)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_core is not { IsShutDown: false })
			{
				_core?.Dispose(TimeSpan.FromSeconds(1));
				// started at the panel's size and for its display: a composition output with no size fails to start
				// ("Failed to get height and width"), and a page may attach before the core exists
				_core = MpvInProcessCore.Create(logger, configDirectory, [.. _display, ("d3d11-composition-size", _panelSize), .. ExtraOptions]);
				_core.SwapChainChanged += OnSwapChainChanged;
				_core.ShutDown += OnShutDown;
				Volatile.Write(ref _swapChain, 0);
				_sizeSent = _panelSize;
			}

			return Task.FromResult(BridgeWindow.Attached(_core.Pipe));
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
		var hr = MpvSwapChainInterop.Bind(panel, 0);
		if (hr < 0)
		{
			logger.LogDebug("Unbinding the swap chain from the player page failed: 0x{Hr:x8}", hr);
		}

		_panel = null;
		_boundSwapChain = 0;
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
			options.Add(("override-display-fps", refreshHz.ToString("0.###", CultureInfo.InvariantCulture)));
		}

		return options;
	}

	private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => SendSize();

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

		var width = (int)Math.Round(panel.ActualWidth * panel.CompositionScaleX);
		var height = (int)Math.Round(panel.ActualHeight * panel.CompositionScaleY);
		if (width < 1 || height < 1)
		{
			return;
		}

		var size = string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");
		lock (_lock)
		{
			// with the core's creation (any thread): a core made now starts at this size, or is told it here
			_panelSize = size;
			if (_core is not { } core || size == _sizeSent)
			{
				return;
			}

			_sizeSent = size;
			core.SetAsync("d3d11-composition-size", size);
		}
	}

	private void ApplyScale()
	{
		if (_panel is { } panel && _boundSwapChain != 0)
		{
			MpvSwapChainInterop.SetInverseScale((nint)_boundSwapChain, panel.CompositionScaleX, panel.CompositionScaleY);
		}
	}

	private void OnSwapChainChanged(long swapChain)
	{
		Volatile.Write(ref _swapChain, swapChain);
		_dispatcher?.TryEnqueue(BindCurrent);
	}

	private void BindCurrent()
	{
		if (_panel is not { } panel)
		{
			return;
		}

		var swapChain = Volatile.Read(ref _swapChain);
		if (swapChain == _boundSwapChain)
		{
			return;
		}

		var hr = MpvSwapChainInterop.Bind(panel, (nint)swapChain);
		if (hr < 0)
		{
			logger.LogWarning("Showing mpv's swap chain in the player page failed: 0x{Hr:x8}", hr);
			_boundSwapChain = 0;
			return;
		}

		_boundSwapChain = swapChain;
		ApplyScale();
		logger.LogInformation("mpv's swap chain is shown in the player page");
	}

	private void OnShutDown()
	{
		Volatile.Write(ref _swapChain, 0);
		if (_dispatcher is { } dispatcher)
		{
			dispatcher.TryEnqueue(() => CoreLost?.Invoke());
		}
		else
		{
			CoreLost?.Invoke();
		}
	}

	/// <summary>Ends the core (the app's close, after the backend's shutdown). Bounded: a hung core is left to the process exit.</summary>
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
		}

		core?.Dispose(TimeSpan.FromSeconds(2));
	}
}
