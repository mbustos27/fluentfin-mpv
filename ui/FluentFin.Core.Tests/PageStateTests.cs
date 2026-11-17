using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: pages say when the server could not give them their data, or has none, with a Retry, instead of
/// staying blank (docs/research/ux-review.md H6, §3.11). The client answers a failed request with null.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class PageStateTests : IDisposable
{
	private readonly Mock<IJellyfinClient> _client = new();

	// fluentfin-mpv (merge of app-states and home-perf): Home keeps its last page in a HomeCache; each test has its own folder
	private readonly string _homeCacheFolder = Path.Combine(Path.GetTempPath(), "fluentfin-pagestate-test-" + Guid.NewGuid().ToString("N"));
	private readonly List<HomeCache> _homeCaches = [];

	static PageStateTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public PageStateTests()
	{
		SessionInfo.ServerName = "Home server";
		SessionInfo.BaseUrl = "https://jf.example:8920";
		_client.SetupGet(x => x.BaseUrl).Returns("https://jf.example:8920");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("dddddddd-0000-0000-0000-000000000001"));
	}

	public void Dispose()
	{
		SessionInfo.ServerName = null!;
		SessionInfo.BaseUrl = null!;
		foreach (var cache in _homeCaches)
		{
			cache.Dispose();
		}
		if (Directory.Exists(_homeCacheFolder))
		{
			Directory.Delete(_homeCacheFolder, recursive: true);
		}
	}

	// ---- Home

	[Fact]
	public async Task Home_WithNothingFromTheServer_Fails_AndSaysWhichServer()
	{
		var vm = Home(continueWatching: null, nextUp: null, recent: []);

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.State.IsFailed);
		Assert.Equal("Couldn't load Home", vm.State.Title);
		Assert.Contains("Home server", vm.State.Message, StringComparison.Ordinal);
		Assert.False(vm.IsLoading);
	}

	[Fact]
	public async Task Home_WithOneSectionFailed_ShowsTheRest_WithAWarning()
	{
		var vm = Home(continueWatching: null, nextUp: Items(2), recent: []);

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.State.IsLoaded);
		Assert.True(vm.State.HasProblem);
		Assert.Equal("Some of Home couldn't be loaded", vm.State.Title);
		Assert.True(vm.HasNextUpItems);
		Assert.False(vm.HasContinueItems);
	}

	[Fact]
	public async Task Home_WithNothingOnTheServer_IsEmpty_NotFailed()
	{
		var vm = Home(continueWatching: Items(0), nextUp: Items(0), recent: []);

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.State.IsEmpty);
		Assert.Equal("Nothing to watch yet", vm.State.Title);
		Assert.Equal("Refresh", vm.State.RetryText);
	}

	[Fact]
	public async Task Home_WithEverything_HasNothingToSay()
	{
		var vm = Home(continueWatching: Items(1), nextUp: Items(1), recent: [3]);

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.State.IsLoaded);
		Assert.False(vm.State.HasProblem);
		Assert.False(vm.State.HasMessage);
	}

	[Fact]
	public async Task Home_Retry_LoadsEverySectionAgain_WithoutDoublingRecentlyAdded()
	{
		var continueWatching = new Queue<BaseItemDtoQueryResult?>([null, Items(2)]);
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(() => continueWatching.Dequeue());
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(Items(1));
		RecentRows(2);
		var vm = CreateHome();
		await vm.OnNavigatedTo(null!);
		Assert.True(vm.State.HasProblem);

		// the page the first load showed is now in the HomeCache, fresh: a Retry asks the server all the same
		await vm.State.RetryCommand.ExecuteAsync(null);

		Assert.False(vm.State.HasProblem);
		Assert.Single(vm.RecentItems);
		Assert.Equal(2, vm.ContinueItems.Count);
		Assert.True(vm.HasContinueItems);
		_client.Verify(x => x.GetContinueWatching(), Times.Exactly(2));

		// the flag follows the list the Retry put in place
		vm.ContinueItems.Clear();
		Assert.False(vm.HasContinueItems);
	}

	[Fact]
	public async Task Home_Retry_ShowsAnEmptySectionsPlaceholdersAgain_UntilItsAnswer()
	{
		var second = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var continueWatching = new Queue<Task<BaseItemDtoQueryResult?>>([Task.FromResult<BaseItemDtoQueryResult?>(null), second.Task]);
		_client.Setup(x => x.GetContinueWatching()).Returns(() => continueWatching.Dequeue());
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(Items(1));
		RecentRows();
		var vm = CreateHome();
		await vm.OnNavigatedTo(null!);
		Assert.False(vm.IsContinueLoading);
		Assert.False(vm.HasContinueItems);

		var retry = vm.State.RetryCommand.ExecuteAsync(null);

		// Continue Watching failed and has nothing: its placeholders; Next Up has its cards and keeps them
		Assert.True(vm.IsContinueLoading);
		Assert.False(vm.IsNextUpLoading);
		second.SetResult(Items(1));
		await retry;
		Assert.False(vm.IsContinueLoading);
		Assert.True(vm.HasContinueItems);
	}

	[Fact]
	public async Task Home_FromTheCache_WhenTheRefreshFails_KeepsTheCachedPage_WithAWarning()
	{
		// the last run's page, on disk: always refreshed
		using (var lastRun = NewHomeCache())
		{
			await lastRun.Save(_client.Object.BaseUrl, _client.Object.UserId, new HomeSnapshot(
				[.. Items(2).Items!], [], [new HomeSnapshotRow(View("Movies"), [.. Items(3).Items!])], []), 0);
		}

		var vm = Home(continueWatching: null, nextUp: null, recent: []);

		await vm.OnNavigatedTo(null!);

		Assert.True(vm.State.IsLoaded);
		Assert.True(vm.State.HasProblem);
		Assert.Equal("Some of Home couldn't be loaded", vm.State.Title);
		Assert.Equal(2, vm.ContinueItems.Count);
		Assert.Equal(["Movies"], vm.RecentItems.Select(x => x.Library.Name));
	}

	[Fact]
	public async Task Home_FreshFromTheCache_HasNothingToSay_AndAsksNothing()
	{
		var cache = NewHomeCache();
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(Items(1));
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(Items(0));
		RecentRows(2);
		await CreateHome(cache).OnNavigatedTo(null!);
		_client.Invocations.Clear();

		var back = CreateHome(cache);
		await back.OnNavigatedTo(null!);

		Assert.True(back.State.IsLoaded);
		Assert.False(back.State.HasProblem);
		Assert.False(back.State.HasMessage);
		Assert.DoesNotContain(_client.Invocations, x => x.Method.Name.StartsWith("Get", StringComparison.Ordinal));
	}

	// ---- Library

	[Fact]
	public async Task Library_ThatCannotBeLoaded_Fails_ThenRetryLoadsIt()
	{
		var answers = new Queue<BaseItemDtoQueryResult?>([null, Items(3)]);
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(() => answers.Dequeue());
		var vm = new LibraryViewModel(_client.Object);

		await vm.OnNavigatedTo(new BaseItemDto { Id = Guid.NewGuid() });
		Assert.True(vm.State.IsFailed);
		Assert.Equal("Couldn't load this library", vm.State.Title);
		Assert.False(vm.IsLoading);

		await vm.State.RetryCommand.ExecuteAsync(null);
		Assert.True(vm.State.IsLoaded);
		Assert.Equal(3, vm.Items.Count);
	}

	[Fact]
	public async Task Library_ThatIsEmpty_SaysSo()
	{
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(Items(0));
		var vm = new LibraryViewModel(_client.Object);

		await vm.OnNavigatedTo(new BaseItemDto { Id = Guid.NewGuid() });

		Assert.True(vm.State.IsEmpty);
		Assert.Equal("This library is empty", vm.State.Title);
	}

	[Fact]
	public async Task Library_OpenedById_ThatCannotBeLookedUp_Fails()
	{
		_client.Setup(x => x.GetItem(It.IsAny<Guid>())).ReturnsAsync((BaseItemDto?)null);
		var vm = new LibraryViewModel(_client.Object);

		await vm.OnNavigatedTo(Guid.NewGuid());

		Assert.True(vm.State.IsFailed);
		Assert.False(vm.IsLoading);
		_client.Verify(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public async Task Library_ARetryAfterItLoaded_ShowsWhatTheServerHasNow()
	{
		var answers = new Queue<BaseItemDtoQueryResult?>([Items(3), Items(1)]);
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(() => answers.Dequeue());
		var vm = new LibraryViewModel(_client.Object);
		await vm.OnNavigatedTo(new BaseItemDto { Id = Guid.NewGuid() });

		await vm.State.RetryCommand.ExecuteAsync(null);

		Assert.Single(vm.Items);
	}

	// ---- item pages

	public static TheoryData<string> ItemPages => ["movie", "series", "season", "episode"];

	[Theory]
	[MemberData(nameof(ItemPages))]
	public async Task AnItemThatCannotBeLoaded_Fails_ThenRetryLoadsIt(string page)
	{
		var item = new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = "An item",
			Type = page switch
			{
				"movie" => BaseItemDto_Type.Movie,
				"series" => BaseItemDto_Type.Series,
				"season" => BaseItemDto_Type.Season,
				_ => BaseItemDto_Type.Episode,
			},
		};
		var answers = new Queue<BaseItemDto?>([null, item]);
		_client.Setup(x => x.GetItem(item.Id!.Value)).ReturnsAsync(() => answers.Dequeue());
		var (navigation, state) = ItemPage(page);

		await navigation.OnNavigatedTo(item);
		Assert.True(state.IsFailed);
		Assert.Equal($"Couldn't load this {page}", state.Title);

		await state.RetryCommand.ExecuteAsync(null);
		Assert.True(state.IsLoaded);
		Assert.False(state.HasMessage);
	}

	// ---- search

	[Fact]
	public void Search_ShowsResults_NoResults_OrAFailureToTryAgain()
	{
		var results = SearchSuggestions.For("matrix", Items(2));
		Assert.Equal(2, results.Count);
		// Seaglass 1.1.4: rows named for Narrator (SearchSuggestion), not the bare BaseItemDto
		Assert.All(results, x => Assert.IsType<SearchSuggestion>(x));

		var none = Assert.IsType<SearchMessage>(Assert.Single(SearchSuggestions.For("zzz", Items(0))));
		Assert.Equal("No results for “zzz”", none.Title);
		Assert.False(none.CanRetry);
		// the box keeps the query (TextMemberPath "Name"); Narrator reads the message (ToString)
		Assert.Equal("zzz", none.Name);
		Assert.Equal("No results for “zzz”. Check the spelling, or try fewer words.", none.ToString());

		var failed = Assert.IsType<SearchMessage>(Assert.Single(SearchSuggestions.For("matrix", null)));
		Assert.Equal("Couldn't search Home server", failed.Title);
		Assert.True(failed.CanRetry);
	}

	[Fact]
	public async Task Search_AnOlderAnswerThatComesBackLate_NeverReplacesTheNewerOne()
	{
		var typed = new Subject<string>();
		var now = new Subject<string>();
		var slow = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var updates = new List<SearchUpdate>();
		var started = new System.Collections.Concurrent.ConcurrentQueue<string>();
		using var _ = SearchSuggestions.Pipeline(typed, now, query =>
			{
				started.Enqueue(query);
				return query == "old" ? slow.Task : Task.FromResult<BaseItemDtoQueryResult?>(Items(1));
			}, TimeSpan.FromMilliseconds(10))
			.Synchronize()
			.Subscribe(updates.Add);

		now.OnNext("old");
		await WaitUntil(() => started.Contains("old"));
		now.OnNext("new");
		await WaitUntil(() => updates.Count == 1);
		slow.SetResult(Items(5));
		await Task.Delay(100);

		var update = Assert.Single(updates);
		Assert.Equal("new", update.Query);
	}

	[Fact]
	public async Task Search_TypingWaitsForAPause_EnterAndRetrySearchAtOnce_AFailureCanBeRetried()
	{
		var typed = new Subject<string>();
		var now = new Subject<string>();
		var answers = new Queue<Func<Task<BaseItemDtoQueryResult?>>>([
			() => throw new HttpRequestException("down"),
			() => Task.FromResult<BaseItemDtoQueryResult?>(Items(2)),
		]);
		var searched = new List<string>();
		var updates = new List<SearchUpdate>();
		using var _ = SearchSuggestions.Pipeline(typed, now, query =>
			{
				searched.Add(query);
				return answers.Dequeue()();
			}, TimeSpan.FromMilliseconds(200))
			.Synchronize()
			.Subscribe(updates.Add);

		typed.OnNext("mat");
		typed.OnNext("matr");
		await WaitUntil(() => updates.Count == 1);
		Assert.Equal(["matr"], searched);
		var failed = Assert.IsType<SearchMessage>(Assert.Single(updates[0].Suggestions));
		Assert.True(failed.CanRetry);

		// the same text again: typing would not search it (distinct), the retry row does
		now.OnNext(failed.Query);
		await WaitUntil(() => updates.Count == 2);
		Assert.Equal(["matr", "matr"], searched);
		Assert.Equal(2, updates[1].Suggestions.Count);
	}

	[Fact]
	public async Task Search_EnterAfterABackspace_SearchesWhatIsInTheBox_NotTheTypedQueryStillWaiting()
	{
		// the reviewer's R1: "abc" typed (held by the pause), backspaced to "ab" (under 3 letters, never typed through), Enter
		var typed = new Subject<string>();
		var now = new Subject<string>();
		var updates = new List<SearchUpdate>();
		using var _ = SearchSuggestions.Pipeline(typed, now,
			query => Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = query }] }),
			TimeSpan.FromMilliseconds(200)).Synchronize().Subscribe(updates.Add);

		typed.OnNext("abc");
		now.OnNext("ab");
		await Task.Delay(700);

		Assert.Equal("ab", updates[^1].Query);
		Assert.DoesNotContain(updates, x => x.Query == "abc");
	}

	// ---- helpers

	/// <param name="recent">The items of each library's "Recently added" row, one library per entry (none: no libraries).</param>
	private HomeViewModel Home(BaseItemDtoQueryResult? continueWatching, BaseItemDtoQueryResult? nextUp, int[] recent)
	{
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(continueWatching);
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(nextUp);
		RecentRows(recent);
		return CreateHome();
	}

	private HomeViewModel CreateHome(HomeCache? cache = null) =>
		new(_client.Object, Observable.Never<IOutboundWebSocketMessage>(), Commands(), cache ?? NewHomeCache());

	private HomeCache NewHomeCache()
	{
		var cache = new HomeCache(_homeCacheFolder, Observable.Never<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		_homeCaches.Add(cache);
		return cache;
	}

	/// <summary>One library per count, its "Recently added" row that many items.</summary>
	private void RecentRows(params int[] counts)
	{
		var views = counts.Select((_, i) => View(i == 0 ? "Movies" : $"Library {i}")).ToList();
		_client.Setup(x => x.GetUserViews()).ReturnsAsync(views);
		foreach (var (view, count) in views.Zip(counts))
		{
			_client.Setup(x => x.GetLatestItems(It.Is<BaseItemDto>(v => v.Id == view.Id))).ReturnsAsync([.. Items(count).Items!]);
		}
	}

	private static BaseItemDto View(string name) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };

	private GlobalCommands Commands() => new(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	private (FluentFin.Contracts.ViewModels.INavigationAware Page, LoadState State) ItemPage(string page)
	{
		switch (page)
		{
			case "movie":
				var movie = new MovieViewModel(_client.Object, Commands());
				return (movie, movie.State);
			case "series":
				var series = new SeriesViewModel(_client.Object);
				return (series, series.State);
			case "season":
				var season = new SeasonViewModel(_client.Object);
				return (season, season.State);
			default:
				var episode = new EpisodeViewModel(_client.Object, Commands());
				return (episode, episode.State);
		}
	}

	private static BaseItemDtoQueryResult Items(int count) => new()
	{
		Items = [.. Enumerable.Range(0, count).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"Item {i}", Type = BaseItemDto_Type.Movie })],
	};

	private static async Task WaitUntil(Func<bool> condition)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "timed out");
			await Task.Delay(10);
		}
	}
}
