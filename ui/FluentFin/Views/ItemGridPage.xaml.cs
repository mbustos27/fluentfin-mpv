using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Views;

/// <summary>Seaglass scrolling: the grid behind a row's "See all" (<see cref="ItemGridViewModel"/>), with the Library's grid model.</summary>
public sealed partial class ItemGridPage : Page
{
	public ItemGridViewModel ViewModel { get; } = App.GetService<ItemGridViewModel>();

	private readonly CardGrid _grid;

	// Seaglass 1.1.4 (interaction audit, item 5): Back puts the grid as far down as it was and the focus on the card that was left
	private bool _comingBack;
	private int _returnScroll = -1;
	private int _returnIndex = -1;

	public ItemGridPage()
	{
		InitializeComponent();

		_grid = new CardGrid(GridScroller, GridList, GridRepeater);
		_grid.ForwardWheel(GridRoot);
		Loaded += (_, _) => RestorePlace();
		GridScroller.ScrollCompleted += (_, e) =>
		{
			if (e.CorrelationId != _returnScroll)
			{
				return;
			}

			_returnScroll = -1;
			FocusReturnCard();
		};
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_comingBack = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward;
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		ViewModel.LeftAt(_grid.LastItem as BaseItemViewModel, GridScroller.VerticalOffset);
		base.OnNavigatedFrom(e);
	}

	private void RestorePlace()
	{
		if (!_comingBack)
		{
			return;
		}

		_comingBack = false;
		DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
		{
			if (XamlRoot is null || ViewModel.ReturnPlace() is not { } place)
			{
				return;
			}

			GridRepeater.UpdateLayout();
			GridScroller.UpdateLayout();
			_returnIndex = place.Index;
			var offset = Math.Min(place.Offset, GridScroller.ScrollableHeight);
			if (Math.Abs(offset - GridScroller.VerticalOffset) >= 1)
			{
				_returnScroll = GridScroller.ScrollTo(0, offset, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
				return;
			}

			FocusReturnCard();
		});
	}

	private void FocusReturnCard()
	{
		if (_returnIndex >= 0)
		{
			GridRepeater.UpdateLayout();
			_grid.FocusAndHold(_returnIndex, FocusState.Programmatic);
		}

		_returnIndex = -1;
	}
}
