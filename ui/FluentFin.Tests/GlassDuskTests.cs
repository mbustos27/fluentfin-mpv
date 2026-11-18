using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentFin.Controls;
using FluentFin.Core.Theming;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// 1.1.4 (the dusk palette, really glassy by default): where the dusk scene shows, when the backdrop moves, what the window's
/// activity, the glass body and the wordmark do in each mode, and that every theme dictionary has the keys the templates use.
/// </summary>
public sealed class GlassDuskTests
{
	private static XElement Load(params string[] path) => XDocument.Load(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. path]))).Root!;

	private static string? Key(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;

	private static XElement Theme(string name) =>
		Load("Styles", "Glass.xaml").Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == name);

	private static readonly Uri Art = new("http://x/a.jpg");

	[Theory]
	[InlineData(false, false, false, false, DuskSceneKind.Full)]   // the server page, sign-in
	[InlineData(true, false, false, false, DuskSceneKind.Full)]    // sign-in over the server's splash image
	[InlineData(false, false, true, false, DuskSceneKind.Strip)]   // Settings, the Library
	[InlineData(false, false, true, true, DuskSceneKind.Full)]     // an empty state that asks
	[InlineData(true, false, true, true, DuskSceneKind.None)]      // artwork wins inside the shell
	[InlineData(true, false, true, false, DuskSceneKind.None)]     // an item page, Home
	[InlineData(false, true, false, false, DuskSceneKind.None)]    // high contrast: nothing decorative
	[InlineData(false, true, true, false, DuskSceneKind.None)]
	public void The_scene_is_full_outside_the_shell_a_strip_inside_and_gone_over_artwork_or_in_high_contrast(
		bool artwork, bool highContrast, bool insideShell, bool fullRequested, DuskSceneKind expected) =>
		Assert.Equal(expected, AmbientBackdropView.SceneFor(artwork ? Art : null, highContrast, insideShell, fullRequested));

	[Theory]
	[InlineData(GlassModeKind.Full, true, true, true)]
	[InlineData(GlassModeKind.Full, true, false, false)]
	[InlineData(GlassModeKind.Full, false, true, false)]
	[InlineData(GlassModeKind.Reduced, true, true, false)]
	[InlineData(GlassModeKind.Solid, true, true, false)]
	public void The_backdrop_moves_only_in_Full_with_animations_and_an_active_window(GlassModeKind mode, bool animations, bool active, bool moves) =>
		Assert.Equal(moves, AmbientBackdropView.Moves(mode, animations, active));

	[Theory]
	[InlineData(DuskSceneKind.Full, GlassModeKind.Full, true)]
	[InlineData(DuskSceneKind.Full, GlassModeKind.Reduced, true)]
	[InlineData(DuskSceneKind.Full, GlassModeKind.Solid, false)]
	[InlineData(DuskSceneKind.Strip, GlassModeKind.Full, false)]
	[InlineData(DuskSceneKind.None, GlassModeKind.Full, false)]
	public void Scanlines_and_the_vignette_are_for_the_full_scene_and_never_Solid(DuskSceneKind scene, GlassModeKind mode, bool crt) =>
		Assert.Equal(crt, AmbientBackdropView.ShowsCrt(scene, mode));

	[Fact]
	public void The_scenes_motion_is_slow_and_small()
	{
		Assert.True(AmbientBackdropView.SwayDuration >= TimeSpan.FromSeconds(30));
		Assert.All(AmbientBackdropView.HillSway, s => Assert.InRange(s, 1f, 8f));
		Assert.InRange(AmbientBackdropView.SunBreath, 1.0f, 1.05f);
		Assert.InRange(AmbientBackdropView.TwinkleScale, 0.8f, 0.95f);
	}

	[Fact]
	public void An_empty_states_request_for_the_full_scene_is_undone_once()
	{
		var changes = 0;
		void OnChanged(object? s, EventArgs e) => changes++;
		AmbientBackdrop.FullSceneChanged += OnChanged;
		try
		{
			Assert.False(AmbientBackdrop.FullSceneRequested);
			AmbientBackdrop.RequestFullScene(true);
			AmbientBackdrop.RequestFullScene(true);
			Assert.True(AmbientBackdrop.FullSceneRequested);
			AmbientBackdrop.RequestFullScene(false);
			Assert.True(AmbientBackdrop.FullSceneRequested);
			AmbientBackdrop.RequestFullScene(false);
			AmbientBackdrop.RequestFullScene(false); // one too many: never below none
			Assert.False(AmbientBackdrop.FullSceneRequested);
			Assert.Equal(2, changes);
		}
		finally
		{
			AmbientBackdrop.FullSceneChanged -= OnChanged;
		}
	}

	[Fact]
	public void A_window_in_the_background_dims_the_light_edge_and_glow_and_back()
	{
		Assert.Equal(1.0, GlassActivity.EdgeLevel(true));
		Assert.InRange(GlassActivity.EdgeLevel(false), 0.3, 0.6);
		var edge = Windows.UI.Color.FromArgb(0x59, 0xFF, 0xED, 0xE8);
		var dim = SeaglassTheme.Scaled(edge, GlassActivity.EdgeLevel(false));
		Assert.Equal((edge.R, edge.G, edge.B), (dim.R, dim.G, dim.B));
		Assert.True(dim.A < edge.A);
		Assert.Equal(edge, SeaglassTheme.Scaled(edge, 1));
		Assert.Equal(0, SeaglassTheme.Scaled(edge, -1).A);

		var raised = 0;
		void OnChanged(object? s, EventArgs e) => raised++;
		GlassActivity.Changed += OnChanged;
		try
		{
			GlassActivity.Set(false);
			GlassActivity.Set(false);
			Assert.False(GlassActivity.IsActive);
			GlassActivity.Set(true);
			Assert.True(GlassActivity.IsActive);
			Assert.Equal(2, raised);
		}
		finally
		{
			GlassActivity.Changed -= OnChanged;
		}
	}

	[Fact]
	public void The_glass_body_is_faint_and_gone_in_Solid()
	{
		Assert.True(GlassVolume.ShowsIn(GlassModeKind.Full));
		Assert.True(GlassVolume.ShowsIn(GlassModeKind.Reduced));
		Assert.False(GlassVolume.ShowsIn(GlassModeKind.Solid));
		foreach (var light in new[] { false, true })
		{
			var (frost, shade) = GlassVolume.Strength(light);
			Assert.InRange(frost, 0.02, 0.35);
			Assert.InRange(shade, 0.02, 0.15);
		}

		Assert.InRange(GlassVolume.FrostStart, 0.4, 0.8);
		Assert.InRange(GlassVolume.ShadeStart, 0.4, 0.8);
	}

	[Fact]
	public void The_pointers_edge_light_is_a_short_bright_stroke()
	{
		Assert.InRange(PanelSheen.EdgeReach, 80f, 260f);
		Assert.InRange(PanelSheen.EdgeThickness, 1f, 2f);
		Assert.True(PanelSheen.EdgeColor(false).A < 0xFF);
	}

	[Theory]
	[InlineData(56, GlassModeKind.Full, false, true)]
	[InlineData(56, GlassModeKind.Reduced, false, true)]
	[InlineData(56, GlassModeKind.Solid, false, false)]
	[InlineData(56, GlassModeKind.Full, true, false)]
	[InlineData(16, GlassModeKind.Full, false, false)]
	public void The_wordmark_glows_only_at_display_size_and_never_in_Solid_or_high_contrast(double size, GlassModeKind mode, bool highContrast, bool glows) =>
		Assert.Equal(glows, AppWordmark.Glows(size, mode, highContrast));

	[Theory]
	[InlineData(false, 0x2A, 0x12, 0x17)] // the dark glass
	[InlineData(true, 0xFC, 0xEA, 0xE8)]  // the light glass
	public void The_wordmarks_tube_colour_reads_on_the_glass(bool light, byte r, byte g, byte b)
	{
		var tube = AppWordmark.TubeColor(light);
		var ratio = OkLab.Contrast(new Rgb(tube.R, tube.G, tube.B).Luminance, new Rgb(r, g, b).Luminance);
		Assert.True(ratio >= 4.5, $"{ratio:0.00}");
	}

	[Fact]
	public void The_neon_font_ships_whole_with_its_licence_and_is_copied_to_the_output()
	{
		var font = AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Assets", "Fonts", "Monoton-Regular.ttf"));
		var licence = AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Assets", "Fonts", "Monoton-OFL.txt"));
		Assert.Equal(51604, new FileInfo(font).Length); // the Google Fonts file, unmodified (its Reserved Font Name forbids a changed copy)
		Assert.Contains("SIL Open Font License", File.ReadAllText(licence));
		var csproj = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "FluentFin.csproj")));
		Assert.Contains("Assets\\Fonts\\Monoton-Regular.ttf", csproj);
		Assert.Contains("Assets\\Fonts\\Monoton-OFL.txt", csproj);
		Assert.EndsWith("#Monoton", AppWordmark.NeonFont);
	}

	[Fact]
	public void The_dark_and_light_dictionaries_define_the_same_keys()
	{
		var dark = Theme("Dark").Elements().Select(Key).ToHashSet();
		var light = Theme("Light").Elements().Select(Key).ToHashSet();
		Assert.Empty(dark.Except(light));
		Assert.Empty(light.Except(dark));
	}

	/// <summary>
	/// Every glass key a template or style looks up as a ThemeResource is in the high-contrast dictionary too (a missing one there is
	/// a crash in high contrast). Only WinUI's own keys, which fall back to WinUI's high-contrast values, are left out of it.
	/// </summary>
	[Fact]
	public void Every_glass_ThemeResource_the_styles_use_exists_in_high_contrast()
	{
		var highContrast = Theme("HighContrast").Elements().Select(Key).ToHashSet();
		var dark = Theme("Dark").Elements().Select(Key).ToHashSet();
		foreach (var file in new[] { "Glass.xaml", "NavigationView.xaml", "TextBlock.xaml", "Button.xaml" })
		{
			var text = Load("Styles", file).ToString();
			foreach (Match m in Regex.Matches(text, @"\{ThemeResource ((?:Glass|Seaglass)\w+)\}"))
			{
				Assert.True(highContrast.Contains(m.Groups[1].Value), $"{file}: {m.Groups[1].Value} has no high-contrast value");
			}
		}

		// what the Dark dictionary adds beyond high contrast is WinUI's own keys (focus visuals, the dialog's smoke), which WinUI
		// defines for it
		Assert.All(dark.Except(highContrast), k => Assert.Contains(k, new[] { "SystemControlFocusVisualPrimaryBrush", "SystemControlFocusVisualSecondaryBrush", "ContentDialogSmokeFill" }));
	}

	/// <summary>Dark: the neon core's near-white ring (a pink one would vanish on the hot-pink Play) around a deep line; light: crimson around white.</summary>
	[Theory]
	[InlineData("Dark", "#FFF4EE", "#170508")]
	[InlineData("Light", "#BC1E47", "#FFFFFF")]
	public void The_focus_rings_read_on_the_accent_and_on_the_glass(string theme, string outer, string inner)
	{
		var dictionary = Theme(theme);
		string Color(string key) => (string)dictionary.Elements().Single(e => Key(e) == key).Attribute("Color")!;
		Assert.Equal(outer, Color("SystemControlFocusVisualPrimaryBrush"));
		Assert.Equal(inner, Color("SystemControlFocusVisualSecondaryBrush"));
		static Rgb Parse(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));
		// the ring against its own inner line: 3:1 for a focus indicator
		Assert.True(OkLab.Contrast(Parse(Color("SystemControlFocusVisualPrimaryBrush")).Luminance, Parse(inner).Luminance) >= 3);
	}

	[Theory]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Light, Microsoft.UI.Windowing.TitleBarTheme.Light)]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Dark, Microsoft.UI.Windowing.TitleBarTheme.Dark)]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Default, Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode)]
	public void An_app_theme_also_themes_the_caption_buttons(Microsoft.UI.Xaml.ElementTheme theme, Microsoft.UI.Windowing.TitleBarTheme expected) =>
		Assert.Equal(expected, GlassTheme.TitleBarThemeFor(theme));

	/// <summary>
	/// The caption buttons in an app-set theme: the glyphs read on the top of the window in both the dusk scene and the plain backdrop
	/// (4.5:1 active, 3:1 in the background); Default leaves Windows' own colours.
	/// </summary>
	[Theory]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Dark, new[] { "#500723", "#170508" })]
	[InlineData(Microsoft.UI.Xaml.ElementTheme.Light, new[] { "#FFF4EE", "#FFF6F3" })]
	public void An_app_theme_gives_the_caption_buttons_readable_glyphs(Microsoft.UI.Xaml.ElementTheme theme, string[] tops)
	{
		var colors = GlassTheme.CaptionColorsFor(theme);
		Assert.NotNull(colors);
		static Rgb Parse(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));
		foreach (var top in tops.Select(Parse))
		{
			var c = colors.Value;
			Assert.True(OkLab.Contrast(new Rgb(c.Foreground.R, c.Foreground.G, c.Foreground.B).Luminance, top.Luminance) >= 4.5, $"{top.Hex}");
			Assert.True(OkLab.Contrast(new Rgb(c.Inactive.R, c.Inactive.G, c.Inactive.B).Luminance, top.Luminance) >= 3, $"{top.Hex} inactive");
		}

		Assert.Null(GlassTheme.CaptionColorsFor(Microsoft.UI.Xaml.ElementTheme.Default));
	}

	[Theory]
	[InlineData(null, true)]
	[InlineData("", true)]
	[InlineData("0", true)]
	[InlineData("true", true)]
	[InlineData("1", false)]
	public void Only_the_harness_switch_set_to_1_stops_following_the_windows_activity(string? value, bool follows) =>
		Assert.Equal(follows, GlassActivity.FollowsActivity(value));

	/// <summary>1.1.4 (the layout audit's boxes in boxes): a section style that draws nothing, and 12 px panels.</summary>
	[Fact]
	public void A_section_can_sit_on_the_page_without_a_panel_and_panels_are_12_px()
	{
		var glass = Load("Styles", "Glass.xaml");
		var section = glass.Elements().Single(e => Key(e) == "GlassSectionStyle").ToString();
		Assert.DoesNotContain("Background", section);
		Assert.DoesNotContain("Shadow", section);
		Assert.DoesNotContain("BorderBrush", section);
		Assert.Equal("12", glass.Elements().Single(e => Key(e) == "GlassPanelCornerRadius").Value);
		Assert.DoesNotContain("CornerRadius=\"20\"", glass.ToString());
		Assert.DoesNotContain("CornerRadius=\"20\"", Load("Styles", "NavigationView.xaml").ToString());
	}

	/// <summary>
	/// The rail's selected icon is a mark (3:1) on the selected backplate: the white selection overlay over the rail's glass as
	/// measured in the dark Full shots (#3E282E). Hot pink fails there (2.7:1), so the dark rail uses the light pink.
	/// </summary>
	[Fact]
	public void The_rails_selected_icon_reads_on_the_selected_backplate()
	{
		var rail = Load("Styles", "NavigationView.xaml").Descendants()
			.Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == "Default");
		var selected = (string)rail.Elements().Single(e => Key(e) == "NavigationViewSelectionIndicatorForeground").Attribute("Color")!;
		var overlay = (string)Theme("Dark").Elements().Single(e => Key(e) == "GlassSelectedOverlayBrush").Attribute("Color")!;
		static Rgb Parse(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));
		var backplate = DuskScene.Blend(new Rgb(255, 255, 255), Convert.ToByte(overlay[1..3], 16) / 255.0, new Rgb(0x3E, 0x28, 0x2E));
		Assert.True(OkLab.Contrast(Parse(selected).Luminance, backplate.Luminance) >= 3, $"{selected} on {backplate.Hex}");
		Assert.True(OkLab.Contrast(SeaglassPalette.AccentDark.Luminance, backplate.Luminance) < 3, "the hot pink would not read here");
		// and never in the light dictionary or high contrast: WinUI's own there
		Assert.DoesNotContain(Load("Styles", "NavigationView.xaml").Descendants().Where(e => e.Name.LocalName == "ResourceDictionary" && Key(e) != "Default")
			.SelectMany(d => d.Elements()), e => Key(e) == "NavigationViewSelectionIndicatorForeground");
	}

	/// <summary>1.2.0, the top bar: its glass, Solid stand-in, hairline and tab accent exist in every theme; the glass swaps in Solid.</summary>
	[Fact]
	public void The_top_bar_resources_exist_in_every_theme_and_the_title_bar_glass_turns_solid()
	{
		foreach (var theme in new[] { "Dark", "Light", "HighContrast" })
		{
			var keys = Theme(theme).Elements().Select(Key).ToHashSet();
			foreach (var key in new[] { "GlassTitleBarBrush", "GlassTitleBarSolidBrush", "GlassTitleBarStrokeBrush", "GlassTabUnderlineBrush" })
			{
				Assert.True(keys.Contains(key), $"{theme}: {key}");
			}
		}

		Assert.Contains("GlassTitleBarBrush", GlassTheme.Keys);
		foreach (var theme in new[] { "Dark", "Light" })
		{
			Assert.Equal("AcrylicBrush", Theme(theme).Elements().Single(e => Key(e) == "GlassTitleBarBrush").Name.LocalName);
		}
	}

	/// <summary>The lit underline: a mark (3:1) on the title bar's glass, its core never the hot pink alone (it must read on pink art too).</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_tab_underline_reads_on_the_title_bar(bool light)
	{
		static Rgb Parse(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));
		var bar = Parse((string)Theme(light ? "Light" : "Dark").Elements().Single(e => Key(e) == "GlassTitleBarSolidBrush").Attribute("Color")!);
		var layers = NeonUnderline.Layers(light, false, default);
		var core = layers[^1];
		Assert.Equal(1, core.Opacity);
		Assert.True(OkLab.Contrast(new Rgb(core.Color.R, core.Color.G, core.Color.B).Luminance, bar.Luminance) >= 3);
		Assert.All(layers, l => Assert.InRange(l.Height, 1, NeonUnderline.StackHeight));
		// the brush the account stream may use without the control: the same accent
		var brush = Parse((string)Theme(light ? "Light" : "Dark").Elements().Single(e => Key(e) == "GlassTabUnderlineBrush").Attribute("Color")!);
		Assert.True(OkLab.Contrast(brush.Luminance, bar.Luminance) >= 3);
		if (!light)
		{
			Assert.Equal(3, layers.Count);
			Assert.Equal(NeonUnderline.NeonCore, core.Color);
		}
	}

	[Fact]
	public void The_tab_underline_is_the_system_highlight_alone_in_high_contrast()
	{
		var highlight = Windows.UI.Color.FromArgb(255, 1, 2, 3);
		var layers = NeonUnderline.Layers(false, true, highlight);
		Assert.Single(layers);
		Assert.Equal(highlight, layers[0].Color);
	}

	[Fact]
	public void The_glow_colour_is_one_static_colour_and_the_glows_go_out_in_high_contrast()
	{
		var glass = Load("Styles", "Glass.xaml");
		Assert.Contains(glass.Elements(), e => e.Name.LocalName == "Color" && Key(e) == "GlassNeonGlowColor");
		var text = glass.ToString();
		// each shadow's opacity is a shadow opacity, which high contrast sets to 0. (Not a value of the glows' own: a key with a
		// value outside the theme dictionaries overrides the theme dictionaries' values, measured: no glow at all.)
		foreach (Match m in Regex.Matches(text, @"<media:AttachedCardShadow[^>]*>", RegexOptions.Singleline))
		{
			Assert.Matches(@"Opacity=""\{ThemeResource Glass(Contact)?ShadowOpacity\}""", m.Value);
		}
		// the trap itself: no key both at the top of Glass.xaml and in a theme dictionary (the top one would win in every theme)
		var top = glass.Elements().Select(Key).Where(k => k is not null).ToHashSet();
		foreach (var theme in new[] { "Dark", "Light", "HighContrast" })
		{
			Assert.Empty(top.Intersect(Theme(theme).Elements().Select(Key)));
		}

		var hc = Theme("HighContrast");
		Assert.Equal("0", hc.Elements().Single(e => Key(e) == "GlassShadowOpacity").Value);
		Assert.Equal("0", hc.Elements().Single(e => Key(e) == "GlassContactShadowOpacity").Value);

	}
}
