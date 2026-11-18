using FluentFin.Themes;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0: ThemeStyle layers a theme's style (PlayerBottomBarStyle) over an element's own. The theme's style is the base,
/// never copied: a copied setter lost its {ThemeResource} value (Drive-in's console had no fill or rim), and Light, Dark and
/// high contrast need those values. Only the element's own setters for properties the theme leaves out go on top.
/// </summary>
public sealed class ThemeStyleLayerTests
{
	[Fact]
	public void The_theme_style_is_the_base_and_its_setters_are_never_copied()
	{
		var source = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Themes", "ThemeStyle.cs")));
		Assert.Contains("BasedOn = theme", source, StringComparison.Ordinal);
		Assert.DoesNotContain("BasedOn = own", source, StringComparison.Ordinal);
	}

	[Fact]
	public void Own_setters_stay_only_where_the_theme_sets_nothing()
	{
		// the bottom bar's own style: Padding, VerticalAlignment and its scrim; the theme sets Padding, Background and a rim
		IEnumerable<(string, object?)>[] own = [[("Padding", "32,64,32,20"), ("VerticalAlignment", "Bottom"), ("Background", "scrim")]];
		var kept = ThemeStyle.Kept(own, new HashSet<string> { "Padding", "Background", "BorderBrush" });
		Assert.Equal([("VerticalAlignment", (object?)"Bottom")], kept);
	}

	[Fact]
	public void The_most_derived_own_setter_wins_in_first_set_order()
	{
		IEnumerable<(string, object?)>[] own = [[("A", 1), ("B", 1)], [("C", 2), ("A", 2)]];
		var kept = ThemeStyle.Kept(own, new HashSet<string>());
		Assert.Equal([("A", (object?)2), ("B", 1), ("C", 2)], kept);
	}
}
