using FluentFin.Controls;
using FluentFin.Services;
using FluentFin.Views;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.4 (1.1.3 UX audit, interaction items 10 and 13): switching seasons with the pills replaces the season in the
/// Back history; the Home hero's previous and next buttons go round.
/// </summary>
public sealed class ItemNavigationTests
{
	private static bool Switch(List<Type> backStack, Type incoming, NavigationMode mode = NavigationMode.New) =>
		NavigationService.DropSwitchedSeasonPage(backStack, x => x, incoming, mode);

	[Fact]
	public void ASeasonPill_ReplacesTheSeasonInTheHistory_BackGoesToTheSeries()
	{
		// Series -> Season 1, then the pills: Season 2, Season 3
		var backStack = new List<Type> { typeof(HomePage), typeof(SeriesPage) };
		backStack.Add(typeof(SeasonPage));   // left Season 1 for Season 2
		Assert.True(Switch(backStack, typeof(SeasonPage)));
		backStack.Add(typeof(SeasonPage));   // left Season 2 for Season 3
		Assert.True(Switch(backStack, typeof(SeasonPage)));

		Assert.Equal([typeof(HomePage), typeof(SeriesPage)], backStack);
	}

	[Theory]
	[InlineData(NavigationMode.Back)]
	[InlineData(NavigationMode.Forward)]
	public void BackAndForward_LeaveTheHistoryAlone(NavigationMode mode)
	{
		var backStack = new List<Type> { typeof(SeriesPage), typeof(SeasonPage) };

		Assert.False(Switch(backStack, typeof(SeasonPage), mode));
		Assert.Equal(2, backStack.Count);
	}

	[Fact]
	public void ASeasonOpenedFromAnythingElse_IsANewEntry()
	{
		var backStack = new List<Type> { typeof(SeriesPage) };
		Assert.False(Switch(backStack, typeof(SeasonPage)));

		var fromEpisode = new List<Type> { typeof(SeasonPage), typeof(EpisodePage) };
		Assert.False(Switch(fromEpisode, typeof(SeasonPage)));
		Assert.False(Switch([typeof(SeasonPage)], typeof(EpisodePage)));
		Assert.False(Switch([], typeof(SeasonPage)));
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
