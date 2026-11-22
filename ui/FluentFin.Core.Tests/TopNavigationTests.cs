using System.Text.Json;
using System.Text.Json.Nodes;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Journal;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail); Nixie 1.4.0: folded by width, one destination lit by the trail rule. Nixie
/// 1.6 (the review's T1 to T4): Home, up to three favourite libraries (the user's stars, else their 1.3 pins, else Movies
/// and Shows by kind and the libraries opened most), Collections, "Libraries ▾" (always a menu); the plugins' sections in
/// the account menu.
/// </summary>
public sealed class TopNavigationTests
{
	private static BaseItemDto Library(string name, BaseItemDto_CollectionType? type = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };

	private static readonly CustomNavigationViewItem Diary = new() { Key = typeof(DiaryViewModel).FullName!, Name = "Diary", Glyph = "" };

	private static readonly string Movie = typeof(MovieViewModel).FullName!;
	private static readonly string Settings = typeof(SettingsViewModel).FullName!;
	private static readonly string Search = typeof(SearchResultsViewModel).FullName!;

	private static List<BaseItemDto> Libraries(int count) => Enumerable.Range(1, count).Select(i => Library($"Library {i}")).ToList();

	private static List<Guid> Ids(IEnumerable<BaseItemDto> libraries) => [.. libraries.Select(l => l.Id!.Value)];

	/// <summary>A server as the review found them: Movies, Shows, Music, then its Collections and Playlists views.</summary>
	private static (BaseItemDto Movies, BaseItemDto Shows, BaseItemDto Music, BaseItemDto BoxSets, BaseItemDto Playlists, List<BaseItemDto> All) Server()
	{
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		var shows = Library("Shows", BaseItemDto_CollectionType.Tvshows);
		var music = Library("Music", BaseItemDto_CollectionType.Music);
		var boxSets = Library("Collections", BaseItemDto_CollectionType.Boxsets);
		var playlists = Library("Playlists", BaseItemDto_CollectionType.Playlists);
		return (movies, shows, music, boxSets, playlists, [boxSets, movies, playlists, shows, music]);
	}

