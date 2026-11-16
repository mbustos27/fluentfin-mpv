namespace FluentFin.Core.Imaging;

/// <summary>
/// Seaglass: a decoder for BlurHash (https://blurha.sh), the few characters Jellyfin keeps per image
/// (<c>BaseItemDto.ImageBlurHashes</c>), of our own: pure C#, no package. Cards draw the decoded blur until the real
/// artwork has loaded and fades into it.
/// </summary>
public static class BlurHash
{
	private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

	// 83 digits as a lookup by character; -1 for a character outside the alphabet
	private static readonly int[] s_digit = BuildDigits();

	private static int[] BuildDigits()
	{
		var table = new int[128];
		Array.Fill(table, -1);
		for (var i = 0; i < Alphabet.Length; i++)
		{
			table[Alphabet[i]] = i;
		}
		return table;
	}

	/// <summary>The number of colour components a hash was made with (x, y), or null when it is not a valid hash.</summary>
	public static (int X, int Y)? Components(string? hash)
	{
		if (hash is null || hash.Length < 6 || Digits(hash, 0, 1) is not { } size)
		{
			return null;
		}

		var x = size % 9 + 1;
		var y = size / 9 + 1;
		return hash.Length == 4 + 2 * x * y && Digits(hash, 0, hash.Length) is not null ? (x, y) : null;
	}

	/// <summary>
	/// Decodes <paramref name="hash"/> to a <paramref name="width"/> x <paramref name="height"/> picture of opaque
	/// pixels, 4 bytes each in the order blue, green, red, alpha (Windows' BGRA), row by row; null when the hash is invalid.
	/// </summary>
	/// <param name="punch">1 is the hash's own contrast; more makes the colours stronger.</param>
	public static byte[]? Decode(string? hash, int width, int height, double punch = 1)
	{
		if (width <= 0 || height <= 0 || Components(hash) is not var (nx, ny))
		{
			return null;
		}

		var maxValue = ((Digits(hash!, 1, 1) ?? 0) + 1) / 166.0 * punch;
		var colours = new (double R, double G, double B)[nx * ny];
		if (Digits(hash!, 2, 4) is not { } dc)
		{
			return null;
		}
		colours[0] = (SrgbToLinear(dc >> 16), SrgbToLinear((dc >> 8) & 255), SrgbToLinear(dc & 255));

		for (var i = 1; i < colours.Length; i++)
		{
			if (Digits(hash!, 4 + i * 2, 2) is not { } ac)
			{
				return null;
			}
			colours[i] = (AcValue(ac / (19 * 19), maxValue), AcValue(ac / 19 % 19, maxValue), AcValue(ac % 19, maxValue));
		}

		// the cosines are the same for every row (columns) and every column (rows): once each
		var cosX = Basis(width, nx);
		var cosY = Basis(height, ny);
		var pixels = new byte[width * height * 4];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				double r = 0, g = 0, b = 0;
				for (var j = 0; j < ny; j++)
				{
					for (var i = 0; i < nx; i++)
					{
						var basis = cosX[x * nx + i] * cosY[y * ny + j];
						var c = colours[j * nx + i];
						r += c.R * basis;
						g += c.G * basis;
						b += c.B * basis;
					}
				}

				var o = (y * width + x) * 4;
				pixels[o] = LinearToSrgb(b);
				pixels[o + 1] = LinearToSrgb(g);
				pixels[o + 2] = LinearToSrgb(r);
				pixels[o + 3] = 255;
			}
		}

		return pixels;
	}

	private static double[] Basis(int size, int components)
	{
		var table = new double[size * components];
		for (var p = 0; p < size; p++)
		{
			for (var c = 0; c < components; c++)
			{
				table[p * components + c] = Math.Cos(Math.PI * p * c / size);
			}
		}
		return table;
	}

	/// <summary>The value of <paramref name="count"/> base-83 digits from <paramref name="start"/>, or null for a bad character.</summary>
	private static int? Digits(string hash, int start, int count)
	{
		var value = 0;
		for (var i = start; i < start + count; i++)
		{
			var c = hash[i];
			if (c >= 128 || s_digit[c] < 0)
			{
				return null;
			}
			value = value * 83 + s_digit[c];
		}
		return value;
	}

	private static double AcValue(int quantised, double maxValue)
	{
		var v = (quantised - 9) / 9.0;
		return Math.Sign(v) * v * v * maxValue;
	}

	private static double SrgbToLinear(int value)
	{
		var v = value / 255.0;
		return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
	}

	private static byte LinearToSrgb(double value)
	{
		var v = Math.Clamp(value, 0, 1);
		var s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
		return (byte)(s * 255 + 0.5);
	}
}
