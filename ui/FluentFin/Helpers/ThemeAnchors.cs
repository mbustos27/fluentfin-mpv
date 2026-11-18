using System.Collections;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentFin.Helpers;

/// <summary>
/// Nixie 1.3.0: the place kept across a theme switch. Layouts differ between themes, so no pixel offset is kept: the item
/// the keyboard is on and the first visible item of each row (a row is known by its items' list, the same object in both
/// views since the view model is the same). The new view scrolls each row so that item leads it and gives the keyboard
/// back to the same item; when that item has no counterpart, to the page's Primary button, else its first control.
/// </summary>
public sealed class ThemeAnchors
{
	/// <summary>The key of the item the keyboard was on, null when it was on none (or not in the page).</summary>
	public object? Focused { get; init; }

	/// <summary>Whether the keyboard was in the page at all.</summary>
	public bool HadFocus { get; init; }

	/// <summary>Each row's items list and the key of its first visible item.</summary>
	public IReadOnlyList<(object Source, object First)> Rows { get; init; } = [];

	/// <summary>An item's identity across views: a Jellyfin item's id, else the object itself.</summary>
	public static object? ItemKey(object? item) => item switch
	{
		null => null,
		BaseItemViewModel { Dto.Id: { } id } => id,
		BaseItemDto { Id: { } id } => id,
		BaseItemPerson { Id: { } id } => id,
		_ => item,
	};

	/// <summary>
	/// The first visible item of a row: the first (by its start along the row) whose middle is inside the viewport, else the
	/// first that shows at all; null when none does.
	/// </summary>
	public static T? FirstVisible<T>(IEnumerable<(T Item, double Start, double End)> items, double viewportStart, double viewportEnd)
		where T : class
	{
		var ordered = items.OrderBy(i => i.Start).ToList();
		foreach (var (item, start, end) in ordered)
		{
			var middle = (start + end) / 2;
			if (middle >= viewportStart && middle <= viewportEnd)
			{
				return item;
			}
		}

		return ordered.FirstOrDefault(i => i.End > viewportStart && i.Start < viewportEnd).Item;
	}

	/// <summary>The index of the item with <paramref name="key"/> in a row's list, -1 when it is not there.</summary>
	public static int IndexOf(IEnumerable source, object key)
	{
		var i = 0;
		foreach (var item in source)
		{
			if (Equals(ItemKey(item), key))
			{
				return i;
			}

			i++;
		}

		return -1;
	}

	/// <summary>The place in <paramref name="page"/> before a switch.</summary>
	public static ThemeAnchors Capture(Page page)
	{
		object? focused = null;
		var hadFocus = false;
		if (page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject element && IsInside(element, page))
		{
			hadFocus = true;
			focused = ItemOf(element, page);
		}

		var rows = new List<(object, object)>();
		foreach (var (list, source) in Lists(page))
		{
			if (Scroller(list) is not { } scroller)
			{
				continue;
			}

			var horizontal = IsHorizontal(scroller);
			var viewport = horizontal ? scroller.ViewportWidth : scroller.ViewportHeight;
			var placed = new List<(object Item, double Start, double End)>();
			foreach (var (container, item) in Realized(list))
			{
				if (item is null || container.ActualWidth <= 0)
				{
					continue;
				}

				try
				{
					var at = container.TransformToVisual(scroller).TransformPoint(default);
					var start = horizontal ? at.X : at.Y;
					placed.Add((item, start, start + (horizontal ? container.ActualWidth : container.ActualHeight)));
				}
				catch (ArgumentException)
				{
					// not in the same tree any more
				}
			}

			if (FirstVisible(placed, 0, viewport) is { } first && ItemKey(first) is { } key)
			{
				rows.Add((source, key));
			}
		}

		return new ThemeAnchors { Focused = focused, HadFocus = hadFocus, Rows = rows };
	}

	/// <summary>Puts <paramref name="page"/> (the new view) back at the place, once it is laid out.</summary>
	public void RestoreWhenLoaded(Page page)
	{
		if (page.IsLoaded)
		{
			page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Restore(page));
			return;
		}

