using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public partial class SeasonViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	/// <summary>fluentfin-mpv: this season could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this season");

	public Task OnNavigatedFrom() => Task.CompletedTask;

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

		if (dto.Type is not BaseItemDto_Type.Season)
		{
			return;
		}

		await State.RunAsync(async () =>
		{
			await UpdateSeason(id);
			if (Dto?.Id != id)
			{
				return LoadMessages.CouldNotLoad("this season");
			}

			await UpdateEpisodes(dto);
			await UpdateSeasonPills();
			return LoadOutcome.Loaded;
		});
	}

	[ObservableProperty]
	public partial BaseItemDto Dto { get; set; }

	[ObservableProperty]
	public partial List<BaseItemDto> Episodes { get; set; }

	/// <summary>Seaglass: the series' seasons, as the picker of glass pills; empty for a series of one season.</summary>
	[ObservableProperty]
	public partial List<SeasonPill> SeasonPills { get; set; } = [];

	// Seaglass: a series that cannot be listed leaves the picker out; the season itself is still shown
	private async Task UpdateSeasonPills()
	{
		if (Dto?.SeriesId is not { } seriesId || Dto.Id is not { } id)
		{
			SeasonPills = [];
			return;
		}

		var response = await jellyfinClient.GetItems(new BaseItemDto { Id = seriesId, Type = BaseItemDto_Type.Series });
		SeasonPills = ItemSiblings.SeasonPills(response?.Items, id);
	}


	private async Task UpdateSeason(Guid id)
	{
		var series = await jellyfinClient.GetItem(id);
		if (series is null)
		{
			return;
		}

		Dto = series;
	}

	private async Task UpdateEpisodes(BaseItemDto dto)
	{
		var response = await jellyfinClient.GetItems(dto);

		if (response is null or { Items: null })
		{
			return;
		}

		Episodes = response.Items;
	}
}
