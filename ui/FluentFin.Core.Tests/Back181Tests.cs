using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8.1, the back stream. BH-175-q2: a page Back showed is refreshed behind when something may have changed; Back,
/// Forward and Back again while the server is slow start two refreshes, and the older one's answers, coming last, were written
/// over the newer one's (only its final state was checked). What the user sees is the newer answer's: Play's label, an
/// episode's watch state.
/// </summary>
[Collection(ItemUserDataCollection.Name)]
public sealed class Back181RefreshTests
{
	[Fact]
	public async Task ARefreshBehind_IsToldWhenANewerRunHasBegun()
	{
		var state = new LoadState("this movie");
		var older = new TaskCompletionSource();
		var newer = new TaskCompletionSource();
		bool? olderNewest = null, newerNewest = null;
		var first = state.RefreshBehindAsync(async newest =>
		{
			await older.Task;
			olderNewest = newest();
			return LoadOutcome.Loaded;
		});
		var second = state.RefreshBehindAsync(async newest =>
		{
			await newer.Task;
			newerNewest = newest();
			return LoadOutcome.Loaded;
		});

		newer.SetResult();
		Assert.True(await second);
		older.SetResult();
		Assert.False(await first);
		Assert.Equal((false, true), (olderNewest, newerNewest));

		// a Retry or R's load is newer too
		var behind = new TaskCompletionSource();
		bool? afterRetry = null;
		var third = state.RefreshBehindAsync(async newest =>
		{
			await behind.Task;
			afterRetry = newest();
			return LoadOutcome.Loaded;
		});
		await state.RunAsync(() => Task.FromResult(LoadOutcome.Loaded));
		behind.SetResult();
		Assert.False(await third);
		Assert.False(afterRetry);
	}

	private static BaseItemDto Film(Guid id, long positionTicks) => new()
	{
		Id = id,
		Type = BaseItemDto_Type.Movie,
		Name = "Wild Canyons",
		RunTimeTicks = TimeSpan.FromMinutes(100).Ticks,
		UserData = new UserItemDataDto { PlaybackPositionTicks = positionTicks, PlayedPercentage = positionTicks * 100.0 / TimeSpan.FromMinutes(100).Ticks },
	};

	[Fact]
	public async Task AFilmPage_TwoRefreshesBehind_TheOlderAnsweringLast_KeepsTheNewerResumePoint()
	{
		var id = Guid.NewGuid();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).ReturnsAsync(Film(id, 0));
		client.Setup(x => x.GetItemCollections(id)).ReturnsAsync([]);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var vm = new MovieViewModel(client.Object, commands);
		await vm.OnNavigatedTo(Film(id, 0));
		Assert.Equal("Play", vm.PlayLabel);

		// Back (its refresh asks, the server slow), Forward, Back again (a newer refresh asks)
		var older = new TaskCompletionSource<BaseItemDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var newer = new TaskCompletionSource<BaseItemDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var answers = new Queue<TaskCompletionSource<BaseItemDto?>>([older, newer]);
		client.Setup(x => x.GetItem(id)).Returns(() => answers.Dequeue().Task);
		var first = ((IKeptOnBack)vm).Returned(Film(id, 0), stale: true);
		var second = ((IKeptOnBack)vm).Returned(Film(id, 0), stale: true);

