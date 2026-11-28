using System.Globalization;
using System.Text;

namespace Nixie.Collections;

/// <summary>
/// 1.9 (the text review): the shows' summary word lists as the app's summaries pass matches them (TextWords, stream text): whole
/// words, case-folded, a word being a run of letters and digits (and their marks), the text composed first; an entry of several
/// words matches only those words in a row within one sentence. No stems: "vampire" is not in "vampires". <see cref="Rule.TextMin"/>
/// counts different entries. The authors' check of a list against tricky sentences (text-cases.json); the app's own matcher is
/// TextWords, and the census tool's tests keep the two saying the same of every case.
/// </summary>
public static class Text
{
	/// <summary>A text's words, case-folded, each with whether a sentence ended before it.</summary>
	public static IReadOnlyList<(string Word, bool AfterBreak)> Tokens(string? text)
	{
		var tokens = new List<(string, bool)>();
		if (string.IsNullOrEmpty(text))
		{
			return tokens;
		}

		var source = text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
		var word = new StringBuilder();
		var ended = false;
		for (var i = 0; i <= source.Length; i++)
		{
			if (i < source.Length && IsWordChar(source[i]))
			{
				word.Append(char.ToLowerInvariant(source[i]));
				continue;
			}

			if (word.Length > 0)
			{
				tokens.Add((word.ToString(), ended));
				word.Clear();
				ended = false;
			}

			ended |= i < source.Length && IsSentenceEnd(source[i]);
		}

		return tokens;
	}

	/// <summary>An entry's words ("co-workers" → "co workers"): what two entries that match alike share.</summary>
	public static string Key(string entry) => string.Join(' ', Tokens(entry).Select(x => x.Word));

	/// <summary>How many different entries of <paramref name="entries"/> the text has (an entry once however often it appears).</summary>
	public static int Found(IEnumerable<string> entries, string? text)
	{
		var tokens = Tokens(text);
		var found = 0;
		foreach (var key in entries.Select(Key).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal))
		{
			var words = key.Split(' ');
			for (var i = 0; i + words.Length <= tokens.Count; i++)
			{
				var all = true;
				for (var k = 0; k < words.Length && all; k++)
				{
					all = tokens[i + k].Word == words[k] && (k == 0 || !tokens[i + k].AfterBreak);
				}

				if (all)
				{
					found++;
					break;
				}
			}
		}

		return found;
	}

	/// <summary>Whether a summary is in a rule's word list: at least <see cref="Rule.TextMin"/> (else one) of its entries.</summary>
	public static bool Has(Rule rule, string? summary) => rule.TextAny.Length > 0 && Found(rule.TextAny, summary) >= (rule.TextMin ?? 1);

	// what ends a sentence or a clause a phrase may not run across (TextWords.IsSentenceEnd)
	private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or ';' or ':' or '\n' or '\r' or '…' or '(' or ')' or '"' or '“' or '”';

	private static bool IsWordChar(char c) =>
		char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
}
