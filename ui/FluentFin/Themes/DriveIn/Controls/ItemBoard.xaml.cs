using FluentFin.Controls;
using FluentFin.Converters;
using FluentFin.Core;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace FluentFin.Themes.DriveIn.Controls;

/// <summary>Drive-in: the item pages' marquee board (see ItemBoard.xaml); its buttons do what the shared ItemHero's do.</summary>
public sealed partial class ItemBoard : UserControl
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(ItemBoard), new PropertyMetadata(null, (d, _) => ((ItemBoard)d).Apply()));

	public static readonly DependencyProperty PrimaryActionsProperty =
		DependencyProperty.Register(nameof(PrimaryActions), typeof(object), typeof(ItemBoard), new PropertyMetadata(null, (d, e) => ((ItemBoard)d).PrimaryPresenter.Content = e.NewValue));

	public static readonly DependencyProperty LeftOfProperty =
		DependencyProperty.Register(nameof(LeftOf), typeof(object), typeof(ItemBoard), new PropertyMetadata(null, (d, _) => ((ItemBoard)d).ApplyLeft()));

	public static readonly DependencyProperty CompactProperty =
		DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(ItemBoard), new PropertyMetadata(false, (d, _) => ((ItemBoard)d).ApplySize()));

	/// <summary>The item the page shows (a series, a season, a movie, an episode).</summary>
	public BaseItemDto? Dto { get => GetValue(DtoProperty) as BaseItemDto; set => SetValue(DtoProperty, value); }

	/// <summary>The page's own Play button (the board's one Primary).</summary>
	public object? PrimaryActions { get => GetValue(PrimaryActionsProperty); set => SetValue(PrimaryActionsProperty, value); }

	/// <summary>What Play resumes: its time left shows in tubes beside Play (nothing when it has no resume point).</summary>
	public BaseItemDto? LeftOf { get => GetValue(LeftOfProperty) as BaseItemDto; set => SetValue(LeftOfProperty, value); }

	/// <summary>A narrow window: smaller type and padding.</summary>
	public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

	public ItemBoard()
	{
		InitializeComponent();
		UndoToast.Listen();
		foreach (var toggle in new[] { WatchedButton, FavoriteButton })
		{
			toggle.Checked += (_, _) => UpdateActionNames();
			toggle.Unchecked += (_, _) => UpdateActionNames();
			toggle.Indeterminate += (_, _) => UpdateActionNames();
		}

		Loaded += (_, _) =>
		{
			ItemUserData.Changed += OnUserDataChanged;
			if (Board.Bulbs is { } bulbs)
			{
				DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, bulbs.Chase);
			}
		};
		Unloaded += (_, _) => ItemUserData.Changed -= OnUserDataChanged;
		Apply();
		ApplySize();
	}

	private void ApplySize()
	{
		var compact = Compact;
		Board.Padding = compact ? new Thickness(28, 32, 28, 28) : new Thickness(40, 40, 40, 36);
		TitleText.FontSize = compact ? 44 : 64;
		TitleText.LineHeight = compact ? 46 : 66;
		KickerText.FontSize = compact ? 18 : 22;
		LogoImage.MaxHeight = compact ? 88 : 120;
		OverviewText.CollapsedLines = compact ? 2 : 3;
	}

	private void Apply()
	{
		var dto = Dto;
		KickerText.Text = DriveInItems.Kicker(dto);
		var title = ItemMeta.HeroTitle(dto);
		var logo = dto?.Type == BaseItemDto_Type.Episode ? null : ItemArtworkConverters.Logo(dto, 520);
		LogoImage.Source = ItemArtworkConverters.Keep(LogoImage.Source, logo);
		LogoImage.Visibility = ItemHero.HeadingVisibility(hasLogo: logo is not null, title, forLogo: true);
		AutomationProperties.SetName(LogoImage, title);
		TitleText.Text = MarqueeSign.Upper(title);
		AutomationProperties.SetName(TitleText, title);
		TitleText.Visibility = ItemHero.HeadingVisibility(hasLogo: logo is not null, title, forLogo: false);

		var subtitle = ItemMeta.HeroSubtitle(dto);
		SubtitleText.Text = subtitle;
		SubtitleText.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

		MetaText.Text = ItemChips.MetaLine(dto);
		AutomationProperties.SetName(MetaText, ItemChips.MetaName(dto));
		MetaText.Visibility = MetaText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		OverviewText.Text = dto?.Overview ?? "";
		OverviewText.Visibility = string.IsNullOrWhiteSpace(dto?.Overview) ? Visibility.Collapsed : Visibility.Visible;

		TrailerButton.Visibility = Services.TrailerPlayback.ButtonVisibility(dto);
		ApplyUserData();
		RatingPanel.Item = dto;
		ApplyLeft();
	}

	private void ApplyLeft()
	{
		if (DriveInText.TimeLeft(DriveInText.Left(LeftOf)) is { } left)
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
	}

	private void ApplyUserData()
	{
		var dto = Dto;
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		MoreButton.Flyout = dto is null ? null : new JellyfinFlyoutConverter { IncludeToggles = false }.Convert(dto, typeof(object), null!, "") as FlyoutBase;
		UpdateActionNames();
	}

	private void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		if (Dto is { Id: { } id } dto && id == e.ItemId)
		{
			dto.UserData = e.UserData;
			ApplyUserData();
		}

		if (LeftOf is { Id: { } left } leftOf && left == e.ItemId)
		{
			leftOf.UserData = e.UserData;
			ApplyLeft();
		}
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
