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

	// ---- review fixes (home-fixes): the session changing under a load, a saved page's empty sections, new artwork, and
	// answers older than a watch-state change

	private static readonly Guid Alice = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
	private static readonly Guid Bob = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

	[Fact]
	public async Task ALoadThatEndsAfterAnotherUserSignedIn_IsSavedForNobody()
	{
		// Alice's Continue Watching is slow; she signs out and Bob signs in before it answers
		var user = Alice;
		_client.SetupGet(x => x.UserId).Returns(() => user);
		var home = NewHome();
		var navigation = home.OnNavigatedTo(null!);

		await home.OnNavigatedFrom();
		user = Bob;
		AnswerEverything();
		await navigation;

		// not under Bob's key (his next Back to Home would show Alice's page, and her history would land in his file), and
		// not under Alice's either: what is shown was read for a session that is over
		Assert.Null((await _cache.Get(_client.Object.BaseUrl, Bob)).Snapshot);
		Assert.Null((await _cache.Get(_client.Object.BaseUrl, Alice)).Snapshot);
		Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
	}

	[Fact]
	public async Task ASavedPagesEmptySections_ShowNoPlaceholders_WhileItRefreshes()
	{
		// the last run's page had nothing in Continue Watching or Next Up: on the next start those sections stay hidden while
		// the page is asked for again (6 placeholder cards that then collapse would move every row below them)
		using (var lastRun = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), _clock))
		{
			await lastRun.Save(_client.Object.BaseUrl, _client.Object.UserId, new HomeSnapshot([], [], [new HomeSnapshotRow(Movies, [Item("Movie 1")])], OtherLibraries), 0);
		}

		using var cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		var home = NewHome(cache);
		var navigation = home.OnNavigatedTo(null!);
		Assert.True(SpinWait.SpinUntil(() => { lock (_latestAsked) { return _latestAsked.Count == 4; } }, TimeSpan.FromSeconds(5)), "the refresh did not start");

		Assert.Equal(["Movies"], home.RecentItems.Select(x => x.Library.Name));
		Assert.False(home.IsContinueLoading, "the empty Continue Watching showed placeholders while the saved page refreshed");
		Assert.False(home.IsNextUpLoading, "the empty Next Up showed placeholders while the saved page refreshed");

		AnswerEverything();
		await navigation;
		Assert.Equal(["Half watched"], home.ContinueItems.Select(x => x.Dto.Name));
	}

	[Fact]
	public async Task NewArtwork_KeepsTheCards_ButReachesThemAndTheSavedPage()
	{
		var episode = Guid.NewGuid();
		var movie = Guid.NewGuid();
		using (var lastRun = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), _clock))
		{
			await lastRun.Save(_client.Object.BaseUrl, _client.Object.UserId, new HomeSnapshot(
				[Tagged(Item("Half watched", episode), "old")], [], [new HomeSnapshotRow(Movies, [Tagged(Item("Movie 1", movie), "old")])], OtherLibraries), 0);
		}

		using var cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		var home = NewHome(cache);
		var navigation = home.OnNavigatedTo(null!);
		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Count == 1 && home.RecentItems.Count == 1, TimeSpan.FromSeconds(5)));
		var continueShown = home.ContinueItems;
		var moviesShown = home.RecentItems[0].Items;

		// the same items, watch state and all: only the artwork changed
		_continue.SetResult(new BaseItemDtoQueryResult { Items = [Tagged(Item("Half watched", episode), "new")] });
		_nextUp.SetResult(new BaseItemDtoQueryResult { Items = [] });
		_latest[Movies.Id!.Value].SetResult([Tagged(Item("Movie 1", movie), "new")]);
		foreach (var latest in _latest.Values)
		{
			latest.TrySetResult([]);
		}
		await navigation;

		Assert.Same(continueShown, home.ContinueItems);
		Assert.Same(moviesShown, home.RecentItems[0].Items);
		Assert.Equal("new", Tag(home.ContinueItems[0].Dto));
		Assert.Equal("new", Tag(home.RecentItems[0].Items[0].Dto));
		var saved = (await cache.Get(_client.Object.BaseUrl, _client.Object.UserId)).Snapshot!;
		Assert.Equal("new", Tag(saved.ContinueItems[0]));
		Assert.Equal("new", Tag(saved.Rows[0].Items[0]));
	}

	[Fact]
	public async Task AnAnswerAskedForBeforeAWatchStateChange_DoesNotUndoIt()
	{
		var watched = Guid.NewGuid();
		var nextEpisode = Guid.NewGuid();
		var movie = Guid.NewGuid();
		using (var lastRun = new HomeCache(_folder, new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), _clock))
		{
			await lastRun.Save(_client.Object.BaseUrl, _client.Object.UserId, new HomeSnapshot(
				[Item("Half watched", watched), Item("Other", A)], [Item("Next episode", nextEpisode)], [new HomeSnapshotRow(Movies, [Item("Movie 1", movie)])], OtherLibraries), 0);
		}

		// Next Up is asked for twice: by the load, and again by the watch-state change (its episode was watched)
		var nextUpAgain = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var nextUps = new Queue<Task<BaseItemDtoQueryResult?>>([_nextUp.Task, nextUpAgain.Task]);
		_client.Setup(x => x.GetNextUp()).Returns(() => nextUps.Dequeue());

		using var cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		var home = NewHome(cache);
		var navigation = home.OnNavigatedTo(null!);
		Assert.True(SpinWait.SpinUntil(() => { lock (_latestAsked) { return _latestAsked.Count == 4; } }, TimeSpan.FromSeconds(5)), "the refresh did not start");

		// while the load waits: "Movie 1", "Half watched" and "Next episode" are watched to the end (Next Up last: the handler
		// waits there for Next Up to be asked for again)
		_socket.OnNext(new UserDataChangedMessage
		{
			Data = new UserDataChangeInfo
			{
				UserDataList =
				[
					new UserItemDataDto { ItemId = movie, Played = true, PlaybackPositionTicks = 0 },
					new UserItemDataDto { ItemId = watched, Played = true, PlaybackPositionTicks = 0 },
					new UserItemDataDto { ItemId = nextEpisode, Played = true, PlaybackPositionTicks = 0 },
				],
			},
		});
		Assert.True(SpinWait.SpinUntil(() => home.ContinueItems.Count == 1 && nextUps.Count == 0, TimeSpan.FromSeconds(5)), "the watch-state change was not handled");
		Assert.True(home.RecentItems[0].Items[0].UserData!.Played);

		// the newer Next Up answers first, then every answer the load asked for before the change, as it was then
		nextUpAgain.SetResult(new BaseItemDtoQueryResult { Items = [Item("Episode after")] });
		Assert.True(SpinWait.SpinUntil(() => home.NextUpItems.Select(x => x.Dto.Name).SequenceEqual(["Episode after"]), TimeSpan.FromSeconds(5)));
		// (a row whose answer matches keeps its cards anyway; one with something new is replaced, the movie unwatched with it)
		_continue.SetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched", watched), Item("Other", A)] });
		_nextUp.SetResult(new BaseItemDtoQueryResult { Items = [Item("Next episode", nextEpisode)] });
		_latest[Movies.Id!.Value].SetResult([Item("Movie 2"), Item("Movie 1", movie)]);
		foreach (var latest in _latest.Values)
		{
			latest.TrySetResult([]);
		}
		await navigation;

		// each section as the change left it: Continue Watching without the watched item, the newer Next Up, the movie watched
		Assert.Equal(("Other", "Episode after", true), (
			string.Join(", ", home.ContinueItems.Select(x => x.Dto.Name)),
			string.Join(", ", home.NextUpItems.Select(x => x.Dto.Name)),
			home.RecentItems[0].Items.First(x => x.Id == movie).UserData?.Played == true));
	}

	// the saved pages here have a Movies row only; the other libraries had nothing recent (no placeholder rows for them)
	private static readonly Guid[] OtherLibraries = [Shows.Id!.Value, Kids.Id!.Value, Collections.Id!.Value];

	private static BaseItemDto Tagged(BaseItemDto item, string tag)
	{
		item.ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = tag } };
		return item;
	}

	private static string Tag(BaseItemDto item) => $"{item.ImageTags!.AdditionalData["Primary"]}";

	private static BaseItemDto View(string name, BaseItemDto_CollectionType type) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };

	private static BaseItemDto Item(string name, Guid? id = null) =>
		new() { Id = id ?? Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 100 } };
}
