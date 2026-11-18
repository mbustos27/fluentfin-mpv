using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;


namespace FluentFin.Themes.DriveIn.Views;


/// <summary>
/// Drive-in: the Library (see DriveInLibraryPage.xaml), over the shared <see cref="LibraryViewModel"/>. The grid's keyboard,
/// Narrator and wheel model, the jump and Back's place are the shared Library page's (this code is its, the header aside).
/// </summary>
public sealed partial class DriveInLibraryPage : Page
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

	public DriveInLibraryPage()
	{
		InitializeComponent();

		GridRepeater.ElementPrepared += (_, _) =>
		{
			if (!_firstCardLogged)
			{
				_firstCardLogged = true;
				Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(App.GetService<Microsoft.Extensions.Logging.ILogger<DriveInLibraryPage>>(),
					"[perf] library: first card {Ms} ms after the page was opened", (int)System.Diagnostics.Stopwatch.GetElapsedTime(_openedAt).TotalMilliseconds);
			}
		};

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		GridScroller.ScrollCompleted += GridScroller_ScrollCompleted;
		void FocusOnLoad(object sender, RoutedEventArgs e)
		{
			if (_focusOnLoad)
			{
				_focusOnLoad = false;
				FocusRequested();
			}
		}

		Loaded += FocusOnLoad;
		GridScroller.Loaded += FocusOnLoad;
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

		// the letter tiles: the place among the letters that have titles, for Narrator ("3 of 24"); a resting tile has none
		LetterRepeater.ElementPrepared += (_, e) =>
		{
			if (e.Element is Button { Tag: LetterJump jump })
			{
				AutomationProperties.SetPositionInSet(e.Element, ViewModel.Letters.ToList().IndexOf(jump) + 1);
				AutomationProperties.SetSizeOfSet(e.Element, ViewModel.Letters.Count);
			}
		};

		// the header's strip of sky follows its size; the page's margins follow the window's width
		Header.SizeChanged += (_, e) => Sky.Layout = Controls.DriveInLayout.Strip(e.NewSize.Width, e.NewSize.Height);
		SizeChanged += (_, e) =>
		{
			var margin = Controls.DriveInLayout.MarginFor(e.NewSize.Width);
			HeaderContent.Padding = new Thickness(margin, 40, margin, 18);
			GridScroller.Padding = new Thickness(margin - 8, 24, margin - 8, 24);
		};
	}

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
			if (ViewModel.IsLoading)
			{
				return;
			}

			// a library that answers at once (Back: 100 ms) can be in before the page, or the grid (the loaded case of the
			// page's switch, put in when loading ends), is in the window: done once it is
			if (XamlRoot is null || !IsLoaded || GridScroller.XamlRoot is null || !GridScroller.IsLoaded)
			{
				_focusOnLoad = true;
				return;
			}

			// Back: how far down the grid was (so the card is where it was on screen), then the card. A ScrollView scrolls
			// on its next tick: the card is focused when that scroll is done, or it would be brought into view from the top
			UpdateLayout();
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
				_grid.FocusAndHold(index, state);
			}
		});
	}

	private bool _focusOnLoad;
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
			_grid.FocusAndHold(_returnFocus.Index, _returnFocus.State);
		}

		_returnFocus = (-1, FocusState.Keyboard);
	}
}
