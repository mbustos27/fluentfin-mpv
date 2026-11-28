using System.Diagnostics;
using FluentFin.Core.SmartCollections;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream match): the micro-genres' matcher (<see cref="MicroGenreMatcher"/>): each rule field means
/// what its name says, films and shows are apart, a summary word list matches nothing yet, and the posting lists keep
/// exactly what the plain rule (<see cref="MicroGenreMatcher.Fits"/>) says of each title.
/// </summary>
public class MicroGenreMatcherTests
{
	private static int s_next;

	private static IndexEntry Title(string name, IndexKind kind = IndexKind.Movie, string[]? genres = null, int? year = 2000, string[]? tags = null, string[]? countries = null,
		string[]? studios = null, TimeSpan? length = null, double? rating = 6, float? critic = null, bool played = false, bool noLength = false) => new()
		{
			Id = new Guid(Interlocked.Increment(ref s_next), 0, 0, new byte[8]),
			Name = name,
			Kind = kind,
			Genres = genres ?? ["Drama"],
			Year = year,
			Tags = tags ?? [],
			Countries = countries ?? [],
			Studios = studios ?? [],
			RunTimeTicks = noLength ? null : (length ?? TimeSpan.FromMinutes(100)).Ticks,
			Rating = rating,
			Critic = critic,
			Played = played,
		};

	private static MicroGenre Films(MicroGenreRule rule) => new("test", "Test Films", IndexKind.Movie, "test", null, rule, "Test");

	private static MicroGenre Shows(MicroGenreRule rule) => new("test", "Test Shows", IndexKind.Series, "test", null, rule, "Test");

	// the names the matcher keeps; the plain rule must keep the same, in the same order
	private static string[] Kept(IReadOnlyList<IndexEntry> entries, MicroGenre category)
	{
		var titles = CanonicalTitles.Each(entries);
		var matcher = MicroGenreMatcher.For(titles);
		var matched = matcher.Match(category).Titles.Select(x => x.Name).ToArray();
		Assert.Equal(titles.Entries.Where(x => MicroGenreMatcher.Fits(category, x, lengthUnknownKept: matcher.LengthUnknownKept(category.Kind))).Select(x => x.Name), matched);
		return matched;
	}

	[Fact]
	public void Genres_AllAnyAndNone_EachMeanWhatItsNameSays()
	{
		List<IndexEntry> titles =
		[
			Title("A", genres: ["Crime", "Drama"]), Title("B", genres: ["Crime"]), Title("C", genres: ["Crime", "Drama", "Animation"]),
			Title("D", genres: ["drama", " crime "]), Title("E", genres: ["Comedy", "Romance"]), Title("F", genres: ["Comedy", "Family"]),
		];

		Assert.Equal(["A", "C", "D"], Kept(titles, Films(new() { GenresAll = ["Crime", "Drama"] })));
		Assert.Equal(["A", "D"], Kept(titles, Films(new() { GenresAll = ["Crime", "Drama"], GenresNone = ["animation"] })));
		Assert.Equal(["E", "F"], Kept(titles, Films(new() { GenresAny = ["Romance", "Family"] })));
		Assert.Equal(["E"], Kept(titles, Films(new() { GenresAny = ["Romance", "Family"], GenresNone = ["Family"] })));
		Assert.Empty(Kept(titles, Films(new() { GenresAll = ["Crime", "Western"] }))); // a genre the library hasn't: nothing
		Assert.Empty(Kept(titles, Films(new() { GenresAny = ["Western"] })));
	}

	[Fact]
	public void Keywords_AreComparedTrimmedAndWithoutCase()
	{
		List<IndexEntry> titles = [Title("A", tags: ["heist"]), Title("B", tags: ["Heist "]), Title("C", tags: ["time loop", "heist"]), Title("D")];

		Assert.Equal(["A", "B", "C"], Kept(titles, Films(new() { KeywordsAny = ["heist"] })));
		Assert.Equal(["C"], Kept(titles, Films(new() { KeywordsAll = ["heist", "time loop"] })));
		Assert.Equal(["A", "B"], Kept(titles, Films(new() { KeywordsAny = ["heist"], KeywordsNone = ["time loop"] })));
	}

