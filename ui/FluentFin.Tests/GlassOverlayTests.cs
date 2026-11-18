using System.Xml.Linq;
using FluentFin.Core.Theming;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.3: menus, context menus, flyouts, tooltips and every ContentDialog share one overlay recipe (Glass.xaml): one
/// blur, one corner radius, the glass text colour, opaque in Solid, never acrylic on acrylic.
/// </summary>
public sealed class GlassOverlayTests
{
	private static readonly string[] Acrylic = ["GlassPanelBrush", "GlassPanelStrongBrush"];

	private static XElement Load(params string[] path) => XDocument.Load(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. path]))).Root!;

	private static string? Key(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;

	private static IEnumerable<XElement> Styles(XElement root) => root.Descendants().Where(e => e.Name.LocalName == "Style");

	private static string? Setter(XElement style, string property) =>
		style.Elements().Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == property).Select(e => (string?)e.Attribute("Value")).FirstOrDefault();

	private static string? ResourceKey(string? markup) =>
		markup is null ? null : System.Text.RegularExpressions.Regex.Match(markup, @"^\{(?:ThemeResource|StaticResource) (\w+)\}$") is { Success: true } m ? m.Groups[1].Value : null;

	/// <summary>Each overlay style, and the setter value it ends up with after its BasedOn chain inside these files.</summary>
	private static string? Resolved(XElement style, string property, IReadOnlyDictionary<string, XElement> keyed)
	{
		for (var s = style; s is not null;)
		{
			if (Setter(s, property) is { } value)
			{
				return value;
			}

			s = ResourceKey((string?)s.Attribute("BasedOn")) is { } basedOn && keyed.TryGetValue(basedOn, out var parent) ? parent : null;
		}

		return null;
	}

	// a file may be passed twice (its own styles and the shared ones): a key is the same style either way
	private static Dictionary<string, XElement> Keyed(params XElement[] roots) =>
		roots.SelectMany(Styles).Where(s => Key(s) is not null).GroupBy(s => Key(s)!).ToDictionary(g => g.Key, g => g.Last());

	public static TheoryData<string, string> OverlayStyles() => new()
	{
		{ "Styles/Glass.xaml", "FlyoutPresenter" },
		{ "Styles/Glass.xaml", "MenuFlyoutPresenter" },
		{ "Styles/Glass.xaml", "ToolTip" },
		{ "Styles/TextBlock.xaml", "ContentDialog" },
		{ "Styles/TextBlock.xaml", "DefaultContentDialogStyleWithTitleBackground" },
		// the player's audio menu and its volume and subtitles flyouts (1.1.3 integration)
		{ "Controls/MpvPlayerControls.xaml", "PlayerMenuPresenter" },
		{ "Controls/MpvPlayerControls.xaml", "PlayerFlyoutPresenter" },
	};

	[Theory]
	[InlineData("PlayerMenuPresenter", "GlassMenuFlyoutPresenterStyle")]
	[InlineData("PlayerFlyoutPresenter", "GlassFlyoutPresenterStyle")]
	public void The_players_menus_take_the_shared_overlay_and_set_only_their_padding(string style, string basedOn)
	{
		var found = Find("Controls/MpvPlayerControls.xaml", style, out _);
		Assert.Equal($"{{StaticResource {basedOn}}}", (string?)found.Attribute("BasedOn"));
		var setters = found.Elements().Where(e => e.Name.LocalName == "Setter").Select(e => (string?)e.Attribute("Property")).ToList();
		Assert.Equal(["Padding"], setters);
	}

	private static XElement Find(string file, string keyOrType, out Dictionary<string, XElement> keyed)
	{
		var root = Load(file.Split('/'));
		keyed = Keyed(root, Load("Styles", "Glass.xaml"), Load("Styles", "TextBlock.xaml"));
		return Styles(root).Single(s => Key(s) == keyOrType || (Key(s) is null && (string?)s.Attribute("TargetType") == keyOrType));
	}

	[Theory]
	[MemberData(nameof(OverlayStyles))]
	public void Every_overlay_has_the_one_overlay_corner_and_the_glass_edge(string file, string style)
	{
		var found = Find(file, style, out var keyed);
		Assert.Equal("{StaticResource GlassOverlayCornerRadius}", Resolved(found, "CornerRadius", keyed));
		Assert.Equal("GlassStrokeBrush", ResourceKey(Resolved(found, "BorderBrush", keyed)));
	}

	[Theory]
	[MemberData(nameof(OverlayStyles))]
	public void Every_overlay_has_a_glass_background_that_Solid_makes_opaque(string file, string style)
	{
		var background = ResourceKey(Resolved(Find(file, style, out var keyed), "Background", keyed));
		Assert.NotNull(background);
		Assert.Contains(background, GlassTheme.Keys);
	}

	[Theory]
	[InlineData("Styles/Glass.xaml", "MenuFlyoutPresenter")]
	[InlineData("Styles/Glass.xaml", "ToolTip")]
	[InlineData("Controls/MpvPlayerControls.xaml", "PlayerMenuPresenter")]
	public void Menus_and_tooltips_are_windows_of_their_own_and_never_draw_in_app_acrylic(string file, string style)
	{
		// a menu's window already has Windows' acrylic under it (its default SystemBackdrop): in-app acrylic on it is acrylic on
		// acrylic, and in a tooltip's window it has nothing to blur
		var found = Find(file, style, out var keyed);
		Assert.DoesNotContain(ResourceKey(Resolved(found, "Background", keyed)), Acrylic);
		Assert.Null(Resolved(found, "SystemBackdrop", keyed)); // the menus keep WinUI's own backdrop: the one blur
	}

	[Fact]
	public void The_dialog_draws_its_glass_once_its_title_and_buttons_sit_on_it()
	{
		var style = Find("Styles/TextBlock.xaml", "DefaultContentDialogStyleWithTitleBackground", out _);
		var template = style.Descendants().Single(e => e.Name.LocalName == "ControlTemplate");
		var withBackground = template.Descendants()
			.Where(e => (string?)e.Attribute("Background") is { } b && (b.Contains("TemplateBinding Background") || Acrylic.Any(a => b.Contains(a))))
			.Select(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")))
			.ToList();
		Assert.Equal(["BackgroundElement"], withBackground);
		Assert.DoesNotContain(template.Descendants(), e => ((string?)e.Attribute("Background"))?.Contains("ContentDialogTopOverlay") == true);
	}

	[Fact]
	public void Dialogs_made_in_code_keep_WinUIs_width_and_every_dialog_title_wraps()
	{
		var textBlock = Load("Styles", "TextBlock.xaml");
		var keyed = Styles(textBlock).Single(s => Key(s) == "DefaultContentDialogStyleWithTitleBackground");
		var implicitDialog = Styles(textBlock).Single(s => Key(s) is null && (string?)s.Attribute("TargetType") == "ContentDialog");
		const string cap = "services:GlassDialog.PanelMaxWidth";

		// a sign-in error made in code: WinUI's 548 px, not the dialog pages' 1000 px (lines too long to read)
		Assert.Equal(548, GlassDialog.DefaultPanelMaxWidth);
		Assert.Equal(GlassDialog.DefaultPanelMaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), Setter(implicitDialog, cap));
		Assert.Equal(GlassDialog.PagePanelMaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), Setter(keyed, cap));

		var template = keyed.Descendants().Single(e => e.Name.LocalName == "ControlTemplate");
		var panel = template.Descendants().Single(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "BackgroundElement");
		Assert.Contains("(services:GlassDialog.PanelMaxWidth)", (string?)panel.Attribute("MaxWidth"));

		// a long title wraps instead of being cut off
		var title = template.Descendants().Single(e => e.Name.LocalName == "TextBlock" && (string?)e.Attribute("Text") == "{TemplateBinding Title}");
		Assert.Equal("Wrap", (string?)title.Attribute("TextWrapping"));
	}

	[Fact]
	public void Dialogs_made_in_code_get_the_same_template_as_the_dialog_pages()
	{
		var textBlock = Load("Styles", "TextBlock.xaml");
		var implicitDialog = Styles(textBlock).Single(s => Key(s) is null && (string?)s.Attribute("TargetType") == "ContentDialog");
		Assert.Equal("{StaticResource DefaultContentDialogStyleWithTitleBackground}", (string?)implicitDialog.Attribute("BasedOn"));
		// Glass.xaml is merged after TextBlock.xaml: an implicit dialog style there would win over this one
		Assert.DoesNotContain(Styles(Load("Styles", "Glass.xaml")), s => Key(s) is null && (string?)s.Attribute("TargetType") == "ContentDialog");
	}

	[Theory]
	[InlineData("Dark")]
	[InlineData("Light")]
	[InlineData("HighContrast")]
	public void Every_brush_the_Solid_swap_replaces_has_its_opaque_stand_in_in_every_theme(string theme)
	{
		var dictionary = Load("Styles", "Glass.xaml").Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == theme);
		var keys = dictionary.Elements().Select(Key).ToHashSet();
		foreach (var key in GlassTheme.Keys)
		{
			Assert.Contains(key, keys);
			Assert.Contains(key.Replace("Brush", "SolidBrush"), keys);
		}
	}

	[Theory]
	[InlineData("Light", Microsoft.UI.Xaml.ApplicationTheme.Light)]
	[InlineData("dark", Microsoft.UI.Xaml.ApplicationTheme.Dark)]
	[InlineData(" Light ", Microsoft.UI.Xaml.ApplicationTheme.Light)]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("Default", null)]
	[InlineData("7", null)]
	public void SEAGLASS_THEME_forces_the_whole_application_or_nothing(string? value, Microsoft.UI.Xaml.ApplicationTheme? expected) =>
		Assert.Equal(expected, GlassTheme.ForcedApplicationTheme(value));

	/// <summary>
	/// The colour menus and tooltips get at run time (NixieTheme.OverlayColor replaces the XAML's), with the palette's text on
	/// it, over the worst thing straight behind (a tooltip has no blur): white in the dark theme, black in the light one.
	/// </summary>
	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void Menu_and_tooltip_text_is_readable_on_the_runtime_overlay_colour_over_anything(bool solid, bool light)
	{
		var palette = NixiePalette.Build(NixiePalette.DefaultHue, solid, light);
		var overlay = NixieTheme.OverlayColor(palette);
		var behind = light ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
		Assert.True(TextContrast(palette, overlay.A, behind) >= 4.5, $"solid {solid}, light {light}: {TextContrast(palette, overlay.A, behind):0.00}:1");
		// the check can fail: a see-through overlay (25 %) over the same worst case does
		Assert.True(TextContrast(palette, 0x40, behind) < 4.5);
	}

	// the lower of the primary and the muted text's contrast on the overlay tint (alpha) composited over 'behind'
	private static double TextContrast(NixiePalette p, byte alpha, Rgb behind)
	{
		var a = alpha / 255.0;
		byte Mix(byte top, byte under) => (byte)Math.Round(top * a + under * (1 - a));
		var surface = new Rgb(Mix(p.ContainerHigh.R, behind.R), Mix(p.ContainerHigh.G, behind.G), Mix(p.ContainerHigh.B, behind.B));
		return Math.Min(OkLab.Contrast(p.OnSurface.Luminance, surface.Luminance), OkLab.Contrast(p.Muted.Luminance, surface.Luminance));
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void The_overlay_tint_is_the_container_colour_translucent_outside_Solid_and_opaque_in_it(bool solid, bool light)
	{
		var palette = NixiePalette.Build(NixiePalette.DefaultHue, solid, light);
		var color = NixieTheme.OverlayColor(palette);
		Assert.Equal((palette.ContainerHigh.R, palette.ContainerHigh.G, palette.ContainerHigh.B), (color.R, color.G, color.B));
		Assert.Equal(solid ? (byte)255 : NixieTheme.OverlayAlpha, color.A);
		Assert.InRange(NixieTheme.OverlayAlpha, 0xD9, 0xF2); // readable text over a sharp window behind a tooltip, and still a hint of glass
	}
}

