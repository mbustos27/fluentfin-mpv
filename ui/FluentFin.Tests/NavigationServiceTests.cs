using FluentFin.Services;
using FluentFin.Views;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Tests;

/// <summary>NavigationService.OnNavigated applies this to Frame.BackStack after every navigation.</summary>
public sealed class NavigationServiceTests
{
	private static bool Drop(List<Type> backStack, Type incoming, NavigationMode mode = NavigationMode.New) =>
		NavigationService.DropReplacedPlayerPage(backStack, x => x, incoming, mode);

	[Fact]
	public void APlayerPageReplacingAPlayerPage_TakesOverItsBackEntry()
	{
		// Home -> Movie -> player (first item); a remote Play opens a second player page
		var backStack = new List<Type> { typeof(HomePage), typeof(MoviePage), typeof(VideoPlayerPage) };

		Assert.True(Drop(backStack, typeof(VideoPlayerPage)));

		// Back from the second player page goes to the movie page, not to the first player page (which would replay)
		Assert.Equal([typeof(HomePage), typeof(MoviePage)], backStack);
	}

	[Fact]
	public void OtherNavigations_KeepTheBackStack()
	{
		var fromMovie = new List<Type> { typeof(HomePage), typeof(MoviePage) };
		var movieToMovie = new List<Type> { typeof(HomePage), typeof(MoviePage) };
		var goingBack = new List<Type> { typeof(HomePage), typeof(VideoPlayerPage) };
		var empty = new List<Type>();

		Assert.False(Drop(fromMovie, typeof(VideoPlayerPage)));
		Assert.False(Drop(movieToMovie, typeof(MoviePage)));
		Assert.False(Drop(goingBack, typeof(VideoPlayerPage), NavigationMode.Back));
		Assert.False(Drop(empty, typeof(VideoPlayerPage)));

		Assert.Equal([typeof(HomePage), typeof(MoviePage)], fromMovie);
		Assert.Equal([typeof(HomePage), typeof(MoviePage)], movieToMovie);
		Assert.Equal([typeof(HomePage), typeof(VideoPlayerPage)], goingBack);
	}

	private static bool Prune(List<Type> forwardStack) => NavigationService.PruneForwardStack(forwardStack, x => x);

	[Fact]
	public void Forward_NeverLeadsIntoThePlayer()
	{
		// Movie -> player, Back out of the player: the player is on the forward stack; Forward would play the item again
		var forward = new List<Type> { typeof(VideoPlayerPage) };

		Assert.True(Prune(forward));

		Assert.Empty(forward);
	}

	[Fact]
	public void Forward_KeepsTheOtherPages_InOrder()
	{
		// Home <- Movie <- Settings <- ... going Back through pages with a player between them
		var forward = new List<Type> { typeof(VideoPlayerPage), typeof(SeasonPage), typeof(VideoPlayerPage), typeof(MoviePage) };

		Assert.True(Prune(forward));

		Assert.Equal([typeof(SeasonPage), typeof(MoviePage)], forward);
		Assert.False(Prune(forward));
		Assert.False(Prune([]));
	}

	[Fact]
	public void TheNewPageAfterBack_LeavesNoForward_AndAPlayerReplacedTakesOverItsEntry()
	{
		// the Frame itself empties the forward stack on a new navigation; what is left for Forward is only pruned pages, and the
		// back stack keeps its one-player-entry rule
		var back = new List<Type> { typeof(HomePage), typeof(VideoPlayerPage) };
		Assert.True(Drop(back, typeof(VideoPlayerPage)));
		Assert.Equal([typeof(HomePage)], back);
	}
}
