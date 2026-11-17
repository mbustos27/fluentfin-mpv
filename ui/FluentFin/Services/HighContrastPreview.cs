using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Services;

/// <summary>
/// 1.1.4 (states): a PREVIEW of a contrast theme for the off-screen harness, which cannot switch Windows' own
/// high contrast (it is per user and would change the user's desktop). <c>SEAGLASS_THEME=HighContrast</c> (Aquatic) or
/// <c>HighContrast:Desert|Dusk|NightSky|Aquatic</c> copies every HighContrast theme dictionary of the application
/// (Glass.xaml's, the other styles', WinUI's own XamlControlsResources) over its Dark, Light and Default dictionaries, with
/// the system colours they were resolved to (this PC's ordinary ones) replaced by the scheme's, and forces Solid (as high
/// contrast does). Limits: the window's own backdrop (Mica) and anything drawn from a system colour outside a
/// HighContrast dictionary keep this PC's ordinary colours; a real high contrast pass is still for a person.
/// Unset (every normal run), nothing happens.
/// </summary>
public static class HighContrastPreview
{
	/// <summary>A Windows 11 contrast theme's colours (Settings > Accessibility > Contrast themes).</summary>
	public sealed record Scheme(string Name, Color Window, Color WindowText, Color Hotlight, Color GrayText, Color Highlight, Color HighlightText, Color ButtonFace, Color ButtonText);

