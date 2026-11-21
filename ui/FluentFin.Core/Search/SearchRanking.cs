using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Search;

/// <summary>Nixie 1.5 (search): the kinds the results page shows, each with its chip and its group.</summary>
public enum SearchKind
{
	Shows,
	Movies,
	Episodes,
	Collections,

	/// <summary>Nixie 1.5 (the second review: people search for actors): a person opens "With <name>".</summary>
	People,
}

/// <summary>A kind's server type, its group title and its word ("show").</summary>
public sealed record SearchKindInfo(SearchKind Kind, BaseItemKind ServerKind, BaseItemDto_Type Type, string Title, string Noun, string Nouns);

public static class SearchKinds
{
	/// <summary>The fixed order: ties between groups, and the chips after "All".</summary>
	public static IReadOnlyList<SearchKindInfo> All { get; } =
	[
		new(SearchKind.Shows, BaseItemKind.Series, BaseItemDto_Type.Series, "Shows", "show", "shows"),
		new(SearchKind.Movies, BaseItemKind.Movie, BaseItemDto_Type.Movie, "Movies", "movie", "movies"),
		new(SearchKind.Episodes, BaseItemKind.Episode, BaseItemDto_Type.Episode, "Episodes", "episode", "episodes"),
		new(SearchKind.Collections, BaseItemKind.BoxSet, BaseItemDto_Type.BoxSet, "Collections", "collection", "collections"),
		new(SearchKind.People, BaseItemKind.Person, BaseItemDto_Type.Person, "People", "person", "people"),
	];

	public static SearchKind? KindOf(BaseItemKind? kind) => All.FirstOrDefault(k => k.ServerKind == kind)?.Kind;

	/// <summary>A hint's kind (/Search/Hints names its type with an enum of its own, by the same names).</summary>
	public static SearchKind? KindOf(SearchHint_Type? type) =>
		type is { } t && Enum.TryParse<BaseItemKind>(t.ToString(), out var kind) ? KindOf(kind) : null;

	public static SearchKindInfo Of(SearchKind kind) => All[(int)kind];

	public static SearchKind? KindOf(BaseItemDto_Type? type) => All.FirstOrDefault(k => k.Type == type)?.Kind;
}

/// <summary>An item with its match, and its place in the server's answer (the last tie-break).</summary>
public sealed record RankedItem(BaseItemDto Item, SearchMatch Match, int Order)
{
	/// <summary>A show ranked by one of its episodes (it did not match by its own titles as well).</summary>
	public BaseItemDto? ViaEpisode { get; init; }

	public static int Compare(RankedItem a, RankedItem b)
	{
		var byMatch = a.Match.CompareTo(b.Match);
		return byMatch != 0 ? byMatch : a.Order.CompareTo(b.Order);
	}
}

/// <summary>One kind's matches, best first: the strong ones (shown) and the weaker ones (behind "Show N more").</summary>
public sealed record RankedGroup(SearchKindInfo Kind, IReadOnlyList<RankedItem> Strong, IReadOnlyList<RankedItem> Weak)
{
	public int Count => Strong.Count + Weak.Count;

	public RankedItem Best => Strong.Count > 0 ? Strong[0] : Weak[0];
}

/// <summary>The results page's model: the top result (an exact or near-exact title of any kind), then the groups in the order of their best match.</summary>
public sealed record RankedResults(string Query, RankedItem? Top, IReadOnlyList<RankedGroup> Groups)
{
	public static RankedResults Empty(string query) => new(query, null, []);

	public int Count => Groups.Sum(g => g.Count);

	public RankedGroup? GroupOf(SearchKind kind) => Groups.FirstOrDefault(g => g.Kind.Kind == kind);
}

/// <summary>
/// Nixie 1.5 (search): ranks the server's answers on the PC, the same on every server and with any search plugin
/// (docs/research/search.md): best match first, groups by their best match, weak matches folded away, episodes leading to
/// their show.
/// </summary>
public static class SearchRanking
{
	/// <summary>How much a show ranked by one of its episodes yields to an item that matched by its own titles.</summary>
	private const double ViaEpisode = 0.05;

	/// <summary>The items, best first (the server's order breaks ties). Items of no searched kind (a folder, a genre) are left out.</summary>
	public static IReadOnlyList<RankedItem> Order(string query, IEnumerable<BaseItemDto>? items)
	{
		var q = new SearchQuery(query);
		return [.. (items ?? []).Where(i => i is not null && SearchKinds.KindOf(i.Type) is not null)
			.Select((item, i) => new RankedItem(item, q.Match(item), i))
			.Order(Comparer<RankedItem>.Create(RankedItem.Compare))];
	}