/// <summary>The Home hero's words and buttons, on the artwork's scrims (1.1.3: on a glass panel; 1.2.0: full-bleed, no panel).</summary>
public sealed class HomeHeroGlassTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	// 1.2.0 (1.1.3 UX audit, layout item 1): the hero is full-bleed and its words sit straight on the artwork's scrims, in the
	// Words panel; there is no frosted panel behind them any more
	private static XElement Hero() => XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Controls", "HomeHero.xaml"))).Root!;

	private static XElement Panel() => Hero().Descendants().Single(e => e.Name.LocalName == "StackPanel" && (string?)e.Attribute(Xaml + "Name") == "Words");

	[Fact]
	public void Play_or_Resume_is_the_accent_pill()
	{
		var play = Panel().Descendants().Single(e => e.Name.LocalName == "Button" && (string?)e.Attribute(Xaml + "Name") == "PlayButton");
		Assert.Equal("{StaticResource GlassAccentPillButtonStyle}", (string?)play.Attribute("Style"));
	}

	[Fact]
	public void The_words_take_the_glass_text_brush_so_high_contrast_gets_the_system_text_colour()
	{
		// a written colour (White, #E6FFFFFF) stays white on the high contrast panel, which is the system window colour (white in
		// the light contrast themes)
		var foregrounds = Panel().DescendantsAndSelf().Select(e => (string?)e.Attribute("Foreground")).Where(f => f is not null).ToList();
		Assert.NotEmpty(foregrounds);
		Assert.All(foregrounds, f => Assert.StartsWith("{ThemeResource ", f));
	}

	[Fact]
	public void The_inset_pill_pairs_its_ButtonFace_fill_with_ButtonText_in_high_contrast()
	{
		// a custom contrast scheme can make ButtonFace differ from Window: WindowText on ButtonFace may then be unreadable
		var glass = XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "Glass.xaml"))).Root!;
		var hc = glass.Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(Xaml + "Key") == "HighContrast");
		string? Color(string key) => (string?)hc.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == key).Attribute("Color");
		Assert.Equal("{ThemeResource SystemColorButtonFaceColor}", Color("GlassInsetFillBrush"));
		Assert.Equal("{ThemeResource SystemColorButtonTextColor}", Color("GlassInsetTextBrush"));

		// 1.2.0: the inset pill is an alias of the Secondary button, whose fill and label are ButtonFace and ButtonText there
		var alias = glass.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == "GlassInsetPillButtonStyle");
		Assert.Equal("NixieButtonStyle", (string?)alias.Attribute("ResourceKey"));
		var style = glass.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == "NixieButtonStyle");
		var setters = style.Elements().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => (string)e.Attribute("Property")!, e => (string?)e.Attribute("Value"));
		Assert.Equal("{ThemeResource NixieButtonFillBrush}", setters["Background"]);
		Assert.Equal("{ThemeResource NixieButtonForegroundBrush}", setters["Foreground"]);
		Assert.Equal("{ThemeResource SystemColorButtonFaceColor}", Color("NixieButtonFillBrush"));
		Assert.Equal("{ThemeResource SystemColorButtonTextColor}", Color("NixieButtonForegroundBrush"));

		// and the hero's More info takes its style's label colour, not one of its own
		var moreInfo = Panel().Descendants().Single(e => e.Name.LocalName == "Button" && (string?)e.Attribute(Xaml + "Name") == "MoreInfoButton");
		Assert.Null(moreInfo.Attribute("Foreground"));
	}

	[Fact]
	public void The_buttons_are_the_accent_or_one_glass_pill_on_the_artwork_with_no_frosted_panel_behind_them()
	{
		// one glass layer at most: More info is the item hero's glass pill (ItemHero.xaml), straight on the scrim
		Assert.DoesNotContain(Hero().Descendants(), e => ((string?)e.Attribute("Style"))?.Contains("GlassSurfaceStyle") == true);
		var styles = Panel().Descendants().Where(e => e.Name.LocalName == "Button").Select(e => (string?)e.Attribute("Style")).ToList();
		Assert.Equal(["{StaticResource GlassAccentPillButtonStyle}", "{StaticResource GlassPillButtonStyle}"], styles);
	}
}
