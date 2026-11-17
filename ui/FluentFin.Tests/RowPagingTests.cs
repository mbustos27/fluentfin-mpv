using FluentFin.Controls;

namespace FluentFin.Tests;

/// <summary>Seaglass scrolling: the row logic (one axis per input, snapping, keys, reveal), apart from WinUI.</summary>
public sealed class RowPagingTests
{
	[Theory]
	[InlineData(false, false, WheelTarget.Page)] // the plain wheel scrolls the page, even over a row
	[InlineData(true, false, WheelTarget.Row)]   // the tilt wheel
	[InlineData(false, true, WheelTarget.Row)]   // Shift+wheel
	[InlineData(true, true, WheelTarget.Row)]
	public void EachInputOwnsOneAxis(bool tilt, bool shift, WheelTarget expected) =>
		Assert.Equal(expected, RowPaging.TargetOf(tilt, shift));

	[Fact]
	public void WheelDirections()
	{
		Assert.Equal(96, RowPaging.Pixels(-120));          // a notch toward you scrolls down
		Assert.Equal(-96, RowPaging.Pixels(120));
		Assert.Equal(96, RowPaging.RowPixels(-120, false)); // Shift+wheel down moves the row right
		Assert.Equal(96, RowPaging.RowPixels(120, true));   // tilt right moves the row right
		Assert.Equal(-96, RowPaging.RowPixels(-120, true));
		Assert.Equal(-12, RowPaging.Pixels(15), 6);          // a precision wheel's small deltas add up
	}

	[Theory]
	// viewport 1000, extent 4000, card 390: a page is the viewport minus a card (610), in whole cards (390)
	[InlineData(0, true, 390)]
	[InlineData(390, true, 780)]
	[InlineData(2700, true, 3000)]  // the row's end: 4000 - 1000
	[InlineData(3000, true, 3000)]
	[InlineData(780, false, 390)]
	[InlineData(390, false, 0)]
	[InlineData(0, false, 0)]
	public void ArrowsPageToACardEdge_AndStopAtTheEnds(double offset, bool forward, double expected) =>
		Assert.Equal(expected, RowPaging.Page(offset, 1000, 4000, 390, forward));

	[Fact]
	public void AnArrowAlwaysMovesAtLeastOneCard_EvenInANarrowRow()
	{
		Assert.Equal(300, RowPaging.Page(0, 200, 3000, 300, forward: true));
		Assert.Equal(0, RowPaging.Page(300, 200, 3000, 300, forward: false));
	}

	[Fact]
	public void TheLastCardShownStaysAsAPeek()
	{
		// 1000 wide, 390 cards: 2.56 cards show; the page moves 1 card (390): card 2 (offset 390) is now the first
		var next = RowPaging.Page(0, 1000, 8000, 390, forward: true);
		Assert.Equal(390, next);
		Assert.True(next < 1000); // the second card is still on screen
	}

	[Theory]
	[InlineData(0, 1000, 4000, false, true)]
	[InlineData(1500, 1000, 4000, true, true)]
	[InlineData(3000, 1000, 4000, true, false)]
	[InlineData(0, 4000, 1000, false, false)] // fits: nothing to scroll
	public void ArrowsAreDisabledAtTheEnds(double offset, double viewport, double extent, bool back, bool forward)
	{
		Assert.Equal(back, RowPaging.CanScrollBack(offset));
		Assert.Equal(forward, RowPaging.CanScrollForward(offset, extent, viewport));
	}

	[Theory]
	[InlineData(RowKey.Left, 0, 10, 0)]   // no wrap
	[InlineData(RowKey.Left, 5, 10, 4)]
	[InlineData(RowKey.Right, 9, 10, 9)]  // no wrap
	[InlineData(RowKey.Right, 3, 10, 4)]
	[InlineData(RowKey.Home, 7, 10, 0)]
	[InlineData(RowKey.End, 2, 10, 9)]
	public void KeysMoveCardByCard(RowKey key, int current, int count, int expected) =>
		Assert.Equal(expected, RowPaging.KeyTarget(key, current, count));

	[Fact]
	public void NoCards_NoTarget() => Assert.Null(RowPaging.KeyTarget(RowKey.Right, 0, 0));

	[Fact]
	public void UpAndDown_KeepTheColumn_ByTheNearestCentre()
	{
		// a wide row's cards (centres 195, 585, 975) to a poster row's (112, 337, 562, 787)
		Assert.Equal(2, RowPaging.NearestByCenter([112, 337, 562, 787], 585));
		Assert.Equal(0, RowPaging.NearestByCenter([112, 337, 562, 787], 100));
		Assert.Equal(-1, RowPaging.NearestByCenter([], 100));
	}

	[Theory]
	[InlineData(100, 400, 800, 24, 0)]        // in view
	[InlineData(-10, 300, 800, 24, -34)]       // under the title bar: up to the margin
	[InlineData(500, 790, 800, 24, 14)]        // past the bottom edge
	[InlineData(0, 900, 800, 24, -24)]         // taller than the view: its top
	public void AFocusedRowIsRevealedClearOfTheEdges(double top, double bottom, double viewport, double margin, double expected) =>
		Assert.Equal(expected, RowPaging.VerticalDeltaToReveal(top, bottom, viewport, margin));

	[Theory]
	[InlineData(0, 390, 0)]
	[InlineData(500, 390, 390)]
	[InlineData(600, 390, 780)]
	[InlineData(5000, 390, 3000)] // kept inside the row
	[InlineData(250, 0, 250)]
	public void SnapLandsOnACardEdge(double offset, double stride, double expected) =>
		Assert.Equal(expected, RowPaging.Snap(offset, stride, 4000, 1000));

	[Fact]
	public void Names_AreUniquePerRow()
	{
		Assert.Equal("Scroll Next Up left", RowPaging.ArrowName("Next Up", false));
		Assert.Equal("Scroll Next Up right", RowPaging.ArrowName("Next Up", true));
		Assert.Equal("See all Next Up", RowPaging.SeeAllName("Next Up"));
		Assert.Equal("Scroll left", RowPaging.ArrowName("", false));
	}
}
