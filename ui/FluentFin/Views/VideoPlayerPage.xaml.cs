using System.ComponentModel;
using FluentFin.Contracts.Services;
using FluentFin.Core;
using FluentFin.Core.Mpv;
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
		// never leave the window cloaked
		if (_cloakedHwnd != 0)
		{
			EndSwitch();
		}

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

	/// <summary>
	/// Every way into and out of full screen (F, F11, the button, a double click, Escape, mpv's own request, leaving the page)
	/// ends here, so they all switch the same way: one switch at a time (<see cref="FullScreenSwitch"/>), the window cloaked
	/// while it changes, the last request winning. Full screen the way MediaPlayerHost.OnPlayerDoubleTapped does it, but to a
	/// given state (mpv asks for one).
	/// </summary>
	private void SetMpvFullScreen(bool fullScreen)
	{
		_mpvFullScreen = fullScreen;
		if (_switch.Request(fullScreen) == FullScreenSwitch.Outcome.Unchanged)
		{
			SetTitleBar(fullScreen);
		}
	}

	private FullScreenSwitch? _switchField;

	private FullScreenSwitch _switch => _switchField ??= new FullScreenSwitch(
		() => App.MainWindow.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen, ApplyFullScreen);

	/// <summary>
	/// The window shows again once the panel has its new size and the picture's hold (the layout), XAML has rendered two frames
	/// since, and at least <see cref="CloakMinimumMilliseconds"/> have passed since the presenter changed: DWM shows what XAML
	/// committed a frame or two later, and uncloaking at the second rendered frame still let the half-done layout through (measured).
	/// After <see cref="CloakLimitMilliseconds"/> it shows anyway: a stalled UI thread must not leave the window hidden
	/// (docs/research/fullscreen-smoothness.md). The frames XAML renders before the layout (inside SetPresenter, with the
	/// window's new size) do not count.
	/// </summary>
	private const int CloakMinimumMilliseconds = 60;

	private const int CloakLimitMilliseconds = 150;

	private const int CloakRenderedFrames = 2;

	private int _switchFrames;
	private bool _switchLayoutSeen;
	private long _switchStarted;

	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _uncloakTimer;

	private void ApplyFullScreen(bool fullScreen)
	{
		var log = App.GetService<ILogger<VideoPlayerPage>>();
		var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
		_switchStarted = Environment.TickCount64;
		var cloaked = WindowCloak.Enabled && _attachedComposition && WindowCloak.Set(hwnd, true);
		try
		{
			var kind = fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;
			log.LogInformation("Full screen {On}: setting the presenter{Cloak}", fullScreen, cloaked ? " (window cloaked)" : "");
			App.MainWindow.AppWindow.SetPresenter(kind);
			SetTitleBar(fullScreen);
		}
		catch
		{
			if (cloaked)
			{
				WindowCloak.Set(hwnd, false);
			}

			throw;
		}

		// the window shows again once the layout and the picture's hold are committed; the next switch waits for that
		_switchFrames = 0;
		_switchLayoutSeen = false;
		_composition.PanelSizeChanged -= OnSwitchLayout;
		_composition.PanelSizeChanged += OnSwitchLayout;
		Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSwitchFrame;
		Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSwitchFrame;
		_uncloakTimer ??= DispatcherQueue.CreateTimer();
		_uncloakTimer.Interval = TimeSpan.FromMilliseconds(CloakMinimumMilliseconds);
		_uncloakTimer.IsRepeating = false;
		_uncloakTimer.Tick -= OnUncloakTick;
		_uncloakTimer.Tick += OnUncloakTick;
		_cloakedHwnd = cloaked ? hwnd : 0;
		_uncloakTimer.Start();
	}

	private nint _cloakedHwnd;

	private void OnUncloakTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
	{
		if (!_attachedComposition || (_switchLayoutSeen && _switchFrames >= CloakRenderedFrames) || Environment.TickCount64 - _switchStarted >= CloakLimitMilliseconds)
		{
			EndSwitch();
			return;
		}

		sender.Interval = TimeSpan.FromMilliseconds(16);
		sender.Start();
	}

	private void OnSwitchLayout() => _switchLayoutSeen = true;

	private void OnSwitchFrame(object? sender, object e)
	{
		if (_switchLayoutSeen)
		{
			_switchFrames++;
		}
	}

	/// <summary>The window shows again and the switch counts as done (a request that waited goes now). Also when the page is left.</summary>
	private void EndSwitch()
	{
		_uncloakTimer?.Stop();
		Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSwitchFrame;
		_composition.PanelSizeChanged -= OnSwitchLayout;
		if (_cloakedHwnd != 0)
		{
			App.GetService<ILogger<VideoPlayerPage>>().LogInformation("Full screen: the window shows again after {Ms} ms ({Frames} rendered frames)", Environment.TickCount64 - _switchStarted, _switchFrames);
			WindowCloak.Set(_cloakedHwnd, false);
			_cloakedHwnd = 0;
		}

		_switch.Settled();
	}

	private void SetTitleBar(bool fullScreen)
	{
		var log = App.GetService<ILogger<VideoPlayerPage>>();
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
