using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: makes the glass brushes follow <see cref="GlassMode"/>. In Solid, <c>GlassPanelBrush</c> and
/// <c>GlassPanelStrongBrush</c> in both theme dictionaries of Glass.xaml draw opaque, in the colours of their stand-ins
/// (<c>GlassPanelSolidBrush</c>, <c>GlassPanelStrongSolidBrush</c>; the acrylic draws its FallbackColor); in Full and Reduced
/// they are acrylic again.
/// Pages keep using the two keys. UI thread only (<see cref="Attach"/> marshals GlassMode.Changed to it).
/// </summary>
public static class GlassTheme
{
	private static readonly string[] _themes = ["Dark", "Light"];
	// each key's Solid stand-in is the key with "Brush" replaced by "SolidBrush" (GlassOverlayBrush: the menus' and tooltips' tint)
	internal static readonly string[] Keys = ["GlassPanelBrush", "GlassPanelStrongBrush", "GlassOverlayBrush", "GlassTitleBarBrush"];
	// each brush's own look before Solid (an acrylic one's AlwaysUseFallback and FallbackColor, a solid one's Color)
	private static readonly Dictionary<(string Theme, string Key), (bool Fallback, Color Color)> _acrylic = [];
	private static bool _solid;
	private static FrameworkElement? _root;
	private static DispatcherQueue? _queue;

	/// <summary>
	/// The theme SEAGLASS_THEME (Light or Dark, the harness's screenshots) forces on the whole application, or null to follow
	/// Windows. Forcing only the window's content left the dialogs, menus and tooltips (popups) in Windows' theme.
	/// </summary>
	public static ApplicationTheme? ForcedApplicationTheme(string? value) =>
		Enum.TryParse<ApplicationTheme>(value?.Trim(), true, out var theme) && Enum.IsDefined(theme) ? theme : null;

	/// <summary>
	/// 1.1.4, for an in-app theme choice (Settings > Appearance; the setting itself is the account stream's): the window's content
	/// takes the theme, and so do the title bar's caption buttons, which otherwise keep Windows' colours (the layout audit's trap:
	/// pale glyphs on the pale light title bar). Default follows Windows.
	/// </summary>
	public static void SetAppTheme(Window window, ElementTheme theme)
	{
		if (window.Content is FrameworkElement root)
		{
			root.RequestedTheme = theme;
		}

		try
		{
			var bar = window.AppWindow.TitleBar;
			bar.PreferredTheme = TitleBarThemeFor(theme);
			// with the content extended into the title bar, the caption buttons take the AppWindow's own button colours, which
			// PreferredTheme does not change (measured: still pale on the light theme). null: Windows' own again.
			var colors = CaptionColorsFor(theme);
			bar.ButtonForegroundColor = colors?.Foreground;
			bar.ButtonHoverForegroundColor = colors?.Foreground;
			bar.ButtonPressedForegroundColor = colors?.Foreground;
			bar.ButtonInactiveForegroundColor = colors?.Inactive;
			bar.ButtonHoverBackgroundColor = colors?.HoverBackground;
			bar.ButtonPressedBackgroundColor = colors?.PressedBackground;
			bar.ButtonBackgroundColor = colors is null ? null : Microsoft.UI.Colors.Transparent;
			bar.ButtonInactiveBackgroundColor = colors is null ? null : Microsoft.UI.Colors.Transparent;
		}
		catch (Exception)
		{
			// a title bar that cannot be themed (older Windows) keeps Windows' caption colours
		}
	}

	/// <summary>The caption buttons' colours for a theme the app sets itself (none: Windows' own, for Default).</summary>
	public readonly record struct CaptionColors(Windows.UI.Color Foreground, Windows.UI.Color Inactive, Windows.UI.Color HoverBackground, Windows.UI.Color PressedBackground);

	/// <summary>The glyphs in the glass text colours (primary; muted while the window is in the background), the hover and press as the glass overlays.</summary>
	public static CaptionColors? CaptionColorsFor(ElementTheme theme) => theme switch
	{
		ElementTheme.Light => new(Windows.UI.Color.FromArgb(255, 0x2D, 0x0F, 0x16), Windows.UI.Color.FromArgb(255, 0x69, 0x48, 0x4E),
			Windows.UI.Color.FromArgb(0x0F, 0, 0, 0), Windows.UI.Color.FromArgb(0x1F, 0, 0, 0)),
		ElementTheme.Dark => new(Windows.UI.Color.FromArgb(255, 0xF8, 0xEF, 0xF0), Windows.UI.Color.FromArgb(255, 0xC8, 0xB4, 0xB7),
			Windows.UI.Color.FromArgb(0x1A, 255, 255, 255), Windows.UI.Color.FromArgb(0x2E, 255, 255, 255)),
		_ => null,
	};

