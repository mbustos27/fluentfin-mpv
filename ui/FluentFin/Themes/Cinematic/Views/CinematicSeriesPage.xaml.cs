using FluentFin.Controls;
using FluentFin.Core;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>Cinematic's series page (see CinematicSeriesPage.xaml), over the shared <see cref="SeriesViewModel"/>.</summary>
public sealed partial class CinematicSeriesPage : Page
{
	public SeriesViewModel ViewModel { get; } = App.GetService<SeriesViewModel>();

	private ItemPagePlace? _place;
	private readonly CinematicItemPageLayout _layout;

	// Nixie 1.5: the episode stills' decoded width in the template shown, which the seasons either side are asked ahead at
	private int _stillWidth;

	public CinematicSeriesPage()
	{
		CinematicResources.Ensure();
		InitializeComponent();
		_layout = new CinematicItemPageLayout(this, PageContent, Stage, narrow =>
		{
			var side = narrow ? 48 : 96;
			TabsRow.Margin = new Thickness(side - 8, 0, 48, 6);
			EpisodeShelf.Margin = new Thickness(side - (narrow ? 12 : 16), 0, 0, 0);
			EpisodeShelf.ItemTemplate = (DataTemplate)Resources[narrow ? "EpisodeSmall" : "EpisodeLarge"];
			_stillWidth = narrow ? CinematicSeriesSupport.SmallStillWidth : CinematicSeriesSupport.LargeStillWidth;
			CinematicShelf.HideHeader(EpisodeShelf);
			Body.Margin = new Thickness(side, 24, 48, 56);
		});
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(SeriesViewModel.SeasonPills) or nameof(SeriesViewModel.SelectedSeason))
			{
				ShowTabs();
			}
		};
		ShowTabs();
		// Nixie 1.5: the seasons either side get their stills asked ahead, as the stills ask them (the template's width)
		FluentFin.Services.StillsAhead.Follow(this, episode => _stillWidth > 0 ? CinematicArtwork.StillPath(episode, _stillWidth, episodeOwnFirst: true) : null);
	}

	public static string KickerFor(BaseItemDto? dto, List<BaseItemViewModel>? seasons) =>
		CinematicLabels.ItemKicker(dto, seasons?.Count(s => s.Dto.IndexNumber is not 0) ?? 0);

	/// <summary>"44 min left" for the episode in progress on the season shown (what Resume continues), else nothing.</summary>
	public static string LeftFor(List<BaseItemDto>? episodes) =>
		episodes?.FirstOrDefault(e => e.UserData?.Played != true && CinematicLabels.TimeLeft(e) is not null) is { } episode ? CinematicLabels.TimeLeft(episode)! : "";

	/// <summary>The seasons as tabs (a show of one season has none: the summary says what there is).</summary>
	private void ShowTabs()
	{
		var pills = ViewModel.SeasonPills ?? [];
		SeasonTabs.SetTabs([.. pills.Select(p => new CinematicTab(p.Name, p.AutomationName, p.IsCurrent, p.Dto))]);
		SeasonTabs.Visibility = pills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	private void SeasonTabs_TabInvoked(object? sender, CinematicTab tab)
	{
		if (tab.Payload is BaseItemDto season && !tab.IsSelected)
		{
			ViewModel.SelectSeasonCommand.Execute(season);
		}
	}

	private void EpisodeShelf_Loaded(object sender, RoutedEventArgs e) => CinematicShelf.HideHeader(EpisodeShelf);

	private void SimilarShelf_Loaded(object sender, RoutedEventArgs e) => CinematicShelf.Fit(SimilarShelf, 12, null);

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading) && !ViewModel.IsLoadingEpisodes, primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		ViewModel.ReturnTo(_place.Arrived(e)?.SelectedSeasonId);
	}

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		_place?.Left(ViewModel.SelectedSeason?.Id);
		base.OnNavigatingFrom(e);
	}

	private void SimilarSeeAll(object sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));
}
