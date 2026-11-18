using System.Numerics;
using FluentFin.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>
/// Cinematic's focus: a 1.1x zoom about a point 40 % down the element (the art grows, its text below moves with it) on a
/// spring, with the neon rim and the sheen the element draws itself. Only while motion is allowed (GlassMode Full with
/// animations on): Reduced and Solid keep the size and show the rim alone, and nothing moves.
/// </summary>
public static class FocusZoom
{
	public const float Scale = 1.1f;

	/// <summary>Where the zoom grows from, as a share of the element's size (the mockups' transform-origin 50 % 40 %).</summary>
	public static readonly Vector2 Origin = new(0.5f, 0.4f);

	/// <summary>The size an element is drawn at: <see cref="Scale"/> while zoomed and motion is allowed, else 1.</summary>
	public static float ScaleFor(bool zoomed, bool motionAllowed) => zoomed && motionAllowed ? Scale : 1f;

	public static void Apply(UIElement element, bool zoomed)
	{
		var visual = ElementCompositionPreview.GetElementVisual(element);
		if (element is FrameworkElement fe)
		{
			visual.CenterPoint = new Vector3((float)fe.ActualWidth * Origin.X, (float)fe.ActualHeight * Origin.Y, 0);
		}

		var scale = ScaleFor(zoomed, SafeMotion.Allowed);
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
