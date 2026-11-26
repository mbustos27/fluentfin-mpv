using CommunityToolkit.Mvvm.ComponentModel;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <param name="seriesEpisodes">1.2.0: the app's kept episodes for Play's label (SeriesEpisodes); a page of its own when
/// none is given.</param>
public partial class SeasonViewModel(IJellyfinClient jellyfinClient, SeriesEpisodes? seriesEpisodes = null) : ObservableObject, INavigationAware, IKeptOnBack
{
	private readonly SeriesEpisodes _seriesEpisodes = seriesEpisodes ?? SeriesEpisodes.ForOnePage(jellyfinClient);

	// 1.2.0 (items review, item 3): only the newest of overlapping refreshes applies (Latest)
	private readonly Latest _item = new();
	private readonly Latest _list = new();
	private readonly Latest _play = new();

	// Nixie 1.5 (season stream, review m2, m3): the newest load (a Retry, or the page opened again, replaces an older one: only
	// the newest may clear the page or ask ahead), and whether the page was left (a left page asks nothing ahead)
	private readonly Latest _load = new();
	private bool _left;

	/// <summary>fluentfin-mpv: this season could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this season");

	public Task OnNavigatedFrom()
	{
		_left = true;
		ItemUserData.Changed -= OnUserDataChanged;
		return Task.CompletedTask;
	}

	// ---- Nixie 1.7.4: Back shows the season's page as it was (IKeptOnBack) ----------------------------------------------------

	public bool CanKeep => State.IsLoaded && Dto is not null && Episodes is not null;

	/// <summary>
	/// Back or Forward shows the page as it was: its episodes, its pills, Play's label. When something may have changed since (an
	/// episode watched or marked, a play), the season and its episodes are asked again behind it: the same episodes keep their
	/// rows (a watch state shows on its row), the season's Watched toggle and Play's label follow. It listens to watch states
	/// again once that is done (its own refresh is not a change to answer).
	/// </summary>
	public async Task Returned(object parameter, bool stale)
	{
		_left = false;
		if (stale && Dto?.Id is { } id)
		{
			var shown = Dto;
			// 1.8.1 (BH-175-q2, the review): the episodes written only while this refresh is the newest (the season and Play's
			// label have their own tickets)
			await State.RefreshBehindAsync(async newest =>
			{
				var episodes = _seriesEpisodes.GetList(shown, fresh: true);
				var play = UpdatePlayTarget(id, reset: false);
				await UpdateSeason(id);
				if (await episodes is { Items: { } rows } && newest() && !KeptCards.RefreshRecords(Episodes ?? [], rows))
				{
					Episodes = [.. rows];
				}

				await play;
				return LoadOutcome.Loaded;
			});
		}

		// (not once the page was left meanwhile: a page not shown would hear every change for as long as the app runs)
		ItemUserData.Changed -= OnUserDataChanged;
		if (!_left)
		{
			ItemUserData.Changed += OnUserDataChanged;
		}
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

			// one episode of an Undo: the kept episodes follow; the page refreshes once, at the Undo's end (Mixed)
			if (e.Partial)
			{
				_seriesEpisodes.Apply(e);
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
		_left = false;
		await State.RunAsync(async () =>
		{
			var load = _load.Next();
			// Nixie 1.4.1: the episodes and the series' seasons (the pills) are asked with the season itself, from what the page
			// was opened with (each waited for the one before); shown in the same order. Play's label still starts once the
			// season is in: it reads the series the server says the season is in.
			var episodes = _seriesEpisodes.GetList(dto); // Nixie 1.4.1: kept per season (a season seen, or asked ahead, shows at once)
			var pills = dto.SeriesId is { } openedSeries ? _seriesEpisodes.GetList(new BaseItemDto { Id = openedSeries, Type = BaseItemDto_Type.Series }) : null;

			// Nixie 1.5: the page does not wait for its own item (one round trip) to show what it already has: the season it was
			// opened with (a pill's or a link's: its name, overview and pictures), and its episodes and the series' seasons as soon
			// as they are in (at once when kept: seen, or asked ahead). All of them together, so nothing above the rows grows over
			// them a moment later (the header, the picker); the server's season replaces the one opened with when it comes.
			if (Dto?.Id != id && !string.IsNullOrWhiteSpace(dto.Name))
			{
				Dto = dto;
			}

			var shownEpisodes = UpdateEpisodes(dto, episodes);
			var shownPills = pills is not null && Dto?.Id == id ? UpdateSeasonPills(pills, dto.SeriesId) : null;
			var found = await UpdateSeason(id);
			if (!_load.Is(load))
			{
				// a newer load (Retry, the page opened again) shows the page now: this one changes nothing (review m2: its
				// failure cleared a page the newer one had loaded)
				return LoadOutcome.Loaded;
			}

			if (!found || Dto?.Id != id)
			{
				// a page that cannot load its season shows only the message, as before: nothing under it
				_list.Next();
				Episodes = null!;
				SeasonPills = [];
				Dto = null!;
				return LoadMessages.CouldNotLoad("this season");
			}

			// 1.2.0: asked alongside the episodes, so the page does not wait for it
			var play = UpdatePlayTarget(id);
			await shownEpisodes;
			await (shownPills ?? UpdateSeasonPills(pills, dto.SeriesId));
			// Nixie 1.4.1: the seasons either side asked now (kept), so a pill to one of them opens its page with its episodes in;
			// Nixie 1.5: then, for a small show, the others in turn (SeriesEpisodes.PrefetchSeasons). Not from a page left, or a
			// load replaced, meanwhile (review m3: it took over the shown page's asking ahead)
			if (!_left && _load.Is(load))
			{
				_seriesEpisodes.PrefetchSeasons(SeasonPills.Select(p => p.Dto), id);
			}
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
	private async Task UpdateSeasonPills(Task<BaseItemDtoQueryResult?>? asked = null, Guid? askedFor = null)
	{
		if (Dto?.SeriesId is not { } seriesId || Dto.Id is not { } id)
		{
			SeasonPills = [];
			return;
		}

		// Nixie 1.4.1: the request started with the page's, when it asked for this series
		var response = await (asked is not null && askedFor == seriesId ? asked : _seriesEpisodes.GetList(new BaseItemDto { Id = seriesId, Type = BaseItemDto_Type.Series }));
		if (Dto?.Id != id)
		{
			return; // Nixie 1.5: the page's season could not be loaded meanwhile (it shows only the message)
		}

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

	// whether the server answered with the season (Nixie 1.5: the page may show the one it was opened with meanwhile)
	private async Task<bool> UpdateSeason(Guid id)
	{
		var ticket = _item.Next();
		var season = await jellyfinClient.GetItem(id);
		if (season is null)
		{
			return false;
		}

		if (_item.Is(ticket))
		{
			Dto = season;
		}

		return true;
	}

	private async Task UpdateEpisodes(BaseItemDto dto, Task<BaseItemDtoQueryResult?>? asked = null)
	{
		var ticket = _list.Next();
		var response = await (asked ?? _seriesEpisodes.GetList(dto, fresh: true)); // a refresh: as the server has it now

		if (response is null or { Items: null } || !_list.Is(ticket))
		{
			return;
		}

		Episodes = response.Items;
	}
}
