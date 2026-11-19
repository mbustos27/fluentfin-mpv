using FluentFin.Core.Contracts.Services;
using FluentFin.Core.Mpv;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Core.Tests;

/// <summary>fluentfin-mpv: the mpv player's queue from one request (MpvQueue), at its edges.</summary>
public sealed class MpvQueueTests
{
	private readonly Mock<IJellyfinClient> _client = new(MockBehavior.Strict);
	private readonly Guid _seriesId = Guid.NewGuid();
	private readonly List<BaseItemDto> _episodes;

	public MpvQueueTests()
	{
		_episodes = [.. Enumerable.Range(0, 4).Select(n => new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Type = BaseItemDto_Type.Episode,
			SeriesId = _seriesId,
			UserData = new UserItemDataDto { Played = n < 2, PlaybackPositionTicks = (n + 1) * 1000 },
		})];
	}

	private void ServeEpisodes(List<BaseItemDto>? episodes = null) =>
		_client.Setup(x => x.GetSeriesEpisodes(_seriesId)).ReturnsAsync(new BaseItemDtoQueryResult { Items = episodes ?? _episodes });

	private static List<string> Ids(IEnumerable<BaseItemDto> items) => [.. items.Select(x => x.Id!.Value.ToString("N"))];

	[Fact]
	public async Task AnEpisode_IsTheSeriesFromItself_InOneRequest()
	{
		ServeEpisodes();

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _episodes[3].Id, Type = BaseItemDto_Type.Episode, SeriesId = _seriesId }, null);

		Assert.Equal(Ids(_episodes), request!.ItemIds);
		Assert.Equal(3, request.StartIndex);
		Assert.Equal(4000L, request.StartPositionTicks);
		Assert.Null(request.AudioStreamIndex);
		Assert.Null(request.SubtitleStreamIndex);
		_client.Verify(x => x.GetSeriesEpisodes(_seriesId), Times.Once);
		_client.VerifyNoOtherCalls();
	}

	[Theory]
	[InlineData(BaseItemDto_Type.Season)]
	[InlineData(BaseItemDto_Type.Series)]
	public async Task ASeasonOrASeries_StartsAtTheSeriesFirstEpisodeNotPlayed(BaseItemDto_Type type)
	{
		ServeEpisodes();
		var item = type == BaseItemDto_Type.Series
			? new BaseItemDto { Id = _seriesId, Type = type }
			: new BaseItemDto { Id = Guid.NewGuid(), Type = type, SeriesId = _seriesId };

		var request = await MpvQueue.BuildAsync(_client.Object, item, null);

		Assert.Equal(2, request!.StartIndex);
		Assert.Equal(3000L, request.StartPositionTicks);
	}

	[Fact]
	public async Task AnEpisodeWithoutUserData_CountsAsNotPlayed()
	{
		_episodes[0].UserData = null;
		ServeEpisodes();

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _seriesId, Type = BaseItemDto_Type.Series }, null);

		Assert.Equal(0, request!.StartIndex);
		Assert.Equal(0L, request.StartPositionTicks);
	}

	[Fact]
	public async Task EpisodesWithoutAnId_AreLeftOut()
	{
		ServeEpisodes([new BaseItemDto { Type = BaseItemDto_Type.Episode }, .. _episodes]);

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _episodes[1].Id, Type = BaseItemDto_Type.Episode, SeriesId = _seriesId }, null);

		Assert.Equal(Ids(_episodes), request!.ItemIds);
		Assert.Equal(1, request.StartIndex);
	}

	[Fact]
	public async Task AnEpisodeTheSeriesListDoesNotHave_IsPlayedAlone_FromItsOwnResumePoint()
	{
		ServeEpisodes();
		var other = new BaseItemDto { Id = Guid.NewGuid(), Type = BaseItemDto_Type.Episode, SeriesId = _seriesId, UserData = new UserItemDataDto { PlaybackPositionTicks = 77 } };
		_client.Setup(x => x.GetItem(other.Id!.Value)).ReturnsAsync(other);

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = other.Id, Type = BaseItemDto_Type.Episode, SeriesId = _seriesId }, new PlaybackSelection(1, 2));

		Assert.Equal([other.Id!.Value.ToString("N")], request!.ItemIds);
		Assert.Equal(0, request.StartIndex);
		Assert.Equal(77L, request.StartPositionTicks);
		Assert.Equal(1, request.AudioStreamIndex);
		Assert.Equal(2, request.SubtitleStreamIndex);
	}

	[Fact]
	public async Task AMovie_IsLookedUpOnce_ForItsResumePoint()
	{
		var id = Guid.NewGuid();
		_client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 5 } });

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 1 } }, null);

		Assert.Equal([id.ToString("N")], request!.ItemIds);
		Assert.Equal(5L, request.StartPositionTicks);
		_client.Verify(x => x.GetItem(id), Times.Once);
	}

	[Fact]
	public async Task AHomeVideoOrAMusicVideo_PlaysAlone_LikeAFilm_FromItsResumePoint()
	{
		// Nixie 1.4.0: every item type opens a page; these two could only say "can't play here"
		foreach (var type in new[] { BaseItemDto_Type.Video, BaseItemDto_Type.MusicVideo })
		{
			var id = Guid.NewGuid();
			_client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Type = type, UserData = new UserItemDataDto { PlaybackPositionTicks = 7 } });

			var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = id, Type = type }, null);

			Assert.NotNull(request);
			Assert.Equal([id.ToString("N")], request.ItemIds);
			Assert.Equal(7L, request.StartPositionTicks);
			_client.Verify(x => x.GetSeriesEpisodes(It.IsAny<Guid>()), Times.Never);
		}
	}

	[Fact]
	public async Task AMovie_FromTheBeginning_StartsAtZero()
	{
		var id = Guid.NewGuid();
		_client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 5 } });

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie }, new PlaybackSelection(1, 2), fromBeginning: true);

		Assert.Equal([id.ToString("N")], request!.ItemIds);
		Assert.Equal(0L, request.StartPositionTicks);
		Assert.Equal((1, 2), (request.AudioStreamIndex, request.SubtitleStreamIndex));
	}

	[Fact]
	public async Task AnEpisode_FromTheBeginning_KeepsItsPlaceInTheSeries_AtZero()
	{
		ServeEpisodes();

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _episodes[3].Id, Type = BaseItemDto_Type.Episode, SeriesId = _seriesId }, null, fromBeginning: true);

		Assert.Equal(Ids(_episodes), request!.ItemIds);
		Assert.Equal(3, request.StartIndex);
		Assert.Equal(0L, request.StartPositionTicks);
	}

	[Fact]
	public async Task AnItemThatDoesNotSayWhatItIs_IsLookedUpFirst_AndOnlyOnce()
	{
		var id = Guid.NewGuid();
		_client.Setup(x => x.GetItem(id)).ReturnsAsync(new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie, UserData = new UserItemDataDto { PlaybackPositionTicks = 9 } });

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = id }, null);

		Assert.Equal(9L, request!.StartPositionTicks);
		_client.Verify(x => x.GetItem(id), Times.Once);
	}

	[Fact]
	public async Task AnEpisodeThatDoesNotSayItsSeries_IsLookedUpFirst()
	{
		ServeEpisodes();
		_client.Setup(x => x.GetItem(_episodes[2].Id!.Value)).ReturnsAsync(_episodes[2]);

		var request = await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _episodes[2].Id, Type = BaseItemDto_Type.Episode }, null);

		Assert.Equal(2, request!.StartIndex);
		Assert.Equal(Ids(_episodes), request.ItemIds);
	}

	[Fact]
	public async Task EpisodesThatCannotBeLoaded_AreAFailure_NotNothingToPlay()
	{
		_client.Setup(x => x.GetSeriesEpisodes(_seriesId)).ReturnsAsync((BaseItemDtoQueryResult?)null);

		var error = await Assert.ThrowsAsync<MpvQueueException>(() => MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _seriesId, Type = BaseItemDto_Type.Series }, null));

		Assert.Equal("The episodes could not be loaded from the server.", error.Message);
	}

	[Fact]
	public async Task AMovieThatCannotBeLoaded_IsAFailure()
	{
		var id = Guid.NewGuid();
		_client.Setup(x => x.GetItem(id)).ReturnsAsync((BaseItemDto?)null);

		await Assert.ThrowsAsync<MpvQueueException>(() => MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = id, Type = BaseItemDto_Type.Movie }, null));
	}

	[Fact]
	public async Task ASeriesWithoutEpisodes_IsNothingToPlay()
	{
		ServeEpisodes([]);

		Assert.Null(await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = _seriesId, Type = BaseItemDto_Type.Series }, null));
	}

	[Theory]
	[InlineData(BaseItemDto_Type.BoxSet)]
	[InlineData(BaseItemDto_Type.Person)]
	public async Task AnotherType_IsNothingToPlay_AsWithFluentFinsOwnPlaylist(BaseItemDto_Type type)
	{
		Assert.Null(await MpvQueue.BuildAsync(_client.Object, new BaseItemDto { Id = Guid.NewGuid(), Type = type }, null));
		_client.VerifyNoOtherCalls();
	}
}
