using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Controls;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using FluentFin.Core.Shortcuts;
using FluentFin.Core.ViewModels;
using FluentFin.UI.Core.Contracts.Services;
using FluentFin.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Windows.System;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail): the tab keys, where a page starts (below the bar, or under it for a hero),
/// the narrow-window fold, when the tabs show, and Settings and Home by their keys. The tabs themselves: TopNavigationTests;
/// the running app (Narrator's tab list, Tab, the arrows, the drag regions): tools\ui-harness\offscreen\then-topbar.ps1.
/// </summary>
public sealed class TopBarTests
{
	[Theory]
	[InlineData(VirtualKey.Right, 0, 6, 1)]
	[InlineData(VirtualKey.Right, 5, 6, 0)]
	[InlineData(VirtualKey.Left, 0, 6, 5)]
	[InlineData(VirtualKey.Left, 3, 6, 2)]
	[InlineData(VirtualKey.Home, 4, 6, 0)]
	[InlineData(VirtualKey.End, 1, 6, 5)]
	public void LeftAndRight_MoveBetweenTheTabs_Round(VirtualKey key, int at, int count, int expected)
	{
		Assert.Equal(expected, TabKeys.Next(key, at, count));
	}

	[Theory]
	[InlineData(VirtualKey.Up)]
	[InlineData(VirtualKey.Down)]
	[InlineData(VirtualKey.Tab)]
	[InlineData(VirtualKey.Enter)]
	public void OtherKeys_AreNotTheTabs(VirtualKey key)
	{
		// Tab leaves the tab list (one Tab stop); Enter and Space open the tab (the button's own)
		Assert.Null(TabKeys.Next(key, 2, 6));
	}

	[Fact]
	public void APage_StartsBelowTheBar_UnlessItExtendsUnderIt_OrTheBarIsHidden()
	{
		Assert.Equal(TopBar.Height, TopBar.TopInset(barVisible: true, extendsUnder: false));
		Assert.Equal(0, TopBar.TopInset(barVisible: true, extendsUnder: true));
		// full screen: no bar, the page (the player) fills the window
		Assert.Equal(0, TopBar.TopInset(barVisible: false, extendsUnder: false));
	}

	// ---- review of 590eaf6: the bar folds when its contents do not fit, not at a fixed 1100 px

	private const double Caption = 138, Back = 48, Wordmark = 120, Account = 32;

	/// <summary>Home, Movies, Television Shows, Documentaries, Anime, Diary at 15 px SemiBold, with padding: the review's case.</summary>
	private const double LongTabs = 12 + 66 + 76 + 152 + 132 + 72 + 66;

	private const double LongMenu = 46 + 150;

	/// <summary>What the bar takes at a width: every part shown, the drag gap, the caption buttons.</summary>
	private static double Taken(TopBarFit fit, bool search, bool searchOpen) =>
		Back + (fit.ShowWordmark ? Wordmark : 0) + (fit.Compact ? (!fit.ShowSections ? 0 : fit.ShowSectionsLabel ? LongMenu : TopBar.MenuGlyphWidth) : LongTabs)
		+ (!search ? 0 : !fit.Compact ? fit.SearchWidth + TopBar.Spacing : searchOpen ? fit.SearchWidth + TopBar.Spacing : TopBar.SearchIconWidth + TopBar.Spacing)
		+ Account + TopBar.Spacing + TopBar.MinDragGap + Caption;

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AtEveryWidth_NothingSlidesUnderTheCaptionButtons_AndTheBarStillDrags(bool searchOpen)
	{
		// the window's minimum is 500 px
		for (var width = 500; width <= 2560; width += 10)
		{
			var fit = TopBar.Fit(width, Caption, Back, Wordmark, LongTabs, LongMenu, Account, search: true, searchOpen: searchOpen);

			Assert.True(Taken(fit, true, searchOpen) <= width, $"{width} px (search open: {searchOpen}): {Taken(fit, true, searchOpen)} px taken, {fit}");
		}
	}

