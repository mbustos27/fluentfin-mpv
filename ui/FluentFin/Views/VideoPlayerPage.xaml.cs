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
		// the state a running switch is making, also before its presenter change (the cover's frame comes first)
		ViewModel.IsFullScreen = () => _switch.Target;
		ViewModel.FocusVideo = FocusVideo;
		ViewModel.GoBack = GoBack;
	}

	protected override void OnNavigatedFrom(NavigationEventArgs e)
	{
		_navigatedFrom = true;
		_chrome.NavigatedFrom();

		// Seaglass 1.1.3 (PlayerPageLeave): the video first, so a request that waited for a running switch goes at once with
		// nothing to wait for; then the switch ends (its cover goes); then the window is restored and the backend told, before
		// the view model stops (the next page starts from the real state), whatever a switch was doing
		PlayerPageLeave.Run(
			DetachVideo,
			() =>
			{
				EndSwitch();
				StopWaitingForRestore();
			},
			() => _ = ViewModel.LeaveFullScreen());
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

		// Seaglass 1.1.3: Escape's auto-repeat, seen wherever the keyboard is in the window (the accelerator is the window's)
		if (XamlRoot?.Content is UIElement root && !ReferenceEquals(root, _keyRoot))
		{
			_keyRoot?.RemoveHandler(PreviewKeyDownEvent, _rootPreviewKeyDown);
			_keyRoot = root;
			root.AddHandler(PreviewKeyDownEvent, _rootPreviewKeyDown, handledEventsToo: true);
		}

		// the loading screen's only control: Enter (its Cancel) or Escape leaves before the start goes on
		if (ViewModel.IsMpvStarting)
		{
			MpvLeaveButton.Focus(FocusState.Programmatic);
		}
	}

	private void OnUnloaded(object sender, RoutedEventArgs e)
	{
		// the video first: a request that waited for the running switch then goes at once, with nothing to wait for
		DetachVideo();

		// never leave the window hidden: the switch ends whether or not it hid it (its timers and handlers go too)
		EndSwitch();
		StopWaitingForRestore();

		_keyRoot?.RemoveHandler(PreviewKeyDownEvent, _rootPreviewKeyDown);
		_keyRoot = null;

		// a sign-out takes the shell away with this page on it: the next shell must start with its pane
		_chrome.Unloaded();

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
		// Seaglass 1.1.3: nothing of the in-process video is on this page any more (a full-screen switch then waits for no layout)
		_attachedComposition = false;
	}

	/// <summary>
	/// Every way into and out of full screen (F, F11, the button, a double click, Escape, mpv's own request, leaving the page)
	/// ends here, so they all switch the same way: one switch at a time (<see cref="FullScreenSwitch"/>), the window hidden
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

	/// <summary>Seaglass 1.1.3: how a switch hides the window (<c>SEAGLASS_FULLSCREEN</c>: cover, cloak, none).</summary>
	private static readonly FullScreenHide HideMode = FullScreenCloak.FromEnvironment();

	/// <summary>The switches' clock (TickCount64 moves in 15.6 ms steps: some 1.1.2 switches showed at 61-63 ms of a 70 ms minimum).</summary>
	private readonly System.Diagnostics.Stopwatch _switchClock = System.Diagnostics.Stopwatch.StartNew();

	private FullScreenTransition? _transitionField;

	/// <summary>The running switch's rules (<see cref="FullScreenTransition"/>); this page only wires it to the window.</summary>
	private FullScreenTransition _transition => _transitionField ??= new FullScreenTransition(HideMode, () => _switchClock.Elapsed, new FullScreenTransition.Host
	{
		Hide = Hide,
		Unhide = Unhide,
		SetPresenter = SetPresenter,
		PrepareInset = PrepareSubtitleInset,
		Settled = OnSwitchSettled,
		AfterFrame = work => DispatcherQueue.TryEnqueue(() => work()),
		Failed = ex => App.GetService<ILogger<VideoPlayerPage>>().LogError(ex, "Full screen: the presenter change failed"),
		Shown = (elapsed, frames, how) => App.GetService<ILogger<VideoPlayerPage>>().LogInformation(
			"Full screen: the window shows again after {Ms} ms ({Frames} rendered frames, {Hide})", (long)elapsed.TotalMilliseconds, frames,
			how == FullScreenHide.Cover ? "covered" : "cloaked"),
		Minimized = () => IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)),
		SwitchWhenRestored = SwitchWhenRestored,
	});

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
	private static extern bool IsIconic(nint hwnd);

	/// <summary>A switch that came due while the window was minimized: made once it is restored.</summary>
	private bool? _afterRestore;

	private void SwitchWhenRestored(bool fullScreen)
	{
		App.GetService<ILogger<VideoPlayerPage>>().LogInformation("Full screen {On}: the window is minimized; switching once it is restored", fullScreen);
		_afterRestore = fullScreen;
		App.MainWindow.AppWindow.Changed -= OnWindowChangedWhileMinimized;
		App.MainWindow.AppWindow.Changed += OnWindowChangedWhileMinimized;
	}

	private void OnWindowChangedWhileMinimized(AppWindow sender, AppWindowChangedEventArgs args)
	{
		if (_afterRestore is not { } fullScreen || IsIconic(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow)))
		{
			return;
		}

		StopWaitingForRestore();
		if (IsLoaded && !_navigatedFrom)
		{
			SetMpvFullScreen(fullScreen);
		}
	}

	private void StopWaitingForRestore()
	{
		_afterRestore = null;
		// Seaglass 1.1.3 integration: the page also unloads while the window closes, when its AppWindow is already gone; an
		// exception here skipped the rest of OnUnloaded (the key handler, the chrome, PlayerPageRemoval) and logged an error
		if (App.MainWindow.AppWindow is { } appWindow)
		{
			appWindow.Changed -= OnWindowChangedWhileMinimized;
		}
	}

	private nint _cloakedHwnd;
	private System.Threading.Timer? _hardLimit;
	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _softLimit;

	/// <summary>The video's height in a window, kept as full screen begins: the subtitle inset going back is for it.</summary>
	private double _windowedVideoHeight;

	private void ApplyFullScreen(bool fullScreen)
	{
		// listening before the presenter changes (a layout may come inside it); a switch that waits for nothing ends inside
		// Apply, and OnSwitchSettled stops listening
		_composition.PanelSizeChanged -= OnSwitchLayout;
		_composition.PanelSizeChanged += OnSwitchLayout;
		Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSwitchFrame;
		Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSwitchFrame;
		_transition.Apply(fullScreen, _videoAttached, _attachedComposition);
		if (_transition.IsRunning)
		{
			// the window shows again once the layout and the picture's hold are done, or at the limit (this switch's)
			_softLimitGeneration = _transition.Generation;
			_softLimit ??= DispatcherQueue.CreateTimer();
			_softLimit.Interval = FullScreenReveal.SoftLimit;
			_softLimit.IsRepeating = false;
			_softLimit.Tick -= OnSoftLimit;
			_softLimit.Tick += OnSoftLimit;
			_softLimit.Start();
		}
	}

	private void SetPresenter(bool fullScreen)
	{
		var kind = fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;
		App.GetService<ILogger<VideoPlayerPage>>().LogInformation("Full screen {On}: setting the presenter{Hide}", fullScreen, _transition.Hidden switch
		{
			FullScreenHide.Cover => " (window covered)",
			FullScreenHide.Cloak => " (window cloaked)",
			_ => "",
		});
		App.MainWindow.AppWindow.SetPresenter(kind);
		SetTitleBar(fullScreen);
	}

	/// <summary>Hides the window for the switch: true when it did. Each hide has a limit kept off the UI thread.</summary>
	private bool Hide(FullScreenHide mode)
	{
		switch (mode)
		{
			case FullScreenHide.Cover:
				ShowCover();
				return true;
			case FullScreenHide.Cloak:
				var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
				if (!WindowCloak.Set(hwnd, true))
				{
					return false;
				}

				_cloakedHwnd = hwnd;
				// the window shows by then even with the UI thread stuck (DWM takes the call from any thread); only for this
				// cloak: a late limit of the switch before must not show the next one early (Dispose does not wait for it)
				var generation = _cloakGeneration.Next();
				_hardLimit?.Dispose();
				_hardLimit = new System.Threading.Timer(_ => _cloakGeneration.RunIfCurrent(generation, () => WindowCloak.Set(hwnd, false)),
					null, FullScreenReveal.HardLimit, System.Threading.Timeout.InfiniteTimeSpan);
				return true;
			default:
				return false;
		}
	}

	private readonly HideGeneration _cloakGeneration = new();

	private void Unhide()
	{
		// the running cloak's limit goes stale first: it can no longer act, even if its callback is already running
		_cloakGeneration.Next();
		_hardLimit?.Dispose();
		_hardLimit = null;
		if (_cloakedHwnd != 0)
		{
			WindowCloak.Set(_cloakedHwnd, false);
			_cloakedHwnd = 0;
		}

		HideCover();
	}

	// -- the cover: black over the whole window while it switches (Seaglass 1.1.3) ------------------------------------------

	/// <summary>Larger than any window (DIPs): the window grows under it as it goes full screen.</summary>
	private const double CoverSize = 16384;

	private Microsoft.UI.Xaml.Controls.Primitives.Popup? _cover;
	private Border? _coverFill;

	/// <summary>
	/// A black layer over all of the window's XAML (the title bar row too: a popup is drawn above the page and the shell), so
	/// the screen shows black for the switch instead of what is behind the window (the cloak's flash). Its compositor
	/// animation takes it away at <see cref="FullScreenReveal.HardLimit"/> even if the UI thread never comes back.
	/// </summary>
	private void ShowCover()
	{
		if (_cover is null)
		{
			_coverFill = new Border
			{
				Width = CoverSize,
				Height = CoverSize,
				Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black),
				IsHitTestVisible = false,
			};
			Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_coverFill, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
			_cover = new Microsoft.UI.Xaml.Controls.Primitives.Popup { Child = _coverFill, IsHitTestVisible = false };
		}

		_cover.XamlRoot = XamlRoot;
		var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_coverFill);
		var compositor = visual.Compositor;
		var linear = compositor.CreateLinearEasingFunction();
		var limit = compositor.CreateScalarKeyFrameAnimation();
		limit.InsertKeyFrame(0f, 1f, linear);
		limit.InsertKeyFrame(0.99f, 1f, linear);
		limit.InsertKeyFrame(1f, 0f, linear);
		limit.Duration = FullScreenReveal.HardLimit;
		visual.Opacity = 1f;
		visual.StartAnimation("Opacity", limit);
		_cover.IsOpen = true;
	}

	private void HideCover()
	{
		if (_cover is { IsOpen: true } cover)
		{
			cover.IsOpen = false;
			if (_coverFill is { } fill)
			{
				var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(fill);
				visual.StopAnimation("Opacity");
				visual.Opacity = 1f;
			}
		}
	}

	// -- when the switch ends -------------------------------------------------------------------------------------------------

	private void OnSwitchLayout() => _transitionField?.Layout();

	private void OnSwitchFrame(object? sender, object e) => _transitionField?.Frame(_composition.IsHolding);

	private int _softLimitGeneration;

	private void OnSoftLimit(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
	{
		if (_transitionField is { } transition && transition.Generation == _softLimitGeneration)
		{
			transition.LimitReached();
		}
	}

	/// <summary>The window shows again and the switch counts as done (a request that waited goes now). Also when the page is left.</summary>
	private void EndSwitch() => _transition.End();

	private void OnSwitchSettled()
	{
		_softLimit?.Stop();
		Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSwitchFrame;
		_composition.PanelSizeChanged -= OnSwitchLayout;
		_switch.Settled();
	}

	/// <summary>
	/// The subtitles' inset (above the bottom bar) for the height the video is about to have: the display's in full screen,
	/// the one it had in the window going back. Sent before the presenter changes, so they move with the switch, under the
	/// cover, not after the window shows.
	/// </summary>
	private void PrepareSubtitleInset(bool fullScreen)
	{
		double height;
		if (fullScreen)
		{
			_windowedVideoHeight = MpvControlsView.ActualHeight;
			var display = DisplayArea.GetFromWindowId(App.MainWindow.AppWindow.Id, DisplayAreaFallback.Nearest);
			var scale = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
			height = display is null ? 0 : display.OuterBounds.Height / scale;
		}
		else
		{
			height = _windowedVideoHeight;
		}

		MpvControlsView.ReportBottomBarFor(height);
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

	private UIElement? _keyRoot;
	private KeyEventHandler? _rootPreviewKeyDownField;
	private KeyEventHandler _rootPreviewKeyDown => _rootPreviewKeyDownField ??= OnRootPreviewKeyDown;

	/// <summary>The Escape that is being handled now is the key's auto-repeat (it was already down).</summary>
	private bool _escapeRepeat;

	/// <summary>
	/// Seaglass 1.1.3: every key in the window, before anything else (the accelerator too): a held Escape repeats, and each
	/// repeat used to be another Escape: out of full screen, then out of the player, stopping the playback. Its repeats are
	/// marked and do nothing (<see cref="FluentFin.Controls.MpvPlayerKeys.IgnoresRepeat"/>).
	/// </summary>
	private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (e.Key != VirtualKey.Escape)
		{
			return;
		}

		_escapeRepeat = FluentFin.Controls.MpvPlayerKeys.IgnoresRepeat(e.Key, e.KeyStatus.WasKeyDown);
		if (_escapeRepeat && ViewModel.IsMpv)
		{
			e.Handled = true;
		}
	}

	private async void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
	{
		if (!ViewModel.IsMpv)
		{
			return;
		}

		args.Handled = true;
		if (_escapeRepeat)
		{
			// a held Escape: the first press did its job
			return;
		}

		switch (PlayerEscape.For(_mpvFullScreen, ViewModel.IsMpvStarting))
		{
			case PlayerEscape.Action.CancelStart:
				// button review P27: on the loading screen Escape is its Cancel again (nothing plays yet)
				if (ViewModel.LeaveMpvCommand.CanExecute(null))
				{
					ViewModel.LeaveMpvCommand.Execute(null);
				}

				break;
			case PlayerEscape.Action.LeaveFullScreen:
				SetMpvFullScreen(false);
				await ViewModel.OnFullScreenChanged(false);
				break;
			default:
				// 1.2.0 (the interaction audit, item 1): in a window Escape does nothing to playback, as in every video player;
				// it shows the controls. Back, Alt+Left, the Back key and Mouse 4 leave the player.
				ViewModel.MpvControls?.NotifyActivity();
				break;
		}
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
	/// Escape stays the page's accelerator (full screen off; in a window, the controls shown).
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
