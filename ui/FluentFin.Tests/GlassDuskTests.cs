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

	/// <summary>
	/// The full scene is held per owner (an empty state's SceneCard): asking twice is one hold, a release by someone who holds
	/// nothing changes nothing, and the scene goes when its last holder lets go. (A counter stayed up after a missed release or a
	/// double request, leaving the full scene, and its unreadable sky bands, behind every page.)
	/// </summary>
	[Fact]
	public void The_full_scene_is_held_per_owner_and_a_double_request_or_a_stray_release_cannot_leave_it_behind()
	{
		var changes = 0;
		void OnChanged(object? s, EventArgs e) => changes++;
		AmbientBackdrop.FullSceneChanged += OnChanged;
		object first = new(), second = new(), stranger = new();
		try
		{
			Assert.False(AmbientBackdrop.FullSceneRequested);
			AmbientBackdrop.RequestFullScene(first);
			AmbientBackdrop.RequestFullScene(first); // the same owner again (loaded twice): still one hold
			Assert.True(AmbientBackdrop.FullSceneRequested);
			AmbientBackdrop.ReleaseFullScene(first);
			Assert.False(AmbientBackdrop.FullSceneRequested); // one release ends it

			AmbientBackdrop.RequestFullScene(first);
			AmbientBackdrop.RequestFullScene(second);
			AmbientBackdrop.ReleaseFullScene(stranger); // holds nothing: no effect
			AmbientBackdrop.ReleaseFullScene(first);
			Assert.True(AmbientBackdrop.FullSceneRequested); // the second still holds it
			AmbientBackdrop.ReleaseFullScene(second);
			AmbientBackdrop.ReleaseFullScene(second); // twice: no effect
			Assert.False(AmbientBackdrop.FullSceneRequested);
			Assert.Equal(4, changes);
		}
		finally
		{
			AmbientBackdrop.FullSceneChanged -= OnChanged;
			AmbientBackdrop.ReleaseFullScene(first);
			AmbientBackdrop.ReleaseFullScene(second);
		}
	}

	/// <summary>
	/// The SceneCard (the only way a page asks for the full scene) puts its text on the strong glass, which keeps the glass text at
	/// 4.5:1 over the scene's worst band: the brightest in dark (light text), the darkest in light (dark text). Acrylic modelled as
	/// OverlayContrastTests does: the tint at its luminosity opacity, then at its tint opacity, over what is behind.
	/// </summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Text_on_a_scene_card_stays_readable_over_every_sky_band_and_hill(bool light)
	{
		var glass = Load("Styles", "Glass.xaml");
		var card = glass.Elements().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("TargetType") == "controls:SceneCard");
		Assert.Contains(card.Elements(), e => (string?)e.Attribute("Property") == "Background" && (string?)e.Attribute("Value") == "{ThemeResource GlassPanelStrongBrush}");

		var theme = Theme(light ? "Light" : "Dark");
		var panel = theme.Elements().Single(e => Key(e) == "GlassPanelStrongBrush");
		static Rgb Parse(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));
		var tint = Parse((string)panel.Attribute("TintColor")!);
		var luminosity = double.Parse((string)panel.Attribute("TintLuminosityOpacity")!, System.Globalization.CultureInfo.InvariantCulture);
		var opacity = double.Parse((string)panel.Attribute("TintOpacity")!, System.Globalization.CultureInfo.InvariantCulture);
		var behind = DuskScene.SkyBands(light).Select(b => b.Color)
			.Concat(Enum.GetValues<DuskHill>().Select(h => DuskScene.Hill(h, light)))
			.Concat([DuskScene.SunGradient(light).Top, DuskScene.SunGradient(light).Bottom]).ToList();
		foreach (var colour in behind)
		{
			var surface = DuskScene.Blend(tint, opacity, DuskScene.Blend(tint, luminosity, colour));
			foreach (var key in new[] { "GlassTextPrimaryBrush", "GlassTextSecondaryBrush" })
			{
				var text = Parse((string)theme.Elements().Single(e => Key(e) == key).Attribute("Color")!);
				var ratio = OkLab.Contrast(text.Luminance, surface.Luminance);
				Assert.True(ratio >= 4.5, $"{key} over {colour.Hex}: {ratio:0.00}");
			}
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
		foreach (var file in new[] { "Glass.xaml", "TextBlock.xaml", "Button.xaml" })
		{
			var text = Load("Styles", file).ToString();
			foreach (Match m in Regex.Matches(text, @"\{ThemeResource ((?:Glass|Seaglass)\w+)\}"))
			{
				Assert.True(highContrast.Contains(m.Groups[1].Value), $"{file}: {m.Groups[1].Value} has no high-contrast value");
			}
		}

		// and every key Dark or Light defines has a high-contrast value (the states stream's rule; a missing one falls back to dark)
		Assert.Empty(dark.Except(highContrast));
		Assert.Empty(Theme("Light").Elements().Select(Key).ToHashSet().Except(highContrast));
	}

	/// <summary>Dark: the neon core's near-white ring (a pink one would vanish on the hot-pink Play) around a deep line; light: deep maroon around white (crimson vanished on the crimson Play).</summary>
	[Theory]
	[InlineData("Dark", "#FFF4EE", "#170508")]
	[InlineData("Light", "#2D0F16", "#FFFFFF")]
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
	[InlineData(null, false, true)]
	[InlineData("", false, true)]
	[InlineData("0", false, true)]
	[InlineData("true", false, true)]
	[InlineData("1", false, false)]
	[InlineData("1", true, true)] // a release package ignores the harness's switch
	public void Only_the_harness_switch_set_to_1_in_a_dev_build_stops_following_the_windows_activity(string? value, bool release, bool follows) =>
		Assert.Equal(follows, GlassActivity.FollowsActivity(value, release));

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
			// a halo that fades in small steps: each layer narrower and stronger than the one around it
			Assert.Equal(5, layers.Count);
			Assert.Equal(NeonUnderline.TubeCore, core.Color);
			for (var i = 1; i < layers.Count; i++)
			{
				Assert.True(layers[i].Height < layers[i - 1].Height && layers[i].Opacity > layers[i - 1].Opacity);
			}
		}
	}

	/// <summary>The lit underline's gradient: symmetric, clear at its edges, the core flat in the middle, no step out of order.</summary>
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void The_underline_glow_is_one_continuous_symmetric_gradient(bool light)
	{
		var layers = NeonUnderline.Layers(light, false, default);
		var stops = NeonUnderline.Stops(layers);
		Assert.Equal(0, stops[0].Offset);
		Assert.Equal(1, stops[^1].Offset);
		Assert.Equal(0, stops[0].Color.A);
		Assert.Equal(0, stops[^1].Color.A);
		for (var i = 1; i < stops.Count; i++)
		{
			Assert.True(stops[i].Offset >= stops[i - 1].Offset);
			Assert.Equal(1 - stops[i].Offset, stops[stops.Count - 1 - i].Offset, 9);
			Assert.Equal(stops[i].Color, stops[stops.Count - 1 - i].Color);
		}

		// the core: full strength across its own height, in the middle
		Assert.Contains(stops, s => s.Color == layers[^1].Color && s.Offset < 0.5);
		Assert.Contains(stops, s => s.Color == layers[^1].Color && s.Offset > 0.5);
	}

	[Fact]
	public void The_tab_underline_is_the_system_highlight_alone_in_high_contrast()
	{
		// the colour is Windows' own highlight (UI settings), not a resource lookup that can miss and fall back to crimson
		var system = new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
		Assert.Equal(system, NeonUnderline.SystemHighlight());

		var highlight = Windows.UI.Color.FromArgb(255, 1, 2, 3);
		var layers = NeonUnderline.Layers(false, true, highlight);
		Assert.Single(layers);
		Assert.Equal(highlight, layers[0].Color);
	}

	[Fact]
	public void The_glow_colour_is_one_static_colour_and_the_glows_go_out_in_high_contrast()
	{
		var glass = Load("Styles", "Glass.xaml");
		// the outer glow's colour is a theme colour (orange in dark, crimson in light, the highlight in high contrast), never a top-level key
		Assert.DoesNotContain(glass.Elements(), e => Key(e) == "GlassNeonGlowColor");
		Assert.Equal("#FF7A50", Theme("Dark").Elements().Single(e => Key(e) == "GlassNeonGlowColor").Value);
		Assert.Equal("#BC1E47", Theme("Light").Elements().Single(e => Key(e) == "GlassNeonGlowColor").Value);
		Assert.Equal("SystemColorHighlightColor", (string?)Theme("HighContrast").Elements().Single(e => Key(e) == "GlassNeonGlowColor").Attribute("ResourceKey"));
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