	[Theory]
	// the review's widths: 1120 with a long library name (the account was under Minimize), 600 and 520 with search open
	[InlineData(1120, false, true, true, true)]
	[InlineData(1920, false, false, true, true)]
	[InlineData(600, true, true, false, false)]
	[InlineData(520, true, true, false, false)]
	public void TheReviewsWidths(double width, bool searchOpen, bool compact, bool wordmark, bool sectionsLabel)
	{
		var fit = TopBar.Fit(width, Caption, Back, Wordmark, LongTabs, LongMenu, Account, search: true, searchOpen);

		Assert.Equal(compact, fit.Compact);
		Assert.Equal(wordmark, fit.ShowWordmark);
		// the sections stay reachable: the menu button with its label, or its glyph alone
		Assert.True(fit.ShowSections);
		Assert.Equal(sectionsLabel, fit.ShowSectionsLabel);
		Assert.True(Taken(fit, true, searchOpen) <= width);
		if (searchOpen)
		{
			Assert.InRange(fit.SearchWidth, TopBar.MinOpenSearchWidth, TopBar.MaxOpenSearchWidth);
		}
	}

	[Fact]
	public void AWideWindow_GivesTheSearchBoxTheRoomLeft()
	{
		var wide = TopBar.Fit(2560, Caption, Back, Wordmark, LongTabs, LongMenu, Account, search: true, searchOpen: false);
		var middle = TopBar.Fit(1300, Caption, Back, Wordmark, LongTabs, LongMenu, Account, search: true, searchOpen: false);

		Assert.Equal(TopBar.MaxSearchWidth, wide.SearchWidth);
		Assert.False(middle.Compact);
		Assert.InRange(middle.SearchWidth, TopBar.MinSearchWidth, TopBar.MaxSearchWidth - 1);
		// short names fit where long ones fold
		Assert.False(TopBar.Fit(1120, Caption, Back, Wordmark, 12 + 6 * 80, LongMenu, Account, search: true, searchOpen: false).Compact);
	}

	// ---- Nixie 1.4.0 (the review's X6): the libraries fold by width, in three steps

	/// <summary>Home and Diary with the strip's margin; nine library tabs of 90 px; "Libraries" 96, folded 112.</summary>
	private static TopRowWidths Row(int libraries) => new(12 + 66 + 66, Enumerable.Repeat(90.0, libraries).ToList(), 96, 112);

	[Theory]
	// the user's bar: about 2000 px, five or six views; the review's 1920 and 2560; 1440 and 1100
	[InlineData(2000, 6, 6)]
	[InlineData(2560, 9, 9)]
	[InlineData(1920, 9, 9)]
	[InlineData(1440, 9, 6)]
	[InlineData(1440, 5, 5)]
	[InlineData(1100, 5, 2)]
	[InlineData(1100, 9, 2)]
	public void TheLibraries_FoldByWidth_FromTheEnd_LeavingTheSearchBoxItsRoom(double width, int libraries, int shown)
	{
		var fit = TopBar.Fit(width, Caption, Back, Wordmark, Row(libraries), LongMenu, Account, search: true, searchOpen: false);

		Assert.False(fit.Compact);
		Assert.Equal(shown, fit.ShownLibraries);
		Assert.InRange(fit.SearchWidth, TopBar.MinSearchWidth, TopBar.MaxSearchWidth);
		var row = Row(libraries);
		var taken = Back + Wordmark + row.Fixed + row.Libraries.Take(shown).Sum() + (shown < libraries ? row.Folded : row.Plain)
			+ fit.SearchWidth + TopBar.Spacing + Account + TopBar.Spacing + TopBar.MinDragGap + Caption;
		Assert.True(taken <= width, $"{taken} px taken at {width}");
	}

