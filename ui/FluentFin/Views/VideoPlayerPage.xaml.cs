using System.ComponentModel;
using CommunityToolkit.WinUI;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Mpv;
using FluentFin.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace FluentFin.Views;

public sealed partial class VideoPlayerPage : Page
{
	private readonly MpvVideoHost _videoHost = App.GetService<MpvVideoHost>();
	private bool _videoAttached;
	private bool _mpvFullScreen;

	/// <summary>This page's frame navigated away from it (the only way the page hears it was left).</summary>
	private bool _navigatedFrom;

	public VideoPlayerViewModel ViewModel { get; } = App.GetService<VideoPlayerViewModel>();

	public VideoPlayerPage()
	{
		InitializeComponent();

		Loaded += OnLoaded;
		Unloaded += OnUnloaded;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;

		var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
		escape.Invoked += OnEscape;
		KeyboardAccelerators.Add(escape);
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		ViewModel.ToggleFullScreen = () => MediaPlayerHost.OnPlayerDoubleTapped(MediaPlayerHost, new Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs());
		ViewModel.SetFullScreen = SetMpvFullScreen;
		ViewModel.IsFullScreen = () => App.MainWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
		ViewModel.FocusVideo = () => _videoHost.Focus();
		ViewModel.GoBack = GoBack;
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		_navigatedFrom = true;
		DetachVideo();

		// restores the window and tells the backend, before the view model stops (the next page starts from the real state)
		_ = ViewModel.LeaveFullScreen();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (ViewModel.IsMpv)
		{
			AttachVideo();
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		DetachVideo();

		// fluentfin-mpv: a navigation of the window's root frame (Log out, Switch user) removes the shell with this page on it
		// and tells only the shell: no OnNavigatedFrom here, nor for the view model. Looked at again once layout has settled
		// (a move within the window unloads and loads again); still out of the window then, it was left (docs/BRIDGE.md §6)
		new PlayerPageRemoval(
			navigatedFrom: () => _navigatedFrom,
			isLoaded: () => IsLoaded,
			isMpv: () => ViewModel.IsMpv,
			leave: ViewModel.OnPageRemoved,
			enqueueLowPriority: work => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () => await work()),
			onError: ex => App.GetService<ILogger<VideoPlayerPage>>().LogError(ex, "Leaving the removed player page failed"))
			.OnUnloaded();
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(VideoPlayerViewModel.IsMpv) when ViewModel.IsMpv && IsLoaded:
				AttachVideo();
				break;
			case nameof(VideoPlayerViewModel.IsMpvVideoVisible) when _videoAttached:
				_videoHost.SetVideoVisible(ViewModel.IsMpvVideoVisible);
				break;
		}
	}

	private void AttachVideo()
	{
		if (_videoAttached)
		{
			return;
		}

		_videoHost.Attach(MpvVideoArea);
		_videoHost.SetVideoVisible(ViewModel.IsMpvVideoVisible);
		_videoAttached = true;
	}

	private void DetachVideo()
	{
		if (!_videoAttached)
		{
			return;
		}

		// a player page that replaced this one may already have taken the window over
		_videoHost.Detach(MpvVideoArea);
		_videoAttached = false;
	}

	/// <summary>Full screen the way MediaPlayerHost.OnPlayerDoubleTapped does it, but to a given state (mpv asks for one).</summary>
	private void SetMpvFullScreen(bool fullScreen)
	{
		_mpvFullScreen = fullScreen;

		var kind = fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;
		if (App.MainWindow.AppWindow.Presenter.Kind != kind)
		{
			App.MainWindow.AppWindow.SetPresenter(kind);
		}

		if (App.GetService<ITitleBarViewModel>() is { } titleBar)
		{
			titleBar.IsVisible = !fullScreen;
		}

		if (this.FindAscendant<NavigationView>() is { } navigationView)
		{
			navigationView.IsPaneVisible = !fullScreen;
		}
	}

	private async void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!ViewModel.IsMpv)
		{
			return;
		}

		args.Handled = true;

		if (_mpvFullScreen)
		{
			SetMpvFullScreen(false);
			await ViewModel.OnFullScreenChanged(false);
			return;
		}

		GoBack();
	}

	/// <summary>
	/// Back with the navigation service whose frame this page is on: the shell's, or in the self-test (--mpv-selftest, no
	/// login, no shell) the window's root frame, where the server list is under this page.
	/// </summary>
	private bool GoBack()
	{
		var setup = App.GetKeyedService<INavigationService>(NavigationRegions.InitialSetup);
		return (Frame is { } frame && setup.Frame == frame ? setup : App.GetService<INavigationService>()).GoBack();
	}

	// fluentfin-mpv: closed by its button: gone in the view model too, so the next notice shows even with the same text
	private void OnMpvNoticeCloseButtonClick(InfoBar sender, object args) => ViewModel.DismissMpvNotice();

	public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	public Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

	public bool HasText(string? value) => !string.IsNullOrEmpty(value);

	public InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
}
