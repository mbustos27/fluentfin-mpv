using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

public partial class SeasonViewModel(IJellyfinClient jellyfinClient) : ObservableObject, INavigationAware
{
	/// <summary>fluentfin-mpv: this season could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this season");

	public Task OnNavigatedFrom()
	{
		ItemUserData.Changed -= OnUserDataChanged;
		return Task.CompletedTask;
	}

	/// <summary>
	/// 1.2.0: the season or one of its episodes was marked watched (or not), here or elsewhere: Play's target follows
	/// the server, and the episode list too when the season itself changed (all its episodes did).
	/// </summary>
	internal async void OnUserDataChanged(object? sender, ItemUserDataChangedEventArgs e)
	{
		try
		{
			if (e.Failed || Dto?.Id is not { } id)
			{
				return;
			}

			var season = e.ItemId == id;
			if (!season && Episodes?.Any(x => x.Id == e.ItemId) != true)
			{
				return;
			}

			if (season)
			{
				await UpdateEpisodes(Dto);
			}

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

		if (dto.Type is not BaseItemDto_Type.Season)
		{
			return;
		}

		ItemUserData.Changed -= OnUserDataChanged;
		ItemUserData.Changed += OnUserDataChanged;
		await State.RunAsync(async () =>
		{
			await UpdateSeason(id);
			if (Dto?.Id != id)
			{
				return LoadMessages.CouldNotLoad("this season");
			}

			// 1.2.0: asked alongside the episodes, so the page does not wait for it
			var play = UpdatePlayTarget(id);
			await UpdateEpisodes(dto);
			await UpdateSeasonPills();
			await play;
			return LoadOutcome.Loaded;
		});
	}

	[ObservableProperty]
	public partial BaseItemDto Dto { get; set; }

	[ObservableProperty]
	public partial List<BaseItemDto> Episodes { get; set; }

	/// <summary>
	/// 1.2.0: what Play starts, on the button (<see cref="ItemPlay"/>): the series' first episode not watched, which
	/// can be in another season, as both players start it.
	/// </summary>
	[ObservableProperty]
	public partial PlayTarget PlayTarget { get; set; } = PlayTarget.Plain;

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


	// the series' episodes as Play reads them (MpvQueue): one request without images; "Play" alone when it fails
	private async Task UpdatePlayTarget(Guid seasonId, bool reset = true)
	{
		if (reset)
		{
			PlayTarget = PlayTarget.Plain;
		}

		if (Dto?.SeriesId is not { } seriesId)
		{
			return;
		}

		var episodes = await jellyfinClient.GetSeriesEpisodes(seriesId);
		if (Dto?.Id == seasonId)
		{
			PlayTarget = ItemPlay.For(Dto, episodes?.Items);
		}
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
