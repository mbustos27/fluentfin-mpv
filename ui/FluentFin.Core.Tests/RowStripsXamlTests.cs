using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.7.4 (the rows stream): the season strips of a long show stay in reach. In a horizontal StackPanel, Cinematic's
/// season tabs and Drive-in's season tubes took all the width they wanted: a show of many seasons had its last seasons cut
/// off at the window's edge (and on Drive-in's series page "Season N details", the way to the season's page, with them).
/// Each strip now gets what the rest of its line leaves (LeadPanel) and scrolls in it, with the rows' pointer behaviour
/// (EdgeScroller). What the running app does with each input is the off-screen gate's (tools\ui-harness\rows-gate.ps1).
/// </summary>
public sealed class RowStripsXamlTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	private static XDocument Load(string relative) => XDocument.Load(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, relative));

	private static string Code(string relative) => File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, relative));

	private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

	[Theory]
	[InlineData("FluentFin/Themes/Cinematic/Views/CinematicSeasonPage.xaml")]
	[InlineData("FluentFin/Themes/Cinematic/Views/CinematicSeriesPage.xaml")]
	public void CinematicSeasonTabs_GetTheWidthTheirLineLeaves(string page)
	{
		var doc = Load(page);
		var row = Named(doc, "TabsRow");
		Assert.Equal("LeadPanel", row.Name.LocalName);
		// the tabs lead (they give way), the season's count after them at its own width
		Assert.Equal("CinematicTabStrip", row.Elements().First().Name.LocalName);
		Assert.Contains("EdgeScroller.Wrap(", Code("FluentFin/Themes/Cinematic/Controls/CinematicTabStrip.cs"));
	}

	[Theory]
	[InlineData("FluentFin/Themes/DriveIn/Views/DriveInSeasonPage.xaml")]
	[InlineData("FluentFin/Themes/DriveIn/Views/DriveInSeriesPage.xaml")]
	public void DriveInSeasonTubes_ScrollInTheWidthTheirLineLeaves(string page)
	{
		var doc = Load(page);
		var tubes = Named(doc, "SeasonTubes");
		var scroller = tubes.Parent!;
		Assert.Equal("ScrollViewer", scroller.Name.LocalName);
		Assert.Equal("Enabled", (string?)scroller.Attribute("HorizontalScrollMode"));
		// the scroller with the rows' arrows over it (its host), in a star column of the lead of a LeadPanel, which sits in a
		// star column of the line
		var host = scroller.Parent!;
		Assert.Equal("SeasonTubesHost", (string?)host.Attribute(Xaml + "Name"));
		Assert.Equal(["SeasonTubesBack", "SeasonTubesNext"], host.Elements(Presentation + "Button").Select(b => (string?)b.Attribute(Xaml + "Name")));
		Assert.All(host.Elements(Presentation + "Button"), b => Assert.Equal(("False", "Collapsed"), ((string?)b.Attribute("IsTabStop"), (string?)b.Attribute("Visibility"))));
		var lead = host.Parent!;
		Assert.Equal("*", (string?)lead.Element(Presentation + "Grid.ColumnDefinitions")!.Elements().ElementAt(int.Parse((string?)host.Attribute("Grid.Column") ?? "0")).Attribute("Width"));
		var panel = lead.Parent!;
		Assert.Equal("LeadPanel", panel.Name.LocalName);
		Assert.Same(lead, panel.Elements().First());
		var line = panel.Parent!;
		Assert.Equal("*", (string?)line.Element(Presentation + "Grid.ColumnDefinitions")!.Elements().ElementAt(int.Parse((string?)panel.Attribute("Grid.Column") ?? "0")).Attribute("Width"));
		Assert.Contains("new FluentFin.Controls.EdgeScroller(SeasonTubesHost, SeasonTubesScroller, SeasonTubes, SeasonTubesBack, SeasonTubesNext,", Code(page + ".cs"));
	}

	// Editorial's Home fits its genre tabs to the strip: only when the genres came, so a window made narrower (or genres that
	// came before the strip had a width) left every tab there, the last ones cut off where the keyboard still went
	[Fact]
	public void EditorialHomeGenreTabs_AreFittedWheneverTheStripsWidthChanges() =>
		Assert.Matches(@"GenreTabs\.SizeChanged \+= \(_, e\) =>\s*\{\s*if \(Math\.Abs\(e\.NewSize\.Width - e\.PreviousSize\.Width\) >= 1\)\s*\{\s*FitGenres\(\);",
			Code("FluentFin/Themes/Editorial/Views/EditorialHomePage.xaml.cs"));

	[Fact]
	public void TheLibraryPresets_HaveTheRowsPointerBehaviour() =>
		Assert.Contains("EdgeScroller.Wrap(Scroller, Strip,", Code("FluentFin/Controls/LibraryPresetStrip.xaml.cs"));
}
