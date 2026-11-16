using FluentFin.Core.Mpv;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

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
		Loaded += (_, _) => StartTick();
		Unloaded += (_, _) => StopTick();

		// the slider marks the pointer handled itself: listen anyway, for the start and the end of a drag or a click
		SeekBar.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekBarPointerPressed), handledEventsToo: true);
		SeekBar.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekBarPointerReleased), handledEventsToo: true);
		SeekBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekBarPointerReleased), handledEventsToo: true);
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
		}

		if (e.NewValue is MpvControlsViewModel controls)
		{
			controls.PropertyChanged += self.OnControlsPropertyChanged;
			controls.PropertyChanging += self.OnControlsPropertyChanging;
			self.UpdateCursor();
			self.ReportBottomBar();
			self.PlaceSkipButton();
		}
	}

	private void OnControlsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
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
		else if (e.PropertyName == nameof(MpvControlsViewModel.IsSkipVisible) && Controls is { IsSkipVisible: false })
		{
			// the Skip button went (pressed, or the segment is over): a focus it left nowhere
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
		else if (e.PropertyName == nameof(MpvControlsViewModel.IsSkipVisible) && Controls is { IsSkipVisible: true }
			&& XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsWithin(focused, SkipSegmentButton))
		{
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

	private void OnTick(DispatcherQueueTimer sender, object args) => Controls?.Tick();

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

	// -- pointer over the video ------------------------------------------------------------------------------------------

	private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => Controls?.NotifyActivity();

	private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
	{
		if (Controls is not { } controls || IsOnControls(e.OriginalSource as DependencyObject))
		{
			return;
		}

		e.Handled = true;
		_ = controls.TogglePause();
	}

	private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (Controls is not { } controls || IsOnControls(e.OriginalSource as DependencyObject))
		{
			return;
		}

		e.Handled = true;
		// the first click of the two paused: undo it, a double click is only full screen
		_ = controls.TogglePause();
		controls.ToggleFullScreen();
	}

	/// <summary>The wheel is volume over the whole player (never a seek, never a scroll); the volume slider's own drag is not.</summary>
	private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		if (Controls is not { } controls)
		{
			return;
		}

		e.Handled = true;
		_ = controls.Wheel(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
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

	/// <summary>A tap there is a control's, not the video's (the bars, and the Skip button over the video).</summary>
	private bool IsOnControls(DependencyObject? source) => IsInBars(source) || (source is not null && IsWithin(source, SkipSegmentButton));

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
	private const double SkipEdgeRight = 20, SkipEdgeBottom = 24;

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
			_barControlsHeight = Math.Max(0, BottomBar.ActualHeight - BottomBar.Padding.Top);
		}

		var bottom = Controls is { ControlsVisible: true } && _barControlsHeight > 0 ? _barControlsHeight + SkipGapAboveBar : SkipEdgeBottom;
		var margin = new Thickness(0, 0, SkipEdgeRight, bottom);
		if (SkipSegmentButton.Margin != margin)
		{
			SkipSegmentButton.Margin = margin;
		}
	}

	private void ReportBottomBar()
	{
		if (Controls is { } controls && Root.ActualHeight > 0 && BottomBar.ActualHeight > 0)
		{
			// the bar's scrim starts transparent: what covers the picture is the part with the controls in it
			var covered = Math.Max(0, BottomBar.ActualHeight - BottomBar.Padding.Top / 2);
			_ = controls.SetBottomBarFraction(covered / Root.ActualHeight);
		}
	}

	protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
	{
		var size = base.ArrangeOverride(finalSize);
		ReportBottomBar();
		return size;
	}

	// -- the seek bar ----------------------------------------------------------------------------------------------------

	private void OnSeekBarPointerPressed(object sender, PointerRoutedEventArgs e)
	{
		_seekBarPressed = true;
		Controls?.BeginScrub();
	}

	private void OnSeekBarPointerReleased(object sender, PointerRoutedEventArgs e)
	{
		if (!_seekBarPressed)
		{
			return;
		}

		_seekBarPressed = false;
		_ = Controls?.SeekTo(TimeSpan.FromSeconds(SeekBar.Value).Ticks);
	}

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

	private void OnSkipForwardClick(object sender, RoutedEventArgs e) => _ = Controls?.SeekBy(MpvControlsViewModel.SkipForwardSeconds);

	private void OnNextClick(object sender, RoutedEventArgs e) => _ = Controls?.Next();

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

	private void OnAudioMenuOpening(object? sender, object e) => FillMenu(AudioMenu, Controls?.AudioChoices ?? [], index => Controls?.SetAudio(index));

	private void OnSubtitlesMenuOpening(object? sender, object e) => FillMenu(SubtitlesMenu, Controls?.SubtitleChoices ?? [], index => Controls?.SetSubtitle(index));

	/// <summary>The menu's items, rebuilt as it opens: one radio item per track (the one playing checked).</summary>
	private static void FillMenu(MenuFlyout menu, IReadOnlyList<MpvTrackChoice> choices, Func<int, Task?> pick)
	{
		menu.Items.Clear();
		foreach (var choice in choices)
		{
			var item = new RadioMenuFlyoutItem { Text = choice.Label, IsChecked = choice.IsSelected, GroupName = "tracks" };
			var index = choice.Index;
			item.Click += (_, _) => _ = pick(index);
			menu.Items.Add(item);
		}
	}

	// -- x:Bind helpers --------------------------------------------------------------------------------------------------

	public Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

	public Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

	public double VolumeValue(int volume) => volume;

	public string VolumeNumber(int volume) => volume.ToString(System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// Ticks as the seek bar's seconds. While the bar is held the view model shows the thumb's own value (it stops following
	/// the backend), so the binding only echoes it and a value change then is always the user's.
	/// </summary>
	public double Seconds(long ticks) => Math.Max(0, TimeSpan.FromTicks(ticks).TotalSeconds);

	public string SkipToolTip(string text) => $"{text} (S)";

	/// <summary>A tooltip only while <paramref name="shown"/> (the Next button's own while enabled, its wrapper's while disabled).</summary>
	public string? ToolTipIf(bool shown, string text) => shown ? text : null;

	public string? ToolTipUnless(bool hidden, string text) => hidden ? null : text;

	public string PlayPauseGlyph(bool paused) => paused ? "\uE768" : "\uE769";

	public string PlayPauseName(bool paused) => paused ? "Play" : "Pause";

	public string FullScreenGlyph(bool fullScreen) => fullScreen ? "\uE73F" : "\uE740";

	public string FullScreenName(bool fullScreen) => fullScreen ? "Exit full screen" : "Full screen";

	public string MuteName(bool muted) => muted ? "Unmute" : "Mute";

	public string VolumeGlyph(int volume, bool muted) => muted || volume == 0 ? "\uE74F" : volume < 34 ? "\uE993" : volume < 67 ? "\uE994" : "\uE995";
}
