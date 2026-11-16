using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>fluentfin-mpv: the search box's rows: an item, or a message (no results, a failed search: SearchSuggestions).</summary>
public sealed partial class SearchSuggestionTemplateSelector : DataTemplateSelector
{
	public DataTemplate? ItemTemplate { get; set; }

	public DataTemplate? MessageTemplate { get; set; }

	protected override DataTemplate? SelectTemplateCore(object item) => item is SearchMessage ? MessageTemplate : ItemTemplate;

	protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
