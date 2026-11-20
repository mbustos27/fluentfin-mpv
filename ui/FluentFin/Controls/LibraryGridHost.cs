using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Controls;

/// <summary>
/// Nixie 1.4.0: what every theme's library page did in four copies, once: the focus a jump, a Back or an arrival asks for
/// (<see cref="LibraryViewModel.FocusRequest"/>), Back's scroll before its card, an A to Z jump's row put at the top (the
/// UX review's X16), the first poster focused on arrival (X8, item 4), type-to-jump (item 4), and the letter in view and
/// the position readout from the first title in view (item 13). For the pages whose grid is a <see cref="CardGrid"/>
/// (Classic, Drive-in, Cinematic); Editorial's lines do the same over their own grid.
/// </summary>
internal sealed class LibraryGridHost
{
	private readonly Page _page;
	private readonly LibraryViewModel _vm;
	private readonly ScrollView _scroller;
	private readonly ItemsRepeater _repeater;
	private readonly CardGrid _grid;

	// how the card asked for gets the focus: a jump by the keyboard, the keyboard's; by a click, or an arrival, the program's
	private FocusState _jumpFocus = FocusState.Keyboard;
	private bool _jumped;
	private bool _focusOnLoad;
	private int _returnScroll = -1;
	private (int Index, FocusState State) _returnFocus = (-1, FocusState.Keyboard);
	private bool _returnLate;
	private bool _viewQueued;

	public LibraryGridHost(Page page, LibraryViewModel vm, ScrollView scroller, ItemsRepeater repeater, CardGrid grid, LetterBar? letters = null)
	{
		_page = page;
		_vm = vm;
		_scroller = scroller;
		_repeater = repeater;
		_grid = grid;

		scroller.ScrollCompleted += Scroller_ScrollCompleted;
		page.Loaded += FocusOnLoad;
		scroller.Loaded += FocusOnLoad;
		grid.SetPlace(0, vm.TotalCount);
		grid.TypeAhead = (typed, from) => _vm.TypeAhead(typed, from);
		// R or F5 (a new view model for the same library): the sort, the filters and the card are put back, as for Back
		// (R and F5 keep the place; the lit tab chosen again starts at the top: LastRefreshKeepsPlace false)
		vm.ReloadedInPlace = () => App.GetService<Contracts.Services.INavigationService>() is Services.NavigationService { LastNavigationWasRefresh: true, LastRefreshKeepsPlace: true };
		// after R, a user who moved first keeps the keyboard where they put it (the refresh's own restore yields the same way)
		vm.FocusYields = () => App.GetService<Contracts.Services.INavigationService>() is Services.NavigationService { LastNavigationWasRefresh: true } && Services.PageRefresh.UserMovedSinceRefresh;
		vm.PropertyChanged += (_, e) =>
		{
			switch (e.PropertyName)
			{
				case nameof(LibraryViewModel.TotalCount):
					_grid.SetPlace(0, _vm.TotalCount);
					QueueView();
					break;
				case nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading):
					FocusRequested();
					break;
				case nameof(LibraryViewModel.SortBy) or nameof(LibraryViewModel.Order) or nameof(LibraryViewModel.AppliedFilters):
					// another order or another set of titles starts at its top (the old place would be the middle of anything)
					if (!_vm.IsLoading)
					{
						ToTop();
					}

					QueueView();
					break;
			}
		};
		((System.Collections.Specialized.INotifyCollectionChanged)vm.Items).CollectionChanged += (_, _) => QueueView();
		repeater.ElementIndexChanged += (_, _) => QueueView();

		if (letters is not null)
		{
			letters.Jumped += (_, e) => Jumping(e.FocusState);
		}

