using FluentFin.Core.Contracts.Services;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Search;

/// <summary>
/// Nixie 1.5 (search): what the PC asks the server (docs/research/search.md, "The request shape Nixie uses"): one
/// <c>/Items</c> request per kind (people through <c>/Persons</c>) and <c>/Search/Hints</c>, together; the query's
/// longest word when the whole query finds nothing (the server matches one substring in the order typed: "night dead"
/// finds nothing, "night" finds Night of the Living Dead); and the shows of strongly matching episodes that did not come
/// back by name.
/// </summary>
public static class SearchRunner
{
	/// <summary>Per kind (Jellyfin 12.x asks its providers for three times this, and counts only those).</summary>
	public const int Limit = 100;

	/// <summary>/Search/Hints' one limit, over every kind.</summary>
	public const int HintLimit = 60;

	/// <summary>At most this many shows are fetched for their episodes (one request each).</summary>
	public const int ShowsForEpisodes = 6;

	/// <summary>The results page's answer: null when every request failed.</summary>
	public static async Task<RankedResults?> Run(IJellyfinClient client, string query)
	{
		// review (m6): nothing to match ("%", "!!!"): no request (the server's LIKE would answer everything)
		if (new SearchQuery(query).IsEmpty)
		{
			return RankedResults.Empty(query);
		}

		var answers = await Ask(client, query);
		if (answers is null)
		{
			return null;
		}

		if (answers.Values.All(a => a.Count == 0) && SearchText.LongestWord(query) is { } word)
		{
			answers = await Ask(client, word) ?? answers;
		}

		var draft = SearchRanking.Build(query, answers);
		var shown = draft.GroupOf(SearchKind.Shows)?.Strong.Concat(draft.GroupOf(SearchKind.Shows)!.Weak).Select(s => s.Item.Id).ToHashSet() ?? [];
		var missing = (draft.GroupOf(SearchKind.Episodes)?.Strong ?? [])
			.Select(e => e.Item.SeriesId)
			.OfType<Guid>()
			.Where(id => !shown.Contains(id))
			.Distinct()
			.Take(ShowsForEpisodes)
			.ToList();
		if (missing.Count == 0)
		{
			return draft;
		}

		var shows = await Task.WhenAll(missing.Select(async id =>
		{
			try
			{
				return await client.GetItem(id);
			}
			catch (Exception)
			{
				return null;
			}
		}));
		return SearchRanking.Build(query, answers, shows.OfType<BaseItemDto>());
	}

	/// <summary>The search box's suggestions (movies and shows), with the same longest-word retry; null when the search failed.</summary>
	public static async Task<BaseItemDtoQueryResult?> Suggest(IJellyfinClient client, string query)
	{
		if (new SearchQuery(query).IsEmpty)
		{
			return new BaseItemDtoQueryResult { Items = [], TotalRecordCount = 0 }; // review (m6): "No results", no request
		}

		var result = await client.Search(query);
		if (result is { Items.Count: 0 } && SearchText.LongestWord(query) is { } word)
		{
			result = await client.Search(word) ?? result;
		}

		return result;
	}

	/// <summary>
	/// Every kind on its own (people through /Persons), and /Search/Hints beside them (the second review: ranking only the
	/// first 100 of each kind can't surface a title the server never returned). The hints' items the kinds did not bring
	/// are fetched by id and added; each item once. Null when every request failed.
	/// </summary>
	private static async Task<Dictionary<SearchKind, IReadOnlyList<BaseItemDto>>?> Ask(IJellyfinClient client, string term)
	{
		var hintsTask = Try(() => client.SearchHints(term, HintLimit));
		var kindTasks = SearchKinds.All.Select(kind => Try(() => kind.Kind == SearchKind.People
			? client.SearchPeople(term, Limit)
			: client.Search(term, [kind.ServerKind], Limit))).ToArray();
		var answers = await Task.WhenAll(kindTasks);
		var hints = await hintsTask;
		if (answers.All(a => a is null) && hints is null)
		{
			return null;
		}

		var lists = SearchKinds.All.Zip(answers).ToDictionary(x => x.First.Kind, x => (x.Second?.Items ?? []).Where(i => i is not null).ToList());
		var seen = lists.Values.SelectMany(l => l).Select(i => i.Id).OfType<Guid>().ToHashSet();
		var missing = (hints?.SearchHints ?? [])
			.Where(h => SearchKinds.KindOf(h.Type) is not null)
			.Select(h => h.Id ?? h.ItemId)
			.OfType<Guid>()
			.Where(seen.Add)
			.ToList();
		if (missing.Count > 0 && await Try(() => client.GetSearchItems(missing)) is { Items: { } found })
		{
			foreach (var item in found)
			{
				if (item is not null && SearchKinds.KindOf(item.Type) is { } kind)
				{
					lists[kind].Add(item);
				}
			}
		}

		return lists.ToDictionary(x => x.Key, x => (IReadOnlyList<BaseItemDto>)x.Value);
	}

	private static async Task<T?> Try<T>(Func<Task<T?>> ask) where T : class
	{
		try
		{
			return await ask();
		}
		catch (Exception)
		{
			return null;
		}
	}
}
