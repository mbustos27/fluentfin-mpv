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

/// <summary>The tests that set the app's shared outside lists run alone: other tests build collections from them.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedOutsideLists
{
	public const string Name = "Shared outside lists";
}

/// <summary>
/// 1.7 keyless: the Collections page with Wikidata's awards: an Awards row after the other kinds, always ending in See all,
/// See all showing the awards as a grid, an award opening on the Collection page, and the row arriving when the lists do
/// while the page is open.
/// </summary>
[Collection(SharedOutsideLists.Name)]
public sealed class OutsideCollectionsPageTests : IDisposable
{
	private static readonly Guid MoviesId = Guid.NewGuid();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-outside-page-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly List<BaseItemDto> _films;

	static OutsideCollectionsPageTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public OutsideCollectionsPageTests()
	{
		OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty;
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000170"));
		_films = [Film("Oldest winner", 1930, "880001"), Film("Newer winner", 1990, "880002"), Film("Not a winner", 2001, "880099")];
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
	}

	public void Dispose()
	{
		OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty;
		_homeCache.Dispose();
		if (Directory.Exists(_folder))
		{
			Directory.Delete(_folder, recursive: true);
		}
	}

	private static BaseItemDto Film(string name, int year, string tmdb) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.Movie,
		ProductionYear = year,
		ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = tmdb } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" + name.Length } },
		UserData = new UserItemDataDto { Played = false },
	};

	private static OutsideListsSnapshot Awards() => OutsideListsSnapshot.Empty with
	{
		Lists =
		[
			new OutsideList("award-best-picture", OutsideSourceId.Wikidata, OutsideListKind.Award, "Best Picture winners", "Academy Award for Best Picture winners, from Wikidata, by year",
				[new(new OutsideRef(Tmdb: "880001", Kind: IndexKind.Movie), 1930), new(new OutsideRef(Tmdb: "880002", Kind: IndexKind.Movie), 1990), new(new OutsideRef(Tmdb: "770000", Kind: IndexKind.Movie), 2000)]),
		],
	};

	private CollectionsViewModel Page(out LibraryIndex index)
	{
		index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		var commands = new GlobalCommands(_navigation.Object, _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return new CollectionsViewModel(_client.Object, commands, index, store);
	}

	[Fact]
	public async Task The_Awards_row_shows_the_awards_the_library_has_and_always_ends_in_See_all()
	{
		OutsideListsSnapshot.Shared = Awards();
		var vm = Page(out _);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;

		var awards = Assert.Single(vm.Rows, r => r.Kind == SmartCollectionKind.Award);
		Assert.Equal("Awards", awards.Title);
		Assert.True(awards.HasSeeAll);
		var tile = Assert.Single(awards.Tiles);
		Assert.Equal("Best Picture winners, made by Nixie, award, 2 films", tile.AutomationName);
		Assert.Equal(vm.Rows.Count - 1, vm.Rows.IndexOf(awards)); // after the other kinds (no Saved row here)
		await vm.OnNavigatedFrom();
	}

	[Fact]
	public async Task See_all_shows_the_awards_as_a_grid_and_an_award_opens_on_the_Collection_page()
	{
		OutsideListsSnapshot.Shared = Awards();
		var vm = Page(out _);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Award, SeeAll: true));
		await vm.Reading;

		Assert.Equal("Awards", vm.Title);
		var tile = Assert.Single(vm.GridTiles);
		Assert.Equal("Made by Nixie · Award", tile.Nixie!.Eyebrow);
		Assert.Contains("from Wikidata", tile.Nixie.Why, StringComparison.Ordinal);
		vm.Open(tile);
		_navigation.Verify(x => x.NavigateTo(It.IsAny<string>(), It.Is<object>(p => p is NixieCollection), It.IsAny<bool>()), Times.Once);
		await vm.OnNavigatedFrom();
	}

	[Fact]
	public async Task The_row_arrives_when_the_lists_do_while_the_page_is_open_and_goes_when_Wikidata_is_turned_off()
	{
		var vm = Page(out _);
		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;
		Assert.DoesNotContain(vm.Rows, r => r.Kind == SmartCollectionKind.Award);

		OutsideListsSnapshot.Shared = Awards();
		Assert.True(await Eventually(() => vm.Rows.Any(r => r.Kind == SmartCollectionKind.Award)));

		OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty;
		Assert.True(await Eventually(() => vm.Rows.All(r => r.Kind != SmartCollectionKind.Award)));

		// left: the page no longer follows the lists
		await vm.OnNavigatedFrom();
		OutsideListsSnapshot.Shared = Awards();
		await Task.Delay(300);
		Assert.DoesNotContain(vm.Rows, r => r.Kind == SmartCollectionKind.Award);
	}

	private static async Task<bool> Eventually(Func<bool> condition)
	{
		for (var i = 0; i < 100; i++)
		{
			if (condition())
			{
				return true;
			}

			await Task.Delay(50);
		}

		return condition();
	}
}
