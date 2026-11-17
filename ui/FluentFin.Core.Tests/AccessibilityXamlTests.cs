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
		Assert.Equal("{StaticResource GlassAccentPillButtonStyle}", (string?)play.Attribute("Style"));

		var fromBeginning = Assert.Single(buttons, b => (string?)b.Attribute("Command") == "{x:Bind ViewModel.PlayFromBeginningCommand}");
		Assert.Equal("Play from beginning", (string?)fromBeginning.Attribute("AutomationProperties.Name"));
		Assert.Equal("{x:Bind ViewModel.HasResumePoint, Mode=OneWay}", (string?)fromBeginning.Attribute("Visibility"));
	}

	[Theory]
	[InlineData("FluentFin/Views/SeriesPage.xaml")]
	[InlineData("FluentFin/Views/SeasonPage.xaml")]
	public void SeriesAndSeasonPages_HaveATextPlayButton(string page)
	{
		var hero = Assert.Single(Load(page).Descendants(), e => e.Name.LocalName == "ItemHero");
		var play = Assert.Single(hero.Descendants(Presentation + "Button"), b => (string?)b.Attribute("Command") == "{x:Bind root:App.Commands.PlayDtoCommand}");

		Assert.Contains(play.Descendants(Presentation + "TextBlock"), t => (string?)t.Attribute("Text") == "Play");
		Assert.Equal("{StaticResource GlassAccentPillButtonStyle}", (string?)play.Attribute("Style"));
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
		Assert.DoesNotContain(doc.Descendants(Presentation + "HyperlinkButton"), _ => true);
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

		Assert.Equal(["Watch trailer", "Watched", "Favorite", "More options"],
			new[] { "TrailerButton", "WatchedButton", "FavoriteButton", "MoreButton" }.Select(n => (string?)FindByName(hero, n).Attribute("AutomationProperties.Name")));
		Assert.DoesNotContain(hero.Descendants(), e => (string?)e.Attribute("Style") == "{StaticResource GlassAccentPillButtonStyle}");
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
		Assert.StartsWith("{ThemeResource GlassTextPrimaryBrush}", (string?)ring.Attribute("BorderBrush"));
		Assert.Equal("3", (string?)ring.Attribute("BorderThickness"));
	}

	[Fact]
	public void Card_ButtonsAreShownAndHiddenInCode_NotByAPointerBehaviour()
	{
		// a PointerExited behaviour that collapses the buttons would hide a button that has keyboard focus
		var doc = Load("FluentFin/Controls/BaseItemDtoCard.xaml");
		var collapsing = doc.Descendants().Where(e => e.Name.LocalName == "ChangePropertyAction" && (string?)e.Attribute("PropertyName") == "Visibility").ToList();

		Assert.Empty(collapsing);
		var canvas = FindByName(doc, "ImageCanvas");
		Assert.Equal("ImageCanvas_PointerEntered", (string?)canvas.Attribute("PointerEntered"));
		Assert.Equal("ImageCanvas_PointerExited", (string?)canvas.Attribute("PointerExited"));
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

	[Theory]
	[InlineData("FluentFin/Views/SelectServerPage.xaml", "Detect", "Next")]
	[InlineData("FluentFin/Views/LibraryPage.xaml", "Sort", "Filter")]
	public void ButtonsShowingAnIconAndAText_AreNamedAfterTheText(string page, string first, string second)
	{
		// visual-check (UIA scan): a TextBlock inside a panel does not name its button, so Narrator read "button"
		var named = Load(page).Descendants().Where(e => e.Name.Namespace == Presentation && ButtonTypes.Contains(e.Name.LocalName))
			.Where(b => (string?)b.Attribute("Content") is null && ContentElements(b).Any(e => e.Name.LocalName == "TextBlock") && ContentElements(b).Any(e => e.Name.LocalName != "TextBlock"))
			.Select(b => (Name: (string?)b.Attribute("AutomationProperties.Name"), Text: ContentElements(b).Where(e => e.Name.LocalName == "TextBlock").Select(t => (string?)t.Attribute("Text")).First()))
			.ToList();

		Assert.Equal(new (string?, string?)[] { (first, first), (second, second) }, named);
	}

	[Theory]
	[InlineData("FluentFin/Views/MoviePage.xaml")]
	[InlineData("FluentFin/Views/EpisodePage.xaml")]
	public void ItemPages_AudioAndSubtitleLists_AreNamedAfterTheirLabels(string page)
	{
		// visual-check (UIA scan): the lists sit beside a label TextBlock that does not name them
		var lists = Load(page).Descendants(Presentation + "ComboBox").ToList();

		Assert.Equal(new string?[] { "Audio", "Subtitle" }, lists.Select(c => (string?)c.Attribute("AutomationProperties.Name")));
		Assert.Equal("{x:Bind ViewModel.AudioStreams, Mode=OneWay}", (string?)lists[0].Attribute("ItemsSource"));
		Assert.StartsWith("{x:Bind ViewModel.SubtitleStreams,", (string?)lists[1].Attribute("ItemsSource"));
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
		var container = Assert.Single(Load("FluentFin/Views/LibrariesLandingPage.xaml").Descendants(Presentation + "ItemContainer"));

		Assert.Equal("{x:Bind Name}", (string?)container.Attribute("AutomationProperties.Name"));
	}

	[Fact]
	public void SavedServerTiles_AreNamed_AndALongNameIsTrimmed()
	{
		// visual-check: Narrator read the tile as "ItemContainer", and "Harness Jellyfin (fake)" was cut off at both ends
		var container = Assert.Single(Load("FluentFin/Views/SelectServerPage.xaml").Descendants(Presentation + "ItemContainer"));

		Assert.Equal("{x:Bind DisplayName}", (string?)container.Attribute("AutomationProperties.Name"));
		Assert.Equal("{x:Bind DisplayName}", (string?)container.Attribute("ToolTipService.ToolTip"));
		var name = Assert.Single(container.Descendants(Presentation + "TextBlock"));
		Assert.Equal("CharacterEllipsis", (string?)name.Attribute("TextTrimming"));
	}

	[Fact]
	public void RowTitle_IsAButtonAndAHeading_NotAPointerOnlyTextBlock()
	{
		var doc = Load("FluentFin/Controls/PagedScrollView.xaml");

		Assert.DoesNotContain(doc.Descendants(), e => e.Attribute("PointerReleased") is not null);
		var header = FindByName(doc, "HeaderButton");
		Assert.Equal("HeaderButton_Click", (string?)header.Attribute("Click"));
		Assert.Equal("Level2", (string?)header.Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("{x:Bind Header, Mode=OneWay}", (string?)header.Attribute("AutomationProperties.Name"));
	}

	[Fact]
	public void SeasonEpisodeRows_NameTheirButtonsForTheEpisode()
	{
		var season = Load("FluentFin/Views/SeasonPage.xaml").Descendants(Presentation + "ItemsRepeater")
			.Single(r => (string?)r.Attribute("ItemsSource") == "{x:Bind ViewModel.Episodes, Mode=OneWay}");
		Assert.Contains(season.Descendants(), e => e.Name.LocalName == "EpisodeRow");

		var row = Load("FluentFin/Controls/EpisodeRow.xaml");
		var buttons = row.Descendants().Where(e => e.Name.LocalName is "Button" or "ToggleButton").ToList();
		Assert.Equal(5, buttons.Count);
		Assert.All(buttons, b => Assert.False(string.IsNullOrWhiteSpace((string?)b.Attribute("AutomationProperties.Name"))));
		var code = File.ReadAllText(Path.Combine(RepoRoot, "FluentFin/Controls/EpisodeRow.xaml.cs"));
		Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(code, @"ItemLabels\.EpisodeAction\(""").Count);
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
