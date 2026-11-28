using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (Collections 2, stream lineup): the Category slot. Up to 2 categories in This week, the week's order (the
/// Categories row of about 12) by how near each is to what the user watched, one a family, none of the three weeks before,
/// one picked to explore, each kind's own ceiling, never beside its parent keyword collection, Home unchanged, and 1.8's
/// promises kept (the same titles in the same order wherever a collection shows).
/// </summary>
[Collection(CollectionNamesSerial.Name)] // 1.9 (the pre-release fix A): the week's keeping reads TextMatches.Shared, which is static
public sealed class CategoryLineup190Tests
{
	// Wednesday 25 November 2026 in California: ISO week 48 (Monday 23 to Sunday 29)
	private static readonly DateTimeOffset Wednesday = new(2026, 11, 25, 20, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static IndexEntry Title(string name, IndexKind kind = IndexKind.Movie, double rating = 7.0, bool played = false) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000190"),
		Name = name,
		Kind = kind,
		Year = 2000,
		Genres = ["Drama"],
		Rating = rating,
		TagCount = 10,
		Played = played,
		Unplayed = kind == IndexKind.Series ? 20 : null,
		PrimaryTag = "p",
		RunTimeTicks = TimeSpan.FromMinutes(100).Ticks,
	};

	private static List<IndexEntry> Titles(string prefix, int count, IndexKind kind = IndexKind.Movie, int played = 0) =>
		[.. Enumerable.Range(0, count).Select(i => Title($"{prefix} {i}", kind, played: i < played))];

	private static SmartCollection Category(string id, IReadOnlyList<IndexEntry> titles) =>
		new($"micro:{id}", SmartCollectionKind.MicroGenre, $"Category {id}", "", false, [.. titles.Select(x => x.Id)]) { Only = titles[0].Kind };

	private static SmartCollection Of(string id, SmartCollectionKind kind, IReadOnlyList<IndexEntry> titles) => new(id, kind, id, "", false, [.. titles.Select(x => x.Id)]);

	// a catalogue of the categories' families and parents. 1.9 (Phase 3): each rule names a keyword (the lineup reads a rule only
	// for R3, which keeps a category of filters alone out of the row without two played titles inside: not these tests' subject)
	private static MicroGenreCatalogue Catalogue(params (string Id, string Family, string? Parent)[] categories) =>
		new([.. categories.Select(c => new MicroGenre(c.Id, $"Category {c.Id}", IndexKind.Movie, c.Family, c.Parent, new MicroGenreRule { GenresAll = ["Drama"], KeywordsAny = ["heist"] }, "Drama"))], []);

	// n categories of ten titles each, each its own family unless given
	private sealed class Shelf
	{
		public List<IndexEntry> Library { get; } = [.. Titles("Filler", 300)];

		public List<SmartCollection> Collections { get; } = [];

		public List<(string Id, string Family, string? Parent)> Families { get; } = [];

		public Dictionary<string, List<IndexEntry>> TitlesOf { get; } = [];

		public SmartCollection Add(string id, string? family = null, string? parent = null, IndexKind kind = IndexKind.Movie, int count = 10)
		{
			var titles = Titles(id, count, kind);
			Library.AddRange(titles);
			TitlesOf[id] = titles;
			var made = Category(id, titles);
			Collections.Add(made);
			Families.Add((id, family ?? id, parent));
			return made;
		}

		public LineupData Data(IReadOnlyDictionary<Guid, SeriesPlays>? plays = null, IReadOnlyDictionary<string, CategoryWeek>? history = null) =>
			LineupData.Of(Library, plays) with
			{
				Catalogue = Catalogue([.. Families]),
				CategoryHistory = history ?? new Dictionary<string, CategoryWeek>(StringComparer.Ordinal),
			};

		public Lineup Assemble(DateTimeOffset? now = null, IReadOnlyDictionary<Guid, SeriesPlays>? plays = null, IReadOnlyDictionary<string, CategoryWeek>? history = null,
			IEnumerable<SmartCollection>? more = null) =>
			CollectionLineup.Assemble(CollectionLineup.Candidates([.. Collections, .. more ?? []]), Data(plays, history), now ?? Wednesday);
	}

	private static IReadOnlyList<string> Ids(IEnumerable<SmartCollection> collections) => [.. collections.Select(x => x.Id)];

	private static IReadOnlyList<string> CategoriesIn(Lineup lineup) => [.. lineup.Picks.Where(x => x.Slot == LineupSlot.Category).Select(x => x.Collection.Id)];

