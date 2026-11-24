using System.Xml;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// fluentfin-mpv: static checks of the keyboard, Narrator, text and contrast-theme fixes from the UX audit
/// (docs/research/ux-review.md A1, A5, I1–I5). Reads the XAML as XML, so it needs neither WinUI nor a running app; what a
/// running app does (focus, UIA tree) is checked by hand or by the dev harness's UIA scan.
/// </summary>
public class AccessibilityXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	private static readonly string[] ButtonTypes = ["Button", "ToggleButton", "RepeatButton", "DropDownButton", "SplitButton", "HyperlinkButton", "AppBarButton", "AppBarToggleButton"];
	private static readonly string[] IconTypes = ["FontIcon", "SymbolIcon", "PathIcon", "BitmapIcon", "ImageIcon", "Image", "PersonPicture"];

	/// <summary>Copies of WinUI's own control templates: the controls name these parts (back, close, search) in code.</summary>
	private static readonly string[] WinUiTemplateCopies = ["FluentFin/App.xaml", "FluentFin/Styles/NavigationView.xaml"];

	private static readonly string[] ItemPages =
	[
		"FluentFin/Views/MoviePage.xaml",
		"FluentFin/Views/EpisodePage.xaml",
		"FluentFin/Views/SeriesPage.xaml",
		"FluentFin/Views/SeasonPage.xaml",
	];

	public static TheoryData<string> ItemPageData() => [.. ItemPages];

	[Fact]
	public void EveryIconOnlyButton_HasANameAndATooltip()
	{
		var missing = new List<string>();
		foreach (var file in AppXamlFiles().Where(f => !WinUiTemplateCopies.Contains(f)))
		{
			foreach (var button in Load(file).Descendants().Where(e => e.Name.Namespace == Presentation && ButtonTypes.Contains(e.Name.LocalName)).Where(IsIconOnly))
			{
				var what = new List<string>();
				if (string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name")))
				{
					what.Add("AutomationProperties.Name");
				}
				if (string.IsNullOrWhiteSpace((string?)button.Attribute("ToolTipService.ToolTip")))
				{
					what.Add("ToolTipService.ToolTip");
				}
				if (what.Count > 0)
				{
					missing.Add($"{file}:{((IXmlLineInfo)button).LineNumber} {button.Name.LocalName} {Describe(button)} has no {string.Join(" and no ", what)}");
				}
			}
		}

		Assert.True(missing.Count == 0, "Icon-only buttons need a name for Narrator and a tooltip (ux-review I3):\n" + string.Join("\n", missing));
	}

	[Theory]
	[InlineData("FluentFin/Views/MoviePage.xaml")]
	[InlineData("FluentFin/Views/EpisodePage.xaml")]
	public void MovieAndEpisodePages_HaveANamedResumeButton_AndPlayFromBeginning(string page)
	{
		var buttons = Load(page).Descendants(Presentation + "Button").ToList();

		var play = Assert.Single(buttons, b => (string?)b.Attribute("Command") == "{x:Bind ViewModel.PlayCommand}");
		Assert.Equal("{x:Bind ViewModel.PlayLabel, Mode=OneWay}", (string?)play.Attribute("AutomationProperties.Name"));
		Assert.Contains(play.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "{x:Bind ViewModel.PlayLabel, Mode=OneWay}");
		Assert.Equal("{StaticResource NixiePrimaryButtonStyle}", (string?)play.Attribute("Style"));

		var fromBeginning = Assert.Single(buttons, b => (string?)b.Attribute("Command") == "{x:Bind ViewModel.PlayFromBeginningCommand}");
		// 1.2.0 (Nixie button review, I37 / I50): "Play from start" in words beside Resume (it was an icon alone), named
		// "Play Inception from start"; the words say it, so no tooltip
		Assert.StartsWith("{x:Bind core:ItemLabels.PlayFromBeginningAction(ViewModel.Dto), Mode=OneWay", (string?)fromBeginning.Attribute("AutomationProperties.Name"));
		Assert.Contains(fromBeginning.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "Play from start");
		Assert.Contains(fromBeginning.Descendants(Presentation + "FontIcon"), i => (string?)i.Attribute("Glyph") == "");
		Assert.Equal("{StaticResource NixieButtonStyle}", (string?)fromBeginning.Attribute("Style"));
		Assert.Equal("{x:Bind ViewModel.HasResumePoint, Mode=OneWay}", (string?)fromBeginning.Attribute("Visibility"));
	}

	[Theory]
	[InlineData("FluentFin/Views/SeriesPage.xaml")]
	[InlineData("FluentFin/Views/SeasonPage.xaml")]
	public void SeriesAndSeasonPages_HaveATextPlayButton(string page)
	{
		var hero = Assert.Single(Load(page).Descendants(), e => e.Name.LocalName == "ItemHero");
		var play = Assert.Single(hero.Descendants(Presentation + "Button"), b => (string?)b.Attribute("Command") == "{x:Bind root:App.Commands.PlayDtoCommand}");

		// 1.2.0: it says what it starts ("Resume S1 E4", "Play S2 E1", "Play from the start"; ItemPlayTests)
		Assert.Contains(play.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "{x:Bind ViewModel.PlayTarget.Label, Mode=OneWay}");
		Assert.Equal("{x:Bind ViewModel.PlayTarget.Name, Mode=OneWay}", (string?)play.Attribute("AutomationProperties.Name"));
		Assert.Equal("{x:Bind ViewModel.PlayTarget.Name, Mode=OneWay}", (string?)play.Attribute("ToolTipService.ToolTip"));
		Assert.Equal("{StaticResource NixiePrimaryButtonStyle}", (string?)play.Attribute("Style"));
	}

	[Theory]
	[MemberData(nameof(ItemPageData))]
	public void ItemPages_WrapTheOverview_WithShowMore(string page)
	{
		var doc = Load(page);

		// the hero hands the overview to an ExpandableText ("Show more")
		var hero = Assert.Single(doc.Descendants(), e => e.Name.LocalName == "ItemHero");
		Assert.Equal("{x:Bind ViewModel.Dto.Overview, Mode=OneWay}", (string?)hero.Attribute("Overview"));
		Assert.Contains(Load("FluentFin/Controls/ItemHero.xaml").Descendants(), e => e.Name.LocalName == "ExpandableText");
		Assert.DoesNotContain(doc.Descendants(Presentation + "TextBlock"), t => ((string?)t.Attribute("Text"))?.Contains("ViewModel.Dto.Overview") == true);
	}

	[Fact]
	public void ExpandableText_Wraps()
	{
		var body = FindByName(Load("FluentFin/Controls/ExpandableText.xaml"), "Body");

		Assert.Equal("WrapWholeWords", (string?)body.Attribute("TextWrapping"));
	}

	[Theory]
	[MemberData(nameof(ItemPageData))]
	public void ItemPages_ShowTheirMetadataAsQuietText_NotAsLinksThatGoNowhere(string page)
	{
		var doc = Load(page);

		Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "ItemMetaRows" && (string?)e.Attribute("Dto") == "{x:Bind ViewModel.Dto, Mode=OneWay}");
		// 1.2.0: the one link left goes somewhere (the series page's "Season 2 details"); the metadata has none
		Assert.All(doc.Descendants(Presentation + "HyperlinkButton"), link =>
			Assert.True(link.Attribute("Command") is not null || link.Attribute("Click") is not null || link.Attribute("NavigateUri") is not null, "a link that goes nowhere"));
		Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "ItemMetaRows" && e.Descendants(Presentation + "HyperlinkButton").Any());
		Assert.DoesNotContain("HyperlinkButton", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/ItemMetaRows.xaml")));
	}

	[Theory]
	[MemberData(nameof(ItemPageData))]
	public void ItemPages_StartWithTheHero_ThatHasTheTitleAsAHeading(string page)
	{
		var doc = Load(page);

		Assert.Single(doc.Descendants(), e => e.Name.LocalName == "ItemHero" && (string?)e.Attribute("Dto") == "{x:Bind ViewModel.Dto, Mode=OneWay}");
		var hero = Load("FluentFin/Controls/ItemHero.xaml");
		var title = FindByName(hero, "TitleText");
		Assert.Equal("Level1", (string?)title.Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("{StaticResource GlassHeroTitleTextBlockStyle}", (string?)title.Attribute("Style"));
		// a logo stands in for the title: it is named for Narrator in code (see ItemHero.Apply)
		Assert.Contains("AutomationProperties.SetName(LogoImage", File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/ItemHero.xaml.cs")));
	}

	[Fact]
	public void ItemHero_KeepsItsGlassButtonsNamed_AndOnlyTheTopLevelPlayIsAccent()
	{
		var hero = Load("FluentFin/Controls/ItemHero.xaml");

		Assert.Equal(["Watch trailer", "Mark as watched", "Add to favorites", "More options"],
			new[] { "TrailerButton", "WatchedButton", "FavoriteButton", "MoreButton" }.Select(n => (string?)FindByName(hero, n).Attribute("AutomationProperties.Name")));
		Assert.DoesNotContain(hero.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource NixiePrimaryButtonStyle}");
		var scrims = hero.Descendants(Presentation + "Border").Select(b => (string?)b.Attribute("Background")).ToList();
		Assert.Contains("{ThemeResource GlassImageScrimBrush}", scrims);
		Assert.Contains("{ThemeResource ItemHeroSideScrimBrush}", scrims);
		Assert.Equal("Dark", (string?)hero.Root!.Elements().Single().Attribute("RequestedTheme"));
	}

	[Fact]
	public void NoFavoriteOrStarColour_IsWrittenIntoAPage()
	{
		var hardCoded = new List<string>();
		foreach (var file in AppXamlFiles().Where(f => f != "FluentFin/Styles/Button.xaml"))
		{
			var doc = Load(file);
			hardCoded.AddRange(doc.Descendants(Presentation + "SolidColorBrush")
				.Where(b => (string?)b.Attribute(Xaml + "Key") == "SystemAccentColor")
				.Select(b => $"{file}:{((IXmlLineInfo)b).LineNumber} SystemAccentColor = {(string?)b.Attribute("Color")}"));
			hardCoded.AddRange(doc.Descendants(Presentation + "FontIcon")
				.Where(i => ((string?)i.Attribute("Foreground")) is { } fg && !fg.StartsWith('{'))
				.Select(i => $"{file}:{((IXmlLineInfo)i).LineNumber} FontIcon Foreground = {(string?)i.Attribute("Foreground")}"));
		}

		Assert.True(hardCoded.Count == 0, "Colours written into pages ignore the contrast themes (ux-review A5/I5):\n" + string.Join("\n", hardCoded));
	}

	[Fact]
	public void FavoriteToggles_UseTheThemedFavoriteBrush()
	{
		var toggles = AppXamlFiles().SelectMany(f => Load(f).Descendants(Presentation + "ToggleButton").Select(t => (File: f, Toggle: t)))
			.Where(x => x.Toggle.Element(Presentation + "ToggleButton.Resources") is not null)
			.ToList();

		Assert.NotEmpty(toggles);
		Assert.All(toggles, x =>
		{
			var alias = Assert.Single(x.Toggle.Element(Presentation + "ToggleButton.Resources")!.Elements());
			Assert.Equal("StaticResource", alias.Name.LocalName);
			Assert.Equal("SystemAccentColor", (string?)alias.Attribute(Xaml + "Key"));
			Assert.Equal("FavoriteCheckedBrush", (string?)alias.Attribute("ResourceKey"));
		});
	}

	[Theory]
	[InlineData("FavoriteCheckedBrush", "{ThemeResource SystemColorHighlightColor}")]
	[InlineData("RatingStarBrush", "{ThemeResource SystemColorWindowTextColor}")]
	public void ThemedColours_HaveAHighContrastValue(string key, string highContrast)
	{
		var themes = Load("FluentFin/Styles/Button.xaml").Root!.Element(Presentation + "ResourceDictionary.ThemeDictionaries")!.Elements().ToList();

		string? Value(string theme) => (string?)themes.Single(d => (string?)d.Attribute(Xaml + "Key") == theme)
			.Elements(Presentation + "SolidColorBrush").Single(b => (string?)b.Attribute(Xaml + "Key") == key).Attribute("Color");

		Assert.NotNull(Value("Default"));
		Assert.Equal(highContrast, Value("HighContrast"));
	}

	[Fact]
	public void Card_IsAKeyboardStopWithAFocusRectangle()
	{
		var root = Load("FluentFin/Controls/BaseItemDtoCard.xaml").Root!;

		Assert.Equal("True", (string?)root.Attribute("IsTabStop"));
		// Seaglass: the ring is the card's own, around the artwork (the system's rectangle would surround the whole slot)
		Assert.Equal("False", (string?)root.Attribute("UseSystemFocusVisuals"));
		var ring = FindByName(Load("FluentFin/Controls/BaseItemDtoCard.xaml"), "FocusRing");
		// 1.4.0: the two-tone ring (WinUI's own focus colours: the theme's and high contrast's), 3 px outside and a 1.5 px line inside
		Assert.Equal("{ThemeResource SystemControlFocusVisualPrimaryBrush}", (string?)ring.Attribute("BorderBrush"));
		Assert.Equal("3", (string?)ring.Attribute("BorderThickness"));
		Assert.Equal("{ThemeResource SystemControlFocusVisualSecondaryBrush}", (string?)ring.Elements().Single().Attribute("BorderBrush"));
	}

	[Fact]
	public void Card_ButtonsAreShownAndHiddenInCode_NotByAPointerBehaviour()
	{
		// a PointerExited behaviour that collapses the buttons would hide a button that has keyboard focus
		var doc = Load("FluentFin/Controls/BaseItemDtoCard.xaml");
		var collapsing = doc.Descendants().Where(e => e.Name.LocalName == "ChangePropertyAction" && (string?)e.Attribute("PropertyName") == "Visibility").ToList();

		Assert.Empty(collapsing);
		// 1.2.0: on the whole card (the title too), no longer the image alone
		var card = FindByName(doc, "RootGrid");
		Assert.Equal("Card_PointerEntered", (string?)card.Attribute("PointerEntered"));
		Assert.Equal("Card_PointerExited", (string?)card.Attribute("PointerExited"));
	}

	[Fact]
	public void Card_EffectAnimations_ReachTheirEffectsThroughXBind()
	{
		// visual-check: an effect of a PipelineVisualFactory is not found by {Binding ElementName=...} at run time, so the
		// animation had no Target and each start (the card's buttons showing or hiding) threw "The target effect is null"
		var doc = Load("FluentFin/Controls/BaseItemDtoCard.xaml");
		var effects = doc.Descendants().Where(e => e.Name.LocalName is "BlurEffect" or "TintEffect").Select(e => (string?)e.Attribute(Xaml + "Name")).ToList();
		var animations = doc.Descendants().Where(e => e.Name.LocalName.EndsWith("EffectAnimation", StringComparison.Ordinal)).ToList();

		// Seaglass: the card has none now (a lift replaces the blur and tint); any that return must reach their effect this way
		Assert.All(animations, a =>
		{
			var target = (string?)a.Attribute("Target") ?? "";
			Assert.Matches(@"^\{x:Bind \w+\}$", target);
			Assert.Contains(target["{x:Bind ".Length..^1], effects);
		});
	}

	// Seaglass 1.1.4: the Library's Sort and Filter show the sort in use and the filters on, and their names say it with more
	// words ("Sort: Name, ascending", "Filter (2 on)"): the view model's (LibraryBrowseTests)
	[Theory]
	[InlineData("FluentFin/Views/SelectServerPage.xaml", "Find servers", "Find servers", "Connect", "Connect")]
	public void ButtonsShowingAnIconAndAText_AreNamedAfterTheText(string page, string firstName, string firstText, string secondName, string secondText)
	{
		// visual-check (UIA scan): a TextBlock inside a panel does not name its button, so Narrator read "button"
		var named = Load(page).Descendants().Where(e => e.Name.Namespace == Presentation && ButtonTypes.Contains(e.Name.LocalName))
			.Where(b => (string?)b.Attribute("Content") is null && ContentElements(b).Any(e => e.Name.LocalName == "TextBlock") && ContentElements(b).Any(e => e.Name.LocalName != "TextBlock"))
			.Select(b => (Name: (string?)b.Attribute("AutomationProperties.Name"), Text: ContentElements(b).Where(e => e.Name.LocalName == "TextBlock").Select(t => (string?)t.Attribute("Text")).First()))
			.ToList();

		Assert.Equal(new (string?, string?)[] { (firstName, firstText), (secondName, secondText) }, named);
	}

	// Nixie 1.4.0: the library toolbar's buttons with an icon and words are named after their words (WCAG 2.5.3: Sort's name
	// starts with them, "Title, A to Z, Sort"; the view model's, LibraryFilterModelTests)
	[Fact]
	public void TheLibraryToolbarsButtons_AreNamedAfterTheirWords()
	{
		var named = Load("FluentFin/Controls/LibraryToolbar.xaml").Descendants().Where(e => e.Name.Namespace == Presentation && ButtonTypes.Contains(e.Name.LocalName))
			.Where(b => (string?)b.Attribute("Content") is null && ContentElements(b).Any(e => e.Name.LocalName == "TextBlock") && ContentElements(b).Any(e => e.Name.LocalName != "TextBlock"))
			.Select(b => (Name: (string?)b.Attribute("AutomationProperties.Name"), Text: ContentElements(b).Where(e => e.Name.LocalName == "TextBlock").Select(t => (string?)t.Attribute("Text")).First()))
			.ToList();

		Assert.Equal(new (string?, string?)[]
		{
			("{x:Bind local:LibraryToolbar.UnwatchedName(ViewModel.UnwatchedCount), Mode=OneWay}", "Unwatched"), ("Favorites", "Favorites"),
			("{x:Bind ViewModel.SortButtonName, Mode=OneWay}", "{x:Bind ViewModel.SortText, Mode=OneWay}"),
			("{x:Bind ViewModel.FilterButtonName, Mode=OneWay}", "{x:Bind ViewModel.FilterText, Mode=OneWay}"),
		}, named);
	}

	[Theory]
	[InlineData("FluentFin/Views/MoviePage.xaml")]
	[InlineData("FluentFin/Views/EpisodePage.xaml")]
	public void ItemPages_AudioAndSubtitleLists_AreNamedAfterTheirLabels(string page)
	{
		// visual-check (UIA scan): the lists sit beside a label TextBlock that does not name them
		var lists = Load(page).Descendants(Presentation + "ComboBox").ToList();

		Assert.Equal(new string?[] { "Audio", "Subtitle" }, lists.Select(c => (string?)c.Attribute("AutomationProperties.Name")));
		// 1.2.0 (layout audit item 16): plain labels (TrackLabels), strings Narrator reads, chosen by place; no codecs
		Assert.Equal("{x:Bind ViewModel.AudioLabels, Mode=OneWay}", (string?)lists[0].Attribute("ItemsSource"));
		Assert.Equal("{x:Bind ViewModel.SelectedAudioIndex, Mode=TwoWay}", (string?)lists[0].Attribute("SelectedIndex"));
		Assert.Equal("{x:Bind ViewModel.SubtitleLabels, Mode=OneWay}", (string?)lists[1].Attribute("ItemsSource"));
		Assert.Equal("{x:Bind ViewModel.SelectedSubtitleIndex, Mode=TwoWay}", (string?)lists[1].Attribute("SelectedIndex"));
		Assert.All(lists, c => Assert.Null(c.Attribute("DisplayMemberPath")));
		// the items before the place, so a new list is in before the place it is set to
		Assert.All(lists, c => Assert.True(c.Attributes().ToList().FindIndex(a => a.Name.LocalName == "ItemsSource") < c.Attributes().ToList().FindIndex(a => a.Name.LocalName == "SelectedIndex")));
		Assert.Contains(Load(page).Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "{x:Bind ViewModel.SelectedAudioLabel, Mode=OneWay}");
	}

	[Fact]
	public void LibraryGrid_TheCardIsTheKeyboardStop_NotItsContainer()
	{
		var container = Load("FluentFin/Views/LibraryPage.xaml").Descendants(Presentation + "ItemContainer")
			.Single(c => c.Descendants().Any(e => e.Name.LocalName == "BaseItemDtoCard"));

		Assert.Equal("False", (string?)container.Attribute("IsTabStop"));
		Assert.Equal("Raw", (string?)container.Attribute("AutomationProperties.AccessibilityView"));
	}

	[Fact]
	public void LibraryTiles_AreNamed()
	{
		// 1.6 topbar: two tiles, the media library's (with its star) and the collections group's
		var containers = Load("FluentFin/Views/LibrariesLandingPage.xaml").Descendants(Presentation + "ItemContainer").ToList();
		Assert.Equal(2, containers.Count);

		// Nixie 1.4.0: with what the tile shows beside the name: "Movies, 320 movies, 97 unwatched" (LibraryTile.AutomationName)
		Assert.All(containers, container => Assert.Equal("{x:Bind AutomationName, Mode=OneWay}", (string?)container.Attribute("AutomationProperties.Name")));
	}

	[Fact]
	public void SavedServerTiles_AreNamed_AndALongNameIsTrimmed()
	{
		// visual-check: Narrator read the tile as "ItemContainer", and "Harness Jellyfin (fake)" was cut off at both ends
		var container = Assert.Single(Load("FluentFin/Views/SelectServerPage.xaml").Descendants(Presentation + "ItemContainer"));

		Assert.Equal("{x:Bind DisplayName}", (string?)container.Attribute("AutomationProperties.Name"));
		Assert.Equal("{x:Bind DisplayName}", (string?)container.Attribute("ToolTipService.ToolTip"));
		// 1.1.4 (layout audit #12): a row, the name then the address, each trimmed only at the row's width
		var texts = container.Descendants(Presentation + "TextBlock").ToList();
		Assert.Equal(["{x:Bind DisplayName}", "{x:Bind local:SelectServerPage.AddressOf(LocalUrl, PublicUrl)}"], texts.Select(t => (string?)t.Attribute("Text")));
		Assert.All(texts, t => Assert.Equal("CharacterEllipsis", (string?)t.Attribute("TextTrimming")));
		Assert.DoesNotContain(container.Attributes(), a => a.Name.LocalName is "Width" or "Height");
	}

	[Fact]
	public void ShelfRow_TitleIsAHeading_AndTheArrowsAreNeitherTabStopsNorHidden()
	{
		var doc = Load("FluentFin/Controls/ShelfRow.xaml");

		var title = FindByName(doc, "TitleText");
		Assert.Equal("Level2", (string?)title.Attribute("AutomationProperties.HeadingLevel"));
		Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("PointerReleased") is not null);

		// the arrows: no Tab stop of their own (the row's keys move it); 32 px (WCAG 2.5.8). Nixie 1.7.4: shown on hover at the
		// row's ends by their Visibility (RowPaging.EdgeArrows), never an Opacity="0" target that a click could still hit
		foreach (var name in new[] { "ScrollLeftBtn", "ScrollRightBtn" })
		{
			var arrow = FindByName(doc, name);
			Assert.Equal("False", (string?)arrow.Attribute("IsTabStop"));
			Assert.Null(arrow.Attribute("Opacity"));
			Assert.True(double.Parse((string)arrow.Attribute("Width")!) >= 24);
			Assert.True(double.Parse((string)arrow.Attribute("Height")!) >= 24);
		}

		// one Tab stop for the row's cards
		var scroller = FindByName(doc, "Scroller");
		Assert.Equal("Once", (string?)scroller.Attribute("TabFocusNavigation"));
		Assert.Equal("Enabled", (string?)scroller.Attribute("HorizontalScrollMode"));
		Assert.Equal("Disabled", (string?)scroller.Attribute("VerticalScrollMode"));
	}

	// Nixie 1.7.4 (the rows stream): "Thomas McDonell" and "Christopher Larkin" ran together under their photos (names 120 wide
	// beside each other, the photo 104), and the keyboard never reached the people past the window's edge (no stop at all)
	[Fact]
	public void CastNames_StayWithinTheirPhoto_AndEachPersonIsAKeyboardStopWithTheirWholeName()
	{
		var doc = Load("FluentFin/Controls/CastRow.xaml");
		var card = doc.Descendants().Single(e => e.Name.LocalName == "CastCard");
		var photo = card.Descendants(Presentation + "PersonPicture").Single();
		var photoWidth = double.Parse((string)photo.Attribute("Width")!);
		Assert.True(double.Parse((string)card.Attribute("Width")!) >= photoWidth + 12, "room between one person's name and the next");
		Assert.Equal("{x:Bind core:ItemMeta.PersonSpoken((jm:BaseItemPerson))}", (string?)card.Attribute("AutomationProperties.Name"));
		Assert.Equal("{x:Bind core:ItemMeta.PersonSpoken((jm:BaseItemPerson))}", (string?)card.Attribute("ToolTipService.ToolTip"));
		var texts = card.Descendants(Presentation + "TextBlock").ToList();
		Assert.Equal(2, texts.Count); // the name and the role
		Assert.All(texts, t =>
		{
			Assert.True(double.Parse((string)t.Attribute("Width")!) <= photoWidth, "within the photo's width");
			Assert.Equal("2", (string?)t.Attribute("MaxLines"));
			Assert.Equal("CharacterEllipsis", (string?)t.Attribute("TextTrimming"));
			Assert.Equal("WrapWholeWords", (string?)t.Attribute("TextWrapping"));
		});
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/CastCard.cs"));
		Assert.Contains("IsTabStop = true;", code);
		Assert.Contains("AutomationControlType.ListItem", code);
	}

	[Fact]
	public void Home_UsesTheShelfRowForEveryRow_AndNoRowIsAFreeScrollViewer()
	{
		var home = Load("FluentFin/Views/HomePage.xaml");
		var rows = home.Descendants().Where(e => e.Name.LocalName == "ShelfRow").ToList();

		Assert.Equal(4, rows.Count); // Continue Watching, Next Up, Recently Added (one template), 1.6: Made by Nixie (one template)
		Assert.All(rows, row => Assert.Equal("True", (string?)row.Attribute("HasSeeAll")));
		// the page's own scroll viewer is the only one left (no hand-made horizontal ScrollViewer rows)
		Assert.Single(home.Descendants(Presentation + "ScrollViewer"));
		// a virtualizing repeater of rows left later rows out of the Tab order
		Assert.DoesNotContain(home.Descendants(Presentation + "ItemsRepeater"), r => (string?)r.Attribute("ItemsSource") == "{x:Bind ViewModel.RecentItems, Mode=OneWay}");
	}

	[Fact]
	public void SeasonEpisodeRows_AreOneStopEach_InANamedList_AndNameTheirButtonsForTheEpisode()
	{
		// Seaglass scrolling: five buttons per episode became one stop per row (Enter plays, the menu has the rest)
		var season = Load("FluentFin/Views/SeasonPage.xaml").Descendants().Where(e => e.Name.LocalName == "EpisodeList")
			.Single(r => (string?)r.Attribute("ItemsSource") == "{x:Bind ViewModel.Episodes, Mode=OneWay}");
		Assert.Contains(season.Descendants(), e => e.Name.LocalName == "EpisodeRow");
		Assert.Equal("Episodes", (string?)season.Attribute("AutomationProperties.Name"));
		// not a virtualizing ItemsRepeater: Tab never reached the episodes after the first
		Assert.DoesNotContain(Load("FluentFin/Views/SeasonPage.xaml").Descendants(Presentation + "ItemsRepeater"), r => ((string?)r.Attribute("ItemsSource"))?.Contains("Episodes") == true);

		var row = Load("FluentFin/Controls/EpisodeRow.xaml");
		Assert.Equal("True", (string?)row.Root!.Attribute("IsTabStop"));
		Assert.Equal("False", (string?)row.Root!.Attribute("UseSystemFocusVisuals"));
		Assert.Equal("Collapsed", (string?)FindByName(row, "FocusRing").Attribute("Visibility"));

		// 1.2.0: the row's "…" stays for the pointer, without a Tab stop of its own, named for its episode; the Play disc is a
		// picture (the row is the button: Nixie button review, I8) and Watched is in the menu (layout audit item 14)
		var buttons = row.Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleButton").ToList();
		Assert.Equal(new[] { "MoreButton" }, buttons.Select(b => (string?)b.Attribute(Xaml + "Name")));
		Assert.All(buttons, b => Assert.Equal("False", (string?)b.Attribute("IsTabStop")));
		Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("AutomationProperties.Name"))));
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/EpisodeRow.xaml.cs"));
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"ItemLabels\.EpisodeAction\(""More options"", dto\)"));
		// favourite and the rest: in the row's menu, which holds the More menu's own items
		Assert.Contains("ContextFlyout = _menu", code);
		Assert.Contains("new JellyfinFlyoutConverter().Convert(dto", code);
	}

	[Theory]
	[InlineData("FluentFin/Views/LibraryPage.xaml")]
	[InlineData("FluentFin/Views/ItemGridPage.xaml")]
	public void CardGrids_AreOneTabStop_AListNamedAfterThePage(string page)
	{
		var doc = Load(page);
		var list = FindByName(doc, "GridList");
		Assert.Equal("RowList", list.Name.LocalName);
		Assert.Equal("Once", (string?)list.Attribute("TabFocusNavigation"));
		Assert.Equal("{x:Bind ViewModel.Title, Mode=OneWay}", (string?)list.Attribute("AutomationProperties.Name"));
		Assert.Equal("ScrollView", list.Parent!.Name.LocalName);
		Assert.Equal("GridScroller", (string?)list.Parent!.Attribute(Xaml + "Name"));
		var repeater = Assert.Single(list.Elements(Presentation + "ItemsRepeater"));
		Assert.Equal("GridRepeater", (string?)repeater.Attribute(Xaml + "Name"));

		var code = File.ReadAllText(Path.Combine(RepoRoot, page + ".cs"));
		Assert.Contains("new CardGrid(GridScroller, GridList, GridRepeater)", code);
		Assert.Contains("_grid.ForwardWheel(", code);
	}

	[Theory]
	[InlineData("FluentFin/Views/LibraryPage.xaml", "LibraryRoot")]
	[InlineData("FluentFin/Views/ItemGridPage.xaml", "GridRoot")]
	public void CardGridPages_TakeTheWheelEverywhere_TheirMarginIsPadding(string page, string root)
	{
		// a page's Margin is outside it: the wheel there reached nothing. The root takes the wheel (CardGrid.ForwardWheel)
		var doc = Load(page);
		Assert.Null(doc.Root!.Attribute("Margin"));
		var grid = FindByName(doc, root);
		Assert.Equal("Transparent", (string?)grid.Attribute("Background"));
		Assert.Equal("{StaticResource MediumLeftTopRightBottomMargin}", (string?)grid.Attribute("Padding"));
		Assert.Contains($"_grid.ForwardWheel({root})", File.ReadAllText(Path.Combine(RepoRoot, page + ".cs")));
	}

	/// <summary>Shows an icon and no text: no Label, no text content, no TextBlock outside its property elements (a flyout's text is not the button's).</summary>
	private static bool IsIconOnly(XElement button)
	{
		if (button.Attribute("Label") is not null)
		{
			return false;
		}

		if ((string?)button.Attribute("Content") is { } content)
		{
			return content.StartsWith("{ui:FontIcon", StringComparison.Ordinal)
				|| content.StartsWith("{ui:SymbolIcon", StringComparison.Ordinal)
				|| (content.StartsWith('{') && (content.Contains("Symbol", StringComparison.Ordinal) || content.Contains("Icon", StringComparison.Ordinal)));
		}

		var visual = ContentElements(button).ToList();
		return visual.Any(e => IconTypes.Contains(e.Name.LocalName)) && !visual.Any(e => e.Name.LocalName == "TextBlock");
	}

	/// <summary>The button's content: its child elements and theirs, without property elements (Button.Flyout, ToolTipService.ToolTip, …).</summary>
	private static IEnumerable<XElement> ContentElements(XElement element)
	{
		foreach (var child in element.Elements().Where(e => !e.Name.LocalName.Contains('.')))
		{
			yield return child;
			foreach (var nested in ContentElements(child))
			{
				yield return nested;
			}
		}
	}

	private static string Describe(XElement button) =>
		(string?)button.Attribute(Xaml + "Name") ?? (string?)button.Attribute("Content") ?? (string?)button.Attribute("Command") ?? "";

	private static IEnumerable<string> AppXamlFiles()
	{
		var app = Path.Combine(RepoRoot, "FluentFin");
		return Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
			.Select(f => Path.GetRelativePath(RepoRoot, f).Replace('\\', '/'))
			.OrderBy(f => f, StringComparer.Ordinal);
	}

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
