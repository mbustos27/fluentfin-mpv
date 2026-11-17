namespace FluentFin.Core.Theming;

/// <summary>
/// The app's palette (1.1.4: a desert sunset, docs/design/SEAGLASS.md): the chrome's ladder is built from one hue, the deep
/// maroon end of the sunset (<see cref="DefaultHue"/>) for the dark theme and the pale sun and peach end for the light one, with
/// a hot-pink (dark) or crimson (light) accent. A title's artwork gives only a HUE (its BlurHash, decoded to 16x9 cells, scored
/// the way Material's colour utilities score a source colour, pulled toward the sunset and clamped to its hues), and that hue
/// only tints the backdrop (TitleTint). Lightness and chroma are the app's own, so contrast is a property of the ladder: it is
/// checked against the worst case (the brightest the backdrop can be after its scrim) and adjusted until the text passes.
/// </summary>
public sealed class SeaglassPalette
{
	/// <summary>The hue of the dark chrome and of an artwork with no colour (grey, no hash): the sunset's deep maroon (#500723 is OKLCH 4.9).</summary>
	public const double DefaultHue = 8;

	/// <summary>The light theme's hue: the pale sun and the peach hill (#F7AFA9 is OKLCH 24, #F7AA9C 31).</summary>
	public const double LightHue = 26;

	/// <summary>How far the artwork's hue is pulled toward the palette's (<see cref="DefaultHue"/>), 0..1.</summary>
	public const double PalettePull = 0.5;

	/// <summary>
	/// The hues of the sunset, as degrees from <see cref="DefaultHue"/>: magenta-rose on one side (-25, 343), coral and peach on the
	/// other (+35, 43). Every title's tint lands inside.
	/// </summary>
	public const double HueBelow = -25;
	public const double HueAbove = 35;

	/// <summary>The brightest the page backdrop gets under its scrim (relative luminance, about L 0.67).</summary>
	public const double BackdropCeiling = 0.22;

	/// <summary>
	/// The accent is the sunset's hot pink in the dark theme (its label the deep maroon-black: 6.7:1) and its crimson slope in the
	/// light one (white label: 6.2:1), whatever the title (the chrome is one fixed palette; the title only tints the backdrop).
	/// </summary>
	public static readonly Rgb AccentDark = new(0xF9, 0x69, 0x85);

	public static readonly Rgb OnAccentDark = new(0x24, 0x06, 0x0F);

	public static readonly Rgb AccentLight = new(0xBC, 0x1E, 0x47);

	public static readonly Rgb OnAccentLight = new(0xFF, 0xFF, 0xFF);

	public static readonly Rgb AccentFillLight = new(0xBC, 0x1E, 0x47);

	/// <summary>The chroma of the dark glass: a calm deep plum, never the saturated maroon itself.</summary>
	public const double DarkTintChroma = 0.04;

	public const double BodyContrast = 4.5;
	public const double SmallTextContrast = 7;

	public double Hue { get; private init; }

	public bool Solid { get; private init; }

	/// <summary>The light theme's ladder: a pale tint with dark text, over a backdrop at least <see cref="LightBackdropFloor"/> bright.</summary>
	public bool Light { get; private init; }

	/// <summary>The darkest the page backdrop gets in the light theme (relative luminance), the worst case for dark text on a pale tint.</summary>
	public const double LightBackdropFloor = 0.50;

	public Rgb Background { get; private set; }

	/// <summary>The glass: the tint panels are filled with (at <see cref="TintAlpha"/> over the backdrop).</summary>
	public Rgb GlassTint { get; private set; }

	public Rgb ContainerHigh { get; private set; }

	public Rgb OnSurface { get; private set; }

	public Rgb Muted { get; private set; }

	public Rgb Accent { get; private set; }

	public Rgb OnAccent { get; private set; }

	/// <summary>The fill of an accent button: the accent, in the light theme a deeper aqua so white text on it reaches 4.5:1.</summary>
	public Rgb AccentFill { get; private set; }

	public Rgb Rim { get; private set; }

	public Rgb RimAlt { get; private set; }

	/// <summary>How opaque the tint is on text panels (1 in Solid).</summary>
	public double TintAlpha { get; private set; }

	/// <summary>The lowest text contrast on the worst case: on-surface and muted text over the glass over the brightest backdrop.</summary>
	public double WorstContrast { get; private set; }

