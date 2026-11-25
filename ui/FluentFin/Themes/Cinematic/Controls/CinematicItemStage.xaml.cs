using FluentFin.Controls;
using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace FluentFin.Themes.Cinematic.Controls;

/// <summary>
/// Cinematic's item page stage (see CinematicItemStage.xaml): the page gives it the item, its own Play (PrimaryActions) and
/// what runs along its foot (BottomContent); the stage draws the backdrop, the title art and the shared buttons (Trailer,
/// Watched, Favourite, More, as the shared ItemHero names and wires them) and sets the window's ambient backdrop.
/// </summary>
public sealed partial class CinematicItemStage : UserControl
{
	private const int BackdropWidth = 1920;
	private Uri? _ambient;
	private string? _ambientHash;

	public CinematicItemStage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		UndoToast.Listen();
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateActionNames();
			toggle.Unchecked += (_, _) => UpdateActionNames();
			toggle.Indeterminate += (_, _) => UpdateActionNames();
		}

		Words.SizeChanged += (_, _) => Fit(); // Nixie 1.7.5: a longer title, the logo coming, Up next drawn: the stage holds them
		BottomPresenter.SizeChanged += (_, _) => Fit();
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
			ItemUserData.Changed += OnUserDataChanged;
		};
		Unloaded += (_, _) =>
		{
			ItemUserData.Changed -= OnUserDataChanged;
			if (_ambient is not null && AmbientBackdrop.Current == _ambient)
			{
				AmbientBackdrop.Set(null);
			}
		};
		Apply();
	}

	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(CinematicItemStage), new PropertyMetadata(null, (d, _) => ((CinematicItemStage)d).Apply()));

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	public static readonly DependencyProperty KickerProperty =
		DependencyProperty.Register(nameof(Kicker), typeof(string), typeof(CinematicItemStage), new PropertyMetadata(null, (d, _) => ((CinematicItemStage)d).Apply()));

	/// <summary>The small capitals over the title (null: <see cref="CinematicLabels.ItemKicker"/>).</summary>
	public string? Kicker
	{
		get => (string?)GetValue(KickerProperty);
		set => SetValue(KickerProperty, value);
	}

	public static readonly DependencyProperty PrimaryActionsProperty =
		DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(CinematicItemStage), new PropertyMetadata(null, (d, e) => ((CinematicItemStage)d).PrimaryPresenter.Content = e.NewValue));

	public object? PrimaryActions
	{
		get => GetValue(PrimaryActionsProperty);
		set => SetValue(PrimaryActionsProperty, value);
	}

	public static readonly DependencyProperty BottomContentProperty =
		DependencyProperty.Register(nameof(BottomContent), typeof(object), typeof(CinematicItemStage), new PropertyMetadata(null, (d, e) => ((CinematicItemStage)d).BottomPresenter.Content = e.NewValue));

	/// <summary>What runs along the stage's foot: the season tabs and the episodes, or More like this.</summary>
	public object? BottomContent
	{
		get => GetValue(BottomContentProperty);
		set => SetValue(BottomContentProperty, value);
	}

	public static readonly DependencyProperty IsNarrowProperty =
		DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(CinematicItemStage), new PropertyMetadata(false, (d, _) => ((CinematicItemStage)d).Layout()));

	public bool IsNarrow
	{
		get => (bool)GetValue(IsNarrowProperty);
		set => SetValue(IsNarrowProperty, value);
	}

	/// <summary>The stage's height: the page gives it the window's (the first screen), never under 640.</summary>
	public static readonly DependencyProperty StageHeightProperty =
		DependencyProperty.Register(nameof(StageHeight), typeof(double), typeof(CinematicItemStage), new PropertyMetadata(double.NaN, (d, _) => ((CinematicItemStage)d).Fit()));

	/// <summary>
	/// Nixie 1.7.5 (the gate's g3, at 1280 x 720): the stage is never shorter than its words over what runs along its foot (an
	/// episode's Up next). The window's height less Up next was shorter than an episode's title, its series' logo, the facts,
	/// the overview and the actions, and the words, held at the bottom, ran up under the top bar: the way up (the crumbs) out
	/// of view and the logo cut (HeroLayout.Fit; the words' own top margin clears the bar).
	/// </summary>
	private void Fit()
	{
		if (double.IsNaN(StageHeight))
		{
			Stage.Height = double.NaN;
			return;
		}

		double words = 0;
		foreach (var child in Words.Children)
		{
			words += child.DesiredSize.Height;
		}

		Stage.Height = HeroLayout.Fit(StageHeight, Math.Max(words, Words.ActualHeight) + Words.Margin.Top + Words.Margin.Bottom + BottomPresenter.ActualHeight, 0);
	}

	public double StageHeight
	{
		get => (double)GetValue(StageHeightProperty);
		set => SetValue(StageHeightProperty, value);
	}

	/// <summary>Gives the keyboard to the page's Play (the first thing on the page after the bar).</summary>
	public bool FocusPrimary(FocusState state) => PrimaryPresenter.Content is Control control && control.Focus(state);

	private int _overviewLines = 3;
	private bool _overviewOpen;

	/// <summary>The overview's lines and its Show more / Show less (the shared ExpandableText's rule: ExpandableTextLayout).</summary>
	private void ShowOverview()
	{
		var (maxLines, showToggle, toggleText) = ExpandableTextLayout.For(_overviewOpen, OverviewText.IsTextTrimmed, _overviewLines);
		OverviewText.MaxLines = maxLines;
		OverviewMore.Content = toggleText;
		OverviewMore.SetExpanded(_overviewOpen);
		OverviewMore.Visibility = showToggle ? Visibility.Visible : Visibility.Collapsed;
	}

	private void OverviewText_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => ShowOverview();

	private void OverviewMore_Click(object sender, RoutedEventArgs e)
	{
		_overviewOpen = !_overviewOpen;
		ShowOverview();
	}

	private void Layout()
	{
		var narrow = IsNarrow;
		var side = narrow ? 48 : 96;
		Words.Margin = new Thickness(side, 96, 48, narrow ? 28 : 36);
		Words.MaxWidth = narrow ? 640 : 820;
		TitleArt.MaxWidth = narrow ? 600 : 820;
		TitleArt.Size = narrow ? 64 : 104;
		TitleArt.LogoMaxHeight = narrow ? 104 : 160;
		OverviewText.MaxWidth = narrow ? 560 : 660;
		_overviewLines = narrow ? 2 : 3;
		OverviewText.FontSize = narrow ? 16 : 18;
		OverviewText.LineHeight = narrow ? 24 : 27;
		ShowOverview();
		CreditsPanel.Margin = new Thickness(0, 0, narrow ? 24 : 72, narrow ? 28 : 40);
		// a narrow window has no room beside the words: the credits wait for the details below
		CreditsPanel.MaxWidth = narrow ? 0 : 440;
	}

	private void Apply()
	{
		var dto = Dto;
		BackdropImage.Source = ItemArtworkConverters.Keep(BackdropImage.Source, CinematicArt.Backdrop(dto, BackdropWidth));
		KickerText.Text = (Kicker ?? CinematicLabels.ItemKicker(dto)).ToUpper(System.Globalization.CultureInfo.CurrentCulture);
		KickerText.Visibility = KickerText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		var title = ItemMeta.HeroTitle(dto);
		TitleArt.Title = title;
		TitleArt.Dto = dto;
		TitleArt.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
		var subtitle = ItemMeta.HeroSubtitle(dto);
		SubtitleText.Text = subtitle;
		SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		MetaText.Text = CinematicLabels.MetaFacts(dto);
		AutomationProperties.SetName(MetaText, ItemChips.MetaName(dto));
		Badges.Children.Clear();
		foreach (var badge in CinematicLabels.MetaBadges(dto))
		{
			Badges.Children.Add(Badge(badge));
		}

		OverviewText.Text = dto?.Overview?.Trim() ?? "";
		_overviewOpen = false;
		ShowOverview();
		OverviewPanel.Visibility = string.IsNullOrWhiteSpace(dto?.Overview) ? Visibility.Collapsed : Visibility.Visible;
		TrailerButton.Visibility = Services.TrailerPlayback.ButtonVisibility(dto);

		CreditsList.Children.Clear();
		foreach (var (label, names) in CinematicLabels.Credits(dto))
		{
			CreditsList.Children.Add(Credit(label, names));
		}

		RatingPanel.Item = dto;
		CreditsPanel.Visibility = CreditsList.Children.Count > 0 || RatingPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
		ApplyUserData();

		_ambient = ItemArtworkConverters.AmbientBackdrop(dto);
		_ambientHash = ItemArtwork.BackdropBlurHash(dto);
		if (IsLoaded)
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
		}
	}

	private static TextBlock Credit(string label, string names)
	{
		var text = new TextBlock { FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
		text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + "  ", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
		text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = names });
		return text;
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
		var dto = Dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		// 1.7.5: shown only with something in it (a series' or a season's had nothing for a user who isn't an administrator)
		JellyfinFlyoutConverter.ApplyPageMenu(MoreButton, dto);
		UpdateActionNames();
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Dto is not { Id: { } id } dto || id != e.ItemId)
		{
			return;
		}

		dto.UserData = e.UserData;
		ApplyUserData();
	}

	private async void WatchedButton_Click(object sender, RoutedEventArgs e)
	{
		if (Dto is { } dto)
		{
			await App.Commands.SetWatched(dto, WatchedButton.IsChecked == true);
		}
	}

	private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
	{
		if (Dto is { } dto)
		{
			await App.Commands.SetFavorite(dto, FavoriteButton.IsChecked == true);
		}
	}

	private void UpdateActionNames()
	{
		var dto = Dto;
		Label(WatchedButton, ItemLabels.WatchedAction(dto, WatchedButton.IsChecked));
		Label(FavoriteButton, ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked));
		FavoriteGlyph.Glyph = ItemRowConverters.FavoriteGlyph(FavoriteButton.IsChecked);
		Label(MoreButton, ItemLabels.MoreOptions(dto));
	}

	private static void Label(FrameworkElement button, string name)
	{
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
	}

	// Nixie 1.3.1: the trailer plays in Nixie (a local one in the player, YouTube in the theatre window)
	private async void TrailerButton_Click(object sender, RoutedEventArgs e) => await Services.TrailerPlayback.Play(Dto, TrailerButton);
}
