using System.Diagnostics;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream match): the micro-genres' builder (<see cref="MicroGenreBuilder"/>): a collection for each
/// category with at least 8 unwatched titles and no more than 1 % of its own kind's, the unwatched first and each part best
/// first, and a census of counts only.
/// </summary>
public class MicroGenreBuilderTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
	private static int s_next;

	private static IndexEntry Title(string name, IndexKind kind = IndexKind.Movie, string[]? genres = null, int year = 1975, double rating = 6, bool played = false, string? tmdb = null) => new()
	{
		Id = new Guid(Interlocked.Increment(ref s_next), 1, 0, new byte[8]),
		Name = name,
		Kind = kind,
		LibraryId = Guid.Empty,
		Genres = genres ?? ["Crime", "Drama"],
		Year = year,
		Tmdb = tmdb,
		RunTimeTicks = TimeSpan.FromMinutes(kind == IndexKind.Movie ? 110 : 30).Ticks,
		Rating = rating,
		Played = played,
		PrimaryTag = "p",
	};

	private static BuildContext Context(IReadOnlyList<IndexEntry> entries) =>
		BuildContext.For(new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Now), Now);

	private static MicroGenreCatalogue Catalogue(params string[] categories) => MicroGenres.Load(MicroGenreFixture.File(categories), MicroGenreFixture.TestMatchable());

	private const string Comedies = """{ "id": "comedies", "name": "Comedy Films", "kind": "films", "family": "comedy", "rule": { "genresAll": ["Comedy"] }, "reason": "Comedies" }""";
	private const string ComedyShows = """{ "id": "comedy-shows", "name": "Comedy Shows", "kind": "shows", "family": "comedy", "rule": { "genresAll": ["Comedy"] }, "reason": "Comedies" }""";

	[Fact]
	public void ACategoryWithEnoughUnwatched_IsACollection_NamedAndReasonedAsTheCatalogueSays()
	{
		List<IndexEntry> titles =
		[
			.. Enumerable.Range(0, 10).Select(i => Title($"Crime {i}", played: i < 2)),
			Title("Too new", year: 1985), Title("A comedy", genres: ["Comedy"]), Title("A cartoon", genres: ["Crime", "Drama", "Animation"]),
		];

		var run = new MicroGenreBuilder(MicroGenreFixture.TestCatalogue()).Run(Context(titles));

		var made = Assert.Single(run.Collections);
		Assert.Equal("micro:crime-drama-1970s", made.Id);
		Assert.Equal(MicroGenreBuilder.PendingKind, made.Kind);
		Assert.Equal("Gritty 1970s Crime Dramas", made.Title);
		Assert.Null(NixieTitles.Check(made.Title));
		// 1.9 (Phase 3, R2): the played titles inside named first (two at most), then the catalogue's reason
		Assert.Equal(["With “Crime 0” and “Crime 1”", "Crime and drama, 1970–79"], made.Reason!.Facts);
		Assert.Equal("10 films, 8 unwatched", made.Reason.Count.Text);
		Assert.Equal("With “Crime 0” and “Crime 1” · Crime and drama, 1970–79", made.Why);
		Assert.Equal(IndexKind.Movie, made.Only);
		Assert.False(made.HasOwnOrder);
		Assert.Equal(10, made.ItemIds.Count);
		Assert.Equal(4, made.Cover.Count);
	}

	// the pre-release fix B: on the owner's library 97 % of the shows had no episode length, and "NBC Sitcoms" was empty for
	// want of one (the sitcoms played most there). The shipped rule, on shows of that shape
	[Fact]
	public void TheShippedNbcSitcoms_IsMade_WhenMostShowsHaveNoEpisodeLength_AndTheCensusSaysSo()
	{
		var nbc = MicroGenres.Shipped.Of("tv-nbc-sitcoms")!;
		Assert.Equal(new MicroRange(null, 35), nbc.Rule.LengthMinutes);
		List<IndexEntry> titles =
		[
			.. Enumerable.Range(0, 12).Select(i => Title($"NBC comedy {i}", IndexKind.Series, ["Comedy"]) with { Studios = ["NBC"], RunTimeTicks = null }),
			Title("An NBC hour", IndexKind.Series, ["Comedy"]) with { Studios = ["NBC"], RunTimeTicks = TimeSpan.FromMinutes(60).Ticks },
			Title("An NBC half hour", IndexKind.Series, ["Comedy"]) with { Studios = ["NBC"] },
			.. Enumerable.Range(0, 30).Select(i => Title($"Drama {i}", IndexKind.Series, ["Drama"]) with { RunTimeTicks = null }),
		];

		var run = new MicroGenreBuilder(new MicroGenreCatalogue([nbc], [])).Run(Context(titles));

		var made = Assert.Single(run.Collections);
		Assert.Equal(13, made.ItemIds.Count); // the 12 of no length and the half hour; the known hour is still out
		Assert.Contains("show episode lengths 5 % known", run.Census.Note, StringComparison.Ordinal);
		Assert.Equal(2 / 44.0, run.Census.ShowLengths, 6);

		// where most shows have a length, one with none is no match, as before
		var most = new MicroGenreBuilder(new MicroGenreCatalogue([nbc], [])).Run(Context([.. titles.Select(x => x.Name.StartsWith("Drama", StringComparison.Ordinal) ? x with { RunTimeTicks = TimeSpan.FromMinutes(45).Ticks } : x)]));
		Assert.Equal(MicroGenreVerdict.UnderFloor, Assert.Single(most.Census.Pools).Verdict);
		Assert.DoesNotContain("episode lengths", most.Census.Note, StringComparison.Ordinal);
	}

	// the pre-release fix C: This week's first category on the owner's library opened on a show with 15 of 16 episodes
	// watched, with another 9 in beside it, and its tile said "41 shows, none watched"
	[Fact]
	public void StartedShows_ComeAfterTheUnstarted_FinishedOnesLast_AndTheCountSaysHowManyAreUnwatched()
	{
		var nearlyDone = Title("Nearly Done", IndexKind.Series, ["Comedy"], rating: 9.0) with { Unplayed = 1 }; // 15 of 16: finished by the 90 % rule
		var underWay = Title("Under Way", IndexKind.Series, ["Comedy"], rating: 8.5) with { Unplayed = 20 }; // 9 in: started
		var fresh = Enumerable.Range(0, 8).Select(i => Title($"Fresh {i}", IndexKind.Series, ["Comedy"], rating: 6 + (i / 10.0)) with { Unplayed = 10 }).ToList();
		var plays = new Dictionary<Guid, SeriesPlays> { [nearlyDone.Id] = new(15, Now.AddDays(-1)), [underWay.Id] = new(9, Now.AddDays(-3)) };
		List<IndexEntry> titles = [nearlyDone, underWay, .. fresh];

		var run = new MicroGenreBuilder(Catalogue(ComedyShows)).Run(BuildContext.For(new IndexSnapshot([], titles, plays, HasPeople: false, Now), Now));

		var made = Assert.Single(run.Collections);
		var names = made.ItemIds.Select(id => titles.First(x => x.Id == id).Name).ToList();
		Assert.Equal(fresh.Select(x => x.Name).Order(StringComparer.Ordinal), names.Take(8).Order(StringComparer.Ordinal)); // the unstarted first, though rated lower
		Assert.Equal(["Under Way", "Nearly Done"], names.Skip(8));
		Assert.All(made.Cover, x => Assert.StartsWith("Fresh", x.Name, StringComparison.Ordinal)); // the tile's cover: none they started
		Assert.Equal("10 shows, 8 unwatched", made.Reason!.Count.Text); // was "10 shows, none watched"
	}

	[Fact]
	public void TheFloor_IsEightUnwatched_TheWatchedDontCount()
	{
		List<IndexEntry> seven = [.. Enumerable.Range(0, 7).Select(i => Title($"Crime {i}")), .. Enumerable.Range(0, 5).Select(i => Title($"Seen {i}", played: true))];
		List<IndexEntry> eight = [.. seven, Title("Crime 7")];
		var builder = new MicroGenreBuilder(Catalogue(CrimeDrama));

		var under = builder.Run(Context(seven));
		var past = builder.Run(Context(eight));

		Assert.Empty(under.Collections);
		Assert.Equal(new MicroGenrePool("crime-drama-1970s", IndexKind.Movie, 12, 7, MicroGenreVerdict.UnderFloor), Assert.Single(under.Census.Pools));
		Assert.Single(past.Collections);
		Assert.Equal(MicroGenreVerdict.Made, Assert.Single(past.Census.Pools).Verdict);
		Assert.Equal(8, MicroGenreBuilder.Floor);
	}

	private const string CrimeDrama = """
		{ "id": "crime-drama-1970s", "name": "Gritty 1970s Crime Dramas", "kind": "films", "family": "crime",
		  "rule": { "genresAll": ["Crime", "Drama"], "years": [1970, 1979] }, "reason": "Crime and drama, 1970–79" }
		""";

	[Fact]
	public void TheCeiling_IsOnePercentOfTheCategorysOwnKind_SoAShowCategoryIsJudgedAgainstTheShows()
	{
		// 6,045 films (ceiling 61) and 3,045 shows (ceiling 31): 45 unwatched comedies are a category of films, a filter of shows
		List<IndexEntry> titles =
		[
			.. Enumerable.Range(0, 6_000).Select(i => Title($"Film {i}", genres: ["Filler"])), .. Enumerable.Range(0, 45).Select(i => Title($"Comedy {i}", genres: ["Comedy"])),
			.. Enumerable.Range(0, 3_000).Select(i => Title($"Show {i}", IndexKind.Series, genres: ["Filler"])),
			.. Enumerable.Range(0, 45).Select(i => Title($"Sitcom {i}", IndexKind.Series, genres: ["Comedy"])),
		];

		var run = new MicroGenreBuilder(Catalogue(Comedies, ComedyShows)).Run(Context(titles));

		Assert.Equal(["micro:comedies"], run.Collections.Select(x => x.Id));
		Assert.Equal(61, run.Census.FilmCeiling);
		Assert.Equal(31, run.Census.ShowCeiling);
		Assert.Equal(MicroGenreVerdict.OverCeiling, run.Census.Pools.Single(x => x.Id == "comedy-shows").Verdict);
		Assert.Equal(259, MicroGenreBuilder.CeilingOf(MicroGenreFixture.SurveyFilms));
		Assert.Equal(50, MicroGenreBuilder.CeilingOf(MicroGenreFixture.SurveyShows));
		Assert.Equal(MicroGenreBuilder.CeilingLeast, MicroGenreBuilder.CeilingOf(100));
		Assert.Equal(MicroGenreBuilder.CeilingLeast, MicroGenreBuilder.CeilingOf(0));
	}

	[Fact]
	public void TheTitles_TheUnwatchedFirst_EachPartBestFirst()
	{
		List<IndexEntry> titles =
		[
			Title("Unwatched 5", rating: 5), Title("Watched 9", rating: 9, played: true), Title("Unwatched 8", rating: 8), Title("Unwatched 6", rating: 6),
			Title("Watched 7", rating: 7, played: true), Title("Unwatched 7", rating: 7), Title("Unwatched 4", rating: 4), Title("Unwatched 9", rating: 9),
			Title("Unwatched 3", rating: 3), Title("Unwatched 2", rating: 2),
		];
		var names = titles.ToDictionary(x => x.Id, x => x.Name);

		var made = Assert.Single(new MicroGenreBuilder(Catalogue(CrimeDrama)).Run(Context(titles)).Collections);

		Assert.Equal(
			["Unwatched 9", "Unwatched 8", "Unwatched 7", "Unwatched 6", "Unwatched 5", "Unwatched 4", "Unwatched 3", "Unwatched 2", "Watched 9", "Watched 7"],
			made.ItemIds.Select(x => names[x]));
	}

	[Fact]
	public void CopiesOfATitle_CountOnce()
	{
		// seven crime dramas, one in two libraries: seven titles, under the floor (eight files)
		List<IndexEntry> titles = [.. Enumerable.Range(0, 7).Select(i => Title($"Crime {i}", tmdb: $"{100 + i}")), Title("Crime 0 (4K)", tmdb: "100")];

		var run = new MicroGenreBuilder(Catalogue(CrimeDrama)).Run(Context(titles));

		Assert.Empty(run.Collections);
		Assert.Equal(7, Assert.Single(run.Census.Pools).Titles);
	}

	[Fact]
	public void TheKind_IsMicroGenre_AndAKindGivenIsUsed()
	{
		// the wiring step (wip/190-wire) added SmartCollectionKind.MicroGenre = 300 and set the seam to it; a builder may still be
		// given any kind (tests)
		List<IndexEntry> titles = [.. Enumerable.Range(0, 8).Select(i => Title($"Crime {i}"))];
		var context = Context(titles);

		Assert.Equal(SmartCollectionKind.MicroGenre, MicroGenreBuilder.PendingKind);
		Assert.Equal(300, (int)SmartCollectionKind.MicroGenre);
		Assert.Equal(SmartCollectionKind.MicroGenre, new MicroGenreBuilder().Kind);
		var made = new MicroGenreBuilder(Catalogue(CrimeDrama), SmartCollectionKind.Keyword).Build(context).ToList();
		Assert.Equal(SmartCollectionKind.Keyword, Assert.Single(made).Kind);
		Assert.Equal(new BuildGates.Gate(1, 1, 1), context.Gates.Of(SmartCollectionKind.Keyword));
	}

	[Fact]
	public void TheCensus_CountsEachVerdict_SaysItThroughTheGates_AndNamesNoTitle()
	{
		var titles = MicroGenreFixture.Library(4_300, 830, seed: 11);
		var context = Context(titles);
		var rules = MicroGenreFixture.Rules(300);

		var run = new MicroGenreBuilder(new MicroGenreCatalogue(rules, [])).Run(context);
		var census = run.Census;

		Assert.Equal(300, census.Rules);
		Assert.Equal(300, census.FilmRules + census.ShowRules);
		Assert.Equal(4_300, census.Films);
		Assert.Equal(830, census.Shows);
		Assert.Equal(census.Rules, census.Made + census.WaitForText + census.WaitForKeywords + census.OutOfSeason + census.MatchNone + census.UnderFloor + census.OverCeiling);
		Assert.Equal(run.Collections.Count, census.Made);
		Assert.True(census.Made > 0 && census.UnderFloor > 0 && census.OverCeiling > 0 && census.WaitForText > 0, census.Note);
		Assert.Equal(rules.Select(x => x.Id), census.Pools.Select(x => x.Id));
		Assert.All(census.Pools.Where(x => x.Verdict == MicroGenreVerdict.Made), x => Assert.InRange(x.Unwatched, MicroGenreBuilder.Floor, x.Kind == IndexKind.Movie ? census.FilmCeiling : census.ShowCeiling));
		Assert.All(run.Collections, x => Assert.Equal(rules.Single(r => r.CollectionId == x.Id).Kind, x.Only)); // films or shows, never both
		Assert.Equal(new BuildGates.Gate(300, census.PastFloor, census.Made), context.Gates.Of(MicroGenreBuilder.PendingKind));
		Assert.Equal(census.Note, context.Gates.NoteOf(MicroGenreBuilder.PendingKind));
		// counts only: no title's name in the note (the titles are "Film 12", "Show 40")
		Assert.DoesNotContain("Film ", census.Note, StringComparison.Ordinal);
		Assert.DoesNotContain("Show ", census.Note, StringComparison.Ordinal);
		Assert.Equal(43, census.FilmCeiling); // 1 % of 4,300
		Assert.Equal(30, census.ShowCeiling); // 1 % of 830 is under the least
		Assert.StartsWith($"films {census.FilmRules} rules, ceiling 43; shows {census.ShowRules} rules, ceiling 30; text pass {census.WaitForText}", census.Note, StringComparison.Ordinal);
	}

	[Fact]
	public void AnEmptyCatalogue_MakesNothing_AndCountsNothing()
	{
		var context = Context([.. Enumerable.Range(0, 20).Select(i => Title($"Crime {i}"))]);

		var run = new MicroGenreBuilder(MicroGenreCatalogue.Empty).Run(context);

		Assert.Empty(run.Collections);
		Assert.Equal(0, run.Census.Rules);
		Assert.Empty(run.Census.Pools);
		Assert.Equal(new BuildGates.Gate(0, 0, 0), context.Gates.Of(MicroGenreBuilder.PendingKind));
	}

	[Fact]
	public void TheShippedCataloguesBuilder_BuildsWithoutAThrow()
	{
		// the cat stream's file may not be in yet: an empty catalogue then, and no collection
		var context = Context([.. Enumerable.Range(0, 20).Select(i => Title($"Crime {i}"))]);

		var made = new MicroGenreBuilder().Build(context).ToList();

		Assert.All(made, x => Assert.StartsWith(MicroGenre.IdPrefix, x.Id, StringComparison.Ordinal));
		Assert.Equal(MicroGenres.Shipped.Categories.Count, context.Gates.Of(MicroGenreBuilder.PendingKind)!.Value.Candidates);
	}
}

