using FluentFin.Controls;
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

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>
/// Drive-in: the outdoor screen (see OutdoorScreen.xaml). The features rotate as Home's hero does (a crossfade, every 7 s,
/// only while motion is allowed and neither the pointer nor the keyboard is on the screen, with a pause); a click on the
/// artwork or the words opens the feature, as More info does.
/// </summary>
public sealed partial class OutdoorScreen : UserControl
{
	public static readonly TimeSpan AdvanceEvery = TimeSpan.FromSeconds(7);
	public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(600);

	public static readonly DependencyProperty SlidesProperty =
		DependencyProperty.Register(nameof(Slides), typeof(IReadOnlyList<HeroSlide>), typeof(OutdoorScreen), new PropertyMetadata(null, (d, e) => ((OutdoorScreen)d).OnSlidesChanged(e.NewValue as IReadOnlyList<HeroSlide>)));

	public static readonly DependencyProperty ShowDetailsProperty =
		DependencyProperty.Register(nameof(ShowDetails), typeof(bool), typeof(OutdoorScreen), new PropertyMetadata(true, (d, _) => ((OutdoorScreen)d).ApplySize()));

	/// <summary>The features, the first shown first (<see cref="DriveInHome.Features"/>).</summary>
	public IReadOnlyList<HeroSlide>? Slides { get => (IReadOnlyList<HeroSlide>?)GetValue(SlidesProperty); set => SetValue(SlidesProperty, value); }

	/// <summary>Wide windows: the overview, the favourite toggle, the larger type.</summary>
	public bool ShowDetails { get => (bool)GetValue(ShowDetailsProperty); set => SetValue(ShowDetailsProperty, value); }

	private readonly DispatcherTimer _timer = new() { Interval = AdvanceEvery };
	private IReadOnlyList<HeroSlide> _slides = [];
	private int _index = -1;
	private bool _pointerIn;
	private bool _focusIn;
	private bool _paused;
	private int _decodeWidth = 1280;

	public OutdoorScreen()
	{
		InitializeComponent();
		_timer.Tick += (_, _) => Show((_index + 1) % _slides.Count, animate: true);
		Loaded += (_, _) =>
		{
			SafeMotion.Subscribe(OnGlassModeChanged);
			UpdateTimer();
			ApplyHighContrast();
		};
		Unloaded += (_, _) =>
		{
			SafeMotion.Unsubscribe(OnGlassModeChanged);
			_timer.Stop();
		};
		SizeChanged += (_, e) => _decodeWidth = (int)Math.Clamp(Math.Round(e.NewSize.Width * (XamlRoot?.RasterizationScale ?? 1) / 160) * 160, 640, 1920);
		ApplySize();
	}

	/// <summary>The feature on screen now, or null.</summary>
	public HeroSlide? Current => _index >= 0 && _index < _slides.Count ? _slides[_index] : null;

	/// <summary>Home's arrival: the keyboard on Resume (or Play), so Enter starts the last thing watched.</summary>
	public bool FocusPrimary(FocusState state = FocusState.Programmatic) => _slides.Count > 0 && PlayButton.Focus(state);

	/// <summary>The next feature (the NOW SHOWING sign's click).</summary>
	public void ShowNext()
	{
		if (_slides.Count > 1)
		{
			Show(HeroDots.Step(_index, _slides.Count, 1), animate: true);
			UpdateTimer();
		}
	}

