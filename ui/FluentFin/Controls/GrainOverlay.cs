using FluentFin.Core.Theming;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass: the frosted grain over a glass surface. The shared 256 px noise bitmap tiled over the surface (a grid of images:
/// composition brushes have no tile mode; the noise has no low-frequency structure, so no seam shows), at 4.5 % in Full,
/// 2 % in Reduced and not at all in Solid. Never takes input or a tab stop.
/// </summary>
public sealed partial class GrainOverlay : Canvas
{
	public const double FullOpacity = 0.015;
	public const double ReducedOpacity = 0.008;

	/// <summary>Beyond this many tiles the rest stays bare (a window-sized surface would otherwise carry hundreds).</summary>
	public const int MaxTiles = 96;

	public GrainOverlay()
	{
		IsHitTestVisible = false;
		// decoration: no part of the UI Automation tree (Narrator, the accessibility scan)
		Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		SizeChanged += (_, _) => Rebuild();
		Loaded += (_, _) =>
		{
			GlassMode.Changed += OnModeChanged;
			Apply();
		};
		Unloaded += (_, _) => GlassMode.Changed -= OnModeChanged;
	}

	/// <summary>The opacity for a mode: 0 in Solid.</summary>
	public static double OpacityFor(GlassModeKind mode) => mode switch
	{
		GlassModeKind.Full => FullOpacity,
		GlassModeKind.Reduced => ReducedOpacity,
		_ => 0,
	};

	/// <summary>How many 256 px tiles cover a surface of this size.</summary>
	public static (int Columns, int Rows) TilesFor(double width, double height) =>
		((int)Math.Ceiling(Math.Max(width, 0) / Grain.Size), (int)Math.Ceiling(Math.Max(height, 0) / Grain.Size));

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
		var opacity = OpacityFor(GlassMode.Current);
		Opacity = opacity;
		Visibility = opacity > 0 ? Visibility.Visible : Visibility.Collapsed;
		Rebuild();
	}

	private void Rebuild()
	{
		Children.Clear();
		if (Visibility != Visibility.Visible || ActualWidth < 1 || ActualHeight < 1)
		{
			return;
		}

		Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight) };
		var (columns, rows) = TilesFor(ActualWidth, ActualHeight);
		var count = 0;
		for (var row = 0; row < rows && count < MaxTiles; row++)
		{
			for (var column = 0; column < columns && count < MaxTiles; column++, count++)
			{
				var tile = new Image { Source = GrainBitmap.Shared, Width = Grain.Size, Height = Grain.Size, Stretch = Stretch.Fill, IsHitTestVisible = false };
				Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(tile, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
				SetLeft(tile, column * Grain.Size);
				SetTop(tile, row * Grain.Size);
				Children.Add(tile);
			}
		}
	}
}
