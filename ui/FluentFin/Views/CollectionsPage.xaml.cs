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

	// 1.7.1: the page has been in the window (before that, an arrival focus waits for it rather than giving up)
	private bool _wasLoaded;

	public CollectionsPage()
	{
		InitializeComponent();
		Loaded += (_, _) => _wasLoaded = true;
		ViewModel.State.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(Core.ViewModels.LoadState.Status) && ViewModel.State.IsLoaded && !_focused)
			{
				_focused = true;
				TakeArrivalFocus(ArrivalTries);
			}
		};
	}

	/// <summary>How many times the arrival focus is tried while the rows' tiles are still being made (150 ms apart).</summary>
	private const int ArrivalTries = 12;

	// the rows' tiles are made a layout pass or more after the rows are in (slower in some themes): the arrival focus is tried
	// again until a tile takes it, by the app's one arrival rule each time (never once the user has moved since the page came)
	private void TakeArrivalFocus(int tries) =>
		UiDispatch.Enqueue(DispatcherQueue, "the Collections page's arrival focus", () =>
		{
			if (ArrivalFocus.TryTake(this, "the section's first tile", FocusSection) || tries <= 1
				|| ArrivalFocus.UserActedAt > ArrivalFocus.ArrivedAt + ArrivalFocus.GraceMs)
			{
				return;
			}

			if (!IsLoaded)
			{
				// 1.7.1: loaded before the page is in the window (with the saved index the load takes milliseconds): tried again
				// once it is; a page that has left gives up
				if (!_wasLoaded)
				{
					void Once(object sender, RoutedEventArgs e)
					{
						Loaded -= Once;
						TakeArrivalFocus(tries - 1);
					}

					Loaded += Once;
				}

				return;
			}

			var timer = DispatcherQueue.CreateTimer();
			timer.Interval = TimeSpan.FromMilliseconds(150);
			timer.IsRepeating = false;
			timer.Tick += (_, _) => TakeArrivalFocus(tries - 1);
			timer.Start();
		}, Microsoft.UI.Dispatching.DispatcherQueuePriority.Low);

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
