namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.9 (the owner's library, fix 6): the order of the Collections page's Franchises row (its first twelve), where it
/// was A to Z: on the owner's 500 franchises the row opened on "[REC]", "100% Wolf" and "101 Dalmatians". See all stays A to Z.
/// <list type="number">
/// <item>The franchises the user started (a film of it played or begun, as <see cref="Builders.FranchiseBuilder.Started"/>
/// says) with films left: the one played last first, then the one they are furthest into.</item>
/// <item>Those nobody started, the largest and best first: the sum over its films of how far each film's score is above (or
/// below) the mean film's here (<see cref="QualityScore"/>), so a long run of good films leads and a long run of poor ones
/// comes last. A film with no score counts as the mean (missing data isn't a verdict); with no scores at all, the largest
/// first.</item>
/// <item>1.9 (the pre-release fix I, BH-190-m4): last, the ones they watched all of, the one played last first: nothing is
/// left in them, and a user who finished twelve had a row of nothing to watch.</item>
/// </list>
/// Ties go to the larger, then A to Z. Counts the canonical titles (copies once). Off the UI thread: it reads each franchise's
/// films once, never the whole library.
/// </summary>
public static class FranchiseRelevance
{
	// the row's groups, in its order (1.9's fix I: the finished last)
	private const int InProgress = 0;
	private const int Unstarted = 1;
	private const int Finished = 2;

	/// <summary>Each franchise's place in the row (0 first), by its collection's id; franchises it doesn't know aren't in it.</summary>
	public static IReadOnlyDictionary<string, int> Rank(IEnumerable<SmartCollection> franchises, CanonicalTitles titles, QualityScore quality)
	{
		var mean = quality.MeanOf(IndexKind.Movie);
		var scored = franchises
			.Where(x => x.Kind == SmartCollectionKind.Franchise)
			.Select(x =>
			{
				var films = x.ItemIds.Select(titles.TitleOf).OfType<IndexEntry>().DistinctBy(f => f.Id).ToList();
				var started = Builders.FranchiseBuilder.Started(films);
				var watched = films.Count(f => f.Played);
				var left = films.Count == 0 || watched < films.Count;
				return new Scored(
					x.Id,
					x.Title,
					Group: !started ? Unstarted : left ? InProgress : Finished,
					LastPlayed: films.Select(f => f.LastPlayed).Max(),
					Into: films.Count == 0 ? 0 : watched / (double)films.Count,
					Worth: films.Sum(f => quality.Of(f) is { } q ? q - mean : 0),
					Films: Math.Max(films.Count, x.ItemIds.Count));
			})
			.OrderBy(x => x.Group)
			.ThenByDescending(x => x.Group != Unstarted ? x.LastPlayed ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue)
			.ThenByDescending(x => x.Group == InProgress ? x.Into : 0)
			.ThenByDescending(x => x.Group == Unstarted ? Math.Round(x.Worth, 6) : 0)
			.ThenByDescending(x => x.Films)
			.ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
			.ThenBy(x => x.Id, StringComparer.Ordinal);
		var rank = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var franchise in scored)
		{
			rank.TryAdd(franchise.Id, rank.Count);
		}

		return rank;
	}

	/// <summary>The index's: its titles and scores as the lineup has them (made once an index, shared with Home).</summary>
	public static IReadOnlyDictionary<string, int> Rank(IEnumerable<SmartCollection> franchises, IndexSnapshot index)
	{
		var data = LineupData.Of(index);
		return Rank(franchises, data.Titles, data.Quality);
	}

	private sealed record Scored(string Id, string Title, int Group, DateTimeOffset? LastPlayed, double Into, double Worth, int Films);
}
