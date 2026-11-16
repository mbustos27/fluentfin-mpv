using System.ComponentModel;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Mpv;
using FluentFin.Services;
using FluentFin.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace FluentFin.Views;

public sealed partial class VideoPlayerPage : Page
{
	private readonly MpvVideoHost _videoHost = App.GetService<MpvVideoHost>();
	// fluentfin-mpv player controls: the in-process mpv's video, shown in MpvControlsView instead of the window above
	private readonly MpvCompositionVideo _composition = App.GetService<MpvCompositionVideo>();

	// fluentfin-mpv: no navigation pane and no search while this page is open (docs/research/ux-review.md V9)
	private readonly PlayerChromeHold _chrome = new(App.GetService<PlayerChrome>());
	private bool _videoAttached;
	private bool _attachedComposition;
	private bool _mpvFullScreen;

	// fluentfin-mpv: the loading screen's button has the keyboard (GotFocus/LostFocus come after the change that causes them)
	private bool _leaveButtonFocused;

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
		// fluentfin-mpv: the page has no tooltip of its own, so WinUI made one of the accelerator's key and showed a stray
		// "Esc" at the top of the window (the loading screen's Cancel had the focus)
		KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
	}

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		_chrome.NavigatedTo();
		ViewModel.ToggleFullScreen = () => MediaPlayerHost.OnPlayerDoubleTapped(MediaPlayerHost, new Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs());
		ViewModel.SetFullScreen = SetMpvFullScreen;
		ViewModel.IsFullScreen = () => App.MainWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
		ViewModel.FocusVideo = FocusVideo;
		ViewModel.GoBack = GoBack;
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		_navigatedFrom = true;
		_chrome.NavigatedFrom();
		DetachVideo();

		// restores the window and tells the backend, before the view model stops (the next page starts from the real state)
		_ = ViewModel.LeaveFullScreen();
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		_chrome.Loaded();
		if (ViewModel.IsMpv)
		{
			AttachVideo();
		}

		// fluentfin-mpv player controls: when their bars hide, the keyboard comes back to the player from anywhere outside
		// this page (the title bar), not only from the bars
		MpvControlsView.KeyboardHome = MpvRoot;

		// the loading screen's only control: Escape or Enter leaves before the start goes on
		if (ViewModel.IsMpvStarting)
		{
			MpvLeaveButton.Focus(FocusState.Programmatic);
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		// a sign-out takes the shell away with this page on it: the next shell must start with its pane
		_chrome.Unloaded();
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
				// fluentfin-mpv player controls: FluentFin's own video shows by binding, mpv's window is shown here
				if (!_attachedComposition)
				{
					_videoHost.SetVideoVisible(ViewModel.IsMpvVideoVisible);
				}

				MoveFocusWithTheStartScreen();
				break;
			case nameof(VideoPlayerViewModel.MpvStatusText) when IsLoaded && !string.IsNullOrEmpty(ViewModel.MpvStatusText):
				// fluentfin-mpv: Narrator hears each step without focus moving (WCAG 4.1.3, docs/research/ux-review.md A4)
				FrameworkElementAutomationPeer.FromElement(MpvStepText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
				break;
			case nameof(VideoPlayerViewModel.UsesFluentFinControls) when _videoAttached && ViewModel.UsesFluentFinControls != _attachedComposition:
				// fluentfin-mpv player controls: decided when the page opened; attached before that, so attached again
				DetachVideo();
				AttachVideo();
				break;
		}
	}

	/// <summary>
	/// fluentfin-mpv: the loading screen goes when the video is shown; if its button had the keyboard, the video gets it, as
	/// once the item plays (FocusVideo), instead of nothing. When the screen comes back (an error, a crash) its button gets it.
	/// </summary>
	private void MoveFocusWithTheStartScreen()
	{
		if (ViewModel.IsMpvVideoVisible)
		{
			if (_leaveButtonFocused)
			{
				_leaveButtonFocused = false;
				FocusVideo();
			}
		}
		else if (IsLoaded && !_navigatedFrom)
		{
			MpvLeaveButton.Focus(FocusState.Programmatic);
		}
	}

	private void OnLeaveButtonFocus(object sender, RoutedEventArgs e) => _leaveButtonFocused = MpvLeaveButton.FocusState != FocusState.Unfocused;

	private void AttachVideo()
	{
		if (_videoAttached)
		{
			return;
		}

		_attachedComposition = ViewModel.UsesFluentFinControls;
		if (_attachedComposition)
		{
			// fluentfin-mpv player controls: no window over the page; mpv's swap chain in the controls' panel
			_composition.CoreLost += OnCompositionCoreLost;
			_composition.Attach(MpvControlsView.VideoSurface);
		}
		else
		{
			_videoHost.Attach(MpvVideoArea);
			_videoHost.SetVideoVisible(ViewModel.IsMpvVideoVisible);
		}

		_videoAttached = true;
	}

	private void DetachVideo()
	{
		if (!_videoAttached)
		{
			return;
		}

		// a player page that replaced this one may already have taken the window (or the video) over
		if (_attachedComposition)
		{
			_composition.CoreLost -= OnCompositionCoreLost;
			_composition.Detach(MpvControlsView.VideoSurface);
		}
		else
		{
			_videoHost.Detach(MpvVideoArea);
		}

		_videoAttached = false;
	}

	/// <summary>Full screen the way MediaPlayerHost.OnPlayerDoubleTapped does it, but to a given state (mpv asks for one).</summary>
	private void SetMpvFullScreen(bool fullScreen)
	{
		_mpvFullScreen = fullScreen;

		var log = App.GetService<ILogger<VideoPlayerPage>>();
		var kind = fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;
		if (App.MainWindow.AppWindow.Presenter.Kind != kind)
		{
			log.LogInformation("Full screen {On}: setting the presenter", fullScreen);
			App.MainWindow.AppWindow.SetPresenter(kind);
		}

		if (App.GetService<ITitleBarViewModel>() is { } titleBar)
		{
			log.LogInformation("Full screen {On}: the title bar {Shown}", fullScreen, fullScreen ? "hides" : "shows");
			titleBar.IsVisible = !fullScreen;
		}

		// fluentfin-mpv: the navigation pane stays hidden in a window too while this page is open (PlayerChrome)
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

	/// <summary>fluentfin-mpv player controls: the keyboard to the player (the controls take it), or to mpv's window.</summary>
	private void FocusVideo()
	{
		if (_attachedComposition)
		{
			MpvControlsView.Focus(FocusState.Programmatic);
		}
		else
		{
			_videoHost.Focus();
		}
	}

	/// <summary>
	/// fluentfin-mpv player controls: the player's keys anywhere on the page (Space, arrows, F, M, ...), before a focused
	/// slider takes them; only while the video is shown, and Space and Enter stay a focused button's (MpvPlayerKeys).
	/// Escape stays the page's accelerator (full screen off, else back).
	/// </summary>
	private void OnMpvPreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (ViewModel.UsesFluentFinControls)
		{
			FluentFin.Controls.MpvPlayerKeys.OnPreviewKeyDown(MpvControlsView, ViewModel.IsMpvVideoVisible, e);
		}
	}

	private void OnCompositionCoreLost() => ViewModel.OnMpvCoreLost();

	// fluentfin-mpv: closed by its button: gone in the view model too, so the next notice shows even with the same text
	private void OnMpvNoticeCloseButtonClick(InfoBar sender, object args) => ViewModel.DismissMpvNotice();

	private void OnMpvMessageCloseButtonClick(InfoBar sender, object args) => ViewModel.DismissMpvMessage();

	public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	public Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

	public Visibility VisibleIfBoth(bool first, bool second) => first && second ? Visibility.Visible : Visibility.Collapsed;

	public Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

	public InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
}