	[Fact]
	public void TheSectionsMenu_OnlyWhenHomeLibrariesAndSearchDoNotFit()
	{
		var row = Row(9);
		// Home, "Libraries ▾", Diary and the smallest search box, just: still tabs, every library folded
		var just = Back + Wordmark + row.Fixed + row.Folded + TopBar.MinSearchWidth + TopBar.Spacing + Account + TopBar.Spacing + TopBar.MinDragGap + Caption;
		var fit = TopBar.Fit(just, Caption, Back, Wordmark, row, LongMenu, Account, search: true, searchOpen: false);
		Assert.False(fit.Compact);
		Assert.Equal(0, fit.ShownLibraries);

		Assert.True(TopBar.Fit(just - 1, Caption, Back, Wordmark, row, LongMenu, Account, search: true, searchOpen: false).Compact);
	}

	[Fact]
	public void TheFold_NeverShowsMoreLibrariesInANarrowerWindow()
	{
		var previous = int.MaxValue;
		for (var width = 2560; width >= 500; width -= 10)
		{
			var fit = TopBar.Fit(width, Caption, Back, Wordmark, Row(9), LongMenu, Account, search: true, searchOpen: false);
			var shown = fit.Compact ? -1 : fit.ShownLibraries;
			Assert.True(shown <= previous, $"{width} px: {shown} after {previous}");
			previous = shown;
		}
	}

	[Theory]
	// room: centred in the window; a long row of tabs: pinned beside the account
	[InlineData(1920, 600, 360, 1720, 1720 - (1920 + 360) / 2)]
	[InlineData(1920, 1100, 360, 1720, 0)]
	[InlineData(1100, 500, 300, 900, 0)]
	public void TheSearchBox_IsCentredWhenThereIsRoom_ElsePinnedToTheAccount(double window, double leftEnd, double search, double right, double offset)
	{
		Assert.Equal(offset, TopBar.SearchOffset(window, leftEnd, search, right));
	}

	// ---- Nixie 1.4.0, item 6 (the review's X14): the bar's geometry

	[Fact]
	public void Back_Is40By32_16PxFromTheEdge_CentredOnTheBarsLine()
	{
		// the TitleBar lays Back at its own left edge (measured live: then-topbar140.ps1)
		Assert.Equal(16, TitleBarControl.BackMargin.Left);
		Assert.Equal(32, TopBar.Height - TitleBarControl.BackMargin.Top - TitleBarControl.BackMargin.Bottom);
		Assert.Equal(TopBar.Height / 2, TitleBarControl.BackMargin.Top + 16);
	}

	[Theory]
	[InlineData(1920)]
	[InlineData(1100)]
	public void DriveInsSparkles_StayClearOfTheBar(double width)
	{
		// the review's DI-m1: a 30 px sparkle sat in the bar between the tabs and search, another beside Back at 1100
		foreach (var (_, y, size) in FluentFin.Themes.DriveIn.Controls.DriveInScene.Sparkles(FluentFin.Themes.DriveIn.Controls.DriveInLayout.Home(width, compact: false)))
		{
			Assert.True(y - size >= TopBar.Height + 8, $"a sparkle at y {y}, {size} px, reaches {y - size}");
		}

		Assert.True(FluentFin.Themes.DriveIn.Controls.DriveInScene.IsUnderTheBar(60, 14));
		Assert.False(FluentFin.Themes.DriveIn.Controls.DriveInScene.IsUnderTheBar(80, 14));
	}

