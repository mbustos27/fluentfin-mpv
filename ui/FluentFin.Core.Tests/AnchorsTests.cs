using System.Diagnostics;
using FluentFin.Core.SmartCollections;
using FluentFin.Core.SmartCollections.Anchors;
using FluentFin.Core.SmartCollections.Builders;
using Xunit.Abstractions;

namespace FluentFin.Core.Tests;

/// <summary>
/// Nixie 1.8 (P1-2): the anchors (finished, rewatched, favourite; films, shows and anime) and the three personal slots made
/// from them: "Next in", "More like" (the report's "Because you watched") and "More from". What the user reads: each slot's
/// title, reason line and Narrator name.
/// </summary>
public sealed class AnchorsTests(ITestOutputHelper output)
{
	// a Tuesday, in the middle of a three-day block (Hold.Block)
	private static readonly DateTimeOffset Now = new(2026, 11, 24, 20, 0, 0, TimeSpan.FromHours(-8));
	private static int s_next;

	private static Guid NextId(int kind) => Guid.Parse($"{Interlocked.Increment(ref s_next):D8}-0000-0000-0000-{kind:D12}");

	internal static IndexEntry Film(string name, int year, string[]? genres = null, string[]? tags = null, string? director = null, string[]? cast = null,
		string? franchise = null, bool played = false, bool favourite = false, double? rating = 7.0, string[]? countries = null, string[]? studios = null, string? premiere = null) => new()
		{
			Id = NextId(1),
			Name = name,
			Kind = IndexKind.Movie,
			Year = year,
			PremiereDate = premiere,
			Genres = genres ?? [],
			Tags = tags ?? [],
			Countries = countries ?? ["United States of America"],
			Studios = studios ?? [],
			TmdbCollection = franchise,
			Played = played,
			Favorite = favourite,
			Rating = rating,
			RunTimeTicks = TimeSpan.FromMinutes(110).Ticks,
			PrimaryTag = "p",
			People = [.. (director is null ? [] : new[] { new IndexPerson(director, null, "Director") }), .. (cast ?? []).Select(x => new IndexPerson(x, null, "Actor"))],
		};

	internal static IndexEntry Series(string name, int year, string[]? genres = null, string[]? tags = null, string[]? cast = null, int unplayed = 10,
		bool played = false, bool favourite = false, string[]? countries = null, double? rating = 8.0) => new()
		{
			Id = NextId(2),
			Name = name,
			Kind = IndexKind.Series,
			Year = year,
			Genres = genres ?? [],
			Tags = tags ?? [],
			Countries = countries ?? ["United States of America"],
			Unplayed = unplayed,
			Played = played,
			Favorite = favourite,
			Rating = rating,
			PrimaryTag = "p",
			People = [.. (cast ?? []).Select(x => new IndexPerson(x, null, "Actor"))],
		};

	/// <summary>The anchors' facts a test gives: plays and dates by title, people from the entries (top-3 billed).</summary>
	internal sealed class Facts(Dictionary<Guid, TitlePlays>? plays = null, bool peopleKnown = true) : IAnchorSource
	{
		public Dictionary<Guid, TitlePlays> Plays { get; } = plays ?? [];

		public TitlePlays PlaysOf(IndexEntry title) => Plays.TryGetValue(title.Id, out var p) ? p : new TitlePlays(title.Played ? 1 : 0, null);

		public IReadOnlyList<IndexPerson> BilledOf(IndexEntry title) => IndexAnchorSource.TopBilled(title.People);

		public bool PeopleKnown => peopleKnown;
	}

	private static IndexSnapshot Index(IEnumerable<IndexEntry> entries, Dictionary<Guid, SeriesPlays>? plays = null) =>
		new([], [.. entries], plays ?? [], HasPeople: true, Now);

	private static List<SmartCollection> Slots(IndexSnapshot index, IAnchorSource facts, DateTimeOffset? now = null, BuildContext? context = null)
	{
		var built = context ?? BuildContext.For(index, now ?? Now);
		return [.. new ISmartCollectionBuilder[] { new NextInFranchiseBuilder(_ => facts), new MoreLikeBuilder(_ => facts), new MoreFromBuilder(_ => facts) }
			.SelectMany(x => x.Build(built))];
	}

	// the fixture library: a director's films, a franchise, anime and other shows, and dramas enough that a genre says little
	private sealed class Library
	{
		public IndexEntry Arrival = Film("Arrival", 2016, ["Science Fiction", "Drama"], ["alien"], "Denis Villeneuve", ["Amy Adams", "Jeremy Renner", "Forest Whitaker"], rating: 7.9);
		public List<IndexEntry> Villeneuve =
		[
			Film("Sicario", 2015, ["Crime", "Thriller"], ["revenge"], "Denis Villeneuve", ["Emily Blunt", "Benicio del Toro", "Josh Brolin"], rating: 7.6),
			Film("Prisoners", 2013, ["Crime", "Thriller", "Drama"], ["detective"], "Denis Villeneuve", ["Hugh Jackman", "Jake Gyllenhaal", "Viola Davis"], rating: 8.1),
			Film("Enemy", 2013, ["Mystery", "Thriller"], [], "Denis Villeneuve", ["Jake Gyllenhaal", "Mélanie Laurent", "Sarah Gadon"], rating: 6.9),
			Film("Incendies", 2010, ["Drama", "Mystery", "War"], [], "Denis Villeneuve", ["Lubna Azabal", "Mélissa Désormeaux-Poulin", "Maxim Gaudette"], rating: 8.3, countries: ["Canada"]),
			Film("Blade Runner 2049", 2017, ["Science Fiction", "Drama"], ["cyberpunk", "dystopia"], "Denis Villeneuve", ["Ryan Gosling", "Harrison Ford", "Ana de Armas"], rating: 8.0),
			Film("Dune", 2021, ["Science Fiction", "Adventure"], ["space"], "Denis Villeneuve", ["Timothée Chalamet", "Rebecca Ferguson", "Oscar Isaac"], rating: 7.8),
		];

		public IndexEntry Mi1 = Film("Mission: Impossible", 1996, ["Action", "Thriller"], ["spy"], "Brian De Palma", ["Tom Cruise", "Jon Voight", "Emmanuelle Béart"], "87359", premiere: "1996-05-22");
		public IndexEntry Mi2 = Film("Mission: Impossible II", 2000, ["Action", "Thriller"], ["spy"], "John Woo", ["Tom Cruise", "Dougray Scott", "Thandiwe Newton"], "87359", premiere: "2000-05-24");
		public IndexEntry Mi3 = Film("Mission: Impossible III", 2006, ["Action", "Thriller"], ["spy"], "J.J. Abrams", ["Tom Cruise", "Philip Seymour Hoffman", "Ving Rhames"], "87359", premiere: "2006-05-03");
		public IndexEntry Mi4 = Film("Mission: Impossible - Ghost Protocol", 2011, ["Action", "Thriller"], ["spy"], "Brad Bird", ["Tom Cruise", "Jeremy Renner", "Simon Pegg"], "87359", premiere: "2011-12-07");

