using FluentFin.Core.SmartCollections;
using Jellyfin.Sdk.Generated.Models;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (the review of wip/190-text, item 2): a category's <c>"textMin"</c>: the fewest different words of its
/// <c>textAny</c> a summary must have. The catalogue reads and checks it; the pass counts the words of the overview and the
/// taglines together; the same words twice (or joined another way) are one.
/// </summary>
public class TextMinTests
{
	private static MicroGenre Category(string id, int min, params string[] words) =>
		new(id, $"Category {id}", IndexKind.Series, "test", null, new MicroGenreRule { TextAny = words, TextMin = min }, "Test");

	private static string[] Matched(TextWords words, string text)
	{
		var found = new SortedSet<int>();
		words.Match(text, found);
		return [.. found.Select(i => words.Categories[i].Id)];
	}

	private static MicroGenreCatalogue Load(string rule) => MicroGenres.Load(MicroGenreFixture.File($$"""
		{ "id": "tv-work", "name": "Workplace TV Comedies", "kind": "shows", "family": "sitcom",
		  "rule": {{rule}}, "reason": "Comedy whose summary mentions work" }
		"""), MicroGenreFixture.TestMatchable());

	[Fact]
	public void TwoDifferentWords_AreNeeded_WhenTheMinimumIsTwo()
	{
		var words = TextWords.Of([Category("work", 2, "office", "boss", "co-workers", "co workers"), Category("any", 1, "office")]);

		Assert.Equal(["any"], Matched(words, "An office in Scranton."));
		Assert.Equal(["any", "work"], Matched(words, "The boss's office.").Order());
		Assert.Equal(["any"], Matched(words, "Office, office, OFFICE.")); // one word three times is one word
		Assert.Empty(Matched(words, "His co-workers, and their co workers.")); // the same words, joined two ways: one
		Assert.Equal(["work"], Matched(words, "A boss and his co-workers."));
	}

	[Fact]
	public void ASummarysWords_CountTogether_ItsOverviewAndItsTaglines()
	{
		var words = TextWords.Of([Category("work", 2, "office", "boss")]);

		Assert.Equal(["work"], TextPass.Score(new BaseItemDto { Overview = "Life in a small office.", Taglines = ["Meet the boss."] }, words)!);
		Assert.Equal([], TextPass.Score(new BaseItemDto { Overview = "Life in a small office.", Taglines = ["Meet the family."] }, words)!);
		Assert.Null(TextPass.Score(new BaseItemDto { Overview = " ", Taglines = [] }, words)); // still unknown, never "too few words"
	}

	[Fact]
	public void TheCatalogue_ReadsTextMin_AndRefusesOneItsListCantMeet()
	{
		var two = Assert.Single(Load("""{ "genresAll": ["Comedy"], "textAny": ["office", "boss", "staff"], "textMin": 2 }""").Categories);
		Assert.Equal(2, two.Rule.TextMin);
		Assert.Equal(1, Assert.Single(Load("""{ "genresAll": ["Comedy"], "textAny": ["office"] }""").Categories).Rule.TextMin);

		(string Rule, string Problem)[] refused =
		[
			("""{ "textAny": ["office", "boss"], "textMin": 0 }""", "textMin is under 1"),
			("""{ "textAny": ["office", "boss"], "textMin": 3 }""", "more than its textAny's different words (2)"),
			("""{ "textAny": ["co-workers", "co workers"], "textMin": 2 }""", "more than its textAny's different words (1)"),
			("""{ "genresAll": ["Comedy"], "textMin": 2 }""", "textMin has no textAny"),
			("""{ "textAny": ["office", "boss"], "textMin": 1.5 }""", "textMin is not a whole number"),
			("""{ "textAny": ["office", "boss"], "textMin": "two" }""", "textMin is not a whole number"),
		];
		foreach (var (rule, problem) in refused)
		{
			var catalogue = Load(rule);
			Assert.Empty(catalogue.Categories);
			Assert.Contains(catalogue.Problems, x => x.Contains(problem, StringComparison.Ordinal));
		}
	}

	[Fact]
	public void TheFingerprint_ChangesWithAMinimum_AndAListsOneLeavesItAsItWas()
	{
		var plain = TextWords.Of([new MicroGenre("a", "Category a", IndexKind.Series, "test", null, new MicroGenreRule { TextAny = ["office", "boss"] }, "Test")]);

		Assert.Equal(plain.Fingerprint, TextWords.Of([Category("a", 1, "office", "boss")]).Fingerprint);
		Assert.NotEqual(plain.Fingerprint, TextWords.Of([Category("a", 2, "office", "boss")]).Fingerprint); // matched otherwise: read again
	}

	[Fact]
	public void TheMatcher_KeepsWhatThePassDecided()
	{
		var category = Category("work", 2, "office", "boss");
		var words = TextWords.Of([category]);
		IndexEntry Show(int i) => new() { Id = new Guid(i, 1902, 0, new byte[8]), Name = $"Show {i}", Kind = IndexKind.Series, Genres = ["Comedy"] };
		var one = Show(1);
		var both = Show(2);
		var text = new TextMatches("key", words.Fingerprint, DateTimeOffset.UnixEpoch, new Dictionary<Guid, string[]?>
		{
			[one.Id] = TextPass.Score(new BaseItemDto { Overview = "An office." }, words),
			[both.Id] = TextPass.Score(new BaseItemDto { Overview = "An office and its boss." }, words),
		});

		Assert.Equal(["Show 2"], MicroGenreMatcher.For(CanonicalTitles.Of([one, both])).Match(category, text).Titles.Select(x => x.Name));
		Assert.False(MicroGenreMatcher.Fits(category, one, text));
		Assert.True(MicroGenreMatcher.Fits(category, both, text));
	}
}
