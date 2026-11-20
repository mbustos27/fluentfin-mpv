using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.Theming;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>Nixie 1.4.0 (the UX review's plan item 1): the shared Collection page, the routing to it, "Part of" and Home's rows.</summary>
public sealed class CollectionPageTests
{
	// "Copper and Glass" as the review captured it: A to Z by name gave the years 2000, 1986, 2016, 2009, 2012, 1979
	private static List<BaseItemDto> CopperAndGlass(params int[] watched) =>
	[
		.. new[] { ("Forest of Brightwater", 2000), ("Painted Forests", 1986), ("The Machine and the Mirror", 2016), ("The Mirror and the Engine", 2009), ("The Silent Letter", 2012), ("Wild Canyons", 1979) }
			.Select((f, i) => new BaseItemDto
			{
				Id = Guid.NewGuid(),
				Name = f.Item1,
				SortName = f.Item1.StartsWith("The ", StringComparison.Ordinal) ? f.Item1[4..] : f.Item1,
				Type = BaseItemDto_Type.Movie,
				ProductionYear = f.Item2,
				PremiereDate = new DateTimeOffset(f.Item2, 6, 1, 0, 0, 0, TimeSpan.Zero),
				DateCreated = new DateTimeOffset(2026, 1, 1 + i, 0, 0, 0, TimeSpan.Zero),
				UserData = new UserItemDataDto { Played = watched.Contains(f.Item2) },
			}),
	];

	private static string Years(IEnumerable<BaseItemDto> items) => string.Join(",", items.Select(x => x.ProductionYear));

	[Fact]
	public void ABoxSet_WithNoDisplayOrder_IsInReleaseOrder()
	{
		var films = CopperAndGlass();

		Assert.Equal("1979,1986,2000,2009,2012,2016", Years(CollectionBrowse.Ordered(films, CollectionOrder.Collection, playlist: false, displayOrder: null)));
		Assert.Equal("1979,1986,2000,2009,2012,2016", Years(CollectionBrowse.Ordered(films, CollectionOrder.Collection, false, "PremiereDate")));
		// the server's other order: by name (SortName: "The Machine…" under M)
		Assert.Equal(["Forest of Brightwater", "The Machine and the Mirror", "The Mirror and the Engine", "Painted Forests", "The Silent Letter", "Wild Canyons"],
			CollectionBrowse.Ordered(films, CollectionOrder.Collection, false, "SortName").Select(x => x.Name));
		Assert.Equal(CollectionOrder.Name, CollectionBrowse.OwnOrder("SortName"));
		Assert.Equal(CollectionOrder.ReleaseDate, CollectionBrowse.OwnOrder("Default"));
		// date added: newest first
		Assert.Equal("Wild Canyons", CollectionBrowse.Ordered(films, CollectionOrder.DateAdded, false, null)[0].Name);
	}

	[Fact]
	public void APlaylist_KeepsTheServersOrder()
	{
		var films = CopperAndGlass();
		Assert.Equal(Years(films), Years(CollectionBrowse.Ordered(films, CollectionOrder.Collection, playlist: true, displayOrder: "SortName")));
	}

	[Fact]
	public void TheSummary_SaysWhatTheSetIs()
	{
		var films = CopperAndGlass(watched: 1979);
		Assert.Equal("6 films · 1979–2016 · 1 of 6 watched", CollectionBrowse.Summary(films, playlist: false));
		Assert.Equal("6 films · 1 of 6 watched", CollectionBrowse.Summary(films, playlist: true));
		Assert.Equal("1979", CollectionBrowse.YearSpan([.. films.Where(x => x.ProductionYear == 1979)]));
		Assert.Equal(1.0 / 6, CollectionBrowse.Progress(films), 3);
		Assert.Equal("5 of 6 films", CollectionBrowse.ShownText(5, films));
		Assert.Equal("6 films", CollectionBrowse.ShownText(6, films));
		Assert.Equal("All 6 films are watched", CollectionBrowse.AllWatchedText(films));
		var mixed = new List<BaseItemDto> { new() { Type = BaseItemDto_Type.Movie }, new() { Type = BaseItemDto_Type.Series } };
		Assert.Equal("2 items · 0 of 2 watched", CollectionBrowse.Summary(mixed, playlist: true));
	}

	[Fact]
	public void Play_StartsTheFirstUnwatchedFilm_InCollectionOrder()
	{
		var films = CollectionBrowse.Ordered(CopperAndGlass(watched: [1979, 1986]), CollectionOrder.Collection, false, null);
		var next = CollectionBrowse.Next(films);
		Assert.Equal("Forest of Brightwater", next.Item?.Name);
		Assert.Equal(3, next.Position);
		Assert.Equal("Play Forest of Brightwater", CollectionBrowse.PlayLabel(next));
		Assert.Equal("Play Forest of Brightwater, film 3 of 6", CollectionBrowse.PlayName(next, films));

		films[2].UserData!.PlaybackPositionTicks = 10_000_000;
		Assert.Equal("Resume Forest of Brightwater", CollectionBrowse.PlayLabel(CollectionBrowse.Next(films)));

		var all = CollectionBrowse.Ordered(CopperAndGlass(1979, 1986, 2000, 2009, 2012, 2016), CollectionOrder.Collection, false, null);
		var again = CollectionBrowse.Next(all);
		Assert.True(again.Again);
		Assert.Equal("Wild Canyons", again.Item?.Name);
		Assert.Equal("Play again from the start", CollectionBrowse.PlayLabel(again));
	}

	[Fact]
	public void Shuffle_PicksAFilmNotWatched()
	{
		var films = CopperAndGlass(1979, 1986, 2000, 2009, 2012);
		Assert.Equal(2016, CollectionBrowse.Shuffle(films, _ => 0)?.ProductionYear);
		Assert.NotNull(CollectionBrowse.Shuffle(CopperAndGlass(1979, 1986, 2000, 2009, 2012, 2016), n => n - 1));
		Assert.Null(CollectionBrowse.Shuffle([], _ => 0));
	}

	private static (CollectionViewModel Vm, Mock<IJellyfinClient> Client, Mock<INavigationServiceCore> Navigation) Page(BaseItemDto set, List<BaseItemDto>? items)
	{
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(set.Id!.Value)).ReturnsAsync(set);
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync(items is null ? null : new BaseItemDtoQueryResult { Items = items });
		client.Setup(x => x.GetPlaylistItems(set.Id!.Value)).ReturnsAsync(items is null ? null : new BaseItemDtoQueryResult { Items = items });
		var navigation = new Mock<INavigationServiceCore>();
		var commands = new GlobalCommands(navigation.Object, client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		return (new CollectionViewModel(client.Object, commands), client, navigation);
	}

	[Fact]
	public async Task TheCollectionPage_ShowsTheSetInItsOwnOrder_Numbered()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass", Overview = "Six films." };
		var (vm, _, _) = Page(set, CopperAndGlass(watched: 1979));

		await vm.OnNavigatedTo(set);

		Assert.Equal(LoadStatus.Loaded, vm.State.Status);
		Assert.Equal("Copper and Glass", vm.Title);
		Assert.Equal("Collection", vm.Kind);
		Assert.Equal("1979,1986,2000,2009,2012,2016", Years(vm.Entries.Select(e => e.Item.Dto)));
		Assert.Equal(["1", "2", "3", "4", "5", "6"], vm.Entries.Select(e => e.PositionText));
		Assert.Equal("6 films · 1979–2016 · 1 of 6 watched", vm.Summary);
		Assert.Equal("Play Painted Forests", vm.PlayLabel);
		Assert.Equal("Order: Collection", vm.OrderLabel);
		Assert.Empty(vm.Letters); // six films: no A to Z

		vm.HideWatched = true;
		Assert.Equal(5, vm.Entries.Count);
		Assert.Equal("2", vm.Entries[0].PositionText); // the place in the collection, not in what is shown
		Assert.Equal("5 of 6 films", vm.ShownText);

		vm.SetOrder(CollectionOrder.Name);
		Assert.All(vm.Entries, e => Assert.Equal("", e.PositionText)); // numbers only in the collection's own order
		Assert.Equal("Order: Name", vm.OrderLabel);
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M2: R and Back on a Collection page reset Order and Hide watched): a new view model for the same
	/// collection (R makes one, and Back a new page) puts them back when the page says it comes back or reloads in place; a
	/// collection opened anew starts in its own order, every film shown; another collection, or another user, has its own.
	/// </summary>
	[Fact]
	public async Task ARefreshOrBack_KeepsOrderAndHideWatched_AFreshVisitDoesNot()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass" };
		var films = CopperAndGlass(watched: 1979);
		var (vm, _, _) = Page(set, films);
		await vm.OnNavigatedTo(set);
		vm.SetOrder(CollectionOrder.Name);
		vm.HideWatched = true;

		// R (or Back): the page says so
		var (again, _, _) = Page(set, films);
		again.KeepsPlace = () => true;
		await again.OnNavigatedTo(set);
		Assert.Equal((CollectionOrder.Name, true, "Order: Name"), (again.Order, again.HideWatched, again.OrderLabel));
		Assert.Equal(["Forest of Brightwater", "The Machine and the Mirror", "The Mirror and the Engine", "Painted Forests", "The Silent Letter"],
			again.Entries.Select(e => e.Item.Dto.Name));
		Assert.Equal("5 of 6 films", again.ShownText);

		// a change on the page kept is kept too (a second R)
		again.SetOrder(CollectionOrder.ReleaseDate);
		var (third, _, _) = Page(set, films);
		third.KeepsPlace = () => true;
		await third.OnNavigatedTo(set);
		Assert.Equal((CollectionOrder.ReleaseDate, true), (third.Order, third.HideWatched));

		// opened anew: the collection's own order, every film
		var (fresh, _, _) = Page(set, films);
		fresh.KeepsPlace = () => false;
		await fresh.OnNavigatedTo(set);
		Assert.Equal((CollectionOrder.Collection, false, 6), (fresh.Order, fresh.HideWatched, fresh.Entries.Count));

		// ...which is now what Back finds
		var (back, _, _) = Page(set, films);
		back.KeepsPlace = () => true;
		await back.OnNavigatedTo(set);
		Assert.Equal((CollectionOrder.Collection, false), (back.Order, back.HideWatched));

		// another collection is its own
		var other = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Winter Films" };
		var (elsewhere, _, _) = Page(other, films);
		elsewhere.KeepsPlace = () => true;
		await elsewhere.OnNavigatedTo(other);
		Assert.Equal((CollectionOrder.Collection, false), (elsewhere.Order, elsewhere.HideWatched));

		// another user's place is not this one's
		CollectionPlaces.Remember(Guid.NewGuid(), set.Id!.Value, new CollectionPlace(CollectionOrder.DateAdded, true));
		Assert.Equal(new CollectionPlace(CollectionOrder.Collection, false), CollectionPlaces.Recall(Guid.Empty, set.Id!.Value));
	}

	[Fact]
	public async Task HideWatched_OnAWatchedSet_SaysSo()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass" };
		var (vm, _, _) = Page(set, CopperAndGlass(1979, 1986, 2000, 2009, 2012, 2016));
		await vm.OnNavigatedTo(set);
		Assert.True(vm.IsWatched);

		vm.HideWatched = true;
		Assert.Empty(vm.Entries);
		Assert.Equal("All 6 films are watched", vm.AllHiddenText);
		vm.ShowWatched();
		Assert.Equal(6, vm.Entries.Count);
		Assert.Equal("", vm.AllHiddenText);
	}

	[Fact]
	public async Task Play_OpensThePlayer_OnTheFirstUnwatchedFilm()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass" };
		var films = CopperAndGlass(watched: 1979);
		var (vm, _, navigation) = Page(set, films);
		await vm.OnNavigatedTo(set);

		await vm.Play();

		var film = films.Single(f => f.ProductionYear == 1986);
		navigation.Verify(x => x.NavigateTo("FluentFin.ViewModels.VideoPlayerViewModel", It.Is<object>(o => ReferenceEquals(o, film) || (o is PlaybackRequest && ((PlaybackRequest)o).Dto == film)), false), Times.Once);
	}

	[Fact]
	public async Task AnEmptyOrFailedCollection_IsWorded()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Winter Films" };
		var (empty, _, _) = Page(set, []);
		await empty.OnNavigatedTo(set);
		Assert.Equal(LoadStatus.Empty, empty.State.Status);
		Assert.Equal("No films in this collection you can see", empty.State.Title);

		var (failed, _, _) = Page(set, null);
		await failed.OnNavigatedTo(set);
		Assert.Equal(LoadStatus.Failed, failed.State.Status);
		Assert.Equal("Couldn't load this collection", failed.State.Title);
	}

	[Fact]
	public async Task APlaylist_IsAnOrderedList_InItsOwnOrder()
	{
		var list = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Playlist, Name = "Rainy Sunday" };
		var items = new[] { "Zulu", "Alpha", "Mike" }.Select(n => new BaseItemDto { Id = Guid.NewGuid(), Name = n, Type = BaseItemDto_Type.Movie, ProductionYear = 2000 }).ToList();
		var (vm, client, _) = Page(list, items);

		await vm.OnNavigatedTo(list);

		Assert.Equal(["Zulu", "Alpha", "Mike"], vm.Entries.Select(e => e.Item.Name));
		Assert.Equal("Order: Playlist", vm.OrderLabel);
		Assert.Equal("Playlist", vm.Kind);
		Assert.Equal("3 films · 0 of 3 watched", vm.Summary);
		client.Verify(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>()), Times.Never);

		var (empty, _, _) = Page(list, []);
		await empty.OnNavigatedTo(list);
		Assert.Equal("This playlist is empty", empty.State.Title);
	}

	[Fact]
	public async Task TheLetters_ComeOnlyFromThirtyItems_SortedByName()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Big" };
		var films = Enumerable.Range(0, 30).Select(i => new BaseItemDto { Id = Guid.NewGuid(), Name = $"{(char)('A' + (i % 26))} film {i}", Type = BaseItemDto_Type.Movie }).ToList();
		var (vm, _, _) = Page(set, films);
		await vm.OnNavigatedTo(set);
		Assert.Empty(vm.Letters);

		vm.SetOrder(CollectionOrder.Name);
		Assert.Equal(26, vm.Letters.Count);

		vm.HideWatched = true; // still 30
		Assert.Equal(26, vm.Letters.Count);
		var (small, _, _) = Page(set, films.Take(29).ToList());
		await small.OnNavigatedTo(set);
		small.SetOrder(CollectionOrder.Name);
		Assert.Empty(small.Letters);
	}

	[Theory]
	[InlineData(BaseItemDto_Type.BoxSet, typeof(CollectionViewModel))]
	[InlineData(BaseItemDto_Type.Playlist, typeof(CollectionViewModel))]
	[InlineData(BaseItemDto_Type.CollectionFolder, typeof(LibraryViewModel))]
	[InlineData(BaseItemDto_Type.Folder, typeof(LibraryViewModel))]
	[InlineData(BaseItemDto_Type.Movie, typeof(MovieViewModel))]
	public void DisplayDto_RoutesEachType(BaseItemDto_Type type, Type page)
	{
		var navigation = new Mock<INavigationServiceCore>();
		var commands = new GlobalCommands(navigation.Object, Mock.Of<IJellyfinClient>(), Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var dto = new BaseItemDto { Id = Guid.NewGuid(), Type = type, Name = "x" };

		commands.DisplayDto(dto);

		navigation.Verify(x => x.NavigateTo(page.FullName!, dto, false), Times.Once);
	}

	// the bug hunt's m5 (the rest of the UX review's X2): every item type opens a page, or says why it cannot; none does nothing
	[Fact]
	public void EveryItemType_OpensSomething_OrSaysWhyNot()
	{
		foreach (var type in Enum.GetValues<BaseItemDto_Type>())
		{
			var navigation = new Mock<INavigationServiceCore>();
			var commands = new GlobalCommands(navigation.Object, Mock.Of<IJellyfinClient>(), Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
			var said = new List<string>();
			commands.CannotOpen += (_, e) => said.Add(e.Title);
			var dto = new BaseItemDto { Id = Guid.NewGuid(), Type = type, Name = "Thing" };

			commands.DisplayDto(dto);

			var navigated = navigation.Invocations.Count(i => i.Method.Name == nameof(INavigationServiceCore.NavigateTo));
			Assert.True(navigated + said.Count == 1, $"{type}: navigated {navigated}, said {said.Count}");
		}

		// the types a library view returns open a page
		foreach (var type in new[] { BaseItemDto_Type.Movie, BaseItemDto_Type.Series, BaseItemDto_Type.Season, BaseItemDto_Type.Episode, BaseItemDto_Type.BoxSet,
			BaseItemDto_Type.Playlist, BaseItemDto_Type.Folder, BaseItemDto_Type.CollectionFolder,
			BaseItemDto_Type.MusicAlbum, BaseItemDto_Type.PhotoAlbum, BaseItemDto_Type.Trailer })
		{
			Assert.NotNull(GlobalCommands.PageFor(type));
		}

		Assert.Equal("Nixie can't open Song here", GlobalCommands.CannotOpenText(new BaseItemDto { Name = "Song", Type = BaseItemDto_Type.Audio }).Title);
	}

	// the bug hunt's m4: a playlist's own order is always offered, and is the way back
	[Fact]
	public async Task APlaylistsOwnOrder_IsAlwaysTheWayBack()
	{
		var list = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Playlist, Name = "Rainy Sunday" };
		var items = new[] { "Zulu", "Alpha", "Mike" }.Select(n => new BaseItemDto { Id = Guid.NewGuid(), Name = n, Type = BaseItemDto_Type.Movie, DateCreated = DateTimeOffset.UtcNow }).ToList();
		var (vm, _, _) = Page(list, items);
		await vm.OnNavigatedTo(list);

		foreach (var order in new[] { CollectionOrder.Name, CollectionOrder.ReleaseDate, CollectionOrder.DateAdded })
		{
			vm.SetOrder(order);
			Assert.NotEqual("Order: Playlist", vm.OrderLabel);
			vm.SetOrder(CollectionOrder.Collection);
			Assert.Equal(["Zulu", "Alpha", "Mike"], vm.Entries.Select(e => e.Item.Name));
			Assert.Equal("Order: Playlist", vm.OrderLabel);
		}
	}

	[Fact]
	public async Task AnAlbum_IsInTrackOrder_AndNotPlayedHere()
	{
		var album = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.MusicAlbum, Name = "Blue" };
		var tracks = new[] { (2, 1, "B"), (1, 2, "Y"), (1, 1, "Z") }.Select(t => new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Audio, ParentIndexNumber = t.Item1, IndexNumber = t.Item2, Name = t.Item3 }).ToList();
		var (vm, _, _) = Page(album, tracks);
		await vm.OnNavigatedTo(album);

		Assert.Equal(["Z", "Y", "B"], vm.Entries.Select(e => e.Item.Name));
		Assert.Equal("Album", vm.Kind);
		Assert.Equal("Order: Album", vm.OrderLabel);
		Assert.False(vm.CanPlay); // songs: Nixie plays films, shows and videos
		Assert.StartsWith("3 songs", vm.Summary, StringComparison.Ordinal);
	}

	[Fact]
	public void Home_HasNoNewInCollectionsOrPlaylists()
	{
		Assert.True(HomeViewModel.HasLatestRow(new BaseItemDto { CollectionType = BaseItemDto_CollectionType.Movies }));
		Assert.True(HomeViewModel.HasLatestRow(new BaseItemDto { CollectionType = BaseItemDto_CollectionType.Tvshows }));
		Assert.False(HomeViewModel.HasLatestRow(new BaseItemDto { CollectionType = BaseItemDto_CollectionType.Boxsets }));
		Assert.False(HomeViewModel.HasLatestRow(new BaseItemDto { CollectionType = BaseItemDto_CollectionType.Playlists }));
		Assert.False(HomeViewModel.HasLatestRow(new BaseItemDto { CollectionType = BaseItemDto_CollectionType.Music }));
	}

	[Fact]
	public void PartOf_NamesTheCollections()
	{
		Assert.Equal("", ItemMeta.PartOfText([]));
		Assert.Equal("Copper and Glass", ItemMeta.PartOfText([new BaseItemDto { Name = "Copper and Glass" }]));
		Assert.Equal("A, B and C", ItemMeta.PartOfText([new() { Name = "A" }, new() { Name = "B" }, new() { Name = "C" }]));
		Assert.Equal("Part of the collection Copper and Glass", ItemMeta.PartOfName(new BaseItemDto { Name = "Copper and Glass" }));
	}

	[Fact]
	public async Task AMoviePage_KnowsItsCollections()
	{
		var id = Guid.NewGuid();
		var movie = new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, Name = "Wild Canyons" };
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass" };
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetItem(id)).ReturnsAsync(movie);
		client.Setup(x => x.GetItemCollections(id)).ReturnsAsync([set]);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var vm = new MovieViewModel(client.Object, commands);

		await vm.OnNavigatedTo(movie);

		Assert.Equal([set], vm.PartOf);
	}

	// ---- edge data (the coordinator's 1.4.0 rule): one film, a collection inside a collection, a playlist with duplicates,
	// and a tested way out of every mode
	[Fact]
	public async Task AOneFilmCollection_ReadsRight()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Single" };
		var (vm, _, _) = Page(set, CopperAndGlass().Take(1).ToList());
		await vm.OnNavigatedTo(set);
		Assert.Equal("1 film · 2000 · 0 of 1 watched", vm.Summary);
		Assert.Equal("1 film", vm.ShownText);
		Assert.Equal("Play Forest of Brightwater, film 1 of 1", vm.PlayName);
	}

	[Fact]
	public async Task ACollectionInsideACollection_IsNotPlayed_AndOpensItsOwnPage()
	{
		var inner = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Box", ProductionYear = 1970, UserData = new UserItemDataDto { Played = false } };
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Outer" };
		var films = CopperAndGlass(watched: 1979);
		var (vm, _, navigation) = Page(set, [inner, .. films]);
		await vm.OnNavigatedTo(set);

		Assert.Equal("Box", vm.Entries[0].Item.Name);             // shown, first by release date
		Assert.Equal("Play Painted Forests", vm.PlayLabel);       // but Play skips it
		Assert.Equal("7 items · 1970–2016 · 1 of 7 watched", vm.Summary);
		Assert.NotEqual("Box", CollectionBrowse.Shuffle([inner, films[1]], _ => 0)?.Name);
		Assert.Null(CollectionBrowse.Next([inner]).Item);

		var commands = new GlobalCommands(navigation.Object, Mock.Of<IJellyfinClient>(), Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		commands.DisplayDto(inner);
		navigation.Verify(x => x.NavigateTo(typeof(CollectionViewModel).FullName!, inner, false), Times.Once);
	}

	[Fact]
	public async Task APlaylistWithTheSameItemTwice_ShowsBoth_InPlace()
	{
		var list = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Playlist, Name = "Twice" };
		var id = Guid.NewGuid();
		BaseItemDto Film(string name, Guid? fid = null) => new() { Id = fid ?? Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.Movie };
		var (vm, _, _) = Page(list, [Film("Alpha", id), Film("Mike"), Film("Alpha", id)]);
		await vm.OnNavigatedTo(list);

		Assert.Equal(["Alpha", "Mike", "Alpha"], vm.Entries.Select(e => e.Item.Name));
		Assert.Equal(["1", "2", "3"], vm.Entries.Select(e => e.PositionText));
		vm.SetOrder(CollectionOrder.Name);
		Assert.Equal(["Alpha", "Alpha", "Mike"], vm.Entries.Select(e => e.Item.Name));
	}

	[Fact]
	public async Task EveryMode_HasAWayBack()
	{
		var set = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet, Name = "Copper and Glass", DisplayOrder = null };
		var (vm, _, _) = Page(set, CopperAndGlass(watched: 1979));
		await vm.OnNavigatedTo(set);
		var start = Years(vm.Entries.Select(e => e.Item.Dto));

		foreach (var order in new[] { CollectionOrder.Name, CollectionOrder.DateAdded, CollectionOrder.ReleaseDate })
		{
			vm.SetOrder(order);
			vm.SetOrder(CollectionOrder.Collection);
			Assert.Equal(start, Years(vm.Entries.Select(e => e.Item.Dto)));
			Assert.Equal("1", vm.Entries[0].PositionText);
		}

		vm.HideWatched = true;
		vm.HideWatched = false;
		Assert.Equal(6, vm.Entries.Count);
		Assert.Equal("6 films", vm.ShownText);
	}

	[Fact]
	public async Task TheLibrariesPage_PutsCollectionsAndPlaylistsApart_WithCounts()
	{
		BaseItemDto View(string name, BaseItemDto_CollectionType type) => new() { Id = Guid.NewGuid(), Name = name, CollectionType = type, Type = BaseItemDto_Type.CollectionFolder };
		var movies = View("Movies", BaseItemDto_CollectionType.Movies);
		var boxsets = View("Collections", BaseItemDto_CollectionType.Boxsets);
		var shows = View("Shows", BaseItemDto_CollectionType.Tvshows);
		var playlists = View("Playlists", BaseItemDto_CollectionType.Playlists);
		var client = new Mock<IJellyfinClient>();
		client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies, boxsets, shows, playlists]);
		client.Setup(x => x.GetLibraryCounts(movies)).ReturnsAsync(new LibraryCounts(320, 97));
		client.Setup(x => x.GetLibraryCounts(boxsets)).ReturnsAsync(new LibraryCounts(12, null));
		var vm = new LibrariesLandingPageViewModel(client.Object, Mock.Of<INavigationServiceCore>());

		await vm.OnNavigatedTo(null!);

		Assert.Equal(LoadStatus.Loaded, vm.State.Status);
		Assert.Equal(["Movies", "Shows"], vm.Media.Select(t => t.Name));
		Assert.Equal(["Collections", "Playlists"], vm.Collections.Select(t => t.Name));
		Assert.True(vm.HasCollections);
		var tile = vm.Media[0];
		await Task.Yield();
		Assert.Equal(" · 320 · 97 unwatched", tile.Detail);
		Assert.Equal("Movies, 320 movies, 97 unwatched", tile.AutomationName);
		Assert.Equal("Collections, 12 collections", vm.Collections[0].AutomationName);
		Assert.Equal("Shows", vm.Media[1].AutomationName); // no counts: the name alone
	}

	[Fact]
	public async Task TheLibrariesPage_SaysWhenItCannotRead_AndRetries()
	{
		var client = new Mock<IJellyfinClient>();
		client.SetupSequence(x => x.GetUserViewsOrNull()).ReturnsAsync((IReadOnlyList<BaseItemDto>?)null).ReturnsAsync([]);
		var vm = new LibrariesLandingPageViewModel(client.Object, Mock.Of<INavigationServiceCore>());

		await vm.OnNavigatedTo(null!);
		Assert.Equal(LoadStatus.Failed, vm.State.Status);
		Assert.Equal("Couldn't load your libraries", vm.State.Title);

		await vm.State.RetryCommand.ExecuteAsync(null);
		Assert.Equal(LoadStatus.Empty, vm.State.Status);
		Assert.Equal("No libraries yet", vm.State.Title);
	}

	[Theory]
	[InlineData(1832, 6, 3)]  // fits five a row: three and three, no orphan
	[InlineData(1832, 4, 4)]
	[InlineData(1012, 6, 3)]  // fits three a row
	[InlineData(1012, 4, 2)]  // fits three: two and two
	[InlineData(1012, 2, 2)]
	[InlineData(600, 3, 1)]
	public void TheLibraryTiles_ShareTheRows(double width, int count, int columns)
	{
		var (c, tile) = PageGridMetrics.Tiles(width, count);
		Assert.Equal(columns, c);
		Assert.InRange(tile, Math.Min(PageGridMetrics.MinTileWidth, width), PageGridMetrics.MaxTileWidth);
		Assert.True(c * tile + (c - 1) * PageGridMetrics.TileSpacing <= width + 0.5);
	}

	[Theory]
	[InlineData(1832, 202, 8)]
	[InlineData(1012, 202, 4)]
	public void TheCollectionGrid_EndsAtTheToolbarsEdge(double width, double item, int columns)
	{
		var gap = PageGridMetrics.ColumnSpacing(width, item);
		Assert.True(gap >= 16);
		Assert.Equal(columns, (int)Math.Floor((width + gap) / (item + gap)));
		Assert.InRange(width - (columns * item + (columns - 1) * gap), 0, columns);
	}

	[Theory]
	[InlineData(36, 36, 1920, 36)]
	[InlineData(40, 96, 1100, 40)]
	[InlineData(40, 96, 1920, 96)]
	[InlineData(40, 96, 1510, 68)]   // halfway: 68, on the 4 epx grid
	[InlineData(40, 96, 1500, 68)]   // 67.3 snaps to 68
	[InlineData(48, 96, 800, 48)]
	[InlineData(48, 96, 2560, 96)]
	public void TheGutter_GrowsWithTheWindow_OnTheGrid(double narrow, double wide, double width, double expected)
	{
		var gutter = PageGridMetrics.Gutter(narrow, wide, width);
		Assert.Equal(expected, gutter);
		Assert.Equal(0, gutter % 4);
	}

	[Fact]
	public void TheTitle_SitsAtTheThemesY()
	{
		Assert.Equal(24, PageGridMetrics.TitleTop(72, 48)); // a page below the bar
		Assert.Equal(72, PageGridMetrics.TitleTop(72, 0));  // a page under the bar
		Assert.Equal(0, PageGridMetrics.TitleTop(40, 48));
	}
}
