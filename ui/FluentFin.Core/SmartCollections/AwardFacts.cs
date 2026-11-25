using System.Globalization;
using FluentFin.Core.Outside;

namespace FluentFin.Core.SmartCollections;

/// <summary>One award fact of a title: "won Best Picture" (from Wikidata's award lists, matched on this PC).</summary>
/// <param name="ListId">The list it came from ("award-best-picture").</param>
/// <param name="Award">The award, short ("Best Picture").</param>
/// <param name="Won">Won; false: nominated.</param>
public sealed record AwardFact(string ListId, string Award, bool Won);

/// <summary>
/// Nixie 1.8 (P1-4): the award facts of the library's titles, from Wikidata's award lists (Settings &gt; Look up lists online),
/// matched on this PC: which titles won or were nominated for which award. A Nixie tile shows them as badges ("3 Best
/// Picture winners", <see cref="BadgesFor"/>), the report's "Award facts become badges on tiles". Empty while Wikidata's lists
/// are off. A title that won an award isn't counted as its nominee too.
/// </summary>
public sealed class AwardFacts
{
	/// <summary>Badges a tile shows at most.</summary>
	public const int MostBadges = 2;

	public static AwardFacts Empty { get; } = new([], new Dictionary<Guid, IReadOnlyList<AwardFact>>());

	// each award list's short name and whether it lists winners: the badge's words. A list not named here (one a later
	// version adds) is taken as winners of an award named by its title
	private static readonly Dictionary<string, (string Award, bool Won, string Body)> s_named = new(StringComparer.Ordinal)
	{
		["award-best-picture"] = ("Best Picture", true, "Academy Awards"),
		["award-best-picture-nominees"] = ("Best Picture", false, "Academy Awards"),
		["award-palme-dor"] = ("Palme d'Or", true, "Cannes Film Festival"),
		["award-golden-globe-drama"] = ("Golden Globe", true, "Golden Globe Awards"),
		["award-golden-globe-comedy"] = ("Golden Globe", true, "Golden Globe Awards"),
		["award-bafta-film"] = ("BAFTA Best Film", true, "BAFTA Film Awards"),
		["award-cesar-film"] = ("César Best Film", true, "César Awards"),
		["award-golden-bear"] = ("Golden Bear", true, "Berlin Film Festival"),
		["award-golden-lion"] = ("Golden Lion", true, "Venice Film Festival"),
	};

	private readonly List<string> _order;
	private readonly IReadOnlyDictionary<Guid, IReadOnlyList<AwardFact>> _facts;

	private AwardFacts(List<string> order, IReadOnlyDictionary<Guid, IReadOnlyList<AwardFact>> facts)
	{
		_order = order;
		_facts = facts;
	}

	/// <summary>Titles with at least one fact.</summary>
	public int Count => _facts.Count;

	/// <summary>A title's facts (the awards' order, winners first); none for a title no list names.</summary>
	public IReadOnlyList<AwardFact> Of(Guid id) => _facts.TryGetValue(id, out var facts) ? facts : [];

	/// <summary>The award a list is of ("Best Picture" for both of its lists); null for a list that isn't an award's.</summary>
	public static string? AwardOf(OutsideList list) => list.Kind != OutsideListKind.Award ? null : s_named.TryGetValue(list.Id, out var named) ? named.Award : list.Title;

	/// <summary>Who gives a list's award, for its reason line ("Academy Awards", "Cannes Film Festival"); "From Wikidata" when not named.</summary>
	public static string BodyOf(OutsideList list) => s_named.TryGetValue(list.Id, out var named) ? named.Body : "From Wikidata";

	/// <summary>
	/// The facts of <paramref name="lists"/>' award lists, each matched by <paramref name="matcher"/> (the titles, copies
	/// collapsed: a badge counts a film once).
	/// </summary>
	public static AwardFacts Of(IReadOnlyList<OutsideList> lists, OutsideMatcher matcher)
	{
		var awards = lists.Where(x => x.Kind == OutsideListKind.Award).ToList();
		if (awards.Count == 0)
		{
			return Empty;
		}

		var facts = new Dictionary<Guid, List<AwardFact>>();
		foreach (var list in awards)
		{
			var (award, won) = s_named.TryGetValue(list.Id, out var named) ? (named.Award, named.Won) : (list.Title, true);
			foreach (var title in matcher.MatchAll(list.Items.Select(x => x.Ref)))
			{
				if (!facts.TryGetValue(title.Id, out var of))
				{
					facts[title.Id] = of = [];
				}

				of.Add(new AwardFact(list.Id, award, won));
			}
		}

		var order = awards.Select(x => x.Id).ToList();
		return new AwardFacts(order, facts.ToDictionary(x => x.Key, x => (IReadOnlyList<AwardFact>)Tidy(x.Value, order)));
	}

	// each award once: won over nominated, in the lists' order
	private static List<AwardFact> Tidy(List<AwardFact> facts, List<string> order) =>
		[.. facts.GroupBy(x => x.Award, StringComparer.Ordinal)
			.Select(g => g.OrderByDescending(x => x.Won).ThenBy(x => order.IndexOf(x.ListId)).First())
			.OrderByDescending(x => x.Won).ThenBy(x => order.IndexOf(x.ListId))];

	/// <summary>
	/// The badges of a tile whose collection holds <paramref name="titles"/>: per award, how many of them won it (or, failing
	/// that, were nominated), winners first, then the awards' order; at most <see cref="MostBadges"/>. A collection of an
	/// award (<paramref name="except"/>: "Best Picture") shows no badge of that award.
	/// </summary>
	public IReadOnlyList<CollectionBadge> BadgesFor(IEnumerable<Guid> titles, string? except = null)
	{
		if (_facts.Count == 0)
		{
			return [];
		}

		var counts = new Dictionary<(string Award, bool Won), (int Titles, int Order)>();
		foreach (var id in titles.Distinct())
		{
			foreach (var fact in Of(id))
			{
				if (string.Equals(fact.Award, except, StringComparison.Ordinal))
				{
					continue;
				}

				var key = (fact.Award, fact.Won);
				var order = _order.IndexOf(fact.ListId) is >= 0 and var i ? i : int.MaxValue;
				counts[key] = counts.TryGetValue(key, out var was) ? (was.Titles + 1, Math.Min(was.Order, order)) : (1, order);
			}
		}

		return [.. counts.OrderByDescending(x => x.Key.Won).ThenBy(x => x.Value.Order).ThenByDescending(x => x.Value.Titles)
			.Take(MostBadges)
			.Select(x => new CollectionBadge(Words(x.Key.Award, x.Key.Won, x.Value.Titles), x.Key.Award, x.Key.Won, x.Value.Titles))];
	}

	/// <summary>"Best Picture winner", "3 Best Picture winners", "2 Best Picture nominees".</summary>
	public static string Words(string award, bool won, int titles)
	{
		var what = won ? titles == 1 ? "winner" : "winners" : titles == 1 ? "nominee" : "nominees";
		return titles == 1 ? $"{award} {what}" : $"{titles.ToString("N0", CultureInfo.CurrentCulture)} {award} {what}";
	}
}
