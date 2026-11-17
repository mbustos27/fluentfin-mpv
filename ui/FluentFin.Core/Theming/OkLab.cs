namespace FluentFin.Core.Theming;

/// <summary>An sRGB colour, 8 bits a channel.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
	public string Hex => $"#{R:X2}{G:X2}{B:X2}";

	/// <summary>WCAG relative luminance.</summary>
	public double Luminance => OkLab.Luminance(BlurHash.SrgbToLinear(R), BlurHash.SrgbToLinear(G), BlurHash.SrgbToLinear(B));
}

/// <summary>OKLab / OKLCH (Bjorn Ottosson's published matrices, https://bottosson.github.io/posts/oklab/) and WCAG contrast.</summary>
public static class OkLab
{
	public static (double L, double C, double H) FromLinear(double r, double g, double b)
	{
		var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
		var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
		var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
		var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
		var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
		var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
		var h = Math.Atan2(bb, a) * 180 / Math.PI;
		return (L, Math.Sqrt(a * a + bb * bb), h < 0 ? h + 360 : h);
	}

	private static (double R, double G, double B) ToLinear(double L, double C, double H)
	{
		var a = C * Math.Cos(H * Math.PI / 180);
		var b = C * Math.Sin(H * Math.PI / 180);
		var l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
		var m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
		var s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
		return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
			-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
			-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
	}

	private static bool InGamut((double R, double G, double B) c) => c.R is >= -0.0005 and <= 1.0005 && c.G is >= -0.0005 and <= 1.0005 && c.B is >= -0.0005 and <= 1.0005;

	/// <summary>The sRGB colour at this lightness, chroma and hue; chroma is lowered (never the lightness) until it fits the gamut.</summary>
	public static Rgb ToRgb(double L, double C, double H)
	{
		L = Math.Clamp(L, 0, 1);
		var lo = 0.0;
		var hi = Math.Max(C, 0);
		if (InGamut(ToLinear(L, hi, H)))
		{
			lo = hi;
		}
		else
		{
			for (var i = 0; i < 24; i++)
			{
				var mid = (lo + hi) / 2;
				if (InGamut(ToLinear(L, mid, H)))
				{
					lo = mid;
				}
				else
				{
					hi = mid;
				}
			}
		}

		var (r, g, b) = ToLinear(L, lo, H);
		return new Rgb(Encode(r), Encode(g), Encode(b));
	}

	private static byte Encode(double linear)
	{
		var v = Math.Clamp(linear, 0, 1);
		v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
		return (byte)Math.Round(v * 255);
	}

	public static double Luminance(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

	/// <summary>WCAG contrast ratio of two relative luminances.</summary>
	public static double Contrast(double y1, double y2) => (Math.Max(y1, y2) + 0.05) / (Math.Min(y1, y2) + 0.05);

	/// <summary>Colour <paramref name="top"/> at <paramref name="alpha"/> over <paramref name="under"/>, in linear light.</summary>
	public static double CompositeLuminance(Rgb top, double alpha, double underLuminance) => alpha * top.Luminance + (1 - alpha) * underLuminance;

	/// <summary>Shortest-arc hue interpolation, degrees.</summary>
	public static double LerpHue(double from, double to, double t)
	{
		var d = ((to - from + 540) % 360) - 180;
		return (from + d * t + 360) % 360;
	}

	/// <summary>A colour between two, mixed in OKLab (the crossfade between one title's palette and the next).</summary>
	public static Rgb Mix(Rgb a, Rgb b, double t)
	{
		var (al, aa, ab) = ToLab(a);
		var (bl, ba, bb) = ToLab(b);
		var L = al + (bl - al) * t;
		var A = aa + (ba - aa) * t;
		var B = ab + (bb - ab) * t;
		var c = Math.Sqrt(A * A + B * B);
		var h = Math.Atan2(B, A) * 180 / Math.PI;
		return ToRgb(L, c, h < 0 ? h + 360 : h);
	}

	private static (double L, double A, double B) ToLab(Rgb c)
	{
		var (l, ch, h) = FromLinear(BlurHash.SrgbToLinear(c.R), BlurHash.SrgbToLinear(c.G), BlurHash.SrgbToLinear(c.B));
		return (l, ch * Math.Cos(h * Math.PI / 180), ch * Math.Sin(h * Math.PI / 180));
	}
}
