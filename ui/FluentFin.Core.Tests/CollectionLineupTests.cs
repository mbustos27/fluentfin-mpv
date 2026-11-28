using System.Diagnostics;
using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;
using FluentFin.Core.ViewModels;
using Moq;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-1): the lineup assembler: its gates (a reason, the size floor, the theme ceiling, the artwork, coherence), the
/// greedy novelty rule, the type caps and ten slots, the date seeds (the same all day, a new one tomorrow; themes by the
/// week), and Home's rows from it.
/// </summary>
public sealed class CollectionLineupTests(ITestOutputHelper output)
{
	// a Tuesday in California: the local date is what counts (SmartCollectionCatalog.Now)
	private static readonly TimeSpan Pacific = TimeSpan.FromHours(-8);
	private static readonly DateTimeOffset Tuesday = new(2026, 11, 24, 12, 0, 0, Pacific);
	private static int s_next;

	private static IndexEntry Film(string name, int year = 2000, string[]? genres = null, string[]? tags = null, double? rating = 7.0, bool played = false,
		bool poster = true, string[]? countries = null, IndexKind kind = IndexKind.Movie, int minutes = 110) => new()
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000180"),
			Name = name,
			Kind = kind,
			Year = year,
			Genres = genres ?? ["Drama"],
			Tags = tags ?? [],
			Countries = countries ?? [],
			Rating = rating,
			TagCount = 10, // backs the rating: the score (QualityScore) tells titles apart
			Played = played,
			PrimaryTag = poster ? "p" : null,
			RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
		};

	private static List<IndexEntry> Films(string prefix, int count, int year = 2000, string[]? genres = null, double rating = 7.0, int played = 0, int posters = int.MaxValue) =>
		[.. Enumerable.Range(0, count).Select(i => Film($"{prefix} {i}", year + (i % 10), genres, rating: rating, played: i < played, poster: i < posters))];

	private static SmartCollection Of(string id, SmartCollectionKind kind, IEnumerable<IndexEntry> titles, string? title = null) =>
		new(id, kind, title ?? id, "", false, [.. titles.Select(x => x.Id)]);

	private static Lineup Assemble(IEnumerable<IndexEntry> index, IEnumerable<SmartCollection> collections, DateTimeOffset? now = null, IReadOnlyDictionary<string, int>? lists = null)
	{
		var data = LineupData.Of([.. index]) with { ListSizes = lists ?? new Dictionary<string, int>() };
		return CollectionLineup.Assemble(CollectionLineup.Candidates(collections), data, now ?? Tuesday);
	}

	private static Lineup Assemble(IEnumerable<IndexEntry> index, IEnumerable<LineupCandidate> candidates, DateTimeOffset? now = null) =>
		CollectionLineup.Assemble([.. candidates], LineupData.Of([.. index]), now ?? Tuesday);

	// ---- the gates -------------------------------------------------------------------------------------------------------

	[Fact]
	public void ACollectionWithNoReason_IsNeverInTheLineup()
	{
		var titles = Films("Plain", 12);
		var kinds = new[] { SmartCollectionKind.Franchise, SmartCollectionKind.Director, SmartCollectionKind.Decade, SmartCollectionKind.Saved, SmartCollectionKind.Popular };

		var lineup = Assemble(titles, kinds.Select(k => Of($"{k}:x", k, titles)));

		Assert.Empty(lineup.Picks);
		Assert.All(kinds, k => Assert.Null(CollectionLineup.SlotOf(Of("x", k, titles))));
		// a trending list of the Collections page is one of the Lists; only Home's merged row is the Trending slot
		Assert.Null(CollectionLineup.SlotOf(Of("trending:movie", SmartCollectionKind.Trending, titles)));
		Assert.Equal(LineupSlot.Trending, CollectionLineup.SlotOf(Of(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, titles)));
	}

	[Fact]
	public void ATheme_NeedsEightUnwatchedTitles()
	{
		var seven = Films("Seven", 20, played: 13);   // 7 unwatched of 20
		var eight = Films("Eight", 20, played: 12);   // 8 unwatched of 20

		var lineup = Assemble([.. seven, .. eight], [Of("keyword:seven", SmartCollectionKind.Keyword, seven), Of("keyword:eight", SmartCollectionKind.Keyword, eight)]);

		Assert.Equal(["keyword:eight"], lineup.Picks.Select(x => x.Collection.Id));
		Assert.Equal(new LineupSlotCount(2, 1, 1, 1, 1, 1, 1), lineup.Counts[LineupSlot.Theme]);
	}

	[Fact]
	public void ACanonList_NeedsTenMatchedOrATenthOfItsList_AndFiveUnwatched()
	{
		var nine = Films("Nine", 9);
		var ten = Films("Ten", 10, played: 5);
		var tenWatched = Films("Watched", 10, played: 6);
		var small = Films("Small", 6);
		var lists = new Dictionary<string, int> { ["award:nine"] = 200, ["award:ten"] = 200, ["award:watched"] = 200, ["award:small"] = 50 };

		var seen = new[] { "award:nine", "award:ten", "award:watched", "award:small" }
			.Select(id => (Id: id, Picks: Assemble([.. nine, .. ten, .. tenWatched, .. small],
				[Of(id, SmartCollectionKind.Award, id switch { "award:nine" => nine, "award:ten" => ten, "award:watched" => tenWatched, _ => small })], lists: lists).Picks.Count))
			.ToDictionary(x => x.Id, x => x.Picks);

		Assert.Equal(0, seen["award:nine"]);    // 9 of 200
		Assert.Equal(1, seen["award:ten"]);     // 10 matched, 5 unwatched
		Assert.Equal(0, seen["award:watched"]); // 10 matched, 4 unwatched
		Assert.Equal(1, seen["award:small"]);   // 6 of a list of 50: more than a tenth
	}

	[Fact]
	public void NextIn_NeedsTwoFilmsOfTheFranchise_OneUnwatched()
	{
		var started = Films("Started", 2, played: 1);
		var done = Films("Done", 3, played: 3);

		var lineup = Assemble([.. started, .. done],
			[new LineupCandidate(Of("next:done", SmartCollectionKind.Franchise, done), LineupSlot.NextIn), new LineupCandidate(Of("next:started", SmartCollectionKind.Franchise, started), LineupSlot.NextIn)]);

		Assert.Equal(["next:started"], lineup.Picks.Select(x => x.Collection.Id)); // the finished one, given first, isn't taken
		Assert.Equal(1, lineup.Counts[LineupSlot.NextIn].Floor);
	}

	[Fact]
	public void APersonsCollection_NeedsFiveUnwatched_AndThePersonalSlotsComeInTheirBuildersOrder()
	{
		// More from and the Spotlight: 5 other unwatched titles (the report's anchored person and Spotlight), not a row's 8
		var four = Films("Four", 6, played: 2);
		var five = Films("Five", 6, played: 1);
		var spotlight = Films("Spot", 5);
		SmartCollection Person(string id, SmartCollectionKind kind, List<IndexEntry> titles) => Of(id, kind, titles);

		var lineup = Assemble([.. four, .. five, .. spotlight],
			[Person("more:four", SmartCollectionKind.MoreFrom, four), Person("more:five", SmartCollectionKind.MoreFrom, five), Person("spotlight:director:x", SmartCollectionKind.Spotlight, spotlight)]);

		Assert.Equal(["more:five", "spotlight:director:x"], lineup.Picks.Select(x => x.Collection.Id));

		// the anchors' builder gives its slots in its own order (a title finished in the last three days first): the lineup
		// keeps it, though a later one's titles are better rated
		var fresh = Films("Fresh", 10, rating: 6.0);
		var older = Films("Older", 10, rating: 9.0);
		var third = Films("Third", 10, rating: 9.5);
		var liked = Assemble([.. fresh, .. older, .. third],
			[Of("morelike:fresh", SmartCollectionKind.MoreLike, fresh), Of("morelike:older", SmartCollectionKind.MoreLike, older), Of("morelike:third", SmartCollectionKind.MoreLike, third)]);
		Assert.Equal(["morelike:fresh", "morelike:older"], liked.Picks.Select(x => x.Collection.Id)); // two at most; none lost to coherence
		Assert.All(new[] { SmartCollectionKind.NextIn, SmartCollectionKind.MoreLike, SmartCollectionKind.MoreFrom }, k => Assert.True(CollectionLineup.IsPersonal(CollectionLineup.SlotOf(Of("x", k, fresh))!.Value)));
		Assert.Equal(LineupSlot.Spotlight, CollectionLineup.SlotOf(Of("spotlight:x", SmartCollectionKind.Spotlight, fresh)));
	}

	// 1.8.1 (the owner's library, fix 1): the personal picks by their anchor's recency across their slots, a fresh anchor (the last
	// three days) first, then a dated one, then one with no date, the slot's prior only breaking ties within a group. On the
	// owner's library "Next in Ant-Man" (films there have no play date) led Home over the More like of a show
	// finished two days before
	[Fact]
	public void ThePersonalPicks_ComeByTheirAnchorsRecency_FreshThenDatedThenUndated()
	{
		var next = Films("Next", 3, played: 1);
		var fresh = Films("Fresh", 10);
		var dated = Films("Dated", 10);
		var undated = Films("Undated", 10);
		var spot = Films("Spot", 6);

		var lineup = Assemble([.. next, .. fresh, .. dated, .. undated, .. spot],
		[
			new LineupCandidate(Of("next:franchise:1", SmartCollectionKind.NextIn, next), LineupSlot.NextIn),
			new LineupCandidate(Of("like:undated", SmartCollectionKind.MoreLike, undated), LineupSlot.MoreLike),
			new LineupCandidate(Of("like:fresh", SmartCollectionKind.MoreLike, fresh) with { AnchoredAt = Tuesday.AddDays(-2) }, LineupSlot.MoreLike),
			new LineupCandidate(Of("more:dated", SmartCollectionKind.MoreFrom, dated) with { AnchoredAt = Tuesday.AddDays(-12) }, LineupSlot.MoreFrom),
			new LineupCandidate(Of("spotlight:x", SmartCollectionKind.Spotlight, spot), LineupSlot.Spotlight),
		]);

		Assert.Equal(["like:fresh", "more:dated", "next:franchise:1", "like:undated"], lineup.Picks.Where(x => CollectionLineup.IsPersonal(x.Slot)).Select(x => x.Collection.Id));
		Assert.Equal(["like:fresh", "more:dated", "next:franchise:1", "like:undated", "spotlight:x"], lineup.Picks.Select(x => x.Collection.Id)); // the personal still lead
		Assert.Equal("like:fresh", lineup.ForHome(CollectionLineup.HomeDefault)[0].Id); // Home's first row
		Assert.Equal((0, 1, 2), (Hold.Recency(Tuesday.AddDays(-2), Tuesday), Hold.Recency(Tuesday.AddDays(-3), Tuesday), Hold.Recency(null, Tuesday)));
	}

	[Fact]
	public void AThemesTitles_ComeBestFirst_UnwatchedBeforeWatched()
	{
		// the keyword builder gives them A to Z: the row on Home and the tile's mosaic began with whatever sorted first
		var titles = new List<IndexEntry>
		{
			Film("Aardvark", rating: 9.5, played: true), Film("Bronze", rating: 5.0), Film("Copper", rating: 8.8), Film("Dune Sea", rating: 6.0),
			Film("Ember", rating: 7.9), Film("Flint", rating: 6.6), Film("Garnet", rating: 7.2), Film("Hollow", rating: 8.1), Film("Iron", rating: 6.9),
		};

		var theme = Assert.Single(Assemble(titles, [Of("keyword:heist", SmartCollectionKind.Keyword, titles)]).Picks).Collection;

		Assert.Equal(["Copper", "Hollow", "Ember", "Garnet", "Iron", "Flint", "Dune Sea", "Bronze", "Aardvark"], theme.ItemIds.Select(id => titles.Single(x => x.Id == id).Name));
		Assert.Equal(theme.ItemIds.Take(4), theme.Cover.Select(x => x.Id));
	}

	[Fact]
	public void ATheme_AboveAboutOnePercentOfTheFilms_IsAFilter()
	{
		// 6,000 films: the ceiling is 60 unwatched
		var rest = Films("Rest", 5_879);
		var sixtyOne = Films("Wide", 61);
		var sixty = Films("Narrow", 60, year: 1970);
		var all = rest.Concat(sixtyOne).Concat(sixty).ToList();
		Assert.Equal(6_000, all.Count);

		var lineup = Assemble(all, [Of("keyword:wide", SmartCollectionKind.Keyword, sixtyOne), Of("keyword:narrow", SmartCollectionKind.Keyword, sixty)]);

		Assert.Equal(["keyword:narrow"], lineup.Picks.Select(x => x.Collection.Id));
		// a small library: never under 30 (1 % of 300 films would leave no theme at all)
		var small = Films("Small", 270).Concat(Films("Thirty", 30)).ToList();
		Assert.Single(Assemble(small, [Of("keyword:thirty", SmartCollectionKind.Keyword, small.Skip(270))]).Picks);
	}

	[Fact]
	public void TheFirstEightPosters_NeedThreeInFourWithArtwork_ButUnfinishedSeriesNeverWaitsForIt()
	{
		var five = Films("Five", 12, posters: 5);  // 5 of the first 8
		var six = Films("Six", 12, posters: 6);    // 6 of the first 8
		var shows = Enumerable.Range(0, 2).Select(i => Film($"Show {i}", kind: IndexKind.Series, poster: false)).ToList();

		var lineup = Assemble([.. five, .. six, .. shows, .. Films("Posters", 30)], // most of the server's titles have a poster
			[Of("keyword:five", SmartCollectionKind.Keyword, five), Of("keyword:six", SmartCollectionKind.Keyword, six), Of("pick:unfinished-series", SmartCollectionKind.UnfinishedSeries, shows)]);

		Assert.Equal(["keyword:six", "pick:unfinished-series"], lineup.Picks.Select(x => x.Collection.Id).Order(StringComparer.Ordinal));
	}

	[Fact]
	public void OnAServerThatKeepsLittleArtwork_TheArtworkGateIsOff()
	{
		// the throwaway 12.4: posters on 140 of 25,736 films. Gated, nothing would ever show (rule 4: missing data is not a
		// decision); a few posters of their own don't count against a theme either
		var bare = Films("Bare", 12, posters: 0);
		var some = Films("Some", 12, posters: 2);
		var rest = Films("Rest", 100, posters: 5);
		var index = bare.Concat(some).Concat(rest).ToList(); // 7 of 124 with a poster

		var lineup = Assemble(index, [Of("keyword:bare", SmartCollectionKind.Keyword, bare), Of("keyword:some", SmartCollectionKind.Keyword, some)]);

		Assert.Equal(["keyword:bare", "keyword:some"], lineup.Picks.Select(x => x.Collection.Id).Order(StringComparer.Ordinal));
		Assert.True(LineupData.Of(index).ArtShare < CollectionLineup.ArtworkKnown);
		// the same themes where most titles have a poster: gated
		var known = index.Concat(Films("Posters", 200)).ToList();
		Assert.Empty(Assemble(known, [Of("keyword:bare", SmartCollectionKind.Keyword, bare), Of("keyword:some", SmartCollectionKind.Keyword, some)]).Picks);
	}

	[Fact]
	public void ADamagedSavedIndex_NullNamesAndGenres_StillHasALineup()
	{
		// a saved index read back with a null genre, studio, country or person's name: coherence skips them (a throw there took
		// the whole lineup, and Home's rows with it)
		List<IndexEntry> Damaged(string name) => [.. Enumerable.Range(0, 10).Select(i => Film($"{name} {i}") with
		{
			Genres = [null!, "Drama"],
			Studios = [null!],
			Countries = [null!],
			People = [new IndexPerson(null!, null, "Actor"), null!, new IndexPerson("Ana Reyes", null, "Director")],
		})];
		var a = Damaged("Damaged A");
		var b = Damaged("Damaged B");
		var c = Damaged("Damaged C");

		var lineup = Assemble([.. a, .. b, .. c], [Of("keyword:a", SmartCollectionKind.Keyword, a), Of("keyword:b", SmartCollectionKind.Keyword, b), Of("keyword:c", SmartCollectionKind.Keyword, c)]);

		Assert.Equal(2, lineup.Picks.Count); // two themes of three: the least coherent third went, nothing threw
		Assert.Equal(0, CollectionLineup.Coherence(a)); // the director all ten share is the defining one
	}

	[Fact]
	public void OfThreeThemes_TheLeastCoherentThirdGoes()
	{
		// two themes whose films share genres, a decade and a country beyond the keyword; one whose films share nothing else
		List<IndexEntry> Coherent(string name, string genre) =>
			[.. Enumerable.Range(0, 10).Select(i => Film($"{name} {i}", 1970 + (i % 2), genres: ["Crime", i % 2 == 0 ? genre : "Drama"], countries: ["France"]))];
		var heists = Coherent("Heist", "Thriller");
		var noir = Coherent("Noir", "Mystery");
		var scattered = Enumerable.Range(0, 10).Select(i => Film($"Scattered {i}", 1920 + (i * 10), genres: [$"Genre {i}"], countries: [$"Country {i}"])).ToList();

		var lineup = Assemble([.. heists, .. noir, .. scattered],
			[Of("keyword:heist", SmartCollectionKind.Keyword, heists), Of("keyword:noir", SmartCollectionKind.Keyword, noir), Of("keyword:scattered", SmartCollectionKind.Keyword, scattered)]);

		Assert.Equal(["keyword:heist", "keyword:noir"], lineup.Picks.Select(x => x.Collection.Id).Order(StringComparer.Ordinal));
		Assert.True(CollectionLineup.Coherence(heists) > CollectionLineup.Coherence(scattered));
		Assert.Equal(0, CollectionLineup.Coherence(scattered));
		Assert.Equal(new LineupSlotCount(3, 3, 3, 3, 2, 2, 2), lineup.Counts[LineupSlot.Theme]);
	}

	// ---- novelty, caps, slots ----------------------------------------------------------------------------------------------

	[Fact]
	public void TwoPoolsOverlappingMoreThanHalf_KeepTheBetterOne_WhicheverComesFirst()
	{
		// a canon list and Trending (both keep their lists' order) with 16 titles in common (Jaccard 16 / 22; their first eight
		// posters share only half): the one scored higher stays, the other goes
		var shared = Films("Shared", 16, rating: 8.0);
		var good = shared.Concat(Films("Good", 2, rating: 9.0)).ToList();
		var poor = Films("Poor", 4, rating: 3.0).Concat(shared).ToList();
		var index = shared.Concat(good.Skip(16)).Concat(poor.Take(4)).ToList();
		const string Trending = TrendingBuilder.HomeRowId;

		var canonBetter = Assemble(index, [Of(Trending, SmartCollectionKind.Trending, poor), Of("award:good", SmartCollectionKind.Award, good)]);
		var trendingBetter = Assemble(index, [Of("award:poor", SmartCollectionKind.Award, poor), Of(Trending, SmartCollectionKind.Trending, good)]);

		Assert.Equal(["award:good"], canonBetter.Picks.Select(x => x.Collection.Id));
		Assert.Equal([Trending], trendingBetter.Picks.Select(x => x.Collection.Id));
		Assert.Equal(new LineupSlotCount(1, 1, 1, 1, 1, 0, 0), canonBetter.Counts[LineupSlot.Trending]); // gone at the novelty rule
	}

	[Fact]
	public void ACollectionWhoseFirstPostersAreMostlyShownHigher_IsLeftOut_HalfIsAllowed()
	{
		// big pools (Jaccard well under 0.5), but the first eight posters shared with Trending's (a list: its own order): five of
		// eight is too many, four is allowed
		var top = Films("Top", 8, rating: 9.0);
		var five = top.Take(5).Concat(Films("FiveRest", 20, rating: 6.0)).ToList();
		var four = top.Skip(4).Take(4).Concat(Films("FourRest", 20, rating: 6.0)).ToList();
		var trending = top.Concat(Films("TrendingRest", 20, rating: 8.5)).ToList();
		var index = trending.Concat(five.Skip(5)).Concat(four.Skip(4)).ToList();

		var lineup = Assemble(index, [Of(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, trending), Of("keyword:five", SmartCollectionKind.Keyword, five)]);
		var allowed = Assemble(index, [Of(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, trending), Of("keyword:four", SmartCollectionKind.Keyword, four)]);

		Assert.Equal([TrendingBuilder.HomeRowId], lineup.Picks.Select(x => x.Collection.Id));
		Assert.Equal([TrendingBuilder.HomeRowId, "keyword:four"], allowed.Picks.Select(x => x.Collection.Id));
		// scored again for novelty: half its first posters shown, half its score (its quality counts 0.8 to 1)
		var theme = CollectionLineup.Prior(LineupSlot.Theme);
		Assert.InRange(allowed.Picks[1].Score, (theme * 0.8 * 0.5) - 0.001, (theme * 0.5) + 0.001);
	}

	[Fact]
	public void TheLineup_IsTenAtMost_EachSlotUnderItsCap_AndAcclaimedOnlyWithNoCanonList()
	{
		var index = new List<IndexEntry>();
		var collections = new List<SmartCollection>();
		List<IndexEntry> Pool(string name, int count = 10, double rating = 7.0)
		{
			var titles = Films(name, count, rating: rating);
			index.AddRange(titles);
			return titles;
		}

		for (var i = 0; i < 12; i++)
		{
			collections.Add(Of($"keyword:theme{i}", SmartCollectionKind.Keyword, Pool($"Theme {i}")));
		}

		for (var i = 0; i < 3; i++)
		{
			collections.Add(Of($"award:list{i}", SmartCollectionKind.Award, Pool($"Award {i}", 12)));
		}

		collections.Add(Of("pick:highly-rated", SmartCollectionKind.HighlyRated, Pool("Acclaimed", 20, 8.5)));
		collections.Add(Of("pick:short-films", SmartCollectionKind.ShortFilms, Pool("Short", 20)));
		collections.Add(Of("seasonal:christmas", SmartCollectionKind.Seasonal, Pool("Christmas", 12)));
		collections.Add(Of(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, Pool("Trending", 15)));
		collections.Add(Of("pick:unfinished-series", SmartCollectionKind.UnfinishedSeries, Pool("Unfinished", 3)));
		var personal = Enumerable.Range(0, 4).Select(i => new LineupCandidate(Of($"because:{i}", SmartCollectionKind.Keyword, Pool($"Because {i}")), LineupSlot.MoreLike))
			.Append(new LineupCandidate(Of("more:1", SmartCollectionKind.Keyword, Pool("More")), LineupSlot.MoreFrom))
			.Append(new LineupCandidate(Of("next:1", SmartCollectionKind.Keyword, Pool("Next", 3)), LineupSlot.NextIn));
		var candidates = CollectionLineup.Candidates(collections).Concat(personal).ToList();

		var lineup = CollectionLineup.Assemble(candidates, LineupData.Of(index), Tuesday);

		Assert.Equal(CollectionLineup.Slots, lineup.Picks.Count); // eleven could come: ten do
		Assert.All(lineup.Picks.GroupBy(x => x.Slot), g => Assert.True(g.Count() <= CollectionLineup.Cap(g.Key), $"{g.Key}: {g.Count()}"));
		Assert.Equal(2, lineup.Picks.Count(x => x.Slot == LineupSlot.MoreLike)); // of 4: the cap
		Assert.Single(lineup.Picks, x => x.Slot == LineupSlot.Canon);
		Assert.DoesNotContain(lineup.Picks, x => x.Slot == LineupSlot.Acclaimed); // a canon list is on
		Assert.Equal([LineupSlot.NextIn, LineupSlot.MoreLike, LineupSlot.MoreLike], lineup.Picks.Take(3).Select(x => x.Slot)); // the personal slots lead
		Assert.Equal(new LineupSlotCount(12, 12, 12, 12, 8, 8, lineup.Picks.Count(x => x.Slot == LineupSlot.Theme)), lineup.Counts[LineupSlot.Theme]);

		// with no canon list, Acclaimed has its place
		var noCanon = CollectionLineup.Assemble([.. candidates.Where(x => x.Slot != LineupSlot.Canon)], LineupData.Of(index), Tuesday);
		Assert.Single(noCanon.Picks, x => x.Slot == LineupSlot.Acclaimed);
	}

	// ---- the date seeds ----------------------------------------------------------------------------------------------------

	private static (List<IndexEntry> Index, List<SmartCollection> Collections) Week()
	{
		var index = new List<IndexEntry>();
		var collections = new List<SmartCollection>();
		for (var i = 0; i < 20; i++)
		{
			var titles = Films($"Week theme {i}", 10, rating: 6.5 + (i % 3 * 0.3));
			index.AddRange(titles);
			collections.Add(Of($"keyword:week{i}", SmartCollectionKind.Keyword, titles));
		}

		var acclaimed = Enumerable.Range(0, 20).Select(i => Film($"Acclaimed {i}", 1990 + i, rating: 9.0 - (i * 0.05))).ToList();
		index.AddRange(acclaimed);
		collections.Add(Of("pick:highly-rated", SmartCollectionKind.HighlyRated, acclaimed));
		return (index, collections);
	}

	private static string Shown(Lineup lineup) =>
		string.Join(" | ", lineup.Picks.Select(x => $"{x.Collection.Id}: {string.Join(",", x.Collection.ItemIds.Take(CollectionLineup.Posters))}"));

	[Fact]
	public void TheLineup_IsTheSameAllDay_AndNewTomorrow()
	{
		var (index, collections) = Week();
		var morning = new DateTimeOffset(2026, 11, 24, 0, 5, 0, Pacific);
		var night = new DateTimeOffset(2026, 11, 24, 23, 55, 0, Pacific); // already the 25th in UTC

		var early = Assemble(index, collections, morning);
		var late = Assemble(index, collections, night);
		var tomorrow = Assemble(index, collections, morning.AddDays(1));

		Assert.Equal(Shown(early), Shown(late));
		Assert.NotEqual(Shown(early), Shown(tomorrow));
		// what changed by the day: Acclaimed's first posters (its order turns daily); its best title stays near the front
		var today = early.Picks.Single(x => x.Slot == LineupSlot.Acclaimed).Collection;
		var next = tomorrow.Picks.Single(x => x.Slot == LineupSlot.Acclaimed).Collection;
		Assert.NotEqual(today.ItemIds.Take(CollectionLineup.Posters), next.ItemIds.Take(CollectionLineup.Posters));
		Assert.Equal(today.ItemIds.Order(), next.ItemIds.Order()); // the same titles, in another order
		Assert.Equal(today.ItemIds.Take(4), today.Cover.Select(x => x.Id)); // the mosaic follows the turn
	}

	[Fact]
	public void TheThemes_AreTheSameAllWeek_MondayToSunday_AndChangeTheNext()
	{
		var (index, collections) = Week();
		var monday = new DateTimeOffset(2026, 11, 23, 9, 0, 0, Pacific);
		string Themes(DateTimeOffset day) => string.Join(",", Assemble(index, collections, day).Picks.Where(x => x.Slot == LineupSlot.Theme).Select(x => x.Collection.Id).Order(StringComparer.Ordinal));

		var week = Enumerable.Range(0, 7).Select(d => Themes(monday.AddDays(d))).ToList();

		Assert.All(week, x => Assert.Equal(week[0], x));
		Assert.Equal(2, week[0].Split(',').Length);
		Assert.NotEqual(week[0], Themes(monday.AddDays(7)));
		Assert.Equal("w2026-48", CollectionLineup.Seed(LineupRefresh.Weekly, monday));
		Assert.Equal("w2026-48", CollectionLineup.Seed(LineupRefresh.Weekly, monday.AddDays(6).AddHours(14))); // Sunday 23:00
		Assert.Equal("m2026-11", CollectionLineup.Seed(LineupRefresh.Monthly, monday));
		Assert.Equal("d2026-11-23", CollectionLineup.Seed(LineupRefresh.Daily, monday));
	}

	[Fact]
	public void TheDraw_IsTheSameOnEveryRun_AndSpread()
	{
		// FNV-1a over UTF-8: not the runtime's string hash (another each process), so a restart keeps the day's lineup
		Assert.Equal(CollectionLineup.Draw("d2026-11-24", "keyword:heist"), CollectionLineup.Draw("d2026-11-24", "keyword:heist"));
		var draws = Enumerable.Range(0, 1000).Select(i => CollectionLineup.Draw("w2026-48", $"keyword:{i}")).ToList();
		Assert.All(draws, x => Assert.InRange(x, 0, 0.9999999));
		Assert.InRange(draws.Average(), 0.45, 0.55);
		Assert.InRange(draws.Count(x => x < 0.1), 70, 130);
	}

	// ---- Home --------------------------------------------------------------------------------------------------------------

	private static LineupPick Pick(LineupSlot slot, string id, SmartCollectionKind kind = SmartCollectionKind.Keyword, double score = 0.5) =>
		new(new SmartCollection(id, kind, id, "", false, [Guid.NewGuid()]), slot, score);

	[Fact]
	public void Home_TakesThePersonalSlot_ThenSeasonalOrTrendingOrSpotlight_ThenTheLineupsNext_TwoByDefault()
	{
		var lineup = new Lineup(
		[
			Pick(LineupSlot.Spotlight, "spotlight:1"), Pick(LineupSlot.MoreFrom, "more:1"), Pick(LineupSlot.Canon, "award:1", SmartCollectionKind.Award),
			Pick(LineupSlot.Trending, "trending:week", SmartCollectionKind.Trending), Pick(LineupSlot.Unfinished, "pick:unfinished-series", SmartCollectionKind.UnfinishedSeries),
			Pick(LineupSlot.Theme, "keyword:a"), Pick(LineupSlot.Seasonal, "seasonal:christmas", SmartCollectionKind.Seasonal),
		], new Dictionary<LineupSlot, LineupSlotCount>(), Tuesday);

		Assert.Equal(["more:1", "seasonal:christmas"], lineup.ForHome(CollectionLineup.HomeDefault).Select(x => x.Id));
		Assert.Equal(["more:1", "seasonal:christmas", "spotlight:1"], lineup.ForHome(3).Select(x => x.Id));
		Assert.Equal(3, lineup.ForHome(9).Count); // never more than three
		Assert.Empty(lineup.ForHome(0));
		// out of season: Trending; no list: Spotlight; never Unfinished series (Home's own row)
		Assert.Equal(["more:1", "trending:week"], lineup.ForHome(2, x => x.Kind != SmartCollectionKind.Seasonal).Select(x => x.Id));
		Assert.Equal(["more:1", "spotlight:1"], lineup.ForHome(2, x => x.Kind is not (SmartCollectionKind.Seasonal or SmartCollectionKind.Trending)).Select(x => x.Id));
		Assert.DoesNotContain(lineup.ForHome(3, x => x.Kind is not (SmartCollectionKind.Seasonal or SmartCollectionKind.Trending)), x => x.Kind == SmartCollectionKind.UnfinishedSeries);
		// nothing personal: the timely one, then the lineup's next
		var guest = new Lineup([.. lineup.Picks.Where(x => !CollectionLineup.IsPersonal(x.Slot))], lineup.Counts, Tuesday);
		Assert.Equal(["seasonal:christmas", "spotlight:1"], guest.ForHome(2).Select(x => x.Id));
	}

	[Fact]
	public void HomesRows_AreTheLineupsThenUnfinishedSeries_UnderTheSwitches()
	{
		var unfinished = new SmartCollection("pick:unfinished-series", SmartCollectionKind.UnfinishedSeries, "Unfinished series", "", false, [Guid.NewGuid()]);
		var lineup = new Lineup(
		[
			Pick(LineupSlot.Theme, "keyword:a"), Pick(LineupSlot.Acclaimed, "pick:highly-rated", SmartCollectionKind.HighlyRated),
			Pick(LineupSlot.UnderNinety, "pick:short-films", SmartCollectionKind.ShortFilms), new LineupPick(unfinished, LineupSlot.Unfinished, 0.7),
		], new Dictionary<LineupSlot, LineupSlotCount>(), Tuesday);
		var none = new HashSet<NixieHomeRow>();
		var noKinds = new HashSet<SmartCollectionKind>();

		Assert.Equal(["keyword:a", "pick:highly-rated", "pick:unfinished-series"], HomeViewModel.PickNixieRows(lineup, [unfinished], none, noKinds, 2).Select(x => x.Id));
		Assert.Equal(["keyword:a", "pick:highly-rated", "pick:short-films", "pick:unfinished-series"], HomeViewModel.PickNixieRows(lineup, [unfinished], none, noKinds, 3).Select(x => x.Id));
		// Acclaimed's row off: the next one takes its place; Unfinished series off: no row of it
		Assert.Equal(["keyword:a", "pick:short-films"],
			HomeViewModel.PickNixieRows(lineup, [unfinished], new HashSet<NixieHomeRow> { NixieHomeRow.HighlyRated, NixieHomeRow.UnfinishedSeries }, noKinds, 2).Select(x => x.Id));
		// "This week's picks" off: only Acclaimed, the seasonal row, Trending and Unfinished series of the lineup's
		Assert.Equal(["pick:highly-rated", "pick:unfinished-series"], HomeViewModel.PickNixieRows(lineup, [unfinished], new HashSet<NixieHomeRow> { NixieHomeRow.Picks }, noKinds, 2).Select(x => x.Id));
		Assert.Equal(["pick:highly-rated", "pick:short-films", "pick:unfinished-series"],
			HomeViewModel.PickNixieRows(lineup, [unfinished], none, new HashSet<SmartCollectionKind> { SmartCollectionKind.Keyword }, 2).Select(x => x.Id));
		// the trending row follows its sources' switches only, as when Home added it apart from the others
		var trending = new SmartCollection(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, TrendingBuilder.HomeRowTitle, "", true, [Guid.NewGuid()]);
		Assert.True(HomeViewModel.NixieRowShown(trending, new HashSet<NixieHomeRow> { NixieHomeRow.Picks }, noKinds));
	}

	[Fact]
	public void A17UsersADayOff_HidesNoneOfThe18Rows_ThisWeeksPicksIsItsOwnSwitch()
	{
		// review (Minor 4): a 1.7 user who turned off "A collection a day" (stored "Rotating") turned off one row a day; 1.8's
		// personal, theme, canon and Spotlight rows are "This week's picks" (stored "Picks"), on until that is turned off
		var settings = new Settings17();
		settings.SaveSetting(CollectionSettingKeys.HomeRowsOff.Name, "Rotating");
		var rowsOff = CollectionSettings.HomeRowsOff(settings);
		var noKinds = new HashSet<SmartCollectionKind>();
		foreach (var kind in new[] { SmartCollectionKind.NextIn, SmartCollectionKind.MoreLike, SmartCollectionKind.MoreFrom, SmartCollectionKind.Keyword, SmartCollectionKind.Award, SmartCollectionKind.Spotlight })
		{
			var row = new SmartCollection($"x:{kind}", kind, "x", "", false, [Guid.NewGuid()]);
			Assert.True(HomeViewModel.NixieRowShown(row, rowsOff, noKinds), $"{kind} with 1.7's day row off");
			Assert.False(HomeViewModel.NixieRowShown(row, new HashSet<NixieHomeRow> { NixieHomeRow.Picks }, noKinds), $"{kind} with This week's picks off");
		}

		// and Settings' switch is the new one: turned off, it stores "Picks"
		var switches = new CollectionsSettingsViewModel(settings);
		var picks = switches.HomeRows.Single(x => x.Name == CollectionsSettingsViewModel.PicksName);
		Assert.True(picks.IsOn);
		picks.IsOn = false;
		Assert.Contains(NixieHomeRow.Picks, CollectionSettings.HomeRowsOff(settings));
	}

	// ---- the review of b2981456 (Minors 1 to 4) -------------------------------------------------------------------------

	[Fact]
	public void ACanonListThatLosesAtNovelty_LeavesAcclaimedIn_OneThatIsTakenTakesItsPlace()
	{
		// Minor 1: Acclaimed only when no canon list is taken. A seasonal row of the same twelve titles takes the award list's
		// place (it comes first): Acclaimed stays; with nothing to beat it, the award list is taken and Acclaimed goes
		var shared = Films("Shared", 12, rating: 8.0);
		var acclaimed = Films("Acclaimed", 20, rating: 8.5);
		var lost = Assemble([.. shared, .. acclaimed], [Of("seasonal:christmas", SmartCollectionKind.Seasonal, shared), Of("award:x", SmartCollectionKind.Award, shared),
			Of("pick:highly-rated", SmartCollectionKind.HighlyRated, acclaimed)]);
		Assert.Contains(lost.Picks, x => x.Slot == LineupSlot.Seasonal);
		Assert.DoesNotContain(lost.Picks, x => x.Slot == LineupSlot.Canon);
		Assert.Contains(lost.Picks, x => x.Slot == LineupSlot.Acclaimed);

		var listed = Films("Listed", 12, year: 1960, rating: 8.0);
		var taken = Assemble([.. listed, .. acclaimed], [Of("award:y", SmartCollectionKind.Award, listed), Of("pick:highly-rated", SmartCollectionKind.HighlyRated, acclaimed)]);
		Assert.Contains(taken.Picks, x => x.Slot == LineupSlot.Canon);
		Assert.DoesNotContain(taken.Picks, x => x.Slot == LineupSlot.Acclaimed);
		Assert.Equal(0, taken.Counts[LineupSlot.Acclaimed].Novelty); // said in the log as gone at novelty, not picked
	}

	[Fact]
	public void TheArtworkGate_IsForRowsOfEight_ASmallNextInWithoutItsNextPosterStays()
	{
		// Minor 2: a started franchise of two (part 1 watched, part 2 without its poster yet) on a server with posters
		var rest = Films("Rest", 100);
		var part1 = Film("Part 1", played: true);
		var part2 = Film("Part 2", poster: false);
		var small = Assemble([.. rest, part1, part2], [new LineupCandidate(Of("next:franchise:x", SmartCollectionKind.NextIn, [part1, part2]), LineupSlot.NextIn)]);
		Assert.Single(small.Picks);

		// a row of ten with four posters still goes (the gate is unchanged for a row)
		var bare = Films("Bare", 10, posters: 4);
		var row = Assemble([.. rest, .. bare], [new LineupCandidate(Of("pick:short-films", SmartCollectionKind.ShortFilms, bare), LineupSlot.UnderNinety)]);
		Assert.Empty(row.Picks);
		Assert.Equal(0, row.Counts[LineupSlot.UnderNinety].Artwork);
	}

	public static TheoryData<LineupSlot> EverySlot => [.. Enum.GetValues<LineupSlot>()];

	[Theory]
	[MemberData(nameof(EverySlot))]
	public void EachSlot_TakesItsCap_NoMore(LineupSlot slot)
	{
		// Minor 3: three candidates of one slot, nothing alike (disjoint titles, different decades): the lineup takes as many as
		// the slot's cap (two "More like", themes and seasonal rows; one of every other), never more. Two started franchises,
		// two people, two Spotlights: one each
		var sets = Enumerable.Range(0, 3).Select(i => Films($"{slot} {i}", 12, year: 1940 + (i * 30))).ToList();
		var lineup = Assemble(sets.SelectMany(x => x), sets.Select((titles, i) => new LineupCandidate(Of($"{slot}:{i}", SmartCollectionKind.Keyword, titles), slot)));

		Assert.Equal(Math.Min(3, CollectionLineup.Cap(slot)), lineup.Picks.Count(x => x.Slot == slot));
		Assert.Equal(slot is LineupSlot.MoreLike or LineupSlot.Theme or LineupSlot.Seasonal or LineupSlot.Category ? 2 : 1, CollectionLineup.Cap(slot)); // the report's caps; 1.9: up to 2 categories
	}

	[Fact]
	public void HomesLineup_IsOfTheRowsHomeShows_ASwitchedOnKindIsntCrowdedOutOfTheTenSlots()
	{
		// Minor 4: ten picks before Acclaimed that Home's switches hide ("This week's picks" and "In season" off, Unfinished
		// series Home's own row), then Acclaimed, its row on. Of the whole lineup Home had none; of its own, Acclaimed
		var candidates = new List<LineupCandidate>();
		var index = new List<IndexEntry>();
		var year = 1900;
		foreach (var (slot, kind, count) in new[]
		{
			(LineupSlot.Theme, SmartCollectionKind.Keyword, 2), (LineupSlot.MoreLike, SmartCollectionKind.MoreLike, 2), (LineupSlot.NextIn, SmartCollectionKind.NextIn, 1),
			(LineupSlot.MoreFrom, SmartCollectionKind.MoreFrom, 1), (LineupSlot.Spotlight, SmartCollectionKind.Spotlight, 1), (LineupSlot.Seasonal, SmartCollectionKind.Seasonal, 2),
			(LineupSlot.Unfinished, SmartCollectionKind.UnfinishedSeries, 1),
		})
		{
			for (var i = 0; i < count; i++)
			{
				var titles = Films($"{slot} {i}", 12, year: year += 5);
				index.AddRange(titles);
				candidates.Add(new LineupCandidate(Of($"{kind}:{i}", kind, titles), slot));
			}
		}

		var acclaimed = Films("Acclaimed", 12, year: 2010);
		index.AddRange(acclaimed);
		candidates.Add(new LineupCandidate(Of("pick:highly-rated", SmartCollectionKind.HighlyRated, acclaimed), LineupSlot.Acclaimed));
		var rowsOff = new HashSet<NixieHomeRow> { NixieHomeRow.Picks, NixieHomeRow.Seasonal };
		var noKinds = new HashSet<SmartCollectionKind>();
		bool Shown(SmartCollection x) => HomeViewModel.NixieRowShown(x, rowsOff, noKinds);
		var data = LineupData.Of(index);

		var all = CollectionLineup.Assemble(candidates, data, Tuesday);
		Assert.Equal(CollectionLineup.Slots, all.Picks.Count);
		Assert.DoesNotContain(all.Picks, x => x.Slot == LineupSlot.Acclaimed); // the ten slots were full before it
		Assert.Empty(all.ForHome(2, Shown));
		Assert.Equal(["pick:highly-rated"], CollectionLineup.HomeLineup(candidates, data, Tuesday, Shown).ForHome(2, Shown).Select(x => x.Id));
		// and Home's rows are picked from Home's lineup
		var home = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin.Core", "ViewModels", "HomeViewModel.Nixie.cs"));
		Assert.Contains("PickNixieRows(made.Home,", home, StringComparison.Ordinal);
		Assert.Contains("bool Shown(SmartCollection x) => NixieRowShown(x, rowsOff, kindsOff);", home, StringComparison.Ordinal);
		Assert.Contains("CollectionLineup.BuildAsync(snapshot, now, boxed, HomeBuildOff(kindsOff), extra, homeShown: Shown)", home, StringComparison.Ordinal); // 1.9 (wire, review Minor 3): no category
		Assert.Contains("CollectionLineup.AssembleAsync(snapshot, made.All, [.. extra, .. spotlight], now, shown, fallback: made)", home, StringComparison.Ordinal); // the Spotlight's too
	}

	private sealed class Settings17 : Contracts.Services.ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}

	// ---- stream a's personal slots, as the catalog makes them ------------------------------------------------------------

	// a's fixture library (AnchorsTests): a director's films, a franchise, anime and other shows, dramas enough that a genre
	// says little; with Arrival, Mission: Impossible and Cowboy Bebop finished when finished is true
	private static IndexSnapshot AnchoredLibrary(bool finished)
	{
		var f = AnchorsTests.Film;
		var s = AnchorsTests.Series;
		var arrival = f("Arrival", 2016, ["Science Fiction", "Drama"], ["alien"], "Denis Villeneuve", ["Amy Adams", "Jeremy Renner", "Forest Whitaker"], null, finished, false, 7.9, null, null, null);
		var mi1 = f("Mission: Impossible", 1996, ["Action", "Thriller"], ["spy"], "Brian De Palma", ["Tom Cruise", "Jon Voight", "Emmanuelle Béart"], "87359", finished, false, 7.0, null, null, "1996-05-22");
		var bebop = s("Cowboy Bebop", 1998, ["Animation", "Action", "Science Fiction"], ["anime", "space"], ["Koichi Yamadera", "Unsho Ishizuka", "Megumi Hayashibara"], 2, false, false, ["Japan"], 8.0);
		if (finished)
		{
			arrival = arrival with { PlayCount = 1, LastPlayed = Tuesday.AddDays(-20) };
			mi1 = mi1 with { PlayCount = 1, LastPlayed = Tuesday.AddDays(-10) };
		}

		IndexEntry[] titles =
		[
			arrival, mi1, bebop,
			f("Sicario", 2015, ["Crime", "Thriller"], ["revenge"], "Denis Villeneuve", ["Emily Blunt", "Benicio del Toro", "Josh Brolin"], null, false, false, 7.6, null, null, null),
			f("Prisoners", 2013, ["Crime", "Thriller", "Drama"], ["detective"], "Denis Villeneuve", ["Hugh Jackman", "Jake Gyllenhaal", "Viola Davis"], null, false, false, 8.1, null, null, null),
			f("Enemy", 2013, ["Mystery", "Thriller"], [], "Denis Villeneuve", ["Jake Gyllenhaal", "Mélanie Laurent", "Sarah Gadon"], null, false, false, 6.9, null, null, null),
			f("Incendies", 2010, ["Drama", "Mystery", "War"], [], "Denis Villeneuve", ["Lubna Azabal", "Mélissa Désormeaux-Poulin", "Maxim Gaudette"], null, false, false, 8.3, ["Canada"], null, null),
			f("Blade Runner 2049", 2017, ["Science Fiction", "Drama"], ["cyberpunk", "dystopia"], "Denis Villeneuve", ["Ryan Gosling", "Harrison Ford", "Ana de Armas"], null, false, false, 8.0, null, null, null),
			f("Dune", 2021, ["Science Fiction", "Adventure"], ["space"], "Denis Villeneuve", ["Timothée Chalamet", "Rebecca Ferguson", "Oscar Isaac"], null, false, false, 7.8, null, null, null),
			f("Mission: Impossible II", 2000, ["Action", "Thriller"], ["spy"], "John Woo", ["Tom Cruise", "Dougray Scott", "Thandiwe Newton"], "87359", false, false, 6.1, null, null, "2000-05-24"),
			f("Mission: Impossible III", 2006, ["Action", "Thriller"], ["spy"], "J.J. Abrams", ["Tom Cruise", "Philip Seymour Hoffman", "Ving Rhames"], "87359", false, false, 6.9, null, null, "2006-05-03"),
			f("Mission: Impossible - Ghost Protocol", 2011, ["Action", "Thriller"], ["spy"], "Brad Bird", ["Tom Cruise", "Jeremy Renner", "Simon Pegg"], "87359", false, false, 7.4, null, null, "2011-12-07"),
			s("Trigun", 1998, ["Animation", "Action", "Science Fiction"], ["anime", "western"], ["Masaya Onosaka", "Hiromi Tsuru", "Satsuki Yukino"], 10, false, false, ["Japan"], 8.0),
			s("Space Dandy", 2014, ["Animation", "Comedy", "Science Fiction"], ["anime", "space"], ["Ryohei Kimura", "Junichi Suwabe", "Megumi Hayashibara"], 10, false, false, ["Japan"], 8.0),
			s("Planetes", 2003, ["Animation", "Drama", "Science Fiction"], ["anime", "space"], ["Hochu Otsuka", "Ami Koshimizu", "Satsuki Yukino"], 10, false, false, ["Japan"], 8.0),
			s("Samurai Champloo", 2004, ["Animation", "Action"], ["anime", "samurai"], ["Kazuya Nakai", "Ayako Kawasumi", "Tatsuya Sato"], 10, false, false, ["Japan"], 8.0),
			s("Outlaw Star", 1998, ["Animation", "Action", "Science Fiction"], ["anime", "space"], ["Shigeru Shibuya", "Rica Fukami", "Yuko Nagashima"], 10, false, false, ["Japan"], 8.0),
			s("Ghost in the Shell: Stand Alone Complex", 2002, ["Animation", "Science Fiction", "Crime"], ["anime", "cyberpunk"], ["Atsuko Tanaka", "Akio Otsuka", "Koichi Yamadera"], 10, false, false, ["Japan"], 8.0),
			s("Psycho-Pass", 2012, ["Animation", "Crime", "Science Fiction"], ["anime", "dystopia"], ["Kana Hanazawa", "Tomokazu Seki", "Takahiro Sakurai"], 10, false, false, ["Japan"], 8.0),
			s("Steins;Gate", 2011, ["Animation", "Science Fiction", "Drama"], ["anime", "time travel"], ["Mamoru Miyano", "Asami Imai", "Kana Hanazawa"], 10, false, false, ["Japan"], 8.0),
			s("Fullmetal Alchemist: Brotherhood", 2009, ["Animation", "Adventure", "Drama"], ["anime", "magic"], ["Romi Park", "Rie Kugimiya", "Shinichiro Miki"], 10, false, false, ["Japan"], 8.0),
			s("Neon Genesis Evangelion", 1995, ["Animation", "Drama", "Science Fiction"], ["anime", "robot"], ["Megumi Ogata", "Megumi Hayashibara", "Yuko Miyamura"], 10, false, false, ["Japan"], 8.0),
			s("The Wire", 2002, ["Crime", "Drama"], ["detective"], ["Dominic West", "Lance Reddick", "Sonja Sohn"], 10, false, false, null, 8.0),
			s("Breaking Bad", 2008, ["Crime", "Drama"], ["dark comedy"], ["Bryan Cranston", "Aaron Paul", "Anna Gunn"], 10, false, false, null, 8.0),
			s("The Expanse", 2015, ["Science Fiction", "Drama"], ["space"], ["Steven Strait", "Dominique Tipper", "Wes Chatham"], 10, false, false, null, 8.0),
			s("Firefly", 2002, ["Science Fiction", "Western"], ["space", "western"], ["Nathan Fillion", "Gina Torres", "Alan Tudyk"], 10, false, false, null, 8.0),
			s("Fargo", 2014, ["Crime", "Drama"], ["dark comedy"], ["Billy Bob Thornton", "Allison Tolman", "Colin Hanks"], 10, false, false, null, 8.0),
			s("Mad Men", 2007, ["Drama"], [], ["Jon Hamm", "Elisabeth Moss", "Vincent Kartheiser"], 10, false, false, null, 8.0),
			.. Enumerable.Range(1, 40).Select(i => f($"A Quiet Drama {i}", 1990 + (i % 30), ["Drama"], [], $"Director {i}", [$"Lead {i}", $"Second {i}", $"Third {i}"], null, false, false, 7.0, null, null, null)),
		];
		// Cowboy Bebop: 24 of its 26 episodes played, yesterday (a show is finished at 90 %)
		var plays = finished ? new Dictionary<Guid, SeriesPlays> { [bebop.Id] = new SeriesPlays(24, Tuesday.AddDays(-1)) } : [];
		return new IndexSnapshot([], titles, plays, HasPeople: true, Tuesday);
	}

	[Fact]
	public void WithTitlesFinished_ThePersonalSlotsLeadTheLineup_AndHome_WithNoneFinished_NoneShows()
	{
		var finished = CollectionLineup.Build(AnchoredLibrary(finished: true), Tuesday, null, null, [], OutsideListsSnapshot.Empty);
		var none = CollectionLineup.Build(AnchoredLibrary(finished: false), Tuesday, null, null, [], OutsideListsSnapshot.Empty);

		foreach (var p in finished.Lineup.Picks)
		{
			output.WriteLine($"{p.Slot} {p.Score}: {p.Collection.Title} | {p.Collection.ReasonText}");
		}

		// the three anchored kinds, each once, ahead of everything else
		Assert.Equal([LineupSlot.NextIn, LineupSlot.MoreLike, LineupSlot.MoreFrom], finished.Lineup.Picks.Take(3).Select(x => x.Slot).Order());
		Assert.Contains(finished.Lineup.Picks, x => x.Collection.Title == "Next in Mission: Impossible" && x.Collection.ReasonText == "Next: Mission: Impossible II (2000) · 1 of 4 films watched");
		Assert.Contains(finished.Lineup.Picks, x => x.Collection.Title == "More from Denis Villeneuve" && x.Collection.ReasonText == "You finished Arrival · 6 more films, none watched");
		Assert.Contains(finished.Lineup.Picks, x => x.Collection.Title == "More like Cowboy Bebop");
		// Home: the best personal slot first; nothing in season, trending or a Spotlight here, so the lineup's next: personal too
		var home = finished.Lineup.ForHome(CollectionLineup.HomeDefault);
		Assert.Equal(finished.Lineup.Picks.Take(2).Select(x => x.Collection.Id), home.Select(x => x.Id));
		Assert.All(home, x => Assert.Contains(x.Kind, new[] { SmartCollectionKind.NextIn, SmartCollectionKind.MoreLike, SmartCollectionKind.MoreFrom }));

		Assert.DoesNotContain(none.Lineup.Picks, x => CollectionLineup.IsPersonal(x.Slot));
		Assert.DoesNotContain(none.Lineup.ForHome(CollectionLineup.HomeMost), x => x.Kind is SmartCollectionKind.NextIn or SmartCollectionKind.MoreLike or SmartCollectionKind.MoreFrom);
	}

	// ---- real shape --------------------------------------------------------------------------------------------------------

	private static readonly string[] Genres = ["Drama", "Comedy", "Thriller", "Horror", "Action", "Romance"];
	private static readonly string[] Moods = ["Crime", "Mystery", "Family"];
	private static readonly string[] Countries = ["United States of America", "France", "Japan", "United Kingdom"];

	[Fact]
	public void A25800FilmIndex_ItsLineupIsMadeInAMoment_AndHomeWaitsNoLongerThanForItsCollections()
	{
		// the owner-sized library: copies in two libraries, allowlisted keywords, ratings, played titles and posters missing
		var random = new Random(180);
		var keywords = Keywords.Allowed.Keys.Take(40).ToArray();
		var library = Guid.NewGuid();
		var films = Enumerable.Range(0, 25_800).Select(i => new IndexEntry
		{
			Id = Guid.NewGuid(),
			Name = $"Film {i}",
			Kind = IndexKind.Movie,
			LibraryId = library,
			Year = 1930 + random.Next(95),
			Tmdb = (i % 23 == 0 ? i - 1 : i).ToString(System.Globalization.CultureInfo.InvariantCulture), // one in 23 a copy of the one before
			Genres = [Genres[random.Next(Genres.Length)], Moods[random.Next(Moods.Length)]],
			Tags = random.Next(4) == 0 ? [keywords[random.Next(keywords.Length)]] : [],
			TagCount = random.Next(20),
			Countries = [Countries[random.Next(Countries.Length)]],
			Rating = 4 + (random.NextDouble() * 5),
			Critic = random.Next(3) == 0 ? random.Next(30, 100) : null,
			RunTimeTicks = TimeSpan.FromMinutes(60 + random.Next(90)).Ticks,
			Played = random.Next(20) == 0,
			PrimaryTag = random.Next(30) == 0 ? null : "p",
		}).ToList();
		var index = new IndexSnapshot([], films, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Tuesday);
		// 1.9 (wire's review): the lineup shares the catalog's collapse of the index, so the first lineup build is as warm as the
		// second: the titles kept is counted, not timed (the second was the faster only while the first collapsed on its own)
		var collapses = 0;
		void Count(IReadOnlyList<IndexEntry> entries)
		{
			if (ReferenceEquals(entries, index.Entries))
			{
				Interlocked.Increment(ref collapses);
			}
		}

		BuildContext.Collapsing += Count;

		var plain = Stopwatch.StartNew();
		var collections = SmartCollectionCatalog.BuildAll(index, Tuesday, outside: OutsideListsSnapshot.Empty);
		plain.Stop();
		var built = Stopwatch.StartNew();
		var made = CollectionLineup.Build(index, Tuesday, null, null, [], OutsideListsSnapshot.Empty);
		built.Stop();
		var again = Stopwatch.StartNew();
		var second = CollectionLineup.Build(index, Tuesday, null, null, [], OutsideListsSnapshot.Empty); // the titles kept
		again.Stop();
		BuildContext.Collapsing -= Count;

		output.WriteLine($"collections alone {plain.ElapsedMilliseconds} ms; collections and lineup {built.ElapsedMilliseconds} ms; again (titles kept) {again.ElapsedMilliseconds} ms");
		output.WriteLine($"{made.All.Count} collections; lineup: {string.Join(", ", made.Lineup.Picks.Select(x => x.Slot))}");
		Assert.Equal(collections.Count, made.All.Count);
		Assert.NotEmpty(made.Lineup.Picks);
		Assert.InRange(made.Lineup.Picks.Count, 1, CollectionLineup.Slots);
		Assert.Equal(Shown(made.Lineup), Shown(second.Lineup));
		Assert.True(built.Elapsed < TimeSpan.FromSeconds(6), $"collections and lineup in {built.ElapsedMilliseconds} ms");
		Assert.Equal(1, collapses); // the titles kept: collapsed once for the collections and both lineups
		var said = MadeByNixieLog.Instance.Entries.Where(x => x.Message.StartsWith("Made by Nixie lineup: ", StringComparison.Ordinal)).ToList();
		Assert.NotEmpty(said);
		Assert.All(said, x => Assert.DoesNotContain("Film", x.Message, StringComparison.Ordinal)); // counts and slots, never a title
		output.WriteLine(said[^1].Message);
	}

	[Fact]
	public void TheThrowaway124sShape_25736FilmsWithNoTagsAndPostersOn140_StillHasALineup_ColdInAMoment()
	{
		// the throwaway real Jellyfin's library as its notes give it: 25,736 synthetic films, an NFO each (title, year, rating,
		// runtime, genres, MPAA rating), no tags, no critics' ratings, posters on the first 140 by title, about 8 % in one of
		// 600 sets. A cold start: the index's titles collapsed, the collections made and the lineup assembled, from nothing kept
		var random = new Random(124);
		var library = Guid.NewGuid();
		var films = Enumerable.Range(0, 25_736).Select(i => new IndexEntry
		{
			Id = Guid.NewGuid(),
			Name = $"Throwaway {i:D5}",
			Kind = IndexKind.Movie,
			LibraryId = library,
			Year = 1920 + random.Next(106),
			TmdbCollection = random.Next(100) < 8 ? random.Next(600).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
			Genres = [Genres[random.Next(Genres.Length)]],
			Rating = Math.Round(3 + (random.NextDouble() * 6.5), 1),
			RunTimeTicks = TimeSpan.FromMinutes(70 + random.Next(80)).Ticks,
			PrimaryTag = i < 140 ? "p" : null,
		}).ToList();
		var index = new IndexSnapshot([], films, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Tuesday);

		var cold = Stopwatch.StartNew();
		var made = CollectionLineup.Build(index, Tuesday, null, null, [], OutsideListsSnapshot.Empty);
		cold.Stop();

		output.WriteLine($"cold: {cold.ElapsedMilliseconds} ms; {made.All.Count} collections; lineup: {string.Join(", ", made.Lineup.Picks.Select(x => x.Slot))}");
		Assert.Equal([LineupSlot.UnderNinety, LineupSlot.Acclaimed], made.Lineup.Picks.Select(x => x.Slot).Order()); // no tags: no theme
		Assert.True(cold.Elapsed < TimeSpan.FromSeconds(6), $"{cold.ElapsedMilliseconds} ms");
		var said = MadeByNixieLog.Instance.Entries.Last(x => x.Message.StartsWith("Made by Nixie lineup: ", StringComparison.Ordinal) && x.Message.Contains("artwork gate off", StringComparison.Ordinal));
		output.WriteLine(said.Message);
	}
}

