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
	public static string Name(IReadOnlyList<IndexEntry> films)
	{
		if (films.Count == 0)
		{
			return "";
		}

		var ordered = films.OrderBy(x => x.Year ?? int.MaxValue).ThenBy(x => x.PremiereDate ?? "").ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
		var oldest = ordered[0].Name.Trim();
		if (films.Count == 1)
		{
			return oldest;
		}

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
		return run.Count == 0 || run.All(w => Articles.Contains(w) || Joining.Contains(w)) || name.Length < 2 ? $"{oldest} films" : name;
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

	private static List<string> Words(string name) => [.. name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

	private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	private static string Bare(string word) => word.TrimEnd(':', '-', '–', '—', ',', ';', '|', '/', '.', '!', '?');
}
