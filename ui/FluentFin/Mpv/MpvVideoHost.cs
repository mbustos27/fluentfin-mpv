using System.ComponentModel;
using System.Runtime.InteropServices;
using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentFin.Mpv;

/// <summary>
/// The window mpv draws into: one Win32 child window of the main window, created once on the UI thread and kept for the
/// app's lifetime (its handle goes to the backend in <c>connect</c> and cannot change for a running backend).
/// On the player page it is shown exactly over a XAML element and follows it through layout, window size and DPI changes.
/// XAML cannot draw over it (airspace): it hides while a XAML popup (flyout, dialog, menu, growl) is open, so those stay
/// usable. Its child window belongs to the backend's process, which attaches the two input queues; a watchdog notices
/// when mpv's window stops answering messages so the backend can be killed before it freezes FluentFin's input.
/// </summary>
public sealed unsafe partial class MpvVideoHost(ILogger<MpvVideoHost> logger) : IMpvVideoSurface
{
	private const string ClassName = "FluentFin.MpvVideoHost";

	private static ushort s_classAtom;

	private nint _hwnd;
	private FrameworkElement? _target;
	private Microsoft.UI.Windowing.AppWindow? _appWindow;
	private XamlRoot? _xamlRoot;
	private bool _videoVisible;
	private RECT _shownAt;
	private bool _shown;
	private DispatcherQueueTimer? _resync;
	private Timer? _watchdog;
	private int _unansweredChecks;

	// fluentfin-mpv player controls: where the window is on the screen, for the controls window kept over it
	private Windows.Graphics.RectInt32? _screenRect;

	/// <summary>mpv's window did not answer for several seconds (from a thread-pool thread). The argument says what happened.</summary>
	public event Action<string>? Unresponsive;

	/// <summary>
	/// fluentfin-mpv: the window was shown at a new place on the screen (physical pixels), or hidden (null). UI thread. The
	/// player controls window (<see cref="MpvControlsOverlay"/>) follows it: it moves with the main window too, which a child
	/// window does not have to.
	/// </summary>
	public event Action<Windows.Graphics.RectInt32?>? ScreenRectChanged;

	/// <summary>fluentfin-mpv: where the window is on the screen (physical pixels), null while hidden.</summary>
	public Windows.Graphics.RectInt32? ScreenRect => _screenRect;

	public nint Handle => _hwnd;

	/// <summary>Where the window is now, in physical pixels of the main window's client area; empty while hidden. For diagnostics.</summary>
	public string Placement => _shown ? $"{_shownAt.Left},{_shownAt.Top} {_shownAt.Right - _shownAt.Left}x{_shownAt.Bottom - _shownAt.Top}" : "hidden";