/// <summary>
/// Nixie 1.8 (P1-1): Home's Made by Nixie rows from the lineup, as the user sees them: their titles, at most two by default and
/// three with the setting, a page saved by 1.7 painted no longer than that, and how long they took said in the log.
/// </summary>
[Collection(CollectionNamesSerial.Name)]
public sealed class HomeLineupTests : IDisposable
{
	private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-lineup-" + Guid.NewGuid().ToString("N"));
	private readonly System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage> _messages = new();
	private readonly Services.HomeCache _homeCache;
	private readonly Moq.Mock<Contracts.Services.IJellyfinClient> _client = new();
	private readonly List<Jellyfin.Sdk.Generated.Models.BaseItemDto> _films = [];
	private readonly LineupSettings _settings = new();
	private readonly ManualClock _clock = new(); // Home's and the Collections page's: a test moves it a day

	static HomeLineupTests() => ReactiveUI.Builder.RxAppBuilder.CreateReactiveUIBuilder().BuildApp();

	public HomeLineupTests()
	{
		_homeCache = new Services.HomeCache(Path.Combine(_folder, "HomeCache"), _messages, new CoreListLogger<Services.HomeCache>(), TimeProvider.System);
		_client.SetupGet(x => x.BaseUrl).Returns("http://server");
		_client.SetupGet(x => x.UserId).Returns(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000180"));
		// a library at real shape for four of the lineup's slots: two themes, Acclaimed, Under 90 minutes; ten titles each
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Lineup heist {i}", 1970 + i, 7.0, 120, "heist", ["Crime", "Thriller"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Lineup loop {i}", 1990 + i, 7.2, 115, "time travel", ["Science Fiction"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Lineup acclaimed {i}", 1950 + i, 8.0 + (i * 0.09), 130, null, ["Drama"])));
		_films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Lineup short {i}", 2005 + i, 6.8, 80, null, ["Comedy"])));
		var movies = new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = Guid.NewGuid(), Name = "Movies", Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.CollectionFolder, CollectionType = Jellyfin.Sdk.Generated.Models.BaseItemDto_CollectionType.Movies };
		_client.Setup(x => x.GetUserViewsOrNull()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetUserViews()).ReturnsAsync([movies]);
		_client.Setup(x => x.GetLibraryIndexPage(Moq.It.IsAny<Jellyfin.Sdk.Generated.Models.BaseItemDto>(), Moq.It.IsAny<int>(), Moq.It.IsAny<int>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()))
			.ReturnsAsync((Jellyfin.Sdk.Generated.Models.BaseItemDto _, int start, int limit, bool _, CancellationToken _) => new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count });
		_client.Setup(x => x.GetItemsByIds(Moq.It.IsAny<IReadOnlyList<Guid>>(), Moq.It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<Guid> ids, CancellationToken _) => [.. ids.Select(id => _films.First(f => f.Id == id))]);
		_client.Setup(x => x.GetBoxSets(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetContinueWatching()).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetNextUp()).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		_client.Setup(x => x.GetLatestItems(Moq.It.IsAny<Jellyfin.Sdk.Generated.Models.BaseItemDto>())).ReturnsAsync([_films[0]]);
	}

