using FluentFin.Controls;
using FluentFin.Services;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction items 10 and 13): switching seasons with the pills replaces the season in the
/// Back history (a season of the same series only); the Home hero's previous and next buttons go round. The items review:
/// how Back gives the focus back, when a row asks for its thumbnail.
/// </summary>
public sealed class ItemNavigationTests
{
	private static readonly Guid Show = Guid.NewGuid();

	private static (Type Page, object? Parameter) Entry(Type page, Guid? seriesId = null) =>
		(page, seriesId is { } id ? new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, SeriesId = id } : new BaseItemDto { Id = Guid.NewGuid() });

	private static bool Switch(List<(Type Page, object? Parameter)> backStack, (Type Page, object? Parameter) incoming, NavigationMode mode = NavigationMode.New) =>
		NavigationService.DropSwitchedSeasonPage(backStack, x => x.Page, x => x.Parameter, incoming.Page, incoming.Parameter, mode);

	[Fact]
	public void ASeasonPill_ReplacesTheSeasonInTheHistory_BackGoesToTheSeries()
	{
		// Series -> Season 1, then the pills: Season 2, Season 3
		var backStack = new List<(Type Page, object? Parameter)> { Entry(typeof(HomePage)), Entry(typeof(SeriesPage)) };
		backStack.Add(Entry(typeof(SeasonPage), Show));   // left Season 1 for Season 2
		Assert.True(Switch(backStack, Entry(typeof(SeasonPage), Show)));
		backStack.Add(Entry(typeof(SeasonPage), Show));   // left Season 2 for Season 3
		Assert.True(Switch(backStack, Entry(typeof(SeasonPage), Show)));

		Assert.Equal([typeof(HomePage), typeof(SeriesPage)], backStack.Select(x => x.Page));
	}

	[Fact]
	public void ASeasonOfAnotherShow_OpenedFromASeasonPage_IsANewEntry()
	{
		// items review, item 7: a search from a season page opens a season of another series: Back must come back here
		var backStack = new List<(Type Page, object? Parameter)> { Entry(typeof(SeriesPage)), Entry(typeof(SeasonPage), Show) };

		Assert.False(Switch(backStack, Entry(typeof(SeasonPage), Guid.NewGuid())));
		Assert.False(Switch(backStack, Entry(typeof(SeasonPage))));          // a season the server gave no series
		Assert.Equal(2, backStack.Count);
	}

	[Theory]
	[InlineData(NavigationMode.Back)]
	[InlineData(NavigationMode.Forward)]
	public void BackAndForward_LeaveTheHistoryAlone(NavigationMode mode)
	{
		var backStack = new List<(Type Page, object? Parameter)> { Entry(typeof(SeriesPage)), Entry(typeof(SeasonPage), Show) };

		Assert.False(Switch(backStack, Entry(typeof(SeasonPage), Show), mode));
		Assert.Equal(2, backStack.Count);
	}

	[Fact]
	public void ASeasonOpenedFromAnythingElse_IsANewEntry()
	{
		Assert.False(Switch([Entry(typeof(SeriesPage))], Entry(typeof(SeasonPage), Show)));
		Assert.False(Switch([Entry(typeof(SeasonPage), Show), Entry(typeof(EpisodePage))], Entry(typeof(SeasonPage), Show)));
		Assert.False(Switch([Entry(typeof(SeasonPage), Show)], Entry(typeof(EpisodePage))));
		Assert.False(Switch([], Entry(typeof(SeasonPage), Show)));
	}

	/// <summary>Items review, item 5: Back gives the focus back as it was given: a click's without the hover look.</summary>
	[Theory]
	[InlineData(true, true)]
	[InlineData(false, false)]
	public void TheFocusBack_ShowsTheCardsRingAndButtons_OnlyWhenTheKeyboardHadPutItThere(bool fromKeyboard, bool look)
	{
		var state = ItemPagePlace.RestoreAs(fromKeyboard);
		var actions = new CardActions();
		actions.GotFocus(byPointer: state == FocusState.Pointer);

		Assert.Equal(look, BaseItemDtoCard.ShowsFocusRing(state));
		Assert.Equal(look, actions.Shown);
	}

	/// <summary>Back gives the focus back only while nothing else has it: not over a dialog or a menu opened meanwhile.</summary>
	[Theory]
	[InlineData(false, 0, true)]    // the focus is on the title bar (where WinUI leaves it)
	[InlineData(true, 0, false)]    // the user has put it in the page
	[InlineData(false, 1, false)]   // F1's sheet, a menu, a flyout is open and has it
	public void TheFocusBack_NeverTakesItFromADialogOrAMenu(bool focusInPage, int openPopups, bool restores)
	{
		Assert.Equal(restores ? ItemPagePlace.FocusStep.Restore : ItemPagePlace.FocusStep.Leave, ItemPagePlace.FocusRestore(focusInPage, openPopups));
	}

	/// <summary>Items review, item 6: a row asks for its thumbnail within a screen of the view, not before it is laid out.</summary>
	[Theory]
	[InlineData(null, false)]                                  // not laid out yet
	[InlineData("0,-300,1600,1000", true)]                     // on screen
	[InlineData("0,-1900,1600,1000", true)]                    // a screen below the view's bottom: on its way
	[InlineData("0,-5000,1600,1000", false)]                   // far below
	[InlineData("0,2500,1600,1000", false)]                    // far above
	[InlineData("0,900,1600,1000", true)]                      // just above
	[InlineData("empty", true)]                                // no scroller: shown
	public void ARowsThumbnail_ComesWhenTheRowNearsTheView(string? viewport, bool loads)
	{
		Rect? view = viewport switch
		{
			null => null,
			"empty" => Rect.Empty,
			_ => Parse(viewport),
		};

		Assert.Equal(loads, EpisodeThumbs.IsNear(view, 120));
	}

	private static Rect Parse(string text)
	{
		var v = text.Split(',').Select(double.Parse).ToArray();
		return new Rect(v[0], v[1], v[2], v[3]);
	}

	[Theory]
	[InlineData(0, 5, 1, 1)]
	[InlineData(4, 5, 1, 0)]    // next on the last: the first
	[InlineData(0, 5, -1, 4)]   // previous on the first: the last
	[InlineData(2, 5, -1, 1)]
	public void HeroArrows_GoRound(int current, int count, int delta, int expected)
	{
		Assert.Equal(expected, HeroDots.Step(current, count, delta));
	}
}