	[Fact]
	public void ACategory_FillsTheCategorySlot_ThisWeekTakesTwoAtMost_TheRowTwelve()
	{
		var shelf = new Shelf();
		for (var i = 0; i < 20; i++)
		{
			shelf.Add($"c{i:D2}");
		}

		var lineup = shelf.Assemble();

		Assert.Equal(LineupSlot.Category, CollectionLineup.SlotOf(shelf.Collections[0]));
		Assert.Equal(CollectionLineup.CategoriesInWeek, CategoriesIn(lineup).Count);
		Assert.Equal(CollectionLineup.CategoryRow, lineup.Categories.Count);
		Assert.Equal(CategoriesIn(lineup), Ids(lineup.Categories.Take(2))); // This week's are the row's first
		Assert.Equal(new LineupSlotCount(20, 20, 20, 20, 20, 20, 2), lineup.Counts[LineupSlot.Category]);
		Assert.Equal(LineupRefresh.Weekly, CollectionLineup.Refresh(LineupSlot.Category));
	}

	[Fact]
	public void AShowCategory_IsJudgedAgainstTheShows_OnePercentOfThem()
	{
		// 3,000 shows and 6,000 films: the films' ceiling is 60, the shows' 30. A show category of 45 unwatched is a filter here,
		// though 1.8's theme ceiling (of the films) would have let it in
		var shelf = new Shelf();
		shelf.Library.AddRange(Titles("Film filler", 6_000 - 300 - 60));
		shelf.Library.AddRange(Titles("Show filler", 3_000 - 75, IndexKind.Series));
		shelf.Add("wide-shows", kind: IndexKind.Series, count: 45);
		shelf.Add("narrow-shows", kind: IndexKind.Series, count: 30);
		shelf.Add("films", count: 60);

		var lineup = shelf.Assemble();

		Assert.Equal(["micro:films", "micro:narrow-shows"], Ids(lineup.Categories).Order(StringComparer.Ordinal));
		Assert.Equal(3, lineup.Counts[LineupSlot.Category].Floor);
		Assert.Equal(2, lineup.Counts[LineupSlot.Category].Ceiling);
	}

	[Fact]
	public void TheCategoriesTheUserWatchedOf_ComeFirst_ShowsWithPlayedEpisodesCountMost()
	{
		var shelf = new Shelf();
		shelf.Library.AddRange(Titles("Show filler", 200, IndexKind.Series));
		var binge = shelf.Add("binge", kind: IndexKind.Series);
		shelf.Add("other-shows", kind: IndexKind.Series);
		for (var i = 0; i < 8; i++)
		{
			shelf.Add($"films-{i}");
		}

		var watched = shelf.TitlesOf["binge"];
		var plays = new Dictionary<Guid, SeriesPlays> { [watched[0].Id] = new(40, Wednesday.AddDays(-1)), [watched[1].Id] = new(30, Wednesday.AddDays(-2)) };

		// every week of a year: the category the user binged leads; with no history it leads only by the draw's chance
		var led = 0;
		var ledBefore = 0;
		for (var w = 0; w < 52; w++)
		{
			var now = Wednesday.AddDays(7 * w);
			led += shelf.Assemble(now, plays).Categories[0].Id == binge.Id ? 1 : 0;
			ledBefore += shelf.Assemble(now).Categories[0].Id == binge.Id ? 1 : 0;
		}

		Assert.Equal(52, led);
		Assert.InRange(ledBefore, 0, 15); // one of ten
		var data = shelf.Data(plays);
		Assert.True(data.Taste.LiftOf(watched.Select(x => x.Id), IndexKind.Series, data.Shows) > 1);
		Assert.True(data.Taste.LiftOf(shelf.TitlesOf["other-shows"].Select(x => x.Id), IndexKind.Series, data.Shows) < 1);
		Assert.Equal(Taste.ShowMost, data.Taste.WeightOf(watched[0].Id)); // 40 episodes: the most a show weighs
	}

	[Fact]
	public void TheRow_StartsWithThisWeeksCategories_OneTheNoveltyRulesPassedOverComesAfter()
	{
		// the nearest category (two of its films played) is nearly the Spotlight's titles: This week takes the Spotlight first
		// (its prior is higher) and leaves the category out as a near copy; the row still has it, after This week's two
		var shelf = new Shelf();
		var near = shelf.Add("near", count: 12);
		for (var i = 0; i < 5; i++)
		{
			shelf.Add($"c{i}");
		}

		var titles = shelf.TitlesOf["near"];
		var played = titles.Take(2).Select(x => x.Id).ToHashSet();
		for (var i = 0; i < shelf.Library.Count; i++)
		{
			shelf.Library[i] = played.Contains(shelf.Library[i].Id) ? shelf.Library[i] with { Played = true } : shelf.Library[i];
		}

		var spotlight = Of("spotlight:studio:near", SmartCollectionKind.Spotlight, titles);
		var lineup = shelf.Assemble(more: [spotlight]);

		Assert.Contains(lineup.Picks, x => x.Collection.Id == spotlight.Id);
		Assert.DoesNotContain(near.Id, CategoriesIn(lineup));
		Assert.Equal(2, CategoriesIn(lineup).Count);
		Assert.Equal(CategoriesIn(lineup), Ids(lineup.Categories.Take(2)));
		Assert.Equal(near.Id, lineup.Categories[2].Id); // first in the week's order, third in the row
		Assert.Equal(near.Id, CollectionLineup.Assemble(CollectionLineup.Candidates(shelf.Collections), shelf.Data(), Wednesday).Categories[0].Id); // without the Spotlight: first
	}

