using System.Text.Json;
using Nixie.Collections;
using static Census.Tests.Fx;

namespace Census.Tests;

/// <summary>The lint catches each thing the brief asks of a name, a rule and a reason line, and of a matchable keyword.</summary>
public class LintTests
{
	private static readonly Matchable s_extra = new()
	{
		Version = 1,
		Groups = new() { ["place"] = "Settings" },
		Entries = [new() { Keyword = "small town", Group = "place" }, new() { Keyword = "witty", Group = "place" }],
	};

	private static List<string> Problems(Category category) => Lint.Check(new Catalogue { Version = 1, Categories = [category] }, s_extra);

	[Fact]
	public void AGoodCategory_HasNoProblems() =>
		Assert.Empty(Problems(Cat(new Rule { GenresAll = ["Crime", "Drama"], Years = [1970, 1979] }, id: "crime-dramas-1970s")));

	public static TheoryData<string, Category> Bad => new()
	{
		{ "over 50", Cat(new Rule { GenresAll = ["Drama"] }, name: "Dramas About Very Long Things That Never Quite End Here") },
		{ "starts with", Cat(new Rule { GenresAll = ["Drama"] }, name: "Your Dramas") },
		{ "speaks of the viewer", Cat(new Rule { GenresAll = ["Drama"] }, name: "Dramas You Missed") },
		{ "a word no rule checks", Cat(new Rule { GenresAll = ["Drama"] }, name: "Hidden Gems") },
		{ "a word no rule checks", Cat(new Rule { GenresAll = ["Drama"] }, name: "Classic Dramas") },
		{ "\"dark\" with no keyword", Cat(new Rule { GenresAll = ["Comedy"] }, name: "Dark Comedies") },
		{ "\"witty\" with no keyword", Cat(new Rule { GenresAll = ["Comedy"], KeywordsAny = ["witty", "small town"] }, name: "Witty Comedies") }, // a list with a non-mood word doesn't back it
		{ "\"british\" with no country", Cat(new Rule { GenresAll = ["Comedy"] }, name: "British Comedies") },
		{ "years aren't within 1970", Cat(new Rule { GenresAll = ["Crime"], Years = [1970, 1989] }, name: "Crime Movies from the 1970s") },
		{ "no longest length", Cat(new Rule { GenresAll = ["Comedy"] }, kind: "shows", name: "Half-Hour Comedies") },
		{ "acclaimed", Cat(new Rule { GenresAll = ["Drama"] }, name: "Acclaimed Dramas") },
		{ "it is rated", Cat(new Rule { GenresAll = ["Drama"] }, name: "Highly Rated Dramas") },
		{ "the id isn't", Cat(new Rule { GenresAll = ["Drama"] }, id: "Crime_Dramas") },
		{ "not films or shows", Cat(new Rule { GenresAll = ["Drama"] }, kind: "movies") },
		{ "no keyword collection", Cat(new Rule { GenresAll = ["Drama"] }, parent: "keyword:neo-noir") },
		{ "reason line speaks of the viewer", Cat(new Rule { GenresAll = ["Drama"] }, reason: "Dramas you might like") },
		{ "doesn't narrow", Cat(new Rule()) },
		{ "is no film genre", Cat(new Rule { GenresAll = ["Sci-Fi"] }) },
		{ "is no show genre", Cat(new Rule { GenresAll = ["TV Movie"] }, kind: "shows", name: "TV Movies on TV") },
		{ "neither allowlisted nor", Cat(new Rule { KeywordsAny = ["based on novel"] }) }, // the research's pitfall 6: a near spelling
		{ "isn't trimmed lower case", Cat(new Rule { KeywordsAny = ["Heist"] }) },
		{ "both wanted and not", Cat(new Rule { GenresAll = ["Drama"], GenresNone = ["Drama"] }) },
		{ "a shows rule uses keywords", Cat(new Rule { GenresAll = ["Comedy"], KeywordsAny = ["small town"] }, kind: "shows", name: "Small-Town Comedies") },
		{ "a shows rule uses keywords, countries", Cat(new Rule { GenresAll = ["Comedy"], CountriesAny = ["Japan"] }, kind: "shows", name: "Comedies") },
		{ "a films rule uses summary words", Cat(new Rule { GenresAll = ["Comedy"], TextAny = ["office"] }, name: "Workplace Comedies") },
		{ "years aren't [from, to]", Cat(new Rule { Years = [1999, 1970] }, name: "Movies") },
		{ "length isn't [least, most]", Cat(new Rule { LengthMinutes = [0, 90] }, name: "Movies") },
		{ "out of its scale", Cat(new Rule { CommunityMin = 70 }, name: "Movies") },
	};

	[Theory]
	[MemberData(nameof(Bad))]
	public void ABadCategory_IsSaidWhy(string why, Category category) =>
		Assert.Contains(Problems(category), p => p.Contains(why, StringComparison.Ordinal));

	[Fact]
	public void ASettingDecade_IsNotAReleaseDecade() =>
		Assert.Empty(Problems(Cat(new Rule { KeywordsAny = ["small town"], Years = [1995, null] }, name: "Movies Set in the 1980s")));

	[Fact]
	public void AMoodWord_IsBackedByItsToneKeyword() =>
		Assert.Empty(Problems(Cat(new Rule { GenresAll = ["Comedy"], KeywordsAll = ["witty"] }, name: "Witty Comedies")));

	[Fact]
	public void TwoCategories_MayNotShareAnIdOrAName()
	{
		var one = Cat(new Rule { GenresAll = ["Drama"] }, id: "one", name: "Dramas from Iran");
		var two = Cat(new Rule { GenresAll = ["War"] }, id: "one", name: "dramas from iran");
		var problems = Lint.Check(new Catalogue { Version = 1, Categories = [one, two] }, s_extra);
		Assert.Contains(problems, p => p.Contains("id is used twice", StringComparison.Ordinal));
		Assert.Contains(problems, p => p.Contains("name is used twice", StringComparison.Ordinal));
	}

