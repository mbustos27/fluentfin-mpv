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

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m3: R on an item page moved the keyboard from Trailer to Play): the control the keyboard was on
	/// when it was on no item (a page's own button): its x:Name, else its type and Narrator name. Null when it was on an item.
	/// </summary>
	public ControlAnchor? FocusedControl { get; init; }

	/// <summary>Each row's items list and the key of its first visible item.</summary>
	public IReadOnlyList<(object Source, object First)> Rows { get; init; } = [];

	/// <summary>
	/// Nixie 1.3.1 (a refresh, R / F5): each row's place among the page's lists, with the key of its first visible item. A
	/// refresh makes a new view model, so its lists are new objects: a row is then found by its place (and its item).
	/// </summary>
	public IReadOnlyList<(int Position, object First)> RowPlaces { get; init; } = [];

	/// <summary>
	/// Nixie 1.3.1: which of a refreshed page's lists stands for a row (-1 for none): the same list when it is the same object
	/// (the view model kept), else the list at the row's place when it still has the anchor item, else the first list that has
	/// it (a row came or went before it).
	/// </summary>
	public static int RowFor(IReadOnlyList<object> sources, object? oldSource, int oldPosition, object key)
	{
		for (var i = 0; i < sources.Count; i++)
		{
			if (ReferenceEquals(sources[i], oldSource))
			{
				return i;
			}
		}

		bool Has(object source) => source is IEnumerable items && IndexOf(items, key) >= 0;
		if (oldPosition >= 0 && oldPosition < sources.Count && Has(sources[oldPosition]))
		{
			return oldPosition;
		}

		for (var i = 0; i < sources.Count; i++)
		{
			if (Has(sources[i]))
			{
				return i;
			}
		}

		return -1;
	}

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
		ControlAnchor? control = null;
		var hadFocus = false;
		if (page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject element && IsInside(element, page))
		{
			hadFocus = true;
			focused = ItemOf(element, page);
			control = focused is null && element is FrameworkElement shown ? ControlAnchor.Of(shown) : null;
		}

		var rows = new List<(object, object)>();
		var places = new List<(int, object)>();
		var position = -1;
		foreach (var (list, source) in Lists(page))
		{
			position++;
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
				places.Add((position, key));
			}
		}

		return new ThemeAnchors { Focused = focused, HadFocus = hadFocus, FocusedControl = control, Rows = rows, RowPlaces = places };
	}

	/// <summary>Puts <paramref name="page"/> (the new view) back at the place, once it is laid out.</summary>
	public void RestoreWhenLoaded(Page page)
	{
		if (page.IsLoaded)
		{
			Services.UiDispatch.Enqueue(page.DispatcherQueue, "the place after a theme switch", () => Restore(page), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
			return;
		}

		void OnLoaded(object sender, RoutedEventArgs e)
		{
			page.Loaded -= OnLoaded;
			// after the first layout pass, when the rows have realized their items
			Services.UiDispatch.Enqueue(page.DispatcherQueue, "the place after a theme switch", () => Restore(page), Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);
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

	/// <summary>
	/// Nixie 1.3.1: puts a refreshed page (a new view model, its rows arriving from the server) back at the place: tried as the
	/// rows fill, until the keyboard is back on its item (or, when it was not on one, the rows are placed), at most
	/// <paramref name="within"/>; then the keyboard goes to the page's Primary, else its first control.
	/// <para>
	/// Nixie 1.4.0 (bug hunt M3: on a slow server the place came back 4.6 s after R and took the keyboard from the search box
	/// the user had moved to): each try first asks <paramref name="yieldTo"/>, which names why the user's own move wins (a
	/// key, a click, the wheel, the keyboard in a text box, the page left); the restore then stops where it is.
	/// </para>
	/// </summary>
	public RefreshRestore RestoreAfterRefresh(Page page, TimeSpan within, Action<string>? log = null, Func<string?>? yieldTo = null)
	{
		var started = DateTime.UtcNow;
		DateTime? loadedAt = null;
		var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
		var restore = new RefreshRestore(page, timer, started, log);
		timer.Tick += (_, _) =>
		{
			if (App.IsClosing)
			{
				return; // 1.7.3 fix round (the close crash's class): nothing in the window's teardown
			}

			if (yieldTo?.Invoke() is { } why)
			{
				restore.Stop(why);
				return;
			}

			var now = DateTime.UtcNow;
			var last = now - started >= within;
			if (!page.IsLoaded && !last)
			{
				return;
			}

			// Nixie 1.4.0 (bug hunt m1: with the place gone, R was locked out for the whole 6 s): once the page has loaded and
			// settled, what is not in it is not waited for
			loadedAt = IsLoading(page) == false ? loadedAt ?? now : null;
			restore.LoadedAt = loadedAt;
			var settled = loadedAt is { } at && now - at >= SettleAfterLoad;
			var done = TryRestoreByPlace(page, last, settled);
			if (done || last)
			{
				timer.Stop();
				log?.Invoke($"the place {(done ? _gone ? "not in the loaded page, given up" : "kept" : "not found")} after {(int)(DateTime.UtcNow - started).TotalMilliseconds} ms (keyboard {(HadFocus ? Focused is null ? FocusedControl is { } c ? $"on {c}" : "on the page, on no item" : "on an item" : "not in the page")}, {RowPlaces.Count} row(s))");
			}
		};
		timer.Start();
		return restore;
	}

	private bool _interimOnPrimary;

	// the place was given up because it is not in the loaded page (for the log)
	private bool _gone;

	/// <summary>Nixie 1.4.0 (bug hunt m1): how long a loaded page is given for its lists to fill before what is not there is given up.</summary>
	public static readonly TimeSpan SettleAfterLoad = TimeSpan.FromMilliseconds(500);

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m1): whether the page is still loading, by its view model's load states (each public LoadState, and
	/// an IsLoading flag); null when its view model says nothing of either (the restore then waits its whole time, as before).
	/// </summary>
	public static bool? IsLoading(Page page)
	{
		var viewModel = page.GetType().GetProperty("ViewModel")?.GetValue(page);
		if (viewModel is null)
		{
			return null;
		}

		bool? loading = null;
		foreach (var property in viewModel.GetType().GetProperties())
		{
			if (property.GetIndexParameters().Length > 0)
			{
				continue;
			}

			if (property.PropertyType == typeof(LoadState) && property.GetValue(viewModel) is LoadState state)
			{
				loading = (loading ?? false) || state.Status is LoadStatus.Idle or LoadStatus.Loading;
			}
			else if (property.Name == "IsLoading" && property.PropertyType == typeof(bool) && property.GetValue(viewModel) is bool busy)
			{
				loading = (loading ?? false) || busy;
			}
		}

		return loading;
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m1: R with the keyboard on the top bar's tab, its rows' place not found, ran the whole 6 s): with the
	/// keyboard not in the page, the rows are done when each is placed, or once the page has loaded and settled (a row not
	/// placed by then is not there: its item gone, or not a row a refresh can find again).
	/// </summary>
	public static bool RowsDone(int rows, int placed, bool settled) => placed >= rows || settled;

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m1): whether a refresh's page is still coming in: loading (or not saying), not yet seen loaded by the
	/// restore, or seen loaded less than <see cref="SettleAfterLoad"/> before <paramref name="now"/>.
	/// </summary>
	public static bool StillComing(bool? loading, DateTime? loadedAt, DateTime now) =>
		loading != false || loadedAt is not { } at || now - at < SettleAfterLoad;

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m1): whether the item the keyboard was on is given up: the page has loaded and settled and the item
	/// is in none of its lists (a Continue Watching item finished on a phone). The keyboard then goes where the time running out
	/// would have put it (the Primary), at once.
	/// </summary>
	public static bool ItemGone(bool settled, bool inLists) => settled && !inLists;

	/// <summary>One try of <see cref="RestoreAfterRefresh"/>: true when the place is back (or, once the page has settled, given up).</summary>
	private bool TryRestoreByPlace(Page page, bool last, bool settled = false)
	{
		page.UpdateLayout();
		var lists = Lists(page).ToList();
		var sources = lists.Select(l => l.Source).ToList();
		var placed = 0;
		for (var i = 0; i < RowPlaces.Count; i++)
		{
			var (position, key) = RowPlaces[i];
			var old = i < Rows.Count ? Rows[i].Source : null;
			var at = RowFor(sources, old, position, key);
			if (at < 0 || Scroller(lists[at].List) is not { } scroller)
			{
				continue;
			}

			var index = IndexOf((IEnumerable)lists[at].Source, key);
			if (index < 0 || Realize(lists[at].List, index) is not { } element)
			{
				continue;
			}

			try
			{
				element.UpdateLayout();
				var horizontal = IsHorizontal(scroller);
				var inList = element.TransformToVisual(lists[at].List).TransformPoint(default);
				var target = Math.Max(0, horizontal ? inList.X : inList.Y);
				_ = horizontal ? scroller.ChangeView(target, null, null, true) : scroller.ChangeView(null, target, null, true);
				placed++;
			}
			catch (ArgumentException)
			{
			}
		}

		if (!HadFocus)
		{
			_gone = settled && placed < RowPlaces.Count;
			return RowsDone(RowPlaces.Count, placed, settled);
		}

		if (Focused is not null && FindItem(page, Focused) is { } control && control.Focus(FocusState.Keyboard))
		{
			control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
			return true;
		}

		if (Focused is not null && !last && ItemGone(settled, sources.Any(source => source is IEnumerable items && IndexOf(items, Focused) >= 0)))
		{
			// the item is not in the loaded page: the Primary now, not after the whole wait
			_gone = true;
			last = true;
		}

		if (Focused is null && FocusedControl is { } anchor && !last)
		{
			// Nixie 1.4.0 (bug hunt m3): the keyboard was on a button of the page's own: that button, once it shows (an item
			// page's Trailer comes with its item; meanwhile the Primary holds the keyboard); the Primary when the page is up
			// (its Primary shows) and has no such button
			if (anchor.Find(page) is { } named && named.Focus(FocusState.Keyboard))
			{
				return true;
			}

			var interim = Primary(page);
			if (interim is null || anchor.InPage(page))
			{
				if (!_interimOnPrimary && interim is not null && interim.Focus(FocusState.Keyboard))
				{
					_interimOnPrimary = true;
				}

				return false;
			}
		}

		if (Focused is null || last)
		{
			if (last && Focused is null && FocusedControl?.Find(page) is { } late && late.Focus(FocusState.Keyboard))
			{
				return true;
			}

			// the keyboard was on the page but on no item (a button), or its item is gone: the Primary, else the first control
			if (Primary(page) is { } primary && primary.Focus(FocusState.Keyboard))
			{
				return true;
			}

			return (FocusManager.FindFirstFocusableElement(page) as Control)?.Focus(FocusState.Keyboard) ?? false;
		}

		return false;
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
			if (node is FrameworkElement shown && ItemOfElement(shown) is { } context && !ReferenceEquals(context, pageContext))
			{
				return ItemKey(context);
			}
		}

		return null;
	}

	private static bool IsItem(object context) => context is BaseItemViewModel or BaseItemDto or BaseItemPerson;

	/// <summary>
	/// The item an element shows: its data context when that is an item, else (Nixie 1.3.1) a card's own Model: a card in a
	/// template compiled with x:Bind (the Library's grid) has no data context, so the grid's card was never found. Nixie 1.4.0:
	/// Editorial's poster, whose data context is its grid cell (R in Editorial's library put the keyboard on Sort).
	/// </summary>
	private static object? ItemOfElement(FrameworkElement element) =>
		element.DataContext is { } context && IsItem(context) ? context
		: element.DataContext is EditorialCell { Item: { } cell } ? cell
		: element is Controls.BaseItemDtoCard { Model: { } model } ? model
		: null;

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
			// Nixie 1.4.0: not a grid's recycled card (kept in the tree out of view with the item it showed last: R in a library
			// put the keyboard on an unseen card, "Circus of Ashford", instead of the one it was on)
			if (ItemOfElement(element) is { } context && Equals(ItemKey(context), key) && !Recycled(element))
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

	/// <summary>Whether <paramref name="element"/> is in an items repeater's card that the repeater no longer shows (its index -1).</summary>
	private static bool Recycled(DependencyObject element)
	{
		for (DependencyObject? child = element, parent = VisualTreeHelper.GetParent(element); parent is not null; child = parent, parent = VisualTreeHelper.GetParent(parent))
		{
			if (parent is ItemsRepeater repeater && child is UIElement card)
			{
				return repeater.GetElementIndex(card) < 0;
			}
		}

		return false;
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

	internal static IEnumerable<DependencyObject> Descendants(DependencyObject root)
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

/// <summary>
/// Nixie 1.4.0 (bug hunt m3): a page's own control the keyboard was on (not an item's): known by its x:Name, else by its type
/// and Narrator name (a button with no x:Name: Watched, Favourite). <see cref="Find"/> gives the refreshed page's one once it
/// shows and takes the keyboard.
/// </summary>
public sealed record ControlAnchor(string? Name, string? TypeName, string? AutomationName)
{
	/// <summary>The control <paramref name="element"/> is, or the one it is in (a button's text), as an anchor; null when none can be told again.</summary>
	public static ControlAnchor? Of(FrameworkElement element)
	{
		for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is Control control)
			{
				var automation = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(control);
				return !string.IsNullOrEmpty(control.Name) ? new ControlAnchor(control.Name, null, null)
					: !string.IsNullOrEmpty(automation) ? new ControlAnchor(null, control.GetType().FullName, automation)
					: null;
			}

			if (node is Page)
			{
				break;
			}
		}

		return null;
	}

	/// <summary>Whether <paramref name="element"/> is the anchored control.</summary>
	public bool Matches(FrameworkElement element) => Name is not null
		? element.Name == Name
		: element.GetType().FullName == TypeName && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(element) == AutomationName;

	/// <summary>Whether the page has the control at all (shown or not yet).</summary>
	public bool InPage(Page page) => ThemeAnchors.Descendants(page).OfType<Control>().Any(Matches);

	/// <summary>The page's control, when it shows and can take the keyboard.</summary>
	public Control? Find(Page page) => ThemeAnchors.Descendants(page).OfType<Control>()
		.FirstOrDefault(c => Matches(c) && c is { IsEnabled: true, IsTabStop: true, Visibility: Visibility.Visible } && c.ActualWidth > 0 && Shown(c, page));

	private static bool Shown(UIElement element, Page page)
	{
		for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, page); node = VisualTreeHelper.GetParent(node))
		{
			if (node is UIElement { Visibility: Visibility.Collapsed })
			{
				return false;
			}
		}

		return true;
	}

	public override string ToString() => Name ?? $"{TypeName?.Split('.').LastOrDefault()} '{AutomationName}'";
}

