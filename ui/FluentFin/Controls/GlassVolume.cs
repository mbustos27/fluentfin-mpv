using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Controls;

/// <summary>
/// 1.1.4: the body of a glass slab, in a glass surface's template between the inner glow and the light edge. Two gradients, no
/// effects: a frosted rim (a radial gradient, clear in the middle and milky toward the edges, as tumbled glass is) and a shade
/// along the bottom (the lit top is the inner glow), so a panel reads as a thick piece of glass, not a flat tint. Full and Reduced
/// only: Solid, and so high contrast, keep their flat surfaces. Never takes input, a tab stop or a place in the automation tree.
/// </summary>
public sealed partial class GlassVolume : Grid
{
	/// <summary>The frost at the rim (white) and the shade at the bottom (black), as alpha 0..1, for a theme.</summary>
	public static (double Frost, double Shade) Strength(bool light) => light ? (0.30, 0.05) : (0.07, 0.14);

	/// <summary>Where the frost starts, from the middle (0) to the rim (1) of the panel's ellipse.</summary>
	public const double FrostStart = 0.62;

	/// <summary>Where the bottom shade starts, from the top (0) to the bottom (1).</summary>
	public const double ShadeStart = 0.55;

	/// <summary>Whether the volume shows in a mode: not in Solid (high contrast is Solid).</summary>
	public static bool ShowsIn(GlassModeKind mode) => mode != GlassModeKind.Solid;

	private readonly Border _frost = new() { IsHitTestVisible = false };

	public GlassVolume()
	{
		IsHitTestVisible = false;
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		Children.Add(_frost);
		Loaded += (_, _) =>
		{
			SafeMotion.Subscribe(OnModeChanged);
			ActualThemeChanged += OnThemeChanged;
			Apply();
		};
		Unloaded += (_, _) =>
		{
			SafeMotion.Unsubscribe(OnModeChanged);
			ActualThemeChanged -= OnThemeChanged;
		};
		RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => _frost.CornerRadius = CornerRadius);
	}

	private void OnThemeChanged(FrameworkElement sender, object args) => Apply();

	private void OnModeChanged(object? sender, EventArgs e)
	{
		if (DispatcherQueue.HasThreadAccess)
		{
			Apply();
		}
		else
		{
			DispatcherQueue.TryEnqueue(Apply);
		}
	}

	private void Apply()
	{
		GlassModeKind mode;
		try { mode = GlassMode.Current; }
		catch (Exception) { mode = GlassModeKind.Solid; }

		if (!ShowsIn(mode))
		{
			Visibility = Visibility.Collapsed;
			return;
		}

		Visibility = Visibility.Visible;
		var (frost, shade) = Strength(ActualTheme == ElementTheme.Light);
		_frost.CornerRadius = CornerRadius;
		_frost.Background = new RadialGradientBrush
		{
			Center = new Windows.Foundation.Point(0.5, 0.45),
			GradientOrigin = new Windows.Foundation.Point(0.5, 0.45),
			RadiusX = 0.62,
			RadiusY = 0.66,
			GradientStops =
			{
				new GradientStop { Offset = 0, Color = White(0) },
				new GradientStop { Offset = FrostStart, Color = White(0) },
				new GradientStop { Offset = 1, Color = White(frost) },
			},
		};
		Background = new LinearGradientBrush
		{
			StartPoint = new Windows.Foundation.Point(0, 0),
			EndPoint = new Windows.Foundation.Point(0, 1),
			GradientStops =
			{
				new GradientStop { Offset = ShadeStart, Color = Color.FromArgb(0, 0, 0, 0) },
				new GradientStop { Offset = 1, Color = Color.FromArgb(Alpha(shade), 0, 0, 0) },
			},
		};
	}

	private static Color White(double alpha) => Color.FromArgb(Alpha(alpha), 255, 255, 255);

	private static byte Alpha(double alpha) => (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
}
