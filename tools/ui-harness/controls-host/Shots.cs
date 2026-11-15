using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace ControlsHost;

/// <summary>
/// Pictures of this host's own windows only: PrintWindow with PW_RENDERFULLCONTENT (DWM's content of the window and its
/// child windows, mpv's video included), never a copy of the screen. The controls window is a window of its own, so a
/// picture of what the user sees is composed here: the main window's capture with the controls drawn over it at the
/// controls window's offset, using the alpha of the controls' own XAML rendering (RenderTargetBitmap).
/// </summary>
internal static partial class Shots
{
	public sealed record Image(byte[] Bgra, int Width, int Height)
	{
		public (byte B, byte G, byte R, byte A) At(int x, int y)
		{
			var i = (y * Width + x) * 4;
			return (Bgra[i], Bgra[i + 1], Bgra[i + 2], Bgra[i + 3]);
		}
	}

	/// <summary>The window as DWM renders it, in physical pixels. Pixels PrintWindow leaves alone keep <paramref name="fill"/>.</summary>
	public static unsafe Image? Print(nint hwnd, uint fill = 0xFF000000)
	{
		if (!GetWindowRect(hwnd, out var r))
		{
			return null;
		}

		int w = r.Right - r.Left, h = r.Bottom - r.Top;
		if (w <= 0 || h <= 0)
		{
			return null;
		}

		var screen = GetDC(0);
		var mem = CreateCompatibleDC(screen);
		var info = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
		var dib = CreateDIBSection(screen, &info, 0, out var bits, 0, 0);
		var old = SelectObject(mem, dib);
		try
		{
			var span = new Span<uint>((void*)bits, w * h);
			span.Fill(fill);
			if (!PrintWindow(hwnd, mem, 2))
			{
				return null;
			}

			var bytes = new byte[w * h * 4];
			new ReadOnlySpan<byte>((void*)bits, bytes.Length).CopyTo(bytes);
			return new Image(bytes, w, h);
		}
		finally
		{
			SelectObject(mem, old);
			DeleteObject(dib);
			DeleteDC(mem);
			_ = ReleaseDC(0, screen);
		}
	}

	/// <summary>The XAML element as XAML renders it, BGRA premultiplied, scaled to <paramref name="width"/> x <paramref name="height"/> physical pixels.</summary>
	public static async Task<Image?> Render(UIElement element, int width, int height)
	{
		var target = new RenderTargetBitmap();
		await target.RenderAsync(element, width, height);
		var buffer = await target.GetPixelsAsync();
		var bytes = new byte[buffer.Length];
		using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
		{
			reader.ReadBytes(bytes);
		}

		return new Image(bytes, target.PixelWidth, target.PixelHeight);
	}

	/// <summary><paramref name="over"/> (premultiplied alpha) drawn onto a copy of <paramref name="under"/> at (x, y).</summary>
	public static Image Compose(Image under, Image over, int x, int y)
	{
		var result = (byte[])under.Bgra.Clone();
		for (var oy = 0; oy < over.Height; oy++)
		{
			var uy = y + oy;
			if (uy < 0 || uy >= under.Height)
			{
				continue;
			}

			for (var ox = 0; ox < over.Width; ox++)
			{
				var ux = x + ox;
				if (ux < 0 || ux >= under.Width)
				{
					continue;
				}

				var o = (oy * over.Width + ox) * 4;
				var u = (uy * under.Width + ux) * 4;
				var a = over.Bgra[o + 3];
				for (var c = 0; c < 3; c++)
				{
					result[u + c] = (byte)Math.Min(255, over.Bgra[o + c] + result[u + c] * (255 - a) / 255);
				}

				result[u + 3] = 255;
			}
		}

		return under with { Bgra = result };
	}

	public static async Task Save(Image image, string path)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var opaque = (byte[])image.Bgra.Clone();
		for (var i = 3; i < opaque.Length; i += 4)
		{
			opaque[i] = 255;
		}

		await using var file = File.Create(path);
		var stream = file.AsRandomAccessStream();
		var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
		encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)image.Width, (uint)image.Height, 96, 96, opaque);
		await encoder.FlushAsync();
	}

	/// <summary>
	/// The premultiplied image of a window from two captures of it, over black and over white: alpha = 255 - (white - black),
	/// colour = the capture over black.
	/// </summary>
	public static Image AlphaFrom(Image overBlack, Image overWhite)
	{
		var result = new byte[overBlack.Bgra.Length];
		for (var i = 0; i < result.Length; i += 4)
		{
			var difference = (overWhite.Bgra[i] - overBlack.Bgra[i] + overWhite.Bgra[i + 1] - overBlack.Bgra[i + 1] + overWhite.Bgra[i + 2] - overBlack.Bgra[i + 2]) / 3;
			result[i] = overBlack.Bgra[i];
			result[i + 1] = overBlack.Bgra[i + 1];
			result[i + 2] = overBlack.Bgra[i + 2];
			result[i + 3] = (byte)Math.Clamp(255 - difference, 0, 255);
		}

		return overBlack with { Bgra = result };
	}

	/// <summary>Mean alpha (0-255) of the pixels in the rectangle.</summary>
	public static double MeanAlpha(Image image, int x0, int y0, int x1, int y1)
	{
		long sum = 0, n = 0;
		for (var y = Math.Max(0, y0); y < Math.Min(image.Height, y1); y++)
		{
			for (var x = Math.Max(0, x0); x < Math.Min(image.Width, x1); x++)
			{
				sum += image.Bgra[(y * image.Width + x) * 4 + 3];
				n++;
			}
		}

		return n == 0 ? 0 : (double)sum / n;
	}

	/// <summary>Share of sampled pixels in the rectangle that are not black (the video is drawing there).</summary>
	public static int PercentLit(Image image, int x0, int y0, int x1, int y1)
	{
		int samples = 0, lit = 0;
		for (var i = 0; i < 40; i++)
		{
			for (var j = 0; j < 40; j++)
			{
				var x = Math.Clamp(x0 + (x1 - x0 - 1) * i / 39, 0, image.Width - 1);
				var y = Math.Clamp(y0 + (y1 - y0 - 1) * j / 39, 0, image.Height - 1);
				var (b, g, r, _) = image.At(x, y);
				samples++;
				if (r + g + b > 48)
				{
					lit++;
				}
			}
		}

		return 100 * lit / Math.Max(1, samples);
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct RECT
	{
		public int Left, Top, Right, Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct POINT
	{
		public int X, Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAPINFOHEADER
	{
		public uint biSize;
		public int biWidth;
		public int biHeight;
		public ushort biPlanes;
		public ushort biBitCount;
		public uint biCompression;
		public uint biSizeImage;
		public int biXPelsPerMeter;
		public int biYPelsPerMeter;
		public uint biClrUsed;
		public uint biClrImportant;
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	public static partial bool GetWindowRect(nint hwnd, out RECT rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

	[LibraryImport("user32.dll")]
	private static partial nint GetDC(nint hwnd);

	[LibraryImport("user32.dll")]
	private static partial int ReleaseDC(nint hwnd, nint hdc);

	[LibraryImport("gdi32.dll")]
	private static partial nint CreateCompatibleDC(nint hdc);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteDC(nint hdc);

	[LibraryImport("gdi32.dll")]
	private static unsafe partial nint CreateDIBSection(nint hdc, BITMAPINFOHEADER* info, uint usage, out nint bits, nint section, uint offset);

	[LibraryImport("gdi32.dll")]
	private static partial nint SelectObject(nint hdc, nint obj);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteObject(nint obj);
}
