using FluentFin.Controls;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Views;

/// <summary>
/// 1.2.0 (1.1.3 UX audit, interaction item 5): an item page keeps its place. Leaving it saves how far down it was and
/// what had the focus (<see cref="PagePlaces"/>); coming back with Back or Forward scrolls there and focuses that again (a card
/// or an episode row by its item, a button by its place in the page) once the page's content has arrived. The movie, series,
/// season and episode pages each make one: their OnNavigatedTo calls <see cref="Arrived"/>, their OnNavigatingFrom
/// <see cref="Left"/> (OnNavigatedFrom is too late: the page is unloaded by then and the focus has gone to the title bar).
/// The series page also gives its shown season (<see cref="PagePlace.SelectedSeasonId"/>), which Back shows again.
/// </summary>
internal sealed class ItemPagePlace
{
	private readonly Page _page;
	private readonly ScrollViewer _scroller;
	private readonly Func<bool> _loaded;

	// the last element of the page that had the focus, and how it got it: when the page is left from a menu or a flyout the
	// focus is in its popup, not in the page (a row's "Episode details")
	private WeakReference<DependencyObject>? _lastFocus;
	private FocusState _lastFocusState;

	public ItemPagePlace(Page page, ScrollViewer scroller, Func<bool> loaded)
	{
		_page = page;
		_scroller = scroller;
		_loaded = loaded;
		page.GotFocus += (_, e) =>
		{
			if (e.OriginalSource is UIElement element && IsInPage(element))
			{
				_lastFocus = new WeakReference<DependencyObject>(element);
				_lastFocusState = element.FocusState;
			}
		};
	}

	/// <summary>
	/// 1.2.0 (items review, item 5): how the focus comes back: as keyboard focus (its ring, a card's buttons and lift) only
	/// when the keyboard put it there; focus a click gave comes back as the pointer's, which shows none of that, and Tab still
	/// goes on from it.
	/// </summary>
	internal static FocusState RestoreAs(bool fromKeyboard) => fromKeyboard ? FocusState.Keyboard : FocusState.Pointer;

	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

	/// <summary>A page that turns out shorter than it was gets its content this long to grow before it is scrolled to its end.</summary>
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

	private string _key = "";
	private DispatcherTimer? _timer;

	/// <returns>The place the page is coming back to (Back or Forward), else null.</returns>
	public PagePlace? Arrived(NavigationEventArgs e)
	{
		_key = PagePlaces.Key(_page.GetType().Name, (e.Parameter as BaseItemDto)?.Id);
		_lastFocus = null;
		if (PagePlaces.ForReturn(_key, e.NavigationMode is NavigationMode.Back or NavigationMode.Forward) is { } place)
		{
			Restore(place);
			return place;
		}

		return null;
	}

	/// <param name="selectedSeasonId">The series page's shown season, to show it again on Back.</param>
	public void Left(Guid? selectedSeasonId = null)
	{
		_timer?.Stop();
		if (_key.Length == 0)
		{
			return;
		}

		Guid? item = null;
		IReadOnlyList<int>? path = null;
		var fromKeyboard = false;
		DependencyObject? focused = _page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject now && IsInPage(now) ? now : null;
		var state = focused is UIElement element ? element.FocusState : FocusState.Unfocused;
		if (focused is null && _lastFocus?.TryGetTarget(out var last) == true && IsInPage(last))
		{
			focused = last;
			state = _lastFocusState;
		}

		if (focused is not null)
		{
			item = ItemOf(focused);
			path = item is null ? PathOf(focused) : null;
			fromKeyboard = state == FocusState.Keyboard;
		}

		PagePlaces.Save(_key, new PagePlace(_scroller.VerticalOffset, item, path, fromKeyboard, selectedSeasonId));
	}

	private void Restore(PagePlace place)
	{
		var deadline = DateTime.UtcNow + Patience;
		DateTime? loadedAt = null;
		var scrolled = false;
		var focused = place.FocusedItemId is null && place.FocusedPath is null;
		_timer?.Stop();
		_timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
		_timer.Tick += (_, _) =>
		{
			var now = DateTime.UtcNow;
			if (_loaded())
			{
				loadedAt ??= now;
			}

			if (!scrolled)
			{
				var settled = loadedAt is { } at && now - at >= Settle;
				if (PagePlaces.OffsetToRestore(place.VerticalOffset, _scroller.ScrollableHeight, settled || now >= deadline) is { } offset)
				{
					_scroller.ChangeView(null, offset, null, disableAnimation: true);
					scrolled = true;
				}
			}
			else if (!focused)
			{
				// the user has already put the focus somewhere in the page: leave it there
				if (_page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject current && IsInPage(current) && !ReferenceEquals(current, _scroller))
				{
					focused = true;
				}
				else if (Target(place) is Control control && control.Focus(RestoreAs(place.FocusFromKeyboard)))
				{
					focused = true;
				}
			}

			if ((scrolled && focused) || now >= deadline)
			{
				_timer?.Stop();
			}
		};
		_timer.Start();
	}

	private DependencyObject? Target(PagePlace place) =>
		place.FocusedItemId is { } id ? Find(_page, id) : place.FocusedPath is { } path ? ElementAt(path) : null;

	private bool IsInPage(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, _page))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The item of the card or episode row the element is in (or is).</summary>
	private static Guid? ItemOf(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			switch (node)
			{
				case BaseItemDtoCard card:
					return card.Model?.Dto.Id;
				case EpisodeRow row:
					return row.Dto?.Id;
			}
		}

		return null;
	}

	/// <summary>The first card or episode row of the page showing that item.</summary>
	private static Control? Find(DependencyObject root, Guid id)
	{
		var count = VisualTreeHelper.GetChildrenCount(root);
		for (var i = 0; i < count; i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			switch (child)
			{
				case BaseItemDtoCard card when card.Model?.Dto.Id == id:
					return card;
				case EpisodeRow row when row.Dto?.Id == id:
					return row;
			}

			if (Find(child, id) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>Where the element is in the page: the child index at each level from the page down.</summary>
	private List<int>? PathOf(DependencyObject element)
	{
		var path = new List<int>();
		for (var node = element; !ReferenceEquals(node, _page);)
		{
			var parent = VisualTreeHelper.GetParent(node);
			if (parent is null)
			{
				return null;
			}

			var index = -1;
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
			{
				if (ReferenceEquals(VisualTreeHelper.GetChild(parent, i), node))
				{
					index = i;
					break;
				}
			}

			path.Add(index);
			node = parent;
		}

		path.Reverse();
		return path;
	}

	private DependencyObject? ElementAt(IReadOnlyList<int> path)
	{
		DependencyObject node = _page;
		foreach (var index in path)
		{
			if (index < 0 || index >= VisualTreeHelper.GetChildrenCount(node))
			{
				return null;
			}

			node = VisualTreeHelper.GetChild(node, index);
		}

		return node;
	}
}
