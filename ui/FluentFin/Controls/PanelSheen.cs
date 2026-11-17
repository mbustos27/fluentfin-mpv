using System.Numerics;
using FluentFin.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass, Full mode only: a soft specular highlight (a radial gradient on a sprite visual, composition only) that follows the
/// pointer over a glass panel, as light on a thick pane. It sits in the panel's template, listens to the pointer on the template's
/// root (what the panel's content handled too), never takes hits, and does nothing in Reduced, Solid, or with animations off.
/// </summary>
public sealed class PanelSheen : Grid
{
	/// <summary>The highlight's diameter, in pixels.</summary>
	public const float Diameter = 420f;

	/// <summary>The corner radius the highlight is clipped to (the glass panel's).</summary>
	public float CornerRadiusPixels { get; set; } = 20f;

	private SpriteVisual? _sprite;
	private CompositionRoundedRectangleGeometry? _clip;
	private FrameworkElement? _root;

	public PanelSheen()
	{
		IsHitTestVisible = false;
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		Loaded += (_, _) =>
		{
			_root = Parent as FrameworkElement;
			_root?.AddHandler(PointerMovedEvent, new PointerEventHandler(OnMoved), true);
			_root?.AddHandler(PointerExitedEvent, new PointerEventHandler(OnExited), true);
		};
		Unloaded += (_, _) =>
		{
			_root?.RemoveHandler(PointerMovedEvent, new PointerEventHandler(OnMoved));
			_root?.RemoveHandler(PointerExitedEvent, new PointerEventHandler(OnExited));
			_root = null;
		};
	}

	/// <summary>Whether the highlight shows for this pointer type: mouse and pen only, and only where motion is allowed.</summary>
	public static bool Applies(Microsoft.UI.Input.PointerDeviceType device, bool motionAllowed) => motionAllowed && device is Microsoft.UI.Input.PointerDeviceType.Mouse or Microsoft.UI.Input.PointerDeviceType.Pen;

	private void OnMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!Applies(e.Pointer.PointerDeviceType, GlassMode.MotionAllowed) || ActualWidth < 1)
		{
			Hide();
			return;
		}

		var visual = ElementCompositionPreview.GetElementVisual(this);
		if (_sprite is null)
		{
			var compositor = visual.Compositor;
			var brush = compositor.CreateRadialGradientBrush();
			brush.EllipseCenter = new Vector2(0.5f, 0.5f);
			brush.EllipseRadius = new Vector2(0.5f, 0.5f);
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0x2E, 255, 255, 255)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(0x10, 200, 255, 245)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));
			_sprite = compositor.CreateSpriteVisual();
			_sprite.Brush = brush;
			_sprite.Size = new Vector2(Diameter, Diameter);
			_sprite.Opacity = 0;
			ElementCompositionPreview.SetElementChildVisual(this, _sprite);
			_clip = compositor.CreateRoundedRectangleGeometry();
			visual.Clip = compositor.CreateGeometricClip(_clip);
		}

		_clip!.CornerRadius = new Vector2(CornerRadiusPixels, CornerRadiusPixels);
		_clip.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
		var point = e.GetCurrentPoint(this).Position;
		_sprite.Offset = new Vector3((float)point.X - Diameter / 2, (float)point.Y - Diameter / 2, 0);
		if (_sprite.Opacity < 1)
		{
			Fade(1, 160);
		}
	}

	private void OnExited(object sender, PointerRoutedEventArgs e) => Hide();

	private void Hide()
	{
		if (_sprite is { Opacity: > 0 })
		{
			Fade(0, 200);
		}
	}

	private void Fade(float to, int milliseconds)
	{
		var animation = _sprite!.Compositor.CreateScalarKeyFrameAnimation();
		animation.InsertKeyFrame(1, to);
		animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
		_sprite.StartAnimation("Opacity", animation);
	}
}
