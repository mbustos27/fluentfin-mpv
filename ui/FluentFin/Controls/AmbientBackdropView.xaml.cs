using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
	}

	private void Show(Uri? uri, bool animate)
	{
		_fade?.Stop();
		_fade = null;
		_shown = uri;

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