	[Fact]
	public void Years_WholeDecadesAndRangesAcrossTwo_ATitleWithNoYearOnlyInARuleWithNone()
	{
		List<IndexEntry> titles = [.. new int?[] { 1969, 1970, 1975, 1979, 1980, 1984, 1985, null }.Select(y => Title(y?.ToString() ?? "none", year: y))];

		Assert.Equal(["1970", "1975", "1979"], Kept(titles, Films(new() { Years = new(1970, 1979) })));
		Assert.Equal(["1975", "1979", "1980", "1984"], Kept(titles, Films(new() { Years = new(1975, 1984) })));
		Assert.Equal(["1980", "1984", "1985"], Kept(titles, Films(new() { Years = new(1980, null) })));
		Assert.Equal(["1969"], Kept(titles, Films(new() { Years = new(null, 1969) })));
		Assert.Equal(8, Kept(titles, Films(new() { GenresAll = ["Drama"] })).Length); // no years in the rule: the title with none too
		Assert.Equal(1970, MicroGenreMatcher.DecadeOf(1979));
		Assert.Equal(1980, MicroGenreMatcher.DecadeOf(1980));
	}

	[Fact]
	public void Countries_AnyOfTheTitlesCountries_TheSecondToo()
	{
		List<IndexEntry> titles =
		[
			Title("A", countries: ["United States of America", "Hong Kong"]), Title("B", countries: ["Hong Kong"]), Title("C", countries: ["United States of America"]), Title("D"),
		];

		Assert.Equal(["A", "B"], Kept(titles, Films(new() { CountriesAny = ["Hong Kong"] })));
		Assert.Equal(["A", "B"], Kept(titles, Films(new() { CountriesAny = ["hong kong", "South Korea"] })));
	}

	[Fact]
	public void Studios_MatchByTheirLettersAndDigits()
	{
		List<IndexEntry> titles = [Title("A", studios: ["HBO"]), Title("B", studios: ["H.B.O."]), Title("C", studios: ["HBO Max"]), Title("D", studios: ["Warner", "hbo"])];

		Assert.Equal(["A", "B", "D"], Kept(titles, Films(new() { StudiosAny = ["HBO"] })));
	}

	[Fact]
	public void AStudiosKey_IsTheOneTheBuildersMake()
	{
		// the matcher's own key (no LINQ: 22,000 names a server) is Make.Key's, letters and digits lower case, on any name
		string[] names =
		[
			"Pixar Animation Studios", "HBO", "H.B.O.", "Gaumont–Pathé", "İstanbul Film", "ÉCLAIR", "Toho Co., Ltd.", "ＮＨＫ", "\U00010400Deseret",
			"Ufa-Film GmbH", "  ", "!!", "ΣΤΑΣΙΣ", "Studio 4°C", new string('a', 150) + " " + new string('B', 150),
		];

		Assert.All(names, x => Assert.Equal(SmartCollections.Builders.Make.Key(x), MicroGenreMatcher.KeyOf(x)));
	}

	[Fact]
	public void Length_InWholeMinutes_HalfAMinuteRoundsUp_ATitleWithNoneIsNoMatch()
	{
		List<IndexEntry> titles =
		[
			Title("35:00", length: TimeSpan.FromMinutes(35)), Title("35:29", length: new TimeSpan(0, 35, 29)), Title("35:30", length: new TimeSpan(0, 35, 30)),
			Title("36:00", length: TimeSpan.FromMinutes(36)), Title("none", noLength: true), Title("zero", length: TimeSpan.Zero),
		];

		Assert.Equal(["35:00", "35:29"], Kept(titles, Films(new() { LengthMinutes = new(null, 35) })));
		Assert.Equal(["35:30", "36:00"], Kept(titles, Films(new() { LengthMinutes = new(36, null) })));
		Assert.Null(MicroGenreMatcher.Minutes(0));
		Assert.Null(MicroGenreMatcher.Minutes(null));
	}

