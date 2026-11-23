using System.Reactive.Subjects;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Microsoft.Extensions.Logging;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.2 (hotfix): the Collections page showed "Couldn't load your collections" at once on the owner's server. The
/// saved index, written by 1.7.0, listed one title twice; 1.7.1 shows the saved index at once, and the genre mixes'
/// builder (ToDictionary on the title's id) threw inside the page's load, which took the server's own collections and
/// playlists down with it. Home lost its Made by Nixie rows the same way. Now a saved index keeps each title once, no
/// builder throws on a repeated id, a builder that throws leaves only its own kind out, and Made by Nixie that can't be
/// made never fails the page.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class RepeatedTitleTests : IDisposable
{
	private static readonly Guid MoviesId = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000172");
	private static readonly DateTimeOffset Noon = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-repeated172-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _films;

	static RepeatedTitleTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public RepeatedTitleTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		CollectionNames.RememberBoxSets([]);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000172"));
		// 24 films: Comedy and Horror both every third (a genre mix), a franchise every three, two decades, some rated 8 and 9
		_films = [.. Enumerable.Range(0, 24).Select(Film)];
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync("24:newest");
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) =>
				new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = total ? _films.Count : null });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult
			{
				Items = [.. _films.Skip(start).Take(limit).Select(x => new BaseItemDto { Id = x.Id, Type = BaseItemDto_Type.Movie, People = [new BaseItemPerson { Name = "Ana Reyes", Type = BaseItemPerson_Type.Director }] })],
				TotalRecordCount = total ? _films.Count : null,
			});
		_client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => [.. ids.Distinct().Select(id => _films.First(f => f.Id == id))]);
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = "Heist Films", Type = BaseItemDto_Type.BoxSet, ChildCount = 6, UserData = new UserItemDataDto { UnplayedItemCount = 3 } }],
		});
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = "Friday", Type = BaseItemDto_Type.Playlist, ChildCount = 4 }],
		});
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>())).ReturnsAsync([_films[3]]);
	}

	public void Dispose()
	{
		SmartCollectionServices.Index = null;
		CollectionNames.ForgetBoxSets();
		_homeCache.Dispose();
		TestFolder.Delete(_folder);
	}

	private static BaseItemDto Film(int i) => new()
	{
		Id = Guid.Parse($"{i:D8}-0172-0000-0000-000000000000"),
		Name = $"Film {i:D2}",
		SortName = $"film {i:D2}",
		Type = BaseItemDto_Type.Movie,
		ProductionYear = 1990 + (i % 20),
		CommunityRating = 5 + (i % 5),
		CriticRating = 80, // 1.7.3 (M1's review): Acclaimed takes only titles something backs
		Genres = (i % 3) switch { 0 => ["Comedy", "Horror"], 1 => ["Comedy"], _ => ["Horror"] },
		ProductionLocations = [i % 2 == 0 ? "France" : "Japan"],
		Tags = i % 2 == 0 ? ["heist"] : [], // 1.7.3: twelve heists (a keyword collection)
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{i}", ["TmdbCollection"] = $"{i / 3}" } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"p{i}" } },
		UserData = new UserItemDataDto { Played = false },
	};

	private static IndexEntry Entry(BaseItemDto film) => IndexEntry.From(film, MoviesId)!;

	private static IndexSnapshot Snapshot(IReadOnlyList<IndexEntry> entries) =>
		new([new IndexLibrary(MoviesId, "Movies", IndexKind.Movie)], entries, new Dictionary<Guid, SeriesPlays>(), true, Noon);

	private LibraryIndex Index(ILogger? log = null) =>
		new(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, log ?? new CoreListLogger<LibraryIndex>(), TimeProvider.System);

	private CollectionsViewModel Page(LibraryIndex index, ILocalSettingsService? settings = null, ILogger<CollectionsViewModel>? log = null, Func<IndexSnapshot, Task>? making = null) =>
		new(_client.Object, Commands(), index, new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>()),
			settings, logger: log)
		{ Making = making };

	private GlobalCommands Commands() => new(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	// the index read and saved as a run before this one left it, then changed in its file: format `version`, `change` on its
	// entries (1.7.0's: the first title listed twice)
	private async Task SaveIndex(int version, Action<JsonArray> change)
	{
		using (var before = Index())
		{
			Assert.NotNull(await before.GetAsync());
			await before.Settled;
		}

		var file = Directory.GetFiles(Path.Combine(_folder, "LibraryIndex"), "*.json").Single();
		var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		change(root["Entries"]!.AsArray());
		root["Version"] = version;
		await File.WriteAllTextAsync(file, root.ToJsonString());
	}

	private static void ATitleTwice(JsonArray entries) => entries.Add(entries[0]!.DeepClone());

	[Theory]
	[InlineData(2)] // as 1.7.0 left it
	[InlineData(1)] // a 1.6 index: shown at once too
	public async Task ASavedIndexWithATitleTwice_ThePageShowsTheServersSetsPlaylistsAndMadeByNixie_AndHomeItsRows(int format)
	{
		await SaveIndex(format, ATitleTwice);
		var log = new CoreListLogger<LibraryIndex>();
		using var index = Index(log); // a restart
		SmartCollectionServices.Index = index;
		var vm = Page(index);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		// 1.7.1: "Couldn't load your collections", and nothing else on the page
		Assert.True(vm.State.IsLoaded, $"{vm.State.Title}: {vm.State.Message}");
		var titles = vm.Rows.Select(r => r.Title).ToList();
		Assert.Equal(["The server's collections", "Playlists"], titles.Take(2));
		Assert.Contains("Franchises", titles);
		Assert.Contains("Keywords", titles);
		// the builder that threw: 1.7.3 (P0-3) makes genre mixes on demand only, never a row
		Assert.DoesNotContain("Genre mixes", titles);
		Assert.Equal(8, SmartCollectionCatalog.Find(index.Current!, "genres:movie:comedy+horror", Noon)!.ItemIds.Count);

		// the saved index keeps each title once, and says so with a count only
		var ids = index.Current!.Entries.Select(x => x.Id).ToList();
		Assert.Equal(24, ids.Count);
		Assert.Equal(24, ids.Distinct().Count());
		Assert.Contains(log.Entries, x => x.Message == "Library index: the saved one had 1 repeated title; each is kept once");

		// Home: its Made by Nixie rows (1.7.1: none, the same throw caught)
		var home = new HomeViewModel(_client.Object, _messages, Commands(), _homeCache);
		await home.OnNavigatedTo(new object());
		Assert.True(home.State.IsLoaded);
		Assert.Contains(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.HighlyRated);
	}

	[Fact]
	public void GenreMixes_WithATitleListedTwice_DoNotThrow_AndStillFindThePair()
	{
		var entries = _films.Select(Entry).ToList();

		var made = new GenreMixBuilder().Build(BuildContext.For(Snapshot([.. entries, entries[0]]), Noon)).ToList(); // 1.7.1: ArgumentException

		var pair = Assert.Single(made);
		Assert.Equal("Comedy · Horror", pair.Title);
		Assert.Equal(8, pair.ItemIds.Count);
		Assert.Equal(8, pair.ItemIds.Distinct().Count());
	}

	// a builder that makes one collection, then throws (part way, as a bug would)
	private sealed class ThrowingBuilder(SmartCollectionKind kind) : ISmartCollectionBuilder
	{
		public SmartCollectionKind Kind => kind;

		public IEnumerable<SmartCollection> Build(BuildContext context)
		{
			yield return new SmartCollection("country:made-first", kind, "Films from Film 00", "made before the throw", false, [context.Index.Entries[0].Id]);
			throw new InvalidOperationException("Film 00 went wrong");
		}
	}

	[Fact]
	public void ABuilderThatThrows_LeavesOutItsKindOnly_AndIsSaidOnceWithNoTitles()
	{
		var index = Snapshot([.. _films.Select(Entry)]);
		var log = new CoreListLogger<RepeatedTitleTests>();
		IReadOnlyList<ISmartCollectionBuilder> builders = [new DecadeBuilder(), new ThrowingBuilder(SmartCollectionKind.Country), new GenreMixBuilder()];

		var first = SmartCollectionCatalog.BuildAll(builders, index, Noon, null, null, null, log);
		var again = SmartCollectionCatalog.BuildAll(builders, index, Noon, null, null, null, log);

		Assert.DoesNotContain(first, x => x.Kind == SmartCollectionKind.Country); // not even what it made before it threw
		Assert.Contains(first, x => x.Kind == SmartCollectionKind.Decade); // the kinds before it
		Assert.Contains(first, x => x.Kind == SmartCollectionKind.GenreMix); // and after it
		Assert.Equal(first.Select(x => x.Id), again.Select(x => x.Id));
		var said = Assert.Single(log.Entries); // once, not at every build
		Assert.Equal(LogLevel.Warning, said.Level);
		Assert.Equal("Made by Nixie: the Country collections couldn't be made (InvalidOperationException); the other kinds still are", said.Message);
		Assert.Null(said.Exception); // its message could name a title
	}

	[Fact]
	public async Task ABuilderThatThrowsOnTheSavedIndex_ThePageStillLoads_WithTheOtherKinds()
	{
		// a saved title with no tags list (null, not empty): the keywords' builder throws on it. 1.7.3: 1.7.2's test nulled the
		// countries, whose kind is made on demand only now
		// fix round (BH-173-q3): a null list loads as an empty one now; a null tag in it still throws in the keywords' builder
		await SaveIndex(2, entries => entries[1]!["Tags"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null));
		using var index = Index();
		var vm = Page(index);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		Assert.True(vm.State.IsLoaded, $"{vm.State.Title}: {vm.State.Message}");
		var titles = vm.Rows.Select(r => r.Title).ToList();
		Assert.Equal(["The server's collections", "Playlists"], titles.Take(2));
		Assert.Contains("Franchises", titles);
		Assert.Contains(vm.Rows, r => r.Title == "Picks");
		Assert.DoesNotContain(vm.Rows, r => r.Kind == SmartCollectionKind.Keyword); // twelve heists, but its builder threw
		Assert.Contains(MadeByNixieLog.Instance.Throws, x => x.StartsWith("Made by Nixie: the Keyword collections couldn't be made (", StringComparison.Ordinal)); // and it is seen
	}

	[Fact]
	public async Task MadeByNixieThatCannotBeMade_NeverFailsThePage_TheServersSetsAndPlaylistsShow()
	{
		await SaveIndex(2, _ => { });
		var settings = new Mock<ILocalSettingsService>();
		settings.Setup(x => x.ReadSetting(CollectionSettingKeys.KindsOff.Name, It.IsAny<string>())).Throws(new InvalidOperationException("Film 00"));
		var log = new CoreListLogger<CollectionsViewModel>();
		using var index = Index();
		var vm = Page(index, settings.Object, log);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		Assert.True(vm.State.IsLoaded, $"{vm.State.Title}: {vm.State.Message}"); // 1.7.1: the error card
		Assert.Equal(["The server's collections", "Playlists"], vm.Rows.Select(r => r.Title));
		Assert.Contains(log.Entries, x => x.Level == LogLevel.Warning && x.Message.StartsWith("Collections: Made by Nixie couldn't be made (InvalidOperationException)", StringComparison.Ordinal));
		Assert.DoesNotContain(log.Entries, x => x.Message.Contains("Film 00", StringComparison.Ordinal));
	}

	[Fact]
	public async Task MadeByNixieThatFailsToShow_NeverFailsThePage_TheServersSetsAndPlaylistsShow()
	{
		await SaveIndex(2, _ => { });
		using var index = Index();
		var vm = Page(index, making: _ => Task.FromException(new InvalidOperationException("a show that failed")));

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		Assert.True(vm.State.IsLoaded, $"{vm.State.Title}: {vm.State.Message}");
		Assert.Equal(["The server's collections", "Playlists"], vm.Rows.Select(r => r.Title));
	}

	[Fact]
	public async Task TheServerFailing_StillShowsTheErrorCard_InWordsForAUserWhoIsNotItsAdmin()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync((IReadOnlyList<BaseItemDto>?)null);
		using var index = Index();
		var vm = Page(index);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		Assert.True(vm.State.IsFailed);
		Assert.Equal("Couldn't load your collections", vm.State.Title);
		Assert.EndsWith(" just now. Check this PC's connection, then select Try again.", vm.State.Message);
		Assert.DoesNotContain("server is running", vm.State.Message);
	}
}