/// <summary>Nixie 1.4.0: a refresh's place restore while it runs (<see cref="ThemeAnchors.RestoreAfterRefresh"/>).</summary>
public sealed class RefreshRestore(Page page, DispatcherTimer timer, DateTime started, Action<string>? log)
{
	/// <summary>The refreshed page.</summary>
	public Page Page { get; } = page;

	/// <summary>Whether it is still looking for the place.</summary>
	public bool IsRunning => timer.IsEnabled;

	/// <summary>Nixie 1.4.0 (bug hunt m1): when the refreshed page was seen loaded (null while it loads).</summary>
	public DateTime? LoadedAt { get; set; }

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m1): whether the refresh is still coming in: running, and its page still loading or loaded less than
	/// <see cref="ThemeAnchors.SettleAfterLoad"/> ago. Another R then joins it (R, R 150 ms apart: bug hunt M4); after that
	/// R loads the page again, even while the place is still looked for (a place that cannot be found locked R out for 6 s).
	/// </summary>
	public bool Settling => IsRunning && ThemeAnchors.StillComing(ThemeAnchors.IsLoading(Page), LoadedAt, DateTime.UtcNow);

	/// <summary>Stops looking (the user moved, or the page was left), and says why in the log.</summary>
	public void Stop(string why)
	{
		if (!timer.IsEnabled)
		{
			return;
		}

		timer.Stop();
		log?.Invoke($"the place given up after {(int)(DateTime.UtcNow - started).TotalMilliseconds} ms: {why}");
	}
}
