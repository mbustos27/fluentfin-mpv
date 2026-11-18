using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.3.0: the shared top bar's chrome, the one part of the bar a layout theme restyles (its TopBarStyle, a Style for
/// this control). The chrome is drawn BEHIND the bar (TitleBarControl's TitleBar: Back, the wordmark, the tabs, search, the
/// account), never around it: a theme switch restyles the chrome alone, so the bar's parts are never moved, rebuilt or
/// reloaded, and keep their state (the search text, an open account menu, the window's title-bar regions) and their
/// automation names. A theme's TopBarStyle sets:
/// <list type="bullet">
/// <item>Template: what is drawn behind the bar, with the states of the group "BarStates": "OverContent" (the bar over a page
/// that starts below it: 1.2.0's glass) and "OverHero" (over a page whose artwork runs under the bar: 1.2.0's dark scrim;
/// the bar's text is then in the dark theme);</item>
/// <item>Padding: how far the bar's parts sit in from the chrome's edges (a floating island insets them).</item>
/// </list>
/// The default style (Styles/TopBarChrome.xaml) is 1.2.0's bar, which Classic keeps.
/// </summary>
public sealed partial class TopBarChrome : Control
{
	public TopBarChrome()
	{
		DefaultStyleKey = typeof(TopBarChrome);
		IsTabStop = false;
	}

	public static readonly DependencyProperty IsOverHeroProperty =
		DependencyProperty.Register(nameof(IsOverHero), typeof(bool), typeof(TopBarChrome), new PropertyMetadata(false, (d, _) => ((TopBarChrome)d).ShowState(true)));

	/// <summary>Whether the page under the bar runs its artwork under it (TopBar.ExtendsUnder).</summary>
	public bool IsOverHero
	{
		get => (bool)GetValue(IsOverHeroProperty);
		set => SetValue(IsOverHeroProperty, value);
	}

	/// <summary>The state for <see cref="IsOverHero"/>.</summary>
	public static string StateFor(bool overHero) => overHero ? "OverHero" : "OverContent";

	protected override void OnApplyTemplate()
	{
		base.OnApplyTemplate();
		ShowState(false);
	}

	private void ShowState(bool useTransitions) => VisualStateManager.GoToState(this, StateFor(IsOverHero), useTransitions);
}
