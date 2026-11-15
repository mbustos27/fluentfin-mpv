using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>fluentfin-mpv: requests only the mpv player makes (a file of its own, so upstream pulls do not touch it).</summary>
public partial class JellyfinClient
{
	/// <summary>
	/// <c>GET /Shows/{seriesId}/Episodes</c> as jellyfin-web asks it when it plays a series (no missing episodes), but with
	/// nothing the queue does not need: the ids in order and the user data (played, resume point); no images, no extra fields.
	/// </summary>
	public async Task<BaseItemDtoQueryResult?> GetSeriesEpisodes(Guid seriesId)
	{
		try
		{
			return await _jellyfinApiClient.Shows[seriesId].Episodes.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.UserId = UserId;
				query.IsMissing = false;
				query.EnableImages = false;
				query.EnableUserData = true;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Loading the episodes of series {SeriesId} failed", seriesId);
			return null;
		}
	}
}