	public void Dispose()
	{
		SmartCollectionServices.Index = null;
		_homeCache.Dispose();
		TestFolder.Delete(_folder);
	}

	private static Jellyfin.Sdk.Generated.Models.BaseItemDto Film(string name, int year, double rating, int minutes, string? tag, List<string> genres) => new()
	{
		Id = Guid.NewGuid(),
		Name = name,
		Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie,
		ProductionYear = year,
		CommunityRating = (float)rating,
		CriticRating = 80,
		RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
		Tags = tag is null ? [] : [tag],
		Genres = genres,
		ImageTags = new Jellyfin.Sdk.Generated.Models.BaseItemDto_ImageTags { AdditionalData = new Dictionary<string, object> { ["Primary"] = "p" + name.Length } },
		UserData = new Jellyfin.Sdk.Generated.Models.UserItemDataDto { Played = false },
	};

	private async Task<LibraryIndex> Index()
	{
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		await index.GetAsync();
		SmartCollectionServices.Index = index;
		return index;
	}

	private HomeViewModel Home(Services.HomeCache? cache = null) =>
		new(_client.Object, _messages, new GlobalCommands(Moq.Mock.Of<Services.INavigationServiceCore>(), _client.Object, Moq.Mock.Of<Services.INavigationViewServiceCore>(), Moq.Mock.Of<Settings.ISettings>()),
			cache ?? _homeCache, _settings, _clock);

