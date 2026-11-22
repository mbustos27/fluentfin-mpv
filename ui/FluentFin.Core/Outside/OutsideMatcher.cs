using FluentFin.Core.SmartCollections;

namespace FluentFin.Core.Outside;

/// <summary>
/// 1.7: one title of a public list, by the ids the list gives: TMDb's (a film's and a show's are separate numbers, so
/// <see cref="Kind"/> says which, when known), IMDb's and TheTVDB's.
/// </summary>
public sealed record OutsideRef(string? Tmdb = null, string? Imdb = null, string? Tvdb = null, IndexKind? Kind = null);

/// <summary>
/// 1.7: matches public lists against the library index, in memory and on this PC only (plan §3): nothing about the library
/// is ever sent. TMDb ids match a film or a show of the same kind; IMDb ids match either; TheTVDB ids match shows.
/// </summary>
public sealed class OutsideMatcher
{
	private readonly Dictionary<string, IndexEntry> _tmdbMovie = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IndexEntry> _tmdbSeries = new(StringComparer.Ordinal);
	private readonly Dictionary<string, IndexEntry> _imdb = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, IndexEntry> _tvdb = new(StringComparer.Ordinal);

	public OutsideMatcher(IndexSnapshot index)
	{
		foreach (var entry in index.Entries)
		{
			if (Clean(entry.Tmdb) is { } tmdb)
			{
				(entry.Kind == IndexKind.Movie ? _tmdbMovie : _tmdbSeries).TryAdd(tmdb, entry);
			}

			if (Clean(entry.Imdb) is { } imdb)
			{
				_imdb.TryAdd(imdb, entry);
			}

			if (entry.Kind == IndexKind.Series && Clean(entry.Tvdb) is { } tvdb)
			{
				_tvdb.TryAdd(tvdb, entry);
			}
		}
	}

	/// <summary>The library's title for <paramref name="item"/>, or null when the library doesn't have it.</summary>
	public IndexEntry? Match(OutsideRef item)
	{
		if (Clean(item.Tmdb) is { } tmdb)
		{
			if (item.Kind != IndexKind.Series && _tmdbMovie.TryGetValue(tmdb, out var movie))
			{
				return movie;
			}

			if (item.Kind != IndexKind.Movie && _tmdbSeries.TryGetValue(tmdb, out var show))
			{
				return show;
			}
		}

		if (Clean(item.Imdb) is { } imdb && _imdb.TryGetValue(imdb, out var byImdb) && (item.Kind is null || byImdb.Kind == item.Kind))
		{
			return byImdb;
		}

		return item.Kind != IndexKind.Movie && Clean(item.Tvdb) is { } tvdb && _tvdb.TryGetValue(tvdb, out var byTvdb) ? byTvdb : null;
	}

	/// <summary>The library's titles of a list, in the list's order, each once.</summary>
	public IReadOnlyList<IndexEntry> MatchAll(IEnumerable<OutsideRef> items)
	{
		var seen = new HashSet<Guid>();
		var found = new List<IndexEntry>();
		foreach (var item in items)
		{
			if (Match(item) is { } entry && seen.Add(entry.Id))
			{
				found.Add(entry);
			}
		}

		return found;
	}

	private static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();
}
