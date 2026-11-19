namespace FluentFin.Core.Theming;

/// <summary>
/// Nixie 1.4.0 (the UX review's plan item 13, X15, CL-m2, CL-m7, DI-p1, C-M11): one page grid per theme. Each theme gives a
/// gutter (its side margin at a 1100 px and at a 1920 px window, the same on both sides) and the y of a page title's top in
/// the window; every shared page (Libraries, a collection, Search, Settings) takes them, so its title and edges are where
/// that theme's library puts its own. Everything lands on the 4 epx grid.
/// </summary>
public static class PageGridMetrics
{
	/// <summary>The window widths the two gutters are given at; between them the gutter grows in proportion.</summary>
	public const double NarrowWidth = 1100, WideWidth = 1920;

	/// <summary>The Classic grid, and the fallback when a theme gives none: 36 px sides, titles at y 72.</summary>
	public const double DefaultGutter = 36, DefaultTitleY = 72;

	/// <summary>The keys a theme dictionary gives them by (x:Double).</summary>
	public const string GutterNarrowKey = "PageGutterNarrow", GutterWideKey = "PageGutterWide", TitleYKey = "PageTitleY";

	/// <summary>The nearest multiple of 4.</summary>
	public static double Snap(double value) => Math.Round(value / 4, MidpointRounding.AwayFromZero) * 4;

	/// <summary>The side margin at <paramref name="width"/>: <paramref name="narrow"/> up to 1100, <paramref name="wide"/> from 1920, in proportion between; on the 4 epx grid.</summary>
	public static double Gutter(double narrow, double wide, double width)
	{
		var t = Math.Clamp((width - NarrowWidth) / (WideWidth - NarrowWidth), 0, 1);
		return Snap(narrow + ((wide - narrow) * t));
	}

	/// <summary>
	/// The gap that makes whole columns of <paramref name="item"/> px fill <paramref name="width"/> (at least
	/// <paramref name="least"/>): the last column ends at the row's right edge, which is the toolbar's.
	/// </summary>
	public static double ColumnSpacing(double width, double item, double least = 16)
	{
		if (width <= 0 || item <= 0)
		{
			return least;
		}

		var columns = Math.Max(1, (int)Math.Floor((width + least) / (item + least)));
		return columns < 2 ? least : Math.Max(least, Math.Floor((width - (columns * item)) / (columns - 1) * 100) / 100);
	}

	/// <summary>A Libraries tile: its 16:9 art and its caption; tiles grow up to the wide size when a row has room.</summary>
	public const double MinTileWidth = 320, MaxTileWidth = 480, TileSpacing = 16, TileCaption = 30;

	/// <summary>
	/// The columns and the tile width for <paramref name="count"/> tiles in <paramref name="width"/>: as many columns as fit,
	/// then as few as still give the same number of rows (six that fit five a row are three and three: no tile alone on a row,
	/// the UX review's CL-p5), each tile as wide as the row allows up to <see cref="MaxTileWidth"/>.
	/// </summary>
	public static (int Columns, double TileWidth) Tiles(double width, int count)
	{
		if (width <= 0 || count <= 0)
		{
			return (1, MinTileWidth);
		}

		var fit = Math.Max(1, (int)Math.Floor((width + TileSpacing) / (MinTileWidth + TileSpacing)));
		var columns = Math.Min(fit, count);
		var rows = (int)Math.Ceiling(count / (double)columns);
		columns = (int)Math.Ceiling(count / (double)rows);
		var tile = Math.Floor(Math.Min(MaxTileWidth, (width - ((columns - 1) * TileSpacing)) / columns));
		return (columns, Math.Max(Math.Min(MinTileWidth, width), tile));
	}

	/// <summary>A tile's height: its 16:9 art, the gap and the caption.</summary>
	public static double TileHeight(double tileWidth) => Math.Round(tileWidth * 9 / 16) + 8 + TileCaption;

	/// <summary>The space above a page's title, inside a page that starts <paramref name="pageTop"/> px down the window, for the title to sit at <paramref name="titleY"/>.</summary>
	public static double TitleTop(double titleY, double pageTop) => Math.Max(0, Snap(titleY - pageTop));
}
