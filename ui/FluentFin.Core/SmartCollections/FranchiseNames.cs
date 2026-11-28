namespace FluentFin.Core.SmartCollections;

/// <summary>
/// Nixie 1.6 (the plan's 1.4): a franchise's name from its films, since the server keeps TMDb's collection name but never
/// sends it. The longest run of whole words the films' names start with, joining words and punctuation trimmed from its
/// end ("Harry Potter and the …" gives "Harry Potter"; "The Lord of the Rings: …" gives "The Lord of the Rings"); with
/// nothing in common ("Alien", "Aliens"), the oldest film's name and "films". An admin's box set for the same franchise
/// always wins (the Collections page shows the box set instead).
/// </summary>
public static class FranchiseNames
{
	private static readonly HashSet<string> Joining = new(StringComparer.OrdinalIgnoreCase)
	{
		"and", "the", "of", "a", "an", "&", "in", "on", "at", "to", "for", "part", "chapter", "episode", "vol.", "volume", "-", "–", "—", ":", "|",
	};

	private static readonly HashSet<string> Articles = new(StringComparer.OrdinalIgnoreCase) { "the", "a", "an" };

	/// <summary>The franchise's name, from its films (in any order; the oldest decides the casing and the fallback).</summary>
	public static string Name(IReadOnlyList<IndexEntry> films) =>
		films.Count == 0 ? "" : films.Count == 1 ? Oldest(films) : Common(films) ?? $"{Oldest(films)} films";

	/// <summary>1.8.1 (BH-180-q3): the oldest film's name (by year, then premiere, then name); "" for none.</summary>
	public static string Oldest(IReadOnlyList<IndexEntry> films) => films.Count == 0 ? "" : Ordered(films)[0].Name.Trim();

	private static List<IndexEntry> Ordered(IReadOnlyList<IndexEntry> films) =>
		[.. films.OrderBy(x => x.Year ?? int.MaxValue).ThenBy(x => x.PremiereDate ?? "").ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];

	/// <summary>
	/// 1.8.1 (BH-180-q3): the run of words two or more films' names start with ("Harry Potter"), or null when they have none
	/// worth a name: "Next in" then names the oldest film instead of "Next in The Frontier and the Mirror films".
	/// </summary>
	public static string? Common(IReadOnlyList<IndexEntry> films)
	{
		if (films.Count < 2)
		{
			return null;
		}

		var ordered = Ordered(films);
		var words = ordered.Select(x => Words(x.Name)).ToList();
		var common = 0;
		while (words.All(w => w.Count > common) && words.All(w => Same(w[common], words[0][common])))
		{
			common++;
		}

		var run = words[0].Take(common).ToList();
		// a last word cut short by punctuation ("Rings:" in one, "Rings" in another) still counts, without it
		if (common < words[0].Count && words.All(w => w.Count > common) && words.Select(w => Bare(w[common])).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && Bare(words[0][common]).Length > 0)
		{
			run.Add(Bare(words[0][common]));
		}

		while (run.Count > 0 && (Joining.Contains(run[^1]) || Bare(run[^1]).Length == 0))
		{
			run.RemoveAt(run.Count - 1);
		}

		if (run.Count > 0)
		{
			run[^1] = run[^1].TrimEnd(':', '-', '–', '—', ',', ';', '|', '/', '.');
		}

		var name = string.Join(' ', run).Trim();
		return run.Count == 0 || run.All(w => Articles.Contains(w) || Joining.Contains(w)) || name.Length < 2 ? null : name;
	}

	/// <summary>
	/// 1.8.1 (the owner's library, fix 11): the run of words more than half of the films' names start with, for "Next in" only:
	/// X-Men's seven films share no first word ("X2"), so "Next in" said "Next after X-Men", while five of them start
	/// "X-Men": "Next in X-Men". Each word compared as <see cref="Common"/> compares the last (its closing punctuation apart);
	/// the oldest of the names in the run writes it. Null when no word is shared by more than half.
	/// </summary>
	public static string? Majority(IReadOnlyList<IndexEntry> films)
	{
		if (films.Count < 2)
		{
			return null;
		}

		var need = (films.Count / 2) + 1;
		var names = Ordered(films).Select(x => Words(x.Name)).ToList();
		var run = new List<string>();
		for (var i = 0; ; i++)
		{
			var at = i;
			var most = names.Where(w => w.Count > at && Bare(w[at]).Length > 0)
				.GroupBy(w => Bare(w[at]), StringComparer.OrdinalIgnoreCase)
				.OrderByDescending(g => g.Count())
				.FirstOrDefault();
			if (most is null || most.Count() < need)
			{
				break;
			}

			names = [.. most];
			run.Add(names[0][i]);
		}

		while (run.Count > 0 && (Joining.Contains(run[^1]) || Bare(run[^1]).Length == 0))
		{
			run.RemoveAt(run.Count - 1);
		}

		if (run.Count > 0)
		{
			run[^1] = run[^1].TrimEnd(':', '-', '–', '—', ',', ';', '|', '/', '.');
		}

		var name = string.Join(' ', run).Trim();
		return run.Count == 0 || run.All(w => Articles.Contains(w) || Joining.Contains(w)) || name.Length < 2 ? null : name;
	}

	// 1.7 keyless
	/// <summary>
	/// 1.7: a franchise's name from TMDb's collection export ("The Mummy Collection"): without its closing "Collection" word,
	/// as the eyebrow already says "Franchise" ("The Mummy"); TMDb's name as it is when that leaves nothing.
	/// </summary>
	public static string FromTmdb(string? tmdbName)
	{
		var name = (tmdbName ?? "").Trim();
		foreach (var suffix in new[] { " - Collection", " Collection", " collection" })
		{
			if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
			{
				return name[..^suffix.Length].TrimEnd(' ', '-', '–', ':');
			}
		}

		return name;
	}
	// end 1.7 keyless

	// 1.9 (the owner's library, fix 6)
	/// <summary>
	/// 1.9 (the owner's library, fix 6): the names two or more franchises share, compared without case ("Godzilla" three
	/// times, "Batman" twice on the owner's library). Each of those franchises is then named with its years (<see cref="WithYears"/>).
	/// </summary>
	public static IReadOnlySet<string> Repeated(IEnumerable<string> names) =>
		names.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).GroupBy(x => x, StringComparer.CurrentCultureIgnoreCase)
			.Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

	/// <summary>
	/// 1.9 (fix 6): a repeated name with its films' years: "Godzilla (1954–1975)", "Halloween (2018)" when they all came out in
	/// one year. The name as it is when none of the films has a year (missing data adds nothing).
	/// </summary>
	public static string WithYears(string name, IEnumerable<IndexEntry> films)
	{
		var years = films.Select(x => x.Year).OfType<int>().Where(x => x > 0).ToList();
		if (years.Count == 0)
		{
			return name;
		}

		var (first, last) = (years.Min(), years.Max());
		return first == last
			? $"{name.Trim()} ({first.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
			: $"{name.Trim()} ({first.ToString(System.Globalization.CultureInfo.InvariantCulture)}–{last.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
	}
	// end 1.9

	private static List<string> Words(string name) => [.. name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

	private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	private static string Bare(string word) => word.TrimEnd(':', '-', '–', '—', ',', ';', '|', '/', '.', '!', '?');
}
