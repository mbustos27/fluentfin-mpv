using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, step 1b, stream text): the categories' summary word lists (<see cref="TextWords"/>) match whole
/// words, case-folded, and phrases within a sentence; a blank summary is unknown, never "none of the words".
/// </summary>
public class TextWordsTests
{
	private static MicroGenre Category(string id, params string[] words) =>
		new(id, $"Category {id}", IndexKind.Series, "test", null, new MicroGenreRule { TextAny = words }, "Test");

	private static string[] Matched(TextWords words, string? text)
	{
		var found = new SortedSet<int>();
		words.Match(text, found);
		return [.. found.Select(i => words.Categories[i].Id)];
	}

	[Fact]
	public void AWord_MatchesOnlyAsAWholeWord_WhateverItsCase()
	{
		var words = TextWords.Of([Category("work", "office", "boss", "bar")]);

		Assert.Equal(["work"], Matched(words, "An OFFICE in Scranton."));
		Assert.Equal(["work"], Matched(words, "The boss's daughter returns."));
		Assert.Equal(["work"], Matched(words, "Behind the bar, at night"));
		Assert.Empty(Matched(words, "A police officer and his offices")); // "officer", "offices": other words
		Assert.Empty(Matched(words, "An embossed letter arrives at the barn")); // "boss" and "bar" inside other words
		Assert.Empty(Matched(words, "Barbara and Bossk")); // at a word's start is not a word
	}

	[Fact]
	public void APhrase_IsItsWordsInARow_WhateverJoinsThem_WithinASentence()
	{
		var words = TextWords.Of([Category("school", "high school"), Category("work", "co-workers"), Category("travel", "time travel")]);

		Assert.Equal(["school"], Matched(words, "A High School teacher"));
		Assert.Equal(["school"], Matched(words, "a high-school drama"));
		Assert.Equal(["work"], Matched(words, "she and her co-workers"));
		Assert.Equal(["work"], Matched(words, "she and her co workers"));
		Assert.Empty(Matched(words, "she and her coworkers")); // one word: the list names it apart if it wants it
		Assert.Equal(["travel"], Matched(words, "Time-travel, again"));
		Assert.Empty(Matched(words, "school high")); // in order only
		Assert.Empty(Matched(words, "He aims high. School starts tomorrow.")); // never across a sentence
		Assert.Empty(Matched(words, "The time: travel plans")); // nor a clause
		Assert.Empty(Matched(words, "high")); // a phrase's first word alone, at the end
	}

	[Fact]
	public void Accents_AreKept_AndAComposedOrDecomposedLetterIsTheSame()
	{
		var words = TextWords.Of([Category("cafe", "café"), Category("school", "école")]);

		Assert.Equal(["cafe"], Matched(words, "A small CAFÉ in Paris"));
		Assert.Equal(["cafe"], Matched(words, "A small café in Paris")); // e and a combining acute
		Assert.Equal(["school"], Matched(words, "L'École des femmes")); // the apostrophe ends "l"
		Assert.Empty(Matched(words, "A small cafe in Paris")); // no accent is another word
	}

	[Fact]
	public void OneWord_CanBeInSeveralCategories_AndATextInSeveral()
	{
		var words = TextWords.Of([Category("a", "school", "teacher"), Category("b", "high school"), Category("c", "space")]);

		Assert.Equal(["a", "b"], Matched(words, "A high school"));
		Assert.Equal(["a", "c"], Matched(words, "A teacher in space"));
	}

	[Fact]
	public void ABlankSummary_IsUnknown_NotNoneOfTheWords()
	{
		var words = TextWords.Of([Category("work", "office")]);
		var found = new HashSet<int>();

		Assert.False(words.Match(null, found));
		Assert.False(words.Match("   ", found));
		Assert.True(words.Match("A family on a farm", found));
		Assert.Empty(found);

		// a title's summary: its overview and taglines; none of them said is unknown (null), said with no word is none ([])
		Assert.Null(TextPass.Score(new BaseItemDto { Overview = " ", Taglines = [] }, words));
		Assert.Null(TextPass.Score(new BaseItemDto(), words));
		Assert.Equal([], TextPass.Score(new BaseItemDto { Overview = "A family on a farm" }, words)!);
		Assert.Equal(["work"], TextPass.Score(new BaseItemDto { Overview = "", Taglines = ["Welcome to the office."] }, words)!);
	}

	[Fact]
	public void TheFingerprint_ChangesWithTheWords_NotTheirOrder()
	{
		var first = TextWords.Of([Category("a", "office", "boss"), Category("b", "space")]);
		var reordered = TextWords.Of([Category("b", "space"), Category("a", "boss", "OFFICE")]);
		var changed = TextWords.Of([Category("a", "office", "bosses"), Category("b", "space")]);

		Assert.Equal(first.Fingerprint, reordered.Fingerprint);
		Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
		Assert.NotEqual(first.Fingerprint, TextWords.Empty.Fingerprint);
	}

	[Fact]
	public void TheShippedCatalogue_HasWordLists_ForShowsOnly_EachWithWords()
	{
		var words = TextWords.Of(MicroGenres.Shipped);

		Assert.False(words.IsEmpty);
		Assert.Equal([IndexKind.Series], words.Kinds); // only shows' summaries are read
		Assert.All(words.Categories, x => Assert.All(x.Rule.TextAny, w => Assert.NotEmpty(TextWords.Words(w))));
		// the survey's example: a workplace comedy's summary
		var workplace = words.Categories.Single(x => x.Id == "tv-workplace-comedies");
		Assert.Contains("tv-workplace-comedies", Matched(words, "A mockumentary about the employees of a paper company's office."));
		Assert.Equal(IndexKind.Series, workplace.Kind);
	}
}
