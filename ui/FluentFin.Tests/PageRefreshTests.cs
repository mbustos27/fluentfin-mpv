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

	/// <summary>
	/// Nixie 1.4.0 (integration: the hunt's M3 against the library's M2, seen in then-refresh on Drive-in): after R on a slow
	/// server, a user who moved first (Ctrl+F and a letter, or Down) keeps the keyboard; the library's own place restore
	/// yields as the refresh's does, and still scrolls back.
	/// </summary>
	[Fact]
	public void TheLibrarysPlaceAfterR_YieldsTheKeyboardToAUserWhoMovedFirst()
	{
		var host = Source("Controls", "LibraryGridHost.cs");
		var editorial = Source("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs");
		// Nixie 1.4.0 (bug hunt m4): the rule is the app's one (ArrivalFocus.LibraryYields, through LibraryViewModel.FocusYields);
		// Editorial's page still sets its own as well (asked too)
		Assert.Contains("{ LastNavigationWasRefresh: true } && PageRefresh.UserMovedSinceRefresh", editorial);
		Assert.Equal(2, host.Split("_vm.FocusYields?.Invoke() != true").Length - 1);      // the card at once, and after the scroll back
		Assert.Contains("index >= 0 && ViewModel.FocusYields?.Invoke() != true", editorial);
		Assert.Contains("public static bool UserMovedSinceRefresh => _userMoved is not null;", Source("Services", "PageRefresh.cs"));
		// after R, the poster put back yields as R's restore does: a key, a click or the wheel since R
		Assert.Contains("kind == ArrivalKind.Refreshed ? (movedSinceRefresh ?", Source("Services", "ArrivalFocus.cs"));
		Assert.Equal("a key, a click or the wheel since R", FluentFin.Services.ArrivalFocus.MovedSince(FluentFin.Services.ArrivalKind.Refreshed, 0, 0, movedSinceRefresh: true));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m1: R with the keyboard on the top bar's tab, its place not to be found: the restore ran its whole
	/// 6 s and every R meanwhile joined it and did nothing): an R joins a refresh only while it is still coming in (its page
	/// loading, or loaded half a second ago at most); once the page has settled, the restore gives up what is not in it at
	/// once (the rows, the keyboard's item: the Primary then), and the next R loads the page again.
	/// </summary>
	[Fact]
	public void ARefreshWhosePlaceIsGone_EndsAtLoad_AndTheNextRRefreshes()
	{
		var now = new DateTime(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc);
		Assert.True(ThemeAnchors.StillComing(loading: true, loadedAt: null, now));
		Assert.True(ThemeAnchors.StillComing(loading: null, loadedAt: now.AddSeconds(-5), now));   // a page that does not say: as before
		Assert.True(ThemeAnchors.StillComing(loading: false, loadedAt: null, now));                // loaded, not yet seen by the restore
		Assert.True(ThemeAnchors.StillComing(loading: false, loadedAt: now.AddMilliseconds(-200), now)); // R, R 150 ms apart: one load (M4)
		Assert.False(ThemeAnchors.StillComing(loading: false, loadedAt: now.AddMilliseconds(-600), now));

		// the keyboard not in the page: the rows are done when placed, or once the page has settled
		Assert.False(ThemeAnchors.RowsDone(rows: 2, placed: 1, settled: false));
		Assert.True(ThemeAnchors.RowsDone(rows: 2, placed: 1, settled: true));
		Assert.True(ThemeAnchors.RowsDone(rows: 2, placed: 2, settled: false));
		// the keyboard's item gone from every list (watched elsewhere, finished on a phone): the Primary at once
		Assert.True(ThemeAnchors.ItemGone(settled: true, inLists: false));
		Assert.False(ThemeAnchors.ItemGone(settled: true, inLists: true));
		Assert.False(ThemeAnchors.ItemGone(settled: false, inLists: false));

		var run = Source("Services", "PageRefresh.cs");
		Assert.Contains("if (Coalesces(_pending?.Settling == true, ReferenceEquals(_pending?.Page, shown), _userMoved))", run);
		var anchors = Source("Helpers", "ThemeAnchors.cs").Replace("\r\n", "\n");
		Assert.Contains("public bool Settling => IsRunning && ThemeAnchors.StillComing(ThemeAnchors.IsLoading(Page), LoadedAt, DateTime.UtcNow);", anchors);
		var tick = anchors[anchors.IndexOf("public RefreshRestore RestoreAfterRefresh", StringComparison.Ordinal)..];
		Assert.Contains("var done = TryRestoreByPlace(page, last, settled);", tick);
		var tryRestore = anchors[anchors.IndexOf("private bool TryRestoreByPlace", StringComparison.Ordinal)..];
		Assert.Contains("return RowsDone(RowPlaces.Count, placed, settled);", tryRestore);
		Assert.Contains("ItemGone(settled, sources.Any(", tryRestore);
	}

	[Fact]
	public void Refresh_RunsWhileBrowsing_NotInADialogOrMenu_NotInThePlayer_NotSignedOut_NotOverATrailer()
	{
		Assert.Equal(WindowShortcuts.ShortcutGate.Run, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Browsing, popupOpen: false, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Browsing, popupOpen: true, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Player, popupOpen: false, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.Ignore, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.SignedOut, popupOpen: false, trailerOpen: false));
		Assert.Equal(WindowShortcuts.ShortcutGate.HoldBack, WindowShortcuts.Gate(AppShortcut.Refresh, ShortcutContext.Browsing, popupOpen: false, trailerOpen: true));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M1): over a trailer every shortcut is handled: Back closes it, the rest is held back. 1.3.1 left
	/// them unhandled (Ignore), so the key went on to the accelerators and the search box's Ctrl+F took the keyboard.
	/// </summary>
	[Fact]
	public void OverATrailer_BackClosesIt_AndEveryOtherShortcutIsHandled_NothingRuns()
	{
		Assert.Equal(WindowShortcuts.ShortcutGate.CloseTrailer, WindowShortcuts.Gate(AppShortcut.Back, ShortcutContext.Browsing, popupOpen: false, trailerOpen: true));
		foreach (var other in Enum.GetValues<AppShortcut>().Where(s => s != AppShortcut.Back))
		{
			Assert.Equal(WindowShortcuts.ShortcutGate.HoldBack, WindowShortcuts.Gate(other, ShortcutContext.Browsing, popupOpen: false, trailerOpen: true));
		}
	}

	/// <summary>Nixie 1.4.0 (bug hunt M1): the window's key handler decides the theatre's keys before any shortcut or accelerator.</summary>
	[Fact]
	public void OverATrailer_TheWindowDecidesTheKeysFirst_AndTheTheatreKeepsTheKeyboard()
	{
		var keys = Source("Services", "WindowShortcuts.cs").Replace("\r\n", "\n");
		var theatre = keys.IndexOf("if (Controls.TrailerTheater.Current is { IsOpen: true } theater)", StringComparison.Ordinal);
		Assert.True(theatre > 0 && theatre < keys.IndexOf("if (shortcut is { } found && Run(root, found", StringComparison.Ordinal));
		Assert.Contains("case TheaterKey.HoldBack:\n\t\t\t\t\te.Handled = true;", keys);
		Assert.Contains("case TheaterKey.Close:\n\t\t\t\t\te.Handled = true;\n\t\t\t\t\ttheater.Close();", keys);

		var theater = Source("Controls", "TrailerTheater.cs");
		Assert.Contains("root.GettingFocus += OnRootGettingFocus;", theater);
		Assert.Contains("args.TryCancel()", theater);
		// let go before the web view closes and the theatre collapses (the keyboard goes back to the Trailer button)
		var close = theater[theater.IndexOf("public void Close()", StringComparison.Ordinal)..];
		Assert.True(close.IndexOf("Release();", StringComparison.Ordinal) < close.IndexOf("CloseWeb();", StringComparison.Ordinal));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M4: R, R 150 ms apart loaded the page twice and the keyboard ended on the TitleBar; a held R 16
	/// times): the window passes a held key's repeat bit on and a repeat runs nothing; a second R while the first refresh's
	/// place is still coming back joins it.
	/// </summary>
	[Fact]
	public void AHeldR_RefreshesOnce_AndASecondRJoinsTheRefreshUnderWay()
	{
		var keys = Source("Services", "WindowShortcuts.cs").Replace("\r\n", "\n");
		Assert.Contains("Run(root, found, e.KeyStatus.WasKeyDown)", keys);
		Assert.Contains("if (repeat && !AppShortcuts.Repeats(shortcut))\n\t\t\t{\n\t\t\t\treturn true;", keys);
		Assert.Contains("ownsLetters: TextInput.OwnsLetters(e.OriginalSource)", keys);

		Assert.True(PageRefresh.Coalesces(running: true, samePage: true, userMoved: null));
		Assert.False(PageRefresh.Coalesces(running: false, samePage: true, userMoved: null));
		Assert.False(PageRefresh.Coalesces(running: true, samePage: false, userMoved: null));
		// the user moved since: this R is a new refresh, from where the user is now
		Assert.False(PageRefresh.Coalesces(running: true, samePage: true, userMoved: "a key"));

		var run = Source("Services", "PageRefresh.cs");
		Assert.True(run.IndexOf("if (Coalesces(", StringComparison.Ordinal) < run.IndexOf("ThemeAnchors.Capture(shown)", StringComparison.Ordinal));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt M3: on a slow server the place came back 4.6 s after R, taking the keyboard out of the search box
	/// the user had typed in): the restore gives way to the user's own move, to a text box, and to leaving the page.
	/// </summary>
	[Fact]
	public void ThePlaceRestore_GivesWayToTheUser()
	{
		Assert.Null(PageRefresh.YieldReason(userMoved: null, focusInTextInput: false, pageShown: true));
		Assert.Contains("the user moved first (a key)", PageRefresh.YieldReason("a key", false, true));
		Assert.Contains("text box", PageRefresh.YieldReason(null, focusInTextInput: true, pageShown: true));
		Assert.Contains("left", PageRefresh.YieldReason(null, false, pageShown: false));

		var keys = Source("Services", "WindowShortcuts.cs").Replace("\r\n", "\n");
		// every key but a refresh key or a modifier alone; a click; the wheel
		Assert.Contains("if (shortcut != AppShortcut.Refresh && !AppShortcuts.IsModifierKey((int)e.Key))\n\t\t{\n\t\t\tPageRefresh.UserActed(\"a key\");", keys);
		Assert.Contains("PageRefresh.UserActed(\"a click\");", keys);
		Assert.Contains("PageRefresh.UserActed(\"the wheel\")", keys);

		var anchors = Source("Helpers", "ThemeAnchors.cs").Replace("\r\n", "\n");
		var tick = anchors[anchors.IndexOf("public RefreshRestore RestoreAfterRefresh", StringComparison.Ordinal)..];
		Assert.True(tick.IndexOf("yieldTo?.Invoke()", StringComparison.Ordinal) < tick.IndexOf("TryRestoreByPlace(page, last, settled)", StringComparison.Ordinal));
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m3: R with the keyboard on Trailer put it on Play): a page's own button is anchored by its
	/// x:Name (else its type and Narrator name) and gets the keyboard back once it shows; the Primary only when it is gone.
	/// </summary>
	[Fact]
	public void AButtonOfThePage_KeepsTheKeyboard()
	{
		var anchors = Source("Helpers", "ThemeAnchors.cs").Replace("\r\n", "\n");
		Assert.Contains("control = focused is null && element is FrameworkElement shown ? ControlAnchor.Of(shown) : null;", anchors);
		Assert.Contains("FocusedControl = control", anchors);
		var tryRestore = anchors[anchors.IndexOf("private bool TryRestoreByPlace", StringComparison.Ordinal)..];
		Assert.True(tryRestore.IndexOf("anchor.Find(page)", StringComparison.Ordinal) < tryRestore.IndexOf("if (Focused is null || last)", StringComparison.Ordinal));
		Assert.Equal("TrailerButton", new ControlAnchor("TrailerButton", null, null).ToString());
		Assert.Equal("Button 'Mark watched'", new ControlAnchor(null, "Microsoft.UI.Xaml.Controls.Button", "Mark watched").ToString());
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
