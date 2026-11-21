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

		var switching = vm.SelectSeasonCommand.ExecuteAsync(_seasons[1]);
		Assert.True(switching.IsCompleted); // kept: shown at once, no wait for the server
		await switching;

		Assert.Equal(_episodes[_seasons[1].Id!.Value].Select(e => e.Id), vm.Episodes.Select(e => e.Id));
		Assert.Equal(1, ListsAsked(_seasons[1])); // not asked again
		Assert.Equal(1, ListsAsked(_seasons[2])); // the one after it, ahead (Nixie 1.5: a small show's, already in turn), once
	}

	// ---- Nixie 1.5: a far season (Season 1 to Season 9) asked ahead too, for a small show -------------------------------

	[Fact]
	public void FurtherSeasons_AreTheOthersNearestFirst_ForASmallShowOnly()
	{
		var ids = (int[] at) => at.Select(i => _seasons[i].Id).ToArray();
		Assert.Equal(ids([2, 3, 4]), ItemSiblings.Further(_seasons, _seasons[0].Id!.Value, 12).Select(s => s.Id));
		Assert.Equal(ids([4, 0]), ItemSiblings.Further(_seasons, _seasons[2].Id!.Value, 12).Select(s => s.Id)); // after, then before
		Assert.Equal(ids([2, 1, 0]), ItemSiblings.Further(_seasons, _seasons[4].Id!.Value, 12).Select(s => s.Id));
		Assert.Empty(ItemSiblings.Further(_seasons, _seasons[0].Id!.Value, 4)); // more seasons than asked ahead: none
		Assert.Equal(3, ItemSiblings.Further(_seasons, _seasons[0].Id!.Value, 5).Count); // as many as the most: all
		Assert.Empty(ItemSiblings.Further(_seasons, Guid.NewGuid(), 12));
		Assert.Empty(ItemSiblings.Further(_seasons.Take(2), _seasons[0].Id!.Value, 12)); // the one either side is Around's
		Assert.Empty(ItemSiblings.Further(null, _seasons[0].Id!.Value, 12));
	}

	[Fact]
	public async Task ASmallShow_AsksEverySeasonAhead_TheOnesEitherSideFirst_ThenOneAtATime()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var next = _held[_seasons[1].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value);
		Assert.Equal(1, ListsAsked(_seasons[1]));
		Assert.Equal(0, ListsAsked(_seasons[2])); // waits for the one either side

		var third = _held[_seasons[2].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		next.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[1].Id!.Value]] });
		await WaitFor(() => ListsAsked(_seasons[2]) == 1);
		Assert.Equal(0, ListsAsked(_seasons[3])); // one at a time

		third.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[2].Id!.Value]] });
		await WaitFor(() => kept.HasList(_seasons[4].Id!.Value));
		Assert.All(_seasons.Skip(1), s => Assert.Equal(1, ListsAsked(s)));
		Assert.Equal(0, ListsAsked(_seasons[0])); // the one shown is the page's own
	}

	[Fact]
	public async Task AFarSeason_OfASmallShow_ShowsWithoutWaitingForTheServer()
	{
		var vm = await OpenSeries();
		var asked = ListsAsked(_seasons[4]);
		Assert.Equal(1, asked);

		var switching = vm.SelectSeasonCommand.ExecuteAsync(_seasons[4]); // Season 1 to Season 5
		Assert.True(switching.IsCompleted);
		await switching;

		Assert.Equal(_episodes[_seasons[4].Id!.Value].Select(e => e.Id), vm.Episodes.Select(e => e.Id));
		Assert.Equal(asked, ListsAsked(_seasons[4]));
	}

	[Fact]
	public void ALongShow_AsksOnlyTheSeasonsEitherSide()
	{
		var seasons = Enumerable.Range(1, SeriesEpisodes.MostSeasonsAhead + 1)
			.Select(n => new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = $"Season {n}", IndexNumber = n, SeriesId = SeriesId })
			.ToList();
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);

		kept.PrefetchSeasons(seasons, seasons[5].Id!.Value);

		Assert.Equal([seasons[4].Id, seasons[6].Id], seasons.Where(s => ListsAsked(s) > 0).Select(s => s.Id));
	}

	[Fact]
	public async Task ANewerAskAhead_StopsTheOlderOneBeforeItsNextRequest()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var third = _held[_seasons[2].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value); // 2 at once, then 3 (held), 4, 5
		await WaitFor(() => ListsAsked(_seasons[2]) == 1);

		// another show opened meanwhile: its own asking ahead is the latest
		var other = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1", IndexNumber = 1 };
		kept.PrefetchSeasons([other], other.Id!.Value);
		third.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[2].Id!.Value]] });
		await Task.Delay(100);

		Assert.Equal(0, ListsAsked(_seasons[3]));
		Assert.Equal(0, ListsAsked(_seasons[4]));
	}

	[Fact]
	public async Task AFailedAskAhead_IsNotKept_AndTheTurnGoesOn()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var third = _held[_seasons[2].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value);
		await WaitFor(() => ListsAsked(_seasons[2]) == 1);
		third.SetResult(null); // the server failed this one

		await WaitFor(() => kept.HasList(_seasons[4].Id!.Value));
		Assert.False(kept.HasList(_seasons[2].Id!.Value));
		Assert.True(kept.HasList(_seasons[3].Id!.Value));
	}

	// ---- Nixie 1.5: the seasons either side tell the pages their episodes, for their stills to be asked ahead -------------

	[Fact]
	public async Task TheSeasonsEitherSide_TellTheirEpisodes_NotTheFurtherOnes_NorAFailure()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var told = new List<Guid?>();
		kept.NextToShownKept += (_, episodes) => told.Add(episodes[0].SeasonId);
		var before = _held[_seasons[1].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();

		kept.PrefetchSeasons(_seasons, _seasons[2].Id!.Value); // Season 3 shown: 4 and 2 either side, 5 and 1 further
		await WaitFor(() => kept.HasList(_seasons[3].Id!.Value));
		Assert.Equal([_seasons[3].Id], told); // the one after it, at once; the one before still on its way

		before.SetResult(null); // the one before fails: nothing to tell
		await WaitFor(() => kept.HasList(_seasons[0].Id!.Value));
		Assert.Equal([_seasons[3].Id], told); // nor the further ones
	}

	[Fact]
	public async Task ASeasonEitherSide_ThatAnswersAfterANewerAskAhead_TellsNothing()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var told = 0;
		kept.NextToShownKept += (_, _) => told++;
		var next = _held[_seasons[1].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value);

		var other = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1", IndexNumber = 1 };
		kept.PrefetchSeasons([other], other.Id!.Value); // another show opened meanwhile
		next.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[1].Id!.Value]] });
		await WaitFor(() => kept.HasList(_seasons[1].Id!.Value));

		Assert.Equal(0, told);
	}

	[Fact]
	public void AKeptSeasonEitherSide_TellsAtOnce()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var told = new List<Guid?>();
		kept.NextToShownKept += (_, episodes) => told.Add(episodes[0].SeasonId);

		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value);
		told.Clear();
		kept.PrefetchSeasons(_seasons, _seasons[2].Id!.Value); // 4 and 2, both kept by the first ask

		Assert.Equal([_seasons[3].Id, _seasons[1].Id], told);
	}

	// ---- Nixie 1.5: a season page shows its kept episodes before its own item answers -----------------------------------

	[Fact]
	public async Task ASeasonPage_ShowsKeptEpisodes_WhileItsSeasonIsOnItsWay()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[2];
		await kept.GetList(season);
		await kept.GetList(new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series }); // the series' seasons, kept too
		var item = new TaskCompletionSource<BaseItemDto?>();
		_client.Setup(x => x.GetItem(season.Id!.Value)).Returns(item.Task);

		var page = new SeasonViewModel(_client.Object, kept);
		var opening = page.OnNavigatedTo(season);

		// at once, together: the season it was opened with (the header), the picker and the rows
		Assert.False(opening.IsCompleted);
		Assert.Same(season, page.Dto);
		Assert.Equal(_seasons.Count, page.SeasonPills.Count);
		Assert.Equal(season.Id, page.SeasonPills.Single(p => p.IsCurrent).Dto.Id);
		Assert.Equal(_episodes[season.Id!.Value].Select(e => e.Id), page.Episodes?.Select(e => e.Id));
		Assert.True(page.State.IsLoading);

		// the server's season replaces the one opened with
		var fromServer = new BaseItemDto { Id = season.Id, Type = BaseItemDto_Type.Season, Name = season.Name, IndexNumber = season.IndexNumber, SeriesId = SeriesId, Overview = "As the server has it" };
		item.SetResult(fromServer);
		await opening;
		Assert.True(page.State.IsLoaded);
		Assert.Same(fromServer, page.Dto);
		Assert.Equal(_episodes[season.Id!.Value].Select(e => e.Id), page.Episodes.Select(e => e.Id));
		Assert.Equal(1, ListsAsked(season));
	}

	[Fact]
	public async Task ASeasonPage_OpenedWithItsIdAlone_ShowsKeptRows_AndItsSeasonOnceIn()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[2];
		await kept.GetList(season);
		var item = new TaskCompletionSource<BaseItemDto?>();
		_client.Setup(x => x.GetItem(season.Id!.Value)).Returns(item.Task);

		var page = new SeasonViewModel(_client.Object, kept);
		var opening = page.OnNavigatedTo(new BaseItemDto { Id = season.Id, Type = BaseItemDto_Type.Season }); // a bare link

		Assert.Null(page.Dto); // nothing to show of it yet (a blank header, as before)
		Assert.Equal(_episodes[season.Id!.Value].Select(e => e.Id), page.Episodes?.Select(e => e.Id));

		item.SetResult(season);
		await opening;
		Assert.Same(season, page.Dto);
		Assert.Equal(season.Id, page.SeasonPills.Single(p => p.IsCurrent).Dto.Id); // the picker from the server's season
	}

	[Fact]
	public async Task ASeasonPage_WhoseSeasonFails_ShowsTheMessageWithoutRows()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[2];
		await kept.GetList(season);
		_client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync((BaseItemDto?)null);

		var page = new SeasonViewModel(_client.Object, kept);
		await page.OnNavigatedTo(season);

		Assert.True(page.State.IsFailed);
		Assert.Null(page.Episodes);
		Assert.Null(page.Dto);
		Assert.Empty(page.SeasonPills);
	}

	[Fact]
	public async Task ASeasonPage_WhoseSeasonFailsBeforeItsEpisodes_ShowsNoLateRows()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[2];
		var list = _held[season.Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var seasons = _held[SeriesId] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		_client.Setup(x => x.GetItem(season.Id!.Value)).ReturnsAsync((BaseItemDto?)null);

		var page = new SeasonViewModel(_client.Object, kept);
		var opening = page.OnNavigatedTo(season);
		await WaitFor(() => page.State.IsFailed);
		// the rows and the picker answer after the failure: neither shows under the message
		list.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[season.Id!.Value]] });
		seasons.SetResult(new BaseItemDtoQueryResult { Items = [.. _seasons] });
		await opening;
		await Task.Delay(50);

		Assert.True(page.State.IsFailed);
		Assert.Null(page.Episodes);
		Assert.Null(page.Dto);
		Assert.Empty(page.SeasonPills);
	}

	// ---- Nixie 1.5 review: m2 (only the newest load clears), m3 (a left page asks nothing ahead), m4 (further seasons once) --

	[Fact]
	public async Task AReplacedLoadThatFails_DoesNotClearThePageTheNewerOneLoaded()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[2];
		var first = new TaskCompletionSource<BaseItemDto?>();
		var calls = 0;
		_client.Setup(x => x.GetItem(season.Id!.Value)).Returns(() => ++calls == 1 ? first.Task : Task.FromResult<BaseItemDto?>(season));

		var page = new SeasonViewModel(_client.Object, kept);
		var older = page.OnNavigatedTo(season);
		await page.OnNavigatedTo(season); // the page opened again (or Retry): this load wins
		Assert.True(page.State.IsLoaded);

		first.SetResult(null); // the older load's season fails, last
		await older;

		Assert.True(page.State.IsLoaded);
		Assert.Equal(season.Id, page.Dto?.Id);
		Assert.Equal(_episodes[season.Id!.Value].Select(e => e.Id), page.Episodes?.Select(e => e.Id));
		Assert.NotEmpty(page.SeasonPills);
	}

	[Fact]
	public async Task ASeasonPageLeft_BeforeItsSeasonAnswers_AsksNothingAhead()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		var season = _seasons[2];
		var item = new TaskCompletionSource<BaseItemDto?>();
		_client.Setup(x => x.GetItem(season.Id!.Value)).Returns(item.Task);

		var page = new SeasonViewModel(_client.Object, kept);
		var opening = page.OnNavigatedTo(season);
		await page.OnNavigatedFrom();
		item.SetResult(season);
		await opening;
		await Task.Delay(50);

		Assert.All(_seasons.Where(s => s.Id != season.Id), s => Assert.Equal(0, ListsAsked(s)));
	}

	[Fact]
	public async Task ASeriesPageLeft_BeforeItsSeasonAnswers_AsksNothingAhead()
	{
		// the opening asks every season ahead: the fourth fails (not kept), the fifth is still on its way
		var fourth = _held[_seasons[3].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		fourth.SetResult(null);
		var fifth = _held[_seasons[4].Id!.Value] = new TaskCompletionSource<BaseItemDtoQueryResult?>();
		var vm = await OpenSeries();

		var switching = vm.SelectSeasonCommand.ExecuteAsync(_seasons[4]); // waits for the fifth's list
		await vm.OnNavigatedFrom(); // the page is left meanwhile
		var asked = ListsAsked(_seasons[3]);
		fifth.SetResult(new BaseItemDtoQueryResult { Items = [.. _episodes[_seasons[4].Id!.Value]] });
		await switching;
		await Task.Delay(50);

		Assert.Equal(asked, ListsAsked(_seasons[3])); // the fourth, either side of the fifth, is not asked ahead
	}

	[Fact]
	public async Task OutOfDateFurtherSeasons_AreNotAskedAheadAgain_TheOnesEitherSideAre()
	{
		var changes = 0;
		using var kept = new SeriesEpisodes(_client.Object, () => changes, TimeProvider.System, subscribe: false);
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value); // 2 either side, then 3, 4, 5
		await WaitFor(() => kept.HasList(_seasons[4].Id!.Value));
		Assert.All(_seasons.Skip(1), s => Assert.Equal(1, ListsAsked(s)));

		changes++; // an episode played (the server's notice): every kept list is out of date
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value);
		await WaitFor(() => kept.HasList(_seasons[1].Id!.Value));
		await Task.Delay(50);

		Assert.Equal(2, ListsAsked(_seasons[1])); // either side: asked again (the next click shows at once)
		Assert.All(_seasons.Skip(2), s => Assert.Equal(1, ListsAsked(s))); // further away: not again until shown

		// a toggle in the app: out of date too, not gone (a further season is not asked ahead again for it)
		kept.Apply(new ItemUserDataChangedEventArgs(_episodes[_seasons[3].Id!.Value][0].Id!.Value, new UserItemDataDto { Played = true }, failed: false));
		kept.PrefetchSeasons(_seasons, _seasons[0].Id!.Value);
		await WaitFor(() => kept.HasList(_seasons[1].Id!.Value));
		await Task.Delay(50);
		Assert.All(_seasons.Skip(2), s => Assert.Equal(1, ListsAsked(s)));
		Assert.False(kept.HasList(_seasons[3].Id!.Value)); // out of date: shown, it is asked again
	}

	private static async Task WaitFor(Func<bool> condition)
	{
		for (var waited = 0; !condition() && waited < 100; waited++)
		{
			await Task.Delay(20);
		}

		Assert.True(condition());
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
