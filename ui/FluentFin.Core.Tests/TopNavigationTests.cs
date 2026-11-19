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
/// 1.2.0 (the top bar, replacing the left rail): Home, the libraries, then the plugins' sections (Diary); which destination a
/// page lights; the sections the rail held. Nixie 1.4.0: every view is a tab (no fold by count, no pins), folded by width into
/// "Libraries ▾", and one destination lit by the trail rule.
/// </summary>
public sealed class TopNavigationTests
{
	private static BaseItemDto Library(string name, BaseItemDto_CollectionType? type = null) =>
		new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder, CollectionType = type };

	private static readonly CustomNavigationViewItem Diary = new() { Key = typeof(DiaryViewModel).FullName!, Name = "Diary" };

	private static readonly string Movie = typeof(MovieViewModel).FullName!;
	private static readonly string Settings = typeof(SettingsViewModel).FullName!;
	private static readonly string Search = typeof(SearchResultsViewModel).FullName!;

	private static List<BaseItemDto> Libraries(int count) => Enumerable.Range(1, count).Select(i => Library($"Library {i}")).ToList();

	// ---- the tabs

	[Theory]
	[InlineData(1)]
	[InlineData(4)]
	[InlineData(5)]
	[InlineData(9)]
	public void EveryView_IsATab_AtAnyCount_ThenLibraries_ThenDiary(int count)
	{
		// the review's X6: five or more views folded into one tab at any width (MaxLibraryTabs = 4)
		var libraries = Libraries(count);

		var tabs = TopTabs.Build(libraries, [Diary]);

		Assert.Equal(["Home", .. libraries.Select(l => l.Name!), "Libraries", "Diary"], tabs.Select(t => t.Title));
		Assert.Equal(TopTabKind.Home, tabs[0].Kind);
		Assert.All(tabs.Skip(1).Take(count), t => Assert.Equal((TopTabKind.Library, TopTabs.LibraryKey), (t.Kind, t.Key)));
		// with nothing folded, "Libraries" is a plain tab to the Libraries page: All libraries at any count
		var all = tabs[count + 1];
		Assert.Equal((TopTabKind.Libraries, TopTabs.LibrariesKey), (all.Kind, all.Key));
		Assert.False(all.HasMenu);
	}

	[Fact]
	public void CollectionsAndPlaylists_GoLast_TheRestInTheServersOrder()
	{
		var tabs = TopTabs.Build(
			[Library("Collections", BaseItemDto_CollectionType.Boxsets), Library("Movies", BaseItemDto_CollectionType.Movies),
				Library("Playlists", BaseItemDto_CollectionType.Playlists), Library("Shows", BaseItemDto_CollectionType.Tvshows),
				Library("Music", BaseItemDto_CollectionType.Music)], [Diary]);

		Assert.Equal(["Home", "Movies", "Shows", "Music", "Collections", "Playlists", "Libraries", "Diary"], tabs.Select(t => t.Title));
	}

	[Fact]
	public void NoLibrariesListed_IsStillALibrariesTab_ThatOpensItsPage()
	{
		// the list failed or is empty: the Libraries page says so (its own load state)
		var tab = Assert.Single(TopTabs.Build([], []), t => t.Kind == TopTabKind.Libraries);

		Assert.False(tab.HasMenu);
		Assert.Equal(TopTabs.LibrariesKey, tab.Key);
	}

	[Fact]
	public void ALibraryPin_IsNoTabOfItsOwn_EveryLibraryIsOne()
	{
		var libraries = Libraries(6);
		var pin = new CustomNavigationViewItem { Key = TopTabs.LibraryKey, Name = "Library 3", Parameter = libraries[2].Id, Persistent = true };

		Assert.Single(TopTabs.Build(libraries, [pin, Diary]), t => t.Title == "Library 3");
	}

	// ---- the fold by width

	[Fact]
	public void TheFold_MovesTheLastLibraries_IntoLibrariesMenu_AfterAllLibraries()
	{
		var tabs = TopTabs.Build(Libraries(6), [Diary]);

		var row = TopTabs.Fold(tabs, 4, lit: null);

		Assert.Equal(["Home", "Library 1", "Library 2", "Library 3", "Library 4", "Libraries", "Diary"], row.Select(t => t.Title));
		var folded = row[5];
		Assert.True(folded.HasMenu);
		Assert.Equal([TopTabs.AllLibraries, "Library 5", "Library 6"], folded.Menu.Select(t => t.Title));
		Assert.Equal(TopTabs.LibrariesKey, folded.Menu[0].Key);
		// every library folded: Home, "Libraries ▾", Diary
		Assert.Equal(["Home", "Libraries", "Diary"], TopTabs.Fold(tabs, 0, null).Select(t => t.Title));
		// room for all: nothing changes
		Assert.Equal(tabs.Select(t => t.Title), TopTabs.Fold(tabs, 6, null).Select(t => t.Title));
	}

	[Fact]
	public void TheFoldedTab_IsNamedAfterTheLitLibrary_AndMarksIt()
	{
		var libraries = Libraries(6);
		var tabs = TopTabs.Build(libraries, [Diary]);
		var lit = new TopMark(TopTabs.LibraryKey, libraries[5].Id);

		var row = TopTabs.Fold(tabs, 3, lit);

		var folded = row.Single(t => t.Kind == TopTabKind.Libraries);
		Assert.Equal("Library 6", folded.Title);
		Assert.Equal(row.ToList().IndexOf(folded), TopTabs.IndexOf(row, lit));
		// a lit library still in the row lights its own tab; the folded tab says "Libraries"
		var shown = new TopMark(TopTabs.LibraryKey, libraries[1].Id);
		var row2 = TopTabs.Fold(tabs, 3, shown);
		Assert.Equal("Libraries", row2.Single(t => t.Kind == TopTabKind.Libraries).Title);
		Assert.Equal(2, TopTabs.IndexOf(row2, shown));
	}

	[Fact]
	public void TheMenus_HaveASeparator_AfterAllLibraries_AndTheNarrowMenuIsFlat()
	{
		var tabs = TopTabs.Build(Libraries(3), [Diary]);
		var folded = TopTabs.Fold(tabs, 1, null).Single(t => t.Kind == TopTabKind.Libraries);

		Assert.Equal([TopTabs.AllLibraries, null, "Library 2", "Library 3"], TopMenus.Libraries(folded).Select(t => t?.Title));
		Assert.Equal(["Home", "Library 1", "Library 2", "Library 3", null, TopTabs.AllLibraries, "Diary"], TopMenus.Sections(tabs).Select(t => t?.Title));
	}

	// ---- the trail rule: which destination a page lights

	[Fact]
	public void ADestinationChosen_LightsItself()
	{
		var movies = Library("Movies");
		var shows = Library("Shows");
		var tabs = TopTabs.Build([movies, shows], [Diary]);
		var home = new TopMark(TopTabs.HomeKey);

		Assert.Equal(home, TopTabs.MarkFor(tabs, TopTabs.HomeKey, null, null));
		Assert.Equal(new TopMark(TopTabs.LibraryKey, shows.Id), TopTabs.MarkFor(tabs, TopTabs.LibraryKey, shows, home));
		Assert.Equal(new TopMark(TopTabs.LibraryKey, movies.Id), TopTabs.MarkFor(tabs, TopTabs.LibraryKey, movies.Id!.Value, home));
		Assert.Equal(new TopMark(TopTabs.LibrariesKey), TopTabs.MarkFor(tabs, TopTabs.LibrariesKey, null, home));
		Assert.Equal(new TopMark(Diary.Key), TopTabs.MarkFor(tabs, Diary.Key, null, home));
		// the review's X7: Search and Settings lit nothing
		Assert.Equal(TopMark.Search, TopTabs.MarkFor(tabs, Search, "quiet", home));
		Assert.Equal(TopMark.Settings, TopTabs.MarkFor(tabs, Settings, null, home));
	}

	[Fact]
	public void APageDrilledInto_KeepsTheLitDestination()
	{
		var tabs = TopTabs.Build([Library("Movies"), Library("Shows")], [Diary]);
		var shows = new TopMark(TopTabs.LibraryKey, tabs[2].LibraryId);

		// a movie opened from Shows (or the player, after it) is still inside Shows
		Assert.Equal(shows, TopTabs.MarkFor(tabs, Movie, null, shows));
		Assert.Equal(shows, TopTabs.MarkFor(tabs, "FluentFin.ViewModels.VideoPlayerViewModel", null, shows));
		// an item from the search results keeps the search box lit; the server's administration keeps Settings
		Assert.Equal(TopMark.Search, TopTabs.MarkFor(tabs, Movie, null, TopMark.Search));
		Assert.Equal(TopMark.Settings, TopTabs.MarkFor(tabs, "FluentFin.Views.JellyfinSettingsViewModel", null, TopMark.Settings));
		Assert.Equal(TopMark.Settings, TopTabs.MarkFor(tabs, typeof(DashboardViewModel).FullName!, null, TopMark.Settings));
		// a collection opens on the library page: drilled into, not a library of its own
		var boxSet = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper and Glass", Type = BaseItemDto_Type.BoxSet };
		Assert.Equal(shows, TopTabs.MarkFor(tabs, TopTabs.LibraryKey, boxSet, shows));
		// an item chosen in the search box's list lights the search box
		Assert.Equal(TopMark.Search, TopTabs.MarkFor(tabs, Movie, null, shows, fromSearch: true));
	}

	// ---- the service

	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly Mock<ILocalSettingsService> _settings = new();

	private TopNavigation Create()
	{
		var top = new TopNavigation(_navigation.Object, _settings.Object, NullLogger<TopNavigation>.Instance);
		top.Reset();
		return top;
	}

	[Fact]
	public void ASection_AddedTwice_IsOneTab_AndANewShellStartsAfresh()
	{
		var top = Create();
		top.AddNavigationItem(Diary);
		top.AddNavigationItem(Diary with { });

		Assert.Single(top.Tabs, t => t.Title == "Diary");

		// signed out and in again: the plugins add their sections again, and still once
		top.Reset();
		top.AddNavigationItem(Diary);
		Assert.Single(top.Tabs, t => t.Title == "Diary");
	}

	[Fact]
	public void ThePins_AreGone_NeitherReadNorSaved()
	{
		var top = Create();
		var libraries = Libraries(6);
		top.SetLibraries(libraries);

		top.AddNavigationItem(new CustomNavigationViewItem { Key = TopTabs.LibraryKey, Name = "Library 3", Parameter = libraries[2].Id, Persistent = true });
		top.SaveCustomViews();

		Assert.Single(top.AllTabs, t => t.Title == "Library 3");
		_settings.Verify(x => x.SaveSetting(It.IsAny<string>(), It.IsAny<List<CustomNavigationViewItem>>()), Times.Never);
		_settings.Verify(x => x.ReadSetting(It.IsAny<string>(), It.IsAny<List<CustomNavigationViewItem>>()), Times.Never);
	}

	[Fact]
	public void TheLitDestination_FollowsThePage_AndSurvivesTheLibrariesArriving()
	{
		var top = Create();
		top.AddNavigationItem(Diary);
		var shows = Library("Shows");

		top.OnPageShown(Diary.Key, null);
		Assert.Equal("Diary", top.SelectedTab?.Title);

		// the libraries come after the shell: Diary moves along, still marked
		top.SetLibraries([Library("Movies"), shows]);
		Assert.Equal("Diary", top.SelectedTab?.Title);

		top.OnPageShown(TopTabs.LibraryKey, shows);
		Assert.Equal("Shows", top.SelectedTab?.Title);

		top.OnPageShown(Settings, null);
		Assert.Null(top.SelectedTab);
		Assert.Equal(-1, top.SelectedIndex);
		Assert.True(top.IsSettingsLit);
		Assert.False(top.IsSearchLit);

		top.OnPageShown(Search, "quiet");
		Assert.True(top.IsSearchLit);
		top.OnPageShown(Movie, null);
		Assert.True(top.IsSearchLit);
	}

	[Fact]
	public void AnItemChosenInTheSearchBox_LightsTheSearchBox_Once()
	{
		var top = Create();
		top.SetLibraries([Library("Movies")]);
		top.OnPageShown(TopTabs.HomeKey, null);

		top.ChooseSearch();
		top.OnPageShown(Movie, null);
		Assert.True(top.IsSearchLit);

		top.OnPageShown(TopTabs.HomeKey, null);
		top.OnPageShown(Movie, null);
		Assert.Equal("Home", top.SelectedTab?.Title);

		// a choice that opened nothing does not light the search box on a page opened later from Home
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
		// review: Home, an item, the Movies tab, Back: the item page kept Movies marked
		var top = Create();
		var movies = Library("Movies");
		top.SetLibraries([movies, Library("Shows")]);

		top.OnPageShown(TopTabs.HomeKey, null, depth: 0);
		top.OnPageShown(Movie, null, depth: 1);
		Assert.Equal("Home", top.SelectedTab?.Title);
		top.OnPageShown(TopTabs.LibraryKey, movies, depth: 2);
		Assert.Equal("Movies", top.SelectedTab?.Title);

		top.OnPageShown(Movie, null, depth: 1, returning: true);
		Assert.Equal("Home", top.SelectedTab?.Title);

		// Forward again: Movies
		top.OnPageShown(TopTabs.LibraryKey, movies, depth: 2, returning: true);
		Assert.Equal("Movies", top.SelectedTab?.Title);

		// a new page from the item drops what was forward of it
		top.OnPageShown(Movie, null, depth: 1, returning: true);
		top.OnPageShown(Settings, null, depth: 2);
		Assert.True(top.IsSettingsLit);
		top.OnPageShown(Movie, null, depth: 1, returning: true);
		Assert.Equal("Home", top.SelectedTab?.Title);
	}

	[Fact]
	public void Folding_KeepsTheLitLibrary_OnTheFoldedTab_UnderItsName()
	{
		var top = Create();
		var libraries = Libraries(6);
		top.SetLibraries(libraries);
		top.OnPageShown(TopTabs.LibraryKey, libraries[5]);
		Assert.Equal("Library 6", top.SelectedTab?.Title);
		Assert.Equal(TopTabKind.Library, top.SelectedTab?.Kind);

		top.Fold(2);

		Assert.Equal(TopTabKind.Libraries, top.SelectedTab?.Kind);
		Assert.Equal("Library 6", top.SelectedTab?.Title);
		Assert.True(top.SelectedTab!.HasMenu);
		Assert.Equal(8, top.AllTabs.Count);

		// another library, still in the row: its own tab, and the folded tab is "Libraries" again
		top.OnPageShown(TopTabs.LibraryKey, libraries[0]);
		Assert.Equal("Library 1", top.SelectedTab?.Title);
		Assert.Equal("Libraries", top.Tabs.Single(t => t.Kind == TopTabKind.Libraries).Title);

		// room again: every library a tab
		top.Fold(int.MaxValue);
		Assert.Equal(top.AllTabs.Select(t => t.Title), top.Tabs.Select(t => t.Title));
	}

	[Fact]
	public void ChoosingTheLitDestination_OnItsOwnPage_ReturnsToItsTop_ElsewhereOpensIt()
	{
		var top = Create();
		var movies = Library("Movies");
		top.SetLibraries([movies]);
		var returned = 0;
		top.ReturnToTopRequested += (_, _) => returned++;

		top.OnPageShown(TopTabs.LibraryKey, movies);
		Assert.True(top.Open(top.Tabs[1]));
		Assert.Equal(1, returned);
		_navigation.Verify(x => x.NavigateTo(It.IsAny<string>(), It.IsAny<object?>(), It.IsAny<bool>()), Times.Never);

		// from a film drilled into from Movies: Movies opens (its top)
		top.OnPageShown(Movie, null);
		top.Open(top.Tabs[1]);
		_navigation.Verify(x => x.NavigateTo(TopTabs.LibraryKey, movies, false));
		Assert.Equal(1, returned);

		// Home on Home
		top.OnPageShown(TopTabs.HomeKey, null);
		top.Open(top.Tabs[0]);
		Assert.Equal(2, returned);
	}

	[Fact]
	public async Task AnItemReachedFromAnotherSection_ShowsItsLibraryAsAnEyebrow_NotFromItsOwn()
	{
		var top = Create();
		var movies = Library("Movies");
		top.SetLibraries([movies, Library("Shows")]);
		var film = new BaseItemDto { Id = Guid.NewGuid(), Name = "Copper", Type = BaseItemDto_Type.Movie };
		top.LibraryOf = item => Task.FromResult<Guid?>(item.Id == film.Id ? movies.Id : null);

		// from Home: "Movies ›"
		top.OnPageShown(TopTabs.HomeKey, null);
		top.OnPageShown(Movie, film);
		await top.EyebrowLookup;
		Assert.Equal("Movies", top.Eyebrow?.Title);

		// from Movies: the bar says it already
		top.OnPageShown(TopTabs.LibraryKey, movies);
		Assert.Null(top.Eyebrow);
		top.OnPageShown(Movie, film);
		await top.EyebrowLookup;
		Assert.Null(top.Eyebrow);

		// from the search results: "Movies ›"; any other page: none
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
		var movies = Library("Movies");
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
	public void ANewLibrary_AddedAtRuntime_IsATab()
	{
		var top = Create();
		top.SetLibraries(Libraries(4));

		top.SetLibraries([.. Libraries(4), Library("Concerts")]);

		Assert.Contains(top.Tabs, t => t.Title == "Concerts" && t.Kind == TopTabKind.Library);
	}

	[Fact]
	public void OpeningATab_NavigatesToItsPage()
	{
		var top = Create();
		var movies = Library("Movies");
		top.SetLibraries([movies]);

		top.Open(top.Tabs[1]);

		_navigation.Verify(x => x.NavigateTo(TopTabs.LibraryKey, movies, false));
	}
}