	/// <summary>
	/// The page's model from the server's answers by kind. <paramref name="shows"/> are shows fetched by id for episodes
	/// whose show did not come back by its own name. When nothing matches strongly, every match is shown (nothing folded).
	/// </summary>
	public static RankedResults Build(string query, IReadOnlyDictionary<SearchKind, IReadOnlyList<BaseItemDto>> answers, IEnumerable<BaseItemDto>? shows = null)
	{
		var q = new SearchQuery(query);
		if (q.IsEmpty)
		{
			return RankedResults.Empty(query);
		}

		var ranked = new Dictionary<SearchKind, List<RankedItem>>();
		foreach (var kind in SearchKinds.All)
		{
			var seen = new HashSet<Guid>();
			ranked[kind.Kind] = [.. (answers.TryGetValue(kind.Kind, out var items) ? items : [])
				.Where(i => i is not null && i.Type == kind.Type && (i.Id is not { } id || seen.Add(id)))
				.Select((item, i) => new RankedItem(item, q.Match(item), i))];
		}

		// episodes lead to their show: a show is ranked by its best episode when that is better than its own titles,
		// and a show that did not come back by name is added from the shows fetched for its episodes
		var bestEpisode = ranked[SearchKind.Episodes]
			.Where(e => e.Item.SeriesId is not null)
			.GroupBy(e => e.Item.SeriesId!.Value)
			.ToDictionary(g => g.Key, g => g.Order(Comparer<RankedItem>.Create(RankedItem.Compare)).First());
		var showList = ranked[SearchKind.Shows];
		for (var i = 0; i < showList.Count; i++)
		{
			if (showList[i].Item.Id is { } id && bestEpisode.TryGetValue(id, out var episode) && episode.Match.Less(ViaEpisode).CompareTo(showList[i].Match) < 0)
			{
				showList[i] = showList[i] with { Match = episode.Match.Less(ViaEpisode), ViaEpisode = episode.Item };
			}
		}

		var known = showList.Where(s => s.Item.Id is not null).Select(s => s.Item.Id!.Value).ToHashSet();
		foreach (var show in shows ?? [])
		{
			if (show?.Id is { } id && show.Type == BaseItemDto_Type.Series && bestEpisode.TryGetValue(id, out var episode) && known.Add(id))
			{
				var own = q.Match(show);
				var via = episode.Match.Less(ViaEpisode);
				showList.Add(via.CompareTo(own) < 0
					? new RankedItem(show, via, showList.Count) { ViaEpisode = episode.Item }
					: new RankedItem(show, own, showList.Count));
			}
		}

		var comparer = Comparer<RankedItem>.Create(RankedItem.Compare);
		var anyStrong = ranked.Values.Any(l => l.Any(r => r.Match.IsStrong));
		var groups = SearchKinds.All
			.Select(kind => ranked[kind.Kind].Order(comparer).ToList())
			.Select((list, k) => anyStrong
				? new RankedGroup(SearchKinds.All[k], [.. list.Where(r => r.Match.IsStrong)], [.. list.Where(r => !r.Match.IsStrong)])
				: new RankedGroup(SearchKinds.All[k], list, []))
			.Where(g => g.Count > 0)
			.OrderBy(g => g, Comparer<RankedGroup>.Create((a, b) =>
			{
				var byBest = RankedItem.Compare(a.Best with { Order = 0 }, b.Best with { Order = 0 });
				return byBest != 0 ? byBest : a.Kind.Kind.CompareTo(b.Kind.Kind);
			}))
			.ToList();

		// the top result: the best item of any kind, when its title is whole or its start; between equals, the kind order
		var top = groups
			.Select(g => g.Best)
			.Where(r => r.Match.CanLead)
			.OrderBy(r => r with { Order = 0 }, comparer)
			.FirstOrDefault();

		return new RankedResults(query, top, groups);
	}

	/// <summary>The suggestions' order: strong matches first; the weak ones only when there is nothing strong.</summary>
	public static IReadOnlyList<BaseItemDto> Suggest(string query, IEnumerable<BaseItemDto>? items)
	{
		var ordered = Order(query, items);
		var strong = ordered.Where(r => r.Match.IsStrong).ToList();
		return [.. (strong.Count > 0 ? strong : ordered).Select(r => r.Item)];
	}
}
