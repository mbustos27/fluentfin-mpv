using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0 (the UX review's X15, item 13): a library's loading placeholders are laid out by the grid's own layout (a copy
/// of its every setting, kept in step), so they stand where the cards will: the skeletons drew 5 tiles a row against 6, 42 px
/// high, or a phantom button.
/// </summary>
internal static class LibrarySkeleton
{
	public static void Mirror(UniformGridLayout from, UniformGridLayout to)
	{
		to.Orientation = from.Orientation;
		to.ItemsJustification = from.ItemsJustification;
		to.ItemsStretch = from.ItemsStretch;
		to.MinItemWidth = from.MinItemWidth;
		to.MinItemHeight = from.MinItemHeight;
		to.MinColumnSpacing = from.MinColumnSpacing;
		to.MinRowSpacing = from.MinRowSpacing;
		to.MaximumRowsOrColumns = from.MaximumRowsOrColumns;
	}
}

/// <summary>
/// Nixie 1.4.0 (item 13): a poster grid's cell for each density: the medium size the theme draws, small and large a step
/// either way, the height following the width (a poster's 2:3 and its caption).
/// </summary>
internal static class LibraryDensities
{
	/// <summary>The cell's width for <paramref name="density"/> from the theme's medium width.</summary>
	public static double Width(LibraryDensity density, double medium) => density switch
	{
		LibraryDensity.Small => Math.Round(medium * 0.75),
		LibraryDensity.Large => Math.Round(medium * 1.3),
		_ => medium,
	};

	/// <summary>Sets a grid's cell for the density, keeping the medium cell's shape.</summary>
	public static void Apply(UniformGridLayout layout, LibraryDensity density, double mediumWidth, double mediumHeight)
	{
		// the caption under the art keeps its height; the art keeps its 2:3
		var caption = Math.Max(0, mediumHeight - mediumWidth * 1.5);
		var width = Width(density, mediumWidth);
		layout.MinItemWidth = width;
		layout.MinItemHeight = Math.Round(width * 1.5 + caption);
	}
}
