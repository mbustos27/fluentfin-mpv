using Microsoft.UI.Xaml;

namespace FluentFin.Themes.Cinematic;

/// <summary>
/// Cinematic's own resources (Cards.xaml: the colours on artwork, the glow, the scrims, the card templates), merged once into
/// the application's resources the first time a Cinematic view or control is made. A control's XAML finds its resources in
/// itself and the application only (it is read before it joins a page), and the theme dictionary (Cinematic.xaml, which
/// merges the same file) is only there while Cinematic is the theme; every key in Cards.xaml carries the Cinematic prefix,
/// so it never stands in for another theme's.
/// </summary>
public static class CinematicResources
{
	public static readonly Uri Cards = new("ms-appx:///Themes/Cinematic/Cards.xaml");

	private static bool s_merged;

	public static void Ensure()
	{
		if (s_merged || Application.Current is not { } app)
		{
			return;
		}

		s_merged = true;
		if (!app.Resources.MergedDictionaries.Any(d => d.Source == Cards))
		{
			app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = Cards });
		}
	}
}
