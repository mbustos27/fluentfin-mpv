using CommunityToolkit.WinUI;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentFin.Controls;

/// <summary>Seaglass: Home's hero, see HomeHero.xaml.</summary>
public sealed partial class HomeHero : UserControl
{
	public static readonly TimeSpan AdvanceEvery = TimeSpan.FromSeconds(7);
	public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(600);
	private const int BackdropWidth = 1920;

	private readonly DispatcherTimer _timer = new() { Interval = AdvanceEvery };
	private IReadOnlyList<HeroSlide> _slides = [];
	private int _index = -1;
	private bool _pointerIn;
	private bool _focusIn;
	private bool _paused;
	private Uri? _shown;
	private string? _shownHash;

	[GeneratedDependencyProperty]
	public partial IReadOnlyList<HeroSlide>? Slides { get; set; }

	public HomeHero()
	{
		InitializeComponent();
		Visibility = Visibility.Collapsed;
		_timer.Tick += (_, _) => Advance();
		Loaded += (_, _) =>
		{
			SafeMotion.Subscribe(OnGlassModeChanged);
			UpdateTimer();
			// the backdrop is set only while the hero is on screen (Show), so coming back to Home puts its slide behind it again
			if (_shown is not null)
			{
				AmbientBackdrop.Set(_shown, _shownHash);
			}
		};
		Unloaded += (_, _) =>
		{
			SafeMotion.Unsubscribe(OnGlassModeChanged);
			_timer.Stop();
			if (AmbientBackdrop.Current is { } current && current == _shown)
			{
				AmbientBackdrop.Set(null);
			}
		};
	}

	partial void OnSlidesChanged(IReadOnlyList<HeroSlide>? newValue)
	{
		var previousKey = _index >= 0 && _index < _slides.Count ? _slides[_index].Dto.Id : null;
		_slides = newValue ?? [];
		Visibility = _slides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		BuildDots();
		if (_slides.Count == 0)
		{
			_index = -1;
			_timer.Stop();
			return;
		}

		// keep the slide the viewer is on when a refresh still has it
		var keep = previousKey is null ? -1 : _slides.ToList().FindIndex(s => s.Dto.Id == previousKey);
		Show(keep >= 0 ? keep : 0, animate: _index >= 0);
		UpdateTimer();
	}

