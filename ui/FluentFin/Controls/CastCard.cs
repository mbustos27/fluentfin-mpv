using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.7.4 (the rows stream): one person of a Cast &amp; crew row (CastRow.xaml): a keyboard stop of the row's list, so
/// Left, Right, Home and End reach every person and bring them into view as on any other row (the photos were no Tab stop,
/// so the keyboard never reached the people past the window's edge). Narrator reads its whole name and role ("Thomas
/// McDonell, Commander Evangeline Ashcroft-Reyes, 3 of 16"), as does its tooltip, since the name under the photo is
/// trimmed to the photo's width. Opens nothing: a person's page is not part of this release.
/// </summary>
public sealed partial class CastCard : ContentControl
{
	// 1.7.4 integration (the rows review's m4): the template parsed once and shared by every person (a cast of 16 parsed it 16
	// times, on the UI thread, as the row came). A template is a recipe, safe to share; the app has one UI thread
	private static ControlTemplate? s_template;

	public CastCard()
	{
		IsTabStop = true;
		UseSystemFocusVisuals = true;
		FocusVisualMargin = new Thickness(-2);
		HorizontalContentAlignment = HorizontalAlignment.Stretch;
		VerticalContentAlignment = VerticalAlignment.Top;
		Template = s_template ??= (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
			"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'>" +
			"<ContentPresenter Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' /></ControlTemplate>");
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new CastCardAutomationPeer(this);
}

internal sealed partial class CastCardAutomationPeer(CastCard owner) : FrameworkElementAutomationPeer(owner)
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

	protected override string GetClassNameCore() => nameof(CastCard);
}
