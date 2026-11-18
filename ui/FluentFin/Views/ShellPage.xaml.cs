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
		_titleBar.PropertyChanged += OnTitleBarChanged;
		Unloaded += (_, _) => _titleBar.PropertyChanged -= OnTitleBarChanged;

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

	private async Task LoadLibrariesAsync()
	{
		try
		{
			var libraries = await App.GetService<FluentFin.Core.Contracts.Services.IJellyfinClient>().GetUserViews();
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
		return key.Length > 0 && _top.Tabs.SelectMany(t => t.Menu.Prepend(t)).Any(t => t.Key == key);
	}

	/// <summary>
	/// 1.2.0: where the page starts: below the bar, or at the window's top for a page that extends under it (a hero) and while
	/// the bar is hidden (full screen); the bar shows its scrim over such a page.
	/// </summary>
	private void UpdateInsets()
	{
		var extendsUnder = NavigationFrame.Content is DependencyObject page && TopBar.GetExtendsUnder(page);
		var top = TopBar.TopInset(_titleBar.IsVisible, extendsUnder);
		ContentRoot.Padding = new Thickness(0, top, 0, 0);
		UpdateBar.Margin = new Thickness(16, 8 + (_titleBar.IsVisible && extendsUnder ? TopBar.Height : 0), 16, 0);
		_titleBar.IsContentUnderBar = _titleBar.IsVisible && extendsUnder;
	}

	private void OnTitleBarChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(TitleBarViewModel.IsVisible))
		{
			DispatcherQueue.TryEnqueue(UpdateInsets);
		}
	}

	// Seaglass 1.1.4: the title bar (TitleBarViewModel.OnPageShown)
	private readonly TitleBarViewModel _titleBar = (TitleBarViewModel)App.GetService<ITitleBarViewModel>();

	private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		_titleBar.OnPageShown(e.SourcePageType, e.Parameter);
		// 1.2.0: the tab this page marks, and where it starts
		_top.OnPageShown(KeyOf(e.SourcePageType), e.Parameter);
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

	private void ShowUpdateNotice()
	{
		if (_updates.Coordinator.Notice is not { } notice)
		{
			UpdateBar.IsOpen = false;
			return;
		}

		UpdateBar.Title = $"Seaglass {notice.Version} is available";
		UpdateInstallButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
		UpdateInstallButton.Content = UpdateNoticeService.IsMsiInstall() && notice.HasInstaller ? "Download and install" : "Open the download page";
		UpdateBar.IsOpen = true;
	}

	private async void OnUpdateInstallClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
	{
		if (_updates.Coordinator.Notice is { } notice)
		{
			UpdateInstallButton.IsEnabled = false;
			await _updates.InstallAsync(notice, () =>
			{
				UpdateBar.Title = "Seaglass will close to update";
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

	// the close button dismisses that version; the bar closing because the notice went away does not
	private void OnUpdateBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
	{
		if (args.Reason == InfoBarCloseReason.CloseButton)
		{
			_updates.Coordinator.Dismiss();
		}
	}
}
