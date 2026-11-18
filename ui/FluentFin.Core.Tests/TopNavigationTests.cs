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
/// 1.2.0 (the top bar, replacing the left rail): Home, the libraries as tabs (up to four) or one "Libraries" tab with a
/// menu, then the plugins' sections (Diary); which tab a page marks; the pins and sections the rail held.
/// </summary>
public sealed class TopNavigationTests
{
	private static BaseItemDto Library(string name) => new() { Id = Guid.NewGuid(), Name = name, Type = BaseItemDto_Type.CollectionFolder };

	private static readonly CustomNavigationViewItem Diary = new() { Key = typeof(DiaryViewModel).FullName!, Name = "Diary" };

	private static readonly string Movie = typeof(MovieViewModel).FullName!;
	private static readonly string Settings = typeof(SettingsViewModel).FullName!;

	// ---- the tabs

	[Fact]
	public void FewLibraries_AreTabsOfTheirOwn_BetweenHomeAndDiary()
	{
		var tabs = TopTabs.Build([Library("Movies"), Library("Shows"), Library("Documentaries"), Library("Anime")], [Diary]);

		Assert.Equal(["Home", "Movies", "Shows", "Documentaries", "Anime", "Diary"], tabs.Select(t => t.Title));
		Assert.Equal(TopTabKind.Home, tabs[0].Kind);
		Assert.All(tabs.Skip(1).Take(4), t => Assert.Equal((TopTabKind.Library, TopTabs.LibraryKey), (t.Kind, t.Key)));
		Assert.DoesNotContain(tabs, t => t.Kind == TopTabKind.Libraries);
	}

	[Fact]
	public void ManyLibraries_AreOneLibrariesTab_WithAMenu()
	{
		var libraries = Enumerable.Range(1, 5).Select(i => Library($"Library {i}")).ToList();

		var tabs = TopTabs.Build(libraries, [Diary]);

		Assert.Equal(["Home", "Libraries", "Diary"], tabs.Select(t => t.Title));
		var menu = tabs[1];
		Assert.True(menu.HasMenu);
		Assert.Equal(["All libraries", "Library 1", "Library 2", "Library 3", "Library 4", "Library 5"], menu.Menu.Select(t => t.Title));
		Assert.Equal(TopTabs.LibrariesKey, menu.Menu[0].Key);
		Assert.Same(libraries[2], menu.Menu[3].Parameter);
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
	public void APinnedLibrary_IsATab_OnlyWhenTheLibrariesAreInTheMenu()
	{
		var libraries = Enumerable.Range(1, 6).Select(i => Library($"Library {i}")).ToList();
		var pin = new CustomNavigationViewItem { Key = TopTabs.LibraryKey, Name = "Library 3", Parameter = libraries[2].Id, Persistent = true };

		Assert.Equal(["Home", "Library 3", "Libraries", "Diary"], TopTabs.Build(libraries, [pin, Diary]).Select(t => t.Title));
		Assert.Equal(["Home", "Library 1", "Library 2", "Diary"], TopTabs.Build(libraries.Take(2).ToList(), [pin, Diary]).Select(t => t.Title));
	}

	// ---- which tab a page marks

	[Fact]
	public void APage_MarksItsTab()
	{
		var movies = Library("Movies");
		var shows = Library("Shows");
		var tabs = TopTabs.Build([movies, shows], [Diary]);

		Assert.Equal(0, TopTabs.SelectionFor(tabs, TopTabs.HomeKey, null, 3));
		Assert.Equal(2, TopTabs.SelectionFor(tabs, TopTabs.LibraryKey, shows, 0));
		Assert.Equal(1, TopTabs.SelectionFor(tabs, TopTabs.LibraryKey, movies.Id!.Value, 0));
		Assert.Equal(3, TopTabs.SelectionFor(tabs, Diary.Key, null, 0));
	}

	[Fact]
	public void APageInsideASection_KeepsItsTab_AndSettingsOrSearchMarkNone()
	{
		var tabs = TopTabs.Build([Library("Movies"), Library("Shows")], [Diary]);

		// a movie opened from Shows (or the player, after it) is still inside Shows
		Assert.Equal(2, TopTabs.SelectionFor(tabs, Movie, null, 2));
		Assert.Equal(2, TopTabs.SelectionFor(tabs, "FluentFin.ViewModels.VideoPlayerViewModel", null, 2));
		Assert.Equal(-1, TopTabs.SelectionFor(tabs, Settings, null, 2));
		Assert.Equal(-1, TopTabs.SelectionFor(tabs, typeof(SearchResultsViewModel).FullName!, "quiet", 2));
		Assert.Equal(-1, TopTabs.SelectionFor(tabs, "FluentFin.Views.JellyfinSettingsViewModel", null, 2));
		// a movie opened from Settings' search marks nothing either
		Assert.Equal(-1, TopTabs.SelectionFor(tabs, Movie, null, -1));
	}

	[Fact]
	public void ALibrary_MarksLibraries_WhenTheLibrariesAreInTheMenu()
	{
		var libraries = Enumerable.Range(1, 5).Select(i => Library($"Library {i}")).ToList();
		var tabs = TopTabs.Build(libraries, [Diary]);

		Assert.Equal(1, TopTabs.SelectionFor(tabs, TopTabs.LibraryKey, libraries[4], 0));
		Assert.Equal(1, TopTabs.SelectionFor(tabs, TopTabs.LibrariesKey, null, 0));
	}

	// ---- the service

	private readonly Mock<INavigationServiceCore> _navigation = new();
	private readonly Mock<ILocalSettingsService> _settings = new();

	private TopNavigation Create(List<CustomNavigationViewItem>? saved = null)
	{
		_settings.Setup(x => x.ReadSetting(TopNavigation.PinsSetting, It.IsAny<List<CustomNavigationViewItem>>())).Returns(saved ?? []);
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
	public void ThePins_AreReadBack_AndSavedWithoutThePluginsSections()
	{
		var pin = new CustomNavigationViewItem { Key = TopTabs.LibraryKey, Name = "Library 3", Parameter = Guid.NewGuid(), Persistent = true };
		var top = Create([pin]);
		top.AddNavigationItem(Diary);

		top.SaveCustomViews();

		_settings.Verify(x => x.SaveSetting(TopNavigation.PinsSetting, It.Is<List<CustomNavigationViewItem>>(l => l.Single() == pin)));

		top.RemoveNavigationItem(pin);
		_settings.Verify(x => x.SaveSetting(TopNavigation.PinsSetting, It.Is<List<CustomNavigationViewItem>>(l => l.Count == 0)));
	}

	[Fact]
	public void TheMarkedTab_FollowsThePage_AndSurvivesTheLibrariesArriving()
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
	}

	[Fact]
	public void Back_RestoresTheTabThePageHadWhenItWasShown()
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
		top.OnPageShown(Movie, null, depth: 1, returning: true);
		Assert.Equal("Home", top.SelectedTab?.Title);
	}

