using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Seaglass 1.1.4 (Seaglass UX review, fixes 4, 7 and 12), read from the XAML and the code behind it: a card's hover buttons
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

	/// <summary>Seaglass 1.1.4 (1.1.3 UX audit, interaction item 3): every surface sends the state its toggle shows, through ItemUserData.</summary>
	[Theory]
	[InlineData("FluentFin/Controls/ItemHero.xaml.cs", "App.Commands.SetWatched(dto, WatchedButton.IsChecked == true)")]
	[InlineData("FluentFin/Controls/ItemHero.xaml.cs", "App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true)")]
	[InlineData("FluentFin/Controls/EpisodeRow.xaml.cs", "App.Commands.SetWatched(dto, WatchedButton.IsChecked == true)")]
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

	/// <summary>Seaglass 1.1.4: every card, hero and row follows the item's user data as the server has it, whoever changed it.</summary>
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

		Assert.Contains("MoreButton.Flyout = ", apply);
		Assert.Contains("WatchedButton.IsChecked = dto?.UserData?.Played;", apply);
	}

	/// <summary>Seaglass 1.1.4 (1.1.3 UX audit, interaction item 11): the whole card is one target, the title too; no tilt.</summary>
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

	/// <summary>Seaglass 1.1.4 (1.1.3 UX audit, interaction item 6): a click on an episode row plays, as Enter does.</summary>
	[Fact]
	public void EpisodeRow_AClickPlays_WithAPlayDisc_AndTheTitleOpensTheEpisode()
	{
		var row = Load("FluentFin/Controls/EpisodeRow.xaml");
		var code = Code("FluentFin/Controls/EpisodeRow.xaml.cs");

		var tapped = Regex.Match(code, @"private void Body_Tapped\([\s\S]*?\n\t}").Value;
		Assert.Contains("Play();", tapped);
		Assert.DoesNotContain("OpenDetails", tapped);
		// a click on the title, a button or the title's link is not the row's
		Assert.Contains("if (node is ButtonBase || ReferenceEquals(node, title))", code);
		Assert.Equal("Title_Tapped", (string?)FindByName(row, "TitleText").Attribute("Tapped"));
		Assert.Matches(new Regex(@"private void Title_Tapped\([\s\S]*?OpenDetails\(\);"), code);
		// the play disc over the thumbnail: decorative (the row is named for Narrator), never takes the click
		var disc = FindByName(row, "PlayDisc");
		Assert.Equal("Raw", (string?)disc.Attribute("AutomationProperties.AccessibilityView"));
		Assert.Equal("False", (string?)disc.Attribute("IsHitTestVisible"));
		Assert.Equal("Collapsed", (string?)disc.Attribute("Visibility"));
		Assert.Contains("PlayDisc.Visibility = _pointerOver || FocusRing.Visibility == Visibility.Visible", code);
		// details stay in the row's menu for the keyboard
		Assert.Contains("Text = \"Episode details\"", code);
	}

	/// <summary>Seaglass 1.1.4 (1.1.3 UX audit, interaction item 5): Back to an item page gives its place back.</summary>
	[Theory]
	[InlineData("MoviePage")]
	[InlineData("SeriesPage")]
	[InlineData("SeasonPage")]
	[InlineData("EpisodePage")]
	public void ItemPages_KeepTheirPlace(string page)
	{
		var code = Code($"FluentFin/Views/{page}.xaml.cs");

		Assert.Contains("new ItemPagePlace(this, PageContent,", code);
		Assert.Matches(new Regex(@"override void OnNavigatedTo\(Microsoft\.UI\.Xaml\.Navigation\.NavigationEventArgs e\)[\s\S]*?_place\.Arrived\(e\);"), code);
		Assert.Matches(new Regex(@"override void OnNavigatedFrom\(Microsoft\.UI\.Xaml\.Navigation\.NavigationEventArgs e\)[\s\S]*?_place\?\.Left\(\);"), code);
		Assert.Equal("PageContent", (string?)Load($"FluentFin/Views/{page}.xaml").Descendants().First(e => e.Name.LocalName == "ScrollViewer").Attribute(Xaml + "Name"));
	}

	[Fact]
	public void ThePlace_IsGivenBack_OnlyOnBackOrForward_AndScrolledThenFocused()
	{
		var code = Code("FluentFin/Views/ItemPagePlace.cs");

		Assert.Contains("PagePlaces.ForReturn(_key, e.NavigationMode is NavigationMode.Back or NavigationMode.Forward)", code);
		Assert.Contains("PagePlaces.Save(_key, new PagePlace(scroller.VerticalOffset, item, path));", code);
		Assert.Contains("scroller.ChangeView(null, offset, null, disableAnimation: true);", code);
		Assert.Contains("control.Focus(FocusState.Programmatic)", code);
	}

	/// <summary>Seaglass 1.1.4 (1.1.3 UX audit, interaction item 10): switching seasons replaces the history entry.</summary>
	[Fact]
	public void SeasonPills_ReplaceTheSeasonInTheHistory()
	{
		var code = Code("FluentFin/Services/NavigationService.cs");

		Assert.Contains("DropSwitchedSeasonPage(frame.BackStack, x => x.SourcePageType, e.SourcePageType, e.NavigationMode);", code);
	}

	/// <summary>Seaglass 1.1.4 (1.1.3 UX audit, interaction item 13): the hero's artwork and words open the item; previous and next.</summary>
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
			Assert.Equal("24", (string?)button.Attribute("Width"));
			Assert.Equal("24", (string?)button.Attribute("Height"));
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

		Assert.Contains("Label(PlayButton, ItemLabels.EpisodeAction(\"Play\", dto))", code);
		Assert.Matches(new Regex(@"AutomationProperties\.SetName\(button, name\);\s*ToolTipService\.SetToolTip\(button, name\);"), code);
		Assert.Matches(new Regex(@"WatchedButton\.Checked \+= \(_, _\) => UpdateWatchedName\(\);\s*WatchedButton\.Unchecked \+= \(_, _\) => UpdateWatchedName\(\);"), code);
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
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", (string?)pill.Attribute("CornerRadius"));
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
	public void HeroDots_Are24PxTargets_WithoutAGap()
	{
		var dots = FindByName(Load("FluentFin/Controls/HomeHero.xaml"), "Dots");
		var code = Code("FluentFin/Controls/HomeHero.xaml.cs");

		Assert.Equal("0", (string?)dots.Attribute("Spacing"));
		Assert.Contains("Width = HeroDots.TargetSize, Height = HeroDots.TargetSize", code);
		Assert.Contains("public const double TargetSize = 24;", code);
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
		Assert.Equal("{ThemeResource SeaglassAccentForegroundBrush}", (string?)bar.Attribute("Foreground"));
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
