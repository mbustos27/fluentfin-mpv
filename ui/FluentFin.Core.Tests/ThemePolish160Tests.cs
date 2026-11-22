using System.Xml.Linq;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 colpage (theme polish, L2, C4, LB4): Drive-in's own look on the Libraries and Collection pages, Editorial's list view,
/// and the title room in Drive-in and Editorial.
/// </summary>
public sealed class ThemePolish160Tests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static string Read(string path) => File.ReadAllText(Path.Combine(RepoRoot, path));

	private static string? Value(string theme, string key) =>
		XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes", theme)).Root!.Elements().SingleOrDefault(e => (string?)e.Attribute(X + "Key") == key)?.Value;

	/// <summary>L2, C4, LB4: every theme's titles at least 24 under the 48 px bar (Drive-in's marquee sat 8 under it, Editorial's big type on its rule).</summary>
	[Theory]
	[InlineData("Classic/Classic.xaml")]
	[InlineData("Cinematic/Cinematic.xaml")]
	[InlineData("DriveIn/DriveIn.xaml")]
	[InlineData("Editorial/Editorial.xaml")]
	public void EveryTheme_LeavesTheTitleRoomUnderTheBar(string theme)
	{
		var y = double.Parse(Value(theme, "PageTitleY")!, System.Globalization.CultureInfo.InvariantCulture);
		Assert.True(y >= 48 + 24, $"{theme}: titles at {y}, {y - 48} under the bar");
	}

	/// <summary>The library pages' own heads take the token (one title y per theme on every page), not a number of their own.</summary>
	[Fact]
	public void TheLibraryHeads_TakeTheThemesTitleY()
	{
		Assert.Contains("PageGrid.Tokens().TitleY", Read("FluentFin/Themes/DriveIn/Views/DriveInLibraryPage.xaml.cs"), StringComparison.Ordinal);
		Assert.Contains("PageGrid.Tokens().TitleY", Read("FluentFin/Themes/Editorial/Views/EditorialLibraryPage.xaml.cs"), StringComparison.Ordinal);
	}

	/// <summary>Drive-in dresses both shared pages through theme keys (the pages stay shared: no Drive-in view of its own).</summary>
	[Fact]
	public void DriveIn_DressesTheCollectionAndLibrariesPages_ThroughKeys()
	{
		var drivein = Read("FluentFin/Themes/DriveIn/DriveIn.xaml");
		foreach (var key in new[] { "PageTitleFrameStyle", "CollectionStageTemplate", "LibrariesStageTemplate", "LibrariesStageUnderBar", "LibraryPresetChipStyle", "LibrariesGroupHeadingStyle" })
		{
			Assert.Contains($"x:Key=\"{key}\"", drivein, StringComparison.Ordinal);
		}

		// the pages ask for them by these names
		Assert.Contains("\"PageTitleFrameStyle\"", Read("FluentFin/Views/CollectionPage.xaml.cs"), StringComparison.Ordinal);
		Assert.Contains("\"PageTitleFrameStyle\"", Read("FluentFin/Views/LibrariesLandingPage.xaml.cs"), StringComparison.Ordinal);
		Assert.Contains("\"LibrariesStageTemplate\"", Read("FluentFin/Views/LibrariesLandingPage.xaml.cs"), StringComparison.Ordinal);
		// the marquee's title is light on its dark board in Light too (it read dark on dark: the page title's Light colour)
		Assert.Contains("x:Key=\"PageTitleFrameForeground\"", drivein, StringComparison.Ordinal);
		Assert.Contains("TitleText.Foreground = ink;", Read("FluentFin/Views/CollectionPage.xaml.cs"), StringComparison.Ordinal);
		Assert.Contains("TitleText.Foreground = ink;", Read("FluentFin/Views/LibrariesLandingPage.xaml.cs"), StringComparison.Ordinal);
		// the title stays the page's level-1 heading inside the frame
		var libraries = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Views/LibrariesLandingPage.xaml"));
		var title = libraries.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "TitleText");
		Assert.Equal("TitleFrame", (string?)title.Parent!.Attribute(X + "Name"));
		Assert.Equal("Level1", (string?)title.Attribute("AutomationProperties.HeadingLevel"));
	}

	/// <summary>Every theme dresses the Collection page's header (C1, C5) in its own way, and no picture is under a word.</summary>
	[Fact]
	public void EveryTheme_HasAHeaderStage_OrItsSoftBackdrop()
	{
		Assert.Contains("x:Key=\"CollectionAmbientBackdrop\"", Read("FluentFin/Themes/Classic/Classic.xaml"), StringComparison.Ordinal);
		foreach (var theme in new[] { "Cinematic/Cinematic.xaml", "DriveIn/DriveIn.xaml", "Editorial/Editorial.xaml" })
		{
			var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes", theme));
			var stage = doc.Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == "CollectionStageTemplate");
			// the art sits in the region right of the words (ArtMargin), or in a framed piece on the right gutter (FrameMargin)
			var text = stage.ToString();
			Assert.True(text.Contains("{Binding ArtMargin}", StringComparison.Ordinal) || text.Contains("{Binding FrameMargin}", StringComparison.Ordinal), theme);
			Assert.Contains("AutomationProperties.AccessibilityView=\"Raw\"", text, StringComparison.Ordinal);
		}
	}

	/// <summary>
	/// Cinematic's stage runs under the bar, where the window's caption buttons draw white on the art: a shade under them (the
	/// contrast matrix measured 1:1 over white art), before the night from the left so its edge has no seam.
	/// </summary>
	[Fact]
	public void Cinematic_ShadesTheCaptionButtons_WithoutASeam()
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/Cinematic/Cinematic.xaml"));
		var stage = doc.Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == "CollectionStageTemplate");
		var rects = stage.Descendants().Where(e => e.Name.LocalName == "Rectangle").ToList();
		var shade = rects.FindIndex(r => (string?)r.Attribute("Fill") == "{ThemeResource CinematicHeroTopScrimBrush}");
		Assert.True(shade >= 0, "no shade under the caption buttons");
		Assert.Equal("Top", (string?)rects[shade].Attribute("VerticalAlignment"));
		var night = rects.FindIndex(r => r.ToString().Contains("EndPoint=\"1,0\"", StringComparison.Ordinal));
		Assert.True(night > shade, "the night from the left must be drawn over the shade (no seam at the art's edge)");
	}

	/// <summary>
	/// Cinematic's tabs (the library's presets) change in place when only which is lit changes: rebuilt on Space, the focused
	/// tab left the tree and the keyboard fell to Sort (the keys matrix: Space on Unwatched, then Space again went to Sort).
	/// </summary>
	[Fact]
	public void CinematicsTabs_ChangeInPlace_AndKeepTheKeyboard()
	{
		var code = Read("FluentFin/Themes/Cinematic/Controls/CinematicTabStrip.cs");
		var set = code[code.IndexOf("public void SetTabs(", StringComparison.Ordinal)..];
		var inPlace = set.IndexOf("_buttons[i].Show(tabs[i]);", StringComparison.Ordinal);
		var clear = set.IndexOf("_panel.Children.Clear();", StringComparison.Ordinal);
		Assert.True(inPlace > 0 && inPlace < clear, "the same tabs must change in place before any rebuild");
		Assert.Contains("Invoke(button.Tab)", code, StringComparison.Ordinal); // a click picks the tab as shown now
	}

	/// <summary>
	/// Review Major 1: the header stage is pictures only, and Narrator reads none of it (Raw on its root hid only the root:
	/// Editorial's contents read "IN THIS COLLECTION", "01", "Wild Canyons", "1979" ... before the page's own title). Every
	/// text, picture and list in each theme's stage is Raw; then-160-colpage.ps1 checks the live tree.
	/// </summary>
	[Theory]
	[InlineData("Cinematic/Cinematic.xaml")]
	[InlineData("DriveIn/DriveIn.xaml")]
	[InlineData("Editorial/Editorial.xaml")]
	public void TheHeaderStage_ExposesNothingToNarrator(string theme)
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes", theme));
		var stage = doc.Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == "CollectionStageTemplate");
		var exposed = stage.Descendants()
			.Where(e => e.Name.LocalName is "TextBlock" or "Image" or "ItemsControl" or "ItemsRepeater" or "ListView" or "NixieReadout" or "Button")
			.Where(e => (string?)e.Attribute("AutomationProperties.AccessibilityView") != "Raw")
			.Select(e => e.Name.LocalName + " " + ((string?)e.Attribute("Text") ?? (string?)e.Attribute("Source") ?? ""))
			.ToList();
		Assert.True(exposed.Count == 0, $"{theme}: {string.Join("; ", exposed)}");
	}

	/// <summary>
	/// Review Major 3: Drive-in's lit preset (the strip's only on/off cue) in high contrast. Its pink text and bulb were fixed
	/// colours, about 1.9:1 on light high contrast's white tile; lit is now the system's selected pair (the highlight behind
	/// the highlight text) there, and the board's pink only in Dark and Light.
	/// </summary>
	[Fact]
	public void DriveInsLitPreset_UsesTheSystemsPair_InHighContrast()
	{
		var doc = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/DriveIn/DriveIn.xaml"));
		var style = doc.Root!.Elements().Single(e => (string?)e.Attribute(X + "Key") == "LibraryPresetChipStyle");
		var states = style.Descendants().Where(e => e.Name.LocalName == "VisualState" && ((string?)e.Attribute(X + "Name"))!.StartsWith("Checked", StringComparison.Ordinal)).ToList();
		Assert.Equal(3, states.Count);
		// no fixed colour in a lit state, nor on the bulb: each comes from a theme key
		var values = states.SelectMany(s => s.Descendants().Where(e => e.Name.LocalName == "Setter")).Select(e => (string?)e.Attribute("Value") ?? "").ToList();
		Assert.DoesNotContain(values, v => v.StartsWith('#'));
		Assert.Contains(values, v => v == "{ThemeResource DriveInPresetLitTileBrush}");
		Assert.Contains(values, v => v == "{ThemeResource DriveInPresetLitTextBrush}");
		Assert.Equal("{ThemeResource DriveInPresetLitBulbBrush}", (string?)style.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "Lit").Attribute("Fill"));

		// high contrast: the selected pair; Dark and Light: the board's pink on its dark tile
		var dictionaries = doc.Root.Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(X + "Key") is not null)
			.ToDictionary(e => (string)e.Attribute(X + "Key")!);
		string Colour(string dictionary, string key) => (string)dictionaries[dictionary].Elements().Single(e => (string?)e.Attribute(X + "Key") == key).Attribute("Color")!;
		Assert.Equal("{ThemeResource SystemColorHighlightColor}", Colour("HighContrast", "DriveInPresetLitTileBrush"));
		Assert.Equal("{ThemeResource SystemColorHighlightTextColor}", Colour("HighContrast", "DriveInPresetLitTextBrush"));
		Assert.Equal("{ThemeResource SystemColorHighlightTextColor}", Colour("HighContrast", "DriveInPresetLitBulbBrush"));
		foreach (var mode in new[] { "Default", "Light" })
		{
			Assert.Equal("#FF9EB1", Colour(mode, "DriveInPresetLitTextBrush"));
			Assert.Equal("#2A0814", Colour(mode, "DriveInPresetLitTileBrush"));
		}
	}

	/// <summary>
	/// Review Major 4: up to 12 Nixie collections join the four presets, so the strip scrolls sideways and the keyboard's
	/// preset comes into view (it neither scrolled nor wrapped: they ran off the window at 1100); Left, Right, Home and End
	/// move along it (the scroller would take them otherwise); Cinematic's tabs scroll too; Editorial gives the presets at
	/// most half its chips' row. then-160-colpage.ps1's 'presets12' part drives it live (NIXIE_PRESETS=12).
	/// </summary>
	[Fact]
	public void ThePresetsStrip_ScrollsSideways_WithTheKeyboard()
	{
		var strip = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Controls/LibraryPresetStrip.xaml"));
		var scroller = strip.Descendants().Single(e => e.Name.LocalName == "ScrollViewer");
		Assert.Equal("Enabled", (string?)scroller.Attribute("HorizontalScrollMode"));
		Assert.Equal("Strip", (string?)scroller.Elements().Single().Attribute(X + "Name"));
		var code = Read("FluentFin/Controls/LibraryPresetStrip.xaml.cs");
		Assert.Contains("Strip.AddHandler(KeyDownEvent", code, StringComparison.Ordinal);
		Assert.Contains("TabKeys.Next(e.Key", code, StringComparison.Ordinal);
		Assert.Contains("StartBringIntoView", code, StringComparison.Ordinal);
		Assert.Contains("HorizontalScrollMode = ScrollMode.Enabled", Read("FluentFin/Themes/Cinematic/Controls/CinematicTabStrip.cs"), StringComparison.Ordinal);
		Assert.Contains("PresetStrip.MaxWidth = ", Read("FluentFin/Themes/Editorial/Views/EditorialLibraryPage.xaml.cs"), StringComparison.Ordinal);
	}

	/// <summary>
	/// Review Major 2: on a page that runs under the 48 px bar (Cinematic's Collection page), the grid's card brought into view
	/// (Back to a deep card, Up through the grid) keeps the bar's height and 16 above it; it had no reveal margin, so its top
	/// and its focus ring sat behind the bar. then-160-colpage.ps1's 'deep' part checks it live.
	/// </summary>
	[Fact]
	public void TheCollectionGrid_RevealsACardClearOfTheBar()
	{
		var code = Read("FluentFin/Views/CollectionPage.xaml.cs");
		Assert.Contains("GridList.BringIntoViewRequested += (_, args) =>", code, StringComparison.Ordinal);
		Assert.Contains("var top = RevealTop(Controls.TopBar.GetExtendsUnder(this));", code, StringComparison.Ordinal);
		Assert.Contains("args.TargetRect = new Windows.Foundation.Rect(rect.X, rect.Y - top, rect.Width, rect.Height + top + RevealMargin);", code, StringComparison.Ordinal);
		Assert.Contains("RevealTop(bool underBar) => underBar ? Controls.TopBar.Height + RevealMargin : RevealMargin;", code, StringComparison.Ordinal);
	}

	// ---- Editorial's list view ----------------------------------------------------------------------------------------

	[Fact]
	public void TheListLine_SaysWhatTheGridHides()
	{
		var film = new BaseItemDto
		{
			Type = BaseItemDto_Type.Movie, Name = "Wild Canyons", ProductionYear = 1979, OfficialRating = "PG",
			RunTimeTicks = TimeSpan.FromMinutes(112).Ticks,
			Overview = "Two brothers ride out. They find the canyon is not what the map promised, and more.",
			UserData = new UserItemDataDto { Played = false, PlayedPercentage = 45.4 },
		};
		Assert.Equal("1979 · 1h 52m · PG", EditorialListForm.Facts(film));
		Assert.Equal("Two brothers ride out.", EditorialListForm.ShortLine(film));
		Assert.Equal("45% watched", EditorialListForm.State(film));

		film.UserData = new UserItemDataDto { Played = true };
		Assert.Equal("Watched", EditorialListForm.State(film));
		var show = new BaseItemDto { Type = BaseItemDto_Type.Series, Name = "Sunny", ChildCount = 3, UserData = new UserItemDataDto { UnplayedItemCount = 5 } };
		Assert.Equal("5 left", EditorialListForm.State(show));
		Assert.Equal("3 seasons", EditorialListForm.Facts(show));

		// edge data: nothing, a long first sentence cut at a word, no overview
		Assert.Equal(("", "", ""), (EditorialListForm.Facts(null), EditorialListForm.ShortLine(null), EditorialListForm.State(null)));
		var wordy = new BaseItemDto { Overview = string.Join(" ", Enumerable.Repeat("canyon", 60)) };
		var line = EditorialListForm.ShortLine(wordy);
		Assert.True(line.Length <= 141, line);
		Assert.EndsWith("canyon…", line, StringComparison.Ordinal);
		Assert.Equal("", EditorialListForm.State(new BaseItemDto { UserData = new UserItemDataDto() }));
	}

	/// <summary>View > List in Editorial (it had none): the list form's line, the same poster (stop, ring, Narrator name) in one column.</summary>
	[Fact]
	public void Editorial_OffersTheList_AndDrawsItAsLines()
	{
		var page = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/Editorial/Views/EditorialLibraryPage.xaml"));
		var bar = page.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "SortFilterBar");
		Assert.Equal("True", (string?)bar.Attribute("ShowListChoice"));
		var list = page.Descendants().Single(e => (string?)e.Attribute(X + "Key") == "ListRowTemplate");
		var poster = list.Descendants().Single(e => e.Name.LocalName == "EditorialPoster");
		Assert.Equal("{x:Bind ListForm}", (string?)poster.Attribute("ListForm"));
		Assert.Equal("{x:Bind Position}", (string?)poster.Attribute("AutomationProperties.PositionInSet"));
		var code = Read("FluentFin/Themes/Editorial/Views/EditorialLibraryPage.xaml.cs");
		Assert.Contains("var columns = list ? 1 :", code, StringComparison.Ordinal);
		Assert.Contains("nameof(LibraryViewModel.Layout)", code, StringComparison.Ordinal);

		// the poster's own form has no columns (an Auto column took a long caption's width and shifted the art right); the
		// list form adds its three in code
		var card = XDocument.Load(Path.Combine(RepoRoot, "FluentFin/Themes/Editorial/Controls/EditorialPoster.xaml"));
		var root = card.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "RootGrid");
		Assert.DoesNotContain(root.Elements(), e => e.Name.LocalName == "Grid.ColumnDefinitions");
		Assert.Contains("RootGrid.ColumnDefinitions.Clear();", Read("FluentFin/Themes/Editorial/Controls/EditorialPoster.xaml.cs"), StringComparison.Ordinal);

		// one mark per meaning: the list form says "Watched" and "5 left" in words, so its small art carries neither mark
		var posterCode = Read("FluentFin/Themes/Editorial/Controls/EditorialPoster.xaml.cs");
		Assert.Contains("WatchedMark.Visibility = ItemBadges.Watched(dto) && !ListForm", posterCode, StringComparison.Ordinal);
		Assert.Contains("CountChip.Visibility = CountText.Text.Length > 0 && !ListForm", posterCode, StringComparison.Ordinal);

		// the lines carry the form to their cells
		var metrics = new EditorialGridMetrics { ListForm = true };
		var row = new EditorialRowLine([BaseItemViewModel.FromDto(new BaseItemDto { Id = Guid.NewGuid(), Name = "A" })], 0, "A", 1) { Metrics = metrics };
		Assert.True(row.Cells.Single().ListForm);
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
