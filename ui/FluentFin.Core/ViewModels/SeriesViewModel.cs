using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public partial class SeriesViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	/// <summary>fluentfin-mpv: this series could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this series");

	public Task OnNavigatedFrom()
	{
		ItemUserData.Changed -= OnUserDataChanged;
		return Task.CompletedTask;
	}

	/// <summary>
	/// 1.2.0: the series, one of its seasons or its next episode was marked watched (or not) here or on a card: Play's
	/// target and Next Up follow the server, and the season cards too when the series itself changed (all its episodes did).
	/// </summary>
	internal async void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		try
		{
			if (e.Failed || Dto?.Id is not { } id)
			{
				return;
			}

			var series = e.ItemId == id;
			var season = Seasons?.Any(x => x.Dto.Id == e.ItemId) == true;
			if (!series && !season && NextUp?.Dto.Id != e.ItemId && Episodes?.Any(x => x.Id == e.ItemId) != true)
			{
				return;
			}

			var dto = Dto;
			if (series)
			{
				await UpdateSeasons(dto);
			}

			// the series or the season shown: all of its episodes changed
			if ((series || e.ItemId == SelectedSeason?.Id) && SelectedSeason is { } shown)
			{
				await ShowSeason(shown);
			}

			await UpdateNextUp(dto);
			await UpdatePlayTarget(id, reset: false);
		}
		catch (Exception)
		{
			// a refresh that fails leaves the page as it was
		}
	}

	public async Task OnNavigatedTo(object parameter)
	{
		if (parameter is not BaseItemDto dto)
		{
			return;
		}

		if (dto.Id is not { } id)
		{
			return;
		}

		if (dto.Type is not BaseItemDto_Type.Series)
		{
			return;
		}

		ItemUserData.Changed -= OnUserDataChanged;
		ItemUserData.Changed += OnUserDataChanged;
		await State.RunAsync(async () =>
		{
			await UpdateSeries(id);
			if (Dto?.Id != id)
			{
				return LoadMessages.CouldNotLoad("this series");
			}

			// 1.2.0: asked alongside the rows, so the page does not wait for it
			var play = UpdatePlayTarget(id);
			await UpdateNextUp(dto);
			await UpdateSeasons(dto);
			await ShowSeason(FirstSeason());
			await UpdateSimilar(dto);
			await play;
			return LoadOutcome.Loaded;
		});
	}

	[ObservableProperty]
	public partial BaseItemDto Dto { get; set; }

	[ObservableProperty]
	public partial List<BaseItemViewModel> Similar { get; set; }

	[ObservableProperty]
	public partial List<BaseItemViewModel> Seasons { get; set; }

	[ObservableProperty]
	public partial BaseItemViewModel NextUp { get; set; }

	/// <summary>1.2.0: what Play starts, on the button ("Resume S1E4", "Play S2E1", "Play from the start"; <see cref="ItemPlay"/>).</summary>
	[ObservableProperty]
	public partial PlayTarget PlayTarget { get; set; } = PlayTarget.Plain;

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	private async Task UpdateSeries(Guid id)
	{
		var series = await JellyfinClient.GetItem(id);
		if (series is null)
		{
			return;
		}

		Dto = series;

	}

	// the series' episodes as Play reads them (MpvQueue): one request without images; "Play" alone when it fails
	private async Task UpdatePlayTarget(Guid seriesId, bool reset = true)
	{
		if (reset)
		{
			PlayTarget = PlayTarget.Plain;
		}

		var episodes = await JellyfinClient.GetSeriesEpisodes(seriesId);
		if (Dto?.Id == seriesId)
		{
			PlayTarget = ItemPlay.For(Dto, episodes?.Items);
		}
	}

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, layout item 9): the series page lists episodes, as Netflix and Apple TV do: one season's,
	/// with the season picker above them (<see cref="SeasonPills"/>, empty for a series of one season), opened on the season
	/// of the next episode to watch. A pill switches the list in place (no page, no history entry).
	/// </summary>
	[ObservableProperty]
	public partial List<SeasonPill> SeasonPills { get; set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SeasonDetailsText))]
	public partial BaseItemDto? SelectedSeason { get; set; }

	/// <summary>
	/// 1.2.0: the link to the shown season's own page (its overview, marking the whole season watched), next to the picker:
	/// "Season 2 details". The pills switch the list in place, so this is the series page's way to a season page.
	/// </summary>
	public string SeasonDetailsText => SelectedSeason?.Name is { Length: > 0 } name ? $"{name.Trim()} details" : "Season details";

	[ObservableProperty]
	public partial List<BaseItemDto> Episodes { get; set; } = [];

	/// <summary>The season of the next episode to watch (Next Up), else the first.</summary>
	internal BaseItemDto? FirstSeason()
	{
		var seasons = Seasons?.Select(x => x.Dto).ToList() ?? [];
		return seasons.FirstOrDefault(s => s.Id is not null && s.Id == NextUp?.Dto.SeasonId)
			?? seasons.FirstOrDefault(s => s.IndexNumber == NextUp?.Dto.ParentIndexNumber && NextUp?.Dto.ParentIndexNumber is not null)
			?? seasons.OrderBy(s => s.IndexNumber is 0 ? int.MaxValue : s.IndexNumber ?? int.MaxValue).FirstOrDefault();
	}

	[RelayCommand]
	private Task SelectSeason(BaseItemDto? season) => season is null ? Task.CompletedTask : ShowSeason(season);

	private async Task ShowSeason(BaseItemDto? season)
	{
		if (season?.Id is not { } id)
		{
			SeasonPills = [];
			Episodes = [];
			return;
		}

		SelectedSeason = season;
		SeasonPills = ItemSiblings.SeasonPills(Seasons?.Select(x => x.Dto), id);
		var response = await JellyfinClient.GetItems(season);
		if (SelectedSeason?.Id == id)
		{
			Episodes = response?.Items ?? [];
		}
	}

	private async Task UpdateNextUp(BaseItemDto dto)
	{
		var response = await JellyfinClient.GetNextUp(dto);

		if (response is null or { Items: null } or { Items.Count: 0 })
		{
			return;
		}

		NextUp = BaseItemViewModel.FromDto(response.Items[0]);
	}

	private async Task UpdateSimilar(BaseItemDto dto)
	{
		var response = await JellyfinClient.GetSimilarItems(dto);

		if (response is null or { Items: null })
		{
			return;
		}

		Similar = [.. response.Items.Select(BaseItemViewModel.FromDto)];
	}

	private async Task UpdateSeasons(BaseItemDto dto)
	{
		var response = await JellyfinClient.GetItems(dto);

		if (response is null or { Items: null })
		{
			return;
		}

		Seasons = [.. response.Items.Select(BaseItemViewModel.FromDto)];
	}
}
