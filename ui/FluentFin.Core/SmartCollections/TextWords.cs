using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FluentFin.Core.SmartCollections;

// Nixie 1.9 (Collections 2, step 1b, stream text): the categories' summary word lists made ready to match (TextWords), and
// what the summaries' pass (TextPass) found, per server and user (TextMatches). A summary is matched as it comes and let go:
// only the ids of the categories whose words it has are kept, never the text (the survey's "scored at read time and dropped").

/// <summary>
/// Nixie 1.9: the summary word lists of the categories that have one (<see cref="MicroGenreRule.TextAny"/>), matched against a
/// title's summary as whole words, case-folded: "office" is in "An office in Scranton" and "the OFFICE's", never in
/// "officer"; "co-workers" is the two words "co workers" whatever joins them, and "high school" is those two words in a row,
/// within a sentence ("…in high. School starts…" is not). A word is a run of letters and digits (accents kept, the text
/// composed first); anything else (spaces, hyphens, apostrophes, punctuation) ends it. 1.9 (the review, item 2): a category
/// with a <see cref="MicroGenreRule.TextMin"/> of 2 or more needs that many different words of its list in the summary.
/// </summary>
public sealed class TextWords
{
	// the format of what a match keeps: a change here makes every kept match be read again
	private const int Format = 1;

	private readonly Dictionary<string, Phrase[]> _byFirst;
	private readonly Dictionary<string, Phrase[]>.AlternateLookup<ReadOnlySpan<char>> _lookup;

	// a word list's entry after its first word: the words after it, and the categories (by position in Categories) that list it
	private sealed record Phrase(string[] Rest, int Id, int[] Categories);

	// each category's minimum of different words (TextMin), by position in Categories
	private readonly int[] _mins;

	private TextWords(IReadOnlyList<MicroGenre> categories, Dictionary<string, Phrase[]> byFirst, string fingerprint)
	{
		Categories = categories;
		_mins = [.. categories.Select(x => Math.Max(1, x.Rule.TextMin))];
		_byFirst = byFirst;
		_lookup = byFirst.GetAlternateLookup<ReadOnlySpan<char>>();
		Fingerprint = fingerprint;
		Kinds = new HashSet<IndexKind>(categories.Select(x => x.Kind));
	}

	/// <summary>No category of the catalogue has a word list: nothing to read.</summary>
	public static TextWords Empty { get; } = Of([]);

	/// <summary>The categories with a word list, in the catalogue's order.</summary>
	public IReadOnlyList<MicroGenre> Categories { get; }

	/// <summary>The kinds those categories are of (the shipped catalogue's: shows only): only their summaries are read.</summary>
	public IReadOnlySet<IndexKind> Kinds { get; }

	/// <summary>
	/// The word lists' fingerprint (each category's id and words, and this format): matches kept under another one (an update
	/// changed a list) are read again, never taken for this one's.
	/// </summary>
	public string Fingerprint { get; }

	public bool IsEmpty => Categories.Count == 0;

	/// <summary>The word lists of <paramref name="catalogue"/>'s categories.</summary>
	public static TextWords Of(MicroGenreCatalogue catalogue) => Of(catalogue.Categories);

	/// <summary>The word lists of those of <paramref name="categories"/> that have one.</summary>
	public static TextWords Of(IEnumerable<MicroGenre> categories)
	{
		var withText = categories.Where(x => x.Rule.TextAny.Count > 0).ToList();
		var phrases = new Dictionary<string, List<int>>(StringComparer.Ordinal); // the words, joined by a space → categories
		var lines = new List<string>();
		for (var c = 0; c < withText.Count; c++)
		{
			var mine = new SortedSet<string>(StringComparer.Ordinal);
			foreach (var entry in withText[c].Rule.TextAny)
			{
				var words = Words(entry);
				if (words.Count == 0)
				{
					continue;
				}

				var key = string.Join(' ', words);
				mine.Add(key);
				if (!phrases.TryGetValue(key, out var list))
				{
					phrases[key] = list = [];
				}

				if (!list.Contains(c))
				{
					list.Add(c);
				}
			}

			// 1.9 (review item 2): a minimum other than 1 is in the fingerprint (a list's 1 leaves it as it was)
			var min = withText[c].Rule.TextMin is > 1 and var m ? $"\tmin {m}" : "";
			lines.Add($"{withText[c].Id}\t{withText[c].Kind}\t{string.Join('|', mine)}{min}");
		}

		var byFirst = new Dictionary<string, Phrase[]>(StringComparer.Ordinal);
		var id = 0;
		foreach (var group in phrases.GroupBy(x => x.Key.Split(' ')[0], StringComparer.Ordinal))
		{
			byFirst[group.Key] = [.. group.Select(x => new Phrase(x.Key.Split(' ')[1..], id++, [.. x.Value]))];
		}

		lines.Sort(StringComparer.Ordinal);
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{Format}\n{string.Join('\n', lines)}"));
		return new TextWords(withText, byFirst, Convert.ToHexString(hash, 0, 12).ToLowerInvariant());
	}

