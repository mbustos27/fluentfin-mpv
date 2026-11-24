using FluentFin.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>One tab of a <see cref="CinematicTabStrip"/>: its words, what Narrator reads, whether it is the one shown, and what it stands for.</summary>
public sealed record CinematicTab(string Label, string Name, bool IsSelected, object? Payload);

/// <summary>
/// Cinematic's text tabs (the seasons on a series page, the library's sections, the player's panel): the button system's Tab
/// role (a neon underline under the one shown), one Tab stop for the strip, Left/Right/Home/End along it, Enter, Space or a
/// click to pick. Narrator reads a tab list whose selection is the tab shown, as the top bar's tabs.
/// </summary>
public sealed partial class CinematicTabStrip : UserControl
{
	private readonly StackPanel _panel = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
	private readonly List<CinematicTabButton> _buttons = [];
	private readonly EdgeScroller _edge;

	public CinematicTabStrip()
	{
		IsTabStop = false;
		_panel.TabFocusNavigation = KeyboardNavigationMode.Once;
		// 1.6 colpage (review Major 4): many tabs (a library's presets and up to 12 Nixie collections) scroll sideways, the
		// keyboard's tab brought into view; tabs that fit show as before
		var scroller = new ScrollViewer
		{
			Content = _panel,
			IsTabStop = false,
			HorizontalScrollMode = ScrollMode.Enabled,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, // 1.7.4: the arrows, a drag and the wheel, as on a row
			VerticalScrollMode = ScrollMode.Disabled,
			VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
		};
		// Nixie 1.7.4 (the rows stream): the rows' pointer behaviour (EdgeScroller: arrows at its ends on hover, the wheel once
		// the page can't move, a drag), so a long show's seasons are in the mouse's reach as a row's cards are
		(var host, _edge) = EdgeScroller.Wrap(scroller, _panel, "tabs");
		Content = host;
		Loaded += (_, _) => _edge.Name(AutomationProperties.GetName(this) is { Length: > 0 } name ? name : "tabs");
		_panel.AddHandler(KeyDownEvent, new KeyEventHandler(Panel_KeyDown), true);
	}

	/// <summary>A tab was picked (a click, Enter, Space, or Narrator's Select): its <see cref="CinematicTab.Payload"/>.</summary>
	public event EventHandler<CinematicTab>? TabInvoked;

	public static readonly DependencyProperty TabFontSizeProperty =
		DependencyProperty.Register(nameof(TabFontSize), typeof(double), typeof(CinematicTabStrip), new PropertyMetadata(17.0));

	public double TabFontSize
	{
		get => (double)GetValue(TabFontSizeProperty);
		set => SetValue(TabFontSizeProperty, value);
	}

	public static readonly DependencyProperty TabStyleProperty =
		DependencyProperty.Register(nameof(TabStyle), typeof(Style), typeof(CinematicTabStrip), new PropertyMetadata(null));

	/// <summary>
	/// Nixie 1.4.0 style (the library UX review, C-M7): the tabs' style; null: the Tab role (NixieTabButtonStyle, the underline,
	/// for the seasons and the player's panel). The library's sections set CinematicSectionTabButtonStyle (Cards.xaml): a plate
	/// and the weight, so the pink underline marks only where you are in the top bar.
	/// </summary>
	public Style? TabStyle
	{
		get => GetValue(TabStyleProperty) as Style;
		set => SetValue(TabStyleProperty, value);
	}

	internal IReadOnlyList<CinematicTabButton> Buttons => _buttons;

	/// <summary>
	/// Nixie 1.3.1: whether a click gives a tab the keyboard (true, as on the browsing pages). The player's strip says no: a
	/// click there opens its panel and leaves the keyboard on the video, so the player's keys go on working (docs/SHORTCUTS.md);
	/// Tab still reaches the tabs.
	/// </summary>
	public bool TabsTakeFocusOnClick { get; set; } = true;

