using CommunityToolkit.WinUI;
using FluentFin.Converters;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.6 (plan 4.3): a collection's art when it has no poster of its own (every Nixie collection): a 2×2 mosaic of its
/// first titles' posters, cut to the frame it sits in (the theme's poster shape and corners come from that frame). One title
/// fills it, two share it side by side, three put the first over the full width. Decorative: Narrator reads the tile's name.
/// </summary>
public sealed partial class CollectionMosaic : UserControl
{
	[GeneratedDependencyProperty]
	public partial IReadOnlyList<BaseItemDto>? Items { get; set; }

	/// <summary>The height the posters are asked at (decoded at display size).</summary>
	[GeneratedDependencyProperty(DefaultValue = 300d)]
	public partial double ImageHeight { get; set; }

	// the seams between the posters: a dark line on any theme (the frame's own fill showed through as a pale one)
	private readonly Grid _grid = new() { ColumnSpacing = 2, RowSpacing = 2, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0E, 0x0C, 0x12)) };

	public CollectionMosaic()
	{
		IsTabStop = false;
		AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
		Content = _grid;
	}

	partial void OnItemsChanged(IReadOnlyList<BaseItemDto>? newValue) => Build();

	private void Build()
	{
		_grid.Children.Clear();
		_grid.RowDefinitions.Clear();
		_grid.ColumnDefinitions.Clear();
		var items = (Items ?? []).Take(4).ToList();
		Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
		if (items.Count == 0)
		{
			return;
		}

		var two = items.Count > 1;
		_grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		if (two)
		{
			_grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		}

		_grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		if (items.Count > 2)
		{
			_grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
		}

		var height = items.Count > 2 ? ImageHeight / 2 : ImageHeight;
		for (var i = 0; i < items.Count; i++)
		{
			var image = new LoadingImage
			{
				Glyph = "",
				Stretch = Stretch.UniformToFill,
				Source = BaseItemDtoConverters.GetImage(items[i], ImageType.Primary, height),
			};
			// three: the first over the full width, then two; four: two and two
			var (row, column, span) = items.Count switch
			{
				1 => (0, 0, 1),
				2 => (0, i, 1),
				3 => i == 0 ? (0, 0, 2) : (1, i - 1, 1),
				_ => (i / 2, i % 2, 1),
			};
			Grid.SetRow(image, row);
			Grid.SetColumn(image, column);
			Grid.SetColumnSpan(image, span);
			_grid.Children.Add(image);
		}
	}
}
