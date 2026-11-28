using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.7.1 (BH-171-q3): the server refused an index page (401, 403): the read ends, with no other try (the sign-in
/// watch already knows). Its message names the status only.
/// </summary>
public sealed class IndexRefusedException(int status) : Exception($"the server refused the library index's request (HTTP {status})")
{
	public int Status { get; } = status;
}

/// <summary>What the server answered a "Save as playlist": the new playlist's id, or why it said no.</summary>
public sealed record PlaylistCreation(Guid? Id, int Status, string? Error)
{
	public bool Created => Id is not null;
}

/// <summary>
/// Nixie 1.6 (smart collections phase 1, docs\research\smart-collections-data.md): the requests behind the library index and
/// the Collections page. An ordinary user's requests only; each is null (or empty) on a failure and never throws, except
/// for cancellation where it takes a token, and (1.7.1) an index page the server refuses (<see cref="IndexRefusedException"/>).
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
		IndexPage(library, PeopleFields, startIndex, limit, withTotal, cancellationToken, lean: true); // 1.7.1: lean

	// 1.9 (Collections 2, stream text)
	/// <summary>
	/// Nixie 1.9: the summaries' pass (<see cref="SmartCollections.TextPass"/>): <see cref="ItemFields.Overview"/> and
	/// <see cref="ItemFields.Taglines"/> alone, lean as the people pass (no images, no user data). Each summary is matched against
	/// the categories' word lists as it comes and let go: the index never keeps one.
	/// </summary>
	internal static readonly ItemFields[] TextFields = [ItemFields.Overview, ItemFields.Taglines];

	public Task<BaseItemDtoQueryResult?> GetLibraryTextPage(BaseItemDto library, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken) =>
		IndexPage(library, TextFields, startIndex, limit, withTotal, cancellationToken, lean: true);

	/// <summary>
	/// Nixie 1.9: these titles' summaries by id (lean: the id, <see cref="TextFields"/>, no images, no user data), for the text
	/// pass's titles new since it last ran; null when the server didn't answer. The server refusing (401, 403) ends the pass
	/// (<see cref="IndexRefusedException"/>), as an index page does.
	/// </summary>
	public async Task<IReadOnlyList<BaseItemDto>?> GetTextByIds(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
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
				query.Fields = TextFields;
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
			logger.LogWarning("Summaries of {Count} titles: refused (HTTP {Status})", ids.Count, ex.ResponseStatusCode);
			throw new IndexRefusedException(ex.ResponseStatusCode);
		}
		catch (Exception ex)
		{
			logger.LogWarning("Summaries of {Count} titles: {Error}", ids.Count, ex.GetType().Name); // no ids or text (rule 10)
			return null;
		}
	}
	// end 1.9 (stream text)

	// 1.7.5 (fx1, BH-171-m5): the same page, of the titles the server saved since a time (MinDateLastSaved; /Items has no
	// MinDateCreated, and a title's DateCreated can be its file's, years back)
	public Task<BaseItemDtoQueryResult?> GetLibraryIndexPageSavedSince(BaseItemDto library, DateTimeOffset since, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken) =>
		IndexPage(library, IndexFields, startIndex, limit, withTotal, cancellationToken, savedSince: since);

	// 1.7.1 (the index on a big server): the people pass asks for the people and the id only: no images (the index pass has
	// the poster's tag) and no user data, which cut its answer by about a quarter on the throwaway 25,736-film server
	private async Task<BaseItemDtoQueryResult?> IndexPage(BaseItemDto library, ItemFields[] fields, int startIndex, int limit, bool withTotal, CancellationToken cancellationToken, bool lean = false,
		DateTimeOffset? savedSince = null)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.SortBy = [ItemSortBy.SortName, ItemSortBy.ProductionYear];
				query.SortOrder = [SortOrder.Ascending, SortOrder.Ascending];
				query.MinDateLastSaved = savedSince; // 1.7.5 (BH-171-m5): null for every title
				query.Fields = fields;
				if (lean)
				{
					query.EnableImages = false;
					query.EnableUserData = false;
				}
				else
				{
					query.ImageTypeLimit = 1;
					query.EnableImageTypes = [ImageType.Primary];
				}

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
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
		{
			// 1.7.1 (BH-171-q3): refused: the pass ends here (asked again three times, as halves, it was only refused again)
			logger.LogWarning("Index page {Start}+{Limit}: refused (HTTP {Status})", startIndex, limit, ex.ResponseStatusCode); // no id (rule 10)
			throw new IndexRefusedException(ex.ResponseStatusCode);
		}
		catch (Exception ex)
		{
			logger.LogWarning("Index page {Start}+{Limit} of {Library}: {Error}", startIndex, limit, library.Id, ex.Message);
			return null;
		}
	}

	/// <summary>
	/// Nixie 1.7.1 (the index on a big server): a cheap look at whether the user's films and shows changed since the index
	/// was read: how many there are, and the newest one added, in one request of one title with no fields, images or user
	/// data ("25736:&lt;id&gt;"). Null when the server didn't answer: then nothing is decided on it.
	/// </summary>
	public async Task<string?> GetIndexCheck(CancellationToken cancellationToken)
	{
		try
		{
			var answer = await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series];
				query.Recursive = true;
				query.SortBy = [ItemSortBy.DateCreated, ItemSortBy.SortName];
				query.SortOrder = [SortOrder.Descending, SortOrder.Ascending];
				query.EnableImages = false;
				query.EnableUserData = false;
				query.Limit = 1;
				query.EnableTotalRecordCount = true;
			}, cancellationToken);
			return answer?.TotalRecordCount is { } total ? $"{total}:{answer.Items?.FirstOrDefault()?.Id?.ToString("N") ?? "-"}" : null;
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception ex)
		{
			logger.LogInformation("Library index check: {Error}", ex.Message);
			return null;
		}
	}

	// 1.8.1 (BH-175-m3)
	/// <summary>
	/// Nixie 1.8.1 (BH-175-m3; the review): how many films (or shows) an indexed library has now, as the index reads it (its
	/// kind, recursive), in one request of one title with no fields, images or user data. The library change's read compares
	/// it with what the saved index has of that library: one removed from Movies while one is added to a library the index
	/// skips moved the server's check by 0. Null when the server didn't answer.
	/// </summary>
	public async Task<int?> GetLibraryTitleCount(BaseItemDto library, CancellationToken cancellationToken)
	{
		try
		{
			var answer = await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.ParentId = library.Id;
				query.IncludeItemTypes = IndexKinds(library);
				query.Recursive = true;
				query.EnableImages = false;
				query.EnableUserData = false;
				query.Limit = 1;
				query.EnableTotalRecordCount = true;
			}, cancellationToken);
			return answer?.TotalRecordCount;
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Exception ex)
		{
			logger.LogInformation("A library's count: {Error}", ex.Message);
			return null;
		}
	}
	// end 1.8.1

	// 1.9 (the index's load on a server that isn't the user's): the user's marks brought up to date without reading every
	// title. The review's items 5 and 9: a 401 or 403 ends them (IndexRefusedException); a failure logs its type, never its
	// message (which can carry the server's address)
	// title. MinDateLastSavedForUser can't do it: on 10.11.11 and 12.4.0 it follows the titles' own save date, not the user's
	// data (a film marked played or favourite a minute before isn't among them: tools\qa-servers\live-user-data-since.ps1)
	/// <summary>
	/// Nixie 1.9: the films and episodes the user played, latest first (by the time played), lean: the id, the user data and
	/// an episode's show, no fields or images. Null on a failure.
	/// </summary>
	public Task<BaseItemDtoQueryResult?> GetPlayedLatelyPage(int startIndex, int limit, CancellationToken cancellationToken) =>
		MarksPage([BaseItemKind.Movie, BaseItemKind.Episode], ItemFilter.IsPlayed, [ItemSortBy.DatePlayed, ItemSortBy.SortName], [SortOrder.Descending, SortOrder.Ascending],
			startIndex, limit, withTotal: false, "Played lately", cancellationToken);

	/// <summary>Nixie 1.9: the user's favourite films and shows, by name, lean (the id and the user data); null on a failure.</summary>
	public Task<BaseItemDtoQueryResult?> GetFavoritesPage(int startIndex, int limit, CancellationToken cancellationToken) =>
		MarksPage([BaseItemKind.Movie, BaseItemKind.Series], ItemFilter.IsFavorite, [ItemSortBy.SortName], [SortOrder.Ascending], startIndex, limit, withTotal: true, "Favourites", cancellationToken);

	/// <summary>Nixie 1.9: how many of a show's episodes the user played (one request of one episode); null on a failure.</summary>
	public async Task<int?> GetPlayedEpisodeCount(Guid seriesId, CancellationToken cancellationToken)
	{
		try
		{
			var answer = await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.ParentId = seriesId;
				query.IncludeItemTypes = [BaseItemKind.Episode];
				query.Recursive = true;
				query.Filters = [ItemFilter.IsPlayed];
				query.EnableImages = false;
				query.EnableUserData = false;
				query.Limit = 1;
				query.EnableTotalRecordCount = true;
			}, cancellationToken);
			return answer?.TotalRecordCount;
		}
		catch (Exception) when (cancellationToken.IsCancellationRequested)
		{
			return null;
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
		{
			throw new IndexRefusedException(ex.ResponseStatusCode); // the review's item 5: the marks end there
		}
		catch (Exception ex)
		{
			logger.LogInformation("A show's played episodes: {Error}", ex.GetType().Name); // the review's item 9: no address
			return null;
		}
	}

	/// <summary>Nixie 1.9: these titles' user data (lean: the id and the user data); null on a failure.</summary>
	public async Task<IReadOnlyList<BaseItemDto>?> GetUserDataByIds(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
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
				query.EnableImages = false;
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
			throw new IndexRefusedException(ex.ResponseStatusCode); // the review's item 5
		}
		catch (Exception ex)
		{
			logger.LogInformation("User data of {Count} titles: {Error}", ids.Count, ex.GetType().Name);
			return null;
		}
	}

	private async Task<BaseItemDtoQueryResult?> MarksPage(BaseItemKind[] kinds, ItemFilter filter, ItemSortBy[] sortBy, SortOrder[] order, int startIndex, int limit, bool withTotal,
		string what, CancellationToken cancellationToken)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.IncludeItemTypes = kinds;
				query.Recursive = true;
				query.Filters = [filter];
				query.SortBy = sortBy;
				query.SortOrder = order;
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
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 401 or 403)
		{
			throw new IndexRefusedException(ex.ResponseStatusCode); // the review's item 5: the marks end there
		}
		catch (Exception ex)
		{
			logger.LogInformation("{What} {Start}+{Limit}: {Error}", what, startIndex, limit, ex.GetType().Name); // the review's item 9: no address
			return null;
		}
	}
	// end 1.9

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
	/// <remarks>Nixie 1.6.1 (BH-160-M2): the list read is the one tidy names stay unique against (<see cref="ViewModels.CollectionNames.RememberBoxSets"/>).</remarks>
	public async Task<BaseItemDtoQueryResult?> GetBoxSets(CancellationToken cancellationToken = default)
	{
		var read = await Sets(BaseItemKind.BoxSet, cancellationToken);
		if (read?.Items is { } items)
		{
			ViewModels.CollectionNames.RememberBoxSets(items);
		}

		return read;
	}

	/// <summary>The playlists the user can see.</summary>
	public Task<BaseItemDtoQueryResult?> GetPlaylists(CancellationToken cancellationToken = default) => Sets(BaseItemKind.Playlist, cancellationToken);

	/// <summary>Nixie 1.6.1 (the 1.6 review): a page of box sets or playlists; a server with more is read page after page.</summary>
	internal const int SetsPageSize = 2000;

	/// <summary>Nixie 1.6.1: the most box sets or playlists read (a server's answer that never ends stops here).</summary>
	internal const int SetsMost = 100_000;

	private async Task<BaseItemDtoQueryResult?> Sets(BaseItemKind kind, CancellationToken cancellationToken)
	{
		// 1.6.1 (the 1.6 review): one request of 2,000 read only the first 2,000 by name: every page now, in order
		List<BaseItemDto> all = [];
		int? total = null;
		while (all.Count < SetsMost)
		{
			BaseItemDtoQueryResult? page;
			try
			{
				var start = all.Count;
				page = await _jellyfinApiClient.Items.GetAsync(x =>
				{
					var query = x.QueryParameters;
					query.IncludeItemTypes = [kind];
					query.Recursive = true;
					query.SortBy = [ItemSortBy.SortName];
					query.SortOrder = [SortOrder.Ascending];
					query.Fields = [ItemFields.ProviderIds, ItemFields.ChildCount, ItemFields.SortName, ItemFields.Overview];
					query.ImageTypeLimit = 1;
					query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb];
					query.StartIndex = start;
					query.Limit = SetsPageSize;
				}, cancellationToken);
			}
			catch (Exception) when (cancellationToken.IsCancellationRequested)
			{
				return null;
			}
			catch (Exception ex)
			{
				// the first page: the list could not be read; a later one: what was read stands
				logger.LogWarning("The {Kind} list (from {Start}): {Error}", kind, all.Count, ex.Message);
				return all.Count == 0 ? null : new BaseItemDtoQueryResult { Items = all, TotalRecordCount = total ?? all.Count, StartIndex = 0 };
			}

			if (page is null)
			{
				return all.Count == 0 ? null : new BaseItemDtoQueryResult { Items = all, TotalRecordCount = total ?? all.Count, StartIndex = 0 };
			}

			var items = page.Items ?? [];
			all.AddRange(items.OfType<BaseItemDto>());
			total = page.TotalRecordCount;
			if (items.Count < SetsPageSize || (total is { } known && all.Count >= known))
			{
				break;
			}
		}

		return new BaseItemDtoQueryResult { Items = all, TotalRecordCount = total ?? all.Count, StartIndex = 0 };
	}

	/// <summary>
	/// Nixie 1.7.5 (the 1.6.1 review): whether the playlist is the user's (<c>GET /Playlists/{id}/Users/{userId}</c>, 10.9 and
	/// later): the owner gets 200 (its own permission, CanEdit); a user it is shared with, 200 with their share; another user's
	/// public playlist, 404 ("User permissions not found"), and one they may not see, 404 or 403. Null when the server didn't
	/// answer (Save then goes by the name, as before). The review: a playlist only shared with the user is not theirs: a
	/// read-only share answers CanEdit false; an editing share answers as the owner does (CanEdit true), so the playlist's own
	/// shares (<c>GET /Playlists/{id}</c>, which never lists its owner) tell them apart.
	/// </summary>
	public async Task<bool?> IsOwnPlaylist(Guid playlistId)
	{
		try
		{
			var permission = await _jellyfinApiClient.Playlists[playlistId].Users[UserId].GetAsync();
			if (permission is not { CanEdit: true })
			{
				return false;
			}

			var playlist = await _jellyfinApiClient.Playlists[playlistId].GetAsync();
			return playlist is not null && playlist.Shares?.Any(x => x.UserId == UserId) != true;
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode is 403 or 404)
		{
			return false;
		}
		catch (Exception ex)
		{
			logger.LogInformation("Save as playlist: whose playlist it is couldn't be read: {Error}", ex.Message);
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
