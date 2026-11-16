using System.Runtime.InteropServices;

namespace ControlsHost;

/// <summary>
/// The controls window as DWM composes it, alone, with its transparency: drawn as a DWM thumbnail into a solid window of
/// this host's own, then PrintWindow of that window. Over black and over white, the difference is its alpha (whether the
/// video shows through). PrintWindow of the controls window itself cannot tell: it renders into a GDI bitmap, opaque black
/// where the window is transparent; and Windows.Graphics.Capture does not run on a private desktop.
/// </summary>
internal static partial class WindowCapture
{
	/// <summary>
	/// What DWM composes for <paramref name="area"/> (screen pixels) of <paramref name="source"/> over a solid window of this
	/// host's own (<paramref name="colorRef"/>, 0x00BBGGRR), 1:1. The colour survives exactly where the source is
	/// transparent there (or clipped away by its window region).
	/// </summary>
	public static unsafe Shots.Image? OverColor(nint source, Windows.Graphics.RectInt32 area, uint colorRef)
	{
		int width = area.Width, height = area.Height;
		var instance = GetModuleHandleW(null);
		var className = $"ControlsHostBackdrop{colorRef:x6}";
		var user32 = GetModuleHandleW("user32.dll");
		var defProc = GetProcAddress(user32, "DefWindowProcW");
		fixed (char* name = className)
		{
			var wc = new WNDCLASSEXW
			{
				cbSize = (uint)sizeof(WNDCLASSEXW),
				lpfnWndProc = defProc,
				hInstance = instance,
				hbrBackground = CreateSolidBrush(colorRef),
				lpszClassName = name,
			};
			RegisterClassExW(&wc);
		}

		var dest = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, className, "magenta", WS_POPUP | WS_VISIBLE, 0, 0, width, height, 0, 0, instance, 0);
		if (dest == 0)
		{
			return null;
		}

		try
		{
			if (DwmRegisterThumbnail(dest, source, out var thumb) != 0)
			{
				return null;
			}

			try
			{
				// 1:1, exactly the screen area asked for (the video's), in window coordinates: whatever the window shows
				// there (a frame, or nothing where a window region clips it) is what is measured
				Shots.GetWindowRect(source, out var outer);
				int sx = area.X - outer.Left, sy = area.Y - outer.Top;
				var props = new DWM_THUMBNAIL_PROPERTIES
				{
					dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
					rcDestination = new Shots.RECT { Left = 0, Top = 0, Right = width, Bottom = height },
					rcSource = new Shots.RECT { Left = sx, Top = sy, Right = sx + width, Bottom = sy + height },
					opacity = 255,
					fVisible = 1,
					fSourceClientAreaOnly = 0,
				};
				DwmUpdateThumbnailProperties(thumb, ref props);
				DwmFlush();
				Thread.Sleep(250);
				DwmFlush();
				return Shots.Print(dest);
			}
			finally
			{
				DwmUnregisterThumbnail(thumb);
			}
		}
		finally
		{
			DestroyWindow(dest);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct WNDCLASSEXW
	{
		public uint cbSize;
		public uint style;
		public nint lpfnWndProc;
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

	[StructLayout(LayoutKind.Sequential)]
	private struct DWM_THUMBNAIL_PROPERTIES
	{
		public uint dwFlags;
		public Shots.RECT rcDestination;
		public Shots.RECT rcSource;
		public byte opacity;
		public int fVisible;
		public int fSourceClientAreaOnly;
	}

	private const uint DWM_TNP_RECTDESTINATION = 0x1;
	private const uint DWM_TNP_RECTSOURCE = 0x2;
	private const uint DWM_TNP_OPACITY = 0x4;
	private const uint DWM_TNP_VISIBLE = 0x8;
	private const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
	private const uint WS_POPUP = 0x80000000;
	private const uint WS_VISIBLE = 0x10000000;
	private const uint WS_EX_TOOLWINDOW = 0x80;
	private const uint WS_EX_NOACTIVATE = 0x08000000;

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint GetModuleHandleW(string? name);

	[LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint GetProcAddress(nint module, string name);

	[LibraryImport("gdi32.dll")]
	private static partial nint CreateSolidBrush(uint color);

	[LibraryImport("user32.dll")]
	private static unsafe partial ushort RegisterClassExW(WNDCLASSEXW* wc);

	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyWindow(nint hwnd);

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmUnregisterThumbnail(nint thumbnail);

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmUpdateThumbnailProperties(nint thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

	[LibraryImport("dwmapi.dll")]
	private static partial int DwmFlush();
}
