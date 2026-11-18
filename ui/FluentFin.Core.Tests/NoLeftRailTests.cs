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
	/// Pages allowed a navigation pane: none. The last two, the server administration's and the Playback Reporting plugin's
	/// section lists, became tabs along the top in the admin stream (AdminNoSidePaneTests); a pane anywhere fails here.
	/// </summary>
	private static readonly string[] KnownSectionLists = [];

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

	[Theory]
	[InlineData("FluentFin/Views/ShellPage.xaml")]
	[InlineData("FluentFin/MainWindow.xaml")]
	public void TheShellAndTheWindow_LeaveNoLeftGutter(string file)
	{
		// the 1.2.0 release's gate: no column beside the pages, and nothing pushed in from the left edge only (a rail's room)
		var doc = XDocument.Load(Path.Combine(RepoRoot, file));
		Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "ColumnDefinition");
		foreach (var a in doc.Descendants().Attributes().Where(a => a.Name.LocalName is "Margin" or "Padding" && !a.Value.StartsWith('{')))
		{
			var parts = a.Value.Split(',').Select(p => double.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
			var (left, right) = parts.Length switch { 1 => (parts[0], parts[0]), 2 => (parts[0], parts[0]), _ => (parts[0], parts[2]) };
			Assert.True(left <= right, $"{file}: {a.Name.LocalName}=\"{a.Value}\" pushes the content in from the left");
		}
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