	/// <summary>Shows <paramref name="tabs"/>; the keyboard's stop is the tab shown (else the first).</summary>
	public void SetTabs(IReadOnlyList<CinematicTab> tabs)
	{
		// 1.6 colpage: the same tabs with another one lit (the library's presets after Space or a click) change in place: made
		// anew, the focused tab left the tree and the keyboard fell to the next control (Sort) before the new tab could take it
		if (_buttons.Count == tabs.Count && _buttons.Count > 0 && _buttons.Zip(tabs).All(x => x.First.Tab.Label == x.Second.Label))
		{
			var focused = _buttons.FindIndex(b => b.FocusState != FocusState.Unfocused);
			var stop = focused >= 0 ? focused : Math.Max(0, tabs.ToList().FindIndex(t => t.IsSelected));
			for (var i = 0; i < tabs.Count; i++)
			{
				_buttons[i].Show(tabs[i]);
				AutomationProperties.SetName(_buttons[i], tabs[i].Name);
				_buttons[i].IsTabStop = i == stop;
			}

			return;
		}

		var focusedAt = _buttons.FindIndex(b => b.FocusState != FocusState.Unfocused);
		_panel.Children.Clear();
		_buttons.Clear();
		for (var i = 0; i < tabs.Count; i++)
		{
			var tab = tabs[i];
			var button = new CinematicTabButton(this, tab) { Content = tab.Label, FontSize = TabFontSize };
			if (TabStyle is { } own)
			{
				button.Style = own;
			}
			else if (Application.Current.Resources.TryGetValue("NixieTabButtonStyle", out var style) && style is Style s)
			{
				button.Style = s;
			}

			button.FontSize = TabFontSize;
			AutomationProperties.SetName(button, tab.Name);
			AutomationProperties.SetPositionInSet(button, i + 1);
			AutomationProperties.SetSizeOfSet(button, tabs.Count);
			button.AllowFocusOnInteraction = TabsTakeFocusOnClick;
			button.Click += (_, _) => Invoke(button.Tab); // the tab as shown now (1.6 colpage: tabs change in place)
			_buttons.Add(button);
			_panel.Children.Add(button);
		}

		var selected = Math.Max(0, _buttons.FindIndex(b => b.Tab.IsSelected));
		for (var i = 0; i < _buttons.Count; i++)
		{
			_buttons[i].IsTabStop = i == selected;
		}

		if (focusedAt >= 0 && _buttons.Count > 0)
		{
			_buttons[Math.Min(focusedAt, _buttons.Count - 1)].Focus(FocusState.Keyboard);
		}
	}

	/// <summary>The keyboard to the tab shown (the page's "pick a season" step).</summary>
	public bool FocusSelected(FocusState state) => (_buttons.FirstOrDefault(b => b.Tab.IsSelected) ?? _buttons.FirstOrDefault()) is { } button && button.Focus(state);

	internal void Invoke(CinematicTab tab) => TabInvoked?.Invoke(this, tab);

	private void Panel_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		var at = _buttons.FindIndex(b => b.FocusState != FocusState.Unfocused);
		if (at < 0 || TabKeys.Next(e.Key, at, _buttons.Count) is not { } next)
		{
			return;
		}

		e.Handled = true;
		_buttons[at].IsTabStop = false;
		_buttons[next].IsTabStop = true;
		_buttons[next].Focus(FocusState.Keyboard);
	}

	protected override AutomationPeer OnCreateAutomationPeer() => new CinematicTabStripAutomationPeer(this);
}

/// <summary>One tab: the Tab role's look, "Selected" while it is the one shown.</summary>
public sealed partial class CinematicTabButton : Button
{
	private readonly CinematicTabStrip? _strip;

	public CinematicTabButton()
	{
		Tab = new CinematicTab("", "", false, null);
	}

	internal CinematicTabButton(CinematicTabStrip strip, CinematicTab tab)
	{
		_strip = strip;
		Tab = tab;
		Loaded += (_, _) => ShowSelection();
	}

	public CinematicTab Tab { get; private set; }

	internal CinematicTabStrip? Strip => _strip;

	/// <summary>1.6 colpage: the same tab, lit or not now (the strip changes its tabs in place).</summary>
	internal void Show(CinematicTab tab)
	{
		Tab = tab;
		Content = tab.Label;
		ShowSelection();
	}

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		ShowSelection();
	}

	private void ShowSelection() => VisualStateManager.GoToState(this, Tab.IsSelected ? "Selected" : "Unselected", false);

	internal void Activate() => _strip?.Invoke(Tab);

	protected override AutomationPeer OnCreateAutomationPeer() => new CinematicTabButtonAutomationPeer(this);
}

internal sealed partial class CinematicTabStripAutomationPeer(CinematicTabStrip owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;

	protected override string GetClassNameCore() => nameof(CinematicTabStrip);

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.Selection ? this : base.GetPatternCore(patternInterface);

	public bool CanSelectMultiple => false;

	public bool IsSelectionRequired => false;

	public IRawElementProviderSimple[] GetSelection() =>
		[.. owner.Buttons.Where(b => b.Tab.IsSelected).Select(b => ProviderFromPeer(FromElement(b) ?? CreatePeerForElement(b)))];
}

internal sealed partial class CinematicTabButtonAutomationPeer(CinematicTabButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
{
	protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;

	protected override string GetClassNameCore() => nameof(CinematicTabButton);

	protected override object GetPatternCore(PatternInterface patternInterface) =>
		patternInterface == PatternInterface.SelectionItem ? this : base.GetPatternCore(patternInterface);

	public bool IsSelected => owner.Tab.IsSelected;

	public IRawElementProviderSimple? SelectionContainer =>
		owner.Strip is { } strip ? ProviderFromPeer(FromElement(strip) ?? CreatePeerForElement(strip)) : null;

	public void AddToSelection() => owner.Activate();

	public void RemoveFromSelection()
	{
		// one tab is always the one shown
	}

	public void Select() => owner.Activate();
}
