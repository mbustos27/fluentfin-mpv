using FluentFin.Controls;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>
/// Cinematic's library preview stage (see CinematicPreviewStage.xaml). <see cref="Show"/> is called as the grid's focus moves;
/// the stage waits for the focus to rest (<see cref="Settle"/>) before it swaps the picture and the words, so arrowing along a
/// row does not load every backdrop on the way.
/// </summary>
public sealed partial class CinematicPreviewStage : UserControl
{
	/// <summary>How long the focus rests on a title before the stage shows it (the concept's 150 to 250 ms).</summary>
	public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);

	private const int BackdropWidth = 1920;
	private readonly DispatcherTimer _settle = new() { Interval = Settle };
	private BaseItemDto? _pending;
	private BaseItemDto? _shown;

	public CinematicPreviewStage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		_settle.Tick += (_, _) =>
		{
			_settle.Stop();
			Apply(_pending, animate: true);
		};
		Loaded += (_, _) => ItemUserData.Changed += OnUserDataChanged;
		Unloaded += (_, _) =>
		{
			_settle.Stop();
			ItemUserData.Changed -= OnUserDataChanged;
		};
		FavoriteButton.Checked += (_, _) => UpdateNames();
		FavoriteButton.Unchecked += (_, _) => UpdateNames();
		Visibility = Visibility.Collapsed;
	}

	/// <summary>The words' height (kicker to buttons): the page keeps the grid under them.</summary>
	public double WordsHeight => Words.ActualHeight;

	/// <summary>The words changed height (another title, a narrower window).</summary>
	public event SizeChangedEventHandler? WordsSizeChanged
	{
		add => Words.SizeChanged += value;
		remove => Words.SizeChanged -= value;
	}

	/// <summary>The title shown (null before the library has any).</summary>
	public BaseItemDto? Current => _shown;

	/// <summary>The stage's picture height (the page sets it from the window's height); the words sit at its top.</summary>
	public double ArtHeight
	{
		get => Art.Height;
		set => Art.Height = value;
	}

	/// <summary>
	/// Nixie 1.4.0 (the UX review's C-M1, C-m12, item 12): the words under the library's name, sized for a stage a third of the
	/// window high: no kicker (at 1100 it read as the name's subtitle), the title at 56 (48 narrow), one line of overview.
	/// </summary>
	public void SetLayout(bool narrow, double top)
	{
		Words.Margin = new Thickness(narrow ? 48 : 96, top, 48, 0);
		KickerText.Visibility = Visibility.Collapsed;
		TitleArt.Margin = new Thickness(0);
		TitleArt.Size = narrow ? 48 : 56;
		TitleArt.LogoMaxHeight = narrow ? 64 : 76;
		TitleArt.MaxWidth = narrow ? 560 : 900;
		OverviewText.MaxWidth = narrow ? 520 : 640;
		OverviewText.FontSize = narrow ? 15 : 16;
		OverviewText.LineHeight = narrow ? 22 : 24;
		OverviewText.MaxLines = 1;
	}

	/// <summary>Nixie 1.4.0: the stage folded to a band (the grid scrolled): the art's strip only, its words and Play gone.</summary>
	public void SetBand(bool band) => Words.Visibility = band ? Visibility.Collapsed : Visibility.Visible;

	/// <summary>Show <paramref name="dto"/> once the focus has rested on it; the first title is shown at once.</summary>
	public void Show(BaseItemDto? dto)
	{
		if (dto is null || dto.Id == _shown?.Id)
		{
			_settle.Stop();
			_pending = null;
			return;
		}

		if (_shown is null)
		{
			Apply(dto, animate: false);
			return;
		}

		_pending = dto;
		_settle.Stop();
		_settle.Start();
	}

	/// <summary>
	/// Nixie 1.3.1 (the UX review's C-B1): nothing to preview (no title matches the filter): the stage goes, with its Play, so it
	/// never offers a title the grid does not show.
	/// </summary>
	public void ShowNothing()
	{
		_settle.Stop();
		_pending = null;
		_shown = null;
		Visibility = Visibility.Collapsed;
	}

	/// <summary>The keyboard to the stage's Play.</summary>
	public bool FocusPrimary(FocusState state) => Visibility == Visibility.Visible && PlayButton.Focus(state);

	private void Apply(BaseItemDto? dto, bool animate)
	{
		if (dto is null)
		{
			return;
		}

		_shown = dto;
		Visibility = Visibility.Visible;
		var picture = CinematicArt.Backdrop(dto, BackdropWidth) ?? CinematicArt.Still(dto, BackdropWidth);
		var fade = animate && SafeMotion.Allowed ? TimeSpan.FromMilliseconds(320) : animate ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;
		BackdropTop.Opacity = 0;
		BackdropTop.Source = picture;
		if (picture is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap)
		{
			bitmap.ImageOpened += (_, _) =>
			{
				if (ReferenceEquals(BackdropTop.Source, bitmap))
				{
					Fade(BackdropTop, fade, () => { BackdropBottom.Source = bitmap; BackdropTop.Opacity = 0; });
				}
			};
		}
		else
		{
			BackdropBottom.Source = null;
		}

		KickerText.Text = CinematicLabels.ItemKicker(dto).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
		KickerText.Visibility = KickerText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		TitleArt.Title = CinematicLabels.TitleArtText(dto);
		TitleArt.Dto = dto;
		MetaText.Text = CinematicLabels.MetaFacts(dto);
		AutomationProperties.SetName(MetaText, ItemChips.MetaName(dto));
		Badges.Children.Clear();
		foreach (var badge in CinematicLabels.MetaBadges(dto))
		{
			Badges.Children.Add(new Border
			{
				Height = 21,
				Padding = new Thickness(6, 0, 6, 0),
				CornerRadius = new CornerRadius(4),
				BorderThickness = new Thickness(1),
				BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
				Child = new TextBlock { Text = badge.ToUpper(System.Globalization.CultureInfo.CurrentCulture), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = 40, VerticalAlignment = VerticalAlignment.Center },
			});
		}

		OverviewText.Text = dto.Overview?.Trim() ?? "";
		OverviewText.Visibility = OverviewText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		var resume = ItemLabels.ResumePosition(dto) is not null;
		PlayText.Text = resume ? "Resume" : "Play";
		FavoriteButton.IsChecked = dto.UserData?.IsFavorite;
		UpdateNames();
		if (animate)
		{
			Fade(Words, fade, null);
		}
	}

	private void UpdateNames()
	{
		var dto = _shown;
		var title = CinematicLabels.TitleArtText(dto);
		var play = ItemLabels.PlayAction(dto);
		AutomationProperties.SetName(PlayButton, play.Length > 4 ? play : $"Play {title}");
		ToolTipService.SetToolTip(PlayButton, ItemLabels.PlayLabel(dto));
		AutomationProperties.SetName(MoreInfoButton, $"More info about {title}");
		var favorite = ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked);
		AutomationProperties.SetName(FavoriteButton, favorite);
		ToolTipService.SetToolTip(FavoriteButton, favorite);
		FavoriteGlyph.Glyph = FluentFin.Converters.ItemRowConverters.FavoriteGlyph(FavoriteButton.IsChecked);
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (_shown is not { Id: { } id } dto || id != e.ItemId)
		{
			return;
		}

		dto.UserData = e.UserData;
		FavoriteButton.IsChecked = e.UserData?.IsFavorite;
		PlayText.Text = ItemLabels.ResumePosition(dto) is not null ? "Resume" : "Play";
		UpdateNames();
	}

	private static void Fade(UIElement target, TimeSpan duration, Action? done)
	{
		if (duration == TimeSpan.Zero)
		{
			target.Opacity = 1;
			done?.Invoke();
			return;
		}

		var animation = new DoubleAnimation { From = 0, To = 1, Duration = duration, EnableDependentAnimation = true };
		Storyboard.SetTarget(animation, target);
		Storyboard.SetTargetProperty(animation, "Opacity");
		var board = new Storyboard();
		board.Children.Add(animation);
		board.Completed += (_, _) => { target.Opacity = 1; done?.Invoke(); };
		board.Begin();
	}

	private void Play_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			_ = App.Commands.PlayDto(dto);
		}
	}

	private void MoreInfo_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			App.Commands.DisplayDto(dto);
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (_shown is { } dto)
		{
			await App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true);
		}
	}
}
