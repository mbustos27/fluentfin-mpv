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

	// Seaglass 1.1.4: the notice after a removal from Continue Watching stays as NoticeTiming says (at most 30 s, held or not)
	private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _noticeTimer;
	private bool _noticePointer;
	private bool _noticeFocus;
	private long _noticeShownAt;

	private TimeSpan NoticeShownFor => System.Diagnostics.Stopwatch.GetElapsedTime(_noticeShownAt);

	public HomePage()
	{
		InitializeComponent();
		ShowNotice(ViewModel.Notice);

		_noticeTimer = DispatcherQueue.CreateTimer();
		_noticeTimer.IsRepeating = false;
		_noticeTimer.Tick += (_, _) =>
		{
			if (App.IsClosing)
			{
				return; // 1.7.3 fix round (the close crash's class): nothing in the window's teardown
			}

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
		};
		Loaded += (_, _) =>
		{
			ViewModel.PropertyChanged -= OnViewModelChanged;
			ViewModel.PropertyChanged += OnViewModelChanged;
			ResumeNotice();
			RestorePlace();
		};
	}

	private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(HomeViewModel.Notice))
		{
			OnNoticeChanged();
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
			if (App.IsClosing)
			{
				return; // 1.7.3 fix round (the close crash's class): nothing in the window's teardown
			}

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
			// Nixie 1.4.0 (review of wip/140-fix2): Back's card takes the keyboard by the app's one arrival rule (not once the user
			// moved while Home came back, nor out of a text box they went to)
			if (place.Row is { } header && place.ItemId is { } id && FluentFin.Services.ArrivalFocus.WhyNot(this) is null)
			{
				FluentFin.Controls.ShelfRow.Shown.FirstOrDefault(r => r.Header == header && IsMine(r))?
					.FocusItem(x => x is BaseItemViewModel vm && vm.Id == id, Microsoft.UI.Xaml.FocusState.Programmatic);
			}
		};
		timer.Start();
	}

	/// <summary>1.1.4: the rows' inset under an edge-to-edge hero: 24 a side, and 36 on top while there is no hero (as before).</summary>
	public static Microsoft.UI.Xaml.Thickness RowsMargin(IReadOnlyList<HeroSlide>? hero) => new(24, hero is { Count: > 0 } ? 20 : 36, 24, 0);

	// 1.5 huntfix (BH-150-M1)
	/// <summary>
	/// Home's load state (a "Some of Home couldn't be loaded" notice, or a message in place of the page): below the top bar the
	/// page runs under (<see cref="Controls.TopBar.Height"/>), with room before the hero; no room at all when there is nothing
	/// to say, so the hero stays the first thing, right under the title bar.
	/// </summary>
	public static Microsoft.UI.Xaml.Thickness StateMargin(bool hasProblem, bool hasMessage) =>
		hasProblem || hasMessage ? new(36, Controls.TopBar.Height + StateGap, 36, hasProblem ? StateGap : 0) : new(36, 0, 36, 0);

	/// <summary>The space between the top bar and the notice, and between the notice and the hero.</summary>
	public const double StateGap = 12;
	// END 1.5 huntfix

	public static Microsoft.UI.Xaml.Visibility NoticeVisibility(HomeNotice? notice) =>
		notice is null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	public static Microsoft.UI.Xaml.Visibility UndoVisibility(HomeNotice? notice) =>
		notice?.CanUndo == true ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

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
		// the card that had the focus left with the row's last card: the keyboard goes on to the next row, not to the notice
		// (the review, item 1: on the notice it held it, and its Undo, for good)
		DispatcherQueue.TryEnqueue(() =>
		{
			if (XamlRoot is not null && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is null && ViewModel.Notice?.CanUndo == true)
			{
				_ = NextUpRow.FocusRow() || FluentFin.Controls.ShelfRow.Shown.Where(IsMine).Any(r => r.FocusRow());
			}
		});
	}

	private void StartNoticeTimer(bool held, bool afterHold)
	{
		_noticeTimer.Stop();
		_noticeTimer.Interval = NoticeTiming.Remaining(NoticeShownFor, held, afterHold);
		_noticeTimer.Start();
	}

	/// <summary>The notice as the view model has it: shown with its words and its Undo, or gone (null).</summary>
	private void ShowNotice(HomeNotice? notice)
	{
		NoticeText.Text = notice?.Message ?? "";
		UndoButton.Visibility = UndoVisibility(notice);
		NoticePanel.Visibility = NoticeVisibility(notice);
	}

	private void HoldNotice(bool pointer, bool focus)
	{
		_noticePointer = pointer;
		_noticeFocus = focus;
		if (ViewModel.Notice is not null)
		{
			// held: the timer runs on to the hard limit; let go: 5 s more (never past the hard limit)
			StartNoticeTimer(pointer || focus, afterHold: !(pointer || focus));
		}
	}

	private void Notice_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoldNotice(true, _noticeFocus);

	private void Notice_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => HoldNotice(false, _noticeFocus);

	private void Notice_GotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => HoldNotice(_noticePointer, true);

	private void Notice_LostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => HoldNotice(_noticePointer, false);

	private async void UndoButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Undo();

	/// <summary>
	/// Undo: the card goes back (at once, before the server answers), and the keyboard goes with it when it was on the notice,
	/// which goes: it would be left nowhere.
	/// </summary>
	private async Task Undo()
	{
		var item = ViewModel.Notice?.Removal?.Item;
		var focused = XamlRoot is null ? null : Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Microsoft.UI.Xaml.DependencyObject;
		var fromNotice = focused is not null && IsInside(focused, NoticePanel);
		var undo = ViewModel.UndoRemoval();
		if (item is not null && fromNotice)
		{
			ContinueRow.UpdateLayout();
			ContinueRow.FocusItem(x => ReferenceEquals(x, item), Microsoft.UI.Xaml.FocusState.Keyboard);
		}

		await undo;
	}

	private static bool IsInside(Microsoft.UI.Xaml.DependencyObject element, Microsoft.UI.Xaml.DependencyObject ancestor)
	{
		for (var node = element; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, ancestor))
			{
				return true;
			}
		}

		return false;
	}

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
		await Undo();
	}

	// fluentfin-mpv: a section is on screen while it loads (placeholder cards) and afterwards only if it has items
	public static Microsoft.UI.Xaml.Visibility SectionVisibility(bool isLoading, bool hasItems) =>
		isLoading || hasItems ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

	public static Microsoft.UI.Xaml.Visibility LoadedVisibility(bool isLoading) =>
		isLoading ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

	private void NextUpInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.HomeLetterKeys.Ignore(XamlRoot)) // 1.5 (L1, L2): the switch, then typing
		{
			return; // typing: N and C are letters then
		}

		// Seaglass 1.1.4: to the row, never playing (a single letter must not start a video)
		args.Handled = NextUpRow.FocusRow();
    }

	private void ContinueInvoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
	{
		if (FluentFin.Services.HomeLetterKeys.Ignore(XamlRoot)) // 1.5 (L1, L2): the switch, then typing
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
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("Up next", [.. ViewModel.NextUpItems]));

	private void RecentSeeAll(object sender, EventArgs e)
	{
		if (sender is FluentFin.Controls.ShelfRow { Tag: { } library })
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
