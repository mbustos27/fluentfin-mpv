using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: the progressive home page. Every request goes out at once, each part of the page shows a placeholder
/// until its own answer is in, the "Recently added" rows keep the server's view order whatever order the answers come in,
/// and a row with nothing (or a failed request) goes away. The last page is shown at once from HomeCache and refreshed
/// unless it is fresh. The client is a mock whose answers the test releases one by one.
/// </summary>
public sealed class HomeViewModelTests : IDisposable
{
	static HomeViewModelTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static readonly BaseItemDto Movies = View("Movies", BaseItemDto_CollectionType.Movies);
	private static readonly BaseItemDto Music = View("Music", BaseItemDto_CollectionType.Music);
	private static readonly BaseItemDto Shows = View("Shows", BaseItemDto_CollectionType.Tvshows);
	private static readonly BaseItemDto Kids = View("Kids", BaseItemDto_CollectionType.Movies);
	private static readonly BaseItemDto Collections = View("Collections", BaseItemDto_CollectionType.Boxsets);

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly TaskCompletionSource<BaseItemDtoQueryResult?> _continue = new();
	private readonly TaskCompletionSource<BaseItemDtoQueryResult?> _nextUp = new();
	private readonly Dictionary<Guid, TaskCompletionSource<List<BaseItemDto>?>> _latest = [];
	private readonly List<string> _latestAsked = [];
	private readonly Subject<IOutboundWebSocketMessage> _socket = new();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-home-test-" + Guid.NewGuid().ToString("N"));
	private readonly ManualClock _clock = new();
	private readonly HomeCache _cache;

