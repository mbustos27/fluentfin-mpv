namespace FluentFin.Controls;

/// <summary>A key a card grid (or the episode list, a grid of one column) moves the focus with.</summary>
public enum GridKey
{
	Left,
	Right,
	Up,
	Down,
	Home,
	End,
	PageUp,
	PageDown,
}

/// <summary>
/// Seaglass scrolling: the grid logic (the Library, a row's "See all", the episode list as one column), apart from WinUI so it
/// can be tested: which card a key moves to, how many cards a line holds, how many lines a page shows, a card's place.
/// The cards are in reading order, <c>columns</c> to a line, the last line possibly short.
/// </summary>
internal static class GridPaging
{
	/// <summary>
	/// The card a key moves to; null when the key leaves the grid (Up from the first line, Down from the last: the focus goes to
	/// what is above or below). Left/Right go card by card in reading order, across the end of a line, never past the ends (no
	/// wrap); Up/Down keep the column, and Down to a short last line without that column goes to its last card; Home/End to the
	/// first and last card; PageUp/PageDown move a page of lines in the same column, stopping at the first or last line.
	/// </summary>
	public static int? KeyTarget(GridKey key, int current, int count, int columns, int pageLines)
	{
		if (count <= 0)
		{
			return null;
		}

		columns = Math.Max(1, columns);
		pageLines = Math.Max(1, pageLines);
		current = Math.Clamp(current, 0, count - 1);
		var line = current / columns;
		var lastLine = (count - 1) / columns;

		switch (key)
		{
			case GridKey.Left:
				return Math.Max(0, current - 1);
			case GridKey.Right:
				return Math.Min(count - 1, current + 1);
			case GridKey.Home:
				return 0;
			case GridKey.End:
				return count - 1;
			case GridKey.Up:
				return line == 0 ? null : current - columns;
			case GridKey.Down:
				return line == lastLine ? null : Math.Min(count - 1, current + columns);
			case GridKey.PageUp:
			{
				var target = current - pageLines * columns;
				return target >= 0 ? target : current % columns;
			}
			case GridKey.PageDown:
			{
				var target = current + pageLines * columns;
				if (target <= count - 1)
				{
					return target;
				}

				// the last card of this column; already there: the last card
				var sameColumn = current + (count - 1 - current) / columns * columns;
				return sameColumn > current ? sameColumn : count - 1;
			}
			default:
				return null;
		}
	}

	/// <summary>How many cards a line holds: the most cards whose tops are on one line (within a pixel); at least 1.</summary>
	public static int Columns(IEnumerable<double> tops)
	{
		var lines = new List<(double Top, int Count)>();
		foreach (var top in tops)
		{
			var i = lines.FindIndex(l => Math.Abs(l.Top - top) < 1);
			if (i < 0)
			{
				lines.Add((top, 1));
			}
			else
			{
				lines[i] = (lines[i].Top, lines[i].Count + 1);
			}
		}

		return lines.Count == 0 ? 1 : lines.Max(l => l.Count);
	}

	/// <summary>The distance from one line to the next (the smallest gap between two different tops); 0 with fewer than two lines.</summary>
	public static double LineStride(IEnumerable<double> tops)
	{
		var distinct = tops.OrderBy(t => t).Aggregate(new List<double>(), (list, top) =>
		{
			if (list.Count == 0 || top - list[^1] >= 1)
			{
				list.Add(top);
			}

			return list;
		});

		var stride = 0.0;
		for (var i = 1; i < distinct.Count; i++)
		{
			var gap = distinct[i] - distinct[i - 1];
			stride = stride == 0 ? gap : Math.Min(stride, gap);
		}

		return stride;
	}

	/// <summary>The whole lines a viewport shows (what PageUp/PageDown move by); at least 1.</summary>
	public static int LinesPerPage(double viewport, double lineStride) =>
		lineStride <= 0 || viewport <= 0 ? 1 : Math.Max(1, (int)Math.Floor(viewport / lineStride));

	/// <summary>
	/// A card's "n of m" for Narrator: its place in the whole set (a library page starts at <paramref name="firstIndex"/>), out
	/// of <paramref name="total"/> (never fewer than the cards there are).
	/// </summary>
	public static (int Position, int Size) Place(int index, int firstIndex, int total, int count) =>
		(Math.Max(0, firstIndex) + index + 1, Math.Max(total, Math.Max(0, firstIndex) + count));
}

/// <summary>
/// Seaglass scrolling: a card a grid focuses once it has scrolled there (a far card: End, Home, a remembered one), apart from
/// WinUI. Anything the user does before the scroll ends (another key, Tab, a click, the focus leaving) cancels it, and it
/// never takes the focus back from outside the grid. A cancelled scroll still runs (WinUI has no way to stop it), so when it
/// ends the card that has the focus by then is scrolled back into view (End then Home at once: the view went to the end).
/// </summary>
internal sealed class PendingFocus
{
	/// <summary>What <see cref="Complete"/> says when a cancelled scroll ended with the focus in the grid: show the focused card.</summary>
	public const int RevealFocused = -2;

	private int _scroll = -1;
	private readonly HashSet<int> _cancelled = [];

	/// <summary>The card waiting for its scroll; -1 for none.</summary>
	public int Index { get; private set; } = -1;

	/// <summary>Moves on at every cancel: a focus queued for later checks it has not changed.</summary>
	public int Generation { get; private set; }

	public void Begin(int index, int scroll)
	{
		Index = index;
		_scroll = scroll;
	}

	/// <returns>Whether a card was waiting.</returns>
	public bool Cancel()
	{
		var was = Index >= 0;
		if (was)
		{
			_cancelled.Add(_scroll);
		}

		Index = _scroll = -1;
		Generation++;
		return was;
	}

	/// <summary>
	/// A scroll ended: the card to focus now; <see cref="RevealFocused"/> for a cancelled scroll of ours with the focus still in
	/// the grid and no newer far card on its way; else -1 (another scroll, or the focus is no longer in the grid).
	/// </summary>
	public int Complete(int scroll, bool focusInGrid)
	{
		if (_cancelled.Remove(scroll))
		{
			return focusInGrid && Index < 0 ? RevealFocused : -1;
		}

		if (Index < 0 || scroll != _scroll)
		{
			return -1;
		}

		var index = Index;
		Index = _scroll = -1;
		return focusInGrid ? index : -1;
	}
}
