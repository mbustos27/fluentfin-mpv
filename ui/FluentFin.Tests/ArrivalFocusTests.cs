using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (bug hunt M1: a slow Collection page took the keyboard out of the search box, and the next letter, R, refreshed
/// the page; m4: the Libraries page and a library's first poster the same; m6: the wheel was not the user moving): one rule
/// for every page's arrival focus (<see cref="ArrivalFocus"/>), the same as R's place restore, and every page that sets its
/// focus on arrival asks it.
/// </summary>
public sealed class ArrivalFocusTests
{
	private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts])).Replace("\r\n", "\n");

	[Fact]
	public void TheArrival_YieldsToTheUser_AsRsRestoreDoes()
	{
		Assert.Null(ArrivalFocus.YieldReason(userMoved: null, focusInTextInput: false, openPopups: 0, pageShown: true));
		Assert.Contains("the user moved first", ArrivalFocus.YieldReason("a key, a click or the wheel", false, 0, true));
		// the search box: never pulled out of it, however the keyboard got there
		Assert.Contains("text box", ArrivalFocus.YieldReason(null, focusInTextInput: true, 0, true));
		Assert.Contains("menu", ArrivalFocus.YieldReason(null, false, openPopups: 1, true));
		Assert.Contains("left", ArrivalFocus.YieldReason(null, false, 0, pageShown: false));
		// the same words as R's restore for what they share
		Assert.Equal(PageRefresh.YieldReason("a key", true, true), ArrivalFocus.YieldReason("a key", true, 0, true));
	}

	[Theory]
	[InlineData(ArrivalKind.Opened, 10_000, 9_000, false, false)]       // the key that opened the page
	[InlineData(ArrivalKind.Opened, 10_000, 10_300, false, false)]      // its repeat, within the grace
	[InlineData(ArrivalKind.Opened, 10_000, 10_401, false, true)]       // Ctrl+F while the page loads
	[InlineData(ArrivalKind.Returned, 10_000, 12_000, false, true)]
	[InlineData(ArrivalKind.Refreshed, 10_000, 12_000, false, false)]   // after R: what R's restore counts
	[InlineData(ArrivalKind.Refreshed, 10_000, 9_000, true, true)]
	public void TheUserMoved_AfterTheGrace(ArrivalKind kind, long arrivedAt, long actedAt, bool movedSinceRefresh, bool moved) =>
		Assert.Equal(moved, ArrivalFocus.MovedSince(kind, arrivedAt, actedAt, movedSinceRefresh) is not null);

	/// <summary>A key, a click and the wheel are the user moving, for the arrival as for R's restore (m6: the wheel was not).</summary>
	[Fact]
	public void AKeyAClickAndTheWheel_AreTheUserMoving()
	{
		var keys = Source("Services", "WindowShortcuts.cs");
		Assert.Equal(3, keys.Split("ArrivalFocus.UserActed();").Length - 1);
		Assert.DoesNotContain("ItemPagePlace.UserActedAt", keys);
		var wheel = keys[keys.IndexOf("PointerWheelChangedEvent", StringComparison.Ordinal)..];
		Assert.True(wheel.IndexOf("ArrivalFocus.UserActed();", StringComparison.Ordinal) < wheel.IndexOf("private static void OnPreviewKeyDown", StringComparison.Ordinal));

		var before = ArrivalFocus.UserActedAt;
		ArrivalFocus.UserActed();
		Assert.True(ArrivalFocus.UserActedAt >= before && ArrivalFocus.UserActedAt > 0);
	}

	/// <summary>The navigation says when and how each page came, before its view model loads.</summary>
	[Fact]
	public void EachArrival_IsRecorded_BeforeTheViewModelLoads()
	{
		var nav = Source("Services", "NavigationService.cs");
		var recorded = nav.IndexOf("ArrivalFocus.Arrived(frame.Content as Page, refresh ? ArrivalKind.Refreshed", StringComparison.Ordinal);
		Assert.True(recorded > 0 && recorded < nav.IndexOf("await navigationAware.OnNavigatedTo(e.Parameter);", StringComparison.Ordinal));
		Assert.Contains("ArrivalFocus.Arrived(frame.Content as Page, ArrivalKind.ThemeSwitched, frame.XamlRoot);", nav);
	}

	/// <summary>Every page that focuses on arrival asks the one rule.</summary>
	[Fact]
	public void EveryArrivalFocus_AsksTheOneRule()
	{
		var collection = Source("Views", "CollectionPage.xaml.cs");
		var arrival = collection[collection.IndexOf("private void FocusOnArrival()", StringComparison.Ordinal)..];
		arrival = arrival[..arrival.IndexOf("private int IndexOf(", StringComparison.Ordinal)];
		Assert.Equal(3, arrival.Split("ArrivalFocus.TryTake(this, ").Length - 1);
		Assert.DoesNotContain("\t\t\tFocusCard(back);", arrival);

		var libraries = Source("Views", "LibrariesLandingPage.xaml.cs");
		Assert.Contains("ArrivalFocus.TryTake(this, \"the first tile\", FocusFirstTile)", libraries);

		var item = Source("Views", "ItemPagePlace.cs");
		Assert.Contains("var why = Services.ArrivalFocus.WhyNot(_page);", item);
		Assert.Contains("switch (ArrivalFocus(_loaded(), ready, inPage || why is not null, popups))", item);
		// its "Play again" watch never takes the keyboard out of a text box either
		Assert.Contains("FocusAgain(inPage || Services.TextInput.IsTextInput(current), popups,", item);
		// a tooltip is not a menu: a poster's "Play ..." tooltip still up kept Play from taking the keyboard (Editorial)
		Assert.DoesNotContain("GetOpenPopupsForXamlRoot(root).Count", item);
		Assert.Equal(2, item.Split("Services.ArrivalFocus.OpenPopups(root)").Length - 1);
		Assert.Contains("isOpen && child is not ToolTip", Source("Services", "ArrivalFocus.cs"));

		// the libraries: every theme's page through the view model (Editorial's page included, which sets its own rule too)
		Assert.Contains("Core.ViewModels.LibraryViewModel.LoadFocusYields = Services.ArrivalFocus.LibraryYields;", Source("App.xaml.cs"));
		Assert.DoesNotContain("vm.FocusYields =", Source("Controls", "LibraryGridHost.cs"));
		var library = Source("Services", "ArrivalFocus.cs");
		Assert.Contains("WhyNot(page, restore: true)", library);
	}

	/// <summary>
	/// Review of wip/140-fix2, 1: a search suggestion chosen with Enter opens its page with the keyboard still in the emptied
	/// box. That box is where the navigation left it, not where the user went: the arrival takes the keyboard (Play), as it
	/// did before the one rule. A box the user went to after the page came keeps it (the hunt's M1).
	/// </summary>
	[Fact]
	public void TheBoxASuggestionWasChosenIn_IsNotTheUsersPlace_ABoxTheyWentToIs()
	{
		Assert.False(ArrivalFocus.InTextInputByTheUser(focusInTextInput: true, whereTheNavigationLeftIt: true));
		Assert.True(ArrivalFocus.InTextInputByTheUser(focusInTextInput: true, whereTheNavigationLeftIt: false));
		Assert.False(ArrivalFocus.InTextInputByTheUser(focusInTextInput: false, whereTheNavigationLeftIt: false));

		var rule = Source("Services", "ArrivalFocus.cs");
		Assert.Contains("InTextInputByTheUser(TextInput.IsTextInput(focused), leftThere)", rule);
		Assert.Contains("_focusedAtArrival = (root ?? page.XamlRoot) is { } at && FocusManager.GetFocusedElement(at) is { } focused", rule);
		var nav = Source("Services", "NavigationService.cs");
		Assert.Contains("ArrivalKind.Returned : ArrivalKind.Opened, frame.XamlRoot);", nav);
		Assert.Contains("ArrivalFocus.Arrived(frame.Content as Page, ArrivalKind.ThemeSwitched, frame.XamlRoot);", nav);
	}

	/// <summary>
	/// Review of wip/140-fix2, 2 and 3: Back to an item page puts its place back (Play, a card) by the one rule too, and so do
	/// the Home pages' Back and Drive-in Home's arrival (it only looked for a text box: a key, a click, the wheel, R and a theme
	/// switch did not stop it).
	/// </summary>
	[Fact]
	public void BackToAnItemPage_AndHome_AskTheOneRule()
	{
		var item = Source("Views", "ItemPagePlace.cs");
		var restore = item[item.IndexOf("private void Restore(PagePlace place)", StringComparison.Ordinal)..];
		restore = restore[..restore.IndexOf("private DependencyObject? Target(", StringComparison.Ordinal)];
		Assert.Contains("FocusRestore(userThere || Services.ArrivalFocus.WhyNot(_page) is not null, Services.ArrivalFocus.OpenPopups(root))", restore);

		var driveIn = Source("Themes", "DriveIn", "Views", "DriveInHomePage.xaml.cs");
		var arrival = driveIn[driveIn.IndexOf("private void TryArrivalFocus()", StringComparison.Ordinal)..];
		arrival = arrival[..arrival.IndexOf("protected override void OnNavigatedFrom", StringComparison.Ordinal)];
		Assert.Contains("if (FluentFin.Services.ArrivalFocus.WhyNot(this) is not null)", arrival);
		Assert.DoesNotContain("TextInput.IsTextInput", arrival);

		foreach (var home in new[] { Source("Views", "HomePage.xaml.cs"), driveIn, Source("Themes", "Cinematic", "Views", "CinematicHomePage.xaml.cs"), Source("Themes", "Editorial", "Views", "EditorialHomePage.xaml.cs") })
		{
			Assert.Contains("if (place.Row is { } header && place.ItemId is { } id && FluentFin.Services.ArrivalFocus.WhyNot(this) is null)", home);
		}
	}

	/// <summary>Review of wip/140-fix2, 4: the 10 s undo notice is not a menu; a page opened while it shows still takes the keyboard.</summary>
	[Fact]
	public void TheUndoNotice_IsNotAMenu()
	{
		Assert.True(ArrivalFocus.CountsAsMenu(isOpen: true, child: null, tag: null));
		Assert.False(ArrivalFocus.CountsAsMenu(isOpen: true, child: null, tag: FluentFin.Controls.UndoToast.NoticeTag));
		Assert.False(ArrivalFocus.CountsAsMenu(isOpen: false, child: null, tag: null));
		Assert.Contains("new Popup { Child = card, XamlRoot = root.XamlRoot, Tag = NoticeTag }", Source("Controls", "UndoToast.cs"));
		Assert.Contains("Count(p => CountsAsMenu(p.IsOpen, p.Child, p.Tag))", Source("Services", "ArrivalFocus.cs"));
	}

	/// <summary>After R or a theme switch the page's own arrival never takes the keyboard: their restore puts it back.</summary>
	[Fact]
	public void AfterROrATheme_TheRestoreOwnsTheKeyboard()
	{
		var rule = Source("Services", "ArrivalFocus.cs");
		Assert.Contains("if (!restore && kind is ArrivalKind.Refreshed or ArrivalKind.ThemeSwitched)", rule);
		Assert.Contains("kind == ArrivalKind.Refreshed ? (movedSinceRefresh ?", rule);
	}
}
