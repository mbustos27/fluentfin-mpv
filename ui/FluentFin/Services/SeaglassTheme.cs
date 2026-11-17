using FluentFin.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: the window's chrome is ONE fixed sea-glass palette: clean neutral dark glass (pale glass in the light theme) with the
/// accent always sea-glass aqua. The title being shown does not recolour any panel or button; its hue only tints the ambient
/// backdrop, faintly (<see cref="TitleTint"/>). This applies <see cref="SeaglassPalette"/> (default hue) to the shared brushes of
/// Glass.xaml, in place, and again when <see cref="GlassMode"/> changes (Solid gets the opaque 7:1 ladder). UI thread only.
/// </summary>
public static class SeaglassTheme
{
	/// <summary>The page backdrop's dim: what the scrim keeps of the artwork (1 - the ceiling the contrast check assumes).</summary>
	public static double DimAlpha => 1 - SeaglassPalette.BackdropCeiling;

	private static ResourceDictionary? _dark;
	private static ResourceDictionary? _light;

	public static void Attach()
	{
		_dark = FindTheme(Application.Current.Resources, "Dark");
		_light = FindTheme(Application.Current.Resources, "Light");
		var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
		GlassMode.Changed += (_, _) =>
		{
			if (queue.HasThreadAccess)
			{
				Refresh();
			}
			else
			{
				queue.TryEnqueue(Refresh);
			}
		};
		Refresh();
	}

	public static void Refresh()
	{
		var solid = GlassMode.Current == GlassModeKind.Solid;
		Apply(SeaglassPalette.Build(SeaglassPalette.DefaultHue, solid));
		Apply(SeaglassPalette.Build(SeaglassPalette.DefaultHue, solid, light: true));
	}

	private static ResourceDictionary? FindTheme(ResourceDictionary resources, string theme)
	{
		foreach (var merged in resources.MergedDictionaries)
		{
			if (merged.ThemeDictionaries.TryGetValue(theme, out var found) && found is ResourceDictionary d && d.ContainsKey("GlassPanelSolidBrush"))
			{
				return d;
			}

			if (FindTheme(merged, theme) is { } nested)
			{
				return nested;
			}
		}

		return null;
	}

	/// <summary>How much of what is behind a menu or a tooltip the overlay tint lets through (0xE6: 10 %), outside Solid.</summary>
	public const byte OverlayAlpha = 0xE6;

	/// <summary>
	/// The overlay tint of menus and tooltips (GlassOverlayBrush): the palette's container colour, translucent over the menu's own
	/// system backdrop, opaque in Solid. In Solid, GlassTheme has put GlassOverlaySolidBrush under that key, and it stays opaque.
	/// </summary>
	public static Color OverlayColor(SeaglassPalette p) => C(p.ContainerHigh, p.Solid ? (byte)255 : OverlayAlpha);

	private static Color C(Rgb c, byte alpha = 255) => Color.FromArgb(alpha, c.R, c.G, c.B);

	private static void Apply(SeaglassPalette p)
	{
		if ((p.Light ? _light : _dark) is not { } d)
		{
			return;
		}

		void Solid(string key, Color color)
		{
			if (d.TryGetValue(key, out var found) && found is SolidColorBrush brush)
			{
				brush.Color = color;
			}
		}

		Solid("GlassPanelSolidBrush", C(p.GlassTint));
		Solid("GlassPanelStrongSolidBrush", C(p.ContainerHigh));
		Solid("GlassOverlaySolidBrush", C(p.ContainerHigh));
		Solid("GlassOverlayBrush", OverlayColor(p));
		Solid("SeaglassAccentBrush", C(p.Accent));
		Solid("SeaglassAccentFillBrush", C(p.AccentFill));
		Solid("SeaglassAccentForegroundBrush", C(p.OnAccent));
		Solid("SeaglassDeepBrush", C(p.Background));
		Solid("GlassTextPrimaryBrush", C(p.OnSurface));
		Solid("GlassTextSecondaryBrush", C(p.Muted));
		Solid("GlassAmbientDimBrush", C(p.Background, (byte)Math.Round(DimAlpha * 255)));

		var tint = C(p.GlassTint);
		if (d.TryGetValue("GlassPanelBrush", out var panel) && panel is AcrylicBrush acrylic)
		{
			acrylic.TintColor = tint;
			acrylic.TintOpacity = Math.Clamp(p.TintAlpha - 0.2, 0.2, 0.9);
			acrylic.FallbackColor = Color.FromArgb(0xF0, tint.R, tint.G, tint.B);
		}

		if (d.TryGetValue("GlassPanelStrongBrush", out var strong) && strong is AcrylicBrush strongAcrylic)
		{
			var high = C(p.ContainerHigh);
			strongAcrylic.TintColor = high;
			strongAcrylic.TintOpacity = Math.Clamp(p.TintAlpha - 0.05, 0.4, 0.95);
			strongAcrylic.FallbackColor = Color.FromArgb(0xF5, high.R, high.G, high.B);
		}

		if (d.TryGetValue("GlassImageScrimBrush", out var scrim) && scrim is LinearGradientBrush gradient && gradient.GradientStops.Count >= 3)
		{
			var deep = p.Background;
			gradient.GradientStops[0].Color = Color.FromArgb(0, deep.R, deep.G, deep.B);
			gradient.GradientStops[1].Color = Color.FromArgb(0x66, deep.R, deep.G, deep.B);
			gradient.GradientStops[2].Color = Color.FromArgb(0xF2, deep.R, deep.G, deep.B);
		}
	}
}
