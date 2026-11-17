using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace FluentFin.Mpv;

/// <summary>
/// fluentfin-mpv: the window FluentFin's player controls draw in, kept exactly over mpv's video window.
/// <para>
/// mpv's window is a child of the main window and every child window sits above the main window's XAML (airspace,
/// docs/research/winui-mpv-embed.md §1), so nothing in the player page can draw over the video. This is a window of its
/// own: owned by the main window (always above it and its video, minimized and restored with it, never in the taskbar or
/// Alt+Tab), borderless, square-cornered, and with a transparent backdrop, so the video shows through wherever the
/// controls draw nothing. It covers the whole video, so all mouse input over the video comes here (mpv's own controls are
/// off while these are on). The decision and the alternatives: docs/research/player-controls.md.
/// </para>
/// <para>
/// One for the app's lifetime, shown and hidden as the video window is (<see cref="MpvVideoHost.ScreenRectChanged"/>):
/// creating a XAML window costs a frame or two. Closed with the main window: a window left open keeps the app running.
/// </para>
/// </summary>
public sealed partial class MpvControlsOverlay : Window
{
	private readonly nint _owner;
	private readonly nint _hwnd;
	private RectInt32? _placed;
	private bool _ownerClosed;

	public MpvControlsOverlay(Window owner)
	{
		_owner = WinRT.Interop.WindowNative.GetWindowHandle(owner);
		_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
		Title = $"{Core.AppIdentity.Name} player controls";

		// transparent where the controls draw nothing: the video below shows through
		SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

		var presenter = OverlappedPresenter.Create();
		presenter.SetBorderAndTitleBar(false, false);
		presenter.IsResizable = false;
		presenter.IsMaximizable = false;
		presenter.IsMinimizable = false;
		AppWindow.SetPresenter(presenter);

		// owned: above the main window (and so above its mpv child) and minimized with it; a tool window, never an app
		// window, so it has no taskbar button or Alt+Tab entry (AppWindow.IsShownInSwitchers needs the shell's taskbar,
		// which a desktop without Explorer does not have)
		SetWindowLongPtrW(_hwnd, GWLP_HWNDPARENT, _owner);
		var exStyle = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
		SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW);

		// no rounded corners, no Windows 11 border colour: it lines up with the video edge to the pixel
		var corner = DWMWCP_DONOTROUND;
		_ = DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
		var border = DWMWA_COLOR_NONE;
		_ = DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(uint));

		AppWindow.Closing += OnClosing;
		owner.Closed += OnOwnerClosed;
	}

	public nint Handle => _hwnd;

	/// <summary>Where the controls are now: the window's client area (physical screen pixels), null while hidden.</summary>
	public RectInt32? Placement => _placed;

	/// <summary>
	/// Shown with its client area (where the XAML is) exactly over <paramref name="rect"/> (physical screen pixels) without
	/// taking the focus, or hidden (null).
	/// <para>
	/// The presenter without border and title bar still leaves WS_DLGFRAME (WinUI puts it back when it is taken away), a
	/// frame a few pixels wide that Windows draws light, round the client area: over the video's edge. So the window is
	/// made larger by that frame on each side and clipped to its client area with a window region; the frame is measured,
	/// not assumed, since it grows with the DPI. Measured in tools\ui-harness\controls-host (scenario frame).
	/// </para>
	/// </summary>
	public void PlaceAt(RectInt32? rect)
	{
		if (_ownerClosed)
		{
			return;
		}

		if (rect is not { Width: > 0, Height: > 0 } target)
		{
			if (_placed is not null)
			{
				var wasActive = GetForegroundWindow() == _hwnd;
				AppWindow.Hide();
				if (wasActive)
				{
					// the keyboard goes back to the page, not to whatever window Windows picks next
					SetForegroundWindow(_owner);
				}
			}

			_placed = null;
			return;
		}

		if (_placed is null || !_placed.Value.Equals(target) || !FrameIs(_frame))
		{
			Move(target);
			if (!FrameIs(_frame))
			{
				// the frame changed with the move (another DPI): once more with the new one
				Move(target);
			}
		}

		if (_placed is null)
		{
			ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
		}

		_placed = target;
	}

	private Frame _frame;
	private SizeInt32 _clippedTo;

	/// <summary>The window's frame round its client area, in physical pixels.</summary>
	private readonly record struct Frame(int Left, int Top, int Right, int Bottom);

	private void Move(RectInt32 target)
	{
		_frame = MeasureFrame();
		var f = _frame;

		// the owner's z-order is kept (owned windows stay above it); no activation
		SetWindowPos(_hwnd, 0, target.X - f.Left, target.Y - f.Top, target.Width + f.Left + f.Right, target.Height + f.Top + f.Bottom,
			SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER);

		var clip = new SizeInt32(target.Width, target.Height);
		if (_clippedTo != clip || !FrameIs(f))
		{
			// the client area in window coordinates; the system owns the region from here
			var region = CreateRectRgn(f.Left, f.Top, f.Left + target.Width, f.Top + target.Height);
			if (region != 0 && SetWindowRgn(_hwnd, region, true) == 0)
			{
				DeleteObject(region);
			}

			_clippedTo = clip;
		}
	}

	private bool FrameIs(Frame frame) => MeasureFrame() == frame;

	private Frame MeasureFrame()
	{
		if (!GetWindowRect(_hwnd, out var window) || !GetClientRect(_hwnd, out var client))
		{
			return _frame;
		}

		var origin = new POINT();
		if (!ClientToScreen(_hwnd, ref origin))
		{
			return _frame;
		}

		return new Frame(origin.X - window.Left, origin.Y - window.Top, window.Right - (origin.X + client.Right), window.Bottom - (origin.Y + client.Bottom));
	}

	/// <summary>Alt+F4 in the controls closes the app, as it would over the video without them.</summary>
	private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (_ownerClosed)
		{
			return;
		}

		args.Cancel = true;
		PostMessageW(_owner, WM_CLOSE, 0, 0);
	}

	private void OnOwnerClosed(object sender, WindowEventArgs args)
	{
		_ownerClosed = true;
		_placed = null;
		try
		{
			Close();
		}
		catch (Exception ex) when (ex is COMException or InvalidOperationException)
		{
		}
	}

	private const int GWLP_HWNDPARENT = -8;
	private const int GWL_EXSTYLE = -20;
	private const nint WS_EX_TOOLWINDOW = 0x00000080;
	private const nint WS_EX_APPWINDOW = 0x00040000;
	private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
	private const int DWMWA_BORDER_COLOR = 34;
	private const int DWMWCP_DONOTROUND = 1;
	private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
	private const uint SWP_NOZORDER = 0x0004;
	private const uint SWP_NOACTIVATE = 0x0010;
	private const uint SWP_NOOWNERZORDER = 0x0200;
	private const int SW_SHOWNOACTIVATE = 4;
	private const uint WM_CLOSE = 0x0010;

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

	[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static partial nint SetWindowLongPtrW(nint hwnd, int index, nint value);

	[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static partial nint GetWindowLongPtrW(nint hwnd, int index);

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ShowWindow(nint hwnd, int command);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetWindowRect(nint hwnd, out RECT rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetClientRect(nint hwnd, out RECT rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ClientToScreen(nint hwnd, ref POINT point);

	[LibraryImport("gdi32.dll")]
	private static partial nint CreateRectRgn(int left, int top, int right, int bottom);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteObject(nint obj);

	[LibraryImport("user32.dll")]
	private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

	[LibraryImport("user32.dll")]
	private static partial nint GetForegroundWindow();

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetForegroundWindow(nint hwnd);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
}
