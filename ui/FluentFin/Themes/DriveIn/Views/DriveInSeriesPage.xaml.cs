using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Themes.DriveIn.Controls;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.DriveIn.Views;

/// <summary>Drive-in: a series (see DriveInSeriesPage.xaml), over the shared <see cref="SeriesViewModel"/>.</summary>
public sealed partial class DriveInSeriesPage : Page
{
	public SeriesViewModel ViewModel { get; } = App.GetService<SeriesViewModel>();

	private ItemPagePlace? _place;

	public DriveInSeriesPage()
	{
		InitializeComponent();
		SizeChanged += (_, e) => Body.Padding = new Thickness(ItemStage.MarginFor(e.NewSize.Width), 8, ItemStage.MarginFor(e.NewSize.Width), 56);
		// Nixie 1.4.1: the season tubes are made anew for each season shown: the current one gets the keyboard back (it fell
		// to "Season N details")
		FluentFin.Controls.SeasonPillFocus.Keep(this, ViewModel, SeasonTubes);
		// Nixie 1.5: the seasons either side get their stills asked ahead, as the showtimes ask them
		FluentFin.Services.StillsAhead.Follow(this, episode => FluentFin.Core.ItemArtwork.ThumbPath(episode, Showtime.ThumbWidth));
	}

	/// <summary>The episode Play starts among the shown season's (NEXT on the board, its time left beside Play).</summary>
	public static BaseItemDto? NextOf(List<BaseItemDto>? episodes, BaseItemViewModel? nextUp) =>
		DriveInItems.NextEpisode(episodes ?? [], nextUp?.Dto);

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading) && !ViewModel.IsLoadingEpisodes, primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		ViewModel.ReturnTo(_place.Arrived(e)?.SelectedSeasonId);
	}

	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left(ViewModel.SelectedSeason?.Id);
		base.OnNavigatingFrom(e);
	}

	private void SimilarSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));
}
