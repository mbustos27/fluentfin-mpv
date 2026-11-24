using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.7.4 (the rows stream): the one way a horizontal row or strip scrolls with a pointer, wherever it is (ShelfRow's
/// card rows, Cinematic's tab strips, a library's presets). A 13-episode season's row on Cinematic's season page showed E1
/// to E6 with no arrow (its theme hid the header the arrows were in) and the wheel scrolled only the page, so the rest of
/// the season was out of the mouse's reach.
/// <list type="bullet">
/// <item>The arrows are at the row's ends, over it: shown while a mouse or a pen is over the row, each only while the row
/// goes further its way (<see cref="RowPaging.EdgeArrows"/>), no Tab stop (the row's own keys move it).</item>
/// <item>The plain wheel scrolls the page while the page can move its way, else the row (<see cref="RowPaging.TargetOf"/>);
/// the tilt wheel and Shift+wheel always the row.</item>
/// <item>A mouse drag moves the row (<see cref="RowDrag"/>); a press that stays a click opens what it pressed, as before.</item>
/// </list>
/// A finger pans the scroll viewer itself, and a precision touchpad's swipe goes to it through DirectManipulation.
/// </summary>
internal sealed class EdgeScroller
{
	private static readonly List<WeakReference<EdgeScroller>> s_all = [];

	private static readonly DependencyProperty BehaviourProperty =
		DependencyProperty.RegisterAttached("Behaviour", typeof(object), typeof(EdgeScroller), new PropertyMetadata(null));

	private readonly FrameworkElement _area;
	private readonly ScrollViewer _scroller;
	private readonly FrameworkElement _content;
	private readonly ButtonBase? _back;
	private readonly ButtonBase? _next;
	private readonly Func<double> _stride;
	private readonly RowDrag _drag = new();
	private bool _pointerOver;

	/// <summary>When a plain wheel notch over a row last went to the page (Environment.TickCount64): one page under the pointer at a time.</summary>
	private static long s_pageTookTheWheelAt = long.MinValue / 2;

	/// <param name="area">What the pointer is over when it is over the row (the arrows' hover).</param>
	/// <param name="scroller">The row's horizontal scroll viewer.</param>
	/// <param name="content">Its content: the wheel and a drag are taken there, before the scroll viewer's own handling.</param>
	/// <param name="back">The arrow at the row's start (null with <paramref name="next"/>: a theme that has its own, Editorial's genres).</param>
	/// <param name="next">The arrow at its end.</param>
	/// <param name="stride">An item's width: an arrow pages to an item's edge (0: any offset).</param>
	public EdgeScroller(FrameworkElement area, ScrollViewer scroller, FrameworkElement content, ButtonBase? back, ButtonBase? next, Func<double> stride)
	{
		_area = area;
		_scroller = scroller;
		_content = content;
		_back = back;
		_next = next;
		_stride = stride;
		// it lives as long as its row (a page that wraps a strip keeps no reference to it)
		area.SetValue(BehaviourProperty, this);
		if (_back is not null && _next is not null)
		{
			_back.IsTabStop = _next.IsTabStop = false;
			_back.Click += (_, _) => Page(forward: false);
			_next.Click += (_, _) => Page(forward: true);
		}

		area.Loaded += (_, _) =>
		{
			Prune();
			if (!s_all.Any(w => w.TryGetTarget(out var s) && ReferenceEquals(s, this)))
			{
				s_all.Add(new WeakReference<EdgeScroller>(this));
			}

			UpdateArrows();
		};
		area.Unloaded += (_, _) =>
		{
			Hover(false); // a page kept in the history comes back without the arrows of a pointer that has gone
			_drag.Release();
		};

		area.PointerEntered += (_, e) => Hover(e.Pointer.PointerDeviceType != PointerDeviceType.Touch);
		area.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Hover(e.Pointer.PointerDeviceType != PointerDeviceType.Touch)), true);
		area.PointerExited += (_, _) =>
		{
			Hover(false);
			_drag.Left(); // 1.7.4 integration (the rows review's m2): a press that isn't a drag yet goes with the pointer
		};
		area.PointerCanceled += (_, _) =>
		{
			Hover(false);
			_drag.Left();
		};

		scroller.ViewChanged += (_, _) => UpdateArrows();
		scroller.SizeChanged += (_, _) => UpdateArrows();
		content.SizeChanged += (_, _) => UpdateArrows();

		// handled events too: the scroll viewer's content handles its own (a card, a tab)
		content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Content_PointerWheelChanged), true);
		content.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(Content_PointerPressed), true);
		content.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(Content_PointerMoved), true);
		content.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Content_PointerReleased), true);
		content.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => _drag.Release()), true);
	}

	/// <summary>
	/// Puts <paramref name="scroller"/> in a grid with an arrow at each end over it (a strip built in code, or a strip whose XAML
	/// has none), and gives it this behaviour; the grid takes the scroller's place. <paramref name="name"/> is the strip's name
	/// for the arrows ("Scroll Seasons right"). The arrows are the compact 32 px ones (a strip of tabs or chips is that tall).
	/// </summary>
	public static (Grid Host, EdgeScroller Scroller) Wrap(ScrollViewer scroller, FrameworkElement content, string name, Func<double>? stride = null)
	{
		var host = new Grid();
		var back = Arrow(name, forward: false);
		var next = Arrow(name, forward: true);
		host.Children.Add(scroller);
		host.Children.Add(back);
		host.Children.Add(next);
		return (host, new EdgeScroller(host, scroller, content, back, next, stride ?? (() => 0)));
	}

	/// <summary>
	/// Home and End on a strip of buttons that only has the arrow keys (XY focus: Drive-in's season tubes): its first and its
	/// last button, brought into view as the arrows bring theirs, as on every row.
	/// </summary>
	public static void HomeEndKeys(ItemsControl strip) =>
		strip.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
		{
			if (e.Key is not (VirtualKey.Home or VirtualKey.End) || strip.Items.Count == 0)
			{
				return;
			}

			var index = e.Key == VirtualKey.Home ? 0 : strip.Items.Count - 1;
			if (strip.ContainerFromIndex(index) is DependencyObject container && FirstControl(container) is { } target)
			{
				e.Handled = target.Focus(FocusState.Keyboard);
			}
		}), true);

	private static Control? FirstControl(DependencyObject node)
	{
		if (node is Control { IsTabStop: true } control)
		{
			return control;
		}

		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
		{
			if (FirstControl(VisualTreeHelper.GetChild(node, i)) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static Button Arrow(string name, bool forward)
	{
		var button = new Button
		{
			Content = new FontIcon { Glyph = forward ? "" : "", FontSize = 14 },
			HorizontalAlignment = forward ? HorizontalAlignment.Right : HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			Visibility = Visibility.Collapsed,
			IsTabStop = false,
		};
		if (Application.Current.Resources.TryGetValue("NixieIconButtonStyle", out var style) && style is Style s)
		{
			button.Style = s;
		}

		button.Width = button.Height = 32;
		AutomationProperties.SetName(button, RowPaging.ArrowName(name, forward));
		AutomationProperties.SetAutomationId(button, forward ? "RowNextButton" : "RowBackButton");
		ToolTipService.SetToolTip(button, forward ? "Scroll right (or Shift+wheel, drag)" : "Scroll left (or Shift+wheel, drag)");
		return button;
	}

	/// <summary>The arrows' names after the row's (it may be named after it is made).</summary>
	public void Name(string name)
	{
		if (_back is not null && _next is not null)
		{
			AutomationProperties.SetName(_back, RowPaging.ArrowName(name, forward: false));
			AutomationProperties.SetName(_next, RowPaging.ArrowName(name, forward: true));
		}
	}

	/// <summary>
	/// The wheel and a drag for a strip whose theme already has its own way on with the pointer (Editorial's genres: a fade
	/// and a chevron): no arrows of ours over it, as the brief says, "follow what the row's theme already does".
	/// </summary>
	public static EdgeScroller WheelAndDrag(FrameworkElement area, ScrollViewer scroller, FrameworkElement content) =>
		new(area, scroller, content, null, null, () => 0);

	private static void Prune() => s_all.RemoveAll(w => !w.TryGetTarget(out _));

	// ---- the arrows ---------------------------------------------------------------------------------------------------

	private void Hover(bool over)
	{
		if (_pointerOver != over)
		{
			_pointerOver = over;
			UpdateArrows();
		}
	}

	/// <summary>Each arrow shows while the pointer is over the row and the row goes further its way (hidden, not disabled).</summary>
	public void UpdateArrows()
	{
		if (_back is null || _next is null)
		{
			return;
		}

		var (back, forward) = RowPaging.EdgeArrows(_pointerOver, _scroller.HorizontalOffset, _scroller.ExtentWidth, _scroller.ViewportWidth);
		_back.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
		_next.Visibility = forward ? Visibility.Visible : Visibility.Collapsed;
	}

	/// <summary>
	/// A page of the row: a viewport minus an item, on an item's edge (<see cref="RowPaging.Page"/>), from where the row is
	/// going (a second click while the first still glides goes a page further, not short of it).
	/// </summary>
	public void Page(bool forward) =>
		SmoothScroll.To(_scroller, horizontal: true, RowPaging.Page(SmoothScroll.Current(_scroller, horizontal: true), _scroller.ViewportWidth, _scroller.ExtentWidth, _stride(), forward));

	// ---- the wheel ----------------------------------------------------------------------------------------------------

	private void Content_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
		{
			return;
		}

		var properties = e.GetCurrentPoint(_scroller).Properties;
		var delta = properties.MouseWheelDelta;
		if (delta == 0)
		{
			return;
		}

		var shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
		var pixels = RowPaging.Pixels(delta);
		var page = PageScroller(_area);
		var view = page is null ? PageScrollView(_area) : null;
		// the plain wheel is the page's while the page can move its way; at the page's end (or on a page that does not scroll)
		// the row under the pointer takes it, so a row is never out of the wheel's reach
		var pageCanUse = page is not null ? RowPaging.PageCanScroll(SmoothScroll.Current(page, horizontal: false), page.ScrollableHeight, pixels)
			: view is not null && RowPaging.PageCanScroll(view.VerticalOffset, view.ScrollableHeight, pixels);
		// 1.7.4 integration (the rows review's m3): at the page's end the row takes the wheel only once the page has stopped there
		// and the wheel has rested (a timestamp, no timer): a fast spin or a touchpad's coasting no longer slides the row
		var now = Environment.TickCount64;
		var scrollable = page?.ScrollableHeight ?? view?.ScrollableHeight ?? 0;
		var settled = !RowPaging.PageCanScroll(page?.VerticalOffset ?? view?.VerticalOffset ?? 0, scrollable, pixels);
		var pageKeeps = pageCanUse || RowPaging.WheelStaysWithPage(scrollable > RowPaging.EndTolerance, settled, now - s_pageTookTheWheelAt);
		if (RowPaging.TargetOf(properties.IsHorizontalMouseWheel, shift, pageKeeps) == WheelTarget.Row)
		{
			if (_scroller.ScrollableWidth > 0)
			{
				SmoothScroll.By(_scroller, horizontal: true, RowPaging.RowPixels(delta, properties.IsHorizontalMouseWheel));
			}
		}
		else if (page is not null)
		{
			s_pageTookTheWheelAt = now;
			if (pageCanUse)
			{
				SmoothScroll.By(page, horizontal: false, pixels);
			}
		}
		else if (view is not null)
		{
			s_pageTookTheWheelAt = now;
			if (pageCanUse)
			{
				// a page of the newer ScrollView (the Collections page), which the row's scroll viewer kept the wheel from
				var animation = SafeMotion.Allowed ? ScrollingAnimationMode.Auto : ScrollingAnimationMode.Disabled;
				view.ScrollBy(0, pixels, new ScrollingScrollOptions(animation, ScrollingSnapPointsMode.Ignore));
			}
		}

		// the row's own scroll viewer never sees it: it would take a vertical wheel for the row (microsoft-ui-xaml #10172)
		e.Handled = true;
	}

	/// <summary>The scroll viewer that scrolls the page <paramref name="element"/> is on: the nearest one above that scrolls vertically.</summary>
	internal static ScrollViewer? PageScroller(DependencyObject element)
	{
		for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is ScrollViewer { VerticalScrollMode: ScrollMode.Enabled or ScrollMode.Auto } viewer && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
			{
				return viewer;
			}
		}

		return null;
	}

	/// <summary>The page's scroller when it is a ScrollView (the Collections page), not a ScrollViewer.</summary>
	private static ScrollView? PageScrollView(DependencyObject element)
	{
		for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is ScrollView { VerticalScrollMode: not ScrollingScrollMode.Disabled } viewer)
			{
				return viewer;
			}
		}

		return null;
	}

	// ---- a mouse drag -------------------------------------------------------------------------------------------------

	private void Content_PointerPressed(object sender, PointerRoutedEventArgs e)
	{
		var point = e.GetCurrentPoint(_area);
		if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && point.Properties.IsLeftButtonPressed && _scroller.ScrollableWidth > 0)
		{
			_drag.Press(point.Position.X, _scroller.HorizontalOffset);
		}
	}

	private void Content_PointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (!_drag.Pressed)
		{
			return;
		}

		var point = e.GetCurrentPoint(_area);
		if (!point.Properties.IsLeftButtonPressed)
		{
			_drag.Release();
			return;
		}

		var starting = !_drag.Dragging;
		if (DragTo(point.Position.X) && starting)
		{
			// the row takes the pointer: what was pressed loses it, so letting go opens nothing
			_content.CapturePointer(e.Pointer);
		}

		e.Handled = _drag.Dragging;
	}

	private void Content_PointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (_drag.Release())
		{
			_content.ReleasePointerCapture(e.Pointer);
			e.Handled = true;
		}
	}

	/// <summary>The pointer at <paramref name="x"/> (the area's coordinates) while pressed: the row follows it once it is a drag.</summary>
	private bool DragTo(double x)
	{
		if (_drag.Move(x, _scroller.ScrollableWidth) is not { } offset)
		{
			return false;
		}

		_scroller.ChangeView(offset, null, null, disableAnimation: true);
		return true;
	}

	// ---- the off-screen harness's pointer (HarnessPointer, dev builds only) ---------------------------------------------

	/// <summary>The harness's pointer moved to <paramref name="hit"/>: the row under it is hovered, every other row not.</summary>
	internal static void HarnessHover(DependencyObject? hit)
	{
		Prune();
		foreach (var weak in s_all.ToList())
		{
			if (weak.TryGetTarget(out var row))
			{
				row.Hover(hit is not null && IsInside(hit, row._area));
			}
		}
	}

	/// <summary>The innermost row whose area holds <paramref name="hit"/> (a row inside a row: the inner one); null for none.</summary>
	internal static EdgeScroller? At(DependencyObject hit)
	{
		Prune();
		EdgeScroller? best = null;
		foreach (var weak in s_all)
		{
			if (weak.TryGetTarget(out var row) && IsInside(hit, row._area) && (best is null || IsInside(row._area, best._area)))
			{
				best = row;
			}
		}

		return best;
	}

	/// <summary>The harness's drag along the row, from <paramref name="fromX"/> to <paramref name="toX"/> (the area's coordinates), through the drag's own steps.</summary>
	internal async Task HarnessDrag(double fromX, double toX)
	{
		if (_scroller.ScrollableWidth <= 0)
		{
			return;
		}

		_drag.Press(fromX, _scroller.HorizontalOffset);
		for (var i = 1; i <= 8; i++)
		{
			DragTo(fromX + (toX - fromX) * i / 8);
			await Task.Delay(30);
		}

		_drag.Release();
	}

	/// <summary>The area the row's pointer behaviour is on (the harness turns its own coordinates into the area's).</summary>
	internal FrameworkElement Area => _area;

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
}
