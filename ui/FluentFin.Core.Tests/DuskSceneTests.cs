using FluentFin.Core.Theming;

namespace FluentFin.Core.Tests;

/// <summary>1.1.4: the dusk scene behind the window when no artwork is shown: its colours keep text readable, its shapes stay in bounds.</summary>
public sealed class DuskSceneTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_strip_never_takes_text_on_the_backdrop_under_4_5_to_1(bool light)
	{
		var palette = SeaglassPalette.Build(SeaglassPalette.DefaultHue, light: light);
		foreach (var color in DuskScene.StripColors(light))
		{
			if (light)
			{
				Assert.True(color.Luminance >= DuskScene.LightFloor, $"{color.Hex} is {color.Luminance:0.000}");
			}
			else
			{
				Assert.True(color.Luminance <= DuskScene.DarkCeiling, $"{color.Hex} is {color.Luminance:0.000}");
			}

			// the glass text straight on it: primary and muted
			Assert.True(OkLab.Contrast(palette.OnSurface.Luminance, color.Luminance) >= 4.5, $"{color.Hex}: primary text");
			Assert.True(OkLab.Contrast(palette.Muted.Luminance, color.Luminance) >= 4.5, $"{color.Hex}: muted text");
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_full_scenes_top_band_keeps_the_title_bar_text_readable(bool light)
	{
		var palette = SeaglassPalette.Build(SeaglassPalette.DefaultHue, light: light);
		var top = DuskScene.SkyBands(light)[0];
		Assert.Equal(0, top.From);
		Assert.True(OkLab.Contrast(palette.OnSurface.Luminance, top.Color.Luminance) >= 4.5);
		Assert.True(OkLab.Contrast(palette.Muted.Luminance, top.Color.Luminance) >= 4.5);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_sky_bands_cover_the_sky_once_top_to_bottom(bool light)
	{
		var bands = DuskScene.SkyBands(light);
		Assert.Equal(0, bands[0].From);
		Assert.Equal(1, bands[^1].To);
		for (var i = 1; i < bands.Count; i++)
		{
			Assert.Equal(bands[i - 1].To, bands[i].From);
			Assert.True(bands[i].To > bands[i].From);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_hills_darken_from_far_to_near(bool light)
	{
		var far = DuskScene.Hill(DuskHill.Far, light).Luminance;
		var mesa = DuskScene.Hill(DuskHill.Mesa, light).Luminance;
		var near = DuskScene.Hill(DuskHill.Near, light).Luminance;
		Assert.True(far > mesa && mesa > near);
	}

	[Theory]
	[InlineData(120)]
	[InlineData(240)]
	[InlineData(37.5)]
	public void The_striped_sun_has_slices_inside_its_disc_thinner_toward_the_horizon(double r)
	{
		var bands = DuskScene.SunBands(r);
		Assert.True(bands.Count >= 4);
		double previousBottom = -1, previousHeight = double.MaxValue;
		foreach (var (top, bottom) in bands)
		{
			Assert.True(top >= 0 && bottom <= 2 * r + 1e-9 && bottom > top);
			Assert.True(top > previousBottom, "slices do not overlap");
			var height = bottom - top;
			Assert.True(height <= previousHeight + 1e-9, "each slice is no taller than the one above");
			previousBottom = bottom;
			previousHeight = height;
		}
	}

	[Theory]
	[InlineData(4, 0.25, false)]
	[InlineData(8, 0.18, true)]
	[InlineData(12, 0.18, false)]
	public void A_starburst_alternates_long_and_short_rays_inside_its_square(int points, double inner, bool alternate)
	{
		const double r = 50;
		var star = DuskScene.Starburst(r, points, inner, alternate);
		Assert.Equal(points * 2, star.Count);
		for (var i = 0; i < star.Count; i++)
		{
			var (x, y) = star[i];
			Assert.InRange(x, -1e-9, 2 * r + 1e-9);
			Assert.InRange(y, -1e-9, 2 * r + 1e-9);
			var length = Math.Sqrt((x - r) * (x - r) + (y - r) * (y - r));
			var expected = i % 2 == 1 ? r * inner : alternate && i % 4 == 2 ? r * 0.6 : r;
			Assert.Equal(expected, length, 6);
		}

		// the first ray points straight up
		Assert.Equal((r, 0.0), (Math.Round(star[0].X, 6), Math.Round(star[0].Y, 6)));
	}

	[Fact]
	public void The_stars_keep_out_of_the_middle_where_the_sign_in_card_sits()
	{
		Assert.InRange(DuskScene.Stars.Length, 5, 9);
		foreach (var (x, y, radius, points, _) in DuskScene.Stars)
		{
			Assert.False(x > 0.36 && x < 0.62 && y > 0.28 && y < 0.72, $"a star at {x},{y} would sit behind the card");
			Assert.InRange(radius, 0.004, 0.03);
			Assert.Contains(points, new[] { 4, 8, 12 });
		}
	}

	[Fact]
	public void The_crt_touches_stay_faint()
	{
		Assert.InRange(DuskScene.ScanlineAlpha, 0.04, 0.08);
		Assert.True(DuskScene.VignetteAlpha(false) <= 0.35 && DuskScene.VignetteAlpha(true) < DuskScene.VignetteAlpha(false));
		Assert.True(DuskScene.StripHillsHeight < DuskScene.SceneHillsHeight);
	}
}
