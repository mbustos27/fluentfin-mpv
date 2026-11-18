using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <param name="seriesEpisodes">1.2.0: the app's kept episodes for Play's label (SeriesEpisodes); a page of its own when
/// none is given.</param>
public partial class SeasonViewModel(IJellyfinClient jellyfinClient, SeriesEpisodes? seriesEpisodes = null) : ObservableObject, INavigationAware
{
	private readonly SeriesEpisodes _seriesEpisodes = seriesEpisodes ?? SeriesEpisodes.ForOnePage(jellyfinClient);

	// 1.2.0 (items review, item 3): only the newest of overlapping refreshes applies (Latest)
	private readonly Latest _item = new();
	private readonly Latest _list = new();
	private readonly Latest _play = new();

	/// <summary>fluentfin-mpv: this season could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this season");

	public Task OnNavigatedFrom()
	{
		ItemUserData.Changed -= OnUserDataChanged;
		return Task.CompletedTask;
	}

	/// <summary>
	/// 1.2.0: the season, its series or one of its episodes was marked watched (or not), here or elsewhere. The page shows the
	/// server's state again: the season itself (its Watched toggle: unmarking one episode of a watched season unmarks the
	/// season, so a second click must not unmark them all; items review, item 2), the episode list when the whole season
	/// changed, and Play's label, from the kept episodes changed in place (no new download; item 6).
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
			var series = Dto.SeriesId is { } seriesId && e.ItemId == seriesId;
			if (!season && !series && Episodes?.Any(x => x.Id == e.ItemId) != true)
			{
				return;
			}

			_seriesEpisodes.Apply(e);
			await Task.Yield();
			var play = UpdatePlayTarget(id, reset: false);
			await UpdateSeason(id);
			if (season || series)
			{
				await UpdateEpisodes(Dto);
			}

			await play;
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


	// the series' episodes as Play reads them (MpvQueue), kept (SeriesEpisodes); "Play" alone when they cannot be loaded
	private async Task UpdatePlayTarget(Guid seasonId, bool reset = true)
	{
		var ticket = _play.Next();
		if (reset)
		{
			PlayTarget = PlayTarget.Plain;
		}

		if (Dto?.SeriesId is not { } seriesId)
		{
			return;
		}

		var episodes = await _seriesEpisodes.Get(seriesId);
		if (_play.Is(ticket) && Dto?.Id == seasonId)
		{
			PlayTarget = ItemPlay.For(Dto, episodes);
		}
	}

	private async Task UpdateSeason(Guid id)
	{
		var ticket = _item.Next();
		var season = await jellyfinClient.GetItem(id);
		if (season is null || !_item.Is(ticket))
		{
			return;
		}

		Dto = season;
	}

	private async Task UpdateEpisodes(BaseItemDto dto)
	{
		var ticket = _list.Next();
		var response = await jellyfinClient.GetItems(dto);

		if (response is null or { Items: null } || !_list.Is(ticket))
		{
			return;
		}

		Episodes = response.Items;
	}
}
