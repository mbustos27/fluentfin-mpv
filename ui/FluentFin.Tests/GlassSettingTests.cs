using System.Xml.Linq;
using FluentFin.Core.Settings;
using FluentFin.Services;

namespace FluentFin.Tests;

/// <summary>Seaglass: the Glass choice reaches GlassMode, and Glass.xaml keeps the tokens the Solid swap and the buttons need.</summary>
public sealed class GlassSettingTests
{
	[Theory]
	[InlineData(GlassChoice.Automatic, null)]
	[InlineData(GlassChoice.Full, GlassModeKind.Full)]
	[InlineData(GlassChoice.Reduced, GlassModeKind.Reduced)]
	[InlineData(GlassChoice.Solid, GlassModeKind.Solid)]
	public void EachChoice_MapsToItsMode(GlassChoice choice, GlassModeKind? expected) =>
		Assert.Equal(expected, GlassSetting.ToOverride(choice));

	[Fact]
	public void Apply_SetsTheOverride_AndAutomaticClearsIt()
	{
		try
		{
			GlassSetting.Apply(GlassChoice.Solid);
			Assert.Equal(GlassModeKind.Solid, GlassMode.Override);
			Assert.Equal(GlassModeKind.Solid, GlassMode.Current);

			GlassSetting.Apply(GlassChoice.Automatic);
			Assert.Null(GlassMode.Override);
		}
		finally
		{
			GlassMode.Override = null;
		}
	}

	[Fact]
	public void Apply_RaisesChanged_OnlyWhenTheOverrideChanges()
	{
		var raised = 0;
		void Count(object? s, EventArgs e) => raised++;
		GlassMode.Changed += Count;
		try
		{
			GlassSetting.Apply(GlassChoice.Reduced);
			GlassSetting.Apply(GlassChoice.Reduced);
			Assert.Equal(1, raised);
		}
		finally
		{
			GlassMode.Changed -= Count;
			GlassMode.Override = null;
		}
	}

	private static XElement GlassXaml() => XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Styles", "Glass.xaml"))).Root!;

	private static string? Key(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key")?.Value;

	[Theory]
	[InlineData("Dark")]
	[InlineData("Light")]
	[InlineData("HighContrast")]
	public void EveryTheme_HasTheSolidBrushesTheSwapUses(string theme)
	{
		var dictionary = GlassXaml().Descendants().Single(e => e.Name.LocalName == "ResourceDictionary" && Key(e) == theme);
		var keys = dictionary.Elements().Select(Key).ToHashSet();

		Assert.Contains("GlassPanelBrush", keys);
		Assert.Contains("GlassPanelStrongBrush", keys);
		Assert.Contains("GlassPanelSolidBrush", keys);
		Assert.Contains("GlassPanelStrongSolidBrush", keys);
		Assert.Contains("GlassSelectedOverlayBrush", keys);
	}

	[Theory]
	[InlineData("GlassPillButtonStyle")]
	[InlineData("GlassAccentPillButtonStyle")]
	[InlineData("GlassIconButtonStyle")]
	public void TheGlassButtons_KeepTheirKeys_AndDrawPointerOverAndPressedAsGlass(string key)
	{
		var xaml = GlassXaml();
		Assert.Contains(xaml.Elements(), e => Key(e) == key);

		// 1.2.0: the 1.1.x keys are aliases of the button system's; its glass buttons draw hover and pressed as glass overlays
		foreach (var name in new[] { "NixieButtonTemplate", "NixieIconButtonTemplate", "NixieSubtleButtonTemplate" })
		{
			var template = xaml.Elements().Single(e => Key(e) == name).ToString();
			Assert.Contains("NixieHoverOverlayBrush", template);
			Assert.Contains("NixiePressedOverlayBrush", template);
		}
	}
}
