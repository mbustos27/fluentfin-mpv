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

	static PageStateTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public PageStateTests()
	{
		SessionInfo.ServerName = "Home server";
		SessionInfo.BaseUrl = "https://jf.example:8920";
	}

	public void Dispose()
	{
		SessionInfo.ServerName = null!;
		SessionInfo.BaseUrl = null!;
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
		var vm = Home(continueWatching: Items(1), nextUp: Items(1), recent: [Recent(3)]);

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
		_client.Setup(x => x.GetRecentItemsFromUserLibraries()).Returns(() => AsAsync(Recent(2)));
		var vm = CreateHome();
		await vm.OnNavigatedTo(null!);
		Assert.True(vm.State.HasProblem);

		await vm.State.RetryCommand.ExecuteAsync(null);

		Assert.False(vm.State.HasProblem);
		Assert.Single(vm.RecentItems);
		Assert.Equal(2, vm.ContinueItems.Count);
		Assert.True(vm.HasContinueItems);

		// the flag follows the list the Retry put in place
		vm.ContinueItems.Clear();
		Assert.False(vm.HasContinueItems);
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
		Assert.All(results, x => Assert.IsType<BaseItemDto>(x));

		var none = Assert.IsType<SearchMessage>(Assert.Single(SearchSuggestions.For("zzz", Items(0))));
		Assert.Equal("No results for “zzz”", none.Title);
		Assert.False(none.CanRetry);
		Assert.Equal("zzz", none.ToString());

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
		using var _ = SearchSuggestions.Pipeline(typed, now, query => query == "old" ? slow.Task : Task.FromResult<BaseItemDtoQueryResult?>(Items(1)), TimeSpan.FromMilliseconds(10))
			.Synchronize()
			.Subscribe(updates.Add);

		now.OnNext("old");
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

	// ---- helpers

	private HomeViewModel Home(BaseItemDtoQueryResult? continueWatching, BaseItemDtoQueryResult? nextUp, RecentItemDtoQueryResult[] recent)
	{
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(continueWatching);
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(nextUp);
		_client.Setup(x => x.GetRecentItemsFromUserLibraries()).Returns(() => AsAsync(recent));
		return CreateHome();
	}

	private HomeViewModel CreateHome() => new(_client.Object, Observable.Never<IOutboundWebSocketMessage>(), Commands());

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

	private static RecentItemDtoQueryResult Recent(int count) =>
		new(new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies" }, [.. Items(count).Items!]);

	private static async IAsyncEnumerable<RecentItemDtoQueryResult> AsAsync(params RecentItemDtoQueryResult[] items)
	{
		foreach (var item in items)
		{
			await Task.Yield();
			yield return item;
		}
	}

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
