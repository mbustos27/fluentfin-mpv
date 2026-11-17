using FluentFin.Core.Theming;

namespace FluentFin.Core.Tests;

/// <summary>Seaglass: the per-title palette. Whatever the artwork, text on the glass passes its contrast on the worst case.</summary>
public sealed class SeaglassPaletteTests
{
	// BlurHashes from the BlurHash reference examples: a pink/green/blue picture and a dark one
	private const string Colourful = "LEHV6nWB2yk8pyo0adR*.7kCMdnj";
	private const string Pastel = "LKO2?U%2Tw=w]~RBVZRi};RPxuwH";

	[Fact]
	public void BlurHash_decodes_to_the_grid_and_rejects_nonsense()
	{
		var cells = BlurHash.Decode(Colourful, 16, 9);
		Assert.NotNull(cells);
		Assert.Equal(16 * 9, cells!.Length);
		Assert.All(cells, c => Assert.InRange(c.R + c.G + c.B, 0, 3));
		Assert.Null(BlurHash.Decode(null, 16, 9));
		Assert.Null(BlurHash.Decode("short", 16, 9));
		Assert.Null(BlurHash.Decode("LEHV6nWB2yk8pyo0adR*.7kCMd!!", 16, 9));
	}

	[Fact]
	public void A_hash_becomes_a_small_smooth_opaque_picture()
	{
		var pixels = BlurHash.ToBgra(Colourful, 32, 18);
		Assert.NotNull(pixels);
		Assert.Equal(32 * 18 * 4, pixels!.Length);
		Assert.All(Enumerable.Range(0, 32 * 18), i => Assert.Equal(255, pixels[i * 4 + 3]));
		// smooth: no two neighbouring pixels differ by much (a 4x3-term cosine field has no edges)
		var steepest = 0;
		for (var y = 0; y < 18; y++)
		{
			for (var x = 0; x < 31; x++)
			{
				steepest = Math.Max(steepest, Math.Abs(pixels[(y * 32 + x) * 4 + 2] - pixels[(y * 32 + x + 1) * 4 + 2]));
			}
		}

		Assert.True(steepest < 60, $"steepest step {steepest}");
		Assert.Null(BlurHash.ToBgra("nope", 32, 18));
	}

	[Fact]
	public void No_hash_or_a_grey_one_gives_the_default_hue()
	{
		Assert.Equal(SeaglassPalette.DefaultHue, SeaglassPalette.HueFromBlurHash(null));
		Assert.Equal(SeaglassPalette.DefaultHue, SeaglassPalette.HueFromBlurHash("not a hash"));
		Assert.Equal(SeaglassPalette.DefaultHue, SeaglassPalette.HueFromBlurHash(SolidHash(128, 128, 128)));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(30)]
	[InlineData(90)]
	[InlineData(145)]
	[InlineData(175)]
	[InlineData(220)]
	[InlineData(280)]
	[InlineData(330)]
	public void Every_hue_gives_a_ladder_with_readable_text_in_the_worst_case(double hue)
	{
		var glass = SeaglassPalette.Build(hue);
		Assert.True(glass.WorstContrast >= SeaglassPalette.BodyContrast, $"hue {hue}: {glass.WorstContrast:0.00}");
		Assert.InRange(glass.TintAlpha, 0.78, 0.95);

		var solid = SeaglassPalette.Build(hue, solid: true);
		Assert.True(solid.WorstContrast >= SeaglassPalette.SmallTextContrast, $"solid hue {hue}: {solid.WorstContrast:0.00}");
		Assert.Equal(1, solid.TintAlpha);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(90)]
	[InlineData(145)]
	[InlineData(175)]
	[InlineData(250)]
	public void The_light_ladder_has_readable_dark_text_and_a_readable_accent_in_the_worst_case(double hue)
	{
		var light = SeaglassPalette.Build(hue, solid: false, light: true);
		Assert.True(light.Light);
		Assert.True(light.WorstContrast >= SeaglassPalette.BodyContrast, $"hue {hue}: {light.WorstContrast:0.00}");
		Assert.True(light.OnSurface.Luminance < light.GlassTint.Luminance);
		Assert.True(OkLab.Contrast(light.OnAccent.Luminance, light.AccentFill.Luminance) >= 4.5, "white on the light accent fill");
		var solid = SeaglassPalette.Build(hue, solid: true, light: true);
		Assert.True(solid.WorstContrast >= SeaglassPalette.SmallTextContrast, $"solid {solid.WorstContrast:0.00}");
	}