	// the pre-release fix B (rule 4: missing data isn't a decision): on the owner's library 3 % of the shows had an episode
	// length, and every rule with a length (the sitcoms', the networks' dramas) kept almost nothing: "NBC Sitcoms" was empty
	[Fact]
	public void Length_WhereMostTitlesOfAKindHaveNone_KeepsThoseWithNone_AndStillBoundsThoseWithOne()
	{
		List<IndexEntry> shows =
		[
			Title("22 min", IndexKind.Series, genres: ["Comedy"], length: TimeSpan.FromMinutes(22)),
			Title("60 min", IndexKind.Series, genres: ["Comedy"], length: TimeSpan.FromMinutes(60)),
			.. Enumerable.Range(0, 3).Select(i => Title($"none {i}", IndexKind.Series, genres: ["Comedy"], noLength: true)),
		];
		var sitcoms = Shows(new() { GenresAll = ["Comedy"], LengthMinutes = new(null, 35) });
		var films = Enumerable.Range(0, 3).Select(i => Title($"film {i}", genres: ["Comedy"], length: TimeSpan.FromMinutes(100))).Append(Title("film none", genres: ["Comedy"], noLength: true)).ToList();

		// 2 of the 5 shows have a length (40 %): those with none are judged on the rest of the rule; a known 60 is still out
		Assert.Equal(["22 min", "none 0", "none 1", "none 2"], Kept(shows, sitcoms));
		var matcher = MicroGenreMatcher.For(CanonicalTitles.Each([.. shows, .. films]));
		Assert.True(matcher.LengthUnknownKept(IndexKind.Series));
		Assert.False(matcher.LengthUnknownKept(IndexKind.Movie)); // each kind by its own titles: 3 of 4 films have one
		Assert.Equal(["22 min", "none 0", "none 1", "none 2"], matcher.Match(sitcoms).Titles.Select(x => x.Name));
		Assert.Empty(matcher.Match(Films(new() { GenresAll = ["Comedy"], LengthMinutes = new(null, 90) })).Titles); // "film none" stays out
		// half or more with a length: a title with none is no match, as before
		Assert.Equal(["22 min"], Kept([.. shows.Take(4)], sitcoms));
		Assert.Equal(["22 min", "30 min"], Kept([.. shows.Take(3), Title("30 min", IndexKind.Series, genres: ["Comedy"], length: TimeSpan.FromMinutes(30))], sitcoms));
	}

	[Fact]
	public void Ratings_TheLeastIsKept_ATitleWithNoneIsNoMatch()
	{
		List<IndexEntry> titles =
		[
			Title("7", rating: 7.0, critic: 80), Title("6.99", rating: 6.99, critic: 79.5f), Title("none", rating: null, critic: null), Title("8", rating: 8, critic: 95),
		];

		Assert.Equal(["7", "8"], Kept(titles, Films(new() { CommunityMin = 7 })));
		Assert.Equal(["7", "8"], Kept(titles, Films(new() { CriticMin = 80 })));
		Assert.Equal(["8"], Kept(titles, Films(new() { CommunityMin = 7.5, CriticMin = 80 })));
	}

