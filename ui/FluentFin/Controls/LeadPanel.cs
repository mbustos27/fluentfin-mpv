using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.7.4 (the rows stream): children left to right, <see cref="Spacing"/> apart, as a horizontal StackPanel lays them
/// out, except that the first one gets only the width the others leave. A strip of tabs before its summary (Cinematic's
/// season tabs) then scrolls in its own viewer instead of running past the window's edge: in a StackPanel a show of many
/// seasons had its last seasons cut off, out of reach of the pointer and out of sight of the keyboard.
/// </summary>
public sealed partial class LeadPanel : Panel
{
	public double Spacing { get; set; }

	private List<UIElement> Shown => [.. Children.Where(c => c.Visibility == Visibility.Visible)];

	protected override Size MeasureOverride(Size availableSize)
	{
		var shown = Shown;
		if (shown.Count == 0)
		{
			return new Size(0, 0);
		}

		double rest = 0, height = 0;
		foreach (var child in shown.Skip(1))
		{
			child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
			rest += child.DesiredSize.Width;
			height = Math.Max(height, child.DesiredSize.Height);
		}

		var gaps = Spacing * (shown.Count - 1);
		shown[0].Measure(new Size(LeadWidth(availableSize.Width, rest, gaps), availableSize.Height));
		height = Math.Max(height, shown[0].DesiredSize.Height);
		return new Size(shown[0].DesiredSize.Width + rest + gaps, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		var shown = Shown;
		if (shown.Count == 0)
		{
			return finalSize;
		}

		var rest = shown.Skip(1).Sum(c => c.DesiredSize.Width);
		var gaps = Spacing * (shown.Count - 1);
		var x = 0.0;
		for (var i = 0; i < shown.Count; i++)
		{
			var width = i == 0 ? Math.Min(shown[0].DesiredSize.Width, LeadWidth(finalSize.Width, rest, gaps)) : shown[i].DesiredSize.Width;
			shown[i].Arrange(new Rect(x, 0, width, finalSize.Height));
			x += width + Spacing;
		}

		return finalSize;
	}

	/// <summary>The first child's width: what the others and the gaps leave of <paramref name="available"/> (all of it when unbounded).</summary>
	internal static double LeadWidth(double available, double others, double gaps) =>
		double.IsInfinity(available) ? double.PositiveInfinity : Math.Max(0, available - others - gaps);
}
