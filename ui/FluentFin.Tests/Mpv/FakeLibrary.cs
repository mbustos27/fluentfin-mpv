using System.Collections.Concurrent;
using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;
using Moq;

namespace FluentFin.Tests.Mpv;

/// <summary>
/// A server's library behind a mocked <see cref="IJellyfinClient"/>, answering both FluentFin's own playlist requests (the item,
/// the series' seasons, each season's episodes) and the mpv queue's one request (the series' episodes in order), each after
/// <see cref="Latency"/> and <see cref="Gate"/>. Records every request as it is made.
/// </summary>
internal sealed class FakeLibrary
{
	private readonly ConcurrentDictionary<Guid, BaseItemDto> _items = new();
	private readonly ConcurrentDictionary<Guid, List<BaseItemDto>> _children = new();
	private readonly ConcurrentQueue<string> _requests = new();

	public FakeLibrary(Mock<IJellyfinClient> client, TimeSpan latency = default)
	{
		Latency = latency;
		client.Setup(x => x.GetItem(It.IsAny<Guid>())).Returns<Guid>(async id =>
		{
			await Answer("GetItem " + Name(id));
			return FailItems ? null : _items.GetValueOrDefault(id);
		});
		client.Setup(x => x.GetItems(It.IsAny<BaseItemDto>(), It.IsAny<bool>())).Returns<BaseItemDto, bool>(async (parent, _) =>
		{
			await Answer("GetItems " + Name(parent.Id ?? Guid.Empty));
			return parent.Id is { } id && _children.TryGetValue(id, out var children) ? new BaseItemDtoQueryResult { Items = [.. children] } : null;
		});
		client.Setup(x => x.GetSeriesEpisodes(It.IsAny<Guid>())).Returns<Guid>(async id =>
		{
			await Answer("GetSeriesEpisodes " + Name(id));
			if (FailEpisodes)
			{
				return null;
			}

			var episodes = _children.GetValueOrDefault(id, []).SelectMany(season => _children.GetValueOrDefault(season.Id!.Value, [])).ToList();
			return new BaseItemDtoQueryResult { Items = episodes, TotalRecordCount = episodes.Count };
		});
	}

	public TimeSpan Latency { get; set; }

	/// <summary>Every request waits for this (after it was recorded).</summary>
	public Task Gate { get; set; } = Task.CompletedTask;

	public bool FailEpisodes { get; set; }

	public bool FailItems { get; set; }

	public string[] Requests => [.. _requests];

	public void ClearRequests() => _requests.Clear();

	public BaseItemDto AddMovie(long resumeTicks = 0)
	{
		var movie = new BaseItemDto
		{
			Id = Guid.NewGuid(),
			Name = "A movie",
			Type = BaseItemDto_Type.Movie,
			UserData = new UserItemDataDto { PlaybackPositionTicks = resumeTicks },
		};
		_items[movie.Id!.Value] = movie;
		return movie;
	}

	/// <summary>
	/// A series of <paramref name="seasons"/> × <paramref name="episodesPerSeason"/> episodes; the first
	/// <paramref name="played"/> are played; episode n (from 0, across seasons) has a resume point of (n + 1) × 1000 ticks.
	/// </summary>
	public FakeShow AddSeries(int seasons, int episodesPerSeason, int played = 0)
	{
		var series = new BaseItemDto { Id = Guid.NewGuid(), Name = "A show", Type = BaseItemDto_Type.Series };
		_items[series.Id!.Value] = series;
		var seasonList = new List<BaseItemDto>();
		var episodes = new List<BaseItemDto>();
		for (var s = 1; s <= seasons; s++)
		{
			var season = new BaseItemDto { Id = Guid.NewGuid(), Name = $"Season {s}", Type = BaseItemDto_Type.Season, SeriesId = series.Id, IndexNumber = s };
			_items[season.Id!.Value] = season;
			seasonList.Add(season);
			var inSeason = new List<BaseItemDto>();
			for (var e = 1; e <= episodesPerSeason; e++)
			{
				var n = episodes.Count;
				var episode = new BaseItemDto
				{
					Id = Guid.NewGuid(),
					Name = $"S{s:00}E{e:00}",
					Type = BaseItemDto_Type.Episode,
					SeriesId = series.Id,
					SeasonId = season.Id,
					ParentIndexNumber = s,
					IndexNumber = e,
					UserData = new UserItemDataDto { Played = n < played, PlaybackPositionTicks = (n + 1) * 1000 },
				};
				_items[episode.Id!.Value] = episode;
				inSeason.Add(episode);
				episodes.Add(episode);
			}

			_children[season.Id.Value] = inSeason;
		}

		_children[series.Id.Value] = seasonList;
		return new FakeShow(series, seasonList, episodes);
	}

	/// <summary>The item as an item page holds it: its type, series and season, and a resume point older than the server's.</summary>
	public static BaseItemDto PageCopy(BaseItemDto item) => new()
	{
		Id = item.Id,
		Name = item.Name,
		Type = item.Type,
		SeriesId = item.SeriesId,
		SeasonId = item.SeasonId,
		UserData = new UserItemDataDto { PlaybackPositionTicks = 1, Played = false },
	};

	private string Name(Guid id) => _items.TryGetValue(id, out var item) ? item.Name ?? id.ToString("N") : id.ToString("N");

	private async Task Answer(string request)
	{
		_requests.Enqueue(request);
		await Gate;
		if (Latency > TimeSpan.Zero)
		{
			await Task.Delay(Latency);
		}
	}
}

internal sealed record FakeShow(BaseItemDto Series, List<BaseItemDto> Seasons, List<BaseItemDto> Episodes);
