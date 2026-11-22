using Microsoft.UI.Xaml;

namespace FluentFin.Views;

/// <summary>
/// 1.6 colpage (C5, theme polish): the words a theme's title frame (PageTitleFrameStyle: Drive-in's marquee board) shows
/// around a shared page's title: a kicker over it ("NOW SHOWING"; none: ""), and a count beside it in the theme's numbers
/// ("6" "FEATURES"; 0: none). Bound with {Binding}; the title itself stays the page's heading.
/// </summary>
public sealed record PageTitleFacts(string Kicker, int Count, string CountLabel)
{
	public Visibility KickerVisibility => string.IsNullOrEmpty(Kicker) ? Visibility.Collapsed : Visibility.Visible;

	public string FeatureCount => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

	public string FeatureLabel => CountLabel;

	public Visibility FeaturesVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
