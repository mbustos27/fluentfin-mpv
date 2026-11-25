using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.8 (P1-2, stream a): the people of titles by id, for the people pass's top-up: the titles new since the index
/// before (a hundred a request). Lean as the people pass's pages: the id and People only, no images, no user data.
/// </summary>
public partial class JellyfinClient
{
	public async Task<IReadOnlyList<BaseItemDto>?> GetPeopleByIds(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
	{
		if (ids.Count == 0)
		{
			return [];
		}

		try
		{
			var page = await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.Ids = [.. ids.Select(id => (Guid?)id)];
				query.Fields = PeopleFields;
				query.EnableImages = false;
				query.EnableUserData = false;
				query.EnableTotalRecordCount = false;
			}, cancellationToken);
			return page?.Items is { } items ? [.. items] : null;
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
		{
			// as an index page (BH-171-q3): refused, the read ends (asked again, it would only be refused again)
			logger.LogWarning("People of {Count} titles: refused (HTTP {Status})", ids.Count, ex.ResponseStatusCode);
			throw new IndexRefusedException(ex.ResponseStatusCode);
		}
		catch (Exception ex)
		{
			logger.LogWarning("People of {Count} titles: {Error}", ids.Count, ex.GetType().Name); // no ids or names (rule 10)
			return null;
		}
	}
}
