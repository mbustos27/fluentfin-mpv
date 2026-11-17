using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass poster motion, Full mode only (docs/design/SEAGLASS.md): the poster tilts a few degrees toward the pointer, in
/// perspective, and a soft specular sheen (a radial gradient on a sprite visual) follows the pointer. Composition only: no
/// layout, no effect brushes. The card's lift stays separate; keyboard focus lifts without tilt (nothing here runs for it).
/// </summary>
internal sealed class PosterTilt
{
	/// <summary>The most a poster tilts, at its edge, in degrees.</summary>
	public const float MaxDegrees = 7f;

	/// <summary>Distance of the viewer, in device-independent pixels: a smaller number is a stronger perspective.</summary>
	public const float PerspectiveDistance = 900f;

	private static readonly TimeSpan _follow = TimeSpan.FromMilliseconds(140);

	private readonly FrameworkElement _poster;
	private readonly FrameworkElement[] _tilted;
	private readonly FrameworkElement _sheenHost;
	private readonly FrameworkElement _perspectiveHost;
	private SpriteVisual? _sheen;
	private CompositionRoundedRectangleGeometry? _clip;
	private bool _active;

	/// <param name="poster">The element whose size and pointer position decide the tilt.</param>
	/// <param name="tilted">The elements that tilt together (the artwork and what casts its shadow).</param>
	/// <param name="sheenHost">A transparent element over the artwork that carries the sheen; it must not take hits.</param>
	/// <param name="perspectiveHost">The parent of the tilted elements.</param>
	public PosterTilt(FrameworkElement poster, FrameworkElement[] tilted, FrameworkElement sheenHost, FrameworkElement perspectiveHost)
	{
		_perspectiveHost = perspectiveHost;
		_poster = poster;
		_tilted = tilted;
		_sheenHost = sheenHost;
	}

	/// <summary>
	/// The rotation of a poster of this size with the pointer at (x, y) inside it: the axis (in the poster's plane) and the angle
	/// in degrees, zero at the centre and at most <paramref name="maxDegrees"/> at the corner. The pointed side dips away.
	/// </summary>
	public static (Vector3 Axis, float Degrees) Rotation(double x, double y, double width, double height, float maxDegrees = MaxDegrees)
	{
		if (width < 1 || height < 1)
		{
			return (Vector3.UnitY, 0);
		}

		var px = (float)Math.Clamp(x / width * 2 - 1, -1, 1);
		var py = (float)Math.Clamp(y / height * 2 - 1, -1, 1);
		var length = MathF.Sqrt(px * px + py * py);
		if (length < 0.001f)
		{
			return (Vector3.UnitY, 0);
		}

		// pointer right: about +Y; pointer low: about -X (a positive turn about Y lowers the right edge, about X the low edge raises)
		return (new Vector3(-py / length, px / length, 0), Math.Min(length, 1f) * maxDegrees);
	}

	/// <summary>The perspective that makes the tilt read as depth, centred on the poster (identity for flat content).</summary>
	public static Matrix4x4 Perspective(double width, double height)
	{
		var perspective = Matrix4x4.Identity;
		perspective.M34 = -1f / PerspectiveDistance;
		var centre = new Vector3((float)width / 2, (float)height / 2, 0);
		return Matrix4x4.CreateTranslation(-centre) * perspective * Matrix4x4.CreateTranslation(centre);
	}

	public void Move(double x, double y)
	{
		if (!SafeMotion.Allowed)
		{
			Leave();
			return;
		}

		_active = true;
		var width = _poster.ActualWidth;
		var height = _poster.ActualHeight;
		var (axis, degrees) = Rotation(x, y, width, height);
		foreach (var element in _tilted)
		{
			var visual = ElementCompositionPreview.GetElementVisual(element);
			visual.CenterPoint = new Vector3((float)width / 2, (float)height / 2, 0);
			visual.RotationAxis = axis;
			var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
			animation.InsertKeyFrame(1, degrees);
			animation.Duration = _follow;
			visual.StartAnimation("RotationAngleInDegrees", animation);
			// the perspective sits on the parent, so the tilted children are drawn in depth
			if (ElementCompositionPreview.GetElementVisual(_perspectiveHost) is { } parent)
			{
				parent.TransformMatrix = Perspective(_perspectiveHost.ActualWidth, _perspectiveHost.ActualHeight);
			}
		}

		ShowSheen(x, y, width, height);
	}

	public void Leave()
	{
		if (!_active)
		{
			return;
		}

		_active = false;
		foreach (var element in _tilted)
		{
			var visual = ElementCompositionPreview.GetElementVisual(element);
			var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
			animation.InsertKeyFrame(1, 0);
			animation.Duration = TimeSpan.FromMilliseconds(220);
			visual.StartAnimation("RotationAngleInDegrees", animation);
		}

		if (_sheen is not null)
		{
			var fade = _sheen.Compositor.CreateScalarKeyFrameAnimation();
			fade.InsertKeyFrame(1, 0);
			fade.Duration = TimeSpan.FromMilliseconds(200);
			_sheen.StartAnimation("Opacity", fade);
		}
	}

	private void ShowSheen(double x, double y, double width, double height)
	{
		var host = ElementCompositionPreview.GetElementVisual(_sheenHost);
		if (_sheen is null)
		{
			var compositor = host.Compositor;
			var brush = compositor.CreateRadialGradientBrush();
			brush.EllipseCenter = new Vector2(0.5f, 0.5f);
			brush.EllipseRadius = new Vector2(0.5f, 0.5f);
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0x40, 255, 255, 255)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(0x12, 255, 255, 255)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));
			_sheen = compositor.CreateSpriteVisual();
			_sheen.Brush = brush;
			_sheen.Opacity = 0;
			ElementCompositionPreview.SetElementChildVisual(_sheenHost, _sheen);
			_clip = compositor.CreateRoundedRectangleGeometry();
			_clip.CornerRadius = new Vector2(12, 12);
			host.Clip = compositor.CreateGeometricClip(_clip);
		}

		_clip!.Size = new Vector2((float)width, (float)height);
		var size = (float)Math.Max(width, height) * 1.1f;
		_sheen.Size = new Vector2(size, size);
		_sheen.Offset = new Vector3((float)x - size / 2, (float)y - size / 2, 0);
		if (_sheen.Opacity < 1)
		{
			var fade = _sheen.Compositor.CreateScalarKeyFrameAnimation();
			fade.InsertKeyFrame(1, 1);
			fade.Duration = TimeSpan.FromMilliseconds(160);
			_sheen.StartAnimation("Opacity", fade);
		}
	}
}
