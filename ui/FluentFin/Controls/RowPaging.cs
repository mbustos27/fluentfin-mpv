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

	/// <summary>
	/// The vertical wheel owns the page; a tilt wheel or Shift+wheel owns the row. Nixie 1.7.4 (the rows stream): so does the
	/// vertical wheel when the page cannot use it (<paramref name="pageCanUse"/> false: no page scroll, or the page at its
	/// end that way), so the wheel over a row always moves something.
	/// </summary>
	public static WheelTarget TargetOf(bool isHorizontalWheel, bool shift, bool pageCanUse = true) =>
		isHorizontalWheel || shift || !pageCanUse ? WheelTarget.Row : WheelTarget.Page;

	/// <summary>Nixie 1.7.4: whether a page at <paramref name="offset"/> (of <paramref name="scrollable"/>) can move by <paramref name="pixels"/>' way.</summary>
	public static bool PageCanScroll(double offset, double scrollable, double pixels) =>
		scrollable > EndTolerance && (pixels > 0 ? offset < scrollable - EndTolerance : pixels < 0 && offset > EndTolerance);

	/// <summary>How long the plain wheel must rest before the page's end hands it to the row under the pointer.</summary>
	public const long WheelHandOverMs = 300;

	/// <summary>
	/// Nixie 1.7.4 integration (the rows review's m3): whether a plain wheel notch that the page can no longer use still stays
	/// with it rather than going to the row. The page's glide may have reached its end while the page is still moving there
	/// (<paramref name="pageSettled"/> false), or the wheel moved the page under <see cref="WheelHandOverMs"/> ago
	/// (<paramref name="sinceThePageTookTheWheelMs"/>): a fast spin or a touchpad's coasting goes on past the end, and those
	/// notches slid the row sideways. The row takes the wheel after a pause. A page that cannot scroll at all
	/// (<paramref name="pageScrolls"/> false) never keeps it.
	/// </summary>
	public static bool WheelStaysWithPage(bool pageScrolls, bool pageSettled, long sinceThePageTookTheWheelMs) =>
		pageScrolls && (!pageSettled || sinceThePageTookTheWheelMs < WheelHandOverMs);

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
	/// Nixie 1.8 (the re-review): a card replaced where it is (Home's Made by Nixie row taking a new lineup's collection): the
	/// keyboard on it goes to the card in its place; on another card, it stays where it is (-1: nothing to move).
	/// </summary>
	public static int FocusAfterReplace(int focused, int replaced, int count) =>
		focused == replaced && replaced >= 0 && replaced < count ? replaced : -1;

	/// <summary>
	/// Nixie 1.8 (BH-180-M1): the remembered card's place after <paramref name="added"/> cards are put in at <paramref name="at"/>
	/// (a row taking a newer one's tiles in place): one later; or where it was (-1 stays: none remembered).
	/// </summary>
	public static int IndexAfterInsert(int remembered, int at, int added) => remembered >= 0 && at >= 0 && at <= remembered ? remembered + Math.Max(added, 0) : remembered;

	/// <summary>Nixie 1.8 (BH-180-M1): the remembered card's place after a card moves from <paramref name="from"/> to <paramref name="to"/>: itself moved, its new place; another one moved past it, one nearer.</summary>
	public static int IndexAfterMove(int remembered, int from, int to)
	{
		if (remembered < 0 || from < 0 || to < 0)
		{
			return remembered;
		}

		if (remembered == from)
		{
			return to;
		}

		if (from < remembered && to >= remembered)
		{
			return remembered - 1;
		}

		return from > remembered && to <= remembered ? remembered + 1 : remembered;
	}

	/// <summary>
	/// Nixie 1.7.4 (the rows stream): which of a row's arrows show. They are at the row's ends, over its cards, in every theme
	/// (they were in its header, which a theme could hide: Cinematic's season page showed E1 to E6 and no arrow), while the
	/// pointer is over the row (<paramref name="pointerOver"/>), each only while the row goes further its way.
	/// </summary>
	public static (bool Back, bool Forward) EdgeArrows(bool pointerOver, double offset, double extent, double viewport) =>
		pointerOver ? (CanScrollBack(offset), CanScrollForward(offset, extent, viewport)) : (false, false);

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

/// <summary>
/// Nixie 1.7.4 (the rows stream): a mouse drag along a row. Pressed on the row, it follows the pointer once the pointer has
/// moved <see cref="Threshold"/> pixels sideways (less is a click, which opens the card as before); a drag opens nothing.
/// </summary>
internal sealed class RowDrag
{
	/// <summary>Pixels sideways before a press is a drag (a click that wobbles a little is still a click).</summary>
	public const double Threshold = 10;

	private double _startX;
	private double _startOffset;

	public bool Pressed { get; private set; }

	public bool Dragging { get; private set; }

	public void Press(double x, double offset)
	{
		_startX = x;
		_startOffset = offset;
		Pressed = true;
		Dragging = false;
	}

	/// <summary>The row's offset for the pointer at <paramref name="x"/> (kept within 0 to <paramref name="max"/>); null while it is still a click, or nothing is pressed.</summary>
	public double? Move(double x, double max)
	{
		if (!Pressed)
		{
			return null;
		}

		var dx = x - _startX;
		if (!Dragging && Math.Abs(dx) < Threshold)
		{
			return null;
		}

		Dragging = true;
		return Math.Clamp(_startOffset - dx, 0, Math.Max(0, max));
	}

	/// <summary>Lets go: true when the press was a drag (its release must open nothing).</summary>
	public bool Release()
	{
		var was = Dragging;
		Pressed = Dragging = false;
		return was;
	}

	/// <summary>
	/// Nixie 1.7.4 integration (the rows review's m2): the pointer left the row. A press that is not a drag yet is forgotten:
	/// its release may come outside the row, where the row never hears it, and a left button held over the row later would
	/// start a drag from the old press (the row jumped). A drag goes on (the row holds the pointer).
	/// </summary>
	public void Left()
	{
		if (!Dragging)
		{
			Pressed = false;
		}
	}
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
