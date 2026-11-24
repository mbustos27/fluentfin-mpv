using FluentFin.Core.ViewModels;
using FluentFin.Themes.DriveIn.Controls;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentFin.Themes.DriveIn.Views;

/// <summary>Drive-in: a season (see DriveInSeasonPage.xaml), over the shared <see cref="SeasonViewModel"/>.</summary>
public sealed partial class DriveInSeasonPage : Page
{
	public SeasonViewModel ViewModel { get; } = App.GetService<SeasonViewModel>();

	private ItemPagePlace? _place;

	public DriveInSeasonPage()
	{
		InitializeComponent();
		// Nixie 1.7.4: the season tubes scroll as a row does (arrows at their ends on hover, the wheel, a drag)
		_ = new FluentFin.Controls.EdgeScroller(SeasonTubesHost, SeasonTubesScroller, SeasonTubes, SeasonTubesBack, SeasonTubesNext, () => 0);
		FluentFin.Controls.EdgeScroller.HomeEndKeys(SeasonTubes);
		SizeChanged += (_, e) => Body.Padding = new Thickness(ItemStage.MarginFor(e.NewSize.Width), 8, ItemStage.MarginFor(e.NewSize.Width), 56);
		// Nixie 1.5: the seasons either side get their stills asked ahead, as the showtimes ask them
		FluentFin.Services.StillsAhead.Follow(this, episode => FluentFin.Core.ItemArtwork.ThumbPath(episode, Showtime.ThumbWidth));
	}

	/// <summary>The episode Play starts in this season (NEXT on the board, its time left beside Play).</summary>
	public static BaseItemDto? NextOf(List<BaseItemDto>? episodes) => DriveInItems.NextEpisode(episodes ?? [], null);

	protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading), primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		_place.Arrived(e);
	}

	protected override void OnNavigatingFrom(Microsoft.UI.Xaml.Navigation.NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}
}
