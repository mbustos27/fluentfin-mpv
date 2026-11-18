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

	/// <summary>The bar keeps at least this much of itself to drag the window by.</summary>
	public const double MinDragGap = 48;

	public const double MinSearchWidth = 200, MaxSearchWidth = 360, MinOpenSearchWidth = 140, MaxOpenSearchWidth = 260;

	/// <summary>The search button (narrow windows) and the space between the right header's items.</summary>
	public const double SearchIconWidth = 32, Spacing = 10;

	/// <summary>The sections' menu button with its glyph only (its label given up in the narrowest windows).</summary>
	public const double MenuGlyphWidth = 46;

	/// <summary>
	/// Review of 590eaf6: what the bar shows in <paramref name="available"/> view pixels, from the widths its parts measured.
	/// It folded at a fixed 1100 px: with a long library name at 1120 px the account slid under Minimize and nothing was left
	/// to drag. Wide: the wordmark, every tab and a search box of at least <see cref="MinSearchWidth"/> (as wide as the room
	/// left, up to <see cref="MaxSearchWidth"/>). Else compact: the sections' menu button and a search button, giving up, as
	/// the room shrinks, the wordmark, then the menu button's label (its glyph stays), then, only while the search box is open
	/// from its button, the menu button itself; the open box gets the room left. Always: Back, the account,
	/// <see cref="MinDragGap"/> and the caption buttons' inset.
	/// </summary>
	public static TopBarFit Fit(double available, double captionInset, double back, double wordmark, double tabs, double menuButton, double account, bool search, bool searchOpen)
	{
		var free = available - captionInset - MinDragGap - back - (account > 0 ? account + Spacing : 0);
		if (free >= wordmark + tabs + (search ? MinSearchWidth + Spacing : 0))
		{
			return new TopBarFit(Compact: false, ShowWordmark: true, ShowSections: true, SearchWidth: search ? Math.Min(MaxSearchWidth, free - wordmark - tabs - Spacing) : 0);
		}

		var searchPart = !search ? 0 : searchOpen ? MinOpenSearchWidth + Spacing : SearchIconWidth + Spacing;
		var showWordmark = free >= wordmark + menuButton + searchPart;
		var showLabel = free >= menuButton + searchPart;
		var showSections = !(search && searchOpen) || free >= MenuGlyphWidth + searchPart;
		var left = (showWordmark ? wordmark : 0) + (!showSections ? 0 : showLabel ? menuButton : MenuGlyphWidth);
		var box = search && searchOpen ? Math.Clamp(free - left - Spacing, MinOpenSearchWidth, MaxOpenSearchWidth) : 0;
		return new TopBarFit(Compact: true, ShowWordmark: showWordmark, ShowSections: showSections, ShowSectionsLabel: showLabel, SearchWidth: box);
	}

	/// <summary>The most the bar adds to the room it keeps, after measuring a drag gap smaller than <see cref="MinDragGap"/>.</summary>
	public const double MaxSlack = 64;

	/// <summary>
	/// The room the bar keeps beyond the parts it measured, after the gap it really left was measured: a gap under
	/// <see cref="MinDragGap"/> adds what was missing (up to <see cref="MaxSlack"/>; the parts' widths ran a few pixels short
	/// in the narrowest windows). It never shrinks: a calibration, not a state of the window.
	/// </summary>
	public static double Slack(double current, double measuredGap) =>
		measuredGap < MinDragGap - 0.5 ? Math.Min(MaxSlack, current + Math.Ceiling(MinDragGap - measuredGap)) : current;
}

/// <summary>What the top bar shows at a width (<see cref="TopBar.Fit"/>).</summary>
public readonly record struct TopBarFit(bool Compact, bool ShowWordmark, bool ShowSections, double SearchWidth, bool ShowSectionsLabel = true);
