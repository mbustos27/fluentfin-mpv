using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace FluentFin.Controls;

/// <summary>
/// 1.2.0: the selected tab's underline in the top bar, as a lit neon tube: the theme guide's stroke stack (a wide faint halo, a
/// glow, a near-white core), no blur and no GPU effect, so it stays crisp at any scale and costs nothing per frame. Dark: hot-pink
/// light around the core. Light: a painted sign, the crimson core alone. High contrast: the system highlight colour, no halo.
/// Solid keeps the stack (opaque strokes, not glass). It stretches to its container's width; show it under the selected tab
/// (Visibility or Opacity). Never takes input or appears to Narrator: the tab itself carries the selected state.
/// </summary>
public sealed partial class NeonUnderline : Grid
{
	/// <summary>One stroke of the stack: its height in DIP, its colour and its opacity, outermost first.</summary>
	public readonly record struct Layer(double Height, Color Color, double Opacity);

	public static readonly Color HotPink = Color.FromArgb(255, 0xF9, 0x69, 0x85);
	public static readonly Color NeonCore = Color.FromArgb(255, 0xFF, 0xF4, 0xEE);

	/// <summary>The lit tube's core: a pink-tinted white (the art pass: the neutral white core read as a plain white line).</summary>
	public static readonly Color TubeCore = Color.FromArgb(255, 0xFF, 0xE3, 0xEA);
	public static readonly Color Crimson = Color.FromArgb(255, 0xBC, 0x1E, 0x47);

	/// <summary>The strokes for a theme (in high contrast, <paramref name="highlight"/> alone).</summary>
	public static IReadOnlyList<Layer> Layers(bool light, bool highContrast, Color highlight) =>
		highContrast ? [new(3, highlight, 1)]
		: light ? [new(3, Crimson, 1)]
		: [new(14, HotPink, 0.05), new(10, HotPink, 0.09), new(7, HotPink, 0.16), new(4.5, HotPink, 0.32), new(2.5, TubeCore, 1)];

	/// <summary>
	/// The user's highlight colour, as high contrast shows it: from Windows' UI settings. (Looking up SystemColorHighlightColor in
	/// the app's resources misses it: it lives in WinUI's theme dictionaries, and the lookup fell back to crimson.)
	/// </summary>
	public static Color SystemHighlight()
	{
		try
		{
			return new Windows.UI.ViewManagement.UISettings().UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
		}
		catch (Exception)
		{
			return Crimson;
		}
	}

	/// <summary>The space the stack takes: its widest layer. (The art pass: five layers in small steps, so the halo fades instead of stepping.)</summary>
	public const double StackHeight = 14;

	public NeonUnderline()
	{
		IsHitTestVisible = false;
		Height = StackHeight;
		VerticalAlignment = VerticalAlignment.Bottom;
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		Loaded += (_, _) =>
		{
			ActualThemeChanged += OnThemeChanged;
			Build();
		};
		Unloaded += (_, _) => ActualThemeChanged -= OnThemeChanged;
	}

	private void OnThemeChanged(FrameworkElement sender, object args) => Build();

	private void Build()
	{
		bool highContrast;
		try { highContrast = GlassMode.IsHighContrast; }
		catch (Exception) { highContrast = false; }

		var layers = Layers(ActualTheme == ElementTheme.Light, highContrast, SystemHighlight());
		var fill = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
		foreach (var (offset, color) in Stops(layers))
		{
			fill.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
		}

		Children.Clear();
		// one bar, its halo one continuous fall-off (the art pass: stacked rectangles showed each layer's edge as a step)
		Children.Add(new Rectangle
		{
			Height = StackHeight,
			RadiusX = StackHeight / 2,
			RadiusY = StackHeight / 2,
			VerticalAlignment = VerticalAlignment.Center,
			Fill = fill,
			IsHitTestVisible = false,
		});
	}

	/// <summary>
	/// The bar's vertical gradient, top to bottom, from the layers (outermost first): transparent at the bar's edges, each layer's
	/// colour at its opacity where its edge falls, ramping between them; the innermost (the core) flat across its height, then
	/// the same, mirrored. A single layer (light, high contrast) is a crisp line: no ramp into it.
	/// </summary>
	public static IReadOnlyList<(double Offset, Color Color)> Stops(IReadOnlyList<Layer> layers)
	{
		static Color At(Color c, double opacity) => Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * c.A), c.R, c.G, c.B);
		double Edge(Layer layer) => (StackHeight - layer.Height) / 2 / StackHeight;

		var top = new List<(double, Color)> { (0, At(layers[0].Color, 0)) };
		if (layers.Count == 1)
		{
			top.Add((Math.Max(0, Edge(layers[0]) - 0.001), At(layers[0].Color, 0)));
		}

		foreach (var layer in layers)
		{
			top.Add((Edge(layer), At(layer.Color, layer.Opacity)));
		}

		var stops = new List<(double, Color)>(top);
		for (var i = top.Count - 1; i >= 0; i--)
		{
			stops.Add((1 - top[i].Item1, top[i].Item2));
		}

		return stops;
	}
}
