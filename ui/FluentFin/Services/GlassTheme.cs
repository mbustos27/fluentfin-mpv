using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace FluentFin.Services;

/// <summary>
/// Seaglass: makes the glass brushes follow <see cref="GlassMode"/>. In Solid, <c>GlassPanelBrush</c> and
/// <c>GlassPanelStrongBrush</c> in both theme dictionaries of Glass.xaml become the opaque brushes of the same colours
/// (<c>GlassPanelSolidBrush</c>, <c>GlassPanelStrongSolidBrush</c>); in Full and Reduced they are the acrylic ones again.
/// Pages keep using the two keys. UI thread only (<see cref="Attach"/> marshals GlassMode.Changed to it).
/// </summary>
public static class GlassTheme
{
	private static readonly string[] _themes = ["Dark", "Light"];
	// each key's Solid stand-in is the key with "Brush" replaced by "SolidBrush" (GlassOverlayBrush: the menus' and tooltips' tint)
	internal static readonly string[] Keys = ["GlassPanelBrush", "GlassPanelStrongBrush", "GlassOverlayBrush"];
	private static readonly Dictionary<(string Theme, string Key), object> _acrylic = [];
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
			window.AppWindow.TitleBar.PreferredTheme = TitleBarThemeFor(theme);
		}
		catch (Exception)
		{
			// a title bar that cannot be themed (older Windows) keeps Windows' caption colours
		}
	}

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
				if (solid)
				{
					if (!_acrylic.ContainsKey((theme, key)) && dictionary.TryGetValue(key, out var original))
					{
						_acrylic[(theme, key)] = original;
					}

					var solidKey = key.Replace("Brush", "SolidBrush");
					if (dictionary.TryGetValue(solidKey, out var opaque))
					{
						dictionary[key] = opaque;
					}
				}
				else if (_acrylic.TryGetValue((theme, key), out var original))
				{
					dictionary[key] = original;
				}
			}
		}

		_solid = solid;

		// ThemeResource lookups are only redone when the theme changes: flip the root's theme and back in one tick
		if (_root is { } root)
		{
			var requested = root.RequestedTheme;
			root.RequestedTheme = requested == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
			root.RequestedTheme = requested;
		}
	}

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
