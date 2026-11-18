namespace FluentFin.Controls;

/// <summary>What the wheel does over a row (Seaglass scrolling redesign: one axis per input).</summary>
public enum WheelTarget
{
	/// <summary>The page: the plain vertical wheel, even over a row.</summary>
	Page,

	/// <summary>The row sideways: the tilt wheel and Shift+wheel.</summary>
	Row,
}

/// <summary>
/// Seaglass: the row logic, apart from WinUI so it can be tested: which axis a wheel input owns, where an arrow or the
/// keyboard takes the row (always to a card edge), and which card a key moves to.
/// </summary>
internal static class RowPaging
{
	/// <summary>Pixels the page moves for one wheel notch (120), as a stock scroll viewer does.</summary>
	public const double PixelsPerNotch = 96;

	/// <summary>An end is reached within this many pixels (fractional offsets at high DPI).</summary>
	public const double EndTolerance = 1;

	/// <summary>The vertical wheel owns the page; a tilt wheel or Shift+wheel owns the row.</summary>
	public static WheelTarget TargetOf(bool isHorizontalWheel, bool shift) =>
		isHorizontalWheel || shift ? WheelTarget.Row : WheelTarget.Page;

	/// <summary>A wheel delta (120 per notch, positive away from the user) as the pixels the content moves (scrolling down is positive).</summary>
	public static double Pixels(int delta) => -delta / 120.0 * PixelsPerNotch;

	/// <summary>A row's wheel: up/right (positive) moves the row left, as with every scroller, and tilt right (positive) moves it right.</summary>
	public static double RowPixels(int delta, bool isHorizontalWheel) =>
		isHorizontalWheel ? delta / 120.0 * PixelsPerNotch : Pixels(delta);

	public static double MaxOffset(double extent, double viewport) => Math.Max(0, extent - viewport);

	public static bool CanScrollBack(double offset) => offset > EndTolerance;

	public static bool CanScrollForward(double offset, double extent, double viewport) => offset < MaxOffset(extent, viewport) - EndTolerance;

	/// <summary>The nearest card edge, kept inside the row's range; the row's end is its own edge.</summary>
	public static double Snap(double offset, double stride, double extent, double viewport)
	{
		var max = MaxOffset(extent, viewport);
		if (stride <= 0)
		{
			return Math.Clamp(offset, 0, max);
		}

		var snapped = Math.Round(offset / stride) * stride;
		return Math.Clamp(snapped, 0, max);
	}

	/// <summary>
	/// Where an arrow takes the row: one viewport minus one card (so the last card shown stays as the first of the next page: the
	/// peek), landing on a card edge; always at least one card, at most to the row's end.
	/// </summary>
	public static double Page(double offset, double viewport, double extent, double stride, bool forward)
	{
		var max = MaxOffset(extent, viewport);
		var step = Math.Max(stride, viewport - stride);
		if (stride > 0)
		{
			step = Math.Max(stride, Math.Floor(step / stride) * stride);
		}

		var target = forward ? offset + step : offset - step;
		if (forward && target >= max - EndTolerance)
		{
			return max;
		}

		if (!forward && target <= EndTolerance)
		{
			return 0;
		}

		return Snap(target, stride, extent, viewport);
	}

	/// <summary>The index a key moves a card to: Left/Right by one with no wrap, Home/End to the ends; null when the key is not the row's.</summary>
	public static int? KeyTarget(RowKey key, int current, int count)
	{
		if (count <= 0)
		{
			return null;
		}

		return key switch
		{
			RowKey.Left => Math.Max(0, current - 1),
			RowKey.Right => Math.Min(count - 1, current + 1),
			RowKey.Home => 0,
			RowKey.End => count - 1,
			_ => null,
		};
	}

	/// <summary>
	/// Whether the focus coming into a row goes to the row's remembered card (<paramref name="remembered"/>) instead of the
	/// card it is going to: only the keyboard's way in from outside the row (Tab, Shift+Tab), and not when the row moves it
	/// itself (<paramref name="directed"/>). A pointer lands where it pressed; programmatic focus (UI Automation's SetFocus,
	/// which Narrator uses, or the app naming a card) lands on the card it names. 1.2.0: programmatic focus went to the
	/// remembered card, so the menu key then opened that card's menu and "Remove from Continue Watching" took out another item.
	/// </summary>
	public static bool RedirectsToRemembered(bool keyboard, bool directed, int remembered, int count, bool comesFromOutside) =>
		keyboard && !directed && remembered >= 0 && remembered < count && comesFromOutside;