	private static Color C(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

	public static readonly Scheme Aquatic = new("Aquatic", C(0x202020), C(0xFFFFFF), C(0x75E9FC), C(0xA6A6A6), C(0x8EE3F0), C(0x263B50), C(0x202020), C(0xFFFFFF));
	public static readonly Scheme Desert = new("Desert", C(0xFFFAEF), C(0x3D3D3D), C(0x1C5E75), C(0x676767), C(0x903909), C(0xFFF5E3), C(0xFFFAEF), C(0x3D3D3D));
	public static readonly Scheme Dusk = new("Dusk", C(0x2D3236), C(0xFFFFFF), C(0x70EBDE), C(0xA6A6A6), C(0xA1BFDE), C(0x212D3B), C(0x2D3236), C(0xB6F6F0));
	public static readonly Scheme NightSky = new("NightSky", C(0x000000), C(0xFFFFFF), C(0x8080FF), C(0xD6B4FD), C(0xFFFF00), C(0x2B2B2B), C(0x000000), C(0xFFEE32));

	/// <summary>The scheme SEAGLASS_THEME asks for ("HighContrast" alone: Aquatic), or null (any other value, or unset).</summary>
	public static Scheme? Parse(string? value)
	{
		var text = value?.Trim() ?? "";
		if (!text.StartsWith("HighContrast", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var name = text.Length > "HighContrast".Length && text["HighContrast".Length] == ':' ? text["HighContrast:".Length..].Trim() : "";
		return name.ToLowerInvariant() switch
		{
			"" or "aquatic" => Aquatic,
			"desert" => Desert,
			"dusk" => Dusk,
			"nightsky" or "night sky" => NightSky,
			_ => null,
		};
	}

	/// <summary>
	/// The colour a resource resolved to under this PC's ordinary system colours, as the scheme has it. The ordinary colours
	/// overlap (Window and HighlightText are both white, WindowText and ButtonText both black): the first in this order wins,
	/// which keeps text on a background readable in each scheme. Alpha is kept; a colour that is no system colour is kept.
	/// </summary>
	public static Color Map(Color color, IReadOnlyList<(Color System, Color Scheme)> map)
	{
		foreach (var (system, scheme) in map)
		{
			if (system.R == color.R && system.G == color.G && system.B == color.B)
			{
				return Color.FromArgb(color.A, scheme.R, scheme.G, scheme.B);
			}
		}

		return color;
	}

	/// <summary>This PC's ordinary system colours, each with the scheme's, in the order <see cref="Map"/> prefers them.</summary>
	public static IReadOnlyList<(Color System, Color Scheme)> ColorMap(Scheme scheme, Func<int, Color> system) =>
	[
		(system(5), scheme.Window),          // COLOR_WINDOW
		(system(8), scheme.WindowText),      // COLOR_WINDOWTEXT
		(system(13), scheme.Highlight),      // COLOR_HIGHLIGHT
		(system(14), scheme.HighlightText),  // COLOR_HIGHLIGHTTEXT
		(system(15), scheme.ButtonFace),     // COLOR_BTNFACE
		(system(18), scheme.ButtonText),     // COLOR_BTNTEXT
		(system(17), scheme.GrayText),       // COLOR_GRAYTEXT
		(system(26), scheme.Hotlight),       // COLOR_HOTLIGHT
	];

	[DllImport("user32.dll")]
	private static extern uint GetSysColor(int index);

	private static Color SystemColor(int index)
	{
		var bgr = GetSysColor(index);
		return Color.FromArgb(255, (byte)bgr, (byte)(bgr >> 8), (byte)(bgr >> 16));
	}

	private static bool _applied;

	/// <summary>Applies the scheme SEAGLASS_THEME names, once per run; nothing when it names none. UI thread.</summary>
	public static void ApplyFromEnvironment()
	{
		if (_applied || Parse(Environment.GetEnvironmentVariable("SEAGLASS_THEME")) is not { } scheme)
		{
			return;
		}

		_applied = true;
		var log = App.GetService<ILoggerFactory>().CreateLogger(typeof(HighContrastPreview).FullName!);

		// high contrast is always Solid (GlassMode.Current); GlassTheme and SeaglassTheme follow the change at once (this
		// thread), so the copies below land on top of what they set
		GlassMode.Override = GlassModeKind.Solid;

		var map = ColorMap(scheme, SystemColor);
		var themed = new List<(ResourceDictionary Contrast, ResourceDictionary[] Targets)>();
		foreach (var dictionary in AllDictionaries(Application.Current.Resources))
		{
			try
			{
				if (dictionary.ThemeDictionaries.TryGetValue("HighContrast", out var found) && found is ResourceDictionary contrast)
				{
					themed.Add((contrast, [.. new[] { "Default", "Dark", "Light" }
						.Select(t => dictionary.ThemeDictionaries.TryGetValue(t, out var d) ? d as ResourceDictionary : null).OfType<ResourceDictionary>()]));
				}
			}
			catch (Exception ex)
			{
				log.LogWarning(ex, "High contrast preview: a dictionary could not be read");
			}
		}

		// A HighContrast entry that is a StaticResource alias (NavigationView.xaml's rail brushes) was resolved at start-up to
		// the CURRENT theme's object (a Dark brush). Such an object is found among the themes' entries, and the alias takes
		// that key's HighContrast value instead, as it would under a real contrast theme.
		var contrastOf = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
		foreach (var (contrast, targets) in themed)
		{
			foreach (var target in targets)
			{
				foreach (var (key, value) in target)
				{
					if (contrast.TryGetValue(key, out var hc) && value is not null && !contrastOf.ContainsKey(value))
					{
						contrastOf[value] = hc;
					}
				}
			}
		}

		var copied = 0;
		foreach (var (contrast, targets) in themed)
		{
			var values = contrast.ToList();
			foreach (var target in targets)
			{
				foreach (var (key, value) in values)
				{
					try
					{
						target[key] = Remap(value is not null && contrastOf.TryGetValue(value, out var hc) ? hc : value, map);
						copied++;
					}
					catch (Exception ex)
					{
						log.LogWarning(ex, "High contrast preview: {Key} could not be copied", key);
					}
				}
			}
		}

		// ThemeResource lookups are redone when the theme changes: flip the root's theme and back
		if (App.MainWindow.Content is FrameworkElement root)
		{
			var requested = root.RequestedTheme;
			root.RequestedTheme = requested == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
			root.RequestedTheme = requested;
		}

		log.LogInformation("High contrast preview {Scheme}: {Count} resources from {Dictionaries} HighContrast dictionaries", scheme.Name, copied, themed.Count);
	}

	private static object Remap(object value, IReadOnlyList<(Color System, Color Scheme)> map) => value switch
	{
		SolidColorBrush brush => new SolidColorBrush(Map(brush.Color, map)) { Opacity = brush.Opacity },
		Color color => Map(color, map),
		_ => value,
	};

	private static IEnumerable<ResourceDictionary> AllDictionaries(ResourceDictionary root)
	{
		yield return root;
		foreach (var merged in root.MergedDictionaries)
		{
			foreach (var nested in AllDictionaries(merged))
			{
				yield return nested;
			}
		}
	}
}
