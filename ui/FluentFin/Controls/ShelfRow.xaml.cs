using CommunityToolkit.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass scrolling: the one row control (see ShelfRow.xaml). The vertical wheel over it scrolls the page; the tilt wheel and
/// Shift+wheel scroll the row; the header arrows page it by a viewport minus a card, landing on a card edge; the keys move the
/// focus card by card (<see cref="RowPaging"/>), between rows and to a row's ends.
/// </summary>
public sealed partial class ShelfRow : UserControl
{
	/// <summary>The row title, also the heading, the list's name and what the arrows' and "See all"'s names are made of.</summary>
	[GeneratedDependencyProperty(DefaultValue = "")]
	public partial string Header { get; set; }

	[GeneratedDependencyProperty]
	public partial object? ItemsSource { get; set; }

	[GeneratedDependencyProperty]
	public partial DataTemplate? ItemTemplate { get; set; }

	/// <summary>Whether the header has a "See all" (raised as <see cref="SeeAll"/>).</summary>
	[GeneratedDependencyProperty(DefaultValue = false)]
	public partial bool HasSeeAll { get; set; }

	public event EventHandler? SeeAll;

	/// <summary>Every row on screen, in no order: how Up and Down find the neighbouring row.</summary>
	private static readonly List<ShelfRow> s_rows = [];

	private int _lastIndex = -1;
	private bool _directed;
	private ScrollViewer? _pageScroller;

	public ShelfRow()
	{
		InitializeComponent();

		Loaded += (_, _) =>
		{
			if (!s_rows.Contains(this))
			{
				s_rows.Add(this);
			}

			UpdateNames();
			UpdateArrows();
		};
		Unloaded += (_, _) => s_rows.Remove(this);

		Scroller.ViewChanged += (_, _) => UpdateArrows();
		Scroller.SizeChanged += (_, _) =>
		{
			List.MinWidth = Scroller.ViewportWidth;
			UpdateArrows();
		};
		List.SizeChanged += (_, _) => UpdateArrows();

		// the wheel: on the row's content, which the event reaches before the scroll viewer's own handling; handled events too
		List.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(List_PointerWheelChanged), true);
		Scroller.AddHandler(KeyDownEvent, new KeyEventHandler(Scroller_KeyDown), false);
		Scroller.GettingFocus += Scroller_GettingFocus;
		Scroller.GotFocus += Scroller_GotFocus;

