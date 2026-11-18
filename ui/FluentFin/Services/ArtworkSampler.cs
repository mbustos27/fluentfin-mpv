using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: for an item with no BlurHash, the artwork itself sampled down to 16x9 pixels (the browser-cached copy the window's
/// backdrop already loads), as linear-sRGB cells for <c>NixiePalette.HueFromCells</c>. Null when it cannot be read.
/// </summary>
public static class ArtworkSampler
{
	public const int Width = 16;
	public const int Height = 9;

	public static async Task<(double R, double G, double B)[]?> SampleAsync(Uri uri)
	{
		try
		{
			using var stream = await RandomAccessStreamReference.CreateFromUri(uri).OpenReadAsync();
			var decoder = await BitmapDecoder.CreateAsync(stream);
			var transform = new BitmapTransform { ScaledWidth = Width, ScaledHeight = Height, InterpolationMode = BitmapInterpolationMode.Fant };
			var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
			var pixels = data.DetachPixelData();
			if (pixels.Length < Width * Height * 4)
			{
				return null;
			}

			var cells = new (double R, double G, double B)[Width * Height];
			for (var i = 0; i < cells.Length; i++)
			{
				cells[i] = (Core.Theming.BlurHash.SrgbToLinear(pixels[i * 4 + 2]), Core.Theming.BlurHash.SrgbToLinear(pixels[i * 4 + 1]), Core.Theming.BlurHash.SrgbToLinear(pixels[i * 4]));
			}

			return cells;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
