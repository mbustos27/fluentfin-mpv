namespace FluentFin.Core.SmartCollections.Anchors;

/// <summary>
/// Nixie 1.8 (P1-2): every person's titles, counting directors and the top three billed actors only (the report's billing
/// gate), so a walk-on part never makes a filmography. One person a name (the server keeps one person a name too),
/// compared trimmed and without case. Made once a build, in one pass over the titles' people. "More from" reads it;
/// Spotlight may.
/// </summary>
public sealed class PeopleIndex
{
	/// <summary>One credit: the title, whether the person directed it, and their billing (0 for a director, 1 to 3 for an actor).</summary>
	public readonly record struct Credit(IndexEntry Title, bool Directed, int Billing);

	private readonly Dictionary<string, (string Name, List<Credit> Credits)> _people;

	private PeopleIndex(Dictionary<string, (string Name, List<Credit> Credits)> people) => _people = people;

	/// <summary>How many people have a credit.</summary>
	public int Count => _people.Count;

	/// <summary>The people of <paramref name="titles"/> (one entry a title), as <paramref name="source"/> bills them.</summary>
	public static PeopleIndex Of(IReadOnlyList<IndexEntry> titles, IAnchorSource source)
	{
		var people = new Dictionary<string, (string Name, List<Credit> Credits)>(StringComparer.OrdinalIgnoreCase);
		foreach (var title in titles)
		{
			if (title is null)
			{
				continue;
			}

			var billed = source.BilledOf(title);
			var actor = 0;
			for (var k = 0; k < billed.Count; k++)
			{
				var person = billed[k];
				var directed = person.Kind == "Director";
				var billing = directed ? 0 : ++actor;
				if ((!directed && billing > IndexAnchorSource.Billed) || KeyOf(person.Name) is not { } key)
				{
					continue;
				}

				if (!people.TryGetValue(key, out var known))
				{
					people[key] = known = (key, new List<Credit>(4));
				}

				// one credit a title (this title's are the last ones added): a director who also acts keeps the directing one
				var list = known.Credits;
				if (list.Count == 0 || !ReferenceEquals(list[^1].Title, title))
				{
					list.Add(new Credit(title, directed, billing));
				}
				else if (directed && !list[^1].Directed)
				{
					list[^1] = new Credit(title, true, 0);
				}
			}
		}

		return new PeopleIndex(people);
	}

	/// <summary>The person's credits (none for a name nobody here has).</summary>
	public IReadOnlyList<Credit> Of(string? name) => KeyOf(name) is { } key && _people.TryGetValue(key, out var known) ? known.Credits : [];

	/// <summary>The person's name as the server first wrote it (trimmed).</summary>
	public string NameOf(string name) => KeyOf(name) is { } key && _people.TryGetValue(key, out var known) ? known.Name : name.Trim();

	/// <summary>Every person with their credits (Spotlight's candidates).</summary>
	public IEnumerable<(string Name, IReadOnlyList<Credit> Credits)> All() => _people.Values.Select(x => (x.Name, (IReadOnlyList<Credit>)x.Credits));

	/// <summary>A person's key: the name trimmed (compared without case); null for no name.</summary>
	public static string? KeyOf(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();
}
