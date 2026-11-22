using CommunityToolkit.WinUI;
using FluentFin.Controls;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>Cinematic's Home (see CinematicHomePage.xaml), over the shared <see cref="HomeViewModel"/>.</summary>
public sealed partial class CinematicHomePage : Page
{
	/// <summary>Under this window width the layout is the narrow one (the mockup's 1100 x 900): smaller stills and margins, dots.</summary>
	public const double NarrowBelow = 1400;

	/// <summary>The first screen is never shorter than this: a short window scrolls instead of laying the words over the shelf.</summary>
	public const double MinFirstScreen = 640;

	public HomeViewModel ViewModel { get; } = App.GetService<HomeViewModel>();

	[GeneratedDependencyProperty(DefaultValue = 384.0)]
	public partial double StillWidth { get; set; }

	[GeneratedDependencyProperty(DefaultValue = 216.0)]
	public partial double StillHeight { get; set; }

	[GeneratedDependencyProperty]
	public partial Thickness RowMargin { get; set; }

	[GeneratedDependencyProperty]
	public partial Thickness PlaceholderMargin { get; set; }

	private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;
	private bool _noticePointer;
	private bool _noticeFocus;
	private long _noticeShownAt;
	private bool? _narrow;

	private TimeSpan NoticeShownFor => System.Diagnostics.Stopwatch.GetElapsedTime(_noticeShownAt);

