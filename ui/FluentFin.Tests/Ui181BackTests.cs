using FluentFin.Services;
using Microsoft.UI.Dispatching;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.8.1, the back stream: what the harness's leak line says (BH-175-q1), and the Collections page's place line.
/// </summary>
public sealed class Ui181BackTests
{
	// stand-ins named as the pages are: the line says each page's type name
	private sealed class LibraryPage;

	private sealed class SeriesPage;

	[Fact]
	public void TheLeakLine_CountsAPageKeptWholeOnce_HoweverOftenItIsShown()
	{
		// BH-175-q1: a library kept whole is the same page every visit; 37 visits read "LibraryPage 37", so a leak check on Home,
		// a library or search failed with nothing leaking. Pages made anew each visit (a series page) still count each
		// (the review: through the path the app's line takes, CountLive; a test of NoteSeen alone passed with the line's own
		// Add put back)
		var seen = new List<WeakReference<object>>();
		var library = new LibraryPage();
		var series = new List<SeriesPage>();
		var said = Visits(seen, library, series, 37);

		Assert.Equal("LibraryPage 1, SeriesPage 37", said);
		// pages let go are counted no more (a series page left and collected)
		series.Clear();
		Assert.Equal("LibraryPage 1", NavigationService.CountLive<object>(seen, library));
		GC.KeepAlive(library);
		// the app's own line goes through it
		Assert.Contains("private void CountLivePages(Page? shown) => _logger?.LogInformation(\"[leak] live pages: {Pages}\", CountLive(SeenPages, shown));",
			Code("Services", "NavigationService.cs"));
	}

	// a library kept whole shown on each visit, and a series page made anew each time (held in `series`, as a leak would hold
	// them); what the line said after the last (no page made here stays on this method's stack)
	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static string Visits(List<WeakReference<object>> seen, LibraryPage library, List<SeriesPage> series, int visits)
	{
		var said = "";
		for (var visit = 0; visit < visits; visit++)
		{
			_ = NavigationService.CountLive<object>(seen, library);
			series.Add(new SeriesPage());
			said = NavigationService.CountLive<object>(seen, series[^1]);
		}

		return said;
	}

