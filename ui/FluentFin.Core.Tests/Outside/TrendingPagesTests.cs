using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Outside;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests.Outside;

/// <summary>
/// 1.7 trending on the pages the user sees: the Collections page's Trending, Popular, Most anticipated and Box office rows
/// (only those with a collection), their See all, a source turned off taking its rows away while the page shows, and Home's
/// "Trending in your library" (only while a source is on and it has titles).
/// </summary>
[Collection(CollectionNamesSerial.Name)]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrendingPagesTests : IDisposable
{
	private readonly string _folder = OutsideKit.TempFolder();
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly List<BaseItemDto> _films;
	private readonly TrendingRig _rig = new(trakt: true);

	static TrendingPagesTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public TrendingPagesTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000170"));
		_films = [Film("The Matrix", 1999, "603"), Film("Dune", 2021, "438631"), Film("Sintel", 2010, "45745")];
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => [.. ids.Select(id => _films.First(f => f.Id == id))]);
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });

		// Trakt: Dune and The Matrix trending (in that order), Sintel at the box office; nothing of the library popular
		_rig.Fake.Trakt["movies/trending"] = [("438631", null, null, "Dune"), ("1", null, null, "Elsewhere"), ("603", null, null, "The Matrix")];
		_rig.Fake.Trakt["shows/trending"] = [("2", null, "3", "Elsewhere show")];
		_rig.Fake.Trakt["movies/popular"] = [("4", null, null, "Elsewhere film")];
		_rig.Fake.Trakt["shows/popular"] = [];
		_rig.Fake.Trakt["movies/anticipated"] = [];
		_rig.Fake.Trakt["shows/anticipated"] = [];
		_rig.Fake.Trakt["movies/boxoffice"] = [("45745", null, null, "Sintel")];
	}

	public void Dispose()
	{
		TrendingServices.Collections = null;
		SmartCollectionServices.Index = null;
		SmartCollectionServices.Store = null;
		_homeCache.Dispose();
		_rig.Dispose();
		TestFolder.Delete(_folder); // 1.7.1: the index's people pass may still be saving behind a test that ended
	}

	private static BaseItemDto Film(string name, int year, string tmdb) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.Movie,
		ProductionYear = year,
		CommunityRating = 6.0f,
		RunTimeTicks = TimeSpan.FromMinutes(100).Ticks,
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = tmdb } },
		UserData = new UserItemDataDto { Played = false },
	};

	private async Task<(LibraryIndex Index, SmartCollectionStore Store, TrendingCollections Trending)> Services()
	{
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		await index.GetAsync();
		await _rig.Sources.RefreshAsync();
		var trending = new TrendingCollections(_rig.Sources, index, _homeCache);
		TrendingServices.Collections = trending;
		SmartCollectionServices.Index = index;
		return (index, store, trending);
	}

	private GlobalCommands Commands() => new(_navigation.Object, _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	[Fact]
	public async Task TheCollectionsPage_ShowsTheTrendingRows_WithTitlesInTheLibrary_AndHidesTheEmptyOnes()
	{
		var (index, store, _) = await Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;

		var titles = vm.Rows.Select(r => r.Title).ToList();
		Assert.Contains("Trending", titles);
		Assert.Contains("Box office this week", titles);
		Assert.DoesNotContain("Popular", titles);            // nothing of the library on it
		Assert.DoesNotContain("Most anticipated", titles);
		Assert.True(titles.IndexOf("Trending") < titles.IndexOf("Box office this week"));
		var trending = vm.Rows.Single(r => r.Title == "Trending");
		var tile = Assert.Single(trending.Tiles);
		Assert.Equal(("Trending films", "2 films"), (tile.Title, tile.Detail));
		Assert.Equal("Trending films, made by Nixie, trending, 2 films", tile.AutomationName);
		Assert.True(tile.IsNixie);
		Assert.Equal(["Dune", "The Matrix"], tile.Mosaic.Select(x => x.Name));

		// opening it: the shared Collection page, in Trakt's order, numbered
		vm.Open(tile);
		_navigation.Verify(x => x.NavigateTo(typeof(CollectionViewModel).FullName!, It.Is<object>(p => ((NixieCollection)p).Collection.Id == "trending:movie"), It.IsAny<bool>()), Times.Once);
		var page = new CollectionViewModel(_client.Object, Commands());
		await page.OnNavigatedTo(new NixieCollection(tile.Nixie!));
		Assert.Equal("Made by Nixie · Trending", page.Eyebrow);
		Assert.Equal("Trakt: watched right now", page.Why);
		Assert.Equal(["Dune", "The Matrix"], page.Entries.Select(e => e.Item.Dto.Name));

		// See all of the row: its collections as a grid
		var all = new CollectionsViewModel(_client.Object, Commands(), index, store);
		await all.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Trending, SeeAll: true));
		Assert.Equal("Trending", all.Title);
		Assert.Equal(["Trending films"], all.GridTiles.Select(t => t.Title));
	}

	[Fact]
	public async Task TurningTraktOff_TakesItsRowsAway_WhileThePageShows()
	{
		var (index, store, _) = await Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);
		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;
		Assert.Contains(vm.Rows, r => r.Title == "Trending");

		_rig.Hub.SetOn(OutsideSourceId.Trakt, false);
		for (var i = 0; i < 50 && vm.Rows.Any(r => r.Title == "Trending"); i++)
		{
			await Task.Delay(20);
		}

		Assert.DoesNotContain(vm.Rows, r => r.Title is "Trending" or "Box office this week");
		await vm.OnNavigatedFrom();
	}

	private HomeViewModel Home()
	{
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>())).ReturnsAsync([_films[2]]);
		return new HomeViewModel(_client.Object, _messages, Commands(), _homeCache);
	}

	[Fact]
	public async Task Home_ShowsTrendingInYourLibrary_InTraktsOrder_AndSnapshotsIt()
	{
		await Services();
		var home = Home();

		await home.OnNavigatedTo(new object());

		var row = Assert.Single(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
		Assert.Equal("Trending in your library", row.Title);
		Assert.Equal("Made by Nixie", row.Kicker);
		Assert.Equal(["Dune", "The Matrix"], row.Items.Select(i => i.Dto.Name));
		Assert.Single((await _homeCache.Get("http://server", _client.Object.UserId)).Snapshot!.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
	}

	[Fact]
	public async Task ListsThatCameInWhileAnotherPageShowed_AreOnHome_AtTheNextVisit_ThoughHomesSavedPageWasFresh()
	{
		// the harness's first run: Home loaded (Trakt off), Settings turned Trakt on, the lists came in, Home again
		await Services();
		_rig.Hub.SetOn(OutsideSourceId.Trakt, false);
		var home = Home();
		await home.OnNavigatedTo(new object());
		Assert.DoesNotContain(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
		await home.OnNavigatedFrom();
		Assert.True((await _homeCache.Get("http://server", _client.Object.UserId)).IsFresh);

		_rig.Hub.SetOn(OutsideSourceId.Trakt, true);
		await _rig.Sources.RefreshAsync();

		Assert.False((await _homeCache.Get("http://server", _client.Object.UserId)).IsFresh);
		var again = Home();
		await again.OnNavigatedTo(new object());
		Assert.Single(again.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
	}

	[Fact]
	public async Task Home_MatchesTheLists_OffItsOwnThread()
	{
		// a8a5e5d: matching builds lookups over the whole index (25,000 titles on a big library): never on Home's thread
		var (_, _, trending) = await Services();
		var threads = new System.Collections.Concurrent.ConcurrentBag<string?>();
		trending.Matching += () => threads.Add(Thread.CurrentThread.Name);
		using var ui = new UiThread();
		var home = await ui.Invoke(Home);

		await ui.Run(() => home.OnNavigatedTo(new object()));

		Assert.Single(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
		Assert.NotEmpty(threads);
		Assert.DoesNotContain("test-ui", threads);
	}

	[Fact]
	public async Task HomesTrendingRow_IsMadeAgain_WhenItsSourcesChange_ThoughItsTitlesStay()
	{
		// the review's #6: Home kept the old collection when its first titles were the same, so its why line (and See all)
		// still named TMDb after TMDb was turned off
		Assert.Null(_rig.Token.Save(TrendingFake.Token));
		_rig.Hub.SetOn(OutsideSourceId.TmdbTrending, true);
		_rig.Fake.Tmdb["movie"] = [(438631, "Dune"), (603, "The Matrix")];
		_rig.Fake.Tmdb["tv"] = [];
		await Services();
		await _rig.Sources.RefreshAsync();
		var home = Home();
		await home.OnNavigatedTo(new object());
		var row = Assert.Single(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
		Assert.Equal("Trakt: watched right now · TMDb: this week", row.Collection.Why);
		await home.OnNavigatedFrom();

		_rig.Hub.SetOn(OutsideSourceId.TmdbTrending, false);
		await home.OnNavigatedTo(new object());

		row = Assert.Single(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
		Assert.Equal(["Dune", "The Matrix"], row.Items.Select(i => i.Dto.Name));   // the same titles
		Assert.Equal("Trakt: watched right now", row.Collection.Why);
	}

	[Fact]
	public async Task Home_HasNoTrendingRow_WhileNoSourceIsOn_OrNoneOfItsTitlesIsInTheLibrary()
	{
		await Services();
		_rig.Hub.SetOn(OutsideSourceId.Trakt, false);
		var home = Home();
		await home.OnNavigatedTo(new object());
		Assert.DoesNotContain(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);

		_rig.Fake.Trakt["movies/trending"] = [("1", null, null, "Elsewhere")];
		_rig.Hub.SetOn(OutsideSourceId.Trakt, true);
		await _rig.Sources.RefreshAsync();
		var again = Home();
		await again.OnNavigatedTo(new object());
		Assert.DoesNotContain(again.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Trending);
	}
}
