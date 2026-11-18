using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// 1.2.0 (the button review, I29): <see cref="ExpandableText"/>'s "Show more" / "Show less", a link (NixieLinkButtonStyle, at
/// least 32 px tall) that tells Narrator whether the text is expanded (UI Automation's ExpandCollapse pattern); Expand and
/// Collapse act as a click does.
/// </summary>
public sealed partial class ExpandToggleButton : HyperlinkButton
{
	public ExpandToggleButton() => DefaultStyleKey = typeof(HyperlinkButton);

	public bool IsExpanded { get; private set; }

	internal void SetExpanded(bool expanded)
	{
		if (IsExpanded == expanded)
		{
			return;
		}

		var old = IsExpanded;
		IsExpanded = expanded;
		if (FrameworkElementAutomationPeer.FromElement(this) is ExpandTogglePeer peer)
		{
			peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, StateOf(old), StateOf(expanded));
		}
	}

	internal static ExpandCollapseState StateOf(bool expanded) => expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

	protected override AutomationPeer OnCreateAutomationPeer() => new ExpandTogglePeer(this);

	private sealed partial class ExpandTogglePeer(ExpandToggleButton owner) : HyperlinkButtonAutomationPeer(owner), IExpandCollapseProvider
	{
		public ExpandCollapseState ExpandCollapseState => StateOf(owner.IsExpanded);

		protected override object GetPatternCore(PatternInterface patternInterface) =>
			patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);

		public void Expand()
		{
			if (!owner.IsExpanded)
			{
				Invoke();
			}
		}

		public void Collapse()
		{
			if (owner.IsExpanded)
			{
				Invoke();
			}
		}
	}
}
