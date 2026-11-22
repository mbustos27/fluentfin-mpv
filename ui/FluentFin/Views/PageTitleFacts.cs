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

	/// <summary>1.6.1 (colpage queue): what Narrator says for the count: "4 screens on the lot" (the tubes alone said "4").</summary>
	public string FeatureSpoken => SpokenCount(FeatureCount, FeatureLabel);

	/// <summary>1.6.1: a count and its word as Narrator says them: "6 features" (the board writes "6" "FEATURES").</summary>
	public static string SpokenCount(string count, string label) =>
		string.IsNullOrWhiteSpace(label) ? count : $"{count} {label.Trim().ToLowerInvariant()}";

	public Visibility FeaturesVisibility => Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
