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
}
