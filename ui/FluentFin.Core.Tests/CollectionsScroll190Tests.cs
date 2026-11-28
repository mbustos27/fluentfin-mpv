using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the scroll stream; the owner on the 1.9 test copy: "scrolling the collections page takes a longgg time"): no
/// picture may hear every layout pass. ImageEx's lazy loading (DevWinUI's ImageExBase, from the toolkit) adds a LayoutUpdated
/// handler when EnableLazyLoading turns true and removes it only when it turns false, loaded or not; on every layout pass
/// anywhere in the window each picture walks up the tree to its scroll viewer and measures itself against it. The Collections
/// page has five a tile (237 on the owner-shaped page): while it scrolled, about 40 layout passes a second each ran 237 walks,
/// and its frames took twice as long (the scroll gate's frame clock, Classic at 2560 x 1392: p95 12 ms with it, 7 without;
/// longest 17 to 20 ms, 8 to 10). Read from the XAML and the code (no WinUI needed); the frames themselves are scroll190's.
/// </summary>
public sealed class CollectionsScroll190Tests
{
	private static IEnumerable<string> AppFiles(string pattern)
	{
		var app = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin");
		return Directory.EnumerateFiles(app, pattern, SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
	}

	[Fact]
	public void TheLoadingImageStyle_TurnsImageExsLazyLoadingOff()
	{
		var doc = XDocument.Load(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Styles", "ImageEx.xaml"));
		var style = doc.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "controls:LoadingImage");
		var lazy = style.Elements().SingleOrDefault(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "EnableLazyLoading");
		Assert.True(lazy is null || string.Equals((string?)lazy.Attribute("Value"), "False", StringComparison.OrdinalIgnoreCase),
			$"the LoadingImage style turns lazy loading on ({lazy}): every picture would hear every layout pass");
	}

	[Fact]
	public void NoPictureInTheApp_TurnsLazyLoadingOn()
	{
		var on = new List<string>();
		foreach (var file in AppFiles("*.xaml"))
		{
			var text = File.ReadAllText(file);
			if (System.Text.RegularExpressions.Regex.IsMatch(text, "EnableLazyLoading\\s*=\\s*\"True\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
				|| System.Text.RegularExpressions.Regex.IsMatch(text, "Property=\"EnableLazyLoading\"\\s+Value=\"True\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
			{
				on.Add(Path.GetFileName(file));
			}
		}

		foreach (var file in AppFiles("*.cs"))
		{
			if (System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(file), "EnableLazyLoading\\s*=\\s*true"))
			{
				on.Add(Path.GetFileName(file));
			}
		}

		Assert.Empty(on);
	}
}
