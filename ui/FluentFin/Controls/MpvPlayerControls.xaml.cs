using FluentFin.Core.Mpv;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Microsoft.UI.Composition;
using FluentFin.Services;

namespace FluentFin.Controls;

/// <summary>
/// fluentfin-mpv player controls: lays out <see cref="MpvControlsViewModel"/> over the composition video and passes the
/// input on (docs/research/player-controls.md, phase 2b). Pointer moves show the controls; a click on the video pauses or
/// plays, a double click goes full screen; the wheel is volume, anywhere over the player; keys come from the page
/// (<see cref="HandleKey"/>). A heartbeat (<see cref="MpvControlsViewModel.Tick"/>) hides the controls and moves the time.
/// </summary>
public sealed partial class MpvPlayerControls : UserControl
{
	public static readonly DependencyProperty ControlsProperty = DependencyProperty.Register(
		nameof(Controls), typeof(MpvControlsViewModel), typeof(MpvPlayerControls), new PropertyMetadata(null, OnControlsChanged));

	private DispatcherQueueTimer? _tick;
	private bool _seekBarPressed;

	public MpvPlayerControls()
	{
		InitializeComponent();
		Loaded += (_, _) =>
		{
			StartTick();
			GlassMode.Changed += OnGlassModeChanged;
			ConfigureFades();
		};
		Unloaded += (_, _) =>
		{
			StopTick();
			GlassMode.Changed -= OnGlassModeChanged;
			DropWaitingClick(Controls);
		};

		// the slider marks the pointer handled itself: listen anyway, for the start and the end of a drag or a click
		SeekBar.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekBarPointerPressed), handledEventsToo: true);
		SeekBar.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekBarPointerReleased), handledEventsToo: true);
		SeekBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekBarPointerReleased), handledEventsToo: true);
		SeekBar.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSeekBarPointerMoved), handledEventsToo: true);
		SeekBar.AddHandler(PointerExitedEvent, new PointerEventHandler(OnSeekBarPointerExited), handledEventsToo: true);

		// Nixie 1.3.1: after any press of the mouse on the player, the keyboard is the player's (KeepKeyboardAfterPointer)
		Root.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnRootPointerReleased), handledEventsToo: true);
		Root.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnRootPointerReleased), handledEventsToo: true);

		// Nixie 1.4.0 (the user's "lost the player controls overlay" on 1.3.1): any move of the pointer over the player shows the
		// bars, also one a control below it marked handled (in 1.3.1 Root heard only moves nothing had handled)
		Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
		Root.AddHandler(PointerEnteredEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
	}

	public MpvControlsViewModel? Controls
	{
		get => (MpvControlsViewModel?)GetValue(ControlsProperty);
		set => SetValue(ControlsProperty, value);
	}

	/// <summary>The panel the in-process mpv's swap chain is shown in (Mpv.MpvCompositionVideo binds it).</summary>
	public SwapChainPanel VideoSurface => VideoPanel;

	private static void OnControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
	{
		var self = (MpvPlayerControls)d;
		if (e.OldValue is MpvControlsViewModel old)
		{
			old.PropertyChanged -= self.OnControlsPropertyChanged;
			old.PropertyChanging -= self.OnControlsPropertyChanging;
			DropWaitingClick(old);
		}

		if (e.NewValue is MpvControlsViewModel controls)
		{
			controls.DoubleClickTime = TimeSpan.FromMilliseconds(GetDoubleClickTime());
			controls.PropertyChanged += self.OnControlsPropertyChanged;
			controls.PropertyChanging += self.OnControlsPropertyChanging;
			self.UpdateCursor();
			self.ReportBottomBar();
			self.PlaceSkipButton();
		}
	}

	private void OnControlsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(MpvControlsViewModel.VolumeFraction))
		{
			AnimateVolumeFill();
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.VolumeText) && Controls is { VolumeIndicatorVisible: true } shown)
		{
			AnnounceVolume(shown.VolumeText);
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.VolumeIndicatorVisible) && Controls is { VolumeIndicatorVisible: true } appearing)
		{
			// a key at the limit changes no text: the indicator showing is the announcement
			AnnounceVolume(appearing.VolumeText);
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.ToastText) or nameof(MpvControlsViewModel.ToastVisible) && Controls is { ToastVisible: true } toast)
		{
			// 1.2.0: a key's new speed or subtitle delay
			Announce(toast.ToastText, "PlayerStatus");
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.IsNextPromptVisible) && Controls is { IsNextPromptVisible: true, IsCountingDown: false } prompt)
		{
			// the Next prompt came (near the end); it is not focused, Shift+N plays it
			Announce($"{prompt.NextToolTip}. Shift+N plays it.", "NextPrompt");
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.IsCountingDown) && Controls is { IsCountingDown: true, NextCountdown: int seconds } counting)
		{
			// 1.2.0: once, as the countdown starts (not every second)
			// Nixie 1.3.1 (the UX review's DI-m2): the control that stops it is named as it is ("Keep watching", "Watch credits"); no control is called Cancel
			Announce(CountdownAnnouncement(seconds, counting.NextItemTitle, counting.CancelNextText), "NextPrompt");
		}

		if (e.PropertyName == nameof(MpvControlsViewModel.ControlsVisible))
		{
			PlaceSkipButton();
		}

		if (e.PropertyName == nameof(MpvControlsViewModel.CursorHidden))
		{
			UpdateCursor();
		}
		else if (e.PropertyName == nameof(MpvControlsViewModel.ControlsVisible) && Controls is { ControlsVisible: false })
		{
			// a focus the collapse left nowhere
			if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is null)
			{
				Focus(FocusState.Programmatic);
			}
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.IsSkipVisible) && Controls is { IsSkipVisible: false }
			|| e.PropertyName is nameof(MpvControlsViewModel.IsNextPromptVisible) && Controls is { IsNextPromptVisible: false }
			|| e.PropertyName is nameof(MpvControlsViewModel.IsCountingDown) && Controls is { IsCountingDown: false })
		{
			// the Skip button or the Next pill went (pressed, or no longer offered): a focus it left nowhere
			if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is null)
			{
				Focus(FocusState.Programmatic);
			}
		}
	}

	/// <summary>
	/// The bars are about to hide (raised before their Visibility follows): the keyboard is moved now, while they still
	/// show. After the collapse WinUI would already have handed it to the next control outside the player (the title bar),
	/// where Space and the player's keys do nothing.
	/// </summary>
	private void OnControlsPropertyChanging(object? sender, System.ComponentModel.PropertyChangingEventArgs e)
	{
		if (e.PropertyName == nameof(MpvControlsViewModel.ControlsVisible) && Controls is { ControlsVisible: true })
		{
			KeepKeyboardWhenBarsHide();
		}
		else if (XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused
			&& (e.PropertyName == nameof(MpvControlsViewModel.IsSkipVisible) && Controls is { IsSkipVisible: true } && IsWithin(focused, SkipSegmentButton)
				|| e.PropertyName == nameof(MpvControlsViewModel.IsNextPromptVisible) && Controls is { IsNextPromptVisible: true } && IsWithin(focused, NextPill)
				|| e.PropertyName == nameof(MpvControlsViewModel.IsCountingDown) && Controls is { IsCountingDown: true } && IsWithin(focused, CancelNextButton)))
		{
			// 1.2.0: the Next pill likewise (pressed: the next item starts, and it is not near its end)
			// the Skip button is about to go with the focus on it (the keyboard's, or the click's): the player takes it now,
			// so its keys go on working (WinUI would hand it to the next control outside the player)
			Focus(FocusState.Programmatic);
		}
	}

	/// <summary>
	/// Set by the page: the part of the window whose other controls keep the keyboard when the bars hide (a notice's close
	/// button the keyboard went to); from anywhere outside it (the title bar) the player takes it back. Null: only from
	/// the bars, or from nowhere.
	/// </summary>
	public UIElement? KeyboardHome { get; set; }

	// -- the keyboard in the bars ----------------------------------------------------------------------------------------

	/// <summary>
	/// The keyboard moved into, within or out of the bars: while Tab has it on one of their controls they stay up (a hidden
	/// bar would take the focused control, and the keyboard, with it).
	/// </summary>
	private void OnBarFocusChanged(object sender, RoutedEventArgs e)
	{
		if (Controls is not { } controls || XamlRoot is null)
		{
			return;
		}

		var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
		var inBars = focused is Control { FocusState: FocusState.Keyboard } && IsInBars(focused);
		if (inBars && !controls.KeyboardInControls)
		{
			controls.NotifyActivity();
		}

		controls.KeyboardInControls = inBars;
	}

	/// <summary>
	/// The bars hide while the focus is on one of their controls (one the mouse clicked or the app focused: the keyboard's
	/// hold them up), nowhere, or outside <see cref="KeyboardHome"/>: the player takes it, so its keys go on working.
	/// </summary>
	private void KeepKeyboardWhenBarsHide()
	{
		if (XamlRoot is null)
		{
			return;
		}

		var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
		if (focused is null || IsInBars(focused) || (KeyboardHome is { } home && !IsWithin(focused, home)))
		{
			Focus(FocusState.Programmatic);
		}
	}

	private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
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

	private void StartTick()
	{
		_tick ??= DispatcherQueue.CreateTimer();
		_tick.Interval = TimeSpan.FromMilliseconds(250);
		_tick.Tick -= OnTick;
		_tick.Tick += OnTick;
		_tick.Start();
	}

	private void StopTick()
	{
		_tick?.Stop();
		RestoreCursor();
	}

	private void OnTick(DispatcherQueueTimer sender, object args)
	{
		WakeOnCursorMove();
		Controls?.Tick();
		KeepKeyboardSomewhere();
	}

	// -- the mouse, whatever XAML hears (Nixie 1.4.0) ----------------------------------------------------------------------

	/// <summary>Where the mouse's cursor was at the last heartbeat (screen pixels); null when it could not be read.</summary>
	private (int X, int Y)? _lastCursor;

	/// <summary>
	/// Nixie 1.4.0, the user's report on 1.3.1: "we've lost the player controls overlay" ("i opened the player and then hit the
	/// spacebar and it like locked me in keyboard only mode"): the keys worked, and moving the mouse never brought the bars back
	/// until the player was opened again. Showing the bars on a move hung on XAML's pointer events reaching the player alone.
	/// Now the heartbeat also reads the cursor itself: a cursor that moved over the player, with the window in front, shows the
	/// bars, whichever element (or window) the pointer's input went to. No mode: a move always shows them, a key always works.
	/// </summary>
	private void WakeOnCursorMove()
	{
		var now = CursorPosition();
		var last = _lastCursor;
		_lastCursor = now;
		if (now is not { } at || Controls is not { } controls || XamlRoot is null || !IsLoaded || Visibility != Visibility.Visible)
		{
			return;
		}

		var heard = _pointerHeard;
		_pointerHeard = false;
		if (CursorWakes(last, at, OverPlayer(at), InFront()))
		{
			if (!heard && !controls.ControlsVisible && !_missedLogged)
			{
				// the evidence of the 1.3.1 report, once per player: the mouse moved over the player and XAML told it nothing
				_missedLogged = true;
				App.GetService<Microsoft.Extensions.Logging.ILogger<MpvPlayerControls>>().LogInformation("[player] the cursor moved over the player and no pointer event came: the bars are shown from the cursor ({State})", OverlayState());
			}

			controls.NotifyActivity();
		}
	}

	/// <summary>A pointer move reached the player through XAML since the last heartbeat.</summary>
	private bool _pointerHeard;

	/// <summary>A move XAML missed was logged for this player.</summary>
	private bool _missedLogged;

	/// <summary>The bars show: the cursor moved since the last heartbeat, it is over the player, and the window is the one in front.</summary>
	internal static bool CursorWakes((int X, int Y)? last, (int X, int Y) now, bool overPlayer, bool inFront) =>
		inFront && overPlayer && last is { } before && before != now;

	private static (int X, int Y)? CursorPosition() => GetCursorPos(out var p) ? (p.X, p.Y) : null;

	private static nint MainWindowHandle()
	{
		try
		{
			return WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
		}
		catch (Exception)
		{
			return 0;
		}
	}

	/// <summary>One of the app's windows is the foreground window (never wake the bars for a cursor moving over another app).</summary>
	private static bool InFront()
	{
		var front = GetForegroundWindow();
		return front != 0 && GetWindowThreadProcessId(front, out var process) != 0 && process == (uint)Environment.ProcessId;
	}

	/// <summary>Whether a point on the screen is over the player (this control's bounds, in the window).</summary>
	private bool OverPlayer((int X, int Y) at)
	{
		var main = MainWindowHandle();
		if (main == 0 || XamlRoot is not { } root || ActualWidth <= 0)
		{
			return false;
		}

		var client = new POINT { X = at.X, Y = at.Y };
		if (!ScreenToClient(main, ref client))
		{
			return false;
		}

		var scale = root.RasterizationScale > 0 ? root.RasterizationScale : 1;
		var bounds = TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight));
		return bounds.Contains(new Windows.Foundation.Point(client.X / scale, client.Y / scale));
	}

	[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
	private struct POINT
	{
		public int X, Y;
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT point);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool ScreenToClient(nint hwnd, ref POINT point);

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);

	/// <summary>A key pressed on the player page. True when it is one of the player's keys (it was handled).</summary>
	public bool HandleKey(Windows.System.VirtualKey key)
	{
		if (Controls is not { } controls)
		{
			return false;
		}

		var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;
		bool Down(Windows.System.VirtualKey k) => state(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
		return controls.HandleKey((int)key, Down(Windows.System.VirtualKey.Control), Down(Windows.System.VirtualKey.Shift), Down(Windows.System.VirtualKey.Menu));
	}

	/// <summary>Nixie 1.3.1: a player action the key router chose (<see cref="MpvPlayerKeys"/>): the controls show and it is done.</summary>
	public void Run(MpvControlsAction action) => _ = Controls?.RunKey(action);

	// -- the keyboard after the mouse (Nixie 1.3.1) ----------------------------------------------------------------------

	/// <summary>
	/// A press of the mouse anywhere on the player has ended (a click, a drag of the seek bar, a tap on the video): the keyboard
	/// is the player's again at once, as in every video player, unless the press opened a menu (its flyout has the keyboard
	/// now). The player's own controls take no focus from a click (AllowFocusOnInteraction), so this is for the rest: the
	/// focus left in the title bar or on nothing, a theme panel's button, and the window's own keyboard focus (a click must
	/// never leave the keys going to a window that does nothing with them). Run after the click's own work (a menu opening).
	/// </summary>
	private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, KeepKeyboardAfterPointer);

	internal void KeepKeyboardAfterPointer()
	{
		if (XamlRoot is not { } root || !IsLoaded || Visibility != Visibility.Visible || HasOpenMenu(root))
		{
			return;
		}

		var focused = FocusManager.GetFocusedElement(root);
		if (!ReferenceEquals(focused, this))
		{
			Focus(FocusState.Pointer);
		}

		KeyboardFocus.EnsureWindowHasIt(root, "a click on the player");
	}

	/// <summary>A menu, flyout or dialog is open (its keys are its own); tooltips do not count.</summary>
	private static bool HasOpenMenu(XamlRoot root)
	{
		foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root))
		{
			if (popup.IsOpen && popup.Child is not ToolTip)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The heartbeat's check: the focus went nowhere (the element that had it was collapsed or removed: a theme's control, the
	/// title bar as full screen begins) while the player shows and the app is in front: it comes back to the player, so the
	/// keys never stop until the bars happen to hide (1.3.0).
	/// </summary>
	private void KeepKeyboardSomewhere()
	{
		if (XamlRoot is { } root && IsLoaded && Visibility == Visibility.Visible && KeyboardFocus.AppIsActive()
			&& FocusManager.GetFocusedElement(root) is null && !HasOpenMenu(root))
		{
			Focus(FocusState.Programmatic);
		}
	}

	// -- pointer over the video ------------------------------------------------------------------------------------------

	private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
	{
		_pointerHeard = true;
		Controls?.NotifyActivity();
	}

	/// <summary>The harness's pointer move (HarnessPointer, dev builds): what <see cref="OnPointerMoved"/> does.</summary>
	internal void HarnessPointerMoved() => Controls?.NotifyActivity();

	/// <summary>The overlay as XAML has it, for the harness's log: the view model's word, each bar's visibility, size and opacity.</summary>
	internal string OverlayState()
	{
		static string Bar(FrameworkElement bar) => $"{bar.Visibility} {bar.ActualWidth:0}x{bar.ActualHeight:0} opacity {bar.Opacity:0.##}";
		var focused = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
		var focus = focused switch
		{
			null => "nothing",
			Control { Name.Length: > 0 } named => $"{named.GetType().Name} {named.Name} ({named.FocusState})",
			Control control => $"{control.GetType().Name} ({control.FocusState})",
			_ => focused.GetType().Name,
		};
		var island = XamlRoot?.ContentIsland is { } content ? Microsoft.UI.Input.InputFocusController.GetForIsland(content).HasFocus.ToString() : "?";
		return $"controls {(Controls is { } c ? $"visible {c.ControlsVisible}, held by {c.HeldBy ?? "nothing"}, cursor hidden {c.CursorHidden}" : "none")}; player {Visibility} {ActualWidth:0}x{ActualHeight:0}; top {Bar(TopBar)}; bottom {Bar(BottomBar)}; focus {focus}; the island has the keyboard {island}";
	}

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern uint GetDoubleClickTime();

	private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
	{
		if (TapVideo(e.OriginalSource as DependencyObject, e.GetPosition))
		{
			e.Handled = true;
		}
	}

	private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (DoubleTapVideo(e.OriginalSource as DependencyObject, e.GetPosition))
		{
			e.Handled = true;
		}
	}

	/// <summary>
	/// A click whose topmost element is <paramref name="source"/> (<paramref name="position"/>: the click's place relative to an
	/// element): on the video it plays or pauses at once (1.2.0, MpvControlsViewModel.VideoClicked; a double click puts it back).
	/// True when it was the video's. Also the harness's way in (HarnessPointer): posted clicks never reach WinUI off-screen.
	/// </summary>
	internal bool TapVideo(DependencyObject? source, Func<UIElement, Windows.Foundation.Point> position)
	{
		if (Controls is not { } controls || IsOnControls(source, position))
		{
			return false;
		}

		_ = controls.VideoClicked();
		return true;
	}

	/// <summary>A double click on the video: full screen, the play state back to what it was before its first click. Never on a control.</summary>
	internal bool DoubleTapVideo(DependencyObject? source, Func<UIElement, Windows.Foundation.Point> position)
	{
		if (Controls is not { } controls || IsOnControls(source, position))
		{
			return false;
		}

		controls.VideoDoubleClicked();
		return true;
	}

	/// <summary>What Narrator hears as the countdown to the next episode starts.</summary>
	internal static string CountdownAnnouncement(int seconds, string? nextTitle, string stopButton) =>
		$"Next episode in {seconds} seconds{(nextTitle is { } title ? $": {title}" : "")}. Shift+N plays it now; {stopButton} keeps this one playing.";

	/// <summary>The controls go or change: a double click after this puts nothing back.</summary>
	private static void DropWaitingClick(MpvControlsViewModel? controls) => controls?.CancelClick();

	/// <summary>The wheel is volume over the whole player (never a seek, never a scroll); the volume slider's own drag is not.</summary>
	private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		if (Controls is not { } controls)
		{
			return;
		}

		e.Handled = true;
		Wheel(controls, e.GetCurrentPoint(this).Properties.MouseWheelDelta);
	}

	/// <summary>A wheel notch over the player: volume, and the keyboard stays the player's (also the harness's way in).</summary>
	internal void Wheel(MpvControlsViewModel controls, int delta)
	{
		_ = controls.Wheel(delta);
		DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, KeepKeyboardAfterPointer);
	}

	private bool IsInBars(DependencyObject? source)
	{
		for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, TopBar) || ReferenceEquals(node, BottomBar))
			{
				return true;
			}

			if (ReferenceEquals(node, Root))
			{
				return false;
			}
		}

		return false;
	}

	/// <summary>
	/// A tap there is a control's, not the video's: the bars and what is in them, and the Skip and Next pills over the video.
	/// Nixie 1.3.1: except a bar's own scrim where it only fades into the picture (the bottom bar's top padding, the top bar's
	/// bottom padding, while the bar's fill is a gradient): a click or double click there is the video's, as it looks. In 1.3.0
	/// it did nothing, so a double click near the bottom whose first click showed the bars under the pointer neither paused
	/// nor went full screen. A theme's solid board (Drive-in's console) stays the controls' everywhere.
	/// </summary>
	private bool IsOnControls(DependencyObject? source, Func<UIElement, Windows.Foundation.Point> position)
	{
		if (source is not null && IsWithin(source, PillRow))
		{
			return true;
		}

		if (!IsInBars(source))
		{
			return false;
		}

		if (source is Grid bar && (ReferenceEquals(bar, TopBar) || ReferenceEquals(bar, BottomBar)) && FadesOut(bar.Background))
		{
			var at = position(bar);
			return !InScrim(at.Y, bar.ActualHeight, bar.Padding, bottomBar: ReferenceEquals(bar, BottomBar));
		}

		return true;
	}

	/// <summary>A scrim: a gradient that fades out to nothing (a theme's board, such as Drive-in's console, is dense all over).</summary>
	private static bool FadesOut(Brush? fill) => fill is GradientBrush gradient && gradient.GradientStops.Any(s => s.Color.A == 0);

	/// <summary>Whether a point <paramref name="y"/> DIPs down a bar <paramref name="height"/> tall is in the scrim's fade over the video (the padding on the video's side).</summary>
	public static bool InScrim(double y, double height, Thickness padding, bool bottomBar) =>
		bottomBar ? y >= 0 && y < padding.Top : y <= height && y > height - padding.Bottom;

	private void OnBarPointerEntered(object sender, PointerRoutedEventArgs e)
	{
		if (Controls is { } controls)
		{
			controls.PointerOverControls = true;
			controls.NotifyActivity();
		}
	}

	private void OnBarPointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (Controls is { } controls)
		{
			controls.PointerOverControls = false;
		}
	}

	private void UpdateCursor()
	{
		if (Controls?.CursorHidden == true)
		{
			// a disposed cursor shows none (what FluentFin's own player does, MediaPlayerHost)
			ProtectedCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Arrow);
			ProtectedCursor.Dispose();
		}
		else
		{
			RestoreCursor();
		}
	}

	private void RestoreCursor() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);

	// -- the subtitles keep clear of the bottom bar ----------------------------------------------------------------------

	private void OnBottomBarSizeChanged(object sender, SizeChangedEventArgs e)
	{
		ReportBottomBar();
		PlaceSkipButton();
	}

	// -- the Skip button's place -----------------------------------------------------------------------------------------

	/// <summary>The Skip button's distance from the right edge (the bottom bar's own padding) and, bars hidden, from the bottom.</summary>
	private const double SkipEdgeRight = 32, SkipEdgeBottom = 24;

	/// <summary>Its gap above the bottom bar's controls while they show.</summary>
	private const double SkipGapAboveBar = 12;

	/// <summary>The height of the bottom bar's controls (without its scrim's transparent top), last measured while it showed.</summary>
	private double _barControlsHeight;

	/// <summary>
	/// The Skip button sits bottom right: just above the seek bar while the bars show (clear of them), a small margin from
	/// the bottom while they are hidden, as streaming apps place it. A collapsed bar measures nothing: its last height is kept.
	/// </summary>
	private void PlaceSkipButton()
	{
		if (BottomBar.Visibility == Visibility.Visible && BottomBar.ActualHeight > 0)
		{
			_barControlsHeight = BarTopAbove(BottomBar.ActualHeight, BottomBar.Margin, BottomBar.Padding, BottomBar.BorderThickness);
		}

		var margin = PromptMargin(Controls is { ControlsVisible: true }, _barControlsHeight, BottomBar.Margin, BottomBar.BorderThickness);
		if (PillRow.Margin != margin)
		{
			PillRow.Margin = margin;
		}
	}

	/// <summary>
	/// Nixie 1.3.1 (the UX review's DI-M4): how far above the player's foot the bottom bar's visible top is. A bar on a scrim
	/// (no border: the default, Cinematic, Editorial) starts its controls below its padding's transparent top; a framed bar
	/// (Drive-in's nixie console) is solid from its edge. Its own margin lifts it: Drive-in's console sits 28 above the foot,
	/// and the prompts were placed as if it did not (Watch credits on the seek bar's end, the Next card over the time left).
	/// </summary>
	internal static double BarTopAbove(double barHeight, Thickness barMargin, Thickness barPadding, Thickness barBorder) =>
		Math.Max(0, barMargin.Bottom + barHeight - (barBorder.Top > 0 ? 0 : barPadding.Top));

	/// <summary>
	/// The prompts' row (Skip, Next): above the bar while it shows (<paramref name="barTop"/> and a gap), a small margin from
	/// the foot while it is hidden; its right edge at the bar's own side: a framed bar's edge, else the scrim's padding.
	/// </summary>
	internal static Thickness PromptMargin(bool barsShown, double barTop, Thickness barMargin, Thickness barBorder)
	{
		var bottom = barsShown && barTop > 0 ? barTop + SkipGapAboveBar : SkipEdgeBottom;
		var right = barBorder.Right > 0 ? Math.Max(SkipEdgeRight, barMargin.Right) : barMargin.Right + SkipEdgeRight;
		return new Thickness(0, 0, right, bottom);
	}

	private void ReportBottomBar() => ReportBottomBarFor(Root.ActualHeight);

	/// <summary>
	/// The share of a video <paramref name="height"/> DIPs tall the bottom bar covers, told to the view model (the subtitles
	/// keep above it). Seaglass 1.1.3: the full-screen switch calls it with the height the video is about to have, before the
	/// presenter changes, so the subtitles move with the switch instead of after it.
	/// </summary>
	public void ReportBottomBarFor(double height)
	{
		if (Controls is { } controls && BottomBarCover(BottomBar.ActualHeight, BottomBar.Padding.Top, height) is { } fraction)
		{
			_ = controls.SetBottomBarFraction(fraction);
		}
	}

	/// <summary>
	/// The share of a video <paramref name="height"/> tall that a bottom bar <paramref name="barHeight"/> tall (with its top
	/// padding) covers: the bar's scrim starts transparent, so what covers the picture is the part with the controls in it.
	/// Null when either is not measured.
	/// </summary>
	public static double? BottomBarCover(double barHeight, double paddingTop, double height) =>
		height > 0 && barHeight > 0 ? Math.Max(0, barHeight - paddingTop / 2) / height : null;

	protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
	{
		var size = base.ArrangeOverride(finalSize);
		ReportBottomBar();
		return size;
	}

	// -- the bars fade (Seaglass) -----------------------------------------------------------------------------------------

	private void OnGlassModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ConfigureFades);

	/// <summary>
	/// The bars fade in and out as their Visibility changes (implicit animations keep them drawn until the fade ends); with
	/// motion allowed they also slide a little. Without (Reduced, Solid, animations off) it is the fade alone.
	/// </summary>
	private void ConfigureFades()
	{
		var motion = GlassMode.MotionAllowed;
		ConfigureFade(TopBar, -12, motion);
		ConfigureFade(BottomBar, 12, motion);
		ConfigureFade(ToastIndicator, -8, motion);
		ConfigureFade(NextPill, 8, motion);
		ConfigureTimecodeGlow();
		ConfigureIndicatorFade(motion);
		AnimateVolumeFill();
	}

	// -- the timecode's glow (1.2.0, Nixie) -------------------------------------------------------------------------------

	private readonly Dictionary<TextBlock, SpriteVisual> _glows = [];

	/// <summary>
	/// A soft glow in the accent behind the elapsed and remaining time, as a nixie tube's digits: a compositor drop shadow of
	/// the text's own alpha mask (it follows the text as it changes, once a second), with no offset, on a visual behind the
	/// text. Static: no animation, no work per frame. None in high contrast (the system colours alone).
	/// </summary>
	private void ConfigureTimecodeGlow()
	{
		var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
		var accent = (Resources["SliderTrackValueFill"] as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.White;
		var opacity = (float)(double)Resources["TimecodeGlowOpacity"];
		var blur = (float)(double)Resources["TimecodeGlowBlur"];
		foreach (var (text, host) in new[] { (ElapsedText, ElapsedGlow), (RemainingText, RemainingGlow) })
		{
			if (highContrast)
			{
				ElementCompositionPreview.SetElementChildVisual(host, null);
				_glows.Remove(text);
				continue;
			}

			if (!_glows.TryGetValue(text, out var sprite))
			{
				var compositor = ElementCompositionPreview.GetElementVisual(text).Compositor;
				sprite = compositor.CreateSpriteVisual();
				sprite.Shadow = compositor.CreateDropShadow();
				ElementCompositionPreview.SetElementChildVisual(host, sprite);
				_glows[text] = sprite;
				var target = sprite;
				text.SizeChanged += (_, e) => target.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
			}

			var shadow = (DropShadow)sprite.Shadow;
			shadow.Mask = text.GetAlphaMask();
			shadow.Color = accent;
			shadow.Opacity = opacity;
			shadow.BlurRadius = blur;
			shadow.Offset = Vector3.Zero;
			sprite.Size = new Vector2((float)text.ActualWidth, (float)text.ActualHeight);
		}
	}

	// -- the volume indicator (Seaglass) ----------------------------------------------------------------------------------

	/// <summary>The indicator's width when full: the fill's width is this times the volume.</summary>
	public const double VolumeFillWidth = VolumeIndicatorMotion.FillWidth;

	/// <summary>How long the fill takes to glide to a new level.</summary>
	public static readonly TimeSpan FillTime = VolumeIndicatorMotion.FillTime;

	/// <summary>
	/// Seaglass 1.1.3: the fill's shape. The fill (a full-width bar) is clipped to a rounded rectangle whose width the
	/// compositor animates. Each new level starts from where the clip is drawn now (<c>this.StartingValue</c>), so a notch made
	/// mid-glide carries on from there. In 1.1.2 a storyboard was stopped on every notch, and stopping it put the width back
	/// to its base value (an empty bar): every notch emptied the bar and filled it again (measured in
	/// <c>tools\ui-harness\offscreen\then-volume.ps1</c>: up to 87 px backwards at each notch). Off the UI thread too.
	/// </summary>
	private CompositionRoundedRectangleGeometry? _fillShape;

	/// <summary>
	/// Fades in (and, with motion allowed, grows from 94 %) and fades out over 0.4 s (shrinking back to 94 %) as its Visibility
	/// changes; with motion not allowed (Reduced, Solid, animations off) the fade alone. Each starts from where the indicator
	/// is drawn now, so a notch while it fades out brings it back from there instead of blinking it out first.
	/// </summary>
	private void ConfigureIndicatorFade(bool motion)
	{
		var visual = ElementCompositionPreview.GetElementVisual(VolumeIndicator);
		var compositor = visual.Compositor;
		visual.CenterPoint = new Vector3((float)VolumeIndicator.ActualWidth / 2, (float)VolumeIndicator.ActualHeight / 2, 0);
		if (VolumeIndicator.Visibility == Visibility.Collapsed)
		{
			// where a show starts the first time (later, where the last hide left it)
			visual.Opacity = 0f;
			visual.Scale = motion ? VolumeIndicatorMotion.HiddenScale : Vector3.One;
		}
		else if (!motion)
		{
			visual.Scale = Vector3.One;
		}

		var fadeIn = compositor.CreateScalarKeyFrameAnimation();
		fadeIn.Target = "Opacity";
		fadeIn.InsertExpressionKeyFrame(0f, "this.StartingValue");
		fadeIn.InsertKeyFrame(1f, 1f);
		fadeIn.Duration = VolumeIndicatorMotion.FadeIn(motion);
		var fadeOut = compositor.CreateScalarKeyFrameAnimation();
		fadeOut.Target = "Opacity";
		fadeOut.InsertExpressionKeyFrame(0f, "this.StartingValue");
		fadeOut.InsertKeyFrame(1f, 0f);
		fadeOut.Duration = VolumeIndicatorMotion.FadeOut(motion);
		var show = compositor.CreateAnimationGroup();
		show.Add(fadeIn);
		var hide = compositor.CreateAnimationGroup();
		hide.Add(fadeOut);
		if (motion)
		{
			var grow = compositor.CreateVector3KeyFrameAnimation();
			grow.Target = "Scale";
			grow.InsertExpressionKeyFrame(0f, "this.StartingValue");
			grow.InsertKeyFrame(1f, Vector3.One, compositor.CreateCubicBezierEasingFunction(new Vector2(0.05f, 0.9f), new Vector2(0.1f, 1f)));
			grow.Duration = VolumeIndicatorMotion.Grow;
			show.Add(grow);
			var shrink = compositor.CreateVector3KeyFrameAnimation();
			shrink.Target = "Scale";
			shrink.InsertExpressionKeyFrame(0f, "this.StartingValue");
			shrink.InsertKeyFrame(1f, VolumeIndicatorMotion.HiddenScale);
			shrink.Duration = VolumeIndicatorMotion.FadeOut(motion);
			hide.Add(shrink);
		}

		ElementCompositionPreview.SetImplicitShowAnimation(VolumeIndicator, show);
		ElementCompositionPreview.SetImplicitHideAnimation(VolumeIndicator, hide);
	}

	private void OnVolumeIndicatorSizeChanged(object sender, SizeChangedEventArgs e)
	{
		ElementCompositionPreview.GetElementVisual(VolumeIndicator).CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
	}

	/// <summary>The fill's clip, made the first time: a rounded rectangle as tall as the bar, as wide as the volume.</summary>
	private CompositionRoundedRectangleGeometry FillShape()
	{
		if (_fillShape is { } shape)
		{
			return shape;
		}

		var visual = ElementCompositionPreview.GetElementVisual(VolumeIndicatorFill);
		var compositor = visual.Compositor;
		shape = compositor.CreateRoundedRectangleGeometry();
		shape.CornerRadius = new Vector2(VolumeIndicatorMotion.FillHeight / 2);
		shape.Size = new Vector2(0, VolumeIndicatorMotion.FillHeight);
		visual.Clip = compositor.CreateGeometricClip(shape);
		return _fillShape = shape;
	}

	/// <summary>
	/// The fill goes to the volume (0 while muted): it glides from where it is drawn now, or moves at once when motion is not
	/// allowed or the indicator is not shown (<see cref="VolumeIndicatorMotion.FillGlides"/>).
	/// </summary>
	private void AnimateVolumeFill()
	{
		var shape = FillShape();
		var target = new Vector2(VolumeIndicatorMotion.FillTo(Controls?.VolumeFraction ?? 0), VolumeIndicatorMotion.FillHeight);
		if (!VolumeIndicatorMotion.FillGlides(GlassMode.MotionAllowed, Controls is { VolumeIndicatorVisible: true }))
		{
			shape.StopAnimation("Size");
			shape.Size = target;
			return;
		}

		var compositor = shape.Compositor;
		var glide = compositor.CreateVector2KeyFrameAnimation();
		glide.InsertExpressionKeyFrame(0f, "this.StartingValue");
		// CubicEase EaseOut (what 1.1.2 used): 1 - (1 - t)^3
		glide.InsertKeyFrame(1f, target, compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 1f), new Vector2(0.68f, 1f)));
		glide.Duration = FillTime;
		shape.StartAnimation("Size", glide);
	}

	/// <summary>Narrator says the new volume ("Volume 45 %" or "Muted"), without moving the focus.</summary>
	private void AnnounceVolume(string text) => Announce(text, "NixieVolume");

	/// <summary>Narrator says <paramref name="text"/> without moving the focus; a newer one of the same <paramref name="id"/> replaces it.</summary>
	private void Announce(string text, string id)
	{
		if (FrameworkElementAutomationPeer.FromElement(this) is { } peer)
		{
			peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.ImportantMostRecent, text, id);
		}
	}

	private static void ConfigureFade(FrameworkElement bar, float slide, bool motion)
	{
		var compositor = ElementCompositionPreview.GetElementVisual(bar).Compositor;
		ElementCompositionPreview.SetIsTranslationEnabled(bar, true);

		var fadeIn = compositor.CreateScalarKeyFrameAnimation();
		fadeIn.Target = "Opacity";
		fadeIn.InsertKeyFrame(0f, 0f);
		fadeIn.InsertKeyFrame(1f, 1f);
		fadeIn.Duration = TimeSpan.FromMilliseconds(motion ? 220 : 150);

		var fadeOut = compositor.CreateScalarKeyFrameAnimation();
		fadeOut.Target = "Opacity";
		fadeOut.InsertKeyFrame(1f, 0f);
		fadeOut.Duration = TimeSpan.FromMilliseconds(motion ? 300 : 150);

		var show = compositor.CreateAnimationGroup();
		show.Add(fadeIn);
		var hide = compositor.CreateAnimationGroup();
		hide.Add(fadeOut);

		if (motion)
		{
			var slideIn = compositor.CreateScalarKeyFrameAnimation();
			slideIn.Target = "Translation.Y";
			slideIn.InsertKeyFrame(0f, slide);
			slideIn.InsertKeyFrame(1f, 0f);
			slideIn.Duration = TimeSpan.FromMilliseconds(220);
			show.Add(slideIn);

			var slideOut = compositor.CreateScalarKeyFrameAnimation();
			slideOut.Target = "Translation.Y";
			slideOut.InsertKeyFrame(1f, slide);
			slideOut.Duration = TimeSpan.FromMilliseconds(300);
			hide.Add(slideOut);
		}

		ElementCompositionPreview.SetImplicitShowAnimation(bar, show);
		ElementCompositionPreview.SetImplicitHideAnimation(bar, hide);
	}

	// -- the seek bar ----------------------------------------------------------------------------------------------------

	/// <summary>The time under the pointer, in a small label above the bar.</summary>
	private void OnSeekBarPointerMoved(object sender, PointerRoutedEventArgs e)
	{
		if (Controls is not { DurationTicks: > 0 } controls || SeekBar.ActualWidth <= 0)
		{
			return;
		}

		var x = Math.Clamp(e.GetCurrentPoint(SeekBar).Position.X, 0, SeekBar.ActualWidth);
		var ticks = (long)(controls.DurationTicks * (x / SeekBar.ActualWidth));
		HoverTimeText.Text = HoverTimeFor(ticks);
		ShowPreview(controls, ticks);
		HoverTime.Visibility = Visibility.Visible;
		HoverTime.UpdateLayout();
		// 1.2.0: in the player's coordinates (the label grew a thumbnail): centred over the pointer, above the bar, in the window
		var origin = SeekBar.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
		var width = HoverTime.ActualWidth;
		var left = Math.Clamp(origin.X + x - width / 2, 8, Math.Max(8, Root.ActualWidth - width - 8));
		HoverTime.Margin = new Thickness(left, Math.Max(0, origin.Y - HoverTime.ActualHeight - 2), 0, 0);
	}

	/// <summary>1.2.0: a trickplay thumbnail's width over the seek bar, in DIPs (Netflix's and jellyfin-web's are about this size).</summary>
	public const double PreviewWidth = 240;

	/// <summary>The sheets of the item's thumbnails already asked for, by address (a 22-minute episode has two); dropped with the item.</summary>
	private readonly Dictionary<Uri, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _previewSheets = [];
	private TrickplayTiles? _previewTiles;

	/// <summary>
	/// The thumbnail for <paramref name="ticks"/> over the time, when the item has trickplay tiles: its sheet (decoded at the
	/// size shown, once per sheet), moved and clipped to the one tile. None: the time alone, as before.
	/// </summary>
	private void ShowPreview(MpvControlsViewModel controls, long ticks)
	{
		if (controls.PreviewAt(ticks) is not { } preview)
		{
			HoverPreview.Visibility = Visibility.Collapsed;
			return;
		}

		var tiles = preview.Tiles;
		if (!ReferenceEquals(_previewTiles, tiles))
		{
			_previewSheets.Clear();
			_previewTiles = tiles;
		}

		var scale = PreviewWidth / tiles.Width;
		if (!_previewSheets.TryGetValue(preview.Sheet, out var sheet))
		{
			sheet = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage
			{
				DecodePixelType = Microsoft.UI.Xaml.Media.Imaging.DecodePixelType.Logical,
				DecodePixelWidth = (int)Math.Round(tiles.SheetWidth * scale),
				UriSource = preview.Sheet,
			};
			_previewSheets[preview.Sheet] = sheet;
		}

		var height = Math.Round(tiles.Height * scale);
		HoverPreviewImage.Source = sheet;
		HoverPreviewImage.Width = tiles.SheetWidth * scale;
		HoverPreview.Width = PreviewWidth;
		HoverPreview.Height = height;
		HoverPreviewClip.Rect = new Windows.Foundation.Rect(0, 0, PreviewWidth, height);
		HoverPreviewShift.X = -preview.X * scale;
		HoverPreviewShift.Y = -preview.Y * scale;
		HoverPreview.Visibility = Visibility.Visible;
	}

	private void OnSeekBarPointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (!_seekBarPressed)
		{
			HoverTime.Visibility = Visibility.Collapsed;
		}
	}

	public static string HoverTimeFor(long ticks) => MpvControlsViewModel.FormatTime(TimeSpan.FromTicks(Math.Max(0, ticks)));

	private void OnSeekBarPointerPressed(object sender, PointerRoutedEventArgs e) => SeekBarPressed();

	private void OnSeekBarPointerReleased(object sender, PointerRoutedEventArgs e) => SeekBarReleased();

	/// <summary>The seek bar is pressed (a click or the start of a drag): the position follows the thumb until it is let go.</summary>
	internal void SeekBarPressed()
	{
		_seekBarPressed = true;
		Controls?.BeginScrub();
	}

	/// <summary>The seek bar is let go: a seek to where the thumb is.</summary>
	internal void SeekBarReleased()
	{
		if (!_seekBarPressed)
		{
			return;
		}

		_seekBarPressed = false;
		_ = Controls?.SeekTo(TimeSpan.FromSeconds(SeekBar.Value).Ticks);
	}

	/// <summary>The seek bar's slider (the harness's drag moves its value as a drag does).</summary>
	internal Slider SeekSlider => SeekBar;

	private void OnSeekBarValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		if (_seekBarPressed)
		{
			Controls?.Scrub(TimeSpan.FromSeconds(e.NewValue).Ticks);
		}
	}

	// -- buttons ---------------------------------------------------------------------------------------------------------

	private void OnBackClick(object sender, RoutedEventArgs e) => Controls?.GoBack?.Invoke();

	private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = Controls?.TogglePause();


	private void OnSkipBackClick(object sender, RoutedEventArgs e) => _ = Controls?.SeekBy(-MpvControlsViewModel.SkipBackSeconds);

	// 1.2.0 (button review P8): +10 s, as L (it was +30 s)
	private void OnSkipForwardClick(object sender, RoutedEventArgs e) => _ = Controls?.SeekBy(MpvControlsViewModel.ForwardButtonSeconds);

	private void OnNextClick(object sender, RoutedEventArgs e) => _ = Controls?.Next();

	private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = Controls?.Previous();

	private void OnCancelNextClick(object sender, RoutedEventArgs e) => _ = Controls?.CancelNextCountdown();

	private void OnSkipSegmentClick(object sender, RoutedEventArgs e) => _ = Controls?.SkipSegment();

	private void OnFullScreenClick(object sender, RoutedEventArgs e) => Controls?.ToggleFullScreen();

	private void OnMuteClick(object sender, RoutedEventArgs e) => _ = Controls?.ToggleMute();

	private void OnVolumeSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		if (Controls is { } controls && (int)Math.Round(e.NewValue) != controls.Volume)
		{
			_ = controls.SetVolume((int)Math.Round(e.NewValue));
		}
	}

	private void OnMenuOpened(object? sender, object e) => Controls?.MenuOpened(true);

	private void OnMenuClosed(object? sender, object e) => Controls?.MenuOpened(false);

	private IReadOnlyList<MpvTrackChoice> _audioChoices = [];
	private IReadOnlyList<MpvTrackChoice> _subtitleChoices = [];
	private bool _fillingSubtitles;

	/// <summary>
	/// 1.2.0: the one "Audio and subtitles" panel as it opens: the audio tracks and the subtitle tracks (those playing
	/// selected), and the remembered text size.
	/// </summary>
	private void OnTracksMenuOpening(object? sender, object e)
	{
		_fillingSubtitles = true;
		try
		{
			_audioChoices = Controls?.AudioChoices ?? [];
			Fill(AudioTracks, _audioChoices);
			_subtitleChoices = Controls?.SubtitleChoices ?? [];
			Fill(SubtitleTracks, _subtitleChoices);
			SubtitleSizeSlider.Value = Controls?.SubtitleSize ?? SubtitleSizes.Default;
		}
		finally
		{
			_fillingSubtitles = false;
		}
	}

	/// <summary>
	/// 1.2.0 (seen at 1100 px): the selections set while the panel opened did not show (its radio buttons are made only
	/// once it is open), so neither list said what plays; they are set again now, still without choosing anything.
	/// </summary>
	private void OnTracksMenuOpened(object? sender, object e)
	{
		OnMenuOpened(sender, e);
		_fillingSubtitles = true;
		try
		{
			Reselect(AudioTracks, _audioChoices);
			Reselect(SubtitleTracks, _subtitleChoices);
		}
		finally
		{
			_fillingSubtitles = false;
		}
	}

	private static void Reselect(RadioButtons list, IReadOnlyList<MpvTrackChoice> choices)
	{
		var at = choices.ToList().FindIndex(c => c.IsSelected);
		list.SelectedIndex = -1;
		list.SelectedIndex = at;
	}

	private static void Fill(RadioButtons list, IReadOnlyList<MpvTrackChoice> choices)
	{
		list.Items.Clear();
		foreach (var choice in choices)
		{
			list.Items.Add(choice.Label);
		}

		list.SelectedIndex = choices.ToList().FindIndex(c => c.IsSelected);
	}

	private void OnAudioTrackChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_fillingSubtitles && AudioTracks.SelectedIndex is >= 0 and var at && at < _audioChoices.Count)
		{
			_ = Controls?.SetAudio(_audioChoices[at].Index);
		}
	}

	private void OnSubtitleTrackChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_fillingSubtitles && SubtitleTracks.SelectedIndex is >= 0 and var at && at < _subtitleChoices.Count)
		{
			_ = Controls?.SetSubtitle(_subtitleChoices[at].Index);
		}
	}

	/// <summary>The Text size slider: live while dragging (the view model snaps to 10 % and sends each new value).</summary>
	private void OnSubtitleSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		if (!_fillingSubtitles && Controls is { } controls)
		{
			_ = controls.SetSubtitleSize((int)Math.Round(e.NewValue));
		}
	}

	private void OnSubtitleSizeReset(object sender, RoutedEventArgs e) => SubtitleSizeSlider.Value = SubtitleSizes.Default;

	// -- 1.2.0: the subtitle delay (the subtitles flyout) and the speed (its menu) --------------------------------

	private void OnSubtitleEarlierClick(object sender, RoutedEventArgs e) => StepDelay(-1);

	private void OnSubtitleLaterClick(object sender, RoutedEventArgs e) => StepDelay(1);

	private void OnSubtitleDelayReset(object sender, RoutedEventArgs e)
	{
		if (Controls is { } controls)
		{
			_ = controls.SetSubtitleDelay(0);
			Announce(DelayName(controls.SubtitleDelay), "SubtitleDelay");
		}
	}

	/// <summary>A delay button: the new delay, said (the flyout keeps the focus on the button).</summary>
	private void StepDelay(int steps)
	{
		if (Controls is { } controls)
		{
			_ = controls.StepSubtitleDelay(steps);
			Announce(DelayName(controls.SubtitleDelay), "SubtitleDelay");
		}
	}

	/// <summary>The speed menu as it opens: one radio item per speed, the one playing checked.</summary>
	private void OnSpeedMenuOpening(object? sender, object e)
	{
		SpeedMenu.Items.Clear();
		var current = Controls?.Speed ?? PlaybackSpeeds.Normal;
		foreach (var speed in PlaybackSpeeds.Steps)
		{
			var item = new RadioMenuFlyoutItem { Text = PlaybackSpeeds.Label(speed), IsChecked = speed == current, GroupName = "speeds" };
			item.Click += (_, _) => _ = Controls?.SetSpeed(speed);
			SpeedMenu.Items.Add(item);
		}
	}

	// -- x:Bind helpers --------------------------------------------------------------------------------------------------

	public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	public Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>The Next pill's tooltip: what plays next, and its key.</summary>
	public string NextHint(string next) => $"{next} (Shift+N)";

	/// <summary>A button's tooltip with its key, the key only while it does something (button review P9).</summary>
	public string KeyHint(string text, string key, bool enabled) => enabled ? $"{text} ({key})" : text;

	/// <summary>The volume button's name (button review P3): the level is the point of it.</summary>
	public string VolumeName(int volume, bool muted) =>
		muted ? "Volume, muted" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Volume, {volume} %");

	/// <summary>The tracks button's tooltip (button review P12): why it does nothing when it is disabled.</summary>
	public string TracksToolTip(bool hasChoice) => hasChoice ? "Audio and subtitles" : "No other audio or subtitles";

	public bool NotZero(double value) => value != 0;

	/// <summary>The speed button's name: "Playback speed", and the speed when it is not normal.</summary>
	public string SpeedName(double speed) => PlaybackSpeeds.IsNormal(speed) ? "Playback speed" : $"Playback speed, {PlaybackSpeeds.Text(speed)}";

	public string SpeedToolTip(double speed) => $"{SpeedName(speed)} ([ and ])";

	/// <summary>The subtitle delay as Narrator says it: "Subtitle delay: 0.3 s later", "Subtitle delay: none".</summary>
	public string DelayName(double seconds) => $"Subtitle delay: {SubtitleDelays.Describe(seconds)}";

	public Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

	public double VolumeValue(int volume) => volume;

	public string VolumeNumber(int volume) => volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// Ticks as the seek bar's seconds. While the bar is held the view model shows the thumb's own value (it stops following
	/// the backend), so the binding only echoes it and a value change then is always the user's.
	/// </summary>
	public double Seconds(long ticks) => Math.Max(0, TimeSpan.FromTicks(ticks).TotalSeconds);

	public string SkipToolTip(string text, string? source = null) => source is null ? $"{text} (S)" : $"{text} (S), {source}";

	/// <summary>A tooltip only while <paramref name="shown"/> (the Next button's own while enabled, its wrapper's while disabled).</summary>
	public string? ToolTipIf(bool shown, string text) => shown ? text : null;

	public string? ToolTipUnless(bool hidden, string text) => hidden ? null : text;

	public string PlayPauseGlyph(bool paused) => paused ? "\uE768" : "\uE769";

	public string PlayPauseName(bool paused) => paused ? "Play" : "Pause";

	// 1.2.0 (the interaction audit, item 14): every button's tooltip names its key
	public string PlayPauseToolTip(bool paused) => $"{PlayPauseName(paused)} (Space)";

	// button review P19: both keys
	public string FullScreenToolTip(bool fullScreen) => $"{FullScreenName(fullScreen)} (F, F11)";

	public string MuteToolTip(bool muted) => $"{MuteName(muted)} (M)";

	public string FullScreenGlyph(bool fullScreen) => fullScreen ? "\uE73F" : "\uE740";

	public string FullScreenName(bool fullScreen) => fullScreen ? "Exit full screen" : "Full screen";

	public string MuteName(bool muted) => muted ? "Unmute" : "Mute";

	public string VolumeGlyph(int volume, bool muted) => muted || volume == 0 ? "\uE74F" : volume < 34 ? "\uE993" : volume < 67 ? "\uE994" : "\uE995";
}
