using System.Numerics;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass: how the player's volume indicator (<see cref="MpvPlayerControls"/>) moves, apart from WinUI so it can be tested.
/// The fill glides 160 ms to each new level, always from where it is drawn now; with motion not allowed (Reduced, Solid,
/// animations off) or the indicator not shown it moves at once. The indicator fades and grows in from 94 % and fades and
/// shrinks out, each from where it is drawn now (a notch while it fades out brings it back from there).
/// </summary>
public static class VolumeIndicatorMotion
{
	/// <summary>The fill's width when full (DIPs): the bar in MpvPlayerControls.xaml is this wide.</summary>
	public const double FillWidth = 240;

	/// <summary>The bar's height (DIPs); its ends are round (a radius of half of it).</summary>
	public const float FillHeight = 10;

	/// <summary>How long the fill takes to glide to a new level.</summary>
	public static readonly TimeSpan FillTime = TimeSpan.FromMilliseconds(160);

	/// <summary>How long the grow from <see cref="HiddenScale"/> takes (motion only).</summary>
	public static readonly TimeSpan Grow = TimeSpan.FromMilliseconds(260);

	/// <summary>The size the indicator grows from and shrinks back to (motion only).</summary>
	public static readonly Vector3 HiddenScale = new(0.94f, 0.94f, 1f);

	public static TimeSpan FadeIn(bool motion) => TimeSpan.FromMilliseconds(motion ? 200 : 120);

	public static TimeSpan FadeOut(bool motion) => TimeSpan.FromMilliseconds(motion ? 400 : 150);

	/// <summary>The fill glides only with motion allowed and the indicator shown; otherwise it is set at once.</summary>
	public static bool FillGlides(bool motionAllowed, bool shown) => motionAllowed && shown;

	/// <summary>The fill's width for a volume fraction (0 to 1; outside is held to it).</summary>
	public static float FillTo(double fraction) => (float)(Math.Clamp(double.IsNaN(fraction) ? 0 : fraction, 0, 1) * FillWidth);
}