/// <summary>Nixie 1.9: the builder's time over the survey's library, run alone (printed for the wiring step's choice of list).</summary>
[Collection(MicroGenreBudgetCollection.Name)]
public class MicroGenreBuilderBudgetTests(ITestOutputHelper output)
{
	[Fact]
	public void TheBuilder_OverTheSurveysLibrary_TakesLittleMoreThanTheMatcher()
	{
		var entries = MicroGenreFixture.Library();
		var index = new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, DateTimeOffset.UnixEpoch);
		var catalogue = new MicroGenreCatalogue(MicroGenreFixture.Rules(300), []);
		new MicroGenreBuilder(catalogue).Run(BuildContext.For(new IndexSnapshot([], MicroGenreFixture.Library(600, 120, seed: 3), new Dictionary<Guid, SeriesPlays>(), false, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch)); // compiled first

		var times = new List<double>();
		MicroGenreRun? run = null;
		for (var i = 0; i < 5; i++)
		{
			// a context as a build round has one: its titles collapsed and its scores made by the builders before (not this one's cost)
			var context = BuildContext.For(index, DateTimeOffset.UnixEpoch);
			_ = context.Quality;
			GC.Collect();
			GC.WaitForPendingFinalizers();
			var watch = Stopwatch.StartNew();
			run = new MicroGenreBuilder(catalogue).Run(context);
			times.Add(watch.Elapsed.TotalMilliseconds);
		}

		var best = times.Min();
		output.WriteLine($"builder rounds: {string.Join(", ", times.Select(x => $"{x:F1}"))} ms; best {best:F1} ms");
		output.WriteLine($"census: {run!.Census.Note}; made {run.Census.Made}");
		// a cold round of the matcher, then the made collections' order and words: under the matcher's own limit (about 40 ms
		// idle on the desktop of 25 November 2026; a 10× regression fails)
		Assert.True(best <= MicroGenreBudgetTests.ColdLimitMs, $"the builder took {best:F1} ms");
		Assert.True(run.Census.Made > 50, run.Census.Note);
	}
}
