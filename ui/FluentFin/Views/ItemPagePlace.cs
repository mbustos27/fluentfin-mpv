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
/// season and episode pages each make one (their OnNavigatedTo and OnNavigatedFrom call it).
/// </summary>
internal sealed class ItemPagePlace(Page page, ScrollViewer scroller, Func<bool> loaded)
{
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(6);

	/// <summary>A page that turns out shorter than it was gets its content this long to grow before it is scrolled to its end.</summary>
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);

	private string _key = "";
	private DispatcherTimer? _timer;

	public void Arrived(NavigationEventArgs e)
	{
		_key = PagePlaces.Key(page.GetType().Name, (e.Parameter as BaseItemDto)?.Id);
		if (PagePlaces.ForReturn(_key, e.NavigationMode is NavigationMode.Back or NavigationMode.Forward) is { } place)
		{
			Restore(place);
		}
	}

	public void Left()
	{
		_timer?.Stop();
		if (_key.Length == 0)
		{
			return;
		}

		Guid? item = null;
		IReadOnlyList<int>? path = null;
		if (page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused && IsInPage(focused))
		{
			item = ItemOf(focused);
			path = item is null ? PathOf(focused) : null;
		}

		PagePlaces.Save(_key, new PagePlace(scroller.VerticalOffset, item, path));
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
			if (loaded())
			{
				loadedAt ??= now;
			}

			if (!scrolled)
			{
				var settled = loadedAt is { } at && now - at >= Settle;
				if (PagePlaces.OffsetToRestore(place.VerticalOffset, scroller.ScrollableHeight, settled || now >= deadline) is { } offset)
				{
					scroller.ChangeView(null, offset, null, disableAnimation: true);
					scrolled = true;
				}
			}
			else if (!focused)
			{
				// the user has already put the focus somewhere in the page: leave it there
				if (page.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject current && IsInPage(current) && !ReferenceEquals(current, scroller))
				{
					focused = true;
				}
				else if (Target(place) is Control control && control.Focus(FocusState.Programmatic))
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
		place.FocusedItemId is { } id ? Find(page, id) : place.FocusedPath is { } path ? ElementAt(path) : null;

	private bool IsInPage(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, page))
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
		for (var node = element; !ReferenceEquals(node, page);)
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
		DependencyObject node = page;
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
