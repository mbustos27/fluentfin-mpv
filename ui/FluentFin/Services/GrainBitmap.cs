using System.Runtime.InteropServices.WindowsRuntime;
using FluentFin.Core.Theming;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Services;

/// <summary>Seaglass: the frosted-glass grain as one shared bitmap, generated the first time it is needed (no asset file).</summary>
public static class GrainBitmap
{
	private static WriteableBitmap? _bitmap;

	public static WriteableBitmap Shared
	{
		get
		{
			if (_bitmap is null)
			{
				var bitmap = new WriteableBitmap(Grain.Size, Grain.Size);
				var pixels = Grain.Generate();
				pixels.CopyTo(0, bitmap.PixelBuffer, 0, pixels.Length);
				bitmap.Invalidate();
				_bitmap = bitmap;
			}

			return _bitmap;
		}
	}
}
