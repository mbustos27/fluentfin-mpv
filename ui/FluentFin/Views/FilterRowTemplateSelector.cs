using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

/// <summary>1.1.4 (the review, item 5): the Library's Filter menu is one virtualised list of headings (a kind) and check boxes (a value).</summary>
public sealed partial class FilterRowTemplateSelector : DataTemplateSelector
{
	public DataTemplate? Header { get; set; }

	public DataTemplate? Option { get; set; }

	protected override DataTemplate? SelectTemplateCore(object item) => item is FilterFacet ? Header : Option;

	protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
