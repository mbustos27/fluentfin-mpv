namespace FluentFin.Themes.Classic;

/// <summary>
/// Nixie 1.3.0: Classic, 1.2.0's layout kept as a theme (the user: today's layout stays, alongside the three new ones). It
/// has no views of its own (the shared pages are 1.2.0's) and its dictionary gives the shared keys 1.2.0's look, the top
/// bar's included (Classic.xaml). Last in the fallback chain, and where installs upgraded from 1.2.x start.
/// </summary>
public sealed class ClassicTheme : ILayoutTheme
{
	public LayoutThemeId Id => LayoutThemeId.Classic;

	public string DisplayName => "Classic";

	public Uri Dictionary { get; } = new("ms-appx:///Themes/Classic/Classic.xaml");

	public Type? ViewFor(Type viewModelType) => null;
}
