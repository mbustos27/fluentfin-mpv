using System.Runtime.CompilerServices;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>Nixie 1.8 (P1-2): how often a title was played, and when last (null when not known).</summary>
public readonly record struct TitlePlays(int Count, DateTimeOffset? Last);

/// <summary>
/// Nixie 1.8 (P1-2): what the anchors read of a title beyond the facts every builder reads (played, favourite, a show's
/// played episodes): its play count and last play, and its directors and top three billed actors.
/// <see cref="IndexAnchorSource"/> reads them from the index (<see cref="IndexEntry.PlayCount"/>,
/// <see cref="IndexEntry.LastPlayed"/>, the people step's <see cref="IndexEntry.People"/>); tests give their own.
/// </summary>
public interface IAnchorSource
{
	/// <summary>How often the title (any of its copies) was played, and when last.</summary>
	TitlePlays PlaysOf(IndexEntry title);

	/// <summary>The title's directors, then its first three actors billed, in billing order.</summary>
	IReadOnlyList<IndexPerson> BilledOf(IndexEntry title);

	/// <summary>Whether the index has the people of (nearly) every title (a person slot is made only then: missing people are not "nobody").</summary>
	bool PeopleKnown { get; }
}

/// <summary>
/// Nixie 1.8 (P1-2): the anchors' facts from the library index: each title's play count and last play
/// (<see cref="IndexEntry.PlayCount"/>, <see cref="IndexEntry.LastPlayed"/>) and its people (the people pass, top three
/// billed). A title's copies are one title (<see cref="CanonicalTitles"/>): the most plays and the latest play of any copy.
/// An index saved before 1.8 has neither: a played title counts once, with no date (nobody rewatched; the anchors rotate on
/// the calendar alone) until the day's read fills them.
/// </summary>
public sealed class IndexAnchorSource : IAnchorSource
{
	/// <summary>Actors count only in the top three billed (Jellyfin's own person rows; Kometa's billing depth).</summary>
	public const int Billed = 3;

	private static readonly ConditionalWeakTable<BuildContext, IndexAnchorSource> Made = new();

	private readonly BuildContext _context;

	private IndexAnchorSource(BuildContext context) => _context = context;

	/// <summary>The source of a build (one a context, so what is made of it is made once).</summary>
	public static IndexAnchorSource For(BuildContext context) => Made.GetValue(context, x => new IndexAnchorSource(x));

	// re-review (Minor): not some people, but the people of nearly every title (a cut step's part would make a More from that
	// changes as the rest come)
	public bool PeopleKnown => _context.Index.PeopleCovered;

	// the title is a canonical one: its copies' most plays and latest play (CanonicalTitles merges them); played counts once
	// in an index saved before 1.8, which kept no play count
	public TitlePlays PlaysOf(IndexEntry title) => new(Math.Max(title.PlayCount, title.Played ? 1 : 0), title.LastPlayed);

	public IReadOnlyList<IndexPerson> BilledOf(IndexEntry title) => TopBilled(title.People);

	/// <summary>Directors, then the first <see cref="Billed"/> actors, in the order the server bills them; none for none.</summary>
	public static IReadOnlyList<IndexPerson> TopBilled(IReadOnlyList<IndexPerson>? people)
	{
		if (people is not { Count: > 0 })
		{
			return [];
		}

		// a loop, not LINQ: it runs for every title of a 25,000-title index, twice a build
		var billed = new List<IndexPerson>(Math.Min(people.Count, Billed + 2));
		for (var i = 0; i < people.Count; i++)
		{
			if (people[i] is { Kind: "Director" } director && !string.IsNullOrWhiteSpace(director.Name))
			{
				AddOnce(billed, director);
			}
		}

		var actors = 0;
		for (var i = 0; i < people.Count && actors < Billed; i++)
		{
			if (people[i] is { Kind: "Actor" } actor && !string.IsNullOrWhiteSpace(actor.Name))
			{
				actors++;
				AddOnce(billed, actor);
			}
		}

		return billed;
	}

	private static void AddOnce(List<IndexPerson> billed, IndexPerson person)
	{
		foreach (var known in billed)
		{
			if (known.Kind == person.Kind && string.Equals(known.Name.Trim(), person.Name.Trim(), StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
		}

		billed.Add(person);
	}
}