		// the newer answer first: watched to 42:00 since; then the older one (12:00)
		newer.SetResult(Film(id, TimeSpan.FromMinutes(42).Ticks));
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal("Resume at 42:00", vm.PlayLabel);
		older.SetResult(Film(id, TimeSpan.FromMinutes(12).Ticks));
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal("Resume at 42:00", vm.PlayLabel);
		Assert.Equal(TimeSpan.FromMinutes(42).Ticks, vm.Dto.UserData!.PlaybackPositionTicks);
	}

	[Fact]
	public async Task ASeriesPage_TwoRefreshesBehind_TheOlderAnsweringLast_KeepsTheNewerWatchStates()
	{
		var seriesId = Guid.NewGuid();
		var series = new BaseItemDto { Id = seriesId, Type = BaseItemDto_Type.Series, Name = "Lost Harbour", UserData = new UserItemDataDto { Played = false } };
		var season = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1", IndexNumber = 1, SeriesId = seriesId, UserData = new UserItemDataDto { Played = false } };
		var ids = Enumerable.Range(1, 13).Select(_ => Guid.NewGuid()).ToList(); // a season at real shape
		List<BaseItemDto> Episodes(int watched) => [.. ids.Select((id, i) => new BaseItemDto
		{
			Id = id, Type = BaseItemDto_Type.Episode, SeriesId = seriesId, SeasonId = season.Id, ParentIndexNumber = 1, IndexNumber = i + 1,
			Name = $"S1 episode {i + 1}", UserData = new UserItemDataDto { Played = i < watched, PlaybackPositionTicks = 0 },
		})];
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(seriesId)).ReturnsAsync(series);
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == seriesId), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [season] });
		var lists = new Queue<Task<BaseItemDtoQueryResult?>>();
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == season.Id), It.IsAny<bool>()))
			.Returns(() => lists.Count > 0 ? lists.Dequeue() : Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = Episodes(0) }));
		client.Setup(x => x.GetSeriesEpisodes(seriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = Episodes(0) });
		client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var vm = new SeriesViewModel(client.Object);
		await vm.OnNavigatedTo(new BaseItemDto { Id = seriesId, Type = BaseItemDto_Type.Series });
		Assert.Equal(13, vm.Episodes.Count);
		Assert.DoesNotContain(vm.Episodes, e => e.UserData?.Played == true);
		await vm.OnNavigatedFrom();

		// Back (slow), Forward, Back again: the newer says three episodes were watched since; the older (none) answers last
		var older = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var newer = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		lists.Enqueue(older.Task);
		lists.Enqueue(newer.Task);
		var first = ((IKeptOnBack)vm).Returned(series, stale: true);
		var second = ((IKeptOnBack)vm).Returned(series, stale: true);
		newer.SetResult(new BaseItemDtoQueryResult { Items = Episodes(3) });
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(3, vm.Episodes.Count(e => e.UserData?.Played == true));
		older.SetResult(new BaseItemDtoQueryResult { Items = Episodes(0) });
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal([true, true, true, false], vm.Episodes.Take(4).Select(e => e.UserData?.Played == true));
		await vm.OnNavigatedFrom();
	}

	// ---- the review: the other refresh-behind places (an episode, a season, a collection, a person, Home, search) ----------

	private static GlobalCommands Commands(IJellyfinClient client) =>
		new(Mock.Of<INavigationServiceCore>(), client, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	// two answers in a row of one request, each held until the test gives it
	private static (TaskCompletionSource<T> Older, TaskCompletionSource<T> Newer, Func<Task<T>> Next) TwoAnswers<T>()
	{
		var older = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		var newer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		var queue = new Queue<TaskCompletionSource<T>>([older, newer]);
		return (older, newer, () => queue.Dequeue().Task);
	}

	[Fact]
	public async Task AnEpisodePage_TheOlderRefreshAnsweringLast_KeepsTheNewerResumePoint()
	{
		var id = Guid.NewGuid();
		BaseItemDto Episode(int minutes) => new()
		{
			Id = id, Type = BaseItemDto_Type.Episode, Name = "The Falling Crown", RunTimeTicks = TimeSpan.FromMinutes(50).Ticks,
			UserData = new UserItemDataDto { PlaybackPositionTicks = TimeSpan.FromMinutes(minutes).Ticks, PlayedPercentage = minutes * 2 },
		};
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).ReturnsAsync(Episode(0));
		var vm = new EpisodeViewModel(client.Object, Commands(client.Object));
		await vm.OnNavigatedTo(Episode(0));
		Assert.Equal("Play", vm.PlayLabel);

		var (older, newer, next) = TwoAnswers<BaseItemDto?>();
		client.Setup(x => x.GetItem(id)).Returns(next);
		var first = ((IKeptOnBack)vm).Returned(Episode(0), stale: true);
		var second = ((IKeptOnBack)vm).Returned(Episode(0), stale: true);
		newer.SetResult(Episode(31));
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		older.SetResult(Episode(4));
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal("Resume at 31:00", vm.PlayLabel);
	}

	[Fact]
	public async Task ASeasonPage_TheOlderRefreshAnsweringLast_KeepsTheNewerWatchStates()
	{
		var seriesId = Guid.NewGuid();
		var season = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1", IndexNumber = 1, SeriesId = seriesId, UserData = new UserItemDataDto { Played = false } };
		var ids = Enumerable.Range(1, 13).Select(_ => Guid.NewGuid()).ToList();
		List<BaseItemDto> Episodes(int watched) => [.. ids.Select((id, i) => new BaseItemDto
		{
			Id = id, Type = BaseItemDto_Type.Episode, SeriesId = seriesId, SeasonId = season.Id, ParentIndexNumber = 1, IndexNumber = i + 1,
			Name = $"S1 episode {i + 1}", UserData = new UserItemDataDto { Played = i < watched, PlaybackPositionTicks = 0 },
		})];
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync(season);
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == seriesId), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [season] });
		var lists = new Queue<Task<BaseItemDtoQueryResult?>>();
		client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == season.Id), It.IsAny<bool>()))
			.Returns(() => lists.Count > 0 ? lists.Dequeue() : Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = Episodes(0) }));
		client.Setup(x => x.GetSeriesEpisodes(seriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = Episodes(0) });
		client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var vm = new SeasonViewModel(client.Object);
		await vm.OnNavigatedTo(season);
		Assert.Equal(13, vm.Episodes!.Count);
		await vm.OnNavigatedFrom();

		var older = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var newer = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		lists.Enqueue(older.Task);
		lists.Enqueue(newer.Task);
		var first = ((IKeptOnBack)vm).Returned(season, stale: true);
		var second = ((IKeptOnBack)vm).Returned(season, stale: true);
		newer.SetResult(new BaseItemDtoQueryResult { Items = Episodes(2) });
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		older.SetResult(new BaseItemDtoQueryResult { Items = Episodes(0) });
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal([true, true, false], vm.Episodes!.Take(3).Select(e => e.UserData?.Played == true));
		await vm.OnNavigatedFrom();
	}

	[Fact]
	public async Task ACollectionPage_TheOlderRefreshAnsweringLast_KeepsTheNewerFilms()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		List<BaseItemDto> Films(int count) => [.. Enumerable.Range(1, count).Select(i => new BaseItemDto
		{
			Id = new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), Name = $"Film {i}", Type = BaseItemDto_Type.Movie, ProductionYear = 1990 + i,
			PremiereDate = new DateTimeOffset(1990 + i, 1, 1, 0, 0, 0, TimeSpan.Zero), UserData = new UserItemDataDto { Played = false },
		})];
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = Films(6) });
		var vm = new CollectionViewModel(client.Object, Commands(client.Object));
		await vm.OnNavigatedTo(set);
		Assert.Equal(6, vm.Entries.Count);

		// a film added to the set since: the newer refresh has 7, the older (answering last) 6
		var (older, newer, next) = TwoAnswers<BaseItemDtoQueryResult?>();
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).Returns(next);
		var first = ((IKeptOnBack)vm).Returned(set, stale: true);
		var second = ((IKeptOnBack)vm).Returned(set, stale: true);
		newer.SetResult(new BaseItemDtoQueryResult { Items = Films(7) });
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(7, vm.Entries.Count);
		older.SetResult(new BaseItemDtoQueryResult { Items = Films(6) });
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal(7, vm.Entries.Count);
	}

	[Fact]
	public async Task APersonsPage_TheOlderRefreshAnsweringLast_KeepsTheNewerTitles()
	{
		var person = new BaseItemDto { Id = Guid.NewGuid(), Name = "Sunny Holm", Type = BaseItemDto_Type.Person };
		BaseItemDto Film(string name, int year) => new() { Id = new Guid(year, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]), Name = name, Type = BaseItemDto_Type.Movie, ProductionYear = year };
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetPersonItems(person.Id!.Value)).ReturnsAsync(new BaseItemDtoQueryResult { Items = [Film("Lantern Bay", 2001)], TotalRecordCount = 1 });
		client.Setup(x => x.GetPersonEpisodes(person.Id!.Value, It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
		var vm = new PersonItemsViewModel(client.Object);
		await vm.OnNavigatedTo(person);
		await vm.Rest;
		Assert.Equal(["Lantern Bay"], vm.Items.Select(x => x.Dto.Name));

		var (older, newer, next) = TwoAnswers<BaseItemDtoQueryResult?>();
		client.Setup(x => x.GetPersonItems(person.Id!.Value)).Returns(next);
		var first = ((IKeptOnBack)vm).Returned(person, stale: true);
		var second = ((IKeptOnBack)vm).Returned(person, stale: true);
		newer.SetResult(new BaseItemDtoQueryResult { Items = [Film("Glass Harbor", 2009), Film("Lantern Bay", 2001)], TotalRecordCount = 2 });
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(2, vm.Items.Count);
		older.SetResult(new BaseItemDtoQueryResult { Items = [Film("Lantern Bay", 2001)], TotalRecordCount = 1 });
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal(["Glass Harbor", "Lantern Bay"], vm.Items.Select(x => x.Dto.Name));
	}

	[Fact]
	public async Task SearchResults_TheOlderSearchAnsweringLast_KeepsTheNewerResults()
	{
		// (the review: with SearchAgain's guard taken out, every other test passed)
		BaseItemDto Film(string name) => new() { Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name))), Name = name, Type = BaseItemDto_Type.Movie };
		var answers = new Queue<Task<BaseItemDtoQueryResult?>>();
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.Search(It.IsAny<string>(), It.IsAny<IReadOnlyList<BaseItemKind>>(), It.IsAny<int>()))
			.Returns((string _, IReadOnlyList<BaseItemKind> kinds, int _) => kinds.Contains(BaseItemKind.Movie) && answers.Count > 0
				? answers.Dequeue()
				: Task.FromResult<BaseItemDtoQueryResult?>(kinds.Contains(BaseItemKind.Movie) ? new BaseItemDtoQueryResult { Items = [Film("Sunny Days")], TotalRecordCount = 1 } : new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 }));
		var vm = new SearchResultsViewModel(client.Object);
		await vm.OnNavigatedTo("sunny");
		List<string?> Shown() => [.. (vm.TopResult is { } top ? [top] : Array.Empty<BaseItemViewModel>()).Concat(vm.CardGroups.SelectMany(g => g.Items)).Select(x => x.Dto?.Name)];
		Assert.Equal(["Sunny Days"], Shown());

		var older = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var newer = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		answers.Enqueue(older.Task);
		answers.Enqueue(newer.Task);
		var first = vm.Returned("sunny", stale: true);
		var second = vm.Returned("sunny", stale: true);
		newer.SetResult(new BaseItemDtoQueryResult { Items = [Film("Sunny Days"), Film("Sunny Mornings")], TotalRecordCount = 2 });
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Contains("Sunny Mornings", Shown());
		older.SetResult(new BaseItemDtoQueryResult { Items = [Film("Sunny Days")], TotalRecordCount = 1 });
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Contains("Sunny Mornings", Shown());
		Assert.Equal(2, Shown().Count);
	}
}

