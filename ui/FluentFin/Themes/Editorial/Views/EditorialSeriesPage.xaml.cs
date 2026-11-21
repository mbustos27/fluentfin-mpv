using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Editorial.Views;

/// <summary>The Editorial theme's series page (see EditorialSeriesPage.xaml), over the shared <see cref="SeriesViewModel"/>.</summary>
public sealed partial class EditorialSeriesPage : Page
{
	public SeriesViewModel ViewModel { get; } = App.GetService<SeriesViewModel>();

	private ItemPagePlace? _place;
	private bool _refreshQueued;

	public EditorialSeriesPage()
	{
		InitializeComponent();
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(SeriesViewModel.Seasons) or nameof(SeriesViewModel.SelectedSeason) or nameof(SeriesViewModel.Dto)
				or nameof(SeriesViewModel.PlayTarget) or nameof(SeriesViewModel.Episodes))
			{
				QueueRefresh();
			}
		};
		SizeChanged += (_, e) => Layout(e.NewSize.Width);
		Loaded += (_, _) =>
		{
			Layout(ActualWidth);
			QueueRefresh();
		};
	}

	/// <summary>The Seasons panel follows the view model (one refresh for a burst of changes; the episodes are kept, no request).</summary>
	private void QueueRefresh()
	{
		if (_refreshQueued)
		{
			return;
		}

		_refreshQueued = true;
		DispatcherQueue.TryEnqueue(async () =>
		{
			_refreshQueued = false;
			try
			{
				await ViewModel.EditorialRefresh();
			}
			catch (Exception)
			{
				// the panel keeps what it showed
			}
		});
	}

	private void Layout(double width)
	{
		if (width <= 0)
		{
			return;
		}

		var narrow = width < 1400;
		Header.IsNarrow = narrow;
		Body.Margin = new Thickness(narrow ? 40 : 56, 8, narrow ? 40 : 56, 56);
		SeasonsPanel.Width = narrow ? double.NaN : Math.Clamp(width * 0.29, 480, 560);
		SimilarShelf.IsNarrow = narrow;
	}

	private void SeasonsPanel_SeasonPicked(object? sender, BaseItemDto season) => ViewModel.SelectSeasonCommand.Execute(season);

	private void Header_CastRequested(object? sender, EventArgs e) =>
		Cast.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.2, AnimationDesired = FluentFin.Controls.SafeMotion.Allowed });

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		// Back gives the scroll position, the focus and the season shown back (the shared page's ItemPagePlace)
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading) && !ViewModel.IsLoadingEpisodes, primary: () => PlayButton); // 1.5 huntfix (BH-150-M3): Play takes the keyboard on arrival, as on a film's page
		ViewModel.ReturnTo(_place.Arrived(e)?.SelectedSeasonId);
	}

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		_place?.Left(ViewModel.SelectedSeason?.Id);
		base.OnNavigatingFrom(e);
	}

	private void SimilarSeeAll(object? sender, EventArgs e) =>
		App.GetService<FluentFin.Contracts.Services.INavigationService>().NavigateTo<ItemGridViewModel>(new ItemGridRequest("More Like This", [.. ViewModel.Similar]));

	public static string SeasonTitle(BaseItemDto? season) =>
		season is null ? "" : string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.IndexNumber}" : season.Name.Trim();

	public static Visibility Shown(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
