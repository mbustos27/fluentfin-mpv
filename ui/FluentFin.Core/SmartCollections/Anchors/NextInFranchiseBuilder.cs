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
		// the franchises of the films the user finished or rewatched (a favourite alone hasn't started one), in the anchors' order,
		// each with its latest anchor's date
		var started = new List<string>();
		var latest = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
		var playedAt = new Dictionary<Guid, DateTimeOffset?>(); // 1.8.1 (BH-181-m1): each anchor film's play date
		foreach (var anchor in data.Ordered)
		{
			if (anchor.Title.Kind == IndexKind.Movie && (anchor.Why & (AnchorWhy.Finished | AnchorWhy.Rewatched)) != 0
				&& anchor.Title.TmdbCollection is { Length: > 0 } key && franchises.ContainsKey(key))
			{
				playedAt[anchor.Title.Id] = anchor.When;
				if (latest.TryGetValue(key, out var was))
				{
					latest[key] = was is { } before && (anchor.When is not { } when || when < before) ? before : anchor.When;
				}
				else
				{
					started.Add(key);
					latest[key] = anchor.When;
				}
			}
		}

		// 1.8.1 (the owner's library, fix 1): a franchise of a film played in the last three days first, then one with a date, then
		// one with none; among those with none (an index without play dates: the owner's 42 films), the one the user is furthest
		// into first (X-Men at 5 of 7 before Ant-Man at 1 of 3: "Ant-Man and the Wasp: Quantumania" only sorted first by name)
		var now = context.Now;
		var ordered = started
			.Select((key, at) => (Key: key, At: at, When: latest[key], Films: Make.ByRelease(franchises[key])))
			.OrderBy(x => Hold.Recency(x.When, now))
			.ThenByDescending(x => x.When is null && x.Films.Count > 0 ? x.Films.Count(f => f.Played) / (double)x.Films.Count : 0)
			.ThenByDescending(x => x.When is null ? x.Films.Count(f => f.Played) : 0)
			.ThenBy(x => x.At)
			.ToList();

		var made = new List<SmartCollection>();
		var floor = 0;
		foreach (var (key, _, when, films) in ordered)
		{
			var next = films.FirstOrDefault(x => !x.Played);
			if (films.Count < Least || next is null)
			{
				continue;
			}

			floor++;
			// 1.8.1 (BH-180-q3): TMDb's name, else the words its films' names start with, else "Next after" the first film (no
			// "Next in The Frontier and the Mirror films"). The owner's library (fix 11): when none all of them share, the words
			// more than half of them start with ("Next in X-Men", not "Next after X-Men": "X2" shares none). 1.8.1 (BH-181-m1): a
			// title never names the film it offers: when the user hasn't watched the first film (it's the next one), "Next after" the
			// film they did watch in it ("Next after Return to Avalon" offered Return to Avalon)
			var title = context.Outside.FranchiseNames.TryGetValue(key, out var tmdbName) && FranchiseNames.FromTmdb(tmdbName) is { Length: > 0 } named ? NixieTitles.NextIn(named)
				: (FranchiseNames.Common(films) ?? FranchiseNames.Majority(films)) is { } words ? NixieTitles.NextIn(words)
				: NixieTitles.NextAfter(AfterWhich(films, playedAt));
			var reason = ReasonLine.Of(ReasonFacts.Next(next.Name, next.Year), ReasonCount.Watched(films.Count(x => x.Played), films.Count, IndexKind.Movie));
			made.Add(Make.Of(context, $"{IdPrefix}{key}", Kind, title, reason, hasOwnOrder: true, films) with { TmdbCollection = key, AnchoredAt = when }); // 1.8.1 (fix 1)
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

	/// <summary>
	/// 1.8.1 (BH-181-m1): the film "Next after" names: the franchise's first film when the user watched it, else the film of it
	/// they watched last (the latest play date, then the latest by release: <paramref name="films"/> is in release order, and
	/// the name holds from one three-day block to the next). The next film is the first one not watched, so it is never named.
	/// </summary>
	private static string AfterWhich(IReadOnlyList<IndexEntry> films, IReadOnlyDictionary<Guid, DateTimeOffset?> playedAt)
	{
		var oldest = FranchiseNames.Oldest(films);
		if (films.Any(x => x.Played && string.Equals(x.Name.Trim(), oldest, StringComparison.OrdinalIgnoreCase)))
		{
			return oldest;
		}

		var last = films.Select((film, at) => (Film: film, At: at)).Where(x => x.Film.Played)
			.OrderByDescending(x => playedAt.GetValueOrDefault(x.Film.Id) ?? DateTimeOffset.MinValue)
			.ThenByDescending(x => x.At)
			.Select(x => x.Film)
			.FirstOrDefault();
		return last?.Name.Trim() ?? oldest;
	}
}