	// ---- the row

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(3)]
	public void TheRow_IsHome_TheFavourites_Collections_ThenLibraries(int favourites)
	{
		var libraries = Libraries(9);

		var tabs = TopTabs.Build(libraries, Ids(libraries.Take(favourites)));

		Assert.Equal(["Home", .. libraries.Take(favourites).Select(l => l.Name!), "Collections", "Libraries"], tabs.Select(t => t.Title));
		Assert.Equal([TopTabKind.Home, .. Enumerable.Repeat(TopTabKind.Library, favourites), TopTabKind.Collections, TopTabKind.Libraries], tabs.Select(t => t.Kind));
	}

	[Fact]
	public void TheFavourites_AreAtMostThree_InTheirOwnOrder_AndOnlyLibrariesTheServerLists()
	{
		var libraries = Libraries(6);

		var tabs = TopTabs.Build(libraries, [libraries[4].Id!.Value, Guid.NewGuid(), libraries[1].Id!.Value, libraries[0].Id!.Value, libraries[2].Id!.Value]);

		Assert.Equal(["Library 5", "Library 2", "Library 1"], tabs.Where(t => t.Kind == TopTabKind.Library).Select(t => t.Title));
	}

	[Fact]
	public void Libraries_IsAlwaysAMenu_OfAllLibrariesThenEveryMediaLibrary_InTheServersOrder()
	{
		// the review's T3: one meaning at every width
		var server = Server();

		var libraries = TopTabs.Build(server.All, [server.Shows.Id!.Value]).Single(t => t.Kind == TopTabKind.Libraries);

		Assert.True(libraries.HasMenu);
		Assert.Equal([TopTabs.AllLibraries, "Movies", "Shows", "Music"], libraries.Menu.Select(t => t.Title));
		Assert.Equal(TopTabs.LibrariesKey, libraries.Menu[0].Key);
		Assert.Equal([TopTabs.AllLibraries, null, "Movies", "Shows", "Music"], TopMenus.Libraries(libraries).Select(t => t?.Title));
	}

	[Fact]
	public void NoLibrariesListed_LibrariesIsStillAMenu_WithAllLibraries()
	{
		// the list failed or is empty: All libraries, whose page says so (its own load state)
		var tabs = TopTabs.Build([], []);

		Assert.Equal(["Home", "Collections", "Libraries"], tabs.Select(t => t.Title));
		var libraries = tabs.Single(t => t.Kind == TopTabKind.Libraries);
		Assert.True(libraries.HasMenu);
		Assert.Equal([TopTabs.AllLibraries], TopMenus.Libraries(libraries).Select(t => t?.Title));
	}

	[Fact]
	public void TheServersCollectionsAndPlaylistsViews_AreNoTabs_OneCollectionsDestinationHoldsThem()
	{
		// the review's T2
		var server = Server();

		var tabs = TopTabs.Build(server.All, Ids([server.BoxSets, server.Movies, server.Playlists]));

		Assert.Equal(["Home", "Movies", "Collections", "Libraries"], tabs.Select(t => t.Title));
		var collections = tabs.Single(t => t.Kind == TopTabKind.Collections);
		Assert.Equal(TopTabs.CollectionsMark, collections.Mark);
		Assert.Equal(Ids([server.BoxSets, server.Playlists]), collections.Views);
		Assert.DoesNotContain(tabs.Single(t => t.Kind == TopTabKind.Libraries).Menu, t => t.Title is "Collections" or "Playlists");
	}

	[Fact]
	public void ThePluginsSections_GoToTheAccountMenu_NotTheBar()
	{
		// the review's T4
		var reports = new CustomNavigationViewItem { Key = "FluentFin.Plugins.Playback_Reporting.ViewModels.PlaybackReportingDashboardViewModel", Name = "Playback reports", Glyph = "" };
		var pin = new CustomNavigationViewItem { Key = TopTabs.LibraryKey, Name = "Library 3", Parameter = Guid.NewGuid(), Persistent = true };

		var sections = TopTabs.SectionTabs([Diary, pin, reports, Diary with { }]);

		Assert.Equal(["Diary", "Playback reports"], sections.Select(t => t.Title));
		Assert.Equal("", sections[0].Glyph);
		Assert.DoesNotContain(TopTabs.Build(Libraries(3), []), t => t.Kind == TopTabKind.Section);
	}

	// ---- the fold by width

	[Fact]
	public void TheFold_DropsTheLastFavourites_FromTheRight_TheMenuKeepsThem()
	{
		var libraries = Libraries(6);
		var tabs = TopTabs.Build(libraries, Ids(libraries.Take(3)));

		var row = TopTabs.Fold(tabs, 1, lit: null);

		Assert.Equal(["Home", "Library 1", "Collections", "Libraries"], row.Select(t => t.Title));
		Assert.Equal([TopTabs.AllLibraries, .. libraries.Select(l => l.Name!)], row[^1].Menu.Select(t => t.Title));
		Assert.Equal(["Home", "Collections", "Libraries"], TopTabs.Fold(tabs, 0, null).Select(t => t.Title));
		Assert.Equal(tabs.Select(t => t.Title), TopTabs.Fold(tabs, 3, null).Select(t => t.Title));
	}

	[Fact]
	public void Libraries_IsNamedAfterALitLibraryWithoutATab_AndMarksIt()
	{
		var libraries = Libraries(6);
		var tabs = TopTabs.Build(libraries, Ids(libraries.Take(3)));
		var anime = new TopMark(TopTabs.LibraryKey, libraries[5].Id);

		var row = TopTabs.Fold(tabs, 3, anime);

		var menu = row.Single(t => t.Kind == TopTabKind.Libraries);
		Assert.Equal("Library 6", menu.Title);
		Assert.Equal(row.ToList().IndexOf(menu), TopTabs.IndexOf(row, anime));
		// a favourite folded away is lit the same way
		var second = new TopMark(TopTabs.LibraryKey, libraries[1].Id);
		var folded = TopTabs.Fold(tabs, 1, second);
		Assert.Equal("Library 2", folded.Single(t => t.Kind == TopTabKind.Libraries).Title);
		Assert.Equal(folded.Count - 1, TopTabs.IndexOf(folded, second));
		// a lit favourite with its tab lights its tab; the menu says "Libraries"
		var row2 = TopTabs.Fold(tabs, 3, second);
		Assert.Equal("Libraries", row2.Single(t => t.Kind == TopTabKind.Libraries).Title);
		Assert.Equal(2, TopTabs.IndexOf(row2, second));
	}

	[Fact]
	public void TheNarrowMenu_IsFlat_TheBarsDestinations_ThenAllLibraries_AndTheOthers()
	{
		var libraries = Libraries(4);
		var tabs = TopTabs.Build(libraries, Ids([libraries[2]]));

		Assert.Equal(["Home", "Library 3", "Collections", null, TopTabs.AllLibraries, "Library 1", "Library 2", "Library 4"], TopMenus.Sections(tabs).Select(t => t?.Title));
	}

	// ---- the favourites

	[Fact]
	public void TheDefaults_AreMoviesAndShowsByKind_ThenTheMostOpened_NeverTheServersOrder()
	{
		// the owner's server: its first libraries are "[HUB] …" hubs, movies and shows among them
		var hubMovies = Library("[HUB] Trending Movies", BaseItemDto_CollectionType.Movies);
		var hubKids = Library("[HUB] Kids Movies", BaseItemDto_CollectionType.Movies);
		var hubShows = Library("[HUB] Trending Shows", BaseItemDto_CollectionType.Tvshows);
		var docs = Library("Documentaries", BaseItemDto_CollectionType.Movies);
		var films = Library("Films", BaseItemDto_CollectionType.Movies);
		var anime = Library("Anime", BaseItemDto_CollectionType.Tvshows);
		var tv = Library("TV", BaseItemDto_CollectionType.Tvshows);
		var music = Library("Music", BaseItemDto_CollectionType.Music);
		List<BaseItemDto> views = [hubMovies, hubKids, hubShows, docs, films, anime, tv, music];

		// nothing opened yet: the first plain-named library of each kind, no hub, nothing by order alone
		Assert.Equal(Ids([docs, anime]), TopFavourites.Defaults(views, new Dictionary<Guid, int>()));

		// then the most opened (ties: the server's order); one already picked is not picked twice
		var opens = new Dictionary<Guid, int> { [music.Id!.Value] = 2, [tv.Id!.Value] = 5, [docs.Id!.Value] = 9, [hubKids.Id!.Value] = 5 };
		Assert.Equal(Ids([docs, anime, hubKids]), TopFavourites.Defaults(views, opens));

		// a server of hubs only: the first of each kind after all
		Assert.Equal(Ids([hubMovies, hubShows]), TopFavourites.Defaults([hubMovies, hubShows], new Dictionary<Guid, int>()));
		// no movies or shows: the opened ones only, none of the rest
		Assert.Empty(TopFavourites.Defaults([music, Library("Books", BaseItemDto_CollectionType.Books)], new Dictionary<Guid, int>()));
	}

	[Theory]
	[InlineData("[HUB] Trending Movies", true)]
	[InlineData("  [4K] Films", true)]
	[InlineData("{HUB} Kids", true)]
	[InlineData("[REC]", false)]
	[InlineData("Movies", false)]
	[InlineData("[] Odd", false)]
	[InlineData("", false)]
	[InlineData(null, false)]
	public void AHubsName_StartsWithABracketedTag(string? name, bool tagged) => Assert.Equal(tagged, TopFavourites.IsTagged(name));

	[Fact]
	public void TheStars_WinOverThePins_ThePinsOverTheDefaults_AndAGoneLibraryIsDroppedQuietly()
	{
		var server = Server();
		var gone = Guid.NewGuid();
		var none = new Dictionary<Guid, int>();

		Assert.Equal(Ids([server.Music]), TopFavourites.Pick(server.All, chosen: null, pins: [gone, server.Music.Id!.Value, server.BoxSets.Id!.Value], none));
		// pins that all went: the defaults
		Assert.Equal(Ids([server.Movies, server.Shows]), TopFavourites.Pick(server.All, null, [gone], none));
		Assert.Equal(Ids([server.Shows]), TopFavourites.Pick(server.All, chosen: [gone, server.Shows.Id!.Value], [server.Music.Id!.Value], none));
		// every star removed: none, not the defaults back
		Assert.Empty(TopFavourites.Pick(server.All, chosen: [], [server.Music.Id!.Value], none));
	}

	[Fact]
	public void AStar_AddsOrRemoves_AndAFourthIsRefused()
	{
		var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();

		Check(TopFavourites.Toggle([a], b), [a, b], FavouriteChange.Added);
		Check(TopFavourites.Toggle([a, b], a), [b], FavouriteChange.Removed);
		Check(TopFavourites.Toggle([a, b, c], d), [a, b, c], FavouriteChange.Full);
		Check(TopFavourites.Toggle([a, b, c], b), [a, c], FavouriteChange.Removed);

		static void Check((IReadOnlyList<Guid> Favourites, FavouriteChange Change) r, List<Guid> favourites, FavouriteChange change)
		{
			Assert.Equal(favourites, r.Favourites);
			Assert.Equal(change, r.Change);
		}
	}

	[Fact]
	public void TheStarsName_SaysWhatItDoes()
	{
		Assert.Equal("Add Movies to the top bar", TopFavourites.StarName("Movies", favourite: false));
		Assert.Equal("Remove Movies from the top bar", TopFavourites.StarName(" Movies ", favourite: true));
		Assert.Equal("Add this library to the top bar", TopFavourites.StarName(null, false));
		Assert.Equal("Up to 3 favourites in the top bar. Remove a star to add Anime.", TopFavourites.FullMessage("Anime"));
	}

	// ---- the Collections destination

	// 1.6 integration: topbar's interim route (CollectionsRoute: the server's box-set view, the playlists view for its section,
	// else the Libraries page lit as Collections) is retired with its test: Collections is the collections stream's page
	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void Collections_WithoutAnOpener_OpensTheCollectionsPage_WhateverTheServersViews(bool views)
	{
		var server = Server();
		var top = Create();
		top.SetLibraries(views ? server.All : [server.Movies]);
		_navigation.Setup(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>())).Returns(true);

		Assert.True(top.Open(top.Tabs.Single(t => t.Kind == TopTabKind.Collections)));

		_navigation.Verify(x => x.NavigateTo(typeof(CollectionsViewModel).FullName!, new CollectionsPlace(CollectionsSection.All), false), Times.Once);
		_navigation.Verify(x => x.NavigateTo(TopTabs.LibrariesKey, It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);
		_navigation.Verify(x => x.NavigateTo(TopTabs.LibraryKey, It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);
	}

	[Fact]
	public void TheCollectionsDestination_LightsItself_WithAndWithoutTheServersViews()
	{
		var server = Server();
		var tabs = TopTabs.Build(server.All, []);
		var home = new TopMark(TopTabs.HomeKey);

		Assert.Equal(TopTabs.CollectionsMark, TopTabs.MarkFor(tabs, TopTabs.LibraryKey, server.BoxSets, home));
		Assert.Equal(TopTabs.CollectionsMark, TopTabs.MarkFor(tabs, TopTabs.LibraryKey, server.Playlists.Id!.Value, home));
		// the Libraries page is Libraries (1.6 integration: never shown for Collections now)
		var bare = TopTabs.Build([server.Movies], []);
		Assert.Equal(new TopMark(TopTabs.LibrariesKey), TopTabs.MarkFor(bare, TopTabs.LibrariesKey, null, home));
		// the Collections page (1.6 integration: the collections stream's, at any place, See all included), with or without views
		Assert.Equal(typeof(CollectionsViewModel).FullName, TopTabs.CollectionsKey);
		Assert.Equal(TopTabs.CollectionsMark, TopTabs.MarkFor(bare, TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.Playlists), home));
		Assert.Equal(TopTabs.CollectionsMark, TopTabs.MarkFor(tabs, TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.MadeByNixie, null, SeeAll: true), home));
		// a box set opened from it is drilled into: Collections stays lit
		var boxSet = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		Assert.Equal(TopTabs.CollectionsMark, TopTabs.MarkFor(tabs, TopTabs.LibraryKey, boxSet, TopTabs.CollectionsMark));
		Assert.Equal(TopTabs.CollectionsMark, TopTabs.MarkFor(tabs, Movie, null, TopTabs.CollectionsMark));
	}

	// ---- the trail rule: which destination a page lights

	[Fact]
	public void ADestinationChosen_LightsItself()
	{
		var movies = Library("Movies");
		var shows = Library("Shows");
		var tabs = TopTabs.Build([movies, shows], Ids([movies]));
		var sections = TopTabs.SectionTabs([Diary]);
		var home = new TopMark(TopTabs.HomeKey);

		Assert.Equal(home, TopTabs.MarkFor(tabs, TopTabs.HomeKey, null, null));
		// a library with a tab and one in the menu only
		Assert.Equal(new TopMark(TopTabs.LibraryKey, shows.Id), TopTabs.MarkFor(tabs, TopTabs.LibraryKey, shows, home));
		Assert.Equal(new TopMark(TopTabs.LibraryKey, movies.Id), TopTabs.MarkFor(tabs, TopTabs.LibraryKey, movies.Id!.Value, home));
		Assert.Equal(new TopMark(TopTabs.LibrariesKey), TopTabs.MarkFor(tabs, TopTabs.LibrariesKey, null, home));
		Assert.Equal(new TopMark(Diary.Key), TopTabs.MarkFor(tabs, Diary.Key, null, home, sections: sections));
		Assert.Equal(TopMark.Search, TopTabs.MarkFor(tabs, Search, "quiet", home));
		Assert.Equal(TopMark.Settings, TopTabs.MarkFor(tabs, Settings, null, home));
	}

	[Fact]
	public void APageDrilledInto_KeepsTheLitDestination()
	{
		var movies = Library("Movies");
		var shows = Library("Shows");
		var tabs = TopTabs.Build([movies, shows], Ids([movies, shows]));
		var lit = new TopMark(TopTabs.LibraryKey, shows.Id);

		Assert.Equal(lit, TopTabs.MarkFor(tabs, Movie, null, lit));
		Assert.Equal(lit, TopTabs.MarkFor(tabs, "FluentFin.ViewModels.VideoPlayerViewModel", null, lit));
		Assert.Equal(TopMark.Search, TopTabs.MarkFor(tabs, Movie, null, TopMark.Search));
		Assert.Equal(TopMark.Settings, TopTabs.MarkFor(tabs, "FluentFin.Views.JellyfinSettingsViewModel", null, TopMark.Settings));
		Assert.Equal(TopMark.Settings, TopTabs.MarkFor(tabs, typeof(DashboardViewModel).FullName!, null, TopMark.Settings));
		var boxSet = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		Assert.Equal(lit, TopTabs.MarkFor(tabs, TopTabs.LibraryKey, boxSet, lit));
		Assert.Equal(TopMark.Search, TopTabs.MarkFor(tabs, Movie, null, lit, fromSearch: true));
	}

	// ---- the service

	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly MemorySettings _settings = new();
	private string? _profile = "server-a|user-1";

	private TopNavigation Create()
	{
		var top = new TopNavigation(_navigation.Object, _settings, NullLogger<TopNavigation>.Instance) { Profile = () => _profile };
		top.Reset();
		return top;
	}

	[Fact]
	public void TheDefaultTwo_ThenAStar_IsKeptForThisServerAndUser()
	{
		var server = Server();
		var top = Create();
		top.SetLibraries(server.All);
		Assert.Equal(["Home", "Movies", "Shows", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));

		Assert.Equal(FavouriteChange.Added, top.ToggleFavourite(server.Music.Id!.Value));
		Assert.Equal(["Home", "Movies", "Shows", "Music", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));
		Assert.True(top.IsFavourite(server.Music.Id));

		// the next start: the same bar
		var again = Create();
		again.SetLibraries(server.All);
		Assert.Equal(Ids([server.Movies, server.Shows, server.Music]), again.Favourites);

		// another user on the same server, or the same user elsewhere: their own bar
		_profile = "server-a|user-2";
		var other = Create();
		other.SetLibraries(server.All);
		Assert.Equal(Ids([server.Movies, server.Shows]), other.Favourites);
	}

	[Fact]
	public void AFourthStar_IsRefused_TheBarUnchanged_AndRemovingOneMakesRoom()
	{
		var libraries = Libraries(5);
		var top = Create();
		top.SetLibraries(libraries);
		foreach (var library in libraries.Take(3))
		{
			Assert.Equal(FavouriteChange.Added, top.ToggleFavourite(library.Id!.Value));
		}

		var saves = _settings.Saves;
		Assert.Equal(FavouriteChange.Full, top.ToggleFavourite(libraries[3].Id!.Value));
		Assert.Equal(Ids(libraries.Take(3)), top.Favourites);
		Assert.Equal(saves, _settings.Saves);

		Assert.Equal(FavouriteChange.Removed, top.ToggleFavourite(libraries[0].Id!.Value));
		Assert.Equal(FavouriteChange.Added, top.ToggleFavourite(libraries[3].Id!.Value));
		Assert.Equal(["Home", "Library 2", "Library 3", "Library 4", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));
		// a star on something that is not a library of this server does nothing
		Assert.Equal(FavouriteChange.NotALibrary, top.ToggleFavourite(Guid.NewGuid()));
	}

	[Fact]
	public void The13Pins_AreTheFirstFavourites_ReadNeverWritten_AndAGonePinIsDroppedQuietly()
	{
		var libraries = Libraries(6);
		_settings.Values[TopNavigation.PinsSetting] = JsonSerializer.SerializeToNode(new List<CustomNavigationViewItem>
		{
			new() { Key = TopTabs.LibraryKey, Name = "Gone", Parameter = Guid.NewGuid(), Persistent = true },
			new() { Key = TopTabs.LibraryKey, Name = "Library 5", Parameter = libraries[4].Id, Persistent = true },
			new() { Key = "FluentFin.Core.ViewModels.DiaryViewModel", Name = "Diary", Persistent = true },
			new() { Key = TopTabs.LibraryKey, Name = "Library 2", Parameter = libraries[1].Id, Persistent = true },
		});
		var pins = _settings.Values[TopNavigation.PinsSetting]!.ToJsonString();

		var top = Create();
		top.SetLibraries(libraries);

		Assert.Equal(["Home", "Library 5", "Library 2", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));
		// a star starts from them
		top.ToggleFavourite(libraries[0].Id!.Value);
		Assert.Equal(Ids([libraries[4], libraries[1], libraries[0]]), top.Favourites);
		Assert.Equal(pins, _settings.Values[TopNavigation.PinsSetting]!.ToJsonString());
		// a pins setting that cannot be read costs the pins, never the bar
		_settings.Values[TopNavigation.PinsSetting] = JsonValue.Create("not a list");
		_profile = "server-b|user-1";
		var broken = Create();
		broken.SetLibraries(libraries);
		Assert.Empty(broken.Favourites);
		Assert.Equal(["Home", "Collections", "Libraries"], broken.Tabs.Select(t => t.Title));
	}

	[Fact]
	public void OpeningALibrary_CountsForTheDefaults_NotBackNorTheSamePageAgain()
	{
		// the review of 1.6: Music is opened once and shown again four times (twice R, twice Back), Books opened twice; were
		// either kind counted, Music (3) would beat Books (2) for the third default
		var server = Server();
		var books = Library("Books", BaseItemDto_CollectionType.Books);
		List<BaseItemDto> views = [.. server.All, books];
		var top = Create();
		top.SetLibraries(views);

		top.OnPageShown(TopTabs.LibraryKey, server.Music, depth: 0);
		top.OnPageShown(TopTabs.LibraryKey, server.Music, depth: 0); // R: the same page again
		top.OnPageShown(TopTabs.LibraryKey, server.Music, depth: 0); // and again
		top.OnPageShown(Movie, null, depth: 1);
		top.OnPageShown(TopTabs.LibraryKey, server.Music, depth: 0, returning: true); // Back
		top.OnPageShown(Movie, null, depth: 1);
		top.OnPageShown(TopTabs.LibraryKey, server.Music, depth: 0, returning: true); // Back again
		top.OnPageShown(TopTabs.LibraryKey, books, depth: 1);
		top.OnPageShown(TopTabs.HomeKey, null, depth: 2);
		top.OnPageShown(TopTabs.LibraryKey, books, depth: 3);
		foreach (var _ in Enumerable.Range(0, 3))
		{
			top.OnPageShown(TopTabs.LibraryKey, server.BoxSets, depth: 4); // not a media library
			top.OnPageShown(TopTabs.HomeKey, null, depth: 5);
		}

		Assert.Equal(["Home", "Movies", "Shows", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));

		// the next start: Books opened twice, Music once, so Books is the third default
		var again = Create();
		again.SetLibraries(views);
		Assert.Equal(["Home", "Movies", "Shows", "Books", "Collections", "Libraries"], again.Tabs.Select(t => t.Title));
		// counted for this server and user only
		_profile = "server-b|user-1";
		var elsewhere = Create();
		elsewhere.SetLibraries(server.All);
		Assert.Equal(Ids([server.Movies, server.Shows]), elsewhere.Favourites);
	}

	[Fact]
	public void ALibraryRemovedOnTheServer_LeavesTheBarQuietly_AndTheLitDestinationFollows()
	{
		var server = Server();
		var top = Create();
		top.SetLibraries(server.All);
		top.ToggleFavourite(server.Music.Id!.Value);
		top.OnPageShown(TopTabs.LibraryKey, server.Shows);

		top.SetLibraries([server.Movies, server.Music]);

		Assert.Equal(["Home", "Movies", "Music", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));
		Assert.Equal(-1, top.SelectedIndex);
		// it comes back: so does its star
		top.SetLibraries(server.All);
		Assert.Equal(["Home", "Movies", "Shows", "Music", "Collections", "Libraries"], top.Tabs.Select(t => t.Title));
	}

	[Fact]
	public void TheCollectionsTab_OpensThroughItsOpener_AndChosenAgainOnItsPageReturnsToItsTop()
	{
		var server = Server();
		var top = Create();
		top.SetLibraries(server.All);
		var opened = new List<CollectionsSection>();
		top.OpenCollections = opened.Add;
		var returned = 0;
		top.ReturnToTopRequested += (_, _) => returned++;
		var collections = top.Tabs.Single(t => t.Kind == TopTabKind.Collections);

		Assert.True(top.Open(collections));
		Assert.Equal([CollectionsSection.All], opened);

		// 1.6 integration: its page is the Collections page (GlobalCommands.OpenCollections), at any section
		top.OnPageShown(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.All), depth: 1);
		Assert.Equal("Collections", top.SelectedTab?.Title);
		top.Open(collections);
		Assert.Equal(1, returned);
		Assert.Single(opened);
		top.OnPageShown(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.Playlists), depth: 1);
		top.Open(collections);
		Assert.Equal(2, returned);
		Assert.Single(opened);

		// from a See all (every franchise) drilled into from it: its page again, not the See all's top
		top.OnPageShown(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.MadeByNixie, null, SeeAll: true), depth: 2);
		Assert.Equal("Collections", top.SelectedTab?.Title);
		top.Open(collections);
		Assert.Equal(2, opened.Count);
		Assert.Equal(2, returned);

		// from a box set drilled into: opened again
		top.OnPageShown(TopTabs.LibraryKey, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.BoxSet }, depth: 3);
		Assert.Equal("Collections", top.SelectedTab?.Title);
		top.Open(collections);
		Assert.Equal(3, opened.Count);

		// from the server's box-set view (lit as Collections, but not its page): the Collections page
		top.OnPageShown(TopTabs.LibraryKey, server.BoxSets, depth: 1);
		Assert.Equal("Collections", top.SelectedTab?.Title);
		top.Open(collections);
		Assert.Equal(4, opened.Count);
		Assert.Equal(2, returned);
	}

	// 1.6 integration: topbar's in-place re-light (the Libraries page shown for Collections on a server with neither view, lit
	// as Collections or Libraries without the frame) is retired with its route. The review's case, Collections then All
	// libraries then Collections doing nothing, is kept: on the Collections page each step is a page of its own
	[Fact]
	public void WithoutTheServersViews_CollectionsOpensItsPage_LitAsCollections_AndAllLibrariesStillOpens()
	{
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		var top = Create();
		top.SetLibraries([movies]);
		var opened = new List<CollectionsSection>();
		top.OpenCollections = opened.Add;
		_navigation.Setup(x => x.NavigateTo(TopTabs.LibrariesKey, null, false)).Returns(true);
		var collections = top.Tabs.Single(t => t.Kind == TopTabKind.Collections);
		var all = top.Tabs.Single(t => t.Kind == TopTabKind.Libraries).Menu[0];

		Assert.True(top.Open(collections));
		Assert.Equal([CollectionsSection.All], opened);
		top.OnPageShown(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.All), depth: 1);
		Assert.Equal("Collections", top.SelectedTab?.Title);

		Assert.True(top.Open(all));
		_navigation.Verify(x => x.NavigateTo(TopTabs.LibrariesKey, null, false), Times.Once);
		top.OnPageShown(TopTabs.LibrariesKey, null, depth: 2);
		Assert.Equal("Libraries", top.SelectedTab?.Title);
	}

	[Fact]
	public void WithoutTheServersViews_CollectionsAfterAllLibraries_OpensItsPageAgain()
	{
		// the review of 1.6: Collections, All libraries, Collections: the third step did nothing and Collections never lit
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		var top = Create();
		top.SetLibraries([movies]);
		var opened = new List<CollectionsSection>();
		top.OpenCollections = opened.Add;
		var returned = 0;
		top.ReturnToTopRequested += (_, _) => returned++;
		var collections = top.Tabs.Single(t => t.Kind == TopTabKind.Collections);
		var all = top.Tabs.Single(t => t.Kind == TopTabKind.Libraries).Menu[0];

		top.Open(collections);
		top.OnPageShown(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.All), depth: 1);
		top.Open(all);
		top.OnPageShown(TopTabs.LibrariesKey, null, depth: 2);
		Assert.Equal("Libraries", top.SelectedTab?.Title);

		Assert.True(top.Open(collections));

		Assert.Equal(2, opened.Count);
		Assert.Equal(0, returned);
		top.OnPageShown(TopTabs.CollectionsKey, new CollectionsPlace(CollectionsSection.All), depth: 3);
		Assert.Equal("Collections", top.SelectedTab?.Title);
		// Back to the Libraries page lights Libraries; All libraries there is its top
		top.OnPageShown(TopTabs.LibrariesKey, null, depth: 2, returning: true);
		Assert.Equal("Libraries", top.SelectedTab?.Title);
		top.Open(all);
		Assert.Equal(1, returned);
	}

	[Fact]
	public void TheDiary_IsInTheAccountMenu_AndLightsTheAccount()
	{
		var top = Create();
		top.AddNavigationItem(Diary);
		top.AddNavigationItem(Diary with { });
		top.SetLibraries([Library("Movies", BaseItemDto_CollectionType.Movies)]);

		Assert.DoesNotContain(top.Tabs, t => t.Title == "Diary");
		Assert.Equal(["Diary"], top.Sections.Select(t => t.Title));

		top.OnPageShown(Diary.Key, null);
		Assert.Equal(-1, top.SelectedIndex);
		Assert.True(top.IsAccountLit);
		Assert.False(top.IsSettingsLit);
		Assert.Equal("Diary", top.AccountLitName);
		top.OnPageShown(Settings, null);
		Assert.Equal("Settings", top.AccountLitName);
		top.OnPageShown(TopTabs.HomeKey, null);
		Assert.False(top.IsAccountLit);
		Assert.Null(top.AccountLitName);

		// signed out and in again: the plugins add their sections again, and still once
		top.Reset();
		top.AddNavigationItem(Diary);
		Assert.Single(top.Sections);
	}

	[Fact]
	public void TheLitDestination_FollowsThePage_AndSurvivesTheLibrariesArriving()
	{
		var top = Create();
		var shows = Library("Shows", BaseItemDto_CollectionType.Tvshows);
		var anime = Library("Anime", BaseItemDto_CollectionType.Tvshows);

		top.OnPageShown(TopTabs.LibrariesKey, null);
		Assert.Equal("Libraries", top.SelectedTab?.Title);

		top.SetLibraries([Library("Movies", BaseItemDto_CollectionType.Movies), shows, anime]);
		Assert.Equal("Libraries", top.SelectedTab?.Title);

		top.OnPageShown(TopTabs.LibraryKey, shows);
		Assert.Equal("Shows", top.SelectedTab?.Title);
		// a library without a tab: "Libraries ▾" named after it
		top.OnPageShown(TopTabs.LibraryKey, anime);
		Assert.Equal((TopTabKind.Libraries, "Anime"), (top.SelectedTab?.Kind, top.SelectedTab?.Title));

		top.OnPageShown(Settings, null);
		Assert.Null(top.SelectedTab);
		Assert.True(top.IsSettingsLit);
		Assert.Equal("Libraries", top.Tabs[^1].Title);

		top.OnPageShown(Search, "quiet");
		Assert.True(top.IsSearchLit);
		top.OnPageShown(Movie, null);
		Assert.True(top.IsSearchLit);
	}

	[Fact]
	public void AnItemChosenInTheSearchBox_LightsTheSearchBox_Once()
	{
		var top = Create();
		top.SetLibraries([Library("Movies", BaseItemDto_CollectionType.Movies)]);
		top.OnPageShown(TopTabs.HomeKey, null);

		top.ChooseSearch();
		top.OnPageShown(Movie, null);
		Assert.True(top.IsSearchLit);

		top.OnPageShown(TopTabs.HomeKey, null);
		top.OnPageShown(Movie, null);
		Assert.Equal("Home", top.SelectedTab?.Title);

		var now = 1000L;
		top.Clock = () => now;
		top.ChooseSearch();
		now += 10_000;
		top.OnPageShown(Movie, null);
		Assert.Equal("Home", top.SelectedTab?.Title);
	}

	[Fact]
	public void Back_RestoresTheDestinationThePageHadWhenItWasShown()
	{
		var top = Create();
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		top.SetLibraries([movies, Library("Shows", BaseItemDto_CollectionType.Tvshows)]);

		top.OnPageShown(TopTabs.HomeKey, null, depth: 0);
		top.OnPageShown(Movie, null, depth: 1);
		Assert.Equal("Home", top.SelectedTab?.Title);
		top.OnPageShown(TopTabs.LibraryKey, movies, depth: 2);
		Assert.Equal("Movies", top.SelectedTab?.Title);

		top.OnPageShown(Movie, null, depth: 1, returning: true);
		Assert.Equal("Home", top.SelectedTab?.Title);

		top.OnPageShown(TopTabs.LibraryKey, movies, depth: 2, returning: true);
		Assert.Equal("Movies", top.SelectedTab?.Title);

		top.OnPageShown(Movie, null, depth: 1, returning: true);
		top.OnPageShown(Settings, null, depth: 2);
		Assert.True(top.IsSettingsLit);
		top.OnPageShown(Movie, null, depth: 1, returning: true);
		Assert.Equal("Home", top.SelectedTab?.Title);
	}

	[Fact]
	public void Folding_KeepsTheLitFavourite_OnLibrariesUnderItsName()
	{
		var top = Create();
		var libraries = Libraries(6);
		top.SetLibraries(libraries);
		foreach (var library in libraries.Take(3))
		{
			top.ToggleFavourite(library.Id!.Value);
		}

		top.OnPageShown(TopTabs.LibraryKey, libraries[2]);
		Assert.Equal((TopTabKind.Library, "Library 3"), (top.SelectedTab?.Kind, top.SelectedTab?.Title));

		top.Fold(1);

		Assert.Equal((TopTabKind.Libraries, "Library 3"), (top.SelectedTab?.Kind, top.SelectedTab?.Title));
		Assert.True(top.SelectedTab!.HasMenu);
		Assert.Equal(6, top.AllTabs.Count);

		top.OnPageShown(TopTabs.LibraryKey, libraries[0]);
		Assert.Equal("Library 1", top.SelectedTab?.Title);
		Assert.Equal("Libraries", top.Tabs.Single(t => t.Kind == TopTabKind.Libraries).Title);

		top.Fold(int.MaxValue);
		Assert.Equal(top.AllTabs.Select(t => t.Title), top.Tabs.Select(t => t.Title));
	}

	[Fact]
	public void ChoosingTheLitDestination_OnItsOwnPage_ReturnsToItsTop_ElsewhereOpensIt()
	{
		var top = Create();
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		top.SetLibraries([movies]);
		var returned = 0;
		top.ReturnToTopRequested += (_, _) => returned++;

		top.OnPageShown(TopTabs.LibraryKey, movies);
		Assert.True(top.Open(top.Tabs[1]));
		Assert.Equal(1, returned);
		_navigation.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);

		top.OnPageShown(Movie, null);
		top.Open(top.Tabs[1]);
		_navigation.Verify(x => x.NavigateTo(TopTabs.LibraryKey, movies, false));
		Assert.Equal(1, returned);

		top.OnPageShown(TopTabs.HomeKey, null);
		top.Open(top.Tabs[0]);
		Assert.Equal(2, returned);
	}

	[Fact]
	public async Task AnItemReachedFromAnotherSection_ShowsItsLibraryAsAnEyebrow_NotFromItsOwn()
	{
		var top = Create();
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		var anime = Library("Anime", BaseItemDto_CollectionType.Tvshows);
		top.SetLibraries([movies, Library("Shows", BaseItemDto_CollectionType.Tvshows), anime]);
		var film = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper", Type = BaseItemDto_Type.Movie };
		var series = new BaseItemDto { Id = Guid.NewGuid(), Name = "Lantern", Type = BaseItemDto_Type.Series };
		top.LibraryOf = item => Task.FromResult<Guid?>(item.Id == film.Id ? movies.Id : item.Id == series.Id ? anime.Id : null);

		top.OnPageShown(TopTabs.HomeKey, null);
		top.OnPageShown(Movie, film);
		await top.EyebrowLookup;
		Assert.Equal("Movies", top.Eyebrow?.Title);

		// a library that is not a favourite names itself too
		top.OnPageShown(typeof(SeriesViewModel).FullName!, series);
		await top.EyebrowLookup;
		Assert.Equal("Anime", top.Eyebrow?.Title);

		top.OnPageShown(TopTabs.LibraryKey, movies);
		Assert.Null(top.Eyebrow);
		top.OnPageShown(Movie, film);
		await top.EyebrowLookup;
		Assert.Null(top.Eyebrow);

		top.OnPageShown(Search, "copper");
		top.OnPageShown(Movie, film);
		await top.EyebrowLookup;
		Assert.Equal("Movies", top.Eyebrow?.Title);
		top.OnPageShown(Settings, null);
		Assert.Null(top.Eyebrow);
	}

	[Fact]
	public async Task ALateEyebrow_ForAPageLeft_IsNotShown()
	{
		var top = Create();
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		top.SetLibraries([movies]);
		var slow = new TaskCompletionSource<Guid?>();
		top.LibraryOf = _ => slow.Task;

		top.OnPageShown(Movie, new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie });
		var first = top.EyebrowLookup;
		top.OnPageShown(TopTabs.HomeKey, null);
		slow.SetResult(movies.Id);
		await first;

		Assert.Null(top.Eyebrow);
	}

	[Fact]
	public void ANewLibrary_AddedAtRuntime_IsInTheMenu_NotTheBar()
	{
		var top = Create();
		top.SetLibraries(Libraries(4));

		top.SetLibraries([.. Libraries(4), Library("Concerts")]);

		Assert.DoesNotContain(top.Tabs, t => t.Title == "Concerts");
		Assert.Contains(top.Tabs.Single(t => t.Kind == TopTabKind.Libraries).Menu, t => t.Title == "Concerts");
	}

	[Fact]
	public void OpeningATab_NavigatesToItsPage()
	{
		var top = Create();
		var movies = Library("Movies", BaseItemDto_CollectionType.Movies);
		top.SetLibraries([movies]);

		top.Open(top.Tabs[1]);

		_navigation.Verify(x => x.NavigateTo(TopTabs.LibraryKey, movies, false));
	}

	[Fact]
	public void BeforeSignIn_NothingIsReadOrWritten()
	{
		_profile = null;
		var top = Create();
		var libraries = Libraries(3);
		top.SetLibraries(libraries);
		top.ToggleFavourite(libraries[0].Id!.Value);
		top.OnPageShown(TopTabs.LibraryKey, libraries[1]);

		Assert.Equal(0, _settings.Saves);
	}

	/// <summary>The settings file in memory (as LocalSettingsService keeps it: JSON nodes by key).</summary>
	private sealed class MemorySettings : ILocalSettingsService
	{
		public Dictionary<string, JsonNode?> Values { get; } = [];

		public int Saves { get; private set; }

		public JsonNode? ReadSettingRaw(string key) => Values.GetValueOrDefault(key);

		public T ReadSetting<T>(string key, T defaultValue) => Values.TryGetValue(key, out var v) && v is not null ? v.Deserialize<T>()! : defaultValue;

		public void SaveSetting<T>(string key, T value)
		{
			Values[key] = JsonNode.Parse(JsonSerializer.Serialize(value));
			Saves++;
		}

		public void RemoveSetting(string key) => Values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
