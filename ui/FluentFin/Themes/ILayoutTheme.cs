namespace FluentFin.Themes;

/// <summary>The three switchable layout themes (Nixie 1.3.0). Drive-in is the default.</summary>
public enum LayoutThemeId
{
	DriveIn,
	Cinematic,
	Editorial,
}

/// <summary>
/// One layout theme: its resource dictionary and its own views for the browsing pages.
/// Only the browsing pages have per-theme views (Home, Series and Season, Movie and Episode, Library, the player's
/// overlay layout, the sign-in backdrop); every other page is shared and styled by the shared tokens.
/// Views bind to the same view models as the shared pages (FluentFin.Core.ViewModels), so switching a theme never
/// refetches anything.
/// Every theme dictionary (<see cref="Dictionary"/>) defines at least these keys: TopBarStyle, RowHeaderTemplate,
/// PosterTemplate, StillTemplate, PageHeaderStyle. Keys only one theme uses carry the theme's prefix
/// (DriveInMarqueeStyle, CinematicPreviewStageStyle, EditorialGenreStripStyle).
/// </summary>
public interface ILayoutTheme
{
	LayoutThemeId Id { get; }

	/// <summary>The name shown in Settings &gt; Appearance, for example "Drive-in".</summary>
	string DisplayName { get; }

	/// <summary>ms-appx:///Themes/&lt;Id&gt;/&lt;Id&gt;.xaml</summary>
	Uri Dictionary { get; }

	/// <summary>The page type this theme shows for a view model, or null to use the shared page.</summary>
	Type? ViewFor(Type viewModelType);
}