	[Fact]
	public void The_worst_case_is_recomputed_against_a_white_backdrop_that_the_scrim_never_lets_through()
	{
		var glass = SeaglassPalette.Build(175);
		var text = OkLab.CompositeLuminance(glass.GlassTint, glass.TintAlpha, SeaglassPalette.BackdropCeiling);
		Assert.True(OkLab.Contrast(glass.OnSurface.Luminance, text) >= 4.5);
		Assert.True(OkLab.Contrast(glass.Muted.Luminance, text) >= 4.5);
		// and the accent is the sunset's hot pink, with dark text on it
		Assert.True(OkLab.Contrast(glass.OnAccent.Luminance, glass.Accent.Luminance) >= 4.5);
	}

	[Fact]
	public void Real_artwork_hashes_give_valid_palettes()
	{
		foreach (var hash in new[] { Colourful, Pastel, null, "" })
		{
			var palette = SeaglassPalette.FromBlurHash(hash);
			Assert.True(palette.WorstContrast >= SeaglassPalette.BodyContrast, hash);
			Assert.Matches("^#[0-9A-F]{6}$", palette.Accent.Hex);
		}
	}

	[Fact]
	public void Mixing_two_colours_in_OKLab_ends_where_it_started_and_finishes()
	{
		var a = OkLab.ToRgb(0.3, 0.05, 20);
		var b = OkLab.ToRgb(0.3, 0.05, 200);
		Assert.Equal(a, OkLab.Mix(a, b, 0));
		var end = OkLab.Mix(a, b, 1);
		Assert.InRange(Math.Abs(end.R - b.R) + Math.Abs(end.G - b.G) + Math.Abs(end.B - b.B), 0, 3);
	}

	[Fact]
	public void A_hash_that_is_mostly_one_hue_gives_a_hue_near_it()
	{
		// every cell of this picture is the same orange: the pull toward teal keeps it between orange and teal
		var (_, _, orange) = OkLab.FromLinear(BlurHash.SrgbToLinear(230), BlurHash.SrgbToLinear(120), BlurHash.SrgbToLinear(30));
		var hue = SeaglassPalette.HueFromBlurHash(SolidHash(230, 120, 30));
		Assert.True(Math.Abs(((hue - orange + 540) % 360) - 180) < Math.Abs(((SeaglassPalette.DefaultHue - orange + 540) % 360) - 180) + 1);
		Assert.NotEqual(SeaglassPalette.DefaultHue, hue);
	}

	[Theory]
	[InlineData(30)]
	[InlineData(0)]
	[InlineData(300)]
	[InlineData(120)]
	[InlineData(250)]
	public void Every_title_lands_inside_the_sunset_hues(double seed)
	{
		var hue = SeaglassPalette.ClampToPaletteHues(seed);
		var offset = ((hue - SeaglassPalette.DefaultHue + 540) % 360) - 180;
		Assert.InRange(offset, SeaglassPalette.HueBelow - 0.001, SeaglassPalette.HueAbove + 0.001);
		Assert.Equal(hue, SeaglassPalette.ClampToPaletteHues(hue), 6);
		// magenta-rose to peach: the sunset's own hues (its swatches are OKLCH 5 to 31)
		Assert.InRange((hue - 340 + 360) % 360, 0, 65);
	}

	[Fact]
	public void Sampled_thumbnail_cells_give_the_same_hue_as_a_hash_of_that_colour()
	{
		var orange = (BlurHash.SrgbToLinear(230), BlurHash.SrgbToLinear(120), BlurHash.SrgbToLinear(30));
		var fromCells = SeaglassPalette.HueFromCells(Enumerable.Repeat(orange, 16 * 9).ToArray());
		Assert.Equal(SeaglassPalette.HueFromBlurHash(SolidHash(230, 120, 30)), fromCells, 1);
		Assert.Equal(SeaglassPalette.DefaultHue, SeaglassPalette.HueFromCells([]));
		Assert.Equal(SeaglassPalette.DefaultHue, SeaglassPalette.HueFromCells(Enumerable.Repeat((0.2, 0.2, 0.2), 144).ToArray()));
	}

