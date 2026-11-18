using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0: the user: "I HATE THE SIDE BAR", "REMEMBER MY HATRED OF THE SIDE BAR". Navigation is the top bar's tabs; these fail
/// if a left navigation rail or pane comes back anywhere: in the shell, in the window, in the styles, or on a new page.
/// </summary>
public sealed class NoLeftRailTests
{
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	/// <summary>Navigation-pane elements: a NavigationView (left pane by default), its items, a SplitView (a pane beside the content).</summary>
	private static readonly string[] PaneElements = ["NavigationView", "NavigationViewItem", "SplitView"];

	/// <summary>
	/// The only NavigationViews left: the server administration's and the Playback Reporting plugin's own section lists (owned
	/// by the admin stream and the plugin; flagged to the coordinator). A new one anywhere else fails here.
	/// </summary>
	private static readonly string[] KnownSectionLists =
	[
		"FluentFin/Views/JellyfinSettingsPage.xaml",
		"FluentFin.Plugins/Playback Reporting/Views/PlaybackReportingDashboardPage.xaml",
	];

	[Theory]
	[InlineData("FluentFin/Views/ShellPage.xaml")]
	[InlineData("FluentFin/MainWindow.xaml")]
	[InlineData("FluentFin/Views/TitleBarControl.xaml")]
	public void TheShellAndTheWindow_HaveNoNavigationPane(string file)
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, file));

		Assert.DoesNotContain(doc.Descendants(), e => PaneElements.Contains(e.Name.LocalName));
		Assert.DoesNotContain(doc.Descendants().Attributes(), a => a.Name.LocalName is "IsPaneVisible" or "PaneDisplayMode" or "OpenPaneLength" or "CompactPaneLength");
	}

	[Fact]
	public void NoStyle_IsARailOrALeftPane()
	{
		Assert.False(File.Exists(Path.Combine(RepoRoot, "FluentFin/Styles/NavigationView.xaml")), "the rail's styles are back");
		var dictionaries = Directory.GetFiles(Path.Combine(RepoRoot, "FluentFin/Styles"), "*.xaml").Append(Path.Combine(RepoRoot, "FluentFin/App.xaml"));

		foreach (var file in dictionaries)
		{
			var doc = XDocument.Load(file);
			Assert.DoesNotContain(doc.Descendants().Attributes(), a => a.Name.LocalName == "Key" && a.Value.Contains("Rail", StringComparison.OrdinalIgnoreCase));
			Assert.DoesNotContain(doc.Descendants(Presentation + "Style"), s => (string?)s.Attribute("TargetType") is "NavigationView" or "NavigationViewItem" or "SplitView");
			Assert.DoesNotContain(doc.Descendants(Presentation + "Setter"), s => (string?)s.Attribute("Property") == "PaneDisplayMode" && (string?)s.Attribute("Value") != "Top");
			Assert.DoesNotContain(doc.Descendants(Presentation + "ResourceDictionary"), d => ((string?)d.Attribute("Source"))?.Contains("NavigationView", StringComparison.OrdinalIgnoreCase) == true);
		}
	}

	[Fact]
	public void NoOtherPage_HasANavigationPane()
	{
		var withPanes = new[] { "FluentFin", "FluentFin.Plugins" }
			.SelectMany(dir => Directory.EnumerateFiles(Path.Combine(RepoRoot, dir), "*.xaml", SearchOption.AllDirectories))
			.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
				&& !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Where(f => XDocument.Load(f).Descendants().Any(e => PaneElements.Contains(e.Name.LocalName)))
			.Select(f => Path.GetRelativePath(RepoRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
			.Order(StringComparer.Ordinal)
			.ToList();

		Assert.Equal(KnownSectionLists.Order(StringComparer.Ordinal), withPanes);
	}

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