	/// <summary>The caption buttons' theme for an app theme.</summary>
	public static Microsoft.UI.Windowing.TitleBarTheme TitleBarThemeFor(ElementTheme theme) => theme switch
	{
		ElementTheme.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
		ElementTheme.Dark => Microsoft.UI.Windowing.TitleBarTheme.Dark,
		_ => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode,
	};

	/// <summary>Whether the opaque brushes are in place now.</summary>
	public static bool IsSolid => _solid;

	/// <summary>Start following <see cref="GlassMode"/> and set the brushes for its current value. Call once, on the UI thread.</summary>
	public static void Attach(FrameworkElement root)
	{
		_root = root;
		_queue = DispatcherQueue.GetForCurrentThread();
		GlassMode.Changed += (_, _) =>
		{
			if (_queue is { } queue && !queue.HasThreadAccess)
			{
				queue.TryEnqueue(Refresh);
			}
			else
			{
				Refresh();
			}
		};
		Refresh();
	}

	public static void Refresh() => Apply(GlassMode.Current == GlassModeKind.Solid);

	public static void Apply(bool solid)
	{
		if (solid == _solid)
		{
			return;
		}

		var glass = FindGlassDictionary(Application.Current.Resources);
		if (glass is null)
		{
			return;
		}

		foreach (var theme in _themes)
		{
			if (!glass.ThemeDictionaries.TryGetValue(theme, out var found) || found is not ResourceDictionary dictionary)
			{
				continue;
			}

			foreach (var key in Keys)
			{
				if (!dictionary.TryGetValue(key, out var brush))
				{
					continue;
				}

				// Nixie 1.3.0: the brush under the key changes in place and is never replaced. Writing the stand-in under the key
				// (dictionary[key] = opaque) made every RequestedTheme="Dark" part of a light window (the heroes, Drive-in's
				// screen and sign) look its brushes up in the Light dictionary from then on: dark labels on dark buttons in Light
				// + Solid, in every layout theme.
				Color? standIn = dictionary.TryGetValue(key.Replace("Brush", "SolidBrush"), out var found2) && found2 is SolidColorBrush opaque ? opaque.Color : null;
				switch (brush)
				{
					case AcrylicBrush acrylic when solid:
						_acrylic.TryAdd((theme, key), (acrylic.AlwaysUseFallback, acrylic.FallbackColor));
						acrylic.FallbackColor = standIn ?? Opaque(acrylic.FallbackColor);
						acrylic.AlwaysUseFallback = true;
						break;
					case AcrylicBrush acrylic when _acrylic.Remove((theme, key), out var was):
						acrylic.AlwaysUseFallback = was.Fallback;
						acrylic.FallbackColor = was.Color;
						break;
					case SolidColorBrush plain when solid:
						_acrylic.TryAdd((theme, key), (false, plain.Color));
						plain.Color = standIn ?? Opaque(plain.Color);
						break;
					case SolidColorBrush plain when _acrylic.Remove((theme, key), out var was):
						plain.Color = was.Color;
						break;
				}
			}
		}

		_solid = solid;

		// the brushes changed in place; flip the root's theme and back in one tick so every part draws them again
		if (_root is { } root)
		{
			var requested = root.RequestedTheme;
			root.RequestedTheme = requested == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
			root.RequestedTheme = requested;
		}
	}

	private static Color Opaque(Color color) => Color.FromArgb(255, color.R, color.G, color.B);

	private static ResourceDictionary? FindGlassDictionary(ResourceDictionary resources)
	{
		foreach (var merged in resources.MergedDictionaries)
		{
			if (merged.ThemeDictionaries.ContainsKey("Dark") && merged.ThemeDictionaries["Dark"] is ResourceDictionary dark && dark.ContainsKey("GlassPanelSolidBrush"))
			{
				return merged;
			}

			if (FindGlassDictionary(merged) is { } nested)
			{
				return nested;
			}
		}

		return null;
	}
}
