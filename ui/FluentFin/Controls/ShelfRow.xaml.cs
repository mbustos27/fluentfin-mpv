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

	/// <summary>Seaglass 1.1.4: the row whose card was last focused or pressed (the card the user left a page from).</summary>
	internal static ShelfRow? LastUsed { get; private set; }

	/// <summary>The rows on screen now.</summary>
	internal static IEnumerable<ShelfRow> Shown => s_rows.Where(r => r.IsShown);

	/// <summary>The item of the row's remembered card (the last focused or pressed), or null.</summary>
	internal object? LastItem => Repeater.ItemsSourceView is { } view && _lastIndex >= 0 && _lastIndex < view.Count ? view.GetAt(_lastIndex) : null;

	/// <summary>Seaglass 1.1.4: focuses the card of the first item that <paramref name="match"/> finds (Back to Home); false when none.</summary>
	internal bool FocusItem(Func<object, bool> match, FocusState state)
	{
		if (Repeater.ItemsSourceView is not { } view)
		{
			return false;
		}

		for (var i = 0; i < view.Count; i++)
		{
			if (match(view.GetAt(i)))
			{
				return FocusIndex(i, state);
			}
		}

		return false;
	}

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

		// Seaglass 1.1.4: the arrows are quiet until the pointer is over the row or the keyboard is in it
		RowRoot.PointerEntered += (_, _) => { _pointerOver = true; UpdateQuiet(); };
		RowRoot.PointerExited += (_, _) => { _pointerOver = false; UpdateQuiet(); };
		RowRoot.PointerCanceled += (_, _) => { _pointerOver = false; UpdateQuiet(); };
		GotFocus += (_, _) => { _focusInside = true; UpdateQuiet(); };
		LostFocus += (_, _) => { _focusInside = false; UpdateQuiet(); };
		UpdateQuiet();

		Scroller.ViewChanged += (_, _) => UpdateArrows();
		Scroller.SizeChanged += (_, _) =>
		{
			List.MinWidth = Scroller.ViewportWidth;
			UpdateArrows();
		};
		List.SizeChanged += (_, _) => UpdateArrows();

		// the wheel: on the row's content, which the event reaches before the scroll viewer's own handling; handled events too
		List.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(List_PointerWheelChanged), true);
		// on the row's content, below the scroll viewer: its own Home/End/arrow handling would take the keys first
		List.AddHandler(KeyDownEvent, new KeyEventHandler(Scroller_KeyDown), false);
		Scroller.GettingFocus += Scroller_GettingFocus;
		Scroller.GotFocus += Scroller_GotFocus;
		// Seaglass 1.1.4: a card clicked (which does not take the focus) is the row's remembered card too: Back comes back to it
		List.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
		{
			if (IndexOf(e.OriginalSource as DependencyObject) is >= 0 and var index)
			{
				_lastIndex = index;
				LastUsed = this;
			}
		}), true);

		Repeater.ElementPrepared += (_, e) =>
		{
			GiveItem(e.Element, e.Index);
			SetPosition(e.Element, e.Index);
		};
		Repeater.ElementIndexChanged += (_, e) => SetPosition(e.Element, e.NewIndex);
		Repeater.Loaded += (_, _) => UpdateNames();
		RegisterPropertyChangedCallback(HeaderProperty, (_, _) => UpdateNames());
		RegisterPropertyChangedCallback(HasSeeAllProperty, (_, _) => UpdateNames());
		RegisterPropertyChangedCallback(ItemsSourceProperty, (_, _) => { _lastIndex = -1; WatchSource(); DispatcherQueue.TryEnqueue(() => { UpdateNames(); UpdateArrows(); }); });
	}

	private System.Collections.Specialized.INotifyCollectionChanged? _source;

	private void WatchSource()
	{
		if (_source is not null)
		{
			_source.CollectionChanged -= Source_CollectionChanged;
		}

		_source = ItemsSource as System.Collections.Specialized.INotifyCollectionChanged;
		if (_source is not null)
		{
			_source.CollectionChanged += Source_CollectionChanged;
		}
	}

	/// <summary>
	/// Seaglass 1.1.4: the card the user was on leaves the row (Remove from Continue Watching, from its menu): once the menu is
	/// closed (it gives the focus back to the card it came from, which is gone), the focus goes to the card that took its place
	/// (or the new last one), not out of the window. A row left empty leaves it to the page.
	/// </summary>
	private void Source_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
	{
		if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Remove || XamlRoot is not { Content: { } content } root)
		{
			return;
		}

		// the card may be off the repeater's books already (it handles the change first): inside it is enough; or the
		// focus is in the menu of the row's remembered card (a menu is a popup, outside the page's tree)
		var focused = FocusManager.GetFocusedElement(root) as DependencyObject;
		var inRow = IsInside(focused, Repeater);
		var inMenu = focused is not null && !IsInside(focused, content) && e.OldStartingIndex == _lastIndex && ReferenceEquals(LastUsed, this);
		if (!inRow && !inMenu)
		{
			return;
		}

		var index = _lastIndex >= 0 ? _lastIndex : e.OldStartingIndex;
		var removed = e.OldStartingIndex;
		var state = inRow && focused is Control { FocusState: FocusState.Keyboard } ? FocusState.Keyboard : FocusState.Programmatic;
		var tries = 0;
		var timer = DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(60);
		timer.Tick += (t, _) =>
		{
			if (XamlRoot is not { Content: { } now } xamlRoot || Count == 0)
			{
				t.Stop();
				return;
			}

			if (VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Any(p => p.Child is MenuFlyoutPresenter or FlyoutPresenter) && ++tries < 30)
			{
				return; // the menu is still closing
			}

			t.Stop();
			var at = FocusManager.GetFocusedElement(xamlRoot) as DependencyObject;
			if (at is null || IsInside(at, Repeater) || !IsInside(at, now))
			{
				Repeater.UpdateLayout();
				FocusIndex(RowPaging.FocusAfterRemoval(index, removed, Count), state);
			}
		};
		timer.Start();
	}

	/// <summary>The cards in the row (what the repeater has).</summary>
	internal int Count => Repeater.ItemsSourceView?.Count ?? 0;

	internal bool IsShown => IsLoaded && ActualHeight > 0 && Count > 0 && Visibility == Visibility.Visible;

	/// <summary>
	/// Nixie 1.3.0: the card is its item's DataContext. ItemsRepeater left it unset for a template whose bindings are
	/// {Binding} (the layout themes' PosterTemplate and StillTemplate), whose cards then came out empty; x:Bind templates
	/// take their item the same way.
	/// </summary>
	private void GiveItem(UIElement element, int index)
	{
		if (element is FrameworkElement card && Repeater.ItemsSourceView is { } view && index >= 0 && index < view.Count
			&& NeedsItem(card.DataContext, view.GetAt(index)) is { } item)
		{
			card.DataContext = item;
		}
	}

	/// <summary>The item a card must take as its DataContext, or null when it already has it (no second binding pass).</summary>
	internal static object? NeedsItem(object? current, object? item) => item is null || ReferenceEquals(current, item) ? null : item;

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
		// 1.4.0: See all is one link at the header's right (the Link role), when the row has one; the title is always the
		// heading, and its visibility is left alone here (a theme may draw its own title in its place: Drive-in's marquee)
		SeeAllButton.Visibility = HasSeeAll ? Visibility.Visible : Visibility.Collapsed;
		for (var i = 0; i < Count; i++)
		{
			if (Repeater.TryGetElement(i) is { } element)
			{
				SetPosition(element, i);
			}
		}
	}

	private bool _pointerOver;
	private bool _focusInside;

	private void UpdateQuiet() => ScrollLeftBtn.Opacity = ScrollRightBtn.Opacity = RowPaging.ArrowOpacity(_pointerOver, _focusInside);

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

	private static bool IsInside(DependencyObject? element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Seaglass 1.1.4: the keyboard to the row (Home's N and C): its remembered card, else its first; false when it shows none.</summary>
	internal bool FocusRow() => IsShown && FocusIndex(Math.Clamp(_lastIndex, 0, Count - 1));

	/// <summary>Focuses card <paramref name="index"/> (made if the row has not drawn it yet); the focus scrolls it into view.</summary>
	internal bool FocusIndex(int index, FocusState state = FocusState.Keyboard)
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
			return element is Control control && control.Focus(state);
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

		// no row above: up to the hero, never to the row's own header (See all and the arrows are above its cards; the keys
		// leave them alone)
		return down || CardGrid.MoveFocusOut(from, FocusNavigationDirection.Up, exclude: this);
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
		// only the keyboard's focus coming in from outside the row is redirected; one moving along it is the row's own keys,
		// and a programmatic one (UI Automation, Narrator) keeps the card it names (RowPaging.RedirectsToRemembered)
		var fromOutside = IndexOf(e.NewFocusedElement) >= 0 && IndexOf(e.OldFocusedElement) < 0;
		if (!RowPaging.RedirectsToRemembered(e.FocusState == FocusState.Keyboard, _directed, _lastIndex, Count, fromOutside))
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
		LastUsed = this;
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
	internal sealed class Pending
	{
		public double Target;
		public bool Moving;
		public bool Hooked;

		/// <summary>
		/// Aims at <paramref name="offset"/> (clamped). False, and not moving, when that is where the view already is: a
		/// ChangeView that changes nothing raises no ViewChanged, so a Moving left true would feed the next By a stale target.
		/// </summary>
		public bool Begin(double offset, double max, double current)
		{
			Target = Math.Clamp(offset, 0, Math.Max(0, max));
			Moving = Math.Abs(Target - current) >= 0.5;
			return Moving;
		}
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
		var current = horizontal ? viewer.HorizontalOffset : viewer.VerticalOffset;
		if (!state.Begin(offset, max, current))
		{
			return;
		}

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
			// A press (the wheel is not one) on the content or a scrollbar thumb takes over from the animation: the next By
			// starts from where the view is. Handled events too: the scrollbars handle theirs.
			viewer.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => state.Moving = false), true);
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
