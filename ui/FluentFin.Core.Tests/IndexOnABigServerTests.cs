using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.1 (the owner, testing 1.7.0: "nixie is reading your libraries takes wayyy too long"): on the owner's server
/// (34,001 titles in 23 libraries) the index took 1,198 s, one page after another, and was read again at every start. Now:
/// the saved index shows at once after a restart and is read again only when it is stale; outside lists re-match it and
/// never read it; a first read shows its rows as its pages come and says how far it has got; pages come two at a time; the
/// people pass is lean; and the log says how long each part took.
/// <para>
/// Nixie 1.7.3: no people pass at all (the owner's answer to the 1.7.3 plan); a title the server lists in two libraries is
/// one entry with both; the saved index's revision 3; played episodes that didn't come are asked again on their own; and the
/// reading line has the server's check's count as its total until every library's own is in.
/// </para>
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed partial class IndexOnABigServerTests : IDisposable
{
	private static readonly Guid MoviesId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000171");
	private static readonly Guid ShowsId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000171");
	private static readonly Guid User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000171");
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-index171-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _films;
	private readonly Lock _lock = new();
	private int _pages;
	private int _peoplePages;
	private int _peopleById; // 1.8 (P1-2): requests of titles' people by id
	private int _peopleByIdTitles;
	private bool _peopleByIdFails;
	private int _inFlight;
	private int _mostAtOnce;
	private string? _check = "2500:newest";

	static IndexOnABigServerTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public IndexOnABigServerTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		CollectionNames.RememberBoxSets([]);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000171"));
		// 2,500 films: a franchise every three, a heist every fourth
		_films = [.. Enumerable.Range(0, 2500).Select(Film)];
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync(() => _check);
		Pages(_ => Task.CompletedTask);
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) =>
			{
				Interlocked.Increment(ref _peoplePages);
				return new BaseItemDtoQueryResult
				{
					Items = [.. _films.Skip(start).Take(limit).Select(x => new BaseItemDto { Id = x.Id, Type = BaseItemDto_Type.Movie, People = [new BaseItemPerson { Name = $"Director {x.ProductionYear}", Type = BaseItemPerson_Type.Director }] })],
					TotalRecordCount = total ? _films.Count : null,
				};
			});
		// 1.8 (P1-2): the people step's top-up, the new titles' people by id (counted; failing when a test says so)
		_client.Setup(x => x.GetPeopleByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) =>
			{
				Interlocked.Increment(ref _peopleById);
				Interlocked.Add(ref _peopleByIdTitles, ids.Count);
				return _peopleByIdFails ? null : (IReadOnlyList<BaseItemDto>?)[.. _films.Where(x => ids.Contains(x.Id!.Value))
					.Select(x => new BaseItemDto { Id = x.Id, Type = BaseItemDto_Type.Movie, People = [new BaseItemPerson { Name = $"Director {x.ProductionYear}", Type = BaseItemPerson_Type.Director }] })];
			});
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
	}

	public void Dispose()
	{
		Core.Outside.OutsideListsSnapshot.Shared = Core.Outside.OutsideListsSnapshot.Empty;
		CollectionNames.ForgetBoxSets();
		_homeCache.Dispose();
		TestFolder.Delete(_folder); // 1.7.1: the index's people pass may still be saving behind a test that ended
	}

	private static BaseItemDto Film(int i) => new()
	{
		Id = Guid.Parse($"{i:D8}-0171-0000-0000-000000000000"),
		Name = $"Film {i:D5}",
		SortName = $"film {i:D5}",
		Type = BaseItemDto_Type.Movie,
		ProductionYear = 1950 + (i % 70),
		CommunityRating = 5 + (i % 5),
		Tags = i % 4 == 0 ? ["heist"] : [],
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{i}", ["TmdbCollection"] = $"{i / 3}" } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"p{i}" } },
		UserData = new UserItemDataDto { Played = false },
	};

	// the index's pages, each after `wait` (start): counted, with the most asked at once
	private void Pages(Func<int, Task> wait) =>
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				Interlocked.Increment(ref _pages);
				lock (_lock)
				{
					_inFlight++;
					_mostAtOnce = Math.Max(_mostAtOnce, _inFlight);
				}

				try
				{
					await Task.Delay(15, token);
					await wait(start);
					return new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null };
				}
				finally
				{
					lock (_lock)
					{
						_inFlight--;
					}
				}
			});

	private LibraryIndex Index(TimeProvider? time = null, ILogger? log = null) =>
		new(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, log ?? new CoreListLogger<LibraryIndex>(), time ?? TimeProvider.System);

	private CollectionsViewModel Page(LibraryIndex index, ILogger<CollectionsViewModel>? log = null) =>
		new(_client.Object, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()), index,
			new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>()), logger: log);

	private sealed class ManualTime(DateTimeOffset now) : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = now;

		public override DateTimeOffset GetUtcNow() => Now;
	}

	// an index read and saved, as a run before this one left it
	private async Task<IndexSnapshot> ReadAndSave(TimeProvider? time = null)
	{
		using var before = Index(time);
		var read = await before.GetAsync();
		await before.Settled;
		Assert.NotNull(read);
		return before.Current!;
	}

	[Fact]
	public async Task AfterARestart_TheSavedIndexShowsAtOnce_AndIsNotReadAgain_WhileTheServerSaysNothingChanged()
	{
		var saved = await ReadAndSave();
		var read = (_pages, _peoplePages);

		using var index = Index(); // a restart
		var shown = await index.GetAsync();
		await Task.Delay(100); // the check, in the background
		await index.Settled;

		Assert.Equal(saved.Entries.Count, shown!.Entries.Count);
		Assert.True(shown.HasPeople); // 1.8 (P1-2): the first read's people pass, saved with it
		Assert.Equal(read, (_pages, _peoplePages)); // 1.7.0 read every title and every person again here, at every start
		Assert.Equal(0, _peopleById);
		_client.Verify(x => x.GetIndexCheck(It.IsAny<CancellationToken>()), Times.Exactly(2)); // the read's own, and the restart's
	}

	[Fact]
	public async Task ASavedIndex_IsReadAgainBehindIt_OnlyWhenTheServersCheckChanged_AndNotWithinTheHour()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var read = _pages;
		_check = "2501:another"; // a film added

		time.Now += TimeSpan.FromMinutes(10);
		using var soon = Index(time);
		Assert.NotNull(await soon.GetAsync());
		await Task.Delay(100);
		await soon.Settled;
		Assert.Equal(read, _pages); // changed, but read ten minutes ago: not yet

		time.Now += TimeSpan.FromHours(2);
		using var later = Index(time);
		var shown = await later.GetAsync();
		Assert.Equal(2500, shown!.Entries.Count); // the saved one at once
		await Task.Delay(100);
		await later.Settled;
		Assert.True(_pages > read); // then read again behind it
		Assert.Equal("2501:another", later.Current!.Check);
	}

	[Fact]
	public async Task ALibraryChange_AsksTheServersCheck_NotEveryTitle()
	{
		// review (Minor): with the clock as it was, the change's visit came within the five minutes of the start's check, so
		// the check was never asked and the test passed without it
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var read = _pages;
		using var index = Index(time);
		await index.GetAsync();
		await Task.Delay(100);
		_client.Verify(x => x.GetIndexCheck(It.IsAny<CancellationToken>()), Times.Exactly(2)); // the read's own, and the start's

		_messages.OnNext(new LibraryChangedMessage());
		time.Now += LibraryIndex.CheckEvery + TimeSpan.FromMinutes(1);
		await index.GetAsync(); // the next visit
		await Task.Delay(100);
		await index.Settled;

		_client.Verify(x => x.GetIndexCheck(It.IsAny<CancellationToken>()), Times.Exactly(3)); // the change's
		Assert.Equal(read, _pages); // unchanged: nothing read (1.7.0 read the whole library at the next visit after any change)
	}

	// review M2 (Minor): a change the check confirms reads the titles only (1.7.3: and no people, nor any lent)
	[Fact]
	public async Task AChangeTheCheckConfirms_ReadsTheTitlesOnly()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var (read, people) = (_pages, _peoplePages);
		_check = "2501:another";
		_films.Add(Film(2500)); // a film added

		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time); // a start two hours on: the check says the libraries changed
		await index.GetAsync();
		await WaitForIndex(index, x => x.Entries.Count == 2501);
		await index.Settled;

		Assert.True(_pages > read);
		// 1.8 (P1-2): no library's people again; the new film's alone, by id, the others' lent from the index before
		Assert.Equal(people, _peoplePages);
		Assert.Equal((1, 1), (_peopleById, _peopleByIdTitles));
		Assert.True(index.Current!.HasPeople);
		Assert.Equal("Director 2000", Assert.Single(index.Current.Entries.Single(x => x.Name == "Film 02500").People).Name);
		Assert.All(index.Current.Entries, x => Assert.Single(x.People));
		Assert.Equal(time.Now - TimeSpan.FromHours(2), index.Current.ReadAt); // the day's date kept
	}

	// 1.8 (P1-2): 1.7.3's "no read asks for people" became the people step's tests (IndexOnABigServerTests.People180.cs)

	// review (Minor): R during a read was kept, and the next visit read the whole library again behind it
	[Fact]
	public async Task RDuringARead_IsAnsweredByThatRead()
	{
		await ReadAndSave();
		var release = new TaskCompletionSource();
		Pages(_ => release.Task);
		using var index = Index();
		await index.GetAsync();
		var reading = index.RefreshAsync(); // R

		index.MarkStale(); // R again, while it reads
		release.SetResult();
		await reading;
		await index.Settled;
		var asked = (_pages, _peoplePages);
		await index.GetAsync(); // the next visit
		await Task.Delay(100);
		await index.Settled;

		Assert.Equal(asked, (_pages, _peoplePages));
	}

	// 1.7.3: with no people pass, a library change's read reads every title, as R's does; R during it was kept, and the next
	// visit read every title again (65 requests on a server of 25,736 films)
	[Fact]
	public async Task RDuringALibraryChangesRead_IsAnsweredByThatRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		_check = "2501:another";
		_films.Add(Film(2500));
		var release = new TaskCompletionSource();
		Pages(_ => release.Task);
		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		await index.GetAsync();
		await index.Checking; // the check said the libraries changed: their titles are being read
		Assert.False(index.Settled.IsCompleted);

		index.MarkStale(); // R, while it reads
		var reading = index.RefreshAsync();
		release.SetResult();
		Assert.Equal(2501, (await reading)!.Entries.Count);
		await index.Settled;
		var asked = _pages;
		time.Now += TimeSpan.FromMinutes(10);
		await index.GetAsync(); // the next visit
		await Task.Delay(100);
		await index.Settled;

		Assert.Equal(asked, _pages);
	}

	// review (Minor, 1.7.1): a first read that stopped left its pages so far as the page's newest index; new outside (or
	// trending) lists made them again, and brought back their reading line, with nothing reading
	[Fact]
	public async Task AFirstReadThatStopped_NewListsDoNotBringBackItsReadingLine()
	{
		Pages(start => start == 0 ? Task.CompletedTask : Task.FromException(new HttpRequestException("the server refused")));
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index);
		await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
		await vm.Reading.WaitAsync(TimeSpan.FromSeconds(20));
		await index.Settled;
		Assert.True(await ui.Invoke(() => vm.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie))); // its first page's rows stay
		Assert.False(await ui.Invoke(() => vm.IsReadingIndex));

		Core.Outside.OutsideListsSnapshot.Shared = Core.Outside.OutsideListsSnapshot.Empty with { FranchiseNames = new Dictionary<string, string> { ["0"] = "Aardvark Collection" } };
		await Task.Delay(300);

		Assert.False(await ui.Invoke(() => vm.IsReadingIndex));
		Assert.Null(index.Current);
		await ui.Run(vm.OnNavigatedFrom);
	}

	// review M3 (1.7.1): a people page was always 1,000 titles, with 45 s each; on the owner's server (about 26 ms a person, the
	// two in flight served together) each took ~52 s: every page ran out, was asked again as halves, and the abandoned request
	// ran on. A page that runs out now halves the pages after it, for the rest of the read and the next one.
	[Fact]
	public async Task APageThatRunsOut_MakesThePagesAfterItSmaller_ForTheRestOfTheReadAndTheNext()
	{
		// the owner's server scaled down a hundred times: 0.25 ms a person, shared by the requests in flight; a page may take
		// 400 ms here (45 s in the app). 1,000 people two at a time take 500 ms; 500, 250 ms
		const int People = 4200;
		var library = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var inFlight = 0;
		var ranOut = 0;
		var sizes = new System.Collections.Concurrent.ConcurrentQueue<int>();
		async Task<BaseItemDtoQueryResult?> Ask(BaseItemDto _, int start, int limit, bool total, CancellationToken token)
		{
			Interlocked.Increment(ref inFlight);
			sizes.Enqueue(limit);
			try
			{
				var count = Math.Clamp(People - start, 0, limit);
				var watch = System.Diagnostics.Stopwatch.StartNew();
				double served = 0, last = 0;
				while (served < 0.25 * count)
				{
					await Task.Delay(5, token);
					var now = watch.Elapsed.TotalMilliseconds;
					served += (now - last) / Math.Max(1, Volatile.Read(ref inFlight));
					last = now;
				}

				return new BaseItemDtoQueryResult
				{
					Items = [.. Enumerable.Range(start, count).Select(i => new BaseItemDto { Id = Guid.Parse($"{i:D8}-0171-0000-0000-0000000000aa"), Type = BaseItemDto_Type.Movie })],
					TotalRecordCount = total ? People : null,
				};
			}
			catch (OperationCanceledException)
			{
				// re-review (Major A): as the app's client: no exception, null (a fake that threw hid that it never counted)
				Interlocked.Increment(ref ranOut);
				return null;
			}
			finally
			{
				Interlocked.Decrement(ref inFlight);
			}
		}

		IndexPages Pass(int size) => new([library], Ask, "people", pageSize: size) { PageTimeout = TimeSpan.FromMilliseconds(400), Pause = (_, _) => Task.CompletedTask };

		var first = Pass(IndexPages.PageSize);
		Assert.True(await first.ReadAsync(CancellationToken.None));
		Assert.Equal(People, first.Items().Count);
		Assert.Equal(2, ranOut); // the two 1,000s in flight when it first ran out; not every page after them too
		Assert.Equal(IndexPages.PageSize / 2, first.PageSizeNow);

		ranOut = 0;
		sizes.Clear();
		var next = Pass(first.PageSizeNow); // the next read starts there
		Assert.True(await next.ReadAsync(CancellationToken.None));
		Assert.Equal(People, next.Items().Count);
		Assert.Equal(0, ranOut);
		Assert.All(sizes, size => Assert.True(size <= IndexPages.PageSize / 2));
	}

	// re-review (Major A): a page that kept running out (here: every page of 250 or more) leaves pages of 250, not of 50
	[Fact]
	public async Task PagesThatKeepRunningOut_LeaveNoSmallerSizeThan250()
	{
		var library = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var quick = SlowPages(0, 4200);
		IndexPages.Ask ask = async (lib, start, limit, total, token) =>
		{
			if (limit >= IndexPages.MinCarriedSize)
			{
				try
				{
					await Task.Delay(Timeout.Infinite, token); // a server that answers no page of 250 or more in time
				}
				catch (OperationCanceledException)
				{
					return null; // as the app's client
				}
			}

			return await quick(lib, start, limit, total, token);
		};
		var pages = new IndexPages([library], ask, "people") { PageTimeout = TimeSpan.FromMilliseconds(200), Pause = (_, _) => Task.CompletedTask };

		Assert.False(await pages.ReadAsync(CancellationToken.None)); // 1,000, then 500, then 250 ran out: the pass stopped
		Assert.Equal(IndexPages.MinCarriedSize, pages.PageSizeNow);
	}

	// re-review (Major A): a server quick again gets its bigger pages back (it stayed at the small size for good)
	[Fact]
	public async Task AServerQuickAgain_GetsItsBiggerPagesBack()
	{
		var library = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var sizes = new System.Collections.Concurrent.ConcurrentQueue<int>();
		var quick = SlowPages(0, 6000, sizes);
		var pages = new IndexPages([library], quick, "titles", pageSize: IndexPages.MinCarriedSize);

		Assert.True(await pages.ReadAsync(CancellationToken.None));
		Assert.Equal(6000, pages.Items().Count);
		Assert.Equal(IndexPages.PageSize, pages.PageSizeNow);
		Assert.Contains(IndexPages.PageSize, sizes);
	}

	// re-review (Major A): only a library's first page asks the server to count; one slow count cut every later page for good
	[Fact]
	public async Task ASlowCountedFirstPage_LeavesThePagesAfterItAsTheyWere()
	{
		var library = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var sizes = new System.Collections.Concurrent.ConcurrentQueue<int>();
		var counted = 0;
		var quick = SlowPages(0, 4200, sizes);
		IndexPages.Ask ask = async (lib, start, limit, total, token) =>
		{
			if (total && Interlocked.Increment(ref counted) == 1)
			{
				try
				{
					await Task.Delay(Timeout.Infinite, token); // the count takes longer than the page may
				}
				catch (OperationCanceledException)
				{
					return null; // as the app's client
				}
			}

			return await quick(lib, start, limit, total, token);
		};
		var pages = new IndexPages([library], ask, "titles") { PageTimeout = TimeSpan.FromMilliseconds(300), Pause = (_, _) => Task.CompletedTask };

		Assert.True(await pages.ReadAsync(CancellationToken.None));
		Assert.Equal(IndexPages.PageSize, pages.PageSizeNow);
		Assert.All(sizes.Where(x => x != IndexPages.FirstPageSize), size => Assert.Equal(IndexPages.PageSize, size));
	}

	// a server whose pages take `perTitle` ms a title, shared by the requests in flight, of `count` titles; null when the page's
	// time runs out, as the app's client answers
	private static IndexPages.Ask SlowPages(double perTitle, int count, System.Collections.Concurrent.ConcurrentQueue<int>? sizes = null) =>
		SlowPages(perTitle, _ => (0, count), sizes);

	// the same, of several libraries (each its number and how many titles it has); `ranOut` counts the pages whose time ran out
	private static IndexPages.Ask SlowPages(double perTitle, Func<BaseItemDto, (int Number, int Count)> library,
		System.Collections.Concurrent.ConcurrentQueue<int>? sizes = null, System.Runtime.CompilerServices.StrongBox<int>? ranOut = null)
	{
		var inFlight = 0;
		return async (lib, start, limit, total, token) =>
		{
			Interlocked.Increment(ref inFlight);
			sizes?.Enqueue(limit);
			var (number, count) = library(lib);
			try
			{
				var titles = Math.Clamp(count - start, 0, limit);
				var watch = System.Diagnostics.Stopwatch.StartNew();
				double served = 0, last = 0;
				while (served < perTitle * titles)
				{
					await Task.Delay(5, token);
					var now = watch.Elapsed.TotalMilliseconds;
					served += (now - last) / Math.Max(1, Volatile.Read(ref inFlight));
					last = now;
				}

				return new BaseItemDtoQueryResult
				{
					Items = [.. Enumerable.Range(start, titles).Select(i => new BaseItemDto { Id = Guid.Parse($"{i:D8}-{number:D4}-0171-0000-0000000000cc"), Type = BaseItemDto_Type.Movie })],
					TotalRecordCount = total ? count : null,
				};
			}
			catch (OperationCanceledException)
			{
				if (ranOut is not null)
				{
					Interlocked.Increment(ref ranOut.Value);
				}

				return null;
			}
			finally
			{
				Interlocked.Decrement(ref inFlight);
			}
		};
	}

	// check of 69002e14 (Major): a short page (a small library's only page, a big one's last) came back quick and counted
	// toward growing: three small libraries took the size from 500 back to 1,000, and the next big library's pages ran out
	[Fact]
	public async Task ShortPages_DoNotGrowThePagesBack()
	{
		// the owner's timing scaled down: 1,000 two at a time run out (500 ms of 400), 500 don't (250 ms)
		int[] counts = [4200, 300, 300, 300, 4200];
		var libraries = counts.Select((_, i) => new BaseItemDto { Id = Guid.Parse($"aaaaaaaa-{i:D4}-0000-0000-000000000171"), Name = $"Library {i}", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies }).ToList();
		var ranOut = new System.Runtime.CompilerServices.StrongBox<int>();
		var ask = SlowPages(0.25, lib => libraries.FindIndex(l => l.Id == lib.Id) is var n ? (n, counts[n]) : default, ranOut: ranOut);
		var pages = new IndexPages(libraries, ask, "people", pageSize: 500) { PageTimeout = TimeSpan.FromMilliseconds(400), Pause = (_, _) => Task.CompletedTask };

		Assert.True(await pages.ReadAsync(CancellationToken.None));
		Assert.Equal(counts.Sum(), pages.Items().Count);
		Assert.Equal(0, ranOut.Value);
		Assert.Equal(500, pages.PageSizeNow);
	}

	// check of 69002e14 (Minor 1): with m6's switch, GetAsync still read for the user who had gone: it cancelled the new
	// user's read and read a whole index with their sign-in, to throw it away
	[Fact]
	public async Task AnAskForAUserWhoIsNoLongerSignedIn_ReadsNothing()
	{
		var bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000171");
		var asks = 0;
		_client.SetupGet(x => x.UserId).Returns(() => Interlocked.Increment(ref asks) == 1 ? User : bob); // alice when it was asked
		using var index = Index();

		Assert.Null(await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(20)));
		await index.Settled;

		Assert.Equal(0, _pages);
		_client.Verify(x => x.GetUserViewsOrNull(), Times.Never());
	}

	// check of 69002e14 (Minor 2): the check kept from an index read without one was never renewed: after a titles read whose
	// own check didn't answer, the next check was compared with the older answer and read the titles again, nothing changed
	[Fact]
	public async Task AfterAChangesReadWhoseCheckDidNotAnswer_TheNextCheckComparesWithTheChange()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var answers = new Queue<string?>([null, "A", "B", null]); // the first read's, the start's, the change's, the change's read's
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync(() => answers.Count > 0 ? answers.Dequeue() : "B");
		await ReadAndSave(time); // its check didn't answer

		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		await index.GetAsync();
		await index.Checking; // "A": kept to compare with
		_messages.OnNext(new LibraryChangedMessage());
		time.Now += LibraryIndex.CheckEvery + TimeSpan.FromMinutes(1);
		await index.GetAsync();
		await index.Checking; // "B": a change, read (the titles), and that read's own check doesn't answer
		await Task.Delay(100);
		await index.Settled;
		Assert.Null(index.Current!.Check);
		var read = _pages;

		_messages.OnNext(new LibraryChangedMessage());
		time.Now += TimeSpan.FromHours(2); // past the hour
		await index.GetAsync();
		await index.Checking; // "B" again: nothing changed
		await Task.Delay(100);
		await index.Settled;
		Assert.Equal(read, _pages);
	}

	// BH-171-M1 (1.7.1 bug hunt): a refresh whose played-episodes request failed (a 500, a dropped connection, over 45 s) saved
	// an index with no plays: Unfinished series left Home and the Collections page until the next whole read
	[Fact]
	public async Task ARefreshWhosePlayedPassFails_KeepsThePlaysItHad()
	{
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var shows = new BaseItemDto { Id = ShowsId, Name = "Shows", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Tvshows };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies, shows]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool total, CancellationToken _) => library.Id == ShowsId
				? new BaseItemDtoQueryResult { Items = [], TotalRecordCount = total ? 0 : null }
				: new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null });
		var played = Enumerable.Range(0, 10).Select(i => new BaseItemDto
		{
			Id = Guid.Parse($"{i:D8}-0171-0000-0000-0000000000ee"),
			Type = BaseItemDto_Type.Episode,
			SeriesId = Guid.Parse($"{i:D8}-0171-0000-0000-0000000000bb"),
			UserData = new UserItemDataDto { Played = true, LastPlayedDate = new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero) },
		}).ToList();
		_client.Setup(x => x.GetPlayedEpisodesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. played.Skip(start).Take(limit)], TotalRecordCount = total ? played.Count : null });
		Assert.Equal(10, (await ReadAndSave()).SeriesPlays.Count);
		_client.Setup(x => x.GetPlayedEpisodesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDtoQueryResult?)null); // a 500, a dropped connection, over 45 s
		using (var index = Index())
		{
			await index.GetAsync();
			var fresh = await index.RefreshAsync(); // R
			await index.Settled;

			Assert.Equal(10, fresh!.SeriesPlays.Count);
			Assert.Equal(10, index.Current!.SeriesPlays.Count);
			Assert.True(index.Current.PlaysDue); // 1.7.3: and asked again within the hour
		}

		using var again = Index(); // and saved so
		Assert.Equal(10, (await again.GetAsync())!.SeriesPlays.Count);
	}

	// a Movies and a Shows library (the shows none of their own), and 10 shows' played episodes; `played` false: they don't come
	private System.Runtime.CompilerServices.StrongBox<int> MoviesAndShows(Func<bool> played)
	{
		var asked = new System.Runtime.CompilerServices.StrongBox<int>();
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(MoviesId, "Movies"), Library(ShowsId, "Shows", BaseItemDto_CollectionType.Tvshows)]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool total, CancellationToken _) =>
			{
				Interlocked.Increment(ref _pages);
				return library.Id == ShowsId
					? new BaseItemDtoQueryResult { Items = [], TotalRecordCount = total ? 0 : null }
					: new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null };
			});
		var episodes = Enumerable.Range(0, 10).Select(i => new BaseItemDto
		{
			Id = Guid.Parse($"{i:D8}-0173-0000-0000-0000000000ee"),
			Type = BaseItemDto_Type.Episode,
			SeriesId = Guid.Parse($"{i:D8}-0173-0000-0000-0000000000bb"),
			UserData = new UserItemDataDto { Played = true, LastPlayedDate = new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero) },
		}).ToList();
		_client.Setup(x => x.GetPlayedEpisodesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((int start, int limit, bool total, CancellationToken _) =>
			{
				Interlocked.Increment(ref asked.Value);
				return played() ? new BaseItemDtoQueryResult { Items = [.. episodes.Skip(start).Take(limit)], TotalRecordCount = total ? episodes.Count : null } : null;
			});
		return asked;
	}

	private static BaseItemDto Library(Guid id, string name, BaseItemDto_CollectionType type = BaseItemDto_CollectionType.Movies) =>
		new() { Id = id, Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };

	// 1.7.3 (BH-171-M1's other half): a first read whose played episodes didn't come kept none until the next whole read, a
	// day away or R: Unfinished series stayed off Home and the Collections page all that time
	[Fact]
	public async Task PlayedEpisodesThatDidNotCome_AreAskedAgainOnTheirOwn_AtMostOnceAnHour()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		var answers = false;
		var asked = MoviesAndShows(() => answers);
		var first = await ReadAndSave(time); // a 500, a dropped connection, over 45 s
		Assert.Empty(first.SeriesPlays);
		Assert.True(first.PlaysDue);
		await WaitForSaved(x => x.Contains("\"PlaysDue\":true", StringComparison.Ordinal));
		var (pages, plays) = (_pages, asked.Value);
		answers = true; // they would come now

		time.Now += TimeSpan.FromMinutes(20); // a start within the hour: not asked yet
		using (var soon = Index(time))
		{
			Assert.True((await soon.GetAsync())!.PlaysDue);
			await Task.Delay(100);
			await soon.Settled;
			Assert.Equal((pages, plays), (_pages, asked.Value));
		}

		time.Now += TimeSpan.FromHours(1); // a start after it: the played episodes alone, the titles kept
		using var later = Index(time);
		await later.GetAsync();
		await WaitForIndex(later, x => !x.PlaysDue);
		await later.Settled;
		Assert.Equal(10, later.Current!.SeriesPlays.Count);
		Assert.Equal(pages, _pages); // no title asked again
		Assert.True(asked.Value > plays);
		Assert.Equal(first.ReadAt, later.Current.ReadAt); // the day's read still comes at its time
		await WaitForSaved(x => x.Contains("\"PlaysDue\":false", StringComparison.Ordinal));
		using var again = Index(time);
		Assert.Equal(10, (await again.GetAsync())!.SeriesPlays.Count); // and saved so
	}

	// 1.7.3 (P0-2): 1.7.1's read kept a title the server lists in two libraries under the first one only (3,167 of 34,006 on
	// the server the owner uses): the other library's presets missed it. Now it is one entry, with both
	[Fact]
	public async Task ATitleInTwoLibraries_IsOneEntryWithBoth_AndStaysSoWhenSaved()
	{
		var kidsId = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000173");
		List<BaseItemDto> kids = [_films[5], _films[10], _films[2400], Film(3000)]; // three shared, one of its own
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(MoviesId, "Movies"), Library(kidsId, "Kids")]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool total, CancellationToken _) =>
			{
				var films = library.Id == kidsId ? kids : _films;
				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});

		var read = await ReadAndSave();

		Assert.Equal(2501, read.Entries.Count); // each title once
		var shared = read.Entries.Single(x => x.Name == "Film 00005");
		Assert.Equal(MoviesId, shared.LibraryId);
		Assert.Equal([MoviesId, kidsId], shared.LibraryIds);
		Assert.Equal(["Film 00005", "Film 00010", "Film 02400", "Film 03000"], read.Entries.Where(x => x.InLibrary(kidsId)).Select(x => x.Name).Order());
		Assert.Equal(2500, read.Entries.Count(x => x.InLibrary(MoviesId)));
		Assert.Equal([kidsId], read.Entries.Single(x => x.Name == "Film 03000").LibraryIds);
		using var restart = Index();
		var saved = await restart.GetAsync();
		Assert.Equal(4, saved!.Entries.Count(x => x.InLibrary(kidsId)));
		Assert.Equal([MoviesId, kidsId], saved.Entries.Single(x => x.Name == "Film 02400").LibraryIds);
	}

	// 1.7.3 (P0-2): 1.7.0 saved a title once per library it is in; 1.7.2 kept the first and dropped the rest, and with them the
	// title's other library
	[Fact]
	public async Task ASavedIndexWithATitleTwice_KeepsItOnce_InBothItsLibraries()
	{
		var kidsId = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000173");
		await ReadAndSave();
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		var entries = root["Entries"]!.AsArray();
		foreach (var entry in entries)
		{
			entry!.AsObject().Remove("LibraryIds"); // as 1.7.0 left it
		}

		var again = entries[7]!.DeepClone().AsObject();
		again["LibraryId"] = kidsId.ToString();
		entries.Add(again); // Film 00007, in Kids too
		root.Remove("Revision");
		await File.WriteAllTextAsync(file, root.ToJsonString());
		var release = new TaskCompletionSource();
		Pages(_ => release.Task); // its read again behind it (an older revision) waits
		var log = new CoreListLogger<LibraryIndex>();

		using var index = Index(log: log);
		try
		{
			var shown = await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(10));

			Assert.Equal(2500, shown!.Entries.Count);
			var twice = shown.Entries.Single(x => x.Name == "Film 00007");
			Assert.True(twice.InLibrary(MoviesId) && twice.InLibrary(kidsId));
			Assert.Equal([MoviesId, kidsId], twice.LibraryIds);
			var once = shown.Entries.Single(x => x.Name == "Film 00008"); // as 1.7.0 saved it: its one library
			Assert.Empty(once.LibraryIds);
			Assert.True(once.InLibrary(MoviesId));
			Assert.Single(shown.Entries, x => x.InLibrary(kidsId));
			Assert.Contains(log.Entries, x => x.Message == "Library index: the saved one had 1 repeated title; each is kept once");
		}
		finally
		{
			release.TrySetResult();
			await index.Settled;
		}
	}

	// 1.7.3: the saved index's revision 3, inside version 2 (1.7.2 skips what it doesn't know, so a downgrade still reads it).
	// An index saved by 1.7.2 (no revision: no LibraryIds, Critic or TagCount) shows at once and is read again behind it, at
	// most once an hour after a read this version began (fix round, BH-173-M1: 1.7.2's own note of its read doesn't hold it,
	// TheUpdateFrom172_KeepsAcclaimed_AndReadsTheIndexAgainAtOnce)
	[Fact]
	public async Task AnIndexOfAnOlderRevision_ShowsAtOnce_AndIsReadAgainBehindIt_NotWithinTheHour()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		var text = await File.ReadAllTextAsync(file);
		Assert.Contains("\"Version\":2", text);
		Assert.Contains("\"Revision\":3", text);
		await File.WriteAllTextAsync(file, text.Replace(",\"Revision\":3", "", StringComparison.Ordinal)); // as 1.7.2 left it
		var read = _pages;

		time.Now += TimeSpan.FromMinutes(10); // within the hour of its read: shown, not read yet
		using (var soon = Index(time))
		{
			Assert.Equal(2500, (await soon.GetAsync())!.Entries.Count);
			await Task.Delay(100);
			await soon.Settled;
			Assert.Equal(read, _pages);
		}

		time.Now += TimeSpan.FromHours(1);
		var release = new TaskCompletionSource();
		Pages(_ => release.Task); // the server is slow now
		using var later = Index(time);
		try
		{
			var shown = await later.GetAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal(2500, shown!.Entries.Count); // at once
		}
		finally
		{
			release.TrySetResult();
		}

		await Task.Delay(100);
		await later.Settled;
		Assert.True(_pages > read); // then read again behind it
		await WaitForSaved(x => x.Contains("\"Revision\":3", StringComparison.Ordinal));
	}

	// 1.7.3 fix round (BH-173-M1): the update from 1.7.2 within the hour of 1.7.2's last read. 1.7.2's index has no Critic or
	// TagCount (every title read as "nothing backs it": Home dropped the Acclaimed row it painted), and its note of the last
	// read no revision (the read again waited out 1.7.2's hour). Acclaimed comes from it, by the rating alone, and it is read
	// again at once; the test above holds for a read 1.7.3 itself began within the hour
	[Fact]
	public async Task TheUpdateFrom172_KeepsAcclaimed_AndReadsTheIndexAgainAtOnce()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		foreach (var film in _films.Where((_, i) => i % 2 == 1))
		{
			film.Tags = ["one", "two", "three", "four", "five", "six"]; // the server keeps TMDb's keywords as tags
		}

		await ReadAndSave(time);
		var folder = Path.Combine(_folder, "LibraryIndex");
		var file = Directory.GetFiles(folder, "*.json").Single();
		var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		root.Remove("Revision");
		foreach (var entry in root["Entries"]!.AsArray())
		{
			entry!.AsObject().Remove("TagCount");
			entry.AsObject().Remove("Critic");
			entry.AsObject().Remove("LibraryIds");
		}

		await File.WriteAllTextAsync(file, root.ToJsonString()); // as 1.7.2 left it
		var note = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Single();
		var reads = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(note))!.AsObject();
		Assert.Equal(3, (int?)reads["Revision"]);
		reads.Remove("Revision");
		await File.WriteAllTextAsync(note, reads.ToJsonString()); // 1.7.2's note of its read, ten minutes before the update
		var read = _pages;
		time.Now += TimeSpan.FromMinutes(10);
		var release = new TaskCompletionSource();
		Pages(_ => release.Task); // the read again, held

		using var index = Index(time);
		try
		{
			var shown = await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.False(shown!.BackingKnown);
			Assert.All(shown.Entries, x => Assert.Equal(0, x.TagCount));
			var acclaimed = Assert.Single(SmartCollectionCatalog.BuildAll(shown, time.Now), x => x.Kind == SmartCollectionKind.HighlyRated);
			Assert.Equal(FluentFin.Core.SmartCollections.Builders.HighlyRatedBuilder.Most, acclaimed.ItemIds.Count);
			for (var i = 0; i < 200 && _pages == read; i++)
			{
				await Task.Delay(10);
			}

			Assert.True(_pages > read, "read again at once, within 1.7.2's hour");
		}
		finally
		{
			release.TrySetResult();
		}

		await index.Settled;
		await WaitForSaved(x => x.Contains("\"Revision\":3", StringComparison.Ordinal) && x.Contains("\"TagCount\":6", StringComparison.Ordinal));
		Assert.True(index.Current!.BackingKnown);
		Assert.Single(SmartCollectionCatalog.BuildAll(index.Current, time.Now), x => x.Kind == SmartCollectionKind.HighlyRated);
	}

	// the fix round's re-review: the note of a read was stamped with 1.7.3's revision when the read began, so an upgrade read
	// that failed (or was closed part way) held 1.7.2's index for the hour, across restarts. It is stamped when a read is in
	[Fact]
	public async Task AnUpgradeReadThatFails_DoesntHoldTheNextStart_OneThatIsInDoes()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var folder = Path.Combine(_folder, "LibraryIndex");
		var file = Directory.GetFiles(folder, "*.json").Single();
		await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace(",\"Revision\":3", "", StringComparison.Ordinal)); // 1.7.2's index
		var note = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Single();
		await File.WriteAllTextAsync(note, (await File.ReadAllTextAsync(note)).Replace(",\"Revision\":3", "", StringComparison.Ordinal)); // and its note
		Assert.DoesNotContain("Revision", await File.ReadAllTextAsync(note), StringComparison.Ordinal);

		// the first start after the update: the read begins at once, and the server goes away part way
		time.Now += TimeSpan.FromMinutes(10);
		Pages(start => start == 0 ? Task.CompletedTask : Task.FromException(new HttpRequestException("the server went away")));
		var before = _pages;
		using (var first = Index(time))
		{
			Assert.NotNull(await first.GetAsync());
			for (var i = 0; i < 200 && _pages == before; i++)
			{
				await Task.Delay(10);
			}

			await first.Settled;
		}

		Assert.True(_pages > before, "the upgrade read began");
		Assert.DoesNotContain("\"Revision\":3", await File.ReadAllTextAsync(file), StringComparison.Ordinal); // it never came in

		// the next start, five minutes later: read again at once (the note's read never finished)
		time.Now += TimeSpan.FromMinutes(5);
		Pages(_ => Task.CompletedTask);
		var failed = _pages;
		using (var again = Index(time))
		{
			Assert.NotNull(await again.GetAsync());
			for (var i = 0; i < 200 && _pages == failed; i++)
			{
				await Task.Delay(10);
			}

			Assert.True(_pages > failed, "read again at once after a failed upgrade read");
			await again.Settled;
		}

		await WaitForSaved(x => x.Contains("\"Revision\":3", StringComparison.Ordinal));
		var stamped = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(note))!;
		Assert.Equal(3, (int?)stamped["Revision"]); // this read came in: stamped
	}

	// 1.7.3 (P0-4): the critics' rating and the tag count before the allowlist (the confidence of a title's score) are kept
	// in the saved index
	[Fact]
	public async Task TheCriticsRatingAndTheTagCount_SurviveASaveAndALoad()
	{
		_films[4].CriticRating = 87;
		_films[4].Tags = ["heist", "Hand typed", "another", "and one more"];

		await ReadAndSave();
		using var restart = Index();
		var saved = await restart.GetAsync();

		var film = saved!.Entries.Single(x => x.Name == "Film 00004");
		Assert.Equal(87f, film.Critic);
		Assert.Equal(4, film.TagCount);
		Assert.Equal(["heist"], film.Tags); // the allowlist's only
		var none = saved.Entries.Single(x => x.Name == "Film 00001");
		Assert.Null(none.Critic);
		Assert.Equal(0, none.TagCount);
	}

	// 1.7.3 (BH-171-q1): the reading line had no total until every library's first page was in: 84 s on a server of 23
	// libraries at 3-10 s a request, "4,600 films" the while. Now the server's check's count, until then
	[Fact]
	public async Task AFirstRead_SaysAboutHowMany_BeforeEveryLibrarysCountIsIn()
	{
		var kidsId = Guid.Parse("aaaaaaaa-3333-0000-0000-000000000173");
		List<BaseItemDto> kids = [.. Enumerable.Range(3000, 100).Select(Film)];
		_check = "2600:newest"; // the check counts every film once
		var kidsFirst = new TaskCompletionSource();
		var release = new TaskCompletionSource();
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([Library(MoviesId, "Movies"), Library(kidsId, "Kids")]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto library, int start, int limit, bool total, CancellationToken token) =>
			{
				var films = library.Id == kidsId ? kids : _films;
				await (library.Id == kidsId ? kidsFirst.Task : start == 0 ? Task.CompletedTask : release.Task).WaitAsync(token);
				return new BaseItemDtoQueryResult { Items = [.. films.Skip(start).Take(limit)], TotalRecordCount = total ? films.Count : null };
			});
		using var index = Index();
		var said = new System.Collections.Concurrent.ConcurrentQueue<IndexProgress>();
		index.Progress += (_, progress) => said.Enqueue(progress);
		using var ui = new UiThread();
		var vm = Page(index);
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await WaitFor(ui, () => vm.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie));

			// what the line says, with the Kids library's first page (and its count) still to come
			// 1.7.5 (fx1): "titles" while the total is only "about" (the check counts shows, and libraries the index skips, too)
			await WaitFor(ui, () => vm.ReadingText.Contains(" of about ", StringComparison.Ordinal));
			Assert.Equal("Nixie is reading your libraries to make its collections: 200 of about 2,600 titles", await ui.Invoke(() => vm.ReadingText));
			Assert.True(await ui.Invoke(() => vm.IsReadingIndex));

			kidsFirst.SetResult(); // every library's count in: the total is theirs
			await WaitFor(ui, () => vm.ReadingText.EndsWith("of 2,600 films", StringComparison.Ordinal) && !vm.ReadingText.Contains("about", StringComparison.Ordinal));
			Assert.Equal("Nixie is reading your libraries to make its collections: 300 of 2,600 films", await ui.Invoke(() => vm.ReadingText));
		}
		finally
		{
			kidsFirst.TrySetResult();
			await Settle(release, vm, index, ui);
		}

		Assert.True(said.First().About);
		Assert.False(said.Last().About);
		Assert.Equal(2600, index.Current!.Entries.Count);
	}

	// BH-171-m5: a read whose check didn't answer saved no check, and the next start's check always "differed": every title
	// read again at every start
	[Fact]
	public async Task AReadWhoseCheckDidNotAnswer_ThenARestart_ReadsNothing()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		_check = null; // the check didn't answer while the index was read
		Assert.Null((await ReadAndSave(time)).Check);
		var read = (_pages, _peoplePages);
		_check = "2500:newest"; // it answers now; nothing changed

		time.Now += TimeSpan.FromHours(2);
		using var index = Index(time);
		await index.GetAsync();
		await index.Checking;
		await index.Settled;
		Assert.Equal(read, (_pages, _peoplePages));
		_client.Verify(x => x.GetIndexCheck(It.IsAny<CancellationToken>()), Times.Exactly(2)); // the read's, and the start's

		// that answer is what the next check is compared with: a change after it is read
		_check = "2501:another";
		_messages.OnNext(new LibraryChangedMessage());
		time.Now += LibraryIndex.CheckEvery + TimeSpan.FromMinutes(1);
		await index.GetAsync();
		await index.Checking;
		await WaitForIndex(index, x => x.Check == "2501:another");
	}

	// BH-171-m5: the check had no time of its own (it waited the client's 100 s) and a sign-out didn't end it
	[Fact]
	public async Task ACheckThatDoesNotAnswer_EndsAtItsLimit_AndAtSignOut()
	{
		await ReadAndSave();
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
		{
			try
			{
				await Task.Delay(Timeout.Infinite, token);
			}
			catch (OperationCanceledException)
			{
			}

			return (string?)null; // as the app's client, when its token ends
		});
		var folder = Path.Combine(_folder, "LibraryIndex");
		using (var limited = new LibraryIndex(_client.Object, _homeCache, folder, _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System) { CheckTimeout = TimeSpan.FromMilliseconds(200) })
		{
			await limited.GetAsync();
			await limited.Checking.WaitAsync(TimeSpan.FromSeconds(10)); // over at its limit
		}

		using var index = new LibraryIndex(_client.Object, _homeCache, folder, _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System) { CheckTimeout = TimeSpan.FromMinutes(10) };
		await index.GetAsync();
		var checking = index.Checking;
		Assert.False(checking.IsCompleted);
		await _homeCache.Forget("http://server", User); // sign-out
		await checking.WaitAsync(TimeSpan.FromSeconds(10));
	}

	// BH-171-m7: a late ask after a sign-out (Home's late rows, a library's presets) started a read with the ended sign-in: five
	// requests refused, and the sign-in watch then said the server no longer took a sign-in the user had ended
	[Fact]
	public async Task AfterASignOut_ALateAsk_AsksTheServerNothing_UntilTheNextSignIn()
	{
		using var index = Index();
		await index.GetAsync();
		await index.Settled;
		var asked = (_pages, _peoplePages);

		await _homeCache.Forget("http://server", User); // sign-out
		Assert.Null(await index.GetAsync());
		Assert.Null(await index.SavedAsync());
		Assert.Null(await index.RefreshAsync());
		await Task.Delay(100);
		await index.Settled;
		Assert.Equal(asked, (_pages, _peoplePages));
		_client.Verify(x => x.GetUserViewsOrNull(), Times.Once()); // the first read's only

		_homeCache.SignedIn("http://server", User, null, null); // the next sign-in
		Assert.NotNull(await index.GetAsync());
	}

	// BH-171-m6: another user signing in while the saved index was read from disk got it raised as theirs (their presets made
	// of it, stamped with their key)
	[Fact]
	public async Task AnotherUserSigningInWhileTheSavedIndexIsRead_GetsNothingOfIt()
	{
		await ReadAndSave();
		var bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000171");
		var asks = 0;
		_client.SetupGet(x => x.UserId).Returns(() => Interlocked.Increment(ref asks) == 1 ? User : bob); // alice when the disk read begins
		using var index = Index();
		var raised = new List<IndexSnapshot>();
		index.Changed += (_, snapshot) => raised.Add(snapshot);

		Assert.Null(await index.SavedAsync());

		Assert.Empty(raised);
		Assert.Null(index.Current);
	}

	// BH-171-q3: a refused page (401) was asked again like a timeout: three tries, with pauses, per page in flight
	[Fact]
	public async Task ARefusedPage_EndsTheRead_WithNoOtherTry()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/UserViews", 200, $$"""{"Items":[{"Id":"{{MoviesId}}","Name":"Movies","Type":"CollectionFolder","CollectionType":"movies"}],"TotalRecordCount":1}""");
		server.Respond("/Items", 401, "");
		server.Respond("/Users/Me", 200, """{"Id":"aaaaaaaa-0000-0000-0000-000000001710","Name":"alice"}"""); // the sign-in itself is good
		var clientLog = new CoreListLogger<JellyfinClient>();
		var client = new JellyfinClient(clientLog, Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-index-171-refused-0123",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001710"), Name = "alice" },
		});

		using var index = new LibraryIndex(client, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		Assert.Null(await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(20)));
		await index.Settled;

		var pages = server.ApiRequests.Where(r => r.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase) && r.Query.GetValues("parentId") is not null).ToList();
		Assert.Single(pages);
		// check of 69002e14 (Minor 3): the log says where in the pass, not which library (rule 10)
		var refused = Assert.Single(clientLog.Entries, e => e.Message.Contains("refused (HTTP 401)", StringComparison.Ordinal));
		Assert.DoesNotContain(MoviesId.ToString(), refused.Message, StringComparison.OrdinalIgnoreCase);
	}

	// BH-171-m1: a first read that stopped part way left the rows of its pages so far as if they were all, with no line
	[Fact]
	public async Task AFirstReadThatStopped_SaysSo_UnderTheRowsItMade()
	{
		Pages(start => start == 0 ? Task.CompletedTask : Task.FromException(new HttpRequestException("the server went away")));
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index);
		await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
		await vm.Reading.WaitAsync(TimeSpan.FromSeconds(20));
		await index.Settled;

		// 1.7.3: the read's end doesn't wait for its pages' rows to be shown (a show still pending, a cold first build);
		// it failed run alone at 1.7.2's 2406079b too, so the rows are waited for
		await WaitFor(ui, () => vm.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie));
		Assert.Equal((false, true, true, CollectionsViewModel.ReadStoppedText),
			await ui.Invoke(() => (vm.IsReadingIndex, vm.IsReadStopped, vm.ShowsReadingLine, vm.ReadingText)));
		// what the page draws: the line by ShowsReadingLine, its ring only while reading
		var xaml = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Views", "CollectionsPage.xaml"));
		var panel = xaml[xaml.IndexOf("x:Name=\"ReadingPanel\"", StringComparison.Ordinal)..];
		panel = panel[..panel.IndexOf("</StackPanel>", StringComparison.Ordinal)];
		Assert.Contains("Visibility=\"{x:Bind ViewModel.ShowsReadingLine, Mode=OneWay}\"", panel);
		Assert.Contains("IsActive=\"{x:Bind ViewModel.IsReadingIndex, Mode=OneWay}\"", panel);
		Assert.Contains("Text=\"{x:Bind ViewModel.ReadingText, Mode=OneWay}\"", panel);
		await ui.Run(vm.OnNavigatedFrom);
	}

	// BH-171-q2 (rule 10): a first read logged each page, but not how long the page took to its first Made by Nixie rows
	[Fact]
	public async Task AFirstRead_LogsTheTimeToItsFirstMadeByNixieRows()
	{
		var pageLog = new CoreListLogger<CollectionsViewModel>();
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index, pageLog);
		await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
		await vm.Reading.WaitAsync(TimeSpan.FromSeconds(20));
		await index.Settled;
		await ui.Invoke(() => 0);

		var line = Assert.Single(pageLog.Entries.Select(e => e.Message), l => l.StartsWith("Collections: first Made by Nixie rows in ", StringComparison.Ordinal));
		Assert.Matches(@"^Collections: first Made by Nixie rows in \d+ ms \(first read(, its pages so far)?\)$", line);
		await ui.Run(vm.OnNavigatedFrom);
	}

	// re-review (Major B): a library change's titles read dated the index anew, so the day's whole read never came on a server
	// whose library changes every day
	[Fact]
	public async Task ALibraryChangesRead_KeepsTheDaysDate_SoTheDaysReadStillComes()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		_check = "2501:another";
		_films.Add(Film(2500));

		time.Now += TimeSpan.FromHours(23); // a change the check confirms: the titles only
		using (var change = Index(time))
		{
			await change.GetAsync();
			await WaitForIndex(change, x => x.Entries.Count == 2501);
			await change.Settled;
		}

		var pages = _pages;
		time.Now += TimeSpan.FromHours(2); // 25 h after the day's read
		using var later = Index(time);
		await later.GetAsync();
		await Task.Delay(100);
		await later.Settled;
		Assert.True(_pages > pages);
		Assert.Equal(time.Now, later.Current!.ReadAt);
	}

	// re-review (Minor): a note of the last read dated after now (the clock moved back) held every automatic read until the
	// clock passed it
	[Fact]
	public async Task ANoteOfTheLastReadDatedAfterNow_HoldsNoRead()
	{
		var time = new ManualTime(new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero));
		await ReadAndSave(time);
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"Version\":2", "\"Version\":1", StringComparison.Ordinal)); // due
		var read = _pages;

		time.Now -= TimeSpan.FromHours(5); // the clock moved back
		using var index = Index(time);
		await index.GetAsync();
		await Task.Delay(100);
		await index.Settled;
		Assert.True(_pages > read);
	}

	// re-review (Minor): two asks at a start (Home and a library's presets): the second went on without the note of the last
	// read while the first was still reading it, and began a read the note would have held
	[Fact]
	public async Task TwoAsksAtAStart_BothWaitForTheNoteOfTheLastRead()
	{
		await ReadAndSave(); // read now: the note says so
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("\"Version\":2", "\"Version\":1", StringComparison.Ordinal)); // due, but for the note
		var read = _pages;
		var note = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System)
		{
			ReadingNote = () => note.Task, // the note slow to read: the second ask gets there while the first reads it
		};

		var first = index.SavedAsync();
		var second = index.SavedAsync();
		await Task.Delay(500); // the second went on without the note, and began a read, by now
		note.SetResult();
		await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
		await Task.Delay(100);
		await index.Settled;
		Assert.Equal(read, _pages);
	}

	private static async Task WaitForIndex(LibraryIndex index, Func<IndexSnapshot, bool> condition)
	{
		for (var i = 0; i < 400 && !(index.Current is { } now && condition(now)); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(index.Current is { } current && condition(current), "the index came in");
	}

	// the saved index file, once it says so
	private async Task WaitForSaved(Func<string, bool> condition)
	{
		var folder = Path.Combine(_folder, "LibraryIndex");
		string Text()
		{
			try
			{
				return Directory.Exists(folder) && Directory.GetFiles(folder, "*.json").SingleOrDefault() is { } file ? File.ReadAllText(file) : "";
			}
			catch (IOException)
			{
				return "";
			}
		}

		for (var i = 0; i < 400 && !condition(Text()); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(condition(Text()), "the index was saved");
	}

	[Fact]
	public async Task OutsideListsThatArrive_RematchTheIndex_AndNeverReadIt()
	{
		await ReadAndSave();
		var read = (_pages, _peoplePages);
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index);
		await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
		Assert.False(await ui.Invoke(() => vm.Rows.SelectMany(r => r.Tiles).Any(t => t.Title == "Aardvark")));

		// TMDb's franchise names come in: the franchise is named again, from the index the page has
		Core.Outside.OutsideListsSnapshot.Shared = Core.Outside.OutsideListsSnapshot.Empty with { FranchiseNames = new Dictionary<string, string> { ["0"] = "Aardvark Collection" } };
		await WaitFor(ui, () => vm.Rows.SelectMany(r => r.Tiles).Any(t => t.Title == "Aardvark"));
		await Task.Delay(100);
		await index.Settled;

		Assert.Equal(read, (_pages, _peoplePages));
		await ui.Run(vm.OnNavigatedFrom);
	}

	[Fact]
	public async Task DuringAFirstRead_OutsideListsRematchThePagesSoFar_WithoutAskingForMore()
	{
		var release = new TaskCompletionSource();
		Pages(start => start == 0 ? Task.CompletedTask : release.Task);
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index);
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await WaitFor(ui, () => vm.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie));
			var asked = _pages;

			Core.Outside.OutsideListsSnapshot.Shared = Core.Outside.OutsideListsSnapshot.Empty with { FranchiseNames = new Dictionary<string, string> { ["0"] = "Aardvark Collection" } };
			await WaitFor(ui, () => vm.Rows.SelectMany(r => r.Tiles).Any(t => t.Title == "Aardvark"));

			Assert.Equal(asked, _pages); // made again from what the page had: nothing more asked for
			Assert.True(await ui.Invoke(() => vm.IsReadingIndex));
		}
		finally
		{
			await Settle(release, vm, index, ui);
		}
	}

	[Fact]
	public async Task AFirstRead_ShowsItsRowsAsItsPagesCome_AndSaysHowFarItHasGot()
	{
		var release = new TaskCompletionSource();
		Pages(start => start == 0 ? Task.CompletedTask : release.Task); // the first page, then the server is slow
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index);
		int early;
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await WaitFor(ui, () => vm.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie));

			// Made by Nixie from the first 200 films, the reading line still there, saying how far it has got
			Assert.True(await ui.Invoke(() => vm.State.IsLoaded));
			Assert.True(await ui.Invoke(() => vm.IsReadingIndex));
			Assert.Equal("Nixie is reading your libraries to make its collections: 200 of 2,500 films", await ui.Invoke(() => vm.ReadingText));
			early = await ui.Invoke(() => vm.MadeByNixieCount);
			Assert.True(early > 0);
			Assert.Null(index.Current); // nothing whole yet: nothing saved, no presets made of it
			Assert.Empty(Directory.Exists(Path.Combine(_folder, "LibraryIndex")) ? Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json") : []);
		}
		catch
		{
			await Settle(release, vm, index, ui);
			throw;
		}

		release.TrySetResult();
		await vm.Reading;
		await index.Settled;
		await WaitFor(ui, () => !vm.IsReadingIndex && vm.MadeByNixieCount > early);
		Assert.Equal(2500, index.Current!.Entries.Count);
		await ui.Run(vm.OnNavigatedFrom);
	}

	[Fact]
	public async Task LeftAndOpenedAgainDuringAFirstRead_ThePageShowsItsRowsSoFarAtOnce()
	{
		var release = new TaskCompletionSource();
		Pages(start => start == 0 ? Task.CompletedTask : release.Task);
		using var index = Index();
		using var ui = new UiThread();
		var first = Page(index);
		var again = Page(index);
		try
		{
			await ui.Run(() => first.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await WaitFor(ui, () => first.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie));
			await ui.Run(first.OnNavigatedFrom);

			await ui.Run(() => again.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));

			// at once: the rows so far and how far the read has got, not a wait for its next pages
			Assert.True(await ui.Invoke(() => again.Rows.Any(r => r.Section == CollectionsSection.MadeByNixie)));
			Assert.True(await ui.Invoke(() => again.IsReadingIndex));
			Assert.Equal("Nixie is reading your libraries to make its collections: 200 of 2,500 films", await ui.Invoke(() => again.ReadingText));
		}
		finally
		{
			await Settle(release, again, index, ui, first);
		}
	}

	// review M1 (1.7.1): a row that stayed while a first read's pages came kept its tiles as the first page made them: with
	// 2,500 films "Highly rated, not watched" said 80 films through the whole read, in its count and when opened.
	// 1.7.3 (P0-4): Picks' first tile is "Acclaimed, not seen yet", 20 titles early and after, so the Keywords row's
	// "Heists" (a film in four, no cap) is the tile that grows
	[Fact]
	public async Task AfterAFirstRead_EveryTileHoldsTheWholeIndexsCollection_InTheRowsThatStayed()
	{
		var release = new TaskCompletionSource();
		Pages(start => start == 0 ? Task.CompletedTask : release.Task);
		using var index = Index();
		using var ui = new UiThread();
		var vm = Page(index);
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await WaitFor(ui, () => vm.Rows.Any(r => r.Title == "Keywords"));
			var keywords = await ui.Invoke(() => vm.Rows.Single(r => r.Title == "Keywords"));
			var tile = await ui.Invoke(() => keywords.Tiles.Single(t => t.Nixie?.Title == "Heists"));
			var early = (tile.Nixie!, tile.Detail);

			release.SetResult(); // every title
			await vm.Reading.WaitAsync(TimeSpan.FromSeconds(20));

			Assert.Same(keywords, await ui.Invoke(() => vm.Rows.Single(r => r.Title == "Keywords"))); // the row stayed (the keyboard with it)
			Assert.True(await ui.Invoke(() => tile.Nixie!.ItemIds.Count) > early.Item1.ItemIds.Count);
			Assert.NotEqual(early.Detail, await ui.Invoke(() => tile.Detail));
			await TilesAreOf(ui, vm, index.Current!);
		}
		finally
		{
			await Settle(release, vm, index, ui);
		}
	}

	// every Nixie tile on the page holds the collection this index makes: its titles, its count, its spoken name
	private static async Task TilesAreOf(UiThread ui, CollectionsViewModel vm, IndexSnapshot whole)
	{
		var made = (await vm.MadeByNixie(whole)).ToDictionary(x => x.Id);
		var tiles = await ui.Invoke(() => vm.Rows.SelectMany(r => r.Tiles).Where(t => t.IsNixie).Select(t => (t.Nixie!, t.Detail, t.AutomationName)).ToList());
		Assert.NotEmpty(tiles);
		foreach (var (nixie, detail, spoken) in tiles)
		{
			var expected = made[nixie.Id];
			Assert.Equal(expected.ItemIds.Order(), nixie.ItemIds.Order()); // 1.8: the same titles (the lineup turns or ranks This week's)
			Assert.Equal((expected.CountText, expected.AutomationName), (detail, spoken));
		}
	}

	[Fact]
	public async Task TheTitles_ComeTwoPagesAtATime_TheCountWithTheFirstOnly_AndEachTitleOnce()
	{
		var counted = 0;
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool total, CancellationToken token) =>
			{
				lock (_lock)
				{
					_inFlight++;
					_mostAtOnce = Math.Max(_mostAtOnce, _inFlight);
				}

				if (total)
				{
					Interlocked.Increment(ref counted);
				}

				await Task.Delay(30, token);
				lock (_lock)
				{
					_inFlight--;
				}

				Assert.True(limit <= IndexPages.PageSize);
				return new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null };
			});

		var index = await LibraryIndex.ReadIndex(_client.Object, TimeProvider.System, CancellationToken.None);

		Assert.Equal(2, _mostAtOnce); // 1.7.0: one at a time
		Assert.Equal(1, counted);
		Assert.Equal(2500, index!.Entries.Count);
		Assert.Equal(2500, index.Entries.Select(x => x.Id).Distinct().Count());
		Assert.Equal(_films.Select(x => x.Id!.Value).ToList(), index.Entries.Select(x => x.Id).ToList()); // in the server's order
	}

	[Fact]
	public async Task APageThatKeepsFailing_StopsTheRead_AndTheIndexInMemoryStays()
	{
		var saved = await ReadAndSave();
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => start >= 1200 ? null : new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null });
		using var index = Index();
		var shown = await index.GetAsync();

		Assert.Null(await index.RefreshAsync()); // R: the read stops
		await index.Settled;
		Assert.Equal(saved.Entries.Count, index.Current!.Entries.Count); // what was shown stays
		Assert.Same(shown, index.Current);
	}

	[Fact]
	public async Task A16Index_ShowsAtOnce_AndIsReadAgainBehindIt()
	{
		await ReadAndSave();
		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		var text = await File.ReadAllTextAsync(file);
		await File.WriteAllTextAsync(file, text.Replace("\"Version\":2", "\"Version\":1", StringComparison.Ordinal)); // as 1.6 left it
		Directory.Delete(Path.Combine(_folder, "LibraryIndex", "Reads"), recursive: true); // 1.6 kept no note of its reads
		var read = _pages;
		var release = new TaskCompletionSource();
		Pages(_ => release.Task); // the server is slow now

		using var index = Index();
		try
		{
			var shown = await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal(2500, shown!.Entries.Count); // 1.7.0 threw it away and made the user wait for a whole read
		}
		finally
		{
			release.TrySetResult();
		}

		await Task.Delay(100);
		await index.Settled;
		Assert.True(_pages > read); // read again behind it
		Assert.NotNull(index.Current!.Check);
	}

	[Fact]
	public async Task ThePeoplePass_IsLean_AndTheCheckIsOneTitle_OnTheWire()
	{
		using var server = new FakeJellyfin();
		server.Start();
		server.Respond("/Items", 200, """{"Items":[],"TotalRecordCount":0}""");
		var client = new JellyfinClient(new CoreListLogger<JellyfinClient>(), Mock.Of<IObserver<IOutboundWebSocketMessage>>(), Mock.Of<IJumpListService>(), Mock.Of<IDeviceProfileFactory>(), Mock.Of<ISettings>(), TestHomeCache.Unused());
		await client.Initialize(server.BaseUrl, new AuthenticationResult
		{
			AccessToken = "fake-token-index-171-0123456789",
			ServerId = "fake-server",
			User = new UserDto { Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000001710"), Name = "alice" },
		});
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };

		await client.GetLibraryIndexPage(library, 0, 200, true, CancellationToken.None);
		await client.GetLibraryPeoplePage(library, 200, 1000, false, CancellationToken.None);
		await client.GetIndexCheck(CancellationToken.None);

		var pages = server.ApiRequests.Where(r => r.Path.Equals("/Items", StringComparison.OrdinalIgnoreCase)).ToList();
		Assert.Equal(3, pages.Count);
		string? Q(FakeRequest r, string name) => r.Query.GetValues(name)?.FirstOrDefault();
		// the titles: the poster's tag (the collections' mosaics), the count with the first page
		Assert.Equal("Primary", Q(pages[0], "enableImageTypes"));
		Assert.Equal("true", Q(pages[0], "enableTotalRecordCount"), ignoreCase: true);
		// the people: no images, no user data, no count
		Assert.Equal("People", Q(pages[1], "fields"));
		Assert.Equal("false", Q(pages[1], "enableImages"), ignoreCase: true);
		Assert.Equal("false", Q(pages[1], "enableUserData"), ignoreCase: true);
		Assert.Null(Q(pages[1], "enableImageTypes"));
		Assert.Equal("false", Q(pages[1], "enableTotalRecordCount"), ignoreCase: true);
		// the check: one title, the count, nothing else
		Assert.Equal("1", Q(pages[2], "limit"));
		Assert.Equal("true", Q(pages[2], "enableTotalRecordCount"), ignoreCase: true);
		Assert.Null(Q(pages[2], "fields"));
		Assert.Equal("false", Q(pages[2], "enableImages"), ignoreCase: true);
	}

	[Fact]
	public async Task TheLog_SaysHowLongEachPartTook_WithNoTitles()
	{
		var log = new CoreListLogger<LibraryIndex>();
		using (var first = Index(log: log))
		{
			await first.GetAsync();
			await first.Settled;
		}

		var pageLog = new CoreListLogger<CollectionsViewModel>();
		using var again = Index(log: log);
		using var ui = new UiThread();
		var vm = Page(again, pageLog);
		await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));

		var lines = log.Entries.Select(e => e.Message).Concat(pageLog.Entries.Select(e => e.Message)).ToList();
		Assert.Contains(lines, l => l.StartsWith("Library index: titles page of library 1 from 0: 200 titles in ", StringComparison.Ordinal) && l.EndsWith(" ms", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("Library index: titles pass done: 2500 titles in 4 requests (2 at a time)", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("Library index: 2500 titles made into the index in ", StringComparison.Ordinal) && l.EndsWith(" ms (0 in more than one library)", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("Library index: 2500 titles in 1 libraries, played episodes in, ", StringComparison.Ordinal) && l.EndsWith(" s", StringComparison.Ordinal));
		// 1.8 (P1-2): the people pass, after the titles, in the first read: counts and durations
		Assert.Contains(lines, l => l.StartsWith("Library index: people pass done: 2500 titles in ", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("Library index: the people are in (every library's): 2500 titles with people, ", StringComparison.Ordinal) && l.EndsWith(" s", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("Library index: the saved one (2500 titles, read ", StringComparison.Ordinal) && l.EndsWith(" ms from this PC", StringComparison.Ordinal));
		Assert.Contains(lines, l => l.StartsWith("Collections: ", StringComparison.Ordinal) && l.Contains(" ms (Made by Nixie from the saved index)", StringComparison.Ordinal));
		Assert.DoesNotContain(lines, l => l.Contains("Film 0", StringComparison.Ordinal) || l.Contains("Movies", StringComparison.Ordinal) || l.Contains("Director", StringComparison.Ordinal));
		await ui.Run(vm.OnNavigatedFrom);
	}

	// the server let go and the page left, before its UI thread ends (a test that failed midway leaves nothing posting to it)
	private static async Task Settle(TaskCompletionSource release, CollectionsViewModel vm, LibraryIndex index, UiThread ui, params CollectionsViewModel[] others)
	{
		release.TrySetResult();
		try
		{
			foreach (var page in others.Append(vm))
			{
				await page.Reading.WaitAsync(TimeSpan.FromSeconds(20));
			}

			await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
			await ui.Run(vm.OnNavigatedFrom).WaitAsync(TimeSpan.FromSeconds(5));
			await ui.Invoke(() => 0).WaitAsync(TimeSpan.FromSeconds(5));
		}
		catch (TimeoutException)
		{
		}
	}

	// a condition on the page, read on its UI thread (the app's pages change on one thread only)
	private static async Task WaitFor(UiThread ui, Func<bool> condition)
	{
		for (var i = 0; i < 400 && !await ui.Invoke(condition); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(await ui.Invoke(condition));
	}
}

/// <summary>Nixie 1.7.1: a test's folder deleted, waiting a moment for a save still running behind it (a read's people pass).</summary>
internal static class TestFolder
{
	public static void Delete(string folder)
	{
		for (var tries = 0; Directory.Exists(folder); tries++)
		{
			try
			{
				Directory.Delete(folder, recursive: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && tries < 40)
			{
				Thread.Sleep(50);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				return; // a temporary folder left: harmless
			}
		}
	}
}
