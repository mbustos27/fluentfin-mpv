using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>
/// Editorial: a fixed number of equal columns across the width (a season's stills): each item gets the column's width, a
/// row is as tall as its tallest item. UniformGridLayout sizes every item from the first one measured, whose wish (a
/// synopsis unwrapped) dropped a column.
/// </summary>
public sealed partial class EditorialColumnsLayout : NonVirtualizingLayout
{
	public static readonly DependencyProperty ColumnsProperty =
		DependencyProperty.Register(nameof(Columns), typeof(int), typeof(EditorialColumnsLayout), new PropertyMetadata(4, (d, _) => ((EditorialColumnsLayout)d).InvalidateMeasure()));

	public static readonly DependencyProperty ColumnSpacingProperty =
		DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(EditorialColumnsLayout), new PropertyMetadata(40.0, (d, _) => ((EditorialColumnsLayout)d).InvalidateMeasure()));

	public static readonly DependencyProperty RowSpacingProperty =
		DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(EditorialColumnsLayout), new PropertyMetadata(26.0, (d, _) => ((EditorialColumnsLayout)d).InvalidateMeasure()));

	public int Columns
	{
		get => (int)GetValue(ColumnsProperty);
		set => SetValue(ColumnsProperty, value);
	}

	public double ColumnSpacing
	{
		get => (double)GetValue(ColumnSpacingProperty);
		set => SetValue(ColumnSpacingProperty, value);
	}

	public double RowSpacing
	{
		get => (double)GetValue(RowSpacingProperty);
		set => SetValue(RowSpacingProperty, value);
	}

	private double ColumnWidth(double width) => Math.Max(0, (width - (Columns - 1) * ColumnSpacing) / Math.Max(1, Columns));

	protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
	{
		var width = double.IsInfinity(availableSize.Width) ? 1600 : availableSize.Width;
		var column = ColumnWidth(width);
		var height = 0.0;
		var rowHeight = 0.0;
		var children = context.Children;
		for (var i = 0; i < children.Count; i++)
		{
			children[i].Measure(new Size(column, double.PositiveInfinity));
			rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
			if (i % Columns == Columns - 1 || i == children.Count - 1)
			{
				height += rowHeight + (i < children.Count - 1 ? RowSpacing : 0);
				rowHeight = 0;
			}
		}

		return new Size(width, height);
	}

	protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
	{
		var column = ColumnWidth(finalSize.Width);
		var children = context.Children;
		var y = 0.0;
		for (var start = 0; start < children.Count; start += Columns)
		{
			var end = Math.Min(children.Count, start + Columns);
			var rowHeight = 0.0;
			for (var i = start; i < end; i++)
			{
				rowHeight = Math.Max(rowHeight, children[i].DesiredSize.Height);
			}

			for (var i = start; i < end; i++)
			{
				children[i].Arrange(new Rect((i - start) * (column + ColumnSpacing), y, column, rowHeight));
			}

			y += rowHeight + RowSpacing;
		}

		return finalSize;
	}
}
