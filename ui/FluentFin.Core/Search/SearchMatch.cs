using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Search;

/// <summary>Nixie 1.5 (search): how well a title answers a query, best first.</summary>
public enum SearchTier
{
	/// <summary>The whole title ("the matrix", "matrix" and "Matrix, The" for The Matrix).</summary>
	Whole = 0,

	/// <summary>The title's start ("it's always sunny" for It's Always Sunny in Philadelphia).</summary>
	Start = 1,

	/// <summary>All the words, in any order ("night dead" for Night of the Living Dead).</summary>
	AllWords = 2,

	/// <summary>Some of the words, or the query inside a word ("tel" in Sintel).</summary>
	SomeWords = 3,

	/// <summary>None: the server found it for another reason (a loose search plugin, a person, the overview).</summary>
	None = 4,
}

/// <summary>A title's match: its tier, then how close it is within the tier (0 to 1, higher is closer).</summary>
public readonly record struct SearchMatch(SearchTier Tier, double Closeness) : IComparable<SearchMatch>
{
	public static SearchMatch Nothing { get; } = new(SearchTier.None, 0);

	/// <summary>Whole, start or all the words: shown at once. The rest wait behind "Show N more".</summary>
	public bool IsStrong => Tier <= SearchTier.AllWords;

	/// <summary>Whole or start: may lead the page as its top result.</summary>
	public bool CanLead => Tier <= SearchTier.Start;

	/// <summary>Negative when this match is better than <paramref name="other"/>.</summary>
	public int CompareTo(SearchMatch other) => Tier != other.Tier ? Tier.CompareTo(other.Tier) : other.Closeness.CompareTo(Closeness);

	public SearchMatch Less(double by) => this with { Closeness = Math.Max(0, Closeness - by) };

	public static SearchMatch Best(SearchMatch a, SearchMatch b) => a.CompareTo(b) <= 0 ? a : b;
}

/// <summary>
/// Nixie 1.5 (search): a query, folded once, and its match against any title (docs/research/search.md). A whole title
/// beats a title's start, which beats all the words, which beats some of them. Accents, case and punctuation count as
/// equal, a leading "The" or "A" never blocks a match, and the original and sort titles count too.
/// </summary>
public sealed class SearchQuery
{
	private readonly string _squashed;
	private readonly string _squashedWithoutArticle;
	private readonly string[] _words;
	private readonly string[] _keyWords;

	public SearchQuery(string? text)
	{
		Text = text?.Trim() ?? "";
		var folded = SearchText.Fold(Text);
		_squashed = folded.Replace(" ", "", StringComparison.Ordinal);
		_squashedWithoutArticle = SearchText.WithoutArticle(folded).Replace(" ", "", StringComparison.Ordinal);
		_words = SearchText.Words(SearchText.WithoutArticle(folded));
		var key = _words.Where(w => !SearchText.IsSmall(w)).ToArray();
		_keyWords = key.Length > 0 ? key : _words;
	}

	public string Text { get; }

	public bool IsEmpty => _squashed.Length == 0;

	/// <summary>The best of the item's name, original title and sort name (the name wins a tie).</summary>
	public SearchMatch Match(BaseItemDto item)
	{
		var best = Match(item.Name);
		if (!string.IsNullOrWhiteSpace(item.OriginalTitle) && !string.Equals(item.OriginalTitle, item.Name, StringComparison.Ordinal))
		{
			best = SearchMatch.Best(best, Match(item.OriginalTitle).Less(0.02));
		}

		if (!string.IsNullOrWhiteSpace(item.SortName))
		{
			best = SearchMatch.Best(best, Match(SearchText.Unpad(item.SortName)).Less(0.03));
		}

		if (!string.IsNullOrWhiteSpace(item.ForcedSortName))
		{
			best = SearchMatch.Best(best, Match(item.ForcedSortName).Less(0.03));
		}

		return best;
	}

