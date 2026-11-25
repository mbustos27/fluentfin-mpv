using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (Seaglass UX review, fixes 4, 7 and 12), read from the XAML and the code behind it: a card's hover buttons
/// and an item page's icon buttons are named after their item and the toggle's state, their tooltips the same words;
/// "Your rating" sits in the hero's action row on every item page; the hero's dots and the rating's stars are 24 px targets.
/// The words themselves: ItemActionLabelsTests and ItemPlayTests.
/// </summary>
public sealed class ItemButtonsXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static readonly string[] ItemPages = ["MoviePage", "SeriesPage", "SeasonPage", "EpisodePage"];

	[Theory]
	[InlineData("PlayButton", "{x:Bind core:ItemLabels.PlayAction(Model.Dto), Mode=OneWay")]
	[InlineData("MarkWatchedButton", "{x:Bind core:ItemLabels.WatchedAction(Model.Dto, MarkWatchedButton.IsChecked), Mode=OneWay")]
	[InlineData("AddToFavoriteButton", "{x:Bind core:ItemLabels.FavoriteAction(Model.Dto, AddToFavoriteButton.IsChecked), Mode=OneWay")]
	[InlineData("MoreButton", "{x:Bind core:ItemLabels.MoreOptions(Model.Dto), Mode=OneWay")]
	public void CardButtons_AreNamedForTheirItemAndState_AndTheirTooltipsSayTheSame(string button, string binding)
	{
		var element = FindByName(Load("FluentFin/Controls/BaseItemDtoCard.xaml"), button);

		Assert.StartsWith(binding, (string?)element.Attribute("AutomationProperties.Name"));
		Assert.Equal((string?)element.Attribute("AutomationProperties.Name"), (string?)element.Attribute("ToolTipService.ToolTip"));
	}

	[Fact]
	public void CardToggles_SetTheStateTheyShow()
	{
		// their names follow the toggle (above); a click sends that state, not the inverse of the model's copy, which only Home refreshes
		var code = Code("FluentFin/Controls/BaseItemDtoCard.xaml.cs");

		Assert.Contains("App.Commands.SetWatched(Model.Dto, MarkWatchedButton.IsChecked == true)", code);
		Assert.Contains("App.Commands.SetFavorite(Model.Dto, AddToFavoriteButton.IsChecked == true)", code);
	}

	/// <summary>1.2.0 (1.1.3 UX audit, interaction item 3): every surface sends the state its toggle shows, through ItemUserData.</summary>
	[Theory]
	[InlineData("FluentFin/Controls/ItemHero.xaml.cs", "App.Commands.SetWatched(dto, WatchedButton.IsChecked == true)")]
	[InlineData("FluentFin/Controls/ItemHero.xaml.cs", "App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true)")]
	public void ItemPageAndRowToggles_SetTheStateTheyShow(string file, string call)
	{
		Assert.Contains(call, Code(file));
	}

	[Fact]
	public void TheToggles_AreNotTheOldToggleCommands()
	{
		// ToggleWatched/ToggleFavorite decide from the item's copy: right for a menu, wrong for a toggle that flipped already
		foreach (var file in new[] { "FluentFin/Controls/ItemHero.xaml", "FluentFin/Controls/EpisodeRow.xaml", "FluentFin/Controls/BaseItemDtoCard.xaml" })
		{
			Assert.DoesNotContain("ToggleWatchedCommand", Code(file));
			Assert.DoesNotContain("ToggleFavoriteCommand", Code(file));
		}
	}

	/// <summary>1.2.0: every card, hero and row follows the item's user data as the server has it, whoever changed it.</summary>
	[Theory]
	[InlineData("FluentFin/Controls/BaseItemDtoCard.xaml.cs", "Bindings.Update();")]
	[InlineData("FluentFin/Controls/ItemHero.xaml.cs", "ApplyUserData();")]
	[InlineData("FluentFin/Controls/EpisodeRow.xaml.cs", "ApplyUserData();")]
	public void Surfaces_FollowTheServersUserData(string file, string refresh)
	{
		var code = Code(file);

		Assert.Contains("ItemUserData.Changed += OnUserDataChanged;", code);
		Assert.Contains("ItemUserData.Changed -= OnUserDataChanged;", code);
		Assert.Matches(new Regex(@"void OnUserDataChanged\(object\? sender, ItemUserDataChangedEventArgs e\)[\s\S]*?" + Regex.Escape(refresh)), code);
	}

	[Fact]
	public void ItemHerosMenu_IsMadeAgainWithTheItemsState()
	{
		// the More menu's "Mark as watched" text is the item's state: made again after a change, not once on load
		var apply = Regex.Match(Code("FluentFin/Controls/ItemHero.xaml.cs"), @"private void ApplyUserData\(\)[\s\S]*?\n\t}").Value;

		Assert.Contains("JellyfinFlyoutConverter.ApplyPageMenu(MoreButton, dto);", apply); // 1.7.5: the menu and the button's visibility
		Assert.Contains("WatchedButton.IsChecked = dto?.UserData?.Played;", apply);
	}

	/// <summary>1.2.0 (1.1.3 UX audit, interaction item 11): the whole card is one target, the title too; no tilt.</summary>
	[Fact]
	public void Card_IsOneTarget_TitleIncluded_AndDoesNotTilt()
	{
		var card = Load("FluentFin/Controls/BaseItemDtoCard.xaml");
		var root = FindByName(card, "RootGrid");
		var code = Code("FluentFin/Controls/BaseItemDtoCard.xaml.cs");

		Assert.Equal("Card_Tapped", (string?)root.Attribute("Tapped"));
		Assert.Equal("Card_PointerEntered", (string?)root.Attribute("PointerEntered"));
		Assert.Equal("Card_PointerExited", (string?)root.Attribute("PointerExited"));
		// the title's panel takes the pointer between its lines
		var title = FindByName(card, "TitleArea");
		Assert.Same(root, title.Parent);
		Assert.Equal("Transparent", (string?)title.Attribute("Background"));
		// a click on a button is the button's
		Assert.Contains("if (Model is null || OnAButton(e.OriginalSource as DependencyObject))", code);
		Assert.DoesNotContain("PosterTilt", code);
		Assert.DoesNotContain(card.Descendants(), e => (string?)e.Attribute(Xaml + "Name") == "SheenHost");
		Assert.False(File.Exists(Path.Combine(RepoRoot, "FluentFin/Controls/PosterTilt.cs")));
	}

	/// <summary>1.2.0 (1.1.3 UX audit, interaction item 6): a click on an episode row plays, as Enter does.</summary>
	[Fact]
	public void EpisodeRow_AClickPlays_WithAPlayDisc_AndTheTitleOpensTheEpisode()
	{
		var row = Load("FluentFin/Controls/EpisodeRow.xaml");
		var code = Code("FluentFin/Controls/EpisodeRow.xaml.cs");

		var tapped = Regex.Match(code, @"private void Body_Tapped\([\s\S]*?\n\t}").Value;
		Assert.Contains("Play();", tapped);
		Assert.DoesNotContain("OpenDetails", tapped);
		// a click on the title, a button or the title's link is not the row's
		// (174-sessions: the walk takes the tree and the button test, ClickGuardTests walks one of its own)
		Assert.Contains("For(source, row, title, VisualTreeHelper.GetParent, node => node is ButtonBase);", code);
		Assert.Contains("if (isButton(node) || ReferenceEquals(node, title))", code);
		// the title is a neutral link to the episode's page with no Tab stop (Nixie button review, I9; the menu is the keyboard's)
		var link = FindByName(row, "TitleLink");
		Assert.Equal("HyperlinkButton", link.Name.LocalName);
		Assert.Equal("False", (string?)link.Attribute("IsTabStop"));
		Assert.Equal("{ThemeResource TextFillColorPrimaryBrush}", (string?)link.Attribute("Foreground"));
		Assert.Same(link, FindByName(row, "TitleText").Parent);
		Assert.Contains("private void TitleLink_Click(object sender, RoutedEventArgs e) => OpenDetails();", code);
		// 1.2.0 (layout item 14): Play is the disc on the thumbnail, shown on hover and keyboard focus by its opacity, so
		// Narrator and UI Automation always find it; the row's "…" sits right after the title, opening the row's menu
		// the Play disc is a picture (Nixie button review, I8: the row is the button): no click, not read
		var disc = FindByName(row, "PlayDisc");
		Assert.Equal("Border", disc.Name.LocalName);
		Assert.Equal("False", (string?)disc.Attribute("IsHitTestVisible"));
		Assert.Equal("Raw", (string?)disc.Attribute("AutomationProperties.AccessibilityView"));
		Assert.Same(FindByName(row, "Thumb").Parent, disc.Parent);
		Assert.Contains("PlayDisc.Opacity = _pointerOver || FocusRing.Visibility == Visibility.Visible ? 1 : 0;", code);
		var more = FindByName(row, "MoreButton");
		Assert.Same(link.Parent, more.Parent);
		Assert.Equal("8", (string?)more.Attribute("CornerRadius"));
		Assert.Contains("_menu.ShowAt(MoreButton)", code);
		// one watched mark: no check button at the row's end
		Assert.DoesNotContain(row.Descendants(), e => e.Name.LocalName == "ToggleButton");
		// details stay in the row's menu for the keyboard
		Assert.Contains("Text = \"Episode details\"", code);
	}

	/// <summary>1.2.0 (1.1.3 UX audit, interaction item 5): Back to an item page gives its place back.</summary>
	[Theory]
	[InlineData("MoviePage")]
	[InlineData("SeriesPage")]
	[InlineData("SeasonPage")]
	[InlineData("EpisodePage")]
	public void ItemPages_KeepTheirPlace(string page)
	{
		var code = Code($"FluentFin/Views/{page}.xaml.cs");

		Assert.Contains("new ItemPagePlace(this, PageContent,", code);
		Assert.Matches(new Regex(@"override void OnNavigatedTo\(Microsoft\.UI\.Xaml\.Navigation\.NavigationEventArgs e\)[\s\S]*?_place\.Arrived\(e\)"), code);
		// saved while the page and its focus are still there: by OnNavigatedFrom the focus has left for the title bar (a run
		// found the focus never restored)
		Assert.Matches(new Regex(@"override void OnNavigatingFrom\(Microsoft\.UI\.Xaml\.Navigation\.NavigatingCancelEventArgs e\)\s*\{\s*_place\?\.Left\("), code);
		Assert.DoesNotContain("override void OnNavigatedFrom(", code);
		Assert.Equal("PageContent", (string?)Load($"FluentFin/Views/{page}.xaml").Descendants().First(e => e.Name.LocalName == "ScrollViewer").Attribute(Xaml + "Name"));
	}

	[Fact]
	public void ThePlace_IsGivenBack_OnlyOnBackOrForward_AndScrolledThenFocused()
	{
		var code = Code("FluentFin/Views/ItemPagePlace.cs");

		Assert.Contains("PagePlaces.ForReturn(_key, e.NavigationMode is NavigationMode.Back or NavigationMode.Forward)", code);
		Assert.Contains("PagePlaces.Save(_key, new PagePlace(_scroller.VerticalOffset, item, path, fromKeyboard, selectedSeasonId, name));", code);
		// Nixie 1.4.0 (DI-M5): a named control (the page's Play) is found again by its name first
		Assert.Contains("place.FocusedName is { } name && _page.FindName(name) is Control { IsLoaded: true } named ? named", code);
		Assert.Contains("_scroller.ChangeView(null, offset, null, disableAnimation: true);", code);
		// as it was given (items review, item 5: ItemNavigationTests)
		Assert.Contains("control.Focus(RestoreAs(place.FocusFromKeyboard))", code);
	}

	/// <summary>1.2.0 (1.1.3 UX audit, interaction item 10): switching seasons replaces the history entry.</summary>
	[Fact]
	public void SeasonPills_ReplaceTheSeasonInTheHistory()
	{
		var code = Code("FluentFin/Services/NavigationService.cs");

		// the rule itself (a season of the same series only): ItemNavigationTests; Nixie 1.3.0: a layout theme's season page
		// counts as the shared one (PageService.SharedPageType)
		Assert.Contains("DropSwitchedSeasonPage(frame.BackStack, x => Shared(x.SourcePageType), x => x.Parameter, Shared(e.SourcePageType), e.Parameter, e.NavigationMode);", code);
	}

	/// <summary>1.2.0 (1.1.3 UX audit, interaction item 13): the hero's artwork and words open the item; previous and next.</summary>
	[Fact]
	public void HomeHero_TheArtworkOpensTheItem_AndHasPreviousAndNext()
	{
		var hero = Load("FluentFin/Controls/HomeHero.xaml");
		var code = Code("FluentFin/Controls/HomeHero.xaml.cs");

		Assert.Equal("Root_Tapped", (string?)FindByName(hero, "Root").Attribute("Tapped"));
		Assert.Matches(new Regex(@"private void Root_Tapped\([\s\S]*?HeroClick\.OnAControl[\s\S]*?App\.Commands\.DisplayDto\(_slides\[_index\]\.Dto\);"), code);
		// a click between the dots is not on the artwork
		Assert.Equal("SlideControls_Tapped", (string?)FindByName(hero, "SlideControls").Attribute("Tapped"));
		Assert.Contains("private void SlideControls_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;", code);
		foreach (var (name, label) in new[] { ("PreviousButton", "Previous slide"), ("NextButton", "Next slide") })
		{
			var button = FindByName(hero, name);
			Assert.Equal(label, (string?)button.Attribute("AutomationProperties.Name"));
			Assert.Equal(label, (string?)button.Attribute("ToolTipService.ToolTip"));
			// Nixie button review, I19 / I21: 32 px targets, 16 px glyphs
			Assert.Equal("32", (string?)button.Attribute("Width"));
			Assert.Equal("32", (string?)button.Attribute("Height"));
			Assert.Equal("16", (string?)button.Descendants().Single(e => e.Name.LocalName == "FontIcon").Attribute("FontSize"));
			// the keyboard has Left/Right on the dots: no extra Tab stops
			Assert.Equal("False", (string?)button.Attribute("IsTabStop"));
			Assert.Same(FindByName(hero, "Dots").Parent, button.Parent);
		}
	}

	[Fact]
	public void ItemHeroButtons_AreNamedInCode_ForTheItemAndTheToggleState_AndRenamedWhenTheyFlip()
	{
		var code = Code("FluentFin/Controls/ItemHero.xaml.cs");

		Assert.Contains("Label(WatchedButton, ItemLabels.WatchedAction(dto, WatchedButton.IsChecked))", code);
		Assert.Contains("Label(FavoriteButton, ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked))", code);
		Assert.Contains("Label(MoreButton, ItemLabels.MoreOptions(dto))", code);
		// the name and the tooltip are set together
		Assert.Matches(new Regex(@"AutomationProperties\.SetName\(button, name\);\s*ToolTipService\.SetToolTip\(button, name\);"), code);
		// a click flips the toggle: its name follows
		Assert.Matches(new Regex(@"toggle\.Checked \+= \(_, _\) => UpdateActionNames\(\);\s*toggle\.Unchecked \+= \(_, _\) => UpdateActionNames\(\);"), code);
		Assert.Contains("new[] { WatchedButton, FavoriteButton }", code);
	}

	[Fact]
	public void EpisodeRowButtons_TheirTooltipsAreTheirNames()
	{
		var code = Code("FluentFin/Controls/EpisodeRow.xaml.cs");

		Assert.DoesNotContain("PlayButton", code);
		Assert.Contains("Label(MoreButton, ItemLabels.EpisodeAction(\"More options\", dto))", code);
		Assert.Matches(new Regex(@"AutomationProperties\.SetName\(button, name\);\s*ToolTipService\.SetToolTip\(button, name\);"), code);
	}

	[Fact]
	public void YourRating_IsInTheHerosActionRow_OnEveryItemPage_AsAPill()
	{
		var hero = Load("FluentFin/Controls/ItemHero.xaml");
		var strip = FindByName(hero, "ButtonStrip");
		var pill = FindByName(hero, "RatingPill");

		Assert.Same(strip, pill.Parent);
		Assert.Equal("RatingPanel", (string?)pill.Elements().Single(e => e.Name.LocalName == "JournalRatingPanel").Attribute(Xaml + "Name"));
		Assert.Equal("{StaticResource GlassPillHeight}", (string?)pill.Attribute("Height"));
		// 1.2.0 (Nixie button review, I27): plain stars on the hero, no pill drawn around them
		Assert.Null(pill.Attribute("Background"));
		Assert.Null(pill.Attribute("BorderThickness"));
		// hidden with the rating (movies, no journal): no empty pill
		Assert.Equal("{x:Bind RatingPanel.Visibility, Mode=OneWay}", (string?)pill.Attribute("Visibility"));
		Assert.Contains("RatingPanel.Item = dto;", Code("FluentFin/Controls/ItemHero.xaml.cs"));

		// and nowhere else: every item page has the hero, none a rating card of its own
		Assert.All(ItemPages, page =>
		{
			var doc = Load($"FluentFin/Views/{page}.xaml");
			Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "ItemHero");
			Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "JournalRatingPanel");
		});
	}

	[Fact]
	public void TheRatingsStars_AreSetToTheirTargetSize()
	{
		// the size itself is checked in FluentFin.Tests (TargetSizeTests); here, that the panel uses it and draws no card of its own
		var code = Code("FluentFin.Plugins/Journal/Controls/JournalRatingPanel.cs");

		Assert.Contains("StarSize = JournalRatingPanel.StarSize, MinHeight = JournalRatingPanel.TargetHeight", code);
		Assert.DoesNotContain("GlassPanelStyle", code);
	}

	[Fact]
	public void HeroDots_Are32PxTargets_WithoutAGap()
	{
		var dots = FindByName(Load("FluentFin/Controls/HomeHero.xaml"), "Dots");
		var code = Code("FluentFin/Controls/HomeHero.xaml.cs");

		Assert.Equal("0", (string?)dots.Attribute("Spacing"));
		Assert.Contains("Width = HeroDots.TargetSize, Height = HeroDots.TargetSize", code);
		Assert.Contains("public const double TargetSize = 32;", code);
		// Narrator hears which slide is shown (Nixie button review, I20)
		Assert.Contains("AutomationProperties.SetItemStatus(Dots.Children[i], i == index ? \"Current slide\" : \"\");", code);
	}

	[Theory]
	[InlineData("SeriesPage")]
	[InlineData("SeasonPage")]
	public void SeriesAndSeasonPlay_ShowsTheResumeProgress_OnTheAccentsOwnTextColour(string page)
	{
		var play = Load($"FluentFin/Views/{page}.xaml").Descendants()
			.Single(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Command") == "{x:Bind root:App.Commands.PlayDtoCommand}");
		var bar = play.Descendants().Single(e => e.Name.LocalName == "ProgressBar");

		Assert.Equal("{x:Bind ViewModel.PlayTarget.ProgressValue, Mode=OneWay}", (string?)bar.Attribute("Value"));
		Assert.Equal("{ThemeResource NixieAccentForegroundBrush}", (string?)bar.Attribute("Foreground"));
		Assert.Equal("{x:Bind ViewModel.PlayTarget.HasProgress, Mode=OneWay}", (string?)bar.Parent!.Attribute("Visibility"));
	}

	private static string Code(string repoRelativePath) => File.ReadAllText(Path.Combine(RepoRoot, repoRelativePath));

	private static XElement FindByName(XDocument doc, string name) =>
		doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

	private static XDocument Load(string repoRelativePath) => XDocument.Load(Path.Combine(RepoRoot, repoRelativePath), LoadOptions.SetLineInfo);

	private static string RepoRoot { get; } = FindRepoRoot();

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
