using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace FluentFin.Views;


public sealed partial class LibraryPage : Page
{
	public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

	// Seaglass scrolling: the grid's keyboard and Narrator model, and the wheel over the rest of the page
	private readonly CardGrid _grid;

	// Seaglass 1.1.4: the A-Z jump was used with the keyboard (its card then gets the keyboard focus, a click's the program's)
	private FocusState _jumpFocus = FocusState.Keyboard;

	// Seaglass 1.1.4: when the page was made, for the first card's time in the log ("[perf] library: ..."), which the harness's
	// then-library-speed reads: the app's own time, without UI Automation's
	private readonly long _openedAt = System.Diagnostics.Stopwatch.GetTimestamp();
	private bool _firstCardLogged;

	public LibraryPage()
	{
		InitializeComponent();

		GridRepeater.ElementPrepared += (_, _) =>
		{
			if (!_firstCardLogged)
			{
				_firstCardLogged = true;
				Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(App.GetService<Microsoft.Extensions.Logging.ILogger<LibraryPage>>(),
					"[perf] library: first card {Ms} ms after the page was opened", (int)System.Diagnostics.Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds);
			}
		};

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		GridScroller.ScrollCompleted += GridScroller_ScrollCompleted;
		_grid.ForwardWheel(LibraryRoot);
		_grid.SetPlace(0, ViewModel.TotalCount);
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(LibraryViewModel.TotalCount))
			{
				_grid.SetPlace(0, ViewModel.TotalCount);
			}
			else if (e.PropertyName is nameof(LibraryViewModel.FocusRequest) or nameof(LibraryViewModel.IsLoading))
			{
				FocusRequested();
			}
		};

		// the A-Z jump's buttons: their place for Narrator ("3 of 26")
		LetterRepeater.ElementPrepared += (_, e) =>
		{
			AutomationProperties.SetPositionInSet(e.Element, e.Index + 1);
			AutomationProperties.SetSizeOfSet(e.Element, ViewModel.Letters.Count);
		};
	}

	// Seaglass 1.1.4: the sort menu shows the sort in use (also the one a Back put back)
	public static bool IsSort(ItemSortBy current, ItemSortBy item) => current == item;

	public static bool AnyOn(int activeFilters) => activeFilters > 0;

	public static Visibility LettersVisibility(IReadOnlyList<LetterJump> letters, LoadStatus status) =>
		letters.Count > 1 && BarVisibility(status) == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Seaglass 1.1.4 (layout audit, item 22): the bar (count, sort, filter, A-Z) only over a library that has titles to show.</summary>
	public static Visibility BarVisibility(LoadStatus status) => status is LoadStatus.Empty or LoadStatus.Failed ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Seaglass 1.1.4: coming back to the library (Back, Forward) puts the user where they were: the view model needs to know before it loads.</summary>
	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		ViewModel.ComingBack = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward;
		_jumpFocus = FocusState.Keyboard;
		base.OnNavigatedTo(e);
	}

	/// <summary>Seaglass 1.1.4: leaving for an item: the card the user was on (the grid's last-focused card) is kept for Back.</summary>
	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.LeftAt(_grid.LastItem as BaseItemViewModel, GridScroller.VerticalOffset);
		base.OnNavigatedFrom(e);
	}

	private void Letter_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Button { Tag: LetterJump jump } button)
		{
			_jumpFocus = button.FocusState is FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
			ViewModel.JumpToCommand.Execute(jump);
		}
	}

	// the card a jump or a Back asked for: focused once the grid has its page (it may still be loading)
	private void FocusRequested()
	{
		if (ViewModel.IsLoading || (ViewModel.FocusRequest < 0 && ViewModel.ReturnOffset is null))
		{
			return;
		}

		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (ViewModel.IsLoading || XamlRoot is null)
			{
				return;
			}

			// Back: how far down the grid was (so the card is where it was on screen), then the card. A ScrollView scrolls
			// on its next tick: the card is focused when that scroll is done, or it would be brought into view from the top
			GridRepeater.UpdateLayout();
			var index = ViewModel.FocusRequest;
			ViewModel.FocusRequest = -1; // done: a Retry later does not take the focus again
			var state = _jumpFocus;
			_jumpFocus = FocusState.Keyboard;
			if (ViewModel.ReturnOffset is { } offset && Math.Abs(Math.Min(offset, GridScroller.ScrollableHeight) - GridScroller.VerticalOffset) >= 1)
			{
				ViewModel.ReturnOffset = null;
				GridScroller.UpdateLayout();
				_returnScroll = GridScroller.ScrollTo(0, Math.Min(offset, GridScroller.ScrollableHeight), new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
				_returnFocus = (index, state);
				return;
			}

			ViewModel.ReturnOffset = null;
			if (index >= 0)
			{
				FocusCard(index, state);
			}
		});
	}

	private int _returnScroll = -1;
	private (int Index, FocusState State) _returnFocus = (-1, FocusState.Keyboard);

	private void GridScroller_ScrollCompleted(ScrollView sender, ScrollingScrollCompletedEventArgs args)
	{
		if (args.CorrelationId != _returnScroll)
		{
			return;
		}

		_returnScroll = -1;
		if (_returnFocus.Index >= 0)
		{
			GridRepeater.UpdateLayout();
			FocusCard(_returnFocus.Index, _returnFocus.State);
		}

		_returnFocus = (-1, FocusState.Keyboard);
	}

	/// <summary>
	/// Focuses card <paramref name="index"/>, and again for a moment if something else took the focus meanwhile: on Back, the
	/// page the user left leaves the window, and the focus it had goes to the title bar about when the grid is shown.
	/// </summary>
	private void FocusCard(int index, FocusState state)
	{
		_grid.FocusIndex(index, state);
		var tries = 0;
		var timer = DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(100);
		timer.Tick += (t, _) =>
		{
			if (XamlRoot is null || ++tries > 10)
			{
				t.Stop();
				return;
			}

			var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
			if (_grid.IndexOfElement(focused) == index)
			{
				t.Stop();
				return;
			}

			if (_grid.IndexOfElement(focused) < 0)
			{
				_grid.FocusIndex(index, state);
			}
			else
			{
				t.Stop(); // the user moved on in the grid
			}
		};
		timer.Start();
	}
}
