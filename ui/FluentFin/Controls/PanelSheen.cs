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
/// Full mode only: a soft specular highlight (a radial gradient on a sprite visual, composition only) that follows the pointer over
/// a glass panel, as light on a thick pane, and (1.1.4) the panel's edge lit where the pointer is near it: the rim's stroke drawn
/// again with a radial gradient centred on the pointer, so the light runs along the edge. It sits in the panel's template, listens
/// to the pointer on the template's root (what the panel's content handled too), never takes hits, and does nothing in Reduced,
/// Solid, with animations off, or while the window is in the background.
/// </summary>
public sealed class PanelSheen : Grid
{
	/// <summary>The highlight's diameter, in pixels.</summary>
	public const float Diameter = 420f;

	/// <summary>How far along the edge the pointer's light reaches, in pixels (the radius of the edge light's gradient).</summary>
	public const float EdgeReach = 170f;

	/// <summary>The edge light's stroke, in pixels: a little over the rim's 1 px, so it reads as the rim catching the light.</summary>
	public const float EdgeThickness = 1.5f;

	/// <summary>The corner radius the highlight is clipped to (the glass panel's).</summary>
	public float CornerRadiusPixels { get; set; } = 12f;

	private SpriteVisual? _sprite;
	private ContainerVisual? _container;
	private ShapeVisual? _edge;
	private CompositionRoundedRectangleGeometry? _edgeShape;
	private CompositionRadialGradientBrush? _edgeBrush;
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

	/// <summary>The edge light's colour at the pointer: warm white on dark glass, white on the light theme's pale glass.</summary>
	public static Color EdgeColor(bool light) => light ? Color.FromArgb(0xFF, 255, 255, 255) : Color.FromArgb(0xA6, 255, 236, 230);

	private void OnMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!Applies(e.Pointer.PointerDeviceType, GlassMode.MotionAllowed) || !GlassActivity.IsActive || ActualWidth < 1)
		{
			Hide();
			return;
		}

		var visual = ElementCompositionPreview.GetElementVisual(this);
		var compositor = visual.Compositor;
		if (_container is null)
		{
			var brush = compositor.CreateRadialGradientBrush();
			brush.EllipseCenter = new Vector2(0.5f, 0.5f);
			brush.EllipseRadius = new Vector2(0.5f, 0.5f);
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0f, Color.FromArgb(0x2E, 255, 255, 255)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(0.45f, Color.FromArgb(0x10, 255, 228, 222)));
			brush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));
			_sprite = compositor.CreateSpriteVisual();
			_sprite.Brush = brush;
			_sprite.Size = new Vector2(Diameter, Diameter);

			// the edge: the panel's outline, stroked with a gradient that is bright only near the pointer
			_edgeBrush = compositor.CreateRadialGradientBrush();
			_edgeBrush.MappingMode = CompositionMappingMode.Absolute;
			_edgeBrush.EllipseRadius = new Vector2(EdgeReach, EdgeReach);
			_edgeBrush.ColorStops.Add(compositor.CreateColorGradientStop(0f, EdgeColor(ActualTheme == ElementTheme.Light)));
			_edgeBrush.ColorStops.Add(compositor.CreateColorGradientStop(1f, Color.FromArgb(0, 255, 255, 255)));
			_edgeShape = compositor.CreateRoundedRectangleGeometry();
			var stroke = compositor.CreateSpriteShape(_edgeShape);
			stroke.StrokeBrush = _edgeBrush;
			stroke.StrokeThickness = EdgeThickness;
			_edge = compositor.CreateShapeVisual();
			_edge.Shapes.Add(stroke);

			_container = compositor.CreateContainerVisual();
			_container.Opacity = 0;
			_container.Children.InsertAtTop(_sprite);
			_container.Children.InsertAtTop(_edge);
			ElementCompositionPreview.SetElementChildVisual(this, _container);
			_clip = compositor.CreateRoundedRectangleGeometry();
			visual.Clip = compositor.CreateGeometricClip(_clip);
		}

		var size = new Vector2((float)ActualWidth, (float)ActualHeight);
		_clip!.CornerRadius = new Vector2(CornerRadiusPixels, CornerRadiusPixels);
		_clip.Size = size;
		_edge!.Size = size;
		// the stroke is centred on the outline: inset by half of it, so all of it is inside the clip
		var inset = EdgeThickness / 2;
		_edgeShape!.Offset = new Vector2(inset, inset);
		_edgeShape.Size = size - new Vector2(EdgeThickness, EdgeThickness);
		_edgeShape.CornerRadius = new Vector2(Math.Max(CornerRadiusPixels - inset, 0), Math.Max(CornerRadiusPixels - inset, 0));
		var point = e.GetCurrentPoint(this).Position;
		_sprite!.Offset = new Vector3((float)point.X - Diameter / 2, (float)point.Y - Diameter / 2, 0);
		_edgeBrush!.EllipseCenter = new Vector2((float)point.X, (float)point.Y);
		// the theme can change while the app runs (Settings' theme choice): the edge's colour follows it
		var edge = EdgeColor(ActualTheme == ElementTheme.Light);
		if (_edgeBrush.ColorStops[0].Color != edge)
		{
			_edgeBrush.ColorStops[0].Color = edge;
		}
		if (_container.Opacity < 1)
		{
			Fade(1, 160);
		}
	}

	private void OnExited(object sender, PointerRoutedEventArgs e) => Hide();

	private void Hide()
	{
		if (_container is { Opacity: > 0 })
		{
			Fade(0, 200);
		}
	}

	private void Fade(float to, int milliseconds)
	{
		var animation = _container!.Compositor.CreateScalarKeyFrameAnimation();
		animation.InsertKeyFrame(1, to);
		animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
		_container.StartAnimation("Opacity", animation);
	}
}