	/// <summary>
	/// Adds to <paramref name="into"/> the categories (by position in <see cref="Categories"/>) whose word list
	/// <paramref name="text"/> has (as many different words as each one's <see cref="MicroGenreRule.TextMin"/>); false when the
	/// text is empty or blank (nothing said about it).
	/// </summary>
	public bool Match(string? text, ISet<int> into)
	{
		var found = new HashSet<Phrase>();
		var said = Find(text, found);
		CategoriesOf(found, into);
		return said;
	}

	/// <summary>
	/// 1.9 (review item 2): a title's summary is its overview and its taglines: the categories whose words they have together
	/// (two words of a list, one in each, are two), added to <paramref name="into"/>; false when every text is blank.
	/// </summary>
	public bool MatchAll(IEnumerable<string?> texts, ISet<int> into)
	{
		var found = new HashSet<Phrase>();
		var said = false;
		foreach (var text in texts)
		{
			said |= Find(text, found);
		}

		CategoriesOf(found, into);
		return said;
	}

	// the categories the phrases found make: each with as many of its own as its minimum
	private void CategoriesOf(HashSet<Phrase> found, ISet<int> into)
	{
		if (found.Count == 0)
		{
			return;
		}

		Span<int> counts = Categories.Count <= 256 ? stackalloc int[Categories.Count] : new int[Categories.Count];
		foreach (var phrase in found)
		{
			foreach (var category in phrase.Categories)
			{
				if (++counts[category] == _mins[category])
				{
					into.Add(category);
				}
			}
		}
	}

	// the phrases of the word lists text has, added to found; false when the text is empty or blank
	private bool Find(string? text, HashSet<Phrase> found)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return false;
		}

		if (_byFirst.Count == 0)
		{
			return true;
		}

		var source = Composed(text);
		var lower = ArrayPool<char>.Shared.Rent(source.Length);
		var starts = ArrayPool<int>.Shared.Rent(source.Length / 2 + 1);
		var lengths = ArrayPool<int>.Shared.Rent(source.Length / 2 + 1);
		var breaks = ArrayPool<bool>.Shared.Rent(source.Length / 2 + 1);
		try
		{
			var n = Tokens(source, lower, starts, lengths, breaks);
			for (var i = 0; i < n; i++)
			{
				if (!_lookup.TryGetValue(lower.AsSpan(starts[i], lengths[i]), out var candidates))
				{
					continue;
				}

				foreach (var phrase in candidates)
				{
					if (i + phrase.Rest.Length >= n)
					{
						continue; // too few words left for it
					}

					var all = true;
					for (var k = 0; k < phrase.Rest.Length && all; k++)
					{
						var at = i + 1 + k;
						all = !breaks[at] && lower.AsSpan(starts[at], lengths[at]).SequenceEqual(phrase.Rest[k]);
					}

					if (all)
					{
						found.Add(phrase); // once: the same words twice are one word of the list
					}
				}
			}

			return true;
		}
		finally
		{
			ArrayPool<char>.Shared.Return(lower);
			ArrayPool<int>.Shared.Return(starts);
			ArrayPool<int>.Shared.Return(lengths);
			ArrayPool<bool>.Shared.Return(breaks);
		}
	}

	/// <summary>The words of <paramref name="text"/>, case-folded, in order (what <see cref="Match"/> compares).</summary>
	public static IReadOnlyList<string> Words(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return [];
		}

		var source = Composed(text);
		var lower = new char[source.Length];
		var starts = new int[source.Length / 2 + 1];
		var lengths = new int[source.Length / 2 + 1];
		var n = Tokens(source, lower, starts, lengths, new bool[source.Length / 2 + 1]);
		return [.. Enumerable.Range(0, n).Select(i => new string(lower, starts[i], lengths[i]))];
	}

	private static string Composed(string text) => text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);

	// the words of source: lower holds the case-folded text, each word's start and length, and whether a sentence ended
	// before it (breaks: a phrase never runs across one); the count of words
	private static int Tokens(string source, char[] lower, int[] starts, int[] lengths, bool[] breaks)
	{
		var n = 0;
		var start = -1;
		var ended = false;
		for (var i = 0; i <= source.Length; i++)
		{
			var inWord = i < source.Length && IsWordChar(source[i]);
			if (inWord)
			{
				lower[i] = char.ToLowerInvariant(source[i]);
				if (start < 0)
				{
					start = i;
				}
			}
			else
			{
				if (start >= 0)
				{
					starts[n] = start;
					lengths[n] = i - start;
					breaks[n] = ended;
					n++;
					start = -1;
					ended = false;
				}

				ended |= i < source.Length && IsSentenceEnd(source[i]);
			}
		}

		return n;
	}

	// what ends a sentence or a clause a phrase may not run across
	private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or ';' or ':' or '\n' or '\r' or '…' or '(' or ')' or '"' or '“' or '”';

	// a letter or a digit, or a mark that belongs to one (a combining accent the composition left)
	private static bool IsWordChar(char c) =>
		char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}

