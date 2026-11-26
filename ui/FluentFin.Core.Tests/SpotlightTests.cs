using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-3): the weekly Spotlight: one a week, chosen on Monday and shown to Sunday with its end date; its notability
/// and coherence gates; never a person with fewer than 5 unwatched titles; the curated studio list.
/// </summary>
public sealed class SpotlightTests(ITestOutputHelper output)
{
	// Tuesday 24 November 2026: ISO week 48, Monday 23 to Sunday 29 November
	private static readonly DateTimeOffset Tuesday = new(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static IndexPerson Director(string name) => new(name, null, "Director");

	private static IndexPerson Actor(string name) => new(name, null, "Actor");

	// every title has a critics' rating and ten tags, so the shrunk score follows the rating (QualityScore: k = 2 for all)
	private static IndexEntry Film(string name, double rating, int year = 1965, bool played = false, IndexPerson[]? people = null, string[]? genres = null,
		string[]? tags = null, string[]? studios = null, string[]? countries = null) => new()
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000048"),
			Name = name,
			Kind = IndexKind.Movie,
			Year = year,
			Rating = rating,
			Critic = 70,
			TagCount = 10,
			Played = played,
			People = people ?? [],
			Genres = genres ?? [],
			Tags = tags ?? [],
			Studios = studios ?? [],
			Countries = countries ?? [],
			PrimaryTag = "p",
		};

	// films nobody spotlights, rated 3.0 to 6.9: the catalogue the 70th percentile is drawn from
	private static List<IndexEntry> Filler(int count = 120) =>
		[.. Enumerable.Range(0, count).Select(i => Film($"Filler {i:D3}", 3.0 + (i % 40 * 0.1), 1980 + (i % 30), genres: [i % 2 == 0 ? "Drama" : "Comedy"]))];

	// a director's films: crime thrillers of one decade with two regular leads, in turn (coherent; neither in eight), rated
	// from 9.0 down by 0.1
	private static List<IndexEntry> Retrospective(string director, int films, int watched = 0, double top = 9.0, string lead = "Alain Delon", string[]? genres = null, int year = 1960)
	{
		return [.. Enumerable.Range(0, films).Select(i => Film($"{director} film {i + 1}", top - (i * 0.1), year + i, played: i >= films - watched,
			people: [Director(director), Actor(i % 2 == 0 ? lead : $"{lead}'s partner"), Actor($"Support {director} {i}")], genres: genres ?? ["Crime", "Thriller"], tags: ["heist"], countries: ["France"]))];
	}

