using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Services;

/// <summary>
/// Nixie 1.5 (search): the client's search requests, moved out of JellyfinClient.cs. What is asked and why:
/// docs/research/search.md ("The request shape Nixie uses"); the ranking is the PC's (FluentFin.Core.Search).
/// </summary>
public partial class JellyfinClient
{
	/// <summary>
	/// Seaglass 1.1.4: what the title bar's box suggests: movies and shows (people have no page in Seaglass, so a person's row
	/// opened nothing; episodes, which would crowd them out, are on the results page).
	/// </summary>
	public static BaseItemKind[] SuggestionKinds { get; } = [BaseItemKind.Movie, BaseItemKind.Series];

	/// <summary>
	/// Nixie 1.5: the titles the PC ranks on besides the name (the original title, and the sort name, which also holds an
	/// administrator's forced sort name).
	/// </summary>
	public static ItemFields[] SearchFields { get; } = [ItemFields.OriginalTitle, ItemFields.SortName];

	public Task<BaseItemDtoQueryResult?> Search(string searchTerm) => Search(searchTerm, SuggestionKinds);

	/// <summary>
	/// Seaglass 1.1.4: the matches of these types only (the results page asks for each type on its own). Nixie 1.5: no sort
	/// is asked for (the server's own relevance picks which come back), and the titles to rank on come with them.
	/// </summary>
	public async Task<BaseItemDtoQueryResult?> Search(string searchTerm, IReadOnlyList<BaseItemKind> kinds, int limit = 100)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.SearchTerm = searchTerm;
				query.Recursive = true;
				query.Limit = limit;
				query.IncludeItemTypes = [.. kinds];
				query.Fields = SearchFields;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	/// <summary>
	/// Nixie 1.5 (the second review, 2026-11-20): /Search/Hints, every kind under one limit, people included. On 10.11 it is
	/// the one way to find people (/Items finds none); on 12.x it is the search providers' own order.
	/// </summary>
	public async Task<SearchHintResult?> SearchHints(string searchTerm, int limit)
	{
		try
		{
			return await _jellyfinApiClient.Search.Hints.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.SearchTerm = searchTerm;
				query.Limit = limit;
				query.UserId = UserId;
				query.IncludeMedia = true;
				query.IncludePeople = true;
				query.IncludeGenres = false;
				query.IncludeStudios = false;
				query.IncludeArtists = false;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	/// <summary>Nixie 1.5: /Persons?searchTerm=: people, as cards (their pictures).</summary>
	public async Task<BaseItemDtoQueryResult?> SearchPeople(string searchTerm, int limit)
	{
		try
		{
			return await _jellyfinApiClient.Persons.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.SearchTerm = searchTerm;
				query.Limit = limit;
				query.UserId = UserId;
				query.Fields = SearchFields;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	/// <summary>Nixie 1.5: the hints' items by id (a hint carries no card data), with the titles search ranks on.</summary>
	public async Task<BaseItemDtoQueryResult?> GetSearchItems(IReadOnlyList<Guid> ids)
	{
		if (ids.Count == 0)
		{
			return new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 };
		}

		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.Ids = [.. ids.Select(id => (Guid?)id)];
				query.Fields = SearchFields;
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	/// <summary>Nixie 1.5: a person's films and shows, newest first (the "With Ava Abara" list a person result opens).</summary>
	public async Task<BaseItemDtoQueryResult?> GetPersonItems(Guid personId)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.PersonIds = [personId];
				query.Recursive = true;
				query.IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series];
				query.SortBy = [ItemSortBy.ProductionYear, ItemSortBy.SortName];
				query.SortOrder = [SortOrder.Descending, SortOrder.Ascending];
				query.Fields = LibraryFields;
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Thumb];
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}

	/// <summary>
	/// 1.6.1 (BH-150-m3; the review): the episodes a person is credited on (Jellyfin credits a guest star there only), newest
	/// first, at most <paramref name="limit"/> of them with their total: the page shows one card per show, so a show's regular
	/// cast on a big server isn't hundreds of episodes thrown away. No extra fields: an episode's show (its id, name and poster
	/// tag) comes with it.
	/// </summary>
	public async Task<BaseItemDtoQueryResult?> GetPersonEpisodes(Guid personId, int limit)
	{
		try
		{
			return await _jellyfinApiClient.Items.GetAsync(x =>
			{
				var query = x.QueryParameters;
				query.PersonIds = [personId];
				query.Recursive = true;
				query.IncludeItemTypes = [BaseItemKind.Episode];
				query.SortBy = [ItemSortBy.ProductionYear, ItemSortBy.SortName];
				query.SortOrder = [SortOrder.Descending, SortOrder.Ascending];
				query.Limit = limit;
				query.EnableTotalRecordCount = true;
				query.ImageTypeLimit = 1;
				query.EnableImageTypes = [ImageType.Primary];
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, @"Unhandled exception");
			return null;
		}
	}
}