/// <summary>
/// Nixie 1.9: what the summaries' pass (<see cref="TextPass"/>) found for one server and user: for each title it read, the
/// categories whose words its summary has (none is an answer too), or that it has no summary. A title with no summary is
/// <em>unknown</em> (rule 4: missing data is not a decision): it is in no category, and it is asked again later; a title the
/// pass hasn't read yet is neither. Kept per server and user on this PC, and forgotten at sign-out.
/// </summary>
public sealed class TextMatches
{
	/// <summary>The share of a kind's titles the pass must have read before its categories are made (as the people's 98 %).</summary>
	public const double CoveredShare = 0.98;

	private readonly Dictionary<Guid, string[]?> _titles; // null: read, no summary
	private readonly Dictionary<string, HashSet<Guid>> _byCategory;

	internal TextMatches(string? key, string fingerprint, DateTimeOffset unknownAskedAt, Dictionary<Guid, string[]?> titles)
	{
		Key = key;
		Fingerprint = fingerprint;
		UnknownAskedAt = unknownAskedAt;
		_titles = titles;
		_byCategory = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
		foreach (var (id, categories) in titles)
		{
			if (categories is null)
			{
				Unknown++;
				continue;
			}

			foreach (var category in categories)
			{
				if (!_byCategory.TryGetValue(category, out var set))
				{
					_byCategory[category] = set = [];
				}

				set.Add(id);
			}
		}
	}

	/// <summary>Nothing read (no pass yet, the categories off, signed out).</summary>
	public static TextMatches Empty { get; } = new(null, "", DateTimeOffset.MinValue, []);

	/// <summary>Whose they are (<see cref="Services.HomeCache.FileKey"/>, as <see cref="IndexSnapshot.Key"/>); null for none.</summary>
	public string? Key { get; }

	/// <summary>The word lists they were matched with (<see cref="TextWords.Fingerprint"/>).</summary>
	public string Fingerprint { get; }

	/// <summary>When the titles with no summary were last asked (they are asked again a week later).</summary>
	public DateTimeOffset UnknownAskedAt { get; }

	/// <summary>
	/// The page size the last read of every library left (a page that ran out of time halves it, as the index's pages); the next
	/// one starts there. 0: not known (the index's own <see cref="IndexPages.PageSize"/>).
	/// </summary>
	internal int PageSize { get; init; }

	/// <summary>The titles read: with a summary or without.</summary>
	public int Count => _titles.Count;

	/// <summary>Of <see cref="Count"/>, those with no summary (unknown).</summary>
	public int Unknown { get; }

	/// <summary>Of <see cref="Count"/>, those with a summary.</summary>
	public int Known => Count - Unknown;

