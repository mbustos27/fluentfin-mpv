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

	/// <summary>Glass.xaml's dark dictionary's brush (the Nixie templates' hover and press overlays, the Primary's fill and ink).</summary>
	private static (double A, (double R, double G, double B) Rgb) GlassBrush(string key)
	{
		var doc = XDocument.Load(AppVersionTestsFindUp(Path.Combine("ui", "FluentFin", "Styles", "Glass.xaml")));
		var dark = doc.Descendants(Presentation + "ResourceDictionary").Single(d => (string?)d.Attribute(Xaml + "Key") == "Dark");
		var element = dark.Elements().Single(b => (string?)b.Attribute(Xaml + "Key") == key);
		if (element.Name == Presentation + "StaticResource")
		{
			return GlassBrush((string)element.Attribute("ResourceKey")!);
		}

		var hex = ((string)element.Attribute("Color")!).TrimStart('#');
		if (hex.Length == 6)
		{
			hex = "FF" + hex;
		}

		byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return (At(0) / 255.0, (At(2), At(4), At(6)));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("NixieHoverOverlayBrush")]
	[InlineData("NixiePressedOverlayBrush")]
	public void Prompt_text_keeps_4_5_to_1_over_a_white_and_a_black_frame(string? overlay)
	{
		var doc = Controls();
		var (alpha, fill) = Brush(doc, "PlayerPromptFill");
		var text = Brush(doc, "PlayerGlassForeground");
		Assert.Equal(1.0, text.A);

		foreach (var (name, frame) in new[] { ("white", White), ("black", Black) })
		{
			var under = Blend(fill, alpha, frame);
			if (overlay is not null)
			{
				// the Nixie template draws its hover and press as an overlay over the button's own fill
				var (overAlpha, over) = GlassBrush(overlay);
				under = Blend(over, overAlpha, under);
			}

			var ratio = Contrast(text.Rgb, under);
			Assert.True(ratio >= TextContrast, $"{overlay ?? "rest"}: {ratio:F2}:1 over a {name} frame, needs {TextContrast}:1");
		}
	}

	[Fact]
	public void The_prompt_fill_is_the_palettes_deep_wine_not_a_teal()
	{
		// Nixie 1.4.0 (the UX review's CL-M11): #EB101C20 was a teal-black found nowhere else in the wine palette
		var (_, fill) = Brush(Controls(), "PlayerPromptFill");
		var (_, deep) = GlassBrush("NixieDeepBrush");
		Assert.Equal(deep, fill);
	}

	[Fact]
	public void The_bars_light_fill_would_not_do_over_a_white_frame()
	{
		// the measure itself: the 1.1.3 pill fill fails where the new one passes
		var doc = Controls();
		var (alpha, fill) = Brush(doc, "PlayerGlassFill");
		Assert.True(Contrast(White, Blend(fill, alpha, White)) < TextContrast);
	}

	/// <summary>
	/// Nixie 1.4.0 (the UX review's X12, plan item 9): the next-episode family on the button system. Next is the Primary; Skip,
	/// Watch credits and Keep watching are Secondary on the dense fill; every prompt is 44 high, on the Nixie templates
	/// (their hover, press and focus glow), centred on one line.
	/// </summary>
	[Theory]
	[InlineData("NextPill", "PlayerPromptPrimaryStyle", "NixiePlayerPromptPrimaryButtonStyle")]
	[InlineData("SkipSegmentButton", "PlayerPromptStyle", "NixiePlayerPromptButtonStyle")]
	[InlineData("CancelNextButton", "PlayerPromptStyle", "NixiePlayerPromptButtonStyle")]
	public void Each_prompt_is_on_its_role_44_high_on_one_centre_line(string name, string style, string role)
	{
		var doc = Controls();
		var prompt = doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);
		Assert.Equal($"{{StaticResource {style}}}", (string?)prompt.Attribute("Style"));
		Assert.Null(prompt.Attribute("Height"));
		Assert.Null(prompt.Attribute("VerticalAlignment"));
		Assert.Null(prompt.Element(Presentation + "Button.Resources"));
		var definition = doc.Descendants(Presentation + "Style").Single(e => (string?)e.Attribute(Xaml + "Key") == style);
		Assert.Equal($"{{StaticResource {role}}}", (string?)definition.Attribute("BasedOn"));
		string? Setter(string property) => (string?)definition.Elements(Presentation + "Setter").SingleOrDefault(e => (string?)e.Attribute("Property") == property)?.Attribute("Value");
		Assert.Equal("44", Setter("Height"));
		Assert.Equal("Center", Setter("VerticalAlignment"));
		Assert.Null(Setter("CornerRadius"));
		Assert.Null(Setter("Template"));
		if (role == "NixiePlayerPromptButtonStyle")
		{
			Assert.Equal("{ThemeResource PlayerPromptFill}", Setter("Background"));
		}
	}

	[Fact]
	public void The_Next_prompt_shows_near_the_end_not_with_the_bars()
	{
		var pill = Controls().Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NextPill");
		Assert.Equal("{x:Bind VisibleIf(Controls.IsNextPromptVisible), Mode=OneWay}", (string?)pill.Attribute("Visibility"));
		// its name says what it does and what plays, without the seconds (a name that changed every second would be read again)
		Assert.Equal("{x:Bind Controls.NextPromptName, Mode=OneWay}", (string?)pill.Attribute(XName.Get("AutomationProperties.Name")));
		// the title is in the name and the tooltip, not a second line (the 69 px card); a leading skip-next icon
		Assert.DoesNotContain(pill.Descendants(), e => ((string?)e.Attribute("Text"))?.Contains("NextItemTitle", StringComparison.Ordinal) == true);
		Assert.Equal("{x:Bind NextHint(Controls.NextToolTip), Mode=OneWay}", (string?)pill.Attribute(XName.Get("ToolTipService.ToolTip")));
		var parts = pill.Descendants().Where(e => e.Name == Presentation + "FontIcon" || (string?)e.Attribute(Xaml + "Name") == "NextPillText").ToList();
		Assert.Equal(Presentation + "FontIcon", parts[0].Name);
		Assert.Equal("E893", ((int)((string)parts[0].Attribute("Glyph")!)[0]).ToString("X4", CultureInfo.InvariantCulture));
	}

	[Fact]
	public void The_countdown_is_a_3_px_line_in_the_ink_on_a_lighter_track()
	{
		// C-P4: 1.3.1's 2 px accent line on a dark track was nearly invisible over bright video
		var doc = Controls();
		var bar = doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NextCountdownBar");
		Assert.Equal("3", (string?)bar.Attribute("Height"));
		Assert.Equal("{x:Bind VisibleIf(Controls.IsCountingDown), Mode=OneWay}", (string?)bar.Attribute("Visibility"));
		var fill = bar.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "NextCountdownFill");
		Assert.Equal("{ThemeResource NixiePrimaryForegroundBrush}", (string?)fill.Attribute("Background"));
		Assert.Equal("{x:Bind Controls.NextCountdownProgress, Mode=OneWay}", (string?)fill.Descendants(Presentation + "ScaleTransform").Single().Attribute("ScaleX"));

		// over the Primary's fill the track is lighter than it, and the ink's line keeps 3:1 on both (WCAG 1.4.11)
		var (primaryAlpha, primary) = GlassBrush("NixiePrimaryFillBrush");
		Assert.Equal(1.0, primaryAlpha);
		var (trackAlpha, track) = Brush(doc, "PlayerPromptTrack");
		var trackOnFill = Blend(track, trackAlpha, primary);
		Assert.True(Luminance(trackOnFill) > Luminance(primary));
		var (_, ink) = GlassBrush("NixiePrimaryForegroundBrush");
		Assert.True(Contrast(ink, trackOnFill) >= 3, $"the line on its track: {Contrast(ink, trackOnFill):F2}:1");
		Assert.True(Contrast(ink, primary) >= 4.5, $"the label on the Primary: {Contrast(ink, primary):F2}:1");
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