	/// <summary>What the runtime assertion had to change (tint lightness, tint alpha, text lightness), for the log.</summary>
	public IReadOnlyList<string> Adjustments { get; private set; } = [];

	/// <summary>The seed hue of an artwork's BlurHash, with the teal pull; the default hue for no hash or no colour.</summary>
	public static double HueFromBlurHash(string? hash)
	{
		const int width = 16;
		const int height = 9;
		var cells = BlurHash.Decode(hash, width, height);
		return cells is null ? DefaultHue : HueFromCells(cells);
	}

	/// <summary>
	/// The seed hue of a small grid of linear-sRGB cells (a decoded BlurHash, or a thumbnail sampled down to a few pixels when an
	/// item has no hash), scored and pulled toward teal as <see cref="HueFromBlurHash"/> does.
	/// </summary>
	public static double HueFromCells(IReadOnlyList<(double R, double G, double B)> cells)
	{
		var population = new double[360];
		var chroma = new double[360];
		var total = 0;
		foreach (var (r, g, b) in cells)
		{
			var (_, c, h) = OkLab.FromLinear(r, g, b);
			if (c < 0.02)
			{
				continue;
			}

			var bucket = (int)Math.Floor(h) % 360;
			population[bucket]++;
			chroma[bucket] += c;
			total++;
		}

		if (total == 0)
		{
			return DefaultHue;
		}

		const double target = 0.12;
		var best = DefaultHue;
		var bestScore = double.MinValue;
		for (var bucket = 0; bucket < 360; bucket++)
		{
			double share = 0, sum = 0, count = 0;
			for (var d = -15; d <= 15; d++)
			{
				var i = (bucket + d + 360) % 360;
				share += population[i];
				sum += chroma[i];
				count += population[i];
			}

			if (count == 0)
			{
				continue;
			}

			var average = sum / count;
			var score = share / total * 0.7 + (average - target) * (average < target ? 0.1 : 0.3);
			if (score > bestScore)
			{
				bestScore = score;
				best = bucket;
			}
		}

		return ClampToPaletteHues(OkLab.LerpHue(best, DefaultHue, PalettePull));
	}

	/// <summary>The hue inside the sunset's range (magenta-rose to peach) nearest to <paramref name="hue"/>.</summary>
	public static double ClampToPaletteHues(double hue)
	{
		var offset = ((hue - DefaultHue + 540) % 360) - 180;
		return (DefaultHue + Math.Clamp(offset, HueBelow, HueAbove) + 360) % 360;
	}

	public static SeaglassPalette FromBlurHash(string? hash, bool solid = false, bool light = false) => Build(HueFromBlurHash(hash), solid, light);

	public static SeaglassPalette Build(double hue, bool solid = false, bool light = false)
	{
		if (light)
		{
			return BuildLight(hue, solid);
		}

		hue = (hue % 360 + 360) % 360;
		var adjustments = new List<string>();
		var palette = new SeaglassPalette { Hue = hue, Solid = solid };
		var tintL = solid ? 0.20 : 0.22;
		var tintAlpha = solid ? 1.0 : 0.82;
		var textL = 0.96;
		var goal = solid ? SmallTextContrast : BodyContrast;
		// the worst case: a glass tint over the brightest backdrop (in Solid there is nothing under it)
		var under = solid ? 0 : BackdropCeiling;

		for (var step = 0; step < 40; step++)
		{
			var tint = OkLab.ToRgb(tintL, DarkTintChroma, hue);
			var text = OkLab.ToRgb(textL, 0.010, hue);
			var muted = OkLab.ToRgb(Math.Min(textL - 0.14, 0.85), 0.022, hue);
			var surface = OkLab.CompositeLuminance(tint, tintAlpha, under);
			var worst = Math.Min(OkLab.Contrast(text.Luminance, surface), OkLab.Contrast(muted.Luminance, surface));
			palette.WorstContrast = worst;
			if (worst >= goal)
			{
				break;
			}

			// lower the tint's lightness, then raise its alpha, then raise the text's lightness
			if (tintL > 0.12)
			{
				tintL -= 0.01;
				adjustments.Add($"tint L {tintL:0.00}");
			}
			else if (tintAlpha < 0.95 && !solid)
			{
				tintAlpha += 0.02;
				adjustments.Add($"tint alpha {tintAlpha:0.00}");
			}
			else if (textL < 0.99)
			{
				textL += 0.01;
				adjustments.Add($"text L {textL:0.00}");
			}
			else
			{
				break;
			}
		}

		palette.TintAlpha = tintAlpha;
		palette.GlassTint = OkLab.ToRgb(tintL, DarkTintChroma, hue);
		palette.Background = OkLab.ToRgb(Math.Max(tintL - 0.07, 0.08), 0.035, hue);
		palette.ContainerHigh = OkLab.ToRgb(tintL + 0.03, 0.045, hue);
		palette.OnSurface = OkLab.ToRgb(textL, 0.010, hue);
		palette.Muted = OkLab.ToRgb(Math.Min(textL - 0.14, 0.85), 0.022, hue);
		palette.Accent = AccentDark;
		palette.AccentFill = AccentDark;
		palette.OnAccent = OnAccentDark;
		// the rim: the pale sun, and the coral it fades to
		palette.Rim = OkLab.ToRgb(0.85, 0.08, hue + 16);
		palette.RimAlt = OkLab.ToRgb(0.75, 0.12, hue + 8);
		palette.Adjustments = adjustments;
		return palette;
	}

