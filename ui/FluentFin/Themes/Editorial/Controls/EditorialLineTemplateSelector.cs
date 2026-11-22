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

	/// <summary>1.6 colpage: a line of the list view (View > List): one title across the width.</summary>
	public DataTemplate? ListRow { get; set; }

	protected override DataTemplate? SelectTemplateCore(object item) => item switch
	{
		EditorialSectionLine => Section,
		EditorialRowLine { Metrics.ListForm: true } => ListRow ?? Row,
		EditorialRowLine => Row,
		EditorialDetailsLine => Details,
		_ => null,
	};

	protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
