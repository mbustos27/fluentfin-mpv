namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.7.3 (P0-2b): the index's titles with the copies collapsed, which every builder counts. Copies are separate
/// files of one title with ids of their own (a film in Movies and in 4K Movies; two cuts in one library): each counted, so
/// two files of one film made a "franchise".
/// <para>
/// One title is: the same TMDb id (films and shows apart, as TMDb numbers them); else, for a copy with no TMDb id, the same
/// IMDb id (joining a TMDb title that has it); else, for a show with neither, the same TheTVDB id; else, only for a copy
/// with no id at all, the same name and year. Two titles with the same name and year but other ids stay two. A placeholder
/// id (TMDb or TheTVDB 0 or less, an IMDb id that isn't "tt" and digits) is no id.
/// </para>
/// <para>
/// The title merges its copies' tags, genres, studios and people; a film counts as played when any copy is, a show only
/// when every copy is (with the most episodes left of its copies); a favourite when any copy is; the first copy with
/// artwork represents it (its id is the one collections hold). Never throws on a damaged saved entry
/// (a null list, a null name): that copy is merged as far as it can be, else kept alone.
/// </para>
/// </summary>
public sealed class CanonicalTitles
{
	private readonly Dictionary<Guid, int> _titleOf;
	private readonly Guid[][] _copies;

	private CanonicalTitles(IReadOnlyList<IndexEntry> titles, Guid[][] copies, Dictionary<Guid, int> titleOf, int entries)
	{
		Entries = titles;
		_copies = copies;
		_titleOf = titleOf;
		EntryCount = entries;
	}

	/// <summary>One entry a title, in the order of each title's first copy in the index.</summary>
	public IReadOnlyList<IndexEntry> Entries { get; }

	/// <summary>The index's entries before collapsing (a title listed twice by one id counted once).</summary>
	public int EntryCount { get; }

	/// <summary>How many files of the title with this id (any copy's id) the index has: 1 for a title with no other copy, 0 for an id it doesn't have.</summary>
	public int CopiesOf(Guid id) => _titleOf.TryGetValue(id, out var title) ? _copies[title].Length : 0;

	/// <summary>Every copy's id of the title with this id (any copy's id), the representing one first; empty for an id the index doesn't have.</summary>
	public IReadOnlyList<Guid> CopyIds(Guid id) => _titleOf.TryGetValue(id, out var title) ? _copies[title] : [];

	/// <summary>The title a copy (by its id) belongs to; null for an id the index doesn't have.</summary>
	public IndexEntry? TitleOf(Guid id) => _titleOf.TryGetValue(id, out var title) ? Entries[title] : null;

	/// <summary>Each title once: nothing collapsed (the fallback when collapsing failed).</summary>
	public static CanonicalTitles Each(IReadOnlyList<IndexEntry> entries)
	{
		var titles = new List<IndexEntry>(entries.Count);
		var titleOf = new Dictionary<Guid, int>(entries.Count);
		foreach (var entry in entries)
		{
			if (entry is not null && titleOf.TryAdd(entry.Id, titles.Count))
			{
				titles.Add(entry);
			}
		}

		return new CanonicalTitles(titles, [.. titles.Select(x => new[] { x.Id })], titleOf, titles.Count);
	}

