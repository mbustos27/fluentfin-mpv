using Microsoft.UI.Xaml;

namespace FluentFin.Controls;

/// <summary>
/// 1.2.0 (the top bar): the window's one bar (back, the wordmark, the tabs, search, the account), translucent glass over the
/// content. A page starts below it; a page with artwork at its top sets <c>lc:TopBar.ExtendsUnder="True"</c> on itself and
/// starts at the window's top instead, its artwork running under the bar, which then shows a dark scrim with light text
/// (as the Apple TV app's heroes do). The shell reads it from each page it shows (ShellPage.UpdateInsets).
/// </summary>
public static class TopBar
{
	/// <summary>The bar's height (TitleBarControl's TitleBar): where a page starts, unless it extends under the bar.</summary>
	public const double Height = 48;

	public static readonly DependencyProperty ExtendsUnderProperty =
		DependencyProperty.RegisterAttached("ExtendsUnder", typeof(bool), typeof(TopBar), new PropertyMetadata(false));

	public static bool GetExtendsUnder(DependencyObject element) => (bool)element.GetValue(ExtendsUnderProperty);

	public static void SetExtendsUnder(DependencyObject element, bool value) => element.SetValue(ExtendsUnderProperty, value);

	/// <summary>Where a page shown in the shell starts: below the bar, or at the top when it extends under it or the bar is hidden.</summary>
	public static double TopInset(bool barVisible, bool extendsUnder) => barVisible && !extendsUnder ? Height : 0;
}
