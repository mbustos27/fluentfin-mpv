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
	[InlineData(true, false, 0, 5, true, true)]    // Tab into the row: its remembered card
	[InlineData(false, false, 0, 5, true, false)]  // UI Automation's SetFocus (Narrator), the app naming a card: that card
	[InlineData(true, true, 0, 5, true, false)]    // the row moving the focus itself
	[InlineData(true, false, -1, 5, true, false)]  // nothing remembered
	[InlineData(true, false, 5, 5, true, false)]   // the remembered card is gone
	[InlineData(true, false, 0, 5, false, false)]  // moving along the row
	public void OnlyTheKeyboardComingIn_GoesToTheRememberedCard(bool keyboard, bool directed, int remembered, int count, bool fromOutside, bool expected) =>
		Assert.Equal(expected, RowPaging.RedirectsToRemembered(keyboard, directed, remembered, count, fromOutside));

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

	// Seaglass 1.1.4: a card leaves the row (Remove from Continue Watching): the keyboard stays in the row
	[Theory]
	[InlineData(2, 2, 5, 2)] // the focused card left: the one that took its place
	[InlineData(4, 4, 4, 3)] // the last one left: the new last one
	[InlineData(3, 1, 4, 2)] // one before it left: the same card, one place earlier
	[InlineData(1, 3, 4, 1)] // one after it left: where it was
	[InlineData(0, 0, 0, -1)] // none left
	public void AfterACardLeaves_TheFocusStaysInTheRow(int focused, int removed, int count, int expected) =>
		Assert.Equal(expected, RowPaging.FocusAfterRemoval(focused, removed, count));

	// never hidden (the 1.1.4 layout audit, item 2); 1.2.0 (the button review, L7 and L8): at full strength at rest too
	[Theory]
	[InlineData(false, false, 1.0)]
	[InlineData(true, false, 1.0)]
	[InlineData(false, true, 1.0)]
	public void TheArrows_AreAtFullStrength_WhereverThePointerOrTheKeyboardIs(bool pointer, bool focus, double opacity) =>
		Assert.Equal(opacity, RowPaging.ArrowOpacity(pointer, focus));
}

public sealed class HeroDotsTests
{
	[Theory]
	[InlineData(Windows.System.VirtualKey.Left, 0, 5, 0)]
	[InlineData(Windows.System.VirtualKey.Left, 3, 5, 2)]
	[InlineData(Windows.System.VirtualKey.Right, 4, 5, 4)]
	[InlineData(Windows.System.VirtualKey.Right, 1, 5, 2)]
	[InlineData(Windows.System.VirtualKey.Home, 3, 5, 0)]
	[InlineData(Windows.System.VirtualKey.End, 0, 5, 4)]
	public void ArrowsPickASlide(Windows.System.VirtualKey key, int current, int count, int expected) =>
		Assert.Equal(expected, HeroDots.KeyTarget(key, current, count));

	[Fact]
	public void OneSlide_NothingToPick() => Assert.Null(HeroDots.KeyTarget(Windows.System.VirtualKey.Right, 0, 1));

	[Fact]
	public void OtherKeys_AreNotTheDots() => Assert.Null(HeroDots.KeyTarget(Windows.System.VirtualKey.Up, 1, 5));
}

/// <summary>
/// 1.1.4 (layout audit, item 22; the states stream's Narrator pass): an empty or failed library shows no Sort, direction,
/// Filter, count or A-Z (and there is no pager: the grid scrolls on).
/// </summary>
public sealed class LibraryBarTests
{
	[Theory]
	[InlineData(FluentFin.Core.ViewModels.LoadStatus.Loading, Microsoft.UI.Xaml.Visibility.Visible)]
	[InlineData(FluentFin.Core.ViewModels.LoadStatus.Loaded, Microsoft.UI.Xaml.Visibility.Visible)]
	[InlineData(FluentFin.Core.ViewModels.LoadStatus.Empty, Microsoft.UI.Xaml.Visibility.Collapsed)]
	[InlineData(FluentFin.Core.ViewModels.LoadStatus.Failed, Microsoft.UI.Xaml.Visibility.Collapsed)]
	public void TheBar_GoesOnAnEmptyOrFailedLibrary(FluentFin.Core.ViewModels.LoadStatus status, Microsoft.UI.Xaml.Visibility expected)
	{
		Assert.Equal(expected, FluentFin.Views.LibraryPage.BarVisibility(status));
		Assert.Equal(expected, FluentFin.Views.LibraryPage.LettersVisibility([new("A", 0), new("B", 5)], status));
	}

