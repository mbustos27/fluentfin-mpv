using FluentFin.Core.Journal;
using Flurl;
using Flurl.Http;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace FluentFin.Core.Services;

/// <summary>
/// fluentfin-mpv: the TV journal's requests (docs/JOURNAL.md), as the signed-in user. A file of its own, so upstream pulls
/// do not touch it. Never throws (but for cancellation); failures are logged with the item id and the HTTP status only.
/// </summary>
public partial class JellyfinClient : IJournalServer
{
	public async Task<JournalUserData?> GetUserDataAsync(Guid itemId, CancellationToken cancellationToken = default)
	{
		try
		{
			var data = await _jellyfinApiClient.UserItems[itemId].UserData.GetAsync(x => x.QueryParameters.UserId = UserId, cancellationToken);
			return data is null ? null : ToJournal(data);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning("Journal: reading the user data of {ItemId} failed: {Error}", itemId, JournalError(ex));
			return null;
		}
	}

	public async Task<JournalEpisode?> GetItemAsync(Guid itemId, CancellationToken cancellationToken = default)
	{
		try
		{
			var dto = await _jellyfinApiClient.Items[itemId].GetAsync(x => x.QueryParameters.UserId = UserId, cancellationToken);
			return dto is null ? null : ToJournal(dto);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning("Journal: reading item {ItemId} failed: {Error}", itemId, JournalError(ex));
			return null;
		}
	}

	public async Task<JournalPage<JournalEpisode>?> GetPlayedEpisodesAsync(int startIndex, int limit, CancellationToken cancellationToken = default)
	{
		try
		{
			var result = await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.UserId = UserId;
				query.IncludeItemTypes = [BaseItemKind.Episode];
				query.Recursive = true;
				query.IsPlayed = true;
				query.SortBy = [ItemSortBy.DatePlayed];
				query.SortOrder = [SortOrder.Descending];
				query.StartIndex = startIndex;
				query.Limit = limit;
				query.EnableUserData = true;
				query.EnableImageTypes = [ImageType.Primary];
				query.ImageTypeLimit = 1;
				query.EnableTotalRecordCount = true;
			}, cancellationToken);

			if (result is null)
			{
				return null;
			}

			return new JournalPage<JournalEpisode>([.. (result.Items ?? []).Select(ToJournal).OfType<JournalEpisode>()], result.TotalRecordCount);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning("Journal: reading the played episodes from {Start} failed: {Error}", startIndex, JournalError(ex));
			return null;
		}
	}

	public async Task<bool> MarkPlayedAsync(Guid itemId, DateTimeOffset datePlayed, CancellationToken cancellationToken = default)
	{
		try
		{
			await _jellyfinApiClient.UserPlayedItems[itemId].PostAsync(x =>
			{
				x.QueryParameters.UserId = UserId;
				x.QueryParameters.DatePlayed = datePlayed;
			}, cancellationToken);
			return true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning("Journal: marking {ItemId} played failed: {Error}", itemId, JournalError(ex));
			return false;
		}
	}

	public async Task<bool> SetRatingAsync(Guid itemId, int rating, CancellationToken cancellationToken = default)
	{
		try
		{
			await _jellyfinApiClient.UserItems[itemId].UserData.PostAsync(new UpdateUserItemDataDto { Rating = rating }, x => x.QueryParameters.UserId = UserId, cancellationToken);
			return true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogWarning("Journal: sending the rating of {ItemId} to Jellyfin failed: {Error}", itemId, JournalError(ex));
			return false;
		}
	}

	public async Task<PlaybackHistory> GetPlaybackHistoryAsync(Guid userId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrEmpty(BaseUrl) || string.IsNullOrEmpty(_token))
		{
			return PlaybackHistory.Unavailable("not signed in");
		}

		try
		{
			// the token in the header, never in the url (which Flurl's errors and the logs would show)
			var response = await BaseUrl
				.AppendPathSegment(PlaybackReportingQuery.Path)
				.WithHeader("Authorization", $"MediaBrowser Token=\"{_token}\"")
				.WithTimeout(TimeSpan.FromSeconds(30))
				.AllowAnyHttpStatus()
				.PostJsonAsync(new { CustomQueryString = PlaybackReportingQuery.For(userId), ReplaceUserId = false }, cancellationToken: cancellationToken);

			if (response.StatusCode is 401 or 403 or 404)
			{
				// not installed, or the user is not an administrator: no exact dates, and nothing to tell anyone about
				logger.LogDebug("Journal: no Playback Reporting history (HTTP {Status})", response.StatusCode);
				return PlaybackHistory.Unavailable($"HTTP {response.StatusCode}");
			}

			if (response.StatusCode is < 200 or >= 300)
			{
				logger.LogWarning("Journal: the Playback Reporting history could not be read (HTTP {Status})", response.StatusCode);
				return PlaybackHistory.Unavailable($"HTTP {response.StatusCode}");
			}

			var history = PlaybackReportingQuery.Parse(await response.GetStringAsync());
			if (!history.Available)
			{
				logger.LogWarning("Journal: the Playback Reporting history was not readable: {Reason}", history.Reason);
			}

			return history;
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			logger.LogWarning("Journal: the Playback Reporting history could not be read: {Error}", JournalError(ex));
			return PlaybackHistory.Unavailable("request failed");
		}
	}

	internal static JournalUserData ToJournal(UserItemDataDto data) =>
		new(data.Played ?? false, data.PlayCount ?? 0, data.LastPlayedDate, data.IsFavorite ?? false, data.Rating);

	internal static JournalEpisode? ToJournal(BaseItemDto? dto)
	{
		if (dto?.Id is not { } id)
		{
			return null;
		}

		string? primary = null;
		if (dto.ImageTags?.AdditionalData is { } tags && tags.TryGetValue(nameof(ImageType.Primary), out var tag) && tag is not null)
		{
			primary = $"{tag}";
		}

		return new JournalEpisode(id, dto.Type?.ToString(), dto.Name, dto.SeriesId, dto.SeriesName, dto.SeasonId, dto.ParentIndexNumber, dto.IndexNumber,
			dto.RunTimeTicks, primary, dto.SeriesPrimaryImageTag, dto.UserData is { } data ? ToJournal(data) : null);
	}

	/// <summary>The status code of a refused request, otherwise the kind of failure: never a url with a token or a body.</summary>
	private static string JournalError(Exception ex) => ex switch
	{
		ApiException { ResponseStatusCode: > 0 } api => $"HTTP {api.ResponseStatusCode}",
		FlurlHttpException { StatusCode: { } status } => $"HTTP {status}",
		FlurlHttpTimeoutException => "timed out",
		_ => ex.GetType().Name,
	};
}