	/// <summary>The card whose centre is nearest to <paramref name="x"/> (keeps the column when moving to another row); -1 for none.</summary>
	public static int NearestByCenter(IReadOnlyList<double> centers, double x)
	{
		var best = -1;
		var bestDistance = double.MaxValue;
		for (var i = 0; i < centers.Count; i++)
		{
			var d = Math.Abs(centers[i] - x);
			if (d < bestDistance)
			{
				best = i;
				bestDistance = d;
			}
		}

		return best;
	}

	/// <summary>
	/// How far the page must move to show an element fully with <paramref name="margin"/> to spare at both edges of the viewport
	/// (WCAG 2.4.11: a focused card is not hidden by the title bar or the edge); 0 when it is already clear. Taller than the
	/// viewport: its top.
	/// </summary>
	public static double VerticalDeltaToReveal(double top, double bottom, double viewport, double margin)
	{
		if (bottom - top + 2 * margin >= viewport)
		{
			return top - margin;
		}

		if (top < margin)
		{
			return top - margin;
		}

		if (bottom > viewport - margin)
		{
			return bottom - (viewport - margin);
		}

		return 0;
	}

	/// <summary>
	/// Seaglass 1.1.4: where the focus goes when a card leaves the row (now <paramref name="count"/> cards): the focused one left,
	/// the card that took its place (the next), or the new last one; another one left, the focused card where it is now.
	/// </summary>
	public static int FocusAfterRemoval(int focused, int removed, int count)
	{
		if (count <= 0)
		{
			return -1;
		}

		var index = focused > removed ? focused - 1 : focused;
		return Math.Clamp(index, 0, count - 1);
	}

	/// <summary>
	/// A row's arrows are always there (hover-only arrows fail mouse users; the 1.1.4 layout audit, item 2). 1.2.0 (the button
	/// review, L7 and L8): at full strength, not half until the pointer or the keyboard is in the row; disabled at the ends.
	/// </summary>
	public static double ArrowOpacity(bool pointerOver, bool focusInside) => 1.0;

	/// <summary>The accessible name of a row's header arrow ("Scroll Recently Added left").</summary>
	public static string ArrowName(string header, bool forward) =>
		string.IsNullOrWhiteSpace(header) ? (forward ? "Scroll right" : "Scroll left") : $"Scroll {header} {(forward ? "right" : "left")}";

	public static string SeeAllName(string header) =>
		string.IsNullOrWhiteSpace(header) ? "See all" : $"See all {header}";
}

public enum RowKey
{
	Left,
	Right,
	Home,
	End,
}

/// <summary>fluentfin-mpv: how far a row scrolls to show an element that got focus (none when it is already in view).</summary>
internal static class RowScroll
{
	/// <returns>The horizontal offset that shows the element with the least scrolling, or null when it is in view.</returns>
	public static double? OffsetToShow(double offset, double viewport, double left, double width)
	{
		var right = left + width;
		if (left < offset)
		{
			return Math.Max(0, left);
		}

		if (right > offset + viewport)
		{
			// wider than the view: its start
			return width >= viewport ? left : right - viewport;
		}

		return null;
	}
}

/// <summary>
/// 1.1.4: the hold after Back to a grid or an A-Z jump (CardGrid.FocusAndHold): the card is focused again on each tick until
/// a card has the focus, for at most <paramref name="maxTicks"/> ticks, and never after the user did anything (the review,
/// item 7: a click in the search box right after Back was pulled back to the card).
/// </summary>
internal sealed class FocusHold(int maxTicks = 10)
{
	private int _ticks;

	public bool Done { get; private set; }

	/// <summary>A click, a key or the wheel: the hold ends.</summary>
	public void UserInput() => Done = true;

	/// <summary>A tick with the focus on card <paramref name="focusedIndex"/> (-1: not on a card of the grid): true to focus the card again.</summary>
	public bool Tick(int focusedIndex)
	{
		if (Done)
		{
			return false;
		}

		if (focusedIndex >= 0 || ++_ticks > maxTicks)
		{
			Done = true; // on the card, the user moved on in the grid, or the time is up
			return false;
		}

		return true;
	}
}