	private void OnGlassModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateTimer);

	/// <summary>Auto-advance runs only with several slides, motion allowed, and no pointer or focus in the hero.</summary>
	private void UpdateTimer()
	{
		if (_slides.Count > 1 && SafeMotion.Allowed && !_paused && !_pointerIn && !_focusIn && IsLoaded)
		{
			_timer.Stop();
			_timer.Start();
		}
		else
		{
			_timer.Stop();
		}
	}

	private void Advance() => Show((_index + 1) % _slides.Count, animate: true);

	private void BuildDots()
	{
		Dots.Children.Clear();
		Dots.Visibility = PauseButton.Visibility = _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
		Dots.KeyDown -= Dots_KeyDown;
		Dots.KeyDown += Dots_KeyDown;
		for (var i = 0; i < _slides.Count; i++)
		{
			var at = i;
			// Seaglass 1.1.4: a 24 px target (WCAG 2.5.8; it was 22) with no gap between dots, so they sit as far apart as before
			var dot = new Button
			{
				Width = HeroDots.TargetSize, Height = HeroDots.TargetSize, Padding = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
				BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(HeroDots.TargetSize / 2),
				Content = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Opacity = 0.45 },
			};
			AutomationProperties.SetName(dot, $"Slide {i + 1} of {_slides.Count}: {_slides[i].Title}");
			dot.IsTabStop = false;
			dot.Click += (_, _) => { Show(at, animate: true); UpdateTimer(); };
			Dots.Children.Add(dot);
		}
	}

	private void Show(int index, bool animate)
	{
		if (index < 0 || index >= _slides.Count)
		{
			return;
		}

		var slide = _slides[index];
		_index = index;
		for (var i = 0; i < Dots.Children.Count; i++)
		{
			var ellipse = (Microsoft.UI.Xaml.Shapes.Ellipse)((Button)Dots.Children[i]).Content;
			((Button)Dots.Children[i]).IsTabStop = i == index;
			ellipse.Opacity = i == index ? 1 : 0.45;
			ellipse.Width = ellipse.Height = i == index ? 10 : 8;
		}

		var baseUrl = SessionInfo.BaseUrl ?? "";
		var backdrop = slide.BackdropUri(baseUrl, BackdropWidth);
		var next = new BitmapImage { DecodePixelWidth = BackdropWidth };
		next.UriSource = backdrop;
		_shown = backdrop;
		_shownHash = slide.BackdropBlurHash;
		if (IsLoaded) // a hero that is off screen must not overwrite another page's backdrop; Loaded sets it again
		{
			AmbientBackdrop.Set(backdrop, slide.BackdropBlurHash);
		}

		// the new image fades in over the old one; the old one then takes its place
		var fade = animate ? FadeTime : TimeSpan.Zero;
		BackdropTop.Opacity = 0;
		BackdropTop.Source = next;
		// fade in once the picture has arrived, so a slow server shows the old backdrop, not a blank
		next.ImageOpened += (_, _) =>
		{
			if (ReferenceEquals(BackdropTop.Source, next))
			{
				Fade(BackdropTop, 0, 1, fade, () => { BackdropBottom.Source = next; BackdropTop.Opacity = 0; });
			}
		};

		TitleText.Text = slide.Title;
		MetaText.Text = slide.Meta;
		MetaText.Visibility = string.IsNullOrEmpty(slide.Meta) ? Visibility.Collapsed : Visibility.Visible;
		OverviewText.Text = slide.Overview;
		OverviewText.Visibility = string.IsNullOrEmpty(slide.Overview) ? Visibility.Collapsed : Visibility.Visible;
		PlayText.Text = slide.PlayLabel;
		AutomationProperties.SetName(PlayButton, $"{slide.PlayLabel} {slide.Title}");
		AutomationProperties.SetName(MoreInfoButton, $"More info about {slide.Title}");

		if (slide.LogoUri(baseUrl, 600) is { } logo)
		{
			Logo.Source = new BitmapImage { DecodePixelWidth = 600, UriSource = logo };
			Logo.Visibility = Visibility.Visible;
			// the logo stands for the title; Narrator still gets it from the buttons and the landmark
			TitleText.Visibility = Visibility.Collapsed;
		}
		else
		{
			Logo.Source = null;
			Logo.Visibility = Visibility.Collapsed;
			TitleText.Visibility = Visibility.Visible;
		}

		if (animate)
		{
			Fade(Words, 0, 1, fade, null);
		}
	}

	private static void Fade(UIElement target, double from, double to, TimeSpan duration, Action? done)
	{
		if (duration == TimeSpan.Zero)
		{
			target.Opacity = to;
			done?.Invoke();
			return;
		}

		var animation = new DoubleAnimation { From = from, To = to, Duration = duration, EnableDependentAnimation = true };
		Storyboard.SetTarget(animation, target);
		Storyboard.SetTargetProperty(animation, "Opacity");
		var board = new Storyboard();
		board.Children.Add(animation);
		board.Completed += (_, _) =>
		{
			target.Opacity = to;
			done?.Invoke();
		};
		board.Begin();
	}

	private void Play_Click(object sender, RoutedEventArgs e)
	{
		if (_index >= 0)
		{
			_ = App.Commands.PlayDto(_slides[_index].Dto);
		}
	}

	private void MoreInfo_Click(object sender, RoutedEventArgs e)
	{
		if (_index >= 0)
		{
			App.Commands.DisplayDto(_slides[_index].Dto);
		}
	}

	/// <summary>Seaglass scrolling: the dots are one control: the Tab stop is the dot of the slide shown (see <see cref="Show"/>), Left/Right/Home/End pick a slide.</summary>
	private void Dots_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		var target = HeroDots.KeyTarget(e.Key, _index, _slides.Count);
		if (target is not { } index)
		{
			return;
		}

		e.Handled = true;
		Show(index, animate: true);
		UpdateTimer();
		if (Dots.Children[index] is Button dot)
		{
			dot.Focus(FocusState.Keyboard);
		}
	}

	private void PauseButton_Click(object sender, RoutedEventArgs e)
	{
		_paused = !_paused;
		PauseIcon.Glyph = _paused ? "" : "";
		var name = _paused ? "Play slideshow" : "Pause slideshow";
		AutomationProperties.SetName(PauseButton, name);
		ToolTipService.SetToolTip(PauseButton, name);
		UpdateTimer();
	}

	private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) { _pointerIn = true; UpdateTimer(); }

	private void Root_PointerExited(object sender, PointerRoutedEventArgs e) { _pointerIn = false; UpdateTimer(); }

	private void Root_GotFocus(object sender, RoutedEventArgs e) { _focusIn = true; UpdateTimer(); }

	private void Root_LostFocus(object sender, RoutedEventArgs e)
	{
		_focusIn = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused);
		UpdateTimer();
	}

	private bool IsInside(DependencyObject element)
	{
		for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
		{
			if (ReferenceEquals(node, Root))
			{
				return true;
			}
		}
		return false;
	}
}

/// <summary>Seaglass scrolling: which slide a key picks on the hero's dots (Left/Right by one, no wrap; Home/End).</summary>
internal static class HeroDots
{
	/// <summary>Seaglass 1.1.4: each dot's target, the WCAG 2.5.8 minimum (the dot drawn in it stays 8 px, 10 for the slide shown);
	/// the dots have no gap (HomeHero.xaml), so their centres stay 24 px apart as before (22 + 2).</summary>
	public const double TargetSize = 24;

	public static int? KeyTarget(Windows.System.VirtualKey key, int current, int count)
	{
		if (count <= 1)
		{
			return null;
		}

		return key switch
		{
			Windows.System.VirtualKey.Left => Math.Max(0, current - 1),
			Windows.System.VirtualKey.Right => Math.Min(count - 1, current + 1),
			Windows.System.VirtualKey.Home => 0,
			Windows.System.VirtualKey.End => count - 1,
			_ => null,
		};
	}
}
