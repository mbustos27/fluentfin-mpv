using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>What the server answered a "Save as playlist": the new playlist's id, or why it said no.</summary>
public sealed record PlaylistCreation(Guid? Id, int Status, string? Error)
{
	public bool Created => Id is not null;
}

/// <summary>
/// Nixie 1.6 (smart collections phase 1, docs\research\smart-collections-data.md): the requests behind the library index and
/// the Collections page. An ordinary user's requests only; each is null (or empty) on a failure and never throws, except
/// for cancellation where it takes a token.
/// </summary>
public partial class JellyfinClient
{
	/// <summary>
	/// The index's fields, and no more: each is a real <see cref="ItemFields"/> name (the server drops a name it doesn't know
	/// and answers 200, so a typo would only show as missing data). ProductionYear, RunTimeTicks, CommunityRating,
	/// OfficialRating and UserData come without being asked.
	/// </summary>
	internal static readonly ItemFields[] IndexFields =
		[ItemFields.ProviderIds, ItemFields.Tags, ItemFields.Genres, ItemFields.Studios, ItemFields.ProductionLocations, ItemFields.SortName, ItemFields.DateCreated];

	/// <summary>A library's people pass: <see cref="ItemFields.People"/> alone (it costs about 3.5 times the index: its own pass, after it).</summary>
	internal static readonly ItemFields[] PeopleFields = [ItemFields.People];

	public Task<BaseItemDtoQueryResult?> GetLibraryIndexPage(BaseItemDto library, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken) =>
		IndexPage(library, IndexFields, startIndex, limit, withTotal, cancellationToken);

	public Task<BaseItemDtoQueryResult?> GetLibraryPeoplePage(BaseItemDto library, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken) =>
		IndexPage(library, PeopleFields, startIndex, limit, withTotal, cancellationToken);

