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
	};

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

	[Theory]
	[InlineData(false, false)]
	[InlineData(false, true)]
	[InlineData(true, false)]
	[InlineData(true, true)]
	public void The_overlay_tint_is_the_container_colour_translucent_outside_Solid_and_opaque_in_it(bool solid, bool light)
	{
		var palette = SeaglassPalette.Build(SeaglassPalette.DefaultHue, solid, light);
		var color = SeaglassTheme.OverlayColor(palette);
		Assert.Equal((palette.ContainerHigh.R, palette.ContainerHigh.G, palette.ContainerHigh.B), (color.R, color.G, color.B));
		Assert.Equal(solid ? (byte)255 : SeaglassTheme.OverlayAlpha, color.A);
		Assert.InRange(SeaglassTheme.OverlayAlpha, 0xD9, 0xF2); // readable text over a sharp window behind a tooltip, and still a hint of glass
	}
}

/// <summary>Seaglass 1.1.3: the Home hero's words panel is glass; what sits on it must not draw a second acrylic.</summary>
public sealed class HomeHeroGlassTests
{
	private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XElement Panel()
	{
		var root = XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Controls", "HomeHero.xaml"))).Root!;
		return root.Descendants().Single(e => e.Name.LocalName == "ContentControl" && (string?)e.Attribute("Style") == "{StaticResource GlassSurfaceStyle}");
	}

	[Fact]
	public void Play_or_Resume_is_the_accent_pill()
	{
		var play = Panel().Descendants().Single(e => e.Name.LocalName == "Button" && (string?)e.Attribute(Xaml + "Name") == "PlayButton");
		Assert.Equal("{StaticResource GlassAccentPillButtonStyle}", (string?)play.Attribute("Style"));
	}

	[Fact]
	public void The_buttons_on_the_glass_panel_are_flat_or_the_accent_never_a_second_acrylic()
	{
		var styles = Panel().Descendants().Where(e => e.Name.LocalName == "Button").Select(e => (string?)e.Attribute("Style")).ToList();
		Assert.NotEmpty(styles);
		Assert.All(styles, s => Assert.Contains(s, new[] { "{StaticResource GlassAccentPillButtonStyle}", "{StaticResource GlassInsetPillButtonStyle}" }));
	}
}
