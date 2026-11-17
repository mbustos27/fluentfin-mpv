using System.Xml.Linq;
using FluentFin.Controls;

namespace FluentFin.Tests;

/// <summary>
/// Seaglass 1.1.3: how the player's volume indicator moves (<see cref="VolumeIndicatorMotion"/>). The fill's glide itself
/// (continuous from where it is drawn, never back to an empty bar) is measured off-screen by
/// tools\ui-harness\offscreen\then-volume.ps1, which reads the drawn fill in every grab.
/// </summary>
public sealed class VolumeIndicatorMotionTests
{
	[Theory]
	[InlineData(true, true, true)]
	[InlineData(false, true, false)]   // Reduced, Solid, animations off: at once
	[InlineData(true, false, false)]   // nothing shows: at once, so the next show starts at the volume
	[InlineData(false, false, false)]
	public void The_fill_glides_only_with_motion_while_shown(bool motion, bool shown, bool glides) =>
		Assert.Equal(glides, VolumeIndicatorMotion.FillGlides(motion, shown));

	[Theory]
	[InlineData(0, 0)]
	[InlineData(0.45, 108)]
	[InlineData(1, 240)]
	[InlineData(1.3, 240)]   // an mpv.conf's volume above 100: a full bar
	[InlineData(-0.2, 0)]
	[InlineData(double.NaN, 0)]
	public void The_fill_is_the_volume_of_the_bar(double fraction, double width) =>
		Assert.Equal(width, VolumeIndicatorMotion.FillTo(fraction), 3);

	[Fact]
	public void The_fill_and_its_shape_match_the_bar_in_the_xaml()
	{
		var xaml = XDocument.Load(AppVersionTests.FindUp(Path.Combine("ui", "FluentFin", "Controls", "MpvPlayerControls.xaml")));
		var fill = xaml.Descendants().Single(e => (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name") == "VolumeIndicatorFill");
		var bar = fill.Parent!;
		Assert.Equal(VolumeIndicatorMotion.FillWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)bar.Attribute("Width"));
		Assert.Equal(VolumeIndicatorMotion.FillHeight.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)bar.Attribute("Height"));
		// the fill is always the full bar: the code clips it; a width of its own would cut the glide short
		Assert.Equal(VolumeIndicatorMotion.FillWidth.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)fill.Attribute("Width"));
	}

	[Fact]
	public void The_indicator_grows_in_and_fades_quicker_without_motion()
	{
		Assert.Equal(0.94f, VolumeIndicatorMotion.HiddenScale.X);
		Assert.Equal(VolumeIndicatorMotion.HiddenScale.X, VolumeIndicatorMotion.HiddenScale.Y);
		Assert.True(VolumeIndicatorMotion.FadeIn(false) < VolumeIndicatorMotion.FadeIn(true));
		Assert.True(VolumeIndicatorMotion.FadeOut(false) < VolumeIndicatorMotion.FadeOut(true));
		Assert.Equal(TimeSpan.FromMilliseconds(160), VolumeIndicatorMotion.FillTime);
	}
}
