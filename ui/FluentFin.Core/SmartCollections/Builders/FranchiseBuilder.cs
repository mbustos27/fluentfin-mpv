namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>Franchises the admin didn't box: films grouped by their TMDb collection id, two or more owned, in release order.</summary>
public sealed class FranchiseBuilder : ISmartCollectionBuilder
{
	public const int Least = 2;

	public SmartCollectionKind Kind => SmartCollectionKind.Franchise;

	public IEnumerable<SmartCollection> Build(BuildContext context) =>
		context.Index.Entries
			.Where(x => x.Kind == IndexKind.Movie && x.TmdbCollection is not null)
			.GroupBy(x => x.TmdbCollection!)
			.Where(g => g.Count() >= Least && !context.BoxedFranchises.Contains(g.Key))
			.Select(g =>
			{
				var films = Make.ByRelease(g.DistinctBy(x => x.Id));
				var name = FranchiseNames.Name(films);
				return Make.Of($"franchise:{g.Key}", Kind, name, $"Your films that TMDb lists in one collection, in release order", hasOwnOrder: true, films) with { TmdbCollection = g.Key };
			})
			.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase);

	/// <summary>
	/// "Also in your library" on an admin's box set: the owned films with the box set's TMDb id that it leaves out, in
	/// release order (none without a TMDb id).
	/// </summary>
	public static IReadOnlyList<IndexEntry> AlsoInLibrary(IndexSnapshot index, string? boxSetTmdb, IEnumerable<Guid> inBoxSet)
	{
		if (string.IsNullOrEmpty(boxSetTmdb))
		{
			return [];
		}

		var have = inBoxSet.ToHashSet();
		return Make.ByRelease(index.Entries.Where(x => x.Kind == IndexKind.Movie && x.TmdbCollection == boxSetTmdb && !have.Contains(x.Id)).DistinctBy(x => x.Id));
	}
}
