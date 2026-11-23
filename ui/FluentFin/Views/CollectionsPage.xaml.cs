using FluentFin.Controls;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Views;

/// <summary>
/// Nixie 1.6: the Collections page (<see cref="CollectionsViewModel"/>). Arriving, the keyboard goes to the first tile of the
/// section it was opened at (by the app's one arrival rule: never once the user has moved since the page came).
/// </summary>
public sealed partial class CollectionsPage : Page
{
	public CollectionsViewModel ViewModel { get; } = App.GetService<CollectionsViewModel>();

	private bool _focused;

	// 1.7.3 (BH-171-m4): the reading line has shown on this visit: when it goes, its space stays (ShowReadingLine)
	private bool _lineShown;

	public CollectionsPage()
	{
		InitializeComponent();
		ViewModel.State.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Core.ViewModels.LoadState.Status) && ViewModel.State.IsLoaded && !_focused)
			{
				_focused = true;
				TakeArrivalFocus(ArrivalTries);
			}
		};

		// 1.7.3 (BH-171-m4): the reading line, from the view model while the page is in the window (a theme switch hands the view
		// model to the next page)
		Loaded += (_, _) =>
		{
			ViewModel.PropertyChanged -= OnViewModelChanged;
			ViewModel.PropertyChanged += OnViewModelChanged;
			ShowReadingLine();
		};
		Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelChanged;
		ShowReadingLine();
	}

	/// <summary>How many times the arrival focus is tried while the rows' tiles are still being made (150 ms apart).</summary>
	private const int ArrivalTries = 12;

	/// <summary>What the arrival focus does after a try that found no tile to take the keyboard (<see cref="NextArrivalTry"/>).</summary>
	public enum ArrivalRetry
	{
		/// <summary>No other try: the page has left, or it is in the window and its tries are used up.</summary>
		None,

		/// <summary>Again in 150 ms (the page is in the window: its tiles come a layout pass or more after its rows).</summary>
		Timer,

		/// <summary>Again when the page is in the window, with a fresh count of tries.</summary>
		Loaded,

		/// <summary>Again when the page is in the window (a fresh count), or in 150 ms, whichever comes first.</summary>
		LoadedOrTimer,
	}

	/// <summary>
	/// Nixie 1.7.3: what follows a try of the arrival focus that found no tile. In the window: the timer, while tries are left. Not
	/// in the window but still the page shown: when it is in the window again (Loaded, with a fresh count: its tiles come after
	/// that), and meanwhile on the timer, since a Loaded already raised may not come again. 1.7.1 gave up on a page that had been
	/// loaded before, and under load the page was loaded, then not, while still the page shown: the keyboard stayed on Back (1 of
	/// 7 Drive-in runs). A page that has left gives up.
	/// </summary>
	/// <param name="loaded">The page is in the window (FrameworkElement.IsLoaded).</param>
	/// <param name="shown">The page is still the one the shell shows.</param>
	/// <param name="tries">The tries left, counting the one that just failed.</param>
	public static ArrivalRetry NextArrivalTry(bool loaded, bool shown, int tries) =>
		!shown ? ArrivalRetry.None
		: loaded ? (tries > 1 ? ArrivalRetry.Timer : ArrivalRetry.None)
		: tries > 1 ? ArrivalRetry.LoadedOrTimer : ArrivalRetry.Loaded;

	// the newest scheduled try of the arrival focus: a try scheduled two ways (Loaded or the timer) runs once
	private int _arrivalTry;
	private RoutedEventHandler? _arrivalOnLoaded;

	// the rows' tiles are made a layout pass or more after the rows are in (slower in some themes): the arrival focus is tried
	// again until a tile takes it, by the app's one arrival rule each time (never once the user has moved since the page came)
	private void TakeArrivalFocus(int tries) =>
		UiDispatch.Enqueue(DispatcherQueue, "the Collections page's arrival focus", () =>
		{
			if (ArrivalFocus.TryTake(this, "the section's first tile", FocusSection)
				|| ArrivalFocus.UserActedAt > ArrivalFocus.ArrivedAt + ArrivalFocus.GraceMs)
			{
				EndArrivalTries();
				return;
			}

			var next = NextArrivalTry(IsLoaded, ArrivalFocus.Shown(this), tries);
			var mine = EndArrivalTries();
			if (next is ArrivalRetry.Loaded or ArrivalRetry.LoadedOrTimer)
			{
				void Once(object sender, RoutedEventArgs e)
				{
					if (mine == _arrivalTry)
					{
						EndArrivalTries();
						TakeArrivalFocus(ArrivalTries);
					}
				}

				_arrivalOnLoaded = Once;
				Loaded += Once;
			}

			if (next is ArrivalRetry.Timer or ArrivalRetry.LoadedOrTimer)
			{
				var timer = DispatcherQueue.CreateTimer();
				timer.Interval = TimeSpan.FromMilliseconds(150);
				timer.IsRepeating = false;
				// 1.7.3 fix round (the close crash's class): nothing once the app is closing, and never a throw out of the tick
				timer.Tick += UiDispatch.Tick("the Collections page's arrival focus, again", () =>
				{
					if (mine == _arrivalTry)
					{
						EndArrivalTries();
						TakeArrivalFocus(tries - 1);
					}
				});
				timer.Start();
			}
		}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

	// any try scheduled before is no longer to run (its Loaded handler goes); returns the number of the next one
	private int EndArrivalTries()
	{
		if (_arrivalOnLoaded is { } handler)
		{
			Loaded -= handler;
			_arrivalOnLoaded = null;
		}

		return ++_arrivalTry;
	}

	/// <summary>The reading line under the title: shown, gone with its space kept (the rows under it stay), or gone.</summary>
	public enum ReadingLineState
	{
		Gone,
		Shown,
		SpaceKept,
	}

	/// <summary>
	/// Nixie 1.7.3 (BH-171-m4, rule 7): the reading line while the first read runs (or says it stopped); when it goes, its space
	/// stays for the rest of the visit, so the rows under the keyboard and the pointer don't move up 40 px. A visit it never
	/// showed on has no space for it.
	/// </summary>
	public static ReadingLineState ReadingLineFor(bool shows, bool shownThisVisit) =>
		shows ? ReadingLineState.Shown : shownThisVisit ? ReadingLineState.SpaceKept : ReadingLineState.Gone;

	private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(CollectionsViewModel.ShowsReadingLine))
		{
			ShowReadingLine();
		}
	}

	private void ShowReadingLine()
	{
		var state = ReadingLineFor(ViewModel.ShowsReadingLine, _lineShown);
		_lineShown |= state == ReadingLineState.Shown;
		if (state == ReadingLineState.SpaceKept)
		{
			// its height as it was (20 px, or more with larger text), held by the panel; the line itself goes by its binding (the
			// words and the ring: nothing left for Narrator)
			ReadingPanel.MinHeight = Math.Max(ReadingPanel.MinHeight, ReadingPanel.ActualHeight);
		}

		ReadingPanel.Visibility = state == ReadingLineState.Gone ? Visibility.Collapsed : Visibility.Visible;
	}

	public static BitmapImage? SetArt(BaseItemDto? set) => BaseItemDtoConverters.GetImage(set, ImageType.Primary, 252);

	public static Visibility ShownUnless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>A section's kicker in capitals ("MADE BY NIXIE"); Narrator reads it as written, a level-2 heading.</summary>
	public static string Kicker(string text) => text.ToUpperInvariant();

	// the section the page was opened at: its row brought into view and its first tile focused (See all: the grid's first)
	private bool FocusSection()
	{
		if (ViewModel.IsSeeAll)
		{
			return GridRepeater.TryGetElement(0) is Control first && first.Focus(FocusState.Programmatic);
		}

		if (ViewModel.FocusRow is not { } row || RowsView.ContainerFromItem(row) is not FrameworkElement container)
		{
			return false;
		}

		var shelf = FindShelf(container);
		container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = false });
		return shelf?.FocusIndex(0, FocusState.Programmatic) == true;
	}

	private static ShelfRow? FindShelf(DependencyObject root)
	{
		var queue = new Queue<DependencyObject>([root]);
		while (queue.Count > 0)
		{
			var node = queue.Dequeue();
			if (node is ShelfRow shelf)
			{
				return shelf;
			}

			for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++)
			{
				queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
			}
		}

		return null;
	}

	private void Tile_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: CollectionTile tile })
		{
			ViewModel.Open(tile);
		}
	}

	private void Row_SeeAll(object? sender, EventArgs e)
	{
		if (sender is FrameworkElement { Tag: CollectionsRow row })
		{
			ViewModel.SeeAll(row);
		}
	}
}
