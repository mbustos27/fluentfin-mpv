using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Phase 3, "because you watched": the critical review's options 3 to 5; the owner: "hold for the rebuild"): one
/// More like a title played in the last 30 days, newest first and none repeating a newer one's titles (R1); a category's
/// reason naming the played titles inside it (R2); a category of filters alone in the row only with two played titles inside
/// (R3); the row reached through what was played lately, holding the most of what the user played, two of a family when
/// both hold played titles; and the More like row leaving out one that opens on a row category's posters.
/// </summary>
[Collection(CollectionNamesSerial.Name)] // the builders read TextMatches.Shared, which is static
public sealed class BecauseYouWatched190Tests
{
	// Thursday 26 November 2026, 10:30 PM in California (the owner's report's "now"): ISO week 48
	private static readonly DateTimeOffset Now = new(2026, 11, 26, 22, 30, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static IndexEntry Show(string name, string[]? genres = null, string? studio = null, double rating = 7.0, bool played = false, int left = 20) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000193"),
		Name = name,
		Kind = IndexKind.Series,
		Year = 2015,
		Genres = genres ?? ["Comedy"],
		Studios = studio is null ? [] : [studio],
		Rating = rating,
		Played = played,
		Unplayed = played ? 0 : left,
		PrimaryTag = "p",
	};

