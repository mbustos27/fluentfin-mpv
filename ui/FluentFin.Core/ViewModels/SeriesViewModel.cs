using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentFin.Contracts.ViewModels;
using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <param name="seriesEpisodes">1.2.0: the app's kept episodes for Play's label (SeriesEpisodes); a page of its own when
/// none is given.</param>
public partial class SeriesViewModel(IJellyfinClient jellyfinClient, SeriesEpisodes? seriesEpisodes = null) : ObservableObject, INavigationAware
{
	private readonly SeriesEpisodes _seriesEpisodes = seriesEpisodes ?? SeriesEpisodes.ForOnePage(jellyfinClient);

	// 1.2.0 (items review, item 3): only the newest of overlapping refreshes applies (Latest)
	private readonly Latest _item = new();
	private readonly Latest _list = new();
	private readonly Latest _play = new();

	// 1.2.0 (items review, item 1): the season the page showed when the user left it, for Back (ReturnTo)
	private Guid? _returnSeasonId;

	// the season of the episode Play starts (ItemPlay), which the list opens on
	private Guid? _startSeasonId;

	/// <summary>fluentfin-mpv: this series could not be loaded: said on the page, with Retry (docs/research/ux-review.md §3.11).</summary>
	public LoadState State { get; } = new("this series");

	// Nixie 1.5 (season stream, review m3): the page was left: a season answering late asks nothing ahead (it took over the
	// shown page's asking ahead)
	private bool _left;

	public Task OnNavigatedFrom()
	{
		_left = true;
		ItemUserData.Changed -= OnUserDataChanged;
		return Task.CompletedTask;
	}

	/// <summary>
	/// 1.2.0: the series, one of its seasons or one of its episodes was marked watched (or not), here or elsewhere. The page
	/// shows the server's state again: the series itself (its Watched toggle: unmarking one episode unmarks the series; items
	/// review, item 2), the seasons and the shown list when a whole season or the series changed, and Play's label, from the
	/// kept episodes changed in place (no new download; item 6).
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
			var series = e.ItemId == id;
			var season = Seasons?.Any(x => x.Dto.Id == e.ItemId) == true;
			var episode = Episodes.Any(x => x.Id == e.ItemId) || NextUp?.Dto.Id == e.ItemId || _seriesEpisodes.Contains(id, e.ItemId);
			if (!series && !season && !episode)
			{
				return;
			}

			_seriesEpisodes.Apply(e);
			await Task.Yield();
			var play = UpdatePlayTarget(id, reset: false);
			await UpdateSeries(id);
			if (series || season)
			{
				await UpdateSeasons(Dto);
			}

			// the series or the season shown: all of its episodes changed
			if ((series || e.ItemId == SelectedSeason?.Id) && SelectedSeason is { } shown)
			{
				await ShowSeason(shown, fresh: true); // as the server has it now (not the season's kept list)
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

		if (dto.Type is not BaseItemDto_Type.Series)
		{
			return;
		}

		ItemUserData.Changed -= OnUserDataChanged;
		ItemUserData.Changed += OnUserDataChanged;
		_left = false;
		await State.RunAsync(async () =>
		{
			// Nixie 1.4.1: the seasons, the series' episodes (Play's label) and More like this need only the series' id: asked
			// with the series itself (each waited for the one before; More like this, often the slowest, came last of all).
			// Shown in the same order.
			var seasons = JellyfinClient.GetItems(dto);
			var similar = JellyfinClient.GetSimilarItems(dto);
			_ = _seriesEpisodes.Get(id); // shared with UpdatePlayTarget's (SeriesEpisodes keeps one request per series)

			await UpdateSeries(id);
			if (Dto?.Id != id)
			{
				return LoadMessages.CouldNotLoad("this series");
			}

			// 1.2.0: asked alongside the seasons; the list opens on the season Play starts in, so Play's label and the list agree
			// (the server's Next Up, the episode after the last one watched, only when the episodes cannot be read)
			var play = UpdatePlayTarget(id);
			await UpdateSeasons(dto, seasons);
			await play;
			if (_startSeasonId is null)
			{
				await UpdateNextUp(dto);
			}

			await ShowSeason(ReturnSeason() ?? FirstSeason());
			await UpdateSimilar(dto, similar);
			return LoadOutcome.Loaded;
		});
	}