	[Fact]
	public async Task Home_ShowsTwoOfTheLineupsRows_ByDefault_ThreeWithTheSetting_NeverTheFourth()
	{
		var index = await Index();
		var lineup = CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(new ManualClock()), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty).Lineup;
		Assert.Equal(4, lineup.Picks.Count);
		var home = Home();

		await home.OnNavigatedTo(new object());

		// what the user reads over each row, in the lineup's order; the fourth is This week's only
		var expected = lineup.Picks.Select(x => new NixieHomeRowViewModel(x.Collection).Title).ToList();
		Assert.Equal(expected.Take(2), home.NixieRows.Select(r => r.Title));
		Assert.Contains("Acclaimed, not seen yet", expected);
		Assert.Contains("Heists", expected);
		Assert.Contains("Time travel", expected);
		Assert.Contains("Under 90 minutes", expected);
		Assert.All(home.NixieRows, r => Assert.Equal(10, r.Items.Count));
		await home.OnNavigatedFrom();

		// Settings → Made by Nixie on Home → "A third row of picks": on, and Home's saved page is stale (the next visit asks)
		var third = Assert.Single(new CollectionsSettingsViewModel(_settings, _homeCache).HomeRows, x => x.Name == CollectionsSettingsViewModel.ThirdRowName);
		Assert.False(third.IsOn);
		Assert.True((await _homeCache.Get("http://server", _client.Object.UserId)).IsFresh);
		third.IsOn = true;
		Assert.False((await _homeCache.Get("http://server", _client.Object.UserId)).IsFresh);
		Assert.Equal(3, CollectionLineup.HomeRows(_settings));
		var again = Home();
		await again.OnNavigatedTo(new object());

