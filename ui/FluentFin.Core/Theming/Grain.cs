namespace FluentFin.Core.Theming;

/// <summary>
/// Seaglass: the frosted-glass grain, generated at run time (no asset file): zero-mean monochrome noise, each pixel white or
/// black with a random strength, premultiplied BGRA. Every pixel is independent, so the picture has no low-frequency
/// structure and tiles without a seam. Shown at 3 to 5 % opacity over glass.
/// </summary>
public static class Grain
{
	public const int Size = 256;

	public static byte[] Generate(int size = Size, int seed = 0x5EA61A55)
	{
		var random = new Random(seed);
		var pixels = new byte[size * size * 4];
		for (var i = 0; i < size * size; i++)
		{
			var v = random.Next(256);
			var alpha = (byte)Math.Min(255, Math.Abs(v - 128) * 2);
			var white = v >= 128 ? alpha : (byte)0;
			pixels[i * 4] = white;
			pixels[i * 4 + 1] = white;
			pixels[i * 4 + 2] = white;
			pixels[i * 4 + 3] = alpha;
		}

		return pixels;
	}

	/// <summary>
	/// The signed value of a pixel (white strength positive, black negative), -1..1. A seam test compares how neighbours
	/// across the tile's wrap edge correlate with neighbours inside it: for noise both are about 0.
	/// </summary>
	public static double Signed(byte[] pixels, int size, int x, int y)
	{
		var i = (y * size + x) * 4;
		var alpha = pixels[i + 3] / 255.0;
		return pixels[i] > 0 ? alpha : -alpha;
	}
}