	[Fact]
	public void FilmsAndShows_AreKeptApart_EvenWhenAShowHasEveryFieldOfAFilmsRule()
	{
		var film = Title("film", genres: ["Comedy"], year: 2005, length: TimeSpan.FromMinutes(30), studios: ["HBO"]);
		var show = Title("show", IndexKind.Series, genres: ["Comedy"], year: 2005, length: TimeSpan.FromMinutes(30), studios: ["HBO"]);
		var rule = new MicroGenreRule { GenresAll = ["Comedy"], Years = new(2000, 2009), LengthMinutes = new(null, 35), StudiosAny = ["HBO"] };
		var matcher = MicroGenreMatcher.For(CanonicalTitles.Each([film, show]));

		Assert.Equal(["film"], Kept([film, show], Films(rule)));
		Assert.Equal(["show"], Kept([film, show], Shows(rule)));
		Assert.False(MicroGenreMatcher.Fits(Films(rule), show));
		Assert.False(MicroGenreMatcher.Fits(Shows(rule), film));
		Assert.Equal(1, matcher.TitlesOf(IndexKind.Movie));
		Assert.Equal(1, matcher.TitlesOf(IndexKind.Series));
	}

	[Fact]
	public void TextAny_IsAccepted_ButMatchesNothingUntilATextPassExists()
	{
		// the index keeps no summary yet (step 1b's text pass): a rule with a summary word list keeps nothing, never a guess
		// (rule 4: missing data is not a decision), though the rest of its rule fits
		List<IndexEntry> shows = [Title("A", IndexKind.Series, genres: ["Comedy"]), Title("B", IndexKind.Series, genres: ["Comedy"])];
		var withText = Shows(new() { GenresAll = ["Comedy"], TextAny = ["office", "boss"] });
		var without = Shows(new() { GenresAll = ["Comedy"] });

		Assert.True(withText.Rule.WaitsForText);
		Assert.Empty(Kept(shows, withText));
		Assert.Equal(["A", "B"], Kept(shows, without));
		Assert.False(MicroGenreMatcher.Fits(withText, shows[0]));
		// a word list alone is a rule (not empty): the catalogue takes it, and it waits
		var workplace = MicroGenreFixture.TestCatalogue().Of("workplace-comedies")!;
		Assert.True(workplace.Rule.WaitsForText);
		Assert.Equal(0, MicroGenreMatcher.For(CanonicalTitles.Each(shows)).Match(workplace).Count);
	}

	[Fact]
	public void AMatch_CountsItsTitlesAndThoseNotWatched()
	{
		List<IndexEntry> titles = [Title("A", played: true), Title("B"), Title("C"), Title("D", genres: ["Comedy"])];

		var match = MicroGenreMatcher.For(CanonicalTitles.Each(titles)).Match(Films(new() { GenresAll = ["Drama"] }));

		Assert.Equal(3, match.Count);
		Assert.Equal(2, match.Unwatched);
		Assert.Equal(["A", "B", "C"], match.Titles.Select(x => x.Name));
	}

	[Fact]
	public void ThePostingLists_KeepExactlyWhatThePlainRuleSays_OnASurveyShapedLibrary()
	{
		// every generated rule (300) and the test catalogue's, over a library of the survey's shape at a sixth of its size
		var titles = CanonicalTitles.Each(MicroGenreFixture.Library(4_300, 830, seed: 7));
		var matcher = MicroGenreMatcher.For(titles);
		var rules = MicroGenreFixture.Rules(300).Concat(MicroGenreFixture.TestCatalogue().Categories).ToList();
		var some = 0;

		foreach (var rule in rules)
		{
			var match = matcher.Match(rule);
			var plain = titles.Entries.Where(x => MicroGenreMatcher.Fits(rule, x, lengthUnknownKept: matcher.LengthUnknownKept(rule.Kind))).ToList();
			Assert.True(plain.Select(x => x.Id).SequenceEqual(match.Titles.Select(x => x.Id)), $"{rule.Id}: postings {match.Count}, plain {plain.Count}");
			Assert.Equal(plain.Count(x => !x.Played), match.Unwatched);
			some += match.Count is > 0 and < 2_000 ? 1 : 0;
		}

		// the comparison isn't empty: most rules keep some titles and not all
		Assert.True(some > 150, $"{some} rules kept some titles");
	}

