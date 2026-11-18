namespace FluentFin.Core.Theming;

/// <summary>Where the dusk scene is drawn in full, as a strip, or not at all.</summary>
public enum DuskSceneKind
{
	/// <summary>Artwork is behind the window (or high contrast): no scene.</summary>
	None,
	/// <summary>Pages inside the shell with no artwork (Settings, the Library): a quiet strip of hills along the bottom.</summary>
	Strip,
	/// <summary>Sign-in, the server page and empty states: the whole scene (banded sky, striped sun, hills, starbursts, CRT lines).</summary>
	Full,
}

/// <summary>The hill layers, far to near.</summary>
public enum DuskHill
{
	Far,
	Mesa,
	Near,
}

/// <summary>
/// 1.1.4: the colours, sizes and shapes of the flat dusk scene drawn behind the window when no artwork is shown
/// (docs/design/DESIGN.md: atomic-age Americana, Googie starbursts, a striped sun over desert hills). Drawn in XAML shapes
/// and gradients by AmbientBackdropView; no image files. The full scene is for pages whose text all sits on a glass card
/// (sign-in, the server page, empty states); elsewhere only a strip of hills, faint enough that text placed straight on the
/// backdrop keeps 4.5:1 (no strip colour brighter than <see cref="DarkCeiling"/> in the dark theme, none darker than
/// <see cref="LightFloor"/> in the light one).
/// </summary>
public static class DuskScene
{
	public static readonly Rgb Night = new(0x1A, 0x07, 0x10);
	public static readonly Rgb Maroon = new(0x50, 0x07, 0x23);
	public static readonly Rgb Crimson = new(0xBC, 0x1E, 0x47);
	public static readonly Rgb Rose = new(0xE6, 0x38, 0x5F);
	public static readonly Rgb HotPink = new(0xF9, 0x69, 0x85);
	public static readonly Rgb Coral = new(0xEC, 0x6A, 0x79);
	public static readonly Rgb WarmCoral = new(0xF3, 0x8C, 0x7D);
	public static readonly Rgb Peach = new(0xF7, 0xAA, 0x9C);
	public static readonly Rgb Sun = new(0xF5, 0xA8, 0xA5);
	public static readonly Rgb NeonCore = new(0xFF, 0xF4, 0xEE);
	public static readonly Rgb Blush = new(0xF9, 0xC9, 0xBF);

	/// <summary>The brightest a strip colour may be in the dark theme: the muted glass text (L 0.82) keeps 4.5:1 on it.</summary>
	public const double DarkCeiling = 0.08;

	/// <summary>The darkest a strip colour may be in the light theme: the light theme's muted text keeps 4.5:1 on it.</summary>
	public const double LightFloor = 0.50;

	/// <summary>The hills' share of the window's height: the full scene, and the strip.</summary>
	public const double SceneHillsHeight = 0.22;
	public const double StripHillsHeight = 0.12;

	/// <summary>How much of the hills the strip shows over its soft sky (dark theme, light theme).</summary>
	/// <remarks>The art pass: a little fainter once the shell's grey content layer no longer washed it (a quiet horizon, not a band).</remarks>
	public static double StripOpacity(bool light) => light ? 0.2 : 0.28;

	/// <summary>The CRT touches, full scene only: a dark line every 3 DIP at this alpha, and a vignette darkest at the corners.</summary>
	public const double ScanlinePeriod = 3;

	/// <summary>
	/// The scanlines' strength: faint at dusk (a texture you notice, not stripes), none at noon (the art pass: on the pale sky they
	/// read as grey hatching).
	/// </summary>
	public static double ScanlineAlpha(bool light) => light ? 0 : 0.035;

	/// <summary>Half the width of the soft edge between two sky bands, as a share of the sky's height (the art pass: hard edges read harsh).</summary>
	public const double BandEdge = 0.012;
	public static double VignetteAlpha(bool light) => light ? 0.10 : 0.28;

	/// <summary>
	/// The sky as flat bands, top to bottom (the colour stops of one gradient, each repeated where a band ends). Dark: maroon at
	/// the top through crimson, rose, coral and hot pink to peach at the horizon. Light ("noon"): blush and pale sun at the top to
	/// peach and coral at the horizon. The top band, where the title bar's text sits, keeps that text at 4.5:1 or more.
	/// </summary>
	public static IReadOnlyList<(Rgb Color, double From, double To)> SkyBands(bool light) => light
		? [(NeonCore, 0, 0.34), (Blush, 0.34, 0.5), (Sun, 0.5, 0.62), (Peach, 0.62, 0.72), (WarmCoral, 0.72, 0.81), (Coral, 0.81, 1)]
		: [(Maroon, 0, 0.34), (Crimson, 0.34, 0.5), (Rose, 0.5, 0.62), (Coral, 0.62, 0.72), (HotPink, 0.72, 0.81), (Peach, 0.81, 1)];

	/// <summary>The strip's sky: a soft gradient from the window's deep colour to a little warmth near the hills.</summary>
	public static (Rgb Top, Rgb Bottom) SoftSky(bool light) => light
		? (new Rgb(0xFF, 0xF6, 0xF3), Landscape(Peach, 0.18, light))
		: (new Rgb(0x17, 0x05, 0x08), Landscape(Maroon, 0.55, light));