	[Fact]
	public void TheBar_TallCaptions_NoTileOnADeadBack_NoTabStopOnTheTitleBar_AndCinematicsIslandCentred()
	{
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;
		var code = File.ReadAllText(Path.Combine(root, "Views", "TitleBarControl.xaml.cs"));
		// the caption buttons 48 px tall: their glyphs on y 24 with the bar's
		Assert.Contains("App.MainWindow.AppWindow.TitleBar.PreferredHeightOption = CaptionHeightFor(ViewModel.IsPlayerPageShown);", code);
		Assert.Equal(Microsoft.UI.Windowing.TitleBarHeightOption.Tall, TitleBarControl.CaptionHeightFor(playerPage: false));
		// the player's own top bar is laid out under the standard caption buttons (its stream's)
		Assert.Equal(Microsoft.UI.Windowing.TitleBarHeightOption.Standard, TitleBarControl.CaptionHeightFor(playerPage: true));
		var bar = System.Xml.Linq.XDocument.Parse(File.ReadAllText(Path.Combine(root, "Views", "TitleBarControl.xaml")));
		var titleBar = bar.Descendants().Single(e => e.Name.LocalName == "TitleBar" && (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "TitleBar");
		// item 4: no Tab stop with no name and no ring
		Assert.Equal("False", (string?)titleBar.Attribute("IsTabStop"));
		// a disabled Back draws no tile, in every theme dictionary
		var disabled = titleBar.Descendants().Where(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "TitleBarBackButtonBackgroundDisabled").ToList();
		Assert.Equal(3, disabled.Count);
		Assert.All(disabled, e => Assert.True((string?)e.Attribute("ResourceKey") == "SubtleFillColorTransparentBrush" || (string?)e.Attribute("Color") == "Transparent"));
		// the wordmark goes Home; search names its keys
		Assert.Contains("_top.Open(home);", code[code.IndexOf("private void OnWordmarkClick", StringComparison.Ordinal)..]);
		var xaml = File.ReadAllText(Path.Combine(root, "Views", "TitleBarControl.xaml"));
		Assert.Equal(2, xaml.Split("ToolTipService.ToolTip=\"Search (Ctrl+F or /)\"").Length - 1);

		var cinematic = System.Xml.Linq.XDocument.Parse(File.ReadAllText(Path.Combine(root, "Themes", "Cinematic", "Cinematic.xaml")));
		var island = cinematic.Descendants().Single(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "Island");
		var margin = ((string)island.Attribute("Margin")!).Split(',').Select(double.Parse).ToArray();
		// y 2 to 46: centred on 24, as the bar's items and the caption buttons are
		Assert.Equal(TopBar.Height / 2, (margin[1] + TopBar.Height - margin[3]) / 2);
		var style = cinematic.Descendants().Single(e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "TopBarStyle");
		Assert.Contains(style.Elements(), e => (string?)e.Attribute("Property") == "Padding" && (string?)e.Attribute("Value") == "0");
		Assert.Contains(cinematic.Descendants(), e => (string?)e.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == "TopBarMenuPresenterStyle");
	}

	[Theory]
	// the item pages' headers in every theme: Classic's hero, Drive-in's board, Cinematic's stage, Editorial's header
	[InlineData("Controls/ItemHero.xaml")]
	[InlineData("Themes/DriveIn/Controls/ItemBoard.xaml")]
	[InlineData("Themes/Cinematic/Controls/CinematicItemStage.xaml")]
	[InlineData("Themes/Editorial/Controls/EditorialItemHeader.xaml")]
	public void EveryItemHeader_HostsTheEyebrow_BeforeItsTitle(string header)
	{
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;
		var xaml = File.ReadAllText(Path.Combine(root, header));
		var eyebrow = xaml.IndexOf("TrailEyebrow", StringComparison.Ordinal);
		Assert.True(eyebrow > 0, $"{header}: no TrailEyebrow");
		Assert.True(eyebrow < xaml.IndexOf("HeadingLevel=\"Level1\"", StringComparison.Ordinal) || eyebrow < xaml.IndexOf("TitleArt", StringComparison.Ordinal), $"{header}: the eyebrow after the title");
	}

	[Fact]
	public void AGapMeasuredShort_KeepsMoreRoom_UpToALimit_AndNeverLess()
	{
		// live at 520 px, search open: the parts measured 8 px short, the gap left was 40 px
		Assert.Equal(8, TopBar.Slack(0, 40));
		Assert.Equal(8, TopBar.Slack(8, 48));
		Assert.Equal(8, TopBar.Slack(8, 300));
		Assert.Equal(TopBar.MaxSlack, TopBar.Slack(60, 0));
		// with it, the bar folds sooner (the reserved room counts as the caption buttons')
		var tight = TopBar.Fit(520, Caption, Back, Wordmark, LongTabs, LongMenu, Account, search: true, searchOpen: true);
		var kept = TopBar.Fit(520, Caption + 8, Back, Wordmark, LongTabs, LongMenu, Account, search: true, searchOpen: true);
		Assert.True(kept.SearchWidth <= tight.SearchWidth - 8 || !kept.ShowSections || kept.SearchWidth == TopBar.MinOpenSearchWidth);
	}

	[Theory]
	[InlineData(true, true, 0)]
	[InlineData(false, true, TopBar.Height)]
	[InlineData(false, false, 0)]
	public void APageBeforeSignIn_StartsBelowTheBar_UnlessTheBarIsHidden(bool shell, bool barVisible, double inset)
	{
		// review: the self-test player in full screen kept a 48 px band
		Assert.Equal(inset, MainWindow.RootTopInset(shell, barVisible));
	}

	[Fact]
	public void TheTabs_ShowSignedIn_AndNotOnThePlayerPage()
	{
		var backend = new Mock<IMpvBackend>();
		backend.Setup(x => x.Events).Returns(Observable.Never<BridgeEvent>());
		backend.Setup(x => x.PlaybackInterrupted).Returns(Observable.Never<MpvInterruptedPlayback>());
		var client = new Mock<IJellyfinClient>();
		var playback = new MpvPlaybackService(backend.Object, client.Object, NullLogger<MpvPlaybackService>.Instance);
		var vm = new TitleBarViewModel(Mock.Of<INavigationService>(), Mock.Of<INavigationService>(), Mock.Of<INavigationViewService>(), client.Object, playback);
		var changed = new List<string?>();
		vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		Assert.False(vm.IsNavigationShown);
		vm.User = new UserDto { Name = "alice" };
		Assert.True(vm.IsNavigationShown);
		Assert.Contains(nameof(TitleBarViewModel.IsNavigationShown), changed);

		vm.OnPageShown(typeof(VideoPlayerPage));
		Assert.False(vm.IsNavigationShown);
		vm.OnPageShown(typeof(HomePage));
		Assert.True(vm.IsNavigationShown);

		vm.User = null;
		Assert.False(vm.IsNavigationShown);
	}

	[Fact]
	public void CtrlComma_OpensSettings_AndAltHome_GoesHome()
	{
		// Settings has no tab and no gear: the Account menu (TitleBarAccountTests) and Ctrl+,
		Assert.Equal(AppShortcut.OpenSettings, AppShortcuts.For(AppShortcuts.Keys.Comma, control: true, shift: false, alt: false, inTextInput: false));
		Assert.Equal(AppShortcut.GoHome, AppShortcuts.For(AppShortcuts.Keys.Home, control: false, shift: false, alt: true, inTextInput: false));
	}

	[Fact]
	public void TheClickableAreas_AreInTheWindowsPhysicalPixels()
	{
		// the bar's headers in view pixels, at 150 %: the regions Windows hit-tests are physical pixels
		var r = TitleBarControl.PassthroughRect(new Windows.Foundation.Rect(210.4, 4, 73, 40), 1.5);
		Assert.Equal((316, 6, 110, 60), (r.X, r.Y, r.Width, r.Height));
	}

	[Fact]
	public void TheBar_IsTheWindowsTitleBar_AndCtrlCommaReachesSettings()
	{
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;
		// without it Windows' default caption strip took the bar's top 32 px: a click on a tab dragged the window (the live
		// check: then-topbar.ps1's hit tests)
		var bar = File.ReadAllText(Path.Combine(root, "Views", "TitleBarControl.xaml.cs"));
		Assert.Contains("App.MainWindow.SetTitleBar(TitleBar);", bar);
		// and its clickable areas follow the headers (the TitleBar looks again only when the window is resized)
		Assert.Contains(".SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [.. rects]);", bar);
		Assert.Contains("foreach (var element in new FrameworkElement?[] { BackButton(), LeftHeaderPanel, SearchHost, SignOutPanel, AccountButton })", bar);
		var keys = File.ReadAllText(Path.Combine(root, "Services", "WindowShortcuts.cs"));
		var settings = keys[keys.IndexOf("case AppShortcut.OpenSettings:", StringComparison.Ordinal)..];
		Assert.StartsWith("case AppShortcut.OpenSettings:", settings);
		Assert.Contains("NavigateTo<SettingsViewModel>()", settings[..settings.IndexOf("case AppShortcut.GoHome", StringComparison.Ordinal)]);
	}

	[Fact]
	public void TheShell_RereadsTheLibraries_AndKeepsEachPagesTab_AndReturnsToTheTop()
	{
		var root = Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;
		var shell = File.ReadAllText(Path.Combine(root, "Views", "ShellPage.xaml.cs"));
		// review 3: read again when the client drops its list (the server's LibraryChanged: TopNavigationTests, ClientRefusalTests)
		Assert.Contains("_client.UserViewsChanged += OnUserViewsChanged;", shell);
		Assert.Contains("Unloaded += (_, _) => _client.UserViewsChanged -= OnUserViewsChanged;", shell);
		// review 4: the page's place in the history, and whether it is returned to
		Assert.Contains("_top.OnPageShown(KeyOf(e.SourcePageType), e.Parameter, NavigationFrame.BackStackDepth,", shell);
		Assert.Contains("returning: e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward);", shell);

		// Nixie 1.4.0 (the trail rule): the lit destination chosen again on its own page goes back to its top
		Assert.Contains("_top.ReturnToTopRequested += OnReturnToTop;", shell);
		Assert.Contains("navigation.ReloadForRefresh(_ => true, keepPlace: false);", shell);
		// Nixie 1.4.0 (integration): the library keeps its place on R only, not on the lit tab chosen again
		foreach (var page in new[] { Path.Combine("Controls", "LibraryGridHost.cs"), Path.Combine("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs") })
		{
			Assert.Contains("{ LastNavigationWasRefresh: true, LastRefreshKeepsPlace: true }", File.ReadAllText(Path.Combine(root, page)));
		}

		var strip = File.ReadAllText(Path.Combine(root, "Controls", "TopTabStrip.xaml.cs"));
		// review 6: a second load listens again
		Assert.Contains("if (_attached && ReferenceEquals(_navigation, navigation))", strip);
		// and the Libraries page's wording; Nixie 1.4.0: its dead pin menu is gone (every library is a tab)
		var landing = File.ReadAllText(Path.Combine(root, "Views", "LibrariesLandingPage.xaml"));
		Assert.DoesNotContain("Add to the top bar", landing);
		Assert.DoesNotContain("side bar", landing, StringComparison.OrdinalIgnoreCase);
		// Nixie 1.4.0: the pins are gone (every library is a tab); the menus check the lit destination and hand the keyboard to the page
		Assert.DoesNotContain("Unpin", strip);
		Assert.Contains("new RadioMenuFlyoutItem { Text = entry.Title, GroupName = \"TopDestinations\", IsChecked = _navigation?.Lit is { } lit && entry.Mark == lit };", strip);
		Assert.Contains("HandFocusToPage();", strip);
		// and the search box's list lights the search box
		var search = File.ReadAllText(Path.Combine(root, "Behaviors", "GlobalSearchBoxBehavior.cs"));
		Assert.Contains("App.GetService<Core.ViewModels.TopNavigation>().ChooseSearch();", search);
	}

	[Fact]
	public void ThePluginsSectionsAndThePins_GoToTheTabs()
	{
		// the shell's navigation-item service is the tabs' (no rail to add them to): the Diary, a pinned library
		var app = File.ReadAllText(Path.Combine(Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!, "App.xaml.cs"));
		Assert.Contains("services.AddSingleton<INavigationViewServiceCore>(sp => sp.GetRequiredService<TopNavigation>());", app);
		Assert.DoesNotContain("services.AddSingleton<INavigationViewServiceCore>(sp => sp.GetRequiredService<INavigationViewService>());", app);
	}
}