	private async Task<BaseItemDtoQueryResult?> IndexPage(BaseItemDto library, ItemFields[] fields, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.SortBy = [ItemSortBy.SortName, ItemSortBy.ProductionYear];
				query.SortOrder = [SortOrder.Ascending, SortOrder.Ascending];
				query.Fields = fields;
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary];
				query.ParentId = library.Id;
				query.IncludeItemTypes = IndexKinds(library);
				query.Recursive = true;
				query.StartIndex = Math.Max(0, startIndex);
				query.Limit = Math.Max(1, limit);
				query.EnableTotalRecordCount = withTotal;
			}, cancellationToken);
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception ex)
		{
			logger.LogWarning("Index page {Start}+{Limit} of {Library}: {Error}", startIndex, limit, library.Id, ex.Message);
			return null;
		}
	}

	/// <summary>What a library's index holds: films, or shows (not their episodes); null for a library the index skips.</summary>
	internal static BaseItemKind[]? IndexKinds(BaseItemDto library) => library.CollectionType switch
	{
		BaseItemDto_CollectionType.Movies => [BaseItemKind.Movie],
		BaseItemDto_CollectionType.Tvshows => [BaseItemKind.Series],
		_ => null,
	};

	/// <summary>
	/// The user's played episodes, latest first, with their show and when they were played: a show's own user data has no
	/// last-played date on 10.11 or 12 (the data check), so "Unfinished series" is read from these.
	/// </summary>
	public async Task<BaseItemDtoQueryResult?> GetPlayedEpisodesPage(int startIndex, int limit, bool withTotal, CancellationToken cancellationToken)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.IncludeItemTypes = [BaseItemKind.Episode];
				query.Recursive = true;
				query.Filters = [ItemFilter.IsPlayed];
				query.SortBy = [ItemSortBy.DatePlayed, ItemSortBy.SortName];
				query.SortOrder = [SortOrder.Descending, SortOrder.Ascending];
				query.EnableImages = false;
				query.StartIndex = Math.Max(0, startIndex);
				query.Limit = Math.Max(1, limit);
				query.EnableTotalRecordCount = withTotal;
			}, cancellationToken);
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception ex)
		{
			logger.LogWarning("Played episodes {Start}+{Limit}: {Error}", startIndex, limit, ex.Message);
			return null;
		}
	}

	/// <summary>These items, with what a card and the Collection page read, in a few requests of at most 100 ids each.</summary>
	public async Task<IReadOnlyList<BaseItemDto>?> GetItemsByIds(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
	{
		var all = new List<BaseItemDto>(ids.Count);
		foreach (var chunk in ids.Distinct().Chunk(100))
		{
			try
			{
				var page = await _jellyfinApiClient.Items.GetAsync(x =>
				{
					var query = x.QueryParameters;
					query.Ids = [.. chunk.Select(id => (Guid?)id)];
					query.Fields = [.. LibraryFields, ItemFields.ProviderIds];
					query.ImageTypeLimit = 1;
					query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb];
				}, cancellationToken);
				if (page?.Items is not { } items)
				{
					return null;
				}

				all.AddRange(items);
			}
			catch (Exception) when (cancellationToken.IsCancellationRequested)
			{
				throw new OperationCanceledException(cancellationToken);
			}
			catch (Exception ex)
			{
				logger.LogWarning("Items by id ({Count}): {Error}", chunk.Length, ex.Message);
				return null;
			}
		}

		// in the order asked (the server answers in its own)
		var at = ids.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);
		return [.. all.Where(x => x.Id is { } id && at.ContainsKey(id)).OrderBy(x => at[x.Id!.Value])];
	}

	/// <summary>The user's box sets (every library's), with their TMDb id and how many items each holds.</summary>
	public Task<BaseItemDtoQueryResult?> GetBoxSets(CancellationToken cancellationToken = default) => Sets(BaseItemKind.BoxSet, cancellationToken);

	/// <summary>The playlists the user can see.</summary>
	public Task<BaseItemDtoQueryResult?> GetPlaylists(CancellationToken cancellationToken = default) => Sets(BaseItemKind.Playlist, cancellationToken);

	private async Task<BaseItemDtoQueryResult?> Sets(BaseItemKind kind, CancellationToken cancellationToken)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.IncludeItemTypes = [kind];
				query.Recursive = true;
				query.SortBy = [ItemSortBy.SortName];
				query.SortOrder = [SortOrder.Ascending];
				query.Fields = [ItemFields.ProviderIds, ItemFields.ChildCount, ItemFields.SortName, ItemFields.Overview];
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb];
				query.Limit = 2000;
			}, cancellationToken);
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception ex)
		{
			logger.LogWarning("The {Kind} list: {Error}", kind, ex.Message);
			return null;
		}
	}

	/// <summary>
	/// "Save as playlist": a new playlist of the user's own (POST /Playlists, open to any signed-in user), with these items in
	/// this order, and private: IsPublic is sent as false, since 10.11 and 12.4 make a playlist that leaves it out public
	/// (every account on the server would see the user's "Unfinished series"; live-save-playlist.ps1). The server's no (403,
	/// 401, 400…) comes back as its status, never as an exception.
	/// </summary>
	public async Task<PlaylistCreation> CreatePlaylist(string name, IReadOnlyList<Guid> ids)
	{
		try
		{
			var created = await _jellyfinApiClient.Playlists.PostAsync(new CreatePlaylistDto
			{
				Name = name,
				Ids = [.. ids.Select(id => (Guid?)id)],
				UserId = UserId,
				MediaType = CreatePlaylistDto_MediaType.Video,
				IsPublic = false,
			});
			return Guid.TryParse(created?.Id, out var id) ? new PlaylistCreation(id, 200, null) : new PlaylistCreation(null, 200, "no playlist id in the answer");
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex)
		{
			logger.LogInformation("Save as playlist: the server said no (HTTP {Status})", ex.ResponseStatusCode);
			return new PlaylistCreation(null, ex.ResponseStatusCode, ex.Message);
		}
		catch (Exception ex)
		{
			logger.LogWarning("Save as playlist: {Error}", ex.Message);
			return new PlaylistCreation(null, 0, ex.Message);
		}
	}
}
