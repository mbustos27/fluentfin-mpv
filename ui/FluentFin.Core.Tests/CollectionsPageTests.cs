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
		TrendingServices.Collections = null; // 1.7.3
		CollectionNames.ForgetBoxSets();
		_homeCache.Dispose();
		_rig?.Dispose(); // 1.7.3
		TestFolder.Delete(_folder); // 1.7.1: the index's people pass may still be saving behind a test that ended
	}

	private static BaseItemDto Film(string name, int year, string? collection, double rating) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = BaseItemDto_Type.Movie,
		ProductionYear = year,
		CommunityRating = (float)rating,
		CriticRating = 80, // 1.7.3 (M1's review): Acclaimed takes only titles something backs
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
		Assert.Equal(["The server's collections", "Playlists", "Picks", "Franchises"], vm.Rows.Select(r => r.Title));
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
			Assert.Equal(["The server's collections", "Picks", "Franchises"], await ui.Invoke(() => vm.Rows.Select(r => r.Title).ToList()));
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
		Assert.Equal(["The server's collections"], vm.Rows.Select(r => r.Title));

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
		Assert.Equal(["The server's collections"], vm.Rows.Select(r => r.Title));
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
	public async Task SaveAsPlaylist_AnotherUsersPublicPlaylistOfThatName_IsNotTheUsers()
	{
		// 1.7.5 (the 1.6.1 review): the check for an earlier save went by the name, so another user's public "The Matrix" read
		// "Already in your playlists", and Open took the user to it; it goes by whose it is now (by its id)
		var theirs = Guid.NewGuid();
		var made = Guid.NewGuid();
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = theirs, Name = "The Matrix", Type = BaseItemDto_Type.Playlist }],
		});
		_client.Setup(x => x.IsOwnPlaylist(theirs)).ReturnsAsync(false);
		_client.Setup(x => x.CreatePlaylist("The Matrix", It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new PlaylistCreation(made, 200, null));
		var (vm, _) = await NixiePage();

		await vm.SaveAsPlaylistCommand.ExecuteAsync(null);

		_client.Verify(x => x.CreatePlaylist("The Matrix", It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
		Assert.Equal(made, vm.SavedPlaylist?.Id);
		Assert.StartsWith("Saved to your playlists as “The Matrix”", vm.PlaylistMessage, StringComparison.Ordinal);
		Assert.DoesNotContain("Already", vm.PlaylistMessage, StringComparison.Ordinal);

		// the user's own beside it (asked by its id): that one is the earlier save, never the other user's
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = theirs, Name = "The Matrix", Type = BaseItemDto_Type.Playlist }, new BaseItemDto { Id = made, Name = "The Matrix", Type = BaseItemDto_Type.Playlist }],
		});
		_client.Setup(x => x.IsOwnPlaylist(made)).ReturnsAsync(true);
		var (again, _) = await NixiePage();
		await again.SaveAsPlaylistCommand.ExecuteAsync(null);

		_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
		Assert.Equal(made, again.SavedPlaylist?.Id);
		Assert.Equal("Already in your playlists as “The Matrix”.", again.PlaylistMessage);
	}

	[Fact]
	public async Task SaveAsPlaylist_TheEarlierSave_RenamedOnTheServer_IsFoundByItsId()
	{
		// 1.7.5 (the review): the earlier save was found by its name first, so one renamed on the server made a second copy
		var store = new Outside.MemoryStore();
		SavedPlaylists.Settings = () => store;
		try
		{
			var made = Guid.NewGuid();
			_client.Setup(x => x.CreatePlaylist("The Matrix", It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new PlaylistCreation(made, 200, null));
			var (first, _) = await NixiePage();
			await first.SaveAsPlaylistCommand.ExecuteAsync(null);
			_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Once);

			// the user renamed it on the server; another user's public "The Matrix" is there too
			_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult
			{
				Items = [new BaseItemDto { Id = Guid.NewGuid(), Name = "The Matrix", Type = BaseItemDto_Type.Playlist },
					new BaseItemDto { Id = made, Name = "Neo night", Type = BaseItemDto_Type.Playlist }],
			});
			var (again, _) = await NixiePage();
			await again.SaveAsPlaylistCommand.ExecuteAsync(null);

			_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
			Assert.Equal(made, again.SavedPlaylist?.Id);
			Assert.Equal("Already in your playlists as “Neo night”.", again.PlaylistMessage);
			_client.Verify(x => x.IsOwnPlaylist(It.IsAny<Guid>()), Times.Never); // known by its id: nothing to ask

			// deleted on the server: forgotten, and saved again
			_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
			var (third, _) = await NixiePage();
			await third.SaveAsPlaylistCommand.ExecuteAsync(null);
			_client.Verify(x => x.CreatePlaylist(It.IsAny<string>(), It.IsAny<IReadOnlyList<Guid>>()), Times.Exactly(2));
		}
		finally
		{
			SavedPlaylists.Settings = () => SmartCollectionServices.Settings;
		}
	}

	[Fact]
	public void SaveAsPlaylist_ARefusalOfTheServers_NamesItsOwner_NeverAnAdministrator()
	{
		// rule 11 (KNOWN-ISSUES, from 1.7.3): a 403 said "Ask its administrator."; the app says "the server's owner" elsewhere
		var refused = CollectionViewModel.PlaylistRefusal(403);
		Assert.Contains("Only the server's owner can change that.", refused, StringComparison.Ordinal);
		Assert.DoesNotContain("administrator", refused, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("your server", CollectionViewModel.PlaylistRefusal(0), StringComparison.OrdinalIgnoreCase);
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
		Assert.Equal("Acclaimed, not seen yet", row.Title); // 1.7.3 (P0-4): was "Highly rated, not watched"
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
	public async Task AKindTurnedOffInSettings_LeavesTheStripAtTheNextAsk_AndTurnedOnComesBack()
	{
		// 1.7.3 fix round (BH-173-m2): the strip's switches are back, and they act without waiting for the index's next read
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var settings = new SwitchSettings();
		var presets = new NixieLibraryPresets(index, _client.Object, store, settings);
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", CollectionType = BaseItemDto_CollectionType.Movies };
		await presets.PresetsForAsync(movies, _films, CancellationToken.None);
		Assert.Contains(presets.PresetsFor(movies, _films), p => p.Id == "nixie:franchise:2344");

		CollectionSettings.Set(settings, CollectionSettingKeys.KindsOff, SmartCollectionKind.Franchise, on: false);
		Assert.DoesNotContain(presets.PresetsFor(movies, _films), p => p.Kind == SmartCollectionKind.Franchise); // at once
		await presets.Making;

		var changed = 0;
		presets.Changed += (_, _) => Interlocked.Increment(ref changed);
		CollectionSettings.Set(settings, CollectionSettingKeys.KindsOff, SmartCollectionKind.Franchise, on: true);
		presets.PresetsFor(movies, _films); // the next ask makes them again
		await presets.Making;
		Assert.Equal(1, changed);
		Assert.Contains(presets.PresetsFor(movies, _films), p => p.Id == "nixie:franchise:2344");
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
	public async Task MadeByNixieThatMadeNothing_SaysSoInOneLine_WhatNixieLooksFor_AndNothingAboutTheServer()
	{
		// 1.7.3 fix round (BH-173-m1): the throwaway's 25,736 films (no tags, no critics' ratings, no franchises) made nothing,
		// and the page showed the server's row alone, with no word
		_films.Clear();
		_films.AddRange(Enumerable.Range(0, 8).Select(i => Film($"Plain {i}", 1990 + i, null, 6.0)));
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("Westerns", null, 2, 1)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All)); // a first visit: the read, then the line
		await vm.Reading;

		Assert.Equal(["The server's collections"], vm.Rows.Select(r => r.Title));
		Assert.True(vm.IsNixieEmpty);
		Assert.True(vm.ShowsReadingLine);
		Assert.False(vm.IsReadingIndex); // no ring: nothing is reading
		Assert.Equal(CollectionsViewModel.NixieEmptyText, vm.ReadingText);
		Assert.DoesNotContain("server", vm.ReadingText, StringComparison.OrdinalIgnoreCase); // rule 11
		await vm.OnNavigatedFrom();

		// the next visit, from the saved index: the same line at once; every kind turned off: that is why
		var settings = new SwitchSettings();
		settings.SaveSetting(CollectionSettingKeys.KindsOff, "Franchise,Keyword,HighlyRated,UnfinishedSeries,Seasonal,ShortFilms");
		var again = new CollectionsViewModel(_client.Object, Commands(), index, store, settings);
		await again.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));
		Assert.True(again.ShowsReadingLine);
		Assert.Equal(CollectionsViewModel.NixieOffText, again.ReadingText);
	}

	// 1.7.2's shape of the saved collections file (SmartCollectionStore's Saved and SavedFilter then): no Revision
	private sealed record Saved172(string Id, string Name, SavedFilter172 Filter);

	private sealed record SavedFilter172(Guid LibraryId, string LibraryName, bool Unwatched = false, bool Favorites = false, IReadOnlyList<string>? Genres = null,
		IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? OfficialRatings = null, IReadOnlyList<string>? Years = null, string? PresetId = null, string? PresetLabel = null);

	[Fact]
	public async Task AFilterSavedBy173_KeepsItsRules_After172RewritesTheFile()
	{
		// the fix round's re-review: 1.7.2 rewrites the whole file when it saves a filter, with the fields it knows, so the
		// Revision of a filter saved by 1.7.3 went, and back on 1.7.3 it took 1.7.2's rules. Its id carries the revision too
		var (_, store) = Services();
		await store.SaveFilter("Short laughs", new SavedFilter(MoviesId, "Movies", Genres: ["Comedy"], PresetId: "nixie:pick:short-films", PresetLabel: "Under 90 minutes"));
		var file = Directory.GetFiles(Path.Combine(_folder, "SmartCollections"), "*.json").Single();
		var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		// 1.7.2 reads the filters into its records, adds one of its own, and writes them back
		var filters = System.Text.Json.JsonSerializer.Deserialize<List<Saved172>>(root["Filters"])!;
		filters.Add(new Saved172($"saved:{Guid.NewGuid():N}", "Old shorts", new SavedFilter172(MoviesId, "Movies", PresetId: "nixie:pick:short-films", PresetLabel: "Short films")));
		root["Filters"] = System.Text.Json.JsonSerializer.SerializeToNode(filters);
		await File.WriteAllTextAsync(file, root.ToJsonString());
		Assert.DoesNotContain("\"Revision\"", await File.ReadAllTextAsync(file), StringComparison.Ordinal);

		var reread = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		var saved = await reread.SavedAsync();

		Assert.Equal(SavedFilter.CurrentRevision, saved.Single(x => x.Title == "Short laughs").Filter!.Revision); // 1.7.3's rules
		Assert.Equal(0, saved.Single(x => x.Title == "Old shorts").Filter!.Revision); // 1.7.2's own: its rules
		Assert.Equal(0, SavedFilter.RevisionOfId($"saved:{Guid.NewGuid():N}"));
		Assert.Equal(3, SavedFilter.RevisionOfId(SavedFilter.NewId()));
	}

	[Fact]
	public async Task AFilterSavedBy172OnShortFilms_Keeps172sMeaning_UncappedAndAnEmptyFilterSaysSo()
	{
		// 1.7.3 fix round (BH-173-M2): "Comedy + Short films" kept 100 titles in 1.7.2 and 0 in the first 1.7.3 build (Under 90
		// minutes' best 20, of 60 to 90 minutes, unwatched), and the empty page said the titles had left the server
		for (var i = 0; i < 30; i++)
		{
			var film = Film($"Short comedy {i:D2}", 1990 + i, null, 6.0);
			film.RunTimeTicks = TimeSpan.FromMinutes(i < 25 ? 40 : 70).Ticks; // 25 shorts under an hour, 5 of 70 minutes
			film.Genres = ["Comedy"];
			film.UserData = new UserItemDataDto { Played = i % 6 == 0 };
			_films.Add(film);
		}

		_client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		var now = await store.SaveFilter("Short laughs now", new SavedFilter(MoviesId, "Movies", Genres: ["Comedy"], PresetId: "nixie:pick:short-films", PresetLabel: "Under 90 minutes"));
		Assert.Equal(SavedFilter.CurrentRevision, now.Filter!.Revision); // the store says which meaning it was saved with
		await store.SaveFilter("Short laughs", new SavedFilter(MoviesId, "Movies", Genres: ["Comedy"], PresetId: "nixie:pick:short-films", PresetLabel: "Short films"));
		await store.SaveFilter("Westerns", new SavedFilter(MoviesId, "Movies", Genres: ["Western"]));
		// as 1.7.2 left the file: no Revision on the filter saved then
		var file = Directory.GetFiles(Path.Combine(_folder, "SmartCollections"), "*.json").Single();
		var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
		var old = root["Filters"]!.AsArray().Single(x => (string?)x!["Name"] == "Short laughs")!;
		old["Filter"]!.AsObject().Remove("Revision");
		old["Id"] = $"saved:{Guid.NewGuid():N}"; // and 1.7.2's id (the re-review: 1.7.3's says its revision)
		await File.WriteAllTextAsync(file, root.ToJsonString());
		var reread = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		SmartCollectionServices.Store = reread;
		var saved = await reread.SavedAsync();

		async Task<CollectionViewModel> Open(string name)
		{
			var vm = new CollectionViewModel(_client.Object, Commands());
			await vm.OnNavigatedTo(new NixieCollection(saved.Single(x => x.Title == name)));
			return vm;
		}

		// 1.7.2's meaning: every comedy under 90 minutes, watched or not: 30, more than Under 90 minutes' 20
		Assert.Equal(30, (await Open("Short laughs")).Entries.Count);
		// saved now: 1.7.3's rule (60 to 90 minutes, unwatched), every one of them
		Assert.Equal(_films.Count(x => x.Name!.StartsWith("Short comedy", StringComparison.Ordinal) && x.RunTimeTicks == TimeSpan.FromMinutes(70).Ticks && x.UserData!.Played != true),
			(await Open("Short laughs now")).Entries.Count);
		// nothing matches: the filter says so, not the server
		var empty = await Open("Westerns");
		Assert.True(empty.State.IsEmpty);
		Assert.Equal(("Nothing matches now", "No titles in Movies match this filter now."), (empty.State.Title, empty.State.Message));
		Assert.DoesNotContain("server", empty.State.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task ASavedFilterWithANixiePreset_InATitlesSecondLibrary_KeepsThatTitle()
	{
		// 1.7.3 (P0-2a; M2's review: nothing held CollectionSources' InLibrary): the server lists the Matrix films under
		// Movies first, and under Classics too. A filter saved in Classics with the franchise's preset on keeps them; with
		// the entry's first library alone it kept nothing
		var classicsId = Guid.NewGuid();
		var movies = new BaseItemDto { Id = MoviesId, Name = "Movies", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var classics = new BaseItemDto { Id = classicsId, Name = "Classics", Type = BaseItemDto_Type.CollectionFolder, CollectionType = BaseItemDto_CollectionType.Movies };
		var inClassics = _films.Where(f => f.Name!.StartsWith("The Matrix", StringComparison.Ordinal)).ToList();
		List<BaseItemDto> Of(BaseItemDto library) => library.Id == classicsId ? inClassics : _films;
		BaseItemDtoQueryResult Page(BaseItemDto library, int start, int limit) => new() { Items = [.. Of(library).Skip(start).Take(limit)], TotalRecordCount = Of(library).Count };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies, classics]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies, classics]);
		_client.Setup(x => x.GetLibraryIndexPage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool _, CancellationToken _) => Page(library, start, limit));
		_client.Setup(x => x.GetLibraryItems(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto library, int start, int limit, bool _, CancellationToken _) => Page(library, start, limit));
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		SmartCollectionServices.Store = store;
		await store.SaveFilter("Matrix in Classics", new SavedFilter(classicsId, "Classics", PresetId: "nixie:franchise:2344", PresetLabel: "The Matrix"));
		var saved = Assert.Single(await store.SavedAsync());
		var matrix = (await index.GetAsync())!.Entries.Single(x => x.Name == "The Matrix");
		Assert.Equal(MoviesId, matrix.LibraryId); // listed first under Movies
		Assert.Contains(classicsId, matrix.LibraryIds);
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

	// --- 1.7.3: Made by Nixie, fewer and better (P0-3, P0-4, the owner's answers 2 and 3) -----------------------------------

	private Outside.TrendingRig? _rig;

	// 24 films that make every kind 1.7.2 made: two decades (14 of the 1990s, 10 of the 2000s), a genre mix (Comedy · Horror, 8),
	// a country besides the commonest (Japan, 12), a studio (North Line, 24), three franchises of eight, and (the people pass)
	// a director and an actor in all of them
	private void UseEveryKindLibrary()
	{
		_films.Clear();
		_films.AddRange(Enumerable.Range(0, 24).Select(i => new BaseItemDto
		{
			Id = Guid.Parse($"{i:D8}-0173-0000-0000-000000000000"),
			Name = $"Film {i:D2}",
			SortName = $"film {i:D2}",
			Type = BaseItemDto_Type.Movie,
			ProductionYear = 1990 + (i % 20),
			CommunityRating = 5 + (i % 5),
			CriticRating = 80, // 1.7.3 (M1's review): Acclaimed takes only titles something backs
			RunTimeTicks = TimeSpan.FromMinutes(120).Ticks,
			Genres = (i % 3) switch { 0 => ["Comedy", "Horror"], 1 => ["Comedy"], _ => ["Horror"] },
			ProductionLocations = [i % 2 == 0 ? "France" : "Japan"],
			Studios = [new NameGuidPair { Name = "North Line" }],
			ProviderIds = new BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = $"{i}", ["TmdbCollection"] = $"{i / 8}" } },
			ImageTags = new BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = $"p{i}" } },
			UserData = new UserItemDataDto { Played = false },
		}));
		_client.Setup(x => x.GetLibraryPeoplePage(It.IsAny<BaseItemDto>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((BaseItemDto _, int start, int limit, bool total, CancellationToken _) => new BaseItemDtoQueryResult
			{
				Items = [.. _films.Skip(start).Take(limit).Select(x => new BaseItemDto
				{
					Id = x.Id, Type = BaseItemDto_Type.Movie,
					People = [new BaseItemPerson { Name = "Ana Reyes", Type = BaseItemPerson_Type.Director }, new BaseItemPerson { Name = "Clara Quill", Type = BaseItemPerson_Type.Actor }],
				})],
				TotalRecordCount = total ? _films.Count : null,
			});
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("[HUB] Marvel", null, 6, 3)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
	}

	// the index read once, with its people (a later visit shows it at once, as a restart's saved index)
	private async Task<(LibraryIndex Index, SmartCollectionStore Store)> ReadServices()
	{
		var (index, store) = Services();
		Assert.NotNull(await index.GetAsync());
		await index.Settled;
		return (index, store);
	}

	[Fact]
	public async Task MadeByNixie_HasNoRowNorTileOfTheSevenRetiredKinds_ThoughTheLibraryMakesThemAll()
	{
		UseEveryKindLibrary();
		var (index, store) = await ReadServices();
		// the library makes every kind: its presets strip still has the decades, the genre mix and the country (the owner's answer 3)
		var strip = NixiePresets.For(index.Current!, MoviesId, SmartCollectionCatalog.Now(), most: 50);
		Assert.Contains(strip, p => p.Kind == SmartCollectionKind.Decade);
		Assert.Contains(strip, p => p.Kind == SmartCollectionKind.GenreMix);
		Assert.Contains(strip, p => p.Kind == SmartCollectionKind.Country);
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));

		Assert.True(vm.State.IsLoaded);
		Assert.Equal(["The server's collections", "Picks", "Franchises"], vm.Rows.Select(r => r.Title));
		Assert.All(vm.Rows.SelectMany(r => r.Tiles).Where(t => t.IsNixie), t => Assert.DoesNotContain(t.Nixie!.Kind, SmartCollectionCatalog.OnDemandOnly));
		var published = vm.Rows.Where(r => r.Section == CollectionsSection.MadeByNixie).Sum(r => r.Tiles.Count);
		Assert.Equal(published, vm.MadeByNixieCount); // the Libraries page's "Made by Nixie · N" counts what the page lists
		// See all of Made by Nixie: the same, as a grid
		var all = new CollectionsViewModel(_client.Object, Commands(), index, store);
		await all.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SeeAll: true));
		Assert.NotEmpty(all.GridTiles);
		Assert.All(all.GridTiles, t => Assert.DoesNotContain(t.Nixie!.Kind, SmartCollectionCatalog.OnDemandOnly));
		Assert.All(new[] { "Decades", "Directors", "Actors", "Studios", "Studios and networks", "Genre mixes", "Countries" }, t => Assert.DoesNotContain(t, vm.Rows.Select(r => r.Title)));
	}

	[Fact]
	public async Task PinsAndSavedFilters_OfDirectorsActorsStudiosAndNetworks_AreRemovedOnce_WithNoEmptyTileOrError()
	{
		// the owner's answer 2: they go (P1's "More from" brings people back on title pages); decades stay (the strip makes them)
		UseEveryKindLibrary();
		var (index, store) = await ReadServices();
		foreach (var pin in new[] { "director:anareyes", "actor:claraquill", "studio:northline", "network:hbo", "franchise:0", "decade:movie:1990" })
		{
			await store.SetFavorite(pin, true);
		}

		await store.SaveFilter("By Ana", new SavedFilter(MoviesId, "Movies", PresetId: "nixie:director:anareyes", PresetLabel: "Ana Reyes"));
		await store.SaveFilter("North Line, unwatched", new SavedFilter(MoviesId, "Movies", Unwatched: true, PresetId: "nixie:studio:northline", PresetLabel: "North Line"));
		await store.SaveFilter("Nineties", new SavedFilter(MoviesId, "Movies", PresetId: "nixie:decade:movie:1990", PresetLabel: "1990s"));
		await store.SaveFilter("Laughs", new SavedFilter(MoviesId, "Movies", Genres: ["Comedy"]));
		var log = new CoreListLogger<CollectionsViewModel>();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store, logger: log);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.Saved));

		Assert.True(vm.State.IsLoaded, $"{vm.State.Title}: {vm.State.Message}");
		var saved = vm.Rows.Single(r => r.Section == CollectionsSection.Saved);
		// the saved filters left, then the pins: the franchise (published) and the decade (made by its id, no longer published)
		Assert.Equal(["Nineties", "Laughs"], saved.Tiles.Take(2).Select(t => t.Title));
		Assert.Equal(["franchise:0", "decade:movie:1990"], saved.Tiles.Skip(2).Select(t => t.Nixie!.Id));
		Assert.All(saved.Tiles, t => Assert.NotEqual(0, t.Nixie!.ItemIds.Count + (t.Nixie.Filter is null ? 0 : 1))); // no empty tile
		Assert.Equal(new HashSet<string> { "franchise:0", "decade:movie:1990" }, await store.FavoritesAsync());
		Assert.Equal(["Nineties", "Laughs"], (await store.SavedAsync()).Select(x => x.Title));
		Assert.Contains(log.Entries, x => x.Message == "Made by Nixie: 6 pinned or saved collections of directors, actors, studios or networks were removed (Nixie no longer makes them)");
		Assert.Equal(0, await RetiredCollections.ForgetAsync(store)); // once: nothing left to do
		// the decade's pin opens with its titles
		Assert.Equal(14, saved.Tiles.Single(t => t.Title == "1990s films").Nixie!.ItemIds.Count);
	}

	[Fact]
	public async Task AtStart_HomeRemovesTheRetiredPinsInTheBackground()
	{
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		SmartCollectionServices.Store = store;
		await index.GetAsync();
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		await store.SetFavorite("director:anareyes", true);
		await store.SetFavorite("franchise:2344", true);
		var home = Home();

		await home.OnNavigatedTo(new object());

		for (var i = 0; i < 100 && (await store.FavoritesAsync()).Count > 1; i++)
		{
			await Task.Delay(20);
		}

		Assert.Equal(new HashSet<string> { "franchise:2344" }, await store.FavoritesAsync());
	}

	[Fact]
	public void TheSwitches_AreThePublishedKinds_AndKindsOffIsLeftAsTheUserSetIt()
	{
		var settings = new SwitchSettings();
		var page = new CollectionsSettingsViewModel(settings);

		Assert.Equal(["Franchises", "Keywords", "Acclaimed, not seen yet", "Unfinished series", "In season", "Under 90 minutes"], page.Kinds.Select(x => x.Name));
		Assert.Equal(["Acclaimed, not seen yet", "Unfinished series", "In season", "A collection a day"], page.HomeRows.Select(x => x.Name));
		Assert.Equal(["Decades", "Genre mixes", "Countries"], page.LibraryFilters.Select(x => x.Name)); // fix round (BH-173-m2): the strip's switches
		Assert.All(page.HomeRows.Concat(page.Kinds).Concat(page.LibraryFilters), x => Assert.DoesNotContain("your server", x.Description, StringComparison.OrdinalIgnoreCase)); // rule 11
		// fix round (BH-173-q1): nothing counts raters
		Assert.DoesNotContain("few people", page.HomeRows[0].Description, StringComparison.Ordinal);

		// fix round (BH-173-m2, q4): 1.7.2 left Decades, Directors and Countries off (and Franchises, still a switch). Nothing is
		// taken out: the strip's kinds stay off (their switches say so), and the four that went stay off for a return to 1.7.2
		const string Before = "Actor,Country,Decade,Director,Franchise,GenreMix,HighlyRated,Network,Studio,SomethingNewer";
		settings.SaveSetting(CollectionSettingKeys.KindsOff, Before);
		var saves = settings.Saves;
		CollectionSettings.Apply(settings);
		Assert.Equal(Before, settings.ReadSetting(CollectionSettingKeys.KindsOff));
		Assert.Equal(saves, settings.Saves);
		Assert.All(page.LibraryFilters, x => Assert.False(x.IsOn, x.Name));
		// the way back for what stayed off: its switch
		Assert.False(page.Kinds[0].IsOn);
		page.Kinds[0].IsOn = true;
		Assert.DoesNotContain(SmartCollectionKind.Franchise, CollectionSettings.KindsOff(settings));
		page.LibraryFilters[0].IsOn = true;
		Assert.DoesNotContain(SmartCollectionKind.Decade, CollectionSettings.KindsOff(settings));
		Assert.Contains(SmartCollectionKind.Director, CollectionSettings.KindsOff(settings));
	}

	[Fact]
	public async Task HomesSavedPage_NeverPaintsARowOfARetiredKind_AndSaysTheRenamedPicksNewNames()
	{
		// 1.7.2's saved page: "Highly rated, not watched", and a director as the collection of the day
		var rated = new SmartCollection("pick:highly-rated", SmartCollectionKind.HighlyRated, "Highly rated, not watched", "", false, [_films[0].Id!.Value]);
		var director = new SmartCollection("director:anareyes", SmartCollectionKind.Director, "Ana Reyes", "", true, [_films[1].Id!.Value]);
		var decade = new SmartCollection("decade:movie:1990", SmartCollectionKind.Decade, "1990s films", "", false, [_films[1].Id!.Value]);
		var snapshot = new HomeSnapshot([], [], [new HomeSnapshotRow(new BaseItemDto { Id = MoviesId, Name = "Movies" }, [_films[3]])], [])
		{
			NixieRows = [new HomeSnapshotNixieRow(rated, [_films[0]]), new HomeSnapshotNixieRow(director, [_films[1]]), new HomeSnapshotNixieRow(decade, [_films[1]])],
		};
		var user = _client.Object.UserId;
		await _homeCache.Save("http://server", user, snapshot, (await _homeCache.Get("http://server", user)).ChangesSeen);
		var painted = new List<string>();
		var home = Home();
		home.NixieRows.CollectionChanged += (_, e) => painted.AddRange(e.NewItems?.OfType<NixieHomeRowViewModel>().Select(r => r.Title) ?? []);

		await home.OnNavigatedTo(new object());

		Assert.Equal(["Acclaimed, not seen yet"], painted.Distinct());
		var none = new HashSet<NixieHomeRow>();
		var noKinds = new HashSet<SmartCollectionKind>();
		Assert.False(HomeViewModel.NixieRowShown(director, none, noKinds));
		Assert.False(HomeViewModel.NixieRowShown(decade, none, noKinds));
		Assert.True(HomeViewModel.NixieRowShown(rated, none, noKinds));
		Assert.True(HomeViewModel.NixieRowShown(new SmartCollection(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, "Trending in your library", "", true, [Guid.NewGuid()]), none, noKinds));
	}

	[Fact]
	public void TheRenamedPicks_SayTheirNewNames_OnTheirTilesChipsRowsAndToNarrator()
	{
		// P0-4: "Acclaimed, not seen yet" and "Under 90 minutes" keep their ids (pins, settings and Home's saved page carry over)
		var acclaimed = new SmartCollection("pick:highly-rated", SmartCollectionKind.HighlyRated, "Acclaimed, not seen yet", "", false, [Guid.NewGuid()]);
		var short90 = new SmartCollection("pick:short-films", SmartCollectionKind.ShortFilms, "Under 90 minutes", "", false, [Guid.NewGuid()]);

		Assert.Equal(("Acclaimed", "Acclaimed, not seen yet"), (SmartCollectionKinds.Singular(SmartCollectionKind.HighlyRated), SmartCollectionKinds.Plural(SmartCollectionKind.HighlyRated)));
		Assert.Equal(("Under 90 minutes", "Under 90 minutes"), (SmartCollectionKinds.Singular(SmartCollectionKind.ShortFilms), SmartCollectionKinds.Plural(SmartCollectionKind.ShortFilms)));
		Assert.Equal("Made by Nixie · Acclaimed", acclaimed.Eyebrow);
		Assert.Equal("Acclaimed, not seen yet, made by Nixie, acclaimed, 1 title", acclaimed.AutomationName);
		Assert.Equal("Under 90 minutes, made by Nixie, under 90 minutes, 1 title", short90.AutomationName);
		Assert.Equal(("Acclaimed", "Under 90 minutes"), (NixiePresets.ShortLabel(acclaimed), NixiePresets.ShortLabel(short90)));
		Assert.Equal("Acclaimed, not seen yet", new NixieHomeRowViewModel(acclaimed with { Title = "Highly rated, not watched" }).Title);
		Assert.Equal("ACCLAIMED, NOT SEEN YET", new NixieHomeRowViewModel(acclaimed).SignTitle);
	}

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public async Task ATrendingMatchThatThrows_LeavesMadeByNixieShowing_OnThePageAndHome_AndIsSaidOnce()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_rig = new Outside.TrendingRig(trakt: true);
		foreach (var list in new[] { "movies/trending", "shows/trending", "movies/popular", "shows/popular", "movies/anticipated", "shows/anticipated", "movies/boxoffice" })
		{
			_rig.Fake.Trakt[list] = [("603", null, null, "The Matrix")];
		}

		await _rig.Sources.RefreshAsync();
		var trending = new TrendingCollections(_rig.Sources);
		trending.Matching += () => throw new InvalidOperationException("The Matrix went wrong");
		TrendingServices.Collections = trending;
		var (index, store) = Services();
		SmartCollectionServices.Index = index;
		await index.GetAsync();
		var log = new CoreListLogger<CollectionsViewModel>();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store, logger: log);

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));

		Assert.True(vm.State.IsLoaded);
		Assert.Equal(["Picks", "Franchises"], vm.Rows.Select(r => r.Title)); // 1.7.2: none at all
		var said = Assert.Single(log.Entries, x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
		Assert.Equal("Made by Nixie: the trending collections couldn't be matched (InvalidOperationException); the other kinds still are", said.Message);
		Assert.DoesNotContain(log.Entries, x => x.Message.Contains("The Matrix", StringComparison.Ordinal));

		var home = Home();
		await home.OnNavigatedTo(new object());
		Assert.Contains(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.HighlyRated); // 1.7.2: no Made by Nixie row
	}

	[Fact]
	public async Task AThrowWhileShowingTheRows_IsLoggedAsItself_NotAsMadeByNixie()
	{
		_client.Setup(x => x.GetBoxSets(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [BoxSet("[HUB] Marvel", null, 6, 3)] });
		_client.Setup(x => x.GetPlaylists(It.IsAny<CancellationToken>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		var (index, store) = Services();
		await index.GetAsync();
		var log = new CoreListLogger<CollectionsViewModel>();
		var vm = new CollectionsViewModel(_client.Object, Commands(), index, store, logger: log);
		// a view's handler that throws once as the first row goes in: nothing to do with making Made by Nixie
		var once = true;
		vm.Rows.CollectionChanged += (_, _) =>
		{
			if (once)
			{
				once = false;
				throw new InvalidOperationException("a view's handler");
			}
		};

		await vm.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		Assert.True(vm.State.IsLoaded, $"{vm.State.Title}: {vm.State.Message}");
		Assert.Contains(vm.Rows, r => r.Title == "The server's collections");
		var said = Assert.Single(log.Entries, x => x.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
		Assert.StartsWith("Collections: the rows couldn't be shown (InvalidOperationException in ", said.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(log.Entries, x => x.Message.Contains("Made by Nixie couldn't be made", StringComparison.Ordinal));
		Assert.DoesNotContain(log.Entries, x => x.Message.Contains("a view's handler", StringComparison.Ordinal));
	}

	/// <summary>Settings in memory: what was saved, else the default asked for (as LocalSettingsService answers).</summary>
	private sealed class SwitchSettings : ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		/// <summary>1.7.3: how many times a setting was written.</summary>
		public int Saves { get; private set; }

		public void SaveSetting<T>(string key, T value)
		{
			_values[key] = value;
			Saves++;
		}

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
