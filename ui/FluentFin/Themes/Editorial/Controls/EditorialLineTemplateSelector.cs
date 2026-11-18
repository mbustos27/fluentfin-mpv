using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial: the Library grid's lines, each with its template (a section's heading, a row of posters, details).</summary>
public sealed partial class EditorialLineTemplateSelector : DataTemplateSelector
{
	public DataTemplate? Section { get; set; }

	public DataTemplate? Row { get; set; }

	public DataTemplate? Details { get; set; }

	protected override DataTemplate? SelectTemplateCore(object item) => item switch
	{
		EditorialSectionLine => Section,
		EditorialRowLine => Row,
		EditorialDetailsLine => Details,
		_ => null,
	};

	protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
