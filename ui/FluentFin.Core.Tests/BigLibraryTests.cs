using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.1 (big libraries): the library page against a fake server holding 30,000 movies, through the real client. A
/// whole library in one request took a real server (25,842 movies) over 100 s, past the client's timeout, and the page
/// stayed empty; here a request without a limit, or over <see cref="LibraryPageReader.MaxPageSize"/>, is refused after a
/// moment the way that timeout ended, so the old one-request load fails these tests.
/// </summary>
[Collection(SessionInfoCollection.Name)]
public sealed class BigLibraryTests : IAsyncLifetime
{
	private const string FakeToken = "fake-token-big-library-0123456789";
	private const int Size = 30_000;
	private static readonly Guid UserId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000031");
	private static readonly Guid MoviesId = Guid.Parse("10000000-0000-0000-0000-000000000031");
	private static readonly string[] Genres = ["Action", "Comedy", "Documentary", "Drama", "Horror"];

	private static readonly BaseItemDto Movies = new() { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };

	// every movie's JSON, in the server's order (by sort name): "A Title 00000" … "Z Title 29999"
	private static readonly string[] s_json = [.. Enumerable.Range(0, Size).Select(Json)];

	private readonly ConcurrentFakeJellyfin _server = new();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "fluentfin-big-library-" + Guid.NewGuid().ToString("N"));
	private readonly ConcurrentQueue<(int Start, int Limit, bool Total, string Fields)> _pages = new();
	private HomeCache _homeCache = null!;
	private JellyfinClient _client = null!;
	private int _inFlight;
	private int _mostInFlight;
	private int _refused;

	/// <summary>How long a page takes the fake server (by its size); a whole library is refused after <see cref="RefuseAfter"/>.</summary>
	private Func<int, int, TimeSpan> _delay = (_, limit) => TimeSpan.FromMilliseconds(5 + (limit * 0.02));

	/// <summary>A page the server fails (500), by its start.</summary>
	private Func<int, bool> _fails = _ => false;

	private static readonly TimeSpan RefuseAfter = TimeSpan.FromSeconds(1.5);

	static BigLibraryTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	private static string Name(int i) => $"{(char)('A' + (i * 26 / Size))} Title {i:D5}";

	private static Guid IdOf(int i) => Guid.Parse($"{i:D8}-0000-0000-0000-000000000031");

	private static string Json(int i) => JsonSerializer.Serialize(new Dictionary<string, object?>
	{
		["Id"] = IdOf(i).ToString("N"),
		["Name"] = Name(i),
		["SortName"] = Name(i).ToLowerInvariant(),
		["Type"] = "Movie",
		["ProductionYear"] = 1950 + (i % 70),
		["CommunityRating"] = i % 10,
		["Genres"] = new[] { Genres[i % Genres.Length] },
		["Tags"] = new[] { $"tag{i % 40}" },
		["ImageTags"] = new Dictionary<string, string> { ["Primary"] = "p" + i },
		["UserData"] = new Dictionary<string, object> { ["Played"] = i % 3 == 0, ["IsFavorite"] = false, ["PlaybackPositionTicks"] = 0 },
	});

	public async Task InitializeAsync()
	{
		_server.Start();
		_server.Respond("/UserViews", 200, """{"Items":[],"TotalRecordCount":0}""");
		_server.RespondWith("/Items", Answer);
		_homeCache = new HomeCache(_folder, new System.Reactive.Subjects.Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), _homeCache);
		await _client.Initialize(_server.BaseUrl, new AuthenticationResult
		{
			AccessToken = FakeToken,
			ServerId = "fake-server",
			User = new UserDto { Id = UserId, Name = "alice" },
		});
	}

	public Task DisposeAsync()
	{
		_server.Dispose();
		_homeCache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}

		return Task.CompletedTask;
	}

	/// <summary>GET /Items for the movies library: a page as asked, after its time; without a limit (or over 1,000) refused.</summary>
	private (int Status, string Body) Answer(FakeRequest request)
	{
		var now = Interlocked.Increment(ref _inFlight);
		int seen;
		while (now > (seen = Volatile.Read(ref _mostInFlight)) && Interlocked.CompareExchange(ref _mostInFlight, now, seen) != seen)
		{
		}

		try
		{
			if (!Guid.TryParse(request.Query["parentId"], out var parent) || parent != MoviesId)
			{
				return (404, "");
			}

			if (!int.TryParse(request.Query["limit"], out var limit) || limit > LibraryPageReader.MaxPageSize)
			{
				// the whole library at once: the real server worked past the client's 100 s and the page stayed empty
				Interlocked.Increment(ref _refused);
				Thread.Sleep(RefuseAfter);
				return (503, "");
			}

			var start = int.TryParse(request.Query["startIndex"], out var s) ? s : 0;
			var total = string.Equals(request.Query["enableTotalRecordCount"], "true", StringComparison.OrdinalIgnoreCase);
			_pages.Enqueue((start, limit, total, request.Query["fields"] ?? ""));
			Thread.Sleep(_delay(start, limit));
			if (_fails(start))
			{
				return (500, "");
			}

			var body = new StringBuilder("{\"Items\":[");
			for (var i = start; i < Math.Min(Size, start + limit); i++)
			{
				body.Append(i == start ? "" : ",").Append(s_json[i]);
			}

			body.Append("],\"TotalRecordCount\":").Append(total ? Size : 0).Append(",\"StartIndex\":").Append(start).Append('}');
			return (200, body.ToString());
		}
		finally
		{
			Interlocked.Decrement(ref _inFlight);
		}
	}

	private static async Task<T> Within<T>(Task<T> task, TimeSpan time) => await task.WaitAsync(time);

	private static Task Within(Task task, TimeSpan time) => task.WaitAsync(time);

	[Fact]
	public async Task ThirtyThousandMovies_ShowTheFirstPageAtOnce_ThenEveryOnePageByPage()
	{
		using var ui = new UiThread();
		var vm = await ui.Invoke(() => new LibraryViewModel(_client));
		var resets = 0;
		var watching = false;
		await ui.Invoke(() =>
		{
			((INotifyCollectionChanged)vm.Items).CollectionChanged += (_, e) => resets += watching && e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
			return 0;
		});

		var opened = Stopwatch.StartNew();
		await Within(ui.Run(() => vm.OnNavigatedTo(Movies)), TimeSpan.FromSeconds(10));
		opened.Stop();

		// the first cards at once: the first page, sorted, with the library's size said
		var first = await ui.Invoke(() => (vm.IsLoading, vm.State.IsLoaded, vm.Items.Count, vm.Items[0].Name, vm.LibraryCountText, vm.FocusRequest, vm.ArrivalFocus));
		Assert.True(opened.Elapsed < TimeSpan.FromSeconds(3), $"the first page took {opened.Elapsed}");
		Assert.Equal((false, true, LibraryPageReader.FirstPageSize, "A Title 00000", "Loading… 100 of 30,000 movies", 0, true), first);

		await ui.Invoke(() => watching = true);
		await Within(await ui.Invoke(() => vm.RestLoaded), TimeSpan.FromSeconds(60));

		var all = await ui.Invoke(() => (vm.IsLoadingMore, vm.LoadStopped, vm.Items.Count, vm.TotalCount, vm.LibraryCountText, vm.CountText));
		Assert.Equal((false, false, Size, Size, "30,000 movies", "30,000 movies"), all);
		// in the sort's order, every title once
		var names = await ui.Invoke(() => vm.Items.Select(x => x.Name).ToList());
		Assert.Equal(Enumerable.Range(0, Size).Select(Name), names);
		// the A to Z has every letter, each at its first title
		var letters = await ui.Invoke(() => vm.Letters.Select(l => (l.Letter, l.Index)).ToList());
		Assert.Equal(26, letters.Count);
		Assert.Equal(("M", names.FindIndex(n => n.StartsWith('M'))), letters[12]);
		// the Filter menu lists from every title, counted
		var genres = await ui.Invoke(() => vm.Facets.Single(f => f.Title == "Genres").Options.Select(o => o.Label).ToList());
		Assert.Equal(Genres.Select(g => $"{g} ({Size / Genres.Length})"), genres);
		var tags = await ui.Invoke(() => vm.Facets.Single(f => f.Title == "Tags").Options.Count);
		Assert.Equal(40, tags);
		// pages were inserted into the grid, never a reset (each would redraw every card and drop the keyboard's card)
		Assert.Equal(0, resets);

		// no request asked for the whole library; each was a bounded page, one at a time, by sort name, without the unused field
		Assert.Equal(0, _refused);
		Assert.Equal(1, _mostInFlight);
		var pages = _pages.ToList();
		Assert.All(pages, p => Assert.InRange(p.Limit, LibraryPageReader.MinPageSize, LibraryPageReader.MaxPageSize));
		for (var i = 1; i < pages.Count; i++)
		{
			Assert.Equal(pages[i - 1].Start + pages[i - 1].Limit, pages[i].Start);
		}

		Assert.True(pages[^1].Start + pages[^1].Limit >= Size);
		Assert.True(pages[0].Total);
		Assert.All(pages.Skip(1), p => Assert.False(p.Total));
		Assert.All(pages, p => Assert.DoesNotContain("PrimaryImageAspectRatio", p.Fields));
		Assert.All(pages, p => Assert.Contains("SortName", p.Fields));
		// pages grew on a fast server (a few large pages rather than three hundred of a hundred)
		Assert.True(pages.Count < 60, $"{pages.Count} pages");
	}

	[Fact]
	public async Task WhileTheRestLoads_SortFilterAndCountsWorkOnWhatIsIn_AndAreWholeOnceItIs()
	{
		_delay = (start, limit) => TimeSpan.FromMilliseconds(start == 0 ? 5 : 150);
		using var ui = new UiThread();
		var vm = await ui.Invoke(() => new LibraryViewModel(_client));
		await ui.Run(() => vm.OnNavigatedTo(Movies));

		await ui.Invoke(() => { vm.Filter.Genres.Add("Drama"); return 0; });
		var during = await ui.Invoke(() => (vm.IsLoadingMore, vm.ShowingText, vm.NoMatches, vm.Items.All(x => x.Genres.Contains("Drama"))));
		Assert.Equal((true, true, false, true), (during.IsLoadingMore, during.ShowingText.EndsWith(" movies so far", StringComparison.Ordinal), during.NoMatches, during.Item4));

		await ui.Invoke(() => { vm.UpdateSortBy(ItemSortBy.CommunityRating); return 0; });
		await Within(await ui.Invoke(() => vm.RestLoaded), TimeSpan.FromSeconds(60));

		var after = await ui.Invoke(() => (vm.Items.Count, vm.ShowingText, vm.Items.Select(x => x.CommunityRating ?? 0).ToList()));
		Assert.Equal(Size / Genres.Length, after.Count);
		Assert.Equal("Showing 6,000 of 30,000 movies", after.ShowingText);
		// highest first, through every page's titles
		Assert.Equal(after.Item3.OrderByDescending(x => x), after.Item3);
	}

	[Fact]
	public async Task ASlowServer_ShowsTheFirstPage_AndLeavingThePageStopsTheRest()
	{
		_delay = (_, _) => TimeSpan.FromSeconds(1);
		using var ui = new UiThread();
		var vm = await ui.Invoke(() => new LibraryViewModel(_client));

		var opened = Stopwatch.StartNew();
		await Within(ui.Run(() => vm.OnNavigatedTo(Movies)), TimeSpan.FromSeconds(10));
		Assert.True(opened.Elapsed < TimeSpan.FromSeconds(4), $"the first page took {opened.Elapsed}");
		Assert.Equal((LibraryPageReader.FirstPageSize, true), await ui.Invoke(() => (vm.Items.Count, vm.IsLoadingMore)));

		// a page or two more come
		await Task.Delay(TimeSpan.FromSeconds(2.5));
		Assert.True(await ui.Invoke(() => vm.Items.Count) > LibraryPageReader.FirstPageSize);

		// left: what is in flight is cancelled, and nothing more is asked
		await ui.Run(vm.OnNavigatedFrom);
		await Task.Delay(TimeSpan.FromMilliseconds(300));
		var asked = _pages.Count;
		await Task.Delay(TimeSpan.FromSeconds(2.5));
		Assert.Equal(asked, _pages.Count);
		await Within(await ui.Invoke(() => vm.RestLoaded), TimeSpan.FromSeconds(5));
		Assert.Equal(0, _refused);
	}

	[Fact]
	public async Task APageThatFailsEveryTry_StopsTheRest_TheLibraryAsFarAsItCameAndSaidSo()
	{
		_fails = start => start >= 300;
		using var ui = new UiThread();
		var vm = await ui.Invoke(() => new LibraryViewModel(_client));
		await ui.Run(() => vm.OnNavigatedTo(Movies));
		await Within(await ui.Invoke(() => vm.RestLoaded), TimeSpan.FromSeconds(30));

		var (count, stopped, more, text) = await ui.Invoke(() => (vm.Items.Count, vm.LoadStopped, vm.IsLoadingMore, vm.LibraryCountText));
		Assert.True(count >= LibraryPageReader.FirstPageSize && count <= 300, $"{count} titles");
		Assert.Equal((true, false, $"{count:N0} of 30,000 movies · the rest didn't load"), (stopped, more, text));
		// asked three times, each half as large as the last
		var failed = _pages.Where(p => p.Start >= 300).Select(p => p.Limit).ToList();
		Assert.Equal(LibraryPageReader.Tries, failed.Count);
		Assert.Equal([failed[0], Math.Max(LibraryPageReader.MinPageSize, failed[0] / 2), Math.Max(LibraryPageReader.MinPageSize, failed[0] / 4)], failed);
	}

	[Fact]
	public async Task APageThatNeverAnswers_IsAskedAgainSmaller_ThenTheReaderStops()
	{
		_delay = (start, _) => start == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(3);
		var reader = new LibraryPageReader(_client, Movies) { PageTimeout = TimeSpan.FromMilliseconds(300), Pause = (_, _) => Task.CompletedTask };

		Assert.Equal(LibraryPageReader.FirstPageSize, (await reader.ReadFirstAsync())!.Count);
		Assert.Equal(Size, reader.Total);
		var timed = Stopwatch.StartNew();
		Assert.Null(await reader.ReadNextAsync());

		Assert.True(timed.Elapsed < TimeSpan.FromSeconds(2.5), $"three tries took {timed.Elapsed}");
		Assert.Equal((true, false, LibraryPageReader.Tries + 1), (reader.HasFailed, reader.IsDone, reader.Requests));
	}

	[Fact]
	public async Task BackToACardFarDown_ComesBackWhenItsPageDoes_UnlessTheUserMovedMeanwhile()
	{
		_delay = (start, _) => TimeSpan.FromMilliseconds(start == 0 ? 5 : 60);
		var far = IdOf(25_000);
		LibraryPlaces.Remember(UserId, MoviesId, new LibraryPlace(ItemSortBy.Name, SortOrder.Ascending, [], [], [], [], far, 51_234, false, false));

		using var ui = new UiThread();
		var back = await ui.Invoke(() => new LibraryViewModel(_client) { ComingBack = true, PlaceWait = TimeSpan.Zero });
		await ui.Run(() => back.OnNavigatedTo(Movies));
		// not in yet: no card asked for, and the grid is not scrolled (the first page is shown at its top)
		Assert.Equal((-1, (double?)null, false), await ui.Invoke(() => (back.FocusRequest, back.ReturnOffset, back.ArrivalFocus)));
		await Within(await ui.Invoke(() => back.RestLoaded), TimeSpan.FromSeconds(60));
		var (index, offset, card) = await ui.Invoke(() => (back.FocusRequest, back.ReturnOffset, back.FocusRequest >= 0 ? back.Items[back.FocusRequest].Id : null));
		Assert.Equal((25_000, 51_234d, (Guid?)far), (index, offset ?? 0, card));

		// the user scrolled while it loaded: the card is not put back over them
		_pages.Clear();
		using var ui2 = new UiThread();
		var moved = await ui2.Invoke(() => new LibraryViewModel(_client) { ComingBack = true, PlaceWait = TimeSpan.Zero });
		await ui2.Run(() => moved.OnNavigatedTo(Movies));
		await ui2.Invoke(() => { moved.ShowingFrom(12); return 0; });
		await Within(await ui2.Invoke(() => moved.RestLoaded), TimeSpan.FromSeconds(60));
		Assert.Equal((-1, (double?)null), await ui2.Invoke(() => (moved.FocusRequest, moved.ReturnOffset)));
	}

	[Fact]
	public async Task BackToACardAFewPagesOn_WaitsForItsPage_ThenShowsTheCardAtOnce()
	{
		var near = IdOf(450);
		LibraryPlaces.Remember(UserId, MoviesId, new LibraryPlace(ItemSortBy.Name, SortOrder.Ascending, [], [], [], [], near, 9_000, false, false));

		using var ui = new UiThread();
		var back = await ui.Invoke(() => new LibraryViewModel(_client) { ComingBack = true });
		await ui.Run(() => back.OnNavigatedTo(Movies));

		var (index, offset, card, loading) = await ui.Invoke(() => (back.FocusRequest, back.ReturnOffset, back.FocusRequest >= 0 ? back.Items[back.FocusRequest].Id : null, back.IsLoading));
		Assert.Equal((450, 9_000d, (Guid?)near, false), (index, offset ?? 0, card, loading));
		await Within(await ui.Invoke(() => back.RestLoaded), TimeSpan.FromSeconds(60));
		// pages after it did not move it: its titles all sort after
		Assert.Equal(450, await ui.Invoke(() => back.FocusRequest));
	}

	[Fact]
	public async Task EditorialHome_GenreStrip_ReadsABigLibraryInPages()
	{
		EditorialGenreSource.Clear();
		try
		{
			var titles = await Within(EditorialGenreSource.Titles(_client, [Movies]), TimeSpan.FromSeconds(60));

			Assert.Equal(Size, titles.Count);
			Assert.Equal(0, _refused);
			Assert.All(_pages, p => Assert.InRange(p.Limit, LibraryPageReader.MinPageSize, LibraryPageReader.MaxPageSize));
		}
		finally
		{
			EditorialGenreSource.Clear();
		}
	}

	[Theory]
	[InlineData(100, 0.1, 200)] // fast: twice as many
	[InlineData(100, 2.0, 100)] // on target: the same
	[InlineData(400, 8.0, 200)] // slow: half
	[InlineData(800, 0.1, 1000)] // never over the most
	[InlineData(60, 30.0, 50)] // never under the least
	public void ThePageSize_AimsAtTheTarget(int size, double seconds, int next) =>
		Assert.Equal(next, LibraryPageReader.NextSize(size, TimeSpan.FromSeconds(seconds)));
}