	private void OnGlassModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => { UpdateTimer(); ApplyHighContrast(); });

	private void ApplySize()
	{
		var wide = ShowDetails;
		Words.Margin = wide ? new Thickness(44, 24, 24, 40) : new Thickness(28, 20, 20, 26);
		Words.MaxWidth = wide ? 640 : 560;
		TitleText.FontSize = wide ? 54 : 37;
		TitleText.LineHeight = wide ? 56 : 40;
		Logo.MaxHeight = wide ? 110 : 76;
		Kicker.Margin = new Thickness(0, 0, 0, wide ? 12 : 8);
		MetaText.Margin = new Thickness(0, wide ? 14 : 10, 0, 0);
		MetaText.FontSize = wide ? 15 : 14;
		Actions.Margin = new Thickness(0, wide ? 22 : 16, 0, 0);
		OverviewText.Visibility = wide && OverviewText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		FavoriteButton.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
		SlideControls.Margin = wide ? new Thickness(0, 0, 20, 22) : new Thickness(0, 0, 12, 14);
	}

	private void ApplyHighContrast()
	{
		var high = BulbFrame.HighContrast();
		Spill.Visibility = high ? Visibility.Collapsed : Visibility.Visible;
		Projector.Visibility = Spill.Visibility;
	}

	private void OnSlidesChanged(IReadOnlyList<HeroSlide>? slides)
	{
		var previous = Current?.Dto.Id;
		_slides = slides ?? [];
		Visibility = _slides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		BuildDots();
		if (_slides.Count == 0)
		{
			_index = -1;
			_timer.Stop();
			return;
		}

		var keep = previous is null ? -1 : _slides.ToList().FindIndex(s => s.Dto.Id == previous);
		Show(keep >= 0 ? keep : 0, animate: _index >= 0 && keep < 0);
		UpdateTimer();
	}

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

	private void BuildDots()
	{
		Dots.Children.Clear();
		var several = _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
		Dots.Visibility = PauseButton.Visibility = several;
		Dots.KeyDown -= Dots_KeyDown;
		Dots.KeyDown += Dots_KeyDown;
		for (var i = 0; i < _slides.Count; i++)
		{
			var at = i;
			var dot = new Button
			{
				Width = HeroDots.TargetSize,
				Height = HeroDots.TargetSize,
				Padding = new Thickness(0),
				Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
				BorderThickness = new Thickness(0),
				CornerRadius = new CornerRadius(8),
				IsTabStop = false,
				Content = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 8, Height = 8, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(DriveInScene.C("#FFF4EE")), Opacity = 0.5 },
			};
			AutomationProperties.SetName(dot, $"Feature {i + 1} of {_slides.Count}: {_slides[i].Title}");
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
			var button = (Button)Dots.Children[i];
			var bar = (Microsoft.UI.Xaml.Shapes.Rectangle)button.Content;
			button.IsTabStop = i == index;
			bar.Width = i == index ? 20 : 8;
			bar.Opacity = i == index ? 1 : 0.5;
			AutomationProperties.SetItemStatus(button, i == index ? "On screen" : "");
		}

		var baseUrl = SessionInfo.BaseUrl ?? "";
		var next = new BitmapImage { DecodePixelWidth = _decodeWidth, UriSource = slide.BackdropUri(baseUrl, _decodeWidth) };
		var fade = animate && SafeMotion.Allowed ? FadeTime : animate ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
		BackdropTop.Opacity = 0;
		BackdropTop.Source = next;
		next.ImageOpened += (_, _) =>
		{
			if (ReferenceEquals(BackdropTop.Source, next))
			{
				Fade(BackdropTop, 0, 1, fade, () => { BackdropBottom.Source = next; BackdropTop.Opacity = 0; });
			}
		};

		Kicker.Text = "FEATURE PRESENTATION";
		TitleText.Text = MarqueeSign.Upper(slide.Title);
		AutomationProperties.SetName(TitleText, slide.Title);
		MetaText.Text = slide.Meta;
		MetaText.Visibility = slide.Meta.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		OverviewText.Text = slide.Overview;

		if (slide.LogoUri(baseUrl, 600) is { } logo)
		{
			Logo.Source = new BitmapImage { DecodePixelWidth = 600, UriSource = logo };
			Logo.Visibility = Visibility.Visible;
			TitleText.Visibility = Visibility.Collapsed;
		}
		else
		{
			Logo.Source = null;
			Logo.Visibility = Visibility.Collapsed;
			TitleText.Visibility = Visibility.Visible;
		}

		// Resume (the time left in tubes beside it) or Play: named as a card's Play is
		PlayText.Text = slide.IsResume ? "Resume" : "Play";
		var name = ItemLabels.PlayAction(slide.Dto);
		AutomationProperties.SetName(PlayButton, name.Length > 4 ? name : $"Play {slide.Title}");
		ToolTipService.SetToolTip(PlayButton, ItemLabels.PlayLabel(slide.Dto));
		if (slide.IsResume && DriveInText.TimeLeft(DriveInText.Left(slide.Dto)) is { } left)
		{
			LeftTubes.Text = left.Digits;
			LeftTubes.Spoken = left.Spoken;
			LeftUnit.Text = left.Unit;
			LeftPanel.Visibility = Visibility.Visible;
		}
		else
		{
			LeftPanel.Visibility = Visibility.Collapsed;
		}

		AutomationProperties.SetName(MoreInfoButton, $"More info about {slide.Title}");
		ApplyFavorite();
		ApplySize();
		if (animate)
		{
			Fade(Words, 0, 1, fade, null);
		}
	}

	private void ApplyFavorite()
	{
		var dto = Current?.Dto;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite == true;
		var name = ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked);
		AutomationProperties.SetName(FavoriteButton, name);
		ToolTipService.SetToolTip(FavoriteButton, name);
		FavoriteGlyph.Glyph = FluentFin.Converters.ItemRowConverters.FavoriteGlyph(FavoriteButton.IsChecked);
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
		if (Current is { } slide)
		{
			_ = App.Commands.PlayDto(slide.Dto);
		}
	}

	private void MoreInfo_Click(object sender, RoutedEventArgs e)
	{
		if (Current is { } slide)
		{
			App.Commands.DisplayDto(slide.Dto);
		}
	}

	private async void Favorite_Click(object sender, RoutedEventArgs e)
	{
		if (Current is { } slide)
		{
			var on = FavoriteButton.IsChecked == true;
			ApplyFavoriteName(on);
			await App.Commands.SetFavorite(slide.Dto, on);
			ApplyFavorite();
		}
	}

	private void ApplyFavoriteName(bool on)
	{
		var name = ItemLabels.FavoriteAction(Current?.Dto, on);
		AutomationProperties.SetName(FavoriteButton, name);
		ToolTipService.SetToolTip(FavoriteButton, name);
		FavoriteGlyph.Glyph = FluentFin.Converters.ItemRowConverters.FavoriteGlyph(on);
	}

	/// <summary>A click on the artwork or the words opens the feature; the buttons and the dots do their own thing.</summary>
	private void Surface_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (Current is not { } slide || HeroClick.OnAControl(e.OriginalSource as DependencyObject, Surface))
		{
			return;
		}

		App.Commands.DisplayDto(slide.Dto);
		e.Handled = true;
	}

	private void SlideControls_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

	private void Dots_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (HeroDots.KeyTarget(e.Key, _index, _slides.Count) is not { } index)
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

	private void Pause_Click(object sender, RoutedEventArgs e)
	{
		_paused = !_paused;
		PauseIcon.Glyph = _paused ? "" : "";
		var name = _paused ? "Play the feature rotation" : "Pause the feature rotation";
		AutomationProperties.SetName(PauseButton, name);
		ToolTipService.SetToolTip(PauseButton, name);
		UpdateTimer();
	}

	private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) { _pointerIn = true; UpdateTimer(); }

	private void Root_PointerExited(object sender, PointerRoutedEventArgs e) { _pointerIn = false; UpdateTimer(); }

	private void Root_GotFocus(object sender, RoutedEventArgs e) { _focusIn = true; UpdateTimer(); }

	private void Root_LostFocus(object sender, RoutedEventArgs e)
	{
		_focusIn = FocusManager.GetFocusedElement(XamlRoot) is DependencyObject focused && IsInside(focused, Root);
		UpdateTimer();
	}

	internal static bool IsInside(DependencyObject element, DependencyObject ancestor)
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
}