		Repeater.ElementPrepared += (_, e) => SetPosition(e.Element, e.Index);
		Repeater.ElementIndexChanged += (_, e) => SetPosition(e.Element, e.NewIndex);
		Repeater.Loaded += (_, _) => UpdateNames();
		RegisterPropertyChangedCallback(HeaderProperty, (_, _) => UpdateNames());
		RegisterPropertyChangedCallback(HasSeeAllProperty, (_, _) => UpdateNames());
		RegisterPropertyChangedCallback(ItemsSourceProperty, (_, _) => { _lastIndex = -1; DispatcherQueue.TryEnqueue(() => { UpdateNames(); UpdateArrows(); }); });
	}

	/// <summary>The cards in the row (what the repeater has).</summary>
	internal int Count => Repeater.ItemsSourceView?.Count ?? 0;

	internal bool IsShown => IsLoaded && ActualHeight > 0 && Count > 0 && Visibility == Visibility.Visible;

	private void SetPosition(UIElement element, int index)
	{
		AutomationProperties.SetPositionInSet(element, index + 1);
		AutomationProperties.SetSizeOfSet(element, Count);
	}

	private void UpdateNames()
	{
		AutomationProperties.SetName(List, Header);
		ToolTipService.SetToolTip(ScrollLeftBtn, "Scroll left (Shift+wheel, tilt wheel or Left key)");
		ToolTipService.SetToolTip(ScrollRightBtn, "Scroll right (Shift+wheel, tilt wheel or Right key)");
		AutomationProperties.SetName(ScrollLeftBtn, RowPaging.ArrowName(Header, forward: false));
		AutomationProperties.SetName(ScrollRightBtn, RowPaging.ArrowName(Header, forward: true));
		AutomationProperties.SetName(SeeAllButton, RowPaging.SeeAllName(Header));
		SeeAllButton.Visibility = HasSeeAll ? Visibility.Visible : Visibility.Collapsed;
		for (var i = 0; i < Count; i++)
		{
			if (Repeater.TryGetElement(i) is { } element)
			{
				SetPosition(element, i);
			}
		}
	}

	/// <summary>The arrows show whenever the row overflows, and are disabled (not hidden) at the ends.</summary>
	private void UpdateArrows()
	{
		var overflow = Scroller.ScrollableWidth > RowPaging.EndTolerance;
		var visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
		ScrollLeftBtn.Visibility = ScrollRightBtn.Visibility = visibility;
		ScrollLeftBtn.IsEnabled = RowPaging.CanScrollBack(Scroller.HorizontalOffset);
		ScrollRightBtn.IsEnabled = RowPaging.CanScrollForward(Scroller.HorizontalOffset, Scroller.ExtentWidth, Scroller.ViewportWidth);
	}

	private void SeeAllButton_Click(object sender, RoutedEventArgs e) => SeeAll?.Invoke(this, EventArgs.Empty);

	private void ScrollLeftBtn_Click(object sender, RoutedEventArgs e) => PageRow(forward: false);

	private void ScrollRightBtn_Click(object sender, RoutedEventArgs e) => PageRow(forward: true);

	private double Stride => Repeater.TryGetElement(0) is FrameworkElement first ? first.ActualWidth : 0;

	private void PageRow(bool forward) =>
		RowScrollTo(RowPaging.Page(Scroller.HorizontalOffset, Scroller.ViewportWidth, Scroller.ExtentWidth, Stride, forward));

	private void RowScrollTo(double offset) => SmoothScroll.To(Scroller, horizontal: true, offset);

	// ---- the wheel: one axis per input --------------------------------------------------------------------------------

	private void List_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
		{
			return;
		}

		var properties = e.GetCurrentPoint(Scroller).Properties;
		var delta = properties.MouseWheelDelta;
		if (delta == 0)
		{
			return;
		}

		var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
		if (RowPaging.TargetOf(properties.IsHorizontalMouseWheel, shift) == WheelTarget.Row)
		{
			if (Scroller.ScrollableWidth > 0)
			{
				SmoothScroll.By(Scroller, horizontal: true, RowPaging.RowPixels(delta, properties.IsHorizontalMouseWheel));
			}
		}
		else if (PageScroller is { } page)
		{
			SmoothScroll.By(page, horizontal: false, RowPaging.Pixels(delta));
		}
		else
		{
			return;
		}

		// the row's own scroll viewer never sees it: it would take a vertical wheel for the row (#10172)
		e.Handled = true;
	}

	/// <summary>The scroll viewer that scrolls the page the row is on: the nearest one above that scrolls vertically.</summary>
	private ScrollViewer? PageScroller
	{
		get
		{
			if (_pageScroller is { } known && known.XamlRoot is not null)
			{
				return known;
			}

			for (var parent = VisualTreeHelper.GetParent(this); parent is not null; parent = VisualTreeHelper.GetParent(parent))
			{
				if (parent is ScrollViewer { VerticalScrollMode: ScrollMode.Enabled or ScrollMode.Auto } viewer && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
				{
					return _pageScroller = viewer;
				}
			}

			return null;
		}
	}

	// ---- the keyboard: TV-style --------------------------------------------------------------------------------------

	private int IndexOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(VisualTreeHelper.GetParent(node), Repeater))
			{
				return Repeater.GetElementIndex((UIElement)node);
			}

			if (ReferenceEquals(node, Repeater))
			{
				return -1;
			}
		}

		return -1;
	}

	/// <summary>Focuses card <paramref name="index"/> (made if the row has not drawn it yet); the focus scrolls it into view.</summary>
	internal bool FocusIndex(int index)
	{
		if (index < 0 || index >= Count)
		{
			return false;
		}

		var element = Repeater.GetOrCreateElement(index);
		Repeater.UpdateLayout();
		_directed = true;
		try
		{
			return element is Control control && control.Focus(FocusState.Keyboard);
		}
		finally
		{
			_directed = false;
		}
	}

	private void Scroller_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		var current = IndexOf(e.OriginalSource as DependencyObject);
		if (current < 0)
		{
			return;
		}

		var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		RowKey? key = e.Key switch
		{
			VirtualKey.Left or VirtualKey.GamepadDPadLeft => RowKey.Left,
			VirtualKey.Right or VirtualKey.GamepadDPadRight => RowKey.Right,
			VirtualKey.Home => RowKey.Home,
			VirtualKey.End => RowKey.End,
			_ => null,
		};
		if (key is { } rowKey && !ctrl)
		{
			if (RowPaging.KeyTarget(rowKey, current, Count) is { } target && target != current)
			{
				FocusIndex(target);
			}

			e.Handled = true; // never leaves the row: no wrap, and the page does not take Left/Right for something else
			return;
		}

		switch (e.Key)
		{
			case VirtualKey.Up or VirtualKey.GamepadDPadUp:
				e.Handled = MoveVertical(down: false, e.OriginalSource as FrameworkElement);
				break;
			case VirtualKey.Down or VirtualKey.GamepadDPadDown:
				e.Handled = MoveVertical(down: true, e.OriginalSource as FrameworkElement);
				break;
			case VirtualKey.PageDown or VirtualKey.PageUp when PageScroller is { } page:
				SmoothScroll.By(page, horizontal: false, (e.Key == VirtualKey.PageDown ? 1 : -1) * page.ViewportHeight * 0.85);
				e.Handled = true;
				break;
			case VirtualKey.GamepadLeftShoulder:
				PageRow(forward: false);
				e.Handled = true;
				break;
			case VirtualKey.GamepadRightShoulder:
				PageRow(forward: true);
				e.Handled = true;
				break;
		}
	}

	/// <summary>Up or Down: the card nearest in the same column of the neighbouring row; above the first row, whatever is above (the hero).</summary>
	private bool MoveVertical(bool down, FrameworkElement? from)
	{
		if (from is null || XamlRoot?.Content is not UIElement root)
		{
			return true;
		}

		var centerX = from.TransformToVisual(root).TransformPoint(new Point(from.ActualWidth / 2, 0)).X;
		for (var attempt = 0; attempt < 4; attempt++)
		{
			if (NeighbourOf(down, root) is { } row)
			{
				return row.FocusNearest(centerX, root);
			}

			// the next row of the page may not be drawn yet (the page draws what is near the view): bring it near and look again
			if (!down || PageScroller is not { } page || page.VerticalOffset >= page.ScrollableHeight - 1)
			{
				break;
			}

			page.ChangeView(null, Math.Min(page.ScrollableHeight, page.VerticalOffset + page.ViewportHeight * 0.5), null, disableAnimation: true);
			page.UpdateLayout();
		}

		// no row above: let the focus go up to the hero and the title bar
		return down || FocusManager.TryMoveFocus(FocusNavigationDirection.Up);
	}

	private ShelfRow? NeighbourOf(bool down, UIElement root)
	{
		var page = PageScroller;
		var mine = TopOf(this, root);
		ShelfRow? best = null;
		var bestTop = down ? double.MaxValue : double.MinValue;
		foreach (var row in s_rows)
		{
			if (ReferenceEquals(row, this) || !row.IsShown || !ReferenceEquals(row.PageScroller, page))
			{
				continue;
			}

			var top = TopOf(row, root);
			if (down && top > mine + 1 && top < bestTop)
			{
				best = row;
				bestTop = top;
			}
			else if (!down && top < mine - 1 && top > bestTop)
			{
				best = row;
				bestTop = top;
			}
		}

		return best;
	}

	private static double TopOf(UIElement element, UIElement root) => element.TransformToVisual(root).TransformPoint(new Point(0, 0)).Y;

	/// <summary>Focuses the card whose centre is nearest to <paramref name="centerX"/> (window coordinates) among the ones drawn.</summary>
	private bool FocusNearest(double centerX, UIElement root)
	{
		var centers = new List<double>();
		var indexes = new List<int>();
		for (var i = 0; i < Count; i++)
		{
			if (Repeater.TryGetElement(i) is FrameworkElement element && element.ActualWidth > 0)
			{
				var left = element.TransformToVisual(root).TransformPoint(new Point(0, 0)).X;
				var middle = left + element.ActualWidth / 2;
				// only the cards that show (the row's edges clip the others)
				var viewLeft = Scroller.TransformToVisual(root).TransformPoint(new Point(0, 0)).X;
				if (middle >= viewLeft && middle <= viewLeft + Scroller.ViewportWidth)
				{
					centers.Add(middle);
					indexes.Add(i);
				}
			}
		}

		var pick = RowPaging.NearestByCenter(centers, centerX);
		return pick >= 0 ? FocusIndex(indexes[pick]) || true : FocusIndex(Math.Max(0, _lastIndex)) || true;
	}

	/// <summary>The row remembers the card that had the focus: coming back to the row (Tab, Shift+Tab) lands on it.</summary>
	private void Scroller_GettingFocus(UIElement sender, GettingFocusEventArgs e)
	{
		if (_directed || e.FocusState == FocusState.Pointer || _lastIndex < 0 || _lastIndex >= Count)
		{
			return;
		}

		// only the focus coming in from outside the row is redirected; one moving along it is the row's own keys
		if (IndexOf(e.NewFocusedElement) < 0 || IndexOf(e.OldFocusedElement) >= 0)
		{
			return;
		}

		if (Repeater.TryGetElement(_lastIndex) is Control last && !ReferenceEquals(last, e.NewFocusedElement) && last.IsTabStop)
		{
			e.TrySetNewFocusedElement(last);
		}
	}

	/// <summary>A card that gets keyboard focus is scrolled into view along the row and down the page, clear of the edges (WCAG 2.4.11).</summary>
	private void Scroller_GotFocus(object sender, RoutedEventArgs e)
	{
		if (e.OriginalSource is not FrameworkElement focused || ReferenceEquals(focused, Scroller))
		{
			return;
		}

		var index = IndexOf(focused);
		if (index < 0)
		{
			return;
		}

		_lastIndex = index;
		if (Repeater.TryGetElement(index) is not FrameworkElement card || card.ActualWidth <= 0)
		{
			return;
		}

		if (focused is Control { FocusState: FocusState.Pointer })
		{
			return; // a click: nothing moves under the pointer
		}

		var left = card.TransformToVisual(List).TransformPoint(new Point(0, 0)).X;
		if (RowScroll.OffsetToShow(Scroller.HorizontalOffset, Scroller.ViewportWidth, left, card.ActualWidth) is { } offset)
		{
			// to a card edge when that still shows the card, else just far enough
			var snapped = RowPaging.Snap(offset, Stride, Scroller.ExtentWidth, Scroller.ViewportWidth);
			var shows = snapped <= left && snapped + Scroller.ViewportWidth >= left + card.ActualWidth;
			RowScrollTo(shows ? snapped : offset);
		}

		RevealOnPage();
	}

	private void RevealOnPage()
	{
		if (PageScroller is not { } page)
		{
			return;
		}

		var top = this.TransformToVisual(page).TransformPoint(new Point(0, 0)).Y;
		var delta = RowPaging.VerticalDeltaToReveal(top, top + ActualHeight, page.ViewportHeight, 24);
		if (Math.Abs(delta) >= 1)
		{
			SmoothScroll.To(page, horizontal: false, Math.Clamp(page.VerticalOffset + delta, 0, page.ScrollableHeight));
		}
	}

}

