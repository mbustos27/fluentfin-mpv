using System.Xml.Linq;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4: the Home rows' names and headers, the Continue Watching notice and the Library's bar, read from the XAML
/// (no WinUI needed); what they do at run time is the harness's (then-rows, then-library).
/// </summary>
public sealed class HomeAndLibraryXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	[Theory]
	[InlineData("Movies", "New in Movies")]
	[InlineData(" Shows ", "New in Shows")]
	[InlineData(null, "New")]
	public void ALibrarysRow_IsNewInIt(string? library, string title)
	{
		Assert.Equal(title, HomeRowTitles.NewIn(library));
		Assert.Equal(title, new NamedQueryResult(new BaseItemDto { Name = library }).Title);
	}

	[Fact]
	public void HomesLibraryRows_AreNamedByTheirTitle_NotRecentlyAddedIn()
	{
		var text = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/HomePage.xaml"));
		Assert.DoesNotContain("Recently Added In", text);

		var rows = Load("FluentFin/Views/HomePage.xaml").Descendants().Where(e => e.Name.LocalName == "ShelfRow").ToList();
		Assert.Contains(rows, r => (string?)r.Attribute("Header") == "{x:Bind Title}");
	}

	[Fact]
	public void ARowsHeaderWithSeeAll_IsOneLink_ItsTitleStillAHeading()
	{
		// 1.4.0 (the library UX review, X13): one See all in every theme, the Link role at the header's right (it was the title
		// as a 179 x 40 button, Drive-in's 56 px marquee tile, Editorial's link and Cinematic's 40-high variant)
		var doc = Load("FluentFin/Controls/ShelfRow.xaml");
		var link = FindByName(doc, "SeeAllButton");

		Assert.Equal(Presentation + "HyperlinkButton", link.Name);
		Assert.Equal("{StaticResource NixieLinkButtonStyle}", (string?)link.Attribute("Style"));
		Assert.Equal("See all", (string?)link.Attribute("Content"));
		Assert.Equal("1", (string?)link.Attribute("Grid.Column"));
		var title = FindByName(doc, "TitleText");
		Assert.Equal("Level2", (string?)title.Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("{x:Bind Header, Mode=OneWay}", (string?)title.Attribute("Text"));
		Assert.Single(doc.Descendants(), e => (string?)e.Attribute("Content") == "See all"); // no second See all

		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/ShelfRow.xaml.cs"));
		Assert.Contains("AutomationProperties.SetName(SeeAllButton, RowPaging.SeeAllName(Header))", code);
		// the title is never hidden by the row (a theme may draw its own in its place, and that must not be undone)
		Assert.DoesNotContain("TitleText.Visibility", code);
		// Drive-in's marquee is the title, never the See all
		var driveIn = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Themes/DriveIn/Controls/DriveInRows.cs"));
		Assert.DoesNotContain("seeAll.Content", driveIn);
	}

	[Fact]
	public void TheContinueWatchingNotice_HasAnUndo_ForTheKeyboardAndNarratorToo()
	{
		var home = Load("FluentFin/Views/HomePage.xaml");
		// shown from code: an x:Bind function of the notice was not called again when it went (null), and an empty panel
		// with its Undo stayed on screen after Undo (seen in the 1.1.4 screenshots)
		Assert.Equal("Collapsed", (string?)FindByName(home, "NoticePanel").Attribute("Visibility"));
		Assert.Equal("Collapsed", (string?)FindByName(home, "UndoButton").Attribute("Visibility"));
		Assert.Null(FindByName(home, "NoticeText").Attribute("Text"));
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/HomePage.xaml.cs"));
		Assert.Contains("NoticePanel.Visibility = NoticeVisibility(notice);", code);
		Assert.Contains("ShowNotice(ViewModel.Notice);", code[code.IndexOf("private void OnNoticeChanged()", StringComparison.Ordinal)..]);
		var undo = FindByName(home, "UndoButton");
		Assert.Equal("Undo", (string?)undo.Attribute("AutomationProperties.Name"));
		Assert.Contains("Ctrl+Z", (string?)undo.Attribute("ToolTipService.ToolTip"));
		Assert.Equal("Polite", (string?)FindByName(home, "NoticeText").Attribute("AutomationProperties.LiveSetting"));
		Assert.Contains(home.Descendants(Presentation + "KeyboardAccelerator"),
			k => (string?)k.Attribute("Key") == "Z" && (string?)k.Attribute("Modifiers") == "Control" && (string?)k.Attribute("Invoked") == "UndoInvoked");
	}

	[Fact]
	public void TheLibrarysBar_SaysTheSortTheFiltersAndTheCount()
	{
		// Nixie 1.4.0: the bar is the shared toolbar (every theme's), its menus built from LibraryBrowse.SortKeys
		var library = Load("FluentFin/Views/LibraryPage.xaml");
		var bar = Load("FluentFin/Controls/LibraryToolbar.xaml");

		Assert.Equal("LibraryToolbar", FindByName(library, "SortFilterBar").Name.LocalName);
		Assert.Equal("{x:Bind ViewModel.SortButtonName, Mode=OneWay}", (string?)FindByName(bar, "SortButton").Attribute("AutomationProperties.Name"));
		Assert.Contains(FindByName(bar, "SortButton").Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "{x:Bind ViewModel.SortText, Mode=OneWay}");
		Assert.Equal("{x:Bind ViewModel.FilterButtonName, Mode=OneWay}", (string?)FindByName(bar, "FilterButton").Attribute("AutomationProperties.Name"));
		Assert.Contains(FindByName(bar, "FilterButton").Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "{x:Bind ViewModel.FilterText, Mode=OneWay}");
		Assert.Equal("{x:Bind ViewModel.LibraryCountText, Mode=OneWay}", (string?)FindByName(library, "CountText").Attribute("Text")); // 1.4.0: the row says what is shown
		// the stable toolbar (item 8): Filter keeps its width when it says "Filter (2)"
		Assert.True(double.Parse((string)FindByName(bar, "FilterButton").Attribute("MinWidth")!) >= 100);
		// the one-click Unwatched (item 2), before Sort
		var unwatched = FindByName(bar, "UnwatchedToggle");
		Assert.Equal("{x:Bind ViewModel.Filter.Unwatched, Mode=TwoWay}", (string?)unwatched.Attribute("IsChecked"));
		Assert.Contains(FindByName(bar, "SortButton"), unwatched.ElementsAfterSelf());
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/LibraryToolbar.xaml.cs"));
		Assert.Contains("foreach (var key in LibraryBrowse.SortKeys)", code);
		Assert.Contains("LibraryBrowse.DirectionWords(vm.SortBy, order)", code);
	}

	// layout audit, item 13: the sort menu in sentence case, the same words as the Sort button
	[Fact]
	public void TheSortMenu_SaysWhatTheButtonSays_InSentenceCase()
	{
		Assert.Equal(8, LibraryBrowse.SortKeys.Count);
		foreach (var key in LibraryBrowse.SortKeys)
		{
			var text = LibraryBrowse.SortName(key);
			Assert.Equal(text[..1] + text[1..].ToLowerInvariant(), text);
			Assert.StartsWith(text, LibraryBrowse.SortText(key, LibraryBrowse.NaturalOrder(key)));
		}
	}

	// interaction audit, item 12: the direction is one click, beside Sort; Filter is lists of check boxes with Clear filters
	[Fact]
	public void TheDirection_IsItsOwnButton_AndFilter_IsCheckBoxLists()
	{
		var bar = Load("FluentFin/Controls/LibraryToolbar.xaml");

		// Nixie 1.4.0 (item 8): the direction is one click again, its name in words ("A to Z, switch to Z to A")
		Assert.Equal("{x:Bind ViewModel.SortOrderButtonName, Mode=OneWay}", (string?)FindByName(bar, "DirectionButton").Attribute("AutomationProperties.Name"));

		var filter = FindByName(bar, "FilterButton");
		Assert.Empty(filter.Descendants().Where(e => e.Name.LocalName == "TokenizingTextBox"));
		// the review, item 5: one virtualised list (an ItemsRepeater in the menu's scroller), not nested ItemsControls
		Assert.Empty(filter.Descendants().Where(e => e.Name.LocalName == "ItemsControl"));
		var rows = FindByName(bar, "FilterRepeater");
		Assert.Equal("ItemsRepeater", rows.Name.LocalName);
		Assert.Equal("{x:Bind ViewModel.FilterRows, Mode=OneWay}", (string?)rows.Attribute("ItemsSource"));
		Assert.Contains(rows.Ancestors(), e => e.Name.LocalName == "ScrollViewer");
		var boxes = filter.Descendants(Presentation + "CheckBox").ToList();
		Assert.Equal(["UnwatchedBox", "FavoritesBox"], boxes.Take(2).Select(b => (string?)b.Attribute(Xaml + "Name")));
		Assert.Equal("{x:Bind ViewModel.Filter.Unwatched, Mode=TwoWay}", (string?)boxes[0].Attribute("IsChecked"));
		var box = bar.Descendants(Presentation + "DataTemplate").Single(t => (string?)t.Attribute(Xaml + "Key") == "FilterOptionTemplate")
			.Descendants(Presentation + "CheckBox").Single();
		Assert.Equal("{x:Bind IsChecked, Mode=TwoWay}", (string?)box.Attribute("IsChecked"));
		Assert.Equal("{x:Bind Name, Mode=OneWay}", (string?)box.Attribute("AutomationProperties.Name"));
		// Nixie 1.4.0 (item 2): a value that would leave nothing is greyed, its count follows the other filters
		Assert.Equal("{x:Bind IsAvailable, Mode=OneWay}", (string?)box.Attribute("IsEnabled"));
		Assert.Equal("{x:Bind Label, Mode=OneWay}", (string?)box.Attribute("Content"));
		Assert.Equal("{x:Bind ViewModel.ClearFiltersCommand}", (string?)FindByName(bar, "ClearFiltersButton").Attribute("Command"));
	}

	[Fact]
	public void TheAToZJump_IsOneTabStop_ANamedList_OfNamedButtons()
	{
		var bar = Load("FluentFin/Controls/LetterBar.xaml");
		var list = FindByName(bar, "LetterList");

		Assert.Equal("RowList", list.Name.LocalName);
		Assert.Equal("Jump to a letter", (string?)list.Attribute("AutomationProperties.Name"));
		Assert.Equal("Once", (string?)list.Attribute("TabFocusNavigation"));
		Assert.Equal("Enabled", (string?)list.Attribute("XYFocusKeyboardNavigation"));
		var letter = list.Descendants(Presentation + "Button").Single();
		Assert.Equal("{x:Bind Name}", (string?)letter.Attribute("AutomationProperties.Name"));
		// Nixie 1.4.0 (item 13): a letter with no titles rests: disabled (not a stop) and silent to Narrator; the mark is the tile's
		Assert.Equal("{x:Bind HasTitles}", (string?)letter.Attribute("IsEnabled"));
		Assert.Equal("{x:Bind local:LetterBar.ViewFor(HasTitles)}", (string?)letter.Attribute("AutomationProperties.AccessibilityView"));
		Assert.Equal("{x:Bind Letter}", (string?)letter.Attribute("Content")); // the letter role (NixieLetterButtonStyle) draws it
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/LetterBar.xaml.cs"));
		Assert.Contains("VisualStateManager.GoToState(button, tile.IsCurrent ? \"Selected\" : \"Unselected\", false);", code);
		Assert.Contains("(Style)Application.Current.Resources[\"NixieLetterButtonStyle\"]", code);
		Assert.Contains("button.Height = Math.Max(TileHeight, 32);", code); // WCAG 2.5.8, and the review's 32
		Assert.Contains("RowGrid.MinHeight = Math.Max(TileHeight, 32);", code); // the row's height kept, letters or directions
	}

	// ---- layout audit, items 2, 6 and 22

	[Fact]
	public void ARowsArrows_AreChevrons_NotPlayTriangles()
	{
		var doc = Load("FluentFin/Controls/ShelfRow.xaml");

		Assert.Equal("", (string?)FindByName(doc, "ScrollLeftBtn").Descendants(Presentation + "FontIcon").Single().Attribute("Glyph"));
		Assert.Equal("", (string?)FindByName(doc, "ScrollRightBtn").Descendants(Presentation + "FontIcon").Single().Attribute("Glyph"));
		Assert.DoesNotContain(doc.Descendants(Presentation + "FontIcon"), i => (string?)i.Attribute("Glyph") is "" or "");
		// still a real target (WCAG 2.5.8: at least 24 px); quiet is half strength, never hidden. The keyboard moves the row
		// with its own keys (Left, Right, Home, End), as in 1.1.3: the arrows are pointer targets
		foreach (var arrow in new[] { FindByName(doc, "ScrollLeftBtn"), FindByName(doc, "ScrollRightBtn") })
		{
			Assert.True(double.Parse((string)arrow.Attribute("Width")!) >= 24 && double.Parse((string)arrow.Attribute("Height")!) >= 24);
		}
		Assert.Contains("ScrollLeftBtn.Opacity = ScrollRightBtn.Opacity = RowPaging.ArrowOpacity(_pointerOver, _focusInside)",
			File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/ShelfRow.xaml.cs")));
	}

	[Fact]
	public void TheLibrary_ScrollsOnWithoutAPager_AndItsBarGoesWhenThereIsNothingToShow()
	{
		var library = Load("FluentFin/Views/LibraryPage.xaml");

		Assert.DoesNotContain(library.Descendants(), e => e.Name.LocalName == "PagerControl");
		Assert.Equal("{x:Bind local:LibraryPage.BarVisibility(ViewModel.State.Status), Mode=OneWay}", (string?)FindByName(library, "SortFilterBar").Attribute("Visibility"));
		Assert.Equal("{x:Bind local:LibraryPage.BarVisibility(ViewModel.State.Status), Mode=OneWay}", (string?)FindByName(library, "CountText").Attribute("Visibility"));

		// Nixie 1.4.0 (item 13): while loading the header stays (the library's name), the letter row keeps its height, and the
		// placeholders are laid out by a copy of the grid's own layout
		Assert.DoesNotContain(library.Descendants(), e => e.Name.LocalName == "Case");
		Assert.Equal("{x:Bind ViewModel.IsLoading, Mode=OneWay}", (string?)FindByName(library, "LetterPlaceholder").Attribute("Visibility"));
		Assert.Equal("{x:Bind ViewModel.IsLoading, Mode=OneWay}", (string?)FindByName(library, "SkeletonRepeater").Attribute("Visibility"));
		Assert.Equal("Uniform", (string?)FindByName(library, "GridLayout").Attribute("ItemsStretch")); // a poster keeps its 2:3 at 1100
		Assert.Contains("LibrarySkeleton.Mirror(GridLayout, SkeletonLayout);", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/LibraryPage.xaml.cs")));
		Assert.Contains(library.Descendants(), e => e.Name.LocalName == "LibraryNoMatchesView");
		Assert.Contains(library.Descendants(), e => e.Name.LocalName == "AppliedFilterRow");
		Assert.Null(typeof(LibraryViewModel).GetProperty("SelectedPage"));
	}

	// ---- the items stream's request: the hero runs edge to edge (the inset came from Home's page, scroller and hero margins)

	[Fact]
	public void HomesHero_RunsEdgeToEdge_TheRowsKeepTheirInset()
	{
		var home = Load("FluentFin/Views/HomePage.xaml");

		Assert.Null(home.Root!.Attribute("Margin"));
		Assert.Equal("0,0,0,36", (string?)FindByName(home, "PageScroller").Attribute("Margin"));
		Assert.Equal("0", (string?)home.Descendants().Single(e => e.Name.LocalName == "HomeHero").Attribute("Margin"));
		var rows = FindByName(home, "RowsPanel");
		Assert.Equal("{x:Bind local:HomePage.RowsMargin(ViewModel.Hero), Mode=OneWay}", (string?)rows.Attribute("Margin"));
		Assert.Contains(rows.Descendants(), e => e.Name.LocalName == "ItemsControl"); // the library rows are inside it
		Assert.Contains("public static Microsoft.UI.Xaml.Thickness RowsMargin(IReadOnlyList<HeroSlide>? hero) => new(24, hero is { Count: > 0 } ? 20 : 36, 24, 0);",
			File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/HomePage.xaml.cs")));
		// nothing above the hero takes room: the page's own spacing is 0 and the state view has no top margin
		var content = FindByName(home, "PageScroller").Elements().Single();
		Assert.Null(content.Attribute("Spacing"));
		Assert.Equal("36,0", (string?)content.Elements().First().Attribute("Margin"));
	}

	// ---- interaction audit, item 4: a single letter must never start playback

	[Fact]
	public void HomesNAndC_GoToTheirRows_AndPlayNothing()
	{
		Assert.Null(typeof(HomeViewModel).GetProperty("ContinueCommand"));
		Assert.Null(typeof(HomeViewModel).GetProperty("NextUpCommand"));

		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Views/HomePage.xaml.cs"));
		Assert.Contains("args.Handled = NextUpRow.FocusRow();", code);
		Assert.Contains("args.Handled = ContinueRow.FocusRow();", code);
		Assert.DoesNotContain("Play", code[code.IndexOf("private void NextUpInvoked", StringComparison.Ordinal)..code.IndexOf("// Seaglass scrolling: \"See all\"", StringComparison.Ordinal)]);

		var home = Core.Shortcuts.ShortcutCatalog.Sections.Single(s => s.Title == "Home page").Entries;
		Assert.Equal(["Go to the Next Up row (Enter plays)", "Go to the Continue Watching row (Enter plays)"], home.Select(e => e.Description));
		Assert.Contains("no\nlonger play anything", File.ReadAllText(Path.Combine(RepoRoot, "../docs/SHORTCUTS.md")).Replace("\r\n", "\n"));
	}

	private static XDocument Load(string relative) => XDocument.Load(Path.Combine(RepoRoot, relative));

	private static XElement FindByName(XDocument doc, string name) =>
		doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

	internal static string RepoRoot { get; } = FindRepoRoot();

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FluentFin.sln")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new DirectoryNotFoundException("FluentFin.sln not found above " + AppContext.BaseDirectory);
	}
}
