namespace FluentFin.Core.Theming;

/// <summary>
/// Seaglass: a BlurHash (the short string Jellyfin sends with every image, https://github.com/woltapp/blurhash) decoded to a
/// small grid of linear-sRGB cells (the cosine sum of the format's own algorithm). No packages: about fifty lines.
/// </summary>
public static class BlurHash
{
	private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";

	/// <summary>The cells (row by row, <paramref name="width"/> x <paramref name="height"/>) as linear sRGB 0..1, or null when the hash is not valid.</summary>
	public static (double R, double G, double B)[]? Decode(string? hash, int width, int height)
	{
		if (string.IsNullOrEmpty(hash) || hash.Length < 6 || width < 1 || height < 1)
		{
			return null;
		}

		var size = Base83(hash, 0, 1);
		if (size < 0)
		{
			return null;
		}

		var componentsY = size / 9 + 1;
		var componentsX = size % 9 + 1;
		if (hash.Length != 4 + 2 * componentsX * componentsY)
		{
			return null;
		}

		var quantisedMax = Base83(hash, 1, 1);
		var dc = Base83(hash, 2, 4);
		if (quantisedMax < 0 || dc < 0)
		{
			return null;
		}

		var maximum = (quantisedMax + 1) / 166.0;
		var colors = new (double R, double G, double B)[componentsX * componentsY];
		colors[0] = (SrgbToLinear(dc >> 16), SrgbToLinear((dc >> 8) & 255), SrgbToLinear(dc & 255));
		for (var i = 1; i < colors.Length; i++)
		{
			var value = Base83(hash, 4 + i * 2, 2);
			if (value < 0)
			{
				return null;
			}

			colors[i] = (Ac(value / (19 * 19), maximum), Ac(value / 19 % 19, maximum), Ac(value % 19, maximum));
		}

		var cells = new (double R, double G, double B)[width * height];
		for (var y = 0; y < height; y++)
		{
			for (var x = 0; x < width; x++)
			{
				double r = 0, g = 0, b = 0;
				for (var j = 0; j < componentsY; j++)
				{
					for (var i = 0; i < componentsX; i++)
					{
						var basis = Math.Cos(Math.PI * i * x / width) * Math.Cos(Math.PI * j * y / height);
						var c = colors[j * componentsX + i];
						r += c.R * basis;
						g += c.G * basis;
						b += c.B * basis;
					}
				}

				cells[y * width + x] = (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
			}
		}

		return cells;
	}

	private static double Ac(int quantised, double maximum)
	{
		var v = (quantised - 9) / 9.0;
		return Math.Sign(v) * v * v * maximum;
	}

	private static int Base83(string s, int start, int length)
	{
		var value = 0;
		for (var i = start; i < start + length; i++)
		{
			var d = Alphabet.IndexOf(s[i]);
			if (d < 0)
			{
				return -1;
			}

			value = value * 83 + d;
		}

		return value;
	}

	public static double SrgbToLinear(int value)
	{
		var v = value / 255.0;
		return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
	}
}
