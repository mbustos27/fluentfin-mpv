using System.ComponentModel;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// 1.2.0 (the top bar, replacing the left rail): the sections of <see cref="TopNavigation"/> as text tabs. A tab opens its
/// page; "Libraries" (many libraries) opens a menu of them. One Tab stop: Tab lands on the marked tab (the first when none
/// is), Left/Right move between the tabs (round), Home/End to the ends. Narrator: a tab list ("Sections") of tab items, each
/// "n of m" and selected or not. <see cref="IsCompact"/> (narrow windows) shows one menu button instead.
/// </summary>
public sealed partial class TopTabStrip : UserControl
{
	private TopNavigation? _navigation;
	private readonly List<TopTabButton> _buttons = [];

	public TopTabStrip()
	{
		InitializeComponent();
		AutomationProperties.SetName(this, "Sections");
		AutomationProperties.SetAutomationId(this, "TopTabs");
		TabsPanel.GettingFocus += OnTabsGettingFocus;
		TabsPanel.KeyDown += OnTabsKeyDown;
		Loaded += (_, _) => Attach(_navigation ?? App.GetService<TopNavigation>());
		Unloaded += (_, _) => Detach();
	}

	/// <summary>Narrow windows (TitleBarControl): the tabs in one menu button.</summary>
	public bool IsCompact
	{
		get => (bool)GetValue(IsCompactProperty);
		set => SetValue(IsCompactProperty, value);
	}

	public static readonly DependencyProperty IsCompactProperty =
		DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(TopTabStrip), new PropertyMetadata(false, (d, _) => ((TopTabStrip)d).ShowMode()));

	/// <summary>The tab buttons, in order (tests, the harness through UI Automation).</summary>
	internal IReadOnlyList<TopTabButton> Buttons => _buttons;

	private void Attach(TopNavigation navigation)
	{
		if (ReferenceEquals(_navigation, navigation) && _buttons.Count > 0)
		{
			return;
		}

		Detach();
		_navigation = navigation;
		_navigation.PropertyChanged += OnNavigationChanged;
		Build();
	}

	private void Detach()
	{
		if (_navigation is not null)
		{
			_navigation.PropertyChanged -= OnNavigationChanged;
		}
	}

	private void OnNavigationChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (!DispatcherQueue.HasThreadAccess)
		{
			DispatcherQueue.TryEnqueue(() => OnNavigationChanged(sender, e));
			return;
		}

		if (e.PropertyName == nameof(TopNavigation.Tabs))
		{
			Build();
		}
		else if (e.PropertyName == nameof(TopNavigation.SelectedIndex))
		{
			Mark();
		}
	}

	private void Build()
	{
		if (_navigation is null)
		{
			return;
		}

		var hadFocus = _buttons.Any(b => b.FocusState != FocusState.Unfocused);
		TabsPanel.Children.Clear();
		_buttons.Clear();
		var tabs = _navigation.Tabs;
		for (var i = 0; i < tabs.Count; i++)
		{
			var button = new TopTabButton(this, tabs[i])
			{
				Style = (Style)Resources["TopTabButtonStyle"],
				Content = tabs[i].Title,
			};
			AutomationProperties.SetName(button, tabs[i].Title);
			AutomationProperties.SetPositionInSet(button, i + 1);
			AutomationProperties.SetSizeOfSet(button, tabs.Count);
			if (tabs[i].HasMenu)
			{
				AutomationProperties.SetHelpText(button, "Opens a menu of the libraries");
			}

			button.Click += OnTabClick;
			TabsPanel.Children.Add(button);
			_buttons.Add(button);
		}

		Mark();
		if (hadFocus)
		{
			(Marked() ?? _buttons.FirstOrDefault())?.Focus(FocusState.Keyboard);
		}
	}

	private void Mark()
	{
		var selected = _navigation?.SelectedIndex ?? -1;
		for (var i = 0; i < _buttons.Count; i++)
		{
			_buttons[i].IsSelected = i == selected;
		}

		CompactLabel.Text = _navigation?.SelectedTab?.Title ?? "Menu";
		AutomationProperties.SetName(CompactButton, $"Sections, {CompactLabel.Text}");
	}

	private TopTabButton? Marked() => _buttons.FirstOrDefault(b => b.IsSelected);

	private void ShowMode()
	{
		TabsPanel.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
		CompactButton.Visibility = IsCompact ? Visibility.Visible : Visibility.Collapsed;
	}

	private void OnTabClick(object sender, RoutedEventArgs e)
	{
		if (sender is TopTabButton button)
		{
			Open(button.Tab, button);
		}
	}

	/// <summary>A tab: its page; the "Libraries" tab with libraries: their menu, under it.</summary>
	internal void Open(TopTab tab, FrameworkElement placement)
	{
		if (_navigation is null)
		{
			return;
		}

		if (tab.HasMenu)
		{
			var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
			foreach (var entry in MenuItems(tab))
			{
				menu.Items.Add(entry);
			}

			menu.ShowAt(placement);
			return;
		}

		_navigation.Open(tab);
	}

	private IEnumerable<MenuFlyoutItemBase> MenuItems(TopTab tab)
	{
		foreach (var entry in tab.Menu)
		{
			var item = new MenuFlyoutItem { Text = entry.Title };
			item.Click += (_, _) => _navigation?.Open(entry);
			yield return item;
		}
	}

	/// <summary>Narrow windows: every section in one menu (the marked one checked; "Libraries" as a submenu).</summary>
	private void OnCompactClick(object sender, RoutedEventArgs e)
	{
		if (_navigation is null)
		{
			return;
		}

		var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
		var tabs = _navigation.Tabs;
		for (var i = 0; i < tabs.Count; i++)
		{
			var tab = tabs[i];
			if (tab.HasMenu)
			{
				var sub = new MenuFlyoutSubItem { Text = tab.Title };
				foreach (var entry in MenuItems(tab))
				{
					sub.Items.Add(entry);
				}

				menu.Items.Add(sub);
				continue;
			}

			var item = new RadioMenuFlyoutItem { Text = tab.Title, GroupName = "TopTabs", IsChecked = i == _navigation.SelectedIndex };
			item.Click += (_, _) => _navigation?.Open(tab);
			menu.Items.Add(item);
		}

		menu.ShowAt(CompactButton);
	}

	/// <summary>Tab from outside lands on the marked tab (the first when none is), not on whichever was first or last.</summary>
	private void OnTabsGettingFocus(UIElement sender, GettingFocusEventArgs args)
	{
		if (args.OldFocusedElement is DependencyObject old && _buttons.Any(b => ReferenceEquals(b, old)))
		{
			return;
		}

		if (args.Direction is FocusNavigationDirection.Next or FocusNavigationDirection.Previous
			&& (Marked() ?? _buttons.FirstOrDefault()) is { } target
			&& !ReferenceEquals(args.NewFocusedElement, target))
		{
			args.TrySetNewFocusedElement(target);
		}
	}

	private void OnTabsKeyDown(object sender, KeyRoutedEventArgs e)
	{
		var at = _buttons.FindIndex(b => b.FocusState != FocusState.Unfocused);
		if (at < 0)
		{
			return;
		}

		var next = TabKeys.Next(e.Key, at, _buttons.Count);
		if (next is { } index)
		{
			_buttons[index].Focus(FocusState.Keyboard);
			e.Handled = true;
		}
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new TopTabStripAutomationPeer(this);
}

