using System.Collections.ObjectModel;
using FluentFin.Controls;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.Themes.DriveIn.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.DriveIn.Views;

/// <summary>Drive-in: Home (see DriveInHomePage.xaml), over the shared <see cref="HomeViewModel"/>.</summary>
public sealed partial class DriveInHomePage : Page
{
	public HomeViewModel ViewModel { get; } = App.GetService<HomeViewModel>();

	/// <summary>The Continue watching row: Continue Watching less what the screen features (<see cref="DriveInHome.ContinueRow"/>).</summary>
	public ObservableCollection<BaseItemViewModel> ContinueShown { get; } = [];

	private DriveInLayout? _layout;
	private bool _compact;
	private bool _arrivalFocus;
	private HomePlace? _restore;

	private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;
	private bool _noticePointer;
	private bool _noticeFocus;
	private long _noticeShownAt;

	private TimeSpan NoticeShownFor => System.Diagnostics.Stopwatch.GetElapsedTime(_noticeShownAt);

	public DriveInHomePage()
	{
		InitializeComponent();
		TopBar.SetExtendsUnder(this, true);
		_compact = ReadCompact();
		ShowNotice(ViewModel.Notice);

		_noticeTimer = DispatcherQueue.CreateTimer();
		_noticeTimer.IsRepeating = false;
		_noticeTimer.Tick += (_, _) =>
		{
			if (ViewModel.Notice is not { } notice)
			{
				return;
			}

			var held = _noticePointer || _noticeFocus;
			if (NoticeTiming.Closes(NoticeShownFor, held))
			{
				ViewModel.DismissNotice(notice);
			}
			else
			{
				StartNoticeTimer(held);
			}
		};

		ViewModel.PropertyChanged += OnViewModelChanged;
		Watch(null, ViewModel.ContinueItems);
		Watch(null, ViewModel.NextUpItems);
		SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
		Loaded += (_, _) =>
		{
			// the lot is opaque: nothing of the window's soft artwork shows behind Home
			AmbientBackdrop.Set(null);
			ApplyLayout(ActualWidth);
			Refresh();
			RestorePlace();
		};
		Unloaded += (_, _) => _noticeTimer.Stop();
	}

	// ---- the view model's changes ------------------------------------------------------------------------------------------

