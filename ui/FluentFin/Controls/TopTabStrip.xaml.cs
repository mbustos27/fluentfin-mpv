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
/// page; "Libraries ▾" (the libraries the bar has no room for: Nixie 1.4.0's fold by width) opens a menu of them. One Tab
/// stop: Tab lands on the marked tab (the first when none is), Left/Right move between the tabs (round), Home/End to the
/// ends. Narrator: a tab list ("Sections") of tab items, each "n of m" and selected or not. <see cref="IsCompact"/> (narrow
/// windows) shows one menu button instead.
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

	/// <summary>
	/// The search box opened in a very narrow window (TopBar.Fit): the sections' menu button steps aside while it is open.
	/// </summary>
	public bool IsMenuHidden
	{
		get => _menuHidden;
		set
		{
			_menuHidden = value;
			ShowMode();
		}
	}

	private bool _menuHidden;

	/// <summary>The narrowest windows: the sections' menu button shows its glyph only (Narrator still reads the section).</summary>
	public bool IsMenuLabelHidden
	{
		get => CompactLabel.Visibility == Visibility.Collapsed;
		set => CompactLabel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>The tabs changed (the libraries arrived or changed, a pin): the bar measures again.</summary>
	public event EventHandler? TabsChanged;

	/// <summary>
	/// Nixie 1.4.0: the widths of every tab, nothing folded (TopBar.Fit folds them): measured off the tree (so they are known
	/// while folded), each corrected by what the row really took the last time it showed (the estimate ran short in the
	/// running app). The folded tab is measured with the longer of "Libraries" and the lit library's name, so the fold does not
	/// change with which library is lit.
	/// </summary>
	public TopRowWidths RowWidths()
	{
		// what each tab shown really took (the estimate ran short in the running app): kept per label, so the fold, measured
		// on the whole row, never swings with which tabs happen to be shown
		foreach (var button in _buttons)
		{
			if (!IsCompact && button.ActualWidth > 0)
			{
				_measured[(button.Tab.Title, button.ShowsMenu)] = button.ActualWidth;
			}
		}

		var all = _navigation?.AllTabs ?? [];
		var fixedWidth = Margin.Left + Margin.Right + all.Where(t => t.Kind is not (TopTabKind.Library or TopTabKind.Libraries)).Sum(t => TabWidth(t.Title, false));
		var libraries = all.Where(t => t.Kind == TopTabKind.Library).Select(t => TabWidth(t.Title, false)).ToList();
		var plain = TabWidth(TopTabs.LibrariesTitle, false);
		var lit = all.FirstOrDefault(t => t.Kind == TopTabKind.Library && _navigation?.Lit is { } m && t.Mark == m)?.Title;
		var folded = Math.Max(TabWidth(TopTabs.LibrariesTitle, true), lit is null ? 0 : TabWidth(lit, true));
		return new TopRowWidths(fixedWidth, libraries, plain, folded);
	}

	private readonly Dictionary<(string Title, bool Menu), double> _measured = [];

	/// <summary>The folded tab's chevron and its gap.</summary>
	private const double ChevronWidth = 16;

	/// <summary>
	/// A tab's width: as last laid out, else its label (SemiBold, the marked one's) and the padding, up to the tab's MaxWidth,
	/// with the chevron for the folded tab.
	/// </summary>
	private double TabWidth(string title, bool menu) =>
		_measured.TryGetValue((title, menu), out var measured) ? measured : Math.Min(200, TextWidth(title) + 24) + (menu ? ChevronWidth : 0);

	/// <summary>The sections' menu button's width (its glyph, the marked section's name up to 150, its padding), margin included.</summary>
	public double MenuButtonWidth() => Margin.Left + Margin.Right + 20 + 16 + 10 + Math.Min(150, TextWidth(CompactTitle()));

	/// <summary>The narrow menu button's label: the lit destination (the search box and Settings too), else "Menu".</summary>
	private string CompactTitle() => _navigation switch
	{
		{ IsSearchLit: true } => "Search",
		{ IsSettingsLit: true } => "Settings",
		{ Lit: { } lit } n when n.AllTabs.FirstOrDefault(t => t.Mark == lit) is { } tab => tab.Title,
		_ => "Menu",
	};

	private readonly Dictionary<string, double> _textWidths = [];

	private double TextWidth(string text)
	{
		if (!_textWidths.TryGetValue(text, out var width))
		{
			var probe = new TextBlock { Text = text, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
			probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
			_textWidths[text] = width = Math.Ceiling(probe.DesiredSize.Width);
		}

		return width;
	}

	/// <summary>The tab buttons, in order (tests, the harness through UI Automation).</summary>
	internal IReadOnlyList<TopTabButton> Buttons => _buttons;

	/// <summary>Listening to the tabs (review: a second load, after Unloaded, did not listen again: the tabs stopped following).</summary>
	private bool _attached;

	private void Attach(TopNavigation navigation)
	{
		if (_attached && ReferenceEquals(_navigation, navigation))
		{
			return;
		}

		Detach();
		_navigation = navigation;
		_navigation.PropertyChanged += OnNavigationChanged;
		_attached = true;
		Build();
	}

	private void Detach()
	{
		if (_navigation is not null && _attached)
		{
			_navigation.PropertyChanged -= OnNavigationChanged;
		}

		_attached = false;
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
		else if (e.PropertyName is nameof(TopNavigation.SelectedIndex) or nameof(TopNavigation.Lit))
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

		var tabs = _navigation.Tabs;
		// Nixie 1.4.0: the same row with new names (the folded tab named after the lit library): the buttons stay, so the
		// keyboard and an open menu's way back (its placement) are not lost
		if (_buttons.Count == tabs.Count && _buttons.Zip(tabs).All(p => p.First.Tab.Kind == p.Second.Kind && p.First.Tab.Key == p.Second.Key && p.First.Tab.LibraryId == p.Second.LibraryId))
		{
			for (var i = 0; i < tabs.Count; i++)
			{
				Show(_buttons[i], tabs[i], i, tabs.Count);
			}

			Mark();
			TabsChanged?.Invoke(this, EventArgs.Empty);
			return;
		}

		var hadFocus = _buttons.Any(b => b.FocusState != FocusState.Unfocused);
		TabsPanel.Children.Clear();
		_buttons.Clear();
		for (var i = 0; i < tabs.Count; i++)
		{
			var button = new TopTabButton(this, tabs[i]) { Style = (Style)Resources["TopTabButtonStyle"] };
			Show(button, tabs[i], i, tabs.Count);

			button.Click += OnTabClick;
			TabsPanel.Children.Add(button);
			_buttons.Add(button);
		}

		Mark();
		if (hadFocus)
		{
			(Marked() ?? _buttons.FirstOrDefault())?.Focus(FocusState.Keyboard);
		}

		TabsChanged?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>A tab's button shows <paramref name="tab"/>: its label, the chevron of the folded tab, Narrator's name and "n of m".</summary>
	private static void Show(TopTabButton button, TopTab tab, int index, int count)
	{
		button.Tab = tab;
		button.Content = tab.Title;
		button.ShowsMenu = tab.HasMenu;
		AutomationProperties.SetName(button, tab.Title);
		AutomationProperties.SetPositionInSet(button, index + 1);
		AutomationProperties.SetSizeOfSet(button, count);
		// Nixie 1.4.0: the folded tab names the lit library ("Movies ▾"); Narrator hears that it is the libraries' menu
		AutomationProperties.SetHelpText(button, tab.HasMenu ? "Libraries menu: All libraries and the libraries the bar has no room for" : "");
	}

	private void Mark()
	{
		var selected = _navigation?.SelectedIndex ?? -1;
		for (var i = 0; i < _buttons.Count; i++)
		{
			_buttons[i].IsSelected = i == selected;
		}

		CompactLabel.Text = CompactTitle();
		AutomationProperties.SetName(CompactButton, $"Sections, {CompactLabel.Text}");
		// the menu button's width follows the marked section's name
		TabsChanged?.Invoke(this, EventArgs.Empty);
	}

	private TopTabButton? Marked() => _buttons.FirstOrDefault(b => b.IsSelected);

	private void ShowMode()
	{
		TabsPanel.Visibility = IsCompact ? Visibility.Collapsed : Visibility.Visible;
		CompactButton.Visibility = IsCompact && !_menuHidden ? Visibility.Visible : Visibility.Collapsed;
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
			ShowMenu(TopMenus.Libraries(tab), placement);
			return;
		}

		_navigation.Open(tab);
	}

	/// <summary>
	/// Nixie 1.4.0: a menu of destinations (the folded libraries, or every section in a narrow window): radio items, the lit
	/// one checked (the review's X7: the menu never marked the current library), a separator where <see cref="TopMenus"/> puts
	/// one. Choosing one hands the keyboard to the page it opens (item 4: the flyout gave it back to the tab, so the first
	/// poster was the 6th to 10th Tab away). The theme's <c>TopBarMenuPresenterStyle</c>, when it has one, dresses it.
	/// </summary>
	private void ShowMenu(IReadOnlyList<TopTab?> entries, FrameworkElement placement)
	{
		var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
		if (Services.ThemeResources.Find("TopBarMenuPresenterStyle") is Style presenter)
		{
			menu.MenuFlyoutPresenterStyle = presenter;
		}

		var chosen = false;
		foreach (var entry in entries)
		{
			if (entry is null)
			{
				menu.Items.Add(new MenuFlyoutSeparator());
				continue;
			}

			var item = new RadioMenuFlyoutItem { Text = entry.Title, GroupName = "TopDestinations", IsChecked = _navigation?.Lit is { } lit && entry.Mark == lit };
			item.Click += (_, _) =>
			{
				chosen = true;
				_navigation?.Open(entry);
			};
			menu.Items.Add(item);
		}

		menu.Closed += (_, _) =>
		{
			if (chosen)
			{
				HandFocusToPage();
			}
		};
		menu.ShowAt(placement);
	}

	/// <summary>
	/// Nixie 1.4.0 (item 4): after a menu opened a page, the keyboard goes to the page's first stop rather than back to the
	/// tab, unless the page has taken it already (its own arrival focus).
	/// </summary>
	private void HandFocusToPage()
	{
		var tries = 0;
		void Try()
		{
			if (XamlRoot is null || App.GetService<Contracts.Services.INavigationService>() is not Services.NavigationService { Frame.Content: FrameworkElement page })
			{
				return;
			}

			if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && !TopBarFocus.IsInBar(focused))
			{
				return;
			}

			if ((!page.IsLoaded || Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(page) is not Control first) && ++tries < 20)
			{
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Try);
				return;
			}

			// Programmatic: the ring shows when the menu was used with the keyboard, not after a click
			(Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(page) as Control)?.Focus(FocusState.Programmatic);
			// once more shortly after: the flyout's own way back (to a tab the new row replaced) can land in the bar after us
			if (!_rechecked)
			{
				_rechecked = true;
				var timer = DispatcherQueue.CreateTimer();
				timer.Interval = TimeSpan.FromMilliseconds(350);
				timer.IsRepeating = false;
				timer.Tick += (_, _) =>
				{
					tries = 0;
					Try();
				};
				timer.Start();
			}
		}

		_rechecked = false;

		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Try);
	}

	private bool _rechecked;

	/// <summary>Narrow windows: every destination in one menu, the lit one checked (TopMenus.Sections).</summary>
	private void OnCompactClick(object sender, RoutedEventArgs e)
	{
		if (_navigation is null)
		{
			return;
		}

		ShowMenu(TopMenus.Sections(_navigation.AllTabs), CompactButton);
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

/// <summary>Nixie 1.4.0: whether an element is in the top bar (the tabs, search, the account, Back).</summary>
internal static class TopBarFocus
{
	public static bool IsInBar(DependencyObject element)
	{
		for (var e = element; e is not null; e = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(e))
		{
			if (e is TopTabStrip or Views.TitleBarControl)
			{
				return true;
			}
		}

		return false;
	}
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

	public TopTab Tab { get; internal set; }

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

	/// <summary>Nixie 1.4.0: the folded "Libraries ▾": its chevron shows (it opens a menu, the review's CL-m10).</summary>
	public bool ShowsMenu
	{
		get => (bool)GetValue(ShowsMenuProperty);
		set => SetValue(ShowsMenuProperty, value);
	}

	public static readonly DependencyProperty ShowsMenuProperty =
		DependencyProperty.Register(nameof(ShowsMenu), typeof(bool), typeof(TopTabButton), new PropertyMetadata(false, (d, _) => ((TopTabButton)d).ShowChevron()));

	private void ShowChevron()
	{
		if (GetTemplateChild("Chevron") is UIElement chevron)
		{
			chevron.Visibility = ShowsMenu ? Visibility.Visible : Visibility.Collapsed;
		}
	}

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		ShowSelection();
		ShowChevron();
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
