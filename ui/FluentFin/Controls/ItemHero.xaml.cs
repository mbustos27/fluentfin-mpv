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
		Loaded += (_, _) => AmbientBackdrop.Set(_ambient, _ambientHash);
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
		WatchedButton.CommandParameter = dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.CommandParameter = dto;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		MoreButton.Flyout = dto is null ? null : new JellyfinFlyoutConverter().Convert(dto, typeof(object), null!, "") as FlyoutBase;

		_ambient = ItemArtworkConverters.AmbientBackdrop(dto);
		_ambientHash = ItemArtwork.BackdropBlurHash(dto);
		if (IsLoaded)
		{
			AmbientBackdrop.Set(_ambient, _ambientHash);
		}
	}

	private async void TrailerButton_Click(object sender, RoutedEventArgs e)
	{
		if (ItemMeta.TrailerUrl(Dto) is { } url)
		{
			await Launcher.LaunchUriAsync(url);
		}
	}
}
