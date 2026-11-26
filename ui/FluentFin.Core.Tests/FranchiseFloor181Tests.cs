using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8.1 (the owner's library, fix 7; the owner's yes: "three or more films"): the franchises Nixie publishes are those of
/// three films or more and those the user started (a film of it played or begun); 1,227 on the owner's library, most of them
/// pairs, were "too many". A pair is still made when asked for by its id (a pin: the Collections page finds pins so, and a pin
/// never goes), and Next in groups the films itself. The census line and See all say the same count.
/// </summary>
[Collection(CollectionNamesSerial.Name)] // SmartCollectionCatalog.Log and SmartCollectionServices are static
public sealed class FranchiseFloor181Tests : IDisposable
{
	private static readonly DateTimeOffset Tuesday = new(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-181-franchises-" + Guid.NewGuid().ToString("N"));
	private HomeCache? _home;

	static FranchiseFloor181Tests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public void Dispose()
	{
		SmartCollectionCatalog.Log = null;
		SmartCollectionServices.Index = null;
		SmartCollectionServices.Store = null;
		_home?.Dispose();
		TestFolder.Delete(_folder);
	}

	private static IndexEntry Film(string name, int year, string franchise, bool played = false, int plays = 0, DateTimeOffset? last = null) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000181"),
		Name = name,
		Kind = IndexKind.Movie,
		Year = year,
		TmdbCollection = franchise,
		Played = played,
		PlayCount = plays,
		LastPlayed = last,
		Rating = 7.0,
		PrimaryTag = "p",
	};

	// a trilogy nobody started; a pair nobody started; a pair begun (a film started, not finished); a pair with a film finished
	private static (List<IndexEntry> All, string Trilogy, string Pair, string Begun, string Finished) Library()
	{
		List<IndexEntry> all =
		[
			Film("Kestrel Bay", 1990, "9101"), Film("Kestrel Bay II", 1993, "9101"), Film("Kestrel Bay III", 1996, "9101"),
			Film("Glass Harbor", 2001, "9102"), Film("Glass Harbor Returns", 2004, "9102"),
			Film("Iron Angels", 2010, "9104", plays: 1, last: Tuesday.AddDays(-4)), Film("Iron Angels 2", 2013, "9104"),
			Film("Dune", 2021, "9105", played: true), Film("Dune: Part Two", 2024, "9105"),
		];
		return (all, "franchise:9101", "franchise:9102", "franchise:9104", "franchise:9105");
	}

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries) => new([], [.. entries], new Dictionary<Guid, SeriesPlays>(), HasPeople: true, Tuesday);

	[Fact]
	public void APairNobodyStarted_IsNotPublished_ButIsMadeByItsId_AndAStartedOneOrThreeFilmsAre()
	{
		var (all, trilogy, pair, begun, finished) = Library();
		var index = Index(all);

		var published = SmartCollectionCatalog.BuildAll(index, Tuesday).Where(x => x.Kind == SmartCollectionKind.Franchise).Select(x => x.Id).ToList();

		Assert.DoesNotContain(pair, published); // not on the Collections page, in See all or in the count
		Assert.Contains(trilogy, published); // three films
		Assert.Contains(begun, published); // a film of it begun
		Assert.Contains(finished, published); // a film of it finished
		// a pin of the pair: the Collections page finds a pin not published by its id (PinnedOnDemand), so it never goes
		var pinned = SmartCollectionCatalog.Find(index, pair, Tuesday);
		Assert.NotNull(pinned);
		Assert.Equal(2, pinned.ItemIds.Count);
		Assert.False(pinned.Ended);
		// a library's presets strip and an on-demand build still have it (not the pages' publishing)
		Assert.Contains(SmartCollectionCatalog.BuildKinds(index, Tuesday, new HashSet<SmartCollectionKind> { SmartCollectionKind.Franchise }), x => x.Id == pair);
	}

	[Fact]
	public void APair_StillGivesNextIn_ByItsOwnGrouping()
	{
		var (all, _, _, _, finished) = Library();
		var context = BuildContext.For(Index(all), Tuesday);

		var next = Assert.Single(new NextInFranchiseBuilder().Build(context));

		Assert.Equal("next:franchise:9105", next.Id); // Dune: Part Two, after Dune
		Assert.Equal(2, next.ItemIds.Count);
		Assert.Contains(finished, SmartCollectionCatalog.BuildAll(Index(all), Tuesday).Select(x => x.Id)); // and, started, it is published
	}

	[Fact]
	public async Task TheCensusAndSeeAll_SayTheSameNumberOfFranchises()
	{
		var (all, _, pair, _, _) = Library();
		var log = new CoreListLogger<FranchiseFloor181Tests>();
		SmartCollectionCatalog.Log = log;
		var (vm, _) = Page(all);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true));
		await vm.Reading;

		var census = log.Entries.Select(x => x.Message).Single(x => x.StartsWith("Made by Nixie census:", StringComparison.Ordinal));
		var franchise = System.Text.RegularExpressions.Regex.Match(census, @"Franchise (\d+)/(\d+)/(\d+)");
		Assert.True(franchise.Success, census);
		Assert.Equal(4, int.Parse(franchise.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)); // four of two films or more
		Assert.Equal(3, int.Parse(franchise.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)); // three published
		Assert.Equal(3, vm.GridTiles.Count); // See all: the same three
		Assert.DoesNotContain(vm.GridTiles, x => x.Nixie?.Id == pair);
	}

	// the Collections page on a fake server with these films (its index read through the mocked client)
	private (CollectionsViewModel Vm, SmartCollectionStore Store) Page(IReadOnlyList<IndexEntry> films)
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupGet(x => x.BaseUrl).Returns("http://server");
		client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000181"));
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		List<BaseItemDto> dtos = [.. films.Select(f => new BaseItemDto
		{
			Id = f.Id,
			Name = f.Name,
			Type = BaseItemDto_Type.Movie,
			ProductionYear = f.Year,
			CommunityRating = 7,
			ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["TmdbCollection"] = f.TmdbCollection! } },
			ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" } },
			UserData = new UserItemDataDto { Played = f.Played, PlayCount = f.PlayCount, LastPlayedDate = f.LastPlayed },
		})];
		client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. dtos.Skip(start).Take(limit)], TotalRecordCount = dtos.Count });
		client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var home = _home = new HomeCache(Path.Combine(_folder, "HomeCache"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
		var index = new LibraryIndex(client.Object, home, Path.Combine(_folder, "LibraryIndex"), new Subject<IOutboundWebSocketMessage>(), new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(client.Object, home, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return (new CollectionsViewModel(client.Object, commands, index, store), store);
	}
}