	[Fact]
	public void OneCategoryAFamily_InTheRowAndInThisWeek()
	{
		var shelf = new Shelf();
		for (var i = 0; i < 6; i++)
		{
			shelf.Add($"crime-{i}", family: "crime");
		}

		shelf.Add("western", family: "western");
		shelf.Add("romance", family: "romance");

		var lineup = shelf.Assemble();

		Assert.Equal(3, lineup.Categories.Count);
		Assert.Single(lineup.Categories, x => x.Id.StartsWith("micro:crime-", StringComparison.Ordinal));
		Assert.Equal(2, CategoriesIn(lineup).Select(id => id.StartsWith("micro:crime-", StringComparison.Ordinal) ? "crime" : id).Distinct().Count());
		Assert.Equal(new CategoryCounts(8, 3, 0, false), lineup.CategoryCounts);
	}

	[Fact]
	public void ACategory_IsNeverBesideItsParentKeywordCollection_InThisWeek()
	{
		var shelf = new Shelf();
		var neoNoir = shelf.Add("neo-noir-1990s", parent: "keyword:neonoir");
		var themeTitles = Titles("Neo-noir", 12);
		shelf.Library.AddRange(themeTitles);
		var theme = Of("keyword:neonoir", SmartCollectionKind.Keyword, themeTitles);
		var other = Of("keyword:heist", SmartCollectionKind.Keyword, [.. Titles("Heist", 12).Also(shelf.Library)]);

		var lineup = shelf.Assemble(more: [theme, other]);
		var withoutParent = new Shelf();
		withoutParent.Library.AddRange([.. shelf.Library]);
		withoutParent.Collections.Add(neoNoir);
		withoutParent.Families.Add(("neo-noir-1990s", "noir", null));
		var unlinked = withoutParent.Assemble(more: [theme, other]);

		Assert.Contains(neoNoir.Id, CategoriesIn(lineup)); // the category, just above the theme
		Assert.DoesNotContain(lineup.Picks, x => x.Collection.Id == theme.Id); // its parent left out of This week
		Assert.Contains(lineup.Picks, x => x.Collection.Id == other.Id); // another theme stays
		Assert.Contains(unlinked.Picks, x => x.Collection.Id == theme.Id); // no parent: both (their titles share nothing)
	}

	[Fact]
	public void NoCategoryAgainWithinFourWeeks_WhileOthersAreLeft_ThenTheLeastRecentlyShown()
	{
		var shelf = new Shelf();
		for (var i = 0; i < 16; i++)
		{
			shelf.Add($"c{i:D2}");
		}

		var week = SpotlightWeek.Of(Wednesday);
		var first = shelf.Assemble(); // a first week: no history
		var history = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal) { [week.Before(1).Seed] = first.CategoryWeek! };

		var next = shelf.Assemble(history: history);

		// 16 categories, 12 shown last week: this week's row is the 4 others first, then the least recent; This week only fresh
		Assert.Equal(16 - 12, Ids(next.Categories).Count(id => !first.Categories.Any(x => x.Id == id)));
		Assert.Equal(Ids(next.Categories).Take(4).Order(StringComparer.Ordinal), shelf.Collections.Select(x => x.Id).Except(Ids(first.Categories)).Order(StringComparer.Ordinal));
		Assert.All(CategoriesIn(next), id => Assert.DoesNotContain(id, Ids(first.Categories)));
		Assert.Equal(12, next.CategoryCounts!.Recent);

