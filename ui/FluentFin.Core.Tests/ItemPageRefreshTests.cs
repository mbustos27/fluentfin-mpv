using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (items review): the series and season pages after a change. Back shows the season the user picked (item 1); the
/// page's own Watched follows its episodes (item 2); only the newest of overlapping refreshes applies (item 3); a season
/// that cannot be listed keeps its picker and says so (item 4); Play's label comes from kept episodes, not a new download
/// per page and per toggle (item 6).
/// </summary>
public sealed class ItemPageRefreshTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _seasons;
	private readonly Dictionary<Guid, List<BaseItemDto>> _episodes;
	private readonly Dictionary<Guid, Queue<TaskCompletionSource<BaseItemDtoQueryResult?>>> _slow = [];
	private BaseItemDto _seriesOnServer;
	private readonly Dictionary<Guid, BaseItemDto> _seasonOnServer = [];

	public ItemPageRefreshTests()
	{
		_seriesOnServer = new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Lost", UserData = new UserItemDataDto { Played = false } };
		_seasons = [.. Enumerable.Range(1, 3).Select(n => new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = $"Season {n}", IndexNumber = n, SeriesId = SeriesId, UserData = new UserItemDataDto { Played = false } })];
		_episodes = _seasons.ToDictionary(s => s.Id!.Value, s => Enumerable.Range(1, 4).Select(n => new BaseItemDto
		{
			Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = SeriesId, SeasonId = s.Id, ParentIndexNumber = s.IndexNumber, IndexNumber = n,
			Name = $"S{s.IndexNumber} episode {n}", UserData = new UserItemDataDto { Played = false },
		}).ToList());
		foreach (var season in _seasons)
		{
			_seasonOnServer[season.Id!.Value] = season;
		}

		_client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(() => _seriesOnServer);
		_client.Setup(x => x.GetItem(It.Is<Guid>(id => _seasonOnServer.ContainsKey(id)))).ReturnsAsync((Guid id) => _seasonOnServer[id]);
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == SeriesId), It.IsAny<bool>())).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = _seasons });
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id != SeriesId), It.IsAny<bool>()))
			.Returns((BaseItemDto d, bool _) => _slow.TryGetValue(d.Id!.Value, out var queue) && queue.Count > 0
				? queue.Dequeue().Task
				: Task.FromResult<BaseItemDtoQueryResult?>(new BaseItemDtoQueryResult { Items = _episodes[d.Id!.Value] }));
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [.. _episodes.Values.SelectMany(x => x).Select(Copy)] });
		_client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.SetPlayed(It.IsAny<BaseItemDto>(), It.IsAny<bool>()))
			.ReturnsAsync((BaseItemDto d, bool played) => new UserItemDataDto { Played = played, PlaybackPositionTicks = 0 });
	}

	private static BaseItemDto Copy(BaseItemDto e) => new()
	{
		Id = e.Id, Type = e.Type, SeriesId = e.SeriesId, SeasonId = e.SeasonId, ParentIndexNumber = e.ParentIndexNumber, IndexNumber = e.IndexNumber, Name = e.Name,
		UserData = new UserItemDataDto { Played = e.UserData?.Played, PlaybackPositionTicks = e.UserData?.PlaybackPositionTicks },
	};

	private GlobalCommands Commands() => new(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());

	private async Task<SeriesViewModel> OpenSeries(Action<SeriesViewModel>? before = null, SeriesEpisodes? kept = null)
	{
		var vm = new SeriesViewModel(_client.Object, kept);
		before?.Invoke(vm);
		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });
		return vm;
	}

	private async Task<SeasonViewModel> OpenSeason(BaseItemDto season)
	{
		var vm = new SeasonViewModel(_client.Object);
		await vm.OnNavigatedTo(season);
		return vm;
	}

	private static async Task WaitUntil(Func<bool> condition, string what)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!condition())
		{
			Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "timed out: " + what);
			await Task.Delay(10);
		}
	}

	// --- item 1: Back shows the season the user picked

	[Fact]
	public async Task Back_OpensOnTheSeasonThePageShowed_NotOnNextUps()
	{
		var vm = await OpenSeries(before: x => x.ReturnTo(_seasons[1].Id));

		Assert.Equal(_seasons[1].Id, vm.SelectedSeason!.Id);
		Assert.Equal(_episodes[_seasons[1].Id!.Value], vm.Episodes);
	}

	[Fact]
	public async Task Back_SaidAfterTheSeasonsAreIn_SwitchesTheListToIt()
	{
		var vm = await OpenSeries();
		Assert.Equal(_seasons[0].Id, vm.SelectedSeason!.Id);

		vm.ReturnTo(_seasons[2].Id);

		await WaitUntil(() => vm.Episodes == _episodes[_seasons[2].Id!.Value], "the list of season 3");
		Assert.Equal([false, false, true], vm.SeasonPills.Select(p => p.IsCurrent));
	}

	[Fact]
	public async Task ANewVisit_OpensOnTheSeasonPlayStartsIn()
	{
		var vm = await OpenSeries(before: x => x.ReturnTo(null));

		Assert.Equal(_seasons[0].Id, vm.SelectedSeason!.Id);
	}

	[Fact]
	public async Task TheListOpensWherePlayStarts_EvenWhenTheServersNextUpIsElsewhere()
	{
		// season 1 watched, season 2 not; the server's Next Up (after the last one watched elsewhere) is in season 3
		foreach (var episode in _episodes[_seasons[0].Id!.Value])
		{
			episode.UserData!.Played = true;
		}

		var nextUp = _episodes[_seasons[2].Id!.Value][0];
		_client.Setup(x => x.GetNextUp(It.IsAny<BaseItemDto>())).ReturnsAsync(new BaseItemDtoQueryResult { Items = [nextUp] });

		var vm = await OpenSeries();

		Assert.Equal("Play S2 E1", vm.PlayTarget.Label);
		Assert.Equal(_seasons[1].Id, vm.SelectedSeason!.Id);
	}

	// --- item 2: the page's own Watched follows its episodes

	[Fact]
	public async Task AWatchedSeason_WithOneEpisodeUnmarked_IsNoLongerShownWatched()
	{
		var season = _seasons[0];
		season.UserData!.Played = true;
		var vm = await OpenSeason(season);
		Assert.True(vm.Dto.UserData!.Played);

		// the server: unmarking one episode unmarks the season
		_seasonOnServer[season.Id!.Value] = new BaseItemDto { Id = season.Id, Type = BaseItemDto_Type.Season, Name = season.Name, SeriesId = SeriesId, IndexNumber = 1, UserData = new UserItemDataDto { Played = false } };
		await Commands().SetWatched(_episodes[season.Id!.Value][3], false);

		await WaitUntil(() => vm.Dto.UserData?.Played == false, "the season's own state");
	}

	[Fact]
	public async Task ASeries_WithItsLastEpisodeMarked_IsShownWatched()
	{
		var vm = await OpenSeries();
		Assert.False(vm.Dto.UserData!.Played);

		_seriesOnServer = new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Lost", UserData = new UserItemDataDto { Played = true } };
		await Commands().SetWatched(_episodes[_seasons[0].Id!.Value][0], true);

		await WaitUntil(() => vm.Dto.UserData?.Played == true, "the series' own state");
	}

	// --- item 3: only the newest of overlapping refreshes applies

	[Fact]
	public async Task TwoRefreshesOfTheSameSeason_TheOlderAnswerArrivingLast_DoesNotWin()
	{
		var vm = await OpenSeries();
		var id = _seasons[1].Id!.Value;
		var older = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var newer = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		_slow[id] = new Queue<TaskCompletionSource<BaseItemDtoQueryResult?>>([older, newer]);
		List<BaseItemDto> oldList = [_episodes[id][0]];

		var first = vm.SelectSeasonCommand.ExecuteAsync(_seasons[1]);
		var second = vm.SelectSeasonCommand.ExecuteAsync(_seasons[1]);
		newer.SetResult(new BaseItemDtoQueryResult { Items = _episodes[id] });
		await second;
		older.SetResult(new BaseItemDtoQueryResult { Items = oldList });
		await first;

		Assert.Same(_episodes[id], vm.Episodes);
		Assert.False(vm.IsLoadingEpisodes);
	}

	[Fact]
	public async Task ASeasonPagesListRefreshedTwice_KeepsTheNewestAnswer()
	{
		var season = _seasons[0];
		var vm = await OpenSeason(season);
		var id = season.Id!.Value;
		var older = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var newer = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		_slow[id] = new Queue<TaskCompletionSource<BaseItemDtoQueryResult?>>([older, newer]);
		var calls = () => _client.Invocations.Count(i => i.Method.Name == nameof(IJellyfinClient.GetItems) && ((BaseItemDto)i.Arguments[0]).Id == id);
		var before = calls();

		// the season marked watched, then unmarked: each refreshes the list
		await Commands().SetWatched(season, true);
		await Commands().SetWatched(season, false);
		await WaitUntil(() => calls() == before + 2, "two list refreshes");
		List<BaseItemDto> newList = [.. _episodes[id].Take(2)];
		newer.SetResult(new BaseItemDtoQueryResult { Items = newList });
		await WaitUntil(() => vm.Episodes == newList, "the newer list");
		older.SetResult(new BaseItemDtoQueryResult { Items = _episodes[id] });
		await Task.Delay(100);

		Assert.Same(newList, vm.Episodes);
	}

	// --- item 4: a season that cannot be listed keeps its picker and says so

	[Fact]
	public async Task ASeasonThatFailsToLoad_KeepsThePicker_AndOffersTryAgain()
	{
		var vm = await OpenSeries();
		var id = _seasons[1].Id!.Value;
		var failed = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		failed.SetResult(null);
		_slow[id] = new Queue<TaskCompletionSource<BaseItemDtoQueryResult?>>([failed]);

		await vm.SelectSeasonCommand.ExecuteAsync(_seasons[1]);

		Assert.True(vm.HasEpisodeSection);
		Assert.Equal(3, vm.SeasonPills.Count);
		Assert.Empty(vm.Episodes);
		Assert.True(vm.EpisodesFailed);
		Assert.Equal("Couldn't load the episodes of Season 2.", vm.EpisodesMessage);

		await vm.RetrySeasonCommand.ExecuteAsync(null);

		Assert.Equal(_episodes[id], vm.Episodes);
		Assert.False(vm.EpisodesFailed);
		Assert.False(vm.HasEpisodesMessage);
	}

	[Fact]
	public async Task ASeasonWithNoEpisodes_SaysSo_WithoutTryAgain()
	{
		var vm = await OpenSeries();
		_episodes[_seasons[2].Id!.Value] = [];

		await vm.SelectSeasonCommand.ExecuteAsync(_seasons[2]);

		Assert.True(vm.HasEpisodeSection);
		Assert.Equal("Season 3 has no episodes yet.", vm.EpisodesMessage);
		Assert.False(vm.EpisodesFailed);
	}

	// --- item 6: Play's label from kept episodes

	private int EpisodeDownloads() => _client.Invocations.Count(i => i.Method.Name == nameof(IJellyfinClient.GetSeriesEpisodes));

	[Fact]
	public async Task AnEpisodeToggled_ChangesPlaysLabel_WithoutDownloadingTheEpisodesAgain()
	{
		var vm = await OpenSeries();
		Assert.Equal("Play S1 E1", vm.PlayTarget.Label);
		var downloads = EpisodeDownloads();

		await Commands().SetWatched(_episodes[_seasons[0].Id!.Value][0], true);

		await WaitUntil(() => vm.PlayTarget.Label == "Play S1 E2", "Play's label");
		Assert.Equal(downloads, EpisodeDownloads());
	}

	[Fact]
	public async Task ASeasonToggled_MarksAllItsKeptEpisodes()
	{
		var vm = await OpenSeries();
		await Commands().SetWatched(_seasons[0], true);

		await WaitUntil(() => vm.PlayTarget.Label == "Play S2 E1", "Play's label");
	}

	[Fact]
	public async Task TheKeptEpisodes_AreSharedBetweenPages_UntilSomethingChangesOnTheServer()
	{
		var changes = 0;
		var time = new ManualTime();
		using var kept = new SeriesEpisodes(_client.Object, () => changes, time, subscribe: false);

		await kept.Get(SeriesId);
		await kept.Get(SeriesId);
		Assert.Equal(1, EpisodeDownloads());

		changes++;   // the server reported a change, a sign-in, a player page opened or left
		await kept.Get(SeriesId);
		Assert.Equal(2, EpisodeDownloads());

		time.Now += (long)(SeriesEpisodes.FreshFor.TotalSeconds + 1) * time.TimestampFrequency;
		await kept.Get(SeriesId);
		Assert.Equal(3, EpisodeDownloads());
	}

	[Fact]
	public async Task AChangeReportedWhileTheAnswerIsOnItsWay_LeavesItStale()
	{
		var changes = 0;
		var answer = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var client = new Mock<IJellyfinClient>();
		client.SetupSequence(x => x.GetSeriesEpisodes(SeriesId)).Returns(answer.Task).ReturnsAsync(new BaseItemDtoQueryResult { Items = [] });
		using var kept = new SeriesEpisodes(client.Object, () => changes, new ManualTime(), subscribe: false);

		var reading = kept.Get(SeriesId);
		changes++;
		answer.SetResult(new BaseItemDtoQueryResult { Items = [] });
		await reading;
		await kept.Get(SeriesId);

		client.Verify(x => x.GetSeriesEpisodes(SeriesId), Times.Exactly(2));
	}

	[Fact]
	public void Patch_FollowsTheServer_AnEpisode_ASeason_TheSeries()
	{
		var list = _episodes.Values.SelectMany(x => x).Select(Copy).ToList();
		list[1].UserData!.PlaybackPositionTicks = 5_000;

		Assert.True(SeriesEpisodes.Patch(list, SeriesId, list[0].Id!.Value, new UserItemDataDto { Played = true }));
		Assert.True(list[0].UserData!.Played);
		Assert.False(list[1].UserData!.Played);

		SeriesEpisodes.Patch(list, SeriesId, _seasons[0].Id!.Value, new UserItemDataDto { Played = true });
		Assert.All(list.Where(e => e.SeasonId == _seasons[0].Id), e => Assert.True(e.UserData!.Played));
		Assert.Equal(0, list[1].UserData!.PlaybackPositionTicks);
		Assert.All(list.Where(e => e.SeasonId != _seasons[0].Id), e => Assert.False(e.UserData!.Played));

		SeriesEpisodes.Patch(list, SeriesId, SeriesId, new UserItemDataDto { Played = false });
		Assert.All(list, e => Assert.False(e.UserData!.Played));
		Assert.False(SeriesEpisodes.Patch(list, SeriesId, Guid.NewGuid(), new UserItemDataDto { Played = true }));
	}

	private sealed class ManualTime : TimeProvider
	{
		public long Now { get; set; } = 1_000;

		public override long TimestampFrequency => 1_000;

		public override long GetTimestamp() => Now;
	}
}
