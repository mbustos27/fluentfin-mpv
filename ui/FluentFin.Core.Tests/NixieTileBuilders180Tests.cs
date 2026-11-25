using System.Globalization;
using FluentFin.Core.Outside;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Builders;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-4, stream t): every collection the existing builders make has a title that keeps the rules (never "Your…",
/// nothing about the viewer), a reason line of one or two facts and its count, a Collection-page line without "Your", and
/// award badges from its titles' award facts. The words are asserted as the tile shows them (en-GB).
/// </summary>
public sealed class NixieTileBuilders180Tests : IDisposable
{
	private static readonly DateTimeOffset October = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset December = new(2026, 12, 2, 12, 0, 0, TimeSpan.Zero);
	private readonly CultureInfo _was = CultureInfo.CurrentCulture;

	public NixieTileBuilders180Tests() => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");

	public void Dispose() => CultureInfo.CurrentCulture = _was;

	private static int s_next;

	private static IndexEntry Film(string name, int year = 2001, string? collection = null, string[]? genres = null, string[]? tags = null, string[]? studios = null,
		string[]? countries = null, double? rating = null, float? critic = null, double minutes = 120, bool played = false, IndexPerson[]? people = null, string? tmdb = null) => new()
		{
			Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000180"),
			Name = name,
			Kind = IndexKind.Movie,
			Year = year,
			TmdbCollection = collection,
			Genres = genres ?? [],
			Tags = tags ?? [],
			Studios = studios ?? [],
			Countries = countries ?? ["United States of America"],
			Rating = rating,
			Critic = critic,
			RunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks,
			Played = played,
			People = people ?? [],
			Tmdb = tmdb,
			PrimaryTag = "p",
		};

	private static IndexEntry Show(string name, string[]? studios = null, int unplayed = 4) => new()
	{
		Id = Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-000000000181"),
		Name = name,
		Kind = IndexKind.Series,
		Year = 2012,
		Studios = studios ?? [],
		Unplayed = unplayed,
	};

	private static OutsideList Award(string id, string title, params string[] tmdb) =>
		new(id, OutsideSourceId.Wikidata, OutsideListKind.Award, title, $"{title}, from Wikidata, by year", [.. tmdb.Select(x => new OutsideListItem(new OutsideRef(Tmdb: x, Kind: IndexKind.Movie)))]);

	// one library at real-ish shape for every builder: franchises (one TMDb names), heists (one a Best Picture and Palme d'Or
	// winner), horror and Christmas titles, acclaimed and short films, a director, an actor, a studio, a network, a decade, a
	// country, a genre mix, and a show left mid-way
	private static (IndexSnapshot Index, OutsideListsSnapshot Outside, IndexEntry Winner, IndexEntry Show) Library()
	{
		var bigelow = new IndexPerson("Kathryn Bigelow", null, "Director");
		var quill = new IndexPerson("Clara Quill", null, "Actor");
		var winner = Film("The Long Job", 1995, genres: ["Crime"], tags: ["heist"], tmdb: "bp1", rating: 8.3, critic: 91);
		var films = new List<IndexEntry>
		{
			Film("The Matrix", 1999, "2344", played: true), Film("The Matrix Reloaded", 2003, "2344"),
			Film("The Mummy", 1999, "1733"), Film("The Mummy Returns", 2001, "1733", played: true), Film("The Mummy: Tomb of the Dragon Emperor", 2008, "1733"),
			winner, Film("Second Best", 1996, tmdb: "bp2", played: true),
		};
		films.AddRange(Enumerable.Range(0, 4).Select(i => Film($"Heist {i}", 2010 + i, genres: ["Crime"], tags: ["heist"])));
		films.AddRange(Enumerable.Range(0, 3).Select(i => Film($"Fright {i}", 2015, genres: ["Horror"], tags: i == 0 ? ["halloween"] : [], rating: 6.5)));
		films.AddRange(Enumerable.Range(0, 3).Select(i => Film($"Snow {i}", 2005, tags: ["christmas"])));
		films.AddRange(Enumerable.Range(0, 3).Select(i => Film($"Praised {i}", 2018, rating: 8.1, critic: 88)));
		films.AddRange(Enumerable.Range(0, 3).Select(i => Film($"Brief {i}", 2019, minutes: 75, rating: 7)));
		films.AddRange(Enumerable.Range(0, 3).Select(i => Film($"Bigelow {i}", 1990 + i, people: [bigelow])));
		films.AddRange(Enumerable.Range(0, 5).Select(i => Film($"Quill {i}", 2000 + i, people: [quill])));
		films.AddRange(Enumerable.Range(0, 5).Select(i => Film($"Studio {i}", 2011, studios: ["Northlight Pictures"])));
		films.AddRange(Enumerable.Range(0, 10).Select(i => Film($"Eighties {i}", 1981 + (i % 9))));
		films.AddRange(Enumerable.Range(0, 5).Select(i => Film($"Paris {i}", 2007, countries: ["France"])));
		films.AddRange(Enumerable.Range(0, 8).Select(i => Film($"Laugh Scream {i}", 2016, genres: ["Comedy", "Horror"])));
		films.AddRange(Enumerable.Range(0, 8).Select(i => Film($"Laugh {i}", 2016, genres: ["Comedy"])));
		var left = Show("Left Mid-way");
		var shows = new List<IndexEntry> { left };
		shows.AddRange(Enumerable.Range(0, 3).Select(i => Show($"Network {i}", ["Harbour TV"])));
		var plays = new Dictionary<Guid, SeriesPlays> { [left.Id] = new SeriesPlays(3, October.AddDays(-40)) };
		var index = new IndexSnapshot([], [.. films, .. shows], plays, HasPeople: true, October);
		var outside = OutsideListsSnapshot.Empty with
		{
			FranchiseNames = new Dictionary<string, string> { ["1733"] = "The Mummy Collection" },
			Lists = [Award("award-best-picture", "Best Picture winners", "bp1", "bp2"), Award("award-palme-dor", "Palme d'Or winners", "bp1")],
		};
		return (index, outside, winner, left);
	}

	private static List<SmartCollection> Built(ISmartCollectionBuilder builder, DateTimeOffset now)
	{
		var (index, outside, _, _) = Library();
		return [.. builder.Build(BuildContext.For(index, now) with { Outside = outside })];
	}

	private static SmartCollection One(ISmartCollectionBuilder builder, string id, DateTimeOffset? now = null) =>
		Assert.Single(Built(builder, now ?? October), x => x.Id == id);

	[Fact]
	public void Every_builders_collection_keeps_the_title_rules_and_has_a_reason_line_with_its_count()
	{
		var kinds = new HashSet<SmartCollectionKind>();
		foreach (var now in new[] { October, December })
		{
			foreach (var builder in SmartCollectionCatalog.Builders)
			{
				foreach (var made in Built(builder, now))
				{
					kinds.Add(made.Kind);
					Assert.Null(NixieTitles.Check(made.Title));
					var reason = Assert.IsType<ReasonLine>(made.Reason);
					Assert.InRange(reason.Facts.Count, 1, 2);
					Assert.All(reason.Facts, x => Assert.False(string.IsNullOrWhiteSpace(x)));
					// the count is the collection's own (1.8 integration: "2 of 7 films watched", stream a's Next in, counts its whole)
					Assert.Equal(made.ItemIds.Count, reason.Count.Form == ReasonCountForm.Watched ? reason.Count.Whole : reason.Count.Count);
					Assert.EndsWith(reason.CountText, made.ReasonText, StringComparison.Ordinal);
					Assert.DoesNotContain("Your", made.Why, StringComparison.Ordinal);
					Assert.DoesNotContain("your", made.ReasonText, StringComparison.OrdinalIgnoreCase);
					Assert.StartsWith($"{made.Title}, made by Nixie, ", made.TileAutomationName, StringComparison.Ordinal);
					Assert.Contains(reason.Spoken, made.TileAutomationName, StringComparison.Ordinal);
				}
			}
		}

		// not vacuous: every kind the builders make made one here. 1.8 integration: but the anchored slots (stream a: More like
		// and More from need what this fixture hasn't, a finished title's people and rare features), whose title and reason rules
		// AnchorsTests checks on a's own fixture
		var anchored = FluentFin.Core.SmartCollections.Anchors.AnchoredSlots.Builders.Select(x => x.Kind).ToHashSet();
		// and (stream s's Major 2) the Spotlight, made after the first rows (LaterBuilders), which needs the people this fixture hasn't: SpotlightTests
		anchored.UnionWith(SmartCollectionCatalog.LaterBuilders.Select(x => x.Kind));
		Assert.Equal(SmartCollectionCatalog.Builders.Select(x => x.Kind).Where(x => !anchored.Contains(x)).ToHashSet(), kinds.Where(x => !anchored.Contains(x)).ToHashSet());
	}

	[Fact]
	public void The_published_kinds_say_where_their_titles_come_from_and_count_them()
	{
		Assert.Equal("TMDb's “The Mummy Collection” · 3 films, 2 unwatched", One(new FranchiseBuilder(), "franchise:1733").ReasonText);
		Assert.Equal("One collection on TMDb · 2 films, 1 unwatched", One(new FranchiseBuilder(), "franchise:2344").ReasonText);
		Assert.Equal("TMDb keyword “heist” · 5 films, none watched", One(new KeywordBuilder(), "keyword:heist").ReasonText);
		Assert.Equal("Academy Awards · 2 films, 1 unwatched", One(new AwardBuilder(), "award:award-best-picture").ReasonText);
		Assert.Equal("Rated 7.5 or more, weighted by what backs each rating · 4 films", One(new HighlyRatedBuilder(), "pick:highly-rated").ReasonText);
		Assert.Equal("Feature films of 60 to 90 minutes, best rated first · 3 films", One(new ShortFilmsBuilder(), "pick:short-films").ReasonText);
		Assert.Equal("Episodes left, none watched for a month · 1 show", One(new UnfinishedSeriesBuilder(), "pick:unfinished-series").ReasonText);
	}

	[Fact]
	public void A_season_says_its_last_day()
	{
		Assert.Equal("Halloween and horror · 11 films, none watched · until Sat 31 Oct", One(new SeasonalBuilder(), "seasonal:halloween").ReasonText);
		Assert.Equal("Christmas films and shows · 3 films, none watched · until Thu 31 Dec", One(new SeasonalBuilder(), "seasonal:christmas", December).ReasonText);
	}

	[Fact]
	public void The_on_demand_kinds_have_reason_lines_too()
	{
		Assert.Equal("Director · 3 films, none watched", One(new DirectorBuilder(), "director:kathrynbigelow").ReasonText);
		Assert.Equal("Among the first five billed · 5 films, none watched", One(new ActorBuilder(), "actor:claraquill").ReasonText);
		Assert.Equal("Studio · 5 films, none watched", One(new StudioBuilder(IndexKind.Movie), "studio:northlightpictures").ReasonText);
		Assert.Equal("Studio or network · 3 shows, none watched", One(new StudioBuilder(IndexKind.Series), "network:harbourtv").ReasonText);
		Assert.Equal("Released 1980 to 1989 · 10 films, none watched", One(new DecadeBuilder(), "decade:movie:1980").ReasonText);
		Assert.Equal("First production country · 5 films, none watched", One(new CountryBuilder(), "country:france").ReasonText);
		Assert.Equal("Both Comedy and Horror · 8 films, none watched", One(new GenreMixBuilder(), "genres:movie:comedy+horror").ReasonText);
	}

	[Fact]
	public void A_tile_wears_its_titles_award_facts_but_not_its_own_award()
	{
		var heists = One(new KeywordBuilder(), "keyword:heist");
		Assert.Equal(["Best Picture winner", "Palme d'Or winner"], heists.Badges.Select(x => x.Text));
		Assert.EndsWith(", Best Picture winner, Palme d'Or winner", heists.TileAutomationName, StringComparison.Ordinal);

		var bestPicture = One(new AwardBuilder(), "award:award-best-picture");
		Assert.Equal(["Palme d'Or winner"], bestPicture.Badges.Select(x => x.Text)); // not "2 Best Picture winners": it is that award
		Assert.Empty(One(new FranchiseBuilder(), "franchise:2344").Badges);

		// Wikidata's lists off: no badges
		var (index, _, _, _) = Library();
		Assert.All(new KeywordBuilder().Build(BuildContext.For(index, October)), x => Assert.Empty(x.Badges));
	}

	[Fact]
	public void The_Collection_pages_line_says_the_same_without_Your()
	{
		Assert.Equal("Films in TMDb's “The Mummy Collection”, in release order", One(new FranchiseBuilder(), "franchise:1733").Why);
		Assert.Equal("Films directed by Kathryn Bigelow, in release order", One(new DirectorBuilder(), "director:kathrynbigelow").Why);
		Assert.Equal("Best Picture winners, from Wikidata, by year", One(new AwardBuilder(), "award:award-best-picture").Why);
		Assert.Equal("Released 1980 to 1989", One(new DecadeBuilder(), "decade:movie:1980").Why);
	}

	[Fact]
	public void The_trending_rows_say_each_sources_window_and_count_their_titles()
	{
		var (index, _, winner, _) = Library();
		var lists = new TrendingSnapshot(
		[
			new TrendingListRead(OutsideSourceId.Trakt, TrendingList.Trending, ListedKind.Movie, October, [new ListedTitle(1, ListedKind.Movie, "bp1", null, null, "t", 1995), new ListedTitle(2, ListedKind.Movie, "bp2", null, null, "t", 1996)]),
			new TrendingListRead(OutsideSourceId.TmdbTrending, TrendingList.Trending, ListedKind.Movie, October, [new ListedTitle(1, ListedKind.Movie, "bp1", null, null, "t", 1995)]),
			new TrendingListRead(OutsideSourceId.Trakt, TrendingList.BoxOffice, ListedKind.Movie, October, [new ListedTitle(1, ListedKind.Movie, "bp1", null, null, "t", 1995)]),
		], 1);

		var home = TrendingBuilder.HomeRow(index, lists)!;
		Assert.Equal("Trending on this server", home.Title);
		Assert.Null(NixieTitles.Check(home.Title));
		Assert.Equal("Trakt: watched right now · TMDb: this week · 2 films, 1 unwatched", home.ReasonText);
		Assert.Equal("Trakt: watched right now · TMDb: this week", home.Why); // the page's line, as 1.7's

		var made = TrendingBuilder.Build(index, lists);
		Assert.Equal("Trakt: watched right now · TMDb: this week · 2 films, 1 unwatched", made[0].ReasonText);
		Assert.Equal("The US box office this weekend, from Trakt · 1 film, not watched", made[1].ReasonText);
		Assert.Equal(winner.Id, made[1].ItemIds.Single());
	}
}

/// <summary>
/// Nixie 1.8 (P1-4, stream t): the trending tiles wear their titles' award badges too, from the app's shared Wikidata lists,
/// and lose them when those lists go (Wikidata turned off): what matched is kept for the lists it was made of. Sets the
/// shared lists, so it runs alone (<see cref="Outside.SharedOutsideLists"/>).
/// </summary>
[Collection(Outside.SharedOutsideLists.Name)]
public sealed class TrendingBadges180Tests : IDisposable
{
	public void Dispose() => OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty;

	[Fact]
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	public async Task A_trending_tile_wears_its_titles_award_badges_and_drops_them_when_the_lists_go()
	{
		using var rig = new Outside.TrendingRig(trakt: true);
		rig.Fake.Trakt["movies/trending"] = [("603", null, null, "The Matrix")];
		await rig.Sources.RefreshAsync();
		var matrix = Outside.TrendingRig.Entry("The Matrix", IndexKind.Movie, tmdb: "603");
		var library = Outside.TrendingRig.Index(matrix, Outside.TrendingRig.Entry("Dune", IndexKind.Movie, tmdb: "438631"));
		var collections = new TrendingCollections(rig.Sources);
		OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty with
		{
			Lists = [new OutsideList("award-golden-lion", OutsideSourceId.Wikidata, OutsideListKind.Award, "Golden Lion winners", "w", [new OutsideListItem(new OutsideRef(Tmdb: "603", Kind: IndexKind.Movie))])],
		};

		var films = Assert.Single(collections.For(library));
		Assert.Equal(["Golden Lion winner"], films.Badges.Select(x => x.Text));
		Assert.Equal(["Golden Lion winner"], collections.HomeRowFor(library)!.Badges.Select(x => x.Text));
		Assert.EndsWith(", Golden Lion winner", films.TileAutomationName, StringComparison.Ordinal);

		OutsideListsSnapshot.Shared = OutsideListsSnapshot.Empty; // Wikidata turned off: the badges go at the next build
		Assert.Empty(Assert.Single(collections.For(library)).Badges);
		Assert.Empty(collections.HomeRowFor(library)!.Badges);
	}
}
