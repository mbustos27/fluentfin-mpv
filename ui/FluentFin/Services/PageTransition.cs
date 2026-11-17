using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Services;

/// <summary>
/// Seaglass page change, through the one curve the theme uses (0.05, 0.9, 0.1, 1.0). Seaglass 1.1.4 (interaction audit, item
/// 16): the motion says which way you went, as Windows' page transitions do. A rail destination (Home, Libraries, Settings, a
/// plugin's) comes in with the page refresh: it fades in and slides up 28 px. Opening something (an item, a library, the search
/// results; Forward) drills in: it fades in and grows from 94 %. Back drills out: the page you return to fades in and settles
/// from 104 %. 300 ms in Full mode. Reduced mode: a 150 ms fade whichever way. Solid and animations off: nothing.
/// Composition only, on the page's visual; the player page is never animated (it carries the video).
/// </summary>
public static class PageTransition
{
	public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);
	public static readonly TimeSpan FadeOnly = TimeSpan.FromMilliseconds(150);

	/// <summary>
	/// Whether a page type is a rail destination (the shell sets it from its rail: ShellPage). A navigation to one is a
	/// switch of section (the refresh), not a drill in.
	/// </summary>
	public static Func<Type, bool> IsTopLevel { get; set; } = _ => false;

	/// <summary>Which way a navigation went: a section switch, in, or out.</summary>
	public static PageMotion MotionFor(NavigationMode mode, bool topLevel) => mode switch
	{
		NavigationMode.Back => PageMotion.DrillOut,
		NavigationMode.New or NavigationMode.Refresh when topLevel => PageMotion.Refresh,
		_ => PageMotion.DrillIn,
	};

	/// <summary>Where the page starts (scale, offset down in DIPs) for a motion; it ends at 1 and 0.</summary>
	public static (float Scale, float OffsetY) StartOf(PageMotion motion) => motion switch
	{
		PageMotion.Refresh => (1f, 28f),
		PageMotion.DrillIn => (0.94f, 0f),
		_ => (1.04f, 0f),
	};

	/// <summary>What a mode does on a page change.</summary>
	public static PageTransitionKind KindFor(GlassModeKind mode, bool animationsEnabled, bool isPlayer) =>
		isPlayer || !animationsEnabled ? PageTransitionKind.None
		: mode switch
		{
			GlassModeKind.Full => PageTransitionKind.Settle,
			GlassModeKind.Reduced => PageTransitionKind.Fade,
			_ => PageTransitionKind.None,
		};

	/// <summary>Plays the transition for a navigation of the frame that now shows <paramref name="content"/>.</summary>
	public static void Play(object? content, NavigationMode mode) =>
		Play(content, MotionFor(mode, content is not null && IsTopLevel(content.GetType())));

	public static void Play(object? content, PageMotion motion)
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
				var (scale, offsetY) = StartOf(motion);
				if (page is FrameworkElement element)
				{
					visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
				}

				if (offsetY != 0)
				{
					var rise = compositor.CreateScalarKeyFrameAnimation();
					rise.InsertKeyFrame(0, offsetY);
					rise.InsertKeyFrame(1, 0, curve);
					rise.Duration = Duration;
					visual.StartAnimation("Translation.Y", rise);
				}

				if (scale != 1f)
				{
					var grow = compositor.CreateVector3KeyFrameAnimation();
					grow.InsertKeyFrame(0, new Vector3(scale, scale, 1));
					grow.InsertKeyFrame(1, Vector3.One, curve);
					grow.Duration = Duration;
					visual.StartAnimation("Scale", grow);
				}
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

/// <summary>Seaglass 1.1.4: which way a page change went (PageTransition.MotionFor).</summary>
public enum PageMotion
{
	/// <summary>A rail destination: fade in, slide up.</summary>
	Refresh,

	/// <summary>Opening something: fade in, grow from 94 %.</summary>
	DrillIn,

	/// <summary>Back: fade in, settle from 104 %.</summary>
	DrillOut,
}
