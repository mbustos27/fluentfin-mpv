using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.Settings;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.4.1: the item pages that list several items ask for what needs only the ids they were opened with together with
/// the item itself; each list waited for the request before it (on a big server, a round trip or more each). The answers
/// still show in the old order (the item first).
/// </summary>
public sealed class ItemPagesAskTogetherTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();
	private static readonly Guid SeasonId = Guid.NewGuid();
	private static readonly Guid EpisodeId = Guid.NewGuid();

	private readonly Mock<IJellyfinClient> _client = new();

	/// <summary>The item's own request answers only when the test says.</summary>
	private TaskCompletionSource<BaseItemDto?> HoldItem(Guid id)
	{
		var held = new TaskCompletionSource<BaseItemDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
		_client.Setup(x => x.GetItem(id)).Returns(held.Task);
		return held;
	}

	private int Asked(string method, Guid? parent = null) =>
		_client.Invocations.Count(i => i.Method.Name == method && (parent is null || i.Arguments.FirstOrDefault() is BaseItemDto { Id: var id } && id == parent));

	[Fact]
	public async Task TheSeasonPage_AsksItsEpisodesAndTheSeriesSeasons_WithTheSeason()
	{
		var season = new BaseItemDto { Id = SeasonId, SeriesId = SeriesId, Type = BaseItemDto_Type.Season, Name = "Season 1" };
		var held = HoldItem(SeasonId);
		var vm = new SeasonViewModel(_client.Object);

		var opening = vm.OnNavigatedTo(season);
		await Task.Delay(50);

		// before the season answered: its episodes and the series' seasons (the pills) are on their way
		Assert.Equal((1, 1), (Asked(nameof(IJellyfinClient.GetItems), SeasonId), Asked(nameof(IJellyfinClient.GetItems), SeriesId)));
		Assert.False(opening.IsCompleted);

		held.SetResult(season);
		await opening;
		Assert.True(vm.State.IsLoaded);
		// asked once each: the early requests are the ones shown
		Assert.Equal((1, 1), (Asked(nameof(IJellyfinClient.GetItems), SeasonId), Asked(nameof(IJellyfinClient.GetItems), SeriesId)));
	}

	[Fact]
	public async Task TheSeriesPage_AsksItsSeasonsTheEpisodesAndMoreLikeThis_WithTheSeries()
	{
		var series = new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Lost" };
		var held = HoldItem(SeriesId);
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult
		{
			Items = [new BaseItemDto { Id = EpisodeId, SeasonId = SeasonId, SeriesId = SeriesId, Type = BaseItemDto_Type.Episode, ParentIndexNumber = 1, IndexNumber = 1 }],
		});
		var vm = new SeriesViewModel(_client.Object);

		var opening = vm.OnNavigatedTo(series);
		await Task.Delay(50);

		Assert.Equal((1, 1, 1), (Asked(nameof(IJellyfinClient.GetItems), SeriesId), Asked(nameof(IJellyfinClient.GetSimilarItems)), Asked(nameof(IJellyfinClient.GetSeriesEpisodes))));

		held.SetResult(series);
		await opening;
		Assert.True(vm.State.IsLoaded);
		// the series' episodes once: Play's label shared the request started early
		Assert.Equal((1, 1, 1), (Asked(nameof(IJellyfinClient.GetItems), SeriesId), Asked(nameof(IJellyfinClient.GetSimilarItems)), Asked(nameof(IJellyfinClient.GetSeriesEpisodes))));
	}

	[Fact]
	public async Task TheEpisodePage_AsksItsSeasonForUpNext_WithTheEpisode()
	{
		var episode = new BaseItemDto { Id = EpisodeId, SeasonId = SeasonId, SeriesId = SeriesId, Type = BaseItemDto_Type.Episode, Name = "Pilot" };
		var held = HoldItem(EpisodeId);
		var commands = new GlobalCommands(Mock.Of<INavigationServiceCore>(), _client.Object, Mock.Of<INavigationViewServiceCore>(), Mock.Of<ISettings>());
		var vm = new EpisodeViewModel(_client.Object, commands);

		var opening = vm.OnNavigatedTo(episode);
		await Task.Delay(50);
		Assert.Equal(1, Asked(nameof(IJellyfinClient.GetItems), SeasonId));

		held.SetResult(episode);
		await opening;
		Assert.Equal(1, Asked(nameof(IJellyfinClient.GetItems), SeasonId));
	}

	[Fact]
	public async Task TheSeriesEpisodes_AskedTwiceWhileOnTheirWay_AreOneRequest()
	{
		var answer = new TaskCompletionSource<BaseItemDtoQueryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).Returns(answer.Task);
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);

		var first = kept.Get(SeriesId);
		var second = kept.Get(SeriesId);
		answer.SetResult(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = EpisodeId }] });

		Assert.Equal([EpisodeId], (await first)!.Select(x => x.Id!.Value));
		Assert.Equal([EpisodeId], (await second)!.Select(x => x.Id!.Value));
		_client.Verify(x => x.GetSeriesEpisodes(SeriesId), Times.Once);
	}

	[Fact]
	public async Task TheSeriesEpisodes_AChangeWhileOnTheirWay_AsksAgain()
	{
		var changes = 0;
		var answers = new Queue<TaskCompletionSource<BaseItemDtoQueryResult?>>([new(), new()]);
		var asked = new List<TaskCompletionSource<BaseItemDtoQueryResult?>>();
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).Returns(() =>
		{
			var next = answers.Dequeue();
			asked.Add(next);
			return next.Task;
		});
		using var kept = new SeriesEpisodes(_client.Object, () => changes, TimeProvider.System, subscribe: false);

		var before = kept.Get(SeriesId);
		changes++; // the server reported a change: the answer on its way may not have it
		var after = kept.Get(SeriesId);
		Assert.Equal(2, asked.Count);

		asked[0].SetResult(new BaseItemDtoQueryResult { Items = [] });
		asked[1].SetResult(new BaseItemDtoQueryResult { Items = [new BaseItemDto { Id = EpisodeId }] });
		Assert.Empty((await before)!);
		Assert.Single((await after)!);
	}
}
