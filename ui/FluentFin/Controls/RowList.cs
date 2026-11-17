using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass scrolling: the container of a row's cards, so Narrator reads the row as a list (named after the row, its cards as
/// items "n of m"). A plain content control otherwise; it fills the row's width so the wheel has something to land on past the
/// last card.
/// </summary>
public sealed partial class RowList : ContentControl
{
	public RowList()
	{
		IsTabStop = false;
		HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
		VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top;
		Padding = new Microsoft.UI.Xaml.Thickness(0);
		// a template of the content alone: no chrome, no focus visual
		Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
			"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'>" +
			"<Border Background='Transparent'><ContentPresenter Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' /></Border></ControlTemplate>");
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new RowListAutomationPeer(this);
}

internal sealed partial class RowListAutomationPeer(RowList owner) : FrameworkElementAutomationPeer(owner)
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

	protected override string GetClassNameCore() => nameof(RowList);

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override bool IsKeyboardFocusableCore() => false;
}
