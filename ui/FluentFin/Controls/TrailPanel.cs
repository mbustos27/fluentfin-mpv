using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentFin.Controls;

// 1.7 upnav
/// <summary>
/// Nixie 1.7: the trail's crumbs (TrailEyebrow) in one line that never wraps over the title. When they are wider than the
/// room, the series' name gives way first (trimmed with an ellipsis; its tooltip says it whole), then the season's, then the
/// library's, none below <see cref="MinCrumb"/>.
/// </summary>
public sealed partial class TrailPanel : Panel
{
	/// <summary>The children, by index, in the order they give way: the series (1), the season (2), the library (0).</summary>
	internal static IReadOnlyList<int> GiveWayOrder { get; } = [1, 2, 0];

	/// <summary>The narrowest a shown crumb gets: a letter, the ellipsis and the chevron.</summary>
	internal const double MinCrumb = 64;

	/// <summary>
	/// Each crumb's width in <paramref name="room"/>: as wide as it wants while they all fit; else the ones in
	/// <see cref="GiveWayOrder"/> give up what is too much, in turn, down to <see cref="MinCrumb"/> (a hidden one, 0, stays 0).
	/// </summary>
	internal static double[] Fit(IReadOnlyList<double> wanted, double room)
	{
		var widths = wanted.ToArray();
		var over = widths.Sum() - room;
		if (double.IsNaN(over) || double.IsInfinity(room) || over <= 0)
		{
			return widths;
		}

		foreach (var i in GiveWayOrder.Concat(Enumerable.Range(0, widths.Length).Except(GiveWayOrder)))
		{
			if (i >= widths.Length || widths[i] <= MinCrumb)
			{
				continue;
			}

			var cut = Math.Min(over, widths[i] - MinCrumb);
			widths[i] -= cut;
			over -= cut;
			if (over <= 0)
			{
				break;
			}
		}

		return widths;
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		var wanted = new double[Children.Count];
		double height = 0;
		for (var i = 0; i < Children.Count; i++)
		{
			Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
			wanted[i] = Children[i].DesiredSize.Width;
		}

		var widths = Fit(wanted, availableSize.Width);
		double width = 0;
		for (var i = 0; i < Children.Count; i++)
		{
			if (widths[i] < wanted[i])
			{
				Children[i].Measure(new Size(widths[i], availableSize.Height));
			}

			width += Children[i].DesiredSize.Width;
			height = Math.Max(height, Children[i].DesiredSize.Height);
		}

		return new Size(width, height);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		double x = 0;
		foreach (var child in Children)
		{
			var width = child.DesiredSize.Width;
			child.Arrange(new Rect(x, 0, width, finalSize.Height));
			x += width;
		}

		return finalSize;
	}
}
// end 1.7 upnav
