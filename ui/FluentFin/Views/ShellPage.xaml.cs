using FluentFin.Contracts.Services;
using FluentFin.Controls;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class ShellPage : Page
{
	public ShellViewModel ViewModel
	{
		get;
	}

	// fluentfin-mpv: no navigation pane while a player page is open (docs/research/ux-review.md V9)
	public PlayerChrome Chrome { get; } = App.GetService<PlayerChrome>();

	// Seaglass: the opt-in update notice (Core/Updates, Services/UpdateNoticeService)
	private readonly UpdateNoticeService _updates = App.GetService<UpdateNoticeService>();

	public ShellPage()
	{
		ViewModel = App.GetService<ShellViewModel>();
		InitializeComponent();
		_updates.Coordinator.NoticeChanged += OnUpdateNoticeChanged;
		Unloaded += (_, _) => _updates.Coordinator.NoticeChanged -= OnUpdateNoticeChanged;
		ShowUpdateNotice();

		ViewModel.NavigationService.Frame = NavigationFrame;

		// 1.2.0 (the top bar): the sections are tabs (TopNavigation, shown by TitleBarControl's TopTabStrip), not a rail: the
		// plugins add theirs (Diary) there, the pins are read back, the libraries asked for
		_top.Reset();
		App.GetService<FluentFin.UI.Core.IPluginManager>().AddNavigationItems(_top);
		_ = LoadLibrariesAsync();
		// Nixie 1.4.0 (the trail rule): an item page reached from another section names its library (TrailEyebrow), from the
		// item's folders on the server (asked once per item)
		_top.LibraryOf = LibraryOfAsync;
		// Nixie 1.4.0 (the trail rule): the lit destination chosen again on its own page goes back to its top
		_top.ReturnToTopRequested += OnReturnToTop;
		Unloaded += (_, _) => _top.ReturnToTopRequested -= OnReturnToTop;
		_titleBar.PropertyChanged += OnTitleBarChanged;
		Unloaded += (_, _) => _titleBar.PropertyChanged -= OnTitleBarChanged;

		// review: the library tabs follow the server: read again whenever the client drops its list (a library added, removed
		// or renamed, here or on the server: LibraryChanged); a fifth library moves them into the Libraries menu
		_client.UserViewsChanged += OnUserViewsChanged;
		Unloaded += (_, _) => _client.UserViewsChanged -= OnUserViewsChanged;

		// Seaglass 1.1.4 (UX review, prioritised fix 5): the title bar's Back and Account follow the page this frame shows, by
		// its type, as soon as the frame shows it (the navigation service's own Navigated waits for the page's view model:
		// the player's waits for the playback to start). The shell going (Log out, Switch user) shows them again.
		NavigationFrame.Navigated += OnFrameNavigated;
		Unloaded += (_, _) => _titleBar.OnPageShown(null);

		// Seaglass 1.1.4 (interaction audit, item 16): a page a tab opens is a switch of section (PageTransition's refresh);
		// anything else opened is a drill in, Back a drill out
		PageTransition.IsTopLevel = IsTabPage;
	}

	// 1.2.0: the top bar's tabs
	private readonly TopNavigation _top = App.GetService<TopNavigation>();

	private readonly FluentFin.Core.Contracts.Services.IJellyfinClient _client = App.GetService<FluentFin.Core.Contracts.Services.IJellyfinClient>();

	private void OnUserViewsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => _ = LoadLibrariesAsync());

	private async Task LoadLibrariesAsync()
	{
		try
		{
			var libraries = await _client.GetUserViews();
			_top.SetLibraries(libraries);
		}
		catch (Exception)
		{
			// no list: one "Libraries" tab, whose page says why (its own load state)
		}
	}

	/// <summary>The view model's key of a page type shown in this frame ("" for one the page service does not know).</summary>
	private static string KeyOf(Type? pageType)
	{
		if (pageType is null)
		{
			return "";
		}

		try
		{
			return App.GetService<FluentFin.UI.Core.Contracts.Services.IPageService>().GetViewModelType(pageType).FullName ?? "";
		}
		catch (ArgumentException)
		{
			return "";
		}
	}

	/// <summary>1.2.0: a page one of the tabs (or the Libraries tab's menu) opens.</summary>
	private bool IsTabPage(Type pageType)
	{
		var key = KeyOf(pageType);
		return key.Length > 0 && _top.AllTabs.SelectMany(t => t.Menu.Prepend(t)).Any(t => t.Key == key);
	}

	private readonly Dictionary<Guid, Guid?> _libraryOf = [];

	/// <summary>Nixie 1.4.0: the library an item is in: the first of its folders that is one of the user's views.</summary>
	private async Task<Guid?> LibraryOfAsync(Jellyfin.Sdk.Generated.Models.BaseItemDto item)
	{
		if (item.Id is not { } id)
		{
			return null;
		}

		if (_libraryOf.TryGetValue(id, out var known))
		{
			return known;
		}

		var libraries = _top.AllTabs.Where(t => t.Kind == TopTabKind.Library).Select(t => t.LibraryId).ToHashSet();
		var library = (await _client.GetAncestors(id)).FirstOrDefault(a => libraries.Contains(a.Id))?.Id;
		if (library is not null)
		{
			_libraryOf[id] = library;
		}

		return library;
	}

	/// <summary>
	/// Nixie 1.4.0 (the trail rule, the review's DI-p7 and C-m13: choosing the library already on screen did nothing): the page
	/// shown again, fresh, at its top (a refresh's reload in place: the history and the bar as they were).
	/// </summary>
	private void OnReturnToTop(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
	{
		if (ViewModel.NavigationService is NavigationService navigation)
		{
			// Nixie 1.4.0 (integration): back to its top, not to the place R keeps
			navigation.ReloadForRefresh(_ => true, keepPlace: false);
		}
	});

	/// <summary>
	/// 1.2.0: where the page starts: below the bar, or at the window's top for a page that extends under it (a hero) and while
	/// the bar is hidden (full screen); the bar shows its scrim over such a page.
	/// </summary>
	private void UpdateInsets()
	{
		var extendsUnder = NavigationFrame.Content is DependencyObject page && TopBar.GetExtendsUnder(page);
		var top = TopBar.TopInset(_titleBar.IsVisible, extendsUnder);
		ContentRoot.Padding = new Thickness(0, top, 0, 0);
		UpdateBar.Margin = UpdateBarTop(UpdateBarOpen, _titleBar.IsVisible, extendsUnder) is { } barTop ? new Thickness(16, barTop, 16, 0) : new Thickness(0);
		_titleBar.IsContentUnderBar = _titleBar.IsVisible && extendsUnder;
	}

	/// <summary>
	/// The update bar's top margin: 8 below the top bar, or below where the bar sits over a page that runs under it; none while
	/// it is closed. 1.3.0: a closed bar kept its margin, so every page meant to run under the top bar (the heroes) started
	/// that much lower and never reached under the bar.
	/// </summary>
	internal static double? UpdateBarTop(bool open, bool barVisible, bool extendsUnder) =>
		open ? 8 + (barVisible && extendsUnder ? TopBar.Height : 0) : null;

	private void OnTitleBarChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TitleBarViewModel.IsVisible))
		{
			DispatcherQueue.TryEnqueue(UpdateInsets);
		}
	}

	private bool _lookChecked;

	// Seaglass 1.1.4: the title bar (TitleBarViewModel.OnPageShown)
	private readonly TitleBarViewModel _titleBar = (TitleBarViewModel)App.GetService<ITitleBarViewModel>();

	private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		// Nixie 1.3.0: a theme switch showing the same page in the new theme's view: the bar keeps its search text and its
		// tab; only where the page starts can change (the new view may run under the bar)
		// Nixie 1.3.1: a refresh (R, F5) too: the same page loaded again
		if (ViewModel.NavigationService is NavigationService { LastNavigationWasThemeReload: true } or NavigationService { LastNavigationWasRefresh: true })
		{
			UpdateInsets();
			return;
		}

		_titleBar.OnPageShown(e.SourcePageType, e.Parameter);

		// Nixie 1.3.0: the one-time "Choose a look" step, once the first page after the sign-in is up
		if (!_lookChecked)
		{
			_lookChecked = true;
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await ChooseLookStep.OfferAsync(XamlRoot));
		}

		// 1.2.0: the tab this page marks, and where it starts
		// review: with the page's place in the history, so Back gives a page back the tab it had
		_top.OnPageShown(KeyOf(e.SourcePageType), e.Parameter, NavigationFrame.BackStackDepth,
			returning: e.NavigationMode is Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward);
		UpdateInsets();
	}

	// Seaglass: Alt+Left, the Back and Forward keys and Mouse 4 / 5 are the window's (WindowShortcuts), on every page
	// Seaglass 1.1.4: a shell shown again (back in the window) tells the title bar what its frame shows
	private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		_titleBar.OnPageShown(NavigationFrame.Content?.GetType());
		UpdateInsets();
	}

	private void OnUpdateNoticeChanged() => DispatcherQueue.TryEnqueue(ShowUpdateNotice);

	/// <summary>Whether the update bar shows (1.3.0: its room comes and goes with it, UpdateBarTop).</summary>
	private bool UpdateBarOpen
	{
		get => UpdateBar.Visibility == Microsoft.UI.Xaml.Visibility.Visible;
		set
		{
			var visibility = value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
			if (UpdateBar.Visibility != visibility)
			{
				UpdateBar.Visibility = visibility;
				UpdateInsets();
			}
		}
	}

	// Nixie 1.3.1: Narrator hears each version's notice once
	private static string? s_announcedUpdate;

	private void ShowUpdateNotice()
	{
		if (_updates.Coordinator.Notice is not { } notice)
		{
			UpdateBarOpen = false;
			return;
		}

		UpdateTitle.Text = $"{Core.AppIdentity.Name} {notice.Version} is available";
		UpdateInstallButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
		UpdateInstallButton.Content = UpdateNoticeService.IsMsiInstall() && notice.HasInstaller ? "Download and install" : "Open the download page";
		UpdateBarOpen = true;
		if (s_announcedUpdate != notice.Version)
		{
			s_announcedUpdate = notice.Version;
			var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(UpdateTitle)
				?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(UpdateTitle);
			peer?.RaiseNotificationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.Other,
				Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent, UpdateTitle.Text, "NixieUpdateNotice");
		}
	}

	private async void OnUpdateInstallClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (_updates.Coordinator.Notice is { } notice)
		{
			UpdateInstallButton.IsEnabled = false;
			await _updates.InstallAsync(notice, () =>
			{
				UpdateTitle.Text = $"{Core.AppIdentity.Name} will close to update";
				UpdateInstallButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
			});
			UpdateInstallButton.IsEnabled = true;
		}
	}

	private void OnUpdateNotesClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (_updates.Coordinator.Notice is { } notice)
		{
			UpdateNoticeService.OpenPage(notice.PageUrl);
		}
	}

	// Nixie 1.3.1: Dismiss: that version is not offered again
	private void OnUpdateDismissClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _updates.Coordinator.Dismiss();
}
