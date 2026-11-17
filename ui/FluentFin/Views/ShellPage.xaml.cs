using System.Reactive.Linq;
using FluentFin.Contracts.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.UI.Xaml.Controls;
using ReactiveUI;

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
		ViewModel.NavigationViewService.Initialize(NavigationViewControl);

		// Seaglass 1.1.4 (UX review, prioritised fix 5): the title bar's Back and Account follow the page this frame shows, by
		// its type, as soon as the frame shows it (the navigation service's own Navigated waits for the page's view model:
		// the player's waits for the playback to start). The shell going (Log out, Switch user) shows them again.
		NavigationFrame.Navigated += OnFrameNavigated;
		Unloaded += (_, _) => _titleBar.OnPageShown(null);

		// Seaglass 1.1.4 (interaction audit, item 16): a page the rail opens is a switch of section (PageTransition's refresh);
		// anything else opened is a drill in, Back a drill out
		PageTransition.IsTopLevel = IsRailPage;

		// Seaglass 1.1.3: a page off the rail (Selected null) passes through too, so RailSelection can unmark Settings
		ViewModel.WhenAnyValue(x => x.Selected)
			.Where(x => x is null or NavigationViewItem)
			.ObserveOn(RxSchedulers.MainThreadScheduler)
			.Subscribe(item =>
			{
				// Seaglass 1.1.3: the footer too, so Settings is marked on the rail while it is open
				var target = item is NavigationViewItem nav
					? NavigationViewControl.MenuItems.Concat(NavigationViewControl.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x => x.Content == nav.Content)
					: null;
				NavigationViewControl.SelectedItem = RailSelection.Next(NavigationViewControl.SelectedItem, target, x => NavigationViewControl.FooterMenuItems.Contains(x));
			});
	}

	// Seaglass 1.1.4: the title bar (TitleBarViewModel.OnPageShown)
	private readonly TitleBarViewModel _titleBar = (TitleBarViewModel)App.GetService<ITitleBarViewModel>();

	private void OnFrameNavigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e) => _titleBar.OnPageShown(e.SourcePageType);

	/// <summary>
	/// Seaglass 1.1.4: the page type one of the rail's items opens (Home, Libraries, Settings, a plugin's), not one pinned with
	/// a parameter (a library pinned to the rail is still a page inside Libraries).
	/// </summary>
	private bool IsRailPage(Type pageType)
	{
		var pages = App.GetService<FluentFin.UI.Core.Contracts.Services.IPageService>();
		foreach (var item in NavigationViewControl.MenuItems.Concat(NavigationViewControl.FooterMenuItems).OfType<NavigationViewItem>())
		{
			if (item.Tag is FluentFin.Core.Settings.CustomNavigationViewItem { Parameter: not null }
				|| item.GetValue(FluentFin.UI.Core.NavigationHelper.NavigateToProperty) is not string key)
			{
				continue;
			}

			try
			{
				if (pages.GetPageType(key) == pageType)
				{
					return true;
				}
			}
			catch (ArgumentException)
			{
				// a rail item whose page is not registered opens nothing
			}
		}

		return false;
	}

	// Seaglass: Alt+Left, the Back and Forward keys and Mouse 4 / 5 are the window's (WindowShortcuts), on every page
	// Seaglass 1.1.4: a shell shown again (back in the window) tells the title bar what its frame shows
	private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _titleBar.OnPageShown(NavigationFrame.Content?.GetType());

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