	[Fact]
	public void APinnedTab_CarriesItsPin_AndUnpinRemovesIt()
	{
		var libraries = Enumerable.Range(1, 6).Select(i => Library($"Library {i}")).ToList();
		var pin = new CustomNavigationViewItem { Key = TopTabs.LibraryKey, Name = "Library 3", Parameter = libraries[2].Id, Persistent = true };
		var top = Create([pin]);
		top.SetLibraries(libraries);

		var pinned = Assert.Single(top.Tabs, t => t.Pin is not null);
		Assert.Equal("Library 3", pinned.Title);
		Assert.Null(top.Tabs.Single(t => t.Title == "Home").Pin);

		top.RemoveNavigationItem(pinned.Pin!);

		Assert.DoesNotContain(top.Tabs, t => t.Pin is not null);
		Assert.Equal(["Home", "Libraries"], top.Tabs.Select(t => t.Title));
	}

	[Fact]
	public void AFifthLibrary_AddedAtRuntime_IsInTheLibrariesMenu()
	{
		var top = Create();
		var four = Enumerable.Range(1, 4).Select(i => Library($"Library {i}")).ToList();
		top.SetLibraries(four);
		Assert.DoesNotContain(top.Tabs, t => t.Kind == TopTabKind.Libraries);

		top.SetLibraries([.. four, Library("Concerts")]);

		var menu = Assert.Single(top.Tabs, t => t.Kind == TopTabKind.Libraries);
		Assert.Contains(menu.Menu, t => t.Title == "Concerts");
	}

	[Fact]
	public void AnUnreadablePinsSetting_CostsThePins_NotTheTabs()
	{
		// seen: a pins setting written in the wrong shape threw out of the shell, which then had no Diary and no libraries
		_settings.Setup(x => x.ReadSetting(TopNavigation.PinsSetting, It.IsAny<List<CustomNavigationViewItem>>())).Throws(new System.Text.Json.JsonException("not a list"));
		var top = new TopNavigation(_navigation.Object, _settings.Object, NullLogger<TopNavigation>.Instance);

		top.Reset();
		top.AddNavigationItem(Diary);
		top.SetLibraries([Library("Movies")]);

		Assert.Equal(["Home", "Movies", "Diary"], top.Tabs.Select(t => t.Title));
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
