using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream wire): the index's revision 4 on Home. The first start after the update from 1.8.1 fills
/// Home's Made by Nixie rows from the saved index (revision 3) and reads nothing for its keywords (the review's Major 2: a
/// forced read began at every start); the day's read brings revision 4 behind Home's rows, every page of it waiting (design
/// rule 1: the first screen waits only for what it shows; rule 3: no whole-library work on its path); the next start reads
/// nothing.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class IndexRevision4HomeTests : IDisposable
{
	private static readonly Guid User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000190");
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-rev4-" + Guid.NewGuid().ToString("N"));
	private readonly System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage> _messages = new();
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _films = [];
	private readonly Settings _settings = new();
	private readonly ManualClock _clock = new();
	private TaskCompletionSource? _readHeld; // while set, the index's pages wait for it
	private int _pages;

	static IndexRevision4HomeTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public IndexRevision4HomeTests()
	{
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(User);
		// the lineup's slots at real shape: two themes, Acclaimed, Under 90 minutes; and office comedies (a matchable keyword)
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Rev4 heist {i}", 1970 + i, 7.0, 120, ["heist"], ["Crime", "Thriller"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Rev4 loop {i}", 1990 + i, 7.2, 115, ["time travel"], ["Science Fiction"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Rev4 acclaimed {i}", 1950 + i, 8.0 + (i * 0.09), 130, [], ["Drama"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Rev4 short {i}", 2005 + i, 6.8, 80, [], ["Comedy"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Rev4 office {i}", 2000 + i, 6.9, 100, ["office", "workplace"], ["Comedy"])));
		_films.AddRange(Enumerable.Range(0, 3).Select(i => Film($"Rev4 other {i}", 1930 + i, 6.0, 95, [], ["Western"]))); // 53 titles: this test's census lines
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync("50:newest");
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool _, CancellationToken _) =>
			{
				Interlocked.Increment(ref _pages);
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

	private static BaseItemDto Film(string name, int year, double rating, int minutes, List<string> tags, List<string> genres) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.Movie,
		ProductionYear = year,
		CommunityRating = (float)rating,
		CriticRating = 80,
		RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
		Tags = tags,
		Genres = genres,
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" + name.Length } },
		UserData = new UserItemDataDto { Played = false },
	};

	private HomeViewModel Home(HomeCache cache) =>
		new(_client.Object, _messages, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()),
			cache, _settings, _clock);

	private HomeCache Cache() => new(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), _clock);

	private LibraryIndex Index(HomeCache cache) =>
		new(_client.Object, cache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), _clock) { PeopleWanted = () => false };

	[Fact]
	public async Task AfterTheUpdate_HomesRowsComeFromThe181Index_NothingIsReadForIt_AndTheDaysReadWaitsBehindHome()
	{
		// the last run, on 1.8.1: the index read and saved (no Home page saved: Home's rows can come only from the index)
		using (var last = Cache())
		{
			using var index = Index(last);
			Assert.NotNull(await index.GetAsync());
			await index.Settled;
		}

		// as 1.8.1 left the files: revision 3, no title's matchable keywords, and its note of its read
		var folder = Path.Combine(_folder, "LibraryIndex");
		var file = Directory.GetFiles(folder, "*.json").Single();
		var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		root["Revision"] = LibraryIndex.BackingRevision;
		foreach (var entry in root["Entries"]!.AsArray())
		{
			entry!.AsObject()["Tags"] = new JsonArray([.. entry["Tags"]!.AsArray().Select(x => (string)x!).Where(Keywords.IsNamed).Select(x => (JsonNode)x)]);
		}

		await File.WriteAllTextAsync(file, root.ToJsonString());
		var note = Directory.GetFiles(Path.Combine(folder, "Reads"), "*.json").Single();
		await File.WriteAllTextAsync(note, (await File.ReadAllTextAsync(note)).Replace($"\"Revision\":{LibraryIndex.FormatRevision}", $"\"Revision\":{LibraryIndex.BackingRevision}", StringComparison.Ordinal));

		// the first start after the update, ten minutes later: Home's rows from the saved index, and no page asked
		var before = _pages;
		_clock.Advance(TimeSpan.FromMinutes(10));
		HomeViewModel.ForgetNixieDayForTests();
		using (var firstCache = Cache())
		{
			using var first = Index(firstCache);
			SmartCollectionServices.Index = first;
			var firstHome = Home(firstCache);
			await firstHome.OnNavigatedTo(new object()).WaitAsync(TimeSpan.FromSeconds(20));
			await firstHome.NixieFill.WaitAsync(TimeSpan.FromSeconds(20));
			Assert.NotEmpty(firstHome.NixieRows);
			Assert.All(firstHome.NixieRows, r => Assert.NotEmpty(r.Items));
			Assert.False(first.Current!.KeywordsKnown); // the index shown is 1.8.1's
			await Task.Delay(100);
			await first.Settled;
			await first.Checking;
			await firstHome.OnNavigatedFrom();
		}

		Assert.Equal(before, _pages);

		// a day later: the day's read, every page of it waiting; Home's rows from the saved index meanwhile
		_clock.Advance(TimeSpan.FromDays(1));
		HomeViewModel.ForgetNixieDayForTests();
		using var cache = Cache();
		using var again = Index(cache);
		SmartCollectionServices.Index = again;
		_readHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var home = Home(cache);
		await home.OnNavigatedTo(new object()).WaitAsync(TimeSpan.FromSeconds(20));
		await home.NixieFill.WaitAsync(TimeSpan.FromSeconds(20)); // Home's Made by Nixie rows: in, from the saved index

		Assert.NotEmpty(home.NixieRows);
		Assert.All(home.NixieRows, r => Assert.NotEmpty(r.Items));
		Assert.False(_readHeld.Task.IsCompleted);
		Assert.False(again.Current!.KeywordsKnown);
		for (var i = 0; i < 200 && _pages == before; i++)
		{
			await Task.Delay(10);
		}

		Assert.True(_pages > before, "the day's read began behind Home");
		await home.OnNavigatedFrom();

		// the day's read comes in: the matchable keywords kept
		_readHeld.SetResult();
		await again.Settled;
		Assert.True(again.Current!.KeywordsKnown);
		Assert.Equal(10, again.Current.Entries.Count(x => x.Tags.Contains("office")));

		// once: the next start, two hours later, reads nothing (the index is fresh, the server's check unchanged)
		var once = _pages;
		_clock.Advance(TimeSpan.FromHours(2));
		HomeViewModel.ForgetNixieDayForTests();
		using (var nextCache = Cache())
		{
			using var next = Index(nextCache);
			SmartCollectionServices.Index = next;
			var nextHome = Home(nextCache);
			await nextHome.OnNavigatedTo(new object()).WaitAsync(TimeSpan.FromSeconds(20));
			await nextHome.NixieFill.WaitAsync(TimeSpan.FromSeconds(20));
			Assert.NotEmpty(nextHome.NixieRows);
			await Task.Delay(100);
			await next.Settled;
			await next.Checking;
			Assert.True(next.Current!.KeywordsKnown);
			await nextHome.OnNavigatedFrom();
		}

		Assert.Equal(once, _pages);
	}

	// the review of wip/190-wire (Minor 3): Home showed no category and built them all (about 40 ms at 31,000 titles); and as its
	// build is the first, the census line it took never said the categories' counts
	[Fact]
	public async Task HomesBuild_MakesNoCategory_ThePagesBuildStillSaysTheirCensus()
	{
		Assert.Contains(SmartCollectionKind.MicroGenre, HomeViewModel.HomeBuildOff(new HashSet<SmartCollectionKind>()));
		var off = new HashSet<SmartCollectionKind> { SmartCollectionKind.Keyword };
		Assert.Equal([SmartCollectionKind.Keyword, SmartCollectionKind.MicroGenre], HomeViewModel.HomeBuildOff(off).Order());
		// this test's own log (the class is serial with the others that set the static one; one of them leaves it null)
		var log = new CoreListLogger<IndexRevision4HomeTests>();
		SmartCollectionCatalog.Log = log;
		try
		{
			using var cache = Cache();
			using var index = Index(cache);
			SmartCollectionServices.Index = index;
			Assert.NotNull(await index.GetAsync());
			await index.Settled;
			var snapshot = index.Current!;
			string[] Census() => [.. log.Entries.Select(x => x.Message).Where(x => x.StartsWith("Made by Nixie census: ", StringComparison.Ordinal))];
			HomeViewModel.ForgetNixieDayForTests();

			var home = Home(cache);
			await home.OnNavigatedTo(new object()).WaitAsync(TimeSpan.FromSeconds(20));
			await home.NixieFill.WaitAsync(TimeSpan.FromSeconds(20));

			Assert.NotEmpty(home.NixieRows);
			Assert.Contains("MicroGenre off", Assert.Single(Census()), StringComparison.Ordinal); // Home's build: no category made
			await home.OnNavigatedFrom();
			// the Collections or Libraries page's build of the same index: the categories made, their census said, once
			await SmartCollectionCatalog.BuildAllAsync(snapshot, SmartCollectionCatalog.Now(_clock));
			await SmartCollectionCatalog.BuildAllAsync(snapshot, SmartCollectionCatalog.Now(_clock));
			var lines = Census();
			Assert.Equal(2, lines.Length);
			Assert.Single(lines, x => System.Text.RegularExpressions.Regex.IsMatch(x, @"MicroGenre \d+/\d+/\d+ \[films "));
		}
		finally
		{
			SmartCollectionCatalog.Log = MadeByNixieLog.Instance;
		}
	}

	/// <summary>The settings a test sets: values kept as they are.</summary>
	private sealed class Settings : ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
