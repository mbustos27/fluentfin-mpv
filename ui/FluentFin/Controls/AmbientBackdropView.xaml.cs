using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass: draws <see cref="AmbientBackdrop.Current"/> under the pages and fades (600 ms) when it changes. With no
/// artwork, and in Solid mode, only the deep colour shows.
/// </summary>
public sealed partial class AmbientBackdropView : UserControl
{
	/// <summary>The width the artwork is decoded at: small, and scaled up, so it is soft and cheap.</summary>
	public const int DecodeWidth = 64;

	public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(600);

	/// <summary>The drift: the artwork zooms between these scales, slowly, in Full mode only.</summary>
	public const float DriftFrom = 1.06f;
	public const float DriftTo = 1.16f;
	public static readonly TimeSpan DriftDuration = TimeSpan.FromSeconds(48);

	/// <summary>Whether the window's own backdrop should be desktop acrylic: glass modes with no artwork (Solid keeps Mica).</summary>
	public static bool WantsDesktopAcrylic(Uri? artwork, GlassModeKind mode) => artwork is null && mode != GlassModeKind.Solid;

	/// <summary>How much of the deep colour covers the desktop acrylic when there is no artwork.</summary>
	public const double TranslucentBase = 0.55;

	private Storyboard? _fade;
	private Uri? _shown;

	public AmbientBackdropView()
	{
		InitializeComponent();
		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
	}

	/// <summary>The decoded-small source for an artwork address.</summary>
	public static BitmapImage SourceFor(Uri uri) => new() { DecodePixelWidth = DecodeWidth, UriSource = uri };

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		AmbientBackdrop.Changed += OnBackdropChanged;
		GlassMode.Changed += OnModeChanged;
		UpdateMode();
		Show(AmbientBackdrop.Current, animate: false);
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		AmbientBackdrop.Changed -= OnBackdropChanged;
		GlassMode.Changed -= OnModeChanged;
		_fade?.Stop();
		var visual = ElementCompositionPreview.GetElementVisual(Drift);
		visual.StopAnimation("Scale");
		visual.StopAnimation("Translation");
	}

	private void OnBackdropChanged(object? sender, Uri? uri) => Show(uri, animate: true);

	// GlassMode.Changed may come from another thread
	private void OnModeChanged(object? sender, EventArgs e)
	{
		if (DispatcherQueue.HasThreadAccess)
		{
			UpdateMode();
		}
		else
		{
			DispatcherQueue.TryEnqueue(UpdateMode);
		}
	}

	private void UpdateMode()
	{
		// Solid: the deep colour alone (no artwork, so nothing to dim)
		var solid = GlassMode.Current == GlassModeKind.Solid;
		var visibility = solid ? Visibility.Collapsed : Visibility.Visible;
		BackImage.Visibility = visibility;
		FrontImage.Visibility = visibility;
		Dim.Visibility = visibility;
		UpdateBase();
		UpdateDrift();
	}

	/// <summary>
	/// With no artwork (and not Solid) the deep colour lets the window's desktop acrylic show through; with artwork, and in
	/// Solid, it is opaque.
	/// </summary>
	private void UpdateBase()
	{
		var translucent = WantsDesktopAcrylic(_shown, GlassMode.Current);
		BaseFill.Opacity = translucent ? TranslucentBase : 1;
	}

	private void OnDriftSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDrift();

	/// <summary>The slow zoom and sway, in Full mode with artwork shown; nothing in Reduced or Solid.</summary>
	private void UpdateDrift()
	{
		var visual = ElementCompositionPreview.GetElementVisual(Drift);
		visual.StopAnimation("Scale");
		visual.StopAnimation("Translation");
		if (!(SafeMotion.Allowed && GlassMode.Current == GlassModeKind.Full) || Drift.ActualWidth < 1)
		{
			visual.Scale = Vector3.One;
			return;
		}

		ElementCompositionPreview.SetIsTranslationEnabled(Drift, true);
		visual.CenterPoint = new Vector3((float)Drift.ActualWidth / 2, (float)Drift.ActualHeight / 2, 0);
		var compositor = visual.Compositor;
		var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));

		var zoom = compositor.CreateVector3KeyFrameAnimation();
		zoom.InsertKeyFrame(0, new Vector3(DriftFrom, DriftFrom, 1), ease);
		zoom.InsertKeyFrame(1, new Vector3(DriftTo, DriftTo, 1), ease);
		zoom.Duration = DriftDuration;
		zoom.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
		zoom.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
		visual.StartAnimation("Scale", zoom);

		// a sway of up to 2% of the width, kept inside what the zoom hides (the zoom adds 3% on each side at least)
		var sway = (float)Drift.ActualWidth * 0.02f;
		var move = compositor.CreateVector3KeyFrameAnimation();
		move.InsertKeyFrame(0, new Vector3(-sway, 0, 0), ease);
		move.InsertKeyFrame(1, new Vector3(sway, 0, 0), ease);
		move.Duration = TimeSpan.FromSeconds(DriftDuration.TotalSeconds * 1.3);
		move.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
		move.Direction = Microsoft.UI.Composition.AnimationDirection.Alternate;
		visual.StartAnimation("Translation", move);
	}

	private void Show(Uri? uri, bool animate)
	{
		_fade?.Stop();
		_fade = null;
		_shown = uri;
		UpdateBase();

		// a fade cut short: what was coming in is what stays
		if (FrontImage.Opacity > 0.5)
		{
			BackImage.Source = FrontImage.Source;
			BackImage.Opacity = 1;
		}

		FrontImage.Opacity = 0;

		if (uri is null)
		{
			FadeOut(animate);
			return;
		}

		if (!animate)
		{
			BackImage.Source = SourceFor(uri);
			BackImage.Opacity = 1;
			FrontImage.Source = null;
			return;
		}

		// shown once decoded (OnFrontImageOpened), so a slow image never leaves a hole
		FrontImage.Source = SourceFor(uri);
	}

	private void OnFrontImageOpened(object sender, RoutedEventArgs e)
	{
		if (_shown is null || FrontImage.Source is not BitmapImage { UriSource: { } source } || source != _shown)
		{
			return;
		}

		var fade = new Storyboard();
		var animation = new DoubleAnimation { From = 0, To = 1, Duration = FadeDuration, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
		Storyboard.SetTarget(animation, FrontImage);
		Storyboard.SetTargetProperty(animation, "Opacity");
		fade.Children.Add(animation);
		fade.Completed += (_, _) =>
		{
			if (_fade != fade)
			{
				return;
			}

			// the back layer takes over, ready for the next fade
			BackImage.Source = FrontImage.Source;
			BackImage.Opacity = 1;
			FrontImage.Opacity = 0;
			_fade = null;
		};
		_fade = fade;
		fade.Begin();
	}

	private void FadeOut(bool animate)
	{
		if (!animate || BackImage.Opacity == 0)
		{
			BackImage.Opacity = 0;
			BackImage.Source = null;
			FrontImage.Source = null;
			return;
		}

		var fade = new Storyboard();
		var animation = new DoubleAnimation { To = 0, Duration = FadeDuration };
		Storyboard.SetTarget(animation, BackImage);
		Storyboard.SetTargetProperty(animation, "Opacity");
		fade.Children.Add(animation);
		fade.Completed += (_, _) =>
		{
			if (_fade != fade)
			{
				return;
			}

			BackImage.Opacity = 0;
			BackImage.Source = null;
			FrontImage.Source = null;
			_fade = null;
		};
		_fade = fade;
		fade.Begin();
	}
}
