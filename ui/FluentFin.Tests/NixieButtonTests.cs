using System.Xml.Linq;
using FluentFin.Core.Theming;

namespace FluentFin.Tests;

/// <summary>
/// 1.2.0: the button system of docs/reports/Nixie button review.md, section 3, in Styles/Glass.xaml: six roles in two sizes,
/// one radius, every state, disabled neutral (never a fade), high-contrast values for every key, the 1.1.x keys as aliases.
/// </summary>
public sealed class NixieButtonTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XElement Glass() => XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "Glass.xaml"))).Root!;

	private static string? Key(XElement e) => (string?)e.Attribute(X + "Key");

	private static XElement Theme(string name) =>
		Glass().Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == name);

	private static XElement Top(string key) => Glass().Elements().Single(e => Key(e) == key);

	private static Dictionary<string, string?> Setters(string style) =>
		Top(style).Elements().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => (string)e.Attribute("Property")!, e => (string?)e.Attribute("Value"));

	private static IEnumerable<XElement> States(XElement template) => template.Descendants().Where(e => e.Name.LocalName == "VisualState");

	/// <summary>The roles and sizes of section 3.1: style, target type, template, height key (or fixed side).</summary>
	public static TheoryData<string, string, string, string> Roles => new()
	{
		{ "NixiePrimaryButtonStyle", "Button", "NixiePrimaryButtonTemplate", "{StaticResource NixieButtonHeight}" },
		{ "NixiePrimaryCompactButtonStyle", "Button", "NixiePrimaryCompactButtonTemplate", "{StaticResource NixieCompactButtonHeight}" },
		{ "NixieButtonStyle", "Button", "NixieButtonTemplate", "{StaticResource NixieButtonHeight}" },
		{ "NixieCompactButtonStyle", "Button", "NixieButtonTemplate", "{StaticResource NixieCompactButtonHeight}" },
		{ "NixieSubtleButtonStyle", "Button", "NixieSubtleButtonTemplate", "{StaticResource NixieCompactButtonHeight}" },
		{ "NixieIconButtonStyle", "Button", "NixieIconButtonTemplate", "0" },
		{ "NixieIconToggleButtonStyle", "ToggleButton", "NixieIconToggleButtonTemplate", "0" },
		{ "NixieCompactIconButtonStyle", "Button", "NixieSubtleButtonTemplate", "0" },
		{ "NixieDestructiveButtonStyle", "Button", "NixieDestructiveButtonTemplate", "{StaticResource NixieButtonHeight}" },
		{ "NixieDestructiveCompactButtonStyle", "Button", "NixieDestructiveButtonTemplate", "{StaticResource NixieCompactButtonHeight}" },
		{ "NixieLinkButtonStyle", "HyperlinkButton", "NixieLinkButtonTemplate", "{StaticResource NixieCompactButtonHeight}" },
		{ "NixieTabButtonStyle", "Button", "NixieTabButtonTemplate", "40" },
	};

	[Theory]
	[MemberData(nameof(Roles))]
	public void Every_role_has_its_key_type_template_and_size(string style, string target, string template, string minHeight)
	{
		Assert.Equal(target, (string?)Top(style).Attribute("TargetType"));
		var setters = Setters(style);
		Assert.Equal($"{{StaticResource {template}}}", setters["Template"]);
		Assert.Equal(target, (string?)Top(template).Attribute("TargetType"));
		Assert.Equal(minHeight, setters["MinHeight"]);
		Assert.Equal("True", setters["UseSystemFocusVisuals"]);
	}

	[Fact]
	public void The_sizes_are_44_and_32_and_every_button_has_one_radius_of_8()
	{
		Assert.Equal("44", Top("NixieButtonHeight").Value);
		Assert.Equal("32", Top("NixieCompactButtonHeight").Value);
		Assert.Equal("8", Top("GlassButtonCornerRadius").Value);
		foreach (var style in new[] { "NixiePrimaryButtonStyle", "NixieButtonStyle", "NixieCompactButtonStyle", "NixieDestructiveButtonStyle", "NixieCompactIconButtonStyle" })
		{
			Assert.Equal("{StaticResource GlassButtonCornerRadius}", Setters(style)["CornerRadius"]);
		}

		// circles only for equal-sided icons, both sides fixed and MinHeight 0 (never an ellipse)
		foreach (var (style, side) in new[] { ("NixieIconButtonStyle", "NixieButtonHeight"), ("NixieIconToggleButtonStyle", "NixieButtonHeight"), ("NixieCompactIconButtonStyle", "NixieCompactButtonHeight") })
		{
			var setters = Setters(style);
			Assert.Equal($"{{StaticResource {side}}}", setters["Width"]);
			Assert.Equal($"{{StaticResource {side}}}", setters["Height"]);
			Assert.Equal("0", setters["MinHeight"]);
		}

		Assert.Equal("{StaticResource GlassCircleCornerRadius}", Setters("NixieIconButtonStyle")["CornerRadius"]);
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", Setters("NixieCompactIconButtonStyle")["CornerRadius"]);
	}

	[Theory]
	[InlineData("NixiePrimaryButtonTemplate")]
	[InlineData("NixiePrimaryCompactButtonTemplate")]
	[InlineData("NixieButtonTemplate")]
	[InlineData("NixieIconButtonTemplate")]
	[InlineData("NixieSubtleButtonTemplate")]
	[InlineData("NixieDestructiveButtonTemplate")]
	[InlineData("NixieLinkButtonTemplate")]
	[InlineData("NixieTabButtonTemplate")]
	[InlineData("NixieIconToggleButtonTemplate")]
	public void Every_template_has_every_state_and_disabled_is_neutral_never_a_fade(string key)
	{
		var template = Top(key);
		var names = States(template).Select(s => (string?)s.Attribute(X + "Name")).ToHashSet();
		foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Disabled", "Focused", "Unfocused", "PointerFocused" })
		{
			Assert.Contains(state, names);
		}

		var disabled = States(template).Single(s => (string?)s.Attribute(X + "Name") == "Disabled");
		var targets = disabled.Descendants().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => (string)e.Attribute("Target")!, e => (string)e.Attribute("Value")!);
		Assert.DoesNotContain(targets.Keys, t => t.EndsWith(".Opacity", StringComparison.Ordinal));
		Assert.Equal("{ThemeResource NixieDisabledForegroundBrush}", targets["ContentPresenter.Foreground"]);
		Assert.DoesNotContain("0.45", template.ToString());
		// hover never changes the weight
		var hover = States(template).Single(s => (string?)s.Attribute(X + "Name") == "PointerOver");
		Assert.DoesNotContain(hover.Descendants(), e => ((string?)e.Attribute("Target"))?.EndsWith("FontWeight", StringComparison.Ordinal) == true);
	}

	[Fact]
	public void The_toggle_shows_on_as_a_glyph_and_rim_and_the_tab_as_an_underline()
	{
		var toggle = States(Top("NixieIconToggleButtonTemplate")).Select(s => (string?)s.Attribute(X + "Name")).ToHashSet();
		Assert.Superset(new HashSet<string?> { "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled" }, toggle);
		var on = States(Top("NixieIconToggleButtonTemplate")).Single(s => (string?)s.Attribute(X + "Name") == "Checked").ToString();
		Assert.Contains("NixieToggleOnBorderBrush", on);
		Assert.Contains("NixieToggleOnForegroundBrush", on);
		Assert.DoesNotContain("NixiePrimaryFillBrush", on); // never a filled accent disc

		var tab = Top("NixieTabButtonTemplate");
		Assert.Contains(States(tab), s => (string?)s.Attribute(X + "Name") == "Selected");
		Assert.Contains(States(tab), s => (string?)s.Attribute(X + "Name") == "Unselected");
		Assert.Contains("GlassTabUnderlineBrush", tab.ToString());
		Assert.Equal("Transparent", Setters("NixieTabButtonStyle")["Background"]);
	}

	[Fact]
	public void Destructive_is_never_filled_and_never_glows_and_links_are_underlined()
	{
		var setters = Setters("NixieDestructiveButtonStyle");
		Assert.Equal("{ThemeResource NixieButtonFillBrush}", setters["Background"]);
		Assert.Equal("{ThemeResource NixieDangerForegroundBrush}", setters["Foreground"]);
		var template = Top("NixieDestructiveButtonTemplate").ToString();
		Assert.DoesNotContain("FocusGlow", template);
		Assert.DoesNotContain("RestGlow", template);
		Assert.DoesNotContain("NixiePrimaryFillBrush", template);

		Assert.Contains("TextDecorations=\"Underline\"", Top("NixieLinkButtonTemplate").ToString());
		Assert.Equal("{ThemeResource NixieLinkForegroundBrush}", Setters("NixieLinkButtonStyle")["Foreground"]);

		// only the Large primary glows at rest
		Assert.Contains("RestGlow", Top("NixiePrimaryButtonTemplate").ToString());
		Assert.DoesNotContain("RestGlow", Top("NixiePrimaryCompactButtonTemplate").ToString());
	}

	[Fact]
	public void Every_new_key_has_a_dark_a_light_and_a_system_colour_high_contrast_value()
	{
		var dark = Theme("Dark").Elements().Where(e => Key(e)!.StartsWith("Nixie", StringComparison.Ordinal)).Select(Key).ToList();
		Assert.True(dark.Count >= 25);
		var light = Theme("Light").Elements().Select(Key).ToHashSet();
		var hc = Theme("HighContrast").Elements().ToDictionary(e => Key(e)!);
		foreach (var key in dark)
		{
			Assert.Contains(key, light);
			Assert.True(hc.TryGetValue(key!, out var entry), $"{key}: no high-contrast value");
			var value = (string?)entry.Attribute("Color") ?? (string?)entry.Attribute("ResourceKey");
			Assert.True(value == "Transparent" || value!.Contains("SystemColor", StringComparison.Ordinal), $"{key}: {value}");
		}
	}

	private static Rgb Hex(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));

	private static string Colour(string theme, string key) => (string)Theme(theme).Elements().Single(e => Key(e) == key).Attribute("Color")!;

	/// <summary>A translucent #AARRGGBB over an opaque colour.</summary>
	private static Rgb Over(string argb, Rgb under) => DuskScene.Blend(Hex(argb), Convert.ToByte(argb[1..3], 16) / 255.0, under);

	[Theory]
	[InlineData(false, "#2A1217")]
	[InlineData(true, "#FCEAE8")]
	public void The_labels_read_at_rest_hover_and_disabled(bool light, string glassHex)
	{
		var theme = light ? "Light" : "Dark";
		var glass = Hex(glassHex);
		var fill = light ? new Rgb(0xBC, 0x1E, 0x47) : SeaglassPalette.AccentDark;

		// primary: the label on the accent, and on the accent with the hover overlay
		var label = Hex(Colour(theme, "NixiePrimaryForegroundBrush"));
		Assert.True(OkLab.Contrast(label.Luminance, fill.Luminance) >= 4.5);
		Assert.True(OkLab.Contrast(label.Luminance, Over(Colour(theme, "NixiePrimaryHoverOverlayBrush"), fill).Luminance) >= 4.5);

		// destructive: the danger ink on the secondary fill over the glass (4.5:1), and not the accent
		var secondary = Over(light ? "#0F000000" : "#1AFFFFFF", glass);
		var danger = Hex(Colour(theme, "NixieDangerForegroundBrush"));
		Assert.True(OkLab.Contrast(danger.Luminance, secondary.Luminance) >= 4.5, $"{danger.Hex} on {secondary.Hex}");
		Assert.NotEqual(fill, danger);

		// disabled: visible (about 3:1, below the rest label) on the disabled fill, never the accent
		var disabledFill = Over(Colour(theme, "NixieDisabledFillBrush"), glass);
		var disabled = Over(Colour(theme, "NixieDisabledForegroundBrush"), disabledFill);
		var ratio = OkLab.Contrast(disabled.Luminance, disabledFill.Luminance);
		Assert.InRange(ratio, 2.5, 4.5);
	}

	[Fact]
	public void The_light_focus_ring_is_deep_maroon_around_white_not_crimson()
	{
		string Brush(string key) => (string)Theme("Light").Elements().Single(e => Key(e) == key).Attribute("Color")!;
		Assert.Equal("#2D0F16", Brush("SystemControlFocusVisualPrimaryBrush"));
		Assert.Equal("#FFFFFF", Brush("SystemControlFocusVisualSecondaryBrush"));
		// the outer ring on the pale page, the inner line on the crimson Play
		Assert.True(OkLab.Contrast(Hex("#2D0F16").Luminance, Hex("#FFF6F3").Luminance) >= 3);
		Assert.True(OkLab.Contrast(1.0, Hex("#BC1E47").Luminance) >= 3);
	}

	[Theory]
	[InlineData("GlassPillButtonStyle", "NixieButtonStyle")]
	[InlineData("GlassAccentPillButtonStyle", "NixiePrimaryButtonStyle")]
	[InlineData("GlassInsetPillButtonStyle", "NixieButtonStyle")]
	[InlineData("GlassIconButtonStyle", "NixieIconButtonStyle")]
	public void The_1_1_keys_are_aliases_of_the_new_roles(string old, string role)
	{
		var alias = Top(old);
		Assert.Equal("StaticResource", alias.Name.LocalName);
		Assert.Equal(role, (string?)alias.Attribute("ResourceKey"));
		// defined after the style it names (a StaticResource alias resolves at load)
		var keys = Glass().Elements().Select(Key).ToList();
		Assert.True(keys.IndexOf(role) < keys.IndexOf(old));
	}

	/// <summary>The item page's styles: its toggles are the system's icon toggle; its scrim is dusk, not 1.1.x's teal-black.</summary>
	[Fact]
	public void The_item_page_takes_the_icon_toggle_and_a_dusk_scrim()
	{
		var item = XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "ItemPage.xaml"))).Root!;
		var toggle = item.Elements().Single(e => Key(e) == "ItemGlassToggleButtonStyle");
		Assert.Equal("NixieIconToggleButtonStyle", (string?)toggle.Attribute("ResourceKey"));
		var stops = item.Descendants().Where(e => e.Name.LocalName == "GradientStop").Select(e => (string)e.Attribute("Color")!).ToList();
		Assert.NotEmpty(stops);
		Assert.All(stops, c => Assert.EndsWith("170508", c));
	}

	[Fact]
	public void Dialogs_get_Compact_buttons_and_a_destructive_dialog_defaults_to_Cancel()
	{
		var plain = Top("NixieDialogStyle").Elements().ToDictionary(e => (string)e.Attribute("Property")!, e => (string?)e.Attribute("Value"));
		Assert.Equal("{StaticResource NixiePrimaryCompactButtonStyle}", plain["PrimaryButtonStyle"]);
		Assert.Equal("{StaticResource NixieCompactButtonStyle}", plain["CloseButtonStyle"]);

		var destructive = Top("NixieDestructiveDialogStyle").Elements().ToDictionary(e => (string)e.Attribute("Property")!, e => (string?)e.Attribute("Value"));
		Assert.Equal("{StaticResource NixieDestructiveCompactButtonStyle}", destructive["PrimaryButtonStyle"]);
		Assert.Equal("{StaticResource NixieCompactButtonStyle}", destructive["CloseButtonStyle"]);
		Assert.Equal("Close", destructive["DefaultButton"]);
	}
}
