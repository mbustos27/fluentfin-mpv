using System.Xml.Linq;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.7 (upnav): the item page's trail over its title in every theme, "Shows › What We Do in the Shadows › Season 3 ›":
/// real buttons in the keyboard's order (the library, the series, the season, then the title's actions), named for Narrator,
/// on one line that trims the series' name rather than wrapping over the title.
/// </summary>
public sealed class TrailEyebrowTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	private static string Root => Path.GetDirectoryName(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")))!;

	private static XDocument Xaml(string relative) => XDocument.Parse(File.ReadAllText(Path.Combine(Root, relative)));

	private static string? NameOf(XElement e) => (string?)e.Attribute(X + "Name");

	// ---- the line

	[Fact]
	public void CrumbsThatFit_KeepTheirWidths()
	{
		Assert.Equal([70, 260, 90], TrailPanel.Fit([70, 260, 90], 800));
		Assert.Equal([70, 260, 90], TrailPanel.Fit([70, 260, 90], double.PositiveInfinity));
		Assert.Equal([0, 260, 90], TrailPanel.Fit([0, 260, 90], 350));
	}

	[Fact]
	public void TooWide_TheSeriesGivesWayFirst_ThenTheSeason_ThenTheLibrary_NoneBelowTheFloor()
	{
		// 100 too wide: the series' name trims
		Assert.Equal([70, 160, 90], TrailPanel.Fit([70, 260, 90], 320));
		// the series down to the floor, then the season
		Assert.Equal([70, TrailPanel.MinCrumb, 80], TrailPanel.Fit([70, 260, 90], 70 + TrailPanel.MinCrumb + 80));
		// all at the floor: the line stays as narrow as it can (the host's width is far more than this in every theme)
		Assert.Equal([TrailPanel.MinCrumb, TrailPanel.MinCrumb, TrailPanel.MinCrumb], TrailPanel.Fit([70, 260, 90], 10));
		// a hidden crumb (no library crumb: reached from the item's own library) stays hidden
		Assert.Equal([0, 230, 90], TrailPanel.Fit([0, 260, 90], 320));
	}

	// ---- the buttons, their order and their names

	[Fact]
	public void TheTrail_IsTheLibrary_TheSeries_ThenTheSeason_EachAButtonTheKeyboardReaches()
	{
		var doc = Xaml(Path.Combine("Controls", "TrailEyebrow.xaml"));
		var panel = doc.Descendants(Ui + "TrailPanel").Concat(doc.Descendants().Where(e => e.Name.LocalName == "TrailPanel")).First();
		var buttons = panel.Elements().ToList();

		Assert.Equal(["LibraryButton", "SeriesButton", "SeasonButton"], buttons.Select(NameOf));
		Assert.All(buttons, b => Assert.Equal("Button", b.Name.LocalName));
		Assert.Equal(["TrailEyebrow", "TrailSeries", "TrailSeason"], buttons.Select(b => (string?)b.Attribute("AutomationProperties.AutomationId")));
		// nothing takes them out of the Tab order or reorders them
		Assert.All(buttons, b => Assert.Null(b.Attribute("IsTabStop")));
		Assert.All(buttons, b => Assert.Null(b.Attribute("TabIndex")));
		Assert.DoesNotContain("TabIndex", File.ReadAllText(Path.Combine(Root, "Controls", "TrailEyebrow.xaml")));
		// each opens its own page
		Assert.Equal(["OnLibraryClick", "OnSeriesClick", "OnSeasonClick"], buttons.Select(b => (string?)b.Attribute("Click")));
		// radius 8, no pill
		var style = doc.Descendants(Ui + "Style").Single(s => (string?)s.Attribute(X + "Key") == "TrailCrumbStyle");
		Assert.Contains(style.Elements(), s => (string?)s.Attribute("Property") == "CornerRadius" && (string?)s.Attribute("Value") == "8");
		Assert.All(buttons, b => Assert.Equal("{StaticResource TrailCrumbStyle}", (string?)b.Attribute("Style")));
	}

	[Fact]
	public void EachLabel_TrimsOnOneLine_AndSaysItselfWholeInItsTooltip()
	{
		var doc = Xaml(Path.Combine("Controls", "TrailEyebrow.xaml"));
		var label = doc.Descendants(Ui + "Style").Single(s => (string?)s.Attribute(X + "Key") == "TrailCrumbLabelStyle");
		string? Setter(string property) => (string?)label.Elements().SingleOrDefault(s => (string?)s.Attribute("Property") == property)?.Attribute("Value");

		Assert.Equal(("CharacterEllipsis", "NoWrap", "1"), (Setter("TextTrimming"), Setter("TextWrapping"), Setter("MaxLines")));
		var labels = doc.Descendants(Ui + "TextBlock").Where(t => NameOf(t) is "LibraryLabel" or "SeriesLabel" or "SeasonLabel").ToList();
		Assert.Equal(3, labels.Count);
		Assert.All(labels, t => Assert.Equal("OnLabelTrimmed", (string?)t.Attribute("IsTextTrimmedChanged")));
		// the label takes the room left (a star column), so the line's width trims it; the chevron is decoration
		Assert.All(labels, t => Assert.Equal("*", (string?)t.Parent!.Element(Ui + "Grid.ColumnDefinitions")!.Elements().First().Attribute("Width")));
		Assert.All(doc.Descendants(Ui + "FontIcon"), i => Assert.Equal("Raw", (string?)i.Attribute("AutomationProperties.AccessibilityView")));
	}

	private static TrailEyebrow.CrumbView? C(string text, string name) => new(text, name);

	private static TrailEyebrow.CrumbView?[] Row(TrailEyebrow.CrumbView? library, TrailEyebrow.CrumbView? series, TrailEyebrow.CrumbView? season) => [library, series, season];

	[Fact]
	public void EachCrumb_ShowsItsWords_AndIsNamedForNarrator()
	{
		var episode = new BaseItemDto
		{
			Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = Guid.NewGuid(), SeriesName = "What We Do in the Shadows",
			SeasonId = Guid.NewGuid(), ParentIndexNumber = 3,
		};
		var season = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, SeriesId = Guid.NewGuid(), SeriesName = "Iron Lighthouses" };

		// an episode from Home: "Shows › What We Do in the Shadows › Season 3"
		Assert.Equal(Row(C("Shows", "Shows"), C("What We Do in the Shadows", "Go to series What We Do in the Shadows"), C("Season 3", "Go to Season 3")),
			TrailEyebrow.Views("Shows", ItemTrail.From(episode)));
		// from its own library: no library crumb, the series and season still
		Assert.Equal(Row(null, C("What We Do in the Shadows", "Go to series What We Do in the Shadows"), C("Season 3", "Go to Season 3")),
			TrailEyebrow.Views(null, ItemTrail.From(episode)));
		// a season: its series only
		Assert.Equal(Row(null, C("Iron Lighthouses", "Go to series Iron Lighthouses"), null), TrailEyebrow.Views(null, ItemTrail.From(season)));
		// a film from Home: the library only; a page with none: nothing (the eyebrow collapses)
		Assert.Equal(Row(C("Movies", "Movies"), null, null), TrailEyebrow.Views("Movies", ItemTrail.None));
		Assert.All(TrailEyebrow.Views(null, ItemTrail.None), v => Assert.Null(v));
	}

	[Theory]
	// the item pages' headers in every theme: Classic's hero, Drive-in's board, Cinematic's stage, Editorial's header
	[InlineData("Controls/ItemHero.xaml")]
	[InlineData("Themes/DriveIn/Controls/ItemBoard.xaml")]
	[InlineData("Themes/Cinematic/Controls/CinematicItemStage.xaml")]
	[InlineData("Themes/Editorial/Controls/EditorialItemHeader.xaml")]
	public void InEveryTheme_TheTrailComesBeforeTheTitlesActions_InTheKeyboardsOrder(string header)
	{
		var doc = Xaml(header);
		var all = doc.Descendants().ToList();
		var trail = all.FindIndex(e => e.Name.LocalName == "TrailEyebrow");
		var actions = all.FindIndex(e => NameOf(e) == "ButtonStrip");
		var primary = all.FindIndex(e => NameOf(e) == "PrimaryPresenter");

		Assert.True(trail >= 0, $"{header}: no trail");
		Assert.True(trail < actions && trail < primary, $"{header}: the trail after the actions");
		// nothing focusable before it in the header: the first stop of the page is the trail
		Assert.DoesNotContain(all.Take(trail), e => e.Name.LocalName is "Button" or "HyperlinkButton" or "ToggleButton" or "ContentPresenter");
		Assert.DoesNotContain(all, e => e.Attribute("TabIndex") is not null);
	}
}
