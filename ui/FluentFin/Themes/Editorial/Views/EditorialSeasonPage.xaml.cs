using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using FluentFin.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FluentFin.Themes.Editorial.Views;

/// <summary>The Editorial theme's season page (see EditorialSeasonPage.xaml), over the shared <see cref="SeasonViewModel"/>.</summary>
public sealed partial class EditorialSeasonPage : Page
{
	public SeasonViewModel ViewModel { get; } = App.GetService<SeasonViewModel>();

	private ItemPagePlace? _place;
	private bool _refreshQueued;

	public EditorialSeasonPage()
	{
		InitializeComponent();
		ViewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName is nameof(SeasonViewModel.SeasonPills) or nameof(SeasonViewModel.Dto) or nameof(SeasonViewModel.PlayTarget) or nameof(SeasonViewModel.Episodes))
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
	}

	/// <summary>Another season of the series: its own page (as the shared season page's pills do).</summary>
	private void SeasonsPanel_SeasonPicked(object? sender, BaseItemDto season)
	{
		if (season.Id != ViewModel.Dto?.Id)
		{
			App.Commands.DisplayDto(season);
		}
	}

	private void Header_CastRequested(object? sender, EventArgs e) =>
		Cast.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.2, AnimationDesired = FluentFin.Controls.SafeMotion.Allowed });

	protected override void OnNavigatedTo(NavigationEventArgs e)
	{
		base.OnNavigatedTo(e);
		_place ??= new ItemPagePlace(this, PageContent, () => ViewModel.State.Status is not (LoadStatus.Idle or LoadStatus.Loading));
		_place.Arrived(e);
	}

	protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
	{
		_place?.Left();
		base.OnNavigatingFrom(e);
	}

	public static string SeasonTitle(BaseItemDto? season) =>
		season is null ? "" : string.IsNullOrWhiteSpace(season.Name) ? $"Season {season.IndexNumber}" : season.Name.Trim();
}
