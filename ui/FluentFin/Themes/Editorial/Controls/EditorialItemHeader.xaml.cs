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
using Windows.System;

namespace FluentFin.Themes.Editorial.Controls;

/// <summary>Editorial's item page header (see EditorialItemHeader.xaml).</summary>
public sealed partial class EditorialItemHeader : UserControl
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(BaseItemDto), typeof(EditorialItemHeader), new PropertyMetadata(null, (d, _) => ((EditorialItemHeader)d).Apply()));

	public static readonly DependencyProperty KickerProperty =
		DependencyProperty.Register(nameof(Kicker), typeof(string), typeof(EditorialItemHeader), new PropertyMetadata("", (d, e) => ((EditorialItemHeader)d).KickerText.Text = e.NewValue as string ?? ""));

	public static readonly DependencyProperty PlayNoteProperty =
		DependencyProperty.Register(nameof(PlayNote), typeof(string), typeof(EditorialItemHeader), new PropertyMetadata("", (d, e) => ((EditorialItemHeader)d).ShowPlayNote()));

	public static readonly DependencyProperty PrimaryActionsProperty =
		DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(EditorialItemHeader), new PropertyMetadata(null, (d, e) => ((EditorialItemHeader)d).PrimaryPresenter.Content = e.NewValue));

	public static readonly DependencyProperty SidePanelProperty =
		DependencyProperty.Register(nameof(SidePanel), typeof(object), typeof(EditorialItemHeader), new PropertyMetadata(null, (d, e) => ((EditorialItemHeader)d).SidePresenter.Content = e.NewValue));

	public static readonly DependencyProperty IsNarrowProperty =
		DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(EditorialItemHeader), new PropertyMetadata(false, (d, _) => ((EditorialItemHeader)d).Layout()));

	public EditorialItemHeader()
	{
		InitializeComponent();
		UndoToast.Listen(); // the Undo after a whole series or season is marked (the shared hero's)
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateNames();
			toggle.Unchecked += (_, _) => UpdateNames();
			toggle.Indeterminate += (_, _) => UpdateNames();
		}

		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(null); // Editorial's ground is opaque: the item's art is drawn here, not behind the window
			ItemUserData.Changed += OnUserDataChanged;
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		// the backdrop runs under the poster and the words (and the panel beside them), fading out under them; a panel moved
		// under the words (a narrow window) sits on the ground
		Words.SizeChanged += (_, _) => FitBackdrop();
		SidePresenter.SizeChanged += (_, _) => FitBackdrop();
		Layout();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	/// <summary>The line over the title: "SERIES · 3 SEASONS · 24 EPISODES".</summary>
	public string Kicker
	{
		get => (string)GetValue(KickerProperty);
		set => SetValue(KickerProperty, value);
	}

	/// <summary>Beside Play: what is left of what it resumes ("44 min left").</summary>
	public string PlayNote
	{
		get => (string)GetValue(PlayNoteProperty);
		set => SetValue(PlayNoteProperty, value);
	}

	/// <summary>The page's own Play (its one Primary) and anything that goes with it.</summary>
	public object? PrimaryActions
	{
		get => GetValue(PrimaryActionsProperty);
		set => SetValue(PrimaryActionsProperty, value);
	}

	/// <summary>The series' Seasons panel: beside the words, under them in a narrow window.</summary>
	public object? SidePanel
	{
		get => GetValue(SidePanelProperty);
		set => SetValue(SidePanelProperty, value);
	}

	public bool IsNarrow
	{
		get => (bool)GetValue(IsNarrowProperty);
		set => SetValue(IsNarrowProperty, value);
	}

	/// <summary>"Cast &amp; crew": the page scrolls to its cast.</summary>
	public event EventHandler? CastRequested;

	private void Layout()
	{
		var narrow = IsNarrow;
		Spread.Margin = new Thickness(narrow ? 40 : 56, narrow ? 24 : 32, narrow ? 40 : 56, 8);
		Spread.ColumnSpacing = narrow ? 24 : 32;
		PosterColumn.Width = new GridLength(narrow ? 160 : 216);
		PosterFrame.Width = narrow ? 160 : 216;
		PosterFrame.Height = narrow ? 240 : 324;
		Grid.SetRow(SidePresenter, narrow ? 1 : 0);
		Grid.SetColumn(SidePresenter, narrow ? 0 : 2);
		Grid.SetColumnSpan(SidePresenter, narrow ? 3 : 1);
		ApplyTitleSize();
		FitBackdrop();
	}

	private void FitBackdrop()
	{
		var top = Math.Max(Words.ActualHeight, PosterFrame.Height);
		if (!IsNarrow)
		{
			top = Math.Max(top, SidePresenter.ActualHeight);
		}

		Backdrop.Height = Math.Max(IsNarrow ? 360 : 440, top + Spread.Margin.Top + 72);
	}

	private void ApplyTitleSize()
	{
		var size = EditorialItemTitle.Size(TitleText.Text, IsNarrow);
		TitleText.FontSize = size;
		TitleText.LineHeight = Math.Round(size * 1.02);
	}

	private void ShowPlayNote()
	{
		PlayNoteText.Text = PlayNote;
		PlayNoteText.Visibility = string.IsNullOrEmpty(PlayNote) ? Visibility.Collapsed : Visibility.Visible;
	}

	private void Apply()
	{
		var dto = Dto;
		BackdropImage.Source = ItemArtworkConverters.Backdrop(dto, 1920);
		PosterImage.Source = BaseItemDtoConverters.GetImage(dto, ImageType.Primary, 486);
		PosterFrame.Visibility = dto is null ? Visibility.Collapsed : Visibility.Visible;
		var title = ItemMeta.HeroTitle(dto);
		TitleText.Text = title;
		TitleText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
		ApplyTitleSize();
		var subtitle = ItemMeta.HeroSubtitle(dto);
		SubtitleText.Text = subtitle;
		SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		MetaText.Text = EditorialItemTitle.Meta(dto);
		Badges.ItemsSource = EditorialCards.Badges(dto);
		OverviewText.Text = dto?.Overview?.Trim() ?? "";
		OverviewText.Visibility = OverviewText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		PeopleText.Text = EditorialCards.People(dto);
		PeopleText.Visibility = PeopleText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		CastLink.Visibility = dto?.People is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
		TrailerButton.Visibility = ItemMeta.TrailerUrl(dto) is null ? Visibility.Collapsed : Visibility.Visible;
		RatingPanel.Item = dto;
		ApplyUserData();
	}

	private void ApplyUserData()
	{
		var dto = Dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		MoreButton.Flyout = dto is null ? null : new JellyfinFlyoutConverter { IncludeToggles = false }.Convert(dto, typeof(object), null!, "") as FlyoutBase;
		UpdateNames();
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

	private void UpdateNames()
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

	private async void TrailerButton_Click(object sender, RoutedEventArgs e)
	{
		if (ItemMeta.TrailerUrl(Dto) is { } url)
		{
			await Launcher.LaunchUriAsync(url);
		}
	}

	private void CastLink_Click(object sender, RoutedEventArgs e) => CastRequested?.Invoke(this, EventArgs.Empty);
}
