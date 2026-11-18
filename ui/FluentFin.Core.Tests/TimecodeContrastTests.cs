using System.Globalization;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (Nixie): the player's time (elapsed, remaining) keeps 4.5:1 over a white frame, with its accent glow at its
/// brightest: the bottom scrim at the time's row (MpvPlayerControls.xaml's BottomScrim), the glow's full opacity in the
/// accent (Glass.xaml's dark SeaglassAccentColor) over it, white text. Conservative: the glow under a glyph's edge is
/// less than its full opacity. Read from the XAML, as OverlayContrastTests does.
/// </summary>
public sealed class TimecodeContrastTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	/// <summary>
	/// Where the time's row sits in the bottom bar, from the bar's bottom: the bar's padding (64 top, 20 bottom), the row of the
	/// time and the seek bar (the slider, 32), then 8 and the transport (the 60 px play button). Its middle: 104 of 184.
	/// </summary>
	private const double TimeRowFromBottom = (20 + 60 + 8 + 16) / 184.0;

	private static string Find(params string[] parts)
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
		{
			var candidate = Path.Combine([dir.FullName, .. parts]);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		throw new FileNotFoundException(Path.Combine(parts));
	}

	private static XDocument Controls() => XDocument.Load(Find("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml"));

	private static (double A, double R, double G, double B) Argb(string value)
	{
		var hex = value.TrimStart('#');
		byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return hex.Length == 8 ? (At(0) / 255.0, At(2), At(4), At(6)) : (1.0, At(0), At(2), At(4));
	}

	/// <summary>The scrim's black alpha at <paramref name="offset"/> (0 the bar's bottom, 1 its top), between its stops.</summary>
	private static double ScrimAlpha(XDocument doc, double offset, string key = "BottomScrim")
	{
		var stops = doc.Descendants(Presentation + "LinearGradientBrush").Single(b => (string?)b.Attribute(Xaml + "Key") == key)
			.Elements(Presentation + "GradientStop")
			.Select(s => (Offset: double.Parse((string)s.Attribute("Offset")!, CultureInfo.InvariantCulture), Alpha: Argb((string)s.Attribute("Color")!).A))
			.OrderBy(s => s.Offset).ToList();
		for (var i = 1; i < stops.Count; i++)
		{
			if (offset <= stops[i].Offset)
			{
				var t = (offset - stops[i - 1].Offset) / (stops[i].Offset - stops[i - 1].Offset);
				return stops[i - 1].Alpha + (stops[i].Alpha - stops[i - 1].Alpha) * t;
			}
		}

		return stops[^1].Alpha;
	}

	private static double Resource(XDocument doc, string key) =>
		double.Parse(doc.Descendants(Xaml + "Double").Single(d => (string?)d.Attribute(Xaml + "Key") == key).Value, CultureInfo.InvariantCulture);

	private static (double R, double G, double B) Accent()
	{
		var glass = XDocument.Load(Find("ui", "FluentFin", "Styles", "Glass.xaml"));
		var dark = glass.Descendants(Presentation + "ResourceDictionary").First(d => (string?)d.Attribute(Xaml + "Key") == "Dark");
		var color = Argb(dark.Elements(Presentation + "Color").Single(c => (string?)c.Attribute(Xaml + "Key") == "SeaglassAccentColor").Value);
		return (color.R, color.G, color.B);
	}

	[Fact]
	public void The_time_keeps_4_5_to_1_over_a_white_frame_with_its_glow_at_full()
	{
		var doc = Controls();
		var scrim = ScrimAlpha(doc, TimeRowFromBottom);
		var under = 255 * (1 - scrim);
		var glow = Resource(doc, "TimecodeGlowOpacity");
		var accent = Accent();
		var background = (accent.R * glow + under * (1 - glow), accent.G * glow + under * (1 - glow), accent.B * glow + under * (1 - glow));

		var withGlow = Contrast((255, 255, 255), background);
		var withoutGlow = Contrast((255, 255, 255), (under, under, under));
		Assert.True(withGlow >= 4.5, $"{withGlow:F2}:1 with the glow (scrim {scrim:F2} at the time's row, glow {glow})");
		Assert.True(withoutGlow >= 4.5, $"{withoutGlow:F2}:1 without it");
	}

	/// <summary>
	/// The top bar's second line (the episode, GlassTextSecondaryBrush: white at 80 %) over a white frame: the top bar's
	/// padding 16 and its 44 px row, then 56 of scrim; the line's middle is about 0.45 of the bar from its top. 1.1.3's
	/// TopScrim (#B3 fading to nothing) left it at about 2:1 (seen in the shots).
	/// </summary>
	[Fact]
	public void The_top_bars_second_line_keeps_4_5_to_1_over_a_white_frame()
	{
		var under = 255 * (1 - ScrimAlpha(Controls(), 0.45, "TopScrim"));
		var text = 0.8 * 255 + 0.2 * under;
		var ratio = Contrast((text, text, text), (under, under, under));
		Assert.True(ratio >= 4.5, $"{ratio:F2}:1");
	}

	[Fact]
	public void The_1_1_3_scrim_did_not_reach_it()
	{
		// the measure itself: #80 at 0.55, as 1.1.3 had it, left white time text at about 3.8:1 over a white frame
		var under = 255 * (1 - 0x80 / 255.0 * (1 - (TimeRowFromBottom - 0.55) / 0.45));
		Assert.True(Contrast((255, 255, 255), (under, under, under)) < 4.5);
	}

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
		var (x, y) = (Luminance(a), Luminance(b));
		return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
	}
}
