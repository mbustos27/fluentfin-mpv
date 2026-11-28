using System.Text.Json;
using FluentFin.Core.SmartCollections;
using Nixie.Collections;
using static Census.Tests.Fx;

namespace Census.Tests;

/// <summary>
/// 1.9 (the text review): the summary word lists as the app's summaries pass reads them (whole words, case-folded, phrases within
/// a sentence, textMin different entries), and every list against text-cases.json's sentences, the review's false matches among
/// them.
/// </summary>
public class TextTests
{
	[Fact]
	public void Words_AreWhole_CaseFolded_AndSplitAtHyphensAndApostrophes()
	{
		Assert.Equal(1, Text.Found(["office"], "An office in Scranton"));
		Assert.Equal(1, Text.Found(["office"], "the OFFICE's printer"));
		Assert.Equal(0, Text.Found(["office"], "a police officer"));
		Assert.Equal(1, Text.Found(["co-workers"], "his co workers"));
		Assert.Equal(1, Text.Found(["co workers"], "his co-workers"));
		Assert.Equal(1, Text.Found(["school"], "an old-school con man")); // why "school" alone left the lists
		Assert.Equal(0, Text.Found(["vampire"], "four vampires")); // no stems
		Assert.Equal(1, Text.Found(["café"], "a CAFÉ in Paris")); // composed first
	}

	[Fact]
	public void APhrase_IsItsWordsInARow_WithinOneSentence()
	{
		Assert.Equal(1, Text.Found(["high school"], "students at a high school"));
		Assert.Equal(0, Text.Found(["high school"], "spirits ran high. School started"));
		Assert.Equal(0, Text.Found(["high school"], "a high (school) dropout"));
		Assert.Equal(0, Text.Found(["small town"], "a small, quiet town"));
	}

	[Fact]
	public void TextMin_CountsDifferentEntries_OnceEach()
	{
		var rule = new Rule { TextAny = ["family", "kids", "dad"], TextMin = 2 };
		Assert.True(Text.Has(rule, "A dad and his kids."));
		Assert.False(Text.Has(rule, "Family, family and more family.")); // one entry, three times
		Assert.True(Text.Has(new Rule { TextAny = ["family"] }, "A family.")); // no textMin: one
		Assert.False(Text.Has(new Rule(), "A family."));
	}

	private sealed record Case(string Id, bool Match, string Text, string[]? Genres);

	private static List<Case> AllCases()
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Tools, "text-cases.json")));
		return [.. doc.RootElement.GetProperty("cases").EnumerateArray().Select(x => new Case(
			x.GetProperty("id").GetString()!,
			x.GetProperty("match").GetBoolean(),
			x.GetProperty("text").GetString()!,
			x.TryGetProperty("genres", out var g) ? [.. g.EnumerateArray().Select(y => y.GetString()!)] : null))];
	}

	// a show its category's other fields let in: its genres (or the case's), a length, years, a network and a rating its rule allows
	private static IndexEntry ShowFor(Category category, string[]? genres)
	{
		var r = category.Rule;
		return Show(
			genres: genres ?? [.. r.GenresAll, .. r.GenresAny.Take(1)],
			year: r.Years is [var from, var to] ? from ?? to ?? 2015 : 2015,
			minutes: r.LengthMinutes is [var least, var most] ? least ?? most ?? 45 : 45,
			studios: [.. r.StudiosAny.Take(1)],
			rating: r.CommunityMin ?? 7.5);
	}

	[Fact]
	public void EveryCase_IsMatchedAsItSays()
	{
		var draft = Catalogue.Read(Path.Combine(Tools, "draft.json")).Categories.ToDictionary(x => x.Id, StringComparer.Ordinal);
		var wrong = AllCases()
			.Where(c => (Rules.Match(draft[c.Id], ShowFor(draft[c.Id], c.Genres)) != Truth.No && Text.Has(draft[c.Id].Rule, c.Text)) != c.Match)
			.Select(c => $"{c.Id} {(c.Match ? "misses" : "matches")}: {c.Text}")
			.ToList();
		Assert.True(wrong.Count == 0, string.Join("\n", wrong));
	}

	[Fact]
	public void EveryShippedWordList_HasASentenceItMatches_AndOneItMustNot()
	{
		var cases = AllCases();
		var shipped = Catalogue.Read(Path.Combine(Data, "micro-genres.json")).Categories.Where(c => c.Rule.TextAny.Length > 0).ToList();
		Assert.NotEmpty(shipped);
		var missing = shipped.Where(c => !cases.Any(x => x.Id == c.Id && x.Match) || !cases.Any(x => x.Id == c.Id && !x.Match)).Select(c => c.Id).ToList();
		Assert.True(missing.Count == 0, "no case that must match, or none that must not, for: " + string.Join(", ", missing));
	}

	[Fact]
	public void TheReviewsFalseMatches_AreAllCases()
	{
		// the text review's table (2026-11-25): each wrong match is a sentence the lists now refuse
		var refused = AllCases().Where(x => !x.Match).Select(x => x.Text).ToList();
		foreach (var phrase in new[] { "The Doctor", "residents of a quiet coastal town", "nuclear disaster", "plane crash", "Survivors of abuse", "two timelines",
			"back in time to 1960s Mississippi", "invasion of Normandy", "regime of a ruthless dictator", "infected population", "student body president",
			"rival world powers", "passed the bar exam", "old-school con man", "ruins the career", "unmask villains", "box office" })
		{
			Assert.Contains(refused, x => x.Contains(phrase, StringComparison.Ordinal));
		}
	}
}
