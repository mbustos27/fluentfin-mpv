using System.Diagnostics;
using System.Globalization;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2): "More from &lt;person&gt;": a director or top-3 billed actor of an anchor (finished, rewatched or a
/// favourite) with <see cref="Least"/> or more other titles here not watched yet (counting only their directing and top-3
/// billed parts, <see cref="PeopleIndex"/>). Their other titles, the best not watched first and the watched at the end.
/// One a person, in the slots' order: the first anchor's directors, then its actors in billing order, then the next
/// anchor's. Nothing while the index has no people (missing people are not "nobody"). "You finished Arrival · 6 more
/// films, none watched".
/// </summary>
public sealed class MoreFromBuilder(Func<BuildContext, IAnchorSource>? source = null) : ISmartCollectionBuilder
{
	public const string IdPrefix = "more:";

	/// <summary>The gate table's anchored-person floor: other titles not watched yet.</summary>
	public const int Least = 5;

	public SmartCollectionKind Kind => SmartCollectionKind.MoreFrom;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		// a library's presets strip builds every published kind over one library's titles (Whole): a personal slot is no chip
		if (context.Whole)
		{
			return [];
		}

		var from = (source ?? IndexAnchorSource.For)(context);
		if (!from.PeopleKnown)
		{
			context.Gates.Count(Kind, 0, 0, 0);
			// counts only: how far the people are (rule 10)
			var index = context.Index;
			context.Gates.Note(Kind, !index.HasPeople ? "no people in the index yet"
				: index.PeopleDue ? "every library's people still to come"
				: string.Create(System.Globalization.CultureInfo.InvariantCulture,
					$"people of {(int)(100 * (1 - (index.PeopleToAsk.Count / (double)Math.Max(1, index.Entries.Count))))} % of the titles so far"));
			return [];
		}

		var watch = Stopwatch.StartNew();
		var data = AnchoredSlots.DataOf(context, from);
		if (data.Ordered.Count == 0)
		{
			context.Gates.Count(Kind, 0, 0, 0); // nothing finished: no people index made for nothing
			return [];
		}

		var people = data.People;
		var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // PeopleIndex's keys: trimmed, compared without case
		var made = new List<SmartCollection>();
		var candidates = 0;
		foreach (var anchor in data.Ordered)
		{
			if (made.Count == AnchoredSlots.MostCandidates)
			{
				break;
			}

			foreach (var person in from.BilledOf(anchor.Title))
			{
				if (made.Count == AnchoredSlots.MostCandidates)
				{
					break;
				}

				if (PeopleIndex.KeyOf(person.Name) is not { } key || !asked.Add(key))
				{
					continue;
				}

				candidates++;
				var others = people.Of(person.Name).Select(x => x.Title).Where(x => !ReferenceEquals(x, anchor.Title) && x.Id != anchor.Title.Id).ToList();
				var unwatched = others.Where(x => !data.Finished(x)).ToList(); // review (Minor): a show at 90 % is watched, as an anchor is
				if (unwatched.Count < Least)
				{
					continue;
				}

				var watched = others.Where(data.Finished).ToList();
				List<IndexEntry> titles = [.. Best.Of(unwatched, data.Quality), .. Best.Of(watched, data.Quality)];
				var reason = ReasonLine.Of(anchor.Said, ReasonCount.More(others.Count, ReasonCount.OnlyOf(others), unwatched.Count));
				// its order is the collection's own on its page (the best not watched first, the watched last), not A to Z
				// 1.8.1 (the owner's library, fix 1): its anchor's date, which the lineup ranks the personal picks by
				made.Add(Make.Of(context, $"{IdPrefix}{(Make.Key(person.Name) is { Length: > 0 } id ? id : key)}", Kind, NixieTitles.MoreFrom(people.NameOf(person.Name)), reason, hasOwnOrder: false, titles)
					with { InListOrder = true, AnchoredAt = anchor.When });
			}
		}

		context.Gates.Count(Kind, candidates, made.Count, made.Count);
		if (data.Anchors.Count > 0)
		{
			// rule 10: its own time in the census line (the people index is made here, once an index read)
			context.Gates.Note(Kind, string.Create(CultureInfo.InvariantCulture, $"{people.Count} people, {watch.ElapsedMilliseconds} ms"));
		}

		return made;
	}
}