	private static IndexEntry Film(string name, string[]? genres = null, string? studio = null, bool played = false, DateTimeOffset? last = null) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000193"),
		Name = name,
		Kind = IndexKind.Movie,
		Year = 2010,
		Genres = genres ?? ["Comedy"],
		Studios = studio is null ? [] : [studio],
		Rating = 7.0,
		Played = played,
		PlayCount = played || last is not null ? 1 : 0,
		LastPlayed = last,
		PrimaryTag = "p",
		RunTimeTicks = TimeSpan.FromMinutes(100).Ticks,
	};

	// shows no likeness picks out: each of its own studio, comedies like the rest (so a comedy isn't a rare feature)
	private static List<IndexEntry> Filler(int count) => [.. Enumerable.Range(0, count).Select(i => Show($"Filler {i}", ["Comedy"], $"Filler Studio {i}"))];

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries, Dictionary<Guid, SeriesPlays>? plays = null) =>
		new([], [.. entries], plays ?? [], HasPeople: true, Now) { Key = "because-190" };

	private static List<SmartCollection> MoreLikes(IndexSnapshot index) => [.. new MoreLikeBuilder().Build(BuildContext.For(index, Now))];

	// ---- R1: More like, one a title played in the last 30 days ---------------------------------------------------------------

	// a show and the ten unwatched shows that share its production company
	private static (IndexEntry Show, List<IndexEntry> Alike) Circle(string name, bool played = false, int alike = 10, string? studio = null)
	{
		studio ??= $"Circle {name}";
		return (Show(name, studio: studio, played: played), [.. Enumerable.Range(0, alike).Select(i => Show($"{name} alike {i}", studio: studio))]);
	}

	[Fact]
	public void MoreLike_IsOneATitlePlayedInTheLast30Days_NewestFirst_AShowUnderWayToo()
	{
		var underWay = Circle("Under Way"); // 3 of 23 episodes, two days ago: no 1.8 anchor (not finished)
		var finished = Circle("Done Lately", played: true); // finished ten days ago
		var old = Circle("Old Binge"); // under way, last played 45 days ago
		var studio = "Circle Film Lately";
		var film = Film("Film Lately", studio: studio, played: true, last: Now.AddDays(-5));
		var films = Enumerable.Range(0, 10).Select(i => Film($"Film alike {i}", studio: studio)).ToList();
		var plays = new Dictionary<Guid, SeriesPlays>
		{
			[underWay.Show.Id] = new(3, Now.AddDays(-2)),
			[finished.Show.Id] = new(20, Now.AddDays(-10)),
			[old.Show.Id] = new(5, Now.AddDays(-45)),
		};
		var otherFilms = Enumerable.Range(0, 200).Select(i => Film($"Other film {i}", ["Drama"], $"Pictures {i}")); // so the studio is rare among films
		var index = Index([underWay.Show, .. underWay.Alike, finished.Show, .. finished.Alike, old.Show, .. old.Alike, film, .. films, .. otherFilms, .. Filler(300)], plays);

		var likes = MoreLikes(index);

		Assert.Equal(["More like Under Way", "More like Film Lately", "More like Done Lately"], likes.Select(x => x.Title));
		Assert.Equal(underWay.Alike.Select(x => x.Id).Order(), likes[0].ItemIds.Order()); // its own alike, none it started
		Assert.Equal(Now.AddDays(-2), likes[0].AnchoredAt); // the lineup ranks the personal picks by it
	}

	[Fact]
	public void MoreLike_AtMostSix_TheNewest()
	{
		var circles = Enumerable.Range(1, 8).Select(i => Circle($"Show {i}")).ToList();
		var plays = circles.Select((c, i) => (c.Show.Id, Plays: new SeriesPlays(4, Now.AddDays(-(i + 1))))).ToDictionary(x => x.Id, x => x.Plays);

		var likes = MoreLikes(Index([.. circles.SelectMany(c => c.Alike.Prepend(c.Show)), .. Filler(300)], plays));

		Assert.Equal(AnchorFinder.Recent, likes.Count);
		Assert.Equal(circles.Take(6).Select(c => $"More like {c.Show.Name}"), likes.Select(x => x.Title));
	}

	[Fact]
	public void MoreLike_LeavesOutTheTitlesANewerOneShows()
	{
		// two sitcoms of one production company played lately: 30 shows alike both
		var studio = "Shared Company";
		var office = Show("Show One", studio: studio);
		var parks = Show("Show Two", studio: studio);
		var alike = Enumerable.Range(0, 30).Select(i => Show($"Alike {i}", studio: studio, rating: 6 + (i % 4))).ToList();
		var plays = new Dictionary<Guid, SeriesPlays> { [office.Id] = new(30, Now.AddDays(-1)), [parks.Id] = new(12, Now.AddDays(-4)) };

		var likes = MoreLikes(Index([office, parks, .. alike, .. Filler(400)], plays));

		Assert.Equal(["More like Show One", "More like Show Two"], likes.Select(x => x.Title));
		Assert.Empty(likes[0].ItemIds.Intersect(likes[1].ItemIds)); // each opens on its own titles
		Assert.True(likes[1].ItemIds.Count >= MoreLikeBuilder.Least);
	}

	[Fact]
	public void MoreLike_WithNothingPlayedLately_Is18s()
	{
		// a show finished in March: 1.8's anchors and turns, unchanged
		var march = Circle("March Show", played: true);
		var index = Index([march.Show, .. march.Alike, .. Filler(300)], new() { [march.Show.Id] = new(10, Now.AddDays(-250)) });

		Assert.Equal(["More like March Show"], MoreLikes(index).Select(x => x.Title));
	}

	// ---- R2: a category's reason names the played titles inside ---------------------------------------------------------------

	private const string ComedyShows = """{ "id": "comedy-shows", "name": "Comedy Shows", "kind": "shows", "family": "comedy", "rule": { "genresAll": ["Comedy"] }, "reason": "Comedies" }""";
	private const string DramaShows = """{ "id": "drama-shows", "name": "Drama Shows", "kind": "shows", "family": "drama", "rule": { "genresAll": ["Drama"], "genresNone": ["Comedy"] }, "reason": "Dramas" }""";
	private const string ActionFilms = """{ "id": "action-films", "name": "Action Films", "kind": "films", "family": "action", "rule": { "genresAll": ["Action"] }, "reason": "Action" }""";

	[Fact]
	public void ACategorysReason_NamesTwoPlayedTitlesInside_TheLatestPlayedFirst_FactsAboutTheTitlesOnly()
	{
		var newest = Show("Newest Show");
		var middle = Show("Middle Show");
		var oldest = Show("Oldest Show", played: true);
		var comedies = Enumerable.Range(0, 10).Select(i => Show($"Comedy {i}")).ToList();
		var dramas = Enumerable.Range(0, 10).Select(i => Show($"Drama {i}", ["Drama"])).ToList();
		var begun = Film("Begun Film", ["Action"], last: Now.AddDays(-1)); // started yesterday, not finished: not named
		var seen = Film("Seen Film", ["Action"], played: true, last: Now.AddDays(-30));
		var action = Enumerable.Range(0, 10).Select(i => Film($"Action {i}", ["Action"])).ToList();
		var plays = new Dictionary<Guid, SeriesPlays>
		{
			[newest.Id] = new(3, Now.AddDays(-1)),
			[middle.Id] = new(10, Now.AddDays(-5)),
			[oldest.Id] = new(20, Now.AddDays(-90)),
		};
		var index = Index([newest, middle, oldest, .. comedies, .. dramas, begun, seen, .. action], plays);
		var catalogue = MicroGenres.Load(MicroGenreFixture.File(ComedyShows, DramaShows, ActionFilms), MicroGenreFixture.TestMatchable());

		var made = new MicroGenreBuilder(catalogue) { Text = TextMatches.Empty }.Run(BuildContext.For(index, Now)).Collections.ToDictionary(x => x.Id);

		var comedy = made["micro:comedy-shows"].Reason!;
		Assert.Equal(["With “Newest Show” and “Middle Show”", "Comedies"], comedy.Facts); // two at most, the latest played first
		Assert.Equal("With “Newest Show” and “Middle Show” · Comedies · 13 shows, 10 unwatched", comedy.Text);
		Assert.Equal(["Dramas"], made["micro:drama-shows"].Reason!.Facts); // none played: as before
		Assert.Equal(["With “Seen Film”", "Action"], made["micro:action-films"].Reason!.Facts); // a film begun isn't played
		foreach (var reason in made.Values.Select(x => x.Reason!.Text))
		{
			Assert.DoesNotMatch(@"\b([Yy]ou|[Yy]our)\b", reason); // facts about titles, never the viewer
		}
	}

	// the Phase 3 review: "With You and Your Lie in April" read as about the viewer, and a summary category's "From each title's
	// summary" went past a narrow tile's two lines behind the names
	[Fact]
	public void ACategorysPlayedTitles_AreNamedInQuotes_AfterFromEachTitlesSummary()
	{
		var you = Show("You", ["Drama"]);
		var lie = Show("Your Lie in April", ["Drama"]);
		var dramas = Enumerable.Range(0, 10).Select(i => Show($"Stalker drama {i}", ["Drama"])).ToList();
		var plays = new Dictionary<Guid, SeriesPlays> { [you.Id] = new(10, Now.AddDays(-1)), [lie.Id] = new(5, Now.AddDays(-2)) };
		var index = Index([you, lie, .. dramas], plays);
		var obsession = new MicroGenre("tv-obsession", "Series About Obsession", IndexKind.Series, "obsession", null,
			new MicroGenreRule { GenresAll = ["Drama"], TextAny = ["obsession"] }, "Drama whose summary mentions obsession");
		var text = new TextMatches(index.Key, "test", Now, new List<IndexEntry>([you, lie, .. dramas]).ToDictionary(x => x.Id, _ => (string[]?)["tv-obsession"]))
		{
			CompleteFor = new HashSet<IndexKind> { IndexKind.Series },
		};

		var made = Assert.Single(new MicroGenreBuilder(new MicroGenreCatalogue([obsession], [])) { Text = text }.Run(BuildContext.For(index, Now)).Collections);

		Assert.Equal([TextPass.ReasonFact, "With “You” and “Your Lie in April”"], made.Reason!.Facts);
		Assert.Equal("With “You” and “Your Lie in April”", ReasonFacts.With(["You", "Your Lie in April"]));
	}

	[Fact]
	public void ReasonFacts_With_NamesAtMostTwo_EachOnce()
	{
		Assert.Equal("With “Cowboy Bebop” and “Trigun”", ReasonFacts.With(["Cowboy Bebop", "Trigun", "Planetes"]));
		Assert.Equal("With “Monster”", ReasonFacts.With(["Monster", " ", null, "Monster"]));
		Assert.Equal("", ReasonFacts.With([]));
	}

	// ---- the lineup: R3, the row reached through what was played, two of a family, the More like row -------------------------

	private sealed class Shelf
	{
		public List<IndexEntry> Library { get; } = Filler(300);

		public List<SmartCollection> Collections { get; } = [];

		public List<MicroGenre> Catalogue { get; } = [];

		public Dictionary<Guid, SeriesPlays> Plays { get; } = [];

		public Dictionary<string, List<IndexEntry>> TitlesOf { get; } = [];

		/// <summary>A show category of <paramref name="count"/> unwatched shows and the played ones given; filters alone or a keyword's.</summary>
		public SmartCollection Add(string id, int count = 12, bool filtersOnly = false, string? family = null, double rating = 7.0, params IndexEntry[] played)
		{
			var titles = Enumerable.Range(0, count).Select(i => Show($"{id} {i}", rating: rating)).ToList();
			Library.AddRange(titles);
			TitlesOf[id] = titles;
			List<IndexEntry> all = [.. titles, .. played];
			var made = new SmartCollection($"micro:{id}", SmartCollectionKind.MicroGenre, $"Category {id}", "", false, [.. all.Select(x => x.Id)]) { Only = IndexKind.Series };
			Collections.Add(made);
			Catalogue.Add(new MicroGenre(id, $"Category {id}", IndexKind.Series, family ?? id, null,
				filtersOnly ? new MicroGenreRule { GenresAll = ["Comedy"] } : new MicroGenreRule { GenresAll = ["Comedy"], KeywordsAny = ["heist"] }, "Comedy"));
			return made;
		}

		/// <summary>A played show (in the library once), <paramref name="episodes"/> played, the last <paramref name="daysAgo"/> days ago.</summary>
		public IndexEntry Played(string name, int episodes, int daysAgo)
		{
			var show = Show(name);
			Library.Add(show);
			Plays[show.Id] = new SeriesPlays(episodes, Now.AddDays(-daysAgo));
			return show;
		}

		public LineupData Data(IReadOnlyDictionary<string, CategoryWeek>? history = null) => LineupData.Of(Library, Plays) with
		{
			Catalogue = new MicroGenreCatalogue(Catalogue, []),
			CategoryHistory = history ?? new Dictionary<string, CategoryWeek>(StringComparer.Ordinal),
		};

		public Lineup Assemble(IReadOnlyDictionary<string, CategoryWeek>? history = null, IEnumerable<SmartCollection>? more = null, DateTimeOffset? now = null) =>
			CollectionLineup.Assemble(CollectionLineup.Candidates([.. Collections, .. more ?? []]), Data(history), now ?? Now);
	}

	private static IReadOnlyList<string> Ids(IEnumerable<SmartCollection> collections) => [.. collections.Select(x => x.Id)];

	[Fact]
	public void ACategoryOfFiltersAlone_IsInTheRowOnlyWithTwoPlayedTitlesInside_SeeAllAsBefore()
	{
		var shelf = new Shelf();
		var binge = shelf.Played("Binge", 30, 100);
		shelf.Add("one-played", filtersOnly: true, played: [binge]); // the report's "Historical Mini-Series", one played show inside
		shelf.Add("two-played", filtersOnly: true, played: [shelf.Played("First", 12, 120), shelf.Played("Second", 12, 130)]);
		shelf.Add("words");
		shelf.Add("more-words");
		shelf.Add("filters-none", filtersOnly: true);

		var lineup = shelf.Assemble();

		Assert.DoesNotContain("micro:one-played", Ids(lineup.Categories));
		Assert.DoesNotContain("micro:filters-none", Ids(lineup.Categories));
		Assert.Contains("micro:two-played", Ids(lineup.Categories));
		Assert.Contains("micro:words", Ids(lineup.Categories));
		Assert.Contains("micro:one-played", lineup.CategoryRanking); // See all keeps cut 1's rule (a lift of 1.5 or more)
		Assert.NotNull(lineup.Explore);
		Assert.False(CollectionLineup.FiltersOnly(shelf.Data().Catalogue.Of(lineup.Explore))); // never one of filters alone
		Assert.Equal(2, lineup.CategoryCounts!.FiltersOnly);
	}

	[Fact]
	public void TheRow_OpensOnTheCategoryReachedThroughTheNewestPlayedTitle_WhereOneFits()
	{
		var shelf = new Shelf();
		var newest = shelf.Played("Newest", 3, 2);
		var third = shelf.Played("Third", 1, 300);
		// binged long ago, in a small category of the best titles: 1.9's order put it first, the week's draw or not
		shelf.Add("binge", count: 10, rating: 9.0, played: [shelf.Played("Old Binge", 40, 200), shelf.Played("Older Binge", 40, 250)]);
		shelf.Add("pair", count: 20, rating: 5.0, played: [newest, third]); // through Newest, with Third
		shelf.Add("lone", count: 12, rating: 5.0, played: [newest]); // Newest alone: that show, not a taste
		for (var i = 0; i < 4; i++)
		{
			shelf.Add($"other-{i}");
		}

		var lineup = shelf.Assemble();

		Assert.Equal("micro:pair", lineup.Categories[0].Id);
		Assert.Equal("micro:pair", lineup.Picks.First(x => x.Slot == LineupSlot.Category).Collection.Id); // This week's first category
		Assert.Equal(lineup.Explore, lineup.Categories[1].Id); // the explore pick second, as before
		// the rest of what was played after them, before the categories nothing played is in
		var row = Ids(lineup.Categories).Where(x => x != lineup.Explore).ToList();
		Assert.Contains("micro:binge", row);
		Assert.All(row.Where(x => x.StartsWith("micro:other-", StringComparison.Ordinal)), x => Assert.True(row.IndexOf("micro:binge") < row.IndexOf(x)));
		Assert.Equal(1, lineup.CategoryCounts!.Anchored);
	}

	[Fact]
	public void ACategoryOfTheUsersTaste_StaysNextWeek_FillersOneShowCategoriesAndTheExplorePickRotate()
	{
		var shelf = new Shelf();
		var newest = shelf.Played("Newest", 3, 1);
		shelf.Add("pair", played: [newest, shelf.Played("Another", 5, 220)]); // through a title played yesterday
		shelf.Add("old-pair", played: [shelf.Played("Older", 8, 200), shelf.Played("Older still", 5, 300)]); // two played long ago: their taste
		shelf.Add("one-show", played: [shelf.Played("Lone", 8, 250)]); // one played title: rotates
		for (var i = 0; i < 14; i++)
		{
			shelf.Add($"other-{i}");
		}

		var lastWeek = SpotlightWeek.Of(Now).Before(1).Seed;
		var history = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal)
		{
			[lastWeek] = new CategoryWeek(["micro:pair", "micro:other-0", "micro:old-pair", "micro:one-show"], "micro:other-0"),
		};

		var lineup = shelf.Assemble(history);
		var row = Ids(lineup.Categories);

		Assert.Equal("micro:pair", row[0]); // still what Newest is in
		Assert.Contains("micro:old-pair", row); // the user's taste stays, the week after too
		Assert.DoesNotContain("micro:one-show", row); // the rest of last week rotates while others are left
		Assert.DoesNotContain("micro:other-0", row);
		Assert.NotNull(lineup.Explore);
		Assert.NotEqual("micro:other-0", lineup.Explore); // a new explore pick
		Assert.Equal(2, lineup.CategoryCounts!.Recent); // the two left out
	}

	[Fact]
	public void TwoOfAFamily_WhenEachHoldsPlayedTitles_TheRestOneAFamily()
	{
		var shelf = new Shelf();
		shelf.Add("sitcoms-a", family: "sitcom", played: [shelf.Played("A1", 10, 100), shelf.Played("A2", 10, 110), shelf.Played("A3", 10, 120)]);
		shelf.Add("sitcoms-b", family: "sitcom", played: [shelf.Played("B1", 10, 130), shelf.Played("B2", 10, 140)]);
		shelf.Add("sitcoms-c", family: "sitcom", played: [shelf.Played("C1", 10, 150)]);
		shelf.Add("sitcoms-d", family: "sitcom");
		for (var i = 0; i < 4; i++)
		{
			shelf.Add($"other-{i}");
		}

		var row = Ids(shelf.Assemble().Categories);

		Assert.Contains("micro:sitcoms-a", row);
		Assert.Contains("micro:sitcoms-b", row);
		Assert.DoesNotContain("micro:sitcoms-c", row); // a third of the family
		Assert.DoesNotContain("micro:sitcoms-d", row);
	}

	// the Phase 3 review (item 10): a week kept holding two of one family (each with played titles) holds them both, in its
	// order, when the page builds again mid-week; one a family there put the second after the rest
	[Fact]
	public void AKeptWeekWithTwoOfAFamily_HoldsBoth_InItsOrder_MidWeek()
	{
		var shelf = new Shelf();
		shelf.Add("sitcoms-a", family: "sitcom", played: [shelf.Played("A1", 10, 100), shelf.Played("A2", 10, 110)]);
		shelf.Add("sitcoms-b", family: "sitcom", played: [shelf.Played("B1", 10, 130)]);
		for (var i = 0; i < 12; i++)
		{
			shelf.Add($"other-{i:D2}");
		}

		var kept = new CategoryWeek(["micro:sitcoms-b", "micro:sitcoms-a", "micro:other-05", "micro:other-03"], null);
		var history = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal) { [SpotlightWeek.Of(Now).Seed] = kept };

		var lineup = shelf.Assemble(history, now: Now.AddDays(1)); // Friday: the same ISO week

		Assert.Equal(kept.Ids, lineup.WeekOrder.Take(4));
		Assert.True(lineup.CategoryCounts!.Kept);
	}

	[Fact]
	public void TheMoreLikeRow_IsNewestFirst_LeavingOutOneThatOpensOnARowCategorysPosters()
	{
		var shelf = new Shelf();
		var category = shelf.Add("words", played: [shelf.Played("P1", 4, 2), shelf.Played("P2", 4, 3)]);
		for (var i = 0; i < 3; i++)
		{
			shelf.Add($"other-{i}");
		}

		SmartCollection Like(string name, IReadOnlyList<IndexEntry> titles, int daysAgo) =>
			new($"like:{name}", SmartCollectionKind.MoreLike, $"More like {name}", "", false, [.. titles.Select(x => x.Id)]) { InListOrder = true, AnchoredAt = Now.AddDays(-daysAgo) };
		List<IndexEntry> Titles(string prefix)
		{
			var titles = Enumerable.Range(0, 12).Select(i => Show($"{prefix} {i}")).ToList();
			shelf.Library.AddRange(titles);
			return titles;
		}

		var same = Like("Same", shelf.TitlesOf["words"], 1); // the category's own first posters
		var fresh = Like("Fresh", Titles("Fresh"), 2);
		var older = Like("Older", Titles("Older"), 20);

		var lineup = shelf.Assemble(more: [older, same, fresh]);

		Assert.Contains(category.Id, Ids(lineup.Categories));
		Assert.Equal(["like:Fresh", "like:Older"], lineup.MoreLike);
	}
}
