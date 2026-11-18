using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;


namespace FluentFin.Views;

/// <summary>
/// An empty page that can be used on its own or navigated to within a Frame.
/// </summary>
public sealed partial class HomePage : Page
{
	public HomeViewModel ViewModel { get; } = App.GetService<HomeViewModel>();

	// Seaglass 1.1.4: how long the notice after a removal from Continue Watching stays (while nothing holds it)
	internal static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(10);
	internal static readonly TimeSpan NoticeTimeAfterHold = TimeSpan.FromSeconds(5);

	private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;
	private bool _noticePointer;
	private bool _noticeFocus;

	public HomePage()
	{
		InitializeComponent();

		_noticeTimer = DispatcherQueue.CreateTimer();
		_noticeTimer.IsRepeating = false;
		_noticeTimer.Tick += (_, _) =>
		{
			if (ViewModel.Notice is { } notice && !_noticePointer && !_noticeFocus)
			{
				ViewModel.DismissNotice(notice);
			}
		};
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(HomeViewModel.Notice))
			{
				OnNoticeChanged();
			}
		};
		Unloaded += (_, _) => _noticeTimer.Stop();
		Loaded += (_, _) => RestorePlace();
	}

	// ---- Seaglass 1.1.4 (interaction audit, item 5): Back to Home puts the user where they were ------------------------

	private HomePlace? _restore;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		_restore = e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward
			? HomePlace.Recall(ViewModel.JellyfinClient.UserId)
			: null;
		base.OnNavigatedTo(e);
	}

	/// <summary>Leaving Home: how far down it was and the card the user was on (focused or clicked), for Back.</summary>
	protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		var row = FluentFin.Controls.ShelfRow.LastUsed is { } used && IsMine(used) ? used : null;
		var item = row?.LastItem as BaseItemViewModel;
		HomePlace.Remember(ViewModel.JellyfinClient.UserId, new HomePlace(PageScroller.VerticalOffset, item is null ? null : row!.Header, item?.Id));
		base.OnNavigatedFrom(e);
	}

	private bool IsMine(Microsoft.UI.Xaml.DependencyObject element)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, this))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Back: once the rows are there (the saved page shows at once; else when they load, at most a few seconds), the page goes
	/// back to how far down it was and the card that was left gets the focus (not the title bar, where it used to land).
	/// </summary>
	private void RestorePlace()
	{
		if (_restore is not { } place)
		{
			return;
		}

		_restore = null;
		var started = System.Diagnostics.Stopwatch.GetTimestamp();
		var timer = DispatcherQueue.CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(50);
		timer.Tick += (t, _) =>
		{
			var waited = System.Diagnostics.Stopwatch.GetElapsedTime(started);
			if (XamlRoot is null)
			{
				t.Stop();
				return;
			}

			if (PageScroller.ScrollableHeight < place.Offset - 1 && ViewModel.IsLoading && waited < TimeSpan.FromSeconds(4))
			{
				return; // the rows are not all there yet
			}

			t.Stop();
			PageScroller.ChangeView(null, Math.Min(place.Offset, PageScroller.ScrollableHeight), null, disableAnimation: true);
			PageScroller.UpdateLayout();
			if (place.Row is { } header && place.ItemId is { } id)
			{
				FluentFin.Controls.ShelfRow.Shown.FirstOrDefault(r => r.Header == header && IsMine(r))?
					.FocusItem(x => x is BaseItemViewModel vm && vm.Id == id, Microsoft.UI.Xaml.FocusState.Programmatic);
			}
		};
		timer.Start();
	}

	public static Microsoft.UI.Xaml.Visibility NoticeVisibility(HomeNotice? notice) =>
		notice is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	public static Microsoft.UI.Xaml.Visibility UndoVisibility(HomeNotice? notice) =>
		notice?.CanUndo == true ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	private void OnNoticeChanged()
	{
		_noticeTimer.Stop();
		if (ViewModel.Notice is null)
		{
			return;
		}

		_noticeTimer.Interval = NoticeTime;
		_noticeTimer.Start();
		// the card that had the focus left with the row's last card: the keyboard goes on to the Undo
		DispatcherQueue.TryEnqueue(() =>
		{
			if (XamlRoot is not null && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is null && ViewModel.Notice?.CanUndo == true)
			{
				UndoButton.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
			}
		});
	}

	private void HoldNotice(bool pointer, bool focus)
	{
		_noticePointer = pointer;
		_noticeFocus = focus;
		if (pointer || focus)
		{
			_noticeTimer.Stop();
		}
		else if (ViewModel.Notice is not null)
		{
			_noticeTimer.Interval = NoticeTimeAfterHold;
			_noticeTimer.Start();
		}
	}

	private void Notice_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoldNotice(true, _noticeFocus);

	private void Notice_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoldNotice(false, _noticeFocus);

	private void Notice_GotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => HoldNotice(_noticePointer, true);

	private void Notice_LostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => HoldNotice(_noticePointer, false);

	private async void UndoButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await ViewModel.UndoRemoval();

	private void NoticeCloseButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (ViewModel.Notice is { } notice)
		{
			ViewModel.DismissNotice(notice);
		}
	}

	private async void UndoInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (ViewModel.Notice?.CanUndo != true || FluentFin.Services.TextInput.IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)))
		{
			return; // typing: Ctrl+Z is the text box's
		}

		args.Handled = true;
		await ViewModel.UndoRemoval();
	}

	// fluentfin-mpv: a section is on screen while it loads (placeholder cards) and afterwards only if it has items
	public static Microsoft.UI.Xaml.Visibility SectionVisibility(bool isLoading, bool hasItems) =>
		isLoading || hasItems ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	public static Microsoft.UI.Xaml.Visibility LoadedVisibility(bool isLoading) =>
		isLoading ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	private void NextUpInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.TextInput.IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)))
		{
			return; // typing: N and C are letters then
		}

		// Seaglass 1.1.4: to the row, never playing (a single letter must not start a video)
		args.Handled = NextUpRow.FocusRow();
    }

	private void ContinueInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.TextInput.IsTextInput(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = ContinueRow.FocusRow();
    }

	// Seaglass scrolling: "See all" on a row. Recently Added opens its library; Continue Watching and Next Up, which have no
	// library of their own, open their items as a grid.
	private void ContinueSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Continue Watching", [.. ViewModel.ContinueItems]));

	private void NextUpSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Next Up", [.. ViewModel.NextUpItems]));

	private void RecentSeeAll(object sender, EventArgs e)
	{
		if (sender is FluentFin.Controls.ShelfRow { Tag: { } library })
		{
			App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<LibraryViewModel>(library);
		}
	}
}
