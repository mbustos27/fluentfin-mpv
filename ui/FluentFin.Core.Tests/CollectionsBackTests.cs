using System.Reactive.Subjects;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.WebSockets;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.5 (review Major 2): the Collections page Back shows reads its own collections again: a saved filter removed on its
/// page (which goes Back by itself) is gone, a pin shows; when the server's may have changed (stale), its box sets and
/// playlists are asked again behind the page. 1.7.4's kept page showed the removed filter until R.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class CollectionsBackTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-collectionsback175-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Guid _moviesId = Guid.NewGuid();
	private List<BaseItemDto> _playlists = [];

	static CollectionsBackTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public CollectionsBackTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-0000000001a5"));
		var movies = new BaseItemDto { Id = _moviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetIndexCheck(It.IsAny<CancellationToken>())).ReturnsAsync("0:none");
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000001a5"), Name = "Box", Type = BaseItemDto_Type.BoxSet, ChildCount = 2 }],
		});
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [.. _playlists] });
	}

	public void Dispose()
	{
		Core.Outside.OutsideListsSnapshot.Shared = Core.Outside.OutsideListsSnapshot.Empty;
		CollectionNames.ForgetBoxSets();
		_homeCache.Dispose();
		TestFolder.Delete(_folder);
	}

	private async Task<(CollectionsViewModel Vm, SmartCollectionStore Store, LibraryIndex Index, UiThread Ui)> Opened()
	{
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		var ui = new UiThread();
		var vm = await ui.Invoke(() => new CollectionsViewModel(_client.Object, new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>()), index, store));
		return (vm, store, index, ui);
	}

	private static async Task Open(CollectionsViewModel vm, LibraryIndex index, UiThread ui)
	{
		await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
		await vm.Reading.WaitAsync(TimeSpan.FromSeconds(20));
		await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
	}

	private static Task<bool> Shows(CollectionsViewModel vm, UiThread ui, string title) => ui.Invoke(() => vm.Rows.SelectMany(r => r.Tiles).Any(t => t.Title == title));

	// the reviewer's probe A
	[Fact]
	public async Task ASavedFilterRemovedOnItsPage_IsGone_FromTheCollectionsPageBackShows()
	{
		var (vm, store, index, ui) = await Opened();
		using (index)
		using (ui)
		{
			var saved = await store.SaveFilter("Probe filter", new SavedFilter(_moviesId, "Movies", Unwatched: true));
			await Open(vm, index, ui);
			Assert.True(await ui.Invoke(() => vm.CanKeep));
			Assert.True(await Shows(vm, ui, "Probe filter"));

			// opened, Remove: CollectionViewModel.RemoveSaved removes it and goes Back (the kept page comes back)
			await ui.Run(vm.OnNavigatedFrom);
			Assert.True(await store.RemoveSaved(saved.Id));
			await ui.Run(() => ((IKeptOnBack)vm).Returned(new CollectionsPlace(CollectionsSection.All), stale: false)).WaitAsync(TimeSpan.FromSeconds(20));

			Assert.False(await Shows(vm, ui, "Probe filter"));
			Assert.True(await Shows(vm, ui, "Box")); // the rest as it was
		}
	}

	[Fact]
	public async Task AStaleBack_AsksForTheServersPlaylistsAgain_AFreshOneAsksNothing()
	{
		var (vm, _, index, ui) = await Opened();
		using (index)
		using (ui)
		{
			await Open(vm, index, ui);
			Assert.False(await Shows(vm, ui, "Saved playlist"));

			// a playlist saved on the page Back left (Save as playlist): the server lists it now
			_playlists = [new BaseItemDto { Id = Guid.NewGuid(), Name = "Saved playlist", Type = BaseItemDto_Type.Playlist, ChildCount = 3 }];
			await ui.Run(vm.OnNavigatedFrom);
			_client.Invocations.Clear();
			await ui.Run(() => ((IKeptOnBack)vm).Returned(new CollectionsPlace(CollectionsSection.All), stale: false)).WaitAsync(TimeSpan.FromSeconds(20));
			Assert.DoesNotContain(_client.Invocations, x => x.Method.Name is nameof(IJellyfinClient.GetPlaylists) or nameof(IJellyfinClient.GetBoxSets));
			Assert.False(await Shows(vm, ui, "Saved playlist"));

			await ui.Run(vm.OnNavigatedFrom);
			await ui.Run(() => ((IKeptOnBack)vm).Returned(new CollectionsPlace(CollectionsSection.All), stale: true)).WaitAsync(TimeSpan.FromSeconds(20));
			Assert.True(await Shows(vm, ui, "Saved playlist"));
		}
	}
}
