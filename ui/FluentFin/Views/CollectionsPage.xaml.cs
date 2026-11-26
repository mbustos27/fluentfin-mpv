using FluentFin.Controls;
using FluentFin.Converters;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
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
		// 1.8.1 (the 1.8.0 release gate's Drive-in Collections cell, 1.3 to 1.5 s under load): kept whole for Back, as Home, the
		// libraries and the search results are (NavigationService.KeptWhole): made anew, its first layout took 0.65 to 1.75 s
		// under load before the tile could take the keyboard ("page loaded 1754, try 2 1778"), and 0.1 s on a quiet PC
		NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;

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

	// Nixie 1.7.4: the page hears its view model's state while it is shown (it added its handler in its constructor, and Back hands a
	// kept view model to a new page: the page left stayed alive with it); a view model handed over already loaded (Back, Forward)
	// has no Status change to wait for: the arrival focus is taken at once
	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		// Nixie 1.7.4: Back (or Forward) to the page: the tile it was left from and how far down it was (the section's first tile
		// took the keyboard, at the top)
		_returnTo = e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward
			&& LeftAt.TryGetValue(PlaceKey(e.Parameter), out var left) ? left : null;
		ViewModel.State.PropertyChanged -= OnStateChanged;
		ViewModel.State.PropertyChanged += OnStateChanged;
		_shownAsItWas = ViewModel.State.IsLoaded; // 1.8.1
		_focused = false; // 1.8.1: the page kept whole comes again on Back: its arrival focus (the tile left from) is this visit's
		if (ViewModel.State.IsLoaded)
		{
			OnStateChanged(ViewModel.State, new System.ComponentModel.PropertyChangedEventArgs(nameof(Core.ViewModels.LoadState.Status)));
		}
	}

	protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		ViewModel.State.PropertyChanged -= OnStateChanged;
		base.OnNavigatedFrom(e);
	}

	// Nixie 1.7.4: per place of the page (its section, a See all), the tile left from (the kept view model's own tile, held weakly)
	// and how far down the page was
	private static readonly Dictionary<string, (WeakReference<CollectionTile>? Tile, double Offset)> LeftAt = [];
	private (WeakReference<CollectionTile>? Tile, double Offset)? _returnTo;

	private static string PlaceKey(object? parameter) => parameter switch
	{
		CollectionsPlace place => place.ToString(),
		CollectionsSection section => new CollectionsPlace(section).ToString(),
		_ => new CollectionsPlace(CollectionsSection.All).ToString(),
	};

	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		CollectionTile? tile = null;
		for (var node = XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as DependencyObject : null; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (node is FrameworkElement { Tag: CollectionTile focused })
			{
				tile = focused;
				break;
			}
		}

		LeftAt[PlaceKey(ViewModel.Place)] = (tile is null ? null : new WeakReference<CollectionTile>(tile), PageScroller.VerticalOffset);
		base.OnNavigatingFrom(e);
	}

	/// <summary>Nixie 1.7.4: Back's place: the page as far down as it was, the tile left from given the keyboard; false when it is not there.</summary>
	private bool FocusReturned()
	{
		if (_returnTo is not { } place)
		{
			return false;
		}

		if (!IsLoaded)
		{
			return false; // 1.8.1: a try at the normal priority can come before the page's first layout: again once it is in the window
		}

		PageScroller.ScrollTo(0, place.Offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
		if (place.Tile?.TryGetTarget(out var tile) != true)
		{
			return FocusSection(); // (no tile was focused, or the page was made anew: its section's first tile, as on arrival)
		}

		if (ViewModel.IsSeeAll)
		{
			var at = IndexIn(ViewModel.GridTiles, tile!);
			return at >= 0 && GridRepeater.GetOrCreateElement(at) is Control element && element.Focus(FocusState.Programmatic);
		}

		foreach (var row in ViewModel.Rows)
		{
			var index = IndexIn(row.Tiles, tile!);
			if (index >= 0)
			{
				return RowsView.ContainerFromItem(row) is FrameworkElement container && FindShelf(container)?.FocusIndex(index, FocusState.Programmatic) == true;
			}
		}

		return FocusSection(); // the tile is no longer on the page
	}

	private static int IndexIn(IReadOnlyList<CollectionTile> tiles, CollectionTile tile)
	{
		for (var i = 0; i < tiles.Count; i++)
		{
			if (ReferenceEquals(tiles[i], tile))
			{
				return i;
			}
		}

		return -1;
	}

	private void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(Core.ViewModels.LoadState.Status) && ViewModel.State.IsLoaded && !_focused)
		{
			_focused = true;
			_placeTry = 0; // 1.8.1
			_arrivalStarted = Environment.TickCount64; // Nixie 1.7.5
			TakeArrivalFocus(ArrivalTries);
		}
	}

	/// <summary>
	/// How many times the arrival focus is tried while the rows' tiles are still being made (150 ms apart, and more under load:
	/// each try waits for the page's other work). Nixie 1.7.5: 40 (12 until 1.7.4, under two seconds).
	/// </summary>
	private const int ArrivalTries = 40;

	// Nixie 1.7.5: when the arrival focus was first tried (Environment.TickCount64), for the line that says why it stopped trying
	private long _arrivalStarted;

	// 1.8.1: the arrival focus's tries this visit (Back's place line says which one took the tile)
	private int _placeTry;

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
			// 1.8.1 (rule 10): Back's place line names each try (the gate's Drive-in cell took 1.3 to 1.5 s under load)
			_placeTry++;
			if (_returnTo is not null && _placeTry <= 3)
			{
				NavigationService.PlaceStep(this, $"try {_placeTry}{(IsLoaded ? "" : " (not in the window)")}");
			}

			// Nixie 1.7.4: Back: the tile left from, where the page was (else, and on arrival, the section's first tile)
			if (ArrivalFocus.TryTake(this, _returnTo is null ? "the section's first tile" : "the tile Back returns to", () => FocusReturned() || (_returnTo is null && FocusSection())))
			{
				EndArrivalTries();
				NavigationService.PlacePutBack(this, $"tile and place (try {_placeTry})"); // 1.8.1 (rule 10): once per Back
				return;
			}

			// Nixie 1.7.5 (KNOWN-ISSUES, 1.6.1 and 1.7.3): the tries that end say why (they ended with no line, and the keyboard
			// stayed on Back)
			if (ArrivalFocus.UserActedAt > ArrivalFocus.ArrivedAt + ArrivalFocus.GraceMs)
			{
				EndArrivalTries();
				ArrivalStopped("the user moved first");
				return;
			}

			var next = NextArrivalTry(IsLoaded, ArrivalFocus.Shown(this), tries);
			var mine = EndArrivalTries();
			if (next is ArrivalRetry.None)
			{
				ArrivalStopped(ArrivalFocus.Shown(this) ? $"no tile took the keyboard in {ArrivalTries} tries" : "the page is no longer shown");
			}

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
				// Nixie 1.7.5: held by the page until it has ticked or the tries end (only a local held it: a timer the collector
				// takes first never ticks, and the tries stopped with no line, under load)
				var timer = _arrivalTimer = DispatcherQueue.CreateTimer();
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
		}, ArrivalPriority(_returnTo is not null, _shownAsItWas));

	/// <summary>
	/// 1.8.1 (the 1.8.0 release gate's Drive-in Collections cell, 1.3 to 1.5 s under load): the tries' priority. Back's tile on a
	/// page shown as it was (its view model kept, loaded: its rows are there) at the normal priority, as a library's and the
	/// search results' place since 1.7.5: on the low one the first try waited for the window's other work, 700 ms after the page
	/// was in the window under load ("page loaded 134, try 1 843"). An arrival's tries stay low: its tiles come later.
	/// </summary>
	public static Microsoft.UI.Dispatching.DispatcherQueuePriority ArrivalPriority(bool returning, bool shownAsItWas) =>
		returning && shownAsItWas ? Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal : Microsoft.UI.Dispatching.DispatcherQueuePriority.Low;

	// 1.8.1: the view model came loaded (Back or Forward to a kept page), noted on arrival
	private bool _shownAsItWas;

	// any try scheduled before is no longer to run (its Loaded handler and its timer go); returns the number of the next one
	private int EndArrivalTries()
	{
		if (_arrivalOnLoaded is { } handler)
		{
			Loaded -= handler;
			_arrivalOnLoaded = null;
		}

		_arrivalTimer?.Stop();
		_arrivalTimer = null;
		return ++_arrivalTry;
	}

	// Nixie 1.7.5: the retry's timer, held while it waits
	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _arrivalTimer;

	// Nixie 1.7.5: why the arrival focus stopped trying with no tile focused, and after how long (a duration only)
	private void ArrivalStopped(string why) =>
		App.GetService<ILogger<CollectionsPage>>().LogInformation("[focus] CollectionsPage: the arrival focus stops trying after {Ms} ms: {Why}",
			Environment.TickCount64 - _arrivalStarted, why);

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

	// 1.8: See all's direction button: the grid runs the other way (the button keeps the keyboard)
	private void GridDirection_Click(object sender, RoutedEventArgs e) => ViewModel.ReverseGrid();
}
