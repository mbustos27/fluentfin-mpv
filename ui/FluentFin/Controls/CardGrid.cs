using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass scrolling: the rows' keyboard and Narrator model for a grid of cards (the Library, a row's "See all"): one Tab stop
/// (the <see cref="RowList"/> around the repeater has TabFocusNavigation Once), arrows in both directions, Home/End and
/// PageUp/PageDown (<see cref="GridPaging"/>), the grid's last-focused card coming back on Tab, the focused card revealed
/// clear of the scroller's edges, Narrator's "n of m" on each card, and the wheel over the rest of the page scrolling the grid.
/// Attached to a page's ScrollView, RowList and ItemsRepeater (of ItemContainers holding a card, or cards).
/// </summary>
internal sealed class CardGrid
{
	/// <summary>Room left around a focused card when it is scrolled into view (as a row's, RowPaging.VerticalDeltaToReveal).</summary>
	internal const double RevealMargin = 24;

	private readonly ScrollView _scroller;
	private readonly RowList _list;
	private readonly ItemsRepeater _repeater;
	private ItemsSourceView? _source;
	private object? _lastItem;
	private bool _directed;
	private readonly PendingFocus _pending = new();
	private int _firstIndex;
	private int _total;

	public CardGrid(ScrollView scroller, RowList list, ItemsRepeater repeater)
	{
		_scroller = scroller;
		_list = list;
		_repeater = repeater;

		// before the card's own KeyDown, which moves only among the cards drawn (XY) and knows no Home/End
		list.PreviewKeyDown += List_PreviewKeyDown;
		list.GettingFocus += List_GettingFocus;
		// the focus leaving the grid (Tab, a click elsewhere) before a far card's scroll ends: the card does not take it back
		list.LosingFocus += (_, _) =>
		{
			if (!_directed)
			{
				_pending.Cancel();
			}
		};
		list.GotFocus += List_GotFocus;
		// Seaglass 1.1.4: a card clicked (which does not take the focus) is the card the user was on too: Back from its page
		// comes back to it. Handled events too: the card handles its own taps.
		list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(List_PointerPressed), true);
		list.BringIntoViewRequested += List_BringIntoViewRequested;
		list.CharacterReceived += List_CharacterReceived;
		scroller.ScrollCompleted += Scroller_ScrollCompleted;
		repeater.ElementPrepared += (_, e) => SetPlace(e.Element, e.Index);
		repeater.ElementIndexChanged += (_, e) => SetPlace(e.Element, e.NewIndex);
		repeater.RegisterPropertyChangedCallback(ItemsRepeater.ItemsSourceProperty, (_, _) => WatchSource());
		WatchSource();
	}

	/// <summary>Nixie 1.4.0: how many cards a line holds now (from the lines drawn; 1 before two lines are drawn).</summary>
	internal int ColumnCount => Math.Max(1, GridPaging.Columns([.. Realized().Select(r => (double)r.Element.ActualOffset.Y)]));

	/// <summary>The cards in the grid now.</summary>
	internal int Count => _repeater.ItemsSourceView?.Count ?? 0;

	/// <summary>Seaglass 1.1.4: the item of the card that last had the focus (the one the user left the grid from), or null.</summary>
	internal object? LastItem => _lastItem;

	// Seaglass 1.1.4: how the card asked for gets the focus (a jump by a click: the program's, no ring until the keyboard is used)
	private FocusState _focusState = FocusState.Keyboard;

	/// <summary>Where the grid's cards are in a larger set (a library page): the first card's place and the set's size.</summary>
	public void SetPlace(int firstIndex, int total)
	{
		_firstIndex = firstIndex;
		_total = total;
		UpdatePlaces();
	}

	/// <summary>The wheel over <paramref name="area"/> outside the grid (a title, a sort bar, a pager) scrolls the grid: it is the page.</summary>
	public void ForwardWheel(UIElement area) =>
		area.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Area_PointerWheelChanged), false);

	private void Area_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch || IsInside(e.OriginalSource as DependencyObject, _scroller))
		{
			return; // the grid's own scroller has it
		}

		var properties = e.GetCurrentPoint(_scroller).Properties;
		if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0 || _scroller.ScrollableHeight <= 0)
		{
			return;
		}

		var animation = SafeMotion.Allowed ? ScrollingAnimationMode.Auto : ScrollingAnimationMode.Disabled;
		_scroller.ScrollBy(0, RowPaging.Pixels(properties.MouseWheelDelta), new ScrollingScrollOptions(animation, ScrollingSnapPointsMode.Ignore));
		e.Handled = true;
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

	private void WatchSource()
	{
		if (_source is not null)
		{
			_source.CollectionChanged -= Source_CollectionChanged;
		}

		_source = _repeater.ItemsSourceView;
		if (_source is not null)
		{
			_source.CollectionChanged += Source_CollectionChanged;
		}

		UpdatePlaces();
	}

	private void Source_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
		_list.DispatcherQueue.TryEnqueue(UpdatePlaces);

	// ---- Narrator ----------------------------------------------------------------------------------------------------

	/// <summary>The card in an element the repeater made (an ItemContainer around it, or the card itself).</summary>
	private static UIElement CardOf(UIElement element) => element is ItemContainer { Child: { } child } ? child : element;

	private void SetPlace(UIElement element, int index)
	{
		var (position, size) = GridPaging.Place(index, _firstIndex, _total, Count);
		var card = CardOf(element);
		AutomationProperties.SetPositionInSet(card, position);
		AutomationProperties.SetSizeOfSet(card, size);
	}

	private void UpdatePlaces()
	{
		foreach (var (element, index) in Realized())
		{
			SetPlace(element, index);
		}
	}

	/// <summary>The elements the repeater shows now, with their indexes (a recycled one has none).</summary>
	private IEnumerable<(UIElement Element, int Index)> Realized()
	{
		var n = VisualTreeHelper.GetChildrenCount(_repeater);
		for (var i = 0; i < n; i++)
		{
			if (VisualTreeHelper.GetChild(_repeater, i) is UIElement element && _repeater.GetElementIndex(element) is var index && index >= 0)
			{
				yield return (element, index);
			}
		}
	}

	// ---- the keyboard ------------------------------------------------------------------------------------------------

	/// <summary>The index of the card <paramref name="element"/> is in, or -1.</summary>
	internal int IndexOfElement(DependencyObject? element) => IndexOf(element);

	private int IndexOf(DependencyObject? element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			var parent = VisualTreeHelper.GetParent(node);
			if (ReferenceEquals(parent, _repeater))
			{
				return _repeater.GetElementIndex((UIElement)node);
			}

			if (ReferenceEquals(node, _list))
			{
				return -1;
			}
		}

		return -1;
	}

	private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

	private void List_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		var current = IndexOf(e.OriginalSource as DependencyObject);
		if (current < 0 || IsDown(VirtualKey.Menu))
		{
			return; // not on a card; Alt+Left is Back
		}

		var ctrl = IsDown(VirtualKey.Control);
		GridKey? key = e.Key switch
		{
			VirtualKey.Left or VirtualKey.GamepadDPadLeft when !ctrl => GridKey.Left,
			VirtualKey.Right or VirtualKey.GamepadDPadRight when !ctrl => GridKey.Right,
			VirtualKey.Up or VirtualKey.GamepadDPadUp when !ctrl => GridKey.Up,
			VirtualKey.Down or VirtualKey.GamepadDPadDown when !ctrl => GridKey.Down,
			VirtualKey.Home => GridKey.Home,       // Ctrl+Home too
			VirtualKey.End => GridKey.End,
			VirtualKey.PageUp or VirtualKey.GamepadLeftShoulder => GridKey.PageUp,
			VirtualKey.PageDown or VirtualKey.GamepadRightShoulder => GridKey.PageDown,
			_ => null,
		};
		if (key is not { } gridKey)
		{
			return;
		}

		// a key before the last one's far card was reached replaces it (End then Home at once ends on the first card)
		_pending.Cancel();
		var tops = Realized().Select(r => (double)r.Element.ActualOffset.Y).ToList();
		var columns = GridPaging.Columns(tops);
		var lines = GridPaging.LinesPerPage(_scroller.ViewportHeight, GridPaging.LineStride(tops));
		if (GridPaging.KeyTarget(gridKey, current, Count, columns, lines) is { } target)
		{
			if (target != current)
			{
				FocusIndex(target);
			}
		}
		else if (e.OriginalSource is UIElement from)
		{
			// past the first or the last line: what is above (sort, filter) or below (the pager) in the page; else nowhere (the
			// See all grid has nothing there). Looked for across the grid's whole width: from a card on the left, straight up
			// is the page's title
			MoveFocusOut(from, gridKey == GridKey.Up ? FocusNavigationDirection.Up : FocusNavigationDirection.Down, columns: _scroller, line: from as FrameworkElement);
		}

		e.Handled = true; // the grid's scroller does not also scroll by a line
	}

	/// <summary>
	/// Moves the keyboard focus from <paramref name="from"/> to the nearest element in <paramref name="direction"/> (XY), within
	/// the page <paramref name="from"/> is on: never the title bar or the navigation (with nothing in the page that way, the
	/// See all grid's Up went to the search box). Without a search root WinUI 3's TryMoveFocus finds nothing for the arrows.
	/// </summary>
	/// <param name="columns">Searched straight up or down from this element's width (a grid's whole width), not only the focused one's.</param>
	/// <param name="line">Searched from this element's top or bottom edge, not the focused one's.</param>
	/// <param name="exclude">Nothing in this element is a candidate (a row's own header, above its cards).</param>
	internal static bool MoveFocusOut(UIElement from, FocusNavigationDirection direction, FrameworkElement? columns = null, FrameworkElement? line = null, FrameworkElement? exclude = null)
	{
		if (from.XamlRoot?.Content is not UIElement root)
		{
			return false;
		}

		var options = new FindNextElementOptions { SearchRoot = PageOf(from) ?? root };
		if ((columns ?? from as FrameworkElement) is { } across && (line ?? from as FrameworkElement) is { } edge)
		{
			var wide = BoundsIn(across, root);
			var tall = BoundsIn(edge, root);
			options.HintRect = new Rect(wide.X, tall.Y, wide.Width, tall.Height);
		}

		if (exclude is not null)
		{
			options.ExclusionRect = BoundsIn(exclude, root);
		}

		return FocusManager.TryMoveFocus(direction, options);
	}

	private static Page? PageOf(DependencyObject element)
	{
		for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is Page page)
			{
				return page;
			}
		}

		return null;
	}

	private static Rect BoundsIn(FrameworkElement element, UIElement root) =>
		element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

	/// <summary>
	/// Focuses card <paramref name="index"/>; the focus scrolls it into view. One the grid has not drawn (far down or up a
	/// page: End, Home, a remembered card) is scrolled to first and focused when the grid gets there: a card made far from
	/// the view (GetOrCreateElement) was given to another index by the time it took the focus (Home from the end of a page
	/// focused the 8th card).
	/// </summary>
	internal bool FocusIndex(int index, FocusState state = FocusState.Keyboard)
	{
		if (index < 0 || index >= Count)
		{
			return false;
		}

		_focusState = state;

		if (_repeater.TryGetElement(index) is { } shown)
		{
			return FocusElement(shown);
		}

		if (LineTop(index) is { } top)
		{
			var offset = Math.Clamp(top - RevealMargin, 0, Math.Max(0, _scroller.ScrollableHeight));
			if (Math.Abs(offset - _scroller.VerticalOffset) >= 1)
			{
				_pending.Begin(index, _scroller.ScrollTo(_scroller.HorizontalOffset, offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)));
				return true;
			}
		}

		// no line to go by (fewer than two drawn): make it and focus it
		var element = _repeater.GetOrCreateElement(index);
		_repeater.UpdateLayout();
		return FocusElement(element);
	}

	/// <summary>
	/// Seaglass 1.1.4: Back to a grid, or an A-Z jump: focuses card <paramref name="index"/>, and again for about a second until
	/// it has the focus (the page that was left leaves the window, and the focus it had lands on the title bar about when the
	/// grid is shown; a far card is made only once the grid has scrolled to it). A move to another card is left alone.
	/// </summary>
	/// <param name="atTop">Nixie 1.4.0: an A to Z jump: the card's row goes to the top of the view first.</param>
	internal void FocusAndHold(int index, FocusState state, bool atTop = false)
	{
		if (atTop)
		{
			FocusAtTop(index, state);
		}
		else
		{
			FocusIndex(index, state);
		}

		if (_repeater.XamlRoot?.Content is not UIElement content)
		{
			return;
		}

		// the review, item 7: anything the user does (a click, a key, the wheel) ends the hold: a click in the search box
		// right after Back was pulled back to the card
		var hold = new FocusHold();
		var pointer = new PointerEventHandler((_, _) => hold.UserInput());
		var key = new KeyEventHandler((_, _) => hold.UserInput());
		content.AddHandler(UIElement.PointerPressedEvent, pointer, true);
		content.AddHandler(UIElement.PointerWheelChangedEvent, pointer, true);
		content.AddHandler(UIElement.KeyDownEvent, key, true);

		var timer = _repeater.DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(100);
		timer.Tick += (t, _) =>
		{
			var root = _repeater.XamlRoot;
			if (root is null)
			{
				hold.UserInput();
			}
			else if (hold.Tick(IndexOf(FocusManager.GetFocusedElement(root) as DependencyObject)))
			{
				FocusIndex(index, state);
			}

			if (hold.Done)
			{
				t.Stop();
				content.RemoveHandler(UIElement.PointerPressedEvent, pointer);
				content.RemoveHandler(UIElement.PointerWheelChangedEvent, pointer);
				content.RemoveHandler(UIElement.KeyDownEvent, key);
			}
		};
		timer.Start();
	}

	private bool FocusElement(UIElement element)
	{
		_directed = true;
		try
		{
			return CardOf(element) is Control card && card.Focus(_focusState);
		}
		finally
		{
			_directed = false;
		}
	}

	/// <summary>Where card <paramref name="index"/>'s line starts in the grid's content, from the lines drawn (a uniform grid); null without two lines.</summary>
	private double? LineTop(int index)
	{
		var realized = Realized().Select(r => (Top: (double)r.Element.ActualOffset.Y, r.Index)).ToList();
		var tops = realized.Select(r => r.Top).ToList();
		var columns = GridPaging.Columns(tops);
		var stride = GridPaging.LineStride(tops);
		if (stride <= 0 || realized.Count == 0)
		{
			return null;
		}

		// the repeater's own top in the scroller's content, and one drawn card to count lines from
		var origin = _repeater.TransformToVisual(_list).TransformPoint(new Point(0, 0)).Y;
		var known = realized[0];
		return origin + known.Top + (index / columns - known.Index / columns) * stride;
	}

	private void Scroller_ScrollCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs args)
	{
		var index = _pending.Complete(args.CorrelationId, FocusInside());
		if (index == PendingFocus.RevealFocused)
		{
			// a cancelled scroll took the view away from the card that has the focus now
			if (_list.XamlRoot is { } xamlRoot && FocusManager.GetFocusedElement(xamlRoot) is UIElement focused)
			{
				focused.StartBringIntoView();
			}

			return;
		}

		if (index < 0 || index >= Count)
		{
			return;
		}

		_repeater.UpdateLayout();
		if (_repeater.TryGetElement(index) is { } shown)
		{
			FocusElement(shown);
		}
		else
		{
			var element = _repeater.GetOrCreateElement(index);
			_repeater.UpdateLayout();
			FocusElement(element);
		}
	}

	/// <summary>
	/// Tab (or Shift+Tab) into the grid lands on its last-focused card; the first time, on the first card in view (Tab's own
	/// choice is the first card drawn, which may be above the view). A remembered card the grid no longer draws is focused
	/// right after.
	/// </summary>
	private void List_GettingFocus(UIElement sender, GettingFocusEventArgs e)
	{
		if (!_directed)
		{
			_pending.Cancel(); // a click on a card, or Tab, before a far card's scroll ended
		}

		if (_directed || e.FocusState == FocusState.Pointer || e.Direction is not (FocusNavigationDirection.Next or FocusNavigationDirection.Previous))
		{
			return;
		}

		if (IndexOf(e.NewFocusedElement) < 0 || IndexOf(e.OldFocusedElement) >= 0)
		{
			return; // only the focus coming in from outside the grid
		}

		var remembered = _lastItem is null || _repeater.ItemsSourceView is not { } source ? -1 : source.IndexOf(_lastItem);
		if (remembered >= 0)
		{
			if (_repeater.TryGetElement(remembered) is { } element && CardOf(element) is Control { IsTabStop: true } card)
			{
				if (!ReferenceEquals(card, e.NewFocusedElement))
				{
					e.TrySetNewFocusedElement(card);
				}
			}
			else
			{
				// unless anything happens first (a key, a click, the focus leaving)
				var generation = _pending.Generation;
				_list.DispatcherQueue.TryEnqueue(() =>
				{
					if (_pending.Generation == generation && FocusInside())
					{
						FocusIndex(remembered);
					}
				});
			}

			return;
		}

		if (FirstInView() is Control first && !ReferenceEquals(first, e.NewFocusedElement))
		{
			e.TrySetNewFocusedElement(first);
		}
	}

	private bool FocusInside() =>
		_list.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInside(focused, _list);

	private UIElement? FirstInView()
	{
		var best = -1;
		UIElement? card = null;
		foreach (var (element, index) in Realized())
		{
			var top = element.TransformToVisual(_scroller).TransformPoint(new Point(0, 0)).Y;
			if (top >= -1 && (best < 0 || index < best))
			{
				best = index;
				card = CardOf(element);
			}
		}

		return card;
	}

	private bool InView(UIElement element)
	{
		try
		{
			var bounds = element.TransformToVisual(_scroller).TransformBounds(new Rect(0, 0, element.ActualSize.X, element.ActualSize.Y));
			return bounds.Height > 0 && bounds.Top >= 0 && bounds.Bottom <= _scroller.ViewportHeight;
		}
		catch (ArgumentException)
		{
			return false;
		}
	}

	// ---- Nixie 1.4.0 (the UX review's X16, item 13): a letter's row to the top; type-to-jump --------------------------------

	/// <summary>Room left above a row a jump puts at the top of the view.</summary>
	internal const double TopMargin = 8;

	/// <summary>
	/// An A to Z jump: card <paramref name="index"/>'s row is scrolled to the top of the view (it landed on the last row of the
	/// letter before), then the card gets the focus.
	/// </summary>
	internal bool FocusAtTop(int index, FocusState state)
	{
		if (index < 0 || index >= Count)
		{
			return false;
		}

		_focusState = state;
		double? top = null;
		if (_repeater.TryGetElement(index) is { } shown)
		{
			try
			{
				top = _scroller.VerticalOffset + shown.TransformToVisual(_scroller).TransformPoint(new Point(0, 0)).Y;
			}
			catch (ArgumentException)
			{
			}
		}

		top ??= LineTop(index) is { } line ? line + _scroller.Padding.Top : null; // the scroller's padding is part of what it scrolls
		if (top is { } at)
		{
			var offset = Math.Clamp(at - TopMargin, 0, Math.Max(0, _scroller.ScrollableHeight));
			if (Math.Abs(offset - _scroller.VerticalOffset) >= 1)
			{
				_pending.Begin(index, _scroller.ScrollTo(_scroller.HorizontalOffset, offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore)), fromOutside: true);
				return true;
			}
		}

		return FocusIndex(index, state);
	}

	/// <summary>Type-to-jump: what a typed run of letters finds (the text, the card it starts from), or null; none: no type-to-jump.</summary>
	internal Func<string, int, int?>? TypeAhead { get; set; }

	private string _typed = "";
	private long _typedAt;

	/// <summary>Letters typed on a card (within a second of each other) go to the first title they start; one letter again goes on to the next.</summary>
	private void List_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
	{
		if (TypeAhead is null || char.IsControl(e.Character) || IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu))
		{
			return;
		}

		var current = IndexOf(e.OriginalSource as DependencyObject);
		var now = Environment.TickCount64;
		if (!Core.Shortcuts.TypeAheadWord.IsOpen(_typedAt, now))
		{
			_typed = "";
		}

		if (e.Character == ' ' && _typed.Length == 0)
		{
			return; // Space presses the card
		}

		_typedAt = now;
		_typed += e.Character;
		GridTypeAhead.Typed(sender); // 1.4.0: an R within the second is a letter of the word, not Refresh
		var query = _typed.Length > 1 && _typed.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(_typed[0])) ? _typed[..1] : _typed;
		var from = query.Length == 1 ? current + 1 : Math.Max(current, 0);
		if (TypeAhead(query, Math.Max(0, from)) is { } target)
		{
			_pending.Cancel();
			FocusIndex(target, FocusState.Keyboard);
			e.Handled = true;
		}
	}

	private void List_PointerPressed(object sender, PointerRoutedEventArgs e) => Remember(IndexOf(e.OriginalSource as DependencyObject));

	private void Remember(int index)
	{
		if (index >= 0 && _repeater.ItemsSourceView is { } source && index < source.Count)
		{
			_lastItem = source.GetAt(index);
		}
	}

	private void List_GotFocus(object sender, RoutedEventArgs e)
	{
		var index = IndexOf(e.OriginalSource as DependencyObject);
		if (index >= 0 && _repeater.ItemsSourceView is { } source && index < source.Count)
		{
			_lastItem = source.GetAt(index);
		}
	}

	/// <summary>
	/// A card scrolled into view (keyboard focus) keeps <see cref="RevealMargin"/> clear above and below it (WCAG 2.4.11).
	/// Nixie 1.4.0: a card already wholly in view is left where it is (a letter's row put at the top stays there).
	/// </summary>
	private void List_BringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
	{
		if (IndexOf(args.TargetElement) < 0)
		{
			return;
		}

		if (InView(args.TargetElement))
		{
			args.Handled = true;
			return;
		}

		var rect = args.TargetRect;
		args.TargetRect = new Rect(rect.X, rect.Y - RevealMargin, rect.Width, rect.Height + 2 * RevealMargin);
		args.AnimationDesired = args.AnimationDesired && SafeMotion.Allowed;
	}
}