	/// <summary>
	/// 1.2.0 (items review, item 1): the page is shown again by Back or Forward, and showed this season when the user left it:
	/// the list opens on it again, not on Next Up's season. The page says so as it arrives; when the seasons are in already,
	/// the list switches to it at once.
	/// </summary>
	public void ReturnTo(Guid? seasonId)
	{
		_returnSeasonId = seasonId;
		if (ReturnSeason() is { } season && SelectedSeason?.Id != season.Id)
		{
			_ = ShowSeason(season);
		}
	}

	private BaseItemDto? ReturnSeason() =>
		_returnSeasonId is { } id ? Seasons?.Select(x => x.Dto).FirstOrDefault(s => s.Id == id) : null;

	[ObservableProperty]
	public partial BaseItemDto Dto { get; set; }

	[ObservableProperty]
	public partial List<BaseItemViewModel> Similar { get; set; }

	[ObservableProperty]
	public partial List<BaseItemViewModel> Seasons { get; set; }

	[ObservableProperty]
	public partial BaseItemViewModel NextUp { get; set; }

	/// <summary>1.2.0: what Play starts, on the button ("Resume S1 E4", "Play S2 E1", "Play from the start"; <see cref="ItemPlay"/>).</summary>
	[ObservableProperty]
	public partial PlayTarget PlayTarget { get; set; } = PlayTarget.Plain;

	public IJellyfinClient JellyfinClient { get; } = jellyfinClient;

	private async Task UpdateSeries(Guid id)
	{
		var ticket = _item.Next();
		var series = await JellyfinClient.GetItem(id);
		if (series is null || !_item.Is(ticket))
		{
			return;
		}

		Dto = series;
	}

	// the series' episodes as Play reads them (MpvQueue), kept (SeriesEpisodes); "Play" alone when they cannot be loaded
	private async Task UpdatePlayTarget(Guid seriesId, bool reset = true)
	{
		var ticket = _play.Next();
		if (reset)
		{
			PlayTarget = PlayTarget.Plain;
		}

		var episodes = await _seriesEpisodes.Get(seriesId);
		if (_play.Is(ticket) && Dto?.Id == seriesId)
		{
			PlayTarget = ItemPlay.For(Dto, episodes);
			_startSeasonId = episodes is { Count: > 0 } ? episodes[ItemPlay.StartIndex(episodes)].SeasonId : null;
		}
	}

