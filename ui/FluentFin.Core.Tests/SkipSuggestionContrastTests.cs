using System.Globalization;
using System.Xml.Linq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.6 skip: "Skip this every episode?" over the video keeps its text at 4.5:1 or more over any picture, as the prompts do
/// (PlayerPromptContrastTests): the card is on the prompts' dense fill (PlayerPromptFill), its question in the controls' white
/// and its detail line in that white at the card's own opacity, measured over a white frame and a black one. Its buttons are
/// the prompts' own roles (Yes the Primary, the others Secondary), so the prompts' tests cover them; checked here that they are.
/// Read from the XAML.
/// </summary>
public sealed class SkipSuggestionContrastTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
	private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

	private static XDocument Controls()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ui", "FluentFin", "Controls", "MpvPlayerControls.xaml")))
		{
			dir = dir.Parent;
		}

		Assert.NotNull(dir);
		return XDocument.Load(Path.Combine(dir!.FullName, "ui", "FluentFin", "Controls", "MpvPlayerControls.xaml"));
	}

	private static XElement Named(XDocument doc, string name) => doc.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

	private static (double A, (double R, double G, double B) Rgb) Brush(XDocument doc, string key)
	{
		var brush = doc.Descendants(Presentation + "ResourceDictionary")
			.Single(d => (string?)d.Attribute(Xaml + "Key") == "Default")
			.Elements(Presentation + "SolidColorBrush")
			.Single(b => (string?)b.Attribute(Xaml + "Key") == key);
		var hex = ((string)brush.Attribute("Color")!).TrimStart('#');
		byte At(int i) => byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
		return (At(0) / 255.0, (At(2), At(4), At(6)));
	}

	private static (double R, double G, double B) Blend((double R, double G, double B) top, double alpha, (double R, double G, double B) bottom) =>
		((top.R * alpha) + (bottom.R * (1 - alpha)), (top.G * alpha) + (bottom.G * (1 - alpha)), (top.B * alpha) + (bottom.B * (1 - alpha)));

	private static double Luminance((double R, double G, double B) c)
	{
		static double Channel(double v)
		{
			v /= 255;
			return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
		}

		return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
	}

	private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
	{
		var (la, lb) = (Luminance(a), Luminance(b));
		return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
	}

	[Theory]
	[InlineData("SkipSuggestionQuestion")]
	[InlineData("SkipSuggestionDetail")]
	public void TheToastsText_Keeps4_5To1_OverAWhiteAndABlackFrame(string line)
	{
		var doc = Controls();
		var card = Named(doc, "SkipSuggestionCard");
		Assert.Equal("{ThemeResource PlayerPromptFill}", (string?)card.Attribute("Background"));
		Assert.Equal("8", (string?)card.Attribute("CornerRadius"));
		var text = Named(doc, line);
		Assert.Equal("{ThemeResource PlayerGlassForeground}", (string?)text.Attribute("Foreground"));
		var opacity = (string?)text.Attribute("Opacity") is { } o ? double.Parse(o, CultureInfo.InvariantCulture) : 1.0;

		var (alpha, fill) = Brush(doc, "PlayerPromptFill");
		var (_, ink) = Brush(doc, "PlayerGlassForeground");
		foreach (var (name, frame) in new[] { ("white", (255.0, 255.0, 255.0)), ("black", (0.0, 0.0, 0.0)) })
		{
			var under = Blend(fill, alpha, frame);
			var shown = Blend(ink, opacity, under);
			var ratio = Contrast(shown, under);
			Assert.True(ratio >= 4.5, $"{line}: {ratio:F2}:1 over a {name} frame");
		}
	}

	[Theory]
	[InlineData("SkipSuggestionYes", "PlayerPromptPrimaryStyle")]
	[InlineData("SkipSuggestionNotNow", "PlayerPromptStyle")]
	[InlineData("SkipSuggestionNever", "PlayerPromptStyle")]
	public void TheToastsButtons_AreThePromptsRoles_AndTakeNoFocusFromAClick(string name, string style)
	{
		var button = Named(Controls(), name);
		Assert.Equal($"{{StaticResource {style}}}", (string?)button.Attribute("Style"));
		Assert.Equal("False", (string?)button.Attribute("AllowFocusOnInteraction"));
		Assert.Null(button.Attribute("IsTabStop"));
	}
}
