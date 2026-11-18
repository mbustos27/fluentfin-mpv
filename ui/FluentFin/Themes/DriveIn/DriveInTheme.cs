using FluentFin.Core.ViewModels;
using FluentFin.Themes.DriveIn.Views;

namespace FluentFin.Themes.DriveIn;

/// <summary>
/// Nixie 1.3.0, the default layout theme: Drive-in. Home is a drive-in at dusk (the featured title on the outdoor screen, a
/// motel sign of what is coming up, marquee row headers, every number a nixie tube); away from Home the theme lives in
/// boards, tubes and bulbs while posters and text stay on plain, opaque ground. The design: "Theme 2: Drive-in" of
/// docs\reports\Nixie redesign concepts\Nixie redesign concepts.md and its Drive-in mockups. Found by ThemeService by
/// reflection: nothing registers it.
/// </summary>
public sealed class DriveInTheme : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.DriveIn;

	public string DisplayName => "Drive-in";

	public Uri Dictionary { get; } = new("ms-appx:///Themes/DriveIn/DriveIn.xaml");

	/// <summary>The theme's own views, by the view model they show (the shared page for any other).</summary>
	public static IReadOnlyDictionary<Type, Type> Views { get; } = new Dictionary<Type, Type>
	{
		[typeof(HomeViewModel)] = typeof(DriveInHomePage),
		[typeof(SeriesViewModel)] = typeof(DriveInSeriesPage),
		[typeof(SeasonViewModel)] = typeof(DriveInSeasonPage),
		[typeof(LibraryViewModel)] = typeof(DriveInLibraryPage),
		[typeof(MovieViewModel)] = typeof(DriveInMoviePage),
		[typeof(EpisodeViewModel)] = typeof(DriveInEpisodePage),
	};

	public Type? ViewFor(Type viewModelType) => Views.TryGetValue(viewModelType, out var view) ? view : null;

	/// <summary>Settings &gt; Appearance: Compact Home (<see cref="DriveInOptions"/>).</summary>
	public object? CreateOptions() => DriveInOptions.Create();
}
