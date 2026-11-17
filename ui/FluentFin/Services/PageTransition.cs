using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace FluentFin.Services;

/// <summary>
/// Seaglass page change: the new page comes in as glass settling into place, through the one curve the theme uses (0.05, 0.9, 0.1,
/// 1.0): it fades in, rises 14 px and grows from 98 % over 350 ms (Full mode). Reduced mode: a 150 ms fade. Solid and animations
/// off: nothing. Composition only, on the page's visual; the player page is never animated (it carries the video).
/// </summary>
public static class PageTransition
{
	public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(350);
	public static readonly TimeSpan FadeOnly = TimeSpan.FromMilliseconds(150);
	public const float Rise = 14f;
	public const float StartScale = 0.98f;

	/// <summary>What a mode does on a page change.</summary>
	public static PageTransitionKind KindFor(GlassModeKind mode, bool animationsEnabled, bool isPlayer) =>
		isPlayer || !animationsEnabled ? PageTransitionKind.None
		: mode switch
		{
			GlassModeKind.Full => PageTransitionKind.Settle,
			GlassModeKind.Reduced => PageTransitionKind.Fade,
			_ => PageTransitionKind.None,
		};

	public static void Play(object? content)
	{
		if (content is not UIElement page)
		{
			return;
		}

		var kind = KindFor(GlassMode.Current, GlassMode.AnimationsOn, page.GetType().Name.Contains("VideoPlayer", StringComparison.Ordinal));
		if (kind == PageTransitionKind.None)
		{
			return;
		}

		try
		{
			ElementCompositionPreview.SetIsTranslationEnabled(page, true);
			var visual = ElementCompositionPreview.GetElementVisual(page);
			var compositor = visual.Compositor;
			var curve = compositor.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.9f), new Vector2(0.1f, 1f));

			var fade = compositor.CreateScalarKeyFrameAnimation();
			fade.InsertKeyFrame(0, 0);
			fade.InsertKeyFrame(1, 1, curve);
			fade.Duration = kind == PageTransitionKind.Settle ? Duration : FadeOnly;
			visual.StartAnimation("Opacity", fade);

			if (kind == PageTransitionKind.Settle)
			{
				if (page is FrameworkElement element)
				{
					visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
				}

				var rise = compositor.CreateScalarKeyFrameAnimation();
				rise.InsertKeyFrame(0, Rise);
				rise.InsertKeyFrame(1, 0, curve);
				rise.Duration = Duration;
				visual.StartAnimation("Translation.Y", rise);

				var grow = compositor.CreateVector3KeyFrameAnimation();
				grow.InsertKeyFrame(0, new Vector3(StartScale, StartScale, 1));
				grow.InsertKeyFrame(1, Vector3.One, curve);
				grow.Duration = Duration;
				visual.StartAnimation("Scale", grow);
			}
		}
		catch (Exception)
		{
			// a transition is decoration: a page that cannot be animated is simply shown
		}
	}
}

public enum PageTransitionKind
{
	None,
	Fade,
	Settle,
}
