using CommunityToolkit.WinUI;
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

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>Cinematic's Home hero (see CinematicHero.xaml). The slides are the shared Home's (<see cref="HomeViewModel.Hero"/>).</summary>
public sealed partial class CinematicHero : UserControl
{
	private const int BackdropWidth = 1920;
	private const int ThumbWidth = 104, ThumbHeight = 58;

	private readonly DispatcherTimer _timer = new() { Interval = HomeHero.AdvanceEvery };
	private IReadOnlyList<HeroSlide> _slides = [];
	private int _index = -1;
	private bool _pointerIn;
	private bool _focusIn;
	private bool _paused;
	private Uri? _shown;
	private string? _shownHash;

	[GeneratedDependencyProperty]
	public partial IReadOnlyList<HeroSlide>? Slides { get; set; }

	/// <summary>How much of the hero's foot the page covers (the Up next shelf): the words and the pager stay above it.</summary>
	[GeneratedDependencyProperty(DefaultValue = 300.0)]
	public partial double ContentBottom { get; set; }

	/// <summary>A narrow window (under 1400 px): smaller title art and margins, dots instead of thumbnails.</summary>
	[GeneratedDependencyProperty(DefaultValue = false)]
	public partial bool IsNarrow { get; set; }

	public CinematicHero()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		Visibility = Visibility.Collapsed;
		_timer.Tick += (_, _) =>
		{
			if (!App.IsClosing) // 1.7.3 fix round (the close crash's class): nothing in the window's teardown
			{
				Advance();
			}
		};
		Loaded += (_, _) =>
		{
			SafeMotion.Subscribe(OnGlassModeChanged);
			ItemUserData.Changed += OnUserDataChanged;
			UpdateTimer();
			if (_shown is not null)
			{
				AmbientBackdrop.Set(_shown, _shownHash);
			}
		};
		Unloaded += (_, _) =>
		{
			SafeMotion.Unsubscribe(OnGlassModeChanged);
			ItemUserData.Changed -= OnUserDataChanged;
			_timer.Stop();
			if (AmbientBackdrop.Current is { } current && current == _shown)
			{
				AmbientBackdrop.Set(null);
			}
		};
		Thumbs.KeyDown += Thumbs_KeyDown;
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateActionNames();
			toggle.Unchecked += (_, _) => UpdateActionNames();
		}

		RegisterPropertyChangedCallback(ContentBottomProperty, (_, _) => Layout());
		RegisterPropertyChangedCallback(IsNarrowProperty, (_, _) => { Layout(); BuildThumbs(); ShowCurrent(); });
		Layout();
	}

	/// <summary>The item the hero shows now, or null.</summary>
	public HeroSlide? Current => _index >= 0 && _index < _slides.Count ? _slides[_index] : null;

	/// <summary>Gives the keyboard to the hero's Resume or Play (Home's first stop under the bar).</summary>
	public bool FocusPrimary(FocusState state) => Visibility == Visibility.Visible && PlayButton.Focus(state);

	partial void OnSlidesChanged(IReadOnlyList<HeroSlide>? newValue)
	{
		var previousKey = Current?.Dto.Id;
		_slides = newValue ?? [];
		Visibility = _slides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
		BuildThumbs();
		if (_slides.Count == 0)
		{
			_index = -1;
			_timer.Stop();
			return;
		}

		var keep = previousKey is null ? -1 : _slides.ToList().FindIndex(s => s.Dto.Id == previousKey);
		Show(keep >= 0 ? keep : 0, animate: _index >= 0);
		UpdateTimer();
	}

	private void Layout()
	{
		var narrow = IsNarrow;
		var side = narrow ? 48 : 96;
		Words.Margin = new Thickness(side, 0, 48, ContentBottom + (narrow ? 40 : 46));
		Words.MaxWidth = narrow ? 600 : 820;
		TitleArt.MaxWidth = narrow ? 560 : 820;
		TitleArt.Size = narrow ? 66 : 104;
		TitleArt.LogoMaxHeight = narrow ? 110 : 170;
		OverviewText.MaxWidth = narrow ? 520 : 640;
		OverviewText.FontSize = narrow ? 16 : 18;
		OverviewText.LineHeight = narrow ? 24 : 27;
		Pager.Margin = new Thickness(0, 0, side, ContentBottom + (narrow ? 70 : 18));
	}

	private void OnGlassModeChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdateTimer);

	private void UpdateTimer()
	{
		if (_slides.Count > 1 && SafeMotion.Allowed && !_paused && !_pointerIn && !_focusIn && IsLoaded)
		{
			_timer.Stop();
			_timer.Start();
			StartProgress();
		}
		else
		{
			_timer.Stop();
			StopProgress();
		}
	}

	private void Advance() => Show((_index + 1) % _slides.Count, animate: true);

	// ---- the pager -------------------------------------------------------------------------------------------------

	private readonly List<Button> _thumbs = [];
	private Border? _runningBar;

	private void BuildThumbs()
	{
		Thumbs.Children.Clear();
		_thumbs.Clear();
		Pager.Visibility = _slides.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
		var baseUrl = SessionInfo.BaseUrl ?? "";
		for (var i = 0; i < _slides.Count; i++)
		{
			var at = i;
			var slide = _slides[i];
			Button button;
			if (IsNarrow)
			{
				// a 32 px target around an 8 px mark (22 px wide for the slide shown)
				button = new Button
				{
					Width = HeroDots.TargetSize, Height = HeroDots.TargetSize, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
					Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
					Content = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = OnArt, Opacity = 0.45 },
				};
			}
			else
			{
				var thumb = new Grid { Width = ThumbWidth, Height = ThumbHeight, CornerRadius = new CornerRadius(6) };
				thumb.Children.Add(new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage { DecodePixelWidth = ThumbWidth * 2, UriSource = slide.BackdropUri(baseUrl, ThumbWidth * 2) } });
				thumb.Children.Add(new Border { BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) });
				var bar = new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 0 };
				bar.Background = Accent;
				thumb.Children.Add(bar);
				button = new Button
				{
					Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
					BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8), Content = thumb,
					Style = (Style)Application.Current.Resources["BareButtonStyle"],
				};
			}

			AutomationProperties.SetName(button, $"Slide {i + 1} of {_slides.Count}: {slide.Title}");
			ToolTipService.SetToolTip(button, slide.Title);
			button.IsTabStop = false;
			button.Click += (_, _) => { Show(at, animate: true); UpdateTimer(); };
			_thumbs.Add(button);
			Thumbs.Children.Add(button);
		}
	}

	private void ShowCurrent()
	{
		for (var i = 0; i < _thumbs.Count; i++)
		{
			var current = i == _index;
			var button = _thumbs[i];
			button.IsTabStop = current;
			AutomationProperties.SetItemStatus(button, current ? "Current slide" : "");
			if (button.Content is Border dot)
			{
				dot.Width = current ? 22 : 8;
				dot.Opacity = current ? 1 : 0.45;
			}
			else if (button.Content is Grid thumb)
			{
				thumb.Opacity = current ? 1 : 0.72;
				// the slide shown: the two focus colours as a ring (as the stills' rim), its bar runs while the timer does
				button.BorderBrush = current ? Core : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
				if (thumb.Children[^1] is Border bar)
				{
					bar.Width = current ? ThumbWidth * 0.46 : 0;
				}
			}
		}

		StartProgress();
	}

	/// <summary>The shown slide's bar fills over the 7 s before the next (Full only; otherwise it rests at 46 %).</summary>
	private void StartProgress()
	{
		StopProgress();
		if (_index < 0 || _index >= _thumbs.Count || _thumbs[_index].Content is not Grid { Children: [.., Border bar] } || !_timer.IsEnabled)
		{
			return;
		}

		_runningBar = bar;
		var grow = new DoubleAnimation { From = 0, To = ThumbWidth, Duration = HomeHero.AdvanceEvery, EnableDependentAnimation = true };
		Storyboard.SetTarget(grow, bar);
		Storyboard.SetTargetProperty(grow, "Width");
		var board = new Storyboard();
		board.Children.Add(grow);
		_progress = board;
		board.Begin();
	}

	private Storyboard? _progress;

	private void StopProgress()
	{
		if (_progress is { } board)
		{
			board.Stop();
			_progress = null;
		}

		if (_runningBar is { } bar)
		{
			bar.Width = _thumbs.Count > _index && _index >= 0 && ReferenceEquals((_thumbs[_index].Content as Grid)?.Children[^1], bar) ? ThumbWidth * 0.46 : 0;
			_runningBar = null;
		}
	}

	private void Thumbs_KeyDown(object sender, KeyRoutedEventArgs e)
	{
		if (HeroDots.KeyTarget(e.Key, _index, _slides.Count) is not { } index)
		{
			return;
		}

		e.Handled = true;
		Show(index, animate: true);
		UpdateTimer();
		_thumbs[index].Focus(FocusState.Keyboard);
	}

	private void Pager_Tapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

	private void PauseButton_Click(object sender, RoutedEventArgs e)
	{
		_paused = !_paused;
		PauseIcon.Glyph = _paused ? "" : "";
		var name = _paused ? "Play slideshow" : "Pause slideshow";
		AutomationProperties.SetName(PauseButton, name);
		ToolTipService.SetToolTip(PauseButton, name);
		UpdateTimer();
	}

	// ---- a slide ---------------------------------------------------------------------------------------------------

	private void Show(int index, bool animate)
	{
		if (index < 0 || index >= _slides.Count)
		{
			return;
		}

		var slide = _slides[index];
		_index = index;
		ShowCurrent();

		var baseUrl = SessionInfo.BaseUrl ?? "";
		var backdrop = slide.BackdropUri(baseUrl, BackdropWidth);
		var next = new BitmapImage { DecodePixelWidth = BackdropWidth, UriSource = backdrop };
		_shown = backdrop;
		_shownHash = slide.BackdropBlurHash;
		if (IsLoaded)
		{
			AmbientBackdrop.Set(backdrop, slide.BackdropBlurHash);
		}

		var fade = animate && SafeMotion.Allowed ? HomeHero.FadeTime : animate ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero;
		BackdropTop.Opacity = 0;
		BackdropTop.Source = next;
		next.ImageOpened += (_, _) =>
		{
			if (ReferenceEquals(BackdropTop.Source, next))
			{
				Fade(BackdropTop, 0, 1, fade, () => { BackdropBottom.Source = next; BackdropTop.Opacity = 0; });
			}
		};

		var dto = slide.Dto;
		KickerText.Text = CinematicLabels.Kicker(dto, slide.IsResume).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
		TitleArt.Title = slide.Title;
		TitleArt.Dto = dto;
		MetaText.Text = CinematicLabels.MetaFacts(dto);
		AutomationProperties.SetName(MetaText, ItemChips.MetaName(dto));
		Badges.Children.Clear();
		foreach (var badge in CinematicLabels.MetaBadges(dto))
		{
			Badges.Children.Add(Badge(badge));
		}

		OverviewText.Text = slide.Overview;
		OverviewText.Visibility = string.IsNullOrEmpty(slide.Overview) ? Visibility.Collapsed : Visibility.Visible;

		PlayText.Text = slide.IsResume ? "Resume" : "Play";
		AutomationProperties.SetName(PlayButton, ItemLabels.PlayAction(dto) is { Length: > 4 } name ? name : $"Play {slide.Title}");
		ToolTipService.SetToolTip(PlayButton, ItemLabels.PlayLabel(dto));
		AutomationProperties.SetName(MoreInfoButton, $"More info about {slide.Title}");
		var left = CinematicLabels.TimeLeft(dto);
		LeftPanel.Visibility = slide.IsResume && left is not null ? Visibility.Visible : Visibility.Collapsed;
		LeftText.Text = left ?? "";
		LeftFill.Width = 110 * CinematicLabels.Progress(dto) / 100;
		ApplyUserData();

		if (animate)
		{
			Fade(Words, 0, 1, fade, null);
		}
	}

	private static Border Badge(string text) => new()
	{
		Height = 21,
		Padding = new Thickness(6, 0, 6, 0),
		CornerRadius = new CornerRadius(4),
		BorderThickness = new Thickness(1),
		BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
		Child = new TextBlock
		{
			Text = text.ToUpper(System.Globalization.CultureInfo.CurrentCulture),
			FontSize = 12,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			CharacterSpacing = 40,
			VerticalAlignment = VerticalAlignment.Center,
		},
	};

	private void ApplyUserData()
	{
		var dto = Current?.Dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		UpdateActionNames();
	}

	private void UpdateActionNames()
	{
		var dto = Current?.Dto;
		Label(WatchedButton, ItemLabels.WatchedAction(dto, WatchedButton.IsChecked));
		Label(FavoriteButton, ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked));
		FavoriteGlyph.Glyph = FluentFin.Converters.ItemRowConverters.FavoriteGlyph(FavoriteButton.IsChecked);
	}

	private static void Label(FrameworkElement button, string name)
	{
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Current?.Dto is not { Id: { } id } dto || id != e.ItemId)
		{
			return;
		}

		dto.UserData = e.UserData;
		ApplyUserData();
	}

	private async void WatchedButton_Click(object sender, RoutedEventArgs e)
	{
		if (Current?.Dto is { } dto)
		{
			await App.Commands.SetWatched(dto, WatchedButton.IsChecked == true);
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (Current?.Dto is { } dto)
		{
			await App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true);
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
		if (Current?.Dto is { } dto)
		{
			_ = App.Commands.PlayDto(dto);
		}
	}

	private void MoreInfo_Click(object sender, RoutedEventArgs e)
	{
		if (Current?.Dto is { } dto)
		{
			App.Commands.DisplayDto(dto);
		}
	}

	private void Root_Tapped(object sender, TappedRoutedEventArgs e)
	{
		if (Current?.Dto is not { } dto || HeroClick.OnAControl(e.OriginalSource as DependencyObject, Root))
		{
			return;
		}

		App.Commands.DisplayDto(dto);
		e.Handled = true;
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

	// the pager sits on the art in every theme: light marks, the accent bar, the neon core's ring (system colours in high contrast)
	private static Brush OnArt => SystemOr("SystemColorWindowTextColor", Microsoft.UI.Colors.White);

	private static Brush Accent => SystemOr("SystemColorHighlightColor", Windows.UI.Color.FromArgb(0xFF, 0xF9, 0x69, 0x85));

	private static Brush Core => SystemOr("SystemColorHighlightColor", Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xF4, 0xEE));

	private static SolidColorBrush SystemOr(string systemColor, Windows.UI.Color color) =>
		new(GlassMode.IsHighContrast && Application.Current.Resources.TryGetValue(systemColor, out var value) && value is Windows.UI.Color system ? system : color);
}
