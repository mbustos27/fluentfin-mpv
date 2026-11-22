using System.Reactive.Subjects;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.6: the saved collections and the library index on disk: per server and user, and gone on sign-out.</summary>
public sealed class SmartCollectionStoreTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-smart-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private Guid _user = Guid.NewGuid();

	public SmartCollectionStoreTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(() => _user);
	}

	public void Dispose()
	{
		_homeCache.Dispose();
		TestFolder.Delete(_folder); // 1.7.1: the index's people pass may still be saving behind a test that ended
	}

	private SmartCollectionStore NewStore() => new(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());

	[Fact]
	public async Task ASavedFilter_IsKept_AndComesBackAfterARestart()
	{
		var library = new BaseItemDto { Id = Guid.NewGuid(), Name = "[HUB] Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		var filter = new LibraryFilter { Unwatched = true };
		filter.Genres.Add("Comedy");
		filter.Genres.Add("Horror");

		var saved = await NewStore().SaveFilter("  Scary laughs ", library, filter);

		Assert.Equal("Scary laughs", saved.Title);
		Assert.Equal(SmartCollectionKind.Saved, saved.Kind);
		Assert.Equal("Your filter in Movies: Comedy, Horror · Unwatched", saved.Why);
		var again = Assert.Single(await NewStore().SavedAsync());
		Assert.Equal(saved.Id, again.Id);
		Assert.Equal(["Comedy", "Horror"], again.Filter!.Genres!);
		Assert.True(again.Filter.Unwatched);
	}

	[Fact]
	public async Task TheSameNameInTheSameLibrary_ReplacesTheEarlierOne()
	{
		var store = NewStore();
		var id = Guid.NewGuid();
		await store.SaveFilter("Mine", new SavedFilter(id, "Movies", Unwatched: true));
		await store.SaveFilter("mine", new SavedFilter(id, "Movies", Favorites: true));
		await store.SaveFilter("Mine", new SavedFilter(Guid.NewGuid(), "Shows"));

		var saved = await store.SavedAsync();

		Assert.Equal(2, saved.Count);
		Assert.True(saved.Single(x => x.Filter!.LibraryId == id).Filter!.Favorites);
		Assert.True(await store.RemoveSaved(saved[0].Id));
		Assert.Single(await store.SavedAsync());
	}

	[Fact]
	public async Task FavouritesAndSavedFilters_GoWhenTheUserSignsOut_AndAreNobodyElses()
	{
		var store = NewStore();
		await store.SetFavorite("franchise:2344", true);
		await store.SaveFilter("Mine", new SavedFilter(Guid.NewGuid(), "Movies"));
		var first = _user;

		_user = Guid.NewGuid(); // another user on the same PC
		Assert.Empty(await NewStore().FavoritesAsync());
		Assert.Empty(await NewStore().SavedAsync());

		_user = first;
		Assert.Equal(["franchise:2344"], await NewStore().FavoritesAsync());
		await _homeCache.Forget("http://server", first);
		await Task.Delay(200);
		Assert.Empty(await NewStore().FavoritesAsync());
		Assert.Empty(await store.SavedAsync());
	}

	[Fact]
	public async Task TheIndex_IsSaved_ReadBackAtTheNextStart_AndForgottenOnSignOut()
	{
		var movies = new BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		var film = new BaseItemDto { Id = Guid.NewGuid(), Name = "The Matrix", Type = BaseItemDto_Type.Movie, ProductionYear = 1999 };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [film], TotalRecordCount = 1 });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		var folder = Path.Combine(_folder, "LibraryIndex");
		using (var index = new LibraryIndex(_client.Object, _homeCache, folder, _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System))
		{
			var read = await index.GetAsync();
			Assert.Equal("The Matrix", Assert.Single(read!.Entries).Name);
			await index.Settled; // 1.7.1: saved behind the index it says is in (and again with its people)
		}

		// the next start: the saved index at once, with the server not answering
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync((IReadOnlyList<BaseItemDto>?)null);
		using (var index = new LibraryIndex(_client.Object, _homeCache, folder, _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System))
		{
			var saved = await index.GetAsync();
			Assert.Equal("The Matrix", Assert.Single(saved!.Entries).Name);

			await _homeCache.Forget("http://server", _user);
			await Task.Delay(200);
			Assert.Null(index.Current);
			Assert.Empty(Directory.GetFiles(folder));
			Assert.Empty(Directory.GetFiles(folder, "*", SearchOption.AllDirectories)); // 1.7.1 review M2: the note of its last read too
		}
	}
}