	[Fact]
	public void OneLetter_IsNoJump() =>
		Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, FluentFin.Views.LibraryPage.LettersVisibility([new("A", 0)], FluentFin.Core.ViewModels.LoadStatus.Loaded));
}

/// <summary>1.1.4 (the items stream's request): Home's hero runs edge to edge; the rows keep their inset.</summary>
public sealed class HomeInsetTests
{
	[Fact]
	public void TheRows_Keep24ASide_And36OnTop_Or20UnderTheHero()
	{
		var slide = new FluentFin.Core.ViewModels.HeroSlide(new Jellyfin.Sdk.Generated.Models.BaseItemDto(), "T", "", "", false, Guid.NewGuid(), "tag", null, null);

		Assert.Equal(new Microsoft.UI.Xaml.Thickness(24, 36, 24, 0), FluentFin.Views.HomePage.RowsMargin([]));
		Assert.Equal(new Microsoft.UI.Xaml.Thickness(24, 36, 24, 0), FluentFin.Views.HomePage.RowsMargin(null));
		Assert.Equal(new Microsoft.UI.Xaml.Thickness(24, 20, 24, 0), FluentFin.Views.HomePage.RowsMargin([slide]));
	}

	/// <summary>
	/// 1.5 huntfix (BH-150-M1): the page runs under the transparent top bar, so Home's notice ("Some of Home couldn't be
	/// loaded") or its message starts below the bar (its text ran through the tabs and Try again sat under the search box in
	/// Classic); with nothing to say it takes no room, and the hero stays right under the title bar.
	/// </summary>
	[Fact]
	public void HomesNotice_StartsBelowTheTopBar_AndTakesNoRoomWhenItHasNothingToSay()
	{
		var notice = FluentFin.Views.HomePage.StateMargin(hasProblem: true, hasMessage: false);
		Assert.True(notice.Top >= TopBar.Height + 8, $"top {notice.Top}");
		Assert.True(notice.Bottom > 0);                                         // room before the hero
		Assert.True(FluentFin.Views.HomePage.StateMargin(false, true).Top >= TopBar.Height + 8);
		Assert.Equal(new Microsoft.UI.Xaml.Thickness(36, 0, 36, 0), FluentFin.Views.HomePage.StateMargin(false, false));
	}
}

/// <summary>The review, item 7: the focus hold after Back or a jump gives way to anything the user does.</summary>
public sealed class FocusHoldTests
{
	[Fact]
	public void TheHold_FocusesAgainUntilACardHasIt()
	{
		var hold = new FluentFin.Controls.FocusHold();

		Assert.True(hold.Tick(-1));  // the title bar took it: again
		Assert.False(hold.Tick(62)); // on the card: done
		Assert.True(hold.Done);
		Assert.False(hold.Tick(-1));
	}

	[Fact]
	public void TheHold_EndsOnAnyUserInput()
	{
		var hold = new FluentFin.Controls.FocusHold();
		Assert.True(hold.Tick(-1));

		hold.UserInput(); // a click in the search box

		Assert.False(hold.Tick(-1));
		Assert.True(hold.Done);
	}

	[Fact]
	public void TheHold_GivesUpAfterItsTicks()
	{
		var hold = new FluentFin.Controls.FocusHold(maxTicks: 3);

		Assert.Equal([true, true, true, false], Enumerable.Range(0, 4).Select(_ => hold.Tick(-1)));
	}

	[Fact]
	public void CardGrid_EndsTheHold_OnAClickAKeyOrTheWheel()
	{
		var dir = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(dir, "FluentFin.sln")))
		{
			dir = Path.GetDirectoryName(dir)!;
		}

		var code = File.ReadAllText(Path.Combine(dir, "FluentFin", "Controls", "CardGrid.cs"));
		Assert.Contains("content.AddHandler(UIElement.PointerPressedEvent, pointer, true);", code);
		Assert.Contains("content.AddHandler(UIElement.KeyDownEvent, key, true);", code);
		Assert.Contains("else if (hold.Tick(IndexOf(", code);
	}
}
