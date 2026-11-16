using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Mpv;

/// <summary>The server did not answer a request the queue needs (the request itself is logged by the client).</summary>
public sealed class MpvQueueException(string message) : Exception(message);

/// <summary>
/// fluentfin-mpv: what an item page's Play hands the mpv backend, in one request to the server. FluentFin's own playlist
/// (PlaylistViewModel) is not used: it asks for the series, its seasons and then each season in turn, full items with
/// images (31 s before mpv could start for a 250-episode show on a remote server). Here a movie is itself, looked up for its
/// resume point; an episode, a season or a series is the series' episodes in play order from
/// <see cref="IJellyfinClient.GetSeriesEpisodes"/>, the type and series read from the page's item. It starts where
/// FluentFin's own player starts: a movie or an episode at itself (with the streams picked on its page), a season or a
/// series at the first episode of the series not played (as FluentFin's playlist does, a season's too).
/// </summary>
public static class MpvQueue
{
	/// <summary>Null = nothing to play (an item of another type, a series without episodes).</summary>
	/// <param name="fromBeginning">"Play from beginning" on the item page: the first item starts at 0, not at its resume point.</param>
	/// <exception cref="MpvQueueException">A request failed.</exception>
	public static async Task<BridgePlayRequest?> BuildAsync(IJellyfinClient client, BaseItemDto item, PlaybackSelection? selection, bool fromBeginning = false)
	{
		var request = await BuildQueueAsync(client, item, selection);
		return fromBeginning && request is not null ? request with { StartPositionTicks = 0 } : request;
	}

	private static async Task<BridgePlayRequest?> BuildQueueAsync(IJellyfinClient client, BaseItemDto item, PlaybackSelection? selection)
	{
		if (item.Id is not { } id)
		{
			return null;
		}

		BaseItemDto? fetched = null;
		var type = item.Type;
		var seriesId = type == BaseItemDto_Type.Series ? id : item.SeriesId;
		if (type is null || (type is BaseItemDto_Type.Episode or BaseItemDto_Type.Season && seriesId is null))
		{
			// the page's item does not say what it is or where it belongs: looked up, as FluentFin's own player does
			fetched = await client.GetItem(id) ?? throw new MpvQueueException("The item could not be loaded from the server.");
			type = fetched.Type;
			seriesId = type == BaseItemDto_Type.Series ? id : fetched.SeriesId;
		}

		switch (type)
		{
			case BaseItemDto_Type.Movie:
				return await Alone(client, id, fetched, selection);

			case BaseItemDto_Type.Episode or BaseItemDto_Type.Season or BaseItemDto_Type.Series when seriesId is { } series:
				var result = await client.GetSeriesEpisodes(series) ?? throw new MpvQueueException("The episodes could not be loaded from the server.");
				var episodes = result.Items?.Where(x => x.Id.HasValue).ToList() ?? [];
				if (episodes.Count == 0)
				{
					return null;
				}

				int start;
				if (type == BaseItemDto_Type.Episode)
				{
					start = episodes.FindIndex(x => x.Id == id);
					if (start < 0)
					{
						// not among the series' episodes the server plays (FluentFin's playlist would start somewhere else)
						return await Alone(client, id, fetched, selection);
					}
				}
				else
				{
					start = Math.Max(0, episodes.FindIndex(x => x.UserData?.Played is false or null));
				}

				return BridgePlayRequest.FromPlaylist(episodes.Select(x => x.Id!.Value), start, episodes[start].UserData?.PlaybackPositionTicks ?? 0, selection);

			default:
				return null;
		}
	}

	/// <summary>The item alone, looked up for its resume point (once): the page's copy can be older than its last playback.</summary>
	private static async Task<BridgePlayRequest> Alone(IJellyfinClient client, Guid id, BaseItemDto? fetched, PlaybackSelection? selection)
	{
		var item = fetched ?? await client.GetItem(id) ?? throw new MpvQueueException("The item could not be loaded from the server.");
		return BridgePlayRequest.FromPlaylist([id], 0, item.UserData?.PlaybackPositionTicks ?? 0, selection);
	}
}