/// <summary>
/// Nixie 1.8.1 (BH-175-q2, the review): Home kept whole and refreshed behind twice (Back, Forward, Back while the server is
/// slow): the older refresh, answering last, no longer puts back the Continue watching row the newer one replaced.
/// </summary>
[Collection(ItemUserDataCollection.Name)]
public sealed class Back181HomeRefreshTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-home181-" + Guid.NewGuid().ToString("N"));
	private readonly System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage> _socket = new();
	private readonly ManualClock _clock = new();

	static Back181HomeRefreshTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public void Dispose() => TestFolder.Delete(_folder);

	private static BaseItemDto Item(string name) =>
		new() { Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(name))), Name = name, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 100 } };

	[Fact]
	public async Task Home_TheOlderRefreshAnsweringLast_KeepsTheNewerContinueWatching()
	{
		using var cache = new HomeCache(_folder, _socket, new CoreListLogger<HomeCache>(), _clock);
		var answers = new Queue<Task<BaseItemDtoQueryResult?>>();
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("https://jellyfin.example");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("cccccccc-0000-0000-0000-000000000181"));
		client.Setup(x => x.GetContinueWatching()).Returns(() => answers.Count > 0
			? answers.Dequeue()
			: Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = [Item("Half watched")] }));
		client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		client.Setup(x => x.GetUserViews()).ReturnsAsync([]);
		var home = new HomeViewModel(client.Object, _socket, new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()),
			cache, null, _clock);
		await home.OnNavigatedTo(new object());
		Assert.Equal(["Half watched"], home.ContinueItems.Select(x => x.Dto.Name));
		await home.OnNavigatedFrom();

		// a play elsewhere since (the saved page stale): Back, Forward, Back while the server is slow
		cache.MarkStale();
		var older = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var newer = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		answers.Enqueue(older.Task);
		answers.Enqueue(newer.Task);
		var first = home.Returned(new object(), stale: true);
		var second = home.Returned(new object(), stale: true);
		newer.SetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched"), Item("Just started")] });
		await second.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(2, home.ContinueItems.Count);
		older.SetResult(new BaseItemDtoQueryResult { Items = [Item("Half watched")] });
		await first.WaitAsync(TimeSpan.FromSeconds(10));

		Assert.Equal(["Half watched", "Just started"], home.ContinueItems.Select(x => x.Dto.Name));
		await home.OnNavigatedFrom();
	}
}