/// <summary>The tab keys: Left/Right one tab (round), Home/End the ends; null for any other key.</summary>
public static class TabKeys
{
	public static int? Next(VirtualKey key, int at, int count) => count == 0 ? null : key switch
	{
		VirtualKey.Left => (at - 1 + count) % count,
		VirtualKey.Right => (at + 1) % count,
		VirtualKey.Home => 0,
		VirtualKey.End => count - 1,
		_ => null,
	};
}

/// <summary>One text tab of the top bar: marked (<see cref="IsSelected"/>) or not; Narrator's tab item.</summary>
public sealed partial class TopTabButton : Button
{
	private readonly TopTabStrip? _strip;

	public TopTabButton()
	{
		Tab = new TopTab("", "");
	}

	internal TopTabButton(TopTabStrip strip, TopTab tab)
	{
		_strip = strip;
		Tab = tab;
		Loaded += (_, _) => ShowSelection();
	}

	public TopTab Tab { get; }

	public bool IsSelected
	{
		get => (bool)GetValue(IsSelectedProperty);
		set => SetValue(IsSelectedProperty, value);
	}

	public static readonly DependencyProperty IsSelectedProperty =
		DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(TopTabButton), new PropertyMetadata(false, OnIsSelectedChanged));

	private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var button = (TopTabButton)d;
		button.ShowSelection();
		if (FrameworkElementAutomationPeer.FromElement(button) is { } peer)
		{
			peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, (bool)e.OldValue, (bool)e.NewValue);
		}
	}

	private void ShowSelection() => VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", false);

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		ShowSelection();
	}

	/// <summary>Narrator's Select: what a click does.</summary>
	internal void Activate() => _strip?.Open(Tab, this);

	internal TopTabStrip? Strip => _strip;

	protected override AutomationPeer OnCreateAutomationPeer() => new TopTabButtonAutomationPeer(this);
}

/// <summary>The strip for Narrator: a tab list, its selection the marked tab.</summary>
internal sealed partial class TopTabStripAutomationPeer(TopTabStrip owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;

	protected override string GetClassNameCore() => nameof(TopTabStrip);

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

	public bool CanSelectMultiple => false;

	public bool IsSelectionRequired => false;

	public IRawElementProviderSimple[] GetSelection() =>
		owner.Buttons.Where(b => b.IsSelected).Select(b => ProviderFromPeer(FromElement(b) ?? CreatePeerForElement(b))).ToArray();
}

/// <summary>A tab for Narrator: a tab item (its "n of m" from AutomationProperties), selected when marked; Select opens it.</summary>
internal sealed partial class TopTabButtonAutomationPeer(TopTabButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

	protected override string GetClassNameCore() => nameof(TopTabButton);

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);

	public bool IsSelected => owner.IsSelected;

	public IRawElementProviderSimple? SelectionContainer =>
		owner.Strip is { } strip ? ProviderFromPeer(FromElement(strip) ?? CreatePeerForElement(strip)) : null;

	public void AddToSelection() => owner.Activate();

	public void RemoveFromSelection()
	{
		// one tab is always the page shown: nothing to remove
	}

	public void Select() => owner.Activate();
}
