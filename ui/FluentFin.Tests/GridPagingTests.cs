using FluentFin.Controls;

namespace FluentFin.Tests;

/// <summary>Seaglass scrolling: the grid logic (the Library, See all, the episode list as one column), apart from WinUI.</summary>
public sealed class GridPagingTests
{
	// 10 cards, 4 to a line: [0 1 2 3] [4 5 6 7] [8 9]
	[Theory]
	[InlineData(GridKey.Right, 0, 1)]
	[InlineData(GridKey.Right, 3, 4)]   // the end of a line: on to the next line's first card (reading order)
	[InlineData(GridKey.Right, 9, 9)]   // the last card: no wrap
	[InlineData(GridKey.Left, 4, 3)]
	[InlineData(GridKey.Left, 0, 0)]    // the first card: no wrap
	[InlineData(GridKey.Down, 1, 5)]    // the same column
	[InlineData(GridKey.Down, 5, 9)]
	[InlineData(GridKey.Down, 6, 9)]    // a short last line without that column: its last card
	[InlineData(GridKey.Up, 9, 5)]
	[InlineData(GridKey.Up, 6, 2)]
	[InlineData(GridKey.Home, 7, 0)]
	[InlineData(GridKey.End, 2, 9)]
	public void KeysMoveInTwoDirections(GridKey key, int current, int expected) =>
		Assert.Equal(expected, GridPaging.KeyTarget(key, current, 10, 4, 2));

	[Theory]
	[InlineData(GridKey.Up, 2)]   // the first line: Up leaves the grid (to the sort and filter bar)
	[InlineData(GridKey.Down, 8)] // the last line: Down leaves it (to the pager)
	[InlineData(GridKey.Down, 9)]
	public void PastTheFirstOrLastLine_TheKeyLeavesTheGrid(GridKey key, int current) =>
		Assert.Null(GridPaging.KeyTarget(key, current, 10, 4, 2));

	[Theory]
	// 100 cards, 5 to a line (20 lines), 3 lines to a page
	[InlineData(GridKey.PageDown, 1, 16)]   // three lines down, same column
	[InlineData(GridKey.PageDown, 86, 96)]  // past the end: this column's last card
	[InlineData(GridKey.PageDown, 96, 99)]  // already there: the last card
	[InlineData(GridKey.PageUp, 16, 1)]
	[InlineData(GridKey.PageUp, 7, 2)]      // past the top: this column's first card
	[InlineData(GridKey.PageUp, 3, 3)]      // already on the first line: stays
	public void PageKeys_MoveAPageOfLines_InTheSameColumn(GridKey key, int current, int expected) =>
		Assert.Equal(expected, GridPaging.KeyTarget(key, current, 100, 5, 3));

	[Fact]
	public void AListIsAGridOfOneColumn()
	{
		Assert.Equal(3, GridPaging.KeyTarget(GridKey.Down, 2, 8, 1, 1));
		Assert.Equal(1, GridPaging.KeyTarget(GridKey.Up, 2, 8, 1, 1));
		Assert.Null(GridPaging.KeyTarget(GridKey.Up, 0, 8, 1, 1));   // above the first episode: the season pills
		Assert.Null(GridPaging.KeyTarget(GridKey.Down, 7, 8, 1, 1)); // below the last: what follows
		Assert.Equal(7, GridPaging.KeyTarget(GridKey.End, 0, 8, 1, 1));
		Assert.Equal(0, GridPaging.KeyTarget(GridKey.Home, 5, 8, 1, 1));
	}

	[Fact]
	public void NoCards_NoTarget_AndOddInputsAreSafe()
	{
		Assert.Null(GridPaging.KeyTarget(GridKey.Right, 0, 0, 4, 2));
		Assert.Equal(1, GridPaging.KeyTarget(GridKey.Right, 0, 5, 0, 0));  // no columns known: one
		Assert.Equal(3, GridPaging.KeyTarget(GridKey.Left, 40, 5, 4, 2));  // a stale index: clamped to the last card first
	}

	[Fact]
	public void Columns_AreTheMostCardsOnOneLine()
	{
		// a full line of 4 at 0, one of 4 at 398, a short last line of 2: four columns, whatever line the focus is on
		double[] tops = [0, 0, 0, 0, 398, 398.4, 398, 398, 796, 796];
		Assert.Equal(4, GridPaging.Columns(tops));
		Assert.Equal(1, GridPaging.Columns([]));
		Assert.Equal(1, GridPaging.Columns([0, 398, 796]));
	}

