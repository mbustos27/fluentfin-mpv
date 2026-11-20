using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.1 (season switching): a season's episodes are kept (SeriesEpisodes.GetList), the seasons either side are asked
/// ahead, the series page's pills never drop a click, and a season page reuses what another page read. Every click asked the
/// server again (300 ms or more on a real server, before the rows), and a quick second click on Classic's and Drive-in's
/// pills was dropped.
/// </summary>
public sealed class SeasonSwitchTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly List<BaseItemDto> _seasons;
	private readonly Dictionary<Guid, List<BaseItemDto>> _episodes = [];
	private readonly Dictionary<Guid, TaskCompletionSource<BaseItemDtoQueryResult?>> _held = [];

	public SeasonSwitchTests()
	{
		_seasons = [.. Enumerable.Range(1, 5).Select(n => new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = $"Season {n}", IndexNumber = n, SeriesId = SeriesId })];
		foreach (var season in _seasons)
		{
			_episodes[season.Id!.Value] = [.. Enumerable.Range(1, 3).Select(e => new BaseItemDto
			{
				Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, Name = $"{season.Name} E{e}", IndexNumber = e, ParentIndexNumber = season.IndexNumber,
				SeasonId = season.Id, SeriesId = SeriesId,
			})];
		}

		var series = new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "A Tide of Seasons" };
		_client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(series);
		foreach (var season in _seasons)
		{
			_client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync(season);
		}

		_client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).Returns((BaseItemDto parent, bool _) =>
		{
			if (parent.Id is { } id && _held.Remove(id, out var held))
			{
				return held.Task;
			}

			return Task.FromResult<BaseItemDtoQueryResult?>(parent.Id == SeriesId
				? new BaseItemDtoQueryResult { Items = [.. _seasons] }
				: new BaseItemDtoQueryResult { Items = [.. _episodes.GetValueOrDefault(parent.Id ?? Guid.Empty, [])] });
		});
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = [.. _episodes.Values.SelectMany(x => x)] });
	}

	private int ListsAsked(BaseItemDto parent) =>
		_client.Invocations.Count(i => i.Method.Name == nameof(IJellyfinClient.GetItems) && i.Arguments[0] is BaseItemDto { Id: var id } && id == parent.Id);

	private async Task<SeriesViewModel> OpenSeries(SeriesEpisodes? kept = null)
	{
		var vm = new SeriesViewModel(_client.Object, kept);
		await vm.OnNavigatedTo(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series });
		return vm;
	}

	[Fact]
	public void AroundASeason_AreTheNextThenThePrevious_InThePillsOrder()
	{
		Assert.Equal([_seasons[3].Id, _seasons[1].Id], ItemSiblings.Around(_seasons, _seasons[2].Id!.Value).Select(s => s.Id));
		Assert.Equal([_seasons[1].Id], ItemSiblings.Around(_seasons, _seasons[0].Id!.Value).Select(s => s.Id));
		Assert.Equal([_seasons[3].Id], ItemSiblings.Around(_seasons, _seasons[4].Id!.Value).Select(s => s.Id));
		Assert.Empty(ItemSiblings.Around(_seasons, Guid.NewGuid()));
	}

	[Fact]
	public async Task TheSeasonAfterTheOneShown_IsAskedAhead_AndShowsWithoutAnotherRequest()
	{
		var vm = await OpenSeries();
		Assert.Equal(_seasons[0].Id, vm.SelectedSeason?.Id);
		Assert.Equal(1, ListsAsked(_seasons[1])); // asked ahead
		Assert.Equal(0, ListsAsked(_seasons[2]));

		var switching = vm.SelectSeasonCommand.ExecuteAsync(_seasons[1]);
		Assert.True(switching.IsCompleted); // kept: shown at once, no wait for the server
		await switching;

		Assert.Equal(_episodes[_seasons[1].Id!.Value].Select(e => e.Id), vm.Episodes.Select(e => e.Id));
		Assert.Equal(1, ListsAsked(_seasons[1])); // not asked again
		Assert.Equal(1, ListsAsked(_seasons[2])); // and now the one after it, ahead
	}

	[Fact]
	public async Task ASeasonSeenBefore_IsShownAgainWithoutARequest()
	{
		var vm = await OpenSeries();
		await vm.SelectSeasonCommand.ExecuteAsync(_seasons[3]);
		var asked = ListsAsked(_seasons[0]);

		await vm.SelectSeasonCommand.ExecuteAsync(_seasons[0]);

		Assert.Equal(_episodes[_seasons[0].Id!.Value].Select(e => e.Id), vm.Episodes.Select(e => e.Id));
		Assert.Equal(asked, ListsAsked(_seasons[0]));
		Assert.True(vm.SeasonPills.Single(p => p.IsCurrent).Dto.Id == _seasons[0].Id);
	}

	[Fact]
	public async Task ThePills_StayClickableWhileASeasonLoads_AndTheLastClickWins()
	{
		var vm = await OpenSeries();
		var slow3 = _held[_seasons[2].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var slow4 = _held[_seasons[3].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();

		var three = vm.SelectSeasonCommand.ExecuteAsync(_seasons[2]);
		// the buttons bound to the command stay enabled: a second click is not dropped
		Assert.True(vm.SelectSeasonCommand.CanExecute(_seasons[3]));
		var four = vm.SelectSeasonCommand.ExecuteAsync(_seasons[3]);
		slow4.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[3].Id!.Value]] });
		await four;
		slow3.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[2].Id!.Value]] }); // the older answer, last
		await three;

		Assert.Equal(_seasons[3].Id, vm.SelectedSeason?.Id);
		Assert.Equal(_episodes[_seasons[3].Id!.Value].Select(e => e.Id), vm.Episodes.Select(e => e.Id));
	}

	[Fact]
	public async Task ARefreshAfterAChange_AsksTheSeasonAgain_NotTheKeptList()
	{
		var vm = await OpenSeries();
		var shown = _seasons[0];
		var before = ListsAsked(shown);
		var season = new ItemUserDataChangedEventArgs(shown.Id!.Value, new UserItemDataDto { Played = true }, failed: false);

		vm.OnUserDataChanged(null, season);
		for (var waited = 0; ListsAsked(shown) < before + 1 && waited < 100; waited++)
		{
			await Task.Delay(20);
		}

		Assert.Equal(before + 1, ListsAsked(shown));
	}

	[Fact]
	public async Task AToggleAnywhere_DropsTheKeptLists_AndAnAnswerAskedBeforeItIsNotKept()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var two = _seasons[1];
		await kept.GetList(two);
		Assert.True(kept.HasList(two.Id!.Value));

		// an episode of season 2 marked watched on its own page (another copy of it): the kept list holds the old state
		var episode = _episodes[two.Id!.Value][0];
		kept.Apply(new ItemUserDataChangedEventArgs(episode.Id!.Value, new UserItemDataDto { Played = true }, failed: false));
		Assert.False(kept.HasList(two.Id!.Value));
		await kept.GetList(two);
		Assert.Equal(2, ListsAsked(two)); // asked again

		// an answer on its way when a toggle lands may predate it: not kept
		var three = _seasons[2];
		var held = _held[three.Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var asking = kept.GetList(three);
		kept.Apply(new ItemUserDataChangedEventArgs(_episodes[three.Id!.Value][0].Id!.Value, new UserItemDataDto { Played = true }, failed: false));
		held.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[three.Id!.Value]] });
		Assert.NotNull((await asking)?.Items); // the page that asked still shows it
		Assert.False(kept.HasList(three.Id!.Value));

		// a failed toggle changes nothing
		await kept.GetList(three);
		kept.Apply(new ItemUserDataChangedEventArgs(Guid.NewGuid(), new UserItemDataDto(), failed: true));
		Assert.True(kept.HasList(three.Id!.Value));
	}

	[Fact]
	public async Task KeptLists_AreAtMostMostLists_TheOldestGoFirst()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var parents = Enumerable.Range(0, SeriesEpisodes.MostLists + 5).Select(_ => new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season }).ToList();
		foreach (var parent in parents)
		{
			await kept.GetList(parent);
		}

		Assert.Equal(SeriesEpisodes.MostLists, kept.ListCount);
		Assert.False(kept.HasList(parents[0].Id!.Value));
		Assert.True(kept.HasList(parents[^1].Id!.Value));
	}

	[Fact]
	public async Task ASeasonPage_ReusesTheListsAnotherPageRead_AndAsksItsNeighboursAhead()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		await OpenSeries(kept); // the series page read season 1 and asked season 2 ahead

		var page = new SeasonViewModel(_client.Object, kept);
		await page.OnNavigatedTo(_seasons[1]);

		Assert.Equal(_episodes[_seasons[1].Id!.Value].Select(e => e.Id), page.Episodes.Select(e => e.Id));
		Assert.Equal(1, ListsAsked(_seasons[1])); // the series page's prefetch, reused
		Assert.Equal(1, ListsAsked(_seasons[2])); // the season after it, ahead
		Assert.True(kept.HasList(_seasons[2].Id!.Value));

		// a pill to the next season: a new page, its episodes kept
		var next = new SeasonViewModel(_client.Object, kept);
		await next.OnNavigatedTo(_seasons[2]);
		Assert.Equal(1, ListsAsked(_seasons[2]));
		Assert.Equal(_episodes[_seasons[2].Id!.Value].Select(e => e.Id), next.Episodes.Select(e => e.Id));
	}

	[Fact]
	public async Task KeptLists_AreAskedAgain_AfterAChangeTheServerReported_OrOnceStale()
	{
		var changes = 0;
		var time = new ManualTime();
		using var kept = new SeriesEpisodes(_client.Object, () => changes, time, subscribe: false);
		var season = _seasons[0];

		await kept.GetList(season);
		await kept.GetList(season);
		Assert.Equal(1, ListsAsked(season));

		changes++;
		await kept.GetList(season);
		Assert.Equal(2, ListsAsked(season));

		time.Now += (long)SeriesEpisodes.FreshFor.TotalMilliseconds;
		await kept.GetList(season);
		Assert.Equal(3, ListsAsked(season));

		await kept.GetList(season, fresh: true);
		Assert.Equal(4, ListsAsked(season));
	}

	[Fact]
	public async Task AListThatFailed_IsNotKept_AndOneOnItsWayIsShared()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[0];
		var failed = _held[season.Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var first = kept.GetList(season);
		var second = kept.GetList(season);
		failed.SetResult(null);

		Assert.Null(await first);
		Assert.Null(await second);
		Assert.Equal(1, ListsAsked(season)); // shared while on its way

		Assert.NotNull((await kept.GetList(season))?.Items); // not kept: asked again
		Assert.Equal(2, ListsAsked(season));
	}

	private sealed class ManualTime : TimeProvider
	{
		public long Now { get; set; } = 1_000;

		public override long TimestampFrequency => 1_000;

		public override long GetTimestamp() => Now;
	}
}