	private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(HomeViewModel.Notice):
				OnNoticeChanged();
				break;
			case nameof(HomeViewModel.Hero):
				Refresh();
				break;
			case nameof(HomeViewModel.ContinueItems):
				Refresh();
				break;
			case nameof(HomeViewModel.NextUpItems):
			case nameof(HomeViewModel.IsContinueLoading):
				Refresh();
				break;
		}
	}

	private System.Collections.Specialized.INotifyCollectionChanged? _continueWatched;
	private System.Collections.Specialized.INotifyCollectionChanged? _nextUpWatched;

	private void Watch(object? _, object? __)
	{
		if (_continueWatched is not null)
		{
			_continueWatched.CollectionChanged -= OnItemsChanged;
		}

		if (_nextUpWatched is not null)
		{
			_nextUpWatched.CollectionChanged -= OnItemsChanged;
		}

		_continueWatched = ViewModel.ContinueItems;
		_nextUpWatched = ViewModel.NextUpItems;
		_continueWatched.CollectionChanged += OnItemsChanged;
		_nextUpWatched.CollectionChanged += OnItemsChanged;
	}

	private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

	/// <summary>The screen's features, the Continue watching row and the sign, from what the view model shows now.</summary>
	private void Refresh()
	{
		if (!ReferenceEquals(_continueWatched, ViewModel.ContinueItems) || !ReferenceEquals(_nextUpWatched, ViewModel.NextUpItems))
		{
			Watch(null, null);
		}

		var features = _layout?.HasScreen == false ? [] : ViewModel.DriveInFeatures();
		Screen.Slides = features;
		DriveInHome.Sync(ContinueShown, DriveInHome.ContinueRow(ViewModel.ContinueItems, x => x.Id, features));
		DriveInRows.SetSubtitle(ContinueRow, DriveInHome.InProgressText(ViewModel.ContinueItems.Count));
		Sign.Items = [.. ViewModel.NextUpItems];
		Sign.UpNextCount = ViewModel.DriveInUpNextCount;
		ContinueRow.Visibility = RowVisibility(ViewModel.IsContinueLoading, ContinueShown.Count);
		TryArrivalFocus();
	}

	// ---- the lot's layout ------------------------------------------------------------------------------------------------

	private void ApplyLayout(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var layout = DriveInLayout.Home(width, _compact, PageTop());
		if (_layout is { } old && old == layout)
		{
			return;
		}

		var hadScreen = _layout?.HasScreen;
		_layout = layout;
		Lot.Height = layout.SceneHeight;
		SceneBack.Layout = layout;
		SceneFront.Layout = layout;
		Screen.Margin = new Thickness(layout.Screen.X, layout.Screen.Y, 0, 0);
		Screen.Width = layout.HasScreen ? layout.Screen.Width : 0;
		Screen.Height = layout.HasScreen ? layout.Screen.Height : 0;
		Screen.ShowDetails = layout.ShowDetails;
		Sign.Margin = layout.HasSign ? new Thickness(layout.Arrow.X, layout.Arrow.Y, 0, 0) : new Thickness(0);
		Sign.ApplyLayout(layout);
		ComingUpRow.Visibility = layout.HasSign ? Visibility.Collapsed : Visibility.Visible;
		RowsPanel.Margin = new Thickness(layout.Margin - 12, Math.Max(0, layout.RowsTop - layout.SceneHeight), 0, 0);
		if (hadScreen != layout.HasScreen)
		{
			Refresh();
		}
	}

	/// <summary>How far below the window's top this page starts (0 under the top bar).</summary>
	private double PageTop()
	{
		try
		{
			return XamlRoot?.Content is UIElement root ? TransformToVisual(root).TransformPoint(default).Y : 0;
		}
		catch (ArgumentException)
		{
			return 0;
		}
	}

	private static bool ReadCompact()
	{
		try
		{
			return App.GetService<ILocalSettingsService>().ReadSetting(DriveInHome.CompactHomeKey, false);
		}
		catch (Exception)
		{
			return false;
		}
	}

	// ---- arrival: Resume has the keyboard; Back: the place the user left ------------------------------------------------

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		var back = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward;
		_restore = back ? HomePlace.Recall(ViewModel.JellyfinClient.UserId) : null;
		_arrivalFocus = !back;
		_arrivedAt = System.Diagnostics.Stopwatch.GetTimestamp();
		base.OnNavigatedTo(e);
	}

	private long _arrivedAt;

	/// <summary>How long after arriving the page may still put the keyboard on Resume (the features may come a moment later).</summary>
	public static readonly TimeSpan ArrivalFocusFor = TimeSpan.FromSeconds(4);

	private void TryArrivalFocus()
	{
		if (!_arrivalFocus || !IsLoaded || XamlRoot is null)
		{
			return;
		}

		if (System.Diagnostics.Stopwatch.GetElapsedTime(_arrivedAt) > ArrivalFocusFor)
		{
			_arrivalFocus = false;
			return;
		}

		// never take the keyboard from a text box the user is typing in (the search box)
		if (FluentFin.Services.TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			_arrivalFocus = false;
			return;
		}

		var done = _layout?.HasScreen == true ? Screen.FocusPrimary() : ContinueRow.FocusRow();
		if (done)
		{
			_arrivalFocus = false;
		}
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		var row = ShelfRow.LastUsed is { } used && IsMine(used) ? used : null;
		var item = row?.LastItem as BaseItemViewModel;
		HomePlace.Remember(ViewModel.JellyfinClient.UserId, new HomePlace(PageScroller.VerticalOffset, item is null ? null : row!.Header, item?.Id));
		base.OnNavigatedFrom(e);
	}

	private bool IsMine(DependencyObject element) => OutdoorScreen.IsInside(element, this);

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
			if (XamlRoot is null)
			{
				t.Stop();
				return;
			}

			if (PageScroller.ScrollableHeight < place.Offset - 1 && ViewModel.IsLoading && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
			{
				return;
			}

			t.Stop();
			PageScroller.ChangeView(null, Math.Min(place.Offset, PageScroller.ScrollableHeight), null, disableAnimation: true);
			PageScroller.UpdateLayout();
			if (place.Row is { } header && place.ItemId is { } id)
			{
				ShelfRow.Shown.FirstOrDefault(r => r.Header == header && IsMine(r))?
					.FocusItem(x => x is BaseItemViewModel vm && vm.Id == id, FocusState.Programmatic);
			}
		};
		timer.Start();
	}

	// ---- x:Bind helpers -------------------------------------------------------------------------------------------------

	public static Visibility SectionVisibility(bool isLoading, bool hasItems) =>
		isLoading || hasItems ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility LoadedVisibility(bool isLoading) => isLoading ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>The Continue watching row: once loaded, and while it has a card the screen does not already show.</summary>
	public static Visibility RowVisibility(bool isLoading, int count) => !isLoading && count > 0 ? Visibility.Visible : Visibility.Collapsed;

	/// <summary>Beside a "New in" marquee: "12 new".</summary>
	public static string NewCount(int count) => count > 0 ? $"{count} new" : "";

	// ---- keys and links -------------------------------------------------------------------------------------------------

	private void NextUpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		// to the sign (or the row), never playing: a single letter must not start a video
		args.Handled = _layout?.HasSign == true ? Sign.FocusFirst() : ComingUpRow.FocusRow();
	}

	private void ContinueInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = ContinueRow.FocusRow() || Screen.FocusPrimary(FocusState.Keyboard);
	}

	private void Sign_AllRequested(object? sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Up next", [.. ViewModel.DriveInUpNext()]));

	private void Sign_NextFeatureRequested(object? sender, EventArgs e) => Screen.ShowNext();

	private void ContinueSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Continue Watching", [.. ViewModel.ContinueItems]));

	private void NextUpSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Next Up", [.. ViewModel.NextUpItems]));

	private void RecentSeeAll(object sender, EventArgs e)
	{
		if (sender is ShelfRow { Tag: { } library })
		{
			App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<LibraryViewModel>(library);
		}
	}

	// ---- the removal notice: the shared Home's behaviour --------------------------------------------------------------

	private void OnNoticeChanged()
	{
		_noticeTimer.Stop();
		ShowNotice(ViewModel.Notice);
		if (ViewModel.Notice is null)
		{
			return;
		}

		_noticeShownAt = System.Diagnostics.Stopwatch.GetTimestamp();
		StartNoticeTimer(_noticePointer || _noticeFocus);
		DispatcherQueue.TryEnqueue(() =>
		{
			if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is null && ViewModel.Notice?.CanUndo == true)
			{
				_ = ContinueRow.FocusRow() || ShelfRow.Shown.Where(IsMine).Any(r => r.FocusRow());
			}
		});
	}

	private void StartNoticeTimer(bool held, bool afterHold = false)
	{
		_noticeTimer.Stop();
		_noticeTimer.Interval = NoticeTiming.Remaining(NoticeShownFor, held, afterHold);
		_noticeTimer.Start();
	}

	private void ShowNotice(HomeNotice? notice)
	{
		NoticeText.Text = notice?.Message ?? "";
		UndoButton.Visibility = notice?.CanUndo == true ? Visibility.Visible : Visibility.Collapsed;
		NoticePanel.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
	}

	private void HoldNotice(bool pointer, bool focus)
	{
		_noticePointer = pointer;
		_noticeFocus = focus;
		if (ViewModel.Notice is not null)
		{
			StartNoticeTimer(pointer || focus, afterHold: !(pointer || focus));
		}
	}

	private void Notice_PointerEntered(object sender, PointerRoutedEventArgs e) => HoldNotice(true, _noticeFocus);

	private void Notice_PointerExited(object sender, PointerRoutedEventArgs e) => HoldNotice(false, _noticeFocus);

	private void Notice_GotFocus(object sender, RoutedEventArgs e) => HoldNotice(_noticePointer, true);

	private void Notice_LostFocus(object sender, RoutedEventArgs e) => HoldNotice(_noticePointer, false);

	private async void UndoButton_Click(object sender, RoutedEventArgs e) => await Undo();

	private async Task Undo()
	{
		var item = ViewModel.Notice?.Removal?.Item;
		var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
		var fromNotice = focused is not null && OutdoorScreen.IsInside(focused, NoticePanel);
		var undo = ViewModel.UndoRemoval();
		if (item is not null && fromNotice)
		{
			Refresh();
			ContinueRow.UpdateLayout();
			ContinueRow.FocusItem(x => ReferenceEquals(x, item), FocusState.Keyboard);
		}

		await undo;
	}

	private void NoticeCloseButton_Click(object sender, RoutedEventArgs e)
	{
		if (ViewModel.Notice is { } notice)
		{
			ViewModel.DismissNotice(notice);
		}
	}

	private async void UndoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (ViewModel.Notice?.CanUndo != true || FluentFin.Services.TextInput.IsTextInput(FocusManager.GetFocusedElement(XamlRoot)))
		{
			return;
		}

		args.Handled = true;
		await Undo();
	}
}
