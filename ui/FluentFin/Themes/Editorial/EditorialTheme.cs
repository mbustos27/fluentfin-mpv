using FluentFin.Core.ViewModels;
using FluentFin.Themes.Editorial.Views;

namespace FluentFin.Themes.Editorial;

/// <summary>
/// Nixie 1.3.0: the Editorial layout theme (an Infuse / Netflix hybrid, a magazine for a big library): a hero tile beside a
/// numbered "Up next" list, a big-type genre strip that picks the shelf, posters whose details open in place under their row,
/// a series page that shows a whole season at once, a library split by letter, and a paused player that reads like a page.
/// Its look is the sunset rule under the top bar, the lit genre, the display headings and a Googie arc; everything else is
/// the shared tokens (docs\reports\Nixie redesign concepts\Nixie redesign concepts.md, "Theme 3: Editorial").
/// </summary>
public sealed class EditorialTheme : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.Editorial;

	public string DisplayName => "Editorial";

	public Uri Dictionary { get; } = new("ms-appx:///Themes/Editorial/Editorial.xaml");

	/// <summary>The browsing pages this theme draws itself; every other page is the shared one.</summary>
	internal static IReadOnlyDictionary<Type, Type> Views { get; } = new Dictionary<Type, Type>
	{
		[typeof(HomeViewModel)] = typeof(EditorialHomePage),
		[typeof(SeriesViewModel)] = typeof(EditorialSeriesPage),
		[typeof(SeasonViewModel)] = typeof(EditorialSeasonPage),
		[typeof(MovieViewModel)] = typeof(EditorialMoviePage),
		[typeof(EpisodeViewModel)] = typeof(EditorialEpisodePage),
		[typeof(LibraryViewModel)] = typeof(EditorialLibraryPage),
	};

	public Type? ViewFor(Type viewModelType) => Views.TryGetValue(viewModelType, out var view) ? view : null;
}