	/// <summary>
	/// The light theme: the glass from the pale sun and peach end (the hue moved by <see cref="LightHue"/> - <see cref="DefaultHue"/>),
	/// a pale warm tint (L 0.95) with deep maroon text (L 0.22, the dark theme's hue), checked on the worst case, a dark backdrop under
	/// the glass. The assertion raises the tint's lightness, then its alpha, then darkens the text.
	/// </summary>
	private static SeaglassPalette BuildLight(double hue, bool solid)
	{
		hue = (hue % 360 + 360) % 360;
		var textHue = hue;
		hue = (hue + LightHue - DefaultHue + 360) % 360;
		var adjustments = new List<string>();
		var palette = new SeaglassPalette { Hue = hue, Solid = solid, Light = true };
		var tintL = 0.95;
		var tintAlpha = solid ? 1.0 : 0.82;
		var textL = 0.22;
		var goal = solid ? SmallTextContrast : BodyContrast;
		var under = solid ? 1 : LightBackdropFloor;
		for (var step = 0; step < 40; step++)
		{
			var tint = OkLab.ToRgb(tintL, 0.02, hue);
			var text = OkLab.ToRgb(textL, 0.05, textHue);
			var muted = OkLab.ToRgb(Math.Max(textL + 0.18, 0.1), 0.05, textHue);
			var surface = OkLab.CompositeLuminance(tint, tintAlpha, under);
			var worst = Math.Min(OkLab.Contrast(text.Luminance, surface), OkLab.Contrast(muted.Luminance, surface));
			palette.WorstContrast = worst;
			if (worst >= goal)
			{
				break;
			}

			if (tintL < 0.99)
			{
				tintL += 0.01;
				adjustments.Add($"tint L {tintL:0.00}");
			}
			else if (tintAlpha < 0.95 && !solid)
			{
				tintAlpha += 0.02;
				adjustments.Add($"tint alpha {tintAlpha:0.00}");
			}
			else if (textL > 0.05)
			{
				textL -= 0.01;
				adjustments.Add($"text L {textL:0.00}");
			}
			else
			{
				break;
			}
		}

		palette.TintAlpha = tintAlpha;
		palette.GlassTint = OkLab.ToRgb(tintL, 0.02, hue);
		palette.Background = OkLab.ToRgb(Math.Min(tintL + 0.02, 0.985), 0.012, hue);
		palette.ContainerHigh = OkLab.ToRgb(Math.Min(tintL + 0.03, 0.99), 0.015, hue);
		palette.OnSurface = OkLab.ToRgb(textL, 0.05, textHue);
		palette.Muted = OkLab.ToRgb(Math.Max(textL + 0.18, 0.1), 0.05, textHue);
		// the accent is deeper on light, with white text on it
		palette.Accent = AccentLight;
		palette.AccentFill = AccentFillLight;
		palette.OnAccent = OnAccentLight;
		palette.Rim = OkLab.ToRgb(0.45, 0.08, textHue);
		palette.RimAlt = OkLab.ToRgb(0.55, 0.12, textHue);
		palette.Adjustments = adjustments;
		return palette;
	}
}