	public Task<nint> GetHandleAsync()
	{
		if (_hwnd != 0)
		{
			return Task.FromResult(_hwnd);
		}

		var dispatcher = App.MainWindow.DispatcherQueue;
		if (dispatcher.HasThreadAccess)
		{
			return Task.FromResult(EnsureCreated());
		}

		var result = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
		{
			try
			{
				result.SetResult(EnsureCreated());
			}
			catch (Exception ex)
			{
				result.SetException(ex);
			}
		}))
		{
			result.SetException(new InvalidOperationException("The UI thread is gone"));
		}

		return result.Task;
	}

	/// <summary>Creates the window (hidden) if it does not exist yet. UI thread only.</summary>
	public nint EnsureCreated()
	{
		if (_hwnd != 0)
		{
			return _hwnd;
		}

		var parent = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
		var instance = GetModuleHandleW(null);

		if (s_classAtom == 0)
		{
			fixed (char* className = ClassName)
			{
				var windowClass = new WNDCLASSEXW
				{
					cbSize = (uint)sizeof(WNDCLASSEXW),
					style = CS_HREDRAW | CS_VREDRAW,
					lpfnWndProc = &WindowProc,
					hInstance = instance,
					hCursor = LoadCursorW(0, IDC_ARROW),
					hbrBackground = GetStockObject(BLACK_BRUSH),
					lpszClassName = className,
				};

				s_classAtom = RegisterClassExW(&windowClass);
				if (s_classAtom == 0)
				{
					throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed for the video window");
				}
			}
		}

		_hwnd = CreateWindowExW(0, ClassName, "FluentFin video", WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN, 0, 0, 1, 1, parent, 0, instance, 0);
		if (_hwnd == 0)
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed for the video window");
		}

		logger.LogInformation("Created the mpv video window {Hwnd:X} in main window {Parent:X}", _hwnd, parent);
		return _hwnd;
	}

	/// <summary>Follows <paramref name="target"/> from now on. The window stays hidden until <see cref="SetVideoVisible"/>.</summary>
	public void Attach(FrameworkElement target)
	{
		Detach();
		EnsureCreated();

		_target = target;
		target.SizeChanged += OnTargetLayoutChanged;
		target.LayoutUpdated += OnLayoutUpdated;
		_appWindow = App.MainWindow.AppWindow;
		_appWindow.Changed += OnAppWindowChanged;
		App.MainWindow.Activated += OnWindowActivated;
		WatchXamlRoot(target.XamlRoot);

		// backstop for changes no event reports (a popup closing without a layout pass)
		_resync ??= App.MainWindow.DispatcherQueue.CreateTimer();
		_resync.Interval = TimeSpan.FromMilliseconds(250);
		_resync.Tick += OnResync;
		_resync.Start();

		Update(force: true);
	}

	/// <summary>
	/// Hides the window and stops following the element (leaving the player page). With <paramref name="owner"/>, only
	/// when the window still follows that element: a page that replaced the caller may have attached it already.
	/// </summary>
	public void Detach(FrameworkElement? owner = null)
	{
		if (owner is not null && !ReferenceEquals(owner, _target))
		{
			return;
		}

		if (_target is { } target)
		{
			target.SizeChanged -= OnTargetLayoutChanged;
			target.LayoutUpdated -= OnLayoutUpdated;

			try
			{
				// also runs when the page unloads because the window is closing, when AppWindow is already gone
				if (_appWindow is not null)
				{
					_appWindow.Changed -= OnAppWindowChanged;
				}

				App.MainWindow.Activated -= OnWindowActivated;
			}
			catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
			{
			}

			_appWindow = null;
		}

		if (_resync is not null)
		{
			_resync.Stop();
			_resync.Tick -= OnResync;
		}

		WatchXamlRoot(null);
		_target = null;
		_videoVisible = false;
		Hide();
	}

	public void SetVideoVisible(bool visible)
	{
		_videoVisible = visible;
		Update(force: visible);
	}

	/// <summary>Gives the keyboard to mpv (the window passes focus on to mpv's own child window).</summary>
	public void Focus()
	{
		if (_hwnd != 0 && _shown)
		{
			SetFocus(_hwnd);
		}
	}

	private void WatchXamlRoot(XamlRoot? root)
	{
		if (_xamlRoot is not null)
		{
			_xamlRoot.Changed -= OnXamlRootChanged;
		}

		_xamlRoot = root;

		if (_xamlRoot is not null)
		{
			_xamlRoot.Changed += OnXamlRootChanged;
		}
	}

	private void OnTargetLayoutChanged(object sender, SizeChangedEventArgs e) => Update();

	// fires for every layout pass anywhere: Update only moves the window when the rect changed
	private void OnLayoutUpdated(object? sender, object e)
	{
		if (_target?.XamlRoot is { } root && root != _xamlRoot)
		{
			WatchXamlRoot(root);
		}

		Update();
	}

	private void OnAppWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
	{
		if (args.DidSizeChange || args.DidPresenterChange || args.DidZOrderChange || args.DidVisibilityChange)
		{
			Update(force: true);
		}
		else if (args.DidPositionChange)
		{
			// fluentfin-mpv: a child window moves with its parent by itself, the controls window on the screen does not
			Update();
		}
	}

	private void OnResync(DispatcherQueueTimer sender, object args) => Update();

	// re-assert HWND_TOP after activation, cheap insurance (research §1)
	private void OnWindowActivated(object sender, WindowActivatedEventArgs args) => Update(force: true);

	// size and DPI (RasterizationScale) changes of the window content
	private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update(force: true);

	private void Update(bool force = false)
	{
		if (_hwnd == 0)
		{
			return;
		}

		if (!_videoVisible || _target is not { XamlRoot: { } root } target || target.ActualWidth < 1 || target.ActualHeight < 1 || target.Visibility != Visibility.Visible)
		{
			Hide();
			return;
		}

		if (HasOpenPopup(root))
		{
			// the popup would open under the video (research §1)
			Hide();
			return;
		}

		RECT rect;
		try
		{
			// epx relative to the XAML root -> physical pixels; left/top and right/bottom rounded separately so edges do not drift
			var scale = root.RasterizationScale;
			var bounds = target.TransformToVisual(null).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
			rect = new RECT
			{
				Left = (int)Math.Round(bounds.Left * scale),
				Top = (int)Math.Round(bounds.Top * scale),
				Right = (int)Math.Round(bounds.Right * scale),
				Bottom = (int)Math.Round(bounds.Bottom * scale),
			};
		}
		catch (ArgumentException)
		{
			// not in the visual tree (any more)
			Hide();
			return;
		}

		// the XAML root's origin is the client origin of the content bridge window; ours is the main window's client area
		var parent = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
		var bridge = FindWindowExW(parent, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
		if (bridge != 0)
		{
			MapWindowPoints(bridge, parent, &rect, 2);
		}

		if (!force && _shown && rect.Equals(_shownAt))
		{
			PublishScreenRect(parent, rect);
			return;
		}

		if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
		{
			// never a zero-size swap chain for mpv
			Hide();
			return;
		}

		// HWND_TOP among the main window's children: above the XAML content's DesktopChildSiteBridge window
		if (!SetWindowPos(_hwnd, HWND_TOP, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_NOOWNERZORDER))
		{
			logger.LogWarning("SetWindowPos failed for the video window: {Error}", Marshal.GetLastPInvokeError());
			return;
		}

		if (!_shown || !rect.Equals(_shownAt))
		{
			logger.LogDebug("Video window at {X},{Y} {Width}x{Height} (scale {Scale})", rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, root.RasterizationScale);
		}

		_shownAt = rect;
		_shown = true;
		StartWatchdog();
		PublishScreenRect(parent, rect);
	}

	private void Hide()
	{
		if (_hwnd != 0 && _shown)
		{
			ShowWindow(_hwnd, SW_HIDE);
		}

		_shown = false;
		StopWatchdog();
		SetScreenRect(null);
	}

	/// <summary>fluentfin-mpv: <paramref name="clientRect"/> (in the main window's client area) on the screen, published when it changed.</summary>
	private void PublishScreenRect(nint parent, RECT clientRect)
	{
		var origin = new POINT();
		if (!ClientToScreen(parent, &origin))
		{
			return;
		}

		SetScreenRect(new Windows.Graphics.RectInt32(clientRect.Left + origin.X, clientRect.Top + origin.Y, clientRect.Right - clientRect.Left, clientRect.Bottom - clientRect.Top));
	}

	private void SetScreenRect(Windows.Graphics.RectInt32? rect)
	{
		if (Nullable.Equals(rect, _screenRect))
		{
			return;
		}

		_screenRect = rect;
		try
		{
			ScreenRectChanged?.Invoke(rect);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Moving the player controls with the video window failed");
		}
	}

	/// <summary>Flyouts, menus, dialogs and growls are popups; tooltips are left alone (they are small and short-lived).</summary>
	private static bool HasOpenPopup(XamlRoot root)
	{
		foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
		{
			if (popup.IsOpen && popup.Child is not ToolTip)
			{
				return true;
			}
		}

		return false;
	}

	private void StartWatchdog()
	{
		_watchdog ??= new Timer(_ => CheckMpvAnswers(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
	}

	private void StopWatchdog()
	{
		_watchdog?.Dispose();
		_watchdog = null;
		_unansweredChecks = 0;
	}

	/// <summary>
	/// Runs on a thread-pool thread, so it still runs when mpv's hang has frozen the UI thread through the attached input
	/// queues. Three checks in a row without an answer (about 6 s) count as hung. An exception escaping a timer callback
	/// ends the process, so nothing may: the whole check is guarded, and so is every handler of <see cref="Unresponsive"/>.
	/// </summary>
	private void CheckMpvAnswers()
	{
		try
		{
			var host = _hwnd;
			var mpv = host == 0 ? 0 : MpvWindow(host);
			if (mpv == 0)
			{
				_unansweredChecks = 0;
				return;
			}

			if (SendMessageTimeoutW(mpv, WM_NULL, 0, 0, SMTO_BLOCK | SMTO_ABORTIFHUNG, 1000, out _) != 0)
			{
				_unansweredChecks = 0;
				return;
			}

			if (++_unansweredChecks < 3)
			{
				return;
			}

			_unansweredChecks = 0;
			logger.LogError("mpv's window {Hwnd:X} has not answered for about 6 seconds", mpv);
			NotifyUnresponsive("mpv stopped responding");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "The mpv window watchdog failed");
		}
	}

	/// <summary>Raises <see cref="Unresponsive"/>; a handler that throws is logged, never passed on to the timer thread.</summary>
	internal void NotifyUnresponsive(string reason)
	{
		if (Unresponsive is not { } handlers)
		{
			return;
		}

		foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>())
		{
			try
			{
				handler(reason);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Handling the unresponsive mpv window failed");
			}
		}
	}

	/// <summary>
	/// mpv creates its window (class "mpv", in the backend's process) as our child and disables it when embedded, so its
	/// mouse input lands here and the OSC would see nothing: enable it on the first mouse move and give it the keyboard on
	/// click. A cross-process parent/child pair has attached input queues, so SetFocus on mpv's window works from here.
	/// (docs/research/winui-mpv-embed.md §3, model A)
	/// </summary>
	[UnmanagedCallersOnly]
	private static nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam)
	{
		switch (message)
		{
			case WM_MOUSEMOVE or WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN:
				if (MpvWindow(hwnd) is var disabled && disabled != 0 && !IsWindowEnabled(disabled))
				{
					EnableWindow(disabled, true);
				}
				break;

			case WM_MOUSEACTIVATE:
				// DefWindowProc of mpv's window passes this up to us when it is clicked
				if (MpvWindow(hwnd) is var clicked && clicked != 0)
				{
					SetFocus(clicked);
				}
				return MA_ACTIVATE;

			case WM_SETFOCUS:
				if (MpvWindow(hwnd) is var focused && focused != 0)
				{
					SetFocus(focused);
					return 0;
				}
				break;
		}

		return DefWindowProcW(hwnd, message, wParam, lParam);
	}

	private static nint MpvWindow(nint host)
	{
		var mpv = FindWindowExW(host, 0, "mpv", null);
		return mpv != 0 ? mpv : GetWindow(host, GW_CHILD);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT : IEquatable<RECT>
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;

		public readonly bool Equals(RECT other) => Left == other.Left && Top == other.Top && Right == other.Right && Bottom == other.Bottom;
		public override readonly bool Equals(object? obj) => obj is RECT other && Equals(other);
		public override readonly int GetHashCode() => HashCode.Combine(Left, Top, Right, Bottom);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WNDCLASSEXW
	{
		public uint cbSize;
		public uint style;
		public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
		public int cbClsExtra;
		public int cbWndExtra;
		public nint hInstance;
		public nint hIcon;
		public nint hCursor;
		public nint hbrBackground;
		public char* lpszMenuName;
		public char* lpszClassName;
		public nint hIconSm;
	}

	private const uint WS_CHILD = 0x40000000;
	private const uint WS_CLIPSIBLINGS = 0x04000000;
	private const uint WS_CLIPCHILDREN = 0x02000000;
	private const uint CS_HREDRAW = 0x0002;
	private const uint CS_VREDRAW = 0x0001;
	private const int BLACK_BRUSH = 4;
	private const int IDC_ARROW = 32512;
	private const uint WM_NULL = 0x0000;
	private const uint SMTO_BLOCK = 0x0001;
	private const uint SMTO_ABORTIFHUNG = 0x0002;
	private const uint WM_SETFOCUS = 0x0007;
	private const uint WM_MOUSEACTIVATE = 0x0021;
	private const uint WM_MOUSEMOVE = 0x0200;
	private const uint WM_LBUTTONDOWN = 0x0201;
	private const uint WM_RBUTTONDOWN = 0x0204;
	private const uint WM_MBUTTONDOWN = 0x0207;
	private const nint MA_ACTIVATE = 1;
	private const uint GW_CHILD = 5;
	private const int SW_HIDE = 0;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_SHOWWINDOW = 0x0040;
	private const uint SWP_NOOWNERZORDER = 0x0200;
	private static readonly nint HWND_TOP = 0;

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint GetModuleHandleW(string? moduleName);

	[LibraryImport("user32.dll", SetLastError = true)]
	private static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);

	[LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

	[LibraryImport("user32.dll")]
	private static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShowWindow(nint hwnd, int command);

	[LibraryImport("user32.dll")]
	private static partial nint SetFocus(nint hwnd);

	[LibraryImport("user32.dll")]
	private static partial nint GetWindow(nint hwnd, uint command);

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsWindowEnabled(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EnableWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool enable);

	[LibraryImport("user32.dll")]
	private static partial int MapWindowPoints(nint from, nint to, RECT* points, uint count);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ClientToScreen(nint hwnd, POINT* point);

	[LibraryImport("user32.dll")]
	private static partial nint LoadCursorW(nint instance, nint cursorName);

	[LibraryImport("user32.dll", SetLastError = true)]
	private static partial nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

	[LibraryImport("gdi32.dll")]
	private static partial nint GetStockObject(int index);
}
