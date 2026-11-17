using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>Seaglass: the quiet metadata lines of an item (see ItemMetaRows.xaml); hidden when there are none.</summary>
public sealed partial class ItemMetaRows : UserControl
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(ItemMetaRows), new PropertyMetadata(null, (d, _) => ((ItemMetaRows)d).Apply()));

	public ItemMetaRows()
	{
		InitializeComponent();
		Apply();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	private void Apply()
	{
		var rows = ItemMeta.Rows(Dto);
		Repeater.ItemsSource = rows;
		Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}
}
