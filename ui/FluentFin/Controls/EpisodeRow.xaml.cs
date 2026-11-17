using FluentFin.Converters;
using FluentFin.Core;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace FluentFin.Controls;

/// <summary>Seaglass: one episode as an opaque card (see EpisodeRow.xaml); the item is <see cref="Dto"/>.</summary>
public sealed partial class EpisodeRow : UserControl
{
	public static readonly DependencyProperty DtoProperty =
		DependencyProperty.Register(nameof(Dto), typeof(object), typeof(EpisodeRow), new PropertyMetadata(null, (d, _) => ((EpisodeRow)d).Apply()));

	public EpisodeRow()
	{
		InitializeComponent();
		Apply();
	}

	public BaseItemDto? Dto
	{
		get => GetValue(DtoProperty) as BaseItemDto;
		set => SetValue(DtoProperty, value);
	}

	private void Apply()
	{
		var dto = Dto;
		Thumb.Source = ItemArtworkConverters.Thumb(dto, 352);
		TitleText.Text = ItemRowConverters.EpisodeTitle(dto);
		var runtime = ItemRowConverters.Runtime(dto);
		RuntimeText.Text = runtime;
		RuntimeText.Visibility = runtime.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
		OverviewText.Text = dto?.Overview ?? "";
		Progress.Value = ItemRowConverters.Progress(dto);
		Progress.Visibility = ItemRowConverters.InProgress(dto);
		WatchedMark.Visibility = ItemRowConverters.Watched(dto);

		foreach (var button in new ButtonBase[] { DetailsButton, PlayButton, WatchedButton, FavoriteButton, MoreButton })
		{
			button.CommandParameter = dto;
		}

		AutomationProperties.SetName(DetailsButton, ItemLabels.EpisodeAction("Episode details", dto));
		AutomationProperties.SetName(PlayButton, ItemLabels.EpisodeAction("Play", dto));
		AutomationProperties.SetName(WatchedButton, ItemLabels.EpisodeAction("Watched", dto));
		AutomationProperties.SetName(FavoriteButton, ItemLabels.EpisodeAction("Favorite", dto));
		AutomationProperties.SetName(MoreButton, ItemLabels.EpisodeAction("More options", dto));
		WatchedButton.IsChecked = dto?.UserData?.Played;
		FavoriteButton.IsChecked = dto?.UserData?.IsFavorite;
		MoreButton.Flyout = dto is null ? null : new JellyfinFlyoutConverter().Convert(dto, typeof(object), null!, "") as FlyoutBase;
	}
}
