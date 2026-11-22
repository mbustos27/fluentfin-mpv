using System.Reactive.Subjects;
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
/// Nixie 1.6: the Collections page (with and without the server's views), a Nixie collection on the Collection page (its
/// eyebrow, why line, Favourite, Save as playlist and the server's no), "Also in your library", Home's Made by Nixie rows and
/// their snapshot, and what Narrator hears.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class CollectionsPageTests : IDisposable
{
	private static readonly Guid MoviesId = Guid.NewGuid();
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-collections-" + Guid.NewGuid().ToString("N"));
	private readonly Subject<IOutboundWebSocketMessage> _messages = new();
	private readonly HomeCache _homeCache;
	private readonly Mock<IJellyfinClient> _client = new();
	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly List<BaseItemDto> _films;

	static CollectionsPageTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public CollectionsPageTests()
	{
		_homeCache = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);
		CollectionNames.RememberBoxSets([]); // review of 1.6.1 (F2): a list read, as the page reads it
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000160"));
		_films =
		[
			Film("The Matrix", 1999, "2344", 8.3),
			Film("The Matrix Reloaded", 2003, "2344", 7.1),
			Film("The Matrix Revolutions", 2003, "2344", 6.8),
			Film("Amélie", 2001, null, 7.9),
			Film("Night of the Living Dead", 1968, "261590", 7.6),
			Film("Sintel", 2010, null, 7.3),
		];
		var movies = new BaseItemDto { Id = MoviesId, Name = "[HUB] Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 });
		_client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => [.. ids.Select(id => _films.First(f => f.Id == id))]);
	}

	public void Dispose()
	{
		SmartCollectionServices.Index = null;
		SmartCollectionServices.Store = null;
		CollectionNames.ForgetBoxSets();
		_homeCache.Dispose();
		TestFolder.Delete(_folder); // 1.7.1: the index's people pass may still be saving behind a test that ended
	}

	private static BaseItemDto Film(string name, int year, string? collection, double rating) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.Movie,
		ProductionYear = year,
		CommunityRating = (float)rating,
		RunTimeTicks = TimeSpan.FromMinutes(120).Ticks,
		ProviderIds = collection is null ? null : new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["TmdbCollection"] = collection } },
		ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" + name.Length } },
		UserData = new UserItemDataDto { Played = false },
	};

	private static BaseItemDto BoxSet(string name, string? tmdb, int count, int unplayed) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.BoxSet,
		ChildCount = count,
		UserData = new UserItemDataDto { UnplayedItemCount = unplayed },
		ProviderIds = tmdb is null ? null : new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = tmdb } },
	};

	private (LibraryIndex Index, SmartCollectionStore Store) Services()
	{
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		return (index, store);
	}

	private GlobalCommands Commands() => new(_navigation.Object, _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	[Fact]
	public async Task ThePage_ListsTheServersSets_Playlists_AndMadeByNixie_ByKind()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("[HUB] Marvel", null, 6, 3)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = "[HUB] Mine", Type = BaseItemDto_Type.Playlist, ChildCount = 4 }] });
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await vm.Reading;

		Assert.True(vm.State.IsLoaded);
		Assert.Equal(["Your server's collections", "Playlists", "Picks", "Franchises"], vm.Rows.Select(r => r.Title));
		var marvel = vm.Rows[0].Tiles[0];
		Assert.Equal(("Marvel", "6 films · 3 left"), (marvel.Title, marvel.Detail));
		Assert.Equal("Marvel, collection, 6 films, 3 left", marvel.AutomationName);
		Assert.False(marvel.IsNixie);
		// a playlist keeps its name: people name those themselves
		Assert.Equal("[HUB] Mine", vm.Rows[1].Tiles[0].Title);
		Assert.Equal("Made by Nixie", vm.Rows[2].Heading);
		var matrix = Assert.Single(vm.Rows[3].Tiles);
		Assert.True(matrix.IsNixie);
		Assert.Equal("The Matrix, made by Nixie, franchise, 3 films", matrix.AutomationName);
		Assert.Equal(3, matrix.Mosaic.Count);
		Assert.Same(vm.Rows[2], vm.FocusRow); // opened at Made by Nixie
	}

	// The 1.7.1 blocker: the first read's own news (Changed) shows its index first, on the UI thread, before the read's own
	// continuation (as in the app). The read saw that show begun and was over before its rows were made, so Reading (what the
	// tests above wait for) was done with no Made by Nixie on the page: a race that a cold start (the test above alone, or early
	// in a run) lost every time. Here that show is held: the read is over only when it is done.
	[Fact]
	public async Task AFirstRead_IsOverOnlyWhenItsRowsAreIn_WhenTheIndexsNewsIsShowingThemAlready()
	{
		var people = new TaskCompletionSource();
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int _, int _, bool _, CancellationToken _) =>
			{
				await people.Task;
				return new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 };
			});
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("[HUB] Marvel", null, 6, 3)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var ui = new UiThread();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store)
		{
			// the read's titles, whole (not a page so far): the first show of them is held
			Making = snapshot => snapshot.Reading is null && held.TrySetResult() ? release.Task : Task.CompletedTask,
		};
		try
		{
			await ui.Run(() => vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All))).WaitAsync(TimeSpan.FromSeconds(20));
			await held.Task.WaitAsync(TimeSpan.FromSeconds(20));
			var reading = vm.Reading;

			Assert.NotSame(reading, await Task.WhenAny(reading, Task.Delay(300))); // not over while its rows are being made
			release.SetResult();
			await reading.WaitAsync(TimeSpan.FromSeconds(20));
			Assert.Equal(["Your server's collections", "Picks", "Franchises"], await ui.Invoke(() => vm.Rows.Select(r => r.Title).ToList()));
			Assert.False(await ui.Invoke(() => vm.IsReadingIndex));
		}
		finally
		{
			release.TrySetResult();
			people.TrySetResult();
			await index.Settled.WaitAsync(TimeSpan.FromSeconds(20));
			await ui.Run(vm.OnNavigatedFrom).WaitAsync(TimeSpan.FromSeconds(5));
			await ui.Invoke(() => 0).WaitAsync(TimeSpan.FromSeconds(5));
			index.Dispose();
		}
	}

	[Fact]
	public async Task WithoutTheServersViews_ThePageStillShowsWhatNixieMade()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(CollectionsSection.Server);

		Assert.True(vm.State.IsLoaded);
		Assert.Equal(["Picks", "Franchises"], vm.Rows.Select(r => r.Title));
		Assert.Same(vm.Rows[0], vm.FocusRow); // no server row: the first there is
	}

	[Fact]
	public async Task AFranchiseTheAdminBoxed_ShowsOnce_AsTheBoxSet()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("The Matrix Collection", "2344", 2, 2)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));
		await vm.Reading;

		Assert.Equal("The Matrix", vm.Rows[0].Tiles[0].Title); // TMDb named it: " Collection" goes
		Assert.DoesNotContain(vm.Rows, r => r.Title == "Franchises");
	}

	[Fact]
	public async Task AFirstReadOfABigLibrary_LeavesThePageLoaded_AndFillsMadeByNixieWhenItIsIn()
	{
		var gate = new TaskCompletionSource();
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.Returns(async (BaseItemDto _, int start, int limit, bool _, CancellationToken _) =>
			{
				await gate.Task;
				return new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count };
			});
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("[HUB] Marvel", null, 6, 3)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		// the server's row at once, the page loaded (not a spinner for the minutes a 25,000-film library takes), "reading" said
		Assert.True(vm.State.IsLoaded);
		Assert.True(vm.IsReadingIndex);
		Assert.Equal(["Your server's collections"], vm.Rows.Select(r => r.Title));

		gate.SetResult();
		await vm.Reading;

		Assert.False(vm.IsReadingIndex);
		Assert.Contains(vm.Rows, r => r.Title == "Franchises");
	}

	[Fact]
	public async Task AFailedFirstRead_TakesTheReadingLineAway()
	{
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync((IReadOnlyList<BaseItemDto>?)null);
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("Marvel", null, 6, 3)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));
		await vm.Reading;

		Assert.True(vm.State.IsLoaded);
		Assert.False(vm.IsReadingIndex);
		Assert.Equal(["Your server's collections"], vm.Rows.Select(r => r.Title));
	}

	[Fact]
	public async Task NothingAnywhere_SaysSo()
	{
		_films.Clear();
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		Assert.Equal(LoadStatus.Empty, vm.State.Status);
		Assert.Equal("No collections yet", vm.State.Title);
	}

	[Fact]
	public async Task OpenCollections_OpensThePageAtItsSection()
	{
		Commands().OpenCollections(CollectionsSection.Playlists);

		_navigation.Verify(x => x.NavigateTo(typeof(CollectionsViewModel).FullName!, new CollectionsPlace(CollectionsSection.Playlists), It.IsAny<bool>()), Times.Once);
		await Task.CompletedTask;
	}

	private async Task<(CollectionViewModel Vm, SmartCollection Matrix)> NixiePage()
	{
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		SmartCollectionServices.Store = store;
		var snapshot = await index.GetAsync();
		var matrix = SmartCollectionCatalog.BuildAll(snapshot!, DateTimeOffset.Now).Single(x => x.Kind == SmartCollectionKind.Franchise);
		var vm = new CollectionViewModel(_client.Object, Commands());
		await vm.OnNavigatedTo(new NixieCollection(matrix));
		return (vm, matrix);
	}

	[Fact]
	public async Task ANixieCollection_OpensOnTheCollectionPage_WithItsEyebrowAndWhy()
	{
		var (vm, matrix) = await NixiePage();

		Assert.True(vm.State.IsLoaded);
		Assert.Equal("The Matrix", vm.Title);
		Assert.Equal("Made by Nixie · Franchise", vm.Eyebrow);
		Assert.Equal("Your films that TMDb lists in one collection, in release order", vm.Why);
		Assert.Equal(["The Matrix", "The Matrix Reloaded", "The Matrix Revolutions"], vm.Entries.Select(e => e.Item.Dto.Name));
		Assert.Equal("1", vm.Entries[0].PositionText); // its order means something: numbered
		Assert.True(vm.CanSaveAsPlaylist);
		_client.Verify(x => x.GetItem(It.IsAny<Guid>()), Times.Never); // nothing asked of the server about the collection itself
	}

	[Fact]
	public async Task ANixieCollection_HasNoWatchedToggle_AndWritesNothingButSave()
	{
		// review Major 4: the whole set's Watched marked each title with its own request, with no way to cancel; a Nixie
		// collection's only write is "Save as playlist"
		var (vm, _) = await NixiePage();
		var before = _client.Invocations.Count;

		Assert.False(vm.CanMarkWatched);
		await vm.SetWatched(true);

		Assert.DoesNotContain(_client.Invocations.Skip(before), i => i.Method.Name is nameof(IJellyfinClient.SetPlayed) or nameof(IJellyfinClient.SetUserData));
	}

	[Fact]
	public async Task AServersBoxSet_KeepsItsWatchedToggle()
	{
		var set = BoxSet("Marvel", null, 2, 2);
		_client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [_films[0], _films[1]] });
		var vm = new CollectionViewModel(_client.Object, Commands());

		await vm.OnNavigatedTo(set);

		Assert.True(vm.CanMarkWatched);
	}

	// 1.6 integration (the final pass's then-160-collections run): each stream left the eyebrow and the why line to the other's
	// header, so the merged page drew neither, and a Nixie collection looked like a server's. The page draws them now, from the
	// view model's Eyebrow and Why, and only on a Nixie collection (a server's set says its kind in the summary line).
	[Fact]
	public async Task TheCollectionPage_ShowsANixieCollectionsEyebrowAndWhy_AndNotAServersSets()
	{
		var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "FluentFin", "Views", "CollectionPage.xaml"));
		string Element(string name)
		{
			var at = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
			Assert.True(at >= 0, $"{name} is on the page");
			var element = xaml[..at];
			element = xaml[element.LastIndexOf('<')..];
			return element[..element.IndexOf("/>", StringComparison.Ordinal)];
		}

		var eyebrow = Element("EyebrowText");
		Assert.Contains("Text=\"{x:Bind ViewModel.Eyebrow, Mode=OneWay}\"", eyebrow);
		Assert.Contains("Visibility=\"{x:Bind ViewModel.IsNixie, Mode=OneWay}\"", eyebrow);
		var why = Element("WhyText");
		Assert.Contains("Text=\"{x:Bind ViewModel.Why, Mode=OneWay}\"", why);
		Assert.Contains("Visibility=\"{x:Bind local:CollectionPage.Shown(ViewModel.Why), Mode=OneWay}\"", why);
		// read in order: the eyebrow before the title, the why line after the summary
		Assert.True(xaml.IndexOf("x:Name=\"EyebrowText\"", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"TitleText\"", StringComparison.Ordinal));
		Assert.True(xaml.IndexOf("x:Name=\"WhyText\"", StringComparison.Ordinal) > xaml.IndexOf("x:Name=\"SummaryText\"", StringComparison.Ordinal));

		var (nixie, _) = await NixiePage();
		Assert.True(nixie.IsNixie);
		Assert.Equal("Made by Nixie · Franchise", nixie.Eyebrow);
		Assert.NotEqual("", nixie.Why);

		var set = BoxSet("Marvel", null, 2, 2);
		_client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [_films[0], _films[1]] });
		var server = new CollectionViewModel(_client.Object, Commands());
		await server.OnNavigatedTo(set);
		Assert.False(server.IsNixie);
		Assert.Equal("", server.Why);
	}

	[Fact]
	public void TheCollectionPage_HidesTheWatchedToggle_ByTheViewModel()
	{
		var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "ui", "FluentFin", "Views", "CollectionPage.xaml"));
		var toggle = xaml[xaml.IndexOf("x:Name=\"WatchedToggle\"", StringComparison.Ordinal)..];
		toggle = toggle[..toggle.IndexOf('>', StringComparison.Ordinal)];
		Assert.Contains("Visibility=\"{x:Bind ViewModel.CanMarkWatched, Mode=OneWay}\"", toggle);
	}

	private static string RepoRoot()
	{
		var dir = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(dir, "ui", "FluentFin.sln")))
		{
			dir = Path.GetDirectoryName(dir)!;
		}

		return dir;
	}

	[Fact]
	public async Task ANixieCollectionsFavourite_IsKeptOnThisPc()
	{
		var (vm, matrix) = await NixiePage();

		await vm.SetFavorite(true);

		Assert.True(vm.IsFavorite);
		Assert.Contains(matrix.Id, await SmartCollectionServices.Store!.FavoritesAsync());
		_client.Verify(x => x.SetIsFavorite(It.IsAny<BaseItemDto>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public async Task SaveAsPlaylist_CopiesTheCollectionInItsOrder_AndOpensIt()
	{
		var (vm, _) = await NixiePage();
		var made = Guid.NewGuid();
		IReadOnlyList<Guid>? sent = null;
		_client.Setup(x => x.CreatePlaylist("The Matrix", It.IsAny<IReadOnlyList<Guid>>()))
			.Callback((string _, IReadOnlyList<Guid> ids) => sent = ids).ReturnsAsync(new PlaylistCreation(made, 200, null));

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		Assert.Equal(_films.Take(3).Select(f => f.Id!.Value), sent);
		Assert.Equal("Saved to your playlists as “The Matrix”.", vm.PlaylistMessage);
		Assert.False(vm.CanSaveAsPlaylist); // a second click makes no second copy
		vm.OpenSavedPlaylist();
		_navigation.Verify(x => x.NavigateTo(typeof(CollectionViewModel).FullName!, It.Is<BaseItemDto>(d => d.Id == made && d.Type == BaseItemDto_Type.Playlist), It.IsAny<bool>()));
	}

	private async Task<CollectionViewModel> PageOf(IReadOnlyList<BaseItemDto> items)
	{
		_client.Setup(x => x.GetItemsByIds(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(items);
		var collection = new SmartCollection("pick:highly-rated", SmartCollectionKind.HighlyRated, "Highly rated, not watched", "", false, [.. items.Select(x => x.Id!.Value)]);
		var vm = new CollectionViewModel(_client.Object, Commands());
		await vm.OnNavigatedTo(new NixieCollection(collection));
		return vm;
	}

	[Fact]
	public async Task SaveAsPlaylist_SendsTheFirst200Films_InOrder_AndSaysSoPlainly()
	{
		// review Major 3: "Highly rated" can hold thousands: the first 200 in the collection's order, and the message says so
		var films = Enumerable.Range(0, 450).Select(i => Film($"Film {i:D3}", 2000, null, 8.0)).ToList();
		IReadOnlyList<Guid>? sent = null;
		_client.Setup(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()))
			.Callback((string _, IReadOnlyList<Guid> ids) => sent = ids).ReturnsAsync(new PlaylistCreation(Guid.NewGuid(), 200, null));
		var vm = await PageOf(films);

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		Assert.Equal(200, sent!.Count); // CollectionViewModel.PlaylistMost
		Assert.Equal(vm.Entries.Take(200).Select(e => e.Item.Dto.Id!.Value), sent);
		Assert.Contains("the first 200 of 450", vm.PlaylistMessage);
		Assert.Contains("200 at most", vm.PlaylistMessage);
	}

	[Fact]
	public async Task SaveAsPlaylist_LeavesShowsOut_AndSaysSo()
	{
		// a show sent to the server would add every one of its episodes
		var show = new BaseItemDto { Id = Guid.NewGuid(), Name = "A show", Type = BaseItemDto_Type.Series, UserData = new UserItemDataDto() };
		IReadOnlyList<Guid>? sent = null;
		_client.Setup(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()))
			.Callback((string _, IReadOnlyList<Guid> ids) => sent = ids).ReturnsAsync(new PlaylistCreation(Guid.NewGuid(), 200, null));
		var vm = await PageOf([_films[0], show, _films[3]]);

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		Assert.Equal(2, sent!.Count);
		Assert.DoesNotContain(show.Id!.Value, sent);
		Assert.Equal("Saved to your playlists as “Highly rated, not watched”. 1 show was left out: a show would add every one of its episodes.", vm.PlaylistMessage);
	}

	[Fact]
	public async Task SaveAsPlaylist_IsNotOffered_OnACollectionOfShowsOnly_AndWritesNothing()
	{
		// 1.6.1 (bug hunt BH-160-m2): "Unfinished series" offered Save, enabled, and pressed it could only say it can't
		var shows = Enumerable.Range(0, 3).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"Show {i}", Type = BaseItemDto_Type.Series, UserData = new UserItemDataDto() }).ToList();
		var vm = await PageOf(shows);

		Assert.True(vm.IsNixie);
		Assert.False(vm.OffersSaveAsPlaylist);
		Assert.False(vm.CanSaveAsPlaylist);
		Assert.False(vm.SaveAsPlaylistCommand.CanExecute(null));

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
		Assert.Equal("", vm.PlaylistMessage);
		// its words, should it ever be pressed, name what a playlist from Nixie holds
		Assert.Contains("holds films and episodes", CollectionViewModel.ShowsOnlyText);

		// a film among the shows: offered (the shows are left out, and it says so)
		var mixed = await PageOf([shows[0], _films[0]]);
		Assert.True(mixed.OffersSaveAsPlaylist);
		Assert.True(mixed.CanSaveAsPlaylist);
	}

	[Fact]
	public async Task SaveAsPlaylist_AfterLeavingAndComingBack_MakesNoSecondCopy()
	{
		// 1.6.1 (BH-160-m2): the saved playlist lived in the page's view model: a second press after coming back made a second
		// playlist of the same name on the server
		var made = Guid.NewGuid();
		_client.Setup(x => x.CreatePlaylist("The Matrix", It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new PlaylistCreation(made, 200, null));
		var (first, _) = await NixiePage();
		await first.SaveAsPlaylistCommand.ExecuteAsync(null);
		_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
		// the server lists it now
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = "Rainy Sunday", Type = BaseItemDto_Type.Playlist }, new BaseItemDto { Id = made, Name = "the matrix ", Type = BaseItemDto_Type.Playlist }],
		});

		var (again, _) = await NixiePage();
		Assert.True(again.CanSaveAsPlaylist);
		await again.SaveAsPlaylistCommand.ExecuteAsync(null);

		_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
		Assert.Equal(made, again.SavedPlaylist?.Id);
		Assert.Equal("Already in your playlists as “the matrix ”.", again.PlaylistMessage);
		Assert.False(again.CanSaveAsPlaylist);
		again.OpenSavedPlaylist();
		_navigation.Verify(x => x.NavigateTo(typeof(CollectionViewModel).FullName!, It.Is<BaseItemDto>(d => d.Id == made), It.IsAny<bool>()));
	}

	[Fact]
	public async Task SaveAsPlaylist_AnAnswerThatNamesNoPlaylist_IsSaidPlainly_AndSavingAgainFindsIt()
	{
		// 1.6.1 (BH-160-m2): a 200 with no playlist id read "didn't make the playlist (error 200)"
		var (vm, _) = await NixiePage();
		_client.Setup(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new PlaylistCreation(null, 200, "no playlist id in the answer"));

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		Assert.Contains("answered without saying which playlist it made", vm.PlaylistMessage);
		Assert.DoesNotContain("error 200", vm.PlaylistMessage);
		Assert.DoesNotContain("no playlist id", vm.PlaylistMessage);
		Assert.True(vm.CanSaveAsPlaylist);

		// it had made it: Save again finds it rather than making a second
		var made = Guid.NewGuid();
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = made, Name = "The Matrix", Type = BaseItemDto_Type.Playlist }] });
		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);
		Assert.Equal(made, vm.SavedPlaylist?.Id);
		_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
	}

	[Theory]
	[InlineData(403, "doesn't let your account make playlists")]
	[InlineData(401, "Your sign-in has ended")]
	[InlineData(0, "Couldn't reach")]
	[InlineData(500, "didn't make the playlist (error 500)")]
	public async Task SaveAsPlaylist_TheServersNo_IsSaidInNixiesWords_AndItCanBeTriedAgain(int status, string said)
	{
		var (vm, _) = await NixiePage();
		_client.Setup(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new PlaylistCreation(null, status, "Forbidden"));

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		Assert.Contains(said, vm.PlaylistMessage);
		Assert.DoesNotContain("Forbidden", vm.PlaylistMessage);
		Assert.True(vm.CanSaveAsPlaylist);
		Assert.Null(vm.SavedPlaylist);
	}

	[Fact]
	public async Task AnAdminsBoxSet_ShowsTheOwnedFilmsItLeavesOut()
	{
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		await index.GetAsync();
		var set = BoxSet("The Matrix Collection", "2344", 2, 2);
		_client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [_films[0], _films[1]] });
		var vm = new CollectionViewModel(_client.Object, Commands());

		await vm.OnNavigatedTo(set);

		Assert.Equal("The Matrix", vm.Title);
		Assert.Equal("Collection", vm.Eyebrow);
		Assert.Equal(["The Matrix Revolutions"], vm.AlsoInLibrary.Select(x => x.Dto.Name));
		Assert.Equal("Also in your library · 1 film", vm.AlsoInLibraryTitle);
		Assert.False(vm.IsNixie);
	}

	[Fact]
	public async Task ASavedFilter_ShowsItsLibrarysTitlesThatPass_AndCanBeRemovedFromItsPage()
	{
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		SmartCollectionServices.Store = store;
		_client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		_films[3].Genres = ["Comedy"];
		_films[5].Genres = ["Comedy", "Fantasy"];
		var saved = await store.SaveFilter("Laughs", new SavedFilter(MoviesId, "Movies", Genres: ["Comedy"]));
		var vm = new CollectionViewModel(_client.Object, Commands());

		await vm.OnNavigatedTo(new NixieCollection(saved));

		Assert.Equal("Made by Nixie · Saved filter", vm.Eyebrow);
		Assert.Equal(["Amélie", "Sintel"], vm.Entries.Select(e => e.Item.Dto.Name));
		Assert.True(vm.IsSavedFilter);
		await vm.RemoveSavedCommand.ExecuteAsync(null);
		Assert.Empty(await store.SavedAsync());
		_navigation.Verify(x => x.GoBack(), Times.Once);
	}

	private HomeViewModel Home()
	{
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>())).ReturnsAsync([_films[3]]);
		return new HomeViewModel(_client.Object, _messages, Commands(), _homeCache);
	}

	[Fact]
	public async Task Home_ShowsItsMadeByNixieRows_AfterNewIn_AndSnapshotsThem()
	{
		var (index, _) = Services();
		SmartCollectionServices.Index = index;
		await index.GetAsync();
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var home = Home();

		await home.OnNavigatedTo(new object());

		Assert.True(home.State.IsLoaded);
		var row = Assert.Single(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.HighlyRated);
		Assert.Equal("Highly rated, not watched", row.Title);
		Assert.Equal("Made by Nixie", row.Kicker);
		Assert.Equal(["The Matrix", "Amélie", "Night of the Living Dead"], row.Items.Select(i => i.Dto.Name));
		Assert.Single((await _homeCache.Get("http://server", _client.Object.UserId)).Snapshot!.NixieRows, r => r.Collection.Kind == SmartCollectionKind.HighlyRated);
	}

	[Fact]
	public async Task Home_StillLoads_WhenNixiesRowsFail()
	{
		var (index, _) = Services();
		SmartCollectionServices.Index = index;
		await index.GetAsync();
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("a bug in a builder"));
		var home = Home();

		await home.OnNavigatedTo(new object());

		Assert.True(home.State.IsLoaded);
		Assert.Empty(home.NixieRows);
		Assert.Single(home.RecentItems);
	}

	[Fact]
	public async Task TheStripsNixiePresets_AreALookup_MadeWhenTheIndexComesIn_AndHoldTheLibrarysTitlesOnly()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var presets = new NixieLibraryPresets(index, _client.Object, store);
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

		// the index not read yet: none now, and it is read for the next call
		Assert.Empty(presets.PresetsFor(movies, _films));
		await index.GetAsync();
		await index.Settled; // 1.7.1: the titles, then with their people (each makes the presets again)
		await presets.Making;

		var made = presets.PresetsFor(movies, _films);
		var matrix = Assert.Single(made, p => p.Kind == SmartCollectionKind.Franchise);
		Assert.Equal(("nixie:franchise:2344", "The Matrix"), (matrix.Id, matrix.Label));
		Assert.Equal(3, matrix.Items.Count);
		Assert.Equal("The Matrix", presets.LabelOf("nixie:franchise:2344"));
		// only the titles the page has: two of the three films loaded, two in the preset; none loaded, no preset
		Assert.Equal(2, presets.PresetsFor(movies, _films.Take(2).ToList()).Single(p => p.Kind == SmartCollectionKind.Franchise).Items.Count);
		Assert.DoesNotContain(presets.PresetsFor(movies, [_films[3]]), p => p.Kind == SmartCollectionKind.Franchise);
	}

	[Fact]
	public async Task AskedLate_ThePresetsWaitForTheIndex_AndChangedSaysWhenTheyAreMade()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var presets = new NixieLibraryPresets(index, _client.Object, store);
		var changed = 0;
		presets.Changed += (_, _) => Interlocked.Increment(ref changed);
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };

		// a library opened before the index is read: its ask waits for it, and gets the presets
		var made = await presets.PresetsForAsync(movies, _films, CancellationToken.None);

		Assert.Contains(made, p => p.Id == "nixie:franchise:2344");
		await index.Settled;
		await presets.Making;
		Assert.InRange(changed, 1, 2); // 1.7.1: the titles, then (maybe made again by then) with their people
		using var cancel = new CancellationTokenSource();
		cancel.Cancel();
		Assert.NotEmpty(await presets.PresetsForAsync(movies, _films, cancel.Token)); // made already: a lookup
		Assert.Empty(await presets.PresetsForAsync(new BaseItemDto { Id = Guid.NewGuid() }, _films, CancellationToken.None));
	}

	[Fact]
	public async Task ASavedFilterWithANixiePreset_KeepsThatCollectionsTitlesOnly()
	{
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		SmartCollectionServices.Store = store;
		_client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		var presets = new NixieLibraryPresets(index, _client.Object, store);
		Assert.True(await presets.SaveFilter("Matrix, unwatched", new SavedFilter(MoviesId, "Movies", Unwatched: true, PresetId: "nixie:franchise:2344", PresetLabel: "The Matrix")));
		var saved = Assert.Single(await store.SavedAsync());
		Assert.Equal("Your filter in Movies: The Matrix · Unwatched", saved.Why);
		var vm = new CollectionViewModel(_client.Object, Commands());

		await vm.OnNavigatedTo(new NixieCollection(saved));

		Assert.Equal(["The Matrix", "The Matrix Reloaded", "The Matrix Revolutions"], vm.Entries.Select(e => e.Item.Dto.Name).Order());
	}

	[Fact]
	public void HomesRows_FollowTheSwitches()
	{
		var picks = new List<SmartCollection>
		{
			new("pick:highly-rated", SmartCollectionKind.HighlyRated, "Highly rated, not watched", "", false, [Guid.NewGuid()]),
			new("pick:unfinished-series", SmartCollectionKind.UnfinishedSeries, "Unfinished series", "", false, [Guid.NewGuid()]),
			new("franchise:1", SmartCollectionKind.Franchise, "The Matrix", "", true, [Guid.NewGuid()]),
		};

		Assert.Equal(["Highly rated, not watched", "Unfinished series", "The Matrix"], HomeViewModel.PickNixieRows(picks, new HashSet<NixieHomeRow>(), DateTimeOffset.Now).Select(x => x.Title));
		Assert.Equal(["Unfinished series"], HomeViewModel.PickNixieRows(picks, new HashSet<NixieHomeRow> { NixieHomeRow.HighlyRated, NixieHomeRow.Rotating }, DateTimeOffset.Now).Select(x => x.Title));
	}

	[Fact]
	public async Task HomesSnapshot_KeepsTheNixieRows_AndAnOldSnapshotHasNone()
	{
		var collection = new SmartCollection("pick:highly-rated", SmartCollectionKind.HighlyRated, "Highly rated, not watched", "Rated 7.5 or more", false, [_films[0].Id!.Value]);
		var snapshot = new HomeSnapshot([], [], [new HomeSnapshotRow(new BaseItemDto { Id = MoviesId, Name = "Movies" }, [_films[3]])], [])
		{
			NixieRows = [new HomeSnapshotNixieRow(collection, [_films[0]])],
		};
		await _homeCache.Save("http://server", Guid.Empty, snapshot, (await _homeCache.Get("http://server", Guid.Empty)).ChangesSeen);
		using var again = new HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<HomeCache>(), TimeProvider.System);

		var read = (await again.Get("http://server", Guid.Empty)).Snapshot!;

		var row = Assert.Single(read.NixieRows);
		Assert.Equal(("pick:highly-rated", "Highly rated, not watched", SmartCollectionKind.HighlyRated), (row.Collection.Id, row.Collection.Title, row.Collection.Kind));
		Assert.Equal([_films[0].Id!.Value], row.Collection.ItemIds);
		Assert.Equal("The Matrix", Assert.Single(row.Items).Name);
		Assert.Empty(new HomeSnapshot([], [], [], []).NixieRows);
	}

	[Fact]
	public async Task HomesSavedPage_PaintsOnlyTheNixieRowsTurnedOnNow()
	{
		// 1.6.1 (the 1.6 review): a row turned off in Settings after Home was saved was painted again at the next visit
		var rated = new SmartCollection("pick:highly-rated", SmartCollectionKind.HighlyRated, "Highly rated, not watched", "", false, [_films[0].Id!.Value]);
		var matrix = new SmartCollection("franchise:2344", SmartCollectionKind.Franchise, "The Matrix", "", true, [_films[0].Id!.Value]);
		var unfinished = new SmartCollection("pick:unfinished", SmartCollectionKind.UnfinishedSeries, "Unfinished series", "", false, [_films[1].Id!.Value]);
		var snapshot = new HomeSnapshot([], [], [new HomeSnapshotRow(new BaseItemDto { Id = MoviesId, Name = "Movies" }, [_films[3]])], [])
		{
			NixieRows = [new HomeSnapshotNixieRow(rated, [_films[0]]), new HomeSnapshotNixieRow(matrix, [_films[0]]), new HomeSnapshotNixieRow(unfinished, [_films[1]])],
		};
		var user = _client.Object.UserId;
		await _homeCache.Save("http://server", user, snapshot, (await _homeCache.Get("http://server", user)).ChangesSeen);
		var settings = new SwitchSettings();
		settings.SaveSetting(CollectionSettingKeys.HomeRowsOff, "HighlyRated");
		settings.SaveSetting(CollectionSettingKeys.KindsOff, "Franchise");
		var painted = new List<string>();
		var home = new HomeViewModel(_client.Object, _messages, Commands(), _homeCache, settings);
		home.NixieRows.CollectionChanged += (_, e) => painted.AddRange(e.NewItems?.OfType<NixieHomeRowViewModel>().Select(r => r.Title) ?? []);
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetLatestItems(It.IsAny<BaseItemDto>())).ReturnsAsync([_films[3]]);

		await home.OnNavigatedTo(new object());

		// never painted: Highly rated (its row off) nor The Matrix (franchises off)
		Assert.Equal(["Unfinished series"], painted.Distinct());
		Assert.True(HomeViewModel.NixieRowShown(unfinished, new HashSet<NixieHomeRow> { NixieHomeRow.Rotating }, new HashSet<SmartCollectionKind>()));
		Assert.False(HomeViewModel.NixieRowShown(matrix, new HashSet<NixieHomeRow> { NixieHomeRow.Rotating }, new HashSet<SmartCollectionKind>()));
	}

	[Fact]
	public void TheLibrariesPagesMadeByNixieTile_SaysWhatItHolds()
	{
		var tile = LibraryTile.MadeByNixie(48);

		Assert.Equal("Made by Nixie", tile.Name);
		Assert.Equal(" · 48", tile.Detail);
		Assert.Equal("Made by Nixie, 48 collections", tile.AutomationName);
		Assert.True(tile.IsMadeByNixie);
	}

	[Fact]
	public void BoxSetTiles_SayWhatIsLeft()
	{
		Assert.Equal("6 films · 3 left", CollectionTile.BoxSetDetail(6, 3));
		Assert.Equal("6 films · all watched", CollectionTile.BoxSetDetail(6, 0));
		Assert.Equal("1 film", CollectionTile.BoxSetDetail(1, null));
		Assert.Equal("", CollectionTile.BoxSetDetail(null, 2));
	}

	[Fact]
	public void TheCardsNarratorName_SaysTheTidyName_AndTheDetailsSayTheServers()
	{
		var set = BoxSet("[HUB] Marvel", null, 6, 3);

		Assert.StartsWith("Marvel", ItemLabels.CardName(BaseItemViewModel.FromDto(set)));
		Assert.Equal("Part of the collection Marvel", ItemMeta.PartOfName(set));
		Assert.Equal("Shown on the server as “[HUB] Marvel”", CollectionNames.ServerNameNote(set));
		Assert.Equal("", CollectionNames.ServerNameNote(BoxSet("Marvel", null, 6, 3)));
		Assert.Equal("[REC]", ItemLabels.CardName(BaseItemViewModel.FromDto(new BaseItemDto { Name = "[REC]", Type = BaseItemDto_Type.Movie })));
	}

	/// <summary>Settings in memory: what was saved, else the default asked for (as LocalSettingsService answers).</summary>
	private sealed class SwitchSettings : ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