/// <summary>Seaglass scrolling: moves a scroll viewer by an animation unless motion is off, adding up wheel notches that arrive while it runs.</summary>
internal static class SmoothScroll
{
	private sealed class Pending
	{
		public double Target;
		public bool Moving;
		public bool Hooked;
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Pending> s_vertical = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, Pending> s_horizontal = new();

	public static void By(ScrollViewer viewer, bool horizontal, double pixels)
	{
		var state = StateOf(viewer, horizontal);
		var current = state.Moving ? state.Target : (horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset);
		To(viewer, horizontal, current + pixels);
	}

	public static void To(ScrollViewer viewer, bool horizontal, double offset)
	{
		var state = StateOf(viewer, horizontal);
		var max = horizontal ? viewer.ScrollableWidth : viewer.ScrollableHeight;
		state.Target = Math.Clamp(offset, 0, Math.Max(0, max));
		state.Moving = true;
		if (horizontal)
		{
			viewer.ChangeView(state.Target, null, null, !SafeMotion.Allowed);
		}
		else
		{
			viewer.ChangeView(null, state.Target, null, !SafeMotion.Allowed);
		}
	}

	private static Pending StateOf(ScrollViewer viewer, bool horizontal)
	{
		var table = horizontal ? s_horizontal : s_vertical;
		var state = table.GetOrCreateValue(viewer);
		if (!state.Hooked)
		{
			state.Hooked = true;
			viewer.ViewChanged += (_, e) =>
			{
				if (!e.IsIntermediate)
				{
					state.Moving = false;
				}
			};
		}

		return state;
	}
}