		public IndexEntry Bebop = Series("Cowboy Bebop", 1998, ["Animation", "Action", "Science Fiction"], ["anime", "space"], ["Koichi Yamadera", "Unsho Ishizuka", "Megumi Hayashibara"], unplayed: 2, countries: ["Japan"]);
		public List<IndexEntry> Anime =
		[
			Series("Trigun", 1998, ["Animation", "Action", "Science Fiction"], ["anime", "western"], ["Masaya Onosaka", "Hiromi Tsuru", "Satsuki Yukino"], countries: ["Japan"]),
			Series("Space Dandy", 2014, ["Animation", "Comedy", "Science Fiction"], ["anime", "space"], ["Ryohei Kimura", "Junichi Suwabe", "Megumi Hayashibara"], countries: ["Japan"]),
			Series("Planetes", 2003, ["Animation", "Drama", "Science Fiction"], ["anime", "space"], ["Hochu Otsuka", "Ami Koshimizu", "Satsuki Yukino"], countries: ["Japan"]),
			Series("Samurai Champloo", 2004, ["Animation", "Action"], ["anime", "samurai"], ["Kazuya Nakai", "Ayako Kawasumi", "Tatsuya Sato"], countries: ["Japan"]),
			Series("Outlaw Star", 1998, ["Animation", "Action", "Science Fiction"], ["anime", "space"], ["Shigeru Shibuya", "Rica Fukami", "Yuko Nagashima"], countries: ["Japan"]),
			Series("Ghost in the Shell: Stand Alone Complex", 2002, ["Animation", "Science Fiction", "Crime"], ["anime", "cyberpunk"], ["Atsuko Tanaka", "Akio Otsuka", "Koichi Yamadera"], countries: ["Japan"]),
			Series("Psycho-Pass", 2012, ["Animation", "Crime", "Science Fiction"], ["anime", "dystopia"], ["Kana Hanazawa", "Tomokazu Seki", "Takahiro Sakurai"], countries: ["Japan"]),
			Series("Steins;Gate", 2011, ["Animation", "Science Fiction", "Drama"], ["anime", "time travel"], ["Mamoru Miyano", "Asami Imai", "Kana Hanazawa"], countries: ["Japan"]),
			Series("Fullmetal Alchemist: Brotherhood", 2009, ["Animation", "Adventure", "Drama"], ["anime", "magic"], ["Romi Park", "Rie Kugimiya", "Shinichiro Miki"], countries: ["Japan"]),
			Series("Neon Genesis Evangelion", 1995, ["Animation", "Drama", "Science Fiction"], ["anime", "robot"], ["Megumi Ogata", "Megumi Hayashibara", "Yuko Miyamura"], countries: ["Japan"]),
		];

		public List<IndexEntry> Shows =
		[
			Series("The Wire", 2002, ["Crime", "Drama"], ["detective"], ["Dominic West", "Lance Reddick", "Sonja Sohn"]),
			Series("Breaking Bad", 2008, ["Crime", "Drama"], ["dark comedy"], ["Bryan Cranston", "Aaron Paul", "Anna Gunn"]),
			Series("The Expanse", 2015, ["Science Fiction", "Drama"], ["space"], ["Steven Strait", "Dominique Tipper", "Wes Chatham"]),
			Series("Firefly", 2002, ["Science Fiction", "Western"], ["space", "western"], ["Nathan Fillion", "Gina Torres", "Alan Tudyk"]),
			Series("Fargo", 2014, ["Crime", "Drama"], ["dark comedy"], ["Billy Bob Thornton", "Allison Tolman", "Colin Hanks"]),
			Series("Mad Men", 2007, ["Drama"], [], ["Jon Hamm", "Elisabeth Moss", "Vincent Kartheiser"]),
		];

		// films of one decade and genre, sharing nothing rare: a genre alone says little
		public List<IndexEntry> Dramas = [.. Enumerable.Range(1, 40).Select(i => Film($"A Quiet Drama {i}", 1990 + (i % 30), ["Drama"], [], $"Director {i}", [$"Lead {i}", $"Second {i}", $"Third {i}"]))];

		public IEnumerable<IndexEntry> All => [Arrival, .. Villeneuve, Mi1, Mi2, Mi3, Mi4, Bebop, .. Anime, .. Shows, .. Dramas];

		public Dictionary<Guid, SeriesPlays> Plays => new() { [Bebop.Id] = new SeriesPlays(24, Now.AddDays(-1)) };
	}