	/// <summary>The pass read this title (with a summary or without).</summary>
	public bool Asked(Guid id) => _titles.ContainsKey(id);

	/// <summary>The pass read this title's summary (false for one with none, or not read yet).</summary>
	public bool IsKnown(Guid id) => _titles.TryGetValue(id, out var categories) && categories is not null;

	/// <summary>The pass read this title and it has no summary.</summary>
	public bool IsUnknown(Guid id) => _titles.TryGetValue(id, out var categories) && categories is null;

	/// <summary>Whether this title's summary has the words of the category with this id.</summary>
	public bool Has(string categoryId, Guid id) => _byCategory.TryGetValue(categoryId, out var set) && set.Contains(id);

	/// <summary>The titles whose summary has the words of the category with this id.</summary>
	public IReadOnlyCollection<Guid> Of(string categoryId) => _byCategory.TryGetValue(categoryId, out var set) ? set : [];

	/// <summary>The categories whose words this title's summary has; null when it has none, or wasn't read.</summary>
	public IReadOnlyList<string>? CategoriesOf(Guid id) => _titles.TryGetValue(id, out var categories) ? categories : null;

	/// <summary>Every title read, and its categories (null: no summary).</summary>
	internal IReadOnlyDictionary<Guid, string[]?> Titles => _titles;

	/// <summary>
	/// 1.9 (the review of wip/190-text, item 4): the kinds a pass has read whole: every title of that kind the index had then was
	/// asked (in one pass, or one cut short and the rest by id). Kept across later passes (a top-up that fails takes nothing
	/// away), and saved; the word lists changing start again.
	/// </summary>
	internal IReadOnlySet<IndexKind> CompleteFor { get; init; } = new HashSet<IndexKind>();

	/// <summary>
	/// The pass has read enough of <paramref name="titles"/> (each one's ids: a title's copies) of <paramref name="kind"/> for
	/// categories to be made of them: at least one with a summary (a server with none says nothing), and the kind read whole once
	/// (<see cref="CompleteFor"/>: titles new since then wait for their by-id read without hiding the rest; the review's item 4:
	/// 6 new shows of 256 hid every summary category), or until then at least <see cref="CoveredShare"/> of them asked.
	/// </summary>
	public bool Covers(IReadOnlyCollection<IReadOnlyList<Guid>> titles, IndexKind? kind = null)
	{
		if (titles.Count == 0 || _titles.Count == 0)
		{
			return false;
		}

		int asked = 0, known = 0;
		foreach (var copies in titles)
		{
			bool any = false, summary = false;
			foreach (var id in copies)
			{
				if (_titles.TryGetValue(id, out var categories))
				{
					any = true;
					summary |= categories is not null;
				}
			}

			asked += any ? 1 : 0;
			known += summary ? 1 : 0;
		}

		return known > 0 && ((kind is { } read && CompleteFor.Contains(read)) || asked >= CoveredShare * titles.Count);
	}

	// 1.9: the signed-in user's, for the builders (as Outside.OutsideListsSnapshot.Shared)
	private static TextMatches s_shared = Empty;

	/// <summary>The signed-in user's matches, set by the app's pass (a test's keeps its own); <see cref="Empty"/> for none.</summary>
	public static TextMatches Shared => Volatile.Read(ref s_shared);

	/// <summary>
	/// 1.9 (the review of wip/190-text, item 5): sets the app's matches without a word: the pass sets them in its lock, where a
	/// sign-out clears them, and tells the listeners after (<see cref="Say"/>).
	/// </summary>
	internal static void Put(TextMatches value) => Volatile.Write(ref s_shared, value ?? Empty);

	/// <summary>Tells the listeners the app's matches changed (with them as they are now).</summary>
	internal static void Say() => SharedChanged?.Invoke(Shared);

	/// <summary>The app's matches changed (Home builds its rows again; the Collections page its categories).</summary>
	public static event Action<TextMatches>? SharedChanged;

	/// <summary>The app's matches when they are <paramref name="index"/>'s user's (<see cref="IndexSnapshot.Key"/>), else none.</summary>
	public static TextMatches For(IndexSnapshot index) => Shared is { Key: { } key } shared && key == index.Key ? shared : Empty;
}
