using System.Xml.Linq;
using FluentFin.Core.ViewModels;
using FluentFin.Themes;
using FluentFin.Themes.DriveIn;
using FluentFin.Themes.DriveIn.Controls;

namespace FluentFin.Tests;

/// <summary>Nixie 1.3.0, the Drive-in layout theme: its contract, its run-time nixie glyphs, the lot's layout, and the shared parts it dresses.</summary>
public sealed class DriveInThemeTests
{
	private static string FindUp(string relative)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine(dir.FullName, relative);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new FileNotFoundException(relative);
	}

	private static XDocument Xaml(params string[] parts) => XDocument.Load(FindUp(Path.Combine(["ui", "FluentFin", .. parts])));

	// ---- the contract ------------------------------------------------------------------------------------------------------

	[Fact]
	public void TheTheme_IsDriveIn_WithItsDictionary()
	{
		ILayoutTheme theme = new DriveInTheme();

		Assert.Equal(LayoutThemeId.DriveIn, theme.Id);
		Assert.Equal("Drive-in", theme.DisplayName);
		Assert.Equal("ms-appx:///Themes/DriveIn/DriveIn.xaml", theme.Dictionary.ToString());
		Assert.True(File.Exists(FindUp(Path.Combine("ui", "FluentFin", "Themes", "DriveIn", "DriveIn.xaml"))));
	}

	[Fact]
	public void EveryView_IsAPage_ForABrowsingViewModel()
	{
		var theme = new DriveInTheme();
		Assert.Equal(typeof(Themes.DriveIn.Views.DriveInHomePage), theme.ViewFor(typeof(HomeViewModel)));
		foreach (var (viewModel, view) in DriveInTheme.Views)
		{
			Assert.True(typeof(Microsoft.UI.Xaml.Controls.Page).IsAssignableFrom(view), $"{view.Name} is a Page");
			Assert.Equal(viewModel, view.GetProperty("ViewModel")!.PropertyType);
		}

		// shared everywhere else: Settings, search, the player page
		Assert.Null(theme.ViewFor(typeof(SettingsViewModel)));
		Assert.Null(theme.ViewFor(typeof(SearchResultsViewModel)));
	}

	[Fact]
	public void TheDictionary_DefinesTheKeysEveryThemeDefines_AndPrefixesItsOwn()
	{
		var keys = Xaml("Themes", "DriveIn", "DriveIn.xaml").Descendants()
			.Select(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")))
			.OfType<string>().ToHashSet();

		foreach (var key in new[] { "TopBarStyle", "RowHeaderTemplate", "PosterTemplate", "StillTemplate", "PageHeaderStyle", "SignInBackdropTemplate" })
		{
			Assert.Contains(key, keys);
		}

		var shared = new[] { "TopBarStyle", "RowHeaderTemplate", "PosterTemplate", "StillTemplate", "PageHeaderStyle", "SignInBackdropTemplate",
			"ThemeOptionsTemplate", "PlayerTopBarStyle", "PlayerBottomBarStyle", "TrailerFrameTemplate", "Default", "Light", "HighContrast",
			// Nixie 1.4.0: the shared pages' ground and the page grid (ThemeContract, Styles/PageGrid.xaml)
			"PageGroundBrush", "PageGutterNarrow", "PageGutterWide", "PageTitleY", "PageTitleUppercase" };
		Assert.All(keys.Except(shared), key => Assert.StartsWith("DriveIn", key));
	}

	[Fact]
	public void EveryThemeColour_HasAHighContrastSystemColour()
	{
		var dictionaries = Xaml("Themes", "DriveIn", "DriveIn.xaml").Descendants()
			.Where(e => e.Name.LocalName == "ResourceDictionary" && e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) is not null)
			.ToDictionary(e => (string)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))!);
		static HashSet<string> KeysOf(XElement d) => d.Elements().Select(e => (string?)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))).OfType<string>().ToHashSet();

		var dark = KeysOf(dictionaries["Default"]);
		Assert.Equal(dark, KeysOf(dictionaries["Light"]));
		Assert.Equal(dark, KeysOf(dictionaries["HighContrast"]));
		Assert.All(dictionaries["HighContrast"].Elements(), brush => Assert.Contains("SystemColor", (string?)brush.Attribute("Color") ?? ""));
	}

	// ---- the nixie glyphs ----------------------------------------------------------------------------------------------

	[Fact]
	public void EveryNumberTheThemeShows_HasItsGlyphs()
	{
		foreach (var text in new[] { "0123456789", "S1 E03", "1:12", "0:50:52", "−0:01:18", "S2 E05-06", "10:42", "1 H 58" })
		{
			Assert.True(NixieGlyphs.CanShow(text), text);
		}

		Assert.All("0123456789", c => Assert.True(NixieGlyphs.For(c)!.InTube, $"{c} sits in a tube"));
		Assert.All("SE:·-", c => Assert.False(NixieGlyphs.For(c)!.InTube, $"{c} glows outside the tubes"));
		Assert.Null(NixieGlyphs.For('Q'));
	}

	[Fact]
	public void TheGlyphs_AreWiresInsideTheirBox()
	{
		foreach (var c in NixieGlyphs.Supported)
		{
			var glyph = NixieGlyphs.For(c)!;
			var points = glyph.Strokes.SelectMany(s => s.Segments.SelectMany(p => p).Prepend(s.Start)).ToList();
			Assert.All(points, p =>
			{
				Assert.InRange(p.X, 0, glyph.Advance);
				Assert.InRange(p.Y, 0, NixieGlyphs.BoxHeight);
			});
			Assert.True(glyph.Strokes.Count > 0 || glyph.Dots.Count > 0 || c == ' ', $"'{c}' draws something");
		}

		Assert.Equal(2, NixieGlyphs.For('8')!.Strokes.Count);
		Assert.True(NixieGlyphs.For('0')!.Strokes[0].Closed);
		Assert.Throws<FormatException>(() => NixieGlyphs.Parse("M0,0 X1,1"));
	}

	[Fact]
	public void SmallTubes_GetATightHaloOnly()
	{
		var (core, glow, halo) = NixieReadout.Strokes(22);
		Assert.Equal(0, halo);
		Assert.True(glow - core <= 2.5, "the theme rule: a 2 px halo under 24 px text");
		Assert.True(NixieReadout.Strokes(56).Halo > NixieReadout.Strokes(56).Glow);
	}

	// ---- the lot's layout -------------------------------------------------------------------------------------------------

	[Fact]
	public void At1920_TheLotIsTheConcepts()
	{
		var lot = DriveInLayout.Home(1920, compact: false);

		Assert.True(lot.Wide && lot.HasScreen && lot.HasSign && lot.ShowDetails);
		Assert.Equal(new Windows.Foundation.Rect(96, 84, 1000, 563), lot.Screen);
		Assert.Equal(444, lot.Board.Width);
		Assert.Equal(1832, lot.Board.Right);
		Assert.Equal(464, lot.Board.Height); // four entries and the link
		Assert.InRange(lot.Sun.X, lot.Screen.Right, lot.Board.Left);
		Assert.True(lot.Ground > lot.Screen.Bottom && lot.SceneHeight > lot.Ground);
	}

	[Fact]
	public void At1100_TheLotIsTheNarrowConcept()
	{
		var lot = DriveInLayout.Home(1100, compact: false);

		Assert.False(lot.Wide);
		Assert.True(lot.HasSign);
		Assert.Equal(640, lot.Screen.Width);
		Assert.Equal(360, lot.Screen.Height);
		Assert.Equal(352, lot.Board.Width);
		Assert.Equal(330, lot.Board.Height); // three entries and the link
		Assert.True(lot.Screen.Right < lot.Board.Left);
		Assert.True(lot.Board.Right <= 1100);
	}

	[Fact]
	public void UnderAThousand_TheSignBecomesARow_AndAWideWindowCapsTheScreen()
	{
		Assert.False(DriveInLayout.Home(900, compact: false).HasSign);
		Assert.True(DriveInLayout.Home(900, compact: false).HasScreen);
		Assert.Equal(DriveInLayout.MaxScreenWidth, DriveInLayout.Home(3440, compact: false).Screen.Width);
		foreach (var width in new[] { 1000.0, 1280, 1400, 1599, 1600, 2560 })
		{
			var lot = DriveInLayout.Home(width, compact: false);
			Assert.True(lot.Screen.Right <= lot.Board.Left - 30, $"{width}: the screen clears the sign");
			Assert.True(lot.Board.Right <= width, $"{width}: the sign fits");
			Assert.All(lot.Cars, row => Assert.NotEmpty(row.Xs));
		}
	}

	[Fact]
	public void CompactHome_FoldsTheLotIntoAStrip_WithNoScreenOrSign()
	{
		var lot = DriveInLayout.Home(1920, compact: true);

		Assert.Equal(DriveInLayout.CompactHeight, lot.SceneHeight);
		Assert.False(lot.HasScreen);
		Assert.False(lot.HasSign);
	}

	[Fact]
	public void APageBelowTheTopBar_MovesTheLotUp()
	{
		Assert.Equal(84, DriveInLayout.Home(1920, false, pageTop: 0).Screen.Y);
		Assert.Equal(28, DriveInLayout.Home(1920, false, pageTop: 56).Screen.Y);
		Assert.Equal(20, DriveInLayout.Home(1100, false, pageTop: 64).Screen.Y);
	}

	[Fact]
	public void Bulbs_AreEvenlySpread_WithBothEndsLit()
	{
		var at = BulbFrame.Positions(14, 430, 18);
		Assert.Equal(14, at[0]);
		Assert.Equal(430, at[^1], 6);
		var gaps = at.Zip(at.Skip(1), (a, b) => b - a).ToList();
		Assert.All(gaps, gap => Assert.InRange(gap, 16, 20));
		Assert.Empty(BulbFrame.Positions(10, 5, 18));
	}

	[Fact]
	public void TheSkyIsBanded_AndKeepsHotPinkOut()
	{
		var stops = DriveInScene.SkyStops(DriveInScene.DuskBands);
		Assert.Equal(DriveInScene.DuskBands.Length * 2, stops.Count);
		Assert.True(stops.Zip(stops.Skip(1)).All(p => p.First.Offset <= p.Second.Offset), "stops in order");
		Assert.DoesNotContain(DriveInScene.DuskBands, b => b.Color == DriveInScene.C("#F96985"));
		Assert.DoesNotContain(DriveInScene.NoonBands, b => b.Color == DriveInScene.C("#F96985"));
	}

	// ---- the shared parts the theme dresses ----------------------------------------------------------------------------

	[Fact]
	public void TheRowAndCardPartsItDresses_StillHaveTheirNames()
	{
		static HashSet<string> Names(XDocument doc) => doc.Descendants()
			.Select(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))).OfType<string>().ToHashSet();

		var player = Names(Xaml("Controls", "MpvPlayerControls.xaml"));
		Assert.All(DriveInPlayer.Parts, part => Assert.Contains(part, player));

		var row = Names(Xaml("Controls", "ShelfRow.xaml"));
		Assert.All(DriveInRows.RowParts, part => Assert.Contains(part, row));
		var card = Names(Xaml("Controls", "BaseItemDtoCard.xaml"));
		Assert.All(DriveInRows.CardParts, part => Assert.Contains(part, card));

		// the card's subtitle is the title area's second line (the tubes stand in for it on an episode)
		var area = Xaml("Controls", "BaseItemDtoCard.xaml").Descendants().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "TitleArea");
		Assert.Equal(["TextBlock", "TextBlock"], area.Elements().Select(e => e.Name.LocalName));
	}

	[Fact]
	public void AMarquee_ShowsCapitals_ButIsNamedInTheRowsWords() =>
		Assert.Equal("CONTINUE WATCHING", MarqueeSign.Upper("Continue Watching"));

	[Fact]
	public void TheImageParameter_Reads()
	{
		Assert.Equal((Jellyfin.Sdk.Generated.Models.ImageType.Primary, 302d), DriveInImageConverter.Parse("Primary,302")!.Value);
		Assert.Null(DriveInImageConverter.Parse("Nothing,1"));
		Assert.Null(DriveInImageConverter.Parse("Primary"));
	}

	// ---- the SHOWTIMES board -------------------------------------------------------------------------------------------

	[Fact]
	public void TheBoard_ReadsDownTheFirstColumn_ThenTheSecond()
	{
		Assert.Equal((0, 0), ShowtimesPanel.CellOf(0, 8, 2));
		Assert.Equal((0, 3), ShowtimesPanel.CellOf(3, 8, 2));
		Assert.Equal((1, 0), ShowtimesPanel.CellOf(4, 8, 2));
		Assert.Equal((1, 2), ShowtimesPanel.CellOf(7, 9, 2)); // 9 in two columns: 5 rows
		Assert.Equal((0, 6), ShowtimesPanel.CellOf(6, 8, 1));
	}

	[Theory]
	[InlineData(Windows.System.VirtualKey.Down, 2, 3)]
	[InlineData(Windows.System.VirtualKey.Down, 3, 4)] // down out of the first column's last row: the next episode
	[InlineData(Windows.System.VirtualKey.Up, 4, 3)]
	[InlineData(Windows.System.VirtualKey.Right, 1, 5)]
	[InlineData(Windows.System.VirtualKey.Left, 6, 2)]
	[InlineData(Windows.System.VirtualKey.Home, 6, 0)]
	[InlineData(Windows.System.VirtualKey.End, 1, 7)]
	public void TheBoardsKeys_MoveAlongTheSeason(Windows.System.VirtualKey key, int from, int to) =>
		Assert.Equal(to, ShowtimesList.KeyTarget(key, from, 8, 2));

	[Fact]
	public void TheBoardsKeys_LeaveTheBoardAtItsEnds()
	{
		Assert.Null(ShowtimesList.KeyTarget(Windows.System.VirtualKey.Up, 0, 8, 2));
		Assert.Null(ShowtimesList.KeyTarget(Windows.System.VirtualKey.Down, 7, 8, 2));
		Assert.Null(ShowtimesList.KeyTarget(Windows.System.VirtualKey.Left, 2, 8, 2));
		Assert.Null(ShowtimesList.KeyTarget(Windows.System.VirtualKey.Right, 5, 8, 2));
		Assert.Null(ShowtimesList.KeyTarget(Windows.System.VirtualKey.Right, 1, 8, 1));
	}

	[Fact]
	public void ThePagesMargin_IsTheConcepts() 
	{
		Assert.Equal(96, ItemStage.MarginFor(1920));
		Assert.Equal(40, ItemStage.MarginFor(1100));
	}

	[Fact]
	public void AHeadersStrip_IsSkyAndHills_WithNoScreenSignOrCars()
	{
		var strip = DriveInLayout.Strip(1920, 300);
		Assert.Equal(300, strip.SceneHeight);
		Assert.False(strip.HasScreen || strip.HasSign);
		Assert.Empty(strip.Cars);
		// Nixie 1.4.0 (the UX review's DI-m1): the sun sets mid-strip, its top below the toolbar's row (it sat behind Sort and Filter)
		Assert.Equal(1920 / 2, strip.Sun.X);
		Assert.True(strip.Sun.Y - strip.SunRadius > 300 - 30 - strip.SunRadius);
	}

	[Theory]
	[InlineData(1920, 1080)]
	[InlineData(1100, 900)]
	[InlineData(2560, 1440)]
	public void TheSignInLot_FramesTheCentredForm_OnItsLegs(double width, double height)
	{
		var lot = DriveInLayout.SignIn(width, height);
		Assert.InRange(lot.Screen.X + lot.Screen.Width / 2 - width / 2, -1, 1);
		Assert.True(lot.Screen.Height >= 540, "tall enough for the sign-in form");
		Assert.True(lot.Screen.Bottom <= lot.Ground - 24, "its legs show above the ground");
		Assert.InRange(lot.Screen.Y + lot.Screen.Height / 2, height / 2 - 60, height / 2 + 1);
		Assert.All(lot.Cars, row => Assert.True(row.Base - 64 * row.Scale > lot.Screen.Bottom, "the cars park below the screen"));
	}

	// ---- the gates: one Primary per view, contrast -------------------------------------------------------------------

	[Fact]
	public void EveryView_HasOnePrimaryAtMost()
	{
		var folder = Path.GetDirectoryName(FindUp(Path.Combine("ui", "FluentFin", "Themes", "DriveIn", "DriveIn.xaml")))!;
		var primaries = new Dictionary<string, int>();
		foreach (var file in Directory.GetFiles(folder, "*.xaml", SearchOption.AllDirectories))
		{
			var text = File.ReadAllText(file);
			primaries[Path.GetFileName(file)] = text.Split("NixiePrimaryButtonStyle").Length - 1 + (text.Split("NixiePrimaryCompactButtonStyle").Length - 1);
		}

		// the screen's Resume (Home), each item page's Play (on its board); the sign, the boards and the rows have none
		Assert.Equal(1, primaries["OutdoorScreen.xaml"]);
		Assert.Equal(0, primaries["ComingUpSign.xaml"]);
		Assert.Equal(0, primaries["ItemBoard.xaml"]);
		Assert.Equal(0, primaries["Showtime.xaml"]);
		Assert.Equal(0, primaries["DriveInHomePage.xaml"]);
		Assert.All(primaries.Where(p => p.Key.StartsWith("DriveIn", StringComparison.Ordinal) && p.Key.EndsWith("Page.xaml", StringComparison.Ordinal)),
			p => Assert.InRange(p.Value, 0, 1));
	}

	private static double Luminance(Windows.UI.Color c)
	{
		static double Channel(byte v) { var x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}

	private static double Contrast(string a, string b)
	{
		var (la, lb) = (Luminance(DriveInScene.C(a)), Luminance(DriveInScene.C(b)));
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	[Theory]
	[InlineData("#FFF4EE", "#130409", "the boards' text")]
	[InlineData("#D9C6C8", "#130409", "the boards' second line")]
	[InlineData("#F7AA9C", "#130409", "NEXT and the watched mark on a board")]
	[InlineData("#FFC9A8", "#4A1A14", "a lit small tube's digit on the tube's brightest glass")]
	[InlineData("#B39890", "#4A1A14", "a resting season tube's digit on the tube's brightest glass")]
	[InlineData("#A88F92", "#2A0814", "a letter tile with no titles")]
	[InlineData("#FFF4EE", "#2A0814", "a letter tile")]
	[InlineData("#FFF4EE", "#A51A43", "the Library's count words on the dusk sky's brightest band behind them")]
	[InlineData("#2D0F16", "#F7B4A8", "the same at noon")]
	[InlineData("#F8EFF0", "#1A0710", "the rows' titles on the ground")]
	[InlineData("#2D0F16", "#FBEFEA", "the rows' titles on the noon ground")]
	[InlineData("#F8EFF0", "#1C0512", "the top bar's text on the sky's top band")]
	[InlineData("#6A1E32", "#FBEFEA", "a code's letters outside the tubes on the noon ground")]
	public void ThemeText_KeepsFourAndAHalfToOne(string text, string ground, string where) =>
		Assert.True(Contrast(text, ground) >= 4.5, $"{where}: {Contrast(text, ground):0.00}:1");

	[Fact]
	public void TheScreensWords_KeepContrast_OverAWhiteFrame()
	{
		// the screen's left scrim at the words (#B3100409 over white) and its bottom one (#C2100409): the worst case is the lighter
		static string Over(string scrim, double alpha)
		{
			var s = DriveInScene.C(scrim);
			byte Mix(byte v) => (byte)Math.Round(v * alpha + 255 * (1 - alpha));
			return $"#{Mix(s.R):X2}{Mix(s.G):X2}{Mix(s.B):X2}";
		}

		var ground = Over("#100409", 0.70);
		Assert.True(Contrast("#F3E6E7", ground) >= 4.5, $"overview {Contrast("#F3E6E7", ground):0.00}");
		Assert.True(Contrast("#D9C6C8", ground) >= 4.5, $"meta {Contrast("#D9C6C8", ground):0.00}");
	}
}