	public HomeViewModelTests()
	{
		_cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		_client.SetupGet(x => x.BaseUrl).Returns("https://jellyfin.example");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("cccccccc-0000-0000-0000-000000000001"));
		_client.Setup(x => x.GetContinueWatching()).Returns(_continue.Task);
		_client.Setup(x => x.GetNextUp()).Returns(_nextUp.Task);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([Movies, Music, Shows, Kids, Collections]);
		foreach (var view in new[] { Movies, Music, Shows, Kids, Collections })
		{
			_latest[view.Id!.Value] = new();
		}
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>()))
			.Returns((BaseItemDto library) => { lock (_latestAsked) { _latestAsked.Add(library.Name!); } return _latest[library.Id!.Value].Task; });
	}

	public void Dispose()
	{
		_cache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private HomeViewModel NewHome(HomeCache? cache = null) => new(_client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<Settings.ISettings>()), cache ?? _cache);

	private void AnswerEverything()
	{
		_continue.TrySetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched")] });
		_nextUp.TrySetResult(new BaseItemDtoQueryResult { Items = [Item("Next episode")] });
		_latest[Movies.Id!.Value].TrySetResult([Item("Movie 1")]);
		_latest[Shows.Id!.Value].TrySetResult(null);
		_latest[Kids.Id!.Value].TrySetResult([Item("Kid 1")]);
		_latest[Collections.Id!.Value].TrySetResult([]);
	}

	[Fact]
	public async Task BackToAFreshHome_ShowsItAtOnce_AndAsksTheServerNothing_UntilItChangesOrAges()
	{
		AnswerEverything();
		await NewHome().OnNavigatedTo(null!);
		_client.Invocations.Clear();

		// Back to Home: the page is there at once, nothing is asked
		var back = NewHome();
		var navigation = back.OnNavigatedTo(null!);
		Assert.True(navigation.IsCompleted);
		Assert.False(back.IsContinueLoading || back.IsNextUpLoading || back.IsLoading);
		Assert.Equal(["Half watched"], back.ContinueItems.Select(x => x.Dto.Name));
		Assert.Equal(["Next episode"], back.NextUpItems.Select(x => x.Dto.Name));
		Assert.Equal(["Movies", "Kids"], back.RecentItems.Select(x => x.Library.Name));
		Assert.All(back.RecentItems, x => Assert.False(x.IsLoading));
		Assert.DoesNotContain(_client.Invocations, x => x.Method.Name.StartsWith("Get", StringComparison.Ordinal));

		// something was watched (the server says so): the next visit shows the page and asks again
		_socket.OnNext(new UserDataChangedMessage { Data = new UserDataChangeInfo { UserDataList = [] } });
		await NewHome().OnNavigatedTo(null!);
		_client.Verify(x => x.GetContinueWatching(), Times.Once);
		_client.Verify(x => x.GetNextUp(), Times.Once);
		_client.Verify(x => x.GetLatestItems(It.IsAny<BaseItemDto>()), Times.Exactly(4));

		await NewHome().OnNavigatedTo(null!);
		_client.Verify(x => x.GetContinueWatching(), Times.Once);

		// or it got old
		_clock.Advance(HomeCache.FreshFor + TimeSpan.FromSeconds(1));
		await NewHome().OnNavigatedTo(null!);
		_client.Verify(x => x.GetContinueWatching(), Times.Exactly(2));
	}

	[Fact]
	public async Task AfterARestart_TheLastPageShowsAtOnce_AndOnlyWhatChangedIsReplaced()
	{
		using (var lastRun = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), _clock))
		{
			await lastRun.Save(_client.Object.BaseUrl, _client.Object.UserId, new HomeSnapshot(
				[Item("Half watched", A), Item("Other", B)],
				[Item("Next episode", C)],
				[new HomeSnapshotRow(Movies, [Item("Movie 1", D)]), new HomeSnapshotRow(Shows, [Item("Show 1", E)]), new HomeSnapshotRow(Kids, [Item("Kid 1", F)])],
				[Collections.Id!.Value]), 0);
		}

		using var ui = new UiThread();
		using var cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		var home = await ui.Invoke(() => NewHome(cache));
		var navigation = ui.Run(() => home.OnNavigatedTo(null!));
		Assert.True(SpinWait.SpinUntil(() => { lock (_latestAsked) { return _latestAsked.Count == 4; } }, TimeSpan.FromSeconds(5)));

		// before any answer: the saved page, every row asked for again, and no placeholder for Collections, which had
		// nothing recent last time (a row that came and went would move the rows below it)
		var (continueShown, moviesShown) = await ui.Invoke(() =>
		{
			Assert.False(home.IsContinueLoading || home.IsNextUpLoading);
			Assert.True(home.HasContinueItems && home.HasNextUpItems);
			Assert.Equal(["Half watched", "Other"], home.ContinueItems.Select(x => x.Dto.Name));
			Assert.Equal(["Movies", "Shows", "Kids"], home.RecentItems.Select(x => x.Library.Name));
			Assert.All(home.RecentItems, x => Assert.False(x.IsLoading));
			return (home.ContinueItems, home.RecentItems[0].Items);
		});

		await ui.Invoke(() =>
		{
			_continue.SetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched", A), Item("Other", B)] }); // as saved
			_nextUp.SetResult(new BaseItemDtoQueryResult { Items = [Item("Episode after", G)] });                       // changed
			_latest[Movies.Id!.Value].SetResult([Item("Movie 1", D)]);                                               // as saved
			_latest[Shows.Id!.Value].SetResult(null);                                                                // failed
			_latest[Kids.Id!.Value].SetResult([]);                                                                    // nothing now
			_latest[Collections.Id!.Value].SetResult([Item("Boxset 1")]);                                           // something now
			return 0;
		});
		await navigation;

		await ui.Invoke(() =>
		{
			Assert.Same(continueShown, home.ContinueItems);
			Assert.Equal(["Episode after"], home.NextUpItems.Select(x => x.Dto.Name));
			Assert.Equal(["Movies", "Shows", "Collections"], home.RecentItems.Select(x => x.Library.Name));
			Assert.Equal(["Boxset 1"], home.RecentItems[2].Items.Select(x => x.Dto.Name));
			Assert.Same(moviesShown, home.RecentItems[0].Items);
			Assert.Equal(["Show 1"], home.RecentItems[1].Items.Select(x => x.Dto.Name));
			Assert.False(home.IsLoading);
			return 0;
		});

		// what it now shows is what the next visit shows, without asking
		var next = await cache.Get(_client.Object.BaseUrl, _client.Object.UserId);
		Assert.True(next.IsFresh);
		Assert.Equal(["Episode after"], next.Snapshot!.NextUpItems.Select(x => x.Name));
		Assert.Equal(["Movies", "Shows", "Collections"], next.Snapshot.Rows.Select(x => x.Library.Name));
		Assert.Equal([Kids.Id!.Value], next.Snapshot.EmptyLibraries);
	}

	private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid(), D = Guid.NewGuid(), E = Guid.NewGuid(), F = Guid.NewGuid(), G = Guid.NewGuid();

	[Fact]
	public async Task EveryRowIsAskedForAtOnce_AndAppearsWhenItsOwnAnswerIsIn_InViewOrder()
	{
		var home = NewHome();
		var navigation = home.OnNavigatedTo(null!);

		// nothing has answered: every part of the page is a placeholder, every row has been asked for, Music has no row
		Assert.True(home.IsLoading);
		Assert.True(home.IsContinueLoading);
		Assert.True(home.IsNextUpLoading);
		Assert.Equal(["Movies", "Shows", "Kids", "Collections"], home.RecentItems.Select(x => x.Library.Name));
		Assert.All(home.RecentItems, x => Assert.True(x.IsLoading));
		Assert.Equal(["Movies", "Shows", "Kids", "Collections"], _latestAsked);
		_client.Verify(x => x.GetContinueWatching(), Times.Once);
		_client.Verify(x => x.GetNextUp(), Times.Once);

		// the third row answers first: it shows, the rows above it stay placeholders in their places
		_latest[Kids.Id!.Value].SetResult([Item("Kid 1"), Item("Kid 2")]);
		Assert.Equal(["Movies", "Shows", "Kids", "Collections"], home.RecentItems.Select(x => x.Library.Name));
		var kids = home.RecentItems[2];
		Assert.False(kids.IsLoading);
		Assert.Equal(["Kid 1", "Kid 2"], kids.Items.Select(x => x.Dto.Name));
		Assert.True(home.RecentItems[0].IsLoading);
		Assert.True(home.RecentItems[1].IsLoading);

		// nothing recent, or a failed request: the row goes
		_latest[Collections.Id!.Value].SetResult([]);
		_latest[Shows.Id!.Value].SetResult(null);
		Assert.Equal(["Movies", "Kids"], home.RecentItems.Select(x => x.Library.Name));

		// Continue Watching shows on its own; an empty Next Up stops loading and has no items (its section goes)
		_continue.SetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched")] });
		Assert.False(home.IsContinueLoading);
		Assert.True(home.HasContinueItems);
		Assert.Equal(["Half watched"], home.ContinueItems.Select(x => x.Dto.Name));
		_nextUp.SetResult(new BaseItemDtoQueryResult { Items = [] });
		Assert.False(home.IsNextUpLoading);
		Assert.False(home.HasNextUpItems);

		// the page is loading until the last answer
		Assert.True(home.IsLoading);
		Assert.False(navigation.IsCompleted);
		_latest[Movies.Id!.Value].SetResult([Item("Movie 1")]);
		await navigation;
		Assert.False(home.IsLoading);
		Assert.Equal(["Movies", "Kids"], home.RecentItems.Select(x => x.Library.Name));
		Assert.All(home.RecentItems, x => Assert.False(x.IsLoading));
		_client.Verify(x => x.GetLatestItems(It.Is<BaseItemDto>(v => v.Id == Music.Id)), Times.Never);
	}

	[Fact]
	public async Task FailedContinueAndNextUp_StopLoading_WithNoSection()
	{
		var home = NewHome();
		var navigation = home.OnNavigatedTo(null!);

		_continue.SetResult(null);
		_nextUp.SetResult(null);
		foreach (var latest in _latest.Values)
		{
			latest.TrySetResult(null);
		}
		await navigation;

		Assert.False(home.IsContinueLoading);
		Assert.False(home.IsNextUpLoading);
		Assert.False(home.HasContinueItems);
		Assert.False(home.HasNextUpItems);
		Assert.Empty(home.RecentItems);
		Assert.False(home.IsLoading);
	}

	[Fact]
	public void AWatchedItem_LeavesContinueWatching_WhileOtherRowsAreStillLoading()
	{
		var watched = Item("Half watched");
		var home = NewHome();
		_ = home.OnNavigatedTo(null!);
		_continue.SetResult(new BaseItemDtoQueryResult { Items = [watched, Item("Other")] });
		Assert.True(home.IsLoading);

		_socket.OnNext(new UserDataChangedMessage
		{
			Data = new UserDataChangeInfo { UserDataList = [new UserItemDataDto { ItemId = watched.Id, Played = true, PlaybackPositionTicks = 0 }] },
		});

		// the handler runs on the main-thread scheduler, a moment later in a test
		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Count == 1, TimeSpan.FromSeconds(5)), "the watched item stayed in Continue Watching");
		Assert.Equal(["Other"], home.ContinueItems.Select(x => x.Dto.Name));
		Assert.True(home.HasContinueItems);
	}

	private static BaseItemDto View(string name, BaseItemDto_CollectionType type) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };

	private static BaseItemDto Item(string name, Guid? id = null) =>
		new() { Id = id ?? Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 100 } };
}
