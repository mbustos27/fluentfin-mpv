using FluentFin.Core.Outside;

namespace FluentFin.Core.SmartCollections.Builders;

/// <summary>
/// 1.7 keyless: the awards, from Wikidata's lists (Settings &gt; Look up lists online): each award's winners (or nominees)
/// the library has, matched on this PC by TMDb and IMDb ids, oldest first (the order means something: position chips).
/// Nothing without Wikidata's lists.
/// </summary>
public sealed class AwardBuilder : ISmartCollectionBuilder
{
	/// <summary>Owned titles an award needs before it makes a collection.</summary>
	public const int Least = 2;

	public SmartCollectionKind Kind => SmartCollectionKind.Award;

	public IEnumerable<SmartCollection> Build(BuildContext context)
	{
		var awards = context.Outside.Lists.Where(x => x.Kind == OutsideListKind.Award).ToList();
		var made = new List<SmartCollection>();
		if (awards.Count > 0)
		{
			var matcher = context.Matcher; // 1.7.3 (P0-2b): matched to the titles, copies collapsed
			foreach (var award in awards)
			{
				var titles = matcher.MatchAll(award.Items.Select(x => x.Ref));
				if (titles.Count >= Least)
				{
					// 1.8 (P1-4): the reason line names who gives the award and counts the titles here; the page keeps the
					// list's own line ("…, from Wikidata, by year"); no badge of the award it is
					made.Add(Make.Of(context, $"award:{award.Id}", Kind, award.Title, ReasonLine.Of(AwardFacts.BodyOf(award), ReasonCount.Of(titles)),
						hasOwnOrder: true, titles, why: award.Why, award: AwardFacts.AwardOf(award)));
				}
			}
		}

		context.Gates.Count(Kind, awards.Count, made.Count, made.Count); // 1.7.3 (P0-1)
		return made;
	}
}