	/// <summary>
	/// 1.2.0 (1.1.3 UX audit, layout item 9): the series page lists episodes, as Netflix and Apple TV do: one season's,
	/// with the season picker above them (<see cref="SeasonPills"/>, empty for a series of one season), opened on the season
	/// Play starts in (<see cref="FirstSeason"/>). A pill switches the list in place (no page, no history entry).
	/// </summary>
	[ObservableProperty]
	public partial List<SeasonPill> SeasonPills { get; set; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(SeasonDetailsText), nameof(HasEpisodeSection))]
	public partial BaseItemDto? SelectedSeason { get; set; }

	/// <summary>
	/// 1.2.0: the link to the shown season's own page (its overview, marking the whole season watched), next to the picker:
	/// "Season 2 details". The pills switch the list in place, so this is the series page's way to a season page.
	/// </summary>
	public string SeasonDetailsText => SelectedSeason?.Name is { Length: > 0 } name ? $"{name.Trim()} details" : "Season details";

	[ObservableProperty]
	public partial List<BaseItemDto> Episodes { get; set; } = [];

	/// <summary>
	/// 1.2.0 (items review, item 4): the Episodes section shows while there is a season to show, its picker too, even when
	/// that season's episodes could not be loaded or it has none (<see cref="EpisodesMessage"/> says which).
	/// </summary>
	public bool HasEpisodeSection => SelectedSeason is not null;

	/// <summary>The list area's words instead of the episodes: the shown season's could not be loaded, or it has none; else "".</summary>
	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(HasEpisodesMessage))]
	public partial string EpisodesMessage { get; set; } = "";

	public bool HasEpisodesMessage => EpisodesMessage.Length > 0;

	/// <summary>The shown season's episodes could not be loaded: the list area offers Try again.</summary>
	[ObservableProperty]
	public partial bool EpisodesFailed { get; set; }

	/// <summary>A season's episodes are on their way (the page keeps its place until they are in).</summary>
	[ObservableProperty]
	public partial bool IsLoadingEpisodes { get; set; }

	/// <summary>
	/// The season the list opens on: the one Play starts in (the series' first episode not watched, as Play's label says),
	/// else, when the episodes could not be read, the server's Next Up's, else the first.
	/// </summary>
	internal BaseItemDto? FirstSeason()
	{
		var seasons = Seasons?.Select(x => x.Dto).ToList() ?? [];
		return seasons.FirstOrDefault(s => s.Id is not null && s.Id == _startSeasonId)
			?? seasons.FirstOrDefault(s => s.Id is not null && s.Id == NextUp?.Dto.SeasonId)
			?? seasons.FirstOrDefault(s => s.IndexNumber == NextUp?.Dto.ParentIndexNumber && NextUp?.Dto.ParentIndexNumber is not null)
			?? seasons.OrderBy(s => s.IndexNumber is 0 ? int.MaxValue : s.IndexNumber ?? int.MaxValue).FirstOrDefault();
	}

	// Nixie 1.4.1: concurrent, so the pills stay enabled while a season loads: a quick second click was dropped (the buttons
	// bound to the command were disabled until the first answer came), and the latest click wins (ShowSeason's ticket)
	[RelayCommand(AllowConcurrentExecutions = true)]
	private Task SelectSeason(BaseItemDto? season) => season is null ? Task.CompletedTask : ShowSeason(season);

	[RelayCommand]
	private Task RetrySeason() => ShowSeason(SelectedSeason);

	private async Task ShowSeason(BaseItemDto? season, bool fresh = false)
	{
		var ticket = _list.Next();
		if (season?.Id is not { } id)
		{
			SelectedSeason = null;
			SeasonPills = [];
			Episodes = [];
			EpisodesMessage = "";
			EpisodesFailed = false;
			IsLoadingEpisodes = false;
			return;
		}

		SelectedSeason = season;
		SeasonPills = ItemSiblings.SeasonPills(Seasons?.Select(x => x.Dto), id);
		IsLoadingEpisodes = true;
		// Nixie 1.4.1: kept per season (SeriesEpisodes.GetList): a season seen or asked ahead shows at once; a page's refresh
		// after a change asks again (fresh). The seasons either side are asked now, so the next click shows at once too.
		var response = await _seriesEpisodes.GetList(season, fresh);
		if (!_list.Is(ticket))
		{
			return; // a later click: its own neighbours are asked ahead, not these
		}

		var name = season.Name?.Trim() is { Length: > 0 } n ? n : "This season";
		Episodes = response?.Items ?? [];
		EpisodesFailed = response?.Items is null;
		EpisodesMessage = EpisodesFailed ? $"Couldn't load the episodes of {name}." : Episodes.Count == 0 ? $"{name} has no episodes yet." : "";
		IsLoadingEpisodes = false;
		// shown first, then the seasons either side asked ahead (Nixie 1.5: then a small show's others, in turn), unless the page
		// was left meanwhile
		if (!_left)
		{
			_seriesEpisodes.PrefetchSeasons(Seasons?.Select(x => x.Dto), id);
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

	private async Task UpdateSimilar(BaseItemDto dto, Task<BaseItemDtoQueryResult?>? asked = null)
	{
		var response = await (asked ?? JellyfinClient.GetSimilarItems(dto));

		if (response is null or { Items: null })
		{
			return;
		}

		Similar = [.. response.Items.Select(BaseItemViewModel.FromDto)];
	}

	private async Task UpdateSeasons(BaseItemDto dto, Task<BaseItemDtoQueryResult?>? asked = null)
	{
		var response = await (asked ?? JellyfinClient.GetItems(dto));

		if (response is null or { Items: null })
		{
			return;
		}

		Seasons = [.. response.Items.Select(BaseItemViewModel.FromDto)];
		if (SelectedSeason?.Id is { } shown)
		{
			SeasonPills = ItemSiblings.SeasonPills(Seasons.Select(x => x.Dto), shown);
		}
	}
}
