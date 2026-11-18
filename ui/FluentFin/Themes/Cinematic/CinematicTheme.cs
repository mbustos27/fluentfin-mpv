using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Views;

namespace FluentFin.Themes.Cinematic;

/// <summary>
/// Nixie 1.3.0, the Cinematic layout theme (Apple TV style): the artwork is the interface. Home opens on one title that fills
/// the window under a floating glass island, shelves of 16:9 stills with title art, one "Up next" shelf, a 1.1x focus zoom
/// with a neon rim, full-bleed item pages with the episodes as a shelf, a preview stage over the library, and a top tabs
/// panel in the player. Design: docs\reports\Nixie redesign concepts\Nixie redesign concepts.md, "Theme 1: Cinematic".
/// </summary>
public sealed class CinematicTheme : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.Cinematic;

	public string DisplayName => "Cinematic";

	public Uri Dictionary { get; } = new("ms-appx:///Themes/Cinematic/Cinematic.xaml");

	/// <summary>The browsing pages this theme draws itself; every other view model keeps the shared page (null).</summary>
	internal static readonly IReadOnlyDictionary<Type, Type> Views = new Dictionary<Type, Type>
	{
		[typeof(HomeViewModel)] = typeof(CinematicHomePage),
		[typeof(SeriesViewModel)] = typeof(CinematicSeriesPage),
		[typeof(SeasonViewModel)] = typeof(CinematicSeasonPage),
		[typeof(MovieViewModel)] = typeof(CinematicMoviePage),
		[typeof(EpisodeViewModel)] = typeof(CinematicEpisodePage),
		[typeof(LibraryViewModel)] = typeof(CinematicLibraryPage),
	};

	public Type? ViewFor(Type viewModelType) => Views.TryGetValue(viewModelType, out var view) ? view : null;
}