	private static string Code(params string[] parts) =>
		File.ReadAllText(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts]))).Replace("\r\n", "\n", StringComparison.Ordinal);

	[Fact]
	public void TheCollectionsPage_SaysWhenBacksTileTookTheKeyboard()
	{
		// rule 10: Back's place line on the Collections page (the gate's Drive-in cell took 1.3 to 1.5 s under load, and the app
		// said only "shown as it was", the view model's part)
		var code = Code("Views", "CollectionsPage.xaml.cs");
		Assert.Contains("NavigationService.PlacePutBack(this, $\"tile and place (try {_placeTry})\");", code);
		// and the item pages (a film, a series, a season, an episode: the gate's series cells read 1.3 to 1.6 s under load while
		// the app said only "shown as it was in 14 ms", the view model's part)
		var item = Code("Views", "ItemPagePlace.cs");
		Assert.Contains("Services.NavigationService.PlacePutBack(_page, \"place\");", item);
		Assert.Contains("Services.NavigationService.PlaceStep(_page, $\"scroll asked ({(int)offset} px)\");", item);
	}

	// ---- 1.7.5's Left: Back with the search box's list of suggestions open did nothing (about 1 samesearch cell in 17) ----

	[Fact]
	public void TheSearchBoxsListOfSuggestions_HoldsNoBackOrForward_ButAMenuStillHoldsEveryKey()
	{
		var gate = WindowShortcuts.ShortcutGate.Run;
		var ignore = WindowShortcuts.ShortcutGate.Ignore;
		// the list alone: Back and Forward go (a key, Mouse 4 and Mouse 5)
		Assert.Equal(gate, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.Back, Core.Shortcuts.ShortcutContext.Browsing, popupOpen: true, trailerOpen: false, searchListOnly: true));
		Assert.Equal(gate, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.Forward, Core.Shortcuts.ShortcutContext.Browsing, popupOpen: true, trailerOpen: false, searchListOnly: true));
		// the list holds the rest as before (R, Home's key, "?" over a list being typed in)
		Assert.Equal(ignore, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.Refresh, Core.Shortcuts.ShortcutContext.Browsing, popupOpen: true, trailerOpen: false, searchListOnly: true));
		Assert.Equal(ignore, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.ShowHelp, Core.Shortcuts.ShortcutContext.Browsing, popupOpen: true, trailerOpen: false, searchListOnly: true));
		// a menu, a flyout or a dialog (or one beside the list) still holds Back
		Assert.Equal(ignore, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.Back, Core.Shortcuts.ShortcutContext.Browsing, popupOpen: true, trailerOpen: false, searchListOnly: false));
		// never where the shortcut isn't allowed at all (Forward in the player)
		Assert.Equal(ignore, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.Forward, Core.Shortcuts.ShortcutContext.Player, popupOpen: true, trailerOpen: false, searchListOnly: true));
		// over a trailer: Back closes it, as with no list
		Assert.Equal(WindowShortcuts.ShortcutGate.CloseTrailer, WindowShortcuts.Gate(Core.Shortcuts.AppShortcut.Back, Core.Shortcuts.ShortcutContext.Browsing, popupOpen: true, trailerOpen: true, searchListOnly: true));

		Assert.True(WindowShortcuts.IsSuggestionList("SuggestionsPopup", inSearchBox: true));
		Assert.False(WindowShortcuts.IsSuggestionList("SuggestionsPopup", inSearchBox: false)); // another control's popup of that name
		Assert.False(WindowShortcuts.IsSuggestionList(null, inSearchBox: true)); // a flyout opened from the box
		Assert.Contains("var (popupOpen, searchListOnly) = OpenPopups(root.XamlRoot);", Code("Services", "WindowShortcuts.cs"));
	}

	[Fact]
	public void AnAnswerToAQueryAskedBeforeEntersDrop_NeverFillsTheList_WheneverItArrives()
	{
		// an answer that arrived just before Enter filled the list after Enter's drop, and the list opened over the results page;
		// the review: stamped when it arrived, one arriving between Enter's count and its drop still counted as after it
		var gate = new Behaviors.SuggestionGate();
		gate.Asked("the");
		Assert.True(gate.Shows(dropped: false, "the")); // its answer, before Enter: the list
		gate.Dropped(); // Enter
		Assert.False(gate.Shows(dropped: false, "the")); // the same answer arriving after Enter (or during it): never
		Assert.True(gate.Shows(dropped: true, "")); // a drop always closes the list
		gate.Asked("the"); // typed again after the drop: its answer shows
		Assert.True(gate.Shows(dropped: false, "the"));
		Assert.False(gate.Shows(dropped: false, "they")); // an answer to a query never asked since
		var code = Code("Behaviors", "GlobalSearchBoxBehavior.cs");
		Assert.Contains("_gate.Asked(sender.Text); // 1.8.1: stamped as it is asked, on the box's thread\n\t\t_textChangedSubject.OnNext(sender.Text);", code);
		Assert.Contains("_gate.Asked(retry.Query); // 1.8.1\n\t\t\t\t_searchNow.OnNext(retry.Query);", code);
		Assert.Contains("if (!_gate.Shows(update.IsDropped, update.Query))", code);
		Assert.Contains("_gate.Dropped(); // 1.8.1", code[code.IndexOf("private void Drop()", StringComparison.Ordinal)..]);
	}

	[Fact]
	public void BackToAnySearchsResults_DropsTheSearchInTheBox()
	{
		// Back from "the" (typed, its list open) to the results of "sunny": the list and an answer due for "the" go with it; the
		// review: back to the same query's results too (the list stayed open over them)
		var backend = new Moq.Mock<Core.Mpv.IMpvBackend>();
		backend.Setup(x => x.Events).Returns(System.Reactive.Linq.Observable.Never<Core.Mpv.BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(System.Reactive.Linq.Observable.Never<Core.Mpv.MpvInterruptedPlayback>());
		var client = new Moq.Mock<Core.Contracts.Services.IJellyfinClient>();
		var playback = new Core.Mpv.MpvPlaybackService(backend.Object, client.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<Core.Mpv.MpvPlaybackService>.Instance);
		var vm = new ViewModels.TitleBarViewModel(Moq.Mock.Of<Contracts.Services.INavigationService>(), Moq.Mock.Of<Contracts.Services.INavigationService>(),
			Moq.Mock.Of<UI.Core.Contracts.Services.INavigationViewService>(), client.Object, playback, null) { User = new Jellyfin.Sdk.Generated.Models.UserDto { Name = "alice" } };
		var resets = 0;
		vm.SearchReset += (_, _) => resets++;

		vm.SearchText = "sunny"; // typed and submitted: its results keep it (the submit dropped it already)
		vm.OnPageShown(typeof(Views.SearchResultsPage), "sunny");
		Assert.Equal((0, "sunny"), (resets, vm.SearchText));
		vm.SearchText = "lantern"; // typed, its list open
		vm.OnPageShown(typeof(Views.SearchResultsPage), "the"); // another search's results (Forward)
		Assert.Equal((1, "the"), (resets, vm.SearchText));
		vm.SearchText = "sunny"; // typed again, the same query as the results Back goes to, its list open
		vm.SteppedBackOrForward(); // (the shell, on Back or Forward)
		vm.OnPageShown(typeof(Views.SearchResultsPage), "sunny");
		Assert.Equal((2, "sunny"), (resets, vm.SearchText));
		vm.OnPageShown(typeof(Views.SeasonPage)); // any other page drops it, as before
		Assert.Equal((3, ""), (resets, vm.SearchText));
		// the shell tells it on every Back and Forward, before the page's own word
		var shell = Code("Views", "ShellPage.xaml.cs");
		Assert.True(shell.IndexOf("_titleBar.SteppedBackOrForward();", StringComparison.Ordinal) is > 0 and var stepped
			&& stepped < shell.IndexOf("_titleBar.OnPageShown(e.SourcePageType, e.Parameter);", StringComparison.Ordinal));
		Assert.Contains("if (e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward)\n\t\t{\n\t\t\t_titleBar.SteppedBackOrForward();", shell);
	}

	[Fact]
	public void EditorialsLibrary_BacksPlaceHoldsTheGridsBringIntoView_UntilItLands()
	{
		// 1.7.5's release gate at max: Back put the grid at 1,253 px, then it moved to 302 px as the poster left from (focused
		// while the scroll was still on its way) was brought up from the view's foot: the collection page's rule, here too
		var code = Code("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs");
		var back = code[code.IndexOf("if (ViewModel.ReturnOffset is { } offset)", StringComparison.Ordinal)..];
		back = back[..back.IndexOf("else if (jumped", StringComparison.Ordinal)];
		Assert.Contains("GridScroller.ChangeView(null, target, null, disableAnimation: true);\n\t\t\t\tGridScroller.UpdateLayout();\n\t\t\t\tHoldPlace(target);", back);
		var focus = code[code.IndexOf("private bool FocusIndex(int index, FocusState state)", StringComparison.Ordinal)..code.IndexOf("private void HoldPlace(double target)", StringComparison.Ordinal)];
		Assert.Contains("if (PlaceHoldNow() == PlaceHold.Hold)\n\t\t\t{\n\t\t\t\t_clearOnLanding = poster;\n\t\t\t\tSayHeld(\"the bar's clearance\");\n\t\t\t\treturn;\n\t\t\t}\n\n\t\t\tKeepClearOfBar(poster);", focus);
		var hold = code[code.IndexOf("private void HoldPlace(double target)", StringComparison.Ordinal)..];
		Assert.Contains("GridList.BringIntoViewRequested += (_, args) =>\n\t\t\t{\n\t\t\t\tif (PlaceHoldNow() == PlaceHold.Hold)\n\t\t\t\t{\n\t\t\t\t\targs.Handled = true;\n\t\t\t\t\tSayHeld(\"a bring-into-view\");", hold);
		Assert.Contains("if (!e.IsIntermediate)\n\t\t\t\t{\n\t\t\t\t\t_ = PlaceHoldNow();", hold);
		// landed or over, the place is no longer noted and what waited goes; only a landing runs the clearance it held
		var now = hold[hold.IndexOf("private PlaceHold PlaceHoldNow()", StringComparison.Ordinal)..];
		Assert.Contains("if (step is PlaceHold.Landed or PlaceHold.Ended)\n\t\t{\n\t\t\t_placeLanding = null;\n\t\t\tvar poster = _clearOnLanding;\n\t\t\t_clearOnLanding = null;", now);
		Assert.Contains("if (step == PlaceHold.Landed && poster is not null)", now);
	}

	[Fact]
	public void EditorialsLibrary_BacksPlaceHold_EndsWhenTheUserMovesOrItsTimeIsUp_LandedOrNot()
	{
		// the review: the hold's time and the user were checked only where it held, never where it landed: a place left noted
		// after its time ran out landed whenever a scroll reached that offset, and its waiting nudge moved the grid then
		var e = Themes.Editorial.Views.EditorialLibraryPage.HoldStep;
		Assert.Equal(Themes.Editorial.Views.EditorialLibraryPage.PlaceHold.Hold, e(true, false, false, 100, 1500));
		Assert.Equal(Themes.Editorial.Views.EditorialLibraryPage.PlaceHold.Landed, e(true, true, false, 100, 1500));
		Assert.Equal(Themes.Editorial.Views.EditorialLibraryPage.PlaceHold.Ended, e(true, true, false, 1501, 1500)); // at the offset, too late
		Assert.Equal(Themes.Editorial.Views.EditorialLibraryPage.PlaceHold.Ended, e(true, true, true, 100, 1500)); // the user's scroll
		Assert.Equal(Themes.Editorial.Views.EditorialLibraryPage.PlaceHold.Ended, e(true, false, false, 1501, 1500));
		Assert.Equal(Themes.Editorial.Views.EditorialLibraryPage.PlaceHold.None, e(false, true, false, 100, 1500));
	}

	[Fact]
	public void TheCollectionsPage_IsKeptWholeForBack_AndItsArrivalFocusIsEachVisits()
	{
		// the 1.8.0 release gate's Drive-in cell: made anew on Back, the page's first layout took 0.65 to 1.75 s under load
		Assert.True(NavigationService.KeptWhole(typeof(Views.CollectionsPage)));
		Assert.False(NavigationService.KeptWhole(typeof(Views.CollectionPage))); // a collection's own page is made anew, as before
		var code = Code("Views", "CollectionsPage.xaml.cs");
		Assert.Contains("NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;", code);
		// the same page instance comes again on Back: its arrival focus runs again (it ran once a page before)
		var arriving = code[code.IndexOf("protected override void OnNavigatedTo(", StringComparison.Ordinal)..code.IndexOf("protected override void OnNavigatedFrom(", StringComparison.Ordinal)];
		Assert.True(arriving.IndexOf("_focused = false;", StringComparison.Ordinal) is > 0 and var reset
			&& reset < arriving.IndexOf("OnStateChanged(ViewModel.State,", StringComparison.Ordinal), "the arrival's flag is reset before the arrival focus is asked for");
		// See all is the same page type: Collections, See all, Back never gets the See all page (the frame's one instance)
		var kept = new object();
		Assert.False(NavigationService.UsesKeptWhole(typeof(Views.CollectionsPage), typeof(Views.CollectionsPage), kept, kept));
		Assert.True(NavigationService.UsesKeptWhole(typeof(Views.CollectionPage), typeof(Views.CollectionsPage), kept, kept)); // a collection, Back
	}

	[Fact]
	public void ACollectionsPageBackShowsAsItWas_GivesItsTileTheKeyboard_AtTheNormalPriority()
	{
		// the 1.8.0 release gate's Drive-in cell: Back's tile 1.3 to 1.5 s after Back under load; on the low priority the first try
		// waited 700 ms behind the window's other work with the page already in the window ("page loaded 134, try 1 843")
		Assert.Equal(DispatcherQueuePriority.Normal, Views.CollectionsPage.ArrivalPriority(returning: true, shownAsItWas: true));
		Assert.Equal(DispatcherQueuePriority.Low, Views.CollectionsPage.ArrivalPriority(returning: false, shownAsItWas: true)); // a theme switch's page
		Assert.Equal(DispatcherQueuePriority.Low, Views.CollectionsPage.ArrivalPriority(returning: true, shownAsItWas: false)); // made anew: tiles later
		Assert.Equal(DispatcherQueuePriority.Low, Views.CollectionsPage.ArrivalPriority(returning: false, shownAsItWas: false)); // an arrival
		var code = Code("Views", "CollectionsPage.xaml.cs");
		var take = code[code.IndexOf("private void TakeArrivalFocus(int tries)", StringComparison.Ordinal)..code.IndexOf("public static Microsoft.UI.Dispatching.DispatcherQueuePriority ArrivalPriority", StringComparison.Ordinal)];
		Assert.Contains("}, ArrivalPriority(_returnTo is not null, _shownAsItWas));", take);
		Assert.Contains("_shownAsItWas = ViewModel.State.IsLoaded;", code);
	}
}
