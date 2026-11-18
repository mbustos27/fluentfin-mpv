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
	public static readonly Color Crimson = Color.FromArgb(255, 0xBC, 0x1E, 0x47);

	/// <summary>The strokes for a theme (in high contrast, <paramref name="highlight"/> alone).</summary>
	public static IReadOnlyList<Layer> Layers(bool light, bool highContrast, Color highlight) =>
		highContrast ? [new(3, highlight, 1)]
		: light ? [new(3, Crimson, 1)]
		: [new(9, HotPink, 0.18), new(5, HotPink, 0.45), new(2, NeonCore, 1)];

	/// <summary>The space the stack takes: its widest layer.</summary>
	public const double StackHeight = 9;

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

		var highlight = Application.Current.Resources.TryGetValue("SystemColorHighlightColor", out var value) && value is Color c ? c : Crimson;
		Children.Clear();
		foreach (var layer in Layers(ActualTheme == ElementTheme.Light, highContrast, highlight))
		{
			Children.Add(new Rectangle
			{
				Height = layer.Height,
				RadiusX = layer.Height / 2,
				RadiusY = layer.Height / 2,
				VerticalAlignment = VerticalAlignment.Center,
				Fill = new SolidColorBrush(layer.Color),
				Opacity = layer.Opacity,
				IsHitTestVisible = false,
			});
		}
	}
}