	[Fact]
	public void ThePostingLists_KeepExactlyWhatThePlainRuleSays_WhenMostShowsHaveNoLength()
	{
		// the pre-release fix B on the owner's shape: 3 % of the shows with an episode length, every film with a runtime
		var random = new Random(3);
		var entries = MicroGenreFixture.Library(4_300, 830, seed: 7).Select(x => x.Kind == IndexKind.Series && random.NextDouble() >= 0.03 ? x with { RunTimeTicks = null } : x).ToList();
		var titles = CanonicalTitles.Each(entries);
		var matcher = MicroGenreMatcher.For(titles);
		var rules = MicroGenreFixture.Rules(300).Concat(MicroGenreFixture.TestCatalogue().Categories).ToList();
		Assert.True(matcher.LengthUnknownKept(IndexKind.Series));
		Assert.False(matcher.LengthUnknownKept(IndexKind.Movie));

		foreach (var rule in rules)
		{
			var match = matcher.Match(rule);
			var plain = titles.Entries.Where(x => MicroGenreMatcher.Fits(rule, x, lengthUnknownKept: matcher.LengthUnknownKept(rule.Kind))).ToList();
			Assert.True(plain.Select(x => x.Id).SequenceEqual(match.Titles.Select(x => x.Id)), $"{rule.Id}: postings {match.Count}, plain {plain.Count}");
		}

		// and the shows' rules with a length keep shows with none, never a known length outside it
		var bounded = rules.Where(x => x.Kind == IndexKind.Series && !x.Rule.LengthMinutes.IsOpen && !x.Rule.WaitsForText).ToList();
		Assert.Contains(bounded, x => matcher.Match(x).Titles.Any(t => t.RunTimeTicks is null));
		Assert.All(bounded, x => Assert.All(matcher.Match(x).Titles, t => Assert.True(x.Rule.LengthMinutes.Contains(MicroGenreMatcher.Minutes(t.RunTimeTicks), unknownKept: true))));
	}

	[Fact]
	public void TheMatcher_IsMadeOnceForTheTitles_AndKeptWhileTheyAre()
	{
		List<IndexEntry> entries = [Title("A"), Title("B")];
		var titles = CanonicalTitles.Each(entries);
		var other = CanonicalTitles.Each(entries);

		Assert.Same(MicroGenreMatcher.For(titles), MicroGenreMatcher.For(titles));
		Assert.NotSame(MicroGenreMatcher.For(titles), MicroGenreMatcher.For(other));
	}

	[Fact]
	public void DamagedEntries_NeverThrow_AndThePostingListsAgreeWithThePlainRule()
	{
		// a saved index damaged by hand: null names and lists, blanks in a list
		List<IndexEntry> titles =
		[
			Title("A") with { Name = null!, Genres = null!, Tags = null!, Countries = null!, Studios = null! },
			Title("B") with { Genres = [null!, " Drama ", ""], Tags = [null!, "heist"], Studios = [null!, "", "!!"], Countries = [" Hong Kong"] },
			Title("C") with { Rating = double.NaN, Critic = float.NaN, RunTimeTicks = -5 },
		];

		Assert.Equal(["B"], Kept(titles, Films(new() { GenresAll = ["Drama"], KeywordsAny = ["heist"], CountriesAny = ["Hong Kong"] })));
		Assert.Empty(Kept(titles, Films(new() { StudiosAny = ["HBO"] })));
		// C's NaN ratings and negative runtime are none: no match for a least of 0
		Assert.DoesNotContain("C", Kept(titles, Films(new() { CommunityMin = 0 })));
		Assert.DoesNotContain("C", Kept(titles, Films(new() { CriticMin = 0 })));
		Assert.Equal(["B"], Kept(titles, Films(new() { LengthMinutes = new(0, null) })).Where(x => x is not null));
	}
}

/// <summary>Nixie 1.9: the matcher's time budget, run alone (no other test competing for the processor).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class MicroGenreBudgetCollection
{
	public const string Name = "Micro-genre budget";
}

