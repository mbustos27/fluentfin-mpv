using FluentFin.Controls;
using FluentFin.Core.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: tints the whole window with the title being shown. The artwork behind the window gives a hue (its BlurHash,
/// <see cref="SeaglassPalette"/>); the dark theme's glass brushes (panels, text, accent, rim, the scrim over the artwork) are
/// recoloured from that palette, crossfaded in OKLab over half a second (at once with animations off). Solid mode gets the same
/// hue with opaque surfaces at 7:1. The brushes are the shared ones in Glass.xaml, changed in place, so every surface follows.
/// UI thread only.
/// </summary>
public static class SeaglassTheme
{
	public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(500);

	/// <summary>The page backdrop's dim: what the scrim keeps of the artwork (1 - the ceiling the contrast check assumes).</summary>
	public static double DimAlpha => 1 - SeaglassPalette.BackdropCeiling;

	private static readonly string[] _brushKeys =
	[
		"GlassPanelSolidBrush", "GlassPanelStrongSolidBrush", "SeaglassAccentBrush", "SeaglassAccentFillBrush", "SeaglassAccentForegroundBrush",
		"SeaglassDeepBrush", "GlassTextPrimaryBrush", "GlassTextSecondaryBrush", "GlassAmbientDimBrush",
	];

	private static ResourceDictionary? _dark;
	private static DispatcherQueueTimer? _timer;
	private static SeaglassPalette _from = SeaglassPalette.Build(SeaglassPalette.DefaultHue);
	private static SeaglassPalette _to = _from;
	private static double _step = 1;
	private static string? _hash;

	/// <summary>The palette now shown (the target of a fade in progress).</summary>
	public static SeaglassPalette Current => _to;

	public static void Attach()
	{
		_dark = FindDark(Application.Current.Resources);
		var queue = DispatcherQueue.GetForCurrentThread();
		_timer = queue.CreateTimer();
		_timer.Interval = TimeSpan.FromMilliseconds(33);
		_timer.Tick += (_, _) => Tick();
		AmbientBackdrop.HashChanged += (_, _) => Follow();
		GlassMode.Changed += (_, _) =>
		{
			if (queue.HasThreadAccess)
			{
				Follow(force: true);
			}
			else
			{
				queue.TryEnqueue(() => Follow(force: true));
			}
		};
		Follow(force: true, instant: true);
	}

	private static void Follow(bool force = false, bool instant = false)
	{
		var hash = AmbientBackdrop.BlurHash;
		var solid = GlassMode.Current == GlassModeKind.Solid;
		if (!force && hash == _hash)
		{
			return;
		}

		_hash = hash;
		var target = SeaglassPalette.FromBlurHash(hash, solid);
		_from = Blend(_from, _to, _step);
		_to = target;
		_step = 0;
		if (instant || !SafeMotion.Allowed || _timer is null)
		{
			_step = 1;
			Apply(_to, _to, 1);
			return;
		}

		_timer.Start();
	}

	private static void Tick()
	{
		_step = Math.Min(1, _step + 33 / FadeTime.TotalMilliseconds);
		Apply(_from, _to, Ease(_step));
		if (_step >= 1)
		{
			_timer?.Stop();
		}
	}

	private static double Ease(double t) => t * t * (3 - 2 * t);

	/// <summary>The palette part-way between two (kept as a palette: the nearer one's hue and numbers).</summary>
	private static SeaglassPalette Blend(SeaglassPalette from, SeaglassPalette to, double t) => t >= 1 ? to : t <= 0 ? from : (Ease(t) < 0.5 ? from : to);

	private static ResourceDictionary? FindDark(ResourceDictionary resources)
	{
		foreach (var merged in resources.MergedDictionaries)
		{
			if (merged.ThemeDictionaries.TryGetValue("Dark", out var dark) && dark is ResourceDictionary d && d.ContainsKey("GlassPanelSolidBrush"))
			{
				return d;
			}

			if (FindDark(merged) is { } nested)
			{
				return nested;
			}
		}

		return null;
	}

	private static Color C(Rgb c, byte alpha = 255) => Color.FromArgb(alpha, c.R, c.G, c.B);

	private static Color M(Rgb a, Rgb b, double t, byte alpha = 255) => C(OkLab.Mix(a, b, t), alpha);

	/// <summary>Writes the mix of two palettes into the brushes.</summary>
	private static void Apply(SeaglassPalette a, SeaglassPalette b, double t)
	{
		if (_dark is not { } d)
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

		Solid("GlassPanelSolidBrush", M(a.GlassTint, b.GlassTint, t));
		Solid("GlassPanelStrongSolidBrush", M(a.ContainerHigh, b.ContainerHigh, t));
		Solid("SeaglassAccentBrush", M(a.Accent, b.Accent, t));
		Solid("SeaglassAccentFillBrush", M(a.Accent, b.Accent, t));
		Solid("SeaglassAccentForegroundBrush", M(a.OnAccent, b.OnAccent, t));
		Solid("SeaglassDeepBrush", M(a.Background, b.Background, t));
		Solid("GlassTextPrimaryBrush", M(a.OnSurface, b.OnSurface, t));
		Solid("GlassTextSecondaryBrush", M(a.Muted, b.Muted, t));
		Solid("GlassAmbientDimBrush", M(a.Background, b.Background, t, (byte)Math.Round(DimAlpha * 255)));

		var tint = M(a.GlassTint, b.GlassTint, t);
		var tintAlpha = a.TintAlpha + (b.TintAlpha - a.TintAlpha) * t;
		if (d.TryGetValue("GlassPanelBrush", out var panel) && panel is AcrylicBrush acrylic)
		{
			acrylic.TintColor = tint;
			acrylic.TintOpacity = Math.Clamp(tintAlpha - 0.2, 0.2, 0.9);
			acrylic.FallbackColor = Color.FromArgb(0xF0, tint.R, tint.G, tint.B);
		}

		if (d.TryGetValue("GlassPanelStrongBrush", out var strong) && strong is AcrylicBrush strongAcrylic)
		{
			var high = M(a.ContainerHigh, b.ContainerHigh, t);
			strongAcrylic.TintColor = high;
			strongAcrylic.TintOpacity = Math.Clamp(tintAlpha - 0.05, 0.4, 0.95);
			strongAcrylic.FallbackColor = Color.FromArgb(0xF5, high.R, high.G, high.B);
		}

		// the rim: light on the top left, the title's aqua on the bottom right
		if (d.TryGetValue("GlassStrokeBrush", out var stroke) && stroke is LinearGradientBrush rim && rim.GradientStops.Count >= 3)
		{
			rim.GradientStops[2].Color = M(a.Rim, b.Rim, t, 0x66);
		}

		if (d.TryGetValue("GlassImageScrimBrush", out var scrim) && scrim is LinearGradientBrush gradient && gradient.GradientStops.Count >= 3)
		{
			var deep = OkLab.Mix(a.Background, b.Background, t);
			gradient.GradientStops[0].Color = Color.FromArgb(0, deep.R, deep.G, deep.B);
			gradient.GradientStops[1].Color = Color.FromArgb(0x66, deep.R, deep.G, deep.B);
			gradient.GradientStops[2].Color = Color.FromArgb(0xF2, deep.R, deep.G, deep.B);
		}
	}
}