	/// <summary>How well one title answers the query.</summary>
	public SearchMatch Match(string? title)
	{
		if (IsEmpty)
		{
			return SearchMatch.Nothing;
		}

		var folded = SearchText.Fold(title);
		if (folded.Length == 0)
		{
			return SearchMatch.Nothing;
		}

		var squashed = folded.Replace(" ", "", StringComparison.Ordinal);
		var foldedWithoutArticle = SearchText.WithoutArticle(folded);
		var withoutArticle = foldedWithoutArticle.Replace(" ", "", StringComparison.Ordinal);

		// the whole title, with or without its leading article (and the query's)
		if (squashed == _squashed || withoutArticle == _squashed || squashed == _squashedWithoutArticle || withoutArticle == _squashedWithoutArticle)
		{
			return new SearchMatch(SearchTier.Whole, 1);
		}

		// the title's start. Review (M2): a start on the title's whole words ("star" in Star Wars) beats one that ends inside
		// a word ("star" in Starman), however short; then the closer the query comes to the whole title, the better
		var start = 0.0;
		foreach (var (t, words) in ((string, string)[])[(squashed, folded), (withoutArticle, foldedWithoutArticle)])
		{
			foreach (var q in (string[])[_squashed, _squashedWithoutArticle])
			{
				if (t.StartsWith(q, StringComparison.Ordinal))
				{
					var whole = EndsOnWord(words, q.Length);
					start = Math.Max(start, (whole ? 0.5 : 0) + 0.5 * q.Length / t.Length);
				}
			}
		}

		if (start > 0)
		{
			return new SearchMatch(SearchTier.Start, start);
		}

		// the title's words, and its elided ones ("d'Amélie" is "damelie" and "amelie")
		var titleWords = folded.Split(' ');
		if (title!.Any(SearchText.IsApostrophe))
		{
			titleWords = [.. titleWords.Union(SearchText.Fold(title, splitApostrophes: true).Split(' '))];
		}
		var phrase = squashed.Contains(_squashedWithoutArticle, StringComparison.Ordinal);

		// all the words, in any order; small words ("of", "the") may be left out of the title
		var keyMatched = _keyWords.Count(w => WordIn(w, titleWords));
		if (keyMatched == _keyWords.Length)
		{
			var covered = (double)Math.Min(squashed.Length, _squashedWithoutArticle.Length) / squashed.Length;
			return new SearchMatch(SearchTier.AllWords, (phrase ? 0.5 : 0) + 0.5 * covered);
		}

		// some of them, or the query inside a word ("tel" in Sintel)
		var inside = _keyWords.Count(w => w.Length >= 3 && squashed.Contains(w, StringComparison.Ordinal));
		var some = Math.Max(keyMatched, inside);
		if (some > 0 || phrase)
		{
			return new SearchMatch(SearchTier.SomeWords, phrase ? 1 : (double)some / _keyWords.Length);
		}

		return SearchMatch.Nothing;
	}

	/// <summary>Whether <paramref name="length"/> letters of the title's words (spaces left out) end where a word ends.</summary>
	private static bool EndsOnWord(string foldedWords, int length)
	{
		var at = 0;
		foreach (var word in foldedWords.Split(' '))
		{
			at += word.Length;
			if (at == length)
			{
				return true;
			}

			if (at > length)
			{
				return false;
			}
		}

		return false;
	}

	/// <summary>
	/// A query word is in a title when a title word starts with it, or the words from one on run into it ("spiderman" in
	/// "Spider-Man", "walle" in "WALL·E").
	/// </summary>
	private static bool WordIn(string word, string[] titleWords)
	{
		for (var i = 0; i < titleWords.Length; i++)
		{
			if (titleWords[i].StartsWith(word, StringComparison.Ordinal))
			{
				return true;
			}

			if (!word.StartsWith(titleWords[i], StringComparison.Ordinal))
			{
				continue;
			}

			var run = titleWords[i];
			for (var j = i + 1; j < titleWords.Length && run.Length < word.Length; j++)
			{
				run += titleWords[j];
				if (run.StartsWith(word, StringComparison.Ordinal))
				{
					return true;
				}

				if (!word.StartsWith(run, StringComparison.Ordinal))
				{
					break;
				}
			}
		}

		return false;
	}
}
