namespace FluentFin.Core;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, layout item 8): how tall a hero is: a share of the window's height between 360 and 640 px,
/// not a fixed 640, so on a 1100x900 window the page's first content (a season's episodes, a movie's details) shows. The
/// movie and episode pages give it 60 %; the series and season pages 45 %, so the season picker and the first episodes are
/// on the first screen; Home 55 %.
/// </summary>
public static class HeroLayout
{
	public const double Min = 360;
	public const double Max = 640;

	public const double ItemShare = 0.6;
	public const double EpisodesShare = 0.45;
	public const double HomeShare = 0.55;

	/// <summary>Below this a hero is compact: a smaller logo and fewer lines of overview, so the words fit on the artwork.</summary>
	public const double CompactBelow = 480;

	public static double Height(double windowHeight, double share) =>
		windowHeight > 0 ? Math.Clamp(Math.Round(windowHeight * share), Min, Max) : Max;

	public static bool IsCompact(double heroHeight) => heroHeight < CompactBelow;

	/// <summary>
	/// Nixie 1.7.5 (the gate's g3, at 1280 x 720): the hero grows to hold its words. A short window's hero (60 % of 720: 432
	/// px) was shorter than an episode's two-line title with its series' logo, the actions and the overview, and the words,
	/// held at its bottom, ran up under the top bar: the way up (the crumbs) out of view and the logo cut by the bar.
	/// <paramref name="words"/> is their height with their margins; <paramref name="top"/> what the hero keeps clear at its
	/// top (the bar its artwork runs under).
	/// </summary>
	public static double Fit(double height, double words, double top) => Math.Max(height, Math.Ceiling(words + top));
}
