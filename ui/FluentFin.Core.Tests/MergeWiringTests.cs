using System.Xml.Linq;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// The 1.2.0 release merge: the hooks one stream left for another, wired when both were in (BRIEF.md's cross-stream
/// wiring). Each fails if a later change unwires it.
/// </summary>
public sealed class MergeWiringTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	[Fact]
	public void TheTopBar_IsGlassTitleBarGlass_WithTheWordmark_AndGlassTabBrushes()
	{
		var bar = Load("FluentFin/Views/TitleBarControl.xaml");
		// Nixie 1.3.0: the glass is the bar's chrome's (TopBarChrome), 1.2.0's in its default style, which Classic keeps; drawn
		// behind the bar (a sibling before it, never around it), its Padding the bar's inset
		var chromeElement = Assert.Single(bar.Descendants(), e => e.Name.LocalName == "TopBarChrome" && (string?)e.Attribute(Xaml + "Name") == "BarChrome");
		Assert.Empty(chromeElement.Elements());
		Assert.Equal("TitleBar", chromeElement.ElementsAfterSelf().First().Name.LocalName);
		Assert.Equal("{x:Bind BarChrome.Padding, Mode=OneWay}", (string?)chromeElement.ElementsAfterSelf().First().Attribute("Margin"));
		Assert.Contains("<ResourceDictionary Source=\"/Styles/TopBarChrome.xaml\" />", Code("FluentFin/Themes/Generic.xaml"));
		var chrome = Load("FluentFin/Styles/TopBarChrome.xaml");
		var glass = chrome.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "BarGlass");
		Assert.Equal("{ThemeResource GlassTitleBarBrush}", (string?)glass.Attribute("Background"));
		Assert.Equal("{ThemeResource GlassTitleBarStrokeBrush}", (string?)glass.Attribute("BorderBrush"));
		Assert.Contains(bar.Descendants(), e => e.Name.LocalName == "AppWordmark" && (string?)e.Attribute("Size") == "15");

		var tabs = Code("FluentFin/Controls/TopTabStrip.xaml");
		Assert.Contains("{ThemeResource NixieTabForegroundBrush}", tabs);
		// Nixie 1.4.0 (CL-m11): hover is a subtle fill, not a line where the underline goes
		Assert.DoesNotContain("HoverLine", tabs);
		Assert.Contains("{ThemeResource SubtleFillColorSecondaryBrush}", tabs);
		Assert.Contains("{ThemeResource GlassTabUnderlineBrush}", tabs);
	}

	[Theory]
	[InlineData("FluentFin/Views/LoginPage.xaml")]
	[InlineData("FluentFin/Views/SelectServerPage.xaml")]
	public void SignIn_ShowsTheLitWordmark_AsThePagesHeading(string page)
	{
		var mark = Assert.Single(Load(page).Descendants(), e => e.Name.LocalName == "AppWordmark");
		Assert.Equal("64", (string?)mark.Attribute("Size"));
		Assert.Equal("Level1", (string?)mark.Attribute("AutomationProperties.HeadingLevel"));
		// the wordmark is a Grid (no automation peer): the heading is its text's
		Assert.Contains("AutomationProperties.SetHeadingLevel(_text, Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(this))", Code("FluentFin/Controls/AppWordmark.cs"));
	}

	[Theory]
	[InlineData("HomePage")]
	[InlineData("MoviePage")]
	[InlineData("SeriesPage")]
	[InlineData("SeasonPage")]
	[InlineData("EpisodePage")]
	public void HeroPages_RunUnderTheTopBar(string page) =>
		Assert.Equal("True", (string?)Load($"FluentFin/Views/{page}.xaml").Root!.Attributes().SingleOrDefault(a => a.Name.LocalName == "TopBar.ExtendsUnder")?.Value);

	[Fact]
	public void Settings_HasTheContinueWatchingCleanup()
	{
		var combo = Assert.Single(Load("FluentFin/Views/SettingsPage.xaml").Descendants(), e => e.Name.LocalName == "ComboBox" && (string?)e.Attribute("ItemsSource") == "{x:Bind ContinueWatching.Choices}");
		Assert.Equal("{x:Bind ContinueWatching.Selected, Mode=TwoWay}", (string?)combo.Attribute("SelectedItem"));
		Assert.Equal("Label", (string?)combo.Attribute("DisplayMemberPath"));
		Assert.Contains("public ContinueWatchingSettingViewModel ContinueWatching { get; }", Code("FluentFin/Views/SettingsPage.xaml.cs"));
	}

	[Fact]
	public void TheHeroAndContinueWatching_WriteTheOneEpisodeFormat()
	{
		var episode = new BaseItemDto { Type = BaseItemDto_Type.Episode, SeriesName = "Lost", ParentIndexNumber = 2, IndexNumber = 5, IndexNumberEnd = 6 };

		Assert.StartsWith("S2 E5–E6", HeroSlides.Meta(episode));
		Assert.Equal("Lost S2 E5–E6", ContinueWatchingRules.NameOf(episode));
	}

	[Fact]
	public void TheLibrariesPage_HasPosterLikeTiles_ThatFillTheRow()
	{
		// the 1.1.3 layout audit's item 23 (an unowned page, applied at the merge): no solid name strip, art not stretched
		var page = Load("FluentFin/Views/LibrariesLandingPage.xaml");
		Assert.DoesNotContain(page.Descendants().Attributes("Background"), a => a.Value.Contains("CardBackgroundFillColorDefaultBrush"));
		// 1.6 topbar: two tile templates (the media library's with its star, the collections group's)
		Assert.All(page.Descendants().Where(e => e.Name.LocalName == "LoadingImage"), image => Assert.Equal("UniformToFill", (string?)image.Attribute("Stretch")));
		// Nixie 1.4.0: two groups; each tile sized by the page to share the rows evenly, its art kept 16:9 (PageGridMetrics.Tiles, TileHeight)
		Assert.All(page.Descendants().Where(e => e.Name.LocalName == "UniformGridLayout"), l => Assert.Equal("None", (string?)l.Attribute("ItemsStretch")));
		Assert.Contains("PageGridMetrics.TileHeight(tile)", Code("FluentFin/Views/LibrariesLandingPage.xaml.cs"), StringComparison.Ordinal);
	}

	private static XDocument Load(string file) => XDocument.Load(Path.Combine(RepoRoot, file));

	private static string Code(string file) => File.ReadAllText(Path.Combine(RepoRoot, file));

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
