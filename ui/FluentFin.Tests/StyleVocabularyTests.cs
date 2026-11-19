using System.Xml.Linq;
using FluentFin.Core.Theming;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.4.0 (the library UX review, plan items 7 and 11): the shared style keys in Styles/Glass.xaml that hold one accent
/// budget, one highlight vocabulary and one button system across the four themes. The library and player streams use these
/// keys by name, so their existence, sizes and meanings are pinned here.
/// </summary>
public sealed class StyleVocabularyTests
{
	private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

	private static XElement Glass() => XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "Glass.xaml"))).Root!;

	private static string? Key(XElement e) => (string?)e.Attribute(X + "Key");

	private static XElement Top(string key) => Glass().Elements().Single(e => Key(e) == key);

	private static XElement Theme(string name) =>
		Glass().Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == name);

	private static Dictionary<string, string?> Setters(string style) =>
		Top(style).Elements().Where(e => e.Name.LocalName == "Setter").ToDictionary(e => (string)e.Attribute("Property")!, e => (string?)e.Attribute("Value"));

	private static IEnumerable<XElement> States(XElement template) => template.Descendants().Where(e => e.Name.LocalName == "VisualState");

	private static XElement State(string template, string name) => States(Top(template)).Single(s => (string?)s.Attribute(X + "Name") == name);

	/// <summary>The keys other streams were told to use (the style stream's report lists them).</summary>
	[Theory]
	[InlineData("NixieWatchedBadgeStyle", "Border")]
	[InlineData("NixieWatchedGlyphStyle", "FontIcon")]
	[InlineData("NixieCountChipStyle", "Border")]
	[InlineData("NixieCountChipTextStyle", "TextBlock")]
	[InlineData("NixieCardActionStripStyle", "StackPanel")]
	[InlineData("NixiePrimaryIconButtonStyle", "Button")]
	[InlineData("NixieCardIconButtonStyle", "Button")]
	[InlineData("NixieCardIconToggleButtonStyle", "ToggleButton")]
	[InlineData("NixieToolbarButtonStyle", "Button")]
	[InlineData("NixieChipButtonStyle", "Button")]
	[InlineData("NixieChipToggleButtonStyle", "ToggleButton")]
	[InlineData("NixieChoiceChipStyle", "RadioButton")]
	[InlineData("NixieSectionTabButtonStyle", "Button")]
	[InlineData("NixieLetterButtonStyle", "Button")]
	[InlineData("NixiePlayerButtonStyle", "Button")]
	[InlineData("NixiePlayerPlayButtonStyle", "Button")]
	[InlineData("NixiePlayerPromptButtonStyle", "Button")]
	[InlineData("NixiePlayerPromptPrimaryButtonStyle", "Button")]
	[InlineData("NixieDeleteCompactButtonStyle", "Button")]
	[InlineData("NixieDeleteDialogStyle", "ContentDialog")]
	public void The_shared_keys_exist_for_their_type(string key, string target)
	{
		var style = Top(key);
		Assert.Equal("Style", style.Name.LocalName);
		Assert.Equal(target, (string?)style.Attribute("TargetType"));
	}

	/// <summary>
	/// X10: one badge meant both "watched" and "N to watch", a pink capsule in one corner. Now two marks: a dim neutral check top
	/// right, a neutral square count top left, both on the dark plate, at the badge radius (6, never a capsule), never the accent.
	/// </summary>
	[Fact]
	public void Watched_and_to_watch_are_two_neutral_square_marks_in_two_corners()
	{
		var watched = Setters("NixieWatchedBadgeStyle");
		var count = Setters("NixieCountChipStyle");
		Assert.Equal("Right", watched["HorizontalAlignment"]);
		Assert.Equal("Left", count["HorizontalAlignment"]);
		Assert.Equal("Top", watched["VerticalAlignment"]);
		Assert.Equal("Top", count["VerticalAlignment"]);
		foreach (var setters in new[] { watched, count })
		{
			Assert.Equal("{StaticResource NixieBadgeCornerRadius}", setters["CornerRadius"]);
			Assert.Equal("{StaticResource NixieBadgeSize}", setters["Height"]);
			Assert.Equal("{ThemeResource NixieBadgeFillBrush}", setters["Background"]);
		}

		Assert.Equal("{StaticResource NixieBadgeSize}", watched["Width"]);
		Assert.Equal("{StaticResource NixieBadgeSize}", count["MinWidth"]);
		Assert.Equal("6", Top("NixieBadgeCornerRadius").Value);
		Assert.Equal("24", Top("NixieBadgeSize").Value);
		Assert.Equal("{ThemeResource NixieWatchedGlyphBrush}", Setters("NixieWatchedGlyphStyle")["Foreground"]);
		Assert.Equal("{ThemeResource NixieBadgeForegroundBrush}", Setters("NixieCountChipTextStyle")["Foreground"]);

		foreach (var key in new[] { "NixieWatchedBadgeStyle", "NixieWatchedGlyphStyle", "NixieCountChipStyle", "NixieCountChipTextStyle" })
		{
			Assert.DoesNotContain("Accent", Top(key).ToString());
		}
	}

	private static Rgb Hex(string hex) => new(Convert.ToByte(hex[^6..^4], 16), Convert.ToByte(hex[^4..^2], 16), Convert.ToByte(hex[^2..], 16));

	private static string Colour(string theme, string key) => (string)Theme(theme).Elements().Single(e => Key(e) == key).Attribute("Color")!;

	private static Rgb Over(string argb, Rgb under) => DuskScene.Blend(Hex(argb), Convert.ToByte(argb[1..3], 16) / 255.0, under);

	/// <summary>The marks read on any artwork: over a white still, the count at 4.5:1 (text) and the dim check at 3:1 (a graphic).</summary>
	[Theory]
	[InlineData("Dark")]
	[InlineData("Light")]
	public void The_badges_read_on_white_art(string theme)
	{
		var plate = Over(Colour(theme, "NixieBadgeFillBrush"), new Rgb(0xFF, 0xFF, 0xFF));
		var count = Hex(Colour(theme, "NixieBadgeForegroundBrush"));
		var check = Hex(Colour(theme, "NixieWatchedGlyphBrush"));
		Assert.True(OkLab.Contrast(count.Luminance, plate.Luminance) >= 4.5, $"count {count.Hex} on {plate.Hex}");
		Assert.True(OkLab.Contrast(check.Luminance, plate.Luminance) >= 3, $"check {check.Hex} on {plate.Hex}");
		// dim: the check sits below the count's brightness
		Assert.True(check.Luminance < count.Luminance);
	}

	/// <summary>Chips are 32 (E-m6: 34 was a third height) at radius 8; a picked chip is a neutral plate, a switch adds its check.</summary>
	[Fact]
	public void Chips_are_32_at_radius_8_and_their_on_states_are_neutral()
	{
		foreach (var key in new[] { "NixieChipToggleButtonStyle", "NixieChoiceChipStyle" })
		{
			var setters = Setters(key);
			Assert.Equal("{StaticResource NixieCompactButtonHeight}", setters["Height"]);
			Assert.Equal("{StaticResource GlassChipCornerRadius}", setters["CornerRadius"]);
			Assert.Equal("0", setters["MinWidth"]);
		}

		Assert.Equal("8", Top("GlassChipCornerRadius").Value);
		Assert.Equal("NixieCompactButtonStyle", Top("NixieChipButtonStyle").Attribute("BasedOn")!.Value.Replace("{StaticResource ", "").TrimEnd('}'));

		var on = State("NixieChipToggleButtonTemplate", "Checked").ToString();
		Assert.Contains("CheckGlyph.Visibility", on);
		Assert.Contains("NixieChipSelectedFillBrush", on);
		Assert.DoesNotContain("Accent", on);
		Assert.DoesNotContain("NixieToggleOn", on);

		var picked = State("NixieChoiceChipTemplate", "Checked").ToString();
		Assert.Contains("NixieChipSelectedFillBrush", picked);
		Assert.Contains("SemiBold", picked);
		Assert.DoesNotContain("CheckGlyph", picked);
		Assert.DoesNotContain("Accent", picked);
	}

	/// <summary>
	/// C-M7: one 3 px pink underline marked the section, the view and the scroll letter. The underline is the top bar's "you are
	/// here" alone: a section tab is a plate and the weight; the current letter bold accent text, no underline, no plate.
	/// </summary>
	[Fact]
	public void Section_tabs_are_a_plate_and_the_current_letter_is_bold_accent_text()
	{
		var section = Top("NixieSectionTabButtonTemplate").ToString();
		Assert.DoesNotContain("Underline", section);
		Assert.DoesNotContain("GlassTabUnderlineBrush", section);
		var selected = State("NixieSectionTabButtonTemplate", "Selected").ToString();
		Assert.Contains("NixieChipSelectedFillBrush", selected);
		Assert.Contains("SemiBold", selected);

		var letter = Top("NixieLetterButtonTemplate").ToString();
		Assert.DoesNotContain("Underline", letter);
		var current = State("NixieLetterButtonTemplate", "Selected").ToString();
		Assert.Contains("NixieCurrentLetterForegroundBrush", current);
		Assert.Contains("Bold", current);
		Assert.DoesNotContain("Backplate.Background", current);

		var setters = Setters("NixieLetterButtonStyle");
		Assert.Equal("{StaticResource NixieCompactButtonHeight}", setters["MinWidth"]);
		Assert.Equal("{StaticResource NixieCompactButtonHeight}", setters["MinHeight"]);
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", setters["CornerRadius"]);
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", Setters("NixieSectionTabButtonStyle")["CornerRadius"]);
	}

	/// <summary>
	/// TopTabStrip and the Tab role: hover drew a 22 x 2 line exactly where the 22 x 3 selected underline sits (hover and selection
	/// differed by hue alone). The role's hover is now the overlay fill; pressed is never the selected look.
	/// </summary>
	[Fact]
	public void The_tab_role_hovers_as_a_fill_not_a_line_and_pressed_is_not_selected()
	{
		var tab = Top("NixieTabButtonTemplate");
		Assert.DoesNotContain(tab.Descendants(), e => (string?)e.Attribute(X + "Name") == "HoverLine");
		Assert.Contains("NixieHoverOverlayBrush", State("NixieTabButtonTemplate", "PointerOver").ToString());
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", Setters("NixieTabButtonStyle")["CornerRadius"]);

		foreach (var template in new[] { "NixieTabButtonTemplate", "NixieSectionTabButtonTemplate", "NixieLetterButtonTemplate", "NixieChoiceChipTemplate", "NixieChipToggleButtonTemplate" })
		{
			var pressed = State(template, "Pressed").ToString();
			Assert.DoesNotContain("Underline", pressed);
			Assert.DoesNotContain("Selected", pressed);
			Assert.DoesNotContain("CurrentLetter", pressed);
		}
	}

	/// <summary>Card actions (DI-m6, CL-M12): the Nixie icon roles and the Primary, 44 with 20 px glyphs, in one strip.</summary>
	[Fact]
	public void Card_actions_are_the_icon_roles_and_the_Primary_with_20_px_glyphs()
	{
		Assert.Equal("{StaticResource NixieIconButtonStyle}", (string?)Top("NixiePrimaryIconButtonStyle").Attribute("BasedOn"));
		Assert.Equal("{ThemeResource NixiePrimaryFillBrush}", Setters("NixiePrimaryIconButtonStyle")["Background"]);
		Assert.Equal("{StaticResource NixieIconButtonStyle}", (string?)Top("NixieCardIconButtonStyle").Attribute("BasedOn"));
		Assert.Equal("{StaticResource NixieIconToggleButtonStyle}", (string?)Top("NixieCardIconToggleButtonStyle").Attribute("BasedOn"));
		Assert.Equal("20", Top("NixieCardGlyphSize").Value);
		Assert.Equal("Horizontal", Setters("NixieCardActionStripStyle")["Orientation"]);
		Assert.Equal("8", Setters("NixieCardActionStripStyle")["Spacing"]);
	}

	/// <summary>The player's transport on the icon role; Play/Pause at 60 the one hero exception; the prompts on Secondary and Primary.</summary>
	[Fact]
	public void The_player_keys_are_on_the_Nixie_roles_with_one_60_px_hero()
	{
		Assert.Equal("{StaticResource NixieIconButtonStyle}", (string?)Top("NixiePlayerButtonStyle").Attribute("BasedOn"));
		Assert.Equal("{StaticResource NixieIconButtonStyle}", (string?)Top("NixiePlayerPlayButtonStyle").Attribute("BasedOn"));
		Assert.Equal("60", Top("NixiePlayerHeroButtonSize").Value);
		Assert.Equal("{StaticResource NixiePlayerHeroButtonSize}", Setters("NixiePlayerPlayButtonStyle")["Width"]);
		Assert.Equal("{StaticResource NixieButtonStyle}", (string?)Top("NixiePlayerPromptButtonStyle").Attribute("BasedOn"));
		Assert.Equal("{StaticResource NixiePrimaryButtonStyle}", (string?)Top("NixiePlayerPromptPrimaryButtonStyle").Attribute("BasedOn"));
	}

	/// <summary>The delete confirmation: the Destructive role with the trash glyph (E74D), Cancel the default, Compact radius 8.</summary>
	[Fact]
	public void The_delete_confirm_carries_the_trash_glyph_on_the_destructive_role()
	{
		var delete = Top("NixieDeleteCompactButtonStyle");
		Assert.Equal("{StaticResource NixieDestructiveCompactButtonStyle}", (string?)delete.Attribute("BasedOn"));
		Assert.Contains("&#xE74D;", File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "Glass.xaml"))).Split("x:Key=\"NixieDeleteCompactButtonStyle\"")[1].Split("</Style>")[0]);
		Assert.Equal("{StaticResource GlassButtonCornerRadius}", Setters("NixieDestructiveCompactButtonStyle")["CornerRadius"]);

		var dialog = Top("NixieDeleteDialogStyle");
		Assert.Equal("{StaticResource NixieDestructiveDialogStyle}", (string?)dialog.Attribute("BasedOn"));
		Assert.Equal("{StaticResource NixieDeleteCompactButtonStyle}", Setters("NixieDeleteDialogStyle")["PrimaryButtonStyle"]);
	}

	/// <summary>Every toolbar is Compact 32: Classic's Large override (CL-M12) is gone.</summary>
	[Fact]
	public void Every_toolbar_is_compact()
	{
		Assert.Equal("{StaticResource NixieCompactButtonStyle}", (string?)Top("NixieToolbarButtonStyle").Attribute("BasedOn"));
		var library = XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Views", "LibraryPage.xaml"))).Root!;
		var style = library.Descendants().Single(e => Key(e) == "LibrarySortFilterButtonStyle");
		Assert.Equal("{StaticResource NixieToolbarButtonStyle}", (string?)style.Attribute("BasedOn"));
		Assert.DoesNotContain(style.Elements(), e => (string?)e.Attribute("Property") is "Padding" or "MinHeight" or "Height");
	}

	private static XElement Ui(string path) => XDocument.Load(AppVersionTests.FindUp(Path.Combine(["ui", "FluentFin", .. path.Split('/')]))).Root!;

	private static XElement Named(XElement root, string name) => root.Descendants().Single(e => (string?)e.Attribute(X + "Name") == name);

	private static XElement Keyed(XElement root, string key) => root.Descendants().Single(e => Key(e) == key);

	/// <summary>A delete's confirm carries the trash glyph; any other destructive verb is the plain Destructive role.</summary>
	[Theory]
	[InlineData("Delete", "NixieDeleteCompactButtonStyle")]
	[InlineData("Delete user", "NixieDeleteCompactButtonStyle")]
	[InlineData("delete library", "NixieDeleteCompactButtonStyle")]
	[InlineData("Remove", "NixieDestructiveCompactButtonStyle")]
	[InlineData("Forget", "NixieDestructiveCompactButtonStyle")]
	[InlineData("Restart server", "NixieDestructiveCompactButtonStyle")]
	[InlineData(null, "NixieDestructiveCompactButtonStyle")]
	public void A_delete_confirm_takes_the_trash_glyph_role(string? verb, string key)
	{
		Assert.Equal(key, FluentFin.Dialogs.ButtonRoles.ConfirmStyleKey(verb));
		Assert.Equal("Style", Top(key).Name.LocalName);
	}

	/// <summary>E-m8: the sunset rule's #F96985 band sat 12 px under the tab's underline and read as a second "here"; in the player it ran over the video.</summary>
	[Fact]
	public void Editorials_sunset_rule_has_no_pink_band_and_hides_over_the_player()
	{
		var editorial = Ui("Themes/Editorial/Editorial.xaml");
		foreach (var rule in editorial.Descendants().Where(e => Key(e) == "EditorialSunsetRuleBrush" && e.Name.LocalName == "LinearGradientBrush"))
		{
			Assert.DoesNotContain(rule.Descendants(), s => ((string?)s.Attribute("Color"))?.EndsWith("F96985", StringComparison.OrdinalIgnoreCase) == true);
		}

		// the rule is the bar's ornament part, which TopBarChrome hides while the player is open
		Assert.Contains(editorial.Descendants(), e => (string?)e.Attribute(X + "Name") == FluentFin.Controls.TopBarChrome.OrnamentPart);
		Assert.True(FluentFin.Controls.TopBarChrome.ShowsOrnament(overPlayer: false));
		Assert.False(FluentFin.Controls.TopBarChrome.ShowsOrnament(overPlayer: true));
	}

	/// <summary>X13 in Editorial: icon buttons are the 44 circles of every theme; chips are 32; a switch is not a picked chip.</summary>
	[Fact]
	public void Editorials_icons_are_circles_and_its_chips_are_32()
	{
		var editorial = Ui("Themes/Editorial/Editorial.xaml");
		foreach (var key in new[] { "EditorialIconButtonStyle", "EditorialIconToggleButtonStyle" })
		{
			Assert.DoesNotContain(Keyed(editorial, key).Elements(), e => (string?)e.Attribute("Property") == "CornerRadius");
		}

		var chip = Keyed(editorial, "EditorialChipStyle");
		Assert.Equal("{StaticResource NixieCompactButtonHeight}", (string?)chip.Elements().Single(e => (string?)e.Attribute("Property") == "Height").Attribute("Value"));
		var picked = chip.Descendants().Single(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == "Selected").ToString();
		Assert.DoesNotContain("Accent", picked);
		Assert.Contains("NixieChipSelectedFillBrush", picked);
		Assert.Equal("{StaticResource NixieChipToggleButtonStyle}", (string?)Keyed(editorial, "EditorialToggleChipStyle").Attribute("BasedOn"));

		// pressed rows are never the selected wash (E-p4)
		foreach (var file in new[] { "Themes/Editorial/Views/EditorialHomePage.xaml", "Themes/Editorial/Controls/EditorialSeasonsPanel.xaml" })
		{
			foreach (var pressed in Ui(file).Descendants().Where(e => e.Name.LocalName == "VisualState" && (string?)e.Attribute(X + "Name") == "Pressed"))
			{
				Assert.DoesNotContain("Selected", pressed.ToString());
			}
		}
	}

	/// <summary>Drive-in's season tubes are the Tab role (DI-p5), the update notice's buttons the Primary Compact and the Link.</summary>
	[Fact]
	public void Season_tubes_are_tabs_and_the_update_notice_is_primary_and_link()
	{
		Assert.Equal("{StaticResource NixieTabButtonStyle}", (string?)Keyed(Ui("Themes/DriveIn/DriveIn.xaml"), "DriveInSeasonTubeButtonStyle").Attribute("BasedOn"));
		foreach (var page in new[] { "Themes/DriveIn/Views/DriveInSeriesPage.xaml", "Themes/DriveIn/Views/DriveInSeasonPage.xaml" })
		{
			var tube = Ui(page).Descendants().Single(e => e.Name.LocalName == "SeasonTube");
			Assert.Equal("{StaticResource DriveInSeasonTubeButtonStyle}", (string?)tube.Parent!.Attribute("Style"));
		}

		var shell = Ui("Views/ShellPage.xaml");
		Assert.Equal("{StaticResource NixiePrimaryCompactButtonStyle}", (string?)Named(shell, "UpdateInstallButton").Attribute("Style"));
		var notes = shell.Descendants().Single(e => e.Name.LocalName == "HyperlinkButton" && (string?)e.Attribute("Content") == "Release notes");
		Assert.Equal("{StaticResource NixieLinkButtonStyle}", (string?)notes.Attribute("Style"));
		Assert.DoesNotContain(Named(shell, "UpdateBar").Descendants(), e => ((string?)e.Attribute("Foreground"))?.Contains("Accent") == true);
	}

	/// <summary>The player's transport is on the Nixie templates; the pills on the Secondary template; Play/Pause the 60 px hero.</summary>
	[Fact]
	public void The_players_transport_styles_are_based_on_the_Nixie_keys()
	{
		var player = Ui("Controls/MpvPlayerControls.xaml");
		Assert.Equal("{StaticResource NixiePlayerButtonStyle}", (string?)Keyed(player, "PlayerButton").Attribute("BasedOn"));
		Assert.Equal("{StaticResource NixiePlayerPlayButtonStyle}", (string?)Keyed(player, "PlayerPlayButton").Attribute("BasedOn"));
		// the prompts (the player stream, 1.4.0): the player roles, Secondary and Primary 44 (PlayerPill went with them)
		Assert.Equal("{StaticResource NixiePlayerPromptButtonStyle}", (string?)Keyed(player, "PlayerPromptStyle").Attribute("BasedOn"));
		Assert.Equal("{StaticResource NixiePlayerPromptPrimaryButtonStyle}", (string?)Keyed(player, "PlayerPromptPrimaryStyle").Attribute("BasedOn"));
		Assert.Null(player.Descendants().SingleOrDefault(e => (string?)e.Attribute(X + "Key") == "PlayerPill"));
		Assert.DoesNotContain(Keyed(player, "PlayerButton").Elements(), e => (string?)e.Attribute("Property") is "CornerRadius" or "Width" or "Height");
	}

	/// <summary>The hidden bold copy that keeps a label's width prints text only: bound to an element it printed the type name.</summary>
	[Fact]
	public void The_hidden_bold_label_takes_text_only()
	{
		Assert.Equal("Drama", FluentFin.Converters.TextOnlyConverter.TextOf("Drama"));
		Assert.Equal("", FluentFin.Converters.TextOnlyConverter.TextOf(new object()));
		Assert.Equal("", FluentFin.Converters.TextOnlyConverter.TextOf(null));
		foreach (var copy in Glass().Descendants().Where(e => (string?)e.Attribute(X + "Name") == "SelectedWidth"))
		{
			Assert.Contains("Converter={StaticResource NixieTextOnlyConverter}", (string?)copy.Attribute("Text"));
		}
	}
}