/// <summary>
/// Nixie 1.8.1 (BH-180-q4): a Home page saved just before midnight and shown again just after it (under five minutes: fresh,
/// no section asked again) shows the new day's Made by Nixie rows. The new-day check knew only the day this app last made
/// rows, which a start whose own fill is still coming (a first read of the index) doesn't know yet: the saved page's rows
/// stayed yesterday's until the next visit.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class Back181HomeMidnightTests : IDisposable
{
	private static readonly Guid User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000181");
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-midnight-" + Guid.NewGuid().ToString("N"));
	private readonly System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage> _messages = new();
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _films = [];
	private readonly Settings _settings = new();
	private readonly ManualClock _clock = new();
	private TaskCompletionSource? _readHeld; // while set, the index's pages wait for it (a first read on its way)

	static Back181HomeMidnightTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public Back181HomeMidnightTests()
	{
		_settings.SaveSetting(SmartCollections.CollectionLineup.HomeRowsKey.Name, 3); // three rows: a day turns what Home shows
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(User);
		// four of the lineup's slots at real shape: two themes, Acclaimed, Under 90 minutes; ten titles each
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Midnight heist {i}", 1970 + i, 7.0, 120, "heist", ["Crime", "Thriller"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Midnight loop {i}", 1990 + i, 7.2, 115, "time travel", ["Science Fiction"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Midnight acclaimed {i}", 1950 + i, 8.0 + (i * 0.09), 130, null, ["Drama"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Midnight short {i}", 2005 + i, 6.8, 80, null, ["Comedy"])));
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool _, CancellationToken _) =>
			{
				if (_readHeld is { } held)
				{
					await held.Task;
				}

				return new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count };
			});
		_client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => [.. ids.Select(id => _films.First(f => f.Id == id))]);
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>())).ReturnsAsync([_films[0]]);
	}

	public void Dispose()
	{
		_readHeld?.TrySetResult();
		SmartCollectionServices.Index = null;
		TestFolder.Delete(_folder);
	}

	private static BaseItemDto Film(string name, int year, double rating, int minutes, string? tag, List<string> genres) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.Movie,
		ProductionYear = year,
		CommunityRating = (float)rating,
		CriticRating = 80,
		RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
		Tags = tag is null ? [] : [tag],
		Genres = genres,
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" + name.Length } },
		UserData = new UserItemDataDto { Played = false },
	};

	private HomeViewModel Home(HomeCache cache) =>
		new(_client.Object, _messages, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()),
			cache, _settings, _clock);

	private HomeCache Cache() => new(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), _clock);

	private LibraryIndex Index(HomeCache cache, string name) =>
		new(_client.Object, cache, Path.Combine(_folder, name), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);

	private string[] Lineup(IndexSnapshot index) =>
		[.. SmartCollections.CollectionLineup.Build(index, SmartCollections.SmartCollectionCatalog.Now(_clock), new HashSet<string>(), new HashSet<SmartCollections.SmartCollectionKind>(), [], FluentFin.Core.Outside.OutsideListsSnapshot.Empty)
			.Lineup.Picks.Take(3).Select(x => string.Join(",", x.Collection.ItemIds))];

	private static string[] Rows(HomeViewModel home) => [.. home.NixieRows.Select(r => string.Join(",", r.Collection.ItemIds))];

	[Fact]
	public async Task AStartJustBeforeMidnight_ItsFillStillComing_AVisitJustAfterShowsTheNewDaysRows()
	{
		// 11:50 PM on this PC's clock
		var local = TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _clock.LocalTimeZone);
		var to = local.Date.AddHours(23).AddMinutes(50) - local.DateTime;
		_clock.Advance(to < TimeSpan.Zero ? to + TimeSpan.FromDays(1) : to);

		// the last run: the index read, Home's rows made and saved for the next start
		using (var last = Cache())
		{
			var index = Index(last, "LibraryIndex");
			SmartCollectionServices.Index = index;
			Assert.NotNull(await index.GetAsync());
			var home = Home(last);
			await home.OnNavigatedTo(new object());
			await home.NixieFill;
			await home.SpotlightLater;
			await home.OnNavigatedFrom();
			await index.Settled;
			Assert.NotEmpty(home.NixieRows);
		}

		// the next start, at 11:57 PM: Home's saved page shows at once; the index has to be read anew (a newer index revision:
		// nothing saved for it), its pages slow; Home's rows wait for it, and Home is saved (fresh for five minutes)
		_clock.Advance(TimeSpan.FromMinutes(7));
		HomeViewModel.ForgetNixieDayForTests();
		using var cache = Cache();
		var again = Index(cache, "LibraryIndexNextRevision");
		SmartCollectionServices.Index = again;
		_readHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var start = Home(cache);
		await start.OnNavigatedTo(new object());
		var yesterday = Rows(start);
		Assert.NotEmpty(yesterday);
		Assert.False(start.NixieFill.IsCompleted, "the start's fill waits for the index's read");
		Assert.True((await cache.Get("http://server", User)).IsFresh);
		await start.OnNavigatedFrom();

		// 12:01 AM: Home again (from the top bar), the page saved 4 minutes ago: fresh, shown as it is
		_clock.Advance(TimeSpan.FromMinutes(4));
		var visit = Home(cache);
		var opening = visit.OnNavigatedTo(new object());
		_readHeld.SetResult();
		await opening.WaitAsync(TimeSpan.FromSeconds(20));
		await visit.NixieFill.WaitAsync(TimeSpan.FromSeconds(20));
		await start.NixieFill.WaitAsync(TimeSpan.FromSeconds(20));
		await visit.SpotlightLater;

		var today = Lineup(again.Current!);
		Assert.NotEqual(yesterday, today); // the fixture: the day turned what Home shows
		Assert.Equal(today, Rows(visit));
		await visit.OnNavigatedFrom();
	}

	/// <summary>The settings a test sets: values kept as they are.</summary>
	private sealed class Settings : ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
