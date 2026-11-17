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

		Assert.Contains("SetPlayed(Model.Dto, MarkWatchedButton.IsChecked == true)", code);
		Assert.Contains("SetIsFavorite(Model.Dto, AddToFavoriteButton.IsChecked == true)", code);
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
