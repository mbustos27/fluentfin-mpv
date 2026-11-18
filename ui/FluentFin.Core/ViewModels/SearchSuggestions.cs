using System.Globalization;
using System.Reactive.Linq;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// Seaglass 1.1.4 (interaction audit, item 2): an item in the search box's list. The box shows <see cref="Name"/> while the
/// arrows are on it (the AutoSuggestBox's TextMemberPath), and Narrator reads <see cref="ToString"/> ("Quiet Brightwater,
/// 2019, series"): the list's items are named from it. It used to be the bare BaseItemDto, read as its type's name.
/// </summary>
public sealed record SearchSuggestion(BaseItemDto Item)
{
	public string Name => Item.Name ?? "";

	public override string ToString() => SearchLabels.For(Item);
}

/// <summary>
/// fluentfin-mpv: a row of the search box's list that is not an item: no results, or a search that failed (choosing it
/// searches again). The box keeps the query (<see cref="Name"/>) while the arrows are on it; Narrator reads the message.
/// </summary>
public sealed record SearchMessage(string Query, string Title, string Message, bool CanRetry)
{
	public string Name => Query;

	public override string ToString() => $"{Title}. {Message}";
}

/// <summary>Seaglass 1.1.4: the list's last row when it shows only some of the matches: the results page.</summary>
public sealed record SearchSeeAll(string Query, int Count)
{
	public string Name => Query;

	public string Title => $"See all {Count} results";

	public override string ToString() => $"See all {Count} results for “{Query}”";
}

/// <summary>The suggestions for one query.</summary>
public sealed record SearchUpdate(string Query, IReadOnlyList<object> Suggestions);

/// <summary>What the search box does when its query is submitted (Enter, a click on a row, the search button).</summary>
public abstract record SearchAction
{
	private SearchAction()
	{
	}

	/// <summary>Opens the item; the box clears.</summary>
	public sealed record Open(BaseItemDto Item) : SearchAction;

	/// <summary>Searches again at once (a failed search's row).</summary>
	public sealed record Retry(string Query) : SearchAction;

	/// <summary>The results page for the query, grouped by type.</summary>
	public sealed record ShowResults(string Query) : SearchAction;

	public sealed record Nothing : SearchAction;
}

/// <summary>
/// fluentfin-mpv: the title bar's search (docs/research/ux-review.md S1, S2): results, "no results", or a failure with a
/// way to try again, instead of an empty list; and only the newest query's answer, never an older one that came back late.
/// Seaglass 1.1.4 (interaction audit, item 2): conventional: the arrows only move through the list (choosing a row puts its
/// name in the box and opens nothing), Enter on a row opens it, Enter on the text opens the results page.
/// </summary>
public static class SearchSuggestions
{
	/// <summary>How many items the list shows (layout audit: 5-6); more matches add a "See all" row (the results page has them all).</summary>
	public const int Shown = 6;

	/// <summary>
	/// Typed queries are searched once the typing pauses for <paramref name="quiet"/>; queries in <paramref name="now"/>
	/// (choosing "try again") at once, and they drop a typed query still waiting for its pause. A newer query drops the answer
	/// of any older one still running.
	/// </summary>
	public static IObservable<SearchUpdate> Pipeline(IObservable<string> typed, IObservable<string> now, Func<string, Task<BaseItemDtoQueryResult?>> search, TimeSpan quiet) =>
		typed.DistinctUntilChanged()
			.Select(query => (Query: query, Wait: quiet))
			.Merge(now.Select(query => (Query: query, Wait: TimeSpan.Zero)))
			.Throttle(x => Observable.Timer(x.Wait))
			.Select(x => x.Query)
			.Select(query => Observable.FromAsync(() => search(query))
				.Catch((Exception _) => Observable.Return<BaseItemDtoQueryResult?>(null))
				.Select(result => new SearchUpdate(query, For(query, result))))
			.Switch();

	/// <summary>What the list shows for a query's answer (null = the search failed).</summary>
	public static IReadOnlyList<object> For(string query, BaseItemDtoQueryResult? result)
	{
		if (result is null)
		{
			return [new SearchMessage(query, FailedTitle, "Choose this to try again.", CanRetry: true)];
		}

		if (result.Items is not { Count: > 0 } items)
		{
			return [new SearchMessage(query, $"No results for “{query}”", "Check the spelling, or try fewer words.", CanRetry: false)];
		}

		List<object> rows = [.. items.Take(Shown).Select(x => new SearchSuggestion(x))];
		if (items.Count > Shown)
		{
			rows.Add(new SearchSeeAll(query, items.Count));
		}

		return rows;
	}

