using System.Reactive.Linq;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.ViewModels;

/// <summary>
/// fluentfin-mpv: a row of the search box's list that is not an item: no results, or a search that failed (choosing it
/// searches again). Its text is the query, so moving onto it with the arrow keys leaves the box as typed.
/// </summary>
public sealed record SearchMessage(string Query, string Title, string Message, bool CanRetry)
{
	public override string ToString() => Query;
}

/// <summary>The suggestions for one query.</summary>
public sealed record SearchUpdate(string Query, IReadOnlyList<object> Suggestions);

/// <summary>
/// fluentfin-mpv: the title bar's search (docs/research/ux-review.md S1, S2): results, "no results", or a failure with a
/// way to try again, instead of an empty list; and only the newest query's answer, never an older one that came back late.
/// </summary>
public static class SearchSuggestions
{
	/// <summary>
	/// Typed queries are searched once the typing pauses for <paramref name="quiet"/>; queries in <paramref name="now"/>
	/// (Enter, or choosing "try again") at once. A newer query drops the answer of any older one still running.
	/// </summary>
	public static IObservable<SearchUpdate> Pipeline(IObservable<string> typed, IObservable<string> now, Func<string, Task<BaseItemDtoQueryResult?>> search, TimeSpan quiet) =>
		typed.DistinctUntilChanged()
			.Throttle(quiet)
			.Merge(now)
			.Select(query => Observable.FromAsync(() => search(query))
				.Catch((Exception _) => Observable.Return<BaseItemDtoQueryResult?>(null))
				.Select(result => new SearchUpdate(query, For(query, result))))
			.Switch();

	/// <summary>What the list shows for a query's answer (null = the search failed).</summary>
	public static IReadOnlyList<object> For(string query, BaseItemDtoQueryResult? result)
	{
		if (result is null)
		{
			return [new SearchMessage(query, $"Couldn't search {LoadMessages.ServerName()}", "Choose this to try again.", CanRetry: true)];
		}

		if (result.Items is not { Count: > 0 } items)
		{
			return [new SearchMessage(query, $"No results for “{query}”", "Check the spelling, or try fewer words.", CanRetry: false)];
		}

		return [.. items];
	}
}