		scroller.ViewChanged += (_, _) => QueueView();
		repeater.ElementPrepared += (_, _) => QueueView();
		// the letter follows the keyboard along the grid too
		repeater.GotFocus += (_, _) => QueueView();
	}

	/// <summary>A page's own A to Z (Drive-in's board) was pressed: its card gets the focus the way the letter was pressed, its row at the top.</summary>
	public void Jumping(FocusState state)
	{
		_jumpFocus = state;
		_jumped = true;
	}

	public void OnNavigatedTo(NavigationEventArgs e)
	{
		_vm.ComingBack = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward;
		_jumpFocus = FocusState.Keyboard;
		_jumped = false;
	}

	public void OnNavigatedFrom() => _vm.LeftAt(_grid.LastItem as BaseItemViewModel, _scroller.VerticalOffset);

	private void FocusOnLoad(object sender, RoutedEventArgs e)
	{
		if (_focusOnLoad)
		{
			_focusOnLoad = false;
			FocusRequested();
		}
	}

	/// <summary>The card a jump, a Back or the arrival asked for: focused once the grid has it (it may still be loading).</summary>
	private void FocusRequested()
	{
		if (_vm.IsLoading || (_vm.FocusRequest < 0 && _vm.ReturnOffset is null))
		{
			return;
		}

		_page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (_vm.IsLoading)
			{
				return;
			}

			// a library that answers at once (Back: 100 ms) can be in before the page, or the grid, is in the window
			if (_page.XamlRoot is null || !_page.IsLoaded || _scroller.XamlRoot is null || !_scroller.IsLoaded)
			{
				_focusOnLoad = true;
				return;
			}

			_page.UpdateLayout();
			var index = _vm.FocusRequest;
			_vm.FocusRequest = -1; // done: a Retry later does not take the focus again
			var arrival = _vm.ArrivalFocus;
			_vm.ArrivalFocus = false;
			var late = _vm.LateFocus;
			_vm.LateFocus = false;
			var jumped = _jumped;
			_jumped = false;
			var state = arrival ? FocusState.Programmatic : _jumpFocus;
			_jumpFocus = FocusState.Keyboard;

			// Back: how far down the grid was (so the card is where it was on screen), then the card
			if (_vm.ReturnOffset is { } offset && Math.Abs(Math.Min(offset, _scroller.ScrollableHeight) - _scroller.VerticalOffset) >= 1)
			{
				_vm.ReturnOffset = null;
				_scroller.UpdateLayout();
				_returnScroll = _scroller.ScrollTo(0, Math.Min(offset, _scroller.ScrollableHeight), new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
				_returnFocus = (index, state);
				_returnLate = late;
				return;
			}

			_vm.ReturnOffset = null;
			if (index >= 0 && _vm.FocusYields?.Invoke() != true && !(late && FocusIsElsewhere()))
			{
				_grid.FocusAndHold(index, state, atTop: jumped);
			}
		});
	}

	private void Scroller_ScrollCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs args)
	{
		if (args.CorrelationId != _returnScroll)
		{
			return;
		}

		_returnScroll = -1;
		var late = _returnLate;
		_returnLate = false;
		if (_returnFocus.Index >= 0 && _vm.FocusYields?.Invoke() != true && !(late && FocusIsElsewhere()))
		{
			_repeater.UpdateLayout();
			_grid.FocusAndHold(_returnFocus.Index, _returnFocus.State);
		}

		_returnFocus = (-1, FocusState.Keyboard);
	}

	/// <summary>
	/// Nixie 1.4.1: the keyboard is on something outside the grid (the search box, the toolbar, the title bar): a place put
	/// back late (<see cref="LibraryViewModel.LateFocus"/>) only scrolls the grid. Nothing focused, the page itself or the
	/// grid is not elsewhere.
	/// </summary>
	private bool FocusIsElsewhere()
	{
		if (_page.XamlRoot is not { } root || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not DependencyObject focused
			|| ReferenceEquals(focused, _page))
		{
			return false;
		}

		for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, _scroller))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>The grid to its top; a card that had the keyboard hands it to the first card (its own may have gone or moved).</summary>
	private void ToTop()
	{
		var hadFocus = _page.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is DependencyObject focused && _grid.IndexOfElement(focused) >= 0;
		if (_scroller.VerticalOffset > 0)
		{
			_scroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
		}

		if (hadFocus)
		{
			_page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
			{
				_repeater.UpdateLayout();
				_grid.FocusIndex(0, FocusState.Keyboard);
			});
		}
	}

	// ---- the letter in view and the readout ----------------------------------------------------------------------------

	private void QueueView()
	{
		if (_viewQueued)
		{
			return;
		}

		_viewQueued = true;
		_page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			_viewQueued = false;
			if (!_vm.IsLoading)
			{
				_vm.ShowingFrom(ShownIndex());
			}
		});
	}

	/// <summary>Where the user is: the card the keyboard is on while it is in view (a jump's M title, not the L titles before it on its row), else the first card in view.</summary>
	private int ShownIndex()
	{
		if (_page.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is FrameworkElement focused
			&& _grid.IndexOfElement(focused) is >= 0 and var index)
		{
			try
			{
				var top = focused.TransformToVisual(_scroller).TransformPoint(default).Y;
				if (top + focused.ActualHeight > 0 && top < _scroller.ActualHeight)
				{
					return index;
				}
			}
			catch (ArgumentException)
			{
			}
		}

		return FirstVisibleIndex(_repeater, _scroller);
	}

	/// <summary>The first and the last card in view (more than half of each inside it), (-1, -1) when none is drawn.</summary>
	public static (int First, int Last) VisibleRange(ItemsRepeater repeater, FrameworkElement viewport)
	{
		int first = -1, last = -1;
		var height = viewport.ActualHeight;
		var count = VisualTreeHelper.GetChildrenCount(repeater);
		for (var i = 0; i < count; i++)
		{
			if (VisualTreeHelper.GetChild(repeater, i) is not FrameworkElement { ActualHeight: > 0 } element
				|| repeater.GetElementIndex(element) is not (>= 0 and var index))
			{
				continue;
			}

			try
			{
				var top = element.TransformToVisual(viewport).TransformPoint(default).Y;
				var middle = top + element.ActualHeight * 0.5;
				if (middle > 0 && middle < height)
				{
					first = first < 0 ? index : Math.Min(first, index);
					last = Math.Max(last, index);
				}
			}
			catch (ArgumentException)
			{
			}
		}

		return (first, last);
	}

	/// <summary>The index of the first card in view (more than half of it below the view's top), -1 when none is drawn.</summary>
	public static int FirstVisibleIndex(ItemsRepeater repeater, FrameworkElement viewport)
	{
		var best = -1;
		var height = viewport.ActualHeight;
		var count = VisualTreeHelper.GetChildrenCount(repeater);
		for (var i = 0; i < count; i++)
		{
			if (VisualTreeHelper.GetChild(repeater, i) is not FrameworkElement { ActualHeight: > 0 } element
				|| repeater.GetElementIndex(element) is not (>= 0 and var index))
			{
				continue;
			}

			try
			{
				var top = element.TransformToVisual(viewport).TransformPoint(default).Y;
				if (top + element.ActualHeight * 0.5 > 0 && top < height && (best < 0 || index < best))
				{
					best = index;
				}
			}
			catch (ArgumentException)
			{
			}
		}

		return best;
	}
}
