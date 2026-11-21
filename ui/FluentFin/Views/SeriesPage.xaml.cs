using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class SeriesPage : Page
{
	public SeriesViewModel ViewModel { get; } = App.GetService<SeriesViewModel>();

	public SeriesPage()
	{
		InitializeComponent();
		// Nixie 1.4.1: the pills are made anew for each season shown: the current one gets the keyboard back (SeasonPillFocus)
		FluentFin.Controls.SeasonPillFocus.Keep(this, ViewModel, SeasonPicker);
		// Nixie 1.5: the seasons either side get their stills asked ahead, as the rows ask them (EpisodeRow)
		FluentFin.Services.StillsAhead.Follow(this, episode => FluentFin.Core.ItemArtwork.ThumbPath(episode, FluentFin.Controls.EpisodeRow.ThumbWidth));
	}

	// 1.2.0: Back (or Forward) to this page gives its scroll position and focus back (ItemPagePlace)
	private ItemPagePlace? _place;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		// the place waits for the shown season's episodes too, so the scroll lands in the right list
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading) && !ViewModel.IsLoadingEpisodes, primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		// 1.2.0 (items review, item 1): Back shows the season the page showed when it was left, not Next Up's
		ViewModel.ReturnTo(_place.Arrived(e)?.SelectedSeasonId);
	}

	// before the page goes: by OnNavigatedFrom it is unloaded and the focus has already left it (for the title bar)
	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left(ViewModel.SelectedSeason?.Id);
		base.OnNavigatingFrom(e);
	}

	// Seaglass scrolling: a row's "See all" opens its items as a grid
	private void SimilarSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));
}
