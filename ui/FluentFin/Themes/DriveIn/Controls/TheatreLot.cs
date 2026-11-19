using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Nixie 1.3.1, Drive-in: the theatre window (the trailer's frame, DriveIn.xaml's TrailerFrameTemplate) as a little drive-in
/// at dusk. The lot is this grid: as wide as the video and some sky either side, its corners rounded; its two
/// <see cref="DriveInScene"/>s (the sky, sun, hills, ground and the screen's two posts behind; two cars and their speaker posts
/// in front) are laid out around the element named <c>Screen</c> wherever the template puts it
/// (<see cref="DriveInLayout.Theatre"/>), and the marquee's bulbs chase once when it opens (motion allowed only).
/// </summary>
public sealed partial class TheatreLot : Grid
{
	public static readonly DependencyProperty VideoWidthProperty =
		DependencyProperty.Register(nameof(VideoWidth), typeof(double), typeof(TheatreLot), new PropertyMetadata(640d, (d, _) => ((TheatreLot)d).ApplyWidth()));

	public double VideoWidth { get => (double)GetValue(VideoWidthProperty); set => SetValue(VideoWidthProperty, value); }

	/// <summary>The sky either side of the screen: room for the sun and the cars' corners.</summary>
	public static double SideRoom(double videoWidth) => Math.Max(76, Math.Round(videoWidth * 0.09));

	/// <summary>The screen's frame around the video (7 DIP of painted steel and its 1 DIP edge, each side).</summary>
	public const double ScreenFrame = 8;

	public TheatreLot()
	{
		SizeChanged += (_, _) => Relayout();
		Loaded += (_, _) =>
		{
			ApplyWidth();
			if (Find<FrameworkElement>(this, e => e.Name == "Screen") is { } screen)
			{
				screen.SizeChanged += (_, _) => Relayout();
			}

			Relayout();
			foreach (var bulbs in All<BulbFrame>(this))
			{
				bulbs.Chase();
			}
		};
	}

	private (double, double, double, double, double, double) _place;

	private void ApplyWidth() => Width = VideoWidth + 2 * ScreenFrame + 2 * SideRoom(VideoWidth);

	private void Relayout()
	{
		if (ActualWidth <= 0 || ActualHeight <= 0 || Find<FrameworkElement>(this, e => e.Name == "Screen") is not { ActualWidth: > 0 } screen)
		{
			return;
		}

		// the scenes fill the lot inside its border: sized to the inside, or each pass would grow the lot by the border (a
		// layout cycle), and placed from the scenes' own corner
		var border = BorderThickness;
		var width = Math.Floor(ActualWidth - border.Left - border.Right);
		var height = Math.Floor(ActualHeight - border.Top - border.Bottom);
		var scenes = All<DriveInScene>(this).ToList();
		if (width <= 0 || height <= 0 || scenes.Count == 0)
		{
			return;
		}

		var at = screen.TransformToVisual(scenes[0]).TransformPoint(new Point(0, 0));
		var place = (width, height, Math.Round(at.X), Math.Round(at.Y), Math.Round(screen.ActualWidth), Math.Round(screen.ActualHeight));
		if (place == _place)
		{
			return;
		}

		_place = place;
		var layout = DriveInLayout.Theatre(width, height, new Rect(place.Item3, place.Item4, place.Item5, place.Item6));
		foreach (var scene in scenes)
		{
			scene.Layout = layout;
		}

		// the window's rounded corners clip the lot (a Grid's CornerRadius rounds only its background)
		var visual = ElementCompositionPreview.GetElementVisual(this);
		var compositor = visual.Compositor;
		var shape = compositor.CreateRoundedRectangleGeometry();
		shape.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
		shape.CornerRadius = new Vector2((float)CornerRadius.TopLeft);
		visual.Clip = compositor.CreateGeometricClip(shape);
	}

	private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : class
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T found && match(found))
			{
				return found;
			}

			if (Find(child, match) is { } deeper)
			{
				return deeper;
			}
		}

		return null;
	}

	private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is T found)
			{
				yield return found;
			}

			foreach (var deeper in All<T>(child))
			{
				yield return deeper;
			}
		}
	}
}
