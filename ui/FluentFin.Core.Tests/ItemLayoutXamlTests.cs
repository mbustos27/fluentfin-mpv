using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, layout items 1, 3, 8, 9, 13, 14, 21 and 23), read from the XAML and the code behind it: a
/// full-bleed Home hero, item pages in plain sections, the hero's facts as text and its height from the window, the series
/// page's episode list, one badge, one name for Next Up.
/// </summary>
public sealed class ItemLayoutXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	public static TheoryData<string> ItemPages => ["MoviePage", "SeriesPage", "SeasonPage", "EpisodePage"];

	[Theory]
	[MemberData(nameof(ItemPages))]
	public void ItemPages_HaveNoGlassPanelPerSection(string page)
	{
		var doc = Load($"FluentFin/Views/{page}.xaml");

		Assert.DoesNotContain(doc.Descendants(), e => ((string?)e.Attribute("Style"))?.Contains("GlassSurfaceStyle") == true);
	}

	[Theory]
	[InlineData("MoviePage")]
	[InlineData("SeriesPage")]
	public void MoreLikeThis_IsNotDrawnWhenEmpty(string page)
	{
		var row = Load($"FluentFin/Views/{page}.xaml").Descendants().Single(e => e.Name.LocalName == "ShelfRow" && (string?)e.Attribute("Header") == "More like this");

		Assert.Equal("{x:Bind c:ItemRowConverters.Show(ViewModel.Similar.Count), Mode=OneWay, FallbackValue=Collapsed}", (string?)row.Attribute("Visibility"));
	}

	[Fact]
	public void EpisodePage_SaysUpNext_InSentenceCase_AndItsSectionIsHiddenWithoutOne()
	{
		// Nixie 1.4.0 (the UX review's X12): one label for the next episode everywhere, "Up next" (it was also "Next up",
		// "Next Up" and "Coming up")
		var doc = Load("FluentFin/Views/EpisodePage.xaml");
		var heading = doc.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "Up next");

		Assert.Equal("{x:Bind ViewModel.HasNextEpisode, Mode=OneWay}", (string?)heading.Parent!.Attribute("Visibility"));
		Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("Text") is "Next up" or "Next Up");
	}

	/// <summary>Nixie 1.4.0 (X12): every theme's Home and episode page says "Up next", never "Next Up", "Next up" or "Coming up".</summary>
	[Theory]
	[InlineData("FluentFin/Views/HomePage.xaml")]
	[InlineData("FluentFin/Views/EpisodePage.xaml")]
	[InlineData("FluentFin/Themes/DriveIn/Views/DriveInHomePage.xaml")]
	[InlineData("FluentFin/Themes/DriveIn/Controls/ComingUpSign.xaml")]
	[InlineData("FluentFin/Themes/DriveIn/Views/DriveInEpisodePage.xaml")]
	[InlineData("FluentFin/Themes/Cinematic/Views/CinematicHomePage.xaml")]
	[InlineData("FluentFin/Themes/Cinematic/Views/CinematicEpisodePage.xaml")]
	[InlineData("FluentFin/Themes/Editorial/Views/EditorialHomePage.xaml")]
	[InlineData("FluentFin/Themes/Editorial/Views/EditorialEpisodePage.xaml")]
	public void TheNextEpisode_IsUpNext_Everywhere(string file)
	{
		var words = Load(file).Descendants().SelectMany(e => e.Attributes()).Where(a => a.Name.LocalName is "Text" or "Header" or "AutomationProperties.Name").Select(a => a.Value).ToList();
		Assert.DoesNotContain(words, w => w.Equals("Next up", StringComparison.OrdinalIgnoreCase) || w.Equals("Coming up", StringComparison.OrdinalIgnoreCase));
		Assert.Contains(words, w => w.Equals("Up next", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void ItemHero_ShowsTheFactsAsText_NotChips_AndNoOverline()
	{
		var hero = Load("FluentFin/Controls/ItemHero.xaml");
		var code = Code("FluentFin/Controls/ItemHero.xaml.cs");

		Assert.DoesNotContain(hero.Descendants(), e => (string?)e.Attribute(Xaml + "Name") is "ChipRow" or "Eyebrow");
		Assert.Equal("TextBlock", FindByName(hero, "MetaText").Name.LocalName);
		Assert.Contains("MetaText.Text = ItemChips.MetaLine(dto);", code);
		Assert.Contains("AutomationProperties.SetName(MetaText, ItemChips.MetaName(dto));", code);
	}

	[Fact]
	public void ItemHero_HasNoEmptyHeading_WhenThePageFailedToLoad()
	{
		// the logo and the title (both Level 1 headings) show only with a title (ItemHeroHeadingTests has the rule)
		var hero = Load("FluentFin/Controls/ItemHero.xaml");
		var code = Code("FluentFin/Controls/ItemHero.xaml.cs");

		Assert.Equal("Level1", (string?)FindByName(hero, "LogoImage").Attribute("AutomationProperties.HeadingLevel"));
		Assert.Equal("Level1", (string?)FindByName(hero, "TitleText").Attribute("AutomationProperties.HeadingLevel"));
		Assert.Contains("LogoImage.Visibility = HeadingVisibility(hasLogo: logo is not null, title, forLogo: true);", code);
		Assert.Contains("TitleText.Visibility = HeadingVisibility(hasLogo: logo is not null, title, forLogo: false);", code);
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"(LogoImage|TitleText)\.Visibility = "), m => m.Value.StartsWith("LogoImage"));
		Assert.Single(System.Text.RegularExpressions.Regex.Matches(code, @"(LogoImage|TitleText)\.Visibility = "), m => m.Value.StartsWith("TitleText"));
	}

	[Fact]
	public void ItemHero_HeightIsAShareOfTheWindow()
	{
		var hero = Load("FluentFin/Controls/ItemHero.xaml");
		var code = Code("FluentFin/Controls/ItemHero.xaml.cs");

		Assert.NotEqual("{StaticResource ItemHeroHeight}", (string?)FindByName(hero, "HeroGrid").Attribute("Height"));
		Assert.Contains("HeroGrid.Height = height;", code);
		Assert.Contains("var height = HeroLayout.Height(XamlRoot?.Size.Height ?? 0, HeightShare);", code);
		Assert.Contains("root.Changed += OnRootChanged;", code);
	}

	[Theory]
	[InlineData("SeriesPage", "0.45")]
	[InlineData("SeasonPage", "0.45")]
	public void SeriesAndSeasonHeroes_LeaveRoomForTheEpisodes(string page, string share)
	{
		var hero = Load($"FluentFin/Views/{page}.xaml").Descendants().Single(e => e.Name.LocalName == "ItemHero");

		Assert.Equal(share, (string?)hero.Attribute("HeightShare"));
		Assert.Equal(HeroLayout.EpisodesShare, double.Parse(share, System.Globalization.CultureInfo.InvariantCulture));
	}

	[Fact]
	public void SeriesPage_ListsEpisodesUnderTheHero_WithASeasonPickerThatSwitchesInPlace()
	{
		var doc = Load("FluentFin/Views/SeriesPage.xaml");
		var body = doc.Descendants().First(e => e.Name.LocalName == "StackPanel" && (string?)e.Attribute("Margin") == "48,28,48,56");

		// the episodes come first under the hero
		var first = body.Elements().First();
		var list = first.Descendants().Single(e => e.Name.LocalName == "EpisodeList");
		Assert.Equal("{x:Bind ViewModel.Episodes, Mode=OneWay}", (string?)list.Attribute("ItemsSource"));
		Assert.Contains(list.Descendants(), e => e.Name.LocalName == "EpisodeRow");
		// Nixie 1.4.1: an ItemsControl (new buttons for each list of pills: the ItemsRepeater kept the first season marked)
		var picker = first.Descendants().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SeasonPicker");
		Assert.Equal("ItemsControl", picker.Name.LocalName);
		Assert.Equal("{x:Bind ViewModel.SeasonPills, Mode=OneWay}", (string?)picker.Attribute("ItemsSource"));
		var pill = picker.Descendants().Single(e => e.Name.LocalName == "Button");
		Assert.Equal("{Binding ElementName=Page, Path=ViewModel.SelectSeasonCommand}", (string?)pill.Attribute("Command"));
		// the pills switch in place, so the series page's way to a season's own page is the link beside them
		var link = FindByName(doc, "SeasonDetailsLink");
		Assert.Equal("HyperlinkButton", link.Name.LocalName);
		Assert.Same(picker.Parent, link.Parent);
		Assert.Equal("{x:Bind root:App.Commands.DisplayDtoCommand}", (string?)link.Attribute("Command"));
		Assert.Equal("{x:Bind ViewModel.SelectedSeason, Mode=OneWay}", (string?)link.Attribute("CommandParameter"));
		Assert.Equal("{x:Bind ViewModel.SeasonDetailsText, Mode=OneWay}", (string?)link.Attribute("Content"));
		// no small season posters and no lone Next Up card any more
		Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "ShelfRow" && (string?)e.Attribute("Header") == "Seasons");
		Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute("Model") == "{x:Bind ViewModel.NextUp, Mode=OneWay}");
	}

	[Fact]
	public void HomeHero_IsFullBleed_WithTheWordsOnTheArtwork()
	{
		var hero = Load("FluentFin/Controls/HomeHero.xaml");
		var root = FindByName(hero, "Root");
		var code = Code("FluentFin/Controls/HomeHero.xaml.cs");

		// no inset card (rounded corners) and no frosted panel inside it
		Assert.Null(root.Attribute("CornerRadius"));
		Assert.DoesNotContain(hero.Descendants(), e => ((string?)e.Attribute("Style"))?.Contains("GlassSurfaceStyle") == true);
		// the item hero's scrims
		var scrims = hero.Descendants().Where(e => e.Name.LocalName == "Border").Select(b => (string?)b.Attribute("Background")).ToList();
		Assert.Contains("{ThemeResource ItemHeroSideScrimBrush}", scrims);
		Assert.Contains("{ThemeResource GlassImageScrimBrush}", scrims);
		// the words straight on it; More info a glass pill (the one glass layer)
		Assert.Same(root, FindByName(hero, "Words").Parent);
		Assert.Equal("{StaticResource NixieButtonStyle}", (string?)FindByName(hero, "MoreInfoButton").Attribute("Style"));
		// a share of the window's height
		Assert.Contains("HeroLayout.Height(XamlRoot?.Size.Height ?? 0, HeroLayout.HomeShare)", code);
	}

	[Fact]
	public void CardMarks_AreTwoNeutralMarks_AndEveryWatchedMarkIsTheSame()
	{
		// 1.4.0 (the library UX review, X10): one pink capsule meant both "watched" and "N to watch"; now a dim neutral check top
		// right and a neutral square count top left, the same watched mark on every card, still and row, never the accent
		var card = Load("FluentFin/Controls/BaseItemDtoCard.xaml");
		Assert.DoesNotContain(card.Descendants(), e => e.Name.LocalName == "InfoBadge");
		Assert.Equal("{StaticResource NixieCountChipStyle}", (string?)FindByName(card, "CountChip").Attribute("Style"));
		foreach (var file in new[] { "FluentFin/Controls/BaseItemDtoCard.xaml", "FluentFin/Controls/EpisodeRow.xaml", "FluentFin/Themes/Cinematic/Controls/CinematicStill.xaml",
			"FluentFin/Themes/Editorial/Controls/EditorialPoster.xaml", "FluentFin/Themes/Editorial/Controls/EditorialStill.xaml" })
		{
			var mark = FindByName(Load(file), "WatchedMark");
			Assert.Equal("{StaticResource NixieWatchedBadgeStyle}", (string?)mark.Attribute("Style"));
			Assert.Null(mark.Attribute("Background"));
			Assert.Equal("{StaticResource NixieWatchedGlyphStyle}", (string?)mark.Descendants().Single(e => e.Name.LocalName == "FontIcon").Attribute("Style"));
		}
	}

	[Fact]
	public void EpisodeRow_TitleIsTheOneFormat_WithTheRuntimeOnItsLine()
	{
		var code = Code("FluentFin/Controls/EpisodeRow.xaml.cs");

		Assert.Contains("TitleText.Text = ItemRowConverters.EpisodeTitle(dto);", code);
		Assert.Contains("public static string EpisodeTitle(BaseItemDto? episode) => ItemLabels.EpisodeTitle(episode);", Code("FluentFin.Core/ItemMeta.cs"));
		var row = Load("FluentFin/Controls/EpisodeRow.xaml");
		Assert.Same(FindByName(row, "TitleLink").Parent, FindByName(row, "RuntimeText").Parent);
	}

	[Fact]
	public void TheConverters_UseTheOneEpisodeFormat()
	{
		var converters = Code("FluentFin/Converters/BaseItemDtoConverters.cs") + Code("FluentFin/Converters/JellyfinTitleConverter.cs");

		Assert.DoesNotContain(":E{", converters);
		Assert.Contains("return ItemLabels.EpisodeTitle(dto);", converters);
		Assert.Contains("return ItemLabels.EpisodeCode(dto);", converters);
	}

	[Fact]
	public void TheHerosMoreMenu_LeavesOutWatchedAndFavorite_WhichAreButtonsBesideIt()
	{
		// 1.2.0 merge (items x library): the menu itself without the toggles is the library's ItemMenu test of includeToggles
		Assert.Contains("new JellyfinFlyoutConverter { IncludeToggles = false }.Convert(dto", Code("FluentFin/Controls/ItemHero.xaml.cs"));
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