		Assert.Equal(expected.Take(3), again.NixieRows.Select(r => r.Title));
		Assert.Equal(expected.Take(3), (await _homeCache.Get("http://server", _client.Object.UserId)).Snapshot!.NixieRows.Select(r => new NixieHomeRowViewModel(r.Collection).Title));

		// and off again: two
		third.IsOn = false;
		Assert.Equal(CollectionLineup.HomeDefault, CollectionLineup.HomeRows(_settings));

		// a setting past three is three
		_settings.SaveSetting(CollectionLineup.HomeRowsKey.Name, 7);
		Assert.Equal(CollectionLineup.HomeMost, CollectionLineup.HomeRows(_settings));
		var said = MadeByNixieLog.Instance.Entries.Where(x => x.Message.StartsWith("Home: ", StringComparison.Ordinal)).ToList();
		Assert.Contains(said, x => x.Message.StartsWith("Home: 3 Made by Nixie rows in ", StringComparison.Ordinal) && x.Message.Contains("from the index in memory", StringComparison.Ordinal));
		Assert.All(said, x => Assert.DoesNotContain("Lineup ", x.Message, StringComparison.Ordinal)); // durations and counts, never a title
	}

	[Fact]
	public async Task APageSavedBy17_PaintsNoMoreRowsThanHomeShowsNow_ThenTheLineupsRows()
	{
		// 1.7's saved page: Acclaimed, Unfinished series, Halloween, a franchise of the day and Trending: five rows, which went to
		// two at once when the lineup's came in, every row under them moving up (rule 7)
		SmartCollection Row(string id, SmartCollectionKind kind, string title) => new(id, kind, title, "", false, [_films[0].Id!.Value]);
		var saved = new[]
		{
			Row("pick:highly-rated", SmartCollectionKind.HighlyRated, "Acclaimed, not seen yet"), Row("pick:unfinished-series", SmartCollectionKind.UnfinishedSeries, "Unfinished series"),
			Row("seasonal:halloween", SmartCollectionKind.Seasonal, "Halloween"), Row("franchise:1", SmartCollectionKind.Franchise, "The Lineup films"),
			Row(TrendingBuilder.HomeRowId, SmartCollectionKind.Trending, TrendingBuilder.HomeRowTitle),
		};
		var snapshot = new Services.HomeSnapshot([], [], [new Services.HomeSnapshotRow(new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = Guid.NewGuid(), Name = "Movies" }, [_films[0]])], [])
		{
			NixieRows = [.. saved.Select(x => new Services.HomeSnapshotNixieRow(x, [_films[0]]))],
		};
		var user = _client.Object.UserId;
		await _homeCache.Save("http://server", user, snapshot, (await _homeCache.Get("http://server", user)).ChangesSeen);
		var painted = new List<string>();
		var home = Home();
		home.NixieRows.CollectionChanged += (_, e) => painted.AddRange(e.NewItems?.OfType<NixieHomeRowViewModel>().Select(r => r.Title) ?? []);

		await home.OnNavigatedTo(new object()); // the saved page is fresh: no index read

		Assert.Equal(["Acclaimed, not seen yet", "Unfinished series", "Halloween"], painted);
		Assert.Equal(["Acclaimed, not seen yet", "Unfinished series", "Halloween"], home.NixieRows.Select(r => r.Title));
	}

	[Fact]
	public async Task ARowWhoseReasonLineChanged_IsMadeAgain_ThoughItsTitlesStay()
	{
		// stream t's model: a saved row says another reason line (a 1.7 page has none; an unwatched count went stale) than the
		// same collection made now: Home shows the new words, not the saved ones
		var index = await Index();
		var lineup = CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(new ManualClock()), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty).Lineup;
		var first = lineup.Picks[0].Collection;
		var second = lineup.Picks[1].Collection;
		var stale = first with { Reason = ReasonLine.Of("Old words", ReasonCount.Titles(3, IndexKind.Movie, 3)) };
		List<Jellyfin.Sdk.Generated.Models.BaseItemDto> Cards(SmartCollection c) => [.. c.ItemIds.Take(HomeViewModel.NixieRowLength).Select(id => _films.First(f => f.Id == id))];
		var snapshot = new Services.HomeSnapshot([], [], [new Services.HomeSnapshotRow(new Jellyfin.Sdk.Generated.Models.BaseItemDto { Id = Guid.NewGuid(), Name = "Movies" }, [_films[0]])], [])
		{
			// Home's two rows as the index makes them, but for the first's reason line
			NixieRows = [new Services.HomeSnapshotNixieRow(stale, Cards(first)), new Services.HomeSnapshotNixieRow(second, Cards(second))],
		};
		var user = _client.Object.UserId;
		await _homeCache.Save("http://server", user, snapshot, (await _homeCache.Get("http://server", user)).ChangesSeen);
		_homeCache.MarkStale();
		var said = new List<string>();
		var home = Home();
		home.NixieRows.CollectionChanged += (_, e) => said.AddRange(e.NewItems?.OfType<NixieHomeRowViewModel>().Select(r => r.Collection.ReasonText) ?? []);

		await home.OnNavigatedTo(new object());

		Assert.Equal(stale.ReasonText, said[0]); // the saved page first (rule 1)
		Assert.Equal(first.ReasonText, home.NixieRows[0].Collection.ReasonText);
		Assert.NotEqual(stale.ReasonText, first.ReasonText);
	}

	// ---- the Collections page: This week, and See all's reverse toggle ----------------------------------------------------

	private CollectionsViewModel Page(LibraryIndex index) =>
		new(_client.Object, new GlobalCommands(Moq.Mock.Of<Services.INavigationServiceCore>(), _client.Object, Moq.Mock.Of<Services.INavigationViewServiceCore>(), Moq.Mock.Of<Settings.ISettings>()),
			index, new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>()), _settings, _clock);

	[Fact]
	public async Task TheCollectionsPage_OpensOnThisWeek_TheLineupsTilesInItsOrder_HomesRowsAmongThem_AndTheSameAfterBack()
	{
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		var index = await Index();
		var lineup = CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(new ManualClock()), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty).Lineup;
		var vm = Page(index);
		var place = new CollectionsPlace(CollectionsSection.MadeByNixie);

		await vm.OnNavigatedTo(place);

		// what the user sees: This week first under the Made by Nixie heading, the lineup's tiles in its order, no See all
		var week = vm.Rows[0];
		Assert.Equal((CollectionsViewModel.ThisWeekTitle, "Made by Nixie", false), (week.Title, week.Heading, week.HasSeeAll));
		Assert.Equal(lineup.Picks.Select(x => x.Collection.Title), week.Tiles.Select(t => t.Title));
		Assert.InRange(week.Tiles.Count, 1, CollectionLineup.Slots);
		Assert.All(week.Tiles, t => Assert.Contains(", made by Nixie, ", t.AutomationName, StringComparison.Ordinal));
		Assert.Same(week, vm.FocusRow); // opened at Made by Nixie: the keyboard's row
		// one collection, one mosaic: This week's Acclaimed and the Picks row's are the same
		var picks = Assert.Single(vm.Rows, r => r.Title == "Picks");
		var acclaimed = Assert.Single(week.Tiles, t => t.Nixie!.Kind == SmartCollectionKind.HighlyRated);
		Assert.Equal(acclaimed.Nixie!.ItemIds, Assert.Single(picks.Tiles, t => t.Nixie!.Kind == SmartCollectionKind.HighlyRated).Nixie!.ItemIds);

		// Home's rows are This week's first ones (the same lineup, the same day)
		var home = Home();
		await home.OnNavigatedTo(new object());
		Assert.Equal(week.Tiles.Take(home.NixieRows.Count).Select(t => t.Nixie!.Id), home.NixieRows.Select(r => r.Collection.Id));

		// Back: the kept page shows This week as it was
		await vm.OnNavigatedFrom();
		await vm.Returned(place, stale: false);
		Assert.Same(week, vm.Rows[0]);
		Assert.Equal(lineup.Picks.Select(x => x.Collection.Title), vm.Rows[0].Tiles.Select(t => t.Title));
	}

	[Fact]
	public async Task SeeAllsDirectionButton_RunsTheGridTheOtherWay_SaysWhich_AndIsKeptOnBackAndR_ForThatGridOnly()
	{
		_client.SetupGet(x => x.UserId).Returns(Guid.NewGuid()); // the session's keep is per user: this test's own
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		foreach (var (name, tmdb) in new[] { ("Alpha Run", "9101"), ("Middle Way", "9102"), ("Zulu Dawn", "9103") })
		{
			foreach (var part in new[] { "", " 2", " 3" }) // 1.8.1 (fix 7): three films, a franchise Nixie publishes
			{
				var film = Film(name + part, 2001, 6.5, 100, null, ["Action"]);
				film.ProviderIds = new Jellyfin.Sdk.Generated.Models.BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["TmdbCollection"] = tmdb } };
				_films.Add(film);
			}
		}

		var index = await Index();
		var franchises = new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Franchise, SeeAll: true);
		var vm = Page(index);
		await vm.OnNavigatedTo(franchises);
		Assert.Equal(["Alpha Run", "Middle Way", "Zulu Dawn"], vm.GridTiles.Select(t => t.Title));
		Assert.Equal(("A to Z", "A to Z, switch to Z to A", "Switch to Z to A"), (vm.GridDirectionWords, vm.GridDirectionName, vm.GridDirectionTip));
		var up = vm.GridDirectionGlyph;
		var tiles = vm.GridTiles.ToList();

		vm.ReverseGrid();

		Assert.Equal(["Zulu Dawn", "Middle Way", "Alpha Run"], vm.GridTiles.Select(t => t.Title));
		Assert.Equal(tiles.AsEnumerable().Reverse(), vm.GridTiles); // the same tiles, the other way: nothing read or made again
		Assert.Equal(("Z to A", "Z to A, switch to A to Z"), (vm.GridDirectionWords, vm.GridDirectionName));
		Assert.NotEqual(up, vm.GridDirectionGlyph);

		// Back: the kept page as it was
		await vm.OnNavigatedFrom();
		await vm.Returned(franchises, stale: false);
		Assert.Equal(["Zulu Dawn", "Middle Way", "Alpha Run"], vm.GridTiles.Select(t => t.Title));
		// R (or Forward): a new page of the same grid reads the direction back
		var again = Page(index);
		await again.OnNavigatedTo(franchises);
		Assert.Equal(["Zulu Dawn", "Middle Way", "Alpha Run"], again.GridTiles.Select(t => t.Title));
		Assert.Equal("Z to A, switch to A to Z", again.GridDirectionName);
		// another grid runs as its order does
		var keywords = Page(index);
		await keywords.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie, SmartCollectionKind.Keyword, SeeAll: true));
		Assert.False(keywords.GridReversed);
		Assert.Equal("First to last, switch to last to first", keywords.GridDirectionName);

		// and back the first way
		again.ReverseGrid();
		Assert.Equal(["Alpha Run", "Middle Way", "Zulu Dawn"], again.GridTiles.Select(t => t.Title));
		var third = Page(index);
		await third.OnNavigatedTo(franchises);
		Assert.False(third.GridReversed);
	}

	[Fact]
	public async Task KeptOnBack_HomesRowsAndThisWeek_AreMadeAgainOnANewDay_NotOnTheSameDay()
	{
		// 1.7.5 keeps Home and the Collections page whole on Back: shown again tomorrow, their lineup is tomorrow's (its daily
		// picks turn: Acclaimed's and Under 90's titles)
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		var index = await Index();
		var home = Home();
		await home.OnNavigatedTo(new object());
		var page = Page(index);
		var place = new CollectionsPlace(CollectionsSection.MadeByNixie);
		await page.OnNavigatedTo(place);
		string HomeShows() => string.Join(" | ", home.NixieRows.Select(r => string.Join(",", r.Collection.ItemIds)));
		string WeekShows() => string.Join(" | ", page.Rows[0].Tiles.Select(t => string.Join(",", t.Nixie!.ItemIds)));
		var homeToday = HomeShows();
		var weekToday = WeekShows();
		Assert.Contains(page.Rows[0].Tiles, t => t.Nixie!.Kind is SmartCollectionKind.HighlyRated or SmartCollectionKind.ShortFilms);

		// the same day: shown as they were (Back, through Home's own Returned)
		await home.OnNavigatedFrom();
		await home.Returned(new object(), stale: false);
		await page.OnNavigatedFrom();
		await page.Returned(place, stale: false);
		Assert.Equal(homeToday, HomeShows());
		Assert.Equal(weekToday, WeekShows());

		// the next day
		_clock.Advance(TimeSpan.FromDays(1));
		await home.OnNavigatedFrom();
		await home.Returned(new object(), stale: false); // the re-review: the hook wired, not called by the test
		await page.OnNavigatedFrom();
		await page.Returned(place, stale: false);
		Assert.NotEqual(weekToday, WeekShows());
		Assert.Equal(CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(_clock), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty)
			.Lineup.Picks.Take(home.NixieRows.Count).Select(x => string.Join(",", x.Collection.ItemIds)), home.NixieRows.Select(r => string.Join(",", r.Collection.ItemIds)));
		Assert.Contains(MadeByNixieLog.Instance.Entries, x => x.Message.Contains("from the index in memory, a new day", StringComparison.Ordinal));
	}

	[Fact]
	public async Task KeptOnBack_ADayLater_HomesRowsAreTheNewDays_ThroughBacksOwnRefresh()
	{
		// the way Back really comes to a kept Home a day later: kept longer than PageKeep.FreshFor (stale) and its saved page
		// older than HomeCache.FreshFor, so back's Returned asks its sections again behind, Made by Nixie's with them: the
		// rows are made from today's lineup (three rows: Acclaimed's daily turn is among them)
		_settings.SaveSetting(CollectionLineup.HomeRowsKey.Name, 3);
		var index = await Index();
		using var cache = new Services.HomeCache(Path.Combine(_folder, "HomeCacheOnTheClock"), _messages, new CoreListLogger<Services.HomeCache>(), _clock);
		var home = Home(cache);
		IEnumerable<string> Picks() => CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(_clock), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty)
			.Lineup.Picks.Take(3).Select(x => string.Join(",", x.Collection.ItemIds));
		string[] Shows() => [.. home.NixieRows.Select(r => string.Join(",", r.Collection.ItemIds))];

		await home.OnNavigatedTo(new object());
		var today = Shows();
		Assert.Equal(Picks(), today);
		await home.OnNavigatedFrom();

		_clock.Advance(TimeSpan.FromDays(1));
		var tomorrow = Picks().ToArray();
		Assert.NotEqual(today, tomorrow); // the fixture: a day turns what Home shows
		await home.Returned(new object(), stale: true);
		Assert.Equal(tomorrow, Shows());
	}

	// ---- 1.8 (the coordinator's Major: Home must not get slower) ----------------------------------------------------------

	private static readonly Guid User = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000180");

	private static async Task<bool> Until(Func<bool> done, int ms = 15000)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		while (!done())
		{
			if (watch.ElapsedMilliseconds > ms)
			{
				return false;
			}

			await Task.Delay(20);
		}

		return true;
	}

	private static string Key(SmartCollection x) => $"{x.Id}|{string.Join(",", x.ItemIds)}";

	// twenty more films rated 8 or more: with the fixture's ten, a full Acclaimed (its 20)
	private static IEnumerable<Jellyfin.Sdk.Generated.Models.BaseItemDto> MoreAcclaimed() =>
		Enumerable.Range(0, 20).Select(i => Film($"Lineup praised {i}", 1960 + i, 8.0 + (i * 0.04), 125, null, ["Drama"]));

	// a library of two pages (a first page is 200 titles): the second waits for the test
	private TaskCompletionSource TwoPages()
	{
		_films.AddRange(Enumerable.Range(0, 210).Select(i => Film($"Lineup filler {i}", 1980 + (i % 40), 5.0, 100, null, ["Documentary"])));
		var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_client.Setup(x => x.GetLibraryIndexPage(Moq.It.IsAny<Jellyfin.Sdk.Generated.Models.BaseItemDto>(), Moq.It.IsAny<int>(), Moq.It.IsAny<int>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()))
			.Returns(async (Jellyfin.Sdk.Generated.Models.BaseItemDto _, int start, int limit, bool _, CancellationToken _) =>
			{
				if (start > 0)
				{
					await second.Task;
				}

				return new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count };
			});
		return second;
	}

	[Fact]
	public async Task FirstRead_LeftAndBack_TheWholeIndexLandedWhileAway_BackShowsItsRows()
	{
		// the re-review (its test): a kept Home left during a first read is filled with the whole index's rows while hidden
		var second = TwoPages();
		_films.InsertRange(0, MoreAcclaimed());
		// the second page brings a better theme and better films: the whole index's lineup differs from the first pages'
		_films.AddRange(Enumerable.Range(0, 12).Select(i => Film($"Zz late heist {i}", 2000 + i, 8.9, 118, "heist", ["Crime"])));
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		SmartCollectionServices.Index = index;
		var home = Home();

		await home.OnNavigatedTo(new object());
		Assert.True(await Until(() => home.NixieRows.Count > 0));
		var partial = home.NixieRows.Select(r => Key(r.Collection)).ToList();

		await home.OnNavigatedFrom(); // a title opened from Home
		second.SetResult();
		await home.NixieFill;
		await home.Returned(new object(), stale: false); // Back
		var made = CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(_clock), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty);
		var whole = HomeViewModel.PickNixieRows(made.Lineup, made.All, new HashSet<NixieHomeRow>(), new HashSet<SmartCollectionKind>(), CollectionLineup.HomeDefault).Select(Key).ToList();
		Assert.NotEqual(partial, whole);
		Assert.Equal(whole, home.NixieRows.Select(r => Key(r.Collection)));
	}

	[Fact]
	public async Task SignedOutAndIntoAnotherServer_WhileAFillWaits_NothingOfTheFirstIsShownOrSavedAsTheOthers()
	{
		// the re-review's Major (its test): a fill notes its server and user when it starts; a fill that ends after a sign-out and
		// a sign-in elsewhere swaps nothing and saves nothing (it saved the first server's Home, marked fresh, as the other's)
		var url = "http://server";
		var user = User;
		_client.SetupGet(x => x.BaseUrl).Returns(() => url);
		_client.SetupGet(x => x.UserId).Returns(() => user);
		var (spotlight, gate) = HeldSpotlight();
		using var cache = new Services.HomeCache(Path.Combine(_folder, "HomeCacheOnTheClock"), _messages, new CoreListLogger<Services.HomeCache>(), _clock);
		var index = new LibraryIndex(_client.Object, cache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		SmartCollectionServices.Index = index;
		var first = Home(cache);
		first.LaterBuild = (_, _, _) => Task.FromResult<IReadOnlyList<SmartCollection>>([]);
		await first.OnNavigatedTo(new object());
		await first.NixieFill;
		await first.SpotlightLater;
		await first.OnNavigatedFrom();
		await index.Settled;
		Assert.NotEmpty(first.NixieRows);

		// the next start: the saved page, the saved index behind it; its fill waits for the week's Spotlight
		_clock.Advance(TimeSpan.FromMinutes(10));
		var again = new LibraryIndex(_client.Object, cache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		SmartCollectionServices.Index = again;
		var next = Home(cache);
		next.LaterBuild = (_, _, _) => gate.Task;
		await next.OnNavigatedTo(new object());
		Assert.True(await Until(() => again.Current is not null));
		await Task.Delay(300);
		Assert.False(next.NixieFill.IsCompleted, "the fill waits for the Spotlight");
		var shown = next.NixieRows.Select(r => Key(r.Collection)).ToList();

		// sign out, sign in to another server (a saved login)
		await next.OnNavigatedFrom();
		await cache.Forget(url, user);
		url = "http://other";
		user = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000180");
		cache.SignedIn(url, user, "other-server", "someone");
		gate.SetResult([spotlight]);
		await next.NixieFill;
		await next.SpotlightLater;

		Assert.Null((await cache.Get(url, user)).Snapshot); // nothing of the first server's is the other's saved Home
		Assert.Equal(shown, next.NixieRows.Select(r => Key(r.Collection))); // nor swapped in
	}

	[Fact]
	public async Task TheCollectionsPage_ShowsBeforeTheWeeksSpotlight_ThisWeekTakesItAfter()
	{
		// the re-review: the page waited for the Spotlight when Home's build couldn't be reused
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		var (spotlight, gate) = HeldSpotlight();
		var index = await Index();
		var page = Page(index);
		page.LaterBuild = (_, _, _) => gate.Task;

		var opening = page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		Assert.True(await Until(() => opening.IsCompleted && page.Rows.Count > 0), "the page shows without waiting for the Spotlight");
		Assert.DoesNotContain(page.Rows[0].Tiles, t => t.Nixie!.Id == "spotlight:test");

		gate.SetResult([spotlight]);
		await page.SpotlightLater;
		Assert.Contains(page.Rows[0].Tiles, t => t.Nixie!.Id == "spotlight:test");
	}

	[Fact]
	public async Task ThisWeekGainingACollection_KeepsItsRowAndTheTileOfEveryCollectionStillInIt()
	{
		// BH-180-M1: the people step (or the Spotlight after the page, the day's read) changes the lineup while the page shows;
		// This week was made again and the keyboard on its tile went to Back. Its row and each tile still in it stay
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		var (spotlight, gate) = HeldSpotlight();
		var index = await Index();
		var page = Page(index);
		page.LaterBuild = (_, _, _) => gate.Task;
		await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		var week = page.Rows[0];
		Assert.Equal(CollectionsViewModel.ThisWeekTitle, week.Title);
		var tiles = week.Tiles.ToDictionary(t => t.Nixie!.Id);
		var rows = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
		page.Rows.CollectionChanged += (_, e) => rows.Add(e.Action);

		gate.SetResult([spotlight]);
		await page.SpotlightLater;

		Assert.Same(week, page.Rows[0]); // the row stays (the keyboard on one of its tiles with it)
		Assert.Contains(week.Tiles, t => t.Nixie!.Id == "spotlight:test"); // the new one put in
		Assert.All(week.Tiles.Where(t => tiles.ContainsKey(t.Nixie!.Id)), t => Assert.Same(tiles[t.Nixie!.Id], t)); // each still in it, the same tile
		Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Replace, rows);
	}

	[Fact]
	public void ARowTakingItsNewerTiles_KeepsEachStillInIt_PutsInTheNew_TakesOutTheGone()
	{
		// BH-180-M1: in place, by what each tile is (its collection's id): a tile still there moves to its place, unchanged
		SmartCollection C(string id) => new(id, SmartCollectionKind.Keyword, id, "", false, [_films[0].Id!.Value]);
		CollectionsRow Row(params string[] ids) => new(CollectionsViewModel.ThisWeekTitle, CollectionsSection.MadeByNixie, null, [.. ids.Select(id => CollectionTile.Made(C(id)))], false)
		{
			Heading = "Made by Nixie",
		};
		var row = Row("a", "b", "c", "d");
		var (b, d) = (row.Tiles[1], row.Tiles[3]);
		var newer = Row("new", "b", "d", "a");

		Assert.True(row.IsRow(newer));
		row.Take(newer);

		Assert.Equal(["new", "b", "d", "a"], row.Tiles.Select(t => t.Nixie!.Id));
		Assert.Same(b, row.Tiles[1]);
		Assert.Same(d, row.Tiles[2]);
		Assert.False(row.IsRow(new CollectionsRow("Picks", CollectionsSection.MadeByNixie, null, [], false)));
	}

	[Fact]
	public async Task APinNixieNoLongerMakes_StaysInSavedByItsTitle_SaidToHaveEnded()
	{
		// BH-180-m1: a pinned Spotlight or personal pick that isn't made any more went from Saved without a word
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		var index = await Index();
		var store = new SmartCollectionStore(_client.Object, _homeCache, Path.Combine(_folder, "SmartCollections"), new CoreListLogger<SmartCollectionStore>());
		await store.SetFavorite($"like:{Guid.NewGuid():N}", true, "More like A Film Long Finished");
		await store.SetFavorite($"more:{Guid.NewGuid():N}", true); // pinned before titles were kept

		var page = Page(index);
		await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.All));

		var saved = Assert.Single(page.Rows, r => r.Section == CollectionsSection.Saved);
		var ended = Assert.Single(saved.Tiles, t => t.Title == "More like A Film Long Finished");
		Assert.Equal(CollectionTile.EndedText, ended.Detail);
		Assert.Contains("ended: Nixie no longer makes it", ended.AutomationName, StringComparison.Ordinal);
		Assert.Contains(saved.Tiles, t => t.Title == CollectionsViewModel.EndedTitle && t.Detail == CollectionTile.EndedText);
		Assert.Equal(CollectionViewModel.EndedHeading, CollectionViewModel.EmptyOutcome(ended.Nixie!).Title); // opened, it says so
	}

	[Fact]
	public async Task EveryMadeByNixieSwitchOff_LeavesNoNixieRowOnHome_NoThisWeek_AndSaysSo()
	{
		// BH-180-M2: 1.8's four kinds had no switch: every switch off still left Home two rows and This week's personal tiles
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		_films[0].UserData = new Jellyfin.Sdk.Generated.Models.UserItemDataDto { Played = true, PlayCount = 1 }; // an anchor: More like
		// what turning every kind switch off writes (the switches themselves: SettingsHasASwitchForEveryPublishedKind)
		_settings.SaveSetting(CollectionSettingKeys.KindsOff.Name, string.Join(",", SmartCollectionKinds.Switched));
		var index = await Index();
		await index.Settled;
		var home = Home();
		await home.OnNavigatedTo(new object());
		var page = Page(index); // its later build (the Spotlight) the catalog's own: switched off too
		await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await page.SpotlightLater;

		Assert.Empty(home.NixieRows);
		Assert.DoesNotContain(page.Rows, r => r.Section == CollectionsSection.MadeByNixie);
		Assert.Equal(CollectionsViewModel.NixieOffText, page.ReadingText);
	}

	[Fact]
	public void SettingsHasASwitchForEveryPublishedKind_ThePeopleKindsTwo()
	{
		// BH-180-M2: every kind the catalog publishes (and the Spotlight, made after the first rows) has its switch; awards follow
		// Wikidata's switch (Look up lists online)
		var switches = new CollectionsSettingsViewModel(new LineupSettings()).Kinds.Select(x => x.Name).ToHashSet();
		var published = SmartCollectionCatalog.PublishedBuilders.Concat(SmartCollectionCatalog.LaterBuilders).Select(x => x.Kind).Where(x => x != SmartCollectionKind.Award).Distinct();
		Assert.All(published, kind => Assert.Contains(SmartCollectionKinds.Plural(kind), switches));
		Assert.Equal(new HashSet<SmartCollectionKind> { SmartCollectionKind.MoreFrom, SmartCollectionKind.Spotlight }, CollectionSettings.PeopleKinds);
	}

	[Fact]
	public async Task WithMoreFromAndSpotlightOff_TheIndexAsksNoPeople_TurnedBackOnItDoes()
	{
		// BH-180-M2: the people step ran whatever the switches said (a whole-library read of a server the user doesn't own)
		var wanted = false;
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System)
		{
			PeopleWanted = () => wanted,
		};
		SmartCollectionServices.Index = index;
		await index.GetAsync();
		await index.Settled;
		_client.Verify(x => x.GetLibraryPeoplePage(Moq.It.IsAny<Jellyfin.Sdk.Generated.Models.BaseItemDto>(), Moq.It.IsAny<int>(), Moq.It.IsAny<int>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.Never);
		Assert.False(index.Current!.HasPeople && !index.Current.PeopleDue);

		wanted = true; // More from turned on
		index.PeopleSwitched();
		await index.Settled;
		_client.Verify(x => x.GetLibraryPeoplePage(Moq.It.IsAny<Jellyfin.Sdk.Generated.Models.BaseItemDto>(), Moq.It.IsAny<int>(), Moq.It.IsAny<int>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.AtLeastOnce);
		Assert.True(CollectionSettings.PeopleWanted(_settings)); // nothing off here
		_settings.SaveSetting(CollectionSettingKeys.HomeRowsOff.Name, "");
		_settings.SaveSetting(CollectionSettingKeys.KindsOff.Name, "MoreFrom,Spotlight");
		Assert.False(CollectionSettings.PeopleWanted(_settings));
	}

	// the pre-release fix H (BH-190-m1): Home never shows a category, so with its Made by Nixie rows off its first row never
	// came, and every summaries' pass waited two minutes for it
	[Fact]
	public async Task AHomeWithNoMadeByNixieRowToShow_TellsTheSummariesPassToBegin_AHomeWithRowsByItsFirstRow()
	{
		_settings.SaveSetting(CollectionSettingKeys.HomeRowsOff.Name, string.Join(",", Enum.GetNames<NixieHomeRow>()));
		await Index();
		var off = Home();
		await off.OnNavigatedTo(new object());
		await off.NixieFill;
		Assert.Empty(off.NixieRows);
		Assert.Null(off.FirstNixieRowFrom);
		Assert.True(off.NoNixieRowSaid);

		await off.OnNavigatedFrom();
		_settings.SaveSetting(CollectionSettingKeys.HomeRowsOff.Name, "");
		_homeCache.MarkStale(); // as the rows' switches do
		var on = Home();
		await on.OnNavigatedTo(new object());
		await on.NixieFill;
		Assert.NotEmpty(on.NixieRows);
		Assert.NotNull(on.FirstNixieRowFrom); // its first row told the pass
		Assert.False(on.NoNixieRowSaid);
	}

	[Fact]
	public async Task FirstRead_HomesRowsComeFromItsFirstPages_ThenTheWholeIndexsAreShownAndSaved()
	{
		// nothing saved, a library of two pages (a first page is 200 titles): the lineup's 40 titles, then 210 that make no
		// collection; the second page waits. Home must not wait for the whole read to show its first Made by Nixie row
		_films.AddRange(Enumerable.Range(0, 210).Select(i => Film($"Lineup filler {i}", 1980 + (i % 40), 5.0, 100, null, ["Documentary"])));
		var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_client.Setup(x => x.GetLibraryIndexPage(Moq.It.IsAny<Jellyfin.Sdk.Generated.Models.BaseItemDto>(), Moq.It.IsAny<int>(), Moq.It.IsAny<int>(), Moq.It.IsAny<bool>(), Moq.It.IsAny<CancellationToken>()))
			.Returns(async (Jellyfin.Sdk.Generated.Models.BaseItemDto _, int start, int limit, bool _, CancellationToken _) =>
			{
				if (start > 0)
				{
					await second.Task;
				}

				return new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [.. _films.Skip(start).Take(limit)], TotalRecordCount = _films.Count };
			});
		_films.InsertRange(0, MoreAcclaimed()); // a full Acclaimed in the first page
		var index = new LibraryIndex(_client.Object, _homeCache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		SmartCollectionServices.Index = index;
		var home = Home();

		await home.OnNavigatedTo(new object());
		Assert.True(await Until(() => home.NixieRows.Count > 0), "Home's rows come from the first read's first pages");
		Assert.Null(index.Current); // the whole read isn't in
		Assert.Equal("the index's first pages", home.FirstNixieRowFrom);
		// the re-review: the first pages are the titles first by name, so only a full Acclaimed or Under 90 minutes shows from
		// them (20 titles: the count the whole index's says too), and nothing of them is saved (a play saves Home meanwhile)
		Assert.All(home.NixieRows, r => Assert.True(r.Collection.Kind is SmartCollectionKind.HighlyRated or SmartCollectionKind.ShortFilms && r.Collection.ItemIds.Count == 20,
			$"{r.Collection.Kind} of {r.Collection.ItemIds.Count}"));
		await home.SaveNow();
		Assert.Empty((await _homeCache.Get("http://server", User)).Snapshot?.NixieRows ?? []);

		second.SetResult();
		await home.NixieFill;
		var whole = index.Current!;
		var made = CollectionLineup.Build(whole, SmartCollectionCatalog.Now(_clock), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty);
		var expected = HomeViewModel.PickNixieRows(made.Lineup, made.All, new HashSet<NixieHomeRow>(), new HashSet<SmartCollectionKind>(), CollectionLineup.HomeDefault).Select(Key).ToList();
		Assert.Equal(expected, home.NixieRows.Select(r => Key(r.Collection)));
		// the page saved is the whole index's
		Assert.Equal(expected, ((await _homeCache.Get("http://server", User)).Snapshot?.NixieRows ?? []).Select(r => Key(r.Collection)));
		Assert.Equal("the index's first pages", home.FirstNixieRowFrom); // said once a visit
	}

	[Fact]
	public async Task ANewLineup_SwapsOnlyTheRowsThatChanged_InPlace_NoRowGoesAndComesBack()
	{
		// rule 7: a lineup worked out behind the rows shown replaces a row where it is; the rows under it never move up and back
		_settings.SaveSetting(CollectionLineup.HomeRowsKey.Name, 3);
		await Index();
		var home = Home();
		await home.OnNavigatedTo(new object());
		var before = home.NixieRows.ToList();
		var collections = before.Select(r => Key(r.Collection)).ToList();
		var rowChanges = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
		home.NixieRows.CollectionChanged += (_, e) => rowChanges.Add(e);
		var cardChanges = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
		foreach (var row in before)
		{
			row.Items.CollectionChanged += (_, e) => cardChanges.Add(e.Action);
		}

		var titles = new List<string>();
		foreach (var row in before)
		{
			row.PropertyChanged += (_, e) => titles.Add(e.PropertyName ?? "");
		}

		_clock.Advance(TimeSpan.FromDays(1)); // Acclaimed's titles turn (KeptOnBack_ADayLater's fixture)
		await home.OnNavigatedFrom();
		await home.Returned(new object(), stale: false);

		// the re-review: the rows stay on the page (the keyboard on one of their cards with them): none replaced, gone or added
		Assert.NotEqual(collections, home.NixieRows.Select(r => Key(r.Collection))); // the day turned them
		Assert.Empty(rowChanges);
		Assert.Equal(before, home.NixieRows);
		// a row taking another collection says so (its title, bound one way) and changes its cards where they are
		Assert.Contains(nameof(NixieHomeRowViewModel.Title), titles);
		Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Replace, cardChanges);
		Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, cardChanges);
	}

	[Fact]
	public void ARowTakingAnotherCollection_KeepsItsCardsOfTheSameTitles_AndReplacesTheOthersWhereTheyAre()
	{
		// the keyboard's card: one of the same title stays the same card; one replaced is replaced at its place (ShelfRow then
		// gives the keyboard to the card in its place); the cards past the new ones go from the end (ShelfRow: the neighbour)
		var films = _films.Take(6).ToList();
		var row = new NixieHomeRowViewModel(new SmartCollection("a", SmartCollectionKind.Keyword, "A", "", false, [.. films.Take(4).Select(x => x.Id!.Value)]))
		{
			Items = [.. films.Take(4).Select(BaseItemViewModel.FromDto)],
		};
		var kept = row.Items[0];
		var actions = new List<(System.Collections.Specialized.NotifyCollectionChangedAction Action, int Index)>();
		row.Items.CollectionChanged += (_, e) => actions.Add((e.Action, e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove ? e.OldStartingIndex : e.NewStartingIndex));

		row.Take(new SmartCollection("b", SmartCollectionKind.Keyword, "B", "", false, [films[0].Id!.Value, films[5].Id!.Value]), [films[0], films[5]]);

		Assert.Equal("b", row.Collection.Id);
		Assert.Same(kept, row.Items[0]);
		Assert.Equal([films[0].Id, films[5].Id], row.Items.Select(x => x.Dto.Id));
		Assert.Equal([(System.Collections.Specialized.NotifyCollectionChangedAction.Replace, 1), (System.Collections.Specialized.NotifyCollectionChangedAction.Remove, 3),
			(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, 2)], actions);
	}

	[Fact]
	public async Task TheNextStart_PaintsTheSavedPagesRowsAtOnce_ThenTheSavedIndexBehind_RedrawingNothingTheSameDay()
	{
		using var cache = new Services.HomeCache(Path.Combine(_folder, "HomeCacheOnTheClock"), _messages, new CoreListLogger<Services.HomeCache>(), _clock);
		var index = new LibraryIndex(_client.Object, cache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		SmartCollectionServices.Index = index;
		var first = Home(cache);
		await first.OnNavigatedTo(new object());
		await first.NixieFill;
		await first.OnNavigatedFrom();
		var shown = first.NixieRows.Select(r => Key(r.Collection)).ToList();
		Assert.NotEmpty(shown);

		// the next start: nothing in memory (a new index on the same folder), Home's saved page ten minutes old
		_clock.Advance(TimeSpan.FromMinutes(10));
		var again = new LibraryIndex(_client.Object, cache, Path.Combine(_folder, "LibraryIndex"), _messages, new CoreListLogger<LibraryIndex>(), TimeProvider.System);
		SmartCollectionServices.Index = again;
		var next = Home(cache);
		var painted = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
		next.NixieRows.CollectionChanged += (_, e) => painted.Add(e);
		await next.OnNavigatedTo(new object());
		Assert.Equal("Home's saved page", next.FirstNixieRowFrom);
		await next.NixieFill;

		Assert.NotNull(again.Current); // the saved index was read behind the rows
		Assert.Equal(shown, next.NixieRows.Select(r => Key(r.Collection)));
		// painted once, from the saved page; the saved index's lineup (the same day's) redrew nothing
		// (the saved page's paint clears the empty list first: a Reset of nothing)
		Assert.Equal(string.Join(",", shown.Select(_ => "Add")), string.Join(",", painted.SkipWhile(e => e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset).Select(e => e.Action)));
	}

	// stream s's Major 2: the week's Spotlight is made after Home's first rows (BuildLaterAsync); a fixture's own, held back
	private (SmartCollection Spotlight, TaskCompletionSource<IReadOnlyList<SmartCollection>> Gate) HeldSpotlight()
	{
		var films = Enumerable.Range(0, 10).Select(i => Film($"Lineup spotlight {i}", 1940 + i, 6.0, 120, null, ["War"])).ToList();
		_films.AddRange(films);
		var spotlight = new SmartCollection("spotlight:test", SmartCollectionKind.Spotlight, "Spotlight", "", false, [.. films.Select(x => x.Id!.Value)]);
		return (spotlight, new TaskCompletionSource<IReadOnlyList<SmartCollection>>(TaskCreationOptions.RunContinuationsAsynchronously));
	}

	[Fact]
	public async Task TheWeeksSpotlight_ComesAfterHomesFirstRows_InPlace_AndIsSaved()
	{
		var (spotlight, gate) = HeldSpotlight();
		await Index();
		var home = Home();
		home.LaterBuild = (_, _, _) => gate.Task;
		var changes = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();

		var opening = home.OnNavigatedTo(new object()); // the index in memory, nothing saved
		Assert.True(await Until(() => home.NixieRows.Count > 0), "Home's first rows don't wait for the Spotlight");
		var first = home.NixieRows.Count;
		Assert.DoesNotContain(home.NixieRows, r => r.Collection.Kind == SmartCollectionKind.Spotlight);
		home.NixieRows.CollectionChanged += (_, e) => changes.Add(e);

		gate.SetResult([spotlight]);
		await opening;
		await home.SpotlightLater;
		Assert.Equal("spotlight:test", home.NixieRows[0].Collection.Id); // the timely row, first when no personal one is
		Assert.Equal(first, home.NixieRows.Count);
		Assert.Empty(changes); // rule 7, the re-review: the rows take it in place (none replaced, gone or added)
		Assert.Contains(((await _homeCache.Get("http://server", User)).Snapshot?.NixieRows ?? []), r => r.Collection.Id == "spotlight:test");
	}

	[Fact]
	public async Task ALineupThatCantBeMadeWithTheSpotlight_LeavesHomesRowsAsTheyAre()
	{
		// the re-review: a throw in the lineup with the Spotlight gave an empty lineup, and Home's lineup rows went
		var (_, gate) = HeldSpotlight();
		await Index();
		var home = Home();
		home.LaterBuild = (_, _, _) => gate.Task;
		var opening = home.OnNavigatedTo(new object());
		Assert.True(await Until(() => home.NixieRows.Count > 0));
		var shown = home.NixieRows.Select(r => Key(r.Collection)).ToList();

		gate.SetResult([null!]); // a collection the lineup can't read
		await opening;
		await home.SpotlightLater;

		Assert.Equal(shown, home.NixieRows.Select(r => Key(r.Collection)));
	}

	[Fact]
	public async Task ThisWeek_HasTheWeeksSpotlight_CountedAsOneOfThePagesCollections()
	{
		// stream s moved the Spotlight out of the published builders: This week takes it from the build after the first rows
		_client.Setup(x => x.GetPlaylists(Moq.It.IsAny<CancellationToken>())).ReturnsAsync(new Jellyfin.Sdk.Generated.Models.BaseItemDtoQueryResult { Items = [] });
		var (spotlight, gate) = HeldSpotlight();
		gate.SetResult([spotlight]);
		var index = await Index();
		var page = Page(index);
		page.LaterBuild = (_, _, _) => gate.Task;
		var without = Page(index);
		without.LaterBuild = (_, _, _) => Task.FromResult<IReadOnlyList<SmartCollection>>([]);

		await page.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));
		await without.OnNavigatedTo(new CollectionsPlace(CollectionsSection.MadeByNixie));

		Assert.Contains(page.Rows[0].Tiles, t => t.Nixie!.Id == "spotlight:test");
		Assert.Equal(without.MadeByNixieCount + 1, page.MadeByNixieCount);
	}

	[Fact]
	public async Task WithTheSavedPageShown_TheSwapWaitsForTheSpotlight_SoNoRowIsSwappedTwice()
	{
		// a page saved with the Spotlight: the next visit's lineup without it would swap its row out, then back in
		var (spotlight, gate) = HeldSpotlight();
		using var cache = new Services.HomeCache(Path.Combine(_folder, "HomeCacheOnTheClock"), _messages, new CoreListLogger<Services.HomeCache>(), _clock);
		await Index();
		var first = Home(cache);
		first.LaterBuild = (_, _, _) => Task.FromResult<IReadOnlyList<SmartCollection>>([spotlight]);
		await first.OnNavigatedTo(new object());
		await first.SpotlightLater;
		await first.OnNavigatedFrom();
		Assert.Contains(first.NixieRows, r => r.Collection.Id == "spotlight:test");

		_clock.Advance(TimeSpan.FromMinutes(10)); // the saved page not fresh: its rows asked again behind it
		var next = Home(cache);
		next.LaterBuild = (_, _, _) => gate.Task;
		var painted = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
		next.NixieRows.CollectionChanged += (_, e) => painted.Add(e);
		var opening = next.OnNavigatedTo(new object());
		Assert.True(await Until(() => next.NixieRows.Count > 0));
		await Task.Delay(300); // the behind fill's build is in by now; it waits for the Spotlight
		gate.SetResult([spotlight]);
		await opening;
		await next.SpotlightLater;

		Assert.Equal(first.NixieRows.Select(r => r.Collection.Id), next.NixieRows.Select(r => r.Collection.Id));
		Assert.DoesNotContain(painted, e => e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace);
	}

	[Fact]
	public async Task AVisitShowingAPageSavedBeforeMidnight_MakesTheNewDaysRows_WithTheIntegrationsHook()
	{
		// the review's midnight case: a visit within five minutes of Home's last save shows that page as it is (HomeCache's
		// IsFresh: no section asked again), so across midnight its Nixie rows are yesterday's. RefreshNixieIfNewDay (the hook
		// LoadSections gets at the integration) knows the day this app last made them, whichever visit did
		_settings.SaveSetting(CollectionLineup.HomeRowsKey.Name, 3);
		var index = await Index();
		string[] Rows(HomeViewModel home) => [.. home.NixieRows.Select(r => string.Join(",", r.Collection.ItemIds))];
		var first = Home();
		await first.OnNavigatedTo(new object());
		var today = Rows(first);
		await first.OnNavigatedFrom();

		_clock.Advance(TimeSpan.FromDays(1));
		var next = Home(); // the saved page, saved a moment ago: fresh (no section asked again)
		await next.OnNavigatedTo(new object()); // the re-review: through LoadSections' own hook

		var tomorrow = CollectionLineup.Build(index.Current!, SmartCollectionCatalog.Now(_clock), new HashSet<string>(), new HashSet<SmartCollectionKind>(), [], OutsideListsSnapshot.Empty)
			.Lineup.Picks.Take(3).Select(x => string.Join(",", x.Collection.ItemIds)).ToArray();
		Assert.NotEqual(today, tomorrow);
		Assert.Equal(tomorrow, Rows(next));
	}

	[Fact]
	public async Task TheAnchorsAndEachAnchorsAlikeTitles_AreMadeOnceAnIndex_NotOnceABuild()
	{
		// stream a's personal slots on Home's R and the Collections page: the anchors and "More like"'s alike titles are of the
		// index alone, so a second build of it uses the first one's
		_films[0].UserData = new Jellyfin.Sdk.Generated.Models.UserItemDataDto { Played = true, PlayCount = 1 };
		_films[10].UserData = new Jellyfin.Sdk.Generated.Models.UserItemDataDto { Played = true, PlayCount = 2 };
		var index = await Index();
		var snapshot = index.Current!;
		var now = SmartCollectionCatalog.Now(_clock);
		var one = new FluentFin.Core.SmartCollections.Builders.BuildContext(snapshot, now, new HashSet<string>());
		var two = new FluentFin.Core.SmartCollections.Builders.BuildContext(snapshot, now, new HashSet<string>());
		var a = FluentFin.Core.SmartCollections.Anchors.AnchoredSlots.DataOf(one, FluentFin.Core.SmartCollections.Anchors.IndexAnchorSource.For(one));
		var b = FluentFin.Core.SmartCollections.Anchors.AnchoredSlots.DataOf(two, FluentFin.Core.SmartCollections.Anchors.IndexAnchorSource.For(two));
		Assert.Equal(2, a.Anchors.Count);
		Assert.Same(a.Anchors, b.Anchors);
		var title = a.Anchors[0].Title;
		Assert.NotEmpty(a.LikeOf(title)); // the heists are alike
		Assert.Same(a.LikeOf(title), b.LikeOf(title));
	}

	[Fact]
	public void SeeAllsDirectionButton_IsAnIconButtonBeforeTheGrid_NamedByItsDirection_OnlyInSeeAll()
	{
		var xaml = File.ReadAllText(Path.Combine(HomeAndLibraryXamlTests.RepoRoot, "FluentFin", "Views", "CollectionsPage.xaml"));
		var bar = xaml.IndexOf("x:Name=\"GridDirectionBar\"", StringComparison.Ordinal);
		var button = xaml.IndexOf("x:Name=\"GridDirectionButton\"", StringComparison.Ordinal);
		var grid = xaml.IndexOf("x:Name=\"GridList\"", StringComparison.Ordinal);

		Assert.True(bar > 0 && bar < button && button < grid, "the direction bar and its button come before See all's grid (Tab reaches it first)");
		var markup = xaml[bar..grid];
		Assert.Contains("Visibility=\"{x:Bind ViewModel.IsSeeAll, Mode=OneWay}\"", markup, StringComparison.Ordinal);
		Assert.Contains("AutomationProperties.Name=\"{x:Bind ViewModel.GridDirectionName, Mode=OneWay}\"", markup, StringComparison.Ordinal);
		Assert.Contains("ToolTipService.ToolTip=\"{x:Bind ViewModel.GridDirectionTip, Mode=OneWay}\"", markup, StringComparison.Ordinal);
		Assert.Contains("Glyph=\"{x:Bind ViewModel.GridDirectionGlyph, Mode=OneWay}\"", markup, StringComparison.Ordinal);
		Assert.Contains("Style=\"{StaticResource NixieCompactIconButtonStyle}\"", markup, StringComparison.Ordinal); // one width: no jump (rule 7)
		Assert.DoesNotContain("IsTabStop=\"False\"", markup, StringComparison.Ordinal);
		Assert.Contains("Click=\"GridDirection_Click\"", markup, StringComparison.Ordinal);
	}

	/// <summary>The settings a test sets: values kept as they are.</summary>
	private sealed class LineupSettings : Contracts.Services.ILocalSettingsService
	{
		private readonly Dictionary<string, object?> _values = [];

		public System.Text.Json.Nodes.JsonNode? ReadSettingRaw(string key) => null;

		public T ReadSetting<T>(string key, T defaultValue) => _values.TryGetValue(key, out var v) && v is T t ? t : defaultValue;

		public void SaveSetting<T>(string key, T value) => _values[key] = value;

		public void RemoveSetting(string key) => _values.Remove(key);

		public byte[] GetEntropyBytes() => [];
	}
}
