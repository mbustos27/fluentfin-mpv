namespace FluentFin.Core.Theming;

/// <summary>
/// Seaglass: the eight colours one title gives the whole window. Only the HUE comes from the artwork (its BlurHash, decoded to
/// 16x9 cells, scored the way Material's colour utilities score a source colour and then pulled toward teal); lightness and
/// chroma are Seaglass's own, so contrast is a property of the ladder, not something checked per title. The ladder is then
/// checked against the worst case (the brightest the backdrop can be after its scrim) and adjusted until the text passes.
/// </summary>
public sealed class SeaglassPalette
{
	/// <summary>The hue of an artwork with no colour (grey, no hash): sea glass.</summary>
	public const double DefaultHue = 175;

	/// <summary>How far the artwork's hue is pulled toward teal (OKLCH 175), 0..1.</summary>
	public const double TealPull = 0.5;

	/// <summary>The hues sea glass comes in, as degrees from teal (175): seafoam and green on one side (-35), cobalt on the other (+75). Every title lands inside.</summary>
	public const double HueBelow = -35;
	public const double HueAbove = 75;

	/// <summary>The brightest the page backdrop gets under its scrim (relative luminance, about L 0.67).</summary>
	public const double BackdropCeiling = 0.30;

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

		return ClampToSeaGlass(OkLab.LerpHue(best, DefaultHue, TealPull));
	}

	/// <summary>The hue inside the sea-glass range (seafoam to cobalt) nearest to <paramref name="hue"/>.</summary>
	public static double ClampToSeaGlass(double hue)
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
			var tint = OkLab.ToRgb(tintL, 0.045, hue);
			var text = OkLab.ToRgb(textL, 0.01, hue);
			var muted = OkLab.ToRgb(Math.Min(textL - 0.14, 0.85), 0.02, hue);
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
		palette.GlassTint = OkLab.ToRgb(tintL, 0.045, hue);
		palette.Background = OkLab.ToRgb(Math.Max(tintL - 0.07, 0.08), 0.03, hue);
		palette.ContainerHigh = OkLab.ToRgb(tintL + 0.03, 0.05, hue);
		palette.OnSurface = OkLab.ToRgb(textL, 0.01, hue);
		palette.Muted = OkLab.ToRgb(Math.Min(textL - 0.14, 0.85), 0.02, hue);
		palette.Accent = OkLab.ToRgb(0.78, 0.11, hue);
		palette.OnAccent = OkLab.ToRgb(0.18, 0.04, hue);
		// the rim goes from the seed hue toward violet (the faint chromatic fringe)
		palette.Rim = OkLab.ToRgb(0.85, 0.08, hue);
		palette.RimAlt = OkLab.ToRgb(0.75, 0.10, (hue + 50) % 360);
		palette.Adjustments = adjustments;
		return palette;
	}

	/// <summary>
	/// The light theme: the same hue, a pale tinted glass (L 0.95) with dark text (L 0.22), checked on the worst case, a dark
	/// backdrop under the glass. The assertion raises the tint's lightness, then its alpha, then darkens the text.
	/// </summary>
	private static SeaglassPalette BuildLight(double hue, bool solid)
	{
		hue = (hue % 360 + 360) % 360;
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
			var text = OkLab.ToRgb(textL, 0.015, hue);
			var muted = OkLab.ToRgb(Math.Max(textL + 0.18, 0.1), 0.02, hue);
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
		palette.OnSurface = OkLab.ToRgb(textL, 0.015, hue);
		palette.Muted = OkLab.ToRgb(Math.Max(textL + 0.18, 0.1), 0.02, hue);
		// the accent is deeper on light, with white text on it
		palette.Accent = OkLab.ToRgb(0.50, 0.11, hue);
		palette.OnAccent = OkLab.ToRgb(0.98, 0.01, hue);
		palette.Rim = OkLab.ToRgb(0.45, 0.08, hue);
		palette.RimAlt = OkLab.ToRgb(0.55, 0.10, (hue + 50) % 360);
		palette.Adjustments = adjustments;
		return palette;
	}
}
