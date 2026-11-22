using System.Globalization;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 player: the Playback stats panel (Controls\PlaybackStatsPanel.xaml) keeps its rows at 4.5:1 or more over any picture,
/// in every theme's look (each theme's PlayerStatsPanelStyle fill, every stop of a gradient), over a white frame and a black
/// one; its corners are 8 in every theme. Read from the XAML, as PlayerPromptContrastTests does.
/// </summary>
public sealed class PlaybackStatsContrastTests
{
	private const double TextContrast = 4.5;

	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly (double R, double G, double B) White = (255, 255, 255), Black = (0, 0, 0);

	private static XDocument Load(params string[] parts) => XDocument.Load(FindUp(Path.Combine(["ui", "FluentFin", .. parts])));

	private static XDocument Panel() => Load("Controls", "PlaybackStatsPanel.xaml");

	/// <summary>A brush of a dictionary's dark ("Default") theme dictionary: its colour stops (one for a solid brush).</summary>
	private static IReadOnlyList<(double A, (double R, double G, double B) Rgb)> Dark(XDocument doc, string key)
	{
		var brush = doc.Descendants(Presentation + "ResourceDictionary")
			.Where(d => (string?)d.Attribute(Xaml + "Key") == "Default")
			.SelectMany(d => d.Elements())
			.Single(b => (string?)b.Attribute(Xaml + "Key") == key);
		return brush.Name.LocalName switch
		{
			"SolidColorBrush" => [Color((string)brush.Attribute("Color")!)],
			"LinearGradientBrush" => [.. brush.Elements(Presentation + "GradientStop").Select(s => Color((string)s.Attribute("Color")!))],
			_ => throw new InvalidOperationException($"{key} is a {brush.Name.LocalName}"),
		};
	}

	private static (double A, (double R, double G, double B) Rgb) Color(string text)
	{
		var hex = text.TrimStart('#');
		if (hex.Length == 6)
		{
			hex = "FF" + hex;
		}

		Assert.Equal(8, hex.Length);
		byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return (At(0) / 255.0, (At(2), At(4), At(6)));
	}

	/// <summary>Each theme's fill for the panel: its PlayerStatsPanelStyle's Background, else the panel's own.</summary>
	public static TheoryData<string> Themes => new() { "Classic", "DriveIn", "Cinematic", "Editorial" };

	private static IReadOnlyList<(double A, (double R, double G, double B) Rgb)> FillOf(string theme)
	{
		var own = Dark(Panel(), "PlaybackStatsFill");
		if (theme == "Classic")
		{
			Assert.DoesNotContain("PlayerStatsPanelStyle", File.ReadAllText(FindUp(Path.Combine("ui", "FluentFin", "Themes", "Classic", "Classic.xaml"))), StringComparison.Ordinal);
			return own;
		}

		var dictionary = Load("Themes", theme, theme + ".xaml");
		var style = dictionary.Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "PlayerStatsPanelStyle");
		Assert.Equal("Border", (string?)style.Attribute("TargetType"));
		string? Setter(string property) => (string?)style.Elements(Presentation + "Setter").SingleOrDefault(e => (string?)e.Attribute("Property") == property)?.Attribute("Value");
		Assert.Null(Setter("CornerRadius"));            // the panel's own 8 stays
		var background = Setter("Background");
		if (background is null)
		{
			return own;
		}

		if (background.StartsWith('#'))
		{
			return [Color(background)];
		}

		var key = background.Replace("{ThemeResource ", "", StringComparison.Ordinal).TrimEnd('}').Trim();
		var source = theme == "Cinematic" ? Load("Themes", "Cinematic", "Cards.xaml") : dictionary;
		return Dark(source, key);
	}

	[Theory]
	[MemberData(nameof(Themes))]
	public void The_rows_keep_4_5_to_1_over_a_white_and_a_black_frame(string theme)
	{
		var panel = Panel();
		var label = Dark(panel, "PlaybackStatsLabel").Single();
		var value = Dark(panel, "PlaybackStatsValue").Single();
		Assert.Equal(1.0, label.A);
		Assert.Equal(1.0, value.A);
		foreach (var (alpha, fill) in FillOf(theme))
		{
			foreach (var (name, frame) in new[] { ("white", White), ("black", Black) })
			{
				var under = Blend(fill, alpha, frame);
				Assert.True(Contrast(label.Rgb, under) >= TextContrast, $"{theme}: the labels {Contrast(label.Rgb, under):F2}:1 over a {name} frame");
				Assert.True(Contrast(value.Rgb, under) >= TextContrast, $"{theme}: the values {Contrast(value.Rgb, under):F2}:1 over a {name} frame");
			}
		}
	}

	[Fact]
	public void The_panel_is_glass_chrome_with_corners_of_8_and_a_name()
	{
		var panel = Panel();
		var baseStyle = panel.Descendants(Presentation + "Style").Single(s => (string?)s.Attribute(Xaml + "Key") == "PlaybackStatsPlateBaseStyle");
		string? Setter(string property) => (string?)baseStyle.Elements(Presentation + "Setter").Single(e => (string?)e.Attribute("Property") == property).Attribute("Value");
		Assert.Equal("8", Setter("CornerRadius"));
		Assert.Equal("{ThemeResource GlassStrokeBrush}", Setter("BorderBrush"));
		var plate = panel.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "Plate");
		Assert.Equal("PlayerStatsPanelStyle", (string?)plate.Attribute(XName.Get("{using:FluentFin.Themes}ThemeStyle.Key")) ?? (string?)plate.Attributes().Single(a => a.Name.LocalName == "ThemeStyle.Key"));
		Assert.Equal("Playback stats", (string?)plate.Attribute(XName.Get("AutomationProperties.Name")));
		Assert.Equal("Dark", (string?)panel.Root!.Attribute("RequestedTheme"));
		// the high contrast look is the system's colours
		var hc = panel.Descendants(Presentation + "ResourceDictionary").Single(d => (string?)d.Attribute(Xaml + "Key") == "HighContrast");
		Assert.All(hc.Elements(Presentation + "SolidColorBrush"), b => Assert.StartsWith("{ThemeResource SystemColor", (string?)b.Attribute("Color"), StringComparison.Ordinal));
		// the buttons are at the top (the bottom bar never covers them), named, and take no focus from a click
		foreach (var (name, said) in new[] { ("CopyButton", "Copy the playback stats"), ("CloseButton", "Close playback stats") })
		{
			var button = panel.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);
			Assert.Equal(said, (string?)button.Attribute(XName.Get("AutomationProperties.Name")));
			Assert.Equal("False", (string?)button.Attribute("AllowFocusOnInteraction"));
		}
	}

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

	private static (double R, double G, double B) Blend((double R, double G, double B) top, double alpha, (double R, double G, double B) bottom) =>
		(top.R * alpha + bottom.R * (1 - alpha), top.G * alpha + bottom.G * (1 - alpha), top.B * alpha + bottom.B * (1 - alpha));

	private static double Luminance((double R, double G, double B) c)
	{
		static double Channel(double v)
		{
			var s = v / 255.0;
			return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
		}

		return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
	}

	private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
	{
		var (hi, lo) = (Luminance(a), Luminance(b));
		return hi >= lo ? (hi + 0.05) / (lo + 0.05) : (lo + 0.05) / (hi + 0.05);
	}
}
