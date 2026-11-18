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

		// a string is underlined (the style's ContentTemplate); any content is shown (a ContentPresenter, not a TextBlock that
		// would print an element's type name)
		var linkStyle = Top("NixieLinkButtonStyle").ToString();
		Assert.Contains("TextDecorations=\"Underline\"", linkStyle);
		foreach (var shown in new[] { "NixieLinkButtonTemplate", "NixieTabButtonTemplate" })
		{
			var presenter = Top(shown).Descendants().Single(e => (string?)e.Attribute(X + "Name") == "ContentPresenter");
			Assert.Equal("ContentPresenter", presenter.Name.LocalName);
			Assert.Equal("{TemplateBinding Content}", (string?)presenter.Attribute("Content"));
		}
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
		var fill = light ? new Rgb(0xBC, 0x1E, 0x47) : NixiePalette.AccentDark;

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

	private static XElement TextBlockXaml() => XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "TextBlock.xaml"))).Root!;

	private static string Name(string? markup) => markup!.Replace("{StaticResource ", "").Replace("{ThemeResource ", "").TrimEnd('}').Trim();

	/// <summary>
	/// The style a dialog's button is drawn with, as it shows: the dialog style's button style (through BasedOn), unless the dialog
	/// template's DefaultButtonStates replace it for the default button. (The template is the base dialog style's, TextBlock.xaml.)
	/// </summary>
	private static string Applied(XElement? dialog, string button, string defaultButton)
	{
		var styles = TextBlockXaml().Elements().Concat(Glass().Elements()).Where(e => e.Name.LocalName == "Style").ToList();
		string? Setter(XElement? style, string property)
		{
			for (var s = style; s is not null;)
			{
				var value = (string?)s.Elements().FirstOrDefault(e => (string?)e.Attribute("Property") == property)?.Attribute("Value");
				if (value is not null)
				{
					return value;
				}

				var based = (string?)s.Attribute("BasedOn");
				s = based is null ? null : styles.FirstOrDefault(x => Key(x) == Name(based));
			}

			return null;
		}

		var template = styles.Single(s => Key(s) == "DefaultContentDialogStyleWithTitleBackground").Descendants().Single(e => e.Name.LocalName == "ControlTemplate");
		var state = template.Descendants().SingleOrDefault(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == $"{defaultButton}AsDefaultButton");
		var swapped = (string?)state?.Descendants().SingleOrDefault(e => (string?)e.Attribute("Target") == $"{button}Button.Style")?.Attribute("Value");
		return Name(swapped ?? Setter(dialog, $"{button}ButtonStyle"));
	}

	/// <summary>
	/// What each dialog's buttons look like once its default button is applied (the review: the template's default-button states
	/// swapped in WinUI's AccentButtonStyle, so a destructive dialog's default Cancel turned hot pink beside a glass Delete, and the
	/// Nixie dialog's commit lost its radius 8 and glow).
	/// </summary>
	[Fact]
	public void Dialogs_draw_their_buttons_in_the_Nixie_roles_once_the_default_button_is_applied()
	{
		var styles = TextBlockXaml().Elements().Concat(Glass().Elements()).Where(e => e.Name.LocalName == "Style").ToList();
		XElement Dialog(string key) => styles.Single(s => Key(s) == key);

		// the destructive dialog: Cancel (Close) is the default and stays Secondary; the confirm is Destructive
		var destructive = Dialog("NixieDestructiveDialogStyle");
		Assert.Equal("Close", (string?)destructive.Elements().Single(e => (string?)e.Attribute("Property") == "DefaultButton").Attribute("Value"));
		Assert.Equal("NixieCompactButtonStyle", Applied(destructive, "Close", "Close"));
		Assert.Equal("NixieDestructiveCompactButtonStyle", Applied(destructive, "Primary", "Close"));

		// the plain Nixie dialog: the default commit is the Primary, with the system's radius and its focus glow
		var plain = Dialog("NixieDialogStyle");
		var primary = Applied(plain, "Primary", "Primary");
		Assert.Equal("NixiePrimaryCompactButtonStyle", primary);
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", Setters(primary)["CornerRadius"]);
		Assert.Contains("FocusGlow", Top(Name(Setters(primary)["Template"])).ToString());
		Assert.Equal("NixieCompactButtonStyle", Applied(plain, "Close", "Primary"));

		// every dialog (the keyed base and the implicit one for dialogs made in code), whatever its default: never WinUI's accent
		var bases = new[] { Dialog("DefaultContentDialogStyleWithTitleBackground"), styles.Single(s => Key(s) is null && (string?)s.Attribute("TargetType") == "ContentDialog") };
		foreach (var dialog in bases.Append(plain).Append(destructive))
		{
			foreach (var defaultButton in new[] { "No", "Primary", "Secondary", "Close" })
			{
				foreach (var button in new[] { "Primary", "Secondary", "Close" })
				{
					var applied = Applied(dialog, button, defaultButton);
					Assert.StartsWith("Nixie", applied);
					Assert.DoesNotContain("Accent", applied);
				}
			}

			Assert.Equal(dialog == destructive ? "NixieDestructiveCompactButtonStyle" : "NixiePrimaryCompactButtonStyle", Applied(dialog, "Primary", dialog == destructive ? "Close" : "Primary"));
		}
	}

	/// <summary>
	/// Hover and pressed keep a button's own label colour (a local Foreground), as the 1.1.x pills did: no foreground setters there.
	/// High contrast shows them with a highlight border instead of a fill, so ButtonText on ButtonFace stays the pair.
	/// </summary>
	[Theory]
	[InlineData("NixieButtonTemplate")]
	[InlineData("NixieIconButtonTemplate")]
	[InlineData("NixieSubtleButtonTemplate")]
	[InlineData("NixieDestructiveButtonTemplate")]
	[InlineData("NixieIconToggleButtonTemplate")]
	public void Hover_and_pressed_keep_a_local_label_colour(string key)
	{
		foreach (var state in new[] { "PointerOver", "Pressed" })
		{
			var setters = States(Top(key)).Single(s => (string?)s.Attribute(X + "Name") == state).Descendants().Select(e => (string?)e.Attribute("Target")).ToList();
			Assert.DoesNotContain("ContentPresenter.Foreground", setters);
			Assert.Contains("Backplate.BorderBrush", setters);
		}

		string HighContrast(string brush) => (string)Theme("HighContrast").Elements().Single(e => Key(e) == brush).Attribute("Color")!;
		Assert.Equal("{ThemeResource SystemColorHighlightColor}", HighContrast("NixieHoverBorderBrush"));
		Assert.Equal("Transparent", HighContrast("NixieHoverOverlayBrush"));
		Assert.Equal("Transparent", HighContrast("NixiePressedOverlayBrush"));
	}

	/// <summary>
	/// The danger ink is not the palette: at least 30 degrees of OKLCH hue from every swatch (coral, pale sun, peach, the pinks,
	/// crimson, maroon, the nixie orange); the review's #FF8A80 sat 4 degrees from the pale sun. Still 4.5:1 on the glass fill.
	/// </summary>
	[Theory]
	[InlineData("Dark")]
	[InlineData("Light")]
	public void The_danger_ink_is_its_own_hue(string theme)
	{
		static double Hue(Rgb c)
		{
			var (_, _, h) = OkLab.FromLinear(BlurHash.SrgbToLinear(c.R), BlurHash.SrgbToLinear(c.G), BlurHash.SrgbToLinear(c.B));
			return (h + 360) % 360;
		}

		var danger = Hex(Colour(theme, "NixieDangerForegroundBrush"));
		Rgb[] palette = [DuskScene.Coral, DuskScene.Sun, DuskScene.Peach, DuskScene.HotPink, DuskScene.Rose, DuskScene.Crimson, DuskScene.Maroon,
			new(0xFF, 0x9E, 0xB1), new(0xFF, 0x7A, 0x50)];
		foreach (var swatch in palette)
		{
			var d = Math.Abs(Hue(danger) - Hue(swatch));
			Assert.True(Math.Min(d, 360 - d) >= 30, $"{danger.Hex} is {Math.Min(d, 360 - d):0} degrees from {swatch.Hex}");
		}
	}
}