	/// <summary>A hill's colour: far rose, the crimson mesa, the near maroon (light theme: the same hills, the painted noon version).</summary>
	public static Rgb Hill(DuskHill hill, bool light) => (hill, light) switch
	{
		(DuskHill.Far, false) => Landscape(Rose, 0.85, light),
		(DuskHill.Mesa, false) => Crimson,
		(DuskHill.Near, false) => Maroon,
		(DuskHill.Far, true) => Landscape(Rose, 0.60, light),
		(DuskHill.Mesa, true) => Crimson,
		_ => Maroon,
	};

	/// <summary>The striped sun's gradient, top to bottom, and its rings' colour and the starbursts' (with their opacity).</summary>
	public static (Rgb Top, Rgb Bottom) SunGradient(bool light) => light ? (NeonCore, Coral) : (Sun, HotPink);

	public static Rgb RingColor(bool light) => light ? Crimson : Sun;

	public static readonly double[] RingOpacity = [0.35, 0.22, 0.12];

	public static Rgb StarColor(bool light) => light ? Rose : Sun;

	public const double StarOpacity = 0.72;

	/// <summary><paramref name="color"/> at <paramref name="alpha"/> over the theme's sky base (Night, or the pale noon base).</summary>
	public static Rgb Landscape(Rgb color, double alpha, bool light) => Blend(color, alpha, light ? new Rgb(0xFF, 0xF6, 0xF3) : Night);

	/// <summary>Every colour the strip can put behind text: its sky, and each hill at the strip's opacity over the sky's bottom.</summary>
	public static IEnumerable<Rgb> StripColors(bool light)
	{
		var (top, bottom) = SoftSky(light);
		yield return top;
		yield return bottom;
		foreach (var hill in Enum.GetValues<DuskHill>())
		{
			yield return Blend(Hill(hill, light), StripOpacity(light), bottom);
		}
	}

	/// <summary>The striped sun's slices, as (top, bottom) from the disc's top, for a radius: wide at the top, thinner and further apart toward the horizon.</summary>
	public static IReadOnlyList<(double Top, double Bottom)> SunBands(double radius)
	{
		// for a radius of 120: (0,132) (138,156) (162,176) (184,194) (204,210) (222,226)
		double[] edges = [0, 132, 138, 156, 162, 176, 184, 194, 204, 210, 222, 226];
		var k = radius / 120;
		var bands = new List<(double, double)>();
		for (var i = 0; i < edges.Length; i += 2)
		{
			bands.Add((edges[i] * k, edges[i + 1] * k));
		}

		return bands;
	}

	/// <summary>
	/// A four-point sparkle in a 2r square, its sides curved in (the art pass: straight spikes looked thin and cheap): the start, then
	/// four quadratic curves (control, end), tip to tip, each curve pulled toward the centre to <paramref name="waist"/> of r.
	/// <paramref name="angle"/> turns it (45: the diagonal one).
	/// </summary>
	public static ((double X, double Y) Start, IReadOnlyList<((double X, double Y) Control, (double X, double Y) End)> Curves) Sparkle(double radius, double waist, double angle = 0)
	{
		(double, double) At(double length, double degrees)
		{
			var a = (degrees + angle - 90) * Math.PI / 180;
			return (radius + length * Math.Cos(a), radius + length * Math.Sin(a));
		}

		var curves = new List<((double, double), (double, double))>(4);
		for (var i = 0; i < 4; i++)
		{
			curves.Add((At(radius * waist, i * 90 + 45), At(radius, (i + 1) * 90)));
		}

		return (At(radius, 0), curves);
	}

	/// <summary>The starbursts of the full scene: centre (as fractions of the window), radius (as a fraction of its height), rays, clock-face.</summary>
	public static readonly (double X, double Y, double Radius, int Points, bool Alternate)[] Stars =
	[
		// the art pass: none where a page's title can fall in a narrow window (left of 0.5, below the title bar, above 0.3);
		// the left ones sit in the strip between the title bar and the page title
		(0.24, 0.075, 0.015, 8, true),
		(0.44, 0.08, 0.010, 4, false),
		(0.75, 0.06, 0.008, 4, false),
		(0.86, 0.16, 0.022, 8, true),
		(0.66, 0.27, 0.008, 4, false),
		(0.95, 0.40, 0.011, 4, false),
	];

	/// <summary>The sun: its centre's x (a fraction of the width), its radius (a fraction of the height), how far it sinks behind the hills.</summary>
	public const double SunX = 0.74;
	public const double SunRadius = 0.17;
	public const double SunSink = 0.12;

	/// <summary>An sRGB alpha blend (what the compositor does).</summary>
	public static Rgb Blend(Rgb top, double alpha, Rgb bottom)
	{
		static byte Channel(byte t, byte b, double a) => (byte)Math.Round(Math.Clamp(a * t + (1 - a) * b, 0, 255));
		return new(Channel(top.R, bottom.R, alpha), Channel(top.G, bottom.G, alpha), Channel(top.B, bottom.B, alpha));
	}
}
