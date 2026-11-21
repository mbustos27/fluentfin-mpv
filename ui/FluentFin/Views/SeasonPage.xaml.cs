using FluentFin.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Views;

public sealed partial class SeasonPage : Page
{
	public SeasonViewModel ViewModel { get; } = App.GetService<SeasonViewModel>();

	public SeasonPage()
	{
		InitializeComponent();
		// Nixie 1.5: the seasons either side get their stills asked ahead, as the rows ask them (EpisodeRow)
		FluentFin.Services.StillsAhead.Follow(this, episode => FluentFin.Core.ItemArtwork.ThumbPath(episode, FluentFin.Controls.EpisodeRow.ThumbWidth));
	}

	// 1.2.0: Back (or Forward) to this page gives its scroll position and focus back (ItemPagePlace)
	private ItemPagePlace? _place;

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading), primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		_place.Arrived(e);
	}

	// before the page goes: by OnNavigatedFrom it is unloaded and the focus has already left it (for the title bar)
	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}
}