	public CinematicHomePage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
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
				StartNoticeTimer(held, afterHold: false);
			}
		};
		// Nixie 1.4.0 (bug hunt m6): the view model outlives the page ("Choose a look" hands it to the new theme's Home): listen
		// only while shown. Subscribed in the constructor, the old page kept listening, and its notice timer closed the new
		// page's "Removed from Continue Watching · Undo" under the user's pointer.
		Unloaded += (_, _) =>
		{
			_noticeTimer.Stop();
			ViewModel.PropertyChanged -= OnViewModelChanged;
			ViewModel.UpNext.PropertyChanged -= OnUpNextChanged;
		};
		Loaded += (_, _) =>
		{
			ViewModel.PropertyChanged -= OnViewModelChanged;
			ViewModel.PropertyChanged += OnViewModelChanged;
			ViewModel.UpNext.PropertyChanged -= OnUpNextChanged;
			ViewModel.UpNext.PropertyChanged += OnUpNextChanged;
			ResumeNotice();
			LayoutFirstScreen();
			RestorePlace();
		};
		PageScroller.SizeChanged += (_, _) => LayoutFirstScreen();
		UpNextBlock.SizeChanged += (_, _) => Hero.ContentBottom = UpNextBlock.ActualHeight;
		UpNextRow.Loaded += (_, _) => CinematicShelf.Fit(UpNextRow, 16, ViewModel.UpNext.Summary);
	}

	private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(HomeViewModel.Notice))
		{
			OnNoticeChanged();
		}
		else if (e.PropertyName == nameof(HomeViewModel.Hero))
		{
			LayoutFirstScreen();
		}
	}

	private void OnUpNextChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(UpNextShelf.Summary))
		{
			CinematicShelf.SetSummary(UpNextRow, ViewModel.UpNext.Summary);
		}
	}

	/// <summary>
	/// Nixie 1.4.0 (bug hunt m6): shown (again, or first after "Choose a look" handed the view model over): the notice as it is
	/// now, timed by this page, the one the pointer and the keyboard are on.
	/// </summary>
	private void ResumeNotice()
	{
		ShowNotice(ViewModel.Notice);
		if (ViewModel.Notice is not null && !_noticeTimer.IsRunning)
		{
			_noticeShownAt = System.Diagnostics.Stopwatch.GetTimestamp();
			StartNoticeTimer(_noticePointer || _noticeFocus, afterHold: false);
		}
	}

	// ---- the layout ---------------------------------------------------------------------------------------------------

	/// <summary>
	/// The first screen is the window's height (the hero's art fills it, the Up next shelf sits at its foot), never under
	/// <see cref="MinFirstScreen"/>; with no slide to show, the shelf simply starts under the bar. A narrow window takes the
	/// mockup's 1100 px sizes.
	/// </summary>
	private void LayoutFirstScreen()
	{
		var width = PageScroller.ActualWidth;
		var height = PageScroller.ViewportHeight > 0 ? PageScroller.ViewportHeight : PageScroller.ActualHeight;
		if (width <= 0 || height <= 0)
		{
			return;
		}

		var narrow = width < NarrowBelow;
		var hasHero = ViewModel.Hero.Count > 0;
		FirstScreen.Height = hasHero ? Math.Max(MinFirstScreen, height) : double.NaN;
		UpNextBlock.Padding = new Thickness(0, hasHero ? 0 : 84, 0, narrow ? 20 : 28);
		Horizon.Visibility = hasHero ? Visibility.Visible : Visibility.Collapsed;
		if (_narrow == narrow)
		{
			return;
		}

		_narrow = narrow;
		var side = narrow ? 48 : 96;
		var still = narrow ? 12 : 16;
		Hero.IsNarrow = narrow;
		StillWidth = narrow ? 300 : 384;
		StillHeight = narrow ? 169 : 216;
		UpNextRow.Margin = new Thickness(side - still, 0, 0, 0);
		UpNextRow.ItemTemplate = (DataTemplate)Resources[narrow ? "UpNextSmall" : "UpNextLarge"];
		UpNextPlaceholder.Margin = new Thickness(side, 0, 0, 0);
		StateView.Margin = new Thickness(side, 0, side, 12);
		RowMargin = new Thickness(side - 12, 0, 0, 0);
		PlaceholderMargin = new Thickness(side, 0, 0, 0);
		CinematicShelf.Fit(UpNextRow, still, ViewModel.UpNext.Summary);
		foreach (var row in ShelfRow.Shown.Where(IsMine))
		{
			if (!ReferenceEquals(row, UpNextRow))
			{
				CinematicShelf.Fit(row, 12, null);
			}
		}
	}

	private void RecentRow_Loaded(object sender, RoutedEventArgs e)
	{
		if (sender is ShelfRow row)
		{
			CinematicShelf.Fit(row, 12, null);
		}
	}

	public static Visibility UpNextVisibility(bool isLoading, bool hasItems) => !isLoading && hasItems ? Visibility.Visible : Visibility.Collapsed;

	public static Visibility LoadedVisibility(bool isLoading) => isLoading ? Visibility.Collapsed : Visibility.Visible;

	// ---- Back to Home puts the user where they were (as the shared Home) ------------------------------------------------

	private HomePlace? _restore;

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_restore = e.NavigationMode is NavigationMode.Back or NavigationMode.Forward ? HomePlace.Recall(ViewModel.JellyfinClient.UserId) : null;
		base.OnNavigatedTo(e);
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		var row = ShelfRow.LastUsed is { } used && IsMine(used) ? used : null;
		var item = row?.LastItem as BaseItemViewModel;
		HomePlace.Remember(ViewModel.JellyfinClient.UserId, new HomePlace(PageScroller.VerticalOffset, item is null ? null : row!.Header, item?.Id));
		base.OnNavigatedFrom(e);
	}

	private bool IsMine(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, this))
			{
				return true;
			}
		}

		return false;
	}

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
				return;
			}

			t.Stop();
			PageScroller.ChangeView(null, Math.Min(place.Offset, PageScroller.ScrollableHeight), null, disableAnimation: true);
			PageScroller.UpdateLayout();
			// Nixie 1.4.0 (review of wip/140-fix2): Back's card takes the keyboard by the app's one arrival rule (not once the user
			// moved while Home came back, nor out of a text box they went to)
			if (place.Row is { } header && place.ItemId is { } id && FluentFin.Services.ArrivalFocus.WhyNot(this) is null)
			{
				ShelfRow.Shown.FirstOrDefault(r => r.Header == header && IsMine(r))?
					.FocusItem(x => x is BaseItemViewModel vm && vm.Id == id, FocusState.Programmatic);
			}
		};
		timer.Start();
	}

	// ---- the notice after a removal, with its Undo (the shared Home's) --------------------------------------------------

	private void OnNoticeChanged()
	{
		_noticeTimer.Stop();
		ShowNotice(ViewModel.Notice);
		if (ViewModel.Notice is null)
		{
			return;
		}

		_noticeShownAt = System.Diagnostics.Stopwatch.GetTimestamp();
		StartNoticeTimer(_noticePointer || _noticeFocus, afterHold: false);
		DispatcherQueue.TryEnqueue(() =>
		{
			if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is null && ViewModel.Notice?.CanUndo == true)
			{
				_ = UpNextRow.FocusRow() || ShelfRow.Shown.Where(IsMine).Any(r => r.FocusRow());
			}
		});
	}

	private void StartNoticeTimer(bool held, bool afterHold)
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
		var fromNotice = focused is not null && IsInside(focused, NoticePanel);
		var undo = ViewModel.UndoRemoval();
		if (item is not null && fromNotice)
		{
			UpNextRow.UpdateLayout();
			UpNextRow.FocusItem(x => ReferenceEquals(x, item), FocusState.Keyboard);
		}

		await undo;
	}

	private static bool IsInside(DependencyObject element, DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
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

	// ---- keys: N and C take the keyboard to Up next (never playing: a stray letter must not start a video) -------------

	private void NextUpInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.HomeLetterKeys.Ignore(XamlRoot)) // 1.5 (L1, L2): the switch, then typing
		{
			return;
		}

		// the first episode that came from Next Up, else the shelf's remembered still
		args.Handled = UpNextRow.FocusItem(x => x is BaseItemViewModel item && ViewModel.UpNext.IsNextUp(item), FocusState.Keyboard) || UpNextRow.FocusRow();
	}

	private void ContinueInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.HomeLetterKeys.Ignore(XamlRoot)) // 1.5 (L1, L2): the switch, then typing
		{
			return;
		}

		args.Handled = UpNextRow.FocusRow();
	}

	// ---- See all ---------------------------------------------------------------------------------------------------------

	private void UpNextSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Up next", [.. ViewModel.UpNext.Items]));

	private void RecentSeeAll(object sender, EventArgs e)
	{
		if (sender is ShelfRow { Tag: { } library })
		{
			App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<LibraryViewModel>(library);
		}
	}

	// 1.6 collections: a Made by Nixie row's "See all" opens its collection
	private void NixieSeeAll(object sender, EventArgs e)
	{
		if (sender is Microsoft.UI.Xaml.FrameworkElement { Tag: Core.ViewModels.NixieHomeRowViewModel row })
		{
			ViewModel.OpenNixieRow(row);
		}
	}
}