	/// <summary>The titles of <paramref name="entries"/>, copies collapsed (see the class).</summary>
	public static CanonicalTitles Of(IReadOnlyList<IndexEntry> entries)
	{
		// each id once (a title listed twice by one id is one entry: the first)
		var once = new List<IndexEntry>(entries.Count);
		var seen = new HashSet<Guid>(entries.Count);
		foreach (var entry in entries)
		{
			if (entry is not null && seen.Add(entry.Id))
			{
				once.Add(entry);
			}
		}

		var group = new int[once.Count];
		var groups = 0;
		var tmdb = new Dictionary<(IndexKind, string), int>();
		var imdb = new Dictionary<(IndexKind, string), int>();
		var tvdb = new Dictionary<(IndexKind, string), int>();
		var named = new Dictionary<(IndexKind, string, int), int>();

		// 1. TMDb: the strongest; its titles' IMDb and TheTVDB ids then take in copies that have only those
		for (var i = 0; i < once.Count; i++)
		{
			if (Number(once[i].Tmdb) is { } id)
			{
				group[i] = Into(tmdb, (once[i].Kind, id), ref groups);
				Note(imdb, once[i].Kind, ImdbId(once[i].Imdb), group[i]);
				Note(tvdb, once[i].Kind, Number(once[i].Tvdb), group[i]);
			}
			else
			{
				group[i] = -1;
			}
		}

		// 2. IMDb, for a copy with no TMDb id
		for (var i = 0; i < once.Count; i++)
		{
			if (group[i] < 0 && ImdbId(once[i].Imdb) is { } id)
			{
				group[i] = Into(imdb, (once[i].Kind, id), ref groups);
				Note(tvdb, once[i].Kind, Number(once[i].Tvdb), group[i]);
			}
		}

		// 3. TheTVDB, for a copy with neither
		for (var i = 0; i < once.Count; i++)
		{
			if (group[i] < 0 && Number(once[i].Tvdb) is { } id)
			{
				group[i] = Into(tvdb, (once[i].Kind, id), ref groups);
			}
		}

		// 4. name and year, only for a copy with no id at all (and both a name and a year); else alone
		for (var i = 0; i < once.Count; i++)
		{
			if (group[i] >= 0)
			{
				continue;
			}

			group[i] = NameKey(once[i]) is { } name && once[i].Year is { } year ? Into(named, (once[i].Kind, name, year), ref groups) : groups++;
		}

		// the titles, in the order of their first copy
		var members = new List<int>[groups];
		var order = new List<int>(groups);
		for (var i = 0; i < once.Count; i++)
		{
			if (members[group[i]] is not { } list)
			{
				members[group[i]] = list = [];
				order.Add(group[i]);
			}

			list.Add(i);
		}

		var titles = new List<IndexEntry>(order.Count);
		var copies = new Guid[order.Count][];
		var titleOf = new Dictionary<Guid, int>(once.Count);
		foreach (var g in order)
		{
			if (members[g] is [var only])
			{
				titleOf[once[only].Id] = titles.Count;
				copies[titles.Count] = [once[only].Id];
				titles.Add(once[only]);
				continue;
			}

			var copiesOfTitle = members[g].Select(i => once[i]).ToList();
			var title = copiesOfTitle.Count == 1 ? copiesOfTitle[0] : Merge(copiesOfTitle);
			var ids = new[] { title.Id }.Concat(copiesOfTitle.Select(x => x.Id).Where(x => x != title.Id)).ToArray();
			foreach (var id in ids)
			{
				titleOf[id] = titles.Count;
			}

			copies[titles.Count] = ids;
			titles.Add(title);
		}

		return new CanonicalTitles(titles, copies, titleOf, once.Count);
	}

	private static int Into<TKey>(Dictionary<TKey, int> keys, TKey key, ref int groups) where TKey : notnull
	{
		if (!keys.TryGetValue(key, out var g))
		{
			keys[key] = g = groups++;
		}

		return g;
	}

	// a TMDb title's other ids: a copy with only those joins it (the first title to have the id keeps it)
	private static void Note(Dictionary<(IndexKind, string), int> keys, IndexKind kind, string? id, int g)
	{
		if (id is not null)
		{
			keys.TryAdd((kind, id), g);
		}
	}

	private static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToLowerInvariant();

