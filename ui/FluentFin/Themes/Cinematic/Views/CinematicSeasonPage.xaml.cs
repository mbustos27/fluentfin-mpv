using FluentFin.Core.ViewModels;
using FluentFin.Themes.Cinematic.Controls;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Cinematic.Views;

/// <summary>Cinematic's season page (see CinematicSeasonPage.xaml), over the shared <see cref="SeasonViewModel"/>.</summary>
public sealed partial class CinematicSeasonPage : Page
{
	public SeasonViewModel ViewModel { get; } = App.GetService<SeasonViewModel>();

	private ItemPagePlace? _place;
	private readonly CinematicItemPageLayout _layout;

	// Nixie 1.5: the episode stills' decoded width in the template shown, which the seasons either side are asked ahead at
	private int _stillWidth;

	public CinematicSeasonPage()
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
		// Nixie 1.7.4: only while the page is in the window (Back hands a kept view model to a new page: WhileLoaded)
		FluentFin.Services.WhileLoaded.Listen(this, ViewModel, (_, e) =>
		{
			if (e.PropertyName is nameof(SeasonViewModel.SeasonPills))
			{
				ShowTabs();
			}
		}, ShowTabs);
		ShowTabs();
		// Nixie 1.5: the seasons either side get their stills asked ahead, as the stills ask them (the template's width)
		FluentFin.Services.StillsAhead.Follow(this, episode => _stillWidth > 0 ? CinematicArtwork.StillPath(episode, _stillWidth, episodeOwnFirst: true) : null);
	}

	private void ShowTabs()
	{
		var pills = ViewModel.SeasonPills ?? [];
		SeasonTabs.SetTabs([.. pills.Select(p => new CinematicTab(p.Name, p.AutomationName, p.IsCurrent, p.Dto))]);
		SeasonTabs.Visibility = pills.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
	}

	// another season: its own page, as the shared season pills (the navigation keeps one season page in Back's way)
	private void SeasonTabs_TabInvoked(object? sender, CinematicTab tab)
	{
		if (tab.Payload is BaseItemDto season && !tab.IsSelected)
		{
			App.Commands.DisplayDto(season);
		}
	}

	private void EpisodeShelf_Loaded(object sender, RoutedEventArgs e) => CinematicShelf.HideHeader(EpisodeShelf);

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading), primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		_place.Arrived(e);
	}

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}
}