		void OnLoaded(object sender, RoutedEventArgs e)
		{
			page.Loaded -= OnLoaded;
			// after the first layout pass, when the rows have realized their items
			page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Restore(page));
		}

		page.Loaded += OnLoaded;
	}

	/// <summary>Scrolls each row to its anchor and gives the keyboard back.</summary>
	public void Restore(Page page)
	{
		page.UpdateLayout();
		foreach (var (list, source) in Lists(page))
		{
			if (Rows.FirstOrDefault(r => ReferenceEquals(r.Source, source)) is not { First: { } key } || Scroller(list) is not { } scroller)
			{
				continue;
			}

			var index = IndexOf((IEnumerable)source, key);
			if (index < 0 || Realize(list, index) is not { } element)
			{
				continue;
			}

			try
			{
				element.UpdateLayout();
				var horizontal = IsHorizontal(scroller);
				var inList = element.TransformToVisual(list).TransformPoint(default);
				// the list's first item sits at the row's start with the scroller at 0: put the anchor there
				var target = Math.Max(0, horizontal ? inList.X : inList.Y);
				_ = horizontal ? scroller.ChangeView(target, null, null, true) : scroller.ChangeView(null, target, null, true);
			}
			catch (ArgumentException)
			{
			}
		}

		if (!HadFocus)
		{
			return;
		}

		if (Focused is not null && FindItem(page, Focused) is { } control && control.Focus(FocusState.Keyboard))
		{
			control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
			return;
		}

		if (Primary(page) is { } primary && primary.Focus(FocusState.Keyboard))
		{
			return;
		}

		(FocusManager.FindFirstFocusableElement(page) as Control)?.Focus(FocusState.Keyboard);
	}

	// ---- the visual tree ----

	private static bool IsInside(DependencyObject element, DependencyObject ancestor)
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

	/// <summary>The key of the item an element belongs to (its own or an ancestor's data context below the page).</summary>
	private static object? ItemOf(DependencyObject element, Page page)
	{
		var pageContext = page.DataContext;
		for (var node = element; node is not null && !ReferenceEquals(node, page); node = VisualTreeHelper.GetParent(node))
		{
			if (node is FrameworkElement { DataContext: { } context } && !ReferenceEquals(context, pageContext) && IsItem(context))
			{
				return ItemKey(context);
			}
		}

		return null;
	}

	private static bool IsItem(object context) => context is BaseItemViewModel or BaseItemDto or BaseItemPerson;

	/// <summary>Every list in the page with items: repeaters and items controls, with their source.</summary>
	private static IEnumerable<(FrameworkElement List, object Source)> Lists(DependencyObject root)
	{
		foreach (var element in Descendants(root))
		{
			switch (element)
			{
				case ItemsRepeater { ItemsSource: IEnumerable source } repeater:
					yield return (repeater, source);
					break;
				case ItemsControl { ItemsSource: IEnumerable source } items:
					yield return (items, source);
					break;
			}
		}
	}

	private static IEnumerable<(FrameworkElement Container, object? Item)> Realized(FrameworkElement list)
	{
		switch (list)
		{
			case ItemsRepeater repeater:
				for (var i = 0; i < VisualTreeHelper.GetChildrenCount(repeater); i++)
				{
					if (VisualTreeHelper.GetChild(repeater, i) is FrameworkElement child && repeater.GetElementIndex(child) is var index and >= 0)
					{
						yield return (child, repeater.ItemsSourceView?.GetAt(index));
					}
				}

				break;
			case ItemsControl items when items.ItemsPanelRoot is { } panel:
				foreach (var child in panel.Children.OfType<FrameworkElement>())
				{
					yield return (child, items.ItemFromContainer(child));
				}

				break;
		}
	}

	private static FrameworkElement? Realize(FrameworkElement list, int index) => list switch
	{
		ItemsRepeater repeater => repeater.GetOrCreateElement(index) as FrameworkElement,
		ItemsControl items => items.ContainerFromIndex(index) as FrameworkElement,
		_ => null,
	};

	private static ScrollViewer? Scroller(DependencyObject element)
	{
		for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is ScrollViewer scroller)
			{
				return scroller;
			}
		}

		return null;
	}

	private static bool IsHorizontal(ScrollViewer scroller) =>
		scroller.HorizontalScrollMode != ScrollMode.Disabled && scroller.ScrollableWidth > 0 && scroller.ScrollableHeight <= 0;

	private static Control? FindItem(Page page, object key)
	{
		foreach (var element in Descendants(page).OfType<FrameworkElement>())
		{
			if (element.DataContext is { } context && IsItem(context) && Equals(ItemKey(context), key))
			{
				var focusable = element as Control ?? Descendants(element).OfType<Control>().FirstOrDefault(c => c.IsTabStop && c.IsEnabled);
				if (focusable is { IsTabStop: true, IsEnabled: true })
				{
					return focusable;
				}
			}
		}

		return null;
	}

	/// <summary>The page's Primary button (the one Nixie primary per view), null when it shows none.</summary>
	private static Control? Primary(Page page)
	{
		var styles = new[] { "NixiePrimaryButtonStyle", "NixiePrimaryCompactButtonStyle" }
			.Select(k => Application.Current.Resources.TryGetValue(k, out var s) ? s as Style : null)
			.OfType<Style>()
			.ToList();
		return Descendants(page).OfType<Button>().FirstOrDefault(b => b.Style is { } s && styles.Contains(s) && b.Visibility == Visibility.Visible && b.IsEnabled);
	}

	private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
	{
		var queue = new Queue<DependencyObject>();
		queue.Enqueue(root);
		while (queue.Count > 0)
		{
			var node = queue.Dequeue();
			var count = VisualTreeHelper.GetChildrenCount(node);
			for (var i = 0; i < count; i++)
			{
				var child = VisualTreeHelper.GetChild(node, i);
				yield return child;
				queue.Enqueue(child);
			}
		}
	}
}
