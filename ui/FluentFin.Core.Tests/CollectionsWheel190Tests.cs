using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the review of the ui stream; the bug is older than 1.9): a page whose newer ScrollView holds rows that scroll
/// sideways (ShelfRow, the presets strip, Cinematic's tab strip: EdgeScroller) must leave the mouse wheel to them. The
/// ScrollView took the wheel itself, so EdgeScroller's hand-over (the page while it can move, then the row under the pointer)
/// never ran: at the Collections page's end the wheel over the Franchises or Keywords row moved nothing (the c190 gate's
/// rows190, 58/60). Read from the XAML and the code-behind (no WinUI needed); the wheel itself is rows190's.
/// </summary>
public sealed class CollectionsWheel190Tests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly string[] SidewaysRows = ["ShelfRow", "LibraryPresetStrip", "CinematicTabStrip"];

	// every XAML file of the app with a ScrollView that holds a row EdgeScroller drives
	private static IEnumerable<(string File, XElement View)> ScrollViewsWithRows()
	{
		var app = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin");
		foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
		{
			XDocument doc;
			try
			{
				doc = XDocument.Load(file);
			}
			catch (System.Xml.XmlException)
			{
				continue;
			}

			foreach (var view in doc.Descendants().Where(e => e.Name.LocalName == "ScrollView"))
			{
				if (view.Descendants().Any(e => SidewaysRows.Contains(e.Name.LocalName)))
				{
					yield return (Path.GetRelativePath(app, file), view);
				}
			}
		}
	}

	[Fact]
	public void AScrollViewHoldingSidewaysRows_LeavesTheWheelToThem_AndItsPageScrollsTheRest()
	{
		var found = ScrollViewsWithRows().ToList();
		Assert.Contains(found, x => x.File.EndsWith("CollectionsPage.xaml", StringComparison.Ordinal)); // the page the bug was on

		foreach (var (file, view) in found)
		{
			var ignored = ((string?)view.Attribute("IgnoredInputKinds") ?? "").Split(',', StringSplitOptions.TrimEntries);
			Assert.True(ignored.Contains("MouseWheel") || ignored.Contains("All"), $"{file}: its ScrollView takes the wheel from its rows");

			// and the page scrolls itself by a wheel no row took (else the wheel over a heading or a gap moves nothing)
			var name = (string?)view.Attribute(Xaml + "Name");
			Assert.False(string.IsNullOrEmpty(name), $"{file}: its ScrollView has no x:Name for the page's wheel handler");
			var code = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", file + ".cs"));
			Assert.Contains($"{name}.AddHandler(PointerWheelChangedEvent", code, StringComparison.Ordinal);
			Assert.Contains($"SmoothScroll.By({name}, RowPaging.Pixels(", code, StringComparison.Ordinal); // 1.9 (the scroll stream): from where it is going
		}
	}

	/// <summary>
	/// Nixie 1.9 (the scroll stream; the owner on the 1.9 test copy: "scrolling the collections page takes a longgg time"): the
	/// page's wheel, in its own handler and in EdgeScroller's over a row, called ScrollView.ScrollBy, animated, for each notch.
	/// ScrollBy counts from where the view is, so a notch while the last one still glided started again from there: a quick spin
	/// of 27 notches moved the page 314 px of the 2,592 it asked (the scroll gate, scroll190, Classic at 2560 x 1392). Every
	/// wheel notch of a ScrollView page now goes through SmoothScroll, which counts it from where the page is going.
	/// </summary>
	[Fact]
	public void TheWheelOnAScrollViewPage_CountsEachNotchFromWhereThePageIsGoing()
	{
		var app = Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin");
		var edge = File.ReadAllText(Path.Combine(app, "Controls", "EdgeScroller.cs"));
		Assert.Contains("SmoothScroll.By(view, pixels)", edge, StringComparison.Ordinal);
		Assert.Contains("RowPaging.PageCanScroll(SmoothScroll.Current(view)", edge, StringComparison.Ordinal);
		foreach (var (file, _) in ScrollViewsWithRows())
		{
			var code = Path.Combine(app, file + ".cs");
			Assert.DoesNotContain(".ScrollBy(", CodeOnly(File.ReadAllText(code)), StringComparison.Ordinal);
		}

		Assert.DoesNotContain(".ScrollBy(", CodeOnly(edge), StringComparison.Ordinal);
	}

	/// <summary>
	/// Nixie 1.9 (the scroll stream; older than 1.9): on the Collections page Page Down and Page Up did
	/// nothing (three Page Downs from a tile: 0 px; the scroll gate's pgdn). Its ScrollView leaves them alone and a row's Page Down
	/// scrolls only a ScrollViewer page (ShelfRow). The page takes them itself, handled events too, through SmoothScroll.
	/// </summary>
	[Fact]
	public void PageDownAndPageUp_ScrollTheCollectionsPage()
	{
		var code = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Views", "CollectionsPage.xaml.cs"));
		Assert.Contains("PageScroller.AddHandler(KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(PageScroller_KeyDown), true)", code, StringComparison.Ordinal);
		Assert.Contains("PageKeyPixels(e.Key, PageScroller.ViewportHeight)", code, StringComparison.Ordinal);
		Assert.Contains("SmoothScroll.By(PageScroller, pixels)", code, StringComparison.Ordinal);
		foreach (var key in new[] { "PageDown", "PageUp" })
		{
			Assert.Contains($"VirtualKey.{key}", code, StringComparison.Ordinal);
		}
	}

	// the code without its // comments (a comment naming ScrollBy is no call)
	private static string CodeOnly(string text) =>
		string.Join("\n", text.Split('\n').Select(line => line.IndexOf("//", StringComparison.Ordinal) is >= 0 and var at ? line[..at] : line));
}