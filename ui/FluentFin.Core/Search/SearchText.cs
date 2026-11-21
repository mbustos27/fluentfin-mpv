using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentFin.Core.Search;

/// <summary>
/// Nixie 1.5 (search): titles and queries in one comparable form (docs/research/search.md). Accents, case, apostrophes and
/// other punctuation count as equal: "It's" = "its", "Amélie" = "amelie", "WALL·E" = "wall-e" = "walle", "M*A*S*H" =
/// "mash". A leading "The", "A" or "An" can be set aside, so "matrix" is the whole of "The Matrix".
/// </summary>
public static partial class SearchText
{
	/// <summary>The words a query may skip without counting against a title ("night of the living dead" = "night living dead").</summary>
	private static readonly HashSet<string> Small = ["the", "a", "an", "of", "and", "in", "on", "at", "to", "for", "or", "de", "la", "le", "el"];

	private static readonly string[] Articles = ["the ", "a ", "an "];

	/// <summary>
	/// Lower case, no accents, apostrophes dropped inside words ("it's" → "its"), every other mark a space, single spaces.
	/// Letters without a decomposition are spelled out (ø → o, æ → ae, ß → ss), as the server's diacritic removal does.
	/// </summary>
	public static string Fold(string? text) => Fold(text, splitApostrophes: false);

	/// <summary>
	/// As <see cref="Fold(string?)"/>; with <paramref name="splitApostrophes"/>, an apostrophe parts words instead ("d'Amélie"
	/// → "d amelie"), so a title's elided words can be found too ("amelie" in "Le Fabuleux Destin d'Amélie Poulain").
	/// </summary>
	public static string Fold(string? text, bool splitApostrophes)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}

		// review (m7): by Rune, so a letter outside the BMP ("𠮷") stays a letter; only the accents of Latin, Greek and Cyrillic
		// go (their combining marks), so ガンダム keeps its voicing marks and never equals カンタム; recomposed at the end
		var decomposed = text.Normalize(NormalizationForm.FormKD);
		var sb = new StringBuilder(decomposed.Length);
		var space = true;
		foreach (var raw in decomposed.EnumerateRunes())
		{
			if (Rune.GetUnicodeCategory(raw) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
			{
				if (!IsAccent(raw) && !space)
				{
					sb.Append(raw.ToString()); // a mark of the word's own script (kana voicing, an Indic vowel sign)
				}

				continue;
			}

			if (raw.IsBmp && IsApostrophe((char)raw.Value) && !splitApostrophes)
			{
				continue; // "it's" is one word, "its"
			}

			var c = Rune.ToLowerInvariant(raw);
			if (Rune.IsLetterOrDigit(c))
			{
				sb.Append(c.IsBmp ? Spelled((char)c.Value) : c.ToString());
				space = false;
			}
			else if (!space)
			{
				sb.Append(' ');
				space = true;
			}
		}

		return sb.ToString().TrimEnd().Normalize(NormalizationForm.FormC);
	}

	/// <summary>The combining marks Latin, Greek and Cyrillic accents decompose into (the only marks folding drops).</summary>
	private static bool IsAccent(Rune mark) => mark.Value is (>= 0x0300 and <= 0x036F) or (>= 0x0483 and <= 0x0489)
		or (>= 0x1AB0 and <= 0x1AFF) or (>= 0x1DC0 and <= 0x1DFF) or (>= 0xFE20 and <= 0xFE2F);

	/// <summary>The folded words ("it's always sunny" → its, always, sunny).</summary>
	public static string[] Words(string? text) => Fold(text) is { Length: > 0 } folded ? folded.Split(' ') : [];

	/// <summary>The folded text without its spaces ("WALL·E" → "walle", "M*A*S*H" → "mash").</summary>
	public static string Squash(string? text) => Fold(text).Replace(" ", "", StringComparison.Ordinal);

	/// <summary>The folded text without a leading "the", "a" or "an" (kept when it is all there is: "The" stays "the").</summary>
	public static string WithoutArticle(string folded)
	{
		foreach (var article in Articles)
		{
			if (folded.Length > article.Length && folded.StartsWith(article, StringComparison.Ordinal))
			{
				return folded[article.Length..];
			}
		}

		return folded;
	}

	/// <summary>A word a query can leave out of a title ("of", "the").</summary>
	public static bool IsSmall(string word) => Small.Contains(word);

	/// <summary>
	/// The server's sort name pads every number to ten digits ("Se7en" sorts as "se0000000007en"): the padding goes, so the
	/// sort name compares as its title does.
	/// </summary>
	public static string Unpad(string? sortName) => string.IsNullOrEmpty(sortName) ? "" : PaddedNumber().Replace(sortName, "");

	/// <summary>The query's longest word, the one a server can still find when the whole query (words out of order) finds nothing.</summary>
	public static string? LongestWord(string? query)
	{
		var all = Words(query);
		return all.Length < 2
			? null
			: all.Where(w => !IsSmall(w) && w.Length >= 3).OrderByDescending(w => w.Length).FirstOrDefault();
	}

	internal static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or 'ʼ' or '`' or '´' or '′';

	private static string Spelled(char c) => c switch
	{
		'ø' => "o",
		'æ' => "ae",
		'œ' => "oe",
		'ß' => "ss",
		'ł' => "l",
		'đ' => "d",
		'ð' => "d",
		'þ' => "th",
		'ı' => "i",
		_ => c.ToString(),
	};

	[GeneratedRegex(@"(?<!\d)0{1,9}(?=\d)")]
	private static partial Regex PaddedNumber();
}
