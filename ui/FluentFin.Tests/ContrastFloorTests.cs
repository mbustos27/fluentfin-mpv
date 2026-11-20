using System.Globalization;
using System.Xml.Linq;
using FluentFin.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0, item 3 of the library review's plan (the contrast floor): the bar's look over a hero by day (DI-B1), the
/// search field's own plates, Drive-in's night bar and Cinematic's pager plate, as brush arithmetic over the worst art (white
/// and black). The labels themselves are measured as drawn, over white and black art, by tools\ui-harness\contrast-matrix.ps1.
/// </summary>
public sealed class ContrastFloorTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XDocument Xaml(params string[] parts) => XDocument.Load(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. parts])));

	private static string? Key(XElement e) => (string?)e.Attribute(X + "Key");

	private static string? TopBarSetter(string theme, string property)
	{
		var style = Xaml("Themes", theme, theme + ".xaml").Root!.Elements()
			.Single(e => e.Name.LocalName == "Style" && Key(e) == "TopBarStyle");
		return style.Elements().Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property)
			.Select(e => (string?)e.Attribute("Value")).SingleOrDefault();
	}

	// ---- colour arithmetic (WCAG 2 relative luminance) -----------------------------------------------------------------------

	private readonly record struct Argb(double A, double R, double G, double B);

	private static Argb Parse(string hex)
	{
		hex = hex.TrimStart('#');
		if (hex.Length == 6)
		{
			hex = "FF" + hex;
		}

		byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return new(At(0) / 255.0, At(2), At(4), At(6));
	}

	private static Argb Over(Argb top, Argb below) =>
		new(1, top.R * top.A + below.R * (1 - top.A), top.G * top.A + below.G * (1 - top.A), top.B * top.A + below.B * (1 - top.A));

	private static double Luminance(Argb c)
	{
		static double Channel(double v) { v /= 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}

	private static double Contrast(Argb a, Argb b)
	{
		var (la, lb) = (Luminance(a), Luminance(b));
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	private static readonly Argb White = Parse("#FFFFFF"), Black = Parse("#000000");

	// ---- DI-B1: Drive-in's bar by day -----------------------------------------------------------------------------------------

	[Theory]
	// over content: never the hero look
	[InlineData(false, true, false, false)]
	[InlineData(false, true, true, false)]
	[InlineData(false, false, true, false)]
	// over a hero: the hero look (the dark scrim and the dark theme's text) in the dark theme, and by day unless the theme keeps its daytime bar
	[InlineData(true, true, false, true)]
	[InlineData(true, true, true, true)]
	[InlineData(true, false, false, true)]
	[InlineData(true, false, true, false)]
	public void The_bar_takes_its_hero_look_over_a_hero_unless_its_theme_keeps_the_daytime_bar(bool underBar, bool heroInLight, bool appIsLight, bool hero) =>
		Assert.Equal(hero, TopBarChrome.ShowsHero(underBar, heroInLight, appIsLight));

	[Fact]
	public void DriveIn_keeps_one_bar_by_day_and_the_other_themes_keep_the_hero_look()
	{
		// DI-B1: Drive-in's noon bar is the same over the Libraries page and over a library (no dusk band with dark-theme text)
		Assert.Equal("False", TopBarSetter("DriveIn", nameof(TopBarChrome.HeroInLight)));
		foreach (var theme in new[] { "Cinematic", "Editorial", "Classic" })
		{
			Assert.Null(TopBarSetter(theme, nameof(TopBarChrome.HeroInLight)));
		}
	}

	/// <summary>
	/// Drive-in's night bar (the dark theme over a hero) keeps the bar's quiet labels (GlassTextSecondaryBrush, #D3BFC1) at
	/// 4.5:1 where they sit (the bar's middle, 30 % to 70 % of its height) over a white picture: a film's backdrop runs under
	/// its right half. At #A3 to #7A the account read 4.41:1 there.
	/// </summary>
	[Theory]
	[InlineData(0.3)]
	[InlineData(0.5)]
	[InlineData(0.7)]
	public void DriveIns_night_bar_keeps_its_labels_over_a_white_picture(double at)
	{
		var night = Xaml("Themes", "DriveIn", "DriveIn.xaml").Descendants()
			.Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == "Default").Elements()
			.Single(e => Key(e) == "DriveInSkyBarBrush");
		var stops = night.Descendants().Where(e => e.Name.LocalName == "GradientStop")
			.Select(e => (Offset: double.Parse((string)e.Attribute("Offset")!, CultureInfo.InvariantCulture), Color: Parse((string)e.Attribute("Color")!))).ToList();
		var (a, b) = (stops[0], stops[^1]);
		var t = (at - a.Offset) / (b.Offset - a.Offset);
		var bar = new Argb(a.Color.A + (b.Color.A - a.Color.A) * t, a.Color.R + (b.Color.R - a.Color.R) * t, a.Color.G + (b.Color.G - a.Color.G) * t, a.Color.B + (b.Color.B - a.Color.B) * t);
		var ground = Over(bar, White);
		var ratio = Contrast(Parse("#D3BFC1"), ground);
		Assert.True(ratio >= 4.5, $"at {at:P0} of the bar: {ratio:0.00}:1");
	}

	// ---- the search field ------------------------------------------------------------------------------------------------------

	private static IReadOnlyDictionary<string, XElement> SearchFieldTheme(string theme)
	{
		var box = Xaml("Views", "TitleBarControl.xaml").Descendants().Single(e => e.Name.LocalName == "AutoSuggestBox" && (string?)e.Attribute(X + "Name") == "SearchBox");
		var dictionary = box.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == theme);
		return dictionary.Elements().ToDictionary(e => Key(e)!);
	}

	/// <summary>
	/// The field's fills are its own brushes in each theme dictionary, so they follow the bar's theme (an alias to the app's
	/// glass resolved in the app's theme: over a hero by day the field stayed light under the dark theme's text, 1.24 to
	/// 3.81:1); high contrast takes the system's colours.
	/// </summary>
	[Theory]
	[InlineData("Default")]
	[InlineData("Light")]
	[InlineData("HighContrast")]
	public void The_search_field_has_its_own_fill_in_each_theme(string theme)
	{
		var keys = SearchFieldTheme(theme);
		foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
		{
			Assert.Equal("SolidColorBrush", keys[key].Name.LocalName);
			if (theme == "HighContrast")
			{
				Assert.Contains("SystemColor", (string?)keys[key].Attribute("Color"));
			}
		}
	}

	/// <summary>
	/// The placeholder (WinUI's TextFillColorSecondary: 77 % white in the dark theme, 62 % black in the light one) and the typed
	/// text keep 4.5:1 on the field over white and black art, at rest and while typing.
	/// </summary>
	[Theory]
	[InlineData("Default", "#C5FFFFFF")]
	[InlineData("Default", "#FFFFFFFF")]
	[InlineData("Light", "#9E000000")]
	[InlineData("Light", "#E4000000")]
	public void The_search_fields_words_keep_45_over_any_art(string theme, string text)
	{
		var keys = SearchFieldTheme(theme);
		foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundFocused" })
		{
			foreach (var art in new[] { White, Black })
			{
				var field = Over(Parse((string)keys[key].Attribute("Color")!), art);
				var ratio = Contrast(Over(Parse(text), field), field);
				Assert.True(ratio >= 4.5, $"{theme} {key} over {(art == White ? "white" : "black")}: {ratio:0.00}:1");
			}
		}
	}

	// ---- Cinematic's controls on art -------------------------------------------------------------------------------------------

	/// <summary>C-M2: the Home hero's pager (pause and the slides) sits where the side scrim has gone: on the stills' badge plate.</summary>
	[Fact]
	public void Cinematics_hero_pager_sits_on_a_plate()
	{
		var pager = Xaml("Themes", "Cinematic", "Controls", "CinematicHero.xaml").Descendants()
			.Single(e => (string?)e.Attribute(X + "Name") == "Pager");
		Assert.Equal("{ThemeResource CinematicBadgeFillBrush}", (string?)pager.Attribute("Background"));
		Assert.Equal("8", (string?)pager.Attribute("CornerRadius"));
	}
}