		// shown three weeks before: still left out; four weeks before: back
		var older = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal) { [week.Before(3).Seed] = first.CategoryWeek!, [week.Before(4).Seed] = new(["micro:c00", "micro:c01"]) };
		var later = shelf.Assemble(history: older);
		Assert.All(CategoriesIn(later), id => Assert.DoesNotContain(id, Ids(first.Categories)));
		Assert.Equal(Ids(first.Categories).Count, later.CategoryCounts!.Recent); // the fourth week back is no longer recent

		// last week's and the week before's, all of them: the one shown two weeks ago comes back before last week's
		var all = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal)
		{
			[week.Before(1).Seed] = new([.. shelf.Collections.Skip(1).Select(x => x.Id)]),
			[week.Before(2).Seed] = new([shelf.Collections[0].Id]),
		};
		Assert.Equal(shelf.Collections[0].Id, shelf.Assemble(history: all).Categories[0].Id);
	}

	[Fact]
	public void OneCategoryAWeek_IsPickedToExplore_SecondInTheRow_NothingTheUserWatchedPointsAtIt()
	{
		var shelf = new Shelf();
		shelf.Library.AddRange(Titles("Show filler", 200, IndexKind.Series));
		shelf.Add("binge-a", family: "sitcom", kind: IndexKind.Series);
		shelf.Add("binge-b", family: "anime", kind: IndexKind.Series);
		for (var i = 0; i < 6; i++)
		{
			shelf.Add($"films-{i}", family: $"film-{i}");
		}

		var plays = new Dictionary<Guid, SeriesPlays>
		{
			[shelf.TitlesOf["binge-a"][0].Id] = new(40, Wednesday.AddDays(-1)),
			[shelf.TitlesOf["binge-b"][0].Id] = new(25, Wednesday.AddDays(-1)),
		};

		for (var w = 0; w < 12; w++)
		{
			var lineup = shelf.Assemble(Wednesday.AddDays(7 * w), plays);
			var data = shelf.Data(plays);
			Assert.NotNull(lineup.Explore);
			Assert.Equal(lineup.Explore, lineup.Categories[1].Id);
			Assert.StartsWith("micro:binge-", lineup.Categories[0].Id, StringComparison.Ordinal); // the nearest first
			Assert.StartsWith("micro:films-", lineup.Explore, StringComparison.Ordinal); // no binge: both are lifted
			Assert.True(data.Taste.LiftOf(shelf.TitlesOf[lineup.Explore!["micro:".Length..]].Select(x => x.Id), IndexKind.Movie, data.Films) <= 1 + 1e-9);
			Assert.Equal(lineup.Categories.Take(2).Select(x => x.Id), CategoriesIn(lineup)); // This week: the nearest and the explore pick
		}
	}

	[Fact]
	public void TheWeeksCategories_HoldAllWeek_EvenAsWhatTheUserWatchesChanges_AndNextWeekAreOthers()
	{
		var shelf = new Shelf();
		shelf.Library.AddRange(Titles("Show filler", 200, IndexKind.Series));
		for (var i = 0; i < 30; i++)
		{
			shelf.Add($"c{i:D2}", kind: i % 2 == 0 ? IndexKind.Series : IndexKind.Movie);
		}

		var kept = new Dictionary<string, CategoryWeek>(StringComparer.Ordinal);
		var week = SpotlightWeek.Of(Wednesday);
		var monday = Wednesday.AddDays(-2);
		var first = shelf.Assemble(monday, history: kept);
		kept[week.Seed] = first.CategoryWeek!; // as KeepWeek does

		// Friday: the user binged two of the shows of a category not in the row: the row holds, its order too
		var outside = shelf.Collections.First(x => !first.Categories.Contains(x) && x.Only == IndexKind.Series);
		var binged = shelf.TitlesOf[outside.Id["micro:".Length..]];
		var plays = new Dictionary<Guid, SeriesPlays> { [binged[0].Id] = new(60, monday.AddDays(4)), [binged[1].Id] = new(45, monday.AddDays(3)) };
		var friday = shelf.Assemble(monday.AddDays(4), plays, kept);
		Assert.Equal(Ids(first.Categories), Ids(friday.Categories));
		Assert.Equal(first.Explore, friday.Explore);
		Assert.True(friday.CategoryCounts!.Kept);
		Assert.Equal(CategoriesIn(first), CategoriesIn(friday));

		// without the week kept, Friday's evidence would have moved it
		Assert.Equal(outside.Id, shelf.Assemble(monday.AddDays(4), plays).Categories[0].Id);

		// next Monday: none of last week's while others are left
		var next = shelf.Assemble(monday.AddDays(7), plays, kept);
		Assert.Empty(CategoriesIn(next).Intersect(Ids(first.Categories)));
		Assert.Empty(Ids(next.Categories).Take(30 - 12).Intersect(Ids(first.Categories)));
	}

	// the weeks' categories as the store keeps them, and who asked
	private sealed class Weeks : ICategoryWeeks
	{
		public Dictionary<string, CategoryWeek> Kept { get; } = new(StringComparer.Ordinal);

		public List<string?> Asked { get; } = [];

		public Task<IReadOnlyDictionary<string, CategoryWeek>> CategoryWeeksAsync(string? of = null)
		{
			Asked.Add(of);
			return Task.FromResult<IReadOnlyDictionary<string, CategoryWeek>>(new Dictionary<string, CategoryWeek>(Kept, StringComparer.Ordinal));
		}

		public Task SaveCategoryWeek(string week, CategoryWeek shown, string? of = null, long? since = null)
		{
			Kept[week] = shown;
			return Task.CompletedTask;
		}
	}

	[Fact]
	public async Task ThePagesBuild_KeepsTheWeeksCategories_OnceThePlaysAreIn_AndTheWeeksNextBuildHoldsThem()
	{
		// the shipped catalogue over a library of the survey's shape: Home's and the Collections page's own build; the summaries'
		// pass has read the shows (the pre-release fix A: no week is kept before it)
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 900, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-a" };
		var weeks = new Weeks();
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;
		TextMatches.Put(ShowsRead(index));
		try
		{
			await KeepsTheWeek(index, weeks, empty);
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	// 1.9 (the index's load, the review of wip/190-index, item 4): a merged read of what changed whose removals are due (titles
	// the server removed that the index still has) keeps no week: chosen with them, the week would hold them; the day's read
	// of every title (RemovalsDue off) keeps it
	[Fact]
	public async Task AMergedReadWhoseRemovalsAreDue_KeepsNoWeek_TheDaysReadDoes()
	{
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 900, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-a" };
		var weeks = new Weeks();
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;
		TextMatches.Put(ShowsRead(index));
		try
		{
			var merged = await CollectionLineup.BuildAsync(index with { RemovalsDue = true, TitlesAt = Wednesday.AddHours(3) }, Wednesday, outside: empty, weeks: weeks);
			Assert.NotEmpty(merged.Lineup.Categories);
			Assert.Empty(weeks.Kept);

			var day = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			Assert.Equal(day.Lineup.WeekOrder, weeks.Kept["2026-W48"].Ids);
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	private static async Task KeepsTheWeek(IndexSnapshot index, Weeks weeks, FluentFin.Core.Outside.OutsideListsSnapshot empty)
	{
		// the played episodes still due, or a first read's part: shown, not kept (a week chosen without what the user watched
		// would hold against it all week)
		var due = await CollectionLineup.BuildAsync(index with { PlaysDue = true }, Wednesday, outside: empty, weeks: weeks);
		var part = await CollectionLineup.BuildAsync(index with { Reading = new IndexProgress(100, 4_900, null) }, Wednesday, outside: empty, weeks: weeks);
		Assert.NotEmpty(due.Lineup.Categories);
		Assert.NotEmpty(part.Lineup.Categories);
		Assert.Empty(weeks.Kept);

		var made = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
		Assert.Equal(CollectionLineup.CategoryRow, made.Lineup.Categories.Count);
		Assert.Equal(made.Lineup.WeekOrder, weeks.Kept["2026-W48"].Ids); // the week's order (the row as shown puts This week's first)
		Assert.Equal(made.Lineup.Explore, weeks.Kept["2026-W48"].Explore);
		Assert.All(weeks.Asked, x => Assert.Equal("key-a", x)); // the index's own sign-in's

		// Friday's build of the day's index, a show binged meanwhile: the week's categories hold
		var show = index.Entries.First(x => x.Kind == IndexKind.Series && !made.Lineup.Categories.Any(c => c.ItemIds.Contains(x.Id)));
		var friday = index with { SeriesPlays = new Dictionary<Guid, SeriesPlays> { [show.Id] = new(60, Wednesday.AddDays(2)) } };
		var again = await CollectionLineup.AssembleAsync(friday, made.All, [], Wednesday.AddDays(2), outside: empty, weeks: weeks);
		Assert.Equal(Ids(made.Lineup.Categories), Ids(again.Lineup.Categories));
		Assert.True(again.Lineup.CategoryCounts!.Kept);

		// next week: none of this week's in This week
		var next = await CollectionLineup.AssembleAsync(friday, made.All, [], Wednesday.AddDays(7), outside: empty, weeks: weeks);
		Assert.Empty(CategoriesIn(next.Lineup).Intersect(Ids(made.Lineup.Categories)));
		Assert.Equal(["2026-W48", "2026-W49"], weeks.Kept.Keys.Order(StringComparer.Ordinal));
	}

	// ---- the pre-release fix G (BH-190-M1): show categories rank on equal footing with film categories ----------------------

	[Fact]
	public void WithNoHistory_ShowCategories_ComeUpInProportion_ThoughShowsHaveNoCriticsRatings()
	{
		// the survey's shape: two films in three have a critics' rating, no show has one, so a show's score is its kind's mean
		// and every show category stood at 0.9 against film categories up to 1. The summaries' pass gives each summary category
		// 10 to 20 shows (BH-190's probe): about 40 % of the categories past the gates are shows, and 2 of For you's first 12 were
		var library = MicroGenreFixture.Library(25_877, 4_975, seed: 7).Select(x => x with { Played = false }).ToList();
		var index = new IndexSnapshot([], library, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-footing" };
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;
		var shows = library.Where(x => x.Kind == IndexKind.Series).ToList();
		var withWords = MicroGenres.Shipped.Categories.Where(x => x.Rule.WaitsForText).ToList();
		var any = new TextMatches(index.Key, "test", Wednesday, shows.ToDictionary(x => x.Id, _ => (string[]?)[.. withWords.Select(c => c.Id)]));
		var read = shows.ToDictionary(x => x.Id, _ => (string[]?)[]);
		foreach (var category in withWords)
		{
			foreach (var show in shows.Where(x => MicroGenreMatcher.Fits(category, x, any)).OrderByDescending(x => x.Rating ?? 0).Take(20))
			{
				read[show.Id] = [.. read[show.Id]!, category.Id];
			}
		}

		IReadOnlyList<SmartCollection> all;
		TextMatches.Put(new TextMatches(index.Key, "test", Wednesday, read) { CompleteFor = new HashSet<IndexKind> { IndexKind.Series } });
		try
		{
			all = CollectionLineup.Build(index, Wednesday, null, null, [], empty).All;
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}

		// the row (the week's order: lift × quality × the draw). Cut 1 (merged after this fix): with no history See all shows the
		// row too (CategoryRanking is empty), so the row is what this checks; For you with history has its own test below
		var candidates = CollectionLineup.Candidates(all);
		var made = all.Where(x => x.Kind == SmartCollectionKind.MicroGenre).ToList();
		var share = made.Count(x => x.Only == IndexKind.Series) / (double)made.Count;
		var row = 0.0;
		const int Weeks = 6;
		for (var w = 0; w < Weeks; w++)
		{
			var now = Wednesday.AddDays(7 * w);
			var lineup = CollectionLineup.Assemble(candidates, LineupData.Of(index, empty, now), now);
			row += lineup.Categories.Count(x => x.Only == IndexKind.Series) / (double)Weeks;
		}

		var fair = share * CollectionLineup.CategoryRow;
		Assert.True(share is >= 0.3 and <= 0.5, $"{share:P0} of {made.Count} categories made are shows"); // the fixture's shape
		Assert.True(row >= 0.75 * fair, $"the Categories row held {row:0.0} shows a week, of {fair:0.0} in proportion");
		Assert.True(row <= 1.5 * fair, $"the Categories row held {row:0.0} shows a week, of {fair:0.0}: now the films are pushed out");
	}

	[Fact]
	public void SeeAllsForYou_RanksLiftedShowCategoriesBesideFilmOnes_ThoughShowsHaveNoCriticsRatings()
	{
		// fix G through cut 1's For you (fit × quality, no draw, lifted to 1.5 or more): six film categories whose films have
		// critics' ratings, six show categories whose shows have none (no backing: each show scores its kind's mean), each
		// lifted alike by what the user watched in it. Ranked on raw quality, the films took For you's first four
		var shelf = new Shelf();
		shelf.Library.AddRange(Titles("Show filler", 300, IndexKind.Series).Select(x => x with { TagCount = 0 }));
		var plays = new Dictionary<Guid, SeriesPlays>();
		for (var i = 0; i < 6; i++)
		{
			var rating = 6.0 + (0.5 * i);
			var films = Titles($"film-{i}", 12, played: 3).Select(x => x with { Rating = rating, Critic = (float)((rating * 10) + 10) }).ToList();
			var shows = Titles($"show-{i}", 12, IndexKind.Series).Select(x => x with { Rating = rating, TagCount = 0 }).ToList();
			plays[shows[0].Id] = new SeriesPlays(15, Wednesday.AddDays(-1 - i));
			foreach (var (id, titles) in new[] { ($"film-{i}", films), ($"show-{i}", shows) })
			{
				shelf.Library.AddRange(titles);
				shelf.TitlesOf[id] = titles;
				shelf.Collections.Add(Category(id, titles));
				shelf.Families.Add((id, id, null));
			}
		}

		var ranking = shelf.Assemble(plays: plays).CategoryRanking;

		Assert.True(ranking.Count(x => x.StartsWith("micro:film-", StringComparison.Ordinal)) == 6 && ranking.Count(x => x.StartsWith("micro:show-", StringComparison.Ordinal)) == 6,
			$"every category lifted: {string.Join(", ", ranking)}");
		Assert.True(ranking.Take(4).Count(x => x.StartsWith("micro:show-", StringComparison.Ordinal)) >= 2, $"For you's first four: {string.Join(", ", ranking.Take(4))}");
		Assert.Equal(["micro:film-5", "micro:show-5"], ranking.Take(2).Order(StringComparer.Ordinal)); // each kind's best first
	}

	// ---- the pre-release fix A (BH-190-M2): no week kept before the summaries' pass has read the shows -----------------------

	// what the summaries' pass read of the index's shows: a share of them (each title once in the fixture), with a summary (no
	// word of any list: the categories with a word list are decided, and empty) or without, and the kind read whole or not
	private static TextMatches ShowsRead(IndexSnapshot index, double share = 1, bool whole = true, bool summaries = true, string? key = null)
	{
		var shows = index.Entries.Where(x => x.Kind == IndexKind.Series).ToList();
		var read = shows.Take((int)(shows.Count * share)).ToDictionary(x => x.Id, _ => summaries ? (string[]?)[] : null);
		return new TextMatches(key ?? index.Key, "test", Wednesday, read) { CompleteFor = whole ? new HashSet<IndexKind> { IndexKind.Series } : new HashSet<IndexKind>() };
	}

	[Fact]
	public async Task ThePagesBuild_KeepsNoWeek_UntilTheSummariesPassHasReadTheShows()
	{
		// a first start, a sign-in, Categories off and on: the pass hasn't read the shows, so every category with a word list
		// waits; a week kept then held them out of the row and This week until Monday
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 900, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-text" };
		var weeks = new Weeks();
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;
		try
		{
			TextMatches.Put(TextMatches.Empty);
			var before = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			Assert.NotEmpty(before.Lineup.Categories); // shown ...
			Assert.Empty(weeks.Kept); // ... not kept

			TextMatches.Put(ShowsRead(index, share: 0.5, whole: false)); // a pass cut short at half the shows
			await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			Assert.Empty(weeks.Kept);

			TextMatches.Put(ShowsRead(index, key: "another-user")); // another sign-in's matches are none of these
			await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			Assert.Empty(weeks.Kept);

			TextMatches.Put(ShowsRead(index, share: 0.99, whole: false)); // enough read (98 %): kept
			var made = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			Assert.Equal(made.Lineup.WeekOrder, weeks.Kept["2026-W48"].Ids);
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	[Fact]
	public async Task AServerWithNoSummaries_ReadWhole_KeepsTheWeek()
	{
		// every show read and none has a summary: its categories with a word list wait for good (rule 4), and the week mustn't
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 900, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-none" };
		var weeks = new Weeks();
		try
		{
			TextMatches.Put(ShowsRead(index, summaries: false));
			var made = await CollectionLineup.BuildAsync(index, Wednesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, weeks: weeks);
			Assert.Equal(made.Lineup.WeekOrder, weeks.Kept["2026-W48"].Ids);
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	[Fact]
	public async Task MatchesThatCameAfterTheCollectionsWereMade_KeepNoWeek_TheBuildOfThemDoes()
	{
		// the Collections page: the build (no matches yet), then the pass ends before the Spotlight's lineup of the same
		// collections: that lineup is of categories made without the matches, so it keeps nothing; the build the new matches
		// bring keeps the week
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 900, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-late" };
		var weeks = new Weeks();
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;
		try
		{
			TextMatches.Put(TextMatches.Empty);
			var made = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			TextMatches.Put(ShowsRead(index));
			var spotlight = await CollectionLineup.AssembleAsync(index, made.All, [], Wednesday, outside: empty, fallback: made, weeks: weeks);
			Assert.NotEmpty(spotlight.Lineup.Categories);
			Assert.Empty(weeks.Kept);

			var again = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
			Assert.Equal(again.Lineup.WeekOrder, weeks.Kept["2026-W48"].Ids);
		}
		finally
		{
			TextMatches.Put(TextMatches.Empty);
		}
	}

	[Fact]
	public void Home_IsUnchanged_ItsTwoRowsThePersonalOneAndTheSpotlight_CategoriesOrNot()
	{
		var shelf = new Shelf();
		for (var i = 0; i < 6; i++)
		{
			shelf.Add($"c{i}");
		}

		var liked = Titles("Liked", 10);
		var spot = Titles("Spot", 6);
		var theme = Titles("Theme", 12);
		shelf.Library.AddRange([.. liked, .. spot, .. theme]);
		SmartCollection[] rest = [Of("morelike:x", SmartCollectionKind.MoreLike, liked), Of("spotlight:studio:x", SmartCollectionKind.Spotlight, spot), Of("keyword:x", SmartCollectionKind.Keyword, theme)];

		var with = shelf.Assemble(more: rest);
		var without = CollectionLineup.Assemble(CollectionLineup.Candidates(rest), shelf.Data(), Wednesday);

		Assert.Equal(["morelike:x", "spotlight:studio:x"], Ids(with.ForHome(CollectionLineup.HomeDefault)));
		Assert.Equal(Ids(without.ForHome(CollectionLineup.HomeDefault)), Ids(with.ForHome(CollectionLineup.HomeDefault)));
		Assert.Equal(CollectionLineup.HomeMost, with.ForHome(10).Count); // three at most, as before
		Assert.Equal(2, CategoriesIn(with).Count);
	}

	[Fact]
	public void ACategory_IsTheSameCollectionInThisWeekInTheRowAndInSeeAll_ItsTitlesInTheBuildersOrder()
	{
		// 1.8's promise: one collection shows the same titles in the same order wherever it is (the page puts the lineup's in
		// place of the build's: CollectionsViewModel's "chosen")
		var shelf = new Shelf();
		for (var i = 0; i < 4; i++)
		{
			shelf.Add($"c{i}");
		}

		var lineup = shelf.Assemble();

		foreach (var pick in lineup.Picks.Where(x => x.Slot == LineupSlot.Category))
		{
			var built = shelf.Collections.Single(x => x.Id == pick.Collection.Id);
			Assert.Same(built, pick.Collection);
			Assert.Same(built, lineup.Categories.Single(x => x.Id == built.Id));
		}
	}

	[Fact]
	public async Task TheStore_KeepsTheWeeksCategories_PerSignIn_TheNewestEight_ADamagedHistoryCostsOnlyItself()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-categories-" + Guid.NewGuid().ToString("N"));
		try
		{
			var user = Guid.NewGuid();
			var client = new Moq.Mock<FluentFin.Core.Contracts.Services.IJellyfinClient>();
			client.SetupGet(x => x.BaseUrl).Returns("http://127.0.0.1:8096");
			client.SetupGet(x => x.UserId).Returns(() => user);
			using var home = new FluentFin.Core.Services.HomeCache(Path.Combine(folder, "home"), new System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>(), new CoreListLogger<FluentFin.Core.Services.HomeCache>(), TimeProvider.System);
			SmartCollectionStore New() => new(client.Object, home, Path.Combine(folder, "sc"), new CoreListLogger<SmartCollectionStore>());
			var store = New();
			var changed = 0;
			store.Changed += (_, _) => changed++;
			var key = FluentFin.Core.Services.HomeCache.FileKey("http://127.0.0.1:8096", user);

			await store.SetFavorite("micro:kept-pin", true, "A pin");
			for (var w = 40; w <= 52; w++)
			{
				await store.SaveCategoryWeek($"2026-W{w:D2}", new CategoryWeek([$"micro:w{w}a", $"micro:w{w}b"], $"micro:w{w}b"), key);
			}

			var weeks = await New().CategoryWeeksAsync(key); // read back after a restart
			Assert.Equal(SmartCollectionStore.CategoryWeeksKept, weeks.Count);
			Assert.Equal(["micro:w52a", "micro:w52b"], weeks["2026-W52"].Ids);
			Assert.Equal("micro:w52b", weeks["2026-W52"].Explore);
			Assert.False(weeks.ContainsKey("2026-W44"));
			Assert.Equal(1, changed); // the pin only: the weeks change nothing the pages list

			// another sign-in's index (a Switch user during the build): nothing kept for it, nothing read
			await store.SaveCategoryWeek("2026-W53", new(["micro:other"]), "another sign-in");
			Assert.False((await store.CategoryWeeksAsync(key)).ContainsKey("2026-W53"));
			Assert.Empty(await store.CategoryWeeksAsync("another sign-in"));

			// a damaged history: the weeks are forgotten, the pin stays
			var file = Path.Combine(folder, "sc", key + ".json");
			var text = await File.ReadAllTextAsync(file);
			var damaged = System.Text.Json.Nodes.JsonNode.Parse(text)!;
			damaged["Categories"] = new System.Text.Json.Nodes.JsonArray(1, 2, 3);
			await File.WriteAllTextAsync(file, damaged.ToJsonString());
			var reread = New();
			Assert.Empty(await reread.CategoryWeeksAsync(key));
			Assert.Contains("micro:kept-pin", await reread.FavoritesAsync());
		}
		finally
		{
			TestFolder.Delete(folder);
		}
	}
}

internal static class CategoryLineupTestsExtensions
{
	/// <summary>The list, added to <paramref name="library"/> too.</summary>
	public static List<IndexEntry> Also(this List<IndexEntry> titles, List<IndexEntry> library)
	{
		library.AddRange(titles);
		return titles;
	}
}
