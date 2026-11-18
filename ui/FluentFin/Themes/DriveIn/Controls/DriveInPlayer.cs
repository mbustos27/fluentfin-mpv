using System.ComponentModel;
using FluentFin.Controls;
using FluentFin.Core.Mpv;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the player overlay's nixie console, set by the theme's PlayerBottomBarStyle and PlayerTopBarStyle (DriveIn.xaml)
/// on the shared player's bars, during playback too (the foundation's ThemeStyle re-styles them in place; switching away
/// takes the setters, and these, off again). The bars keep every part and all their logic (MpvPlayerControls: the seek bar,
/// the transport, the tracks and the volume code are untouched); the theme only dresses named parts:
/// <list type="bullet">
/// <item><see cref="ConsoleProperty"/> on the bottom bar: elapsed and remaining in nixie tubes over the shared timecodes (which
/// stay, unseen, for Narrator), the volume in small tubes beside its button, and the seek bar lit as a neon tube;</item>
/// <item><see cref="ClockProperty"/> on the top bar: when the film ends, in tubes ("10:42 ENDS AT · PM").</item>
/// </list>
/// A part that is not found is left as it is (<see cref="Parts"/>; DriveInThemeTests fails if they are renamed).
/// </summary>
public static class DriveInPlayer
{
	/// <summary>The shared player's parts this dresses (MpvPlayerControls.xaml).</summary>
	public static readonly string[] Parts = ["ElapsedText", "RemainingText", "ElapsedGlow", "RemainingGlow", "SeekBar", "VolumeButton", "TopBar", "BottomBar"];

	public static readonly DependencyProperty ConsoleProperty =
		DependencyProperty.RegisterAttached("Console", typeof(bool), typeof(DriveInPlayer), new PropertyMetadata(false, (d, e) => Toggle(d, (bool)e.NewValue, console: true)));

	public static readonly DependencyProperty ClockProperty =
		DependencyProperty.RegisterAttached("Clock", typeof(bool), typeof(DriveInPlayer), new PropertyMetadata(false, (d, e) => Toggle(d, (bool)e.NewValue, console: false)));

	public static bool GetConsole(DependencyObject d) => (bool)d.GetValue(ConsoleProperty);

	public static void SetConsole(DependencyObject d, bool value) => d.SetValue(ConsoleProperty, value);

	public static bool GetClock(DependencyObject d) => (bool)d.GetValue(ClockProperty);

	public static void SetClock(DependencyObject d, bool value) => d.SetValue(ClockProperty, value);

	private static readonly DependencyProperty DressProperty =
		DependencyProperty.RegisterAttached("PlayerDress", typeof(Dress), typeof(DriveInPlayer), new PropertyMetadata(null));

	/// <summary>What the theme added to a bar and what it changed, to take off again.</summary>
	private sealed class Dress
	{
		public readonly List<(Panel Parent, UIElement Child)> Added = [];
		public readonly List<Action> Restore = [];
		public MpvControlsViewModel? Controls;
		public PropertyChangedEventHandler? Handler;
		public NixieReadout? Elapsed, Remaining, Volume, Clock;
		public NeonUnderline? Tube;
		public Slider? Seek;
		public TextBlock? ClockCaption;
		public bool Console;
	}

	private static void Toggle(DependencyObject d, bool on, bool console)
	{
		if (d is not FrameworkElement bar)
		{
			return;
		}

		if (!on)
		{
			Undress(bar);
			return;
		}

		if (bar.IsLoaded)
		{
			DressBar(bar, console);
		}
		else
		{
			void Loaded(object sender, RoutedEventArgs e)
			{
				bar.Loaded -= Loaded;
				if (console ? GetConsole(bar) : GetClock(bar))
				{
					DressBar(bar, console);
				}
			}

			bar.Loaded += Loaded;
		}
	}