	// a director whose films share nothing but the name: each its own genre, decade, country and cast
	private static List<IndexEntry> Scattered(string director, int films = 6, double top = 9.0)
	{
		string[] genres = ["Western", "Musical", "Horror", "Romance", "Documentary", "Animation", "War", "Sport"];
		string[] countries = ["Japan", "Brazil", "Norway", "Egypt", "Peru", "Iceland", "India", "Chile"];
		return [.. Enumerable.Range(0, films).Select(i => Film($"{director} film {i + 1}", top - (i * 0.1), 1930 + (i * 13),
			people: [Director(director), Actor($"Lead {director} {i}")], genres: [genres[i]], countries: [countries[i]]))];
	}

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries) => new([], [.. entries], new Dictionary<Guid, SeriesPlays>(), HasPeople: true, Tuesday);

	private static BuildContext Context(IEnumerable<IndexEntry> entries, DateTimeOffset? now = null, bool whole = false) =>
		new(Index(entries), now ?? Tuesday, new HashSet<string>()) { Whole = whole };

	private static List<SmartCollection> Built(IEnumerable<IndexEntry> entries, DateTimeOffset? now = null, bool whole = false) =>
		[.. new SpotlightBuilder().Build(Context(entries, now, whole))];

	[Fact]
	public void ANotableDirector_IsTheWeeksSpotlight_WithItsEndDate_BestUnwatchedFirst_WatchedLast()
	{
		var melville = Retrospective("Jean-Pierre Melville", films: 8, watched: 2);

		var spotlight = Assert.Single(Built([.. Filler(), .. melville]));

		Assert.Equal("Spotlight: Jean-Pierre Melville", spotlight.Title);
		Assert.Equal("spotlight:director:jeanpierremelville", spotlight.Id);
		Assert.Equal(SmartCollectionKind.Spotlight, spotlight.Kind);
		Assert.True(spotlight.InListOrder); // the page keeps the Spotlight's order
		Assert.False(spotlight.HasOwnOrder);
		// film 1 is rated best; films 7 and 8 are watched: at the end
		Assert.Equal([.. melville.Take(6).Select(x => x.Id), .. melville.Skip(6).Select(x => x.Id)], spotlight.ItemIds);
		// the end date: this week's Sunday
		Assert.Equal(new DateOnly(2026, 11, 29), SpotlightWeek.Of(Tuesday).Sunday);
		Assert.Equal($"Director · 8 films, 6 unwatched · until {ReasonLine.UntilDay(new DateOnly(2026, 11, 29))}", spotlight.ReasonText);
		Assert.Equal(new DateOnly(2026, 11, 29), spotlight.Reason?.Until);
		Assert.Equal($"Films directed by Jean-Pierre Melville, the best rated not watched yet first, until {ReasonLine.UntilDay(new DateOnly(2026, 11, 29))}", spotlight.Why);
		Assert.Null(NixieTitles.Check(spotlight.Title)); // the hook first, never "Your…", nothing about the viewer
		Assert.Equal($"Spotlight: Jean-Pierre Melville, made by Nixie, spotlight, Director, 8 films, 6 unwatched, until {ReasonLine.UntilDay(new DateOnly(2026, 11, 29))}", spotlight.TileAutomationName);
		Assert.Contains("29", ReasonLine.UntilDay(new DateOnly(2026, 11, 29)), StringComparison.Ordinal);
	}

	[Fact]
	public void NeverAPersonWithFewerThanFiveUnwatched()
	{
		Assert.Empty(Built([.. Filler(), .. Retrospective("Four Left", films: 8, watched: 4)]));
		Assert.Single(Built([.. Filler(), .. Retrospective("Five Left", films: 8, watched: 3)]));
	}

	// review (Major 1): an actor in nine titles: four films not watched, four watched, and a show whose episodes are played
	// to <paramref name="played"/> of 31 (as stream a's anchors count it, 90 % played is watched)
	private static (IndexSnapshot Index, IndexEntry Show) ActorWithAShow(int played)
	{
		IndexEntry Titled(int i, bool watched) => Film($"Lead film {i}", 9.0 - (i * 0.05), 1970 + i, played: watched,
			people: [Director($"Someone {i}"), Actor("Show Lead"), Actor($"B{i}")], genres: ["Crime"], tags: ["heist"]);
		var show = new IndexEntry
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000049"),
			Name = "The Long Show",
			Kind = IndexKind.Series,
			Year = 1975,
			Rating = 8.8,
			Critic = 70,
			TagCount = 10,
			Unplayed = 31 - played,
			People = [Actor("Show Lead"), Actor("Show Partner")],
			Genres = ["Crime"],
			Tags = ["heist"],
			PrimaryTag = "p",
		};
		List<IndexEntry> entries = [.. Filler(), .. Enumerable.Range(0, 4).Select(i => Titled(i, false)), .. Enumerable.Range(4, 4).Select(i => Titled(i, true)), show];
		return (new IndexSnapshot([], entries, new Dictionary<Guid, SeriesPlays> { [show.Id] = new(played, Tuesday.AddDays(-2)) }, HasPeople: true, Tuesday), show);
	}

	[Fact]
	public void AShowNinetyPercentWatched_IsWatched_SoFourFilmsLeftAreTooFew()
	{
		List<SmartCollection> Of(IndexSnapshot index) => [.. new SpotlightBuilder().Build(new BuildContext(index, Tuesday, new HashSet<string>()))];

		// 30 of 31 episodes: watched (the anchors' rule): four unwatched, under five: no Spotlight
		Assert.Empty(Of(ActorWithAShow(played: 30).Index));

		// 20 of 31: not watched: five unwatched, the show among them (before the watched films), and the line says five
		var (index, show) = ActorWithAShow(played: 20);
		var spotlight = Assert.Single(Of(index));
		Assert.Equal("Spotlight: Show Lead", spotlight.Title);
		Assert.StartsWith("Actor · 9 titles, 5 unwatched", spotlight.ReasonText, StringComparison.Ordinal);
		Assert.InRange(spotlight.ItemIds.ToList().IndexOf(show.Id), 0, 4);
	}

	[Fact]
	public void ADirectorNeedsSixFilms_AnActorEightTitlesInTheFirstThreeBilled()
	{
		Assert.Empty(Built([.. Filler(), .. Retrospective("Five Films", films: 5)]));

		// billed fourth in ten fine films: never a candidate; billed third in eight: one
		var fourth = Enumerable.Range(0, 10).Select(i => Film($"Ensemble {i}", 9.0 - (i * 0.05), 1970 + i,
			people: [Director($"Someone {i}"), Actor($"A{i}"), Actor($"B{i}"), Actor($"C{i}"), Actor("Fourth Billed")], genres: ["Crime"], tags: ["heist"]));
		Assert.Empty(Built([.. Filler(), .. fourth]));

		var third = Enumerable.Range(0, 8).Select(i => Film($"Lead {i}", 9.0 - (i * 0.05), 1970 + i,
			people: [Director($"Other {i}"), Actor($"A{i}"), Actor($"B{i}"), Actor("Third Billed")], genres: ["Crime"], tags: ["heist"]));
		var spotlight = Assert.Single(Built([.. Filler(), .. third]));
		Assert.Equal("Spotlight: Third Billed", spotlight.Title);
		Assert.StartsWith("Actor · 8 films", spotlight.ReasonText, StringComparison.Ordinal);
	}

	[Fact]
	public void TheBestSix_MustEachBeAtOrAboveTheCataloguesSeventiethPercentile()
	{
		// six films, five of them fine and one rated like the filler's middle: the best six include it, so no Spotlight
		var films = Retrospective("Uneven", films: 6);
		films[5] = films[5] with { Rating = 5.0 };
		Assert.Empty(Built([.. Filler(), .. films]));

		// a seventh fine film puts the weak one out of the best six
		Assert.Single(Built([.. Filler(), .. films, .. Retrospective("Uneven", films: 1, top: 8.0).Select(x => x with { Name = "Uneven film 7" })]));
	}

	[Fact]
	public void Coherence_DropsTheBottomThirdOfEachSubject()
	{
		var counts = new SpotlightCounts();
		var candidates = SpotlightBuilder.Candidates(Context([.. Filler(), .. Retrospective("Coherent One", 6), .. Retrospective("Coherent Two", 6, lead: "Lino Ventura"),
			.. Scattered("All Over")]), counts);

		Assert.Equal(["spotlight:director:coherentone", "spotlight:director:coherenttwo"], candidates.Select(x => x.Id));
		Assert.Equal(3, counts.Of(SpotlightSubject.Director, SpotlightGate.Notable));
		Assert.Equal(2, counts.Of(SpotlightSubject.Director, SpotlightGate.Coherent));
		Assert.All(candidates, x => Assert.True(x.Coherence > 0));
	}

	[Fact]
	public void TheSameSpotlight_AllWeek_AndANewOneOnMonday()
	{
		var entries = new List<IndexEntry>(Filler());
		foreach (var name in new[] { "Ann", "Bea", "Cy", "Dee", "Eve", "Flo" })
		{
			entries.AddRange(Retrospective($"Director {name}", 6, lead: $"Lead {name}"));
		}

		var candidates = SpotlightBuilder.Candidates(Context(entries));
		Assert.True(candidates.Count >= 4); // six pass notability; coherence keeps two thirds
		var monday = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.FromHours(-8));
		var week = Enumerable.Range(0, 7).Select(d => Assert.Single(Built(entries, monday.AddDays(d).AddHours(23.99))).Id).Distinct().ToList();
		var thisWeek = Assert.Single(week); // Monday 00:00 to Sunday 23:59 the same

		var nextMonday = Assert.Single(Built(entries, monday.AddDays(7))).Id;
		Assert.NotEqual(thisWeek, nextMonday);
		Assert.Contains(ReasonLine.UntilDay(new DateOnly(2026, 12, 6)), Assert.Single(Built(entries, monday.AddDays(7))).ReasonText, StringComparison.Ordinal);

		// a year of Mondays: never the same two weeks running, every candidate has its weeks, and the order the library lists
		// them in doesn't matter
		var picks = Enumerable.Range(0, 52).Select(w => SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(monday.AddDays(7 * w)))!.Id).ToList();
		Assert.All(picks.Zip(picks.Skip(1)), x => Assert.NotEqual(x.First, x.Second));
		Assert.Equal(candidates.Count, picks.Distinct().Count());
		Assert.Equal(picks[0], SpotlightBuilder.Pick([.. candidates.Reverse()], SpotlightWeek.Of(monday))!.Id);
	}

	[Theory]
	[InlineData(2)]
	[InlineData(3)]
	public void ThreeOrFewer_TakeTurns(int count)
	{
		var candidates = Enumerable.Range(0, count).Select(i => new SpotlightCandidate($"spotlight:director:d{i}", SpotlightSubject.Director, $"D{i}", [], 6, 0.5)).ToList();
		var monday = SpotlightWeek.Of(new DateOnly(2026, 11, 23));

		var picks = Enumerable.Range(0, 12).Select(w => SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(monday.Monday.AddDays(7 * w)))!.Id).ToList();

		Assert.All(picks.Zip(picks.Skip(1)), x => Assert.NotEqual(x.First, x.Second));
		Assert.Equal(count, picks.Take(count).Distinct().Count());
	}

	[Theory]
	[InlineData(1999, 7, 14)]
	[InlineData(1970, 1, 1)]
	[InlineData(1, 1, 3)]
	public void AClockSetBefore2001_StillPicks_TurnByTurn(int year, int month, int day)
	{
		// review (Minor 6): the week's number is negative then, and the turn of three or fewer threw (a negative index)
		var candidates = Enumerable.Range(0, 3).Select(i => new SpotlightCandidate($"spotlight:director:d{i}", SpotlightSubject.Director, $"D{i}", [], 6, 0.5)).ToList();
		var week = SpotlightWeek.Of(new DateOnly(year, month, day));

		Assert.NotNull(SpotlightBuilder.Pick(candidates, week));
		Assert.NotNull(SpotlightBuilder.Pick([.. candidates, candidates[0] with { Id = "spotlight:director:d3" }], week)); // the draw's weeks back too
		Assert.NotEqual(SpotlightBuilder.Pick(candidates, week)!.Id, SpotlightBuilder.Pick(candidates, SpotlightWeek.Of(week.Monday.AddDays(7)))!.Id);
	}

	[Fact]
	public void ACandidateJoiningThatLosesTheDraw_DoesntMoveTheWeeksPick()
	{
		var candidates = Enumerable.Range(0, 12).Select(i => new SpotlightCandidate($"spotlight:director:d{i}", SpotlightSubject.Director, $"D{i}", [], 6, 0.5)).ToList();
		var week = SpotlightWeek.Of(new DateOnly(2026, 11, 24));
		var pick = SpotlightBuilder.Pick(candidates, week)!.Id;

		// of twenty newcomers one at a time, those that don't win the week leave the pick where it was (rendezvous)
		var moved = 0;
		for (var i = 0; i < 20; i++)
		{
			var joined = SpotlightBuilder.Pick([.. candidates, new SpotlightCandidate($"spotlight:actor:new{i}", SpotlightSubject.Actor, $"N{i}", [], 6, 0.5)], week)!.Id;
			moved += joined != pick && !joined.StartsWith("spotlight:actor:new", StringComparison.Ordinal) ? 1 : 0;
		}

		Assert.InRange(moved, 0, 2); // only through last week's draw changing (rare)
	}

	[Fact]
	public void AWeekRunsMondayToSunday_ByTheLocalDate()
	{
		var sunday = SpotlightWeek.Of(new DateTimeOffset(2026, 11, 29, 23, 59, 0, TimeSpan.FromHours(-8)));
		var monday = SpotlightWeek.Of(new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.FromHours(-8)));

		Assert.Equal("2026-W48", sunday.Seed);
		Assert.Equal("2026-W49", monday.Seed);
		Assert.Equal(new DateOnly(2026, 11, 23), sunday.Monday);
		Assert.Equal(new DateOnly(2026, 12, 6), monday.Sunday);
		// the year turns inside an ISO week: 1 January 2027 is in 2026's week 53
		Assert.Equal("2026-W53", SpotlightWeek.Of(new DateOnly(2027, 1, 1)).Seed);
		Assert.Equal("2026-W52", SpotlightWeek.Of(new DateOnly(2027, 1, 1)).Before(1).Seed);
	}

	[Fact]
	public void ACuratedStudio_UnderAnyOfItsNames_CanBeTheSpotlight_AnotherStudioNever()
	{
		var pixar = Enumerable.Range(0, 7).Select(i => Film($"Toy film {i}", 8.9 - (i * 0.1), 1995 + (i * 2), genres: ["Animation", "Family"], tags: ["toy"],
			studios: [i % 2 == 0 ? "Pixar Animation Studios" : "Pixar"], people: [Director($"Pixar director {i}")]));
		var spotlight = Assert.Single(Built([.. Filler(), .. pixar]));
		Assert.Equal("Spotlight: Pixar", spotlight.Title);
		Assert.Equal("spotlight:studio:pixar", spotlight.Id);
		Assert.StartsWith("Studio · 7 films", spotlight.ReasonText, StringComparison.Ordinal);

		var other = pixar.Select(x => x with { Studios = ["Generic Pictures"] });
		Assert.Empty(Built([.. Filler(), .. other]));
	}

	[Fact]
	public void ADirectorOrActor_WaitsForThePeopleToBeIn_AStudioDoesnt()
	{
		// stream a's PeopleCovered: the people of 98 % of the titles in, none due. A people step cut short (or its keep a minute
		// in) has a part of them only: a Spotlight of a person then would change as the rest came
		List<IndexEntry> entries = [.. Filler(), .. Retrospective("Jean-Pierre Melville", 8)];
		var covered = Index(entries);
		List<SmartCollection> Of(IndexSnapshot index) => [.. new SpotlightBuilder().Build(new BuildContext(index, Tuesday, new HashSet<string>()))];

		Assert.True(covered.PeopleCovered);
		Assert.Equal("spotlight:director:jeanpierremelville", Assert.Single(Of(covered)).Id);

		var cutShort = covered with { PeopleToAsk = [.. entries.Take((int)Math.Ceiling(entries.Count * 0.03)).Select(x => x.Id)] };
		Assert.False(cutShort.PeopleCovered);
		Assert.Empty(Of(cutShort));
		Assert.Empty(Of(covered with { PeopleDue = true }));
		Assert.Empty(Of(covered with { HasPeople = false }));
		Assert.Single(Of(covered with { PeopleToAsk = [entries[0].Id] })); // one title of 128 still to ask: covered

		// a curated studio's titles come with the titles read: its Spotlight doesn't wait, and the census says the people do
		var pixar = Enumerable.Range(0, 7).Select(i => Film($"Toy film {i}", 8.9 - (i * 0.1), 1995 + (i * 2), genres: ["Animation", "Family"], tags: ["toy"],
			studios: ["Pixar"], people: [Director($"Pixar director {i}")]));
		var context = new BuildContext(Index([.. entries, .. pixar]) with { HasPeople = false }, Tuesday, new HashSet<string>());
		Assert.Equal("spotlight:studio:pixar", Assert.Single(new SpotlightBuilder().Build(context)).Id);
		Assert.Contains("studio 1>1>1>1, people not all in; a pick", context.Gates.NoteOf(SmartCollectionKind.Spotlight), StringComparison.Ordinal);
	}

	[Fact]
	public async Task TheWeeksPick_IsKeptOnlyOnceThePeopleAreIn_AFirstStartsStudioPickIsShownNotKept()
	{
		// 1.8 (stream l, the re-review): a first start's Spotlight is made before the people step (a studio's): kept for the week,
		// it held the week against the directors and actors that came minutes later
		List<IndexEntry> entries = [.. Filler(), .. Retrospective("Jean-Pierre Melville", 8)];
		var pixar = Enumerable.Range(0, 7).Select(i => Film($"Toy film {i}", 8.9 - (i * 0.1), 1995 + (i * 2), genres: ["Animation", "Family"], tags: ["toy"],
			studios: ["Pixar"], people: [Director($"Pixar director {i}")])).ToList();
		var picks = new KeptPicks();

		var early = Index([.. entries, .. pixar]) with { HasPeople = false };
		Assert.Equal("spotlight:studio:pixar", Assert.Single(await SmartCollectionCatalog.BuildLaterAsync(early, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: picks)).Id);
		Assert.Empty(picks.Kept); // shown, not kept

		var covered = Index([.. entries, .. pixar]);
		var made = Assert.Single(await SmartCollectionCatalog.BuildLaterAsync(covered, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: picks));
		Assert.Equal(made.Id, Assert.Single(picks.Kept).Value); // the people in: kept for the week
	}

	[Fact]
	public void APersonWhoPassesAsDirectorAndActor_IsADirector()
	{
		var films = Enumerable.Range(0, 8).Select(i => Film($"Auteur film {i}", 9.0 - (i * 0.05), 1970 + i,
			people: [Director("Clint Auteur"), Actor("Clint Auteur"), Actor($"Partner {i}")], genres: ["Western"], tags: ["revenge"]));

		var candidates = SpotlightBuilder.Candidates(Context([.. Filler(), .. films]));

		Assert.Equal(["spotlight:director:clintauteur"], candidates.Select(x => x.Id));
	}

	private static List<IndexEntry> ThreeDirectors()
	{
		var entries = new List<IndexEntry>(Filler());
		foreach (var name in new[] { "Ann", "Bea", "Cy" })
		{
			entries.AddRange(Retrospective($"Director {name}", 6, lead: $"Lead {name}"));
		}

		return entries;
	}

	[Fact]
	public async Task TheSpotlight_IsMadeAfterTheFirstRows_NeverInTheBuildHomesFirstRowWaitsOn()
	{
		// the review's Major 2: BuildAllAsync is what Home's first Nixie row waits on; the Spotlight is made by BuildLaterAsync
		var index = Index(ThreeDirectors());
		var published = await SmartCollectionCatalog.BuildAllAsync(index, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty);
		Assert.DoesNotContain(published, x => x.Kind == SmartCollectionKind.Spotlight);
		Assert.DoesNotContain(SmartCollectionCatalog.PublishedBuilders, x => x is SpotlightBuilder);

		var picks = new KeptPicks();
		var later = SmartCollectionCatalog.BuildLaterAsync(index, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: picks);
		Assert.Same(later, SmartCollectionCatalog.BuildLaterAsync(index, Tuesday.AddHours(1), outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: picks)); // one work a page or two
		var spotlight = Assert.Single(await later); // what a page adds when its task comes
		Assert.Equal(spotlight.Id, picks.Kept["2026-W48"]); // kept on this PC
		Assert.Equal(SmartCollectionKind.Spotlight, SmartCollectionCatalog.KindOf(spotlight.Id));
		Assert.False(RetiredCollections.IsGone(spotlight.Id)); // never taken out as a director's pin was
		Assert.Equal(spotlight.ItemIds, SmartCollectionCatalog.Find(index, spotlight.Id, Tuesday)?.ItemIds); // a pin of the week's
		var other = SpotlightBuilder.Candidates(new BuildContext(index, Tuesday, new HashSet<string>())).First(x => x.Id != spotlight.Id);
		var pinned = SmartCollectionCatalog.Find(index, other.Id, Tuesday);
		Assert.NotNull(pinned); // another one's, by its id (a pin): its retrospective, with no end date
		Assert.Null(pinned.Reason?.Until);
		Assert.Empty(await SmartCollectionCatalog.BuildLaterAsync(index, Tuesday, new HashSet<SmartCollectionKind> { SmartCollectionKind.Spotlight }, FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks)); // switched off
	}

	// 1.8.1 (BH-180-q5): BuildLaterAsync's LaterReady had no subscriber in the app (the pages await its task): gone, so nothing
	// is told to nobody, and no test keeps a seam the app doesn't use. The review: that event only, not any event the catalog
	// may need later
	[Fact]
	public void TheLaterBuild_HasNoLaterReadyNobodyListensTo()
	{
		const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
		Assert.Empty(typeof(SmartCollectionCatalog).GetMember("LaterReady", any)); // no event, nor its backing field or delegate
	}

	// 1.8.1 (BH-180-q1): a Switch user during the Spotlight's build (0.2 to 0.7 s): the build is of user A's index, and its
	// pick was saved in the store as it was when the build ended, user B's: B's week held A's pick. The picks are read and
	// saved for the index's own sign-in (its key) only
	[Fact]
	public async Task ASwitchUserDuringTheBuild_SavesNothingInTheNextUsersStore()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-spotlight-" + Guid.NewGuid().ToString("N"));
		try
		{
			var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
			var user = a;
			var client = new Moq.Mock<FluentFin.Core.Contracts.Services.IJellyfinClient>();
			client.SetupGet(x => x.BaseUrl).Returns("http://127.0.0.1:8096");
			client.SetupGet(x => x.UserId).Returns(() => user);
			using var home = new FluentFin.Core.Services.HomeCache(Path.Combine(folder, "home"), new System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>(), new CoreListLogger<FluentFin.Core.Services.HomeCache>(), TimeProvider.System);
			var store = new SmartCollectionStore(client.Object, home, Path.Combine(folder, "sc"), new CoreListLogger<SmartCollectionStore>());
			var ofA = Index(ThreeDirectors()) with { Key = FluentFin.Core.Services.HomeCache.FileKey("http://127.0.0.1:8096", a) };

			user = b; // Switch user: B signed in while A's Spotlight is made
			var made = Assert.Single(await SmartCollectionCatalog.BuildLaterAsync(ofA, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: store));

			Assert.Empty(await store.SpotlightPicksAsync()); // B's week holds nothing of A's
			user = a;
			Assert.DoesNotContain(made.Id, (await store.SpotlightPicksAsync()).Values); // and A's is made again at A's next visit
			var mine = Assert.Single(await SmartCollectionCatalog.BuildLaterAsync(ofA with { }, Tuesday, outside: FluentFin.Core.Outside.OutsideListsSnapshot.Empty, picks: store));
			Assert.Equal(mine.Id, (await store.SpotlightPicksAsync())["2026-W48"]); // A signed in: kept for A
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	[Fact]
	public void AKeptPick_HoldsAllWeek_WhileItHasFiveUnwatched_AndNextWeekIsAnother()
	{
		// the review's Minor 3: with the candidates changing mid-week (2 to 3) the pick moved in 35 weeks of 52, and a Monday that
		// took 4 to 3 repeated last week's in 15. Kept on this PC, the week's holds and last week's never comes back
		var all = Enumerable.Range(0, 4).Select(i => new SpotlightCandidate($"spotlight:director:d{i}", SpotlightSubject.Director, $"D{i}", [], 6, 0.5)).ToList();
		var kept = new Dictionary<string, string>(StringComparer.Ordinal);
		var monday = SpotlightWeek.Of(Tuesday).Monday;
		string? before = null;
		for (var w = 0; w < 52; w++)
		{
			var week = SpotlightWeek.Of(monday.AddDays(7 * w));
			// Monday: four; Wednesday: one leaves; Friday: two (whichever the week's draw would not keep)
			string? held = null;
			foreach (var set in new[] { all, all.Skip(w % 4).Concat(all.Take(w % 4)).Skip(1).ToList(), all.Where((_, i) => i % 2 == w % 2).ToList() })
			{
				var pick = SpotlightBuilder.Pick(set, week, kept, id => all.FirstOrDefault(x => x.Id == id));
				Assert.NotNull(pick);
				kept[week.Seed] = pick.Id;
				held ??= pick.Id;
				Assert.Equal(held, pick.Id); // all week
			}

			Assert.NotEqual(before, held); // never last week's
			before = held;
		}
	}

	[Fact]
	public void AKeptPick_UnderFiveUnwatched_GivesWayMidWeek_NeverToLastWeeks()
	{
		var a = new SpotlightCandidate("spotlight:director:a", SpotlightSubject.Director, "A", [], 6, 0.5);
		var b = a with { Id = "spotlight:director:b", Name = "B" };
		var c = a with { Id = "spotlight:director:c", Name = "C" };
		var week = SpotlightWeek.Of(Tuesday);
		var kept = new Dictionary<string, string> { [week.Before(1).Seed] = b.Id, [week.Seed] = a.Id };

		// A watched down to four: not a candidate, not held: another, but never B (last week's)
		Assert.Equal(c.Id, SpotlightBuilder.Pick([b, c], week, kept, _ => null)!.Id);
		// only last week's left: none this week rather than a repeat
		Assert.Null(SpotlightBuilder.Pick([b], week, kept, _ => null));
		// A ranked out by the other gates but still five unwatched: it holds
		Assert.Equal(a.Id, SpotlightBuilder.Pick([b, c], week, kept, id => id == a.Id ? a : null)!.Id);
	}

	[Fact]
	public async Task TheStore_KeepsTheWeeksPicks_TheNewestEight_AndSaysNothingChanged()
	{
		var folder = Path.Combine(Path.GetTempPath(), "nixie-spotlight-" + Guid.NewGuid().ToString("N"));
		try
		{
			var client = new Moq.Mock<FluentFin.Core.Contracts.Services.IJellyfinClient>();
			client.SetupGet(x => x.BaseUrl).Returns("http://127.0.0.1:8096");
			client.SetupGet(x => x.UserId).Returns(Guid.NewGuid());
			using var home = new FluentFin.Core.Services.HomeCache(Path.Combine(folder, "home"), new System.Reactive.Subjects.Subject<Jellyfin.Sdk.WebSockets.IOutboundWebSocketMessage>(), new CoreListLogger<FluentFin.Core.Services.HomeCache>(), TimeProvider.System);
			var store = new SmartCollectionStore(client.Object, home, Path.Combine(folder, "sc"), new CoreListLogger<SmartCollectionStore>());
			var changed = 0;
			store.Changed += (_, _) => changed++;
			for (var w = 40; w <= 52; w++)
			{
				await store.SaveSpotlightPick($"2026-W{w:D2}", $"spotlight:director:w{w}");
			}

			await store.SaveSpotlightPick("2026-W52", "spotlight:director:w52"); // the same again: nothing written
			var picks = await store.SpotlightPicksAsync();
			Assert.Equal(SmartCollectionStore.SpotlightWeeksKept, picks.Count);
			Assert.Equal("spotlight:director:w52", picks["2026-W52"]);
			Assert.False(picks.ContainsKey("2026-W44"));
			Assert.Equal(0, changed); // the pages' saved collections didn't change
		}
		finally
		{
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, recursive: true);
			}
		}
	}

	[Fact]
	public void TwoSpellingsOfOneName_AreOneCandidate_TheOneWithMoreTitles()
	{
		var candidates = SpotlightBuilder.Candidates(Context([.. Filler(), .. Retrospective("Jean-Pierre Melville", 7), .. Retrospective("Jean Pierre Melville", 6, lead: "Lino Ventura")]));

		var one = Assert.Single(candidates);
		Assert.Equal("Jean-Pierre Melville", one.Name);
		Assert.Equal(7, one.Titles.Count);
	}

	[Fact]
	public void ALibrarysPresetsStrip_NeverGetsASpotlight()
	{
		// the strip runs every published builder over one library's titles, Whole (every title of a rule): a Spotlight there
		// would be a chip of one library's director, made again for each library
		var entries = new List<IndexEntry>(Filler());
		foreach (var name in new[] { "Ann", "Bea", "Cy" })
		{
			entries.AddRange(Retrospective($"Director {name}", 6, lead: $"Lead {name}"));
		}

		Assert.Single(Built(entries));
		Assert.Empty(Built(entries, whole: true));
		Assert.DoesNotContain(NixiePresets.For(Index(entries), Guid.Empty, Tuesday), x => x.Kind == SmartCollectionKind.Spotlight);
	}

	[Fact]
	public void TheCensus_SaysCountsOnly_NeverAName()
	{
		var context = Context([.. Filler(), .. Retrospective("Jean-Pierre Melville", 8)]);

		_ = new SpotlightBuilder().Build(context).ToList();

		var note = context.Gates.NoteOf(SmartCollectionKind.Spotlight);
		Assert.NotNull(note);
		Assert.StartsWith("2026-W48: director 1>1>1>1", note, StringComparison.Ordinal);
		Assert.DoesNotContain("Melville", note, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(new BuildGates.Gate(1, 1, 1), context.Gates.Of(SmartCollectionKind.Spotlight));
	}

	[Fact]
	public void NoPeopleAndNoRatings_MakeNoSpotlight_AndNothingThrows()
	{
		Assert.Empty(Built([]));
		Assert.Empty(Built(Filler()));
		Assert.Empty(Built(Retrospective("Unrated", 8).Select(x => x with { Rating = null })));
		Assert.Empty(Built(Retrospective("Nobody", 8).Select(x => x with { People = [] })));
	}

	[Fact]
	public void TheStudioList_Loads_EveryNameItsOwn()
	{
		var all = SpotlightStudios.All;

		Assert.InRange(all.Count, 20, 40); // short: a curated list, not every studio
		Assert.Contains(all, x => x.Name == "Studio Ghibli");
		Assert.Contains(all, x => x.Name == "A24");
		Assert.Equal("Pixar", SpotlightStudios.Of("PIXAR ANIMATION STUDIOS")?.Name);
		Assert.Equal("Studio 4°C", SpotlightStudios.Of("Studio 4C")?.Name);
		Assert.Null(SpotlightStudios.Of("Warner Bros. Pictures"));
		Assert.Null(SpotlightStudios.Of(""));
		// no name is claimed by two studios
		var keys = all.SelectMany(x => x.Keys.Select(k => (k, x.Name))).ToList();
		Assert.Equal(keys.Count, keys.Select(x => x.k).Distinct(StringComparer.Ordinal).Count());
	}

	[Fact]
	public void AStudioListThatCantBeRead_IsEmpty()
	{
		Assert.Empty(SpotlightStudios.Load(null).Studios);
		Assert.Empty(SpotlightStudios.Load(new MemoryStream("{ not json"u8.ToArray())).Studios);
		Assert.Empty(SpotlightStudios.Load(new MemoryStream("""{ "studios": [ { "name": "  " }, null ] }"""u8.ToArray())).Studios);
		Assert.Single(SpotlightStudios.Load(new MemoryStream("""{ "studios": [ { "name": "Laika", "aliases": [ null, "LAIKA" ] } ] }"""u8.ToArray())).Studios);
	}

	// the owner's server's shape (25,736 films there): 25,800 films, about 6,000 directors and 40,000 actors billed in the first
	// five, a long tail (most of them in one or two films), studios with the curated ones among them, ratings and tags as a
	// server keeps them
	private static List<IndexEntry> RealShape(int films = 25_800)
	{
		var random = new Random(48);
		string[] genres = ["Drama", "Comedy", "Thriller", "Action", "Crime", "Horror", "Romance", "Documentary", "Animation", "Science Fiction",
			"Adventure", "Family", "Fantasy", "Mystery", "War", "Western", "Music", "History", "TV Movie", "Foreign"];
		string[] countries = ["United States of America", "United Kingdom", "France", "Japan", "Germany", "Italy", "India", "South Korea", "Spain", "Canada", "Hong Kong", "Mexico"];
		var curated = SpotlightStudios.All.Select(x => x.Name).ToArray();
		int Zipf(int n) => (int)Math.Min(n - 1, Math.Floor(Math.Pow(random.NextDouble(), 2.2) * n));
		return [.. Enumerable.Range(0, films).Select(i =>
		{
			IndexPerson[] people = [Director($"Director {Zipf(6000)}"), .. Enumerable.Range(0, 5).Select(_ => Actor($"Actor {Zipf(40_000)}"))];
			var studio = random.NextDouble() < 0.03 ? curated[random.Next(curated.Length)] : $"Studio {Zipf(3000)}";
			return Film($"Film {i}", Math.Round(Math.Clamp(6.3 + (random.NextDouble() * 3) - 1.5, 1, 10), 1), 1930 + random.Next(95), played: random.NextDouble() < 0.02,
				people: people, genres: [genres[Zipf(20)], genres[random.Next(20)]], tags: random.NextDouble() < 0.3 ? [$"keyword {random.Next(60)}"] : [],
				studios: [studio], countries: [countries[Zipf(countries.Length)]]) with { Critic = random.NextDouble() < 0.4 ? random.Next(20, 100) : null, TagCount = random.Next(0, 25) };
		})];
	}

	[Fact]
	public void AtRealShape_TheSpotlightIsMadeWellWithinItsBudget_AndLogsItsTime()
	{
		var entries = RealShape();
		var context = Context(entries);
		_ = context.Titles; // collapsed once before the clock, as the catalog's build does for every builder
		_ = context.Quality;
		var watch = System.Diagnostics.Stopwatch.StartNew();

		var made = new SpotlightBuilder().Build(context).ToList();

		watch.Stop();
		var note = context.Gates.NoteOf(SmartCollectionKind.Spotlight);
		output.WriteLine($"Spotlight at 25,800 films: {watch.Elapsed.TotalMilliseconds:0} ms; census: {note}");
		var spotlight = Assert.Single(made);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalMilliseconds:0} ms");
		Assert.Matches(@" in \d+ ms$", note);
		Assert.InRange(spotlight.ItemIds.Count, SpotlightBuilder.LeastUnwatched, SpotlightBuilder.Most);
	}

	[Fact]
	public void TheCandidates_AreMadeOnceAnIndex_AndAgainForANewOneOrOtherLists()
	{
		List<IndexEntry> entries = [.. Filler(), .. Retrospective("Jean-Pierre Melville", 8)];
		var index = Index(entries);

		var first = SpotlightBuilder.Candidates(new BuildContext(index, Tuesday, new HashSet<string>()));
		var nextWeek = SpotlightBuilder.Candidates(new BuildContext(index, Tuesday.AddDays(7), new HashSet<string>()));

		Assert.Same(first, nextWeek); // Home's build, then the Collections page's: the second pays nothing
		Assert.NotSame(first, SpotlightBuilder.Candidates(Context(entries))); // the next read of the index
		Assert.NotSame(first, SpotlightBuilder.Candidates(new BuildContext(index, Tuesday, new HashSet<string>()) { Outside = new FluentFin.Core.Outside.OutsideListsSnapshot() }));
		var counts = new SpotlightCounts();
		_ = SpotlightBuilder.Candidates(new BuildContext(index, Tuesday, new HashSet<string>()), counts);
		Assert.Equal(1, counts.Of(SpotlightSubject.Director, SpotlightGate.Coherent)); // a made-once index still says its counts
	}

	[Fact]
	public void TheDrawsWeight_IsTheSameEverywhere()
	{
		// the TV builds the same lineup for the same index and date (P2-5): the weight is FNV-1a plus splitmix64's finish, not .NET's string hash
		Assert.Equal(SpotlightBuilder.Weight("2026-W48", "spotlight:director:jeanpierremelville"), SpotlightBuilder.Weight("2026-W48", "spotlight:director:jeanpierremelville"));
		Assert.NotEqual(SpotlightBuilder.Weight("2026-W48", "spotlight:director:a"), SpotlightBuilder.Weight("2026-W49", "spotlight:director:a"));
		// pinned (worked out apart from the app, in Python): a port to the TV must give the same
		Assert.Equal(0x920cd93471b4ab7cUL, SpotlightBuilder.Weight("2026-W48", "spotlight:director:jeanpierremelville"));
	}
}

/// <summary>1.8 (the review's Minor 3): the weeks' Spotlight picks, in memory (the app's are the user's SmartCollectionStore).</summary>
internal sealed class KeptPicks : ISpotlightPicks
{
	public Dictionary<string, string> Kept { get; } = new(StringComparer.Ordinal);

	public Task<IReadOnlyDictionary<string, string>> SpotlightPicksAsync(string? of = null) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(Kept));

	public Task SaveSpotlightPick(string week, string id, string? of = null)
	{
		Kept[week] = id;
		return Task.CompletedTask;
	}
}
