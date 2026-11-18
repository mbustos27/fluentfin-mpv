using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Services;
using FluentFin.Core.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>
/// 1.2.0 (Nixie button review, I4 and I24): marking a whole series or season (un)watched is one click and offers an Undo
/// that puts each episode back as it was, its resume point too; the pages refresh once at its end.
/// </summary>
public sealed class ItemUndoTests
{
	private static readonly Guid SeriesId = Guid.NewGuid();

	private readonly Mock<IJellyfinClient> _client = new();
	private readonly BaseItemDto _season = new() { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Season, Name = "Season 1", SeriesName = "Lost", SeriesId = SeriesId, UserData = new UserItemDataDto { Played = false } };
	private readonly List<BaseItemDto> _episodes;
	private readonly List<(Guid Id, bool Played, long Position)> _restored = [];

	public ItemUndoTests()
	{
		_episodes =
		[
			Episode(1, played: true, position: 0),
			Episode(2, played: false, position: 9_000),
			Episode(3, played: false, position: 0),
		];
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == _season.Id), It.IsAny<bool>())).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = _episodes });
		_client.Setup(x => x.GetSeriesEpisodes(SeriesId)).ReturnsAsync(() => new BaseItemDtoQueryResult { Items = _episodes });
		_client.Setup(x => x.SetPlayed(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).ReturnsAsync((BaseItemDto _, bool played) => new UserItemDataDto { Played = played });
		_client.Setup(x => x.SetUserData(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<long>()))
			.Callback<Guid, bool, long>((id, played, position) => _restored.Add((id, played, position)))
			.ReturnsAsync((Guid _, bool played, long position) => new UserItemDataDto { Played = played, PlaybackPositionTicks = position });
		_client.Setup(x => x.GetItem(_season.Id!.Value)).ReturnsAsync(() => new BaseItemDto { Id = _season.Id, Type = BaseItemDto_Type.Season, UserData = new UserItemDataDto { Played = false, UnplayedItemCount = 2 } });
	}

	private BaseItemDto Episode(int n, bool played, long position) => new()
	{
		Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = SeriesId, SeasonId = _season.Id, ParentIndexNumber = 1, IndexNumber = n,
		UserData = new UserItemDataDto { Played = played, PlaybackPositionTicks = position },
	};

	private async Task<(bool Took, List<UndoOffer> Offers)> Mark(BaseItemDto item, bool played)
	{
		var offers = new List<UndoOffer>();
		void OnOffer(object? s, UndoOffer offer) => offers.Add(offer);
		ItemUserData.UndoOffered += OnOffer;
		try
		{
			return (await ItemUserData.SetPlayedAsync(_client.Object, item, played), offers);
		}
		finally
		{
			ItemUserData.UndoOffered -= OnOffer;
		}
	}

	[Fact]
	public async Task MarkingASeasonWatched_OffersAnUndo_ThatPutsBackEachEpisodeItChanged()
	{
		var (took, offers) = await Mark(_season, true);

		Assert.True(took);
		var offer = Assert.Single(offers);
		// episode 1 was watched already and had no resume point: the mark did not change it
		Assert.Equal("Marked 2 episodes of Season 1 of Lost as watched.", offer.Message);

		var events = new List<ItemUserDataChangedEventArgs>();
		void OnChanged(object? s, ItemUserDataChangedEventArgs e) => events.Add(e);
		ItemUserData.Changed += OnChanged;
		try
		{
			Assert.True(await offer.Undo());
		}
		finally
		{
			ItemUserData.Changed -= OnChanged;
		}

		Assert.Equal([(_episodes[1].Id!.Value, false, 9_000L), (_episodes[2].Id!.Value, false, 0L)], _restored);
		// each episode as the server has it, a page's refresh only at the end, for the season
		var mine = events.Where(e => e.ItemId == _season.Id || _episodes.Any(x => x.Id == e.ItemId)).ToList();
		Assert.Equal([true, true, false], mine.Select(e => e.Partial));
		Assert.True(mine[^1].Mixed);
		Assert.Equal(_season.Id, mine[^1].ItemId);
		Assert.Equal(2, _season.UserData!.UnplayedItemCount);
	}

	[Fact]
	public async Task MarkingASeriesUnwatched_PutsBackTheWatchedOnes()
	{
		var series = new BaseItemDto { Id = SeriesId, Type = BaseItemDto_Type.Series, Name = "Lost", UserData = new UserItemDataDto { Played = false } };
		_client.Setup(x => x.GetItem(SeriesId)).ReturnsAsync(series);

		var (_, offers) = await Mark(series, false);
		var offer = Assert.Single(offers);
		Assert.Equal("Marked 2 episodes of Lost as unwatched.", offer.Message);
		await offer.Undo();

		// episode 1 watched again; episode 2's resume point (which unmarking cleared) back
		Assert.Equal([(_episodes[0].Id!.Value, true, 0L), (_episodes[1].Id!.Value, false, 9_000L)], _restored);
	}

	[Fact]
	public async Task AnUndoTheServerDoesNotTakeInFull_SaysSo()
	{
		_client.Setup(x => x.SetUserData(_episodes[2].Id!.Value, It.IsAny<bool>(), It.IsAny<long>())).ReturnsAsync((UserItemDataDto?)null);
		var (_, offers) = await Mark(_season, true);

		Assert.False(await Assert.Single(offers).Undo());
	}

	[Fact]
	public async Task NoUndo_ForOneEpisodeOrAMovie_ForARefusedMark_OrWhenTheEpisodesCannotBeRead()
	{
		Assert.Empty((await Mark(_episodes[2], true)).Offers);
		Assert.Empty((await Mark(new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Movie }, true)).Offers);

		_client.Setup(x => x.SetPlayed(_season, true)).ReturnsAsync((UserItemDataDto?)null);
		var (took, offers) = await Mark(_season, true);
		Assert.False(took);
		Assert.Empty(offers);

		_client.Setup(x => x.SetPlayed(_season, true)).ReturnsAsync(new UserItemDataDto { Played = true });
		_client.Setup(x => x.GetItems(It.Is<BaseItemDto>(d => d.Id == _season.Id), It.IsAny<bool>())).ReturnsAsync((BaseItemDtoQueryResult?)null);
		(took, offers) = await Mark(_season, true);
		Assert.True(took);
		Assert.Empty(offers);
	}

	[Fact]
	public async Task NoUndo_WhenTheMarkChangedNoEpisode()
	{
		foreach (var e in _episodes)
		{
			e.UserData = new UserItemDataDto { Played = true };
		}

		Assert.Empty((await Mark(_season, true)).Offers);
	}

	[Fact]
	public async Task AnUndosEpisodes_KeepTheirOwnStateInTheKeptList_TheSeasonDoesNotMarkThemAll()
	{
		using var kept = new SeriesEpisodes(_client.Object, () => 0, TimeProvider.System, subscribe: false);
		await kept.Get(SeriesId);

		// an Undo's last event, for the season: its episodes came one by one, so nothing marks them all
		kept.Apply(new ItemUserDataChangedEventArgs(_season.Id!.Value, new UserItemDataDto { Played = false }, failed: false, mixed: true));
		var after = await kept.Get(SeriesId);

		Assert.Equal([true, false, false], after!.Select(e => e.UserData!.Played == true));
		Assert.Equal(9_000, after![1].UserData!.PlaybackPositionTicks);
	}

	[Fact]
	public async Task ASeasonPage_RefreshesOnceForAnUndo_NotOncePerEpisode()
	{
		var vm = new SeasonViewModel(_client.Object);
		await vm.OnNavigatedTo(_season);
		var (_, offers) = await Mark(_season, true);
		await Task.Delay(200);
		_client.Invocations.Clear();

		await Assert.Single(offers).Undo();
		await Task.Delay(300);

		// the season fetched again by the Undo itself (the server's state of it) and once by the page; its list once
		Assert.True(_client.Invocations.Count(i => i.Method.Name == nameof(IJellyfinClient.GetItem)) <= 2);
		Assert.Equal(1, _client.Invocations.Count(i => i.Method.Name == nameof(IJellyfinClient.GetItems)));
		await vm.OnNavigatedFrom();
	}
}