	private static void DressBar(FrameworkElement bar, bool console, int tries = 0)
	{
		if (bar.GetValue(DressProperty) is Dress || Owner(bar) is not { } owner)
		{
			return;
		}

		// the player's view model can come a moment after its bars are loaded
		if (owner.Controls is not { } controls)
		{
			if (tries < 40)
			{
				var timer = bar.DispatcherQueue.CreateTimer();
				timer.Interval = TimeSpan.FromMilliseconds(250);
				timer.IsRepeating = false;
				timer.Tick += (_, _) =>
				{
					if (console ? GetConsole(bar) : GetClock(bar))
					{
						DressBar(bar, console, tries + 1);
					}
				};
				timer.Start();
			}

			return;
		}

		var dress = new Dress { Controls = controls, Console = console };
		bar.SetValue(DressProperty, dress);
		if (console)
		{
			DressConsole(bar, dress);
		}
		else
		{
			DressClock(bar, dress);
		}

		dress.Handler = (_, e) =>
		{
			if (e.PropertyName is nameof(MpvControlsViewModel.PositionTicks) or nameof(MpvControlsViewModel.DurationTicks)
				or nameof(MpvControlsViewModel.Volume) or nameof(MpvControlsViewModel.IsMuted) or nameof(MpvControlsViewModel.Speed))
			{
				bar.DispatcherQueue.TryEnqueue(() => Update(dress));
			}
		};
		controls.PropertyChanged += dress.Handler;
		Update(dress);
	}

	private static void DressConsole(FrameworkElement bar, Dress dress)
	{
		// the console's board: its fill and rim from the theme's brushes (set here, so they follow the theme's dictionary)
		if (bar is Grid board)
		{
			SetForNow(dress, board, Panel.BackgroundProperty, Application.Current.Resources["DriveInConsoleBrush"]);
			SetForNow(dress, board, Grid.BorderBrushProperty, Application.Current.Resources["DriveInBoardRimBrush"]);
		}

		if (DriveInRows.Find<TextBlock>(bar, "ElapsedText") is { Parent: Panel elapsedCell } elapsed)
		{
			dress.Elapsed = new NixieReadout { DigitHeight = 34 };
			Add(dress, elapsedCell, Counter(dress.Elapsed, "ELAPSED", HorizontalAlignment.Left));
			Hide(dress, elapsed);
			if (DriveInRows.Find<Canvas>(bar, "ElapsedGlow") is { } glow)
			{
				Hide(dress, glow);
			}
		}

		if (DriveInRows.Find<TextBlock>(bar, "RemainingText") is { Parent: Panel remainingCell } remaining)
		{
			dress.Remaining = new NixieReadout { DigitHeight = 34 };
			Add(dress, remainingCell, Counter(dress.Remaining, "REMAINING", HorizontalAlignment.Right));
			Hide(dress, remaining);
			if (DriveInRows.Find<Canvas>(bar, "RemainingGlow") is { } glow)
			{
				Hide(dress, glow);
			}
		}

		if (DriveInRows.Find<Button>(bar, "VolumeButton") is { Parent: StackPanel row } volumeButton)
		{
			dress.Volume = new NixieReadout { DigitHeight = 22 };
			var volume = Counter(dress.Volume, "VOL", HorizontalAlignment.Left);
			volume.VerticalAlignment = VerticalAlignment.Center;
			row.Children.Insert(row.Children.IndexOf(volumeButton) + 1, volume);
			dress.Added.Add((row, volume));
		}

		// the seek bar lit as a neon tube: the stroke stack of the top bar's tab underline, under the bar's own fill, as long
		// as the part played (no blur effect, nothing per frame but its width)
		if (DriveInRows.Find<Slider>(bar, "SeekBar") is { Parent: Grid seekCell } seek && !BulbFrame.HighContrast())
		{
			dress.Seek = seek;
			dress.Tube = new NeonUnderline { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
			Grid.SetColumn(dress.Tube, Grid.GetColumn(seek));
			Grid.SetRow(dress.Tube, Grid.GetRow(seek));
			seekCell.Children.Insert(seekCell.Children.IndexOf(seek), dress.Tube);
			dress.Added.Add((seekCell, dress.Tube));
			void Resized(object sender, SizeChangedEventArgs e) => Update(dress);
			seek.SizeChanged += Resized;
			dress.Restore.Add(() => seek.SizeChanged -= Resized);
		}
	}

	private static void DressClock(FrameworkElement bar, Dress dress)
	{
		if (bar is not Panel top)
		{
			return;
		}

		dress.Clock = new NixieReadout { DigitHeight = 34, HorizontalAlignment = HorizontalAlignment.Right };
		dress.ClockCaption = Caption("ENDS AT", HorizontalAlignment.Right);
		var clock = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Spacing = 6, IsHitTestVisible = false };
		clock.Children.Add(dress.Clock);
		clock.Children.Add(dress.ClockCaption);
		Add(dress, top, clock);
	}

