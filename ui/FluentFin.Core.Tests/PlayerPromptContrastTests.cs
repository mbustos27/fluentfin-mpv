using System.Globalization;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (UX review, prioritised fix 5): the pills over the video (Next near the end, Skip) keep their text at
/// 4.5:1 or more over any picture: their fill (PlayerPromptFill and its hover and pressed states, MpvPlayerControls.xaml)
/// is measured over a white frame and a black one, under the controls' white text. Read from the XAML, as
/// OverlayContrastTests does. In 1.1.3 the pills were on the bars' 20 % white (PlayerGlassFill): about 1.6:1 over white.
/// </summary>
public sealed class PlayerPromptContrastTests
{
	private const double TextContrast = 4.5;

	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
	private static readonly (double R, double G, double B) White = (255, 255, 255), Black = (0, 0, 0);

	private static XDocument Controls() => XDocument.Load(AppVersionTestsFindUp(Path.Combine("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml")));

	/// <summary>The dark ("Default") theme dictionary's brush: the controls are dark in both themes (RequestedTheme Dark).</summary>
	private static (double A, (double R, double G, double B) Rgb) Brush(XDocument doc, string key)
	{
		var brush = doc.Descendants(Presentation + "ResourceDictionary")
			.Single(d => (string?)d.Attribute(Xaml + "Key") == "Default")
			.Elements(Presentation + "SolidColorBrush")
			.Single(b => (string?)b.Attribute(Xaml + "Key") == key);
		var hex = ((string)brush.Attribute("Color")!).TrimStart('#');
		Assert.Equal(8, hex.Length);
		byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return (At(0) / 255.0, (At(2), At(4), At(6)));
	}

	[Theory]
	[InlineData("PlayerPromptFill")]
	[InlineData("PlayerPromptFillHover")]
	[InlineData("PlayerPromptFillPressed")]
	public void Pill_text_keeps_4_5_to_1_over_a_white_and_a_black_frame(string key)
	{
		var doc = Controls();
		var (alpha, fill) = Brush(doc, key);
		var text = Brush(doc, "PlayerGlassForeground");
		Assert.Equal(1.0, text.A);

		foreach (var (name, frame) in new[] { ("white", White), ("black", Black) })
		{
			var ratio = Contrast(text.Rgb, Blend(fill, alpha, frame));
			Assert.True(ratio >= TextContrast, $"{key}: {ratio:F2}:1 over a {name} frame, needs {TextContrast}:1");
		}
	}

	[Fact]
	public void The_bars_light_fill_would_not_do_over_a_white_frame()
	{
		// the measure itself: the 1.1.3 pill fill fails where the new one passes
		var doc = Controls();
		var (alpha, fill) = Brush(doc, "PlayerGlassFill");
		Assert.True(Contrast(White, Blend(fill, alpha, White)) < TextContrast);
	}

	[Theory]
	[InlineData("NextPill")]
	[InlineData("SkipSegmentButton")]
	public void Both_pills_use_the_dense_fill_also_under_the_pointer(string name)
	{
		var pill = Controls().Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);
		Assert.Equal("{StaticResource PlayerPromptPill}", (string?)pill.Attribute("Style"));
		var resources = pill.Element(Presentation + "Button.Resources")!.Elements(Presentation + "StaticResource")
			.ToDictionary(x => (string)x.Attribute(Xaml + "Key")!, x => (string)x.Attribute("ResourceKey")!);
		Assert.Equal("PlayerPromptFill", resources["ButtonBackground"]);
		Assert.Equal("PlayerPromptFillHover", resources["ButtonBackgroundPointerOver"]);
		Assert.Equal("PlayerPromptFillPressed", resources["ButtonBackgroundPressed"]);
	}

	[Fact]
	public void The_Next_pill_shows_near_the_end_not_with_the_bars()
	{
		var pill = Controls().Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NextPill");
		Assert.Equal("{x:Bind VisibleIf(Controls.IsNextPromptVisible), Mode=OneWay}", (string?)pill.Attribute("Visibility"));
		// its name says what it does and what plays, without the seconds (a name that changed every second would be read again)
		Assert.Equal("{x:Bind Controls.NextPromptName, Mode=OneWay}", (string?)pill.Attribute(XName.Get("AutomationProperties.Name")));
	}

	private static string AppVersionTestsFindUp(string relative)
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