	[Fact]
	public void ThreeFinishedTitles_ShowTheAnchoredSlots_WithTheirReasonLines()
	{
		var library = new Library();
		library.Arrival = library.Arrival with { Played = true };
		library.Mi1 = library.Mi1 with { Played = true };
		var facts = new Facts(new()
		{
			[library.Arrival.Id] = new TitlePlays(1, Now.AddDays(-20)),
			[library.Mi1.Id] = new TitlePlays(1, Now.AddDays(-10)),
		});
		var index = Index(library.All, library.Plays); // Cowboy Bebop: 24 of 26 episodes played, yesterday

		var slots = Slots(index, facts);
		foreach (var slot in slots)
		{
			output.WriteLine($"{slot.Kind}: {slot.Title} | {slot.ReasonText} | {slot.TileAutomationName}");
		}

		var next = Assert.Single(slots, x => x.Kind == SmartCollectionKind.NextIn);
		Assert.Equal("Next in Mission: Impossible", next.Title);
		Assert.Equal("Next: Mission: Impossible II (2000) · 1 of 4 films watched", next.ReasonText);
		Assert.Equal([library.Mi1.Id, library.Mi2.Id, library.Mi3.Id, library.Mi4.Id], next.ItemIds); // release order
		Assert.True(next.HasOwnOrder);
		Assert.Equal("Made by Nixie · Next in a franchise", next.Eyebrow);

		var more = Assert.Single(slots, x => x.Kind == SmartCollectionKind.MoreFrom);
		Assert.Equal("More from Denis Villeneuve", more.Title);
		Assert.Equal("You finished Arrival · 6 more films, none watched", more.ReasonText);
		Assert.Equal("More from Denis Villeneuve, made by Nixie, more from a person, You finished Arrival, 6 more films, none watched", more.TileAutomationName);
		Assert.Equal("More from Denis Villeneuve, made by Nixie, more from a person, 6 films", more.AutomationName); // a chip's short name
		Assert.Equal(["Incendies", "Prisoners", "Blade Runner 2049", "Dune", "Sicario", "Enemy"], more.ItemIds.Select(id => Name(index, id))); // best first

		// the anime finished yesterday leads; its alike shows are anime and space shows, never the dramas
		var like = Assert.Single(slots, x => x.Kind == SmartCollectionKind.MoreLike);
		Assert.Equal("More like Cowboy Bebop", like.Title);
		Assert.Equal("Shares space, anime and Megumi Hayashibara · 12 shows", like.ReasonText);
		Assert.All(like.ItemIds, id => Assert.Contains(id, library.Anime.Select(x => x.Id).Concat(library.Shows.Select(x => x.Id))));
		Assert.DoesNotContain(library.Bebop.Id, like.ItemIds);

		// the titles keep the rule (never "Your…", nothing about the viewer); no line says "your" (rule 11)
		Assert.All(slots, x => Assert.Null(NixieTitles.Check(x.Title)));
		Assert.All(slots, x => Assert.DoesNotContain("your", $"{x.Title} {x.ReasonText} {x.Why}", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void NothingFinished_NoAnchoredSlots()
	{
		var library = new Library();
		var index = Index(library.All); // nothing played, no favourites, no episodes played

		Assert.Empty(Slots(index, new Facts()));
		Assert.Empty(AnchorFinder.Of(BuildContext.For(index, Now), new Facts()));
	}

	[Fact]
	public void NothingFinished_ReadsNoTitlesPeople()
	{
		// every published build (Home, the Collections and Libraries pages) runs the three: with no anchor they cost nothing
		var facts = new CountingFacts();
		Assert.Empty(Slots(Index(new Library().All), facts));
		Assert.Equal(0, facts.Billed);
	}

	private sealed class CountingFacts : IAnchorSource
	{
		public int Billed;

		public TitlePlays PlaysOf(IndexEntry title) => new(title.Played ? 1 : 0, null);

		public IReadOnlyList<IndexPerson> BilledOf(IndexEntry title)
		{
			Interlocked.Increment(ref Billed);
			return IndexAnchorSource.TopBilled(title.People);
		}

		public bool PeopleKnown => true;
	}

	[Fact]
	public void AnEmptyOrOneTitleIndex_MakesNothing_AndNeverThrows()
	{
		Assert.Empty(Slots(Index([]), new Facts()));
		Assert.Empty(Slots(Index([Film("Alone", 2001, played: true)]), new Facts()));

		// a damaged saved entry: no name, null lists
		var damaged = new IndexEntry { Id = NextId(9), Name = null!, Kind = IndexKind.Movie, Played = true, People = null!, Genres = null!, Tags = null! };
		Assert.Empty(Slots(Index([damaged, Film("Other", 2002)]), new Facts()));
	}

	[Fact]
	public void AShowIsFinishedAtNinetyPercentOfItsEpisodes_AnimeAlike()
	{
		var nine = Series("Mushishi", 2005, ["Animation"], ["anime"], unplayed: 1, countries: ["Japan"]); // 9 of 10 played
		var eight = Series("Monster", 2004, ["Animation"], ["anime"], unplayed: 2, countries: ["Japan"]); // 8 of 10
		var whole = Series("The Wire", 2002, ["Crime"], unplayed: 0, played: true);
		var plays = new Dictionary<Guid, SeriesPlays> { [nine.Id] = new(9, Now.AddDays(-4)), [eight.Id] = new(8, Now.AddDays(-2)) };

		var anchors = AnchorFinder.Of(BuildContext.For(Index([nine, eight, whole], plays), Now), new Facts());

		Assert.Equal(["Mushishi", "The Wire"], anchors.Select(x => x.Title.Name)); // the dated first, then the undated
		Assert.Equal(Now.AddDays(-4), anchors[0].When);
		Assert.Equal("You watched 9 of 10 episodes of Mushishi", anchors[0].Said); // 1.8.1 (fix 3): an episode left, said
		Assert.Equal("You finished The Wire", anchors[1].Said);
	}

	[Fact]
	public void AFilmStartedTwice_IsAnAnchorOnlyWhenFinished_NeverSaidTwice_AndCopiesAreOneTitle()
	{
		// review (Major): the server's play count counts starts. Heat: finished, started twice (two sittings, or a rewatch: the
		// count can't tell). Abandoned: started twice, never finished. As the server has them
		var heat = Film("Heat", 1995, played: true) with { Tmdb = "949", PlayCount = 2 };
		var heat4K = Film("Heat", 1995, played: true) with { Tmdb = "949", PrimaryTag = null, PlayCount = 2 };
		var abandoned = Film("Abandoned", 2010, ["Drama"], director: "Dee Rector") with { PlayCount = 2, LastPlayed = Now.AddDays(-1) };
		var ronin = Film("Ronin", 1998, favourite: true);
		var facts = new Facts(new()
		{
			[heat.Id] = new TitlePlays(2, Now.AddDays(-3)),
			[abandoned.Id] = new TitlePlays(2, Now.AddDays(-1)),
		});

		var anchors = AnchorFinder.Of(BuildContext.For(Index([heat, heat4K, abandoned, ronin]), Now), facts);

		Assert.Equal(["Heat", "Ronin"], anchors.Select(x => x.Title.Name)); // Heat once, however many files; never Abandoned
		Assert.Equal(AnchorWhy.Finished | AnchorWhy.Rewatched, anchors[0].Why);
		Assert.Equal("You finished Heat", anchors[0].Said); // no count named
		Assert.Equal(AnchorWhy.Favourite, anchors[1].Why);
		Assert.Equal("A favourite: Ronin", anchors[1].Said);

		// the reviewer's probe: Abandoned's director with six more films makes no "More from", and no line says "twice"
		var others = Enumerable.Range(1, 6).Select(i => Film($"Other {i}", 2001 + i, ["Drama"], director: "Dee Rector")).ToList();
		var slots = Slots(Index([abandoned, .. others]), facts);
		Assert.DoesNotContain(slots, x => x.Kind == SmartCollectionKind.MoreFrom);
		Assert.All(Slots(Index([heat, abandoned, .. others]), facts), x => Assert.DoesNotContain("twice", x.ReasonText, StringComparison.Ordinal));
	}

	[Fact]
	public void AFranchiseFilmStartedTwiceAndNeverFinished_StartsNoFranchise()
	{
		var first = Film("Saga", 2001, franchise: "5", premiere: "2001-01-01") with { PlayCount = 2, LastPlayed = Now.AddDays(-1) };
		var second = Film("Saga II", 2003, franchise: "5", premiere: "2003-01-01");
		var facts = new Facts(new() { [first.Id] = new TitlePlays(2, Now.AddDays(-1)) });

		Assert.DoesNotContain(Slots(Index([first, second]), facts), x => x.Kind == SmartCollectionKind.NextIn);
	}

	[Fact]
	public void TheSlotIsTheSameAllDay_AFreshFinishLeadsThreeCalendarDays()
	{
		// review (Minor): the reviewer's probe: a title finished at 21:00 three days before led at 09:00 and not at 22:00
		var entries = new List<IndexEntry>();
		var facts = new Facts();
		var day = new DateTimeOffset(Now.Date, Now.Offset); // Tuesday 00:00
		for (var d = 1; d <= 5; d++)
		{
			var anchor = Film($"Anchor {d}", 2000 + d, ["Drama"], director: $"Director {d}", played: true);
			entries.Add(anchor);
			facts.Plays[anchor.Id] = new TitlePlays(1, d == 5 ? day.AddDays(-3).AddHours(21) : Now.AddDays(-20 - d));
			entries.AddRange(Enumerable.Range(1, 5).Select(i => Film($"Film {d}.{i}", 2000 + i, ["Drama"], director: $"Director {d}")));
		}

		var index = Index(entries);
		string First(DateTimeOffset at) => Slots(index, facts, at).First(x => x.Kind == SmartCollectionKind.MoreFrom).Title;

		// Saturday 21:00: fresh on Saturday, Sunday and Monday, all day; on Tuesday it takes its turn like the rest, all day
		Assert.Equal("More from Director 5", First(day.AddDays(-1).AddMinutes(30)));
		Assert.Equal("More from Director 5", First(day.AddDays(-1).AddHours(23).AddMinutes(30)));
		Assert.Single(new[] { 0.5, 9, 22, 23.5 }.Select(h => First(day.AddHours(h))).Distinct());
	}

	[Fact]
	public void AShowFinishedAtNinetyPercent_IsWatched_InMoreFromAndMoreLike()
	{
		// review (Minor): the reviewer's probe: a show at 30 of 31 episodes made More from's floor of 5 as unwatched
		var anchor = Film("Anchor", 2010, ["Drama"], cast: ["Star Person", "B", "C"], played: true);
		var films = Enumerable.Range(1, 4).Select(i => Film($"Film {i}", 2001 + i, ["Drama"], cast: ["Star Person", "X", "Y"])).ToList();
		var show = Series("Long Show", 2005, ["Drama"], cast: ["Star Person", "Q", "R"], unplayed: 1);
		var index = Index([anchor, .. films, show], new Dictionary<Guid, SeriesPlays> { [show.Id] = new SeriesPlays(30, Now.AddDays(-10)) });

		Assert.DoesNotContain(Slots(index, new Facts()), x => x.Kind == SmartCollectionKind.MoreFrom);

		// one more film: five unwatched; the show is with the watched, at the end
		var fifth = Film("Film 5", 2009, ["Drama"], cast: ["Star Person", "X", "Y"]);
		var more = Assert.Single(Slots(Index([anchor, .. films, fifth, show], new Dictionary<Guid, SeriesPlays> { [show.Id] = new SeriesPlays(30, Now.AddDays(-10)) }), new Facts()),
			x => x.Kind == SmartCollectionKind.MoreFrom);
		Assert.Equal("You finished Anchor · 6 more titles, 5 unwatched", more.ReasonText);
		Assert.Equal(show.Id, more.ItemIds[^1]);

		// More like leaves it out with the watched: a finished show is no suggestion
		var dramas = Enumerable.Range(1, 9).Select(i => Series($"Drama Show {i}", 2000 + i, ["Drama"], ["detective"])).ToList();
		var liked = Series("Liked", 2003, ["Drama"], ["detective"], played: true);
		var finished = Series("Nearly Done", 2004, ["Drama"], ["detective"], unplayed: 1);
		var comedies = Enumerable.Range(1, 20).Select(i => Series($"Comedy Show {i}", 2000 + i, ["Comedy"])).ToList(); // so "detective" says something
		var like = Assert.Single(Slots(Index([liked, finished, .. dramas, .. comedies], new Dictionary<Guid, SeriesPlays> { [finished.Id] = new SeriesPlays(30, Now.AddDays(-40)) }),
			new Facts()), x => x.Title == "More like Liked");
		Assert.Equal(9, like.ItemIds.Count);
		Assert.DoesNotContain(finished.Id, like.ItemIds);
	}

	[Fact]
	public void MoreFrom_CountsDirectorsAndTheTopThreeBilledOnly()
	{
		// the anchor's fourth-billed actor leads six films here; its lead is billed fourth in six others: neither is "More from"
		var anchor = Film("The Anchor", 2010, ["Drama"], director: "Ann Director", cast: ["Lead One", "Second Two", "Third Three", "Fourth Four"], played: true);
		List<IndexEntry> fourthLeads = [.. Enumerable.Range(1, 6).Select(i => Film($"Fourth Leads {i}", 2000 + i, ["Drama"], cast: ["Fourth Four", "X", "Y"]))];
		List<IndexEntry> leadIsFourth = [.. Enumerable.Range(1, 6).Select(i => Film($"Lead Is Fourth {i}", 2000 + i, ["Drama"], cast: ["A", "B", "C", "Lead One"]))];

		Assert.DoesNotContain(Slots(Index([anchor, .. fourthLeads, .. leadIsFourth]), new Facts()), x => x.Kind == SmartCollectionKind.MoreFrom);

		// billed second in them, the lead is "More from"
		List<IndexEntry> leadIsSecond = [.. Enumerable.Range(1, 6).Select(i => Film($"Lead Is Second {i}", 2000 + i, ["Drama"], cast: ["A", "Lead One", "C"]))];
		var more = Assert.Single(Slots(Index([anchor, .. leadIsSecond]), new Facts()), x => x.Kind == SmartCollectionKind.MoreFrom);
		Assert.Equal("More from Lead One", more.Title);
	}

	[Fact]
	public void MoreFrom_NeedsFiveOtherTitlesNotWatched_AndPutsTheWatchedLast()
	{
		var anchor = Film("First Film", 2001, ["Drama"], director: "Dee Rector", played: true);
		List<IndexEntry> others = [.. Enumerable.Range(1, 6).Select(i => Film($"Other {i}", 2001 + i, ["Drama"], director: "Dee Rector", rating: 6 + (i * 0.3), played: i <= 2))];

		Assert.DoesNotContain(Slots(Index([anchor, .. others]), new Facts()), x => x.Kind == SmartCollectionKind.MoreFrom); // 4 not watched

		var fifth = Film("Other 7", 2009, ["Drama"], director: "Dee Rector", rating: 5);
		var index = Index([anchor, .. others, fifth]);
		var facts = new Facts(new() { [anchor.Id] = new TitlePlays(1, Now.AddDays(-1)) }); // finished yesterday: the first anchor
		var more = Assert.Single(Slots(index, facts), x => x.Kind == SmartCollectionKind.MoreFrom);
		Assert.Equal("You finished First Film · 7 more films, 5 unwatched", more.ReasonText);
		Assert.Equal(["Other 6", "Other 5", "Other 4", "Other 3", "Other 7", "Other 2", "Other 1"], more.ItemIds.Select(id => Name(index, id))); // the watched last
	}

	[Fact]
	public void MoreFrom_WaitsForThePeople()
	{
		var anchor = Film("First Film", 2001, ["Drama"], director: "Dee Rector", played: true);
		var index = Index([anchor, .. Enumerable.Range(1, 6).Select(i => Film($"Other {i}", 2001 + i, ["Drama"], director: "Dee Rector"))]);
		var context = BuildContext.For(index with { HasPeople = false }, Now); // an index with no people pass yet

		Assert.DoesNotContain(Slots(index, new Facts(peopleKnown: false), context: context), x => x.Kind == SmartCollectionKind.MoreFrom);
		Assert.Equal("no people in the index yet", context.Gates.NoteOf(SmartCollectionKind.MoreFrom));
		Assert.Single(Slots(index, new Facts()), x => x.Kind == SmartCollectionKind.MoreFrom);
	}

	// sixty films, half 1990s dramas and half newer comedies, every one American: a genre, a decade or a country alone is
	// common here
	private static List<IndexEntry> Common() =>
	[
		.. Enumerable.Range(1, 30).Select(i => Film($"Nineties Drama {i}", 1990 + (i % 10), ["Drama"], director: $"Drama Director {i}")),
		.. Enumerable.Range(1, 30).Select(i => Film($"Newer Comedy {i}", 2000 + (i % 20), ["Comedy"], director: $"Comedy Director {i}")),
	];

	[Fact]
	public void MoreLike_NeedsARareSharedFeature_NotAGenreAndADecade()
	{
		var anchor = Film("The Drama", 1995, ["Drama"], director: "Someone Else", played: true);
		var common = Common();

		Assert.DoesNotContain(Slots(Index([anchor, .. common]), new Facts()), x => x.Kind == SmartCollectionKind.MoreLike);

		// a keyword the anchor shares with nine of them: those nine, and the reason names it first
		var heists = common.Take(9).Select(x => x with { Tags = ["heist"] }).ToList();
		var index = Index([anchor with { Tags = ["heist"] }, .. heists, .. common.Skip(9)]);
		var like = Assert.Single(Slots(index, new Facts()), x => x.Kind == SmartCollectionKind.MoreLike);
		Assert.Equal("More like The Drama", like.Title);
		Assert.Equal("Shares heists, drama and the 1990s · 9 films", like.ReasonText);
		Assert.Equal(heists.Select(x => x.Id).Order(), like.ItemIds.Order());
	}

	// 1.8.1 (the owner's library, fix 10b): shows with no keywords on the server are alike by genre, network and decade, and the
	// network led every reason ("Shares FX, horror and the 2010s"): a network is never named first, and never alone
	[Fact]
	public void MoreLike_OfAShow_NeverNamesItsNetworkFirst()
	{
		var anchor = Series("Night Shift", 2019, ["Horror", "Comedy"], played: true) with { Studios = ["FX"] };
		var alike = Enumerable.Range(1, 9).Select(i => Series($"FX Horror {i}", 2010 + i, ["Horror"]) with { Studios = ["FX"] }).ToList();
		var others = Enumerable.Range(1, 120).Select(i => Series($"Other {i}", 1990 + (i % 20), ["Drama"]) with { Studios = [$"Network {i % 7}"] }).ToList(); // FX and horror rare

		var like = Assert.Single(Slots(Index([anchor, .. alike, .. others]), new Facts()), x => x.Kind == SmartCollectionKind.MoreLike);

		Assert.StartsWith("Shares horror", like.ReasonText, StringComparison.Ordinal);
		Assert.Contains("FX", like.ReasonText, StringComparison.Ordinal); // after the others
		Assert.DoesNotContain("Shares FX", like.ReasonText, StringComparison.Ordinal);
		var similarity = TitleSimilarity.Of([anchor, .. alike, .. others], new Facts());
		Assert.NotEqual(TitleSimilarity.FeatureKind.Studio, similarity.Shared(anchor, alike, 3)[0].Kind);
		// a network the only thing two shows share: not named at all
		var bare = anchor with { Genres = [], Year = null };
		var twin = alike[0] with { Genres = [], Year = null };
		Assert.Empty(TitleSimilarity.Of([bare, twin, .. others], new Facts()).Shared(bare, [twin], 3));
	}

	// 1.8.1 (the owner's library, fix 3): a show the 90 % rule counts finished, with episodes left, says how far ("You finished
	// What We Do in the Shadows" at 36 of 39 was untrue); one played to its end, and a film, still say "You finished"
	[Fact]
	public void AShowWithEpisodesLeft_SaysHowFar_NotYouFinished()
	{
		var shadows = Series("What We Do in the Shadows", 2019, unplayed: 3);
		var done = Series("Chernobyl", 2019, unplayed: 0, played: true);
		var film = Film("Arrival", 2016, played: true);
		var plays = new Dictionary<Guid, SeriesPlays> { [shadows.Id] = new(36, Now.AddDays(-2)), [done.Id] = new(5, Now.AddDays(-20)) };

		var anchors = AnchorFinder.Of(BuildContext.For(Index([shadows, done, film], plays), Now), new Facts());

		Assert.Equal("You watched 36 of 39 episodes of What We Do in the Shadows", anchors.Single(x => x.Title.Id == shadows.Id).Said);
		Assert.Equal("You finished Chernobyl", anchors.Single(x => x.Title.Id == done.Id).Said);
		Assert.Equal("You finished Arrival", anchors.Single(x => x.Title.Id == film.Id).Said);
		Assert.Equal("You watched 1,204 of 1,300 episodes of Long Show", ReasonFacts.WatchedEpisodes("Long Show", 1204, 1300));
	}

	// 1.8.1 (the owner's library, fix 2): More like leaves out what the user started, not only what they finished: a show with
	// any episode played (It's Always Sunny, 168 episodes in, was in two of them) and a film begun
	[Fact]
	public void MoreLike_LeavesOutWhatTheUserStarted_AShowWithAnEpisodePlayed_AFilmBegun()
	{
		var library = new Library();
		library.Bebop = library.Bebop with { Played = true };
		var trigun = library.Anime[0];
		var plays = library.Plays;
		plays[trigun.Id] = new SeriesPlays(1, Now.AddDays(-30)); // one episode of Trigun
		var shows = Assert.Single(Slots(Index(library.All, plays), new Facts()), x => x.Kind == SmartCollectionKind.MoreLike && x.Title == "More like Cowboy Bebop");
		Assert.DoesNotContain(trigun.Id, shows.ItemIds);
		Assert.Equal("Shares space, anime and Megumi Hayashibara · 11 shows", shows.ReasonText); // of 12

		var anchor = Film("The Drama", 1995, ["Drama"], ["heist"], director: "Someone Else", played: true);
		var common = Common();
		var heists = common.Take(10).Select(x => x with { Tags = ["heist"] }).ToList();
		var begun = new Facts(new() { [heists[0].Id] = new TitlePlays(1, Now.AddDays(-2)) }); // a heist film started two days ago
		var films = Assert.Single(Slots(Index([anchor, .. heists, .. common.Skip(10)]), begun), x => x.Kind == SmartCollectionKind.MoreLike);
		Assert.DoesNotContain(heists[0].Id, films.ItemIds);
		Assert.Equal(9, films.ItemIds.Count);
	}

	// 1.8.1 (BH-180-q3): a country is never named as a trait the titles share ("Shares heists, Germany and drama"): it counts
	// toward how alike they are, and the reason names the next feature instead
	[Fact]
	public void MoreLike_NamesNoCountryAsASharedTrait()
	{
		var anchor = Film("The Drama", 1995, ["Drama"], ["heist"], director: "Someone Else", played: true, countries: ["Germany"]);
		var common = Common();
		var german = common.Take(9).Select(x => x with { Tags = ["heist"], Countries = ["Germany"] }).ToList();

		var like = Assert.Single(Slots(Index([anchor, .. german, .. common.Skip(9)]), new Facts()), x => x.Kind == SmartCollectionKind.MoreLike);

		Assert.Equal("Shares heists, drama and the 1990s · 9 films", like.ReasonText);
		Assert.DoesNotContain("Germany", like.TileAutomationName, StringComparison.Ordinal);
		Assert.Equal(german.Select(x => x.Id).Order(), like.ItemIds.Order()); // still the German heists: alike as before
	}

	// 1.8.1 (BH-180-q3): a franchise with no TMDb name whose films' names share no words was "Next in The Frontier and the
	// Mirror films": named by its first film now; one whose names share words keeps them
	[Fact]
	public void NextIn_WithNoFranchiseName_IsNamedByItsFirstFilm()
	{
		var first = Film("The Frontier and the Mirror", 1990, franchise: "4242", played: true, premiere: "1990-03-01");
		var second = Film("Glass Harbour", 1994, franchise: "4242", premiere: "1994-03-01");
		var next = Assert.Single(Slots(Index([first, second]), new Facts()), x => x.Kind == SmartCollectionKind.NextIn);
		Assert.Equal("Next after The Frontier and the Mirror", next.Title);
		Assert.Null(NixieTitles.Check(next.Title));
		Assert.Equal("Next: Glass Harbour (1994) · 1 of 2 films watched", next.ReasonText);

		var saga = new[] { Film("Dragon Saga: Dawn", 2001, franchise: "777", played: true), Film("Dragon Saga: Dusk", 2003, franchise: "777") };
		Assert.Equal("Next in Dragon Saga", Assert.Single(Slots(Index(saga), new Facts()), x => x.Kind == SmartCollectionKind.NextIn).Title);
		Assert.Equal("The Frontier and the Mirror films", FranchiseNames.Name([first, second])); // the Franchise row's own name, as before
	}

	// 1.8.1 (BH-181-m1): "Next after Return to Avalon" offered Return to Avalon. A franchise the user began past its first film
	// (only the newest played, one begun) is named by the film they watched in it, never by the film it offers
	[Fact]
	public void NextIn_BegunPastItsFirstFilm_NeverNamesTheFilmItOffers()
	{
		var avalon = Film("Return to Avalon", 1995, franchise: "9103", premiere: "1995-04-01");
		var harbour = Film("Glass Harbour", 2000, franchise: "9103", premiere: "2000-04-01");
		var begun = Film("Under a Copper Sky", 2011, franchise: "9103", premiere: "2011-04-01");
		var orchard = Film("The Restless Orchard", 2015, franchise: "9103", played: true, premiere: "2015-04-01");
		var facts = new Facts(new() { [begun.Id] = new TitlePlays(1, Now.AddDays(-1)), [orchard.Id] = new TitlePlays(1, Now.AddDays(-5)) });

		var next = Assert.Single(Slots(Index([orchard, begun, harbour, avalon]), facts), x => x.Kind == SmartCollectionKind.NextIn);

		Assert.Equal("Next: Return to Avalon (1995) · 1 of 4 films watched", next.ReasonText);
		Assert.DoesNotContain("Return to Avalon", next.Title, StringComparison.OrdinalIgnoreCase);
		Assert.DoesNotContain("Return to Avalon", next.TileAutomationName.Split(',')[0], StringComparison.OrdinalIgnoreCase); // what Narrator says first
		Assert.Equal("Next after The Restless Orchard", next.Title);
		Assert.Null(NixieTitles.Check(next.Title));

		// two films watched, the first not: named by the one watched last, by its play date, in every three-day block (the anchors'
		// order turns with the block)
		var harbourPlayed = harbour with { Played = true };
		var lastWeek = new Facts(new() { [harbour.Id] = new TitlePlays(1, Now.AddDays(-10)), [orchard.Id] = new TitlePlays(1, Now.AddDays(-40)) });
		foreach (var day in new[] { 0, 3, 6 })
		{
			var named = Assert.Single(Slots(Index([orchard, begun, harbourPlayed, avalon]), lastWeek, Now.AddDays(day)), x => x.Kind == SmartCollectionKind.NextIn);
			Assert.Equal("Next after Glass Harbour", named.Title);
			Assert.Equal("Next: Return to Avalon (1995) · 2 of 4 films watched", named.ReasonText);
		}
	}

	// 1.8.1 (the owner's library, fix 1): among franchises whose films have no play date (the owner's 42 film anchors), the one
	// the user is furthest into first: X-Men at 5 of 7 before Ant-Man at 1 of 3 ("Ant-Man and the Wasp: Quantumania" only sorted
	// first by name); a franchise of a film with a date comes before both, and each carries its anchor's date
	[Fact]
	public void NextIn_AmongUndatedFranchises_TheFurthestIntoFirst_AfterADatedOne()
	{
		string[] xmen = ["X-Men", "X2", "X-Men: The Last Stand", "X-Men Origins: Wolverine", "X-Men: First Class", "X-Men: Days of Future Past", "X-Men: Apocalypse"];
		var mutants = xmen.Select((x, i) => Film(x, 2000 + (i * 2), franchise: "748", played: i < 5, premiere: $"{2000 + (i * 2)}-05-01")).ToList();
		var ant = new[] { Film("Ant-Man", 2015, franchise: "422834"), Film("Ant-Man and the Wasp", 2018, franchise: "422834"), Film("Ant-Man and the Wasp: Quantumania", 2023, franchise: "422834", played: true) };
		var dated = new[] { Film("Dune", 2021, franchise: "726871", played: true), Film("Dune: Part Two", 2024, franchise: "726871") };
		var facts = new Facts(new() { [dated[0].Id] = new TitlePlays(1, Now.AddDays(-20)) });

		var next = Slots(Index([.. ant, .. mutants, .. dated]), facts).Where(x => x.Kind == SmartCollectionKind.NextIn).ToList();

		Assert.Equal(["Next in Dune", "Next in X-Men", "Next in Ant-Man"], next.Select(x => x.Title));
		Assert.Equal([Now.AddDays(-20), null, null], next.Select(x => x.AnchoredAt));
	}

	// 1.8.1 (the owner's library, fix 11): "X2" shares no word with the other six X-Men films, so 1.8.1's fallback said "Next after
	// X-Men" (read as "after the 2000 film", while the next is the fourth); five of the seven start "X-Men": "Next in X-Men"
	[Fact]
	public void NextIn_IsNamedByTheWordsMostOfItsFilmsStartWith()
	{
		string[] names = ["X-Men", "X2", "X-Men: The Last Stand", "X-Men Origins: Wolverine", "X-Men: First Class", "X-Men: Days of Future Past", "X-Men: Apocalypse"];
		var films = names.Select((x, i) => Film(x, 2000 + (i * 2), franchise: "748", played: i < 5, premiere: $"{2000 + (i * 2)}-05-01")).ToList();

		var next = Assert.Single(Slots(Index(films), new Facts()), x => x.Kind == SmartCollectionKind.NextIn);

		Assert.Equal("Next in X-Men", next.Title);
		Assert.Equal("X-Men", FranchiseNames.Majority(films));
		Assert.Null(FranchiseNames.Common(films)); // no word all seven share
		Assert.Null(FranchiseNames.Majority([Film("Alien", 1979), Film("Aliens", 1986), Film("Prometheus", 2012)])); // none more than half
	}

	[Fact]
	public void MoreLike_LeavesTheAnchorsFranchiseToNextIn_AndTheWatchedOut()
	{
		var saga = Enumerable.Range(1, 10).Select(i => Film($"Dragon Saga {i}", 2000 + i, ["Fantasy"], ["dragon"], franchise: "777", played: i == 1, premiere: $"{2000 + i}-06-01")).ToList();
		var dragons = Enumerable.Range(1, 10).Select(i => Film($"Other Dragons {i}", 1990 + i, ["Fantasy"], ["dragon"], played: i == 10)).ToList();
		var index = Index([.. saga, .. dragons, .. Common()]);

		var slots = Slots(index, new Facts());

		var like = Assert.Single(slots, x => x.Title == "More like Dragon Saga 1");
		Assert.Equal(dragons.Take(9).Select(x => x.Id).Order(), like.ItemIds.Order()); // not the saga's, not the watched one
		var next = Assert.Single(slots, x => x.Kind == SmartCollectionKind.NextIn);
		Assert.Equal("Next: Dragon Saga 2 (2002) · 1 of 10 films watched", next.ReasonText);
	}

	[Fact]
	public void NextIn_NeedsAFilmOfItFinished_AndAFilmOfItLeft()
	{
		var first = Film("Saga", 2001, franchise: "5", premiere: "2001-01-01");
		var second = Film("Saga II", 2003, franchise: "5", premiere: "2003-01-01");

		Assert.DoesNotContain(Slots(Index([first with { Favorite = true }, second]), new Facts()), x => x.Kind == SmartCollectionKind.NextIn); // a favourite alone hasn't started it
		Assert.DoesNotContain(Slots(Index([first with { Played = true }, second with { Played = true }]), new Facts()), x => x.Kind == SmartCollectionKind.NextIn); // nothing left
		Assert.DoesNotContain(Slots(Index([Film("Lonely", 2001, franchise: "6", played: true)]), new Facts()), x => x.Kind == SmartCollectionKind.NextIn); // one owned
		Assert.Single(Slots(Index([first with { Played = true }, second]), new Facts()), x => x.Kind == SmartCollectionKind.NextIn);
	}

	[Fact]
	public void ThePersonalSlotsHoldThreeDays_AndAFreshFinishLeadsAtOnce()
	{
		// four anchors finished weeks ago, each a director with five more films here
		var entries = new List<IndexEntry>();
		var facts = new Facts();
		for (var d = 1; d <= 5; d++)
		{
			var anchor = Film($"Anchor {d}", 2000 + d, ["Drama"], director: $"Director {d}", played: d <= 4);
			entries.Add(anchor);
			facts.Plays[anchor.Id] = new TitlePlays(d <= 4 ? 1 : 0, d <= 4 ? Now.AddDays(-20 - d) : null);
			entries.AddRange(Enumerable.Range(1, 5).Select(i => Film($"Film {d}.{i}", 2000 + i, ["Drama"], director: $"Director {d}")));
		}

		const int ThreeDays = 3; // the report's "personal slots hold for 3 days or more"
		var days = Now.Date.Ticks / TimeSpan.TicksPerDay;
		var blockStart = new DateTimeOffset(new DateTime((days - (days % ThreeDays)) * TimeSpan.TicksPerDay), Now.Offset).AddHours(12);
		string FirstMoreFrom(IndexSnapshot index, DateTimeOffset at) => Slots(index, facts, at).First(x => x.Kind == SmartCollectionKind.MoreFrom).Title;

		var index = Index(entries);
		var held = Enumerable.Range(0, ThreeDays).Select(d => FirstMoreFrom(index, blockStart.AddDays(d).AddHours(d == 2 ? 11 : 0))).ToList();
		Assert.Single(held.Distinct()); // the same three days, to the last evening
		Assert.NotEqual(held[0], FirstMoreFrom(index, blockStart.AddDays(ThreeDays))); // then the next anchor's

		// the fifth film finished on the block's second day leads at once
		var fifth = entries.First(x => x.Name == "Anchor 5");
		entries[entries.IndexOf(fifth)] = fifth with { Played = true };
		facts.Plays[fifth.Id] = new TitlePlays(1, blockStart.AddDays(1).AddHours(-2));
		Assert.Equal("More from Director 5", FirstMoreFrom(Index(entries), blockStart.AddDays(1)));
	}

	[Fact]
	public void OnABigServer_TheThreeSlotsTakeLittleTime()
	{
		// 25,000 films (3,000 of them second copies) and 2,000 shows, people for each, 80 films and 6 shows finished
		var genres = new[] { "Drama", "Comedy", "Thriller", "Action", "Horror", "Romance", "Crime", "Science Fiction", "Adventure", "Family", "Animation", "Mystery", "Fantasy", "War", "Western", "History", "Music", "Documentary" };
		var tags = Keywords.Allowed.Keys.ToArray();
		var entries = new List<IndexEntry>(27_000);
		var facts = new Facts();
		for (var i = 0; i < 22_000; i++)
		{
			var film = Film($"Film {i}", 1950 + (i % 75), [genres[i % 18], genres[(i / 18) % 18]], i % 5 == 0 ? [tags[i % tags.Length]] : [], $"Director {i % 4_000}",
				[$"Actor {(i * 7) % 30_000}", $"Actor {(i * 11) % 30_000}", $"Actor {(i * 13) % 30_000}"], i % 40 == 0 ? $"{i / 40 % 500}" : null, played: i % 275 == 0,
				rating: 5 + (i % 50 / 10.0), countries: [i % 3 == 0 ? "France" : "United States of America"], studios: [$"Studio {i % 300}"]) with { Tmdb = $"{i + 1}" };
			entries.Add(film);
			if (film.Played)
			{
				facts.Plays[film.Id] = new TitlePlays(1 + (i % 2), Now.AddDays(-(i % 30)));
			}

			if (i < 3_000)
			{
				entries.Add(film with { Id = NextId(3), PrimaryTag = null }); // a second copy of the same film
			}
		}

		var plays = new Dictionary<Guid, SeriesPlays>();
		for (var i = 0; i < 2_000; i++)
		{
			var show = Series($"Show {i}", 1980 + (i % 45), [genres[i % 18], "Animation"], i % 3 == 0 ? ["anime"] : [], [$"Voice {i % 900}", $"Voice {(i * 3) % 900}", $"Voice {(i * 5) % 900}"], unplayed: i % 330 == 0 ? 1 : 12);
			entries.Add(show);
			if (i % 330 == 0)
			{
				plays[show.Id] = new SeriesPlays(12, Now.AddDays(-(i % 9)));
			}
		}

		var index = Index(entries, plays);
		_ = Slots(Index([.. entries.Take(500)], plays), facts); // the code made ready first (the app builds many times a session)
		var context = BuildContext.For(index, Now);
		var watch = Stopwatch.StartNew();
		var slots = Slots(index, facts, context: context);
		watch.Stop();

		// what the time went on, made again in a fresh context part by part (the builders' own share is the rest)
		var parts = BuildContext.For(index, Now);
		var part = Stopwatch.StartNew();
		_ = parts.Titles;
		var collapse = part.ElapsedMilliseconds;
		_ = parts.Quality;
		var quality = part.ElapsedMilliseconds - collapse;
		var data = AnchoredSlots.DataOf(parts, facts);
		_ = data.Anchors;
		var anchors = part.ElapsedMilliseconds - collapse - quality;
		_ = data.People;
		var people = part.ElapsedMilliseconds - collapse - quality - anchors;
		_ = data.Similarity;
		var similarity = part.ElapsedMilliseconds - collapse - quality - anchors - people;
		var builders = new ISmartCollectionBuilder[] { new NextInFranchiseBuilder(_ => facts), new MoreLikeBuilder(_ => facts), new MoreFromBuilder(_ => facts) }
			.Select(x => { var each = Stopwatch.StartNew(); _ = x.Build(parts).ToList(); return $"{x.Kind} {each.ElapsedMilliseconds}"; });
		output.WriteLine($"{entries.Count:N0} entries: {slots.Count(x => x.Kind == SmartCollectionKind.NextIn)} Next in, {slots.Count(x => x.Kind == SmartCollectionKind.MoreLike)} More like, "
			+ $"{slots.Count(x => x.Kind == SmartCollectionKind.MoreFrom)} More from in {watch.ElapsedMilliseconds} ms (collapse {collapse}, quality {quality}, anchors {anchors}, "
			+ $"people {people}, similarity {similarity}, then {string.Join(", ", builders)} ms)");
		// each kind its six best candidates at most (the lineup takes one or two; each counts in "Made by Nixie · N")
		Assert.All(new[] { SmartCollectionKind.NextIn, SmartCollectionKind.MoreLike, SmartCollectionKind.MoreFrom }, kind => Assert.InRange(slots.Count(x => x.Kind == kind), 1, 6));
		Assert.All(slots, x => Assert.Equal(x.ItemIds.Count, x.ItemIds.Distinct().Count()));
		Assert.InRange(watch.ElapsedMilliseconds, 0, 5_000); // the budget on a loaded test machine; the log line says the real time
	}

	[Fact]
	public void ThePagesShowEachSlotInItsOwnOrder_NotAToZ()
	{
		var anchor = Film("First Film", 2001, ["Drama"], ["heist"], director: "Dee Rector", played: true);
		// the better rated, the later in A to Z: "I Other" is the best, "A Other" the only one watched
		List<IndexEntry> others = [.. Enumerable.Range(1, 9).Select(i => Film($"{(char)('A' + i - 1)} Other", 2001 + i, ["Drama"], ["heist"], director: "Dee Rector", rating: 6 + (i * 0.3), played: i == 1))];
		var saga = new[] { Film("Saga", 2001, franchise: "5", played: true, premiere: "2001-01-01"), Film("Saga II", 2003, franchise: "5", premiere: "2003-01-01") };
		var index = Index([anchor, .. others, .. saga, .. Common()]);
		var facts = new Facts(new() { [anchor.Id] = new TitlePlays(1, Now.AddDays(-1)) });

		var three = Slots(index, facts).Where(x => x.Title is "More from Dee Rector" or "More like First Film" or "Next in Saga").ToList();
		Assert.Equal(3, three.Count);
		foreach (var slot in three)
		{
			// the Collection page's own order for it (Order: Collection), the titles as the server sends them back by id
			var item = new FluentFin.Core.ViewModels.NixieCollection(slot).AsItem(favorite: false);
			var cards = slot.ItemIds.Select(id => index.Entries.First(x => x.Id == id).ToDto()).ToList();
			var shown = FluentFin.Core.ViewModels.CollectionBrowse.Ordered(cards, FluentFin.Core.ViewModels.CollectionOrder.Collection, playlist: false, item.DisplayOrder);
			Assert.Equal(slot.ItemIds, shown.Select(x => x.Id!.Value));
		}

		var more = Slots(index, facts).Single(x => x.Title == "More from Dee Rector");
		Assert.Equal("I Other", Name(index, more.ItemIds[0])); // the best not watched first, last in A to Z
		Assert.Equal("A Other", Name(index, more.ItemIds[^1])); // the watched last, first in A to Z
	}

	[Fact]
	public void TheIndexsOwnFacts_MakeTheSlots_AndItsPeopleAndSimilarityOnceARead()
	{
		var library = new Library();
		library.Arrival = library.Arrival with { Played = true };
		library.Mi1 = library.Mi1 with { Played = true };
		var index = Index(library.All, library.Plays);
		var first = BuildContext.For(index, Now);
		var second = BuildContext.For(index, Now);

		var slots = AnchoredSlots.Builders.SelectMany(x => x.Build(first)).ToList();

		Assert.Equal(["Next in Mission: Impossible", "More like Cowboy Bebop", "More from Denis Villeneuve"], slots.Select(x => x.Title));
		Assert.Equal(slots.Select(x => x.ReasonText), AnchoredSlots.Builders.SelectMany(x => x.Build(second)).Select(x => x.ReasonText));
		var a = AnchoredSlots.DataOf(first, IndexAnchorSource.For(first));
		var b = AnchoredSlots.DataOf(second, IndexAnchorSource.For(second));
		Assert.Same(a.People, b.People); // another page's build of the same index read reuses them
		Assert.Same(a.Similarity, b.Similarity);
		var reread = BuildContext.For(index with { }, Now);
		Assert.NotSame(a.People, AnchoredSlots.DataOf(reread, IndexAnchorSource.For(reread)).People); // a new read, new ones

		// an index without its people (1.7.3's, or its people pass still to come): no "More from", the rest as before
		var noPeople = index with { HasPeople = false };
		Assert.DoesNotContain(AnchoredSlots.Builders.SelectMany(x => x.Build(BuildContext.For(noPeople, Now))), x => x.Kind == SmartCollectionKind.MoreFrom);
	}

	[Fact]
	public void TheCatalogPublishesAndFindsThem_AndALibrarysStripHasNone()
	{
		var library = new Library();
		library.Arrival = library.Arrival with { Played = true };
		library.Mi1 = library.Mi1 with { Played = true };
		var index = Index(library.All, library.Plays);

		var all = SmartCollectionCatalog.BuildAll(index, Now);
		var mine = all.Where(x => x.Kind is SmartCollectionKind.NextIn or SmartCollectionKind.MoreLike or SmartCollectionKind.MoreFrom).ToList();
		Assert.Equal(["Next in Mission: Impossible", "More like Cowboy Bebop", "More from Denis Villeneuve"], mine.Select(x => x.Title));

		// a pin, Back or R finds each by its id, the same; the id names its kind
		foreach (var slot in mine)
		{
			Assert.Equal(slot.Kind, SmartCollectionCatalog.KindOf(slot.Id));
			var found = SmartCollectionCatalog.Find(index, slot.Id, Now);
			Assert.NotNull(found);
			Assert.Equal(slot.ItemIds, found.ItemIds);
			Assert.Equal(slot.ReasonText, found.ReasonText);
		}

		// a kind turned off in Settings is not made
		Assert.DoesNotContain(SmartCollectionCatalog.BuildAll(index, Now, kindsOff: new HashSet<SmartCollectionKind> { SmartCollectionKind.MoreFrom }), x => x.Kind == SmartCollectionKind.MoreFrom);

		// a library's presets strip (every published kind over one library's titles, Whole): no chip of a personal slot
		var movies = Guid.NewGuid();
		var oneLibrary = index with { Entries = [.. index.Entries.Select(x => x with { LibraryId = movies, LibraryIds = [movies] })] };
		Assert.DoesNotContain(NixiePresets.For(oneLibrary, movies, Now, most: 100), x => x.Kind is SmartCollectionKind.NextIn or SmartCollectionKind.MoreLike or SmartCollectionKind.MoreFrom);
	}

	[Fact]
	public void TheCensusSaysEachPersonalKindsTime_CountsOnly()
	{
		var library = new Library();
		library.Arrival = library.Arrival with { Played = true };
		library.Mi1 = library.Mi1 with { Played = true };
		var index = Index(library.All, library.Plays) with { Key = "anchors-census:" + Guid.NewGuid() };
		var log = new CoreListLogger<AnchorsTests>();

		SmartCollectionCatalog.BuildAll(SmartCollectionCatalog.PublishedBuilders, index, Now, null, null, FluentFin.Core.Outside.OutsideListsSnapshot.Empty, log, census: true);

		var line = Assert.Single(log.Entries).Message;
		output.WriteLine(line);
		Assert.Matches(@"NextIn 1/1/1 \[3 anchors\], MoreLike 3/1/1 \[3 anchors, \d+ ms\], MoreFrom \d+/1/1 \[\d+ people, \d+ ms\], Franchise", line);
		Assert.DoesNotMatch("Arrival|Bebop|Mission|Villeneuve|Hayashibara", line); // counts and kinds only (rule 10)
	}

	[Fact]
	public void PlaysComeFromTheServer_MergeOverCopies_AreSavedOnlyWhenKnown_AndMakeARewatch()
	{
		var last = new DateTimeOffset(2026, 11, 23, 21, 0, 0, TimeSpan.Zero);
		Jellyfin.Sdk.Generated.Models.BaseItemDto Dto(int plays, DateTimeOffset? at) => new()
		{
			Id = Guid.NewGuid(), Name = "Heat", Type = Jellyfin.Sdk.Generated.Models.BaseItemDto_Type.Movie, ProductionYear = 1995,
			ProviderIds = new Jellyfin.Sdk.Generated.Models.BaseItemDto_ProviderIds { AdditionalData = new Dictionary<string, object> { ["Tmdb"] = "949" } },
			UserData = new Jellyfin.Sdk.Generated.Models.UserItemDataDto { Played = plays > 0, PlayCount = plays, LastPlayedDate = at },
		};

		var movies = IndexEntry.From(Dto(2, last.AddDays(-30)), Guid.NewGuid())!;
		var fourK = IndexEntry.From(Dto(1, last), Guid.NewGuid())!;
		Assert.Equal((2, last.AddDays(-30)), (movies.PlayCount, movies.LastPlayed));

		// one title: the most plays, the latest play
		var title = Assert.Single(CanonicalTitles.Of([movies, fourK]).Entries);
		Assert.Equal((2, last), (title.PlayCount, title.LastPlayed));

		// saved only when known: an index nobody played stays as small as before
		var never = IndexEntry.From(Dto(0, null), Guid.NewGuid())!;
		Assert.DoesNotContain("PlayCount", System.Text.Json.JsonSerializer.Serialize(never));
		Assert.DoesNotContain("LastPlayed", System.Text.Json.JsonSerializer.Serialize(never));
		var back = System.Text.Json.JsonSerializer.Deserialize<IndexEntry>(System.Text.Json.JsonSerializer.Serialize(movies))!;
		Assert.Equal((2, last.AddDays(-30)), (back.PlayCount, back.LastPlayed));

		// the index's own facts: finished and started again, dated by its latest play, said without a count (review, Major)
		var context = BuildContext.For(Index([movies, fourK]), Now);
		var anchor = Assert.Single(AnchorFinder.Of(context, IndexAnchorSource.For(context)));
		Assert.Equal(AnchorWhy.Finished | AnchorWhy.Rewatched, anchor.Why);
		Assert.Equal("You finished Heat", anchor.Said);
		Assert.Equal(last, anchor.When);

		// started twice, never finished (as the server keeps it: two starts, not played): no anchor
		var twoStarts = Dto(2, last);
		twoStarts.UserData!.Played = false;
		var started = IndexEntry.From(twoStarts, Guid.NewGuid())!;
		Assert.Equal((2, false), (started.PlayCount, started.Played));
		var notFinished = BuildContext.For(Index([started]), Now);
		Assert.Empty(AnchorFinder.Of(notFinished, IndexAnchorSource.For(notFinished)));
	}

	// re-review (Minor 2): a people step cut short, or a minute's keep of a long one, has some titles' people: More from waits
	// for nearly all (IndexSnapshot.PeopleCoveredShare, 98 %), so it isn't made of a part and then changed
	[Fact]
	public void MoreFrom_WaitsUntilThePeopleOfNearlyEveryTitleAreIn()
	{
		var library = new Library();
		library.Arrival = library.Arrival with { Played = true };
		var index = Index(library.All, library.Plays);
		var ids = index.Entries.Select(x => x.Id).ToList();
		List<SmartCollection> MoreFrom(IndexSnapshot of, BuildContext? context = null) =>
			[.. AnchoredSlots.Builders.SelectMany(x => x.Build(context ?? BuildContext.For(of, Now))).Where(x => x.Kind == SmartCollectionKind.MoreFrom)];

		var half = index with { PeopleToAsk = [.. ids.Where((_, i) => i % 2 == 0)] }; // a step cut halfway
		var context = BuildContext.For(half, Now);
		Assert.Empty(MoreFrom(half, context));
		Assert.Equal("people of 50 % of the titles so far", context.Gates.NoteOf(SmartCollectionKind.MoreFrom));
		Assert.Empty(MoreFrom(index with { PeopleDue = true })); // every library's still to come
		Assert.Single(MoreFrom(index with { PeopleToAsk = [ids[^1]] })); // one new title still to ask: 98 % or more in
		Assert.Single(MoreFrom(index));
	}

	private static string Name(IndexSnapshot index, Guid id) => index.Entries.First(x => x.Id == id).Name;
}
