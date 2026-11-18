using System.Text.RegularExpressions;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>
/// Nixie 1.3.0: Solid changes the glass brushes in place. Writing the opaque stand-in under a theme dictionary's key made
/// every RequestedTheme="Dark" part of a light window (Classic's hero, Drive-in's screen and sign) look its brushes up in
/// the Light dictionary: in Light + Solid their buttons were dark labels on dark (seen off-screen, "More info" on Home).
/// </summary>
public sealed class GlassSolidInPlaceTests
{
	[Fact]
	public void GlassTheme_never_writes_an_entry_of_a_dictionary()
	{
		var source = File.ReadAllText(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Services", "GlassTheme.cs")));
		var code = string.Join('\n', source.Split('\n').Select(l => Regex.Replace(l, @"//.*$", "")));
		// an indexer assignment (dictionary[key] = brush), never a comparison
		Assert.DoesNotMatch(new Regex(@"\w\s*\[[^\]\n]+\]\s*=(?!=)"), code);
	}

	[Fact]
	public void An_acrylic_panel_falls_back_to_an_opaque_colour_in_Solid()
	{
		var tint = Windows.UI.Color.FromArgb(255, 0x2A, 0x12, 0x17);
		Assert.Equal(Windows.UI.Color.FromArgb(255, 0x2A, 0x12, 0x17), NixieTheme.FallbackFor(tint, 0xF0, solid: true));
		Assert.Equal(Windows.UI.Color.FromArgb(0xF0, 0x2A, 0x12, 0x17), NixieTheme.FallbackFor(tint, 0xF0, solid: false));
	}
}
