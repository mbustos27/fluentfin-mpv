namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>Franchises the admin didn't box: films grouped by their TMDb collection id, two or more owned, in release order.</summary>
public sealed class FranchiseBuilder : ISmartCollectionBuilder
{
	public const int Least = 2;

	public SmartCollectionKind Kind => SmartCollectionKind.Franchise;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		// 1.7.3 (P0-2b): the floor counts titles, not files: two files of one film are not a franchise
		var groups = context.Titles.Entries
			.Where(x => x.Kind == IndexKind.Movie && x.TmdbCollection is not null)
			.GroupBy(x => x.TmdbCollection!)
			.ToList();
		var floor = groups.Where(g => g.Count() >= Least).ToList();
		var made = floor.Where(g => !context.BoxedFranchises.Contains(g.Key))
			.Select(g =>
			{
				var films = Make.ByRelease(g);
				// 1.7 keyless: TMDb's own name for the franchise when its export file is on; else the name from the films
				if (context.Outside.FranchiseNames.TryGetValue(g.Key, out var tmdbName) && FranchiseNames.FromTmdb(tmdbName) is { Length: > 0 } named)
				{
					return Make.Of($"franchise:{g.Key}", Kind, named, $"Your films in TMDb's “{tmdbName.Trim()}”, in release order", hasOwnOrder: true, films) with { TmdbCollection = g.Key };
				}

				var name = FranchiseNames.Name(films);
				return Make.Of($"franchise:{g.Key}", Kind, name, $"Your films that TMDb lists in one collection, in release order", hasOwnOrder: true, films) with { TmdbCollection = g.Key };
			})
			.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
			.ToList();
		context.Gates.Count(Kind, groups.Count, floor.Count, made.Count); // the cap: the franchises an admin's box set already is
		return made;
	}

	/// <summary>
	/// "Also in your library" on an admin's box set: the owned films with the box set's TMDb id that it leaves out, in
	/// release order (none without a TMDb id). 1.7.3 (P0-2b): each film once, and none the box set has a copy of.
	/// </summary>
	public static IReadOnlyList<IndexEntry> AlsoInLibrary(IndexSnapshot index, string? boxSetTmdb, IEnumerable<Guid> inBoxSet)
	{
		if (string.IsNullOrEmpty(boxSetTmdb))
		{
			return [];
		}

		var have = inBoxSet.ToHashSet();
		var titles = CanonicalTitles.Of([.. index.Entries.Where(x => x.Kind == IndexKind.Movie && x.TmdbCollection == boxSetTmdb)]);
		return Make.ByRelease(titles.Entries.Where(x => !titles.CopyIds(x.Id).Any(have.Contains)));
	}
}