	private static StackPanel Counter(NixieReadout tubes, string caption, HorizontalAlignment align)
	{
		var panel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = align, IsHitTestVisible = false };
		tubes.HorizontalAlignment = align;
		panel.Children.Add(tubes);
		panel.Children.Add(Caption(caption, align));
		return panel;
	}

	private static TextBlock Caption(string text, HorizontalAlignment align)
	{
		var caption = new TextBlock
		{
			Text = text,
			HorizontalAlignment = align,
			FontSize = 11,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			CharacterSpacing = 140,
			Foreground = new SolidColorBrush(Color.FromArgb(255, 0xD9, 0xC6, 0xC8)),
		};
		AutomationProperties.SetAccessibilityView(caption, AccessibilityView.Raw);
		return caption;
	}

	private static void Add(Dress dress, Panel parent, UIElement child)
	{
		parent.Children.Add(child);
		dress.Added.Add((parent, child));
	}

	/// <summary>A shared part the tubes stand in for: unseen, still there for Narrator and the layout.</summary>
	private static void Hide(Dress dress, UIElement part) => SetForNow(dress, part, UIElement.OpacityProperty, 0d);

	/// <summary>Sets <paramref name="property"/> while the theme's dress is on, and gives back what was there: a local value, or none (so a later style still applies).</summary>
	private static void SetForNow(Dress dress, DependencyObject element, DependencyProperty property, object value)
	{
		var local = element.ReadLocalValue(property);
		element.SetValue(property, value);
		dress.Restore.Add(() =>
		{
			if (local == DependencyProperty.UnsetValue)
			{
				element.ClearValue(property);
			}
			else
			{
				element.SetValue(property, local);
			}
		});
	}

	private static void Update(Dress dress)
	{
		if (dress.Controls is not { } c)
		{
			return;
		}

		var position = TimeSpan.FromTicks(Math.Max(0, c.PositionTicks));
		var duration = TimeSpan.FromTicks(Math.Max(0, c.DurationTicks));
		var left = duration > position ? duration - position : TimeSpan.Zero;
		if (dress.Elapsed is { } elapsed)
		{
			Set(elapsed, DriveInText.Counter(position), $"{DriveInText.CounterSpoken(position)} elapsed");
		}

		if (dress.Remaining is { } remaining)
		{
			Set(remaining, duration > TimeSpan.Zero ? "−" + DriveInText.Counter(left) : "", $"{DriveInText.CounterSpoken(left)} remaining");
		}

		if (dress.Tube is { } tube && dress.Seek is { } bar)
		{
			// the slider's track runs between its thumb's half-widths (about 10 DIP each side)
			var track = Math.Max(0, bar.ActualWidth - 20);
			tube.Margin = new Thickness(10, 0, 0, 0);
			tube.Width = duration > TimeSpan.Zero ? Math.Clamp(position / duration, 0, 1) * track : 0;
		}

		if (dress.Volume is { } volume)
		{
			Set(volume, c.IsMuted ? "0" : Math.Clamp(c.Volume, 0, 999).ToString(System.Globalization.CultureInfo.InvariantCulture), c.IsMuted ? "Muted" : $"Volume {c.Volume}");
		}

		if (dress.Clock is { } clock && dress.ClockCaption is { } caption)
		{
			if (duration <= TimeSpan.Zero)
			{
				clock.Text = "";
				caption.Text = "";
				return;
			}

			var speed = c.Speed > 0 ? c.Speed : 1;
			var (digits, half) = DriveInText.Clock(DateTime.Now + TimeSpan.FromTicks((long)(left.Ticks / speed)));
			Set(clock, digits, $"Ends at {digits} {half}");
			caption.Text = $"ENDS AT · {half}";
		}
	}

	private static void Set(NixieReadout tubes, string text, string spoken)
	{
		if (tubes.Text != text)
		{
			tubes.Text = text;
		}

		tubes.Spoken = spoken;
	}

	private static void Undress(FrameworkElement bar)
	{
		if (bar.GetValue(DressProperty) is not Dress dress)
		{
			return;
		}

		if (dress.Controls is { } controls && dress.Handler is { } handler)
		{
			controls.PropertyChanged -= handler;
		}

		foreach (var (parent, child) in dress.Added)
		{
			parent.Children.Remove(child);
		}

		foreach (var restore in dress.Restore)
		{
			restore();
		}

		bar.ClearValue(DressProperty);
	}

	private static MpvPlayerControls? Owner(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (node is MpvPlayerControls controls)
			{
				return controls;
			}
		}

		return null;
	}
}
