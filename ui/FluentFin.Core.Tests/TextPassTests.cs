using System.Collections.Concurrent;
using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, step 1b, stream text): the summaries' pass (<see cref="TextPass"/>) behind the library index. It
/// begins after Home's first Made by Nixie row and after the index's own read, reads only the kinds the word lists are for
/// (shows), every library once and then only new titles by id; a title with no summary is unknown and asked again a week
/// later; standby pauses it; the categories off stop it and delete its matches; a sign-out forgets them; and the log never
/// names a title or quotes a summary.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class TextPassTests : IDisposable
{
	private const string Url = "http://server";
	private static readonly Guid MoviesId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000190");
	private static readonly Guid ShowsId = Guid.Parse("aaaaaaaa-2222-0000-0000-000000000190");
	private static readonly Guid User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000190");

	// a word no title, catalogue or log line has but every summary: the log must never have it
	private const string Marker = "quillfeather";

	private static readonly MicroGenreCatalogue Catalogue = new(
	[
		new MicroGenre("tv-workplace", "Workplace TV Comedies", IndexKind.Series, "sitcom", null, new MicroGenreRule { GenresAll = ["Comedy"], TextAny = ["office", "co-workers"] }, "Comedy whose summary mentions work"),
		new MicroGenre("tv-space", "Space TV Shows", IndexKind.Series, "space", null, new MicroGenreRule { TextAny = ["space", "starship"] }, "Its summary mentions space"),
		new MicroGenre("films-crime", "Crime Films", IndexKind.Movie, "crime", null, new MicroGenreRule { GenresAll = ["Crime"] }, "Crime"),
	], []);

	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-text190-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _films;
	private readonly List<BaseItemDto> _shows;
	private readonly ConcurrentDictionary<Guid, string?> _overviews = new();
	private readonly ConcurrentQueue<string> _asked = new(); // "index", "text page <library>", "text ids <count>", in order
	private readonly CoreListLogger<TextPass> _log = new();
	private Func<int, CancellationToken, Task> _textWait = (_, _) => Task.CompletedTask;
	private Func<int, bool> _textFails = _ => false; // the client's null: a request that didn't come (it never throws)
	private volatile bool _wanted = true;
	private readonly List<IDisposable> _made = [];

	static TextPassTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public TextPassTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns(Url);
		_client.SetupGet(x => x.UserId).Returns(User);
		_films = [.. Enumerable.Range(0, 60).Select(i => Title(i, BaseItemDto_Type.Movie))];
		_shows = [.. Enumerable.Range(0, 250).Select(i => Title(i, BaseItemDto_Type.Series))];
		foreach (var show in _shows)
		{
			var i = int.Parse(show.Name![5..]);
			_overviews[show.Id!.Value] = Overview(i);
		}

		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var shows = new BaseItemDto { Id = ShowsId, Name = "Shows", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Tvshows };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies, shows]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync(() => $"{_films.Count + _shows.Count}:newest");
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto library, int start, int limit, bool total, CancellationToken token) =>
			{
				_asked.Enqueue("index");
				await Task.Delay(5, token);
				var all = library.Id == MoviesId ? _films : _shows;
				return new BaseItemDtoQueryResult { Items = [.. all.Skip(start).Take(limit)], TotalRecordCount = total ? all.Count : null };
			});
		_client.Setup(x => x.GetPlayedEpisodesPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetLibraryTextPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto library, int start, int limit, bool total, CancellationToken token) =>
			{
				_asked.Enqueue($"text page {(library.Id == MoviesId ? "movies" : "shows")}");
				await _textWait(start, token);
				if (_textFails(start))
				{
					return null;
				}

				var all = library.Id == MoviesId ? _films : _shows;
				return new BaseItemDtoQueryResult { Items = [.. all.Skip(start).Take(limit).Select(Text)], TotalRecordCount = total ? all.Count : null };
			});
		_client.Setup(x => x.GetTextByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.Returns(async (IReadOnlyList<Guid> ids, CancellationToken token) =>
			{
				_asked.Enqueue($"text ids {ids.Count}");
				await _textWait(-1, token);
				if (_textFails(-1))
				{
					return null;
				}

				return (IReadOnlyList<BaseItemDto>?)[.. _shows.Concat(_films).Where(x => ids.Contains(x.Id!.Value)).Select(Text)];
			});
	}

	public void Dispose()
	{
		foreach (var made in _made)
		{
			made.Dispose();
		}

		_homeCache.Dispose();
		TestFolder.Delete(_folder);
	}

	private static BaseItemDto Title(int i, BaseItemDto_Type type) => new()
	{
		Id = Guid.Parse($"{i:D8}-{(type == BaseItemDto_Type.Movie ? "0001" : "0002")}-0190-0000-000000000000"),
		Name = $"{(type == BaseItemDto_Type.Movie ? "Film" : "Show")} {i:D4}",
		SortName = $"{(type == BaseItemDto_Type.Movie ? "film" : "show")} {i:D4}",
		Type = type,
		ProductionYear = 2000 + (i % 20),
		Genres = ["Comedy"],
		RunTimeTicks = TimeSpan.FromMinutes(22).Ticks,
		UserData = new UserItemDataDto { Played = false },
	};

	// every tenth show has no summary; of the rest a third are at an office, a third in space, a third neither
	private static string? Overview(int i) => (i % 10) switch
	{
		0 => null,
		_ => (i % 3) switch
		{
			0 => $"An office manager and his co-workers, {Marker} {i}.",
			1 => $"A starship crew lost in deep space, {Marker} {i}.",
			_ => $"A family runs a farm, {Marker} {i}.",
		},
	};

	private BaseItemDto Text(BaseItemDto title) => new() { Id = title.Id, Type = title.Type, Overview = _overviews.GetValueOrDefault(title.Id!.Value), Taglines = [] };

	private LibraryIndex Index(TimeProvider time)
	{
		// 1.9 (the index's load): R reads every title again, as before (these tests bring new titles with R; the day's limit: IndexOnABigServerTests.Load190.cs)
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { PeopleWanted = () => false, WholeReadEvery = TimeSpan.Zero };
		_made.Add(index);
		return index;
	}

	private TextPass Pass(LibraryIndex index, TimeProvider time, Func<CancellationToken, Task>? firstScreen = null, PauseGate? pause = null, MicroGenreCatalogue? catalogue = null,
		TimeSpan? pageTimeout = null)
	{
		var pass = new TextPass(_client.Object, index, _homeCache, Path.Combine(_folder, "LibraryIndex", "Summaries"), _log, time, pause ?? new PauseGate(), shared: false)
		{
			Wanted = () => _wanted,
			Catalogue = () => catalogue ?? Catalogue,
			AfterFirstScreen = firstScreen ?? (_ => Task.CompletedTask),
			RetryPause = (_, _) => Task.CompletedTask, // a page's next try at once
			PageTimeout = pageTimeout ?? IndexPages.DefaultPageTimeout,
		};
		_made.Add(pass);
		return pass;
	}

	private string[] Asked() => [.. _asked];

	private int TextRequests() => _asked.Count(x => x.StartsWith("text", StringComparison.Ordinal));

	private string[] SavedFiles() => Directory.Exists(Path.Combine(_folder, "LibraryIndex", "Summaries")) ? Directory.GetFiles(Path.Combine(_folder, "LibraryIndex", "Summaries"), "*.json") : [];

	private static async Task Until(Func<bool> done, string what)
	{
		for (var i = 0; i < 400 && !done(); i++)
		{
			await Task.Delay(25);
		}

		Assert.True(done(), what);
	}

	// the index read, and the pass after it, both settled
	private static async Task Settle(LibraryIndex index, TextPass pass)
	{
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
		await Task.Delay(50); // the index's Changed reaches the pass
		await pass.Running.WaitAsync(TimeSpan.FromSeconds(20));
	}

	private Guid ShowId(int i) => _shows[i].Id!.Value;

	// shows up to `count` (a pass of two pages: past TextPass.PageSize)
	private void ShowsUpTo(int count)
	{
		for (var i = _shows.Count; i < count; i++)
		{
			var show = Title(i, BaseItemDto_Type.Series);
			_overviews[show.Id!.Value] = Overview(i);
			_shows.Add(show);
		}
	}

	[Fact]
	public async Task ThePass_ComesAfterTheIndexsRead_AndReadsOnlyTheShows_EachOnce()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = Pass(index, time);

		Assert.NotNull(await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(20)));
		await Settle(index, pass);

		var asked = Asked();
		var firstText = Array.FindIndex(asked, x => x.StartsWith("text", StringComparison.Ordinal));
		Assert.True(firstText > 0, string.Join(", ", asked));
		Assert.DoesNotContain("index", asked[firstText..]); // the index's read was all in before the pass asked anything
		Assert.DoesNotContain("text page movies", asked); // no word list is for films: their summaries are never asked
		Assert.Equal(["text page shows"], asked.Where(x => x.StartsWith("text", StringComparison.Ordinal)).Distinct());
		Assert.Equal(2, TextRequests()); // 250 shows: as the index's pages, the library's first page of 200 with its count, then the rest

		var found = pass.Current;
		Assert.Equal(250, found.Count);
		Assert.Equal(25, found.Unknown); // every tenth has no summary
		Assert.True(found.Has("tv-workplace", ShowId(3)));
		Assert.True(found.Has("tv-space", ShowId(1)));
		Assert.Equal([], found.CategoriesOf(ShowId(2)));
		Assert.True(found.IsUnknown(ShowId(10)));
		Assert.Single(SavedFiles());
	}

	[Fact]
	public async Task ThePass_WaitsForTheIndexsPeopleStep_ToFinish()
	{
		var time = new ManualClock();
		var release = new TaskCompletionSource();
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto library, int start, int limit, bool total, CancellationToken token) =>
			{
				_asked.Enqueue("people");
				await release.Task.WaitAsync(token);
				var all = library.Id == MoviesId ? _films : _shows;
				return new BaseItemDtoQueryResult { Items = [.. all.Skip(start).Take(limit).Select(x => new BaseItemDto { Id = x.Id, Type = x.Type, People = [] })], TotalRecordCount = total ? all.Count : null };
			});
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { PeopleWanted = () => true, WholeReadEvery = TimeSpan.Zero }; // 1.9: as Index()
		_made.Add(index);
		var pass = Pass(index, time);

		Assert.NotNull(await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(20))); // the titles are in
		await Until(() => _asked.Contains("people"), "the index's people step");
		await Task.Delay(300);
		Assert.Equal(0, TextRequests()); // its people step still on its way: nothing asked beside it

		release.SetResult();
		await Until(() => pass.Current.Count == 250, "the pass after the people");
		var asked = Asked();
		Assert.True(Array.LastIndexOf(asked, "people") < Array.FindIndex(asked, x => x.StartsWith("text", StringComparison.Ordinal)), string.Join(", ", asked));
	}

	// ---- the pre-release fix H (BH-190-m1): with no Made by Nixie row on Home, every pass waited two minutes first ----------

	// a clock whose timers fire only when it is moved (TimeProvider's own run on real time; ManualClock has none)
	private sealed class StepClock : TimeProvider
	{
		private readonly Lock _gate = new();
		private readonly List<Step> _timers = [];
		private DateTimeOffset _now = ManualClock.Start;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override DateTimeOffset GetUtcNow()
		{
			lock (_gate)
			{
				return _now;
			}
		}

		public override long GetTimestamp() => GetUtcNow().UtcTicks;

		public int Waiting
		{
			get
			{
				lock (_gate)
				{
					return _timers.Count;
				}
			}
		}

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = new Step(this, callback, state);
			timer.Change(dueTime, period);
			return timer;
		}

		public void Advance(TimeSpan by)
		{
			List<Step> due;
			lock (_gate)
			{
				_now += by;
				due = [.. _timers.Where(x => x.Due <= _now)];
				_timers.RemoveAll(due.Contains);
			}

			foreach (var timer in due)
			{
				timer.Fire();
			}
		}

		private sealed class Step(StepClock clock, TimerCallback callback, object? state) : ITimer
		{
			public DateTimeOffset Due { get; private set; }

			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				lock (clock._gate)
				{
					clock._timers.Remove(this);
					if (dueTime != Timeout.InfiniteTimeSpan)
					{
						Due = clock._now + dueTime;
						clock._timers.Add(this);
					}
				}

				return true;
			}

			public void Fire() => callback(state);

			public void Dispose()
			{
				lock (clock._gate)
				{
					clock._timers.Remove(this);
				}
			}

			public ValueTask DisposeAsync()
			{
				Dispose();
				return ValueTask.CompletedTask;
			}
		}
	}

	// the app's wait for Home's first screen (not a test's AfterFirstScreen), on a clock the test moves; Home's first screen
	// never comes (no Made by Nixie row there)
	private TextPass PassWithTheAppsWait(LibraryIndex index, TimeProvider time)
	{
		var pass = new TextPass(_client.Object, index, _homeCache, Path.Combine(_folder, "LibraryIndex", "Summaries"), _log, time, new PauseGate(), shared: false)
		{
			Wanted = () => _wanted,
			Catalogue = () => Catalogue,
			FirstScreen = new TaskCompletionSource().Task,
			RetryPause = (_, _) => Task.CompletedTask,
		};
		_made.Add(pass);
		return pass;
	}

	[Fact]
	public async Task SettingsTurningTheCategoriesOn_AsksAtOnce_ThoughHomesFirstScreenNeverCame()
	{
		var clock = new StepClock();
		_wanted = false; // the categories off at the start (1.8.1's settings with every kind off: the one-time step)
		var index = Index(new ManualClock());
		var pass = PassWithTheAppsWait(index, clock);
		await index.GetAsync();
		await Settle(index, pass);
		Assert.Equal(0, TextRequests());

		_wanted = true;
		pass.Switched(); // Settings > Collections: Categories on (the clock never moves: no two minutes pass)
		await Until(() => pass.Current.Count == 250, "the pass at once after Settings turned the categories on");
	}

	[Fact]
	public async Task TheWaitForHomesFirstScreen_IsCountedOnce_ALibraryChangesPassDoesntWaitAgain()
	{
		var clock = new StepClock();
		var index = Index(new ManualClock());
		var pass = PassWithTheAppsWait(index, clock);
		await index.GetAsync();
		await Until(() => clock.Waiting > 0, "the pass waits for Home's first screen");
		Assert.Equal(0, TextRequests());

		clock.Advance(TextPass.FirstRowLongest + TimeSpan.FromSeconds(1)); // Home showed none: two minutes at most
		await Until(() => pass.Current.Count == 250, "the pass after two minutes");
		await Settle(index, pass);

		ShowsUpTo(260); // a library change: its 10 new shows, by id, now (the clock doesn't move)
		Assert.NotNull(await index.RefreshAsync());
		await Until(() => pass.Current.Count == 260, "the library change's pass at once");
		Assert.Contains("text ids 10", Asked());
	}

	[Fact]
	public async Task ThePass_WaitsForHomesFirstRow()
	{
		var time = new ManualClock();
		var firstRow = new TaskCompletionSource();
		var index = Index(time);
		var pass = Pass(index, time, firstScreen: token => firstRow.Task.WaitAsync(token));

		await index.GetAsync().WaitAsync(TimeSpan.FromSeconds(20));
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
		await Task.Delay(300);
		Assert.Equal(0, TextRequests()); // the index is in, Home's first row not yet: nothing asked

		firstRow.SetResult();
		await Until(() => pass.Current.Count == 250, "the pass after Home's first row");
	}

	[Fact]
	public async Task OnItsOwnGate_ThePassBegins_WhenHomeSaysItsFirstRowIsShown()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = new TextPass(_client.Object, index, _homeCache, Path.Combine(_folder, "LibraryIndex", "Summaries"), _log, time, new PauseGate(), shared: false)
		{
			Wanted = () => _wanted,
			Catalogue = () => Catalogue,
		}; // the app's own wait: Home's first Made by Nixie row, or two minutes
		_made.Add(pass);

		await index.GetAsync();
		TextPass.FirstRowShown(); // HomeViewModel, at its first Made by Nixie row
		await Until(() => pass.Current.Count == 250, "the pass after Home's first row");
	}

	[Fact]
	public async Task AfterTheFirstPass_OnlyNewTitlesAreAskedById_AndNothingWhenNoneIsNew()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		var before = TextRequests();

		// three shows added: R reads the titles, then the pass asks exactly those three by id
		for (var i = 250; i < 253; i++)
		{
			var show = Title(i, BaseItemDto_Type.Series);
			_overviews[show.Id!.Value] = $"Office life, {Marker}.";
			_shows.Add(show);
		}

		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		Assert.Equal(["text ids 3"], Asked().Where(x => x.StartsWith("text", StringComparison.Ordinal)).Skip(before));
		Assert.True(pass.Current.Has("tv-workplace", ShowId(252)));
		Assert.Equal(253, pass.Current.Count);

		// nothing new: nothing asked
		var after = TextRequests();
		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		Assert.Equal(after, TextRequests());
	}

	// 1.9 (the index's load, the review of wip/190-index): R within the day reads what changed (the titles saved since,
	// merged), and the pass asks the new shows' summaries by id from that merged index as from a read of every title
	[Fact]
	public async Task AMergedReadOfWhatChanged_AsksOnlyTheNewShowsById()
	{
		var time = new ManualClock();
		var saved = new List<BaseItemDto>();
		_client.Setup(x => x.GetLibraryTitleCount(It.IsAny<BaseItemDto>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, CancellationToken _) => library.Id == MoviesId ? _films.Count : _shows.Count);
		_client.Setup(x => x.GetLibraryIndexPageSavedSince(It.IsAny<BaseItemDto>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, DateTimeOffset _, int start, int limit, bool total, CancellationToken _) =>
			{
				List<BaseItemDto> mine = library.Id == ShowsId ? saved : [];
				return new BaseItemDtoQueryResult { Items = [.. mine.Skip(start).Take(limit)], TotalRecordCount = total ? mine.Count : null };
			});
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), time) { PeopleWanted = () => false };
		_made.Add(index);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		var before = TextRequests();
		var pages = Asked().Count(x => x == "index");

		for (var i = 250; i < 253; i++) // three shows added on the server
		{
			var show = Title(i, BaseItemDto_Type.Series);
			_overviews[show.Id!.Value] = $"Office life, {Marker}.";
			_shows.Add(show);
			saved.Add(show);
		}

		Assert.NotNull(await index.RefreshAsync()); // R: what changed
		await Settle(index, pass);
		Assert.Equal(pages, Asked().Count(x => x == "index")); // no page of every title
		Assert.Equal(["text ids 3"], Asked().Where(x => x.StartsWith("text", StringComparison.Ordinal)).Skip(before));
		Assert.True(pass.Current.Has("tv-workplace", ShowId(252)));
		Assert.Equal(253, pass.Current.Count);
	}

	[Fact]
	public async Task ATitleWithNoSummary_IsUnknown_NotANonMatch_AndIsAskedAgainAfterAWeek()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		Assert.True(pass.Current.IsUnknown(ShowId(30)));
		Assert.False(pass.Current.IsKnown(ShowId(30)));
		Assert.Null(pass.Current.CategoriesOf(ShowId(30))); // not "none of the words"

		// a day on: the day's read, and the unknown ones not asked again yet
		var before = TextRequests();
		time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		Assert.Equal(before, TextRequests());

		// a week on: the 25 unknown asked again by id; one has a summary now
		_overviews[ShowId(30)] = $"A space station's crew, {Marker}.";
		time.Advance(TextPass.UnknownAskedAfter);
		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		Assert.Equal(["text ids 25"], Asked().Where(x => x.StartsWith("text", StringComparison.Ordinal)).Skip(before));
		Assert.True(pass.Current.Has("tv-space", ShowId(30)));
		Assert.Equal(24, pass.Current.Unknown);
	}

	[Fact]
	public async Task Standby_PausesThePassAtItsNextRequest_AndItGoesOnWhenStandbyEnds()
	{
		var time = new ManualClock();
		ShowsUpTo(1_150); // two pages
		var gate = new PauseGate();
		var firstPage = new TaskCompletionSource();
		_textWait = async (start, token) =>
		{
			if (start == 0)
			{
				gate.Set("standby", true); // the PC goes to standby while the first page is on its way
				firstPage.TrySetResult();
			}

			await Task.CompletedTask;
		};
		var index = Index(time);
		var pass = Pass(index, time, pause: gate);
		await index.GetAsync();
		await firstPage.Task.WaitAsync(TimeSpan.FromSeconds(20));
		await Task.Delay(400);

		Assert.Equal(1, TextRequests()); // the page asked before standby came; no other after it
		Assert.False(pass.Running.IsCompleted);
		Assert.Contains(_log.Entries, x => x.Message.Contains("waits (standby)", StringComparison.Ordinal));

		gate.Set("standby", false);
		await pass.Running.WaitAsync(TimeSpan.FromSeconds(20));
		Assert.Equal(2, TextRequests());
		Assert.Equal(1_150, pass.Current.Count);
	}

	[Fact]
	public async Task TurningTheCategoriesOff_StopsThePass_DeletesItsMatches_AndNothingLandsAfter()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		Assert.Single(SavedFiles());

		// a pass in flight (the unknown ones a week on), held at its request
		var held = new TaskCompletionSource();
		var release = new TaskCompletionSource();
		_textWait = async (_, _) =>
		{
			held.TrySetResult();
			await release.Task;
		};
		time.Advance(TextPass.UnknownAskedAfter + TimeSpan.FromDays(1));
		var reading = index.RefreshAsync();
		await held.Task.WaitAsync(TimeSpan.FromSeconds(20));

		_wanted = false;
		pass.Switched(); // Settings > Collections: the categories off
		await Until(() => SavedFiles().Length == 0, "the matches deleted");
		Assert.Equal(0, pass.Current.Count);
		release.SetResult(); // the request in flight answers after
		await reading;
		await pass.Running.WaitAsync(TimeSpan.FromSeconds(20));
		await Task.Delay(200);

		Assert.Empty(SavedFiles()); // nothing it began landed
		Assert.Equal(0, pass.Current.Count);
		var asked = TextRequests();
		Assert.NotNull(await index.RefreshAsync()); // and no pass while off
		await Settle(index, pass);
		Assert.Equal(asked, TextRequests());
		Assert.Contains(_log.Entries, x => x.Message.Contains("its matches were deleted", StringComparison.Ordinal));
	}

	[Fact]
	public async Task TheCategoriesOff_WithoutTheSwitchesWord_StillStopAtTheNextRequest_AndDeleteTheMatches()
	{
		var time = new ManualClock();
		ShowsUpTo(1_150); // two pages
		var release = new TaskCompletionSource();
		var held = new TaskCompletionSource();
		_textWait = async (start, _) =>
		{
			if (start == 0)
			{
				held.TrySetResult();
				await release.Task;
			}
		};
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await held.Task.WaitAsync(TimeSpan.FromSeconds(20));

		_wanted = false; // off, and nothing told the pass
		release.SetResult();
		await pass.Running.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal(1, TextRequests()); // the page in flight; none after it
		Assert.Empty(SavedFiles());
		Assert.Equal(0, pass.Current.Count);
	}

	[Fact]
	public async Task SigningOut_ForgetsTheUsersMatches()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		Assert.Single(SavedFiles());

		Assert.Equal(250, pass.Current.Count);
		var atSignOut = -1;
		_homeCache.Forgetting += (_, _) => atSignOut = pass.Current.Count; // during the sign-out, after the pass heard it
		await _homeCache.Forget(Url, User);
		Assert.Equal(0, atSignOut); // at once, not once the file has gone (nothing may read them meanwhile)
		Assert.Equal(0, pass.Current.Count);
		await Until(() => SavedFiles().Length == 0, "the matches deleted at sign-out");
		Assert.Equal(0, pass.Current.Count);
	}

	[Fact]
	public async Task TheSavedMatches_ComeBackAtTheNextStart_WithoutAskingTheServer_AndChangedWordListsReadThemAgain()
	{
		var time = new ManualClock();
		using (var first = Index(time))
		{
			using var pass = Pass(first, time);
			await first.GetAsync();
			await Settle(first, pass);
		}

		Assert.Single(SavedFiles()); // a pass's matches are kept on this PC
		var asked = TextRequests();
		var index = Index(time);
		var again = Pass(index, time, firstScreen: _ => new TaskCompletionSource().Task); // Home's first row never comes
		await index.GetAsync();
		await Until(() => again.Current.Count == 250, "the saved matches at once");
		Assert.True(again.Current.Has("tv-workplace", ShowId(3)));
		Assert.Equal(asked, TextRequests());
		Assert.Contains(_log.Entries, x => x.Message.Contains("the saved matches (250 titles)", StringComparison.Ordinal)); // said, counts only
		// 1.9 (the pre-release nit): the pass has no event of its own (its Changed and Found had no listener): the app hears new
		// matches from TextMatches.SharedChanged, where the Collections page makes its categories again (Home shows none)
		Assert.Empty(typeof(TextPass).GetEvents());

		// an update changed a word list: every summary is read again
		var changed = new MicroGenreCatalogue([new MicroGenre("tv-farm", "Farm TV Shows", IndexKind.Series, "farm", null, new MicroGenreRule { TextAny = ["farm"] }, "Farm")], []);
		var index3 = Index(time);
		var third = Pass(index3, time, catalogue: changed);
		await index3.GetAsync();
		await Settle(index3, third);
		Assert.Equal(asked + 2, TextRequests());
		Assert.True(third.Current.Has("tv-farm", ShowId(2)));
		Assert.False(third.Current.Has("tv-workplace", ShowId(3)));
	}

	// the review of wip/190-text, item 4: once a pass read every show, new shows waiting for their read hide nothing
	[Fact]
	public async Task OnceEveryShowWasRead_NewShowsWhoseReadFails_HideNoCategory_AcrossARestart()
	{
		var time = new ManualClock();
		using (var first = Index(time))
		{
			using var pass = Pass(first, time);
			await first.GetAsync();
			await Settle(first, pass);

			ShowsUpTo(260); // 10 new shows (3.8 %), and their read by id doesn't come
			_textFails = start => start < 0;
			Assert.NotNull(await first.RefreshAsync());
			await Settle(first, pass);
			Assert.Contains("text ids 10", Asked());
			Assert.Equal(250, pass.Current.Count);
			Assert.True(Covers(first, pass.Current));
		}

		var index = Index(time);
		var again = Pass(index, time, firstScreen: _ => new TaskCompletionSource().Task); // the saved matches alone
		await index.GetAsync();
		await Until(() => again.Current.Count == 250, "the saved matches");
		Assert.True(Covers(index, again.Current));

		static bool Covers(LibraryIndex index, TextMatches text) =>
			MicroGenreMatcher.For(CanonicalTitles.Of(index.Current!.Entries)).TextCovers(IndexKind.Series, text);
	}

	// the review of wip/190-text, item 5: a publish that crossed a sign-out set the signed-out user's matches as the app's again
	[Fact]
	public async Task ASignOutJustAfterAPublish_LeavesNoMatchOfTheirsAsTheApps()
	{
		var time = new ManualClock();
		var index = Index(time);
		var signedOut = 0;
		var pass = new TextPass(_client.Object, index, _homeCache, Path.Combine(_folder, "LibraryIndex", "Summaries"), _log, time, new PauseGate(), shared: true)
		{
			Wanted = () => _wanted,
			Catalogue = () => Catalogue,
			AfterFirstScreen = _ => Task.CompletedTask,
			AfterKept = async () =>
			{
				if (Interlocked.Exchange(ref signedOut, 1) == 0)
				{
					await _homeCache.Forget(Url, User); // the user signs out as the pass's matches are published
				}
			},
		};
		_made.Add(pass);
		try
		{
			await index.GetAsync();
			await Settle(index, pass);

			Assert.Equal(1, signedOut);
			Assert.Same(TextMatches.Empty, TextMatches.Shared);
			Assert.Equal(0, pass.Current.Count);
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	// the review of wip/190-text, probe A: the restart test above holds Home's first row back, so a second start that asked for
	// every summary again could never fail it
	[Fact]
	public async Task ASecondStart_WithHomesFirstRowShown_AsksForNoSummary()
	{
		var time = new ManualClock();
		using (var first = Index(time))
		{
			using var pass = Pass(first, time);
			await first.GetAsync();
			await Settle(first, pass);
		}

		var asked = TextRequests();
		time.Advance(TimeSpan.FromHours(2));
		var index = Index(time);
		var again = Pass(index, time); // Home's first row given at once
		await index.GetAsync();
		await Settle(index, again);
		await Task.Delay(300);
		await again.Running.WaitAsync(TimeSpan.FromSeconds(20));

		Assert.Equal(250, again.Current.Count);
		Assert.Equal(asked, TextRequests());
	}

	[Fact]
	public async Task TheLog_SaysCountsAndDurations_NeverATitleOrASummary()
	{
		var time = new ManualClock();
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);

		Assert.Contains(_log.Entries, x => x.Message.StartsWith("Summaries: 250 titles read (every library) in 2 requests", StringComparison.Ordinal));
		Assert.All(_log.Entries, x =>
		{
			Assert.DoesNotContain(Marker, x.Message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("Show 0", x.Message, StringComparison.Ordinal);
			Assert.DoesNotContain("office", x.Message, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain(ShowId(3).ToString(), x.Message, StringComparison.OrdinalIgnoreCase);
		});
	}

	[Fact]
	public async Task APageThatNeverComes_EndsThePass_KeepsWhatCame_AndBacksOff()
	{
		var time = new ManualClock();
		ShowsUpTo(1_150);
		_textFails = start => start > 0; // past its first page the server answers nothing (it went offline): every try, every half
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);

		Assert.Equal(200, pass.Current.Count); // the first page's are kept
		var asked = TextRequests();
		Assert.Equal(1 + IndexPages.Tries, asked); // the page, then its halves, three tries in all
		_textFails = _ => false;
		Assert.NotNull(await index.RefreshAsync()); // within RetryAfter: not asked again
		await Settle(index, pass);
		Assert.Equal(asked, TextRequests());

		time.Advance(TextPass.RetryAfter + TimeSpan.FromMinutes(1));
		Assert.NotNull(await index.RefreshAsync()); // then the 950 left, by id, a hundred a request
		await Settle(index, pass);
		Assert.Equal([.. Enumerable.Repeat("text ids 100", 9), "text ids 50"], Asked().Where(x => x.StartsWith("text", StringComparison.Ordinal)).Skip(asked));
		Assert.Equal(1_150, pass.Current.Count);
	}

	// the review of wip/190-text, item 7: a refusal (401, 403) was asked again every 15 minutes, to be refused again
	[Fact]
	public async Task TheServerRefusingThePass_StopsIt_UntilTheNextSignIn()
	{
		var time = new ManualClock();
		ShowsUpTo(1_150);
		var refusing = true;
		_textWait = (start, _) => refusing && start > 0 ? Task.FromException(new IndexRefusedException(401)) : Task.CompletedTask; // as the client throws it
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		Assert.Equal(200, pass.Current.Count); // what came is kept
		Assert.Contains(_log.Entries, x => x.Message.Contains("refused the pass (HTTP 401)", StringComparison.Ordinal));

		refusing = false;
		var asked = TextRequests();
		time.Advance(TextPass.RetryAfter + TimeSpan.FromHours(1));
		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		pass.Switched(); // Settings: still on
		await Task.Delay(200);
		await pass.Running.WaitAsync(TimeSpan.FromSeconds(20));
		Assert.Equal(asked, TextRequests()); // nothing asked again

		_homeCache.SignedIn(Url, User, "fake-server", "alice"); // the next sign-in
		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		Assert.Equal(1_150, pass.Current.Count); // the rest, by id
	}

	// the review of wip/190-text, probe B: a pass cut short at its third page read every library again from its first
	[Fact]
	public async Task APassCutShort_IsNeverReadWholeAgain_TheRestIsAskedById()
	{
		var time = new ManualClock();
		ShowsUpTo(2_500);
		_textFails = start => start is >= 1_200 and < 2_200; // that page (and its halves) never comes
		var index = Index(time);
		var pass = Pass(index, time);
		await index.GetAsync();
		await Settle(index, pass);
		var kept = pass.Current.Count;
		Assert.InRange(kept, 200, 1_500); // what came is kept (the page beside the one that failed stops with it, as the index's)

		var before = Asked().Length;
		_textFails = _ => false;
		time.Advance(TextPass.RetryAfter + TimeSpan.FromMinutes(1));
		Assert.NotNull(await index.RefreshAsync());
		await Settle(index, pass);
		var again = Asked().Skip(before).Where(x => x.StartsWith("text", StringComparison.Ordinal)).ToArray();

		Assert.DoesNotContain("text page shows", again); // no page of the library again, its first least of all
		Assert.Equal(2_500 - kept, again.Sum(x => int.Parse(x["text ids ".Length..], System.Globalization.CultureInfo.InvariantCulture)));
		Assert.Equal(2_500, pass.Current.Count);
	}

	// the review of wip/190-text, probe C: a page that didn't come in time was asked again at the same size, for ever
	[Fact]
	public async Task APageThatRunsOutOfTime_IsAskedAgainAsHalves_AndTheNextWholeReadStartsAtTheSizeItLeft()
	{
		var time = new ManualClock();
		ShowsUpTo(1_150);
		var asks = new ConcurrentQueue<string>();
		var hung = 0;
		_client.Setup(x => x.GetLibraryTextPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto library, int start, int limit, bool total, CancellationToken token) =>
			{
				_asked.Enqueue("text page shows");
				asks.Enqueue($"{start}+{limit}");
				if (start > 0 && Interlocked.Exchange(ref hung, 1) == 0)
				{
					try
					{
						await Task.Delay(Timeout.Infinite, token); // a slow server: the page doesn't come in time
					}
					catch (OperationCanceledException)
					{
						return null; // as the app's client answers when its time runs out
					}
				}

				return new BaseItemDtoQueryResult { Items = [.. _shows.Skip(start).Take(limit).Select(Text)], TotalRecordCount = total ? _shows.Count : null };
			});
		using (var first = Index(time))
		{
			using var pass = Pass(first, time, pageTimeout: TimeSpan.FromMilliseconds(300));
			await first.GetAsync();
			await Settle(first, pass);
			Assert.Equal(1_150, pass.Current.Count);
		}

		Assert.Equal(["0+200", "200+1000", "200+500", "700+500"], asks.ToArray()); // the page that ran out, then its two halves
		asks.Clear();

		// an update changed the word lists: every library again, its pages at the size the last read left
		var changed = new MicroGenreCatalogue([new MicroGenre("tv-farm", "Farm TV Shows", IndexKind.Series, "farm", null, new MicroGenreRule { TextAny = ["farm"] }, "Farm")], []);
		var index = Index(time);
		var again = Pass(index, time, catalogue: changed);
		await index.GetAsync();
		await Settle(index, again);
		Assert.Equal(["0+200", "200+500", "700+500"], asks.Order().ToArray());
		Assert.True(again.Current.Has("tv-farm", ShowId(2)));
	}
}