	/// <summary>Seaglass 1.1.4 (layout audit, item 10): a failed search, in plain words (it named the server by its address).</summary>
	public const string FailedTitle = "Can't search right now";

	/// <summary>
	/// The query was submitted: with a row chosen (Enter on it, or a click), that row's action; with none, the results page
	/// for the text. A "no results" row and an empty box do nothing.
	/// </summary>
	public static SearchAction OnSubmitted(object? chosen, string? text) => chosen switch
	{
		SearchSuggestion suggestion => new SearchAction.Open(suggestion.Item),
		BaseItemDto item => new SearchAction.Open(item),
		SearchMessage { CanRetry: true } failed => new SearchAction.Retry(failed.Query),
		SearchSeeAll all => new SearchAction.ShowResults(all.Query),
		null when !string.IsNullOrWhiteSpace(text) => new SearchAction.ShowResults(text.Trim()),
		_ => new SearchAction.Nothing(),
	};
}

/// <summary>Seaglass 1.1.4: what Narrator reads for a search result: its name, its year, what it is.</summary>
public static class SearchLabels
{
	public static string For(BaseItemDto item)
	{
		List<string> parts = [];
		void Add(string? part)
		{
			if (!string.IsNullOrWhiteSpace(part))
			{
				parts.Add(part.Trim());
			}
		}

		Add(item.Type == BaseItemDto_Type.Episode && !string.IsNullOrWhiteSpace(item.SeriesName) ? $"{item.SeriesName.Trim()}: {item.Name}" : item.Name);
		Add(item.ProductionYear?.ToString(CultureInfo.InvariantCulture));
		Add(Kind(item.Type));
		return string.Join(", ", parts);
	}

	/// <summary>The type in words ("movie", "show"), as Narrator reads it; null for a type the search does not return.</summary>
	public static string? Kind(BaseItemDto_Type? type) => type switch
	{
		BaseItemDto_Type.Movie => "movie",
		BaseItemDto_Type.Series => "show",
		BaseItemDto_Type.Episode => "episode",
		BaseItemDto_Type.Person => "person",
		BaseItemDto_Type.BoxSet => "collection",
		_ => null,
	};

	/// <summary>
	/// Seaglass 1.1.4 (layout audit, item 10): the type under a suggestion ("Show", "Movie"); it showed the raw kind
	/// ("Series", and a collection would read "BoxSet").
	/// </summary>
	public static string KindTitle(BaseItemDto_Type? type) => Kind(type) is { Length: > 0 } kind ? char.ToUpperInvariant(kind[0]) + kind[1..] : "";
}

/// <summary>Seaglass 1.1.4: one group of the search results page ("Movies" and its items).</summary>
public sealed record SearchGroup(string Title, IReadOnlyList<BaseItemDto> Items, int? Total = null)
{
	public int Count => Items.Count;

	/// <summary>"Movies (3)", or "Movies (100 of 163)" when the server has more than the page asked for.</summary>
	public string Heading => Total is { } total && total > Count ? $"{Title} ({Count} of {total})" : $"{Title} ({Count})";
}

/// <summary>Seaglass 1.1.4: the results page's groups: movies, then shows, then episodes; empty groups left out.</summary>
public static class SearchResults
{
	private static readonly (BaseItemDto_Type Type, string Title)[] Order =
	[
		(BaseItemDto_Type.Movie, "Movies"),
		(BaseItemDto_Type.Series, "Shows"),
		(BaseItemDto_Type.Episode, "Episodes"),
	];

	/// <summary>What the page asks the server for, one request each (types with a page in Seaglass).</summary>
	public static IReadOnlyList<BaseItemKind> Kinds { get; } = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode];

	/// <summary>
	/// The items by type, in the server's order within a group. Types without a page in Seaglass (people) are left out: a
	/// result that opens nothing is not offered.
	/// </summary>
	public static IReadOnlyList<SearchGroup> Group(IEnumerable<BaseItemDto>? items, IReadOnlyDictionary<BaseItemDto_Type, int>? totals = null)
	{
		var all = items?.ToList() ?? [];
		return [.. Order
			.Select(x => new SearchGroup(x.Title, [.. all.Where(i => i.Type == x.Type)], totals is not null && totals.TryGetValue(x.Type, out var total) ? total : null))
			.Where(g => g.Count > 0)];
	}

	/// <summary>The server's count of matches of a kind (its TotalRecordCount), for the group's heading.</summary>
	public static BaseItemDto_Type TypeOf(BaseItemKind kind) => kind switch
	{
		BaseItemKind.Movie => BaseItemDto_Type.Movie,
		BaseItemKind.Series => BaseItemDto_Type.Series,
		_ => BaseItemDto_Type.Episode,
	};
}
