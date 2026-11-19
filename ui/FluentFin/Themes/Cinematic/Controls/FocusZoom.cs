using System.Numerics;
using FluentFin.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>
/// Cinematic's focus: a zoom of the ART about its centre on a spring, with the neon rim and the sheen the element draws itself.
/// Nixie 1.4.0 (the library UX review, C-m1): the whole tile grew 1.1x about a point 40 % down, so its title dropped 18 px below
/// its row, its gaps shrank from 24 to 10 px and the last column ran past the A to Z. Now only the art (and its rings) grows,
/// by at most <see cref="MaxGrowthPerSide"/> px a side, which the tile's margins reserve. Only while motion is allowed (GlassMode
/// Full with animations on): Reduced and Solid keep the size and show the rim alone, and nothing moves.
/// </summary>
public static class FocusZoom
{
	/// <summary>The most the zoom may scale anything (a small still reaches the cap before the 8 px).</summary>
	public const float Scale = 1.1f;

	/// <summary>The most the art grows on each side, in px: the room the still's margins keep (16 and 12 px).</summary>
	public const double MaxGrowthPerSide = 8;

	/// <summary>Where the zoom grows from, as a share of the element's size: its centre (the words below are not in it).</summary>
	public static readonly Vector2 Origin = new(0.5f, 0.5f);

	/// <summary>The size an element <paramref name="width"/> wide is drawn at: grown by at most 8 px a side while zoomed and motion is allowed, else 1.</summary>
	public static float ScaleFor(bool zoomed, bool motionAllowed, double width) =>
		zoomed && motionAllowed && width > 0 ? (float)Math.Min(Scale, 1 + 2 * MaxGrowthPerSide / width) : 1f;

	public static void Apply(UIElement element, bool zoomed)
	{
		var visual = ElementCompositionPreview.GetElementVisual(element);
		var width = 0.0;
		if (element is FrameworkElement fe)
		{
			width = fe.ActualWidth;
			visual.CenterPoint = new Vector3((float)fe.ActualWidth * Origin.X, (float)fe.ActualHeight * Origin.Y, 0);
		}

		var scale = ScaleFor(zoomed, SafeMotion.Allowed, width);
		if (SafeMotion.Allowed)
		{
			var spring = visual.Compositor.CreateSpringVector3Animation();
			spring.DampingRatio = 0.72f;
			spring.Period = TimeSpan.FromMilliseconds(55);
			spring.FinalValue = new Vector3(scale, scale, 1);
			visual.StartAnimation("Scale", spring);
		}
		else
		{
			visual.StopAnimation("Scale");
			visual.Scale = new Vector3(scale, scale, 1);
		}
	}

	/// <summary>Fades <paramref name="element"/> to <paramref name="opacity"/> in 180 ms (at once without motion).</summary>
	public static void Fade(UIElement element, float opacity)
	{
		var visual = ElementCompositionPreview.GetElementVisual(element);
		if (!SafeMotion.Allowed)
		{
			visual.StopAnimation("Opacity");
			visual.Opacity = opacity;
			return;
		}

		var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
		fade.InsertKeyFrame(1f, opacity);
		fade.Duration = TimeSpan.FromMilliseconds(180);
		visual.StartAnimation("Opacity", fade);
	}

	/// <summary>
	/// The sheen's one pass: a soft diagonal light slides in from the left edge as the focus arrives (Full only), then
	/// rests at the top-left corner.
	/// </summary>
	public static void Sweep(UIElement sheen, double width)
	{
		ElementCompositionPreview.SetIsTranslationEnabled(sheen, true);
		var visual = ElementCompositionPreview.GetElementVisual(sheen);
		if (!SafeMotion.Allowed)
		{
			visual.StopAnimation("Translation.X");
			visual.Properties.InsertVector3("Translation", Vector3.Zero);
			return;
		}

		var slide = visual.Compositor.CreateScalarKeyFrameAnimation();
		slide.InsertKeyFrame(0f, (float)(-width * 0.45));
		slide.InsertKeyFrame(1f, 0f, visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.9f), new Vector2(0.1f, 1f)));
		slide.Duration = TimeSpan.FromMilliseconds(520);
		visual.StartAnimation("Translation.X", slide);
	}
}