	[Fact]
	public void LinesPerPage_AreTheWholeLinesTheViewShows()
	{
		Assert.Equal(398, GridPaging.LineStride([796, 0, 0, 398, 398.3, 1194]), 1);
		Assert.Equal(0, GridPaging.LineStride([0, 0, 0]));
		Assert.Equal(2, GridPaging.LinesPerPage(1000, 398));
		Assert.Equal(1, GridPaging.LinesPerPage(300, 398)); // at least one
		Assert.Equal(1, GridPaging.LinesPerPage(1000, 0));
	}

	[Theory]
	[InlineData(0, 0, 320, 100, 1, 320)]     // the first card of a library's first page
	[InlineData(4, 200, 320, 20, 205, 320)]  // page three: its place in the whole library
	[InlineData(2, 0, 0, 12, 3, 12)]         // a See all grid: no larger set
	[InlineData(0, 300, 10, 20, 301, 320)]   // a stale total is never fewer than the cards there are
	public void Place_IsInTheWholeSet(int index, int firstIndex, int total, int count, int position, int size) =>
		Assert.Equal((position, size), GridPaging.Place(index, firstIndex, total, count));
}

/// <summary>Seaglass scrolling: a far card focused when the grid's scroll to it ends, unless anything happens first.</summary>
public sealed class PendingFocusTests
{
	[Fact]
	public void TheScrollsEnd_FocusesTheCard_Once()
	{
		var pending = new PendingFocus();
		pending.Begin(99, scroll: 7);

		Assert.Equal(99, pending.Complete(7, focusInGrid: true));
		Assert.Equal(-1, pending.Complete(7, focusInGrid: true));
	}

	[Fact]
	public void AnotherScrollsEnd_IsNotIts()
	{
		var pending = new PendingFocus();
		pending.Begin(99, scroll: 7);

		Assert.Equal(-1, pending.Complete(6, focusInGrid: true)); // the user's wheel, an earlier scroll
		Assert.Equal(99, pending.Complete(7, focusInGrid: true));
	}

	[Fact]
	public void AKeyBeforeTheEnd_CancelsIt()
	{
		// End then Home at once: Home's card is the focused one, so nothing new begins; End's must not win later
		var pending = new PendingFocus();
		pending.Begin(99, scroll: 7);

		Assert.True(pending.Cancel());
		Assert.False(pending.Cancel());
		// the cancelled scroll still ran: when it ends, the card focused by then is shown again; once
		Assert.Equal(PendingFocus.RevealFocused, pending.Complete(7, focusInGrid: true));
		Assert.Equal(-1, pending.Complete(7, focusInGrid: true));
	}

	[Fact]
	public void ACancelledScrollsEnd_LeavesAFocusOutsideTheGridAlone()
	{
		var pending = new PendingFocus();
		pending.Begin(99, scroll: 7);
		pending.Cancel(); // Tab away

		Assert.Equal(-1, pending.Complete(7, focusInGrid: false));
	}

	[Fact]
	public void ANewFarCard_AfterACancelledOne_IsFocusedAtItsOwnScrollsEnd()
	{
		// Home then End at once, both far: Home's scroll ends first and shows nothing (End's card is on its way), End's focuses its card
		var pending = new PendingFocus();
		pending.Begin(0, scroll: 7);
		pending.Cancel();
		pending.Begin(99, scroll: 8);

		Assert.Equal(-1, pending.Complete(7, focusInGrid: true));
		Assert.Equal(99, pending.Complete(8, focusInGrid: true));
	}

	[Fact]
	public void TheFocusHavingLeftTheGrid_IsNotPulledBack()
	{
		// Tab or a click elsewhere before the scroll ended (the focus left, nothing cancelled it)
		var pending = new PendingFocus();
		pending.Begin(99, scroll: 7);

		Assert.Equal(-1, pending.Complete(7, focusInGrid: false));
		Assert.Equal(-1, pending.Index);
	}

	[Fact]
	public void EveryCancel_MovesTheGenerationOn()
	{
		// a focus queued for later (the remembered card) checks it before acting
		var pending = new PendingFocus();
		var before = pending.Generation;

		pending.Cancel();

		Assert.NotEqual(before, pending.Generation);
	}
}
