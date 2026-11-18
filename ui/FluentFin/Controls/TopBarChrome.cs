using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.3.0: the shared top bar's chrome, the one part of the bar a layout theme restyles (its TopBarStyle, a Style for
/// this control). Its content is the bar itself (TitleBarControl's TitleBar: Back, the wordmark, the tabs, search, the
/// account), the same element in every theme, so a theme switch keeps the search text and the parts' automation names.
/// The template draws what is behind and around that content and shows it through a ContentPresenter. Its states, in the
/// group "BarStates": "OverContent" (the bar over a page that starts below it: today's glass) and "OverHero" (over a page
/// whose artwork runs under the bar: today's dark scrim; the bar's text is then in the dark theme). The default style
/// (Styles/TopBarChrome.xaml) is 1.2.0's bar, which Classic keeps.
/// </summary>
public sealed partial class TopBarChrome : ContentControl
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
