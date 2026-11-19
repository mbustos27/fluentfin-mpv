using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0 (the UX review's item 15: the measurements turned into checks): a library page's own measurements, logged where
/// the harness reads them (then-140-checks.ps1): the first card's time ("[perf] library: first card N ms after the page was
/// opened", which Classic and Drive-in already logged, now Cinematic and Editorial too) and the loading placeholders' cells
/// against the grid's first cells ("[layout] library skeleton: the same cells as the grid" or what differs), so a skeleton
/// drawn 5 a row against the grid's 6, or 42 px high, fails a run.
/// </summary>
internal sealed class LibraryProbe
{
	/// <summary>How many cells are compared (the first row or so).</summary>
	private const int Cells = 6;

	/// <summary>Within this, two cells are the same (rounding of a scaled layout).</summary>
	public const double Tolerance = 2;

	private readonly FrameworkElement _page;
	private readonly FrameworkElement _skeleton;
	private readonly FrameworkElement _grid;
	private readonly Func<int, Rect?> _skeletonCell;
	private readonly Func<int, Rect?> _gridCell;
	private readonly bool _logFirstCard;
	private readonly long _openedAt = Stopwatch.GetTimestamp();
	private List<Rect>? _placeholders;
	private bool _firstCardLogged, _compared;

	private LibraryProbe(FrameworkElement page, FrameworkElement skeleton, FrameworkElement grid, Func<int, Rect?> skeletonCell, Func<int, Rect?> gridCell, bool logFirstCard)
	{
		_page = page;
		_skeleton = skeleton;
		_grid = grid;
		_skeletonCell = skeletonCell;
		_gridCell = gridCell;
		_logFirstCard = logFirstCard;
		skeleton.LayoutUpdated += Skeleton_LayoutUpdated;
		grid.LayoutUpdated += Grid_LayoutUpdated;
	}

	/// <param name="logFirstCard">Whether the page's first card time is logged here (Classic and Drive-in log their own).</param>
	public static LibraryProbe Attach(FrameworkElement page, FrameworkElement skeleton, FrameworkElement grid, Func<int, Rect?> skeletonCell, Func<int, Rect?> gridCell, bool logFirstCard) =>
		new(page, skeleton, grid, skeletonCell, gridCell, logFirstCard);

	/// <summary>Cell <paramref name="index"/> of a repeater laid out by a grid layout: its layout slot, in the page's coordinates.</summary>
	public static Rect? SlotOf(ItemsRepeater repeater, int index, UIElement page)
	{
		if (repeater.TryGetElement(index) is not FrameworkElement { ActualWidth: > 0 } element)
		{
			return null;
		}

		try
		{
			var slot = LayoutInformation.GetLayoutSlot(element);
			var at = repeater.TransformToVisual(page).TransformPoint(new Point(slot.X, slot.Y));
			return new Rect(at.X, at.Y, slot.Width, slot.Height);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>An element's own rectangle, in the page's coordinates.</summary>
	public static Rect? RectOf(FrameworkElement? element, UIElement page)
	{
		if (element is not { ActualWidth: > 0, ActualHeight: > 0 })
		{
			return null;
		}

		try
		{
			var at = element.TransformToVisual(page).TransformPoint(new Point(0, 0));
			return new Rect(at.X, at.Y, element.ActualWidth, element.ActualHeight);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	/// <summary>What differs between the placeholders' cells and the grid's (empty: the same), cell by cell.</summary>
	public static IReadOnlyList<string> Differences(IReadOnlyList<Rect> placeholders, IReadOnlyList<Rect> grid)
	{
		var found = new List<string>();
		for (var i = 0; i < Math.Min(placeholders.Count, grid.Count); i++)
		{
			var (s, g) = (placeholders[i], grid[i]);
			if (Math.Abs(s.X - g.X) > Tolerance || Math.Abs(s.Y - g.Y) > Tolerance || Math.Abs(s.Width - g.Width) > Tolerance || Math.Abs(s.Height - g.Height) > Tolerance)
			{
				found.Add($"cell {i}: placeholder {Show(s)}, grid {Show(g)}");
			}
		}

		return found;
	}

	private static string Show(Rect r) => FormattableString.Invariant($"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}");

	private List<Rect> Take(Func<int, Rect?> cell)
	{
		var rects = new List<Rect>();
		for (var i = 0; i < Cells && cell(i) is { } r; i++)
		{
			rects.Add(r);
		}

		return rects;
	}

	private void Skeleton_LayoutUpdated(object? sender, object e)
	{
		if (_compared || _skeleton.Visibility != Visibility.Visible || _page.XamlRoot is null)
		{
			return;
		}

		// the latest drawing while it shows (a resize while loading lays it out again)
		var rects = Take(_skeletonCell);
		if (rects.Count >= 2)
		{
			_placeholders = rects;
		}
	}

	private void Grid_LayoutUpdated(object? sender, object e)
	{
		if (_page.XamlRoot is null)
		{
			return;
		}

		var first = _gridCell(0);
		if (first is not null && _logFirstCard && !_firstCardLogged)
		{
			_firstCardLogged = true;
			Log().LogInformation("[perf] library: first card {Ms} ms after the page was opened", (int)Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds);
		}

		if (_compared || first is null || _skeleton.Visibility == Visibility.Visible)
		{
			return;
		}

		// compared once two cells are drawn (a library of one title has no row to compare)
		var grid = Take(_gridCell);
		if (grid.Count < 2)
		{
			return;
		}

		_compared = true;
		_skeleton.LayoutUpdated -= Skeleton_LayoutUpdated;
		_grid.LayoutUpdated -= Grid_LayoutUpdated;
		if (_placeholders is not { } placeholders)
		{
			return; // loaded before the placeholders were drawn: nothing to compare
		}

		var differences = Differences(placeholders, grid);
		if (differences.Count == 0)
		{
			Log().LogInformation("[layout] library skeleton: the same cells as the grid ({Count} compared, {Page})", Math.Min(placeholders.Count, grid.Count), _page.GetType().Name);
		}
		else
		{
			Log().LogWarning("[layout] library skeleton: differs from the grid ({Page}): {Differences}", _page.GetType().Name, string.Join("; ", differences));
		}
	}

	private static ILogger Log() => App.GetService<ILogger<LibraryProbe>>();
}