	/// <summary>A BlurHash of one flat colour (1x1 components: the size flag, a zero maximum, the DC).</summary>
	private static string SolidHash(int r, int g, int b)
	{
		const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz#$%*+,-.:;=?@[]^_{|}~";
		var dc = (r << 16) | (g << 8) | b;
		var chars = new char[4];
		for (var i = 3; i >= 0; i--)
		{
			chars[i] = alphabet[dc % 83];
			dc /= 83;
		}

		// size flag 0 (1x1), quantised maximum 0, the DC, and a padding pair so the length is valid for the reader (6)
		return "00" + new string(chars);
	}
}

public sealed class SeaglassFixedChromeTests
{
	[Fact]
	public void The_accent_is_always_the_sunsets_hot_pink_or_crimson_whatever_the_hue()
	{
		foreach (var hue in new[] { 0.0, 60, 120, 175, 250, 320 })
		{
			Assert.Equal("#F96985", SeaglassPalette.Build(hue).Accent.Hex);
			Assert.Equal("#BC1E47", SeaglassPalette.Build(hue, light: true).Accent.Hex);
		}
	}

	/// <summary>1.1.4: the Play label on the accent fill: dark on hot pink (white there is only 2.8:1), white on crimson.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_label_on_the_accent_fill_reads(bool light)
	{
		var p = SeaglassPalette.Build(SeaglassPalette.DefaultHue, light: light);
		Assert.True(OkLab.Contrast(p.OnAccent.Luminance, p.AccentFill.Luminance) >= 4.5, $"light {light}: {OkLab.Contrast(p.OnAccent.Luminance, p.AccentFill.Luminance):0.00}");
		Assert.True(OkLab.Contrast(1.0, SeaglassPalette.AccentDark.Luminance) < 4.5, "white on hot pink must never be the label");
	}

	[Fact]
	public void The_glass_is_a_calm_deep_plum_not_the_saturated_maroon()
	{
		var glass = SeaglassPalette.Build(SeaglassPalette.DefaultHue);
		var (l, chroma, hue) = OkLab.FromLinear(BlurHash.SrgbToLinear(glass.GlassTint.R), BlurHash.SrgbToLinear(glass.GlassTint.G), BlurHash.SrgbToLinear(glass.GlassTint.B));
		Assert.InRange(chroma, 0.02, 0.05);
		Assert.InRange(l, 0.15, 0.26);
		Assert.InRange((hue + 360) % 360, 0, 20);
		// the light theme's glass leans to the pale sun and peach
		var pale = SeaglassPalette.Build(SeaglassPalette.DefaultHue, light: true);
		var (pl, _, ph) = OkLab.FromLinear(BlurHash.SrgbToLinear(pale.GlassTint.R), BlurHash.SrgbToLinear(pale.GlassTint.G), BlurHash.SrgbToLinear(pale.GlassTint.B));
		Assert.True(pl > 0.9);
		Assert.InRange(ph, 18, 40);
	}

	/// <summary>
	/// Seaglass 1.1.3: the accent as a mark on the glass (a selected tab's underline, a checked icon, a toggle) keeps the 3:1 of
	/// non-text contrast on the worst case the palette checks its text against, in Full and Solid, dark and light.
	/// </summary>
	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void The_accent_is_a_visible_mark_on_the_glass_in_the_worst_case(bool solid, bool light)
	{
		var p = SeaglassPalette.Build(SeaglassPalette.DefaultHue, solid, light);
		var under = solid ? (light ? 1 : 0) : light ? SeaglassPalette.LightBackdropFloor : SeaglassPalette.BackdropCeiling;
		var surface = OkLab.CompositeLuminance(p.GlassTint, p.TintAlpha, under);
		var ratio = OkLab.Contrast(p.Accent.Luminance, surface);
		Assert.True(ratio >= 3, $"solid {solid}, light {light}: the accent has {ratio:0.00}:1 on the glass");
	}
}
