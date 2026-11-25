using FluentFin.Controls;
using Microsoft.UI.Dispatching;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7.5, the back stream: the Collections page's arrival focus retry (its timer held by the page, and a line when the
/// tries stop) and the priority of a kept library's place on Back. The harness checks what the user sees of each
/// (tools\ui-harness\offscreen\then-175-arrival.ps1, back-gate.ps1's library row).
/// </summary>
public sealed class Ui175BackTests
{
	private static string Code(params string[] parts) =>
		File.ReadAllText(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts]))).Replace("\r\n", "\n", StringComparison.Ordinal);

	[Fact]
	public void CollectionsArrival_TheRetrysTimerIsHeldByThePage_AndTheTriesSayWhyTheyStop()
	{
		var code = Code("Views", "CollectionsPage.xaml.cs");
		var take = code[code.IndexOf("private void TakeArrivalFocus(int tries)", StringComparison.Ordinal)..code.IndexOf("private int EndArrivalTries()", StringComparison.Ordinal)];
		// held by the page until it ticks (a local alone held it: one "could not take", then no try and no line)
		Assert.Contains("var timer = _arrivalTimer = DispatcherQueue.CreateTimer();", take);
		Assert.Contains("ArrivalStopped(\"the user moved first\")", take);
		Assert.Contains("ArrivalStopped(ArrivalFocus.Shown(this) ? $\"no tile took the keyboard in {ArrivalTries} tries\" : \"the page is no longer shown\")", take);
		var end = code[code.IndexOf("private int EndArrivalTries()", StringComparison.Ordinal)..];
		Assert.Contains("_arrivalTimer?.Stop();\n\t\t_arrivalTimer = null;", end);
		Assert.Contains("private const int ArrivalTries = 40;", code);
	}

	[Fact]
	public void ALibraryBackShowsAsItWas_PutsItsPlaceBack_AtTheNormalPriority()
	{
		Assert.Equal(DispatcherQueuePriority.Normal, LibraryGridHost.BackPlacePriority(returnOffset: true, puttingBack: true));
		Assert.Equal(DispatcherQueuePriority.Low, LibraryGridHost.BackPlacePriority(returnOffset: true, puttingBack: false)); // a load's place (R, a late page)
		Assert.Equal(DispatcherQueuePriority.Low, LibraryGridHost.BackPlacePriority(returnOffset: false, puttingBack: true)); // a jump, the arrival
		Assert.Contains("var priority = BackPlacePriority(_vm.ReturnOffset is not null, Services.NavigationService.PuttingBack(_page));", Code("Controls", "LibraryGridHost.cs"));
		Assert.Contains("}, priority);", Code("Controls", "LibraryGridHost.cs"));
		Assert.Contains("DispatcherQueue.TryEnqueue(FluentFin.Controls.LibraryGridHost.BackPlacePriority(ViewModel.ReturnOffset is not null, NavigationService.PuttingBack(this)), () =>",
			Code("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs"));
	}

	[Fact]
	public void ACollectionBackShows_KeepsItsPlace_WhileItsCardsFocusWouldBringItIntoViewFromTheTop()
	{
		// back-gate3 (Classic, Cinematic): left 279 px down, Back landed at 218 (the card at the view's foot): the card's focus
		// asked for a bring-into-view from the page's top before the ScrollTo had landed, and won
		Assert.True(Views.CollectionPage.HoldsBringIntoView(landed: false, userMoved: false, now: 100, until: 1500));
		Assert.False(Views.CollectionPage.HoldsBringIntoView(landed: true, userMoved: false, now: 100, until: 1500)); // the scroll is there
		Assert.False(Views.CollectionPage.HoldsBringIntoView(landed: false, userMoved: true, now: 100, until: 1500)); // the user's own
		Assert.False(Views.CollectionPage.HoldsBringIntoView(landed: false, userMoved: false, now: 1501, until: 1500)); // never for long
		var code = Code("Views", "CollectionPage.xaml.cs");
		Assert.Contains("_placeScroll = PageScroller.ScrollTo(0, offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));\n\t\t\tHoldPlace();\n\t\t\tUpdateLayout();", code);
		Assert.Contains("args.Cancel = true;", code);
		Assert.Contains("PageScroller.ScrollCompleted += OnPlaceLanded;", code);
	}

	[Theory]
	[InlineData("Views/HomePage.xaml.cs")]
	[InlineData("Themes/Cinematic/Views/CinematicHomePage.xaml.cs")]
	[InlineData("Themes/DriveIn/Views/DriveInHomePage.xaml.cs")]
	[InlineData("Themes/Editorial/Views/EditorialHomePage.xaml.cs")]
	[InlineData("Views/LibraryPage.xaml.cs")]
	[InlineData("Themes/Cinematic/Views/CinematicLibraryPage.xaml.cs")]
	[InlineData("Themes/DriveIn/Views/DriveInLibraryPage.xaml.cs")]
	[InlineData("Themes/Editorial/Views/EditorialLibraryPage.xaml.cs")]
	[InlineData("Views/SearchResultsPage.xaml.cs")]
	public void Home_IsKeptWholeForBack_InEveryTheme_AndANewVisitMakesItAnew(string file)
	{
		// back-gate3: Back to Home made the whole page again, 1.5 to 1.8 s before its rows, scroll and card were back
		Assert.Contains("NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;", Code([.. file.Split('/')]));
		Assert.True(Services.NavigationService.KeptWhole(typeof(Views.HomePage)));
		Assert.True(Services.NavigationService.KeptWhole(typeof(Views.LibraryPage))); // 1.7.5, the gate's second round
		Assert.True(Services.NavigationService.KeptWhole(typeof(Views.SearchResultsPage)));
		Assert.False(Services.NavigationService.KeptWhole(typeof(Views.CollectionPage)));
		var nav = Code("Services", "NavigationService.cs");
		Assert.Contains("if (KeptWholePage(pageType))\n\t\t\t\t{\n\t\t\t\t\tDropKeptWhole(frame, pageType);\n\t\t\t\t}\n\n\t\t\t\tNoteLeaving(frame);", nav); // a tab, a sign-in
		// Back and Forward get the page kept whole only when it is the entry's own (its kept view model)
		Assert.Contains("PrepareKeptWhole(_frame, _frame.BackStack[^1]); // Nixie 1.7.5\n\t\t\t\t_frame.GoBack();", nav);
		Assert.Contains("PrepareKeptWhole(_frame, target); // Nixie 1.7.5\n\t\t\t\t_frame.GoForward();", nav);
		Assert.Contains("DropKeptWhole(frame, page.GetType());", nav); // R
		Assert.Contains("ClearPageCache(frame); // Nixie 1.7.5: the old theme's page", nav);
		Assert.Contains("homeCache.Forgetting += (_, _) => { LetGo(_keep.Clear(), \"a sign-out\"); ClearPageCacheSoon(); };", nav);
	}

	[Fact]
	public void AnEditorialPageLeftFromItsDetailsUnderARow_OpensThemAgainOnBack_BeforeItsScroll()
	{
		// back-gate3 (Editorial, the film page): left from "Full details" with the details open under More like this; Back closed
		// them, the page was 234 px shorter, and the scroll stopped at its end (287 -> 69 px)
		var code = Code("Views", "ItemPagePlace.cs");
		Assert.Contains("DetailsLeftOpen[_key] = open;", code);
		Assert.Contains("host.ToggleDetails(shown, poster);", code);
		Assert.True(code.IndexOf("host.ToggleDetails(shown, poster);", StringComparison.Ordinal) < code.IndexOf("if (!scrolled)", StringComparison.Ordinal));
		// back-gate9: its poster 9 of 12 was never made (1821 ms, 243 px short): the row makes it, with the keyboard the app's
		// own; back-gate10: the details then opened with the keyboard on their Play, which must stay the app's too
		Assert.Contains("shelf.ShelfRow.FocusItem(", code);
		Assert.Contains("made = FocusManager.GetFocusedElement(openRoot) as DependencyObject ?? made;", code);
		Assert.Contains("&& !ReferenceEquals(current, made);", code);
	}

	[Fact]
	public void TheItemHero_GrowsToHoldItsWords()
	{
		// the gate's g3 (1280 x 720): the page was at its top, but the hero (432 px) was shorter than an episode's words, and
		// they ran up under the top bar: its way up out of view (be9b9ad2's hold on Play's bring-into-view fixed nothing: gone)
		var code = Code("Controls", "ItemHero.xaml.cs");
		Assert.Contains("Words.SizeChanged += (_, _) => Fit();", code);
		Assert.Contains("HeroGrid.Height = HeroLayout.Fit(_shareHeight, Words.ActualHeight + Words.Margin.Top + Words.Margin.Bottom, TopBar.Height);", code);
		Assert.DoesNotContain("HeroGrid.Height = height;", code);
		Assert.DoesNotContain("HoldTop", Code("Views", "ItemPagePlace.cs"));
		// Cinematic's stage too (g3's check at 1280 x 720 on build-m: 23 of 34, the series' logo cut under the bar)
		var stage = Code("Themes", "Cinematic", "Controls", "CinematicItemStage.xaml.cs");
		Assert.Contains("Stage.Height = HeroLayout.Fit(StageHeight, Math.Max(words, Words.ActualHeight) + Words.Margin.Top + Words.Margin.Bottom + BottomPresenter.ActualHeight, 0);", stage);
		Assert.Contains("Words.SizeChanged += (_, _) => Fit();", stage);
		Assert.DoesNotContain("Stage.Height = (double)e.NewValue", stage);
	}

	[Fact]
	public void ALibraryKeptWhole_PutsBacksPlaceBack_OnItsLoaded()
	{
		// back-gate7 (Drive-in): the library page kept whole heard its view model only from its Loaded, and Back's place was asked
		// before it: the scroll kept, the keyboard on the top bar's Back
		Assert.Contains("if (_focusOnLoad || _vm.FocusRequest >= 0 || _vm.ReturnOffset is not null)", Code("Controls", "LibraryGridHost.cs"));
		Assert.Contains("if (_focusOnLoad || ViewModel.FocusRequest >= 0 || ViewModel.ReturnOffset is not null)", Code("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs"));
	}

	[Fact]
	public void DriveInHome_KeptWhole_DoesNotMakeItsScreenAgain_ForTheSameSlides()
	{
		// back-gate7: Drive-in's Home kept whole for Back made its outdoor screen's slides again on every Loaded
		var dto = new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = Guid.NewGuid(), Name = "A" };
		Core.ViewModels.HeroSlide Slide(string title) => new(dto, title, "", "", false, Guid.Empty, "", null, null);
		IReadOnlyList<Core.ViewModels.HeroSlide> shown = [Slide("A")];
		Assert.True(Themes.DriveIn.Views.DriveInHomePage.SameSlides(shown, [Slide("A")]));
		Assert.False(Themes.DriveIn.Views.DriveInHomePage.SameSlides(shown, [Slide("B")]));
		Assert.False(Themes.DriveIn.Views.DriveInHomePage.SameSlides(null, [Slide("A")]));
		Assert.Contains("if (!SameSlides(Screen.Slides, features))", Code("Themes", "DriveIn", "Views", "DriveInHomePage.xaml.cs"));
	}

	[Fact]
	public void TwoPagesOfOneKind_NeverShareThePageTheFrameKeeps()
	{
		// the re-review's Blocker: WinUI keeps one page per type, the shown one included. Movies, the Shows tab, Back: the frame
		// gave Movies' entry the Shows page (titled Movies, Shows' cards, its load stopped)
		var movies = new object();
		Assert.True(Services.NavigationService.UsesKeptWhole(typeof(Views.MoviePage), typeof(Views.LibraryPage), movies, movies)); // a film, Back to its library
		Assert.False(Services.NavigationService.UsesKeptWhole(typeof(Views.LibraryPage), typeof(Views.LibraryPage), movies, movies)); // Shows, Back to Movies
		Assert.False(Services.NavigationService.UsesKeptWhole(typeof(Views.MoviePage), typeof(Views.LibraryPage), new object(), movies)); // another library's page
		Assert.False(Services.NavigationService.UsesKeptWhole(typeof(Views.MoviePage), typeof(Views.LibraryPage), null, movies));
		var nav = Code("Services", "NavigationService.cs");
		Assert.Contains("shown.NavigationCacheMode = NavigationCacheMode.Disabled;", nav); // only that kind's page goes: Home stays
		Assert.Contains("Page { NavigationCacheMode: not NavigationCacheMode.Disabled } page", nav); // a page just dropped is never noted
	}
}