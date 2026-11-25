using System.Globalization;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2): "Next in &lt;franchise&gt;": a franchise (films TMDb lists in one collection) the user has started (a
/// film of it finished or rewatched), two or more owned and one or more not watched: its films in release order. The
/// franchise of the anchor the slots take first comes first. "Next: Mission: Impossible III (2006) · 2 of 7 films watched".
/// An admin's box set of it doesn't matter here: this is the user's place in it, not the franchise as a shelf.
/// </summary>
public sealed class NextInFranchiseBuilder(Func<BuildContext, IAnchorSource>? source = null) : ISmartCollectionBuilder
{
	public const string IdPrefix = "next:franchise:";

	/// <summary>Owned films a franchise needs (the gate table's franchise floor), one or more of them not watched.</summary>
	public const int Least = 2;

	public SmartCollectionKind Kind => SmartCollectionKind.NextIn;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		// a library's presets strip builds every published kind over one library's titles (Whole): a personal slot is no chip
		if (context.Whole)
		{
			return [];
		}

		var data = AnchoredSlots.DataOf(context, (source ?? IndexAnchorSource.For)(context));
		var franchises = context.Titles.Entries
			.Where(x => x?.Kind == IndexKind.Movie && !string.IsNullOrEmpty(x.TmdbCollection))
			.GroupBy(x => x.TmdbCollection!, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
		// the franchises of the films the user finished or rewatched (a favourite alone hasn't started one), in the anchors' order
		var started = new List<string>();
		var seenKeys = new HashSet<string>(StringComparer.Ordinal);
		foreach (var anchor in data.Ordered)
		{
			if (anchor.Title.Kind == IndexKind.Movie && (anchor.Why & (AnchorWhy.Finished | AnchorWhy.Rewatched)) != 0
				&& anchor.Title.TmdbCollection is { Length: > 0 } key && franchises.ContainsKey(key) && seenKeys.Add(key))
			{
				started.Add(key);
			}
		}

		var made = new List<SmartCollection>();
		var floor = 0;
		foreach (var key in started)
		{
			var films = Make.ByRelease(franchises[key]);
			var next = films.FirstOrDefault(x => !x.Played);
			if (films.Count < Least || next is null)
			{
				continue;
			}

			floor++;
			var name = context.Outside.FranchiseNames.TryGetValue(key, out var tmdbName) && FranchiseNames.FromTmdb(tmdbName) is { Length: > 0 } named ? named : FranchiseNames.Name(films);
			var reason = ReasonLine.Of(ReasonFacts.Next(next.Name, next.Year), ReasonCount.Watched(films.Count(x => x.Played), films.Count, IndexKind.Movie));
			made.Add(Make.Of(context, $"{IdPrefix}{key}", Kind, NixieTitles.NextIn(name), reason, hasOwnOrder: true, films) with { TmdbCollection = key });
			if (made.Count == AnchoredSlots.MostCandidates)
			{
				break;
			}
		}

		context.Gates.Count(Kind, started.Count, floor, made.Count);
		if (data.Anchors.Count > 0)
		{
			context.Gates.Note(Kind, string.Create(CultureInfo.InvariantCulture, $"{data.Anchors.Count} anchors"));
		}

		return made;
	}
}
