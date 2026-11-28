using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;
using Microsoft.Extensions.Logging;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream wire): the categories wired into the app: their kind (numbered apart, with its words),
/// the catalog (published with the first rows, found by their ids, off with their switch), the pages' builds of one index
/// making the posting lists once, and the library strip without them.
/// </summary>
public class MicroGenreWiringTests
{
	private static readonly DateTimeOffset Now = new(2026, 11, 25, 18, 0, 0, TimeSpan.Zero);
	private static int s_next;

	private static IndexEntry Title(string name, string[] genres, int? year, IndexKind kind = IndexKind.Movie, Guid? library = null, long? minutes = null) => new()
	{
		Id = new Guid(Interlocked.Increment(ref s_next), 190, 0, new byte[8]),
		Name = name,
		Kind = kind,
		LibraryId = library ?? Guid.Empty,
		Genres = genres,
		Year = year,
		RunTimeTicks = TimeSpan.FromMinutes(minutes ?? (kind == IndexKind.Movie ? 110 : 30)).Ticks,
		Rating = 7,
		PrimaryTag = "p",
	};

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries) =>
		new([], [.. entries], new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Now) { Key = "wire:" + Guid.NewGuid() };

	// a shipped category whose rule is genres and years alone (so a fixture can be made to fit it, whatever the owner renames),
	// and twelve unwatched titles that fit it, beside 200 that fit nothing of it
	private static (MicroGenre Category, IndexSnapshot Index) OneShippedCategory(Guid? library = null)
	{
		var category = MicroGenres.Shipped.Categories.First(x => x.Kind == IndexKind.Movie && x.Rule.GenresAll.Count > 0 && !x.Rule.Years.IsOpen
			&& x.Rule is { GenresAny.Count: 0, KeywordsAny.Count: 0, KeywordsAll.Count: 0, CountriesAny.Count: 0, StudiosAny.Count: 0, CommunityMin: null, CriticMin: null, TextAny.Count: 0 }
			&& x.Rule.LengthMinutes.IsOpen);
		var year = category.Rule.Years.From ?? category.Rule.Years.To!.Value;
		var fits = Enumerable.Range(0, 12).Select(i => Title($"Fits {i}", [.. category.Rule.GenresAll], year, library: library)).ToList();
		Assert.All(fits, x => Assert.True(MicroGenreMatcher.Fits(category, x)));
		var others = Enumerable.Range(0, 200).Select(i => Title($"Other {i}", ["Filler"], 1900 + (i % 120), library: library));
		return (category, Index([.. fits, .. others]));
	}

	[Fact]
	public void TheKind_IsNumberedApart_AndHasItsWords()
	{
		// Home's saved page keeps a kind by its number: the categories' is 300, apart from 1.8's (200s) and the lists' (100s)
		Assert.Equal(300, (int)SmartCollectionKind.MicroGenre);
		Assert.Equal(SmartCollectionKind.MicroGenre, MicroGenreBuilder.PendingKind);
		Assert.Equal("Category", SmartCollectionKinds.Singular(SmartCollectionKind.MicroGenre));
		Assert.Equal("Categories", SmartCollectionKinds.Plural(SmartCollectionKind.MicroGenre));

		var (category, index) = OneShippedCategory();
		var made = SmartCollectionCatalog.BuildAll(index, Now).Single(x => x.Id == category.CollectionId);
		Assert.Equal(SmartCollectionKind.MicroGenre, made.Kind);
		Assert.Equal("Made by Nixie · Category", made.Eyebrow);
		Assert.Equal($"{category.Name}, made by Nixie, category, 12 films", made.AutomationName);
		Assert.StartsWith($"{category.Name}, made by Nixie, category, {category.Reason}", made.TileAutomationName, StringComparison.Ordinal);
	}

	[Fact]
	public void TheCatalog_PublishesTheCategories_WithTheFirstRows_TheirPageRowAndSwitchFirst()
	{
		Assert.Single(SmartCollectionCatalog.PublishedBuilders.OfType<MicroGenreBuilder>());
		Assert.DoesNotContain(SmartCollectionCatalog.LaterBuilders, x => x.Kind == SmartCollectionKind.MicroGenre);
		Assert.Contains(SmartCollectionKind.MicroGenre, SmartCollectionCatalog.Published);
		Assert.DoesNotContain(SmartCollectionKind.MicroGenre, SmartCollectionCatalog.OnDemandOnly);
		Assert.Equal(SmartCollectionKind.MicroGenre, SmartCollectionKinds.PageOrder[0]); // the Collections page's first kind row
		Assert.Equal(SmartCollectionKind.MicroGenre, SmartCollectionKinds.Switched[0]); // and Settings' first kind switch
		Assert.Equal(SmartCollectionKind.MicroGenre, SmartCollectionCatalog.KindOf("micro:crime-dramas-1970s"));
		Assert.Equal(SmartCollectionKind.MicroGenre, SmartCollectionCatalog.KindOf(MicroGenre.IdPrefix));
		Assert.Null(SmartCollectionCatalog.KindOf("micro"));
	}

	[Fact]
	public void ACategory_IsMadeByThePagesBuild_AndFoundByItsId_TheSameTitles()
	{
		var (category, index) = OneShippedCategory();

		var built = SmartCollectionCatalog.BuildAll(index, Now).Single(x => x.Id == category.CollectionId);
		var found = SmartCollectionCatalog.Find(index, category.CollectionId, Now);

		Assert.NotNull(found);
		Assert.Equal(SmartCollectionKind.MicroGenre, found.Kind);
		Assert.Equal(category.Name, found.Title);
		Assert.Equal(built.ItemIds, found.ItemIds);
		Assert.Equal(12, found.ItemIds.Count);
		Assert.Equal(IndexKind.Movie, found.Only);
		Assert.Null(SmartCollectionCatalog.Find(index, MicroGenre.IdPrefix + "no-such-category", Now));
	}

	[Fact]
	public void TheSwitchOff_MakesNoCategory_AndTheirBuilderDoesntRun()
	{
		// rule 9: off is no work at all, not work thrown away: the census says the kind was off, not its counts
		var (category, index) = OneShippedCategory();
		var log = new CoreListLogger<MicroGenreWiringTests>();
		var off = new HashSet<SmartCollectionKind> { SmartCollectionKind.MicroGenre };

		var made = SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, index, Now, null, off, FluentFin.Core.Outside.OutsideListsSnapshot.Empty, log, census: true);

		Assert.DoesNotContain(made, x => x.Kind == SmartCollectionKind.MicroGenre);
		var line = Assert.Single(log.Entries, x => x.Message.StartsWith("Made by Nixie census: ", StringComparison.Ordinal)).Message;
		Assert.Contains("MicroGenre off", line, StringComparison.Ordinal);
		Assert.Contains(SmartCollectionCatalog.BuildAll(index, Now), x => x.Id == category.CollectionId); // on: made
	}

	[Fact]
	public async Task ThePagesBuildsOfOneIndex_CollapseItsTitlesOnce_SoThePostingListsAreMadeOnce()
	{
		// Home, the Collections page and the Libraries page each build the same index: the categories' posting lists are kept
		// with its collapsed titles (MicroGenreMatcher.For), so the titles must be the same ones at each build (35 ms a build at
		// the survey's size otherwise)
		var (_, index) = OneShippedCategory();
		var collapses = 0;
		void Count(IReadOnlyList<IndexEntry> entries)
		{
			if (ReferenceEquals(entries, index.Entries))
			{
				Interlocked.Increment(ref collapses);
			}
		}

		BuildContext.Collapsing += Count;
		try
		{
			SmartCollectionCatalog.BuildAll(index, Now);
			SmartCollectionCatalog.BuildAll(index, Now.AddHours(1));
			await SmartCollectionCatalog.BuildAllAsync(index, Now);
		}
		finally
		{
			BuildContext.Collapsing -= Count;
		}

		Assert.Equal(1, collapses);
	}

	// --- the keyword tiers: an index of revision 3 (1.8.1's) kept the allowlist's keywords alone -------------------------------

	private const string NotAtTheOffice = """
		{ "id": "comedies-not-office", "name": "Comedies Away from the Office", "kind": "films", "family": "comedy",
		  "rule": { "genresAll": ["Comedy"], "keywordsNone": ["office"] }, "reason": "Comedy, TMDb keywords for no office" }
		""";

	private const string HeistComedies = """
		{ "id": "heist-comedies", "name": "Heist Comedies", "kind": "films", "family": "heist",
		  "rule": { "genresAll": ["Comedy"], "keywordsAll": ["heist"] }, "reason": "TMDb keyword “heist”, comedy" }
		""";

	[Fact]
	public void OnAnIndexOfRevision3_ACategoryOnAKeywordItDidntKeep_Waits_OneOnTheAllowlistIsMade()
	{
		// rule 4: 1.8.1's index has no "office" tag on any title because it never kept one, not because no title has it: a
		// keywordsNone of it would let the office comedies in. The category waits for the read again behind it
		var catalogue = MicroGenres.Load(MicroGenreFixture.File(NotAtTheOffice, HeistComedies), MicroGenres.Matchable);
		Assert.Empty(catalogue.Problems);
		List<IndexEntry> titles =
		[
			.. Enumerable.Range(0, 10).Select(i => Title($"Heist comedy {i}", ["Comedy"], 1990) with { Tags = ["heist"] }),
			.. Enumerable.Range(0, 5).Select(i => Title($"Office comedy {i}", ["Comedy"], 2000) with { Tags = ["office"] }),
			.. Enumerable.Range(0, 10).Select(i => Title($"Comedy {i}", ["Comedy"], 2010)),
		];
		var four = Index(titles);
		var three = Index(titles.Select(x => x with { Tags = [.. x.Tags.Where(Keywords.IsNamed)] })) with { KeywordsKnown = false };

		var onThree = new MicroGenreBuilder(catalogue).Run(BuildContext.For(three, Now));
		var onFour = new MicroGenreBuilder(catalogue).Run(BuildContext.For(four, Now));

		Assert.Equal(["micro:heist-comedies"], onThree.Collections.Select(x => x.Id));
		Assert.Equal(MicroGenreVerdict.WaitsForKeywords, onThree.Census.Pools.Single(x => x.Id == "comedies-not-office").Verdict);
		Assert.Equal(1, onThree.Census.WaitForKeywords);
		Assert.EndsWith(", keywords to read again 1; keywords not kept yet", onThree.Census.Note, StringComparison.Ordinal);
		// read again (revision 4): made, without the office comedies
		var away = onFour.Collections.Single(x => x.Id == "micro:comedies-not-office");
		Assert.Equal(20, away.ItemIds.Count);
		Assert.DoesNotContain(away.ItemIds, id => titles.Single(x => x.Id == id).Tags.Contains("office"));
		Assert.Equal(0, onFour.Census.WaitForKeywords);
		Assert.DoesNotContain("keywords to read again", onFour.Census.Note, StringComparison.Ordinal);
	}

	// --- the seasonal family: in season only (the review of wip/190-wire, Minor 4) -------------------------------------------

	private static string Seasonal(string id, string name, string family, string rule) =>
		$$"""{ "id": "{{id}}", "name": "{{name}}", "kind": "films", "family": "{{family}}", "rule": { {{rule}} }, "reason": "TMDb keywords" }""";

	private static readonly string[] s_seasonalCatalogue =
	[
		Seasonal("christmas-comedies", "Christmas Comedies", "seasonal", """ "genresAll": ["Comedy"], "keywordsAll": ["christmas"] """),
		Seasonal("halloween-family", "Halloween Movies for Families", "seasonal", """ "genresAll": ["Family"], "keywordsAll": ["halloween"] """),
		Seasonal("thanksgiving", "Thanksgiving Movies", "seasonal", """ "keywordsAny": ["thanksgiving"] """),
		Seasonal("holiday-films", "Holiday Films", "seasonal", """ "keywordsAll": ["holiday"] """),
		Seasonal("christmas-crime", "Crime Stories at Christmas", "crime", """ "genresAll": ["Crime"], "keywordsAll": ["christmas"] """), // not the seasonal family
	];

	private static List<IndexEntry> SeasonalTitles() =>
	[
		.. Enumerable.Range(0, 10).Select(i => Title($"Christmas comedy {i}", ["Comedy"], 1990) with { Tags = ["christmas"] }),
		.. Enumerable.Range(0, 10).Select(i => Title($"Halloween family {i}", ["Family"], 1995) with { Tags = ["halloween"] }),
		.. Enumerable.Range(0, 10).Select(i => Title($"Thanksgiving {i}", ["Drama"], 2000) with { Tags = ["thanksgiving"] }),
		.. Enumerable.Range(0, 10).Select(i => Title($"Holiday {i}", ["Romance"], 2005) with { Tags = ["holiday"] }),
		.. Enumerable.Range(0, 10).Select(i => Title($"Christmas crime {i}", ["Crime"], 2010) with { Tags = ["christmas"] }),
	];

	[Theory]
	[InlineData("2026-06-10T12:00:00-08:00", "christmas-crime")] // Christmas horror in June was the review's case
	[InlineData("2026-10-20T12:00:00-08:00", "halloween-family,holiday-films,christmas-crime")]
	[InlineData("2026-11-10T12:00:00-08:00", "thanksgiving,holiday-films,christmas-crime")]
	[InlineData("2026-11-25T12:00:00-08:00", "christmas-comedies,thanksgiving,holiday-films,christmas-crime")] // Thanksgiving's last week
	[InlineData("2026-11-27T09:00:00-08:00", "christmas-comedies,holiday-films,christmas-crime")]
	[InlineData("2026-12-05T12:00:00-08:00", "christmas-comedies,holiday-films,christmas-crime")]
	[InlineData("2027-01-02T12:00:00-08:00", "christmas-crime")]
	public void TheSeasonalFamily_IsMadeOnlyInItsSeason_ByTheSeasonalRowsOwnWindows(string when, string made)
	{
		var now = DateTimeOffset.Parse(when, System.Globalization.CultureInfo.InvariantCulture);
		var catalogue = MicroGenres.Load(MicroGenreFixture.File(s_seasonalCatalogue), MicroGenres.Matchable);
		Assert.Empty(catalogue.Problems);

		var run = new MicroGenreBuilder(catalogue).Run(BuildContext.For(Index(SeasonalTitles()), now));

		Assert.Equal(made.Split(','), run.Collections.Select(x => x.Id[MicroGenre.IdPrefix.Length..]));
		var outOfSeason = 5 - run.Collections.Count;
		Assert.Equal(outOfSeason, run.Census.OutOfSeason);
		Assert.All(run.Census.Pools.Where(x => !run.Collections.Any(c => c.Id == MicroGenre.IdPrefix + x.Id)), x => Assert.Equal(MicroGenreVerdict.OutOfSeason, x.Verdict));
		Assert.Equal(outOfSeason > 0, run.Census.Note.Contains($", out of season {outOfSeason}", StringComparison.Ordinal));
	}

	[Fact]
	public void OnAnIndexOfRevision3_TheSeasonalKeywordsItKeptArePresent_ThanksgivingsWaits()
	{
		// 1.8.1 kept the six seasonal keywords: a Christmas category on its index is made in December, not held for the day's
		// read; Thanksgiving's keyword came with revision 4, so its category waits for it in November
		var catalogue = MicroGenres.Load(MicroGenreFixture.File(s_seasonalCatalogue), MicroGenres.Matchable);
		var three = Index([.. SeasonalTitles().Select(x => x with { Tags = [.. x.Tags.Where(Keywords.KeptByRevision3)] })]) with { KeywordsKnown = false };

		var december = new MicroGenreBuilder(catalogue).Run(BuildContext.For(three, new DateTimeOffset(2026, 12, 5, 12, 0, 0, TimeSpan.FromHours(-8))));
		var november = new MicroGenreBuilder(catalogue).Run(BuildContext.For(three, new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(-8))));

		Assert.Equal(["micro:christmas-comedies", "micro:holiday-films", "micro:christmas-crime"], december.Collections.Select(x => x.Id));
		Assert.Equal(0, december.Census.WaitForKeywords);
		Assert.Equal(MicroGenreVerdict.WaitsForKeywords, november.Census.Pools.Single(x => x.Id == "thanksgiving").Verdict);
		Assert.True(Keywords.KeptByRevision3("Christmas "));
		Assert.False(Keywords.KeptByRevision3("thanksgiving"));
		Assert.False(Keywords.KeptByRevision3("office"));
	}

	// --- the census line: the keyword tiers' tally, counts only ----------------------------------------------------------------

	[Fact]
	public void TheCensusLine_TalliesTheKeywordTiers_CountsOnly_AndSaysWhenTheyArentKeptYet()
	{
		// six titles with "heist" (named), "office" and "witty" (matchable), out of nine tags each; four with "office", of two
		List<IndexEntry> titles =
		[
			.. Enumerable.Range(0, 6).Select(i => Title($"Tagged {i}", ["Comedy"], 2000) with { Tags = ["heist", "office", "witty"], TagCount = 9 }),
			.. Enumerable.Range(0, 4).Select(i => Title($"Plain {i}", ["Drama"], 1990) with { Tags = ["office"], TagCount = 2 }),
		];
		string Line(IndexSnapshot index)
		{
			var log = new CoreListLogger<MicroGenreWiringTests>();
			SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, index, Now, null, null, FluentFin.Core.Outside.OutsideListsSnapshot.Empty, log, census: true);
			return Assert.Single(log.Entries, x => x.Message.StartsWith("Made by Nixie census: ", StringComparison.Ordinal)).Message;
		}

		var four = Line(Index(titles));
		var three = Line(Index(titles) with { KeywordsKnown = false });

		var beyond = Keywords.Matchable.Count(x => !Keywords.IsNamed(x));
		Assert.Contains($"; keywords 2 of {beyond} seen, tags 22 of 62 kept]", four, StringComparison.Ordinal);
		Assert.Contains("; keywords not kept yet]", three, StringComparison.Ordinal);
		Assert.Contains(", keywords to read again ", three, StringComparison.Ordinal); // the shipped catalogue's keyword rules wait
		foreach (var line in new[] { four, three })
		{
			Assert.DoesNotContain("office", line, StringComparison.OrdinalIgnoreCase); // counts only: no keyword, no title
			Assert.DoesNotContain("witty", line, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("Tagged", line, StringComparison.Ordinal);
			Assert.DoesNotContain("Plain", line, StringComparison.Ordinal);
		}
	}

	// --- the naming guard: More like's reason line names only the allowlist's keywords ----------------------------------------

	[Fact]
	public void MoreLikesReason_NeverNamesAKeywordBeyondTheAllowlist_ThoughItCountsTowardHowAlike()
	{
		// 1.7's review #1: only hand-picked keywords may be said. Since revision 4 the index keeps the categories' matchable ones
		// too ("witty", "office"): they make titles alike, and are never named
		var anchor = Title("The Anchor", ["Comedy"], 1995) with { Tags = ["witty", "office", "heist"] };
		var alike = Enumerable.Range(0, 9).Select(i => Title($"Alike {i}", ["Comedy"], 1995) with { Tags = ["witty", "office", "heist"] }).ToList();
		var officeOnly = Enumerable.Range(0, 9).Select(i => Title($"Office {i}", ["Drama"], 1960) with { Tags = ["witty", "office"] }).ToList();
		var others = Enumerable.Range(0, 120).Select(i => Title($"Other {i}", ["Drama"], 1950 + (i % 60))).ToList();
		var similarity = TitleSimilarity.Of([anchor, .. alike, .. officeOnly, .. others], new AnchorsTests.Facts());

		var named = similarity.Shared(anchor, alike, 5);
		Assert.Equal(["heists"], named.Where(x => x.Kind == TitleSimilarity.FeatureKind.Keyword).Select(x => x.Label));
		Assert.Contains(named, x => x.Label == "comedy");
		// sharing matchable keywords alone, held by 14 % of the films: not alike (a matchable keyword is no hook, and these
		// aren't rare by share: the review of wip/190-wire, Major 1), and nothing of them said
		Assert.DoesNotContain(similarity.Like(anchor, _ => true), x => x.Title.Id == officeOnly[0].Id);
		Assert.DoesNotContain(similarity.Shared(anchor, officeOnly, 5), x => x.Kind == TitleSimilarity.FeatureKind.Keyword);
		Assert.All(similarity.Shared(anchor, [.. alike, .. officeOnly], 9), x => Assert.DoesNotContain(x.Label, new[] { "witty", "office" }));
	}

	// the review of wip/190-wire (Major 1), its probe kept: a common matchable keyword ("suspenseful", on a fifth of the films) made
	// it a hook, and opened More like's rare gate for dramas that shared nothing else rare: "More like The Drama: Shares drama
	// and the 1990s" on Home's first slot, the very row 1.8's gate stops
	private static readonly DateTimeOffset ProbeNow = new(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(-8));

	private static List<SmartCollection> MoreLike(IEnumerable<IndexEntry> entries) =>
		[.. new MoreLikeBuilder(_ => new AnchorsTests.Facts()).Build(BuildContext.For(new IndexSnapshot([], [.. entries], new Dictionary<Guid, SeriesPlays>(), HasPeople: true, ProbeNow), ProbeNow))];

	[Fact]
	public void MoreLike_ACommonMatchableKeyword_DoesntOpenTheRareGate()
	{
		var anchor = AnchorsTests.Film("The Drama", 1995, ["Drama"], director: "Someone Else", played: true);
		var dramas = Enumerable.Range(1, 30).Select(i => AnchorsTests.Film($"Nineties Drama {i}", 1990 + (i % 10), ["Drama"], director: $"Drama Director {i}")).ToList();
		var comedies = Enumerable.Range(1, 30).Select(i => AnchorsTests.Film($"Newer Comedy {i}", 2000 + (i % 20), ["Comedy"], director: $"Comedy Director {i}")).ToList();

		// as 1.8.1's index (revision 3) had them: the tone keyword dropped
		Assert.DoesNotContain(MoreLike([anchor, .. dramas, .. comedies]), x => x.Kind == SmartCollectionKind.MoreLike);

		// revision 4: "suspenseful" (matchable, never named) on the anchor and 12 of 60 others (20 %: not rare by share)
		var tagged = dramas.Take(12).Select(x => x with { Tags = ["suspenseful"] }).ToList();
		var made = MoreLike([anchor with { Tags = ["suspenseful"] }, .. tagged, .. dramas.Skip(12), .. comedies]);

		Assert.DoesNotContain(made, x => x.Kind == SmartCollectionKind.MoreLike);
		// an allowlisted keyword in its place is a hook, as in 1.8.1: the row is made, and says it
		var heists = MoreLike([anchor with { Tags = ["heist"] }, .. dramas.Take(12).Select(x => x with { Tags = ["heist"] }), .. dramas.Skip(12), .. comedies]);
		Assert.StartsWith("Shares heists", Assert.Single(heists, x => x.Kind == SmartCollectionKind.MoreLike).ReasonText, StringComparison.Ordinal);
	}

	[Fact]
	public void ALibrarysPresetsStrip_HasNoCategory()
	{
		// the categories are the Collections page's row; a library's strip keeps its few broad filters (and builds no 300 rules)
		var library = Guid.NewGuid();
		var (category, index) = OneShippedCategory(library);
		Assert.Contains(SmartCollectionCatalog.BuildAll(index, Now), x => x.Id == category.CollectionId);

		var presets = NixiePresets.For(index, library, Now, most: 100);

		Assert.DoesNotContain(presets, x => x.Kind == SmartCollectionKind.MicroGenre);
		Assert.Contains(SmartCollectionKind.MicroGenre, NixiePresets.NotInStrip);
	}
}
