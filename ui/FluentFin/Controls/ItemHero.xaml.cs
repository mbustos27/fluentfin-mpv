using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace FluentFin.Controls;

/// <summary>
/// Seaglass: the top of the item pages (see ItemHero.xaml). The page gives it the item (<see cref="Dto"/>), its own play buttons
/// (<see cref="PrimaryActions"/>) and the overview; the hero draws the backdrop and logo, the chips and the shared buttons, and
/// sets the window's ambient backdrop while the page is open.
/// </summary>
public sealed partial class ItemHero : UserControl
{
	// 1920 covers a full-HD window; a 4K one scales it up, which behind a scrim is not visible
	private const int BackdropWidth = 1920;

	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(ItemHero), new PropertyMetadata(null, (d, _) => ((ItemHero)d).Apply()));

	public static readonly DependencyProperty OverviewProperty =
		DependencyProperty.Register(nameof(Overview), typeof(string), typeof(ItemHero), new PropertyMetadata("", (d, e) => ((ItemHero)d).OverviewText.Text = e.NewValue as string ?? ""));

	public static readonly DependencyProperty PrimaryActionsProperty =
		DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(ItemHero), new PropertyMetadata(null, (d, e) => ((ItemHero)d).PrimaryPresenter.Content = e.NewValue));

	private Uri? _ambient;
	private string? _ambientHash;

	public ItemHero()
	{
		InitializeComponent();
		// Seaglass 1.1.4: the toggles' names follow what they show (a click flips them before the server hears it)
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateActionNames();
			toggle.Unchecked += (_, _) => UpdateActionNames();
			toggle.Indeterminate += (_, _) => UpdateActionNames();
		}
		Loaded += (_, _) =>
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
			ItemUserData.Changed += OnUserDataChanged;
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		// a page that opens before this one is closed has already set its own; only take down what is ours
		Unloaded += (_, _) =>
		{
			if (_ambient is not null && AmbientBackdrop.Current == _ambient)
			{
				AmbientBackdrop.Set(null);
			}
		};
		Apply();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	public string Overview
	{
		get => (string)GetValue(OverviewProperty);
		set => SetValue(OverviewProperty, value);
	}

	public object? PrimaryActions
	{
		get => GetValue(PrimaryActionsProperty);
		set => SetValue(PrimaryActionsProperty, value);
	}

	private void Apply()
	{
		var dto = Dto;
		BackdropImage.Source = ItemArtworkConverters.Backdrop(dto, BackdropWidth);

		var logo = ItemArtworkConverters.Logo(dto, 480);
		LogoImage.Source = logo;
		LogoImage.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
		var title = ItemMeta.HeroTitle(dto);
		TitleText.Text = title;
		// the logo is the title for the eye; the heading stays for Narrator
		TitleText.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
		AutomationProperties.SetName(LogoImage, title);

		var eyebrow = ItemMeta.Eyebrow(dto);
		Eyebrow.Text = eyebrow;
		AutomationProperties.SetName(Eyebrow, ItemMeta.EyebrowName(dto));
		Eyebrow.Visibility = eyebrow.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		var subtitle = ItemMeta.HeroSubtitle(dto);
		SubtitleText.Text = subtitle;
		SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		ChipRow.ItemsSource = ItemChips.For(dto);

		TrailerButton.Visibility = ItemMeta.TrailerUrl(dto) is null ? Visibility.Collapsed : Visibility.Visible;
		ApplyUserData();
		RatingPanel.Item = dto;

		_ambient = ItemArtworkConverters.AmbientBackdrop(dto);
		_ambientHash = ItemArtwork.BackdropBlurHash(dto);
		if (IsLoaded)
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
		}
	}

	/// <summary>
	/// Seaglass 1.1.4: the toggles and the More menu (built from the item, so its "Mark as watched" text is the item's state)
	/// show the item's user data as the server has it: on load and after every change (<see cref="ItemUserData"/>).
	/// </summary>
	private void ApplyUserData()
	{
		var dto = Dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		MoreButton.Flyout = dto is null ? null : new JellyfinFlyoutConverter().Convert(dto, typeof(object), null!, "") as FlyoutBase;
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

	// Seaglass 1.1.4: a toggle sends the state it shows after the click (ItemUserData; a failure springs it back)
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

	/// <summary>
	/// Seaglass 1.1.4: "Mark Inception as watched" / "... as unwatched", "Add Inception to favorites" / "Remove ...", "More options
	/// for Inception": named for the item and for what a click does now, the tooltip the same words (Seaglass UX review, fix 4).
	/// </summary>
	private void UpdateActionNames()
	{
		var dto = Dto;
		Label(WatchedButton, ItemLabels.WatchedAction(dto, WatchedButton.IsChecked));
		Label(FavoriteButton, ItemLabels.FavoriteAction(dto, FavoriteButton.IsChecked));
		Label(MoreButton, ItemLabels.MoreOptions(dto));
	}

	private static void Label(FrameworkElement button, string name)
	{
		AutomationProperties.SetName(button, name);
		ToolTipService.SetToolTip(button, name);
	}

	private async void TrailerButton_Click(object sender, RoutedEventArgs e)
	{
		if (ItemMeta.TrailerUrl(Dto) is { } url)
		{
			await Launcher.LaunchUriAsync(url);
		}
	}
}
