namespace FluentFin.Themes;

/// <summary>
/// The switchable layout themes (Nixie 1.3.0), in the order the picker shows them and the fallback follows
/// (<see cref="Services.ThemeChoice.Resolve"/>). Classic is 1.2.0's layout, kept as a theme of its own: it is last, so a
/// theme that is missing falls back through the next ones and ends there. New installs start on
/// <see cref="Services.ThemeChoice.DefaultForNewInstalls"/>; installs upgraded from 1.2.x start on Classic.
/// </summary>
public enum LayoutThemeId
{
	DriveIn,
	Cinematic,
	Editorial,
	Classic,
}

/// <summary>
/// One layout theme: its resource dictionary and its own views for the browsing pages.
/// Only the browsing pages have per-theme views (Home, Series and Season, Movie and Episode, Library, the player's
/// overlay layout, the sign-in backdrop); every other page is shared and styled by the shared tokens.
/// Views bind to the same view models as the shared pages (FluentFin.Core.ViewModels), so switching a theme never
/// refetches anything.
/// Every theme dictionary (<see cref="Dictionary"/>) defines at least the keys in <see cref="ThemeContract.RequiredKeys"/>:
/// TopBarStyle (a Style for <see cref="Controls.TopBarChrome"/>), RowHeaderTemplate, PosterTemplate, StillTemplate
/// (DataTemplates) and PageHeaderStyle (a Style); optionally SignInBackdropTemplate (a DataTemplate the shared LoginPage
/// hosts behind its form, given the LoginViewModel). Keys only one theme uses carry the theme's prefix
/// (DriveInMarqueeStyle, CinematicPreviewStageStyle, EditorialGenreStripStyle).
/// The app finds every non-abstract ILayoutTheme with a public parameterless constructor in its assembly
/// (<see cref="Services.ThemeService"/>): a theme registers itself by existing.
/// </summary>
public interface ILayoutTheme
{
	LayoutThemeId Id { get; }

	/// <summary>The name shown in Settings &gt; Appearance, for example "Drive-in".</summary>
	string DisplayName { get; }

	/// <summary>ms-appx:///Themes/&lt;Id&gt;/&lt;Id&gt;.xaml</summary>
	Uri Dictionary { get; }

	/// <summary>
	/// The page type this theme shows for a view model, or null to use the shared page. Asked only for the view models in
	/// <see cref="ThemeContract.ThemableViewModels"/>; every other page is shared whatever this returns.
	/// </summary>
	Type? ViewFor(Type viewModelType);

	/// <summary>
	/// The theme's own block in Settings &gt; Appearance (a UI element, or an object a DataTemplate in the theme's dictionary
	/// draws), shown under the picker while the theme is active; null for none.
	/// </summary>
	object? CreateOptions() => null;
}