[Collection(MicroGenreBudgetCollection.Name)]
public class MicroGenreBudgetTests(ITestOutputHelper output)
{
	/// <summary>
	/// The budget of a cold round (the posting lists made, then 300 rules matched) over the survey's library: under it, the
	/// micro-genres can be made with the first rows (PublishedBuilders), else after them (LaterBuilders). About 35 ms on the
	/// 16-processor desktop of 25 November 2026, idle; the matching alone, once the lists are kept, about 5 ms.
	/// </summary>
	public const double BudgetMs = 50;

	/// <summary>
	/// What the test fails on: three times the budget for a cold round, and half of it for the matching alone. A slow or busy
	/// machine passes (the same desktop under the other streams' builds: 53 to 75 ms cold, 7 ms matching); a 10× regression of
	/// either part (about 350 ms cold, 45 ms matching) doesn't.
	/// </summary>
	public const double ColdLimitMs = 3 * BudgetMs, MatchingLimitMs = BudgetMs / 2;

	[Fact]
	public void ThreeHundredRules_OverTheSurveysLibrary_26000FilmsAnd5000Shows_TakeUnder50Ms()
	{
		var entries = MicroGenreFixture.Library();
		var titles = CanonicalTitles.Each(entries);
		var rules = MicroGenreFixture.Rules(300);
		Assert.Equal(MicroGenreFixture.SurveyFilms + MicroGenreFixture.SurveyShows, titles.Entries.Count);
		Run(CanonicalTitles.Each(MicroGenreFixture.Library(600, 120, seed: 3)), rules); // the code compiled first

		var rounds = Enumerable.Range(0, 5).Select(_ => Run(titles, rules)).ToList();

		var best = rounds.MinBy(x => x.Total)!;
		var matching = rounds.Min(x => x.Rules);
		foreach (var round in rounds)
		{
			output.WriteLine($"posting lists {round.Postings:F1} ms + 300 rules {round.Rules:F1} ms = {round.Total:F1} ms; {round.Made} rules keep 8+ unwatched, {round.Kept:N0} titles kept in all; {round.Collections} collections");
		}

		output.WriteLine($"best cold round {best.Total:F1} ms of a {BudgetMs} ms budget (fails over {ColdLimitMs}); matching alone {matching:F1} ms (fails over {MatchingLimitMs}); {Environment.ProcessorCount} processors");
		Assert.True(best.Total <= ColdLimitMs, $"300 rules over {titles.Entries.Count:N0} titles took {best.Total:F1} ms cold (posting lists {best.Postings:F1} ms); the budget is {BudgetMs} ms");
		Assert.True(matching <= MatchingLimitMs, $"matching 300 rules took {matching:F1} ms with the posting lists kept");
		Assert.True(best.Made > 100, $"{best.Made} rules keep 8 or more unwatched: the rules are of a real catalogue's shape");
	}

	private sealed record Round(double Postings, double Rules, int Made, long Kept, int Collections)
	{
		public double Total => Postings + Rules;
	}

	// a cold round: the posting lists made, then every rule matched and counted (as the builder does before it lists any titles).
	// The collector first: the library's own making leaves garbage that isn't the matcher's cost
	private static Round Run(CanonicalTitles titles, IReadOnlyList<MicroGenre> rules)
	{
		GC.Collect();
		GC.WaitForPendingFinalizers();
		var collections = GC.CollectionCount(0);
		var watch = Stopwatch.StartNew();
		var matcher = new MicroGenreMatcher(titles);
		var postings = watch.Elapsed.TotalMilliseconds;
		var made = 0;
		long kept = 0;
		foreach (var rule in rules)
		{
			var match = matcher.Match(rule);
			kept += match.Count;
			made += match.Unwatched >= 8 ? 1 : 0;
		}

		watch.Stop();
		return new Round(postings, watch.Elapsed.TotalMilliseconds - postings, made, kept, GC.CollectionCount(0) - collections);
	}
}
