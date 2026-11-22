using System.Xml.Linq;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (the release's layout cycle): nothing in the Editorial Library's grid or head changes size after it has been
/// laid out. A row's posters took their width from <c>{Binding ElementName=Page}</c>, which reaches a poster only once it is in
/// the window, after its first measure: every row the grid made changed height a pass later, and a far scroll (the scroll
/// bar, Narrator) sent the grid's layout round for about 30 s, logged a LayoutCycleException and left the window unusable.
/// The details panel sized its art from its own SizeChanged, and the genre row's chevron resized the row it was shown for.
/// (The running check: tools\ui-harness\offscreen\then-library-far.ps1.)
/// </summary>
public sealed class EditorialLibraryLayoutTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static string App(params string[] parts) => Path.Combine([FlyleafEngineTests.RepoRoot, "FluentFin", .. parts]);

	private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

	[Fact]
	public void TheRowsPosters_HaveTheirWidthFromTheCell_BeforeTheyAreMeasured()
	{
		var page = XDocument.Load(App("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml"));
		var row = page.Descendants().Single(e => e.Name.LocalName == "DataTemplate" && (string?)e.Attribute(X + "Key") == "RowTemplate");
		var poster = row.Descendants().Single(e => e.Name.LocalName == "EditorialPoster");

		Assert.Equal("{x:Bind PosterWidth}", (string?)poster.Attribute("PosterWidth"));
		// nothing in a row waits for the window to learn its size
		Assert.DoesNotContain(row.Descendants().SelectMany(e => e.Attributes()), a => a.Value.Contains("ElementName", StringComparison.Ordinal));
		// and the page keeps the posters drawn already at the grid's width (the rows drawn later read it from their cells)
		var code = File.ReadAllText(App("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml.cs"));
		Assert.Contains("EditorialLibrary.Flow(ViewModel.Items, _columns, ByLetter, _opened, _metrics)", code);
	}

	[Fact]
	public void APostersPicture_HasItsSize_BeforeItsImageArrives()
	{
		// the picture grew from nothing as its image opened, which laid the grid out again: after a far scroll each image that
		// arrived re-made the rows from the top for about 30 s (the window stalled up to 2 s at a time)
		var code = File.ReadAllText(App("Themes", "Editorial", "Controls", "EditorialPoster.xaml.cs"));
		var resize = code[code.IndexOf("private void Resize()", StringComparison.Ordinal)..];
		resize = resize[..resize.IndexOf("ApplyImage();", StringComparison.Ordinal)];
		// (1.6 colpage: the poster's art width, or the list form's small art: ArtWidthNow)
		Assert.Contains("Picture.Width = ArtWidthNow;", resize);
		Assert.Contains("Picture.Height = ArtHeight;", resize);
	}

	[Fact]
	public void TheDetailsPanel_IsSizedByLayoutAlone()
	{
		Assert.DoesNotContain("SizeChanged", File.ReadAllText(App("Themes", "Editorial", "Controls", "InlineDetailsRow.xaml.cs")));

		var panel = XDocument.Load(App("Themes", "Editorial", "Controls", "InlineDetailsRow.xaml"));
		var art = Named(panel, "ArtArea");
		Assert.Null(art.Attribute("Width"));
		Assert.Null(art.Attribute("Height"));
		// a picture would grow the panel to its own height: the backdrop is a brush, which has no size of its own
		Assert.DoesNotContain(art.Descendants(), e => e.Name.LocalName == "Image");
		Assert.Contains(art.Descendants(), e => e.Name.LocalName == "ImageBrush" && (string?)e.Attribute(X + "Name") == "Backdrop");
		// its column: 55 % of the panel, 760 at most (what the SizeChanged handler worked out)
		var columns = Named(panel, "Panel").Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").ToList();
		Assert.Equal(["45*", "55*"], columns.Select(c => (string?)c.Attribute("Width")));
		Assert.Equal("760", (string?)columns[1].Attribute("MaxWidth"));
	}

	[Fact]
	public void TheGenreRowsChevron_LiesOverTheRow_SoShowingItNeverResizesIt()
	{
		var page = XDocument.Load(App("Themes", "Editorial", "Views", "EditorialLibraryPage.xaml"));
		var scroller = Named(page, "ChipScroller");
		var chevron = Named(page, "ChipMore");

		Assert.Equal((string?)scroller.Attribute("Grid.Column") ?? "0", (string?)chevron.Attribute("Grid.Column") ?? "0");
		Assert.Equal("Right", (string?)chevron.Attribute("HorizontalAlignment"));
		// the row keeps room for it after its last chip
		Assert.Equal("0,0,40,0", (string?)Named(page, "ChipList").Attribute("Margin"));
	}
}
