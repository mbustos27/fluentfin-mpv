namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>
/// Franchises the admin didn't box: films grouped by their TMDb collection id, two or more owned, in release order. 1.8.1 (the
/// owner's library, fix 7; the owner's yes: "three or more films"): published (<see cref="BuildContext.Publishing"/>) only with
/// <see cref="Published"/> or more films, or one the user started (a film of it played or begun): the owner's 1,227 franchises,
/// most of them pairs, were "too many". A pair is still made when asked for by its id (a pin never goes: the Collections
/// page's pins are found so), and Next in groups the films itself.
/// </summary>
public sealed class FranchiseBuilder : ISmartCollectionBuilder
{
	public const int Least = 2;

	/// <summary>1.8.1 (fix 7): the films a franchise nobody started needs to be published.</summary>
	public const int Published = 3;

	public SmartCollectionKind Kind => SmartCollectionKind.Franchise;

	/// <summary>1.8.1 (fix 7): a film of it played, or begun (a play count or a last play: the server counts a start).</summary>
	public static bool Started(IEnumerable<IndexEntry> films) => films.Any(x => x.Played || x.PlayCount > 0 || x.LastPlayed is not null);

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		// 1.7.3 (P0-2b): the floor counts titles, not files: two files of one film are not a franchise
		var groups = context.Titles.Entries
			.Where(x => x.Kind == IndexKind.Movie && x.TmdbCollection is not null)
			.GroupBy(x => x.TmdbCollection!)
			.ToList();
		var floor = groups.Where(g => g.Count() >= Least).ToList();
		// 1.7 keyless: TMDb's own name for the franchise when its export file is on; else the name from the films. 1.9 (the
		// owner's library, fix 6): a name two franchises share takes its years ("Godzilla (1954–1975)"), counted over every
		// franchise of two films or more, boxed or published or not, so a pin, a title page and the Collections page name it alike
		string? TmdbName(string key) => context.Outside.FranchiseNames.TryGetValue(key, out var tmdbName) ? tmdbName : null;
		string NameOf(IGrouping<string, IndexEntry> g) => TmdbName(g.Key) is { } tmdb && FranchiseNames.FromTmdb(tmdb) is { Length: > 0 } named
			? named
			: FranchiseNames.Name(Make.ByRelease(g));
		var names = floor.ToDictionary(g => g.Key, NameOf);
		var repeated = FranchiseNames.Repeated(names.Values);
		var made = floor.Where(g => !context.BoxedFranchises.Contains(g.Key))
			.Where(g => !context.Publishing || g.Count() >= Published || Started(g)) // 1.8.1 (fix 7)
			.Select(g =>
			{
				var films = Make.ByRelease(g);
				var name = repeated.Contains(names[g.Key]) ? FranchiseNames.WithYears(names[g.Key], films) : names[g.Key]; // 1.9 (fix 6)
				// 1.8 (P1-4): the reason line says where the films are listed together and counts them
				if (TmdbName(g.Key) is { } tmdbName && FranchiseNames.FromTmdb(tmdbName) is { Length: > 0 })
				{
					return Make.Of(context, $"franchise:{g.Key}", Kind, name, ReasonLine.Of($"TMDb's “{tmdbName.Trim()}”", ReasonCount.Of(films)), hasOwnOrder: true, films,
						why: $"Films in TMDb's “{tmdbName.Trim()}”, in release order") with { TmdbCollection = g.Key };
				}

				return Make.Of(context, $"franchise:{g.Key}", Kind, name, ReasonLine.Of("One collection on TMDb", ReasonCount.Of(films)), hasOwnOrder: true, films,
					why: "Films that TMDb lists in one collection, in release order") with { TmdbCollection = g.Key };
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
