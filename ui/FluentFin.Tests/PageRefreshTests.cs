using System.Collections.ObjectModel;
using FluentFin.Core.Journal;
using FluentFin.Core.Shortcuts;
using FluentFin.Core.ViewModels;
using FluentFin.Helpers;
using FluentFin.Services;
using FluentFin.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.1 (the user: "make r refresh the jellyfin page"): R, F5 and Ctrl+R load the page again from the server, in
/// place. The key routing (never in a dialog or menu, the player, signed out, or over a trailer), the pages it applies to,
/// and the place kept across the new view model.
/// </summary>
public sealed class PageRefreshTests
{
	private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts]));

	[Fact]
	public void Refresh_RunsWhileBrowsing_NotInADialogOrMenu_NotInThePlayer_NotSignedOut_NotOverATrailer()
	{
		Assert.Equal(WindowShortcuts.ShortcutGate.Run, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Browsing, popupOpen: false, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Browsing, popupOpen: true, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Player, popupOpen: false, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.SignedOut, popupOpen: false, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Browsing, popupOpen: false, trailerOpen: true));
	}

	[Fact]
	public void OverATrailer_BackClosesIt_AndNothingElseRuns()
	{
		Assert.Equal(WindowShortcuts.ShortcutGate.CloseTrailer, WindowShortcuts.Gate(AppShortcut.Back, ShortcutContext.Browsing, popupOpen: false, trailerOpen: true));
		foreach (var other in Enum.GetValues<AppShortcut>().Where(s => s != AppShortcut.Back))
		{
			Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(other, ShortcutContext.Browsing, popupOpen: false, trailerOpen: true));
		}
	}

	[Theory]
	[InlineData(typeof(HomeViewModel), true)]
	[InlineData(typeof(LibraryViewModel), true)]
	[InlineData(typeof(ItemGridViewModel), true)]
	[InlineData(typeof(MovieViewModel), true)]
	[InlineData(typeof(EpisodeViewModel), true)]
	[InlineData(typeof(SeriesViewModel), true)]
	[InlineData(typeof(SeasonViewModel), true)]
	[InlineData(typeof(SearchResultsViewModel), true)]
	[InlineData(typeof(DiaryViewModel), true)]
	[InlineData(typeof(CollectionViewModel), true)]
	[InlineData(typeof(LibrariesLandingPageViewModel), true)]
	[InlineData(typeof(SettingsViewModel), false)]
	[InlineData(typeof(VideoPlayerViewModel), false)]
	[InlineData(null, false)]
	public void OnlyThePagesWithServerData_Refresh(Type? viewModel, bool refreshes) =>
		Assert.Equal(refreshes, PageRefresh.Refreshes(viewModel));

	private static BaseItemDto Item(Guid id) => new() { Id = id, Name = id.ToString("N")[..6] };

	[Fact]
	public void TheAnchor_FindsItsRow_InTheRefreshedPagesNewLists()
	{
		var anchor = Guid.NewGuid();
		var continueRow = new ObservableCollection<BaseItemDto> { Item(Guid.NewGuid()), Item(anchor) };
		var nextUp = new ObservableCollection<BaseItemDto> { Item(Guid.NewGuid()) };

		// the same list (the view model kept)
		Assert.Equal(1, ThemeAnchors.RowFor([nextUp, continueRow], continueRow, 0, anchor));

		// a refresh: new lists; the row at the same place still has the item
		var freshContinue = new ObservableCollection<BaseItemDto> { Item(Guid.NewGuid()), Item(anchor) };
		var freshNextUp = new ObservableCollection<BaseItemDto> { Item(Guid.NewGuid()) };
		Assert.Equal(0, ThemeAnchors.RowFor([freshContinue, freshNextUp], continueRow, 0, anchor));

		// a row came before it: the list that has the item
		var added = new ObservableCollection<BaseItemDto> { Item(Guid.NewGuid()) };
		Assert.Equal(1, ThemeAnchors.RowFor([added, freshContinue, freshNextUp], continueRow, 0, anchor));

		// the item is gone from the server: no row (the keyboard goes to the page's Primary)
		Assert.Equal(-1, ThemeAnchors.RowFor([freshNextUp, added], continueRow, 0, anchor));
	}

	[Fact]
	public void TheRefresh_IsTheSamePageAgain_InPlaceInTheHistory_WithANewLoad()
	{
		var nav = Source("Services", "NavigationService.cs");
		Assert.Contains("frame.Navigate(page.GetType(), _currentParameter, new SuppressNavigationTransitionInfo())", nav);
		Assert.Contains("frame.ForwardStack.Add(entry);", nav);
		// the refresh's Navigated drops the old copy from Back, plays no transition, and still loads (OnNavigatedTo), with no
		// view model handed over (a new one asks the server)
		var refresh = nav.IndexOf("var refresh = _pendingRefresh is { } refreshed", StringComparison.Ordinal);
		Assert.True(refresh > 0 && refresh < nav.IndexOf("await navigationAware.OnNavigatedTo(e.Parameter);", StringComparison.Ordinal));
		var reload = nav[nav.IndexOf("public Page? ReloadForRefresh", StringComparison.Ordinal)..nav.IndexOf("/// <summary>The page the active theme shows", StringComparison.Ordinal)];
		Assert.DoesNotContain("ViewModelHandoff", reload);

		var shell = Source("Views", "ShellPage.xaml.cs");
		Assert.True(shell.IndexOf("LastNavigationWasRefresh: true", StringComparison.Ordinal) < shell.IndexOf("_titleBar.OnPageShown(e.SourcePageType, e.Parameter);", StringComparison.Ordinal));

		var run = Source("Services", "PageRefresh.cs");
		Assert.Contains("ThemeAnchors.Capture(shown)", run);
		Assert.Contains("anchors.RestoreAfterRefresh(fresh, RestoreWithin,", run);
		Assert.Contains("cache.MarkStale()", run);
		Assert.Equal("Refreshed", PageRefresh.Announcement);
		Assert.Contains("case AppShortcut.Refresh:\n\t\t\t\t\treturn PageRefresh.Run();", Source("Services", "WindowShortcuts.cs").Replace("\r\n", "\n"));
	}
}