	// 1.7.3 (M1's review): a placeholder id is no id, or it merged different films ("0" on every unmatched file). TMDb's and
	// TheTVDB's ids are whole numbers above 0; IMDb's are "tt" and digits.
	private static string? Number(string? id) =>
		long.TryParse(Clean(id), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0
			? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
			: null;

	private static string? ImdbId(string? id) =>
		Clean(id) is { Length: > 2 } imdb && imdb.StartsWith("tt", StringComparison.Ordinal) && imdb.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0 ? imdb : null;

	// the name, lower case, letters and digits only ("The Thing!" and "the thing" are one); null for none
	private static string? NameKey(IndexEntry entry)
	{
		var name = entry.Name is null ? "" : new string([.. entry.Name.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
		return name.Length == 0 ? null : name;
	}

	// one title of its copies: the first with artwork represents it; lists merged; played or a favourite when any copy is
	private static IndexEntry Merge(List<IndexEntry> copies)
	{
		var first = copies.FirstOrDefault(x => x.PrimaryTag is not null) ?? copies[0];
		var rest = copies.Where(x => !ReferenceEquals(x, first)).ToList();
		IEnumerable<IndexEntry> all = [first, .. rest];
		return first with
		{
			Name = first.Name ?? rest.Select(x => x.Name).FirstOrDefault(x => x is not null) ?? "",
			LibraryIds = [.. all.SelectMany(x => (Guid[])[x.LibraryId, .. x.LibraryIds ?? []]).Where(x => x != Guid.Empty).Distinct()],
			Year = first.Year ?? rest.Select(x => x.Year).FirstOrDefault(x => x is not null),
			PremiereDate = first.PremiereDate ?? rest.Select(x => x.PremiereDate).FirstOrDefault(x => x is not null),
			TmdbCollection = first.TmdbCollection ?? rest.Select(x => x.TmdbCollection).FirstOrDefault(x => x is not null),
			Tmdb = first.Tmdb ?? rest.Select(x => x.Tmdb).FirstOrDefault(x => x is not null),
			Imdb = first.Imdb ?? rest.Select(x => x.Imdb).FirstOrDefault(x => x is not null),
			Tvdb = first.Tvdb ?? rest.Select(x => x.Tvdb).FirstOrDefault(x => x is not null),
			Tags = Union(all.Select(x => x.Tags)),
			Genres = Union(all.Select(x => x.Genres)),
			Studios = Union(all.Select(x => x.Studios)),
			Countries = first.Countries is { Length: > 0 } countries ? countries : rest.Select(x => x.Countries).FirstOrDefault(x => x is { Length: > 0 }) ?? first.Countries ?? [],
			Rating = first.Rating ?? rest.Select(x => x.Rating).FirstOrDefault(x => x is not null),
			Critic = first.Critic ?? rest.Select(x => x.Critic).FirstOrDefault(x => x is not null),
			TagCount = all.Max(x => x.TagCount),
			OfficialRating = first.OfficialRating ?? rest.Select(x => x.OfficialRating).FirstOrDefault(x => x is not null),
			RunTimeTicks = first.RunTimeTicks ?? rest.Select(x => x.RunTimeTicks).FirstOrDefault(x => x is not null),
			// 1.7.3 (M1's review): a show is played only when every copy is, its episodes left those of the copy with most
			// left: the lowest count, and played when any copy was, lost a half-watched show its copy in another library
			// (season 1 watched there) from Unfinished series and Acclaimed. A film is played when any copy is.
			Played = first.Kind == IndexKind.Series ? all.All(x => x.Played) : all.Any(x => x.Played),
			Favorite = all.Any(x => x.Favorite),
			Unplayed = first.Kind == IndexKind.Series
				? all.Select(x => x.Unplayed).Where(x => x is not null).Max()
				: all.Select(x => x.Unplayed).Where(x => x is not null).Min(),
			People = [.. all.SelectMany(x => x.People ?? []).Where(p => p?.Name is not null).DistinctBy(p => (p.Kind, p.Name.ToLowerInvariant()))],
		};
	}

	private static string[] Union(IEnumerable<string[]?> lists) =>
		[.. lists.SelectMany(x => x ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)];
}