	[Fact]
	public void AMisspeltField_IsAnError_NotARuleThatMatchesEverything() =>
		Assert.Throws<JsonException>(() => Catalogue.Parse("""{ "version": 1, "categories": [ { "id": "x", "rule": { "genreAll": ["Drama"] } } ] }"""));

	[Theory]
	[InlineData("duringcreditsstinger", "denied")]
	[InlineData("woman director", "denied")]
	[InlineData("twist ending", "denied")]
	[InlineData("death of father", "denied")]
	[InlineData("suicide", "denied")]
	[InlineData("heist", "already kept")]
	[InlineData("Small Town", "not trimmed lower case")]
	public void AMatchableKeyword_IsVetted(string keyword, string why)
	{
		var list = new Matchable { Version = 1, Groups = new() { ["g"] = "A reason" }, Entries = [new() { Keyword = keyword, Group = "g" }] };
		Assert.Contains(Lint.CheckMatchable(list), p => p.Contains(why, StringComparison.Ordinal));
	}

	[Fact]
	public void AMatchableGroup_NeedsAReason_AndAKeywordItsGroup()
	{
		var list = new Matchable
		{
			Version = 1,
			Groups = new() { ["empty"] = "Nothing in it", ["silent"] = "" },
			Entries = [new() { Keyword = "small town", Group = "silent" }, new() { Keyword = "small town", Group = "nowhere" }],
		};
		var problems = Lint.CheckMatchable(list).ToList();
		Assert.Contains(problems, p => p.Contains("\"empty\": no keywords", StringComparison.Ordinal));
		Assert.Contains(problems, p => p.Contains("\"silent\": no reason", StringComparison.Ordinal));
		Assert.Contains(problems, p => p.Contains("group \"nowhere\" has no reason", StringComparison.Ordinal));
		Assert.Contains(problems, p => p.Contains("listed twice", StringComparison.Ordinal));
	}
}

/// <summary>1.9 (the text review): textMin, summary entries the pass reads alike, and TMDb's combined show genres.</summary>
public class LintTextAndGenreTests
{
	private static List<string> Problems(Rule rule, string kind = "shows") =>
		Lint.Check(new Catalogue { Version = 1, Categories = [Fx.Cat(rule, kind: kind, name: "Workplace TV Comedies")] }, new Matchable { Version = 1 });

	[Theory]
	[InlineData(1)]
	[InlineData(4)] // more than the list's different entries
	public void TextMin_IsBetweenTwoAndTheEntries(int least) =>
		Assert.Contains(Problems(new Rule { GenresAll = ["Comedy"], TextAny = ["boss", "office", "staff"], TextMin = least }), p => p.Contains("textMin", StringComparison.Ordinal));

	[Fact]
	public void TextMin_NeedsSummaryWords() =>
		Assert.Contains(Problems(new Rule { GenresAll = ["Comedy"], TextMin = 2 }), p => p.Contains("textMin", StringComparison.Ordinal));

	[Fact]
	public void TwoEntriesThePassReadsAlike_AreOne() =>
		Assert.Contains(Problems(new Rule { GenresAll = ["Comedy"], TextAny = ["co-workers", "co workers"] }), p => p.Contains("listed twice", StringComparison.Ordinal));

	[Fact]
	public void AnEntryWithASentenceBreak_CanNeverMatch() =>
		Assert.Contains(Problems(new Rule { GenresAll = ["Comedy"], TextAny = ["dr. who"] }), p => p.Contains("sentence break", StringComparison.Ordinal));

	[Fact]
	public void AGoodWordList_HasNoProblems() =>
		Assert.Empty(Problems(new Rule { GenresAll = ["Comedy"], TextAny = ["boss", "office", "staff"], TextMin = 2 }));

	[Theory]
	[InlineData("genresAll needs \"Science Fiction\"", new[] { "Science Fiction" }, new string[0], new string[0])]
	[InlineData("genresAll needs \"Children\"", new[] { "Children" }, new string[0], new string[0])]
	[InlineData("without TMDb's \"Action & Adventure\"", new string[0], new[] { "Action", "Comedy" }, new string[0])]
	[InlineData("without TMDb's \"Kids\"", new[] { "Comedy" }, new string[0], new[] { "Children" })]
	[InlineData("without TMDb's \"Talk\"", new[] { "Comedy" }, new string[0], new[] { "Talk Show" })]
	public void AShowsRuleNeedingATvdbGenre_AcceptsTmdbsToo(string why, string[] all, string[] any, string[] none) =>
		Assert.Contains(Problems(new Rule { GenresAll = all, GenresAny = any, GenresNone = none }), p => p.Contains(why, StringComparison.Ordinal));

	[Fact]
	public void TheTmdbPairs_AreEnough_AndFilmsAreLeftAlone()
	{
		Assert.Empty(Problems(new Rule { GenresAll = ["Drama"], GenresAny = ["Science Fiction", "Sci-Fi & Fantasy"], GenresNone = ["Children", "Kids"] }));
		// one OR group: a second TheTVDB genre may stay in genresAll once genresAny holds a pair
		Assert.Empty(Problems(new Rule { GenresAll = ["Adventure", "Drama"], GenresAny = ["Fantasy", "Sci-Fi & Fantasy"] }));
		Assert.Empty(Problems(new Rule { GenresAll = ["Science Fiction", "Action"] }, kind: "films")); // TMDb's film genres are these
	}
}
