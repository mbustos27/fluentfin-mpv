using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Views;
using Microsoft.UI.Xaml.Navigation;
using Moq;

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

	// ---- Nixie 1.6.1 (the merge review's R1): the refusal asks about the page the frame shows, Back included

	/// <summary>
	/// Review of 1.6.1 (F1): the real <see cref="NavigationService"/> with a frame of the test's own, as WinUI's Frame behaves:
	/// Navigate raises Navigated (the service's Arrived) with the new entry's parameter before it returns, and GoBack raises
	/// it with the entry shown again. The service decides (<see cref="NavigationService.Open"/>: NavigateTo's decision and move)
	/// and keeps the parameter shown; this frame only moves when told and says what it shows.
	/// </summary>
	private sealed class History
	{
		private readonly NavigationService _navigation = new(Mock.Of<FluentFin.UI.Core.Contracts.Services.IPageService>());
		private readonly List<(Type Page, object? Parameter)> _entries = [];

		public (Type Page, object? Parameter) Shown => _entries[^1];

		/// <summary>NavigateTo on this frame: true when the frame moved.</summary>
		public bool Open(Type page, object? parameter = null) =>
			_navigation.Open(_entries.Count > 0 ? Shown.Page : null, page, parameter, () =>
			{
				_entries.Add((page, parameter));
				_navigation.Arrived(parameter); // Frame.Navigated, before Navigate returns
				return true;
			});

		/// <summary>Frame.GoBack: the entry before shows again, and Navigated says its parameter.</summary>
		public void Back()
		{
			_entries.RemoveAt(_entries.Count - 1);
			_navigation.Arrived(Shown.Parameter);
		}
	}

	private static Jellyfin.Sdk.Generated.Models.BaseItemDto Library(string name) => new() { Id = Guid.NewGuid(), Name = name };

	[Fact]
	public void TheLibraryTabs_MoviesShowsBackShows_OpensShows()
	{
		var movies = Library("Movies");
		var shows = Library("Shows");
		var history = new History();
		Assert.True(history.Open(typeof(HomePage)));
		Assert.True(history.Open(typeof(LibraryPage), movies));
		Assert.True(history.Open(typeof(LibraryPage), shows));

		history.Back();

		Assert.Same(movies, history.Shown.Parameter);
		Assert.True(history.Open(typeof(LibraryPage), shows));
		Assert.Same(shows, history.Shown.Parameter);
	}

	[Fact]
	public void TheSameSeeAll_AfterBack_OpensItsGridAgain()
	{
		var history = new History();
		Assert.True(history.Open(typeof(CollectionsPage), new CollectionsPlace(CollectionsSection.All)));
		Assert.True(history.Open(typeof(CollectionsPage), new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true)));

		history.Back();

		// the row's See all makes a new place each time: equal by value
		Assert.True(history.Open(typeof(CollectionsPage), new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true)));
		Assert.Equal(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true), history.Shown.Parameter);
	}

	[Fact]
	public void TheCollectionsTab_FromASeeAll_BackThenAgain_OpensTheCollectionsPage()
	{
		var all = new CollectionsPlace(CollectionsSection.All);
		var seeAll = new CollectionsPlace(CollectionsSection.Server, SeeAll: true);
		var history = new History();
		Assert.True(history.Open(typeof(CollectionsPage), all));
		Assert.True(history.Open(typeof(CollectionsPage), seeAll));
		Assert.True(history.Open(typeof(CollectionsPage), all));

		history.Back();

		Assert.Equal(seeAll, history.Shown.Parameter);
		Assert.True(history.Open(typeof(CollectionsPage), all));
		Assert.Equal(all, history.Shown.Parameter);
	}

	[Fact]
	public void ThePlaceShown_IsStillRefused_AfterBackToo()
	{
		var movies = Library("Movies");
		var shows = Library("Shows");
		var history = new History();
		Assert.True(history.Open(typeof(HomePage)));
		Assert.False(history.Open(typeof(HomePage)));
		Assert.True(history.Open(typeof(LibraryPage), movies));
		Assert.False(history.Open(typeof(LibraryPage), movies));
		Assert.False(history.Open(typeof(LibraryPage)));
		Assert.True(history.Open(typeof(LibraryPage), shows));

		history.Back();

		// Movies is shown again: Movies is the page shown (the top bar takes it to its top instead), Shows is not
		Assert.False(history.Open(typeof(LibraryPage), movies));
		Assert.False(NavigationService.ShowsAlready(typeof(LibraryPage), movies, typeof(LibraryPage), shows));
		Assert.False(NavigationService.ShowsAlready(typeof(HomePage), null, typeof(LibraryPage), movies));
		Assert.False(NavigationService.ShowsAlready(null, null, typeof(HomePage), null));
	}

	/// <summary>
	/// The gate's parameter is the frame's: every arrival sets it before anything else (a theme switch returns early), and
	/// NavigateTo keeps no parameter of its own that Back would leave behind.
	/// </summary>
	[Fact]
	public void TheRefusal_AsksAboutTheParameterEveryArrivalSets()
	{
		var nav = File.ReadAllText(Path.Combine(FlyleafEngineTests.RepoRoot, "FluentFin", "Services", "NavigationService.cs")).Replace("\r\n", "\n");

		Assert.DoesNotContain("_lastParameterUsed", nav);
		// NavigateTo is Open around the frame's move (what the tests above drive), and every arrival goes through Arrived first
		Assert.Contains("return Open(frame.Content?.GetType(), pageType, parameter, () =>", nav);
		Assert.Contains("internal bool Open(Type? shownPage, Type pageType, object? parameter, Func<bool> navigate) =>\n\t\t!ShowsAlready(shownPage, _currentParameter, pageType, parameter) && navigate();", nav);
		var arrival = nav[nav.IndexOf("private async void OnNavigated(object sender, NavigationEventArgs e)", StringComparison.Ordinal)..];
		var set = arrival.IndexOf("Arrived(e.Parameter);", StringComparison.Ordinal);
		Assert.True(set > 0 && set < arrival.IndexOf("return;", StringComparison.Ordinal));
		Assert.Equal(1, nav.Split("_currentParameter = ").Length - 1);
	}
	/// <summary>
	/// 1.7.5 (the review's Major): Sign out and Switch user let the old shell's frame go (Release): it has no frame, nothing
	/// navigates (a cast or a remote command that still arrived played in the detached frame, with no page or controls), and
	/// its Frame doesn't fall back to the window's root frame (where the player would open over the sign-in page).
	/// </summary>
	[Fact]
	public void Released_HasNoFrame_AndNavigatesNowhere()
	{
		var nav = new NavigationService(Mock.Of<FluentFin.UI.Core.Contracts.Services.IPageService>());

		nav.Release();

		Assert.Null(nav.Frame); // the getter would read App.MainWindow (null here: it threw)
		Assert.False(nav.NavigateTo("FluentFin.ViewModels.VideoPlayerViewModel", "a cast"));
		Assert.False(nav.CanGoBack);
	}
}