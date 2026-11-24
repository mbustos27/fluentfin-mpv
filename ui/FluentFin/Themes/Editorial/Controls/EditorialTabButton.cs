using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>
/// Editorial: one tab of a strip that picks what shows under it (the genre strip, the Library's genre chips, the Seasons
/// panel). A button with a selected state (the "Selected" visual state); Narrator reads a tab item, selected or not, "n of m"
/// inside its <see cref="EditorialTabList"/>.
/// </summary>
public sealed partial class EditorialTabButton : Button
{
	public static readonly DependencyProperty IsSelectedProperty =
		DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(EditorialTabButton), new PropertyMetadata(false, (d, _) => ((EditorialTabButton)d).UpdateSelection()));

	public EditorialTabButton()
	{
		Loaded += (_, _) => UpdateSelection();
	}

	public bool IsSelected
	{
		get => (bool)GetValue(IsSelectedProperty);
		set => SetValue(IsSelectedProperty, value);
	}

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		UpdateSelection();
	}

	private void UpdateSelection()
	{
		VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions: false);
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, !IsSelected, IsSelected);
		}
	}

	/// <summary>Select (the automation pattern's, and Narrator's): what a click does.</summary>
	internal void Invoke()
	{
		if (FrameworkElementAutomationPeer.FromElement(this) is ButtonAutomationPeer peer && peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
		{
			invoke.Invoke();
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new EditorialTabButtonAutomationPeer(this);
}

internal sealed partial class EditorialTabButtonAutomationPeer(EditorialTabButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

	protected override string GetClassNameCore() => nameof(EditorialTabButton);

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);

	public bool IsSelected => owner.IsSelected;

	public IRawElementProviderSimple? SelectionContainer =>
		EditorialTabList.Of(owner) is { } list && FromElement(list) is { } peer ? ProviderFromPeer(peer) : null;

	public void AddToSelection() => owner.Invoke();

	public void RemoveFromSelection()
	{
	}

	public void Select() => owner.Invoke();
}

/// <summary>
/// Editorial: the container of <see cref="EditorialTabButton"/>s: a tab list for Narrator, one Tab stop (the keyboard lands
/// on the selected tab), Left/Right (Up/Down in a vertical list) and Home/End between the tabs.
/// </summary>
public sealed partial class EditorialTabList : ContentControl
{
	public static readonly DependencyProperty OrientationProperty =
		DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(EditorialTabList), new PropertyMetadata(Orientation.Horizontal));

	public EditorialTabList()
	{
		IsTabStop = false;
		TabFocusNavigation = KeyboardNavigationMode.Once;
		HorizontalContentAlignment = HorizontalAlignment.Stretch;
		VerticalContentAlignment = VerticalAlignment.Top;
		Template = (ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
			"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContentControl'>" +
			"<ContentPresenter Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' " +
			"HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}' /></ControlTemplate>");
		AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), false);
		GettingFocus += OnGettingFocus;
	}

	public Orientation Orientation
	{
		get => (Orientation)GetValue(OrientationProperty);
		set => SetValue(OrientationProperty, value);
	}

	/// <summary>The tabs, in order (the visual tree's, so a repeater's tabs count as they are drawn).</summary>
	internal List<EditorialTabButton> Tabs()
	{
		var found = new List<EditorialTabButton>();
		void Walk(DependencyObject node)
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
			{
				var child = VisualTreeHelper.GetChild(node, i);
				if (child is EditorialTabButton { Visibility: Visibility.Visible } tab)
				{
					found.Add(tab);
				}
				else
				{
					Walk(child);
				}
			}
		}

		Walk(this);
		return found;
	}

	internal static EditorialTabList? Of(DependencyObject element)
	{
		for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is EditorialTabList list)
			{
				return list;
			}
		}

		return null;
	}

	/// <summary>The keyboard comes into the list from outside: to the selected tab (else the first), not to whichever is first.</summary>
	private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
	{
		if (args.FocusState != FocusState.Keyboard || args.OldFocusedElement is DependencyObject old && IsInside(old))
		{
			return;
		}

		var tabs = Tabs();
		if (tabs.FirstOrDefault(t => t.IsSelected) is { } selected && !ReferenceEquals(args.NewFocusedElement, selected))
		{
			args.TrySetNewFocusedElement(selected);
		}
	}

	private bool IsInside(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, this))
			{
				return true;
			}
		}

		return false;
	}

	private void OnKeyDown(object sender, KeyRoutedEventArgs e)
	{
		var tabs = Tabs();
		// Nixie 1.7.4: the tab the key came from, or holds it (the source may be a part of the tab)
		var at = -1;
		for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, this) && at < 0; node = VisualTreeHelper.GetParent(node))
		{
			at = tabs.FindIndex(t => ReferenceEquals(t, node));
		}

		if (at < 0)
		{
			return;
		}

		var horizontal = Orientation == Orientation.Horizontal;
		if (KeyTarget(e.Key, at, [.. tabs.Select(t => t.IsEnabled)], horizontal) is not { } next)
		{
			return;
		}

		e.Handled = true; // the ends do not wrap, and the page does not take the key for something else
		if (next >= 0 && next < tabs.Count && next != at)
		{
			tabs[next].Focus(FocusState.Keyboard);
			// Nixie 1.7.4 (the rows gate: "End doesn't reach the last genre"): the first and the last tab take the strip to its
			// very start or end, so the last clears what lies over the strip's end (the Library's fade and its chevron, which
			// the strip's trailing room is for); the others are just brought into view
			// (a strip's own sideways scroller only: a vertical strip, or one in the page's scroller, is brought into view)
			if (horizontal && FindScroller() is { HorizontalScrollMode: not ScrollMode.Disabled, ScrollableWidth: > 0 } scroller
				&& EdgeOffset(next, tabs.Count, scroller.ScrollableWidth) is { } edge)
			{
				scroller.ChangeView(edge, null, null, true);
			}
			else
			{
				tabs[next].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
			}
		}
	}

	/// <summary>
	/// Where a key takes the keyboard in a strip of tabs (<paramref name="enabled"/>: which take it): one along, no wrap; Home
	/// and End to the first and the last that can take it (Nixie 1.7.4: End went to the last even when it was off, and so
	/// nowhere). Null for a key the strip leaves alone.
	/// </summary>
	internal static int? KeyTarget(VirtualKey key, int at, IReadOnlyList<bool> enabled, bool horizontal)
	{
		var count = enabled.Count;
		int Step(int from, int by)
		{
			for (var i = from + by; i >= 0 && i < count; i += by)
			{
				if (enabled[i])
				{
					return i;
				}
			}

			return from;
		}

		return key switch
		{
			VirtualKey.Left when horizontal => Step(at, -1),
			VirtualKey.Right when horizontal => Step(at, 1),
			VirtualKey.Up when !horizontal => Step(at, -1),
			VirtualKey.Down when !horizontal => Step(at, 1),
			VirtualKey.Home => Step(-1, 1) is var first && first >= 0 ? first : at,
			VirtualKey.End => Step(count, -1) is var last && last < count ? last : at,
			_ => null,
		};
	}

	/// <summary>Nixie 1.7.4: the strip's offset for tab <paramref name="target"/> of <paramref name="count"/>: its start for the first, its end for the last, null otherwise.</summary>
	internal static double? EdgeOffset(int target, int count, double scrollable) =>
		count <= 0 ? null : target == 0 ? 0 : target == count - 1 ? Math.Max(0, scrollable) : null;

	private ScrollViewer? FindScroller()
	{
		for (var node = VisualTreeHelper.GetParent(this); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is ScrollViewer viewer)
			{
				return viewer;
			}
		}

		return null;
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new EditorialTabListAutomationPeer(this);
}

internal sealed partial class EditorialTabListAutomationPeer(EditorialTabList owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;

	protected override string GetClassNameCore() => nameof(EditorialTabList);

	protected override bool IsControlElementCore() => true;

	protected override bool IsContentElementCore() => true;

	protected override bool IsKeyboardFocusableCore() => false;

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

	public bool CanSelectMultiple => false;

	public bool IsSelectionRequired => false;

	public IRawElementProviderSimple[] GetSelection() =>
		[.. owner.Tabs().Where(t => t.IsSelected).Select(t => FromElement(t) ?? CreatePeerForElement(t)).OfType<AutomationPeer>().Select(ProviderFromPeer)];
}
