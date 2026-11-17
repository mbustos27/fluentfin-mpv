using FluentFin.Core.Mpv;
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
			self.DropWaitingClick(old);
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
			// Seaglass 1.1.4: a key's new speed or subtitle delay
			Announce(toast.ToastText, "SeaglassPlayerStatus");
		}
		else if (e.PropertyName is nameof(MpvControlsViewModel.IsNextPromptVisible) && Controls is { IsNextPromptVisible: true } prompt)
		{
			// Seaglass 1.1.4: the Next pill came (near the end); it is not focused, Shift+N plays it
			Announce($"{prompt.NextToolTip}. Shift+N plays it.", "SeaglassNextPrompt");
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
			|| e.PropertyName is nameof(MpvControlsViewModel.IsNextPromptVisible) && Controls is { IsNextPromptVisible: false })
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
				|| e.PropertyName == nameof(MpvControlsViewModel.IsNextPromptVisible) && Controls is { IsNextPromptVisible: true } && IsWithin(focused, NextPill)))
		{
			// Seaglass 1.1.4: the Next pill likewise (pressed: the next item starts, and it is not near its end)
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

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern uint GetDoubleClickTime();

	/// <summary>
	/// Seaglass 1.1.3: a click's play/pause waits the double-click time (<see cref="MpvControlsViewModel.VideoClicked"/>);
	/// this timer ends the wait. Stopped when the controls go or change (a click must not reach a backend after its stop).
	/// </summary>
	private DispatcherQueueTimer? _clickTimer;

	private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
	{
		if (Controls is not { } controls || IsOnControls(e.OriginalSource as DependencyObject))
		{
			return;
		}

		e.Handled = true;
		controls.VideoClicked();
		_clickTimer ??= DispatcherQueue.CreateTimer();
		_clickTimer.Interval = controls.DoubleClickTime;
		_clickTimer.IsRepeating = false;
		_clickTimer.Tick -= OnClickWaitOver;
		_clickTimer.Tick += OnClickWaitOver;
		_clickTimer.Start();
	}

	private void OnClickWaitOver(DispatcherQueueTimer sender, object args) => _ = Controls?.ClickWaitOver();

	private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (Controls is not { } controls || IsOnControls(e.OriginalSource as DependencyObject))
		{
			return;
		}

		e.Handled = true;
		_clickTimer?.Stop();
		controls.VideoDoubleClicked();
	}

	/// <summary>A click still waiting does nothing (the controls go or change).</summary>
	private void DropWaitingClick(MpvControlsViewModel? controls)
	{
		_clickTimer?.Stop();
		controls?.CancelClick();
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

	/// <summary>A tap there is a control's, not the video's (the bars, and the Skip and Next pills over the video).</summary>
	private bool IsOnControls(DependencyObject? source) => IsInBars(source) || (source is not null && IsWithin(source, PillRow));

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
			_barControlsHeight = Math.Max(0, BottomBar.ActualHeight - BottomBar.Padding.Top);
		}

		var bottom = Controls is { ControlsVisible: true } && _barControlsHeight > 0 ? _barControlsHeight + SkipGapAboveBar : SkipEdgeBottom;
		var margin = new Thickness(0, 0, SkipEdgeRight, bottom);
		if (PillRow.Margin != margin)
		{
			PillRow.Margin = margin;
		}
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
		ConfigureIndicatorFade(motion);
		AnimateVolumeFill();
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
	private void AnnounceVolume(string text) => Announce(text, "SeaglassVolume");

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
		HoverTime.Visibility = Visibility.Visible;
		HoverTime.UpdateLayout();
		HoverTime.Margin = new Thickness(Math.Clamp(x - HoverTime.ActualWidth / 2, 0, Math.Max(0, SeekBar.ActualWidth - HoverTime.ActualWidth)), 0, 0, 34);
	}

	private void OnSeekBarPointerExited(object sender, PointerRoutedEventArgs e)
	{
		if (!_seekBarPressed)
		{
			HoverTime.Visibility = Visibility.Collapsed;
		}
	}

	public static string HoverTimeFor(long ticks) => MpvControlsViewModel.FormatTime(TimeSpan.FromTicks(Math.Max(0, ticks)));

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

	private void OnShortcutsClick(object sender, RoutedEventArgs e) => FluentFin.Services.WindowShortcuts.ShowHelp(XamlRoot);

	private void OnSkipBackClick(object sender, RoutedEventArgs e) => _ = Controls?.SeekBy(-MpvControlsViewModel.SkipBackSeconds);

	private void OnSkipForwardClick(object sender, RoutedEventArgs e) => _ = Controls?.SeekBy(MpvControlsViewModel.SkipForwardSeconds);

	private void OnNextClick(object sender, RoutedEventArgs e) => _ = Controls?.Next();

	private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = Controls?.Previous();

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

	private IReadOnlyList<MpvTrackChoice> _subtitleChoices = [];
	private bool _fillingSubtitles;

	/// <summary>The subtitles flyout as it opens: the tracks (the one playing selected) and the remembered text size.</summary>
	private void OnSubtitlesMenuOpening(object? sender, object e)
	{
		_fillingSubtitles = true;
		try
		{
			_subtitleChoices = Controls?.SubtitleChoices ?? [];
			SubtitleTracks.Items.Clear();
			foreach (var choice in _subtitleChoices)
			{
				SubtitleTracks.Items.Add(choice.Label);
			}

			SubtitleTracks.SelectedIndex = _subtitleChoices.ToList().FindIndex(c => c.IsSelected);
			SubtitleSizeSlider.Value = Controls?.SubtitleSize ?? SubtitleSizes.Default;
		}
		finally
		{
			_fillingSubtitles = false;
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

	// -- Seaglass 1.1.4: the subtitle delay (the subtitles flyout) and the speed (its menu) --------------------------------

	private void OnSubtitleEarlierClick(object sender, RoutedEventArgs e) => StepDelay(-1);

	private void OnSubtitleLaterClick(object sender, RoutedEventArgs e) => StepDelay(1);

	private void OnSubtitleDelayReset(object sender, RoutedEventArgs e)
	{
		if (Controls is { } controls)
		{
			_ = controls.SetSubtitleDelay(0);
			Announce(DelayName(controls.SubtitleDelay), "SeaglassSubtitleDelay");
		}
	}

	/// <summary>A delay button: the new delay, said (the flyout keeps the focus on the button).</summary>
	private void StepDelay(int steps)
	{
		if (Controls is { } controls)
		{
			_ = controls.StepSubtitleDelay(steps);
			Announce(DelayName(controls.SubtitleDelay), "SeaglassSubtitleDelay");
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

	public Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>The Next button's and pill's tooltip: what plays next, and its key.</summary>
	public string NextHint(string next) => $"{next} (Shift+N)";

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

	public string FullScreenGlyph(bool fullScreen) => fullScreen ? "\uE73F" : "\uE740";

	public string FullScreenName(bool fullScreen) => fullScreen ? "Exit full screen" : "Full screen";

	public string MuteName(bool muted) => muted ? "Unmute" : "Mute";

	public string VolumeGlyph(int volume, bool muted) => muted || volume == 0 ? "\uE74F" : volume < 34 ? "\uE993" : volume < 67 ? "\uE994" : "\uE995";
}
