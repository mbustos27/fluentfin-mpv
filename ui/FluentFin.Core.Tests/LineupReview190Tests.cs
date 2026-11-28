using FluentFin.Core.Services;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.9 (stream lineup): the review of wip/190-lineup, and the reviewer's probes (P1-P8) made tests.
/// </summary>
public sealed class LineupReview190Tests
{
	// Wednesday 25 November 2026 in California: ISO week 48
	private static readonly DateTimeOffset Wednesday = new(2026, 11, 25, 20, 0, 0, TimeSpan.FromHours(-8));
	private const string Server = "http://127.0.0.1:8096";
	private static int s_next;

	private static IndexEntry Title(string name, IndexKind kind = IndexKind.Movie, double rating = 7.0, float? critic = null, bool played = false, string[]? genres = null,
		string[]? tags = null) => new()
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-00000019a190"),
			Name = name,
			Kind = kind,
			Year = 2005,
			Genres = genres ?? ["Drama"],
			Tags = tags ?? [],
			Rating = rating,
			Critic = critic,
			TagCount = 10,
			Played = played,
			Unplayed = kind == IndexKind.Series ? 20 : null,
			PrimaryTag = "p",
			RunTimeTicks = TimeSpan.FromMinutes(100).Ticks,
		};

	// a store of one user on this PC, whose client keeps the user's id after sign-out (JellyfinClient sets it at sign-in only)
	private sealed class SignedIn : IDisposable
	{
		private readonly string _folder = Path.Combine(Path.GetTempPath(), "nixie-review190-" + Guid.NewGuid().ToString("N"));
		private readonly HomeCache _home;
		private readonly Moq.Mock<Contracts.Services.IJellyfinClient> _client = new();

		public SignedIn()
		{
			_client.SetupGet(x => x.BaseUrl).Returns(Server);
			_client.SetupGet(x => x.UserId).Returns(User);
			_home = new HomeCache(Path.Combine(_folder, "home"), new System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>(), new CoreListLogger<HomeCache>(), TimeProvider.System);
			Store = new SmartCollectionStore(_client.Object, _home, Path.Combine(_folder, "sc"), new CoreListLogger<SmartCollectionStore>());
		}

		public Guid User { get; } = Guid.NewGuid();

		public SmartCollectionStore Store { get; }

		public string Key => HomeCache.FileKey(Server, User);

		public string File => Path.Combine(_folder, "sc", Key + ".json");

		// signs the user out, and waits for the delete behind it
		public async Task SignOut()
		{
			await _home.Forget(Server, User);
			for (var i = 0; i < 100 && System.IO.File.Exists(File); i++)
			{
				await Task.Delay(20);
			}
		}

		public void Dispose()
		{
			_home.Dispose();
			TestFolder.Delete(_folder);
		}
	}

	// ---- 1. Major: a week saved after sign-out brought the signed-out user's file back (probe P7) ---------------------------

	[Fact]
	public async Task AWeekOrASpotlightPickKeptAfterSignOut_DoesntBringTheUsersFileBack_ASignInAgainKeepsThem()
	{
		using var user = new SignedIn();
		await user.Store.SaveCategoryWeek("2026-W47", new CategoryWeek(["micro:old"]), user.Key);
		var read = await user.Store.CategoryWeeksAsync(user.Key); // a build read the weeks ...
		_ = await user.Store.SpotlightPicksAsync(user.Key);        // ... and the Spotlight's picks

		await user.SignOut();                                       // the user signs out while the builds run
		Assert.False(File.Exists(user.File));

		// the builds end: the week (with the read's number, as KeepWeek passes it), and the same with the store's own note
		await user.Store.SaveCategoryWeek("2026-W48", new CategoryWeek(["micro:a", "micro:b"]), user.Key, (read as KeptCategoryWeeks)?.ReadAfter);
		Assert.False(File.Exists(user.File));
		await user.Store.SaveCategoryWeek("2026-W48", new CategoryWeek(["micro:a", "micro:b"]), user.Key);
		Assert.False(File.Exists(user.File));
		await user.Store.SaveSpotlightPick("2026-W48", "spotlight:studio:pixar", user.Key);
		Assert.False(File.Exists(user.File));

		// signed in again: a new read, and what it keeps is written
		var again = await user.Store.CategoryWeeksAsync(user.Key);
		Assert.Empty(again);
		await user.Store.SaveCategoryWeek("2026-W48", new CategoryWeek(["micro:c"]), user.Key, (again as KeptCategoryWeeks)?.ReadAfter);
		Assert.True(File.Exists(user.File));
		Assert.Equal(["micro:c"], (await user.Store.CategoryWeeksAsync(user.Key))["2026-W48"].Ids);
	}

	[Fact]
	public async Task ThePagesBuild_PassesItsReadOn_ASignOutDuringItKeepsNothing()
	{
		using var user = new SignedIn();
		var weeks = new SignOutWhileBuilding(user);
		// films only (the pre-release fix A: a library with shows keeps no week before the summaries' pass has read them; a
		// library with none waits for nothing)
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 0, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = user.Key };

		var made = await CollectionLineup.BuildAsync(index, Wednesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, weeks: weeks);

		Assert.NotEmpty(made.Lineup.Categories); // shown
		Assert.True(weeks.Saved); // KeepWeek asked to keep it, with the read's number ...
		Assert.False(File.Exists(user.File)); // ... and the signed-out user's file stays gone
	}

	// ---- 2. Major: the critics' fold-in pushed the shows down against the films (probe P1) ----------------------------------

	[Fact]
	public void AShowWithoutCritics_KeepsItsPlaceAgainstAFilmWithTheUsualCritics_OnlyADepartureCounts()
	{
		// films carry critics as a real server's do (the tomatometer runs above rating x 10: here 1.0 above, every one); the
		// shows have none. An 8.4 show led a 7.9 film in 1.8.1; reading the tomatometer as a score put the film first
		var films = Enumerable.Range(0, 60).Select(i => 6.0 + (i % 15 / 10.0)).Select((r, i) => Title($"Film {i}", rating: r, critic: (float)Math.Round((r * 10) + 10))).ToList();
		var shows = Enumerable.Range(0, 30).Select(i => Title($"Show {i}", IndexKind.Series, 6.5 + (i % 10 / 10.0))).ToList();
		var show = Title("A show rated 8.4, no critics", IndexKind.Series, 8.4);
		var film = Title("A film rated 7.9, critics 89", rating: 7.9, critic: 89);       // the films' usual gap: +1.0
		var praised = Title("A film rated 7.9, critics 100", rating: 7.9, critic: 100);  // above it: +2.1
		List<IndexEntry> all = [.. films, .. shows, show, film, praised];
		var index = new IndexSnapshot([], all, new Dictionary<Guid, SeriesPlays>(), false, Wednesday);
		var quality = BuildContext.For(index, Wednesday).Quality;

		var gap = (61 + 2.1) / 62; // sixty films and this one at +1.0, the praised one at +2.1
		Assert.Equal(gap, quality.GapOf(IndexKind.Movie), 6);
		Assert.Equal(0, quality.GapOf(IndexKind.Series));
		Assert.Equal(7.9 + ((1.0 - gap) / 3), quality.RatingOf(film)!.Value, 6);    // about the usual gap: about its own rating
		Assert.Equal(1.1 / 3, quality.RatingOf(praised)!.Value - quality.RatingOf(film)!.Value, 6); // only the departure counts
		Assert.Equal(8.4, quality.RatingOf(show));
		Assert.Equal(all.Where(x => x.Kind == IndexKind.Movie).Average(x => x.Rating!.Value), quality.MeanOf(IndexKind.Movie), 2); // the films' mean: their ratings'

		// one ranking of both kinds: the show first, as in 1.8.1
		var acclaimed = Assert.Single(new HighlyRatedBuilder().Build(BuildContext.For(index, Wednesday))).ItemIds.ToList();
		Assert.True(acclaimed.IndexOf(show.Id) >= 0 && acclaimed.IndexOf(film.Id) >= 0);
		Assert.True(acclaimed.IndexOf(show.Id) < acclaimed.IndexOf(film.Id), "the show without critics ranks below the film");
	}

	// ---- 3. The lift smoothed almost nothing at a category's size (probe P2) -------------------------------------------------

	[Theory]
	// the owner's evidence: 25,800 films, 51 watched; 5,000 shows, about 110 of weight
	[InlineData(Taste.Started, 51, 30, 25_800, 1.0, 1.2)]  // one film started, a category of 30: a nudge (1.9's first form: 7 times, the most)
	[InlineData(1.0, 51, 30, 25_800, 1.3, 1.6)]            // one film finished: more
	[InlineData(3.0, 51, 259, 25_800, 1.8, 2.2)]           // three in the widest film category
	[InlineData(6.0, 110, 50, 5_000, 2.5, 3.0)]            // two shows binged in a show category of 50
	[InlineData(0.0, 110, 50, 5_000, 0.6, 0.7)]            // none of them: below the mix
	[InlineData(0.0, 0, 30, 25_800, 1.0, 1.0)]             // no history: no lift
	public void TheLift_OfAFewTitles_IsANudge_ABingeIsMuchMore(double hits, double evidence, int size, int ofKind, double least, double most) =>
		Assert.InRange(Taste.Lift(hits, evidence, size / (double)ofKind), least, most);

	[Fact]
	public void AShowCategoryWithEpisodesPlayed_LeadsAFilmCategoryWithAFilmStarted()
	{
		// the plan: "shows with played episodes count most". A film sampled and left weighs a quarter of one finished
		var library = Enumerable.Range(0, 400).Select(i => Title($"Film {i}")).Concat(Enumerable.Range(0, 200).Select(i => Title($"Show {i}", IndexKind.Series))).ToList();
		var started = library[0] with { PlayCount = 1 };
		library[0] = started;
		var plays = new Dictionary<Guid, SeriesPlays> { [library[400].Id] = new(15, Wednesday.AddDays(-2)) };
		var taste = Taste.Of(CanonicalTitles.Of(library), plays);

		Assert.Equal(Taste.Started, taste.WeightOf(started.Id));
		Assert.Equal(0.25, Taste.Started);
		Assert.Equal(2.5, taste.WeightOf(library[400].Id), 6); // 15 episodes: 1 and 1.5
		var film = taste.LiftOf(library.Take(30).Select(x => x.Id), IndexKind.Movie, 400);
		var show = taste.LiftOf(library.Skip(400).Take(30).Select(x => x.Id), IndexKind.Series, 200);
		Assert.True(show > film, $"show {show:0.00}, film {film:0.00}");
		Assert.InRange(film, 1.0, 1.2);
	}

	// ---- 4. The first week after the update was chosen, and kept, from an index without the matchable keywords -------------

	[Fact]
	public async Task AWeekChosenBeforeTheIndexKeepsTheMatchableKeywords_IsShownNotKept_TheDaysReadKeepsIt()
	{
		// 1.8.1's saved index (revision 3) shows at once; its categories naming other keywords wait for the day's read. Films only:
		// the pre-release fix A keeps no week of a library with shows before the summaries' pass has read them
		var index = new IndexSnapshot([], MicroGenreFixture.Library(4_000, 0, seed: 7), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-a" };
		var weeks = new Weeks();
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;

		var saved = await CollectionLineup.BuildAsync(index with { KeywordsKnown = false }, Wednesday, outside: empty, weeks: weeks);
		Assert.NotEmpty(saved.Lineup.Categories);
		Assert.Empty(weeks.Kept);

		var read = await CollectionLineup.BuildAsync(index, Wednesday, outside: empty, weeks: weeks);
		Assert.Equal(read.Lineup.CategoryWeek!.Ids, weeks.Kept["2026-W48"].Ids);
	}

	// ---- 5. Home wasn't unchanged with no personal pick (probe P4) ------------------------------------------------------------

	[Fact]
	public void WithNoPersonalPick_HomeIsAs181_TheSpotlightThenTheTheme_NeverACategory()
	{
		var library = Enumerable.Range(0, 300).Select(i => Title($"Filler {i}")).ToList();
		SmartCollection Make(string id, SmartCollectionKind kind, int count)
		{
			var titles = Enumerable.Range(0, count).Select(i => Title($"{id} {i}")).ToList();
			library.AddRange(titles);
			return new SmartCollection(id, kind, id, "", false, [.. titles.Select(x => x.Id)]) { Only = kind == SmartCollectionKind.MicroGenre ? IndexKind.Movie : null };
		}

		var spotlight = Make("spotlight:studio:x", SmartCollectionKind.Spotlight, 8);
		var theme = Make("keyword:x", SmartCollectionKind.Keyword, 12);
		var categories = Enumerable.Range(0, 4).Select(i => Make($"micro:c{i}", SmartCollectionKind.MicroGenre, 10)).ToList();
		var data = LineupData.Of(library);

		var with = CollectionLineup.Assemble(CollectionLineup.Candidates([spotlight, theme, .. categories]), data, Wednesday);
		var without = CollectionLineup.Assemble(CollectionLineup.Candidates([spotlight, theme]), data, Wednesday);

		Assert.Contains(with.Picks, x => x.Slot == LineupSlot.Category); // This week has them, above the theme
		Assert.Equal(["spotlight:studio:x", "keyword:x"], with.ForHome(CollectionLineup.HomeDefault).Select(x => x.Id));
		Assert.Equal(without.ForHome(CollectionLineup.HomeMost).Select(x => x.Id), with.ForHome(CollectionLineup.HomeMost).Select(x => x.Id));
	}

	// ---- 6. The week kept depended on which build kept it (probe P8) -------------------------------------------------------

	[Fact]
	public void TheWeekKept_IsTheSameWhicheverBuildKeptIt_TheRowStillShowsThisWeeksFirst()
	{
		// the nearest category (two of its films played) is nearly the Spotlight's titles: a build with the Spotlight leaves it
		// out of This week, one without it (Home's first rows, before the Spotlight) takes it
		var library = Enumerable.Range(0, 300).Select(i => Title($"Filler {i}")).ToList();
		var categories = new List<SmartCollection>();
		var families = new List<MicroGenre>();
		List<IndexEntry> Add(string id, int count, int played = 0)
		{
			var titles = Enumerable.Range(0, count).Select(i => Title($"{id} {i}", played: i < played)).ToList();
			library.AddRange(titles);
			categories.Add(new SmartCollection($"micro:{id}", SmartCollectionKind.MicroGenre, id, "", false, [.. titles.Select(x => x.Id)]) { Only = IndexKind.Movie });
			families.Add(new MicroGenre(id, id, IndexKind.Movie, id, null, new MicroGenreRule { GenresAll = ["Drama"], KeywordsAny = ["heist"] }, "Drama")); // 1.9 (Phase 3): not filters alone (R3)
			return titles;
		}

		var near = Add("near", 12, played: 2);
		for (var i = 0; i < 5; i++)
		{
			Add($"c{i}", 10);
		}

		var spotlight = new SmartCollection("spotlight:studio:near", SmartCollectionKind.Spotlight, "s", "", false, [.. near.Select(x => x.Id)]);
		LineupData Data(IReadOnlyDictionary<string, CategoryWeek>? history = null) => LineupData.Of(library) with
		{
			Catalogue = new MicroGenreCatalogue(families, []),
			CategoryHistory = history ?? new Dictionary<string, CategoryWeek>(StringComparer.Ordinal),
		};
		IReadOnlyList<string> ThisWeek(Lineup lineup) => [.. lineup.Picks.Where(x => x.Slot == LineupSlot.Category).Select(x => x.Collection.Id)];
		var week = SpotlightWeek.Of(Wednesday).Seed;

		var withSpotlight = CollectionLineup.Assemble(CollectionLineup.Candidates([.. categories, spotlight]), Data(), Wednesday);
		var plain = CollectionLineup.Assemble(CollectionLineup.Candidates(categories), Data(), Wednesday);
		var afterTheSpotlightsKept = CollectionLineup.Assemble(CollectionLineup.Candidates(categories), Data(new Dictionary<string, CategoryWeek> { [week] = withSpotlight.CategoryWeek! }), Wednesday);

		Assert.Equal(plain.CategoryWeek!.Ids, withSpotlight.CategoryWeek!.Ids); // one week, whichever build keeps it
		Assert.Equal("micro:near", withSpotlight.WeekOrder[0]);
		Assert.Equal(ThisWeek(plain), ThisWeek(afterTheSpotlightsKept)); // a later build without the Spotlight: as if none was kept
		Assert.Equal(["micro:near"], ThisWeek(plain).Take(1));
		Assert.Equal(ThisWeek(withSpotlight), withSpotlight.Categories.Take(2).Select(x => x.Id)); // the row as shown: This week's first
		Assert.DoesNotContain("micro:near", ThisWeek(withSpotlight));
	}

	// ---- 7. The Spotlight's pick was kept before the played episodes were in -------------------------------------------------

	[Fact]
	public async Task TheWeeksSpotlight_IsKeptOnlyOnceThePlayedEpisodesAreIn_ShownMeanwhile()
	{
		// the draw leans on what the user watched since 1.9: a pick made without the played episodes held the week against them
		List<IndexEntry> entries =
		[
			.. Enumerable.Range(0, 120).Select(i => Title($"Filler {i:D3}", rating: 3.0 + (i % 40 * 0.1), critic: 70)),
			.. Enumerable.Range(0, 7).Select(i => Title($"Toy film {i}", rating: 8.9 - (i * 0.1), critic: 70, genres: ["Animation", "Family"]) with { Studios = ["Pixar"], Year = 1995 + (i * 2) }),
		];
		var index = new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), HasPeople: true, Wednesday) { Key = "key-a" };
		var picks = new KeptPicks();
		var empty = FluentFin.Core.Outside.OutsideListsSnapshot.Empty;

		var due = Assert.Single(await SmartCollectionCatalog.BuildLaterAsync(index with { PlaysDue = true }, Wednesday, outside: empty, picks: picks));
		Assert.Equal("spotlight:studio:pixar", due.Id);
		Assert.Empty(picks.Kept); // shown, not kept

		var made = Assert.Single(await SmartCollectionCatalog.BuildLaterAsync(index, Wednesday, outside: empty, picks: picks));
		Assert.Equal(made.Id, Assert.Single(picks.Kept).Value); // the plays in: kept for the week
	}

	// ---- 8. A score asked for without a day evicted the day's (probe P5) ----------------------------------------------------

	[Fact]
	public void TheDaysScoreAndTheUndatedOne_AreKeptApart_NeitherEvictsTheOther()
	{
		var entries = Enumerable.Range(0, 50).Select(i => Title($"Film {i}")).ToList();
		var index = new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), false, Wednesday) { Key = "key-a" };

		var day = LineupData.Of(index, now: Wednesday).Quality;
		var undated = LineupData.Of(index).Quality; // the ui's franchise order asks without a day
		var dayAgain = LineupData.Of(index, now: Wednesday).Quality;
		var undatedAgain = LineupData.Of(index).Quality;

		Assert.Same(day, dayAgain);
		Assert.Same(undated, undatedAgain);
		Assert.NotSame(day, undated);
		Assert.NotSame(day, LineupData.Of(index, now: Wednesday.AddDays(1)).Quality); // another day: another score
	}

	// ---- 9. The concert rule took a drama tagged "concert" for a concert film --------------------------------------------------

	[Theory]
	[InlineData(new[] { "Drama" }, "concert", false)]                    // a drama about a pianist
	[InlineData(new[] { "Drama", "Romance" }, "live performance", false)]  // a stage drama
	[InlineData(new[] { "Music", "Documentary" }, "concert", true)]
	[InlineData(new[] { "Documentary" }, "live performance", true)]
	[InlineData(new[] { "Music" }, "live performance", true)]
	[InlineData(new[] { "Drama" }, "concert film", true)]                // named a concert film: one whatever its genres
	[InlineData(new[] { "Drama" }, "rock concert", true)]
	public void AConcertOrALivePerformance_IsAConcertFilmOnlyWithMusicOrDocumentary(string[] genres, string tag, bool concert)
	{
		var title = Title("A title", genres: genres, tags: [tag]);
		var titles = CanonicalTitles.Of([title, .. Enumerable.Range(0, 10).Select(i => Title($"Other {i}"))]);

		Assert.Equal(concert, QualityScore.IsConcert(title));
		Assert.Equal(concert ? 0.5 : 1.0, QualityScore.Of(titles, now: Wednesday).Confidence(title));
	}

	// ---- 10. From wire's review: the seasonal rows by their end date ----------------------------------------------------------

	[Fact]
	public void InThanksgivingsLastWeek_ThanksgivingLeadsChristmas_InThisWeekAndOnHome_EveryYear()
	{
		// the slot's monthly draw ordered the two by chance: Christmas (better titles) first in some years, and Home's seasonal
		// row was Christmas in the week of Thanksgiving
		for (var year = 2026; year <= 2035; year++)
		{
			var library = Enumerable.Range(0, 300).Select(i => Title($"Filler {i}")).ToList();
			SmartCollection Seasonal(string id, double rating, DateOnly until)
			{
				var titles = Enumerable.Range(0, 12).Select(i => Title($"{id} {i}", rating: rating)).ToList();
				library.AddRange(titles);
				return new SmartCollection(id, SmartCollectionKind.Seasonal, id, "", false, [.. titles.Select(x => x.Id)])
				{
					Reason = ReasonLine.Of(id, ReasonCount.Of(titles)) with { Until = until },
				};
			}

			var thanksgivingDay = SeasonalBuilder.ThanksgivingDay(year);
			var thanksgiving = Seasonal("seasonal:thanksgiving", 6.5, thanksgivingDay);
			var christmas = Seasonal("seasonal:christmas", 8.5, new DateOnly(year, 12, 31));
			var now = new DateTimeOffset(thanksgivingDay.AddDays(-2).ToDateTime(new TimeOnly(20, 0)), TimeSpan.FromHours(-8));

			var lineup = CollectionLineup.Assemble(CollectionLineup.Candidates([christmas, thanksgiving]), LineupData.Of(library), now);

			Assert.Equal(["seasonal:thanksgiving", "seasonal:christmas"], lineup.Picks.Where(x => x.Slot == LineupSlot.Seasonal).Select(x => x.Collection.Id));
			Assert.Equal("seasonal:thanksgiving", lineup.ForHome(CollectionLineup.HomeDefault)[0].Id);
		}
	}

	[Fact]
	public void TheLineupAndThePagesBuilds_CollapseOneIndexOnce()
	{
		// wire's review: LineupData collapsed the index on its own, beside the catalog's (about 70 ms at 31,000 titles, and the
		// categories' posting lists are kept with the titles they were made of)
		var index = new IndexSnapshot([], MicroGenreFixture.Library(2_000, 400, seed: 11), new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday) { Key = "key-a" };
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
			_ = LineupData.Of(index, now: Wednesday);
			_ = SmartCollectionCatalog.BuildAll(index, Wednesday);
			_ = CollectionLineup.Build(index, Wednesday, null, null, [], FluentFin.Core.Outside.OutsideListsSnapshot.Empty);
		}
		finally
		{
			BuildContext.Collapsing -= Count;
		}

		Assert.Equal(1, collapses);
		Assert.Same(SmartCollectionCatalog.CollapsedOf(index), LineupData.Of(index).Titles);
	}

	// ---- 11. Nits: no tie on the critics' value in the Spotlight's order -------------------------------------------------------

	[Fact]
	public void InTheSpotlightsOrder_TwoTitlesScoredAlike_AreNotToldApartByWhetherTheyHaveCritics()
	{
		// Alpha: no critics, in two libraries; Bravo: critics at the films' usual gap (the only film with them, so its own gap):
		// the same confidence, the same rating read, one score. The critics' tie put Bravo first: a title without critics last
		// among equals (missing data ranked low)
		Guid[] twoLibraries = [Guid.NewGuid(), Guid.NewGuid()];
		IndexEntry Toy(string name, float? critic = null, bool twice = true) => Title(name, rating: 8.0, critic: critic, genres: ["Animation", "Family"]) with
		{
			Studios = ["Pixar"],
			LibraryIds = twice ? twoLibraries : [],
		};

		List<IndexEntry> entries =
		[
			.. Enumerable.Range(0, 120).Select(i => Title($"Filler {i:D3}", rating: 3.0 + (i % 40 * 0.1))),
			Toy("Alpha"),
			Toy("Bravo", critic: 85, twice: false),
			.. Enumerable.Range(0, 5).Select(i => Toy($"Toy film {i}") with { Rating = 8.9 - (i * 0.1) }),
		];
		var context = BuildContext.For(new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays>(), HasPeople: false, Wednesday), Wednesday);
		var alpha = entries.Single(x => x.Name == "Alpha");
		var bravo = entries.Single(x => x.Name == "Bravo");
		Assert.Equal(context.Quality.Of(alpha), context.Quality.Of(bravo));

		var pixar = SpotlightBuilder.Held(context, SpotlightBuilder.IdOf(SpotlightSubject.Studio, "pixar"));

		Assert.NotNull(pixar);
		var names = pixar.Titles.Select(x => x.Name).ToList();
		Assert.True(names.IndexOf("Alpha") < names.IndexOf("Bravo"), string.Join(", ", names)); // equal: by name
	}

	// the weeks kept, in memory
	private sealed class Weeks : ICategoryWeeks
	{
		public Dictionary<string, CategoryWeek> Kept { get; } = new(StringComparer.Ordinal);

		public Task<IReadOnlyDictionary<string, CategoryWeek>> CategoryWeeksAsync(string? of = null) =>
			Task.FromResult<IReadOnlyDictionary<string, CategoryWeek>>(new Dictionary<string, CategoryWeek>(Kept, StringComparer.Ordinal));

		public Task SaveCategoryWeek(string week, CategoryWeek shown, string? of = null, long? since = null)
		{
			Kept[week] = shown;
			return Task.CompletedTask;
		}
	}

	// the store, signing the user out between the build's read and its keep
	private sealed class SignOutWhileBuilding(SignedIn user) : ICategoryWeeks
	{
		public bool Saved { get; private set; }

		public async Task<IReadOnlyDictionary<string, CategoryWeek>> CategoryWeeksAsync(string? of = null)
		{
			var read = await user.Store.CategoryWeeksAsync(of);
			await user.SignOut();
			return read;
		}

		public async Task SaveCategoryWeek(string week, CategoryWeek shown, string? of = null, long? since = null)
		{
			Saved = since is not null;
			await user.Store.SaveCategoryWeek(week, shown, of, since);
		}
	}
}
