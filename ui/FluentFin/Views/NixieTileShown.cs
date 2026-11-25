using FluentFin.Core.SmartCollections;
using Microsoft.UI.Xaml;

namespace FluentFin.Views;

/// <summary>
/// Nixie 1.8 (P1-4, stream t): what the Collections page's tile template needs of the app (its words are
/// <see cref="NixieTileWords"/>' in Core): a badge shown or not, and the height of the tile's words at Windows' text size.
/// A Visibility, not a bool: x:Bind's own cast of a function's bool to Visibility doesn't compile in a DataTemplate (its
/// generated code names a value it never declared).
/// </summary>
public static class NixieTileShown
{
	private static Windows.UI.ViewManagement.UISettings? s_settings;

	/// <summary>Visible when the tile has a badge <paramref name="index"/>.</summary>
	public static Visibility Badge(SmartCollection? nixie, int index) => NixieTileWords.HasBadge(nixie, index) ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>
	/// The tile's words' height (<see cref="NixieTileWords.WordsHeight"/>) at Windows' text size now (Settings &gt;
	/// Accessibility &gt; Text size): 124 at 100 %, 154 at 125 %, 184 at 150 %. Read as each tile is made, so the tiles of a
	/// page are one height; a text size changed while a page shows takes effect on the next visit.
	/// </summary>
	public static double WordsHeight => NixieTileWords.WordsHeight(TextScale());

	private static double TextScale()
	{
		try
		{
			s_settings ??= new Windows.UI.ViewManagement.UISettings();
			return s_settings.TextScaleFactor;
		}
		catch (Exception ex) when (ex is not OutOfMemoryException)
		{
			return 1; // no settings to read (a test host): 100 %
		}
	}
}
